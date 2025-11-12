using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Baklava.Model;

namespace Baklava
{
    // Callback methods invoked by the FileTransformation plugin.
    // The FileTransformation plugin will call the method specified in the
    // registration payload (callbackClass + callbackMethod). 
    public static class FileTransformations
    {
        private const string InjectionMarker = "<!-- Baklava Injected -->";
    // Folder name used under the Jellyfin plugins directory.
    // Use the assembly name at runtime so fallback static references
    // match the actual folder the plugin is installed to (e.g. "Baklava").
    private static readonly string PluginFolderName = Assembly.GetExecutingAssembly().GetName().Name ?? "Baklava";

        // Get JavaScript files to inject
        private static string[] GetJsFiles()
        {
            // Always inject select-to-cards - CSS will control visibility based on config
            return new[]
            {
                "details-modal.js",       // Modal & TMDB metadata integration
                "library-status.js",      // Library presence / status UI
                "select-to-cards.js",     // Playback streams UI (carousel/dropdown controlled by CSS)
                "reviews-carousel.js",    // TMDB reviews carousel
                "requests.js",            // Consolidated requests manager, header button, menu
                "search-toggle.js"        // Search toggle globe icon
            };
        }        // Transform method signature: accepts PatchRequestPayload, returns string
        public static string Transform(PatchRequestPayload payload)
        {
            try
            {
                if (payload?.Contents == null)
                {
                    return string.Empty;
                }

                var html = payload.Contents;

                // CRITICAL: Only transform HTML files, not JavaScript chunks or fragments
                // If content doesn't look like a complete HTML document, return unchanged
                if (!html.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) &&
                    !html.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    return html;
                }

                // Check if already injected to prevent double-injection
                if (html.Contains(InjectionMarker, StringComparison.Ordinal))
                {
                    return html;
                }

                // Also check if scripts are already present (additional safety check)
                if (html.Contains("SelectToCardsLoaded", StringComparison.Ordinal) ||
                    html.Contains("select-to-cards-style", StringComparison.Ordinal))
                {
                    return html;
                }

                // Inject the script
                var modified = InjectScript(html);

                return modified;
            }
            catch (Exception ex)
            {
                PluginLogger.Log($"Transform EXCEPTION: {ex.Message}");
                return payload?.Contents ?? string.Empty; // Return original on error
            }
        }

        private static string InjectScript(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;

            // Don't inject multiple times
            if (html.Contains(InjectionMarker, StringComparison.Ordinal)) return html;

            try { PluginLogger.Log("InjectScript: starting"); } catch { }
            var scriptTags = new List<string>();
            var styleTags = new List<string>();
            var asm = Assembly.GetExecutingAssembly();
            var resourceNames = asm.GetManifestResourceNames();

            // First, inject CSS
            try
            {
                var cssPattern = "Files.wwwroot.custom.css";
                var cssMatch = Array.Find(resourceNames, n => n.EndsWith(cssPattern, StringComparison.OrdinalIgnoreCase));
                
                if (cssMatch != null)
                {
                    using (var stream = asm.GetManifestResourceStream(cssMatch))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var cssContent = reader.ReadToEnd();
                        styleTags.Add($"<style>{cssContent}</style>");
                    }
                }
                else
                {
                    // Fallback: reference the static CSS file
                    styleTags.Add($"<link rel=\"stylesheet\" href=\"/plugins/{PluginFolderName}/Files/wwwroot/custom.css\">");
                }
                
                // Add dynamic CSS based on config
                var config = Plugin.Instance?.Configuration;
                var dynamicCss = new System.Text.StringBuilder();
                dynamicCss.AppendLine("<style id=\"baklava-dynamic-ui-style\">");
                
                // Version UI toggle
                var versionUi = config?.VersionUi ?? "carousel";
                if (versionUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    // Show dropdown, hide carousel
                    dynamicCss.AppendLine(".selectContainer.selectSourceContainer { display: flex !important; }");
                    dynamicCss.AppendLine("#stc-carousel-version { display: none !important; }");
                }
                else
                {
                    // Show carousel, hide dropdown
                    dynamicCss.AppendLine(".selectContainer.selectSourceContainer { display: none !important; }");
                    dynamicCss.AppendLine("#stc-carousel-version { display: flex !important; }");
                }
                
                // Audio UI toggle
                var audioUi = config?.AudioUi ?? "carousel";
                if (audioUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    // Show dropdown, hide carousel
                    dynamicCss.AppendLine(".selectContainer.selectAudioContainer { display: flex !important; }");
                    dynamicCss.AppendLine("#stc-carousel-audio { display: none !important; }");
                }
                else
                {
                    // Show carousel, hide dropdown
                    dynamicCss.AppendLine(".selectContainer.selectAudioContainer { display: none !important; }");
                    dynamicCss.AppendLine("#stc-carousel-audio { display: flex !important; }");
                }
                
                // Subtitle UI toggle
                var subtitleUi = config?.SubtitleUi ?? "carousel";
                if (subtitleUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    // Show dropdown, hide carousel
                    dynamicCss.AppendLine(".selectContainer.selectSubtitlesContainer { display: flex !important; }");
                    dynamicCss.AppendLine("#stc-carousel-subtitle { display: none !important; }");
                }
                else
                {
                    // Show carousel, hide dropdown
                    dynamicCss.AppendLine(".selectContainer.selectSubtitlesContainer { display: none !important; }");
                    dynamicCss.AppendLine("#stc-carousel-subtitle { display: flex !important; }");
                }

                dynamicCss.AppendLine("</style>");
                styleTags.Add(dynamicCss.ToString());
            }
            catch (Exception ex)
            {
                try { PluginLogger.Log($"InjectScript: Failed to load CSS: {ex.Message}"); } catch { }
            }

            // Inject each JavaScript file in order
            var jsFiles = GetJsFiles();
            foreach (var jsFile in jsFiles)
            {
                bool embedded = false;
                try
                {
                    // Try to load as embedded resource first
                    var resourcePattern = $"Files.wwwroot.{jsFile}";
                    var match = Array.Find(resourceNames, n => n.EndsWith(resourcePattern, StringComparison.OrdinalIgnoreCase));
                    
                    if (match != null)
                    {
                        using var stream = asm.GetManifestResourceStream(match);
                        if (stream != null)
                        {
                            using var reader = new System.IO.StreamReader(stream);
                            var jsContent = reader.ReadToEnd();
                            scriptTags.Add($"<script type=\"text/javascript\">\n/* {jsFile} */\n{jsContent}\n</script>");
                            embedded = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { PluginLogger.Log($"InjectScript: Error loading {jsFile}: {ex.Message}"); } catch { }
                }

                // Fallback to static file reference ONLY if embedded failed
                if (!embedded)
                {
                    scriptTags.Add($"<script src=\"/plugins/{PluginFolderName}/Files/wwwroot/{jsFile}\"></script>");
                }
            }

            if (scriptTags.Count == 0 && styleTags.Count == 0)
            {
                return html;
            }

            // Combine all tags with marker - CSS first, then JS
            var allTags = $"{InjectionMarker}\n{string.Join("\n", styleTags)}\n{string.Join("\n", scriptTags)}\n";

            // Prefer injecting before </body>, fallback to </head>, else append
            var bodyClose = Regex.Match(html, "</body>", RegexOptions.IgnoreCase);
            if (bodyClose.Success)
            {
                return html.Substring(0, bodyClose.Index) + allTags + html.Substring(bodyClose.Index);
            }

            var headClose = Regex.Match(html, "</head>", RegexOptions.IgnoreCase);
            if (headClose.Success)
            {
                return html.Substring(0, headClose.Index) + allTags + html.Substring(headClose.Index);
            }

            return html + allTags;
        }
    }
}