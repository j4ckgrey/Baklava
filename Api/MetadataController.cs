using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/myplugin/metadata")]
    [Produces("application/json")]
    public class MetadataController : ControllerBase
    {
        private readonly ILogger<MetadataController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IUserManager _userManager;
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        
        // Simple in-memory cache (consider using IMemoryCache for production)
        private static readonly Dictionary<string, (DateTime Expiry, string Data)> _cache = new();
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromMinutes(30);

        public MetadataController(
            ILogger<MetadataController> logger, 
            ILibraryManager libraryManager, 
            IMediaSourceManager mediaSourceManager,
            IUserManager userManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _userManager = userManager;
        }

        /// <summary>
        /// Get comprehensive TMDB metadata (replaces multiple JS calls with one endpoint)
        /// </summary>
        [HttpGet("tmdb")]
        public async Task<ActionResult> GetTMDBMetadata(
            [FromQuery] string? tmdbId,
            [FromQuery] string? imdbId,
            [FromQuery] string itemType,
            [FromQuery] string? title,
            [FromQuery] string? year,
            [FromQuery] bool includeCredits = true,
            [FromQuery] bool includeReviews = true)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    return BadRequest(new { error = "TMDB API key not configured" });
                }

                var mediaType = itemType == "series" ? "tv" : "movie";
                JsonDocument? mainData = null;

                // Try TMDB ID first
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    mainData = await FetchTMDBAsync($"/{mediaType}/{tmdbId}", apiKey);
                    if (mainData != null)
                    {
                        return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                    }
                }

                // Try IMDB ID via find endpoint
                if (!string.IsNullOrEmpty(imdbId))
                {
                    var findResult = await FetchTMDBAsync($"/find/{imdbId}", apiKey, new Dictionary<string, string>
                    {
                        { "external_source", "imdb_id" }
                    });

                    if (findResult != null)
                    {
                        var root = findResult.RootElement;
                        JsonElement results = default;
                        
                        if (itemType == "series" && root.TryGetProperty("tv_results", out var tvResults) && tvResults.GetArrayLength() > 0)
                        {
                            results = tvResults[0];
                        }
                        else if (root.TryGetProperty("movie_results", out var movieResults) && movieResults.GetArrayLength() > 0)
                        {
                            results = movieResults[0];
                        }

                        if (results.ValueKind != JsonValueKind.Undefined)
                        {
                            var resultTmdbId = results.GetProperty("id").GetInt32().ToString();
                            mainData = await FetchTMDBAsync($"/{mediaType}/{resultTmdbId}", apiKey);
                            if (mainData != null)
                            {
                                return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                            }
                        }
                    }
                }

                // Fallback: Search by title
                if (!string.IsNullOrEmpty(title))
                {
                    var searchParams = new Dictionary<string, string> { { "query", title } };
                    if (!string.IsNullOrEmpty(year))
                    {
                        searchParams[itemType == "series" ? "first_air_date_year" : "year"] = year;
                    }

                    var searchResult = await FetchTMDBAsync($"/search/{mediaType}", apiKey, searchParams);
                    if (searchResult != null)
                    {
                        var root = searchResult.RootElement;
                        if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                        {
                            var firstResult = results[0];
                            var resultTmdbId = firstResult.GetProperty("id").GetInt32().ToString();
                            mainData = await FetchTMDBAsync($"/{mediaType}/{resultTmdbId}", apiKey);
                            if (mainData != null)
                            {
                                return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                            }
                        }
                    }

                    // Try alternate type if primary search failed
                    var altMediaType = itemType == "series" ? "movie" : "tv";
                    var altSearchParams = new Dictionary<string, string> { { "query", title } };
                    if (!string.IsNullOrEmpty(year))
                    {
                        altSearchParams[altMediaType == "tv" ? "first_air_date_year" : "year"] = year;
                    }

                    var altSearchResult = await FetchTMDBAsync($"/search/{altMediaType}", apiKey, altSearchParams);
                    if (altSearchResult != null)
                    {
                        var root = altSearchResult.RootElement;
                        if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                        {
                            var firstResult = results[0];
                            var resultTmdbId = firstResult.GetProperty("id").GetInt32().ToString();
                            mainData = await FetchTMDBAsync($"/{altMediaType}/{resultTmdbId}", apiKey);
                            if (mainData != null)
                            {
                                return await BuildCompleteResponse(mainData, altMediaType, apiKey, includeCredits, includeReviews);
                            }
                        }
                    }
                }

                return NotFound(new { error = "No metadata found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataController] Error getting TMDB metadata");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Check if item is in library and/or requested (replaces library-status.js logic)
        /// </summary>
        [HttpGet("library-status")]
        public ActionResult CheckLibraryStatus(
            [FromQuery] string? imdbId,
            [FromQuery] string? tmdbId,
            [FromQuery] string itemType)
        {
            // Check inputs and proceed
            try
            {
                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId))
                {
                    return BadRequest(new { error = "Either imdbId or tmdbId is required" });
                }

                // Check if in library by querying all items and checking provider IDs
                // This is faster than JS fetching all 5000 items to the client!
                var inLibrary = false;
                
                try
                {
                    // Query items without type filter first, then filter manually
                    var allItems = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true
                    });

                    inLibrary = allItems.Where(item =>
                    {
                        // Filter by type
                        var itemTypeName = item.GetType().Name;
                        if (itemType == "series" && itemTypeName != "Series") return false;
                        if (itemType == "movie" && itemTypeName != "Movie") return false;
                        
                        var providerIds = item.ProviderIds;
                        if (providerIds == null) return false;
                        
                        if (imdbId != null && providerIds.TryGetValue("Imdb", out var itemImdb) && itemImdb == imdbId)
                            return true;
                        if (tmdbId != null && providerIds.TryGetValue("Tmdb", out var itemTmdb) && itemTmdb == tmdbId)
                            return true;
                            
                        return false;
                    }).Any();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[MetadataController] Error querying library items");
                    // Fallback - just check requests
                }

                // Check if requested
                var config = Plugin.Instance?.Configuration;
                var requests = config?.Requests ?? new List<MediaRequest>();
                var existingRequest = requests.FirstOrDefault(r =>
                    r.ItemType == itemType &&
                    ((imdbId != null && r.ImdbId == imdbId) || (tmdbId != null && r.TmdbId == tmdbId))
                );

                // Look up the actual username from userId if request exists
                string actualUsername = null;
                if (existingRequest != null && !string.IsNullOrEmpty(existingRequest.UserId))
                {
                    try
                    {
                        var userId = Guid.Parse(existingRequest.UserId);
                        var user = _userManager.GetUserById(userId);
                        actualUsername = user?.Username ?? existingRequest.Username;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MetadataController] Could not look up username for userId {UserId}", existingRequest.UserId);
                        actualUsername = existingRequest.Username;
                    }
                }

                return Ok(new
                {
                    inLibrary,
                    existingRequest = existingRequest != null ? new
                    {
                        id = existingRequest.Id,
                        status = existingRequest.Status,
                        username = actualUsername ?? existingRequest.Username,
                        title = existingRequest.Title
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataController] Error checking library status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get external IDs for a TMDB item
        /// </summary>
        [HttpGet("external-ids")]
        public async Task<ActionResult> GetExternalIds(
            [FromQuery] string tmdbId,
            [FromQuery] string mediaType)
        {

            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    return BadRequest(new { error = "TMDB API key not configured" });
                }

                var result = await FetchTMDBAsync($"/{mediaType}/{tmdbId}/external_ids", apiKey);
                if (result != null)
                {
                    return Content(result.RootElement.GetRawText(), "application/json");
                }

                return NotFound(new { error = "External IDs not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataController] Error getting external IDs");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get media streams (audio/subtitle tracks) for an item
        /// This proxies Jellyfin's PlaybackInfo endpoint with optimizations
        /// </summary>
        [HttpGet("streams")]
        public async Task<ActionResult> GetMediaStreams(
            [FromQuery] string itemId,
            [FromQuery] string? mediaSourceId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return BadRequest(new { error = "itemId is required" });
            }

            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest(new { error = "Invalid itemId format" });
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                return NotFound(new { error = "Item not found" });
            }

            // Get media sources with probing and path substitution enabled so remote/streaming sources expose MediaStreams
            var mediaSourceResult = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, true, CancellationToken.None);
            var mediaSources = mediaSourceResult.ToList();

            if (mediaSources.Count == 0)
            {
                return NotFound(new { error = "No media sources found" });
            }

            MediaBrowser.Model.Dto.MediaSourceInfo? targetSource = null;
            if (!string.IsNullOrEmpty(mediaSourceId)) targetSource = mediaSources.FirstOrDefault(ms => ms.Id == mediaSourceId);
            else targetSource = mediaSources.FirstOrDefault();

            if (targetSource == null)
            {
                return NotFound(new { error = "Media source not found" });
            }

            // (No retry; initial call uses probing + path substitution.)

            // Use DTO lists so we can merge ffprobe results without anonymous-type mismatches
            var audioDtos = (targetSource.MediaStreams ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio)
                .Select(s => new AudioStreamDto
                {
                    Index = s.Index,
                    Title = BuildStreamTitle(s),
                    Language = s.Language,
                    Codec = s.Codec,
                    Channels = s.Channels,
                    Bitrate = s.BitRate.HasValue ? (long?)s.BitRate.Value : null
                })
                .ToList();

            var subtitleDtos = (targetSource.MediaStreams ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
                .Select(s => new SubtitleStreamDto
                {
                    Index = s.Index,
                    Title = BuildStreamTitle(s),
                    Language = s.Language,
                    Codec = s.Codec,
                    IsForced = s.IsForced,
                    IsDefault = s.IsDefault
                })
                .ToList();
            // If Jellyfin didn't populate any streams, try a lightweight ffprobe fallback for HTTP(S) sources.
            if ((audioDtos.Count == 0 && subtitleDtos.Count == 0) && !string.IsNullOrEmpty(targetSource.Path) && (targetSource.Path.StartsWith("http://") || targetSource.Path.StartsWith("https://")))
            {
                try
                {
                    var probe = await RunFfprobeAsync(targetSource.Path);
                    if (probe != null)
                    {
                        audioDtos.AddRange(probe.Audio.Select(a => new AudioStreamDto
                        {
                            Index = a.Index,
                            Title = a.Title ?? ($"Audio {a.Index}"),
                            Language = a.Language,
                            Codec = a.Codec,
                            Channels = a.Channels,
                            Bitrate = a.Bitrate
                        }));

                        subtitleDtos.AddRange(probe.Subtitles.Select(s => new SubtitleStreamDto
                        {
                            Index = s.Index,
                            Title = s.Title ?? ($"Subtitle {s.Index}"),
                            Language = s.Language,
                            Codec = s.Codec,
                            IsForced = s.IsForced,
                            IsDefault = s.IsDefault
                        }));
                    }
                }
                catch
                {
                    // swallow - fallback shouldn't break endpoint
                }
            }

            return Ok(new
            {
                audio = audioDtos.Select(a => new
                {
                    index = a.Index,
                    title = a.Title,
                    language = a.Language,
                    codec = a.Codec,
                    channels = a.Channels,
                    bitrate = a.Bitrate.HasValue ? (int?)(a.Bitrate.Value > int.MaxValue ? int.MaxValue : (int)a.Bitrate.Value) : null
                }),
                subs = subtitleDtos.Select(s => new
                {
                    index = s.Index,
                    title = s.Title,
                    language = s.Language,
                    codec = s.Codec,
                    isForced = s.IsForced,
                    isDefault = s.IsDefault
                }),
                mediaSourceId = targetSource.Id
            });
        }

        private string BuildStreamTitle(MediaBrowser.Model.Entities.MediaStream stream)
        {
            var title = stream.DisplayTitle ?? stream.Title ?? $"{stream.Type} {stream.Index}";
            
            if (!string.IsNullOrEmpty(stream.Language))
            {
                title += $" ({stream.Language})";
            }
            
            if (!string.IsNullOrEmpty(stream.Codec))
            {
                title += $" [{stream.Codec.ToUpperInvariant()}]";
            }
            
            return title;
        }

        #region Private Helpers

        private async Task<ActionResult> BuildCompleteResponse(
            JsonDocument mainData,
            string mediaType,
            string apiKey,
            bool includeCredits,
            bool includeReviews)
        {
            var root = mainData.RootElement;
            var tmdbId = root.GetProperty("id").GetInt32().ToString();

            var tasks = new List<Task<JsonDocument?>>();
            
            if (includeCredits)
            {
                tasks.Add(FetchTMDBAsync($"/{mediaType}/{tmdbId}/credits", apiKey));
            }
            if (includeReviews)
            {
                tasks.Add(FetchTMDBAsync($"/{mediaType}/{tmdbId}/reviews", apiKey));
            }

            var results = await Task.WhenAll(tasks);

            var response = new
            {
                main = JsonSerializer.Deserialize<object>(root.GetRawText()),
                credits = includeCredits && results[0] != null ? JsonSerializer.Deserialize<object>(results[0].RootElement.GetRawText()) : null,
                reviews = includeReviews && results[tasks.Count > 1 ? 1 : 0] != null ? JsonSerializer.Deserialize<object>(results[tasks.Count > 1 ? 1 : 0]!.RootElement.GetRawText()) : null
            };

            return Ok(response);
        }

        private async Task<JsonDocument?> FetchTMDBAsync(string endpoint, string apiKey, Dictionary<string, string>? queryParams = null)
        {
            try
            {
                // Build cache key
                var cacheKey = $"{endpoint}?{string.Join("&", queryParams?.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>())}";
                
                // Check cache
                lock (_cache)
                {
                    if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
                    {
                        _logger.LogDebug("[MetadataController] Cache hit: {Key}", cacheKey);
                        return JsonDocument.Parse(cached.Data);
                    }
                }

                var builder = new StringBuilder();
                builder.Append(TMDB_BASE).Append(endpoint);
                builder.Append("?api_key=").Append(Uri.EscapeDataString(apiKey));

                if (queryParams != null)
                {
                    foreach (var param in queryParams)
                    {
                        builder.Append('&').Append(Uri.EscapeDataString(param.Key))
                               .Append('=').Append(Uri.EscapeDataString(param.Value));
                    }
                }

                var url = builder.ToString();
                _logger.LogDebug("[MetadataController] Fetching: {Url}", url.Replace(apiKey, "***"));

                using var http = new HttpClient();
                var response = await http.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[MetadataController] TMDB error {Status}: {Content}", response.StatusCode, content);
                    return null;
                }

                // Cache the result
                lock (_cache)
                {
                    _cache[cacheKey] = (DateTime.UtcNow.Add(CACHE_DURATION), content);
                    
                    // Simple cache cleanup (remove expired entries)
                    var expiredKeys = _cache.Where(kv => kv.Value.Expiry <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
                    foreach (var key in expiredKeys)
                    {
                        _cache.Remove(key);
                    }
                }

                return JsonDocument.Parse(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataController] Error fetching from TMDB: {Endpoint}", endpoint);
                return null;
            }
        }

        

        #endregion

        // Minimal ffprobe helpers (used as a last-resort fallback)
        private class AudioStreamDto
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public int? Channels { get; set; }
            public long? Bitrate { get; set; }
        }

        private class SubtitleStreamDto
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public bool? IsForced { get; set; }
            public bool? IsDefault { get; set; }
        }
        private class FfprobeAudio
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public int? Channels { get; set; }
            public long? Bitrate { get; set; }
        }

        private class FfprobeSubtitle
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public bool IsForced { get; set; }
            public bool IsDefault { get; set; }
        }

        private class FfprobeResult
        {
            public List<FfprobeAudio> Audio { get; set; } = new();
            public List<FfprobeSubtitle> Subtitles { get; set; } = new();
        }

        private async Task<FfprobeResult?> RunFfprobeAsync(string url)
        {
            // Prefer Jellyfin-bundled ffprobe if present
            var candidates = new[] { "/usr/lib/jellyfin-ffmpeg/ffprobe", "/usr/bin/ffprobe", "ffprobe" };
            string? ffprobePath = null;
            foreach (var c in candidates)
            {
                try { if (System.IO.File.Exists(c)) { ffprobePath = c; break; } } catch { }
            }
            if (ffprobePath == null) ffprobePath = "ffprobe";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            var exited = proc.WaitForExit(8000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return null;
            }

            var outJson = await proc.StandardOutput.ReadToEndAsync();
            if (string.IsNullOrEmpty(outJson)) return null;

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
                return res;
            }
            catch
            {
                return null;
            }
        }
    }
}
