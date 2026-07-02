using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie.Agent
{
    public sealed class ContextCompactionPlanner
    {
        public CompactionPlan Plan(IList<object> messages, int keepRecent)
        {
            var plan = new CompactionPlan();
            if (messages == null || messages.Count == 0)
                return plan;

            var pinned = new HashSet<int>();
            int count = messages.Count;

            PinLeadingSystemMessages(messages, pinned);
            PinRecentMessages(count, Math.Max(1, keepRecent), pinned);
            PinLastUserMessage(messages, pinned);
            EnforceToolCallPairs(messages, pinned);

            for (int i = 0; i < count; i++)
            {
                if (pinned.Contains(i)) plan.PinnedIndices.Add(i);
                else plan.SummarizeIndices.Add(i);
            }

            return plan;
        }

        private static void PinLeadingSystemMessages(IList<object> messages, HashSet<int> pinned)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (!string.Equals(GetRole(messages[i]), "system", StringComparison.OrdinalIgnoreCase))
                    break;
                pinned.Add(i);
            }
        }

        private static void PinRecentMessages(int count, int keepRecent, HashSet<int> pinned)
        {
            int start = Math.Max(0, count - keepRecent);
            for (int i = start; i < count; i++)
                pinned.Add(i);
        }

        private static void PinLastUserMessage(IList<object> messages, HashSet<int> pinned)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(GetRole(messages[i]), "user", StringComparison.OrdinalIgnoreCase))
                {
                    pinned.Add(i);
                    return;
                }
            }
        }

        private static void EnforceToolCallPairs(IList<object> messages, HashSet<int> pinned)
        {
            var callById = new Dictionary<string, int>(StringComparer.Ordinal);
            var resultById = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < messages.Count; i++)
            {
                foreach (string id in GetAssistantToolCallIds(messages[i]))
                    if (!callById.ContainsKey(id)) callById[id] = i;

                string resultId = GetToolResultId(messages[i]);
                if (!string.IsNullOrWhiteSpace(resultId) && !resultById.ContainsKey(resultId))
                    resultById[resultId] = i;
            }

            bool changed;
            int maxIterations = Math.Max(10, messages.Count);
            do
            {
                changed = false;
                foreach (int idx in pinned.ToList())
                {
                    foreach (string callId in GetAssistantToolCallIds(messages[idx]))
                    {
                        if (resultById.TryGetValue(callId, out int resultIdx) && pinned.Add(resultIdx))
                            changed = true;
                    }

                    string toolResultId = GetToolResultId(messages[idx]);
                    if (!string.IsNullOrWhiteSpace(toolResultId)
                        && callById.TryGetValue(toolResultId, out int callIdx)
                        && pinned.Add(callIdx))
                    {
                        changed = true;
                    }
                }
            }
            while (changed && --maxIterations > 0);
        }

        private static string GetRole(object msg)
        {
            if (msg is JObject jo) return jo["role"]?.ToString();
            var type = msg?.GetType();
            return type?.GetProperty("role")?.GetValue(msg)?.ToString();
        }

        private static string GetToolResultId(object msg)
        {
            if (!string.Equals(GetRole(msg), "tool", StringComparison.OrdinalIgnoreCase))
                return "";
            if (msg is JObject jo) return jo["tool_call_id"]?.ToString();
            var type = msg?.GetType();
            return type?.GetProperty("tool_call_id")?.GetValue(msg)?.ToString();
        }

        private static IEnumerable<string> GetAssistantToolCallIds(object msg)
        {
            if (!string.Equals(GetRole(msg), "assistant", StringComparison.OrdinalIgnoreCase))
                yield break;

            if (msg is JObject jo)
            {
                foreach (string id in GetToolCallIdsFromToken(jo["tool_calls"]))
                    yield return id;
                yield break;
            }

            var type = msg?.GetType();
            var calls = type?.GetProperty("tool_calls")?.GetValue(msg);
            if (calls is JToken token)
            {
                foreach (string id in GetToolCallIdsFromToken(token))
                    yield return id;
            }
        }

        private static IEnumerable<string> GetToolCallIdsFromToken(JToken token)
        {
            if (!(token is JArray arr)) yield break;
            foreach (var item in arr.OfType<JObject>())
            {
                string id = item["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }
        }
    }
}
