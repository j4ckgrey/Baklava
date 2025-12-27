using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Common.Configuration;

namespace Baklava.Api
{
    /// <summary>
    /// Middleware to intercept index.html requests and inject Baklava UI scripts.
    /// This runs before StaticFileMiddleware, ensuring our version is served.
    /// </summary>
    public class UIInjectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IApplicationPaths _appPaths;

        public UIInjectionMiddleware(RequestDelegate next, IApplicationPaths appPaths)
        {
            _next = next;
            _appPaths = appPaths;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Check if request is for the web interface index
            var path = context.Request.Path.Value;
            bool isIndexRequest = path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/web/", StringComparison.OrdinalIgnoreCase);

            // If it is an index request AND injection is enabled
            if (isIndexRequest && (Plugin.Instance?.Configuration?.EnableBaklavaUI ?? true))
            {
                try
                {
                    var webPath = _appPaths.WebPath;
                    var indexFile = Path.Combine(webPath, "index.html");

                    if (File.Exists(indexFile))
                    {
                        // value-add: Inject scripts in-memory
                        var originalHtml = await File.ReadAllTextAsync(indexFile);
                        
                        // Prevent double injection
                        if (!originalHtml.Contains("<!-- Baklava Injected -->"))
                        {
                            var modifiedHtml = InjectBaklavaUI(originalHtml);
                            
                            context.Response.ContentType = "text/html; charset=utf-8";
                            context.Response.StatusCode = 200;
                            await context.Response.WriteAsync(modifiedHtml, Encoding.UTF8);
                            return; // Short-circuit: do not call _next
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLogger.Log($"UIInjectionMiddleware error: {ex.Message}");
                    // On error, fall through to default handler (StaticFiles)
                }
            }

            // Not an index request or disabled or error -> continue pipeline
            await _next(context);
        }

        private string InjectBaklavaUI(string html)
        {
            var injection = GenerateInjectionContent();

            if (html.Contains("</body>"))
                return html.Replace("</body>", injection + "\n</body>");
            
            if (html.Contains("</head>"))
                return html.Replace("</head>", injection + "\n</head>");

            return html + injection;
        }

        private string GenerateInjectionContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n<!-- Baklava Injected -->");
            
            // Inject user config so client-side JS can read it
            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                sb.AppendLine("<script id=\"baklava-config\">");
                sb.AppendLine("window.BaklavaConfig = {");
                sb.AppendLine($"    versionUi: '{config.VersionUi ?? "carousel"}',");
                sb.AppendLine($"    audioUi: '{config.AudioUi ?? "carousel"}',");
                sb.AppendLine($"    subtitleUi: '{config.SubtitleUi ?? "carousel"}'");
                sb.AppendLine("};");
                sb.AppendLine("</script>");
            }

            var scripts = new[]
            {
                "details-modal.js",
                "library-status.js",
                "select-to-cards.js",
                "reviews-carousel.js",
                "requests.js",
                "search-toggle.js",
                "catalogs.js"
            };

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var asmName = asm.GetName().Name;

            // JS Injection
            foreach (var script in scripts)
            {
                try
                {
                    var resourceName = $"{asmName}.Files.wwwroot.{script}";
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var content = reader.ReadToEnd();
                        sb.AppendLine($"<script id=\"baklava-{script}\">");
                        sb.AppendLine(content);
                        sb.AppendLine("</script>");
                    }
                }
                catch (Exception ex) { PluginLogger.Log($"Error reading resource {script}: {ex.Message}"); }
            }

            // CSS Injection
            try
            {
                var cssResource = $"{asmName}.Files.wwwroot.custom.css";
                using var stream = asm.GetManifestResourceStream(cssResource);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    sb.AppendLine("<style id=\"baklava-custom-css\">");
                    sb.AppendLine(reader.ReadToEnd());
                    sb.AppendLine("</style>");
                }
            }
            catch (Exception ex) { PluginLogger.Log($"Error reading CSS: {ex.Message}"); }

            // Dynamic CSS based on config (show/hide carousels vs dropdowns)
            var cfg = Plugin.Instance?.Configuration;
            if (cfg != null)
            {
                sb.AppendLine("<style id=\"baklava-dynamic-ui-style\">");
                
                // Version UI toggle
                var versionUi = cfg.VersionUi ?? "carousel";
                if (versionUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(".selectContainer.selectSourceContainer { display: flex !important; }");
                    sb.AppendLine("#stc-carousel-version { display: none !important; }");
                }
                else
                {
                    sb.AppendLine(".selectContainer.selectSourceContainer { display: none !important; }");
                    sb.AppendLine("#stc-carousel-version { display: flex !important; }");
                }
                
                // Audio UI toggle
                var audioUi = cfg.AudioUi ?? "carousel";
                if (audioUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(".selectContainer.selectAudioContainer { display: flex !important; }");
                    sb.AppendLine("#stc-carousel-audio { display: none !important; }");
                }
                else
                {
                    sb.AppendLine(".selectContainer.selectAudioContainer { display: none !important; }");
                    sb.AppendLine("#stc-carousel-audio { display: flex !important; }");
                }
                
                // Subtitle UI toggle
                var subtitleUi = cfg.SubtitleUi ?? "carousel";
                if (subtitleUi.Equals("dropdown", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(".selectContainer.selectSubtitlesContainer { display: flex !important; }");
                    sb.AppendLine("#stc-carousel-subtitle { display: none !important; }");
                }
                else
                {
                    sb.AppendLine(".selectContainer.selectSubtitlesContainer { display: none !important; }");
                    sb.AppendLine("#stc-carousel-subtitle { display: flex !important; }");
                }
                
                sb.AppendLine("</style>");
            }

            return sb.ToString();
        }
    }
}
