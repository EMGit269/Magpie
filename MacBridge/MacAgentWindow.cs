using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie.MacBridge
{
    public sealed class MacAgentWindow : Form
    {
        private static MacAgentWindow _current;
        private readonly WebView _web = new WebView();
        private readonly Label _status = new Label { Text = "Connecting to Agent…", VerticalAlignment = VerticalAlignment.Center };
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly MacAgentService _service;
        private CancellationTokenSource _sendCancellation;
        private Task _sendTask = Task.CompletedTask;
        private string _session = Guid.NewGuid().ToString("N");
        private readonly UITimer _canvasTimer = new UITimer { Interval = 1 };
        private JObject _canvasBinding;
        private bool _hasConversation;
        private static string BindingsPath => Path.Combine(MacAgentSettings.SupportDirectory, "canvas-bindings.json");
        private bool _closed;
        private bool _loaded;
        private readonly StringBuilder _incoming = new StringBuilder();
        private string _incomingId;
        private int _nextChunk;

        public static void ShowAgent()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    Bridge.Start();
                    var window = MacProcessState.FindWindow();
                    if (window == null)
                    {
                        _current = new MacAgentWindow();
                        MacProcessState.RegisterWindow(_current);
                        window = _current;
                    }
                    MacProcessState.FloatWindow(window);
                }
                catch (Exception error) { MessageBox.Show("Could not open Magpie: " + error.Message, "Magpie"); }
            }));
        }

        private MacAgentWindow()
        {
            _service = new MacAgentService(MacAgentSettings.Load());
            Title = "Magpie Agent"; ClientSize = new Size(530, 850); MinimumSize = new Size(410, 600);
            Owner = Rhino.UI.RhinoEtoApp.MainWindow;
            // RhinoEtoApp.MainWindow may be null on Mac. A floating window stays
            // above the GH editor without repeatedly activating it on canvas updates.
            Topmost = true;
            var retry = new Button { Text = "Reconnect" };
            retry.Click += async (_, __) => await RefreshStatus();
            Content = new TableLayout {
                Rows = { new TableRow(_web) { ScaleHeight = true },
                    new TableRow(new TableLayout { Padding = 6, Spacing = new Size(6, 0), Rows = { new TableRow(_status, retry) } }) } };
            _canvasTimer.Elapsed += (_, __) => { if (!_closed && _loaded) PublishCanvas(CanvasSnapshot()); };
            _canvasTimer.Start();
            _web.DocumentLoading += OnNavigation;
            Shown += (_, __) => { if (!_loaded) { _loaded = true; _web.LoadHtml(BuildHtml(), null); } };
            Closed += (_, __) => {
                _closed = true; _canvasTimer.Stop(); _canvasTimer.Dispose(); _lifetime.Cancel(); _sendCancellation?.Cancel();
                _current = null;
                // Closing the conversation does not stop the bridge or a shared service.
            };
        }

        private static string Resource(string name)
        {
            using (var stream = typeof(MacAgentWindow).Assembly.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException("Embedded Magpie UI resource not found: " + name))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        private static string BuildHtml()
        {
            var html = Resource("Magpie.ChatShell.html");
            foreach (var name in new[] { "marked.min.js", "purify.min.js" })
                html = html.Replace("<script src=\"vendor/" + name + "\"></script>", "<script>" + Resource("Magpie." + name) + "</script>");
            var bootstrap = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:;\"><script>"
                + Resource("Magpie.MacTransport.js") + "</script>";
            return html.Insert(html.IndexOf("<head>", StringComparison.Ordinal) + "<head>".Length, bootstrap);
        }

        private void OnNavigation(object sender, WebViewLoadingEventArgs args)
        {
            var uri = args.Uri;
            if (uri == null) { args.Cancel = true; return; }
            if (uri.Scheme == "about" && uri.AbsolutePath == "blank") return;
            // Keep the privileged message channel confined to the embedded page.
            args.Cancel = true;
            if (uri.Scheme != "magpie" || uri.Host != "message") return;
            Application.Instance.AsyncInvoke(() => {
                if (_closed) return;
                try
                {
                    var parts = uri.AbsolutePath.TrimStart('/').Split('/');
                    if (parts.Length != 4 || !int.TryParse(parts[1], out var index) || !int.TryParse(parts[2], out var total)
                        || total < 1 || total > 90 || index < 0 || index >= total || parts[3].Length > 16000)
                        throw new InvalidOperationException("Invalid WebView message packet.");
                    if (index == 0) { _incoming.Clear(); _incomingId = parts[0]; _nextChunk = 0; }
                    if (parts[0] != _incomingId || index != _nextChunk++) throw new InvalidOperationException("WebView message packets arrived out of order.");
                    _incoming.Append(parts[3]);
                    if (_incoming.Length > 1400000) throw new InvalidOperationException("Message attachments are too large.");
                    if (index + 1 < total) return;
                    var bytes = Convert.FromBase64String(_incoming.ToString().Replace('-', '+').Replace('_', '/'));
                    _incoming.Clear();
                    if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Message attachments are too large.");
                    var request = JObject.Parse(Encoding.UTF8.GetString(bytes));
                    _ = HandleMessage(request);
                }
                catch (Exception error) { Post("error_message", new JObject { ["text"] = error.Message }); }
                finally { if (!_closed) _web.ExecuteScript("window.magpieHostAck()"); }
            });
        }

        private void Post(string type, JObject payload)
        {
            var json = new JObject { ["type"] = type, ["payload"] = payload }.ToString(Formatting.None);
            Application.Instance.AsyncInvoke(() => {
                if (_closed) return;
                try { _web.ExecuteScript("window.magpieReceive(" + json + ")"); }
                catch (Exception error) { AddGhLog.Warn("Mac WebView delivery failed: " + error.GetType().Name); }
            });
        }

        private async Task HandleMessage(JObject message)
        {
            var payload = message["payload"] as JObject ?? new JObject();
            try
            {
                switch (message["type"]?.ToString())
                {
                    case "ready":
                        Post("init", new JObject { ["serviceUrl"] = _service.Settings.ServiceUrl, ["sessionId"] = _session,
                            ["themeMode"] = "system", ["modelSettings"] = _service.Settings.PublicModelSettings(),
                            ["diagnostics"] = new JObject { ["buildId"] = "mac-agent-v2", ["entryPath"] = "Embedded Mac WebView" } });
                        await BindCanvas(false);
                        await RefreshStatus();
                        break;
                    case "copy_text": Clipboard.Instance.Text = payload["text"]?.ToString() ?? ""; break;
                    case "reference_canvas": await BindCanvas(true); break;
                    case "refresh_status": await RefreshStatus(); break;
                    case "send_message":
                        if (!_sendTask.IsCompleted) return;
                        var text = ComposeMessage(payload);
                        if (string.IsNullOrWhiteSpace(text)) return;
                        var canvas = await ReadCanvas();
                        if (_canvasBinding != null && canvas?["id"]?.ToString() != _canvasBinding["id"]?.ToString())
                            throw new InvalidOperationException("当前画布与对话绑定的画布不同。请切回原画布，或点击「引用当前画布」开始新对话。");
                        if (_canvasBinding == null && canvas != null)
                            throw new InvalidOperationException("请先点击「引用当前画布」，再发送画布任务。");
                        _hasConversation = true;
                        Post("agent_ack", new JObject { ["text"] = "Working…" });
                        _sendTask = Send(text, canvas);
                        break;
                    case "stop_generation": _sendCancellation?.Cancel(); break;
                    case "new_session":
                        await StopSending();
                        _session = Guid.NewGuid().ToString("N");
                        _hasConversation = false; _canvasBinding = null;
                        await BindCanvas(false);
                        break;
                    case "theme_changed": Post("theme_state", new JObject { ["mode"] = payload["mode"] ?? "system" }); break;
                    case "load_model_settings": Post("model_settings", _service.Settings.PublicModelSettings()); break;
                    case "save_model_settings":
                        if (!_sendTask.IsCompleted) throw new InvalidOperationException("Stop generation before changing settings.");
                        _service.Settings.Save(payload);
                        Post("model_settings_saved", _service.Settings.PublicModelSettings());
                        await RefreshStatus();
                        break;
                    case "history_open": await History(); break;
                    case "history_load":
                        await StopSending();
                        var id = Required(payload, "sessionId");
                        var session = await Api(HttpMethod.Get, SessionPath(id));
                        _session = id;
                        _hasConversation = true; _canvasBinding = MacAgentSettings.Read(BindingsPath)[id] as JObject;
                        Post("session_loaded", new JObject { ["sessionId"] = id, ["messages"] = session["recent_messages"] ?? new JArray() });
                        PublishCanvas(await ReadCanvas());
                        break;
                    case "history_delete":
                        var deleteId = Required(payload, "sessionId");
                        if (deleteId == _session) await StopSending();
                        await Api(HttpMethod.Delete, SessionPath(deleteId));
                        var bindings = MacAgentSettings.Read(BindingsPath); bindings.Remove(deleteId); MacAgentSettings.WritePrivate(BindingsPath, bindings);
                        if (deleteId == _session)
                        {
                            _session = Guid.NewGuid().ToString("N");
                            _hasConversation = false; _canvasBinding = null;
                            Post("session_loaded", new JObject { ["sessionId"] = _session, ["messages"] = new JArray() });
                            await BindCanvas(false);
                        }
                        await History();
                        break;
                    case "history_rename":
                        await Api(HttpMethod.Patch, SessionPath(Required(payload, "sessionId")), new JObject { ["title"] = payload["title"]?.ToString() ?? "" });
                        await History(); break;
                }
            }
            catch (OperationCanceledException) { if (!_closed) Post("system_message", new JObject { ["text"] = "Operation cancelled." }); }
            catch (Exception error)
            {
                Post("error_message", new JObject { ["text"] = error.Message });
                if (message["type"]?.ToString() == "send_message") Post("generation_state", new JObject { ["isGenerating"] = false });
            }
        }

        private static string Required(JObject payload, string key) => !string.IsNullOrWhiteSpace(payload[key]?.ToString()) ? payload[key].ToString() : throw new InvalidOperationException("Missing " + key);
        private static string SessionPath(string id) => "/sessions/" + Uri.EscapeDataString(id);

        private async Task<JToken> Api(HttpMethod method, string path, JObject payload = null)
        {
            using (var ct = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                ct.CancelAfter(TimeSpan.FromSeconds(40));
                await _service.EnsureRunning(ct.Token).ConfigureAwait(false);
                return await _service.Api(method, path, payload, ct.Token).ConfigureAwait(false);
            }
        }
        private async Task History() => Post("history_items", new JObject { ["items"] = await Api(HttpMethod.Get, "/sessions") });

        private async Task RefreshStatus()
        {
            try
            {
                var health = await _service.EnsureRunning(_lifetime.Token).ConfigureAwait(false);
                var model = health?["model_configured"]?.Value<bool>() == true;
                var bridge = health?["host_bridge_available"]?.Value<bool>() == true;
                var text = !model ? "Configure a model in Settings." : !bridge ? "Agent connected; Grasshopper bridge unavailable." : "Agent and Grasshopper connected";
                Application.Instance.AsyncInvoke(() => { if (!_closed) _status.Text = text; });
                Post("status", new JObject { ["text"] = text, ["isReady"] = true, ["hostBridgeReady"] = bridge, ["serviceUrl"] = _service.Settings.ServiceUrl });
            }
            catch (Exception error)
            {
                Application.Instance.AsyncInvoke(() => { if (!_closed) _status.Text = "Agent unavailable — check Settings"; });
                Post("status", new JObject { ["text"] = error.Message, ["isReady"] = false, ["hostBridgeReady"] = false });
                Post("error_message", new JObject { ["text"] = error.Message });
            }
        }

        private async Task Send(string text, JObject canvas)
        {
            using (var ct = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                // Generation runs until completion, user stop, or window close.
                // A fixed wall-clock deadline also interrupts productive long tasks.
                _sendCancellation = ct;
                Post("generation_state", new JObject { ["isGenerating"] = true, ["status"] = "Working…" });
                try { await _service.Stream(_session, text, evt => Post("agent_event", evt), ct.Token, canvas?["id"]?.ToString() ?? "unbound", canvas).ConfigureAwait(false); }
                catch (OperationCanceledException) { Post("system_message", new JObject { ["text"] = "Generation stopped." }); }
                catch (Exception error) { Post("error_message", new JObject { ["text"] = error.Message }); }
                finally { _sendCancellation = null; Post("generation_state", new JObject { ["isGenerating"] = false }); }
            }
        }

        private async Task StopSending() { _sendCancellation?.Cancel(); await _sendTask; }

        private static JObject CanvasSnapshot()
        {
            var doc = Grasshopper.Instances.ActiveCanvas?.Document;
            if (doc == null) return null;
            return new JObject { ["id"] = doc.DocumentID.ToString(),
                ["name"] = string.IsNullOrEmpty(doc.FilePath) ? "未保存画布" : Path.GetFileName(doc.FilePath),
                ["componentCount"] = doc.Objects.Count,
                ["selectedCount"] = doc.Objects.Count(o => o.Attributes?.Selected == true),
                ["selected"] = new JArray(doc.Objects.Where(o => o.Attributes?.Selected == true).Take(30)
                    .Select(o => new JObject { ["id"] = o.InstanceGuid.ToString(), ["name"] = o.Name })) };
        }

        private static Task<JObject> ReadCanvas()
        {
            var done = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                try { done.SetResult(CanvasSnapshot()); } catch (Exception e) { done.SetException(e); }
            }));
            return done.Task;
        }

        private void PublishCanvas(JObject active)
        {
            Post("canvas_state", new JObject { ["bound"] = _canvasBinding?.DeepClone(), ["active"] = active,
                ["matches"] = _canvasBinding != null && active?["id"]?.ToString() == _canvasBinding["id"]?.ToString() });
        }

        private async Task BindCanvas(bool explicitReference)
        {
            if (!_sendTask.IsCompleted) throw new InvalidOperationException("请先停止生成，再引用画布。");
            var active = await ReadCanvas();
            if (explicitReference && active == null) throw new InvalidOperationException("请先打开 Grasshopper 画布。");
            var changed = _canvasBinding?["id"]?.ToString() != active?["id"]?.ToString();
            if (explicitReference && changed && _hasConversation)
            {
                _session = Guid.NewGuid().ToString("N"); _hasConversation = false;
                Post("session_loaded", new JObject { ["sessionId"] = _session, ["messages"] = new JArray() });
            }
            _canvasBinding = active;
            var saved = MacAgentSettings.Read(BindingsPath);
            if (active != null) saved[_session] = active.DeepClone(); else saved.Remove(_session);
            MacAgentSettings.WritePrivate(BindingsPath, saved);
            Post("session_state", new JObject { ["sessionId"] = _session });
            PublishCanvas(active);
            if (explicitReference) Post("canvas_referenced", new JObject { ["name"] = active?["name"] });
        }

        private static string ComposeMessage(JObject payload)
        {
            var text = new StringBuilder(payload["text"]?.ToString()?.Trim() ?? "");
            var budget = 40000;
            foreach (var file in (payload["attachments"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var name = file["name"]?.ToString() ?? "attachment";
                if (file["kind"]?.ToString() == "image")
                    throw new InvalidOperationException("Image analysis is not supported yet. Attach a text file or describe the image instead.");
                var content = file["textContent"]?.ToString() ?? "";
                var take = Math.Min(budget, Math.Min(12000, content.Length));
                text.Append("\n\n[Attached text file: ").Append(name).Append("]\n").Append(content.Substring(0, take)); budget -= take;
            }
            return text.ToString();
        }
    }

    public sealed class MacAgentComponent : GH_Component
    {
        public MacAgentComponent() : base("Magpie Agent", "Agent", "Double-click to open the Magpie conversation window.", "Magpie", "Agent") { }
        public override Guid ComponentGuid => new Guid("265fdc22-3216-4faa-82ae-090be13a2b5d");
        public override void CreateAttributes() { m_attributes = new AgentAttributes(this); }
        protected override void RegisterInputParams(GH_InputParamManager parameters) { }
        protected override void RegisterOutputParams(GH_OutputParamManager parameters) { }
        protected override void SolveInstance(IGH_DataAccess data) { Message = "Double-click to chat"; }
        private sealed class AgentAttributes : GH_ComponentAttributes
        {
            internal AgentAttributes(IGH_Component component) : base(component) { }
            public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent args)
            { MacAgentWindow.ShowAgent(); return GH_ObjectResponse.Handled; }
        }
    }
}
