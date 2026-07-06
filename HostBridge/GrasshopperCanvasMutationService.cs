using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal sealed class GrasshopperCanvasMutationService
    {
        private readonly IGrasshopperHostBridgeBackend _backend;

        public GrasshopperCanvasMutationService(IGrasshopperHostBridgeBackend backend)
        {
            _backend = backend;
        }

        public string RecomputeCanvas()
        {
            return _backend.ExecuteRecomputeCanvas();
        }

        public string ConnectComponents(string fromId, int fromIndex, string toId, int toIndex, string fromPortLabel, string toPortLabel)
        {
            return _backend.ExecuteConnectComponents(
                _backend.ResolveToolObjectId(fromId),
                fromIndex,
                _backend.ResolveToolObjectId(toId),
                toIndex,
                fromPortLabel,
                toPortLabel);
        }

        public string RemoveComponent(string id)
        {
            return _backend.ExecuteRemoveComponent(_backend.ResolveToolObjectId(id));
        }

        public string SetComponentValue(string id, string value, double? min, double? max, int? decimals, string property, string graphMapperType)
        {
            return _backend.ExecuteSetComponentValue(
                _backend.ResolveToolObjectId(id),
                value,
                min,
                max,
                decimals,
                property,
                graphMapperType);
        }

        public string CreateComponentGraph(JArray components, JArray connections, string groupName, string csharpFirstHelperReason = null, string csharpFirstHelperReasonDetail = null)
        {
            return _backend.ExecuteCreateComponentGraph(
                components ?? new JArray(),
                connections ?? new JArray(),
                groupName,
                csharpFirstHelperReason,
                csharpFirstHelperReasonDetail);
        }

        public string CreateCSharpScript(string aliasId, string label, float x, float y, JArray inputs, JArray outputs, string body, JArray components, JArray connections, string groupName)
        {
            return _backend.ExecuteCreateCSharpScript(aliasId, label, x, y, inputs ?? new JArray(), outputs ?? new JArray(), body, components ?? new JArray(), connections ?? new JArray(), groupName);
        }

        public string EditCSharpScript(string id, string mode, string body)
        {
            return _backend.ExecuteEditCSharpScript(_backend.ResolveToolObjectId(id), mode, body);
        }
    }
}
