using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteBasicCanvasTool(ToolDispatchResult result, string funcName)
        {
            if (funcName == "ensure_gh_canvas")
            {
                result.ToolResult = ExecuteEnsureGhCanvas();
                return true;
            }
            if (funcName == "get_gh_components")
            {
                result.ToolResult = ExecuteGetGhComponents();
                return true;
            }
            if (funcName == "recompute_gh_canvas")
            {
                result.ToolResult = ExecuteRecomputeGhCanvas();
                return true;
            }
            if (funcName == "capture_rhino_viewport")
            {
                result.ToolResult = "Error: capture_rhino_viewport is not exposed to AI tools.";
                return true;
            }
            if (funcName == "check_gh_errors")
            {
                result.ToolResult = ExecuteCheckGhErrors();
                return true;
            }
            return false;
        }

        private static bool TryExecuteCanvasMutationTool(ToolDispatchResult result, string funcName, JObject argsObj)
        {
            if (funcName == "add_gh_component")
            {
                if (!ValidateOptionalCSharpFirstHelperReason(argsObj, out string reasonError))
                {
                    result.ToolResult = reasonError;
                    return true;
                }

                string label = argsObj["label"]?.ToString();
                string name = argsObj["name"]?.ToString();
                string cguid = argsObj["component_guid"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cguid))
                {
                    result.ToolResult = "Error: name or component_guid is required.";
                }
                else
                {
                    result.ToolResult = ExecuteAddGhComponent(
                        name ?? "",
                        argsObj["x"]?.ToObject<float>() ?? 0f,
                        argsObj["y"]?.ToObject<float>() ?? 0f,
                        label,
                        cguid,
                        argsObj["graph_mapper_type"]?.ToString() ?? argsObj["graph_type"]?.ToString(),
                        argsObj["value"]?.ToString(),
                        argsObj["min"]?.ToObject<double?>(),
                        argsObj["max"]?.ToObject<double?>(),
                        argsObj["decimals"]?.ToObject<int?>(),
                        argsObj["csharp_first_helper_reason"]?.ToString(),
                        argsObj["csharp_first_helper_reason_detail"]?.ToString());
                    if (!result.ToolResult.StartsWith("Error:")) result.AddComp++;
                }
                return true;
            }

            if (funcName == "connect_gh_components")
            {
                result.ToolResult = ExecuteConnectGhComponents(
                    ResolveToolObjectId(argsObj["from_id"]?.ToString()),
                    argsObj["from_index"]?.ToObject<int>() ?? 0,
                    ResolveToolObjectId(argsObj["to_id"]?.ToString()),
                    argsObj["to_index"]?.ToObject<int>() ?? 0,
                    argsObj["from_port_label"]?.ToString(),
                    argsObj["to_port_label"]?.ToString());
                if (!result.ToolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) result.AddConn++;
                return true;
            }

            if (funcName == "remove_gh_component")
            {
                result.ToolResult = ExecuteRemoveGhComponent(ResolveToolObjectId(argsObj["id"]?.ToString()));
                if (!result.ToolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) result.DelComp++;
                return true;
            }

            if (funcName == "set_gh_component_value")
            {
                result.ToolResult = ExecuteSetGhComponentValue(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["value"]?.ToString(),
                    ReadNullableDouble(argsObj, "min"),
                    ReadNullableDouble(argsObj, "max"),
                    ReadNullableInt(argsObj, "decimals"),
                    argsObj["property"]?.ToString(),
                    argsObj["graph_mapper_type"]?.ToString() ?? argsObj["graph_type"]?.ToString());
                return true;
            }

            if (funcName == "remove_gh_connection")
            {
                result.ToolResult = ExecuteRemoveGhConnection(
                    ResolveToolObjectId(argsObj["from_id"]?.ToString()),
                    argsObj["from_index"]?.ToObject<int>() ?? 0,
                    ResolveToolObjectId(argsObj["to_id"]?.ToString()),
                    argsObj["to_index"]?.ToObject<int>() ?? 0);
                if (!result.ToolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) result.DelConn++;
                return true;
            }

            if (funcName == "create_component_graph")
            {
                if (!ValidateOptionalCSharpFirstHelperReason(argsObj, out string reasonError))
                {
                    result.ToolResult = reasonError;
                    return true;
                }

                bool autoGroup = argsObj["auto_group"]?.ToObject<bool>() ?? false;
                string groupName = argsObj["group_name"]?.ToString();
                if (string.IsNullOrEmpty(groupName))
                    groupName = autoGroup ? "AI Generated" : null;
                result.ToolResult = ExecuteCreateComponentGraph(
                    argsObj["components"] as JArray,
                    argsObj["connections"] as JArray,
                    groupName,
                    argsObj["csharp_first_helper_reason"]?.ToString(),
                    argsObj["csharp_first_helper_reason_detail"]?.ToString());
                if (!result.ToolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddComp += ReadResultInt(result.ToolResult, "created_components");
                    result.AddConn += ReadResultInt(result.ToolResult, "created_connections");
                }
                return true;
            }

            if (funcName == "manage_gh_groups")
            {
                string groupId = ResolveToolObjectId(argsObj["group_id"]?.ToString());
                string groupName = argsObj["name"]?.ToString();
                JArray idsArray = argsObj["ids"] as JArray;
                List<string> idsList = ResolveToolObjectIds(idsArray?.Select(v => v.ToString()));
                result.ToolResult = ExecuteManageGhGroupsUnified(argsObj["action"]?.ToString(), idsList, groupId, groupName);
                return true;
            }

            return false;
        }

        private static bool ValidateOptionalCSharpFirstHelperReason(JObject argsObj, out string error)
        {
            error = null;
            if (_layoutMode != LayoutMode.CSharpFirst)
                return true;

            string reason = argsObj?["csharp_first_helper_reason"]?.ToString();
            if (string.IsNullOrWhiteSpace(reason))
                return true;

            switch (reason.Trim())
            {
                case "component_more_efficient":
                case "user_requested_component":
                    return true;
                default:
                    error = "Error: invalid csharp_first_helper_reason. Choose one of: component_more_efficient, user_requested_component.";
                    return false;
            }
        }
    }
}
