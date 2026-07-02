using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private sealed class GeneratedImageRecord
        {
            public string Path { get; set; }
            public string MimeType { get; set; }
            public string Prompt { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public string Intent { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private sealed class AiImageExecutionResult
        {
            public bool Success { get; set; }
            public string Intent { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public string Prompt { get; set; }
            public string Error { get; set; }
            public List<GeneratedImageRecord> Images { get; set; } = new List<GeneratedImageRecord>();
        }

        private static JObject NormalizeCanvasImageNode(JObject node)
        {
            if (node == null)
                return null;

            string nodeType = node["nodeType"]?.ToString();
            if (!string.Equals(nodeType, "image", StringComparison.OrdinalIgnoreCase))
                return node;

            JObject meta = node["meta"] as JObject ?? new JObject();
            string imagePath = meta["imagePath"]?.ToString() ?? "";
            string imageDataUrl = meta["imageDataUrl"]?.ToString() ?? "";
            string mimeType = meta["mimeType"]?.ToString() ?? node["mimeType"]?.ToString() ?? "image/png";

            if (string.IsNullOrWhiteSpace(imageDataUrl) && !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                meta["imageDataUrl"] = BuildImageDataUrl(imagePath, mimeType);

            node["meta"] = meta;
            return node;
        }

        private static async Task<string> ExecuteCreateAiImageAsync(string prompt, string intent, bool useUploadedImages, string aspectRatio, System.Threading.CancellationToken ct)
        {
            AiImageExecutionResult result = await RunAiImageGenerationAsync(prompt, intent, useUploadedImages, aspectRatio, ct).ConfigureAwait(false);
            return SerializeAiImageExecutionResult(result);
        }

        private static JObject BuildCanvasAiImageConfigPayload()
        {
            var providerSettings = GetImageProviderRuntimeSettings();
            return new JObject
            {
                ["success"] = !string.IsNullOrWhiteSpace(providerSettings?.ApiKey),
                ["providerId"] = GetCurrentImageProviderId(),
                ["provider"] = providerSettings?.Config?.DisplayName ?? "",
                ["baseUrl"] = providerSettings?.BaseUrl ?? "",
                ["model"] = providerSettings?.ModelName ?? "",
                ["apiKey"] = providerSettings?.ApiKey ?? "",
                ["hostProxy"] = true,
                ["proxyUrl"] = providerSettings?.ProxyUrl ?? "",
                ["error"] = string.IsNullOrWhiteSpace(providerSettings?.ApiKey) ? "Image API key is empty." : ""
            };
        }

        private static async Task RunCanvasAiImageNodeAsync(JObject payload)
        {
            string sourceRef = payload?["sourceRef"]?.ToString() ?? "";
            try
            {
                string prompt = payload?["prompt"]?.ToString() ?? "";
                string intent = payload?["intent"]?.ToString() ?? "generate";
                string aspectRatio = payload?["aspectRatio"]?.ToString() ?? "";
                int count = Math.Max(1, Math.Min(4, payload?["count"]?.ToObject<int?>() ?? 1));
                string size = payload?["size"]?.ToString() ?? "";
                string imageSize = payload?["imageSize"]?.ToString() ?? "";
                string imageDataUrl = ExtractCanvasImageDataUrl(payload?["image"]);

                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8)))
                {
                    var result = await RunCanvasAiImageGenerationAsync(
                        prompt,
                        intent,
                        aspectRatio,
                        count,
                        size,
                        imageSize,
                        imageDataUrl,
                        cts.Token).ConfigureAwait(false);

                    var images = new JArray((result.Images ?? new List<GeneratedImageRecord>()).Select(item => new JObject
                    {
                        ["dataUrl"] = BuildImageDataUrl(item.Path, item.MimeType),
                        ["path"] = item.Path,
                        ["mimeType"] = item.MimeType,
                        ["width"] = item.Width,
                        ["height"] = item.Height,
                        ["prompt"] = item.Prompt,
                        ["provider"] = item.Provider,
                        ["model"] = item.Model,
                        ["intent"] = item.Intent
                    }));

                    PostCanvasMessage("canvas_ai_image_result", new JObject
                    {
                        ["sourceRef"] = sourceRef,
                        ["success"] = result.Success,
                        ["provider"] = result.Provider,
                        ["model"] = result.Model,
                        ["prompt"] = result.Prompt,
                        ["size"] = size,
                        ["images"] = images,
                        ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                        ["error"] = result.Error ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("RunCanvasAiImageNodeAsync failed: " + ex.Message);
                PostCanvasMessage("canvas_ai_image_result", new JObject
                {
                    ["sourceRef"] = sourceRef,
                    ["success"] = false,
                    ["error"] = ex.Message
                });
            }
        }

        private static async Task<AiImageExecutionResult> RunCanvasAiImageGenerationAsync(string prompt, string intent, string aspectRatio, int count, string size, string imageSize, string imageDataUrl, System.Threading.CancellationToken ct)
        {
            var providerSettings = GetImageProviderRuntimeSettings();
            string normalizedIntent = string.Equals(intent, "edit", StringComparison.OrdinalIgnoreCase) ? "edit" : "generate";
            var outcome = new AiImageExecutionResult
            {
                Success = false,
                Intent = normalizedIntent,
                Provider = providerSettings?.Config?.DisplayName ?? "",
                Model = providerSettings?.ModelName ?? "",
                Prompt = prompt ?? "",
                Error = ""
            };

            if (string.IsNullOrWhiteSpace(providerSettings?.ApiKey))
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "Image generation failed: image API key is empty.");
                return outcome;
            }

            try
            {
                if (!IsGeminiNativeImageModel(providerSettings.ModelName))
                {
                    outcome.Error = BuildProviderDiagnostic(providerSettings, "Canvas image generation failed: canvas AI Image only supports Gemini native image models.");
                    return outcome;
                }

                string usedEndpoint = BuildGeminiNativeImageEndpoint(providerSettings.BaseUrl, providerSettings.ModelName);
                int requestedCount = Math.Max(1, Math.Min(4, count));
                var generatedImages = new List<GeneratedImageRecord>();
                string finalResponse = "";
                for (int i = 0; i < requestedCount; i++)
                {
                    finalResponse = await PostCanvasGeminiNativeImageAsync(providerSettings, usedEndpoint, prompt, imageDataUrl, aspectRatio, imageSize, ct).ConfigureAwait(false);
                    var saved = await SaveGeneratedImagesFromResponseAsync(finalResponse, prompt, normalizedIntent, providerSettings, ct).ConfigureAwait(false);
                    generatedImages.AddRange(saved);
                }
                outcome.Images = generatedImages;
                if (outcome.Images.Count == 0)
                {
                    outcome.Error = BuildProviderDiagnostic(providerSettings, "Image generation failed: request succeeded but no image data was found.", finalResponse, usedEndpoint);
                    return outcome;
                }

                outcome.Success = true;
                return outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "Canvas image generation failed: Gemini native request failed.", FormatExceptionChain(ex));
                return outcome;
            }
        }

        private static JObject BuildCanvasImageTaskRequestBody(ProviderRuntimeSettings providerSettings, string prompt, string intent, string aspectRatio, int count, string size, string imageSize, string imageDataUrl)
        {
            var body = new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["prompt"] = InjectImageGenerationTrigger(prompt),
                ["n"] = Math.Max(1, Math.Min(4, count))
            };

            if (!string.IsNullOrWhiteSpace(size))
                body["size"] = size.Trim();
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                body["aspect_ratio"] = aspectRatio.Trim();
            if (!string.IsNullOrWhiteSpace(imageSize))
                body["image_size"] = imageSize.Trim();
            if (!string.IsNullOrWhiteSpace(imageDataUrl))
                body["image"] = new JArray(imageDataUrl);
            if (string.Equals(intent, "edit", StringComparison.OrdinalIgnoreCase))
                body["intent"] = "edit";

            return body;
        }

        private static string InjectImageGenerationTrigger(string prompt)
        {
            string text = prompt ?? "";
            if (text.IndexOf("生成图片", StringComparison.OrdinalIgnoreCase) >= 0)
                return text;

            if (string.IsNullOrWhiteSpace(text))
                return "生成图片";

            return "生成图片\n" + text;
        }

        private static JObject BuildGeminiImageConfig(string aspectRatio, string imageSize)
        {
            var config = new JObject();
            string normalizedAspectRatio = NormalizeGeminiAspectRatio(aspectRatio);
            if (!string.IsNullOrWhiteSpace(normalizedAspectRatio))
                config["aspectRatio"] = normalizedAspectRatio;

            string normalizedImageSize = NormalizeGeminiImageSize(imageSize);
            if (!string.IsNullOrWhiteSpace(normalizedImageSize))
                config["imageSize"] = normalizedImageSize;

            return config;
        }

        private static string NormalizeGeminiAspectRatio(string aspectRatio)
        {
            string text = (aspectRatio ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (string.IsNullOrWhiteSpace(text) || text == "original")
                return "";

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "1:1",
                "3:4",
                "4:3",
                "9:16",
                "16:9",
                "21:9",
                "2:3",
                "3:2"
            };
            return allowed.Contains(text) ? text : "";
        }

        private static string NormalizeGeminiImageSize(string imageSize)
        {
            string text = (imageSize ?? "").Trim().ToUpperInvariant();
            return text == "1K" || text == "2K" || text == "4K" ? text : "";
        }

        private static bool IsImageTaskModel(string modelName)
        {
            string model = (modelName ?? "").Trim();
            return model.Equals("image2", StringComparison.OrdinalIgnoreCase)
                || model.Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGeminiNativeImageModel(string modelName)
        {
            string model = (modelName ?? "").Trim();
            return model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                && model.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<string> PostCanvasGeminiNativeImageAsync(ProviderRuntimeSettings providerSettings, string endpoint, string prompt, string imageDataUrl, string aspectRatio, string imageSize, System.Threading.CancellationToken ct)
        {
            string requestPrompt = InjectImageGenerationTrigger(prompt);
            var parts = new JArray
            {
                new JObject
                {
                    ["text"] = requestPrompt
                }
            };

            if (!string.IsNullOrWhiteSpace(imageDataUrl))
            {
                string mimeType = "image/png";
                string data = ExtractBase64Payload(imageDataUrl, out mimeType);
                if (!string.IsNullOrWhiteSpace(data))
                {
                    parts.Add(new JObject
                    {
                        ["inline_data"] = new JObject
                        {
                            ["mime_type"] = mimeType,
                            ["data"] = data
                        }
                    });
                }
            }

            var body = new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["parts"] = parts
                    }
                }
            };
            JObject imageConfig = BuildGeminiImageConfig(aspectRatio, imageSize);
            if (imageConfig.Count > 0)
            {
                body["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray("TEXT", "IMAGE"),
                    ["imageConfig"] = imageConfig
                };
            }
            else
            {
                body["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray("TEXT", "IMAGE")
                };
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using (var response = await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    string text = await ReadResponseTextAsync(response, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("POST " + endpoint + " returned HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + text);
                    ParseJsonObjectResponse(text, "POST " + endpoint);
                    return text;
                }
            }
        }

        private static async Task<string> PostImageChatCompletionAsync(ProviderRuntimeSettings providerSettings, string endpoint, JObject imageBody, string imageDataUrl, System.Threading.CancellationToken ct)
        {
            string requestPrompt = InjectImageGenerationTrigger(imageBody["prompt"]?.ToString());
            var content = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = requestPrompt
                }
            };

            if (!string.IsNullOrWhiteSpace(imageDataUrl))
            {
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject { ["url"] = imageDataUrl }
                });
            }

            var body = new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["stream"] = false,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = content
                    }
                }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using (var response = await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    string text = await ReadResponseTextAsync(response, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("POST " + endpoint + " returned HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + text);
                    ParseJsonObjectResponse(text, "POST " + endpoint);
                    return text;
                }
            }
        }

        private static async Task<JObject> PostImageTaskAsync(ProviderRuntimeSettings providerSettings, string endpoint, JObject body, System.Threading.CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using (var response = await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    string text = await ReadResponseTextAsync(response, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("POST " + endpoint + " returned HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + text);
                    return ParseJsonObjectResponse(text, "POST " + endpoint);
                }
            }
        }

        private static async Task<string> PollImageTaskAsync(ProviderRuntimeSettings providerSettings, string taskEndpoint, string taskId, System.Threading.CancellationToken ct)
        {
            string url = taskEndpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(taskId);
            JObject last = null;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (var response = await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        string text = await ReadResponseTextAsync(response, ct).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("GET " + url + " returned HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + text);

                        last = ParseJsonObjectResponse(text, "GET " + url);
                    }
                }

                string status = (last["status"]?.ToString() ?? "").Trim().ToLowerInvariant();
                if (status == "succeeded" || status == "success" || status == "completed")
                    return last.ToString();
                if (status == "failed" || status == "failure" || status == "cancelled" || status == "canceled")
                    throw new Exception("Image task failed: " + last.ToString());

                if (HasImageResponseData(last))
                    return last.ToString();
            }

            throw new TimeoutException("Image task timed out: " + (last?.ToString() ?? taskId));
        }

        private static JObject ParseJsonObjectResponse(string text, string context)
        {
            string trimmed = (text ?? "").TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed))
                throw new Exception(context + " returned an empty response.");
            if (trimmed[0] != '{')
            {
                string preview = trimmed.Length > 500 ? trimmed.Substring(0, 500) : trimmed;
                throw new Exception(context + " returned non-JSON response. Check the image Base URL. Preview:\n" + preview);
            }
            return JObject.Parse(trimmed);
        }

        private static string BuildImageTaskEndpoint(string baseUrl)
        {
            string raw = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "https://api.vibelearning.top/v1";

            raw = StripEndpointSuffix(raw, "/v1/images/generations");
            raw = StripEndpointSuffix(raw, "/v1/images/edits");
            raw = StripEndpointSuffix(raw, "/v1/images/tasks");

            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return raw + "/images/tasks";

            return raw + "/v1/images/tasks";
        }

        private static string BuildGeminiNativeImageEndpoint(string baseUrl, string modelName)
        {
            string raw = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "https://api.vibelearning.top";

            raw = StripEndpointSuffix(raw, "/v1/chat/completions");
            raw = StripEndpointSuffix(raw, "/v1/images/generations");
            raw = StripEndpointSuffix(raw, "/v1/images/edits");
            raw = StripEndpointSuffix(raw, "/v1/images/tasks");
            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 3).TrimEnd('/');

            string model = Uri.EscapeDataString((modelName ?? "").Trim());
            return raw + "/v1beta/models/" + model + ":generateContent";
        }

        private static string BuildImageChatCompletionEndpoint(string baseUrl)
        {
            string raw = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "https://api.vibelearning.top/v1";

            raw = StripEndpointSuffix(raw, "/v1/images/generations");
            raw = StripEndpointSuffix(raw, "/v1/images/edits");
            raw = StripEndpointSuffix(raw, "/v1/images/tasks");
            raw = StripEndpointSuffix(raw, "/v1/chat/completions");

            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return raw + "/chat/completions";

            return raw + "/v1/chat/completions";
        }

        private static string StripEndpointSuffix(string value, string suffix)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(suffix))
                return value ?? "";
            return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - suffix.Length).TrimEnd('/')
                : value;
        }

        private static bool HasImageResponseData(JObject root)
        {
            if (root == null) return false;
            if (root["b64_json"] != null || root["base64"] != null || root["image_base64"] != null || root["url"] != null || root["image_url"] != null || root["image"] != null)
                return true;
            if (HasImageResponseData(root["data"]))
                return true;
            if (HasImageResponseData(root["images"]))
                return true;
            if (HasImageResponseData(root["output"]))
                return true;
            return false;
        }

        private static bool HasImageResponseData(JToken token)
        {
            if (token == null) return false;
            if (token is JObject obj)
                return HasImageResponseData(obj);
            if (token is JArray array)
                return array.Any(HasImageResponseData);
            return false;
        }

        private static string ExtractCanvasImageDataUrl(JToken image)
        {
            if (image == null || image.Type == JTokenType.Null || image.Type == JTokenType.Undefined)
                return "";

            if (image is JValue)
                return image.ToString();

            var obj = image as JObject;
            if (obj == null)
                return "";

            string dataUrl = obj["dataUrl"]?.ToString() ?? obj["imageDataUrl"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(dataUrl))
                return dataUrl;

            string path = obj["path"]?.ToString() ?? obj["imagePath"]?.ToString() ?? "";
            string mimeType = obj["mimeType"]?.ToString() ?? "image/png";
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return BuildImageDataUrl(path, mimeType);

            return "";
        }

        private static async Task<AiImageExecutionResult> RunAiImageGenerationAsync(string prompt, string intent, bool useUploadedImages, string aspectRatio, System.Threading.CancellationToken ct)
        {
            string normalizedIntent = string.Equals(intent, "edit", StringComparison.OrdinalIgnoreCase) ? "edit" : "generate";
            var providerSettings = GetImageProviderRuntimeSettings();
            var outcome = new AiImageExecutionResult
            {
                Success = false,
                Intent = normalizedIntent,
                Provider = providerSettings?.Config?.DisplayName ?? "",
                Model = providerSettings?.ModelName ?? "",
                Prompt = prompt ?? "",
                Error = ""
            };

            if (string.IsNullOrWhiteSpace(providerSettings.ApiKey))
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "图片生成失败：请先配置图片生成模型的 API Key。");
                return outcome;
            }

            var sourceImages = useUploadedImages
                ? (_currentTurnAttachments ?? new List<AttachmentItem>()).Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)).ToList()
                : new List<AttachmentItem>();

            if (normalizedIntent == "edit" && sourceImages.Count != 1)
            {
                outcome.Error = sourceImages.Count == 0
                    ? "图片编辑需要当前轮恰好上传 1 张图片。"
                    : "v1 图片编辑仅支持单图编辑，请只保留 1 张原图。";
                return outcome;
            }

            HttpResponseMessage response = null;
            string usedEndpoint = null;
            try
            {
                if (normalizedIntent == "edit")
                {
                    usedEndpoint = BuildImageEndpoint(providerSettings.BaseUrl, true);
                    response = await SendImageEditRequestAsync(providerSettings, prompt, sourceImages[0], aspectRatio, usedEndpoint, ct).ConfigureAwait(false);
                }
                else
                {
                    usedEndpoint = BuildImageEndpoint(providerSettings.BaseUrl, false);
                    response = await SendImageGenerationRequestAsync(providerSettings, prompt, sourceImages, aspectRatio, usedEndpoint, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "图片生成失败：请求未能发送到图片模型服务，" + ex.GetType().Name, FormatExceptionChain(ex), usedEndpoint);
                return outcome;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                string errPreview = response == null ? "no_response" : await SafeReadErrorAsync(response).ConfigureAwait(false);
                outcome.Error = BuildProviderDiagnostic(
                    providerSettings,
                    "图片生成失败：图片模型服务返回 HTTP " + (response == null ? "?" : ((int)response.StatusCode).ToString()) + " " + (response?.ReasonPhrase ?? ""),
                    errPreview,
                    usedEndpoint);
                return outcome;
            }

            string responseText = await ReadResponseTextAsync(response, ct).ConfigureAwait(false);
            try
            {
                outcome.Images = await SaveGeneratedImagesFromResponseAsync(responseText, prompt, normalizedIntent, providerSettings, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "图片生成失败：响应解析或图片落盘失败，" + ex.GetType().Name, ex.Message, usedEndpoint);
                return outcome;
            }

            if (outcome.Images.Count == 0)
            {
                outcome.Error = BuildProviderDiagnostic(providerSettings, "图片生成失败：接口返回成功，但未找到可保存的图片结果。", responseText, usedEndpoint);
                return outcome;
            }

            outcome.Success = true;
            outcome.Error = "";
            return outcome;
        }

        private static string BuildImageEndpoint(string baseUrl, bool isEdit)
        {
            string raw = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "https://api.openai.com/v1";

            if (raw.EndsWith("/v1/images/generations", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith("/v1/images/edits", StringComparison.OrdinalIgnoreCase))
            {
                int suffixIndex = raw.LastIndexOf("/v1/images", StringComparison.OrdinalIgnoreCase);
                if (suffixIndex > 0)
                    raw = raw.Substring(0, suffixIndex);
            }

            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return raw + (isEdit ? "/images/edits" : "/images/generations");

            return raw + (isEdit ? "/v1/images/edits" : "/v1/images/generations");
        }

        private static async Task<HttpResponseMessage> SendImageGenerationRequestAsync(ProviderRuntimeSettings providerSettings, string prompt, List<AttachmentItem> sourceImages, string aspectRatio, string endpoint, System.Threading.CancellationToken ct)
        {
            var body = new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["prompt"] = InjectImageGenerationTrigger(prompt),
                ["response_format"] = "b64_json"
            };

            if (!string.IsNullOrWhiteSpace(aspectRatio))
                body["aspect_ratio"] = aspectRatio.Trim();

            if (sourceImages != null && sourceImages.Count > 0)
                body["image"] = new JArray(sourceImages.Select(image => $"data:{image.MimeType};base64,{image.Base64}"));

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            return await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> SendImageEditRequestAsync(ProviderRuntimeSettings providerSettings, string prompt, AttachmentItem sourceImage, string aspectRatio, string endpoint, System.Threading.CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerSettings.ApiKey);

            var form = new MultipartFormDataContent();
            form.Add(new StringContent(providerSettings.ModelName ?? ""), "model");
            form.Add(new StringContent(InjectImageGenerationTrigger(prompt)), "prompt");
            form.Add(new StringContent("b64_json"), "response_format");
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                form.Add(new StringContent(aspectRatio.Trim()), "aspect_ratio");

            byte[] bytes = await GetImageBytesFromAttachmentAsync(sourceImage, ct).ConfigureAwait(false);
            var imageContent = new ByteArrayContent(bytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(sourceImage.MimeType ?? "image/png");
            form.Add(imageContent, "image", sourceImage.FileName ?? "image.png");

            request.Content = form;
            return await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return "无法读取错误响应体：" + ex.Message;
            }
        }

        private static async Task<List<GeneratedImageRecord>> SaveGeneratedImagesFromResponseAsync(string responseText, string prompt, string intent, ProviderRuntimeSettings providerSettings, System.Threading.CancellationToken ct)
        {
            var result = new List<GeneratedImageRecord>();
            var root = JObject.Parse(responseText);
            var data = CollectGeneratedImageItems(root);
            int index = 0;
            foreach (var item in data.OfType<JObject>())
            {
                string mimeType = "image/png";
                byte[] bytes = null;

                string b64 = item["b64_json"]?.ToString()
                    ?? item["base64"]?.ToString()
                    ?? item["image_base64"]?.ToString();
                string url = item["url"]?.ToString()
                    ?? item["image_url"]?.ToString()
                    ?? item["image"]?.ToString();

                if (!string.IsNullOrWhiteSpace(b64) && !b64.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = DecodeImageBytes(b64, out string detectedMimeType);
                    mimeType = item["mime_type"]?.ToString()
                        ?? item["mimeType"]?.ToString()
                        ?? detectedMimeType
                        ?? mimeType;
                }
                else if (!string.IsNullOrWhiteSpace(url))
                {
                    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        bytes = DecodeImageBytes(url, out string detectedMimeType);
                        mimeType = detectedMimeType ?? mimeType;
                    }
                    else
                    {
                        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            bytes = await DownloadImageBytesAsync(url, providerSettings, ct).ConfigureAwait(false);
                            mimeType = GuessMimeTypeFromUrl(url) ?? mimeType;
                        }
                        else
                        {
                            bytes = DecodeImageBytes(url, out string detectedMimeType);
                            mimeType = detectedMimeType ?? mimeType;
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0)
                    continue;

                string path = SaveImageBytesToConversationPath(bytes, mimeType, index++);
                int imageWidth = 0;
                int imageHeight = 0;
                TryReadImageDimensions(path, out imageWidth, out imageHeight);
                result.Add(new GeneratedImageRecord
                {
                    Path = path,
                    MimeType = mimeType,
                    Prompt = prompt ?? "",
                    Provider = providerSettings?.Config?.DisplayName ?? "",
                    Model = providerSettings?.ModelName ?? "",
                    Intent = intent,
                    Width = imageWidth,
                    Height = imageHeight
                });
            }

            return result;
        }

        private static bool TryReadImageDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                using (var image = System.Drawing.Image.FromFile(path))
                {
                    width = image.Width;
                    height = image.Height;
                    return width > 0 && height > 0;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("TryReadImageDimensions failed: " + ex.Message);
                return false;
            }
        }

        private static JArray CollectGeneratedImageItems(JToken token)
        {
            var items = new JArray();
            CollectGeneratedImageItems(token, items);
            return items;
        }

        private static void CollectGeneratedImageItems(JToken token, JArray items)
        {
            if (token == null) return;
            if (token is JObject obj)
            {
                if (obj["b64_json"] != null || obj["base64"] != null || obj["image_base64"] != null ||
                    obj["url"] != null || obj["image_url"] != null || obj["image"] != null ||
                    obj["inlineData"] != null || obj["inline_data"] != null)
                {
                    items.Add(NormalizeGeneratedImageItem(obj));
                    return;
                }

                CollectGeneratedImageItems(obj["data"], items);
                CollectGeneratedImageItems(obj["images"], items);
                CollectGeneratedImageItems(obj["output"], items);
                CollectGeneratedImageItems(obj["result"], items);
                CollectGeneratedImageItems(obj["candidates"], items);
                CollectGeneratedImageItems(obj["choices"], items);
                CollectGeneratedImageItems(obj["message"], items);
                CollectGeneratedImageItems(obj["delta"], items);
                CollectGeneratedImageItems(obj["content"], items);
                CollectGeneratedImageItems(obj["parts"], items);
                return;
            }

            if (token is JArray array)
            {
                foreach (var child in array)
                    CollectGeneratedImageItems(child, items);
                return;
            }

            if (token is JValue value && value.Type == JTokenType.String)
            {
                foreach (var item in CollectImageItemsFromText(value.ToString()))
                    items.Add(item);
            }
        }

        private static JObject NormalizeGeneratedImageItem(JObject obj)
        {
            var inlineData = obj["inlineData"] as JObject ?? obj["inline_data"] as JObject;
            if (inlineData != null)
            {
                string data = inlineData["data"]?.ToString() ?? "";
                string mimeType = inlineData["mimeType"]?.ToString()
                    ?? inlineData["mime_type"]?.ToString()
                    ?? "image/png";
                return new JObject
                {
                    ["b64_json"] = data,
                    ["mime_type"] = mimeType
                };
            }

            return obj;
        }

        private static IEnumerable<JObject> CollectImageItemsFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            foreach (Match match in Regex.Matches(text, @"data:image\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase))
                yield return new JObject { ["b64_json"] = match.Value };

            foreach (Match match in Regex.Matches(text, @"https?:\/\/[^\s""'<>)]*\.(?:png|jpe?g|webp|gif)(?:\?[^\s""'<>)]*)?", RegexOptions.IgnoreCase))
                yield return new JObject { ["url"] = match.Value };
        }

        private static Task<byte[]> GetImageBytesFromAttachmentAsync(AttachmentItem sourceImage, System.Threading.CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(sourceImage?.Base64))
                return Task.FromResult(DecodeImageBytes(sourceImage.Base64, out _));

            if (!string.IsNullOrWhiteSpace(sourceImage?.Path) && File.Exists(sourceImage.Path))
                return Task.FromResult(File.ReadAllBytes(sourceImage.Path));

            return Task.FromResult(Array.Empty<byte>());
        }

        private static byte[] DecodeImageBytes(string raw, out string mimeType)
        {
            mimeType = "image/png";
            string value = ExtractBase64Payload(raw, out mimeType);
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<byte>();

            return Convert.FromBase64String(value);
        }

        private static string ExtractBase64Payload(string raw, out string mimeType)
        {
            mimeType = "image/png";
            string value = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
                return "";

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = value.IndexOf(',');
                if (commaIndex > 5)
                {
                    string header = value.Substring(5, commaIndex - 5);
                    string payload = value.Substring(commaIndex + 1);
                    string mime = header.Split(';').FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(mime))
                        mimeType = mime.Trim();
                    value = payload.Trim();
                }
            }

            return value.Replace("\r", "").Replace("\n", "").Trim();
        }

        private static async Task<byte[]> DownloadImageBytesAsync(string url, ProviderRuntimeSettings providerSettings, System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Array.Empty<byte>();

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await GetConfiguredHttpClient(providerSettings).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        private static string GuessMimeTypeFromUrl(string url)
        {
            string lower = (url ?? "").ToLowerInvariant();
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            if (lower.Contains(".webp")) return "image/webp";
            if (lower.Contains(".gif")) return "image/gif";
            if (lower.Contains(".bmp")) return "image/bmp";
            return "image/png";
        }

        private static string SaveImageBytesToConversationPath(byte[] bytes, string mimeType, int index)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Magpie",
                "generated-images",
                GetCurrentCanvasConversationId());
            Directory.CreateDirectory(dir);

            string extension = ".png";
            if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) extension = ".jpg";
            else if (string.Equals(mimeType, "image/webp", StringComparison.OrdinalIgnoreCase)) extension = ".webp";
            else if (string.Equals(mimeType, "image/gif", StringComparison.OrdinalIgnoreCase)) extension = ".gif";
            else if (string.Equals(mimeType, "image/bmp", StringComparison.OrdinalIgnoreCase)) extension = ".bmp";

            string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + "_" + index + extension;
            string path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string SerializeAiImageExecutionResult(AiImageExecutionResult result)
        {
            return new JObject
            {
                ["success"] = result?.Success ?? false,
                ["intent"] = result?.Intent ?? "",
                ["provider"] = result?.Provider ?? "",
                ["model"] = result?.Model ?? "",
                ["prompt"] = result?.Prompt ?? "",
                ["savedImages"] = new JArray((result?.Images ?? new List<GeneratedImageRecord>()).Select(item => new JObject
                {
                    ["path"] = item.Path,
                    ["mimeType"] = item.MimeType,
                    ["width"] = item.Width,
                    ["height"] = item.Height,
                    ["prompt"] = item.Prompt,
                    ["provider"] = item.Provider,
                    ["model"] = item.Model,
                    ["intent"] = item.Intent
                })),
                ["error"] = result?.Error ?? ""
            }.ToString();
        }

        private static void ApplyAiImageToolResult(string toolResultJson)
        {
            if (string.IsNullOrWhiteSpace(toolResultJson))
                return;

            try
            {
                var root = JObject.Parse(toolResultJson);
                bool success = root["success"]?.ToObject<bool>() ?? false;
                if (!success)
                {
                    string error = root["error"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(error))
                        AppendQuietDiagnosticCard("图片生成", error);
                    return;
                }

                var savedImages = root["savedImages"] as JArray;
                if (savedImages == null || savedImages.Count == 0)
                    return;

                var generated = new List<GeneratedImageRecord>();
                foreach (var item in savedImages.OfType<JObject>())
                {
                    generated.Add(new GeneratedImageRecord
                    {
                        Path = item["path"]?.ToString(),
                        MimeType = item["mimeType"]?.ToString() ?? "image/png",
                        Prompt = item["prompt"]?.ToString() ?? root["prompt"]?.ToString() ?? "",
                        Provider = item["provider"]?.ToString() ?? root["provider"]?.ToString() ?? "",
                        Model = item["model"]?.ToString() ?? root["model"]?.ToString() ?? "",
                        Intent = item["intent"]?.ToString() ?? root["intent"]?.ToString() ?? "",
                        Width = item["width"]?.ToObject<int?>() ?? 0,
                        Height = item["height"]?.ToObject<int?>() ?? 0
                    });
                }

                AppendGeneratedImageAssistantMessage(generated);
                SaveGeneratedImagesToCanvasSnapshot(generated);
            }
            catch (Exception ex)
            {
                AppendQuietDiagnosticCard("图片生成", "工具结果解析失败：" + ex.Message);
            }
        }

        private static void AppendGeneratedImageAssistantMessage(List<GeneratedImageRecord> generated)
        {
            if (generated == null || generated.Count == 0)
                return;

            var generatedImages = new JArray();
            foreach (var item in generated)
            {
                generatedImages.Add(new JObject
                {
                    ["path"] = item.Path,
                    ["mimeType"] = item.MimeType,
                    ["width"] = item.Width,
                    ["height"] = item.Height,
                    ["prompt"] = item.Prompt,
                    ["provider"] = item.Provider,
                    ["model"] = item.Model,
                    ["intent"] = item.Intent
                });
            }

            var messageNode = new JObject
            {
                ["role"] = "assistant",
                ["content"] = $"已生成 {generated.Count} 张图片。",
                ["generated_images"] = generatedImages
            };

            AppendBubble($"已生成 {generated.Count} 张图片。", false, false);
            AppendAssistantImageMessage(messageNode);
        }

        private static void SaveGeneratedImagesToCanvasSnapshot(List<GeneratedImageRecord> generated)
        {
            if (generated == null || generated.Count == 0)
                return;

            string conversationId = GetCurrentCanvasConversationId();
            JObject envelope = LoadCanvasConversationEnvelope(conversationId) ?? new JObject();
            JObject snapshot = envelope["snapshot"] as JObject ?? new JObject();
            snapshot["kind"] = "addgh-lightweight-canvas-v1";
            snapshot["viewport"] = snapshot["viewport"] as JObject ?? new JObject { ["x"] = 80, ["y"] = 90, ["z"] = 1.0 };
            var nodes = snapshot["nodes"] as JArray ?? new JArray();
            var connections = snapshot["connections"] as JArray ?? new JArray();
            var typedNodes = new JArray();

            foreach (var node in nodes.OfType<JObject>())
            {
                string sourceRef = node["sourceRef"]?.ToString() ?? "";
                if (IsCanvasPersistableTypedNodeSourceRef(sourceRef))
                    typedNodes.Add(NormalizeCanvasImageNode((JObject)node.DeepClone()));
            }

            double centerX = 160;
            double centerY = 120;
            if (snapshot["viewport"] is JObject viewport)
            {
                centerX = viewport["x"]?.ToObject<double?>() ?? 80;
                centerY = viewport["y"]?.ToObject<double?>() ?? 90;
            }

            for (int i = 0; i < generated.Count; i++)
            {
                var item = generated[i];
                string sourceRef = $"generated_image:{conversationId}:{DateTime.UtcNow.Ticks}:{i}";
                double nodeWidth = item.Width > 0 ? Math.Min(520, Math.Max(160, item.Width)) : 360;
                double nodeHeight = item.Width > 0 && item.Height > 0
                    ? Math.Min(420, Math.Max(110, nodeWidth * item.Height / item.Width))
                    : 260;
                typedNodes.Add(NormalizeCanvasImageNode(new JObject
                {
                    ["id"] = "node:" + sourceRef.Replace(":", "_"),
                    ["sourceRef"] = sourceRef,
                    ["nodeType"] = "image",
                    ["x"] = centerX + i * 380,
                    ["y"] = centerY,
                    ["w"] = nodeWidth,
                    ["h"] = nodeHeight,
                    ["meta"] = new JObject
                    {
                        ["sourceRef"] = sourceRef,
                        ["nodeType"] = "image",
                        ["title"] = "Generated Image",
                        ["summary"] = "AI image result",
                        ["body"] = item.Prompt ?? "",
                        ["imagePath"] = item.Path,
                        ["imageDataUrl"] = BuildImageDataUrl(item.Path, item.MimeType),
                        ["mimeType"] = item.MimeType ?? "image/png",
                        ["naturalWidth"] = item.Width,
                        ["naturalHeight"] = item.Height,
                        ["prompt"] = item.Prompt ?? "",
                        ["provider"] = item.Provider ?? "",
                        ["model"] = item.Model ?? "",
                        ["intent"] = item.Intent ?? "",
                        ["ports"] = new JArray
                        {
                            new JObject { ["id"] = "in", ["label"] = "Input", ["direction"] = "input", ["dataType"] = "image", ["slot"] = 0 },
                            new JObject { ["id"] = "out", ["label"] = "Output", ["direction"] = "output", ["dataType"] = "image", ["slot"] = 1 }
                        },
                        ["w"] = nodeWidth,
                        ["h"] = nodeHeight
                    }
                }));
            }

            snapshot["nodes"] = typedNodes;
            snapshot["connections"] = connections;
            SaveCanvasConversationSnapshot(conversationId, snapshot, envelope["cardMetaPatches"]);
            NotifyCanvasConversationChanged(true);
        }

        private static void SavePromptAndInputImagesToCanvasSnapshot(string promptText, List<AttachmentItem> attachments)
        {
            // User prompts and uploaded images stay in chat/model context only.
            // They are intentionally not mirrored into the frontend canvas.
        }
    }
}
