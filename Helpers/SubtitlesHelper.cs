using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;

namespace Baklava.Helpers
{
    public static class SubtitlesHelper
    {
        public static string? GetGelatoStremioUrl(IApplicationPaths appPaths, Guid? userId)
        {
            try
            {
                // Typically: /config/plugins/configurations/Gelato.xml
                var configPath = Path.Combine(appPaths.PluginConfigurationsPath, "Gelato.xml");
                if (!File.Exists(configPath)) return null;

                var doc = XDocument.Load(configPath);
                string? url = null;

                if (userId.HasValue)
                {
                    var userConfig = doc.Root?.Element("UserConfigs")
                        ?.Elements("UserConfig")
                        .FirstOrDefault(x => x.Element("UserId")?.Value.Equals(userId.Value.ToString(), StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (userConfig != null)
                    {
                        var userUrl = userConfig.Element("Url")?.Value;
                        if (!string.IsNullOrWhiteSpace(userUrl))
                        {
                            url = userUrl;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    url = doc.Root?.Element("Url")?.Value;
                }

                if (string.IsNullOrWhiteSpace(url)) return null;

                url = url.Trim().TrimEnd('/');
                if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                    url = url[..^"/manifest.json".Length];

                return url;
            }
            catch
            {
                return null;
            }
        }
    }
}
