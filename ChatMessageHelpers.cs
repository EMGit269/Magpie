using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Magpie.Agent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    /// <summary>
    /// 与 UI 无关的消息压缩、参数解析与历史裁剪逻辑，便于单测与复用。
    /// </summary>
    public static class ChatMessageHelpers
    {
        public static List<object> ProjectMessagesForSend(IList<object> messages)
        {
            if (messages == null || messages.Count == 0)
                return new List<object>();
            return CompressMessages(new List<object>(messages));
        }

        public static List<object> CompressMessages(List<object> fullMessages)
        {
            var compressed = new List<object>();
            int lastCanvasStateIndex = FindLastGetGhComponentsIndex(fullMessages);
            for (int i = 0; i < fullMessages.Count; i++)
            {
                var msg = fullMessages[i];
                if (IsGetGhToolMessage(msg) && i != lastCanvasStateIndex)
                {
                    compressed.Add(CloneToolPlaceholder(msg, "get_gh_components", BuildFoldedToolSummary("get_gh_components", TryGetToolContentString(msg))));
                }
                else
                {
                    compressed.Add(CloneMessageForProjection(msg));
                }
            }
            ApplyLargeToolFoldInPlace(compressed, DeploymentOptions.LargeToolFoldMinChars);
            return compressed;
        }

        private static object CloneMessageForProjection(object msg)
        {
            if (msg is JObject jo) return (JObject)jo.DeepClone();
            if (msg is JArray ja) return (JArray)ja.DeepClone();
            if (msg is JToken jt) return jt.DeepClone();
            return msg;
        }

        /// <summary>就地折叠历史 get_gh_components（摘要失败时的机械回退）。</summary>
        public static void ApplyGetGhComponentsFoldInPlace(IList<object> messages)
        {
            if (messages == null || messages.Count == 0) return;
            int lastIdx = FindLastGetGhComponentsIndex(messages);
            if (lastIdx < 0) return;
            for (int i = 0; i < messages.Count; i++)
            {
                if (i == lastIdx || !IsGetGhToolMessage(messages[i])) continue;
                ReplaceToolContentInPlace(messages, i, BuildFoldedToolSummary("get_gh_components", TryGetToolContentString(messages[i])));
            }
        }

        /// <summary>
        /// 对体积过大的 tool 结果就地折叠，每种 function name 仅保留最后一次「大」payload。
        /// </summary>
        public static void ApplyLargeToolFoldInPlace(IList<object> messages, int minChars = 0)
        {
            if (minChars <= 0) minChars = DeploymentOptions.LargeToolFoldMinChars;
            if (messages == null || messages.Count == 0) return;
            var lastLargeByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < messages.Count; i++)
            {
                if (!IsToolMessage(messages[i], out string name, out _)) continue;
                int len = TryGetToolContentLength(messages[i]);
                if (len < minChars) continue;
                lastLargeByName[name] = i;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                if (!IsToolMessage(messages[i], out string name, out _)) continue;
                if (!lastLargeByName.TryGetValue(name, out int keep) || keep == i) continue;
                int len = TryGetToolContentLength(messages[i]);
                if (len < minChars) continue;
                ReplaceToolContentInPlace(messages, i, BuildFoldedToolSummary(name, TryGetToolContentString(messages[i])));
            }
        }

        private static string BuildFoldedToolSummary(string toolName, string rawContent)
        {
            try
            {
                var envelope = ToolResultCompactor.BuildEnvelope(toolName, rawContent);
                var jo = new JObject
                {
                    ["folded_tool_result"] = true,
                    ["tool_name"] = envelope.ToolName ?? toolName ?? "",
                    ["success"] = envelope.Success,
                    ["summary"] = envelope.Summary ?? "",
                    ["raw_char_count"] = envelope.RawCharCount,
                    ["result_kind"] = envelope.ResultKind ?? ""
                };
                if (!string.IsNullOrWhiteSpace(envelope.ArtifactPath))
                    jo["artifact_path"] = envelope.ArtifactPath;
                return jo.ToString(Formatting.None);
            }
            catch
            {
                string compact = ToolResultCompactor.Compact(rawContent, 320);
                return "[folded tool result: " + (toolName ?? "unknown") + "; " + compact + "]";
            }
        }

        public static void ApplyMechanicalContextReductionInPlace(IList<object> messages)
        {
            ApplyGetGhComponentsFoldInPlace(messages);
            ApplyLargeToolFoldInPlace(messages, DeploymentOptions.LargeToolFoldMinChars);
        }

        public static int EstimateMessageListTokens(IList<object> messages)
        {
            if (messages == null || messages.Count == 0) return 0;
            try
            {
                string json = JsonConvert.SerializeObject(messages);
                return Math.Max(1, json.Length / 3);
            }
            catch
            {
                return messages.Count * 200;
            }
        }

        public static int EstimateProjectedMessageListTokens(IList<object> messages)
        {
            return EstimateMessageListTokens(ProjectMessagesForSend(messages));
        }

        /// <summary>Tier2 起始下标之后的消息估算 tokens（不含系统前缀与可选的 Tier1 摘要头）。用于 UI 圆环显示「本轮对话」增长。</summary>
        public static int EstimateTier2TailTokens(IList<object> messages)
        {
            if (messages == null || messages.Count == 0) return 0;
            GetTierBoundaries(messages, out _, out int tier2Start, out _);
            if (tier2Start >= messages.Count) return 0;
            var tail = new List<object>(messages.Count - tier2Start);
            for (int i = tier2Start; i < messages.Count; i++)
                tail.Add(messages[i]);
            return EstimateMessageListTokens(tail);
        }

        public static int EstimateProjectedTier2TailTokens(IList<object> messages)
        {
            return EstimateTier2TailTokens(ProjectMessagesForSend(messages));
        }

        /// <summary>系统前缀与可选 Tier1 摘要的估算 tokens。</summary>
        public static int EstimateTierPrefixTokens(IList<object> messages)
        {
            if (messages == null || messages.Count == 0) return 0;
            GetTierBoundaries(messages, out _, out int tier2Start, out _);
            if (tier2Start <= 0) return 0;
            var prefix = new List<object>(tier2Start);
            for (int i = 0; i < tier2Start; i++)
                prefix.Add(messages[i]);
            return EstimateMessageListTokens(prefix);
        }

        public static int EstimateProjectedTierPrefixTokens(IList<object> messages)
        {
            return EstimateTierPrefixTokens(ProjectMessagesForSend(messages));
        }

        /// <summary>Tier0 结束下标（第一条非 system）；Tier1 为可选的一条「摘要」assistant；Tier2 起始于 tier2Start。</summary>
        public static void GetTierBoundaries(IList<object> messages, out int tier0End, out int tier2Start, out bool hasTier1Summary)
        {
            tier0End = CountLeadingSystemMessages(messages);
            hasTier1Summary = false;
            tier2Start = tier0End;
            if (tier0End < messages.Count && IsRollingSummaryTier1Message(messages[tier0End], out _))
            {
                hasTier1Summary = true;
                tier2Start = tier0End + 1;
            }
        }

        public static bool IsRollingSummaryTier1Message(object msg, out string bodyAfterHeader)
        {
            bodyAfterHeader = null;
            string role = TryGetRole(msg);
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
            string content = TryGetPlainTextContent(msg);
            if (string.IsNullOrEmpty(content) || !content.StartsWith(DeploymentOptions.RollingSummaryHeader, StringComparison.Ordinal))
                return false;
            bodyAfterHeader = content.Substring(DeploymentOptions.RollingSummaryHeader.Length).TrimStart();
            return true;
        }

        /// <summary>在 Tier2 内选取可安全截断的下标；从 maxTail 开始逐步收紧保留条数直至得到有效 cut。</summary>
        public static bool TryFindSummaryCutExclusive(IList<object> messages, int tier2Start, int maxVerbatimTail, out int cutExclusive)
        {
            cutExclusive = messages?.Count ?? 0;
            if (messages == null || tier2Start >= messages.Count) return false;
            for (int tail = maxVerbatimTail; tail >= 1; tail--)
            {
                int cut = FindSummaryCutExclusive(messages, tier2Start, tail);
                if (cut < messages.Count && cut > tier2Start)
                {
                    cutExclusive = cut;
                    return true;
                }
            }
            return false;
        }

        /// <summary>在 Tier2 内选取可安全截断的下标，使 [cutExclusive, Count) 在 tool_call 对上自洽。</summary>
        public static int FindSummaryCutExclusive(IList<object> messages, int tier2Start, int verbatimTailCount)
        {
            if (messages == null || tier2Start >= messages.Count) return messages?.Count ?? 0;
            int cut = messages.Count - Math.Max(0, verbatimTailCount);
            if (cut <= tier2Start) return messages.Count;
            while (cut > tier2Start && !IsValidToolSuffix(messages, cut))
                cut--;
            for (int iter = 0; iter < 8; iter++)
            {
                bool changed = false;
                while (cut < messages.Count && string.Equals(TryGetRole(messages[cut]), "tool", StringComparison.OrdinalIgnoreCase))
                {
                    cut--;
                    if (cut <= tier2Start) return messages.Count;
                    changed = true;
                }
                while (cut < messages.Count && !string.Equals(TryGetRole(messages[cut]), "user", StringComparison.OrdinalIgnoreCase))
                {
                    cut--;
                    if (cut <= tier2Start) return messages.Count;
                    changed = true;
                }
                while (cut > tier2Start && !IsValidToolSuffix(messages, cut))
                {
                    cut--;
                    changed = true;
                }
                if (!changed) break;
            }
            return cut;
        }

        public static string FlattenMessagesForSummary(IList<object> messages, int fromInclusive, int toExclusive, int maxChars)
        {
            if (messages == null || fromInclusive >= toExclusive) return "";
            var sb = new StringBuilder();
            for (int i = fromInclusive; i < toExclusive && i < messages.Count; i++)
            {
                AppendOneMessageForSummary(sb, messages[i]);
                if (sb.Length >= maxChars) break;
            }
            if (sb.Length > maxChars)
                return sb.ToString(0, maxChars) + "\n[…下文已截断…]";
            return sb.ToString();
        }

        /// <summary>
        /// 保留开头的连续 system 消息，之后若存在 Tier1 摘要则保留；再之后按消息组删除最旧条目直至数量不超过上限。
        /// </summary>
        public static void TrimMessageHistory(IList<object> messages, int maxCount)
        {
            if (messages == null || messages.Count <= maxCount) return;

            int systemPrefix = CountLeadingSystemMessages(messages);
            int idx = systemPrefix;
            if (idx < messages.Count && IsRollingSummaryTier1Message(messages[idx], out _))
                idx++;

            while (messages.Count > maxCount && idx < messages.Count)
            {
                int end = EndExclusiveMessageGroup(messages, idx);
                if (end <= idx) { messages.RemoveAt(idx); break; }
                for (int k = end - 1; k >= idx; k--)
                    messages.RemoveAt(k);
            }
        }

        public static int CountLeadingSystemMessages(IList<object> messages)
        {
            int n = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (!string.Equals(TryGetRole(messages[i]), "system", StringComparison.OrdinalIgnoreCase))
                    break;
                n++;
            }
            return n;
        }

        public static JObject ParseToolArgumentsForExecution(string funcName, string argsJson, out string cardSummary, out string cardSummaryDetail)
        {
            cardSummary = null;
            cardSummaryDetail = null;
            JObject o;
            try
            {
                o = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ParseToolArgumentsForExecution invalid JSON: " + ex.Message);
                o = new JObject();
            }

            if (string.Equals(funcName, "show_plan_steps", StringComparison.OrdinalIgnoreCase))
            {
                JToken op = o["operation_summary"];
                if (op != null && op.Type != JTokenType.Null) cardSummary = op.ToString().Trim();
                if (string.IsNullOrWhiteSpace(cardSummary)) cardSummary = "生成实施步骤卡片";

                JToken opd = o["operation_summary_detail"];
                if (opd != null && opd.Type != JTokenType.Null) cardSummaryDetail = opd.ToString().Trim();

                o.Remove("operation_summary");
                o.Remove("operation_summary_detail");
                return o;
            }

            JToken st = o["summary"];
            if (st != null && st.Type != JTokenType.Null) cardSummary = st.ToString().Trim();
            JToken sd = o["summary_detail"];
            if (sd != null && sd.Type != JTokenType.Null) cardSummaryDetail = sd.ToString().Trim();
            o.Remove("summary");
            o.Remove("summary_detail");
            return o;
        }

        public static bool ShouldDisplayReasoningBubble(string reasoning, string assistantContent, JArray toolCalls = null)
        {
            string normalized = NormalizeReasoning(reasoning);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (HasSubstantiveReasoningSignal(normalized))
                return true;

            bool hasToolCalls = toolCalls != null && toolCalls.Count > 0;
            if (!hasToolCalls)
                return true;

            bool hasVisibleContent = !string.IsNullOrWhiteSpace(assistantContent);
            bool isShortSingleLine = normalized.Length <= 48 && CountReasoningSentences(normalized) <= 1 && normalized.IndexOf('\n') < 0;

            if (LooksLikeToolPreamble(normalized) && isShortSingleLine)
                return false;

            if (!hasVisibleContent && isShortSingleLine)
                return false;

            return true;
        }

        private static string NormalizeReasoning(string reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning))
                return "";
            return Regex.Replace(reasoning.Trim(), @"[ \t]+", " ");
        }

        private static bool HasSubstantiveReasoningSignal(string reasoning)
        {
            if (reasoning.IndexOf('\n') >= 0)
                return true;

            if (reasoning.Length >= 70)
                return true;

            string[] keywords = {
                "\u56E0\u4E3A", "\u6240\u4EE5", "\u56E0\u6B64", "\u7531\u4E8E", "\u9700\u8981", "\u4E3A\u4E86", "\u4EE5\u514D", "\u907F\u514D",
                "\u5982\u679C", "\u5426\u5219", "\u4F46\u662F", "\u4E0D\u8FC7", "\u540C\u65F6", "\u5148\u786E\u8BA4", "\u518D\u5224\u65AD",
                "\u98CE\u9669", "\u7EA6\u675F", "\u51B2\u7A81", "\u517C\u5BB9", "\u9694\u79BB", "\u4E0A\u4E0B\u6587", "\u72B6\u6001", "\u7EE7\u627F",
                "\u62D3\u6251", "\u6570\u636E\u6D41", "\u7ED3\u6784", "\u65B9\u6848", "\u5EFA\u6A21", "\u5B9A\u4F4D", "\u6392\u67E5", "\u4FEE\u590D",
                "\u62A5\u9519", "\u9519\u8BEF", "\u5F02\u5E38", "\u5931\u8D25", "\u9A8C\u8BC1", "\u6838\u5B9E", "\u9884\u671F", "\u4E0D\u4E00\u81F4",
                "\u7A7A\u503C", "null", "\u8F93\u51FA", "\u8F93\u5165", "\u539F\u56E0", "\u4F9D\u8D56", "\u7B56\u7565"
            };

            for (int i = 0; i < keywords.Length; i++)
            {
                if (reasoning.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return CountReasoningSentences(reasoning) >= 2;
        }

        private static bool LooksLikeToolPreamble(string reasoning)
        {
            string[] starts = {
                "\u6211\u5148", "\u5148", "\u8BA9\u6211", "\u6211\u6765", "\u6211\u4F1A\u5148", "\u63A5\u4E0B\u6765", "\u73B0\u5728\u5148", "\u5148\u53BB", "\u5148\u7528"
            };
            string[] actions = {
                "\u68C0\u67E5\u4E00\u4E0B", "\u770B\u4E00\u4E0B", "\u770B\u4E0B", "\u770B\u770B", "\u786E\u8BA4\u4E00\u4E0B", "\u8BFB\u53D6", "\u83B7\u53D6",
                "\u8C03\u7528", "\u626B\u4E00\u904D", "\u8BD5\u4E00\u4E0B", "\u6253\u5F00", "\u5BF9\u7167\u4E00\u4E0B", "\u68C0\u67E5\u753B\u5E03", "\u8BFB\u53D6\u753B\u5E03"
            };

            bool startMatched = false;
            for (int i = 0; i < starts.Length; i++)
            {
                if (reasoning.StartsWith(starts[i], StringComparison.OrdinalIgnoreCase))
                {
                    startMatched = true;
                    break;
                }
            }

            if (!startMatched)
                return false;

            for (int i = 0; i < actions.Length; i++)
            {
                if (reasoning.IndexOf(actions[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static int CountReasoningSentences(string reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning))
                return 0;

            int count = 1;
            for (int i = 0; i < reasoning.Length; i++)
            {
                char c = reasoning[i];
                if (c == '\u3002' || c == '\uFF01' || c == '\uFF1F' || c == ';' || c == '\uFF1B')
                    count++;
            }
            return count;
        }

        private static int FindLastGetGhComponentsIndex(IList<object> fullMessages)
        {
            for (int i = fullMessages.Count - 1; i >= 0; i--)
            {
                if (IsGetGhToolMessage(fullMessages[i]))
                    return i;
            }
            return -1;
        }

        private static bool IsGetGhToolMessage(object msg)
        {
            return IsToolMessage(msg, out string name, out _) && name == "get_gh_components";
        }

        private static bool IsToolMessage(object msg, out string name, out string toolCallId)
        {
            name = null;
            toolCallId = null;
            if (msg is JObject j)
            {
                if (!string.Equals(j["role"]?.ToString(), "tool", StringComparison.OrdinalIgnoreCase)) return false;
                name = j["name"]?.ToString();
                toolCallId = j["tool_call_id"]?.ToString();
                return true;
            }
            var type = msg?.GetType();
            var rp = type?.GetProperty("role");
            if (rp?.GetValue(msg)?.ToString() != "tool") return false;
            name = type.GetProperty("name")?.GetValue(msg)?.ToString();
            toolCallId = type.GetProperty("tool_call_id")?.GetValue(msg)?.ToString();
            return true;
        }

        private static object CloneToolPlaceholder(object sourceMsg, string name, string placeholder)
        {
            if (sourceMsg is JObject j)
            {
                return new JObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = j["tool_call_id"],
                    ["name"] = name,
                    ["content"] = placeholder
                };
            }
            string id = sourceMsg?.GetType().GetProperty("tool_call_id")?.GetValue(sourceMsg)?.ToString();
            return new { role = "tool", tool_call_id = id, name, content = placeholder };
        }

        private static void ReplaceToolContentInPlace(IList<object> list, int index, string newContent)
        {
            object msg = list[index];
            if (msg is JObject j)
            {
                j["content"] = newContent;
                return;
            }
            var type = msg?.GetType();
            var contentProp = type?.GetProperty("content");
            if (contentProp != null && contentProp.CanWrite)
            {
                try
                {
                    contentProp.SetValue(msg, newContent, null);
                    return;
                }
                catch { /* fall through replace whole object */ }
            }
            string id = type?.GetProperty("tool_call_id")?.GetValue(msg)?.ToString();
            string name = type?.GetProperty("name")?.GetValue(msg)?.ToString();
            list[index] = new { role = "tool", tool_call_id = id, name, content = newContent };
        }

        private static int TryGetToolContentLength(object msg)
        {
            string c = TryGetToolContentString(msg);
            return c?.Length ?? 0;
        }

        private static string TryGetToolContentString(object msg)
        {
            if (msg is JObject j) return j["content"]?.ToString();
            return msg?.GetType().GetProperty("content")?.GetValue(msg)?.ToString();
        }

        private static bool IsValidToolSuffix(IList<object> messages, int cutExclusive)
        {
            for (int i = cutExclusive; i < messages.Count; i++)
            {
                if (!IsToolMessage(messages[i], out _, out string tid)) continue;
                if (string.IsNullOrEmpty(tid)) return false;
                bool ok = false;
                for (int j = i - 1; j >= cutExclusive; j--)
                {
                    if (!string.Equals(TryGetRole(messages[j]), "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                    if (AssistantHasToolCallId(messages[j], tid)) { ok = true; break; }
                }
                if (!ok) return false;
            }
            return true;
        }

        private static bool AssistantHasToolCallId(object msg, string toolCallId)
        {
            if (msg is JObject j)
            {
                var arr = j["tool_calls"] as JArray;
                if (arr == null) return false;
                foreach (var t in arr)
                {
                    if (string.Equals(t?["id"]?.ToString(), toolCallId, StringComparison.Ordinal)) return true;
                }
                return false;
            }
            var type = msg?.GetType();
            var tcp = type?.GetProperty("tool_calls");
            if (tcp == null) return false;
            // anonymous / dynamic: best effort via JToken
            try
            {
                var token = JToken.FromObject(tcp.GetValue(msg));
                if (token is JArray ja)
                {
                    foreach (var t in ja)
                    {
                        if (string.Equals(t?["id"]?.ToString(), toolCallId, StringComparison.Ordinal)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool AssistantHasAnyToolCalls(object msg)
        {
            if (msg is JObject j)
            {
                var arr = j["tool_calls"] as JArray;
                return arr != null && arr.Count > 0;
            }
            var tcp = msg?.GetType().GetProperty("tool_calls");
            if (tcp == null) return false;
            try
            {
                var token = JToken.FromObject(tcp.GetValue(msg));
                return token is JArray ja && ja.Count > 0;
            }
            catch { return false; }
        }

        private static int EndExclusiveMessageGroup(IList<object> m, int start)
        {
            if (start >= m.Count) return start;
            string r = TryGetRole(m[start]);
            if (string.Equals(r, "tool", StringComparison.OrdinalIgnoreCase))
                return start + 1;
            if (string.Equals(r, "user", StringComparison.OrdinalIgnoreCase))
            {
                int i = start + 1;
                if (i < m.Count && string.Equals(TryGetRole(m[i]), "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (AssistantHasAnyToolCalls(m[i]))
                    {
                        i++;
                        while (i < m.Count && string.Equals(TryGetRole(m[i]), "tool", StringComparison.OrdinalIgnoreCase))
                            i++;
                    }
                    else i++;
                }
                return i;
            }
            if (string.Equals(r, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                int i = start + 1;
                if (AssistantHasAnyToolCalls(m[start]))
                {
                    while (i < m.Count && string.Equals(TryGetRole(m[i]), "tool", StringComparison.OrdinalIgnoreCase))
                        i++;
                }
                else if (i < m.Count && string.Equals(TryGetRole(m[i]), "tool", StringComparison.OrdinalIgnoreCase))
                    i++;
                return i;
            }
            return start + 1;
        }

        private static void AppendOneMessageForSummary(StringBuilder sb, object msg)
        {
            string role = TryGetRole(msg) ?? "?";
            sb.Append(role.ToUpperInvariant()).Append(": ");
            sb.AppendLine(FlattenContentForSummary(msg));
            sb.AppendLine();
        }

        private static string FlattenContentForSummary(object msg)
        {
            if (msg is JObject j)
            {
                var content = j["content"];
                return FlattenTokenContent(content);
            }
            var prop = msg?.GetType().GetProperty("content");
            var val = prop?.GetValue(msg);
            if (val is JToken tok) return FlattenTokenContent(tok);
            string s = val?.ToString();
            if (string.IsNullOrEmpty(s)) return "";
            return StripBase64Like(s);
        }

        private static string FlattenTokenContent(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return "";
            if (content.Type == JTokenType.String) return StripBase64Like(content.ToString());
            if (content is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (var part in arr)
                {
                    string t = part["type"]?.ToString();
                    if (string.Equals(t, "text", StringComparison.OrdinalIgnoreCase))
                        sb.Append(part["text"]?.ToString());
                    else if (string.Equals(t, "image_url", StringComparison.OrdinalIgnoreCase))
                        sb.Append("[图片]");
                    else
                        sb.Append(part.ToString(Formatting.None));
                }
                return StripBase64Like(sb.ToString());
            }
            return StripBase64Like(content.ToString(Formatting.None));
        }

        private static string StripBase64Like(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 200) return s;
            if (s.IndexOf("data:image", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[含 base64 图片或附件，已省略]";
            return s.Length > DeploymentOptions.SummaryRequestMaxChars
                ? s.Substring(0, 4096) + "\n[…截断…]"
                : s;
        }

        public static string TryGetPlainTextContent(object msg)
        {
            if (msg is JObject j) return FlattenTokenContent(j["content"]);
            return FlattenContentForSummary(msg);
        }

        public static string TryGetRole(object msg)
        {
            if (msg is JObject jo) return jo["role"]?.ToString();
            var type = msg?.GetType();
            var rp = type?.GetProperty("role");
            return rp?.GetValue(msg)?.ToString();
        }
    }
}
