using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static int CountCodeLinesForStats(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return 0;
            return code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;
        }

        private static string ReadCSharpScriptBodyForStats(string id)
        {
            string body = null;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) return;
                if (!Guid.TryParse(id, out Guid guid)) return;
                var obj = doc.FindObject(guid, true);
                if (obj == null || !IsCSharpScriptComponent(obj)) return;
                if (TryReadCSharpScriptBodyPreservingTemplate(obj, out string currentBody, out _))
                    body = currentBody;
            }));
            return body;
        }

        private static int ReadResultInt(string toolResult, string key)
        {
            if (string.IsNullOrWhiteSpace(toolResult) || string.IsNullOrWhiteSpace(key))
                return 0;
            try
            {
                var root = JObject.Parse(toolResult);
                return root[key]?.ToObject<int?>() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double? ReadNullableDouble(JObject argsObj, string key)
        {
            return argsObj?[key] == null || argsObj[key].Type == JTokenType.Null
                ? (double?)null
                : argsObj[key].ToObject<double>();
        }

        private static int? ReadNullableInt(JObject argsObj, string key)
        {
            return argsObj?[key] == null || argsObj[key].Type == JTokenType.Null
                ? (int?)null
                : argsObj[key].ToObject<int>();
        }

        private static bool? ReadNullableBool(JObject argsObj, string key)
        {
            return argsObj?[key] == null || argsObj[key].Type == JTokenType.Null
                ? (bool?)null
                : argsObj[key].ToObject<bool>();
        }

        internal static string ResolveToolObjectId(string id)
        {
            var doc = Grasshopper.Instances.ActiveCanvas?.Document;
            if (doc == null || string.IsNullOrWhiteSpace(id))
                return id;

            return TryResolveGuidFromPublicId(doc, id, out Guid guid)
                ? guid.ToString()
                : id;
        }

        private static List<string> ResolveToolObjectIds(IEnumerable<string> ids)
        {
            if (ids == null)
                return null;

            return ids.Select(ResolveToolObjectId).ToList();
        }
    }
}
