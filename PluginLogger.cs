using System;

namespace Baklava
{
    public static class PluginLogger
    {
        private const string RedColor = "\u001b[31m";
        private const string ResetColor = "\u001b[0m";

        public static void Log(string message)
        {
            try
            {
                // Keep the Console.WriteLine for containers that capture stdout
                Console.WriteLine($"[Baklava] 🟢 {message}");

                // Also append to a dedicated log file in Jellyfin's config/log so we can
                // reliably see plugin activity even if Console output isn't routed the
                // same way as Jellyfin's logger.
                try
                {
                    var logPath = "/config/log/baklava-plugin.log";
                    var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] [Baklava] {message}\n";
                    System.IO.File.AppendAllText(logPath, line);
                }
                catch
                {
                    // Swallow file write errors to avoid impacting plugin startup
                }
            }
            catch
            {
                // Very defensive: do not throw from logger
            }
        }
    }
}
