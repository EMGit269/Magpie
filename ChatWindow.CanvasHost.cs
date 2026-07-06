using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grasshopper.Kernel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private enum WorkbenchPane
        {
            Canvas,
            Inspector
        }

        private static Button _btnCanvasPaneView;
        private static Button _btnInspectorPaneView;
        private static Button _btnCanvasSync;
        private static Grid _canvasPane;
        private static Grid _inspectorPane;
        private static Grid _canvasSurfaceHost;
        private static Border _canvasStatusHost;
        private static TextBlock _txtCanvasStatus;
        private static WebView2 _canvasWebView;
        private static WorkbenchPane _activeWorkbenchPane = WorkbenchPane.Canvas;
        private static bool _canvasRuntimeReady = false;
        private static bool _canvasRuntimeInitializing = false;
        private static bool _canvasRuntimeFailed = false;
        private static string _activeCanvasId = null;
        private const string CanvasVirtualHostName = "addgh-canvas.local";

        private static void InitializeCanvasWorkbenchBindings()
        {
            _btnCanvasPaneView = (Button)_window.FindName("BtnCanvasPaneView");
            _btnInspectorPaneView = (Button)_window.FindName("BtnInspectorPaneView");
            _btnCanvasSync = (Button)_window.FindName("BtnCanvasSync");
            _canvasPane = (Grid)_window.FindName("CanvasPane");
            _inspectorPane = (Grid)_window.FindName("InspectorPane");
            _canvasSurfaceHost = (Grid)_window.FindName("CanvasSurfaceHost");
            _canvasStatusHost = (Border)_window.FindName("CanvasStatusHost");
            _txtCanvasStatus = (TextBlock)_window.FindName("TxtCanvasStatus");

            if (_btnCanvasPaneView != null)
                _btnCanvasPaneView.Click += (s, e) => SetWorkbenchPane(WorkbenchPane.Inspector);
            if (_btnInspectorPaneView != null)
                _btnInspectorPaneView.Click += (s, e) => SetWorkbenchPane(WorkbenchPane.Canvas);
            if (_btnCanvasSync != null)
                _btnCanvasSync.Click += (s, e) =>
                {
                    ForceReloadCanvasWorkbench();
                    NotifyCanvasConversationChanged(true);
                };

            RefreshCanvasWorkbenchViewState();
        }

        private static void SetWorkbenchPane(WorkbenchPane pane)
        {
            _activeWorkbenchPane = pane;
            RefreshCanvasWorkbenchViewState();
            if (pane == WorkbenchPane.Inspector)
            {
                UpdateCodeView();
            }
            else
            {
                EnsureCanvasWorkbench();
                NotifyCanvasConversationChanged(true);
            }
        }

        private static void RefreshCanvasWorkbenchViewState()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                bool canvasActive = _activeWorkbenchPane == WorkbenchPane.Canvas;
                if (_canvasPane != null)
                    _canvasPane.Visibility = canvasActive ? Visibility.Visible : Visibility.Collapsed;
                if (_inspectorPane != null)
                    _inspectorPane.Visibility = canvasActive ? Visibility.Collapsed : Visibility.Visible;
                if (_btnCanvasPaneView != null)
                    _btnCanvasPaneView.Visibility = _isCodeVisible && canvasActive ? Visibility.Visible : Visibility.Collapsed;
                if (_btnInspectorPaneView != null)
                    _btnInspectorPaneView.Visibility = _isCodeVisible && !canvasActive ? Visibility.Visible : Visibility.Collapsed;
                if (_btnCanvasSync != null)
                    _btnCanvasSync.Visibility = _isCodeVisible && canvasActive ? Visibility.Visible : Visibility.Collapsed;
                if (_btnToggleViewMode != null)
                    _btnToggleViewMode.Visibility = _isCodeVisible && !canvasActive ? Visibility.Visible : Visibility.Collapsed;
                if (_chatCodeSplitter != null)
                    _chatCodeSplitter.Visibility = _isCodeVisible ? Visibility.Visible : Visibility.Collapsed;
                if (_splitterCol != null)
                    _splitterCol.Width = _isCodeVisible ? new GridLength(4) : new GridLength(0);

                ApplyWorkbenchTabStyle(_btnCanvasPaneView, canvasActive);
                ApplyWorkbenchTabStyle(_btnInspectorPaneView, !canvasActive);

                if (_isCodeVisible && canvasActive)
                    EnsureCanvasWorkbench();
            }));
        }

        private static void ApplyWorkbenchTabStyle(Button button, bool isSelected)
        {
            if (button == null) return;
            button.Background = isSelected
                ? ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(43, 49, 58))
                : Brushes.Transparent;
            button.BorderBrush = isSelected
                ? ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(58, 64, 74))
                : ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(50, 56, 67));
            button.Foreground = isSelected
                ? ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(229, 231, 235))
                : ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(174, 180, 189));
        }

        private static void SetCanvasStatus(string text, bool isError = false)
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_txtCanvasStatus != null)
                {
                    _txtCanvasStatus.Text = string.IsNullOrWhiteSpace(text) ? "准备加载画布工作台..." : text.Trim();
                    _txtCanvasStatus.Foreground = isError
                        ? new SolidColorBrush(Color.FromRgb(255, 189, 189))
                        : new SolidColorBrush(Color.FromRgb(170, 178, 191));
                }
                if (_canvasStatusHost != null)
                    _canvasStatusHost.Visibility = Visibility.Visible;
            }));
        }

        private static void HideCanvasStatus()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_canvasStatusHost != null)
                    _canvasStatusHost.Visibility = Visibility.Collapsed;
            }));
        }

        private static async void EnsureCanvasWorkbench()
        {
            if (_canvasRuntimeFailed || _canvasRuntimeInitializing || _canvasSurfaceHost == null)
                return;

            string entryPath = ResolveCanvasEntrypointPath();
            if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
            {
                _canvasRuntimeFailed = true;
                SetCanvasStatus("未找到 Canvas 前端资源，已回退到 Inspector。", true);
                SetWorkbenchPane(WorkbenchPane.Inspector);
                return;
            }

            if (_canvasWebView != null)
            {
                ReloadCanvasWorkbenchIfOutdated(entryPath);
                return;
            }

            _canvasRuntimeInitializing = true;
            SetCanvasStatus("正在初始化画布宿主...");
            try
            {
                var webView = new WebView2
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x16, 0x1C)
                };

                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _canvasSurfaceHost.Children.Insert(0, webView);
                }));

                _canvasWebView = webView;
                string userDataFolder = GetCanvasWebViewUserDataFolder();
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _canvasWebView.EnsureCoreWebView2Async(environment);
                _canvasWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _canvasWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _canvasWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _canvasWebView.CoreWebView2.WebMessageReceived += CanvasWebView_WebMessageReceived;
                _canvasWebView.CoreWebView2.NavigationStarting += CanvasWebView_NavigationStarting;
                _canvasWebView.CoreWebView2.SourceChanged += CanvasWebView_SourceChanged;
                _canvasWebView.CoreWebView2.ProcessFailed += CanvasWebView_ProcessFailed;
                _canvasWebView.NavigationCompleted += CanvasWebView_NavigationCompleted;

                Uri navigationUri = ConfigureCanvasContentMapping(_canvasWebView.CoreWebView2, entryPath)
                    ?? new Uri(entryPath, UriKind.Absolute);
                _canvasWebView.Source = navigationUri;
            }
            catch (Exception ex)
            {
                _canvasRuntimeFailed = true;
                AddGhLog.Warn("Canvas WebView init failed: " + ex.Message);
                SetCanvasStatus("WebView2 初始化失败，已回退到 Inspector。\n" + ex.Message, true);
                SetWorkbenchPane(WorkbenchPane.Inspector);
            }
            finally
            {
                _canvasRuntimeInitializing = false;
            }
        }

        private static void ForceReloadCanvasWorkbench()
        {
            try
            {
                string entryPath = ResolveCanvasEntrypointPath();
                if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                    return;

                ReloadCanvasWorkbenchIfOutdated(entryPath, true);
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("ForceReloadCanvasWorkbench failed: " + ex.Message);
            }
        }

        private static void ReloadCanvasWorkbenchIfOutdated(string entryPath, bool forceReload = false)
        {
            try
            {
                if (_canvasWebView?.CoreWebView2 == null || string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                    return;

                Uri navigationUri = ConfigureCanvasContentMapping(_canvasWebView.CoreWebView2, entryPath, forceReload)
                    ?? new Uri(entryPath, UriKind.Absolute);
                string current = _canvasWebView.Source?.ToString() ?? "";
                string target = navigationUri.ToString();
                if (!forceReload && string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                    return;

                _canvasRuntimeReady = false;
                SetCanvasStatus("正在重新加载画布工作台...");
                _canvasWebView.Source = navigationUri;
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("ReloadCanvasWorkbenchIfOutdated failed: " + ex.Message);
            }
        }

        private static void CanvasWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                _canvasRuntimeFailed = true;
                SetCanvasStatus("画布页面加载失败，已回退到 Inspector。", true);
                SetWorkbenchPane(WorkbenchPane.Inspector);
                return;
            }

            SetCanvasStatus("正在等待画布工作台就绪...");
        }

        private static void CanvasWebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            AddGhLog.Debug("Canvas navigation starting: " + e.Uri);
        }

        private static void CanvasWebView_SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            try
            {
                AddGhLog.Debug("Canvas source changed: " + _canvasWebView?.Source);
            }
            catch { }
        }

        private static void CanvasWebView_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _canvasRuntimeFailed = true;
            AddGhLog.Warn("Canvas WebView process failed: " + e.ProcessFailedKind + " " + e.Reason);
            SetCanvasStatus("Canvas WebView process failed.\n" + e.ProcessFailedKind + " " + e.Reason, true);
        }

        private static void CanvasWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var root = JObject.Parse(e.WebMessageAsJson);
                string type = root["type"]?.ToString();
                var payload = root["payload"] as JObject;
                if (string.IsNullOrWhiteSpace(type)) return;

                switch (type)
                {
                    case "canvas_ready":
                        _canvasRuntimeReady = true;
                        HideCanvasStatus();
                        NotifyCanvasConversationChanged(true);
                        NotifyCanvasInspectorUpdated();
                        break;
                    case "document_snapshot_save":
                        SaveCanvasDocumentSnapshot(payload?["canvasId"]?.ToString() ?? GetCurrentCanvasDocumentId(), payload?["snapshot"], null);
                        break;
                    case "card_meta_patch":
                        SaveCanvasDocumentMetaPatch(payload?["canvasId"]?.ToString() ?? GetCurrentCanvasDocumentId(), payload);
                        break;
                    case "canvas_new":
                        _activeCanvasId = CreateCanvasDocument(payload?["title"]?.ToString());
                        NotifyCanvasConversationChanged(true);
                        break;
                    case "canvas_open":
                        {
                            string canvasId = payload?["canvasId"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(canvasId) && File.Exists(GetCanvasDocumentStatePath(canvasId)))
                            {
                                _activeCanvasId = canvasId;
                                NotifyCanvasConversationChanged(true);
                            }
                        }
                        break;
                    case "canvas_delete":
                        DeleteCanvasDocument(payload?["canvasId"]?.ToString());
                        NotifyCanvasConversationChanged(true);
                        break;
                    case "capture_rhino_view":
                        {
                            string captureJson = ExecuteCaptureRhinoViewport(
                                payload?["framing"]?.ToString() ?? "current_view",
                                payload?["width"]?.Value<int?>(),
                                payload?["height"]?.Value<int?>(),
                                payload?["paddingRatio"]?.Value<double?>());
                            JObject capturePayload;
                            try
                            {
                                capturePayload = JObject.Parse(captureJson);
                            }
                            catch
                            {
                                capturePayload = new JObject { ["error"] = captureJson };
                            }

                            PostCanvasMessage("rhino_capture_result", capturePayload);
                        }
                        break;
                    case "canvas_ai_image_config_request":
                        PostCanvasMessage("canvas_ai_image_config", BuildCanvasAiImageConfigPayload());
                        break;
                    case "canvas_ai_image_request":
                        _ = RunCanvasAiImageNodeAsync(payload);
                        break;
                    case "canvas_prompt_optimize_request":
                        _ = OptimizeCanvasPromptNodeAsync(payload);
                        break;
                    case "host_bridge_manifest_request":
                        PostCanvasMessage("host_bridge_manifest", GrasshopperHost.BuildHostBridgeManifest());
                        break;
                    case "host_bridge_invoke":
                        {
                            JObject request = payload?["request"] as JObject ?? payload ?? new JObject();
                            PostCanvasMessage("host_bridge_result", GrasshopperHost.ExecuteHostBridgeRequest(request));
                        }
                        break;
                    case "canvas_switch_to_code":
                        SetWorkbenchPane(WorkbenchPane.Inspector);
                        break;
                    case "open_inspector_for_source":
                        SetWorkbenchPane(WorkbenchPane.Inspector);
                        UpdateCodeView();
                        break;
                    case "canvas_error":
                        AddGhLog.Warn("Canvas web error: " + (payload?["kind"]?.ToString() ?? "error")
                            + " " + (payload?["message"]?.ToString() ?? "")
                            + "\n" + (payload?["stack"]?.ToString() ?? ""));
                        SetCanvasStatus("Canvas web error.\n" + (payload?["message"]?.ToString() ?? ""), true);
                        break;
                    case "canvas_diag":
                        AddGhLog.Debug("Canvas diag: " + payload?.ToString(Formatting.None));
                        break;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Canvas web message parse failed: " + ex.Message);
            }
        }

        private static void DisposeCanvasWorkbench()
        {
            try
            {
                if (_canvasWebView?.CoreWebView2 != null)
                {
                    _canvasWebView.CoreWebView2.WebMessageReceived -= CanvasWebView_WebMessageReceived;
                    _canvasWebView.CoreWebView2.NavigationStarting -= CanvasWebView_NavigationStarting;
                    _canvasWebView.CoreWebView2.SourceChanged -= CanvasWebView_SourceChanged;
                    _canvasWebView.CoreWebView2.ProcessFailed -= CanvasWebView_ProcessFailed;
                }
            }
            catch { }

            try
            {
                if (_canvasWebView != null)
                    _canvasWebView.NavigationCompleted -= CanvasWebView_NavigationCompleted;
            }
            catch { }

            try { _canvasWebView?.Dispose(); }
            catch { }

            _canvasWebView = null;
            _canvasRuntimeReady = false;
            _canvasRuntimeInitializing = false;
            _canvasRuntimeFailed = false;
        }

        private static void NotifyCanvasConversationChanged(bool forceBootstrap)
        {
            if (_window == null) return;

            if (!_canvasRuntimeReady)
            {
                if (_isCodeVisible && _activeWorkbenchPane == WorkbenchPane.Canvas)
                    EnsureCanvasWorkbench();
                return;
            }

            var payload = forceBootstrap
                ? BuildCanvasBootstrapPayload()
                : BuildCanvasConversationDeltaPayload();

            PostCanvasMessage(forceBootstrap ? "bootstrap" : "conversation_delta", payload);
            NotifyCanvasInspectorUpdated();
        }

        private static void NotifyCanvasInspectorUpdated()
        {
            if (!_canvasRuntimeReady) return;
            PostCanvasMessage("inspector_update", BuildInspectorSnapshot());
        }

        private static void PostCanvasMessage(string type, JToken payload)
        {
            try
            {
                var message = new JObject
                {
                    ["type"] = type,
                    ["payload"] = payload ?? new JObject()
                };
                string json = message.ToString(Formatting.None);
                Action post = () =>
                {
                    if (_canvasWebView?.CoreWebView2 == null) return;
                    _canvasWebView.CoreWebView2.PostWebMessageAsJson(json);
                };

                var dispatcher = _canvasWebView?.Dispatcher ?? _window?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.BeginInvoke(post);
                else
                    post();
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("PostCanvasMessage failed: " + ex.Message);
            }
        }

        private static async System.Threading.Tasks.Task OptimizeCanvasPromptNodeAsync(JObject payload)
        {
            string sourceRef = payload?["sourceRef"]?.ToString() ?? "";
            try
            {
                string prompt = payload?["prompt"]?.ToString() ?? "";
                JObject inputPayload = payload?["input"] as JObject;
                string input = inputPayload?["text"]?.ToString() ?? payload?["input"]?.ToString() ?? "";
                JArray inputImages = inputPayload?["images"] as JArray ?? new JArray();
                var providerSettings = GetCanvasPromptOptimizerProviderSettings(inputImages.Count > 0);
                if (string.IsNullOrWhiteSpace(providerSettings?.ApiKey))
                {
                    PostCanvasMessage("canvas_prompt_optimize_result", new JObject
                    {
                        ["sourceRef"] = sourceRef,
                        ["success"] = false,
                        ["error"] = inputImages.Count > 0 ? "Vision/Qwen API key is empty." : "Qwen API key is empty."
                    });
                    return;
                }

                JObject requestBody = BuildCanvasPromptOptimizerRequestBody(providerSettings, prompt, input, inputImages);

                HttpResponseMessage response = null;
                string usedUrl = null;
                string lastError = null;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                foreach (var endpoint in BuildEndpointCandidates(providerSettings.BaseUrl))
                {
                    usedUrl = endpoint.Url;
                    response = await SendProviderRequestAsync(providerSettings, requestBody, endpoint.Url, cts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) break;
                    lastError = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!ShouldTryNextEndpoint(response.StatusCode)) break;
                }

                if (response == null || !response.IsSuccessStatusCode)
                    throw new Exception("Qwen request failed. " + (lastError ?? usedUrl ?? ""));

                string responseJson = await ReadResponseTextAsync(response, cts.Token).ConfigureAwait(false);
                if (!TryParseAssistantMessageFromResponse(responseJson, out JObject messageNode, out string parseError))
                    throw new Exception("Qwen response parse failed: " + parseError);

                string optimized = StripPromptOptimizerThinking(messageNode["content"]?.ToString());
                if (string.IsNullOrWhiteSpace(optimized))
                    optimized = StripPromptOptimizerThinking(messageNode["reasoning_content"]?.ToString());
                if (string.IsNullOrWhiteSpace(optimized))
                    throw new Exception("Qwen returned empty prompt.");

                PostCanvasMessage("canvas_prompt_optimize_result", new JObject
                {
                    ["sourceRef"] = sourceRef,
                    ["success"] = true,
                    ["prompt"] = optimized.Trim()
                });
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("OptimizeCanvasPromptNodeAsync failed: " + ex.Message);
                PostCanvasMessage("canvas_prompt_optimize_result", new JObject
                {
                    ["sourceRef"] = sourceRef,
                    ["success"] = false,
                    ["error"] = ex.Message
                });
            }
        }

        private static ProviderRuntimeSettings GetCanvasPromptOptimizerProviderSettings(bool hasImages)
        {
            if (hasImages)
            {
                var visionSettings = GetVisionProviderRuntimeSettings();
                if (!string.IsNullOrWhiteSpace(visionSettings?.ApiKey))
                    return visionSettings;
            }
            return GetProviderRuntimeSettings("qwen");
        }

        private static JObject BuildCanvasPromptOptimizerRequestBody(ProviderRuntimeSettings providerSettings, string prompt, string input, JArray inputImages)
        {
            var userContent = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = "Canvas input-port data:\n" + (string.IsNullOrWhiteSpace(input) ? "(empty)" : input) + "\n\nUser prompt:\n" + (string.IsNullOrWhiteSpace(prompt) ? "(empty)" : prompt)
                }
            };

            foreach (var image in inputImages.OfType<JObject>().Take(4))
            {
                string dataUrl = image["dataUrl"]?.ToString() ?? "";
                if (!dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    continue;
                userContent.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = dataUrl
                    }
                });
            }

            return new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["stream"] = false,
                ["temperature"] = 0.2,
                ["enable_thinking"] = false,
                ["thinking"] = new JObject { ["type"] = "disabled" },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "You are a fast prompt rewriting engine. Do not reason step by step. Do not output analysis. Use the user's original prompt and canvas input-port data, including multimodal references, to produce one clearer, executable, information-complete prompt. Return only the optimized prompt."
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = userContent
                    }
                }
            };
        }

        private static string StripPromptOptimizerThinking(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            string result = text.Trim();
            int thinkEnd = result.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
                result = result.Substring(thinkEnd + "</think>".Length).Trim();
            if (result.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
                return "";
            return result;
        }

        private static JObject BuildCanvasBootstrapPayload()
        {
            string conversationId = GetCurrentCanvasConversationId();
            string canvasId = GetCurrentCanvasDocumentId();
            JObject envelope = LoadCanvasDocumentEnvelope(canvasId) ?? CreateCanvasDocumentEnvelope(canvasId, null);
            JObject snapshot = envelope["snapshot"] as JObject ?? new JObject();
            snapshot["kind"] = "addgh-lightweight-canvas-v1";
            snapshot["nodes"] = BuildCanvasTypedNodesFromEnvelope(canvasId);
            snapshot["connections"] = snapshot["connections"] as JArray ?? new JArray();
            envelope["snapshot"] = snapshot;
            return new JObject
            {
                ["conversationId"] = conversationId,
                ["conversationTitle"] = GetCurrentCanvasConversationTitle(),
                ["canvasId"] = canvasId,
                ["canvasTitle"] = envelope["title"]?.ToString() ?? "Canvas",
                ["currentUserName"] = Environment.UserName ?? "User",
                ["canvasHistory"] = BuildCanvasHistoryItems(),
                ["messages"] = BuildCanvasMessageItems(),
                ["toolEvents"] = BuildCanvasToolItems(),
                ["inspectorSnapshot"] = BuildInspectorSnapshot(),
                ["canvasSnapshot"] = envelope
            };
        }

        private static JObject BuildCanvasConversationDeltaPayload()
        {
            return new JObject
            {
                ["conversationId"] = GetCurrentCanvasConversationId(),
                ["conversationTitle"] = GetCurrentCanvasConversationTitle(),
                ["canvasId"] = GetCurrentCanvasDocumentId(),
                ["messages"] = BuildCanvasMessageItems(),
                ["toolEvents"] = BuildCanvasToolItems()
            };
        }

        private static JArray BuildCanvasMessageItems()
        {
            var result = new JArray();
            if (_messages == null) return result;

            int messageIndex = 0;
            int toolFallbackIndex = 0;
            foreach (var msg in _messages)
            {
                string role = ChatMessageHelpers.TryGetRole(msg);
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                JObject messageObj = msg as JObject;
                string title = role == "user"
                    ? "User"
                    : role == "assistant"
                        ? "Assistant"
                        : messageObj?["name"]?.ToString() ?? "Tool";
                string body = ChatMessageHelpers.TryGetPlainTextContent(msg);
                if (string.IsNullOrWhiteSpace(body))
                    body = messageObj?["content"]?.ToString() ?? "";
                string summary = ClampCanvasText(body, 120);

                string sourceRef;
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    string callId = messageObj?["tool_call_id"]?.ToString();
                    sourceRef = "tool:" + (!string.IsNullOrWhiteSpace(callId) ? callId : ("fallback_" + toolFallbackIndex++));
                }
                else
                {
                    sourceRef = "message:" + messageIndex++;
                }

                result.Add(new JObject
                {
                    ["sourceRef"] = sourceRef,
                    ["role"] = role ?? "",
                    ["kind"] = role == "tool" ? "tool_result" : role == "assistant" ? "assistant_message" : "user_message",
                    ["title"] = title,
                    ["summary"] = string.IsNullOrWhiteSpace(summary) ? title : summary,
                    ["body"] = body ?? "",
                    ["tags"] = new JArray(),
                    ["collapsed"] = false,
                    ["pinned"] = false
                });
            }

            return result;
        }

        private static bool IsCanvasTypedNodeSourceRef(string sourceRef)
        {
            if (string.IsNullOrWhiteSpace(sourceRef)) return false;
            return IsCanvasPersistableTypedNodeSourceRef(sourceRef);
        }

        private static bool IsCanvasUserInputNodeSourceRef(string sourceRef)
        {
            if (string.IsNullOrWhiteSpace(sourceRef)) return false;
            return sourceRef.StartsWith("input_prompt:", StringComparison.OrdinalIgnoreCase)
                || sourceRef.StartsWith("input_image:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCanvasPersistableTypedNodeSourceRef(string sourceRef)
        {
            if (string.IsNullOrWhiteSpace(sourceRef)) return false;
            return sourceRef.StartsWith("generated_image:", StringComparison.OrdinalIgnoreCase);
        }

        private static JArray BuildCanvasTypedNodesFromEnvelope(string canvasId)
        {
            var nodes = new JArray();
            try
            {
                JObject envelope = LoadCanvasDocumentEnvelope(canvasId) ?? new JObject();
                JObject snapshot = envelope["snapshot"] as JObject;
                JArray existingNodes = snapshot?["nodes"] as JArray ?? new JArray();
                foreach (var node in existingNodes.OfType<JObject>())
                {
                    string sourceRef = node["sourceRef"]?.ToString();
                    if (IsCanvasTypedNodeSourceRef(sourceRef))
                        nodes.Add(NormalizeCanvasImageNode((JObject)node.DeepClone()));
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("BuildCanvasTypedNodesFromEnvelope failed: " + ex.Message);
            }
            return nodes;
        }

        private static JArray BuildCanvasToolItems()
        {
            var result = new JArray();
            if (_messages == null) return result;

            int fallbackIndex = 0;
            foreach (var msg in _messages.OfType<JObject>())
            {
                string role = msg["role"]?.ToString();
                if (!string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                    continue;

                string content = msg["content"]?.ToString() ?? "";
                string callId = msg["tool_call_id"]?.ToString();
                string name = msg["name"]?.ToString() ?? "tool";
                result.Add(new JObject
                {
                    ["sourceRef"] = "tool:" + (!string.IsNullOrWhiteSpace(callId) ? callId : ("fallback_" + fallbackIndex++)),
                    ["callId"] = callId ?? "",
                    ["name"] = name,
                    ["summary"] = ClampCanvasText(content, 120),
                    ["content"] = content
                });
            }

            return result;
        }

        private static JObject BuildInspectorSnapshot()
        {
            bool asPlainComment;
            string text = BuildInspectorCodeText(_isJsonMode, out asPlainComment);
            return new JObject
            {
                ["mode"] = _isJsonMode ? "json" : "raw",
                ["text"] = text ?? "",
                ["asPlainComment"] = asPlainComment,
                ["canvasIssues"] = _txtCanvasIssues?.Text ?? "",
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static string BuildInspectorCodeText(bool jsonMode, out bool asPlainComment)
        {
            asPlainComment = false;
            if (!jsonMode)
            {
                string raw = Magpie.Host.GrasshopperDocumentHost.ExecuteGetCanvasSummary();
                try
                {
                    var obj = JsonConvert.DeserializeObject(raw);
                    return JsonConvert.SerializeObject(obj, Formatting.Indented);
                }
                catch
                {
                    return raw ?? "";
                }
            }

            var doc = Grasshopper.Instances.ActiveCanvas?.Document;
            if (doc == null)
            {
                asPlainComment = true;
                return "// 没有激活的画布";
            }

            var graph = new JObject();
            if (DeploymentOptions.IncludeCanvasExportTimestamp)
                graph["timestamp"] = DateTime.Now.ToString("HH:mm:ss");
            graph["object_count"] = doc.ObjectCount;

            var components = new JArray();
            foreach (var obj in doc.Objects)
            {
                var compJson = new JObject
                {
                    ["name"] = obj.Name,
                    ["nickname"] = obj.NickName,
                    ["id"] = GetPublicId(doc, obj),
                    ["guid"] = obj.InstanceGuid.ToString(),
                    ["pivot"] = new JObject
                    {
                        ["x"] = Math.Round(obj.Attributes.Pivot.X),
                        ["y"] = Math.Round(obj.Attributes.Pivot.Y)
                    }
                };

                AppendSliderStateJson(obj, compJson);

                if (obj is IGH_Component comp)
                {
                    var inputs = new JArray();
                    foreach (var param in comp.Params.Input)
                    {
                        var paramJson = new JObject
                        {
                            ["name"] = param.Name,
                            ["nickname"] = param.NickName
                        };
                        var sources = new JArray();
                        foreach (var source in param.Sources)
                            sources.Add(GetPublicId(doc, source.Attributes.GetTopLevel.DocObject));
                        paramJson["sources"] = sources;
                        inputs.Add(paramJson);
                    }
                    compJson["inputs"] = inputs;

                    var outputs = new JArray();
                    foreach (var param in comp.Params.Output)
                    {
                        outputs.Add(new JObject
                        {
                            ["name"] = param.Name,
                            ["nickname"] = param.NickName
                        });
                    }
                    compJson["outputs"] = outputs;
                }
                else if (obj is IGH_Param param)
                {
                    var sources = new JArray();
                    foreach (var source in param.Sources)
                        sources.Add(GetPublicId(doc, source.Attributes.GetTopLevel.DocObject));
                    compJson["sources"] = sources;
                }

                components.Add(compJson);
            }

            graph["components"] = components;
            return graph.ToString(Formatting.Indented);
        }

        private static string ClampCanvasText(string text, int maxLen)
        {
            string s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "…";
        }

        private static string GetCurrentCanvasConversationId()
        {
            if (!string.IsNullOrWhiteSpace(_activeHistoryId))
                return _activeHistoryId;
            return "draft";
        }

        private static string GetCurrentCanvasConversationTitle()
        {
            var active = FindHistoryConversation(_activeHistoryId);
            if (active != null && !string.IsNullOrWhiteSpace(active.Title))
                return active.Title;
            return "新对话";
        }

        private static string GetCanvasStateDirectory()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magpie", "canvas");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string GetCanvasDocumentDirectory()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magpie", "canvas-documents");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string GetCanvasWebViewUserDataFolder()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magpie", "WebView2", "CanvasWorkbench");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static Uri ConfigureCanvasContentMapping(CoreWebView2 core, string entryPath, bool forceReload = false)
        {
            if (core == null || string.IsNullOrWhiteSpace(entryPath) || !Path.IsPathRooted(entryPath))
                return null;

            try
            {
                string entryDirectory = Path.GetDirectoryName(entryPath);
                string canvasRoot = Directory.GetParent(entryDirectory ?? "")?.FullName;
                if (string.IsNullOrWhiteSpace(canvasRoot) || !Directory.Exists(canvasRoot))
                    return null;

                string relativePath = entryPath.Substring(canvasRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                if (string.IsNullOrWhiteSpace(relativePath))
                    return null;

                core.SetVirtualHostNameToFolderMapping(
                    CanvasVirtualHostName,
                    canvasRoot,
                    CoreWebView2HostResourceAccessKind.Allow);

                string cacheKey = File.GetLastWriteTimeUtc(entryPath).Ticks.ToString();
                if (forceReload)
                    cacheKey += "-" + DateTime.UtcNow.Ticks.ToString();
                return new Uri("https://" + CanvasVirtualHostName + "/" + relativePath + "?v=" + cacheKey, UriKind.Absolute);
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("ConfigureCanvasContentMapping failed: " + ex.Message);
                return null;
            }
        }

        private static string GetCanvasConversationStatePath(string conversationId)
        {
            string safe = string.IsNullOrWhiteSpace(conversationId) ? "draft" : conversationId.Trim();
            return Path.Combine(GetCanvasStateDirectory(), safe + ".json");
        }

        private static string GetCurrentCanvasDocumentId()
        {
            if (!string.IsNullOrWhiteSpace(_activeCanvasId))
                return _activeCanvasId;
            _activeCanvasId = GetDefaultCanvasDocumentId();
            EnsureCanvasDocumentExists(_activeCanvasId, "Canvas");
            return _activeCanvasId;
        }

        private static string GetDefaultCanvasDocumentId()
        {
            return "canvas-default";
        }

        private static string GetCanvasDocumentStatePath(string canvasId)
        {
            string safe = string.IsNullOrWhiteSpace(canvasId) ? GetDefaultCanvasDocumentId() : canvasId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return Path.Combine(GetCanvasDocumentDirectory(), safe + ".json");
        }

        private static JObject CreateCanvasDocumentEnvelope(string canvasId, string title)
        {
            string now = DateTime.UtcNow.ToString("o");
            return new JObject
            {
                ["schemaVersion"] = "canvas-v2",
                ["canvasId"] = canvasId,
                ["title"] = string.IsNullOrWhiteSpace(title) ? "Canvas" : title.Trim(),
                ["createdAtUtc"] = now,
                ["updatedAtUtc"] = now,
                ["snapshot"] = new JObject
                {
                    ["kind"] = "addgh-lightweight-canvas-v1",
                    ["nodes"] = new JArray(),
                    ["connections"] = new JArray()
                },
                ["cardMetaPatches"] = new JObject()
            };
        }

        private static void EnsureCanvasDocumentExists(string canvasId, string title)
        {
            try
            {
                string path = GetCanvasDocumentStatePath(canvasId);
                if (File.Exists(path)) return;
                File.WriteAllText(path, CreateCanvasDocumentEnvelope(canvasId, title).ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("EnsureCanvasDocumentExists failed: " + ex.Message);
            }
        }

        private static string CreateCanvasDocument(string title)
        {
            string id = "canvas-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            EnsureCanvasDocumentExists(id, string.IsNullOrWhiteSpace(title) ? ("Canvas " + DateTime.Now.ToString("HH:mm")) : title);
            return id;
        }

        private static JObject LoadCanvasDocumentEnvelope(string canvasId)
        {
            try
            {
                string path = GetCanvasDocumentStatePath(canvasId);
                if (!File.Exists(path)) return null;
                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("LoadCanvasDocumentEnvelope failed: " + ex.Message);
                return null;
            }
        }

        private static JArray BuildCanvasHistoryItems()
        {
            var result = new JArray();
            try
            {
                foreach (string path in Directory.GetFiles(GetCanvasDocumentDirectory(), "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    string id = root["canvasId"]?.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                        id = Path.GetFileNameWithoutExtension(path);
                    result.Add(new JObject
                    {
                        ["canvasId"] = id,
                        ["title"] = root["title"]?.ToString() ?? id,
                        ["updatedAtUtc"] = root["updatedAtUtc"]?.ToString() ?? File.GetLastWriteTimeUtc(path).ToString("o"),
                        ["nodeCount"] = root["snapshot"]?["nodes"] is JArray nodes ? nodes.Count : 0,
                        ["drawingCount"] = root["snapshot"]?["drawingItems"] is JArray drawingItems ? drawingItems.Count : 0
                    });
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildCanvasHistoryItems failed: " + ex.Message);
            }
            return result;
        }

        private static void DeleteCanvasDocument(string canvasId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(canvasId))
                    return;

                string path = GetCanvasDocumentStatePath(canvasId);
                if (File.Exists(path))
                    File.Delete(path);

                if (string.Equals(_activeCanvasId, canvasId, StringComparison.OrdinalIgnoreCase))
                {
                    string nextPath = Directory.GetFiles(GetCanvasDocumentDirectory(), "*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(nextPath))
                    {
                        try
                        {
                            JObject next = JObject.Parse(File.ReadAllText(nextPath, Encoding.UTF8));
                            _activeCanvasId = next["canvasId"]?.ToString();
                        }
                        catch
                        {
                            _activeCanvasId = Path.GetFileNameWithoutExtension(nextPath);
                        }
                    }
                    else
                    {
                        _activeCanvasId = GetDefaultCanvasDocumentId();
                        EnsureCanvasDocumentExists(_activeCanvasId, "Canvas");
                    }
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("DeleteCanvasDocument failed: " + ex.Message);
            }
        }

        private static JObject LoadCanvasConversationEnvelope(string conversationId)
        {
            try
            {
                string path = GetCanvasConversationStatePath(conversationId);
                if (!File.Exists(path)) return null;
                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("LoadCanvasConversationEnvelope failed: " + ex.Message);
                return null;
            }
        }

        private static void SaveCanvasConversationSnapshot(string conversationId, JToken snapshot, JToken metaPatches)
        {
            SaveCanvasDocumentSnapshot(GetCurrentCanvasDocumentId(), snapshot, metaPatches);
        }

        private static void SaveCanvasDocumentSnapshot(string canvasId, JToken snapshot, JToken metaPatches)
        {
            try
            {
                string path = GetCanvasDocumentStatePath(canvasId);
                JObject root = LoadCanvasDocumentEnvelope(canvasId) ?? CreateCanvasDocumentEnvelope(canvasId, null);
                root["schemaVersion"] = "canvas-v2";
                root["canvasId"] = canvasId;
                root["updatedAtUtc"] = DateTime.UtcNow.ToString("o");
                if (snapshot != null)
                {
                    JObject snapshotObject = snapshot as JObject;
                    JArray nodes = snapshotObject?["nodes"] as JArray;
                    if (nodes != null)
                    {
                        var normalizedNodes = new JArray();
                        foreach (var node in nodes.OfType<JObject>())
                        {
                            string sourceRef = node["sourceRef"]?.ToString() ?? "";
                            if (IsCanvasUserInputNodeSourceRef(sourceRef))
                                continue;
                            normalizedNodes.Add(NormalizeCanvasImageNode((JObject)node.DeepClone()));
                        }
                        snapshotObject["nodes"] = normalizedNodes;
                    }
                    root["snapshot"] = snapshot;
                }
                if (metaPatches != null)
                    root["cardMetaPatches"] = metaPatches;
                File.WriteAllText(path, root.ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("SaveCanvasConversationSnapshot failed: " + ex.Message);
            }
        }

        private static void SaveCanvasMetaPatch(string conversationId, JObject patch)
        {
            SaveCanvasDocumentMetaPatch(GetCurrentCanvasDocumentId(), patch);
        }

        private static void SaveCanvasDocumentMetaPatch(string canvasId, JObject patch)
        {
            if (patch == null) return;
            try
            {
                JObject root = LoadCanvasDocumentEnvelope(canvasId) ?? CreateCanvasDocumentEnvelope(canvasId, null);
                var bucket = root["cardMetaPatches"] as JObject ?? new JObject();
                string sourceRef = patch["sourceRef"]?.ToString();
                if (string.IsNullOrWhiteSpace(sourceRef))
                    sourceRef = patch["cardId"]?.ToString() ?? Guid.NewGuid().ToString("n");
                bucket[sourceRef] = patch.DeepClone();
                root["schemaVersion"] = "canvas-v2";
                root["canvasId"] = canvasId;
                root["updatedAtUtc"] = DateTime.UtcNow.ToString("o");
                root["cardMetaPatches"] = bucket;
                File.WriteAllText(GetCanvasDocumentStatePath(canvasId), root.ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("SaveCanvasMetaPatch failed: " + ex.Message);
            }
        }

        private static void DeleteCanvasConversationState(string conversationId)
        {
            try
            {
                string path = GetCanvasConversationStatePath(conversationId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("DeleteCanvasConversationState failed: " + ex.Message);
            }
        }

        private static string ResolveCanvasEntrypointPath()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CanvasWeb", "dist", "index.html"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "CanvasWeb", "dist", "index.html")
            }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

            foreach (string root in GetSearchAncestors(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)))
            {
                candidates.Add(Path.Combine(root, "CanvasWeb", "dist", "index.html"));
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        private static IEnumerable<string> GetSearchAncestors(string start)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = start;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (seen.Add(current))
                    yield return current;
                try
                {
                    string parent = Directory.GetParent(current)?.FullName;
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                        break;
                    current = parent;
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
