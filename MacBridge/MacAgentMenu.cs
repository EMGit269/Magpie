using System;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;

namespace Magpie.MacBridge
{
    // Grasshopper's Mac editor exposes its menu through Rhino's WinForms shim.
    internal static class MacAgentMenu
    {
        private static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            Rhino.RhinoApp.Idle += OnIdle;
            OnIdle(null, EventArgs.Empty);
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(13)) { Rhino.RhinoApp.Idle -= OnIdle; return; }
            var menu = Instances.DocumentEditor?.MainMenuStrip;
            if (menu == null) return;
            Rhino.RhinoApp.Idle -= OnIdle;
            try
            {
                foreach (var old in menu.Items.OfType<ToolStripMenuItem>().ToList())
                    if (old.Name == "Magpie.AgentMenu") { menu.Items.Remove(old); old.Dispose(); }
                var root = new ToolStripMenuItem("Magpie") { Name = "Magpie.AgentMenu" };
                var open = new ToolStripMenuItem("打开 Agent") { Name = "Magpie.OpenAgent" };
                open.Click += (_, __) => MacAgentWindow.ShowAgent();
                root.DropDownItems.Add(open);
                menu.Items.Add(root);
            }
            catch (Exception error) { AddGhLog.Warn("Could not add Magpie menu: " + error.Message); }
        }
    }
}
