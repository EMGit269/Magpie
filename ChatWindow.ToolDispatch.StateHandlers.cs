using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteStateRepairTool(ToolDispatchResult result, string funcName, JObject argsObj)
        {
            if (funcName == "set_gh_component_status")
            {
                result.ToolResult = ExecuteSetGhComponentStatus(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    ReadNullableBool(argsObj, "preview"),
                    ReadNullableBool(argsObj, "enabled"));
                return true;
            }
            if (funcName == "set_all_csharp_script_previews")
            {
                result.ToolResult = ExecuteSetAllCSharpScriptPreviews(ReadNullableBool(argsObj, "preview"));
                return true;
            }
            if (funcName == "prepare_visual_review_preview")
            {
                result.ToolResult = "Error: prepare_visual_review_preview is disabled.";
                return true;
            }
            if (funcName == "modify_gh_component_ports")
            {
                result.ToolResult = ExecuteModifyGhComponentPorts(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["is_input"]?.ToObject<bool>() ?? false,
                    argsObj["action"]?.ToString(),
                    argsObj["port_name"]?.ToString(),
                    argsObj["index"]?.ToObject<int?>(),
                    argsObj["type_hint"]?.ToString());
                return true;
            }
            if (funcName == "modify_gh_port_data")
            {
                result.ToolResult = ExecuteModifyGhPortData(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["is_input"]?.ToObject<bool>() ?? false,
                    argsObj["index"]?.ToObject<int>() ?? 0,
                    argsObj["operation"]?.ToString());
                return true;
            }
            return false;
        }
    }
}
