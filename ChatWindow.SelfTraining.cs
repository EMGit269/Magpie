using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private const int SelfTrainingDefaultMaxIterations = 3;
        private const string SelfTrainingIndexHeader = "## 自训练 skill 索引";

        private sealed class SelfTrainingIterationRecord
        {
            public int Iteration;
            public string ToolSummary;
            public string GhCheck;
            public string VisualReview;
            public string Decision;
        }

        private sealed class SelfTrainingVisualDecision
        {
            public bool? Pass;
            public bool? SkillSuitable;
            public string Status;
            public string SkillReason;
            public string SkillTitle;
            public string SkillSlug;
            public string SkillMarkdown;
            public string RawJson;
        }

        private static string _selfTrainingGoal = null;
        private static string _selfTrainingOriginalGoal = null;
        private static int _selfTrainingIteration = 0;
        private static int _selfTrainingMaxIterations = SelfTrainingDefaultMaxIterations;
        private static bool _selfTrainingSkillWritten = false;
        private static bool _selfTrainingAwaitingUserFeedback = false;
        private static bool _selfTrainingFeedbackRevisionActive = false;
        private static string _selfTrainingActiveSkillFileName = null;
        private static string _selfTrainingCurrentUserFeedback = null;
        private static readonly List<SelfTrainingIterationRecord> _selfTrainingRecords = new List<SelfTrainingIterationRecord>();

        private static bool IsExecutionAgentMode()
        {
            return _agentMode == AgentMode.Create || _agentMode == AgentMode.SelfTrain;
        }

        private static bool IsSelfTrainingMode()
        {
            return _agentMode == AgentMode.SelfTrain;
        }

        private static string BuildSelfTrainingPrompt()
        {
            return @"

【当前 Agent 模式：自训练】
1. 目标是完成用户任务，并把成功经验沉淀为可复用 skill；不要为了写 skill 而牺牲当前画布结果。
2. 每轮执行后宿主会自动检查 GH 报错并做截图级视觉复核；你收到视觉反馈后应做局部修复，避免无故重做整套画布。
3. 如果反馈指出未达标，只修正主要偏差；如果反馈已经基本达标，不要继续修改画布。
4. skill 写入由宿主内部完成；不要主动调用 create_gh_skill，也不要把 skill 正文直接输出给用户。";
        }

        private static void ResetSelfTrainingState(string input)
        {
            if (!IsSelfTrainingMode())
            {
                ResetSelfTrainingTransientState();
                return;
            }

            string trimmedInput = (input ?? "").Trim();
            if (_selfTrainingAwaitingUserFeedback && !string.IsNullOrWhiteSpace(_selfTrainingActiveSkillFileName))
            {
                _selfTrainingFeedbackRevisionActive = true;
                _selfTrainingAwaitingUserFeedback = false;
                _selfTrainingCurrentUserFeedback = trimmedInput;
                if (string.IsNullOrWhiteSpace(_selfTrainingOriginalGoal))
                    _selfTrainingOriginalGoal = _selfTrainingGoal;
                _selfTrainingGoal = (_selfTrainingOriginalGoal ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(trimmedInput))
                    _selfTrainingGoal = (_selfTrainingGoal + "\n用户后验反馈：" + trimmedInput).Trim();
                _finalVisualReviewSourceInput = _selfTrainingGoal;
            }
            else
            {
                _selfTrainingGoal = trimmedInput;
                _selfTrainingOriginalGoal = trimmedInput;
                _selfTrainingAwaitingUserFeedback = false;
                _selfTrainingFeedbackRevisionActive = false;
                _selfTrainingActiveSkillFileName = null;
                _selfTrainingCurrentUserFeedback = null;
            }

            _selfTrainingIteration = 0;
            _selfTrainingMaxIterations = SelfTrainingDefaultMaxIterations;
            _selfTrainingSkillWritten = false;
            _selfTrainingRecords.Clear();
        }

        private static void ResetSelfTrainingTransientState()
        {
            _selfTrainingGoal = null;
            _selfTrainingOriginalGoal = null;
            _selfTrainingIteration = 0;
            _selfTrainingMaxIterations = SelfTrainingDefaultMaxIterations;
            _selfTrainingSkillWritten = false;
            _selfTrainingAwaitingUserFeedback = false;
            _selfTrainingFeedbackRevisionActive = false;
            _selfTrainingActiveSkillFileName = null;
            _selfTrainingCurrentUserFeedback = null;
            _selfTrainingRecords.Clear();
        }

        private static string BuildSelfTrainingModelInput(string input)
        {
            if (!IsSelfTrainingMode() || !_selfTrainingFeedbackRevisionActive)
                return input;

            var sb = new StringBuilder();
            sb.AppendLine("用户对刚完成的自训练结果提出反馈。请在当前画布基础上继续局部修改，不要把它当成全新任务。");
            sb.AppendLine("达标后宿主会更新同一个 skill 文件，不要新建或调用 create_gh_skill。");
            sb.AppendLine("当前 skill 文件：" + (_selfTrainingActiveSkillFileName ?? "未记录"));
            if (!string.IsNullOrWhiteSpace(_selfTrainingOriginalGoal))
            {
                sb.AppendLine();
                sb.AppendLine("原始目标：");
                sb.AppendLine(_selfTrainingOriginalGoal.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("用户反馈：");
            sb.AppendLine((input ?? "").Trim());
            return sb.ToString().Trim();
        }

        private static void AppendSelfTrainingCard(string title, string body)
        {
            AppendQuietDiagnosticCard(title, string.IsNullOrWhiteSpace(body) ? "无" : body.Trim());
        }

        private static void MarkSelfTrainingCanvasMutationForReview(List<(string primary, string secondary, string undoId)> operationCards)
        {
            if (!IsSelfTrainingMode()) return;

            _pendingFinalVisualReview = true;
            _finalVisualReviewCompleted = false;
            _finalVisualReviewAttempted = false;

            string summary = "";
            if (operationCards != null && operationCards.Count > 0)
            {
                var parts = new List<string>();
                foreach (var card in operationCards)
                {
                    if (!string.IsNullOrWhiteSpace(card.primary))
                        parts.Add(card.primary.Trim());
                    if (parts.Count >= 6) break;
                }
                summary = string.Join("；", parts);
            }

            AppendSelfTrainingCard("第 " + System.Math.Max(1, _selfTrainingIteration + 1) + " 轮执行",
                string.IsNullOrWhiteSpace(summary) ? "已执行画布修改，等待数据检查与视觉复核。" : summary);
        }

        private static string BuildSelfTrainingRepairPrompt(string visualReview, string ghCheck, int iteration, int maxIterations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("自训练视觉复核未达标，请只根据以下反馈做局部修复，然后再次检查关键输出。");
            sb.AppendLine("不要重做无关部分；优先处理主要偏差、GH Error、Null/空输出。");
            sb.AppendLine("当前轮次：" + iteration + "/" + maxIterations);
            if (!string.IsNullOrWhiteSpace(ghCheck))
            {
                sb.AppendLine();
                sb.AppendLine("GH 检查：");
                sb.AppendLine(ghCheck.Trim());
            }
            if (!string.IsNullOrWhiteSpace(visualReview))
            {
                sb.AppendLine();
                sb.AppendLine("视觉反馈：");
                sb.AppendLine(visualReview.Trim());
            }
            return sb.ToString().Trim();
        }

        private static bool IsGhCheckClean(string ghCheck)
        {
            if (string.IsNullOrWhiteSpace(ghCheck)) return true;
            string s = ghCheck.Trim();
            if (s.StartsWith("Error:", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (s.IndexOf("Error:", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static bool VisualReviewLooksPassing(string visualReview)
        {
            if (string.IsNullOrWhiteSpace(visualReview)) return false;
            string s = visualReview.Trim();
            if (s.IndexOf("未达标", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (s.IndexOf("明显偏差", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (s.IndexOf("需要修正", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return s.IndexOf("基本达标", System.StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("达标", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool VisualReviewAllowsSkill(string visualReview)
        {
            if (string.IsNullOrWhiteSpace(visualReview)) return false;
            string s = visualReview.Trim();
            if (s.IndexOf("不适合沉淀", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (s.IndexOf("不适合写入", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static SelfTrainingVisualDecision ParseSelfTrainingVisualDecision(string visualReview)
        {
            var decision = new SelfTrainingVisualDecision();
            JObject json = TryExtractVisualReviewJson(visualReview);
            if (json == null) return decision;

            decision.RawJson = json.ToString(Newtonsoft.Json.Formatting.None);
            decision.Pass = ReadSelfTrainingNullableBool(json, "pass");
            decision.SkillSuitable = ReadSelfTrainingNullableBool(json, "skill_suitable");
            decision.Status = ReadString(json, "status");
            decision.SkillReason = ReadString(json, "skill_reason");
            decision.SkillTitle = ReadString(json, "skill_title");
            decision.SkillSlug = ReadString(json, "skill_slug");
            decision.SkillMarkdown = ReadString(json, "skill_markdown");
            return decision;
        }

        private static JObject TryExtractVisualReviewJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            foreach (Match match in Regex.Matches(text, @"(?is)```(?:json)?\s*(\{.*?\})\s*```"))
            {
                JObject parsed = TryParseJObject(match.Groups[1].Value);
                if (parsed != null) return parsed;
            }

            int lastEnd = text.LastIndexOf('}');
            if (lastEnd < 0) return null;
            for (int start = text.LastIndexOf('{', lastEnd); start >= 0; start = text.LastIndexOf('{', start - 1))
            {
                string candidate = text.Substring(start, lastEnd - start + 1);
                JObject parsed = TryParseJObject(candidate);
                if (parsed != null) return parsed;
            }
            return null;
        }

        private static JObject TryParseJObject(string text)
        {
            try
            {
                return JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static bool? ReadSelfTrainingNullableBool(JObject json, string name)
        {
            JToken token = json[name];
            if (token == null) return null;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            string s = token.ToString().Trim();
            if (s.Equals("true", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (s == "是" || s == "适合" || s == "达标" || s == "基本达标") return true;
            if (s == "否" || s == "不适合" || s == "未达标") return false;
            return null;
        }

        private static string ReadString(JObject json, string name)
        {
            JToken token = json[name];
            return token == null ? "" : token.ToString().Trim();
        }

        private static string CompactLine(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= maxChars ? s : s.Substring(0, maxChars).TrimEnd() + "...";
        }

        private static string NormalizeTrainingSkillSlug(string input)
        {
            string s = (input ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(s)) return "task";
            s = Regex.Replace(s, @"[^\p{L}\p{Nd}]+", "_");
            s = Regex.Replace(s, @"_+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(s)) s = "task";
            if (s.Length > 36) s = s.Substring(0, 36).Trim('_');
            return string.IsNullOrWhiteSpace(s) ? "task" : s;
        }

        private static string GetTrainingSkillSlug(SelfTrainingVisualDecision visualDecision)
        {
            if (visualDecision != null)
            {
                string fromSlug = NormalizeTrainingSkillSlug(visualDecision.SkillSlug);
                if (fromSlug != "task") return fromSlug;

                string fromTitle = NormalizeTrainingSkillSlug(visualDecision.SkillTitle);
                if (fromTitle != "task") return fromTitle;

                string fromReason = NormalizeTrainingSkillSlug(visualDecision.SkillReason);
                if (fromReason != "task") return fromReason;
            }

            string compactGoal = CompactLine(_selfTrainingGoal, 24);
            string fallback = NormalizeTrainingSkillSlug(compactGoal);
            return fallback == "task" ? "self_training_task" : fallback;
        }

        private static string GetTrainingSkillTitle(SelfTrainingVisualDecision visualDecision)
        {
            if (visualDecision != null && !string.IsNullOrWhiteSpace(visualDecision.SkillTitle))
                return CompactLine(visualDecision.SkillTitle, 80);
            if (visualDecision != null && !string.IsNullOrWhiteSpace(visualDecision.SkillReason))
                return CompactLine(visualDecision.SkillReason, 80);
            return "自训练沉淀 skill";
        }

        private static string BuildSelfTrainingSkillContent(string fileName, string visualReview, string ghCheck)
        {
            var toolSummaries = new List<string>();
            foreach (var record in _selfTrainingRecords)
            {
                if (string.IsNullOrWhiteSpace(record.ToolSummary)) continue;
                string compact = CompactLine(record.ToolSummary, 220);
                if (!toolSummaries.Contains(compact))
                    toolSummaries.Add(compact);
                if (toolSummaries.Count >= 8) break;
            }

            var sb = new StringBuilder();
            sb.AppendLine("## 触发条件");
            sb.AppendLine("- 用户任务与以下目标相似时优先读取本 skill：" + (_selfTrainingGoal ?? "未记录"));
            sb.AppendLine("- 适用于已经验证达标的一次自训练任务；若用户需求明显不同，应重新规划。");
            sb.AppendLine();
            sb.AppendLine("## 推荐流程");
            if (toolSummaries.Count == 0)
                sb.AppendLine("- 先读取当前画布状态，再按用户要求执行局部建模或修改。");
            else
                foreach (string summary in toolSummaries)
                    sb.AppendLine("- " + summary);
            sb.AppendLine("- 完成后检查 GH runtime message、关键输出是否为空，并做视觉/截图复核。");
            sb.AppendLine();
            sb.AppendLine("## 常见失败与修复");
            sb.AppendLine("- 不要只以无报错作为完成标准；还要检查目标输出是否为空、比例是否明显偏离。");
            sb.AppendLine("- 若视觉反馈指出主要偏差，优先局部修正，不要无故重建整套画布。");
            if (!string.IsNullOrWhiteSpace(ghCheck))
                sb.AppendLine("- 最近一次 GH 检查摘要：" + CompactLine(ghCheck, 260));
            sb.AppendLine();
            sb.AppendLine("## 视觉检查要点");
            sb.AppendLine(string.IsNullOrWhiteSpace(visualReview)
                ? "- 检查当前 Rhino 结果是否与用户目标一致。"
                : "- 最近一次达标复核：" + CompactLine(visualReview, 700));
            sb.AppendLine();
            sb.AppendLine("## 成功案例摘要");
            sb.AppendLine("- 目标：" + (_selfTrainingGoal ?? "未记录"));
            if (_selfTrainingFeedbackRevisionActive && !string.IsNullOrWhiteSpace(_selfTrainingCurrentUserFeedback))
                sb.AppendLine("- 用户后验反馈：" + CompactLine(_selfTrainingCurrentUserFeedback, 240));
            sb.AppendLine("- 迭代轮数：" + _selfTrainingIteration);
            sb.AppendLine("- skill 文件：" + fileName);
            return sb.ToString().Trim();
        }

        private static string BuildTrainingSkillMarkdown(string fileName, string visualReview, string ghCheck, SelfTrainingVisualDecision visualDecision)
        {
            string modelMarkdown = visualDecision == null ? "" : (visualDecision.SkillMarkdown ?? "").Trim();
            if (string.IsNullOrWhiteSpace(modelMarkdown))
                return BuildSelfTrainingSkillContent(fileName, visualReview, ghCheck);

            modelMarkdown = StripFrontMatter(modelMarkdown);
            var sb = new StringBuilder();
            sb.AppendLine(modelMarkdown.Trim());
            sb.AppendLine();
            sb.AppendLine("## 自训练验证记录");
            sb.AppendLine("- 原始目标：" + CompactLine(_selfTrainingOriginalGoal ?? _selfTrainingGoal, 240));
            if (_selfTrainingFeedbackRevisionActive && !string.IsNullOrWhiteSpace(_selfTrainingCurrentUserFeedback))
                sb.AppendLine("- 用户后验反馈：" + CompactLine(_selfTrainingCurrentUserFeedback, 240));
            sb.AppendLine("- 迭代轮数：" + _selfTrainingIteration);
            sb.AppendLine("- skill 文件：" + fileName);
            if (!string.IsNullOrWhiteSpace(ghCheck))
                sb.AppendLine("- GH 检查摘要：" + CompactLine(ghCheck, 260));
            if (!string.IsNullOrWhiteSpace(visualDecision.SkillReason))
                sb.AppendLine("- 沉淀理由：" + CompactLine(visualDecision.SkillReason, 220));
            return sb.ToString().Trim();
        }

        private static string StripFrontMatter(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            return Regex.Replace(markdown.Trim(), @"\A---\s*[\s\S]*?\s*---\s*", "").Trim();
        }

        private static string CreateOrUpdateTrainingSkill(string visualReview, string ghCheck, SelfTrainingVisualDecision visualDecision)
        {
            try
            {
                string skillsPath = GetSkillsDirectory();
                if (!Directory.Exists(skillsPath)) Directory.CreateDirectory(skillsPath);

                bool updateExistingSkill = _selfTrainingFeedbackRevisionActive
                    && !string.IsNullOrWhiteSpace(_selfTrainingActiveSkillFileName);

                string slug = GetTrainingSkillSlug(visualDecision);
                string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmm");
                string baseName = "trained_" + slug + "_" + stamp;
                string fileName = baseName + ".md";
                string filePath = Path.Combine(skillsPath, fileName);
                if (updateExistingSkill)
                {
                    filePath = Path.Combine(skillsPath, fileName);
                    fileName = Path.GetFileName(_selfTrainingActiveSkillFileName);
                    filePath = Path.Combine(skillsPath, fileName);
                }
                else
                {
                    int suffix = 2;
                    while (File.Exists(filePath))
                    {
                        fileName = baseName + "_" + suffix + ".md";
                        filePath = Path.Combine(skillsPath, fileName);
                        suffix++;
                    }
                }

                string skillName = updateExistingSkill
                    ? Path.GetFileNameWithoutExtension(fileName).Replace("_", "-")
                    : "trained-" + slug.Replace("_", "-") + "-" + stamp;
                string description = GetTrainingSkillTitle(visualDecision) + "。适用于类似任务：" + CompactLine(_selfTrainingGoal, 120);
                string content = "---\nname: " + skillName + "\ndescription: " + description + "\n---\n\n"
                    + BuildTrainingSkillMarkdown(fileName, visualReview, ghCheck, visualDecision) + "\n";
                File.WriteAllText(filePath, content, Encoding.UTF8);

                var qualityReport = new Magpie.Agent.SkillQualityGate().Evaluate(fileName, content);
                if (!qualityReport.Pass)
                    AddGhLog.Warn("Self-training skill quality gate: " + qualityReport.ToLogLine());

                AppendTrainingSkillIndexEntry(fileName, description);
                UpsertSkillCatalogEntry(fileName, qualityReport.RecommendedQuality, qualityReport.Verified);

                Rhino.RhinoApp.InvokeOnUiThread((System.Action)(() => {
                    UpdateSkillLibraryUI();
                }));

                _selfTrainingSkillWritten = true;
                _selfTrainingActiveSkillFileName = fileName;
                return fileName;
            }
            catch (System.Exception ex)
            {
                AddGhLog.Error("Create training skill failed", ex);
                return "Error: " + ex.Message;
            }
        }

        private static void AppendTrainingSkillIndexEntry(string fileName, string description)
        {
            string skillsPath = GetSkillsDirectory();
            string indexPath = Path.Combine(skillsPath, "reference_index.md");
            string entry = "- " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                + " | `" + fileName + "` | " + CompactLine(description, 180);

            string text = File.Exists(indexPath) ? File.ReadAllText(indexPath, Encoding.UTF8) : "";
            if (text.IndexOf("`" + fileName + "`", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            if (string.IsNullOrWhiteSpace(text))
            {
                text = "---\nname: reference-index\ndescription: Reference and trained skill index.\n---\n\n"
                    + SelfTrainingIndexHeader + "\n" + entry + "\n";
            }
            else if (text.IndexOf(SelfTrainingIndexHeader, System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                text = text.TrimEnd() + "\n\n" + SelfTrainingIndexHeader + "\n" + entry + "\n";
            }
            else
            {
                text = text.TrimEnd() + "\n" + entry + "\n";
            }

            File.WriteAllText(indexPath, text, Encoding.UTF8);
        }

        private static ApiResponse CompleteSelfTrainingWithSkill(string visualReview, string ghCheck, SelfTrainingVisualDecision visualDecision)
        {
            if (_selfTrainingSkillWritten)
            {
                string already = "自训练结果已达标，skill 已写入；本次不重复写入。";
                AppendSelfTrainingCard("Skill 写入结果", already);
                AppendSelfTrainingFeedbackPrompt();
                AppendSystemMessage(already);
                _pendingFinalVisualReview = false;
                _finalVisualReviewCompleted = true;
                return new ApiResponse { Content = already };
            }

            string fileName = CreateOrUpdateTrainingSkill(visualReview, ghCheck, visualDecision);
            string message;
            if (fileName.StartsWith("Error:", System.StringComparison.OrdinalIgnoreCase))
            {
                message = "自训练结果已达标，但 skill 写入失败：" + fileName;
                AppendSelfTrainingCard("Skill 写入结果", message);
            }
            else
            {
                message = _selfTrainingFeedbackRevisionActive
                    ? "自训练结果已达标，已更新同一个 skill：" + fileName
                    : "自训练结果已达标，已写入 skill：" + fileName;
                AppendSelfTrainingCard("Skill 写入结果", message);
                _messages.Add(new { role = "system", content = "自训练已生成 skill `" + fileName + "`；后续相似任务应优先 read_skill_file 阅读该文件。" });
                EnforceChatHistoryLimit();
                AppendSelfTrainingFeedbackPrompt();
            }

            AppendSystemMessage(message);
            _pendingFinalVisualReview = false;
            _finalVisualReviewCompleted = true;
            return new ApiResponse { Content = message };
        }

        private static void AppendSelfTrainingFeedbackPrompt()
        {
            if (string.IsNullOrWhiteSpace(_selfTrainingActiveSkillFileName))
                return;

            _selfTrainingAwaitingUserFeedback = true;
            _selfTrainingFeedbackRevisionActive = false;
            string body = "如果结果还需要调整，请直接在输入框反馈问题或补充要求。下一条反馈会继续修改当前画布，并在达标后更新同一个 skill："
                + _selfTrainingActiveSkillFileName;
            AppendSelfTrainingCard("用户反馈", body);
        }
    }
}
