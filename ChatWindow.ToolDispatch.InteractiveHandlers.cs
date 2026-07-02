using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static bool TryExecuteInteractiveTool(
            ToolDispatchResult result,
            string funcName,
            JObject argsObj,
            string argsJson,
            string callId,
            string fullContent,
            string fullReasoning,
            List<(string primary, string secondary)> operationCards)
        {
            if (funcName == ShowReferenceOptionsTool.FunctionName)
            {
                var (refToolMsg, refEndRound) = ShowReferenceOptionsTool.Run(argsObj, argsJson, operationCards);
                result.ToolResult = refToolMsg;
                ApplyInteractiveToolEndRound(result, refEndRound, callId, funcName, fullContent, fullReasoning);
                return true;
            }
            if (funcName == ShowPlanStepsTool.FunctionName)
            {
                var (planToolMsg, planEndRound) = ShowPlanStepsTool.Run(argsObj, argsJson, operationCards);
                result.ToolResult = planToolMsg;
                ApplyInteractiveToolEndRound(result, planEndRound, callId, funcName, fullContent, fullReasoning);
                return true;
            }
            return false;
        }

        private static void ApplyInteractiveToolEndRound(
            ToolDispatchResult result,
            bool endRound,
            string callId,
            string funcName,
            string fullContent,
            string fullReasoning)
        {
            if (!endRound) return;

            _messages.Add(new { role = "tool", tool_call_id = callId, name = funcName, content = result.ToolResult });
            EnforceChatHistoryLimit();
            result.EndApiRoundAwaitingUser = true;
            result.EarlyResponse = new ApiResponse
            {
                Content = fullContent,
                Reasoning = fullReasoning
            };
        }
    }
}
