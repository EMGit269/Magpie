using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly object _httpClientCacheLock = new object();
        private static readonly Dictionary<string, HttpClient> _httpClientCache = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);

        private static JObject BuildChatRequestBody(ProviderRuntimeSettings providerSettings, List<object> messagesToSend, object[] toolDefinitions)
        {
            bool useStream = providerSettings?.Config?.ProviderId != null
                && providerSettings.Config.ProviderId.Equals("custom", StringComparison.OrdinalIgnoreCase);

            var body = new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["messages"] = BuildOutboundMessagesForRequest(messagesToSend),
                ["stream"] = useStream,
                ["temperature"] = 0.3
            };

            if (providerSettings.Config.SupportsTools)
            {
                body["tools"] = JToken.FromObject(toolDefinitions);
            }

            if (providerSettings.Config.EnableThinking)
            {
                body["thinking"] = new JObject { { "type", "enabled" } };
            }

            if (!string.IsNullOrWhiteSpace(providerSettings.Config.DefaultReasoningEffort))
            {
                body["reasoning_effort"] = providerSettings.Config.DefaultReasoningEffort;
            }

            return body;
        }

        private static JArray BuildOutboundMessagesForRequest(List<object> messagesToSend)
        {
            var result = new JArray();
            if (messagesToSend == null) return result;

            foreach (var msg in messagesToSend)
            {
                JObject jo;
                if (msg is JObject existing)
                    jo = (JObject)existing.DeepClone();
                else if (msg is JToken token && token.Type == JTokenType.Object)
                    jo = (JObject)token.DeepClone();
                else
                    jo = JObject.FromObject(msg);

                string role = jo["role"]?.ToString();
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    jo.Remove("name");
                }
                else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveLocalDisplayMetadata(jo);
                    NormalizeAssistantToolCallsForRequest(jo);
                    if (jo["tool_calls"] != null && (jo["content"] == null || jo["content"].Type == JTokenType.Null))
                        jo["content"] = "";
                }
                else
                {
                    RemoveLocalDisplayMetadata(jo);
                }

                result.Add(jo);
            }

            return result;
        }

        private static void RemoveLocalDisplayMetadata(JObject message)
        {
            if (message == null) return;
            var names = message.Properties()
                .Where(p => p.Name.StartsWith("_display_", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList();
            foreach (string name in names)
                message.Remove(name);
        }

        private static void NormalizeAssistantToolCallsForRequest(JObject message)
        {
            if (message == null) return;
            var toolCalls = message["tool_calls"] as JArray;
            if (toolCalls != null && toolCalls.Count == 0)
                message.Remove("tool_calls");
        }

        private static List<EndpointCandidate> BuildEndpointCandidates(string baseUrl)
        {
            string raw = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "https://api.openai.com/v1";

            var candidates = new List<EndpointCandidate>();
            Action<string, bool> add = (url, isFallback) =>
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                if (candidates.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase))) return;
                candidates.Add(new EndpointCandidate { Url = url, IsFallback = isFallback });
            };

            if (raw.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                add(raw, false);
                return candidates;
            }

            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                add(raw + "/chat/completions", false);
                return candidates;
            }

            add(raw + "/v1/chat/completions", false);
            add(raw + "/chat/completions", true);
            return candidates;
        }

        private static bool ShouldTryNextEndpoint(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code == 404 || code == 405 || code == 415 || code == 422;
        }

        private static void ApplyBrowserLikeHeaders(HttpRequestMessage request, string url, bool wantsStream)
        {
            if (request == null || string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return;

            request.Headers.TryAddWithoutValidation("Accept", wantsStream ? "text/event-stream" : "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            request.Headers.TryAddWithoutValidation("DNT", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
            request.Headers.TryAddWithoutValidation("Referer", uri.GetLeftPart(UriPartial.Authority) + "/");
        }

        private static IWebProxy TryGetSystemProxy()
        {
            try
            {
                if (!IsSystemProxyConfigured())
                    return null;

                return WebRequest.DefaultWebProxy;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Read system proxy failed: " + ex.Message);
                return null;
            }
        }

        private static bool IsSystemProxyConfigured()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    if (key == null) return false;

                    bool proxyEnable = false;
                    bool autoDetect = false;
                    string proxyServer = "";
                    string autoConfigUrl = "";

                    object proxyEnableObj = key.GetValue("ProxyEnable");
                    if (proxyEnableObj != null)
                        proxyEnable = Convert.ToInt32(proxyEnableObj) != 0;

                    object autoDetectObj = key.GetValue("AutoDetect");
                    if (autoDetectObj != null)
                        autoDetect = Convert.ToInt32(autoDetectObj) != 0;

                    proxyServer = (key.GetValue("ProxyServer") as string ?? "").Trim();
                    autoConfigUrl = (key.GetValue("AutoConfigURL") as string ?? "").Trim();

                    if (proxyEnable && !string.IsNullOrWhiteSpace(proxyServer))
                        return true;

                    if (!string.IsNullOrWhiteSpace(autoConfigUrl))
                        return true;

                    if (autoDetect)
                        return true;

                    return false;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Detect system proxy config failed: " + ex.Message);
                return false;
            }
        }

        private static string GetHttpTransportCacheKey(ProviderRuntimeSettings providerSettings)
        {
            string proxy = providerSettings?.ProxyUrl?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(proxy))
                return "proxy:" + proxy.ToLowerInvariant();

            return TryGetSystemProxy() != null ? "proxy:[system]" : "direct";
        }

        private static string DescribeTransport(ProviderRuntimeSettings providerSettings)
        {
            string proxy = providerSettings?.ProxyUrl?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(proxy))
                return "custom proxy " + proxy;

            return TryGetSystemProxy() != null ? "system proxy" : "direct";
        }

        private static HttpClient CreateConfiguredHttpClient(ProviderRuntimeSettings providerSettings)
        {
            var handler = new HttpClientHandler();
            try
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Enable automatic decompression failed: " + ex.Message);
            }

            IWebProxy proxy = null;
            if (!string.IsNullOrWhiteSpace(providerSettings?.ProxyUrl))
                proxy = new WebProxy(providerSettings.ProxyUrl, false);
            else
                proxy = TryGetSystemProxy();

            if (proxy != null)
            {
                handler.UseProxy = true;
                handler.Proxy = proxy;
                try
                {
                    handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("Set proxy credentials failed: " + ex.Message);
                }
            }
            else
            {
                handler.UseProxy = false;
            }

            return new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(5) };
        }

        private static HttpClient GetConfiguredHttpClient(ProviderRuntimeSettings providerSettings)
        {
            string key = GetHttpTransportCacheKey(providerSettings);
            lock (_httpClientCacheLock)
            {
                if (_httpClientCache.TryGetValue(key, out HttpClient existing))
                    return existing;

                var created = CreateConfiguredHttpClient(providerSettings);
                _httpClientCache[key] = created;
                return created;
            }
        }

        private static async Task<HttpResponseMessage> SendProviderRequestAsync(ProviderRuntimeSettings providerSettings, JObject requestBody, string url, System.Threading.CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {providerSettings.ApiKey}");
            if (providerSettings?.Config?.ProviderId == "custom")
                ApplyBrowserLikeHeaders(request, url, requestBody?["stream"]?.ToObject<bool>() ?? false);
            request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            return await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        private static async Task<string> ReadResponseTextAsync(HttpResponseMessage response, System.Threading.CancellationToken ct)
        {
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new System.IO.StreamReader(stream))
            {
                var task = reader.ReadToEndAsync();
                while (!task.IsCompleted)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct);
                }
                return task.Result;
            }
        }

        private static async Task<string> PreprocessImageAttachmentsAsync(string input, List<AttachmentItem> attachments, System.Threading.CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var providerSettings = GetVisionProviderRuntimeSettings();

            if (string.IsNullOrWhiteSpace(providerSettings.ApiKey))
            {
                string diag = BuildProviderDiagnostic(providerSettings, "图片理解失败：请先配置 " + providerSettings.Config.DisplayName + " 的 API Key。");
                AddGhLog.Warn("Vision preprocess: " + diag.Replace("\r", " ").Replace("\n", " | "));
                AppendQuietDiagnosticCard("图片理解", diag);
                return null;
            }

            if (attachments == null || !attachments.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
            {
                AppendQuietDiagnosticCard("图片理解", "未找到可发送给视觉模型的图片附件。");
                return null;
            }

            JObject requestBody = BuildVisionPreprocessRequestBody(providerSettings, input, attachments);
            HttpResponseMessage response = null;
            string usedEndpoint = null;
            string lastEndpointError = null;
            DateTime startTime = DateTime.Now;

            try
            {
                ShowThinkingAnimation("理解中...");
                foreach (var endpoint in BuildEndpointCandidates(providerSettings.BaseUrl))
                {
                    ct.ThrowIfCancellationRequested();
                    usedEndpoint = endpoint.Url;
                    AddGhLog.Info("Trying vision endpoint: " + endpoint.Url + ", model=" + providerSettings.ModelName);

                    response = await SendProviderRequestAsync(providerSettings, requestBody, endpoint.Url, ct);
                    if (response.IsSuccessStatusCode)
                        break;

                    string errPreview = "";
                    try { errPreview = await response.Content.ReadAsStringAsync(); }
                    catch (Exception readEx) { errPreview = "无法读取错误响应体：" + readEx.Message; }

                    lastEndpointError = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + ClampDiagDetail(errPreview, 900);
                    AddGhLog.Warn("Vision endpoint failed: " + endpoint.Url + " | " + lastEndpointError.Replace("\r", " ").Replace("\n", " | "));

                    if (!ShouldTryNextEndpoint(response.StatusCode))
                    {
                        AppendQuietDiagnosticCard("图片理解",
                            BuildProviderDiagnostic(providerSettings, "图片理解失败：视觉模型服务返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, errPreview, endpoint.Url));
                        return null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendQuietDiagnosticCard("图片理解",
                    BuildProviderDiagnostic(providerSettings, "图片理解失败：请求未能发送到视觉模型服务，" + ex.GetType().Name, FormatExceptionChain(ex), usedEndpoint));
                return null;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                AppendQuietDiagnosticCard("图片理解",
                    BuildProviderDiagnostic(providerSettings, "图片理解失败：视觉模型服务没有返回成功响应。", lastEndpointError, usedEndpoint));
                return null;
            }

            string responseJson = await ReadResponseTextAsync(response, ct);
            if (!TryParseAssistantMessageFromResponse(responseJson, out JObject messageNode, out string parseError))
            {
                AppendQuietDiagnosticCard("图片理解",
                    BuildProviderDiagnostic(providerSettings, "图片理解失败：视觉模型响应不是可解析的聊天响应，" + parseError, responseJson, usedEndpoint));
                return null;
            }

            string analysis = messageNode["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(analysis))
                analysis = messageNode["reasoning_content"]?.ToString();

            if (string.IsNullOrWhiteSpace(analysis))
            {
                AppendQuietDiagnosticCard("图片理解",
                    BuildProviderDiagnostic(providerSettings, "图片理解失败：视觉模型返回成功，但没有输出图片分析。", responseJson, usedEndpoint));
                return null;
            }

            double durationSeconds = (DateTime.Now - startTime).TotalSeconds;
            AppendCollapsibleBubble(analysis.Trim(), "图片理解 " + Math.Round(durationSeconds, 1) + "s", "🖼");
            return analysis.Trim();
        }

        private static async Task<string> RunFinalVisualReviewLegacyAsync(string priorDraft, System.Threading.CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_pendingFinalVisualReview || _finalVisualReviewCompleted || !_currentTurnHadToolExecution)
                return null;

            var sourceImages = _finalVisualReviewSourceImages ?? new List<AttachmentItem>();
            if (sourceImages.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(_visualReviewPreviewComponentId))
            {
                AppendQuietDiagnosticCard("最终视觉复核", "未提前准备视觉预览出口；截图将继续使用当前可见预览，可能受过程几何干扰。");
            }
            else try
            {
                string previewCleanup = ExecuteSetAllCSharpScriptPreviews(false);
                if (!string.IsNullOrWhiteSpace(previewCleanup) && !previewCleanup.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    _messages.Add(new { role = "tool", tool_call_id = "final_visual_review_cleanup", name = "set_all_csharp_script_previews", content = previewCleanup });
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("Final visual review preview cleanup failed: " + ex.Message);
            }

            string captureJson = ExecuteCaptureRhinoViewport("auto", 1600, 900, 0.12);
            if (string.IsNullOrWhiteSpace(captureJson) || captureJson.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                AppendQuietDiagnosticCard("最终视觉复核", string.IsNullOrWhiteSpace(captureJson) ? "截图失败。": captureJson);
                return null;
            }

            string screenshotPath = null;
            try
            {
                screenshotPath = JObject.Parse(captureJson)["path"]?.ToString();
            }
            catch (Exception ex)
            {
                AppendQuietDiagnosticCard("最终视觉复核", "截图结果解析失败：" + ex.Message);
                return null;
            }

            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
            {
                AppendQuietDiagnosticCard("最终视觉复核", "截图结果缺少有效文件路径。");
                return null;
            }

            var providerSettings = GetVisionProviderRuntimeSettings();
            if (string.IsNullOrWhiteSpace(providerSettings.ApiKey))
            {
                string diag = BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：请先配置 " + providerSettings.Config.DisplayName + " 的 API Key。");
                AppendQuietDiagnosticCard("最终视觉复核", diag);
                return null;
            }

            JObject requestBody = BuildFinalVisualReviewRequestBody(
                providerSettings,
                _finalVisualReviewSourceInput,
                sourceImages,
                screenshotPath,
                priorDraft);

            HttpResponseMessage response = null;
            string usedEndpoint = null;
            string lastEndpointError = null;
            DateTime startTime = DateTime.Now;
            try
            {
                ShowThinkingAnimation("复核中...");
                foreach (var endpoint in BuildEndpointCandidates(providerSettings.BaseUrl))
                {
                    ct.ThrowIfCancellationRequested();
                    usedEndpoint = endpoint.Url;
                    response = await SendProviderRequestAsync(providerSettings, requestBody, endpoint.Url, ct);
                    if (response.IsSuccessStatusCode)
                        break;

                    string errPreview = "";
                    try { errPreview = await response.Content.ReadAsStringAsync(); }
                    catch (Exception readEx) { errPreview = "无法读取错误响应体：" + readEx.Message; }

                    lastEndpointError = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + ClampDiagDetail(errPreview, 900);
                    if (!ShouldTryNextEndpoint(response.StatusCode))
                    {
                        AppendQuietDiagnosticCard("最终视觉复核",
                            BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：视觉模型服务返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, errPreview, endpoint.Url));
                        return null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendQuietDiagnosticCard("最终视觉复核",
                    BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：请求未能发送到视觉模型服务，" + ex.GetType().Name, FormatExceptionChain(ex), usedEndpoint));
                return null;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                AppendQuietDiagnosticCard("最终视觉复核",
                    BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：视觉模型服务没有返回成功响应。", lastEndpointError, usedEndpoint));
                return null;
            }

            string responseJson = await ReadResponseTextAsync(response, ct);
            if (!TryParseAssistantMessageFromResponse(responseJson, out JObject messageNode, out string parseError))
            {
                AppendQuietDiagnosticCard("最终视觉复核",
                    BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：视觉模型响应不是可解析的聊天响应，" + parseError, responseJson, usedEndpoint));
                return null;
            }

            string analysis = messageNode["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(analysis))
                analysis = messageNode["reasoning_content"]?.ToString();
            if (string.IsNullOrWhiteSpace(analysis))
            {
                AppendQuietDiagnosticCard("最终视觉复核",
                    BuildProviderDiagnostic(providerSettings, "最终视觉复核失败：视觉模型返回成功，但没有输出复核结论。", responseJson, usedEndpoint));
                return null;
            }

            double durationSeconds = (DateTime.Now - startTime).TotalSeconds;
            AppendCollapsibleBubble(analysis.Trim(), "最终视觉复核 " + Math.Round(durationSeconds, 1) + "s", "🖼");
            return analysis.Trim();
        }

        private static string BuildProviderDiagnostic(ProviderRuntimeSettings providerSettings, string headline, string detail = null, string usedUrl = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(headline)) sb.AppendLine(headline.Trim());

            sb.AppendLine("Provider: " + (providerSettings?.Config?.DisplayName ?? "(unknown)"));
            sb.AppendLine("Model: " + (providerSettings?.ModelName ?? "(empty)"));
            sb.AppendLine("Base URL: " + (providerSettings?.BaseUrl ?? "(empty)"));
            sb.AppendLine("Transport: " + DescribeTransport(providerSettings));
            if (!string.IsNullOrWhiteSpace(usedUrl))
                sb.AppendLine("Endpoint: " + usedUrl.Trim());

            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.AppendLine();
                sb.AppendLine(ClampDiagDetail(detail, 900));
            }

            return sb.ToString().Trim();
        }

        private static ApiResponse ReturnProviderError(ProviderRuntimeSettings providerSettings, string category, string headline, string detail = null, string usedUrl = null)
        {
            string diag = BuildProviderDiagnostic(providerSettings, headline, detail, usedUrl);
            AddGhLog.Warn(category + ": " + diag.Replace("\r", " ").Replace("\n", " | "));
            AppendQuietDiagnosticCard(category, diag);
            return new ApiResponse { Content = "Error: " + diag };
        }

        private static string FormatExceptionChain(Exception ex)
        {
            if (ex == null) return "";
            var sb = new StringBuilder();
            int depth = 0;
            for (Exception cur = ex; cur != null && depth < 6; cur = cur.InnerException, depth++)
            {
                if (depth > 0) sb.AppendLine();
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
            }
            return sb.ToString();
        }

        private static void MergeToolCallDeltas(Dictionary<int, JObject> toolCallsByIndex, JArray deltaToolCalls)
        {
            if (toolCallsByIndex == null || deltaToolCalls == null) return;

            foreach (var token in deltaToolCalls)
            {
                var delta = token as JObject;
                if (delta == null) continue;

                int index = ResolveToolCallDeltaIndex(toolCallsByIndex, delta);
                if (!toolCallsByIndex.TryGetValue(index, out JObject target))
                {
                    target = new JObject();
                    toolCallsByIndex[index] = target;
                }

                if (delta["id"] != null) target["id"] = delta["id"];
                if (delta["type"] != null) target["type"] = delta["type"];

                var deltaFn = delta["function"] as JObject;
                if (deltaFn == null) continue;

                var fn = target["function"] as JObject;
                if (fn == null)
                {
                    fn = new JObject();
                    target["function"] = fn;
                }

                if (deltaFn["name"] != null) fn["name"] = deltaFn["name"];
                if (deltaFn["arguments"] != null)
                {
                    string currentArgs = fn["arguments"]?.ToString() ?? "";
                    fn["arguments"] = currentArgs + deltaFn["arguments"].ToString();
                }
            }
        }

        private static int ResolveToolCallDeltaIndex(Dictionary<int, JObject> toolCallsByIndex, JObject delta)
        {
            int? explicitIndex = delta["index"]?.ToObject<int?>();
            if (explicitIndex.HasValue) return explicitIndex.Value;

            string id = delta["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                foreach (var kv in toolCallsByIndex)
                    if (string.Equals(kv.Value["id"]?.ToString(), id, StringComparison.Ordinal))
                        return kv.Key;
            }

            if (toolCallsByIndex.Count == 1)
                return toolCallsByIndex.Keys.First();

            return toolCallsByIndex.Count;
        }

        private static bool TryParseAssistantMessageFromResponse(string responseText, out JObject messageNode, out string parseError)
        {
            messageNode = null;
            parseError = null;

            if (string.IsNullOrWhiteSpace(responseText))
            {
                parseError = "响应内容为空。";
                return false;
            }

            string trimmedStart = responseText.TrimStart();
            if (trimmedStart.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return TryParseAssistantMessageFromSse(responseText, out messageNode, out parseError);

            try
            {
                var json = JObject.Parse(responseText);
                var msg = json["choices"]?[0]?["message"] as JObject;
                if (msg == null)
                {
                    parseError = "JSON 里缺少 choices[0].message。";
                    return false;
                }

                NormalizeAssistantToolCallsForRequest(msg);
                messageNode = msg;
                return true;
            }
            catch (Exception ex)
            {
                parseError = ex.Message;
                return false;
            }
        }

        private static bool TryParseAssistantMessageFromSse(string responseText, out JObject messageNode, out string parseError)
        {
            messageNode = null;
            parseError = null;

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var toolCallsByIndex = new Dictionary<int, JObject>();
            bool sawAnyData = false;
            bool sawChoiceChunk = false;
            bool sawUsageOnlyChunk = false;
            bool sawFinalMessage = false;

            string[] lines = responseText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                string payload = line.Substring(5).TrimStart();
                if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    continue;

                sawAnyData = true;

                JObject chunk;
                try
                {
                    chunk = JObject.Parse(payload);
                }
                catch (Exception ex)
                {
                    parseError = "SSE chunk 不是有效 JSON：" + ex.Message;
                    return false;
                }

                var choices = chunk["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    if (chunk["usage"] != null) sawUsageOnlyChunk = true;
                    continue;
                }

                var choice0 = choices[0] as JObject;
                if (choice0 == null) continue;
                sawChoiceChunk = true;

                var message = choice0["message"] as JObject;
                if (message != null)
                {
                    NormalizeAssistantToolCallsForRequest(message);
                    messageNode = message;
                    sawFinalMessage = true;
                    continue;
                }

                var delta = choice0["delta"] as JObject;
                if (delta == null) continue;

                string deltaContent = delta["content"]?.ToString();
                if (!string.IsNullOrEmpty(deltaContent)) content.Append(deltaContent);

                string deltaReasoning = delta["reasoning_content"]?.ToString();
                if (!string.IsNullOrEmpty(deltaReasoning)) reasoning.Append(deltaReasoning);

                string altDeltaReasoning = delta["reasoning"]?.ToString();
                if (!string.IsNullOrEmpty(altDeltaReasoning)) reasoning.Append(altDeltaReasoning);

                MergeToolCallDeltas(toolCallsByIndex, delta["tool_calls"] as JArray);
            }

            if (sawFinalMessage)
                return true;

            if (content.Length > 0 || reasoning.Length > 0 || toolCallsByIndex.Count > 0)
            {
                var synthesized = new JObject { ["role"] = "assistant" };
                if (content.Length > 0 || toolCallsByIndex.Count > 0) synthesized["content"] = content.ToString();
                if (reasoning.Length > 0) synthesized["reasoning_content"] = reasoning.ToString();
                if (toolCallsByIndex.Count > 0)
                {
                    var toolCalls = new JArray();
                    foreach (var kv in toolCallsByIndex.OrderBy(kv => kv.Key))
                    {
                        if (string.IsNullOrWhiteSpace(kv.Value["id"]?.ToString()))
                            kv.Value["id"] = "call_" + kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (string.IsNullOrWhiteSpace(kv.Value["type"]?.ToString()))
                            kv.Value["type"] = "function";
                        toolCalls.Add(kv.Value);
                    }
                    synthesized["tool_calls"] = toolCalls;
                }
                messageNode = synthesized;
                return true;
            }

            if (sawUsageOnlyChunk && !sawChoiceChunk)
                parseError = "SSE 只返回了 usage 结束块，没有返回任何 choices/delta 内容。通常表示中转站没有实际输出模型流，或请求体被上游拒绝但被包装成空流。";
            else
                parseError = sawAnyData ? "SSE 响应里没有可提取的 assistant 内容。" : "未找到任何 data: 事件。";
            return false;
        }
    }
}
