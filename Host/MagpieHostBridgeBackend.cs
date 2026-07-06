using Newtonsoft.Json.Linq;
using Magpie.HostBridge;

namespace Magpie.Host
{
    /// <summary>
    /// Magpie 的 Host Bridge 后端实现 — 所有工具调用已迁移到 GrasshopperDocumentHost。
    /// ChatWindow 仅作为传统 UI 入口保留，不再直接执行 bridge tools。
    /// </summary>
    internal sealed class MagpieHostBridgeBackend : IGrasshopperHostBridgeBackend
    {
        public string ResolveToolObjectId(string id) => GrasshopperDocumentHost.ResolveToolObjectId(id);
        public string ExecuteGetCanvasSummary() => GrasshopperDocumentHost.ExecuteGetCanvasSummary();
        public string ExecuteQueryComponents(string id, string nameContains, bool? hasErrors, bool? isScript, bool? hasConnections, string portNameContains, int maxResults, int neighborDepth)
            => GrasshopperDocumentHost.ExecuteQueryGhComponents(id, nameContains, hasErrors, isScript, hasConnections, portNameContains, maxResults, neighborDepth);
        public string ExecuteGetComponentContext(string id, int depth, bool includeScriptBodies)
            => GrasshopperDocumentHost.ExecuteGetComponentContext(id, depth, includeScriptBodies);
        public string ExecuteReadComponentScript(string id) => GrasshopperDocumentHost.ExecuteReadComponentScript(id);
        public string ExecuteCheckGhErrors() => GrasshopperDocumentHost.ExecuteCheckGhErrors();
        public string ExecuteRecomputeCanvas() => GrasshopperDocumentHost.ExecuteRecomputeGhCanvas();
        public string ExecuteConnectComponents(string fromId, int fromIndex, string toId, int toIndex, string fromPortLabel, string toPortLabel)
            => GrasshopperDocumentHost.ExecuteConnectGhComponents(fromId, fromIndex, toId, toIndex, fromPortLabel, toPortLabel);
        public string ExecuteRemoveComponent(string id) => GrasshopperDocumentHost.ExecuteRemoveGhComponent(id);
        public string ExecuteSetComponentValue(string id, string value, double? min, double? max, int? decimals, string property, string graphMapperType)
            => GrasshopperDocumentHost.ExecuteSetGhComponentValue(id, value, min, max, decimals, property, graphMapperType);
        public string ExecuteCreateComponentGraph(JArray components, JArray connections, string groupName, string csharpFirstHelperReason = null, string csharpFirstHelperReasonDetail = null)
            => GrasshopperDocumentHost.ExecuteCreateComponentGraph(components, connections, groupName, csharpFirstHelperReason, csharpFirstHelperReasonDetail);
        public string ExecuteCreateCSharpScript(string aliasId, string label, float x, float y, JArray inputs, JArray outputs, string body, JArray components, JArray connections, string groupName)
            => GrasshopperDocumentHost.ExecuteCreateCSharpScriptComponent(aliasId, label, x, y, inputs, outputs, body, components, connections, groupName);
        public string ExecuteEditCSharpScript(string id, string mode, string body)
            => GrasshopperDocumentHost.ExecuteEditCSharpScriptComponent(id, mode, body);
    }
}
