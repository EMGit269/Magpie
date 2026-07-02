using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie.Agent
{
    public static class ToolResultCompactor
    {
        private const int SummaryMaxChars = 420;

        public static ToolResultEnvelope BuildEnvelope(string toolName, string rawResult)
        {
            var envelope = ToolResultEnvelope.Empty(toolName);
            envelope.RawCharCount = rawResult == null ? 0 : rawResult.Length;
            envelope.TimestampUtc = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(rawResult))
            {
                envelope.Summary = "No tool result content.";
                return envelope;
            }

            string trimmed = rawResult.Trim();
            envelope.Success = !trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
            envelope.ResultKind = trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)
                ? "json"
                : "text";

            if (TrySummarizeJson(toolName, trimmed, envelope))
                return envelope;

            envelope.Summary = Compact(trimmed, SummaryMaxChars);
            return envelope;
        }

        private static bool TrySummarizeJson(string toolName, string rawResult, ToolResultEnvelope envelope)
        {
            try
            {
                var token = JToken.Parse(rawResult);
                if (token is JObject obj)
                {
                    envelope.Success = InferJsonSuccess(obj, envelope.Success);
                    envelope.ArtifactPath = FirstString(obj, "path", "file_path", "output_path", "image_path", "snapshot_path");
                    envelope.Summary = SummarizeObjectForTool(toolName, obj);
                    return true;
                }

                if (token is JArray arr)
                {
                    envelope.Summary = SummarizeArrayForTool(toolName, arr);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static string SummarizeObjectForTool(string toolName, JObject obj)
        {
            switch ((toolName ?? "").Trim())
            {
                case "get_gh_components":
                    return SummarizeGhComponents(obj);
                case "check_gh_errors":
                    return SummarizeGhErrors(obj);
                case "read_reference_json":
                    return SummarizeReferenceJson(obj);
                case "web_research":
                    return SummarizeWebResearch(obj);
                default:
                    return SummarizeObject(obj);
            }
        }

        private static string SummarizeArrayForTool(string toolName, JArray arr)
        {
            if (string.Equals(toolName, "web_research", StringComparison.OrdinalIgnoreCase))
            {
                var titles = arr.OfType<JObject>()
                    .Select(o => FirstString(o, "title", "url", "source"))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(4);
                string joined = string.Join(" | ", titles);
                return Compact("web results=" + arr.Count + (string.IsNullOrWhiteSpace(joined) ? "" : "; top=" + joined), SummaryMaxChars);
            }

            return "JSON array result with " + arr.Count + " item(s).";
        }

        private static string SummarizeGhComponents(JObject obj)
        {
            int components = CountArray(obj, "components", "objects", "items", "nodes");
            int connections = CountArray(obj, "connections", "wires", "edges");
            int groups = CountArray(obj, "groups");
            var errors = FindErrorLikeItems(obj).Take(5).ToList();

            var parts = new List<string>
            {
                "components=" + FirstNonZero(components, ReadInt(obj, "component_count", "components_count", "object_count")),
                "connections=" + FirstNonZero(connections, ReadInt(obj, "connection_count", "connections_count", "wire_count")),
                "groups=" + FirstNonZero(groups, ReadInt(obj, "group_count", "groups_count"))
            };

            if (errors.Count > 0)
                parts.Add("errors=" + errors.Count + " [" + string.Join(" | ", errors) + "]");
            AddPart(parts, obj, "summary");
            return Compact(string.Join("; ", parts), SummaryMaxChars);
        }

        private static string SummarizeGhErrors(JObject obj)
        {
            var errors = FindErrorLikeItems(obj).Take(8).ToList();
            var parts = new List<string>();
            AddPart(parts, obj, "status");
            AddPart(parts, obj, "errors_count");
            AddPart(parts, obj, "warnings_count");
            if (errors.Count > 0)
                parts.Add("items=" + string.Join(" | ", errors));
            if (parts.Count == 0)
                parts.Add(SummarizeObject(obj));
            return Compact(string.Join("; ", parts), SummaryMaxChars);
        }

        private static string SummarizeReferenceJson(JObject obj)
        {
            var metadata = obj["reference_metadata"] as JObject ?? obj["metadata"] as JObject;
            var parts = new List<string>();
            if (metadata != null)
            {
                AddPart(parts, metadata, "description");
                AddPart(parts, metadata, "source_file");
                AddPart(parts, metadata, "created_at");
                AddPart(parts, metadata, "csharp_scripts");
            }
            AddPart(parts, obj, "description");
            parts.Add("components=" + FirstNonZero(CountArray(obj, "components", "objects", "nodes"), ReadInt(obj, "component_count")));
            parts.Add("connections=" + FirstNonZero(CountArray(obj, "connections", "wires", "edges"), ReadInt(obj, "connection_count")));
            return Compact(string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), SummaryMaxChars);
        }

        private static string SummarizeWebResearch(JObject obj)
        {
            var parts = new List<string>();
            AddPart(parts, obj, "query");
            AddPart(parts, obj, "url");
            AddPart(parts, obj, "title");
            AddPart(parts, obj, "source");
            AddPart(parts, obj, "summary");
            int results = CountArray(obj, "results", "items", "sources");
            if (results > 0) parts.Add("results=" + results);
            return parts.Count == 0 ? SummarizeObject(obj) : Compact(string.Join("; ", parts), SummaryMaxChars);
        }

        private static bool InferJsonSuccess(JObject obj, bool fallback)
        {
            string status = FirstString(obj, "status", "result", "ok");
            if (string.IsNullOrWhiteSpace(status))
                return fallback && string.IsNullOrWhiteSpace(FirstString(obj, "error", "message_error"));

            status = status.Trim().ToLowerInvariant();
            if (status == "ok" || status == "success" || status == "true") return true;
            if (status == "error" || status == "failed" || status == "false") return false;
            return fallback;
        }

        private static string SummarizeObject(JObject obj)
        {
            var parts = new List<string>();
            AddPart(parts, obj, "status");
            AddPart(parts, obj, "message");
            AddPart(parts, obj, "error");
            AddPart(parts, obj, "file_name");
            AddPart(parts, obj, "path");
            AddPart(parts, obj, "created_components");
            AddPart(parts, obj, "created_connections");
            AddPart(parts, obj, "created_scripts");
            AddPart(parts, obj, "imported_count");
            AddPart(parts, obj, "source_object_count");
            AddPart(parts, obj, "component_count");
            AddPart(parts, obj, "errors_count");
            AddPart(parts, obj, "warnings_count");

            if (parts.Count == 0)
            {
                var props = obj.Properties().Take(8).Select(p => p.Name + "=" + Compact(TokenPreview(p.Value), 80));
                parts.AddRange(props);
            }

            return Compact(string.Join("; ", parts), SummaryMaxChars);
        }

        private static void AddPart(List<string> parts, JObject obj, string key)
        {
            if (parts == null || obj == null || string.IsNullOrWhiteSpace(key)) return;
            var value = obj[key];
            if (value == null || value.Type == JTokenType.Null) return;
            string preview = TokenPreview(value);
            if (!string.IsNullOrWhiteSpace(preview))
                parts.Add(key + "=" + Compact(preview, 120));
        }

        private static int CountArray(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return 0;
            foreach (string key in keys)
            {
                if (obj[key] is JArray arr) return arr.Count;
                if (obj[key] is JObject nested)
                {
                    int nestedCount = CountArray(nested, keys);
                    if (nestedCount > 0) return nestedCount;
                }
            }
            return 0;
        }

        private static int ReadInt(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return 0;
            foreach (string key in keys)
            {
                var token = obj[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                if (token.Type == JTokenType.Integer) return token.ToObject<int>();
                if (int.TryParse(token.ToString(), out int parsed)) return parsed;
            }
            return 0;
        }

        private static int FirstNonZero(params int[] values)
        {
            if (values == null) return 0;
            foreach (int value in values)
                if (value != 0) return value;
            return 0;
        }

        private static IEnumerable<string> FindErrorLikeItems(JToken token)
        {
            if (token == null) yield break;

            if (token is JObject obj)
            {
                string message = FirstString(obj, "error", "message", "runtime_message", "warning");
                if (!string.IsNullOrWhiteSpace(message) && LooksLikeProblem(obj, message))
                {
                    string id = FirstString(obj, "id", "component_id", "name", "nickname");
                    yield return Compact((string.IsNullOrWhiteSpace(id) ? "" : id + ": ") + message, 160);
                }

                foreach (var child in obj.Properties().Select(p => p.Value))
                {
                    foreach (string item in FindErrorLikeItems(child))
                        yield return item;
                }
            }
            else if (token is JArray arr)
            {
                foreach (var child in arr)
                {
                    foreach (string item in FindErrorLikeItems(child))
                        yield return item;
                }
            }
        }

        private static bool LooksLikeProblem(JObject obj, string message)
        {
            if (obj == null) return false;
            string haystack = (message ?? "") + " " + FirstString(obj, "level", "severity", "status", "type");
            return haystack.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || haystack.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                || haystack.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0
                || haystack.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || haystack.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstString(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return "";
            foreach (string key in keys)
            {
                var value = obj[key];
                if (value == null || value.Type == JTokenType.Null) continue;
                string s = value.Type == JTokenType.String ? value.ToString() : value.ToString(Newtonsoft.Json.Formatting.None);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return "";
        }

        private static string TokenPreview(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return token.ToString();
            if (token is JArray arr) return "array[" + arr.Count + "]";
            if (token is JObject obj) return "object{" + obj.Properties().Count() + "}";
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string Compact(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var s = string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (maxChars <= 0 || s.Length <= maxChars) return s;
            return s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }
    }
}
