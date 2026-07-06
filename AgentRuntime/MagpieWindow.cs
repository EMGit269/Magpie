using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Grasshopper;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace Magpie.AgentRuntime
{
    public static class MagpieWindow
    {
        private static Window _window;
        private static WebView2 _webView;
        private static Dispatcher _uiDispatcher;
        private static string _sessionId;
        private static bool _isSending;
        private static CancellationTokenSource _sendCts;
        private static string _entryPath;
        private const string BuildId = "2026-07-03.session-memory";

        public static void Show()
        {
            GrasshopperHost.EnsureHostBridgeRuntime();
            MagpieSettings.PersistAgentServiceModelConfig();

            if (_window != null)
            {
                _window.Show();
                _window.Activate();
                _ = WarmStartAgentServiceAsync();
                _ = RefreshServiceStatusAsync();
                return;
            }

            _window = BuildWindow();
            _sessionId = Guid.NewGuid().ToString("N");
            _window.Closed += (s, e) =>
            {
                try
                {
                    if (_webView?.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= WebView_WebMessageReceived;
                }
                catch { }

                try { _webView?.Dispose(); } catch { }
                _webView = null;
                _window = null;
                _isSending = false;
                try { _sendCts?.Cancel(); } catch { }
                try { _sendCts?.Dispose(); } catch { }
                _sendCts = null;
                GrasshopperHost.StopHostBridgeRuntime();
            };

            _window.Show();
            InitializeWebViewAsync();
            _ = WarmStartAgentServiceAsync();
        }

        private static Window BuildWindow()
        {
            var window = new Window
            {
                Title = MagpieSettings.WindowTitle,
                Width = 450,
                Height = 850,
                MinWidth = 410,
                MinHeight = 760,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = System.Windows.Media.Brushes.Black
            };

            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x11, 0x13, 0x15)
            };
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            window.Content = _webView;
            return window;
        }

        private static async void InitializeWebViewAsync()
        {
            if (_webView == null)
                return;

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Magpie",
                    "WebView2",
                    "MagpieWindow");
                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(environment);
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _webView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

                string entryPath = ResolveEntryPath();
                if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                    throw new FileNotFoundException("Magpie web UI entry not found.", entryPath);

                _entryPath = entryPath;
                Magpie.AddGhLog.Info("MagpieWindow loading Web UI: " + entryPath + " build=" + BuildId);
                _webView.Source = new Uri(entryPath, UriKind.Absolute);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initialize Magpie web UI.\n" + ex.Message, "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject root;
            try
            {
                root = JObject.Parse(e.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Warn("MagpieWindow WebMessage parse failed: " + ex.Message + " raw=" + e.WebMessageAsJson);
                return;
            }

            string type = root["type"]?.ToString();
            JObject payload = root["payload"] as JObject ?? new JObject();
            Magpie.AddGhLog.Info("MagpieWindow WebMessage received: " + type);

            switch (type)
            {
                case "ready":
                    PostToWeb("init", new JObject
                    {
                        ["serviceUrl"] = MagpieSettings.ServiceBaseUrl,
                        ["welcome"] = "Magpie connected to the LangGraph runtime shell.",
                        ["sessionId"] = _sessionId,
                        ["themeMode"] = "system",
                        ["diagnostics"] = new JObject
                        {
                            ["buildId"] = BuildId,
                            ["entryPath"] = _entryPath ?? "",
                            ["assemblyPath"] = typeof(MagpieWindow).Assembly.Location
                        },
                        ["modelSettings"] = BuildModelSettingsPayload()
                    });
                    await RefreshServiceStatusAsync();
                    break;

                case "refresh_status":
                    await RefreshServiceStatusAsync();
                    break;

                case "send_message":
                    PostToWeb("agent_ack", new JObject
                    {
                        ["text"] = "Agent runtime contacted."
                    });
                    _ = HandleSendMessageAsync(payload["text"]?.ToString(), payload["attachments"] as JArray);
                    break;

                case "new_session":
                    _sessionId = Guid.NewGuid().ToString("N");
                    Magpie.AddGhLog.Info("Started new Magpie session: " + _sessionId);
                    PostToWeb("session_state", new JObject
                    {
                        ["sessionId"] = _sessionId
                    });
                    break;

                case "stop_generation":
                    StopGeneration();
                    break;

                case "theme_changed":
                    PostToWeb("theme_state", new JObject
                    {
                        ["mode"] = payload["mode"]?.ToString() ?? "system"
                    });
                    break;

                case "load_model_settings":
                    PostToWeb("model_settings", BuildModelSettingsPayload());
                    break;

                case "save_model_settings":
                    SaveModelSettings(payload);
                    AgentServiceProcessManager.RestartForSettingsChange();
                    _ = WarmStartAgentServiceAsync();
                    _ = RefreshServiceStatusAsync();
                    PostToWeb("model_settings_saved", BuildModelSettingsPayload());
                    break;

                case "history_open":
                    _ = HandleHistoryOpenAsync();
                    break;

                case "history_load":
                    {
                        string loadSessionId = payload["sessionId"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(loadSessionId))
                            _ = HandleHistoryLoadAsync(loadSessionId);
                    }
                    break;

                case "history_delete":
                    {
                        string deleteSessionId = payload["sessionId"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(deleteSessionId))
                            _ = HandleHistoryDeleteAsync(deleteSessionId);
                    }
                    break;

                case "history_rename":
                    {
                        string renameSessionId = payload["sessionId"]?.ToString();
                        string newTitle = payload["title"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(renameSessionId) && newTitle != null)
                            _ = HandleHistoryRenameAsync(renameSessionId, newTitle.Trim());
                    }
                    break;

            }
        }

        private static JObject BuildModelSettingsPayload()
        {
            string currentProvider = ReadProviderId(Instances.Settings.GetValue("AI_CurrentProvider", "deepseek"));
            return new JObject
            {
                ["currentProvider"] = currentProvider,
                ["service"] = BuildServiceSettingsPayload(),
                ["providers"] = new JArray
                {
                    BuildProviderSettings("deepseek", "DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-v4-flash"),
                    BuildProviderSettings("qwen", "Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen3.6-plus")
                }
            };
        }

        private static JObject BuildServiceSettingsPayload()
        {
            return new JObject
            {
                ["serviceUrl"] = MagpieSettings.ServiceBaseUrl,
                ["startCommand"] = MagpieSettings.ServiceStartCommand,
                ["workingDirectory"] = MagpieSettings.ServiceWorkingDirectory,
                ["invokePath"] = MagpieSettings.InvokePath
            };
        }

        private static JObject BuildProviderSettings(string providerId, string displayName, string defaultBaseUrl, string defaultModel)
        {
            string baseUrlDefault = providerId == "deepseek"
                ? Instances.Settings.GetValue("AI_API_BaseUrl", defaultBaseUrl)
                : defaultBaseUrl;
            string modelDefault = providerId == "deepseek"
                ? Instances.Settings.GetValue("AI_ModelName", defaultModel)
                : defaultModel;

            return new JObject
            {
                ["id"] = providerId,
                ["displayName"] = displayName,
                ["defaultBaseUrl"] = defaultBaseUrl,
                ["defaultModel"] = defaultModel,
                ["baseUrl"] = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "BaseUrl"), baseUrlDefault),
                ["modelName"] = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ModelName"), modelDefault),
                ["apiKeyConfigured"] = IsApiKeyConfigured(providerId)
            };
        }

        private static void SaveModelSettings(JObject payload)
        {
            string providerId = ReadProviderId(payload?["providerId"]?.ToString());
            string baseUrl = (payload?["baseUrl"]?.ToString() ?? "").Trim();
            string modelName = (payload?["modelName"]?.ToString() ?? "").Trim();
            string apiKey = payload?["apiKey"]?.ToString();
            bool clearApiKey = payload?["clearApiKey"]?.Value<bool?>() == true;
            JObject service = payload?["service"] as JObject;

            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = providerId == "qwen"
                    ? "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
                    : "https://api.deepseek.com/chat/completions";
            if (string.IsNullOrWhiteSpace(modelName))
                modelName = providerId == "qwen" ? "qwen3.6-plus" : "deepseek-v4-flash";

            Instances.Settings.SetValue("AI_CurrentProvider", providerId);
            Instances.Settings.SetValue(GetProviderSettingKey(providerId, "BaseUrl"), baseUrl);
            Instances.Settings.SetValue(GetProviderSettingKey(providerId, "ModelName"), modelName);

            if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                Instances.Settings.SetValue("AI_API_BaseUrl", baseUrl);
                Instances.Settings.SetValue("AI_ModelName", modelName);
            }

            if (clearApiKey)
            {
                PersistApiKey(providerId, "");
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
            {
                PersistApiKey(providerId, apiKey.Trim());
            }

            if (service != null)
                SaveServiceSettings(service);

            MagpieSettings.PersistAgentServiceModelConfig();
        }

        private static void SaveServiceSettings(JObject service)
        {
            string serviceUrl = (service?["serviceUrl"]?.ToString() ?? "").Trim().TrimEnd('/');
            string startCommand = (service?["startCommand"]?.ToString() ?? "").Trim();
            string workingDirectory = (service?["workingDirectory"]?.ToString() ?? "").Trim();
            string invokePath = (service?["invokePath"]?.ToString() ?? "").Trim();

            Instances.Settings.SetValue("Agent_Service_Url", serviceUrl);
            Instances.Settings.SetValue("Agent_Service_Command", startCommand);
            Instances.Settings.SetValue("Agent_Service_Workdir", workingDirectory);
            Instances.Settings.SetValue("Agent_Invoke_Path", invokePath);
        }

        private static string ReadProviderId(string providerId)
        {
            return string.Equals(providerId, "qwen", StringComparison.OrdinalIgnoreCase)
                ? "qwen"
                : "deepseek";
        }

        private static string GetProviderSettingKey(string providerId, string name)
        {
            return "AI_" + providerId + "_" + name;
        }

        private static bool IsApiKeyConfigured(string providerId)
        {
            string dpapi = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            string plain = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key"), "");
            string legacy = providerId == "deepseek" ? Instances.Settings.GetValue("AI_API_Key", "") : "";
            return !string.IsNullOrWhiteSpace(dpapi)
                || !string.IsNullOrWhiteSpace(plain)
                || !string.IsNullOrWhiteSpace(legacy);
        }

        private static void PersistApiKey(string providerId, string apiKeyPlain)
        {
            string key = apiKeyPlain ?? "";
            if (string.IsNullOrEmpty(key))
            {
                Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
                Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), "");
                if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                    Instances.Settings.SetValue("AI_API_Key", "");
                return;
            }

            if (DeploymentOptions.UseDpapiForApiKeys && ApiCredentialStore.TryProtectToBase64(key, out string enc))
            {
                Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), enc);
                Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), "");
                if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                    Instances.Settings.SetValue("AI_API_Key", "");
                return;
            }

            Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), key);
            if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                Instances.Settings.SetValue("AI_API_Key", key);
        }

        private static async Task HandleSendMessageAsync(string text, JArray attachments)
        {
            string composedText = BuildMessageWithAttachments(text, attachments);
            if (_isSending || string.IsNullOrWhiteSpace(composedText))
                return;

            Magpie.AddGhLog.Info("HandleSendMessageAsync begin");
            MagpieSettings.PersistAgentServiceModelConfig();
            AgentRuntimeHealth initialHealth = await MagpieServiceClient.GetHealthAsync();
            _isSending = true;
            _sendCts = new CancellationTokenSource();
            _sendCts.CancelAfter(TimeSpan.FromSeconds(75));
            PostToWeb("generation_state", new JObject
            {
                ["isGenerating"] = true,
                ["status"] = initialHealth.Success && !initialHealth.HostBridgeAvailable
                    ? "通讯中..."
                    : "Working..."
            });
            try
            {
                Magpie.AddGhLog.Info("HandleSendMessageAsync before streaming");
                var endpoints = MagpieServiceClient.ResolveStreamEndpoints(initialHealth);
                string chosenEndpoint = null;
                Exception lastException = null;
                foreach (string endpoint in endpoints)
                {
                    try
                    {
                        await MagpieServiceClient.SendStreamingAsync(
                            _sessionId,
                            composedText,
                            MagpieSettings.DefaultUserGoal,
                            endpoint,
                            evt => PostToWeb("agent_event", evt),
                            _sendCts.Token).ConfigureAwait(false);
                        chosenEndpoint = endpoint;
                        Magpie.AddGhLog.Info("HandleSendMessageAsync streaming completed on " + endpoint);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        Magpie.AddGhLog.Warn("Streaming endpoint " + endpoint + " failed: " + ex.Message);
                    }
                }

                if (chosenEndpoint == null)
                {
                    Magpie.AddGhLog.Warn("All streaming endpoints failed, falling back to non-streaming request. Last error: " + (lastException?.Message ?? "none"));
                    AgentServiceResponse output = await MagpieServiceClient.SendAsync(
                        _sessionId,
                        composedText,
                        MagpieSettings.DefaultUserGoal,
                        _sendCts.Token);
                    if (output.IsError)
                    {
                        PostToWeb("error_message", new JObject
                        {
                            ["text"] = output.ErrorText ?? "Unknown error."
                        });
                    }
                    else
                    {
                        PostToWeb("assistant_response", new JObject
                        {
                            ["mode"] = output.Mode ?? "agent",
                            ["status"] = output.Status ?? "completed",
                            ["finalText"] = output.FinalText ?? "",
                            ["outputText"] = output.OutputText ?? "",
                            ["events"] = new JArray(output.Events)
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Magpie.AddGhLog.Warn("HandleSendMessageAsync canceled");
                PostToWeb("system_message", new JObject
                {
                    ["text"] = "Generation stopped."
                });
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("HandleSendMessageAsync failed", ex);
                PostToWeb("error_message", new JObject
                {
                    ["text"] = "agent runtime connection failed: " + ex.Message
                });
            }
            finally
            {
                Magpie.AddGhLog.Info("HandleSendMessageAsync finally");
                _isSending = false;
                try { _sendCts?.Dispose(); } catch { }
                _sendCts = null;
                PostToWeb("generation_state", new JObject
                {
                    ["isGenerating"] = false
                });
            }
        }

        private static void StopGeneration()
        {
            try { _sendCts?.Cancel(); } catch { }
        }

        private static async Task HandleHistoryOpenAsync()
        {
            try
            {
                await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                var items = await MagpieServiceClient.ListSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                PostToWeb("history_items", new JObject
                {
                    ["items"] = items ?? new JArray()
                });
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Warn("HandleHistoryOpenAsync failed: " + ex.Message);
                PostToWeb("history_items", new JObject
                {
                    ["items"] = new JArray()
                });
            }
        }

        private static async Task HandleHistoryLoadAsync(string sessionId)
        {
            try
            {
                await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                var data = await MagpieServiceClient.GetSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                if (data == null)
                {
                    PostToWeb("error_message", new JObject { ["text"] = "Failed to load session." });
                    return;
                }
                _sessionId = sessionId;
                Magpie.AddGhLog.Info("Loaded Magpie session: " + sessionId);
                PostToWeb("session_loaded", new JObject
                {
                    ["sessionId"] = sessionId,
                    ["messages"] = data["recent_messages"] as JArray ?? new JArray()
                });
                _ = HandleHistoryOpenAsync();
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("HandleHistoryLoadAsync failed", ex);
                PostToWeb("error_message", new JObject { ["text"] = "Failed to load session: " + ex.Message });
            }
        }

        private static async Task HandleHistoryDeleteAsync(string sessionId)
        {
            try
            {
                await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                bool deleted = await MagpieServiceClient.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                if (!deleted)
                {
                    PostToWeb("error_message", new JObject { ["text"] = "Failed to delete session." });
                    return;
                }
                Magpie.AddGhLog.Info("Deleted Magpie session: " + sessionId);
                if (string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionId = Guid.NewGuid().ToString("N");
                    PostToWeb("session_loaded", new JObject
                    {
                        ["sessionId"] = _sessionId,
                        ["messages"] = new JArray()
                    });
                }
                _ = HandleHistoryOpenAsync();
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("HandleHistoryDeleteAsync failed", ex);
                PostToWeb("error_message", new JObject { ["text"] = "Failed to delete session: " + ex.Message });
            }
        }

        private static async Task HandleHistoryRenameAsync(string sessionId, string title)
        {
            try
            {
                await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                bool ok = await MagpieServiceClient.RenameSessionAsync(sessionId, title, CancellationToken.None).ConfigureAwait(false);
                if (!ok)
                {
                    PostToWeb("error_message", new JObject { ["text"] = "Failed to rename session." });
                    return;
                }
                _ = HandleHistoryOpenAsync();
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("HandleHistoryRenameAsync failed", ex);
                PostToWeb("error_message", new JObject { ["text"] = "Failed to rename session: " + ex.Message });
            }
        }

        private static string BuildMessageWithAttachments(string text, JArray attachments)
        {
            string trimmed = (text ?? "").Trim();
            if (attachments == null || attachments.Count == 0)
                return trimmed;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(trimmed))
                sb.AppendLine(trimmed);

            int remainingTextChars = 40000;
            foreach (JObject attachment in attachments.OfType<JObject>())
            {
                string name = attachment["name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = "attachment";

                string kind = attachment["kind"]?.ToString()?.Trim().ToLowerInvariant();
                string mimeType = attachment["mimeType"]?.ToString()?.Trim();
                long size = attachment["size"]?.Value<long?>() ?? 0L;

                if (string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase))
                {
                    string content = attachment["textContent"]?.ToString() ?? "";
                    if (remainingTextChars <= 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("[Uploaded text file omitted because the attachment budget was exhausted.]");
                        continue;
                    }

                    int take = Math.Min(content.Length, Math.Min(remainingTextChars, 12000));
                    string excerpt = take > 0 ? content.Substring(0, take) : "";
                    remainingTextChars -= take;

                    sb.AppendLine();
                    sb.AppendLine($"[Uploaded text file: {name}]");
                    if (!string.IsNullOrWhiteSpace(mimeType))
                        sb.AppendLine($"MIME type: {mimeType}");
                    if (size > 0)
                        sb.AppendLine($"Size: {size} bytes");
                    sb.AppendLine(excerpt);
                    if (take < content.Length)
                        sb.AppendLine("[Text truncated before sending.]");
                    continue;
                }

                sb.AppendLine();
                sb.AppendLine($"[Uploaded image: {name}]");
                if (!string.IsNullOrWhiteSpace(mimeType))
                    sb.AppendLine($"MIME type: {mimeType}");
                if (size > 0)
                    sb.AppendLine($"Size: {size} bytes");
                sb.AppendLine("The current runtime forwards image metadata to the agent shell, but does not yet send image pixels to the model.");
            }

            return sb.ToString().Trim();
        }

        private static async Task RefreshServiceStatusAsync()
        {
            try
            {
                var startup = await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                string status = MagpieServiceClient.DescribeHealth(startup);
                PostToWeb("status", new JObject
                {
                    ["text"] = status,
                    ["serviceUrl"] = MagpieSettings.ServiceBaseUrl,
                    ["isReady"] = startup.Health != null && startup.Health.Success,
                    ["startedProcess"] = startup.StartedProcess,
                    ["hostBridgeReady"] = startup.Health != null && startup.Health.HostBridgeAvailable
                });
            }
            catch (Exception ex)
            {
                PostToWeb("status", new JObject
                {
                    ["text"] = "agent runtime unavailable: " + ex.Message,
                    ["serviceUrl"] = MagpieSettings.ServiceBaseUrl,
                    ["isReady"] = false,
                    ["startedProcess"] = false,
                    ["hostBridgeReady"] = false
                });
            }
        }

        private static async Task WarmStartAgentServiceAsync()
        {
            try
            {
                Magpie.AddGhLog.Info("WarmStartAgentServiceAsync begin");
                MagpieSettings.PersistAgentServiceModelConfig();
                var startup = await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None);
                if (startup.Health != null && startup.Health.Success)
                {
                    Magpie.AddGhLog.Info("WarmStartAgentServiceAsync service ready");
                }
                else if (startup.TimedOut)
                {
                    Magpie.AddGhLog.Warn("WarmStartAgentServiceAsync timed out");
                }
                else if (!startup.StartConfigured)
                {
                    Magpie.AddGhLog.Warn("WarmStartAgentServiceAsync skipped: no start command configured");
                }
                else
                {
                    Magpie.AddGhLog.Warn("WarmStartAgentServiceAsync failed: " + (startup.Health?.Error ?? "unknown"));
                }
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("WarmStartAgentServiceAsync failed", ex);
            }
        }

        private static void PostToWeb(string type, JObject payload)
        {
            try
            {
                var root = new JObject
                {
                    ["type"] = type ?? "",
                    ["payload"] = payload ?? new JObject()
                };
                string json = root.ToString();
                var dispatcher = _uiDispatcher;
                var webView = _webView;
                if (webView == null || dispatcher == null)
                    return;

                if (dispatcher.CheckAccess())
                {
                    if (webView.CoreWebView2 == null)
                        return;
                    webView.CoreWebView2.PostWebMessageAsJson(json);
                    Magpie.AddGhLog.Info("PostToWeb sent: " + type);
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            webView.CoreWebView2?.PostWebMessageAsJson(json);
                            Magpie.AddGhLog.Info("PostToWeb sent via dispatcher: " + type);
                        }
                        catch (Exception ex)
                        {
                            Magpie.AddGhLog.Error("PostToWeb dispatcher failed for " + type, ex);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Magpie.AddGhLog.Error("PostToWeb failed for " + type, ex);
            }
        }

        private static string ResolveEntryPath()
        {
            string assemblyDir = null;
            string appBaseDir = null;
            try
            {
                assemblyDir = Path.GetDirectoryName(typeof(MagpieWindow).Assembly.Location);
            }
            catch
            {
            }

            try
            {
                appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
            }

            string[] baseDirs =
            {
                assemblyDir,
                appBaseDir,
                Environment.CurrentDirectory
            };

            foreach (var baseDir in baseDirs)
            {
                if (string.IsNullOrWhiteSpace(baseDir))
                    continue;

                string[] candidates =
                {
                    Path.Combine(baseDir, "AgentRuntime", "Web", "magpie-shell.html"),
                    Path.Combine(baseDir, "magpie-shell.html"),
                    Path.Combine(baseDir, "..", "AgentRuntime", "Web", "magpie-shell.html"),
                    Path.Combine(baseDir, "..", "..", "AgentRuntime", "Web", "magpie-shell.html"),
                    Path.Combine(baseDir, "..", "..", "..", "AgentRuntime", "Web", "magpie-shell.html")
                };

                foreach (var candidate in candidates)
                {
                    try
                    {
                        string full = Path.GetFullPath(candidate);
                        if (File.Exists(full))
                            return full;
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }
    }
}
