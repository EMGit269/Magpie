namespace Magpie.HostBridge
{
    internal sealed class GrasshopperDocumentQueryService
    {
        private readonly IGrasshopperHostBridgeBackend _backend;

        public GrasshopperDocumentQueryService(IGrasshopperHostBridgeBackend backend)
        {
            _backend = backend;
        }

        public string GetCanvasSummary()
        {
            return _backend.ExecuteGetCanvasSummary();
        }

        public string QueryComponents(string id, string nameContains, bool? hasErrors, bool? isScript, bool? hasConnections, string portNameContains, int maxResults, int neighborDepth)
        {
            return _backend.ExecuteQueryComponents(id, nameContains, hasErrors, isScript, hasConnections, portNameContains, maxResults, neighborDepth);
        }

        public string GetComponentContext(string id, int depth, bool includeScriptBodies)
        {
            return _backend.ExecuteGetComponentContext(_backend.ResolveToolObjectId(id), depth, includeScriptBodies);
        }

        public string ReadComponentScript(string id)
        {
            return _backend.ExecuteReadComponentScript(_backend.ResolveToolObjectId(id));
        }

        public string CheckGhErrors()
        {
            return _backend.ExecuteCheckGhErrors();
        }
    }
}
