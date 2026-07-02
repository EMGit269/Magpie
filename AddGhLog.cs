using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Magpie
{
    public enum AddGhLogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 轻量级文件 + 调试输出日志，避免吞异常时无任何排查线索。
    /// </summary>
    public static class AddGhLog
    {
        private static readonly object Sync = new object();
        private static string _logDir;

        private static string LogDirectory
        {
            get
            {
                if (_logDir != null) return _logDir;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _logDir = Path.Combine(root, "Magpie", "logs");
                try { Directory.CreateDirectory(_logDir); } catch { /* ignore */ }
                return _logDir;
            }
        }

        private static string LogFilePath =>
            Path.Combine(LogDirectory, "magpie-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        public static void Debug(string message)
        {
            if (!DeploymentOptions.EnableVerboseLogging) return;
            Write(AddGhLogLevel.Debug, message);
        }

        public static void Info(string message) => Write(AddGhLogLevel.Info, message);

        public static void Warn(string message) => Write(AddGhLogLevel.Warning, message);

        public static void Error(string message, Exception ex = null)
        {
            string m = ex == null ? message : message + ": " + ex;
            Write(AddGhLogLevel.Error, m);
            MaybeShowTemporaryPopup("Magpie Error", m, MessageBoxIcon.Error);
        }

        /// <summary>
        /// 临时排错：在启用 MAGPIE_ERROR_POPUP=1 时弹窗提示（并写入 Warning 日志行）。
        /// </summary>
        public static void UserAlert(string title, string message)
        {
            title = string.IsNullOrWhiteSpace(title) ? "Magpie" : title.Trim();
            string line = message ?? "";
            Write(AddGhLogLevel.Warning, "UserAlert: " + title + " — " + line);
            MaybeShowTemporaryPopup(title, line, MessageBoxIcon.Warning);
        }

        /// <summary> 仅 getenv MAGPIE_ERROR_POPUP=1 时弹出；避免常驻打扰。 </summary>
        internal static void MaybeShowTemporaryPopup(string title, string text, MessageBoxIcon icon = MessageBoxIcon.Exclamation)
        {
            if (!DeploymentOptions.EnableTemporaryErrorPopup) return;
            if (string.IsNullOrWhiteSpace(text)) text = "(无详情)";
            if (text.Length > 3800)
                text = text.Substring(0, 3797) + "...";

            void ShowSafe()
            {
                try
                {
                    MessageBox.Show(text ?? "", title ?? "Magpie", MessageBoxButtons.OK, icon,
                        MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                }
                catch { /* 宿主极简环境 */ }
            }

            try
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)ShowSafe);
            }
            catch
            {
                try { ShowSafe(); } catch { }
            }
        }
        private static void Write(AddGhLogLevel level, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + (message ?? "") + Environment.NewLine;
            System.Diagnostics.Debug.WriteLine("[Magpie] " + line.TrimEnd());

            lock (Sync)
            {
                try
                {
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
                catch
                {
                    // 日志本身失败时不抛出，避免干扰宿主
                }
            }
        }
    }
}
