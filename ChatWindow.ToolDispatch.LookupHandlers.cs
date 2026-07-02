using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteLookupTool(ToolDispatchResult result, string funcName, JObject argsObj)
        {
            if (funcName == "search_component_library")
            {
                result.ToolResult = ExecuteSearchComponentLibrary(argsObj["keyword"]?.ToString());
                return true;
            }
            if (funcName == "search_gh_component_catalog")
            {
                int maxResults = argsObj["max_results"]?.ToObject<int?>() ?? 30;
                string categoryContains = argsObj["category_contains"]?.ToString();
                result.ToolResult = ExecuteSearchGhComponentCatalog(argsObj["query"]?.ToString(), maxResults, categoryContains);
                return true;
            }
            if (funcName == "query_gh_components")
            {
                result.ToolResult = ExecuteQueryGhComponents(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["name_contains"]?.ToString(),
                    ReadNullableBool(argsObj, "has_errors"),
                    ReadNullableBool(argsObj, "is_script"),
                    ReadNullableBool(argsObj, "has_connections"),
                    argsObj["port_name_contains"]?.ToString(),
                    argsObj["max_results"]?.ToObject<int?>() ?? 8,
                    argsObj["neighbor_depth"]?.ToObject<int?>() ?? 1);
                return true;
            }
            if (funcName == "get_component_context")
            {
                result.ToolResult = ExecuteGetComponentContext(
                    ResolveToolObjectId(argsObj["id"]?.ToString()),
                    argsObj["depth"]?.ToObject<int?>() ?? 1,
                    ReadNullableBool(argsObj, "include_script_bodies") ?? false);
                return true;
            }
            return false;
        }
    }
}
