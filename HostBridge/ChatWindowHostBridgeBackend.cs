using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal sealed class ChatWindowHostBridgeBackend : IGrasshopperHostBridgeBackend
    {
        public string ResolveToolObjectId(string id) => ChatWindow.ResolveToolObjectId(id);
        public string ExecuteGetCanvasSummary() => ChatWindow.ExecuteGetGhComponents();
        public string ExecuteQueryComponents(string id, string nameContains, bool? hasErrors, bool? isScript, bool? hasConnections, string portNameContains, int maxResults, int neighborDepth)
            => ChatWindow.ExecuteQueryGhComponents(id, nameContains, hasErrors, isScript, hasConnections, portNameContains, maxResults, neighborDepth);
        public string ExecuteGetComponentContext(string id, int depth, bool includeScriptBodies)
            => ChatWindow.ExecuteGetComponentContext(id, depth, includeScriptBodies);
        public string ExecuteReadComponentScript(string id) => ChatWindow.ExecuteReadComponentScript(id);
        public string ExecuteCheckGhErrors() => ChatWindow.ExecuteCheckGhErrors();
        public string ExecuteRecomputeCanvas() => ChatWindow.ExecuteRecomputeGhCanvas();
        public string ExecuteConnectComponents(string fromId, int fromIndex, string toId, int toIndex, string fromPortLabel, string toPortLabel)
            => ChatWindow.ExecuteConnectGhComponents(fromId, fromIndex, toId, toIndex, fromPortLabel, toPortLabel);
        public string ExecuteRemoveComponent(string id) => ChatWindow.ExecuteRemoveGhComponent(id);
        public string ExecuteSetComponentValue(string id, string value, double? min, double? max, int? decimals, string property, string graphMapperType)
            => ChatWindow.ExecuteSetGhComponentValue(id, value, min, max, decimals, property, graphMapperType);
        public string ExecuteCreateComponentGraph(JArray components, JArray connections, string groupName)
            => ChatWindow.ExecuteCreateComponentGraph(components, connections, groupName);
        public string ExecuteCreateCSharpScript(string aliasId, string label, float x, float y, JArray inputs, JArray outputs, string body, JArray components, JArray connections, string groupName)
            => ChatWindow.ExecuteCreateCSharpScriptComponent(aliasId, label, x, y, inputs, outputs, body, components, connections, groupName);
        public string ExecuteEditCSharpScript(string id, string mode, string body)
            => ChatWindow.ExecuteEditCSharpScriptComponent(id, mode, body);
    }
}
