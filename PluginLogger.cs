using System;
using System.IO;

namespace Baklava
{
    internal static class PluginLogger
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "myjellyfinplugin.log");

        public static void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.UtcNow:O}] {message}" + Environment.NewLine;
                // Write to stdout so the container logs will capture this too.
                Console.WriteLine("[MyJellyfinPlugin] " + line);
                // Also try to persist to temp file as a fallback for debugging on the host.
                File.AppendAllText(LogPath, line);
            }
            catch
            {
                // Swallow — best-effort logging for debugging only
            }
        }
    }
}
