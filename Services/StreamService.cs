#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Baklava.Services
{
    public class StreamService
    {
        private readonly ILogger<StreamService> _logger;
        private readonly MediaBrowser.Common.Plugins.IPluginManager _pluginManager;

        public StreamService(ILogger<StreamService> logger, MediaBrowser.Common.Plugins.IPluginManager pluginManager)
        {
            _logger = logger;
            _pluginManager = pluginManager;
        }

        private string? GetGelatoStremioUrl()
        {
            try
            {
                var localPlugin = _pluginManager.Plugins.FirstOrDefault(p => p.Name == "Gelato");
                if (localPlugin == null || localPlugin.Instance == null) return null;
                
                var plugin = localPlugin.Instance;

                // Plugin.Configuration is BasePluginConfiguration, we need to cast to actual type or use dynamic/reflection
                // Gelato.Configuration.PluginConfiguration has "Url" property
                var config = plugin.GetType().GetProperty("Configuration")?.GetValue(plugin);
                if (config == null) return null;
                
                var urlProp = config.GetType().GetProperty("Url");
                if (urlProp != null)
                {
                    return urlProp.GetValue(config) as string;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Stream] Failed to get Gelato Stremio URL via reflection");
            }
            return null;
        }

        public async Task<string?> ResolveStremioStreamAsync(string stremioUrl)
        {
            // stremioUrl format: stremio://type/id or stremio://type/id/streamId
            try 
            {
                // 1. Get Addon URL from Gelato config
                var addonUrl = GetGelatoStremioUrl();
                if (string.IsNullOrEmpty(addonUrl)) 
                {
                    _logger.LogWarning("[Baklava.Stream] Cannot resolve stremio URL: Gelato addon URL not found in config");
                    return null;
                }
                
                // Clean URLs
                addonUrl = addonUrl.TrimEnd('/');
                if (addonUrl.EndsWith("/manifest.json")) addonUrl = addonUrl.Substring(0, addonUrl.Length - "/manifest.json".Length);
                
                // 2. Parse stremio URL manually (Uri class treats 'movie' as Host in stremio://movie/id)
                var cleanUrl = stremioUrl.Replace("stremio://", "", StringComparison.OrdinalIgnoreCase).Trim('/');
                var parts = cleanUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length < 2) 
                {
                    _logger.LogWarning("[Baklava.Stream] Stremio URL format invalid: {Url}", stremioUrl);
                    return null;
                }
                
                var type = parts[0]; // movie
                var id = parts[1];   // tt12345
                // parts[2] might be streamId
                
                // 3. Request stream list from addon
                // Endpoint: {addonUrl}/stream/{type}/{id}.json
                var requestUrl = $"{addonUrl}/stream/{type}/{id}.json";
                _logger.LogInformation("[Baklava.Stream] Resolving Stremio URL: {StremioUrl} -> Fetching {RequestUrl}", stremioUrl, requestUrl);
                
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetStringAsync(requestUrl);
                
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("streams", out var streamsElement) && streamsElement.GetArrayLength() > 0)
                {
                    // Pick first valid stream
                    foreach (var stream in streamsElement.EnumerateArray())
                    {
                        if (stream.TryGetProperty("url", out var urlProp) && !string.IsNullOrEmpty(urlProp.GetString()))
                        {
                            var resolvedUrl = urlProp.GetString();
                            _logger.LogInformation("[Baklava.Stream] Resolved Stremio URL {StremioUrl} -> {ResolvedUrl}", stremioUrl, resolvedUrl);
                            return resolvedUrl;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[Baklava.Stream] Check for streams returned empty for {StremioUrl}", stremioUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Stream] Error resolving Stremio URL: {Url}", stremioUrl);
            }
            return null;
        }

        public async Task<FfprobeResult?> RunFfprobeAsync(string url)
        {
            // Standard probe (fast): 5MB / 5s
            int standardProbeSize = 5000000;
            int standardAnalyzeDuration = 5000000;
            
            // Prefer Jellyfin-bundled ffprobe if present
            var candidates = new[] { "/usr/lib/jellyfin-ffmpeg/ffprobe", "/usr/bin/ffprobe", "ffprobe" };
            string? ffprobePath = null;
            foreach (var c in candidates)
            {
                try { if (System.IO.File.Exists(c)) { ffprobePath = c; break; } } catch { }
            }
            if (ffprobePath == null) ffprobePath = "ffprobe";

            // Helper to execute probe
            async Task<(string? json, string? error)> ExecuteProbe(int probeSize, int analyzeDuration)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -print_format json -show_streams -analyzeduration {analyzeDuration} -probesize {probeSize} -fflags +nobuffer+fastseek -rw_timeout 5000000 -user_agent \"Baklava/1.0\" \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                var tcs = new TaskCompletionSource<bool>();
                
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();

                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) => tcs.TrySetResult(true);

                if (!proc.Start()) return (null, "failed to start");
                
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var stdoutTask = Task.Run(async () => {
                    while (!proc.HasExited) await Task.Delay(100);
                    return stdoutBuilder.ToString();
                });
                var stderrTask = Task.Run(async () => {
                   while (!proc.HasExited) await Task.Delay(100);
                   return stderrBuilder.ToString();
                });

                // timeout 30s
                var waitTask = Task.WhenAny(tcs.Task, Task.Delay(30000));
                
                // Allow longer timeout if duration is high
                if (analyzeDuration > 20000000) waitTask = Task.WhenAny(tcs.Task, Task.Delay(analyzeDuration / 1000 + 10000));
                
                var completed = await waitTask;
                bool exited = completed == tcs.Task;
                
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    _logger.LogWarning("[Baklava.Stream] FFprobe timed out for {Url}", url);
                    return (null, "timeout");
                }

                return (await stdoutTask, await stderrTask);
            }

            // 1. Initial Standard Probe
            var (outJson, errText) = await ExecuteProbe(standardProbeSize, standardAnalyzeDuration);

            if (!string.IsNullOrEmpty(errText))
            {
                if (errText.Contains("403 Forbidden") || errText.Contains("404 Not Found") || string.IsNullOrEmpty(outJson))
                    _logger.LogWarning("[Baklava.Stream] FFprobe Stderr: {Err}", errText);
            }

            if (string.IsNullOrEmpty(outJson)) return null;
            
            _logger.LogDebug("[Baklava.Stream] FFprobe Output: {Json}", outJson);

            try
            {
                using var doc = JsonDocument.Parse(outJson);
                var res = new FfprobeResult();
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        var codecType = s.GetProperty("codec_type").GetString();
                        var index = s.GetProperty("index").GetInt32();
                        var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        if (codec == "hdmv_pgs_subtitle") codec = "pgssub";
                        string? lang = null;
                        string? title = null;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("language", out var tLang)) lang = tLang.GetString();
                            if (tags.TryGetProperty("title", out var tTitle)) title = tTitle.GetString();
                        }

                        if (codecType == "audio")
                        {
                            int? channels = null;
                            if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number) channels = ch.GetInt32();
                            long? bitRate = null;
                            if (s.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String)
                            {
                                if (long.TryParse(br.GetString(), out var brv)) bitRate = brv;
                            }
                            res.Audio.Add(new FfprobeAudio { Index = index, Title = title, Language = lang, Codec = codec, Channels = channels, Bitrate = bitRate });
                        }
                        else if (codecType == "subtitle")
                        {
                            res.Subtitles.Add(new FfprobeSubtitle { Index = index, Title = title, Language = lang, Codec = codec });
                        }
                    }
                }
                
                _logger.LogInformation("[Baklava.Stream] Parsed Probe: {AudioCount} audio, {SubsCount} subs", res.Audio.Count, res.Subtitles.Count);
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Stream] FFprobe Parsing Error");
                return null;
            }
        }
    }

    public class FfprobeResult
    {
        public List<FfprobeAudio> Audio { get; set; } = new();
        public List<FfprobeSubtitle> Subtitles { get; set; } = new();
    }

    public class FfprobeAudio
    {
        public int Index { get; set; }
        public string? Title { get; set; }
        public string? Language { get; set; }
        public string? Codec { get; set; }
        public int? Channels { get; set; }
        public long? Bitrate { get; set; }
    }

    public class FfprobeSubtitle
    {
        public int Index { get; set; }
        public string? Title { get; set; }
        public string? Language { get; set; }
        public string? Codec { get; set; }
        public bool IsForced { get; set; }
        public bool IsDefault { get; set; }
    }
}
