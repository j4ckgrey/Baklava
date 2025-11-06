using System;

namespace Baklava
{
    public static class PluginLogger
    {
        public static void Log(string message)
        {
            Console.WriteLine($"[Baklava] {message}");
        }
    }
}
