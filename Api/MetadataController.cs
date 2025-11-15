using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

#nullable enable
namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/metadata")]
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
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromMinutes(1);

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
            _logger.LogInformation("[MetadataController.GetTMDBMetadata] Called with: tmdbId={TmdbId}, imdbId={ImdbId}, itemType={ItemType}, title={Title}, year={Year}", 
                tmdbId ?? "null", imdbId ?? "null", itemType ?? "null", title ?? "null", year ?? "null");
            
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogError("[MetadataController.GetTMDBMetadata] TMDB API key not configured");
                    return BadRequest(new { error = "TMDB API key not configured" });
                }

                var mediaType = itemType == "series" ? "tv" : "movie";
                _logger.LogInformation("[MetadataController.GetTMDBMetadata] Using mediaType: {MediaType}", mediaType);
                
                JsonDocument? mainData = null;

                // Try TMDB ID first
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogInformation("[MetadataController.GetTMDBMetadata] Trying TMDB ID: {TmdbId}", tmdbId);
                    mainData = await FetchTMDBAsync($"/{mediaType}/{tmdbId}", apiKey);
                    if (mainData != null)
                    {
                        _logger.LogInformation("[MetadataController.GetTMDBMetadata] Found via TMDB ID");
                        return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                    }
                }

                // Try IMDB ID via find endpoint
                if (!string.IsNullOrEmpty(imdbId))
                {
                    _logger.LogInformation("[MetadataController.GetTMDBMetadata] Trying IMDB ID: {ImdbId}", imdbId);
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
                            _logger.LogInformation("[MetadataController.GetTMDBMetadata] Found in tv_results");
                            results = tvResults[0];
                        }
                        else if (root.TryGetProperty("movie_results", out var movieResults) && movieResults.GetArrayLength() > 0)
                        {
                            _logger.LogInformation("[MetadataController.GetTMDBMetadata] Found in movie_results");
                            results = movieResults[0];
                        }

                        if (results.ValueKind != JsonValueKind.Undefined)
                        {
                            var resultTmdbId = results.GetProperty("id").GetInt32().ToString();
                            _logger.LogInformation("[MetadataController.GetTMDBMetadata] Extracted TMDB ID from IMDB lookup: {ResultTmdbId}", resultTmdbId);
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
                    _logger.LogInformation("[MetadataController.GetTMDBMetadata] Fallback to title search: {Title}", title);
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
                            _logger.LogInformation("[MetadataController.GetTMDBMetadata] Found via title search, TMDB ID: {ResultTmdbId}", resultTmdbId);
                            mainData = await FetchTMDBAsync($"/{mediaType}/{resultTmdbId}", apiKey);
                            if (mainData != null)
                            {
                                return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                            }
                        }
                    }

                    // Try alternate type if primary search failed
                    _logger.LogInformation("[MetadataController.GetTMDBMetadata] Primary search failed, trying alternate type");
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
            [FromQuery] string itemType,
            [FromQuery] string? jellyfinId)
        {
            _logger.LogInformation("[MetadataController.CheckLibraryStatus] Called with: imdbId={ImdbId}, tmdbId={TmdbId}, itemType={ItemType}, jellyfinId={JellyfinId}",
                imdbId ?? "null", tmdbId ?? "null", itemType ?? "null", jellyfinId ?? "null");
            
            // Check inputs and proceed
            try
            {
                // Allow jellyfinId alone if provided
                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(jellyfinId))
                {
                    _logger.LogWarning("[MetadataController.CheckLibraryStatus] No IDs provided");
                    return BadRequest(new { error = "Either imdbId, tmdbId, or jellyfinId is required" });
                }

                // Check if in library by querying all items and checking provider IDs
                // This is faster than JS fetching all 5000 items to the client!
                var inLibrary = false;
                string? foundImdbId = imdbId;
                string? foundTmdbId = tmdbId;
                
                try
                {
                    // If a direct Jellyfin item id is provided, prefer that fast path
                    if (!string.IsNullOrEmpty(jellyfinId) && Guid.TryParse(jellyfinId, out var jfGuid))
                    {
                        var itemById = _libraryManager.GetItemById(jfGuid);
                        if (itemById != null)
                        {
                            // Ensure matching type if itemType provided
                            var itemTypeName = itemById.GetType().Name;
                            if ((itemType == "series" && itemTypeName == "Series") || (itemType == "movie" && itemTypeName == "Movie") || string.IsNullOrEmpty(itemType))
                            {
                                inLibrary = true;
                                
                                _logger.LogInformation("[MetadataController.CheckLibraryStatus] Found item in library by JellyfinId: {Id}, type: {Type}",
                                    jellyfinId, itemTypeName);
                                
                                // Extract provider IDs for request checking
                                if (itemById.ProviderIds != null)
                                {
                                    itemById.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                    itemById.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);
                                    
                                    _logger.LogInformation("[MetadataController.CheckLibraryStatus] Extracted provider IDs: imdb={Imdb}, tmdb={Tmdb}",
                                        foundImdbId ?? "null", foundTmdbId ?? "null");
                                }
                            }
                        }
                        else
                        {
                            _logger.LogInformation("[MetadataController.CheckLibraryStatus] JellyfinId {Id} not found in library - item may have been deleted, falling back to TMDB/IMDB check",
                                jellyfinId);
                        }
                    }

                    // If not found by JellyfinId (or no JellyfinId provided), search by TMDB/IMDB ID
                    if (!inLibrary && (!string.IsNullOrEmpty(imdbId) || !string.IsNullOrEmpty(tmdbId)))
                    {
                        _logger.LogInformation("[MetadataController.CheckLibraryStatus] Searching library by TMDB/IMDB ID: tmdb={Tmdb}, imdb={Imdb}",
                            tmdbId ?? "null", imdbId ?? "null");

                        // Build query with type filter to avoid deserialization errors with unknown types
                        var query = new InternalItemsQuery
                        {
                            Recursive = true
                        };

                        // Filter by type at the query level to prevent Jellyfin from trying to deserialize unknown types
                        if (itemType == "series")
                        {
                            query.IncludeItemTypes = new[] { BaseItemKind.Series };
                        }
                        else if (itemType == "movie")
                        {
                            query.IncludeItemTypes = new[] { BaseItemKind.Movie };
                        }

                        var allItems = _libraryManager.GetItemList(query);

                        var foundItem = allItems.FirstOrDefault(item =>
                        {
                            var providerIds = item.ProviderIds;
                            if (providerIds == null) return false;

                            if (imdbId != null && providerIds.TryGetValue("Imdb", out var itemImdb) && itemImdb == imdbId)
                                return true;
                            if (tmdbId != null && providerIds.TryGetValue("Tmdb", out var itemTmdb) && itemTmdb == tmdbId)
                                return true;

                            return false;
                        });

                        if (foundItem != null)
                        {
                            inLibrary = true;
                            _logger.LogInformation("[MetadataController.CheckLibraryStatus] Found item in library by TMDB/IMDB ID");

                            // Extract provider IDs from found item (may fill in missing IDs)
                            if (foundItem.ProviderIds != null)
                            {
                                if (string.IsNullOrEmpty(foundImdbId))
                                    foundItem.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                if (string.IsNullOrEmpty(foundTmdbId))
                                    foundItem.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);

                                _logger.LogInformation("[MetadataController.CheckLibraryStatus] Extracted provider IDs: imdb={Imdb}, tmdb={Tmdb}",
                                    foundImdbId ?? "null", foundTmdbId ?? "null");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[MetadataController] Error querying library items");
                    // Fallback - just check requests
                }

                                // Check if requested using the found IDs (use foundImdbId/foundTmdbId if we extracted them from Jellyfin item)
                var config = Plugin.Instance?.Configuration;
                var requests = config?.Requests ?? new List<MediaRequest>();
                
                _logger.LogInformation("[MetadataController.CheckLibraryStatus] Checking {Count} requests with foundImdbId={FoundImdb}, foundTmdbId={FoundTmdb}, jellyfinId={JfId}, inLibrary={InLib}",
                    requests.Count, foundImdbId ?? "null", foundTmdbId ?? "null", jellyfinId ?? "null", inLibrary);
                
                // Match by TMDB/IMDB ID first (more reliable), then by JellyfinId ONLY if item is still in library
                var existingRequest = requests.FirstOrDefault(r =>
                    r.ItemType == itemType &&
                    (
                        // Prefer matching by TMDB/IMDB IDs (these are stable even if item is deleted/re-added)
                        ((foundImdbId != null && !string.IsNullOrEmpty(r.ImdbId) && r.ImdbId == foundImdbId) || 
                         (foundTmdbId != null && !string.IsNullOrEmpty(r.TmdbId) && r.TmdbId == foundTmdbId)) ||
                        // Only match by JellyfinId if the item is currently in the library
                        // (prevents false matches when item was deleted but request still has old JellyfinId)
                        (inLibrary && !string.IsNullOrEmpty(jellyfinId) && !string.IsNullOrEmpty(r.JellyfinId) && r.JellyfinId == jellyfinId)
                    )
                );
                
                if (existingRequest != null)
                {
                    _logger.LogInformation("[MetadataController.CheckLibraryStatus] Found existing request: id={Id}, status={Status}, imdbId={Imdb}, tmdbId={Tmdb}",
                        existingRequest.Id, existingRequest.Status, existingRequest.ImdbId ?? "null", existingRequest.TmdbId ?? "null");
                }
                else
                {
                    _logger.LogInformation("[MetadataController.CheckLibraryStatus] No existing request found");
                }

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

                _logger.LogInformation("[MetadataController.CheckLibraryStatus] Returning: inLibrary={InLib}, hasRequest={HasReq}",
                    inLibrary, existingRequest != null);

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

            // Re-fetch a fresh MediaSource from Jellyfin to ensure updated stream versions
            try
            {
                // Call GetStaticMediaSources which may be synchronous or return a Task depending on Jellyfin build.
                var staticSourcesObj = _mediaSourceManager.GetStaticMediaSources(item, true);
                IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo>? freshInfo = null;

                if (staticSourcesObj is System.Threading.Tasks.Task task)
                {
                    // Await the task then try to read its Result property
                    await task.ConfigureAwait(false);
                    var resultProp = task.GetType().GetProperty("Result");
                    if (resultProp != null)
                    {
                        freshInfo = resultProp.GetValue(task) as IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo>;
                    }
                }
                else
                {
                    freshInfo = staticSourcesObj as IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo>;
                }

                if (freshInfo != null && freshInfo.Count > 0)
                {
                    var freshTarget = !string.IsNullOrEmpty(mediaSourceId)
                        ? freshInfo.FirstOrDefault(ms => ms.Id == mediaSourceId)
                        : freshInfo.FirstOrDefault();

                    if (freshTarget != null)
                        targetSource = freshTarget;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MetadataController] Could not refresh media source for item {ItemId}", item.Id);
            }

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
            // If Jellyfin didn't populate any streams, try a lightweight ffprobe fallback.
            // Use Jellyfin's built-in properties to determine if probing is safe:
            // - SupportsProbing: Jellyfin already knows if this source can be probed
            // - Protocol == Http: External HTTP sources (not File protocol which is local)
            // This respects Jellyfin's own logic and works with all plugins/sources correctly.
            if ((audioDtos.Count == 0 && subtitleDtos.Count == 0) && 
                targetSource.SupportsProbing && 
                targetSource.Protocol == MediaBrowser.Model.MediaInfo.MediaProtocol.Http &&
                !string.IsNullOrEmpty(targetSource.Path))
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

            // Build raw JSON strings for main, credits and reviews and return as a single JSON payload.
            // Returning as raw JSON avoids double-deserialization that produces JsonElement wrappers
            // with ValueKind fields when re-serialized by ASP.NET.
            var mainRaw = root.GetRawText();

            string creditsRaw = "null";
            string reviewsRaw = "null";

            if (includeCredits && results.Length > 0 && results[0] != null)
            {
                creditsRaw = results[0].RootElement.GetRawText();
            }

            if (includeReviews)
            {
                // If both credits and reviews were requested then reviews will be at index 1
                var reviewsIndex = tasks.Count > 1 ? 1 : 0;
                if (results.Length > reviewsIndex && results[reviewsIndex] != null)
                {
                    reviewsRaw = results[reviewsIndex].RootElement.GetRawText();
                }
            }

            var combined = $"{{\"main\":{mainRaw},\"credits\":{creditsRaw},\"reviews\":{reviewsRaw}}}";
            
            // Return raw JSON string directly to avoid JsonElement/ValueKind wrapper issues
            return Content(combined, "application/json");
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
