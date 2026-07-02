using Newtonsoft.Json.Linq;
using Magpie.HostBridge;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly GrasshopperHostToolExecutor _hostBridgeExecutor =
            new GrasshopperHostToolExecutor(new ChatWindowHostBridgeBackend());

        private static JObject BuildHostBridgeManifestPayload()
        {
            return _hostBridgeExecutor.BuildManifestPayload();
        }

        private static JObject ExecuteHostBridgeRequest(JObject request)
        {
            return _hostBridgeExecutor.ExecuteRequest(request);
        }
    }
}
