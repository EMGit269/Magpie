using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteReferenceSkillTool(ToolDispatchResult result, string funcName, JObject argsObj)
        {
            if (funcName == "read_skill_file")
            {
                result.ToolResult = ExecuteReadSkillFile(argsObj["file_name"]?.ToString());
                return true;
            }
            if (funcName == "read_reference_json")
            {
                result.ToolResult = ExecuteReadReferenceJson(argsObj["file_name"]?.ToString());
                return true;
            }
            if (funcName == "import_reference_gh")
            {
                result.ToolResult = ExecuteImportReferenceGh(
                    argsObj["file_name"]?.ToString(),
                    ReadNullableDouble(argsObj, "offset_x"),
                    ReadNullableDouble(argsObj, "offset_y"),
                    argsObj["group_name"]?.ToString());
                return true;
            }
            if (funcName == "create_gh_skill")
            {
                result.ToolResult = ExecuteCreateGhSkill(
                    argsObj["file_name"]?.ToString(),
                    argsObj["name"]?.ToString(),
                    argsObj["description"]?.ToString(),
                    argsObj["content"]?.ToString());
                return true;
            }
            return false;
        }
    }
}
