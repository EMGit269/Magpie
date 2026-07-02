using System;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteScriptTool(ToolDispatchResult result, string funcName, JObject argsObj)
        {
            if (funcName == "gh_native_script_editor")
            {
                result.ToolResult = ExecuteGhNativeScriptEditor(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["mode"]?.ToString(),
                    argsObj["code"]?.ToString(),
                    argsObj["language"]?.ToString());
                return true;
            }

            if (funcName == "create_csharp_script_component")
            {
                string csharpName = argsObj["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(csharpName)) csharpName = argsObj["label"]?.ToString();
                result.ToolResult = ExecuteCreateCSharpScriptComponent(
                    argsObj["alias_id"]?.ToString(),
                    csharpName,
                    argsObj["x"]?.ToObject<float>() ?? 0f,
                    argsObj["y"]?.ToObject<float>() ?? 0f,
                    argsObj["inputs"] as JArray,
                    argsObj["outputs"] as JArray,
                    argsObj["body"]?.ToString(),
                    argsObj["components"] as JArray,
                    argsObj["connections"] as JArray,
                    argsObj["group_name"]?.ToString());
                if (!result.ToolResult.StartsWith("Error:"))
                {
                    result.AddComp += ReadResultInt(result.ToolResult, "created_scripts");
                    result.AddCodeLines += CountCodeLinesForStats(argsObj["body"]?.ToString());
                    result.AddComp += ReadResultInt(result.ToolResult, "created_components");
                }
                return true;
            }

            if (funcName == "edit_csharp_script_component")
            {
                string beforeBody = null;
                bool settingBody = string.Equals(argsObj["mode"]?.ToString(), "set_body", StringComparison.OrdinalIgnoreCase);
                string resolvedId = ResolveToolObjectId(argsObj["id"]?.ToString());
                if (settingBody)
                    beforeBody = ReadCSharpScriptBodyForStats(resolvedId);

                result.ToolResult = ExecuteEditCSharpScriptComponent(
                    resolvedId,
                    argsObj["mode"]?.ToString(),
                    argsObj["body"]?.ToString());

                if (settingBody && !result.ToolResult.StartsWith("Error:"))
                {
                    int beforeLines = CountCodeLinesForStats(beforeBody);
                    int afterLines = CountCodeLinesForStats(argsObj["body"]?.ToString());
                    if (afterLines >= beforeLines) result.AddCodeLines += afterLines - beforeLines;
                    else result.DelCodeLines += beforeLines - afterLines;
                }
                return true;
            }

            if (funcName == "create_script_component_graph")
            {
                result.ToolResult = ExecuteCreateScriptComponentGraph(
                    argsObj["mode"]?.ToString(),
                    argsObj["scripts"] as JArray,
                    argsObj["components"] as JArray,
                    argsObj["connections"] as JArray,
                    argsObj["group_name"]?.ToString());
                if (!result.ToolResult.StartsWith("Error:"))
                {
                    result.AddComp += ReadResultInt(result.ToolResult, "created_scripts");
                    result.AddComp += ReadResultInt(result.ToolResult, "created_components");
                    result.AddConn += ReadResultInt(result.ToolResult, "created_connections");
                    if (argsObj["scripts"] is JArray scriptItems)
                    {
                        foreach (var script in scriptItems)
                        {
                            result.AddCodeLines += CountCodeLinesForStats(script?["body"]?.ToString() ?? script?["code"]?.ToString() ?? script?["value"]?.ToString());
                        }
                    }
                }
                return true;
            }

            if (funcName == "read_component_script")
            {
                result.ToolResult = ExecuteReadComponentScript(ResolveToolObjectId(argsObj["id"]?.ToString()));
                return true;
            }

            return false;
        }
    }
}
