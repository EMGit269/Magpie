using Magpie.HostBridge;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly GrasshopperHostBridgeRuntime _hostBridgeRuntime =
            new GrasshopperHostBridgeRuntime(_hostBridgeExecutor);

        public static void EnsureHostBridgeRuntimeForExternalClients()
        {
            _hostBridgeRuntime.EnsureServer();
        }

        public static void StopHostBridgeRuntimeForExternalClients()
        {
            if (_window != null)
                return;
            _hostBridgeRuntime.StopServer();
        }
    }
}
