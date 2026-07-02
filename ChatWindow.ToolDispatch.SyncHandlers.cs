using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static void ExecuteSynchronousToolCallCore(
            ToolDispatchResult result,
            string funcName,
            JObject argsObj,
            string argsJson,
            string callId,
            string fullContent,
            string fullReasoning,
            List<(string primary, string secondary)> operationCards)
        {
            if (funcName == "create_ai_image")
                throw new InvalidOperationException("create_ai_image must be executed through the async tool dispatch path.");

            if (TryExecuteBasicCanvasTool(result, funcName)
                || TryExecuteCanvasMutationTool(result, funcName, argsObj)
                || TryExecuteScriptTool(result, funcName, argsObj)
                || TryExecuteLookupTool(result, funcName, argsObj)
                || TryExecuteStateRepairTool(result, funcName, argsObj)
                || TryExecuteReferenceSkillTool(result, funcName, argsObj)
                || TryExecuteInteractiveTool(result, funcName, argsObj, argsJson, callId, fullContent, fullReasoning, operationCards))
            {
                return;
            }
        }
    }
}
