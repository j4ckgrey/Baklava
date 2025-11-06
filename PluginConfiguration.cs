using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using Baklava.Api;

namespace Baklava
{
    // Holds settings for your plugin. Add properties here to persist configuration.
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<MediaRequest> Requests { get; set; } = new List<MediaRequest>();
        public string TmdbApiKey { get; set; } = string.Empty;
        public bool GlobalSearchByDefault { get; set; } = true;
    }
}
