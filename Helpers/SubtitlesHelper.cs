#nullable enable
using System;
using System.IO;
using System.Xml;
using MediaBrowser.Common.Configuration;

namespace Baklava.Helpers
{
    public static class SubtitlesHelper
    {
        public static string? GetGelatoStremioUrl(IApplicationPaths appPaths, Guid userId)
        {
            try
            {
                // Standard Jellyfin plugin config path
                var configDir = appPaths.PluginConfigurationsPath;
                var configPath = Path.Combine(configDir, "Gelato.xml");

                if (!File.Exists(configPath))
                {
                    return null;
                }

                var doc = new XmlDocument();
                doc.Load(configPath);
                
                // <PluginConfiguration> <Url>...</Url> </PluginConfiguration>
                var node = doc.SelectSingleNode("//Url");
                if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                {
                    var url = node.InnerText.Trim();
                    // Basic cleanup
                    if (url.EndsWith("/manifest.json")) 
                        url = url.Substring(0, url.Length - "/manifest.json".Length);
                    return url.TrimEnd('/');
                }
            }
            catch 
            {
                // Ignore errors
            }
            return null;
        }
    }
}
