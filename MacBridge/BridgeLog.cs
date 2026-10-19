using System;
using System.IO;

namespace Magpie
{
    // The Mac host deliberately has no Windows UI or credential dependencies.
    public static class AddGhLog
    {
        private static readonly object Gate = new object();
        public static void Debug(string message) => Write("DEBUG", message);
        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        private static void Write(string level, string message)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Magpie");
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, "bridge.log"), $"{DateTime.UtcNow:o} {level} {message}{Environment.NewLine}");
                }
            }
            catch { /* Logging must not prevent a host operation. */ }
        }
    }
}
