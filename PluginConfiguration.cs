using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using MyJellyfinPlugin.Api;

namespace Baklava
{
    // Holds settings for your plugin. Add properties here to persist configuration.
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<MediaRequest> Requests { get; set; } = new List<MediaRequest>();
    }
}
