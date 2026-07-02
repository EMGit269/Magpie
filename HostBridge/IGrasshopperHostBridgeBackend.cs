using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal interface IGrasshopperHostBridgeBackend
    {
        string ResolveToolObjectId(string id);
        string ExecuteGetCanvasSummary();
        string ExecuteQueryComponents(string id, string nameContains, bool? hasErrors, bool? isScript, bool? hasConnections, string portNameContains, int maxResults, int neighborDepth);
        string ExecuteGetComponentContext(string id, int depth, bool includeScriptBodies);
        string ExecuteReadComponentScript(string id);
        string ExecuteCheckGhErrors();
        string ExecuteRecomputeCanvas();
        string ExecuteConnectComponents(string fromId, int fromIndex, string toId, int toIndex, string fromPortLabel, string toPortLabel);
        string ExecuteRemoveComponent(string id);
        string ExecuteSetComponentValue(string id, string value, double? min, double? max, int? decimals, string property, string graphMapperType);
        string ExecuteCreateComponentGraph(JArray components, JArray connections, string groupName);
        string ExecuteCreateCSharpScript(string aliasId, string label, float x, float y, JArray inputs, JArray outputs, string body, JArray components, JArray connections, string groupName);
        string ExecuteEditCSharpScript(string id, string mode, string body);
    }
}
