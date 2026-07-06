using System;
using Newtonsoft.Json.Linq;
using Magpie.HostBridge;
using Magpie.Host;

namespace Magpie
{
    /// <summary>
    /// Grasshopper 宿主门面 — 集中管理 Host Bridge 的生命周期和对外暴露的服务。
    /// 从 ChatWindow 的 partial class 中独立出来，使 Host Bridge 不再依赖 UI 静态类。
    /// </summary>
    public static class GrasshopperHost
    {
        private static readonly MagpieHostBridgeBackend _hostBridgeBackend = new MagpieHostBridgeBackend();
        private static readonly GrasshopperDocumentQueryService _hostDocumentQueryService = new GrasshopperDocumentQueryService(_hostBridgeBackend);
        private static readonly GrasshopperCanvasMutationService _hostCanvasMutationService = new GrasshopperCanvasMutationService(_hostBridgeBackend);
        private static readonly GrasshopperHostToolExecutor _hostBridgeExecutor = new GrasshopperHostToolExecutor(_hostDocumentQueryService, _hostCanvasMutationService);
        private static readonly GrasshopperHostBridgeRuntime _hostBridgeRuntime = new GrasshopperHostBridgeRuntime(_hostBridgeExecutor);

        internal static GrasshopperDocumentQueryService DocumentQueryService => _hostDocumentQueryService;
        internal static GrasshopperCanvasMutationService CanvasMutationService => _hostCanvasMutationService;

        public static JObject BuildHostBridgeManifest() => _hostBridgeExecutor.BuildManifestPayload();
        public static JObject ExecuteHostBridgeRequest(JObject request) => _hostBridgeExecutor.ExecuteRequest(request);

        public static void EnsureHostBridgeRuntime()
        {
            _hostBridgeRuntime.EnsureServer();
        }

        public static void StopHostBridgeRuntime()
        {
            _hostBridgeRuntime.StopServer();
        }
    }
}
