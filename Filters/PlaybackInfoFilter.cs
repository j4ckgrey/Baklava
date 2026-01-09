using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Baklava.Filters;

/// <summary>
/// Middleware that intercepts PlaybackInfo responses and filters MediaSources for web compatibility
/// </summary>
public class PlaybackInfoMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PlaybackInfoMiddleware> _logger;
    
    private static readonly HashSet<string> IncompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac3", "eac3", "dolby", "truehd", "dts", "atmos"
    };
    
    private static readonly HashSet<string> WebCompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp4a", "opus", "vorbis", "mp3", "mpeg"
    };

    public PlaybackInfoMiddleware(RequestDelegate next, ILogger<PlaybackInfoMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if this is a PlaybackInfo request with directPlayOnly parameter
        var isPlaybackInfo = context.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;
        var directPlayOnly = context.Request.Query.ContainsKey("directPlayOnly");

        // Only log when it's actually a PlaybackInfo request or when filtering would happen
        if (isPlaybackInfo)
        {
            _logger.LogInformation("[Baklava] 🟢 PlaybackInfo request: DirectPlayOnly={DirectPlayOnly}", directPlayOnly);
        }

        if (!isPlaybackInfo || !directPlayOnly)
        {
            await _next(context);
            return;
        }

        _logger.LogInformation("[Baklava] 🟢 INTERCEPTING PlaybackInfo request for web compatibility filtering");

        // Capture the response
        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await _next(context);

        // Read and modify the response
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        context.Response.Body.Seek(0, SeekOrigin.Begin);

        try
        {
            var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("MediaSources", out var mediaSources))
            {
                var sources = mediaSources.EnumerateArray().ToList();
                _logger.LogInformation("[Baklava] 🟢 Filtering {Count} media sources", sources.Count);

                var filteredSources = sources.Where(source => HasWebCompatibleAudio(source)).ToList();
                _logger.LogInformation("[Baklava] 🟢 Filtered to {Count} web-compatible sources", filteredSources.Count);

                // Rebuild the JSON with filtered sources
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var property in json.RootElement.EnumerateObject())
                    {
                        if (property.Name == "MediaSources")
                        {
                            writer.WritePropertyName("MediaSources");
                            writer.WriteStartArray();
                            foreach (var source in filteredSources)
                            {
                                source.WriteTo(writer);
                            }
                            writer.WriteEndArray();
                        }
                        else
                        {
                            property.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                text = Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Baklava] 🟢 Error filtering PlaybackInfo response");
        }

        // Write the modified response
        context.Response.Body = originalBodyStream;
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(text);
        await context.Response.WriteAsync(text);
    }

    private bool HasWebCompatibleAudio(JsonElement source)
    {
        if (!source.TryGetProperty("MediaStreams", out var streams))
        {
            return false;
        }

        var audioStreams = streams.EnumerateArray()
            .Where(s => s.TryGetProperty("Type", out var type) && type.GetString() == "Audio")
            .ToList();

        if (audioStreams.Count == 0)
        {
            return false;
        }

        foreach (var audio in audioStreams)
        {
            if (!audio.TryGetProperty("Codec", out var codecProp))
            {
                continue;
            }

            var codec = codecProp.GetString()?.ToLowerInvariant() ?? string.Empty;
            
            if (IncompatibleAudioCodecs.Any(bad => codec.Contains(bad)))
            {
                continue;
            }
            
            if (WebCompatibleAudioCodecs.Any(good => codec.Contains(good)))
            {
                return true;
            }
        }

        return false;
    }
}
