using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static async Task ExecuteAsyncToolCallCore(
            ToolDispatchResult result,
            string funcName,
            JObject argsObj,
            CancellationToken ct)
        {
            if (string.Equals(funcName, "create_ai_image", StringComparison.Ordinal))
            {
                result.ToolResult = await ExecuteCreateAiImageAsync(
                    argsObj["prompt"]?.ToString(),
                    argsObj["intent"]?.ToString(),
                    ReadNullableBool(argsObj, "use_uploaded_images") ?? true,
                    argsObj["aspect_ratio"]?.ToString(),
                    ct).ConfigureAwait(false);
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => ApplyAiImageToolResult(result.ToolResult)));
            }
            else if (string.Equals(funcName, "capture_rhino_viewport", StringComparison.Ordinal))
            {
                result.ToolResult = "Error: capture_rhino_viewport is not exposed to AI tools.";
            }
            else if (string.Equals(funcName, "web_research", StringComparison.Ordinal))
            {
                result.ToolResult = await ExecuteWebResearchAsync(
                    argsObj["mode"]?.ToString(),
                    argsObj["query"]?.ToString(),
                    argsObj["url"]?.ToString(),
                    argsObj["allowed_domains"] as JArray,
                    argsObj["max_results"]?.ToObject<int?>() ?? 5,
                    argsObj["max_chars"]?.ToObject<int?>() ?? 6000,
                    ct).ConfigureAwait(false);
            }
        }
    }
}
