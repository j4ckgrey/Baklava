using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Baklava
{
    // Main plugin class. Exposes a configuration page (embedded resource).
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        // The server will call this constructor and provide the application
        // paths and XML serializer. Use this in production so Jellyfin can
        // construct the plugin correctly.
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            try { PluginLogger.Log("Plugin constructed - registration will happen via StartupService"); } catch { }
            // Also attempt to register as early as possible in case scheduled startup tasks
            // are not picked up in some environments. This will run the same registration
            // logic but asynchronously so it won't block plugin construction.
            try
            {
                System.Threading.Tasks.Task.Run(() => TransformationRegistrar.Register());
            }
            catch (Exception ex)
            {
                try { PluginLogger.Log("Plugin constructor registration attempt failed: " + ex.Message); } catch { }
            }
            // Registration moved to StartupService to ensure FileTransformation plugin is loaded first
        }

        // Static instance for access from other classes
        public static Plugin Instance { get; private set; }

        // Human-readable plugin name
        public override string Name => "Baklava";

        // Unique plugin GUID. Generated and inserted automatically.
        public override Guid Id => Guid.Parse("109470b0-d97c-4540-89b7-856d4e5831c7");

        // Provide the embedded HTML config page as a plugin page
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                // Admin/config page
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
                },
                // Client-side script served via PluginPages (embedded resource)
                new PluginPageInfo
                {
                    Name = Name + ".clientScript",
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Files.wwwroot.search-toggle.js", GetType().Namespace)
                }
            };
        }
    }
}
