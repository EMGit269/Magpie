using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie.MacBridge
{
    internal sealed class MacAgentService
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly SemaphoreSlim StartGate = new SemaphoreSlim(1, 1);
        private static Process _ownedProcess;
        private static string _ownedServiceUrl;
        internal MacAgentSettings Settings { get; }
        internal MacAgentService(MacAgentSettings settings) { Settings = settings; }

        internal async Task<JObject> Health(CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                return await Api(HttpMethod.Get, "/health", null, timeout.Token).ConfigureAwait(false) as JObject;
            }
        }

        private async Task<JObject> TryHealth(CancellationToken ct)
        {
            try { return await Health(ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        }

        internal async Task<JObject> EnsureRunning(CancellationToken ct)
        {
            var health = await TryHealth(ct).ConfigureAwait(false);
            if (health != null) return health;
            await StartGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                health = await TryHealth(ct).ConfigureAwait(false);
                if (health != null) return health;
                if (!File.Exists(Settings.PythonPath) || !File.Exists(Path.Combine(Settings.ServiceDirectory, "run_service.py")))
                    throw new InvalidOperationException("Agent service is unavailable. Run MacBridge/build-install.sh after preparing the Python environment, or set the Python executable and service folder in Settings.");
                if (_ownedProcess != null && _ownedServiceUrl != Settings.ServiceUrl) StopOwnedProcess();
                if (_ownedProcess == null || _ownedProcess.HasExited)
                {
                    _ownedProcess?.Dispose();
                    var start = new ProcessStartInfo {
                        FileName = Settings.PythonPath, WorkingDirectory = Settings.ServiceDirectory,
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    start.ArgumentList.Add(Path.Combine(Settings.ServiceDirectory, "run_service.py"));
                    start.ArgumentList.Add("--host"); start.ArgumentList.Add(Settings.ServiceUri.Host);
                    start.ArgumentList.Add("--port"); start.ArgumentList.Add(Settings.ServiceUri.Port.ToString());
                    start.Environment["MAGPIE_AGENT_MODEL_CONFIG"] = MacAgentSettings.ModelPath;
                    start.Environment["MAGPIE_AGENT_STATE_DIR"] = MacAgentSettings.StateDirectory;
                    start.Environment["MAGPIE_HOST_BRIDGE_BASE_URL"] = "http://127.0.0.1:" + Bridge.Port;
                    start.Environment["PYTHONUNBUFFERED"] = "1";
                    _ownedProcess = new Process { StartInfo = start };
                    // Drain both pipes without logging request bodies or model output.
                    _ownedProcess.OutputDataReceived += (_, __) => { };
                    _ownedProcess.ErrorDataReceived += (_, __) => { };
                    _ownedProcess.Start();
                    _ownedServiceUrl = Settings.ServiceUrl;
                    _ownedProcess.BeginOutputReadLine(); _ownedProcess.BeginErrorReadLine();
                }
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    health = await TryHealth(ct).ConfigureAwait(false);
                    if (health != null) return health;
                    if (_ownedProcess.HasExited) throw new InvalidOperationException("Agent service exited with code " + _ownedProcess.ExitCode + ". Check the Python environment and dependencies.");
                    await Task.Delay(350, ct).ConfigureAwait(false);
                }
                throw new TimeoutException("Agent service did not become ready within 30 seconds.");
            }
            finally { StartGate.Release(); }
        }

        internal static void StopOwnedProcess()
        {
            // Never terminate a manually started service.
            if (_ownedProcess == null) return;
            try { if (!_ownedProcess.HasExited) _ownedProcess.Kill(true); }
            catch (Exception error) { AddGhLog.Warn("Stopping owned Agent service failed: " + error.Message); }
            finally { _ownedProcess.Dispose(); _ownedProcess = null; }
        }

        internal async Task<JToken> Api(HttpMethod method, string path, JObject payload, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(method, Settings.ServiceUrl + path))
            {
                if (payload != null) request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var root = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (!response.IsSuccessStatusCode || root["success"]?.Value<bool>() != true)
                        throw new InvalidOperationException(root["error"]?.ToString() ?? root["detail"]?.ToString() ?? "Agent API returned HTTP " + (int)response.StatusCode);
                    return root["data"];
                }
            }
        }

        internal async Task Stream(string session, string text, Action<JObject> onEvent, CancellationToken ct, string documentId = null, JObject canvasContext = null)
        {
            var health = await EnsureRunning(ct).ConfigureAwait(false);
            if (documentId != null && health?["canvas_binding_supported"]?.Value<bool>() != true)
                throw new InvalidOperationException("Agent 服务版本过旧，请重启 Python Agent 服务以启用画布绑定。");
            using (var request = new HttpRequestMessage(HttpMethod.Post, Settings.ServiceUrl + Settings.InvokePath))
            {
                request.Content = new StringContent(new JObject { ["session_id"] = session, ["user_input"] = text, ["document_id"] = documentId, ["canvas_context"] = canvasContext ?? new JObject() }.ToString(), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Agent stream returned HTTP " + (int)response.StatusCode);
                    using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)))
                    {
                        var data = new StringBuilder();
                        while (true)
                        {
                            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                            if (line == null) throw new IOException("Agent stream ended before completion.");
                            if (line.Length == 0)
                            {
                                if (data.Length == 0) continue;
                                var raw = data.ToString(); data.Clear();
                                if (raw == "[DONE]") return;
                                var envelope = JObject.Parse(raw);
                                var type = envelope["type"]?.ToString();
                                var payload = envelope["payload"] as JObject ?? new JObject();
                                if (type == "done") return;
                                if (type == "error") throw new InvalidOperationException(payload["message"]?.ToString() ?? "Agent stream failed.");
                                if (type == "agent_event") onEvent(payload);
                            }
                            else if (line.StartsWith("data:", StringComparison.Ordinal))
                            { if (data.Length > 0) data.Append('\n'); data.Append(line.Substring(5).TrimStart(' ')); }
                        }
                    }
                }
            }
        }
    }
}
