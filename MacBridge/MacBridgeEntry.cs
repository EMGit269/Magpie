using System;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Magpie.HostBridge;
using Newtonsoft.Json.Linq;

namespace Magpie.MacBridge
{
    public sealed class MacBridgeInfo : GH_AssemblyInfo
    {
        public override string Name => "Magpie Mac Bridge";
        public override string Description => "Local Agent bridge for Rhino 8 / Grasshopper on macOS.";
        public override Guid Id => new Guid("388c30be-e650-471c-9ce5-1f3d47c0307e");
        public override string AuthorName => "Magpie";
    }

    public sealed class MacBridgePriority : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            Bridge.Start();
            Rhino.RhinoApp.Closing += (_, __) => { Bridge.Stop(); MacAgentService.StopOwnedProcess(); };
            return GH_LoadingInstruction.Proceed;
        }
    }

    public static class Bridge
    {
        private static readonly object Gate = new object();
        private static GrasshopperHostBridgeRuntime _runtime;
        public static int Port { get; private set; } = 8765;
        public static bool IsRunning => _runtime?.IsRunning == true;

        public static void Start()
        {
            lock (Gate)
            {
                if (IsRunning) return;
                var setting = Environment.GetEnvironmentVariable("MAGPIE_HOST_BRIDGE_PORT");
                if (!string.IsNullOrEmpty(setting))
                {
                    if (!int.TryParse(setting, out var port) || port < 1 || port > 65535)
                        throw new InvalidOperationException("MAGPIE_HOST_BRIDGE_PORT must be between 1 and 65535.");
                    Port = port;
                }
                var backend = new MacDocumentBackend();
                var executor = new GrasshopperHostToolExecutor(
                    new GrasshopperDocumentQueryService(backend),
                    new GrasshopperCanvasMutationService(backend), MacDocumentBackend.Tools);
                _runtime = new GrasshopperHostBridgeRuntime(executor, Port, request => OnUiThread(executor, request));
                _runtime.EnsureServer();
                if (!IsRunning)
                    throw new InvalidOperationException($"Could not start Magpie bridge on port {Port}. See ~/Library/Logs/Magpie/bridge.log.");
                AddGhLog.Info($"Mac bridge ready at http://127.0.0.1:{Port}");
            }
        }

        public static void Stop()
        {
            lock (Gate) { _runtime?.StopServer(); _runtime = null; }
        }

        private static Task<JObject> OnUiThread(GrasshopperHostToolExecutor executor, JObject request)
        {
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Dispatch the entire request, including ID resolution, to Rhino's main thread.
                // Await the completion instead of assuming InvokeOnUiThread ran synchronously.
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        var bound = request?["document_id"]?.ToString();
                        var active = Grasshopper.Instances.ActiveCanvas?.Document;
                        if (bound != null && (active == null || active.DocumentID.ToString() != bound))
                            completion.TrySetResult(new JObject { ["request_id"] = request?["request_id"], ["tool"] = request?["tool"],
                                ["status"] = "error", ["error"] = "Canvas binding mismatch. Return to the referenced canvas or reference the current canvas in a new conversation." });
                        else completion.TrySetResult(executor.ExecuteRequest(request));
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                }));
            }
            catch (Exception error) { completion.TrySetException(error); }
            return completion.Task;
        }
    }

    public sealed class BridgeStatusComponent : GH_Component
    {
        public BridgeStatusComponent() : base("Magpie Mac Bridge", "Magpie", "Start the local Agent bridge and report its address.", "Magpie", "Host") { }
        public override Guid ComponentGuid => new Guid("f658711b-e784-45fb-b28c-c676e0e8a38d");
        protected override void RegisterInputParams(GH_InputParamManager parameters) { }
        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddTextParameter("URL", "U", "Local bridge address", GH_ParamAccess.item);
            parameters.AddBooleanParameter("Running", "R", "Whether the HTTP listener is running", GH_ParamAccess.item);
        }
        protected override void SolveInstance(IGH_DataAccess data)
        {
            Bridge.Start();
            data.SetData(0, $"http://127.0.0.1:{Bridge.Port}");
            data.SetData(1, Bridge.IsRunning);
        }
    }
}
