using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static string _visualReviewTargetSourceId = null;
        private static int _visualReviewTargetOutputIndex = 0;

        private static void ResetVisualWorkflowState(string input, List<AttachmentItem> attachmentsToSend)
        {
            bool hasImageAttachments = attachmentsToSend.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64));
            _currentTurnHadToolExecution = false;
            _finalVisualReviewCompleted = false;
            _finalVisualReviewAttempted = false;
            _hasActiveVisionInputContext = hasImageAttachments;
            _pendingFinalVisualReview = false;
            _finalVisualReviewSourceInput = input;
            _finalVisualReviewSourceImages = hasImageAttachments
                ? attachmentsToSend
                    .Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64))
                    .ToList()
                : new List<AttachmentItem>();
            _visualReviewPreviewComponentId = null;
            _visualReviewTargetSourceId = null;
            _visualReviewTargetOutputIndex = 0;
        }

        private static bool IsVisionToolContextActive()
        {
            return _hasActiveVisionInputContext
                || (_finalVisualReviewSourceImages != null && _finalVisualReviewSourceImages.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)));
        }

        private static bool CanUseViewportCaptureTool()
        {
            return true;
        }

        private static async Task<bool> PrepareImageDrivenExecutionContextAsync(string input, List<AttachmentItem> attachmentsToSend, System.Threading.CancellationToken ct)
        {
            if (!attachmentsToSend.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
                return true;

            bool primaryModelReceivesImages = ShouldIncludeImagesInPrimaryModelMessage(_activeImageInputRoute);
            if (primaryModelReceivesImages)
                return true;

            string visionAnalysis = await PreprocessImageAttachmentsAsync(input, attachmentsToSend, ct);
            if (string.IsNullOrWhiteSpace(visionAnalysis))
                return false;

            _messages.Add(new { role = "user", content = BuildVisionExecutionUserText(input, attachmentsToSend, visionAnalysis) });
            EnforceChatHistoryLimit();
            SyncActiveHistoryConversation();
            return true;
        }

        private static bool ShouldRunFinalVisualReviewThisRound(JArray fullToolCalls)
        {
            if (IsVisualReviewTemporarilyDisabled())
                return false;

            if (_agentMode != AgentMode.SelfTrain)
                return false;

            return (fullToolCalls == null || fullToolCalls.Count == 0)
                && _pendingFinalVisualReview
                && !_finalVisualReviewCompleted
                && !_finalVisualReviewAttempted
                && _currentTurnHadToolExecution;
        }

        private static bool IsVisualReviewTemporarilyDisabled()
        {
            return false;
        }

        private static async Task<ApiResponse> TryContinueWithFinalVisualReviewAsync(string apiKey, int depth, string fullContent, System.Threading.CancellationToken ct)
        {
            if (!ShouldRunFinalVisualReviewThisRound(null))
                return null;

            _finalVisualReviewAttempted = true;
            string ghCheck = IsSelfTrainingMode() ? Magpie.Host.GrasshopperDocumentHost.ExecuteCheckGhErrors() : null;
            string finalVisualReview = await RunFinalVisualReviewAsync(fullContent, ct);
            if (string.IsNullOrWhiteSpace(finalVisualReview))
                return null;

            if (IsSelfTrainingMode())
                return await ContinueSelfTrainingAfterVisualReviewAsync(apiKey, depth, fullContent, finalVisualReview, ghCheck, ct);

            _finalVisualReviewCompleted = true;
            _pendingFinalVisualReview = false;
            _messages.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = fullContent ?? ""
            });
            _messages.Add(new { role = "user", content = BuildFinalVisualReviewExecutionUserText(fullContent, finalVisualReview) });
            EnforceChatHistoryLimit();
            SyncActiveHistoryConversation();
            ct.ThrowIfCancellationRequested();
            return await CallLLMAPI(apiKey, depth + 1, ct);
        }

        private static async Task<ApiResponse> ContinueSelfTrainingAfterVisualReviewAsync(
            string apiKey,
            int depth,
            string fullContent,
            string finalVisualReview,
            string ghCheck,
            System.Threading.CancellationToken ct)
        {
            _selfTrainingIteration++;
            bool ghClean = IsGhCheckClean(ghCheck);
            SelfTrainingVisualDecision visualDecision = ParseSelfTrainingVisualDecision(finalVisualReview);
            bool visualPass = visualDecision.Pass.HasValue
                ? visualDecision.Pass.Value
                : VisualReviewLooksPassing(finalVisualReview);
            bool skillSuitable = visualDecision.SkillSuitable.HasValue
                ? visualDecision.SkillSuitable.Value
                : VisualReviewAllowsSkill(finalVisualReview);
            bool canWriteSkill = visualPass && ghClean && skillSuitable;
            string decisionSummary = "视觉=" + (visualPass ? "达标" : "未达标")
                + "；GH=" + (ghClean ? "干净" : "有问题")
                + "；模型判断skill=" + (skillSuitable ? "适合" : "不适合");
            if (!string.IsNullOrWhiteSpace(visualDecision.Status))
                decisionSummary += "；状态=" + visualDecision.Status;
            if (!string.IsNullOrWhiteSpace(visualDecision.SkillReason))
                decisionSummary += "；原因=" + visualDecision.SkillReason;

            _selfTrainingRecords.Add(new SelfTrainingIterationRecord
            {
                Iteration = _selfTrainingIteration,
                ToolSummary = fullContent,
                GhCheck = ghCheck,
                VisualReview = finalVisualReview,
                Decision = canWriteSkill ? "达标，等待 skill 写入" : decisionSummary
            });

            AppendSelfTrainingCard("视觉反馈", finalVisualReview);
            if (!string.IsNullOrWhiteSpace(ghCheck))
                AppendSelfTrainingCard("第 " + _selfTrainingIteration + " 轮 GH 检查", ghCheck);
            AppendSelfTrainingCard("自训练判定", decisionSummary);

            if (canWriteSkill)
            {
                _finalVisualReviewCompleted = true;
                _pendingFinalVisualReview = false;
                SyncActiveHistoryConversation();
                return CompleteSelfTrainingWithSkill(finalVisualReview, ghCheck, visualDecision);
            }

            if (_selfTrainingIteration >= _selfTrainingMaxIterations)
            {
                _finalVisualReviewCompleted = true;
                _pendingFinalVisualReview = false;
                string stop = "自训练已达到最大迭代次数（" + _selfTrainingMaxIterations + "），未写入 skill。"
                    + (ghClean ? "" : "\nGH 检查仍存在问题：\n" + ghCheck)
                    + "\n最后视觉反馈：\n" + finalVisualReview;
                AppendSelfTrainingCard("修复/停止原因", stop);
                AppendSystemMessage(stop);
                SyncActiveHistoryConversation();
                return new ApiResponse { Content = stop };
            }

            _finalVisualReviewCompleted = false;
            _finalVisualReviewAttempted = false;
            _pendingFinalVisualReview = false;
            string repairPrompt = BuildSelfTrainingRepairPrompt(finalVisualReview, ghCheck, _selfTrainingIteration + 1, _selfTrainingMaxIterations);

            _messages.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = fullContent ?? ""
            });
            _messages.Add(new { role = "user", content = repairPrompt });
            EnforceChatHistoryLimit();
            SyncActiveHistoryConversation();
            AppendSelfTrainingCard("修复/停止原因", "未达标，已进入第 " + (_selfTrainingIteration + 1) + " 轮局部修复。");
            ct.ThrowIfCancellationRequested();
            return await CallLLMAPI(apiKey, depth + 1, ct);
        }

        private static bool EnsureVisualReviewPreviewReady()
        {
            if (!string.IsNullOrWhiteSpace(_visualReviewPreviewComponentId))
                return true;

            if (string.IsNullOrWhiteSpace(_visualReviewTargetSourceId))
            {
                AppendQuietDiagnosticCard("最终视觉复核", "未记录最终目标输出，无法自动创建干净的预览出口。");
                return false;
            }

            string prepareResult = ExecutePrepareVisualReviewPreview(_visualReviewTargetSourceId, _visualReviewTargetOutputIndex, "VisualReviewPreview");
            if (string.IsNullOrWhiteSpace(prepareResult) || prepareResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                AppendQuietDiagnosticCard("最终视觉复核", string.IsNullOrWhiteSpace(prepareResult)
                    ? "自动准备视觉预览出口失败。"
                    : prepareResult);
                return false;
            }

            return true;
        }

        private static async Task<string> RunFinalVisualReviewAsync(string priorDraft, System.Threading.CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_pendingFinalVisualReview || _finalVisualReviewCompleted || !_currentTurnHadToolExecution)
                return null;

            var sourceImages = _finalVisualReviewSourceImages ?? new List<AttachmentItem>();
            if (sourceImages.Count == 0 && !IsSelfTrainingMode())
                return null;

            bool hasDedicatedPreview = EnsureVisualReviewPreviewReady();
            if (hasDedicatedPreview)
            {
                try
                {
                    string previewCleanup = ExecuteSetAllCSharpScriptPreviews(false);
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("Final visual review preview cleanup failed: " + ex.Message);
                }
            }

            string captureJson = ExecuteCaptureRhinoViewport("auto", 1600, 900, 0.12);
            if (string.IsNullOrWhiteSpace(captureJson) || captureJson.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                AppendQuietDiagnosticCard("最终视觉复核", string.IsNullOrWhiteSpace(captureJson) ? "截图失败。" : captureJson);
                return null;
            }

            string screenshotPath = null;
            try
            {
                screenshotPath = JObject.Parse(captureJson)["path"]?.ToString();
            }
            catch (Exception ex)
            {
                AppendQuietDiagnosticCard("最终视觉复核", "截图结果解析失败: " + ex.Message);
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
            AppendCollapsibleBubble(analysis.Trim(), "最终视觉复核 " + Math.Round(durationSeconds, 1) + "s", "👁");
            return analysis.Trim();
        }

        private static async Task<string> ExecuteCaptureRhinoViewportAsync(
            string question,
            string framing,
            int? width,
            int? height,
            double? paddingRatio,
            bool visualCheck,
            string visualDetail,
            System.Threading.CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string captureJson = ExecuteCaptureRhinoViewport(framing, width, height, paddingRatio);
            if (string.IsNullOrWhiteSpace(captureJson) || captureJson.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(captureJson) ? "Error: screenshot capture failed." : captureJson;

            string screenshotPath = null;
            try
            {
                screenshotPath = JObject.Parse(captureJson)["path"]?.ToString();
            }
            catch (Exception ex)
            {
                return "Error: screenshot metadata parse failed: " + ex.Message;
            }

            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
                return "Error: screenshot file path is missing or invalid.";

            JObject payload;
            try
            {
                payload = JObject.Parse(captureJson);
            }
            catch
            {
                payload = new JObject { ["raw_capture_result"] = captureJson };
            }

            bool visionAnalysisDisabled = true;
            if (visionAnalysisDisabled)
            {
                payload["visual_check"] = false;
                payload["visual_detail"] = "none";
                payload["visual_warning"] = "Vision analysis is temporarily disabled. This screenshot was not analyzed by a vision model. Do not infer visual facts from metadata.";
                return payload.ToString(Formatting.None);
            }

            string normalizedVisualDetail = NormalizeViewportVisualDetail(visualDetail);
            if (string.Equals(normalizedVisualDetail, "none", StringComparison.OrdinalIgnoreCase))
            {
                payload["visual_check"] = false;
                payload["visual_detail"] = "none";
                payload["visual_warning"] = "visual_detail=none skipped vision model analysis. Do not infer visual facts from metadata.";
                return payload.ToString(Formatting.None);
            }

            var providerSettings = GetVisionProviderRuntimeSettings();
            if (string.IsNullOrWhiteSpace(providerSettings?.ApiKey))
            {
                payload["visual_check"] = false;
                payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：请先配置视觉模型 API Key。");
                payload["visual_detail"] = normalizedVisualDetail;
                payload["visual_warning"] = "No vision model analysis was performed. Do not infer visual facts from screenshot metadata.";
                return payload.ToString(Formatting.None);
            }

            string reviewImagePath = screenshotPath;
            JObject reviewImageMetadata = BuildViewportReviewImage(screenshotPath, normalizedVisualDetail);
            if (!string.IsNullOrWhiteSpace(reviewImageMetadata?["path"]?.ToString()))
                reviewImagePath = reviewImageMetadata["path"].ToString();
            payload["visual_detail"] = normalizedVisualDetail;
            if (reviewImageMetadata != null)
                payload["review_image"] = reviewImageMetadata;

            JObject requestBody = BuildViewportScreenshotAnalysisRequestBody(providerSettings, question, reviewImagePath, captureJson, reviewImageMetadata);

            HttpResponseMessage response = null;
            string usedEndpoint = null;
            string lastEndpointError = null;
            DateTime startTime = DateTime.Now;
            try
            {
                ShowThinkingAnimation("看图中...");
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
                        payload["visual_check"] = false;
                        payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：视觉模型服务返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, errPreview, endpoint.Url);
                        return payload.ToString(Formatting.None);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                payload["visual_check"] = false;
                payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：请求未能发送到视觉模型服务，" + ex.GetType().Name, FormatExceptionChain(ex), usedEndpoint);
                return payload.ToString(Formatting.None);
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                payload["visual_check"] = false;
                payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：视觉模型服务没有返回成功响应。", lastEndpointError, usedEndpoint);
                return payload.ToString(Formatting.None);
            }

            string responseJson = await ReadResponseTextAsync(response, ct);
            if (!TryParseAssistantMessageFromResponse(responseJson, out JObject messageNode, out string parseError))
            {
                payload["visual_check"] = false;
                payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：视觉模型响应不是可解析的聊天响应，" + parseError, responseJson, usedEndpoint);
                return payload.ToString(Formatting.None);
            }

            string analysis = messageNode["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(analysis))
                analysis = messageNode["reasoning_content"]?.ToString();
            if (string.IsNullOrWhiteSpace(analysis))
            {
                payload["visual_check"] = false;
                payload["visual_analysis_error"] = BuildProviderDiagnostic(providerSettings, "截图视觉检查失败：视觉模型返回成功，但没有输出分析结论。", responseJson, usedEndpoint);
                return payload.ToString(Formatting.None);
            }

            double durationSeconds = (DateTime.Now - startTime).TotalSeconds;
            payload["visual_check"] = true;
            payload["visual_detail"] = normalizedVisualDetail;
            payload["visual_analysis"] = analysis.Trim();
            payload["vision_model"] = providerSettings.ModelName;
            payload["vision_duration_seconds"] = Math.Round(durationSeconds, 1);
            payload["note"] = "visual_analysis was produced by the configured vision model from the captured screenshot. Metadata fields are not visual facts.";
            return payload.ToString(Formatting.None);
        }

        private static string NormalizeViewportVisualDetail(string visualDetail)
        {
            string value = (visualDetail ?? "").Trim().ToLowerInvariant();
            if (value == "none" || value == "false" || value == "off")
                return "none";
            if (value == "high" || value == "original" || value == "full")
                return "high";
            return "low";
        }

        private static JObject BuildViewportReviewImage(string screenshotPath, string visualDetail)
        {
            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
                return null;

            var metadata = new JObject
            {
                ["source_path"] = screenshotPath,
                ["detail"] = visualDetail
            };

            try
            {
                using (var source = System.Drawing.Image.FromFile(screenshotPath))
                {
                    metadata["source_width"] = source.Width;
                    metadata["source_height"] = source.Height;

                    if (string.Equals(visualDetail, "high", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata["path"] = screenshotPath;
                        metadata["width"] = source.Width;
                        metadata["height"] = source.Height;
                        metadata["mime"] = GetMimeType(Path.GetExtension(screenshotPath).ToLowerInvariant());
                        metadata["compressed"] = false;
                        return metadata;
                    }

                    const int maxSide = 1024;
                    int width = source.Width;
                    int height = source.Height;
                    double scale = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
                    int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
                    int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
                    string reviewPath = Path.Combine(
                        Path.GetDirectoryName(screenshotPath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(screenshotPath) + "_vision_low.jpg");

                    using (var bitmap = new System.Drawing.Bitmap(targetWidth, targetHeight))
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        graphics.Clear(System.Drawing.Color.White);
                        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
                        SaveJpeg(bitmap, reviewPath, 75L);
                    }

                    metadata["path"] = reviewPath;
                    metadata["width"] = targetWidth;
                    metadata["height"] = targetHeight;
                    metadata["mime"] = "image/jpeg";
                    metadata["compressed"] = true;
                    metadata["jpeg_quality"] = 75;
                    metadata["max_side"] = maxSide;
                    return metadata;
                }
            }
            catch (Exception ex)
            {
                metadata["path"] = screenshotPath;
                metadata["mime"] = GetMimeType(Path.GetExtension(screenshotPath).ToLowerInvariant());
                metadata["compressed"] = false;
                metadata["compression_error"] = ex.Message;
                return metadata;
            }
        }

        private static void SaveJpeg(System.Drawing.Image image, string path, long quality)
        {
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders()
                .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
            if (codec == null)
            {
                image.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
                return;
            }

            using (var parameters = new System.Drawing.Imaging.EncoderParameters(1))
            {
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                image.Save(path, codec, parameters);
            }
        }
    }
}
