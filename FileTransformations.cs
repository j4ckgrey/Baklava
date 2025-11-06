using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using MyJellyfinPlugin.Model;

namespace Baklava
{
    // Callback methods invoked by the FileTransformation plugin.
    // The FileTransformation plugin will call the method specified in the
    // registration payload (callbackClass + callbackMethod). 
    public static class FileTransformations
    {
        private const string InjectionMarker = "<!-- MyJellyfinPlugin Injected -->";
        // Folder name used under the Jellyfin plugins directory. We include the
        // version here to match how plugins are typically installed, e.g.
        // MyJellyfinPlugin_0.1.0.0
        private const string PluginFolderName = "MyJellyfinPlugin_0.1.0.0";

        // JavaScript files to inject, IN ORDER (dependencies first)
        private static readonly string[] JsFiles = new[]
        {
            "shared-utils.js",          // Creates window.MediaServerUI - MUST BE FIRST
            "library-status.js",        // Creates window.LibraryStatus  
            "details-modal.js",         // Uses window.MediaServerUI (with retry)
            "select-to-cards.js",       // Uses window.MediaServerUI (with retry)
            "request-manager.js",       // API functions for request operations
            "requests-header-button.js", // Adds requests button to header with dropdown
            "search-toggle.js"          // Standalone
        };

        // Transform method signature: accepts PatchRequestPayload, returns string
        public static string Transform(PatchRequestPayload payload)
        {
            try
            {
                PluginLogger.Log("=== Transform METHOD CALLED ===");
                
                if (payload?.Contents == null)
                {
                    PluginLogger.Log("Transform: payload or Contents is null");
                    return string.Empty;
                }

                var html = payload.Contents;
                PluginLogger.Log($"Transform: Processing content ({html.Length} chars)");

                // CRITICAL: Only transform HTML files, not JavaScript chunks!
                // If content doesn't look like HTML, return unchanged
                if (!html.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) &&
                    !html.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    PluginLogger.Log("Transform: Not an HTML file, skipping injection");
                    return html;
                }

                // Check if already injected to prevent double-injection
                if (html.Contains(InjectionMarker))
                {
                    PluginLogger.Log("Transform: Already injected, skipping");
                    return html;
                }

                // Inject the script
                var modified = InjectScript(html);
                PluginLogger.Log($"Transform: Injection complete ({modified.Length} chars)");

                return modified;
            }
            catch (Exception ex)
            {
                PluginLogger.Log($"Transform EXCEPTION: {ex.Message}");
                PluginLogger.Log($"Stack: {ex.StackTrace}");
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
