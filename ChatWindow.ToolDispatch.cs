using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static ToolDispatchResult ExecuteToolCall(
            string funcName,
            JObject argsObj,
            string argsJson,
            string callId,
            string fullContent,
            string fullReasoning,
            List<(string primary, string secondary)> operationCards)
        {
            var result = new ToolDispatchResult { ToolResult = "" };
            if (IsCanvasMutatingTool(funcName))
                result.UndoSnapshotPath = CreateCanvasUndoSnapshot(funcName, callId);

            try
            {
                ExecuteSynchronousToolCallCore(
                    result,
                    funcName,
                    argsObj,
                    argsJson,
                    callId,
                    fullContent,
                    fullReasoning,
                    operationCards);
            }
            catch (Exception ex)
            {
                result.ToolResult = "Error: " + ex.Message;
                AddGhLog.Error("Tool dispatch failed: " + (funcName ?? "?"), ex);
            }

            return result;
        }

        private static async Task<ToolDispatchResult> ExecuteToolCallAsync(
            string funcName,
            JObject argsObj,
            string argsJson,
            string callId,
            string fullContent,
            string fullReasoning,
            List<(string primary, string secondary)> operationCards,
            System.Threading.CancellationToken ct)
        {
            if (!string.Equals(funcName, "create_ai_image", StringComparison.Ordinal)
                && !string.Equals(funcName, "capture_rhino_viewport", StringComparison.Ordinal)
                && !string.Equals(funcName, "web_research", StringComparison.Ordinal))
            {
                return ExecuteToolCall(funcName, argsObj, argsJson, callId, fullContent, fullReasoning, operationCards);
            }

            var result = new ToolDispatchResult { ToolResult = "" };
            try
            {
                await ExecuteAsyncToolCallCore(result, funcName, argsObj, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.ToolResult = "Error: " + ex.Message;
                AddGhLog.Error("Async tool dispatch failed: " + (funcName ?? "?"), ex);
            }

            return result;
        }
    }
}
