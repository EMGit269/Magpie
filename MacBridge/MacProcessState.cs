using System;
using System.Linq;
using System.Reflection;
using Eto.Forms;

namespace Magpie.MacBridge
{
    // Hot-loaded builds have separate statics, but share Rhino's process and Eto types.
    internal static class MacProcessState
    {
        internal static readonly object Gate = string.Intern("Magpie.MacBridge.ProcessGate");
        private const string WindowKey = "Magpie.MacBridge.AgentWindow";

        private static Type[] Types(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "Magpie.MacBridge" || a.GetName().Name.StartsWith("Magpie.MacBridge.Preview", StringComparison.Ordinal))
            .Select(a => a.GetType("Magpie.MacBridge." + name)).Where(t => t != null).ToArray();

        internal static bool BridgeIsRunning(int port)
        {
            foreach (var type in Types("Bridge"))
            {
                if ((int)type.GetProperty("Port").GetValue(null) != port) continue;
                // Inspect the owned runtime, not IsRunning, which may itself query this helper.
                var runtime = type.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (runtime != null && (bool)runtime.GetType().GetProperty("IsRunning").GetValue(runtime)) return true;
            }
            return false;
        }

        internal static Form FindWindow()
        {
            if (AppDomain.CurrentDomain.GetData(WindowKey) is Form shared) return shared;
            // Preserve an existing conversation when a newer build is loaded into Rhino.
            foreach (var type in Types("MacAgentWindow"))
            {
                var window = type.GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Form;
                if (window != null) { RegisterWindow(window); return window; }
            }
            return null;
        }

        internal static void RegisterWindow(Form window)
        {
            AppDomain.CurrentDomain.SetData(WindowKey, window);
            window.Closed += (_, __) => {
                if (ReferenceEquals(AppDomain.CurrentDomain.GetData(WindowKey), window))
                    AppDomain.CurrentDomain.SetData(WindowKey, null);
            };
        }

        internal static void FloatWindow(Form window)
        {
            window.Topmost = true;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            // GH itself floats when activated. Stay one level above palettes,
            // below modal dialogs, and hide when Rhino yields to another app.
            var native = window.ControlObject;
            var level = native.GetType().GetProperty("Level");
            if (level?.PropertyType.IsEnum == true)
                level.SetValue(native, Enum.ToObject(level.PropertyType,
                    Convert.ToInt64(Enum.Parse(level.PropertyType, "Floating")) + 1));
            native.GetType().GetProperty("HidesOnDeactivate")?.SetValue(native, true);
            window.BringToFront();
        }
    }
}
