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
        private const string InjectionMarker = "<!-- MyJellyfinPlugin Injected -->";
    // Folder name used under the Jellyfin plugins directory.
    // Use the assembly name at runtime so fallback static references
    // match the actual folder the plugin is installed to (e.g. "Baklava").
    private static readonly string PluginFolderName = Assembly.GetExecutingAssembly().GetName().Name ?? "Baklava";

        // JavaScript files to inject, IN ORDER (dependencies first).
        // Updated to match the actual files shipped in `Files/wwwroot`.
        // `requests.js` is a consolidated bundle that replaces several
        // older per-file modules (request-manager, requests-menu, header button).
        private static readonly string[] JsFiles = new[]
        {
            "details-modal.js",    // Modal & TMDB metadata integration
            "library-status.js",   // Library presence / status UI
            "select-to-cards.js",  // Playback streams -> card UI
            "requests.js",         // Consolidated requests manager, header button, menu
            "search-toggle.js"     // Search toggle globe icon
        };

        // Transform method signature: accepts PatchRequestPayload, returns string
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
                        PluginLogger.Log($"InjectScript: embedded custom.css ({cssContent.Length} chars)");
                    }
                }
                else
                {
                    // Fallback: reference the static CSS file in the plugin folder so
                    // Jellyfin can serve it from /plugins/<PluginFolder>/Files/wwwroot/custom.css
                    styleTags.Add($"<link rel=\"stylesheet\" href=\"/plugins/{PluginFolderName}/Files/wwwroot/custom.css\">");
                    PluginLogger.Log("InjectScript: using static reference to custom.css");
                }
            }
            catch (Exception ex)
            {
                PluginLogger.Log($"InjectScript: Failed to load CSS: {ex.Message}");
            }

            // Inject each JavaScript file in order
            foreach (var jsFile in JsFiles)
            {
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
                            PluginLogger.Log($"InjectScript: embedded {jsFile} ({jsContent.Length} chars)");
                            continue;
                        }
                    }

                    // Fallback to static file reference
                    scriptTags.Add($"<script src=\"/plugins/{PluginFolderName}/Files/wwwroot/{jsFile}\"></script>");
                    PluginLogger.Log($"InjectScript: static reference to {jsFile}");
                }
                catch (Exception ex)
                {
                    PluginLogger.Log($"InjectScript: Error loading {jsFile}: {ex.Message}");
                }
            }

            if (scriptTags.Count == 0 && styleTags.Count == 0)
            {
                PluginLogger.Log("InjectScript: No scripts or styles to inject");
                return html;
            }

            // Combine all tags with marker - CSS first, then JS
            var allTags = $"{InjectionMarker}\n{string.Join("\n", styleTags)}\n{string.Join("\n", scriptTags)}\n";
            PluginLogger.Log($"InjectScript: Total scripts to inject: {scriptTags.Count}, styles: {styleTags.Count}");

            // Prefer injecting before </body>, fallback to </head>, else append
            var bodyClose = Regex.Match(html, "</body>", RegexOptions.IgnoreCase);
            if (bodyClose.Success)
            {
                try { PluginLogger.Log("InjectScript: injecting before </body>"); } catch { }
                return html.Substring(0, bodyClose.Index) + allTags + html.Substring(bodyClose.Index);
            }

            var headClose = Regex.Match(html, "</head>", RegexOptions.IgnoreCase);
            if (headClose.Success)
            {
                try { PluginLogger.Log("InjectScript: injecting before </head>"); } catch { }
                return html.Substring(0, headClose.Index) + allTags + html.Substring(headClose.Index);
            }

            try { PluginLogger.Log("InjectScript: appending at end"); } catch { }
            return html + allTags;
        }
    }
}
