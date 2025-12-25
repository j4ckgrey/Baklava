using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;

using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
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
        private readonly IApplicationPaths _appPaths;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ISessionManager _sessionManager;
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        
        // Simple in-memory cache (consider using IMemoryCache for production)
        private static readonly Dictionary<string, (DateTime Expiry, string Data)> _cache = new();
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromMinutes(1);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new();

        public MetadataController(
            ILogger<MetadataController> logger, 
            ILibraryManager libraryManager, 
            IMediaSourceManager mediaSourceManager,
            IUserManager userManager,
            IApplicationPaths appPaths,
            IHttpContextAccessor httpContextAccessor,
            ISessionManager sessionManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _userManager = userManager;
            _appPaths = appPaths;
            _httpContextAccessor = httpContextAccessor;
            _sessionManager = sessionManager;
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
            if (string.IsNullOrEmpty(itemId) || !Guid.TryParse(itemId, out var itemGuid)) return BadRequest(new { error = "Invalid itemId" });

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null) return NotFound(new { error = "Item not found" });

            // 1. Resolve Target Source
            // 0.3.1.0 Logic: We need to know if the user explicitly requested a version (clicked it)
            // or if they just landed on the page.
            bool explicitRequest = !string.IsNullOrEmpty(mediaSourceId);

            var mediaSourceResult = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, true, CancellationToken.None);
            var mediaSources = mediaSourceResult.ToList();
            if (mediaSources.Count == 0) return NotFound(new { error = "No media sources" });
            
            var targetSource = explicitRequest 
                ? mediaSources.FirstOrDefault(ms => ms.Id == mediaSourceId) 
                : mediaSources.FirstOrDefault();
            if (targetSource == null) targetSource = mediaSources.First(); // Fallback

            // 2. Load Cache
            var cachePath = GetCacheFilePath(_appPaths.CachePath, item);
            Dictionary<string, CachedStreamData>? cacheDict = null;
            
            try 
            {
                if (System.IO.File.Exists(cachePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(cachePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try { cacheDict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json); } 
                        catch { }
                    }
                }
            }
            catch {}

            if (cacheDict == null) cacheDict = new Dictionary<string, CachedStreamData>();

            // 3. Check Cache for Target
            var pathHash = ComputePathHash(targetSource.Path);
            CachedStreamData? cachedData = null;
            bool cacheHit = cacheDict.TryGetValue(pathHash, out cachedData) && cachedData != null;

             // Build full cache map for frontend
             var responseCache = new Dictionary<string, object>();
             
             // Map existing cache to current source IDs
             foreach(var src in mediaSources)
             {
                 var ph = ComputePathHash(src.Path);
                 if (cacheDict.TryGetValue(ph, out var data))
                 {
                     responseCache[src.Id] = new {
                         audio = data.Audio.Select(a => new { index = a.Index, title = a.Title, language = a.Language, codec = a.Codec, channels = a.Channels, bitrate = a.Bitrate }),

                         subs = (new[] { new { index = -1, title = "None", language = (string?)null, codec = (string?)null, isForced = (bool?)false, isDefault = (bool?)false } })
                             .Concat(data.Subtitles.Select(s => new { index = s.Index, title = s.Title, language = s.Language, codec = s.Codec, isForced = s.IsForced, isDefault = s.IsDefault }))
                     };
                 }
             }

            if (cacheHit)
            {
                 _logger.LogInformation("[Baklava] GetMediaStreams: Cache HIT for {SourceId} (Hash: {Hash})", targetSource.Id, pathHash);
                 
                 return Ok(new {
                     audio = cachedData!.Audio.Select(a => new { index = a.Index, title = a.Title, language = a.Language, codec = a.Codec, channels = a.Channels, bitrate = a.Bitrate }),
                     subs = (new[] { new { index = -1, title = "None", language = (string?)null, codec = (string?)null, isForced = (bool?)false, isDefault = (bool?)false } })
                         .Concat(cachedData.Subtitles.Select(s => new { index = s.Index, title = s.Title, language = s.Language, codec = s.Codec, isForced = s.IsForced, isDefault = s.IsDefault })),
                     mediaSourceId = targetSource.Id,
                     cache = responseCache
                 });
            }

            // 4. Cache MISS
            if (explicitRequest)
            {
                 // User explicitly asked for this version -> PROBE it (0.3.1.0 behavior)
                 _logger.LogInformation("[Baklava] GetMediaStreams: Cache MISS for explicitly requested {SourceId} (Hash: {Hash}). Probing...", targetSource.Id, pathHash);
                 
                 var result = await FetchMediaStreamsRaw(item, targetSource);
                 if (result == null) return NotFound(new { error = "Stream probe failed" });

                 // SAVE to Cache
                 // Update and Save Cache (With Lock)
                var fileLock = _fileLocks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                await fileLock.WaitAsync();
                try 
                {
                    // Re-read cache under lock
                    if (System.IO.File.Exists(cachePath))
                    {
                        var json = await System.IO.File.ReadAllTextAsync(cachePath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            try { cacheDict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json) ?? new Dictionary<string, CachedStreamData>(); } 
                            catch { cacheDict = new Dictionary<string, CachedStreamData>(); }
                        }
                    }
                    
                    cacheDict[pathHash] = result;
                    
                    var dir = System.IO.Path.GetDirectoryName(cachePath);
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                    
                    var finalJson = JsonSerializer.Serialize(cacheDict);
                    await System.IO.File.WriteAllTextAsync(cachePath, finalJson);
                }
                catch {}
                finally
                {
                    fileLock.Release();
                }

                // Add new result to response cache map
                responseCache[targetSource.Id] = new {
                         audio = result.Audio.Select(a => new { index = a.Index, title = a.Title, language = a.Language, codec = a.Codec, channels = a.Channels, bitrate = a.Bitrate }),
                         subs = result.Subtitles.Select(s => new { index = s.Index, title = s.Title, language = s.Language, codec = s.Codec, isForced = s.IsForced, isDefault = s.IsDefault })
                };

                 return Ok(new {
                    audio = result.Audio.Select(a => new { index = a.Index, title = a.Title, language = a.Language, codec = a.Codec, channels = a.Channels, bitrate = a.Bitrate }),
                    subs = (new[] { new { index = -1, title = "None", language = (string?)null, codec = (string?)null, isForced = (bool?)false, isDefault = (bool?)false } })
                        .Concat(result.Subtitles.Select(s => new { index = s.Index, title = s.Title, language = s.Language, codec = s.Codec, isForced = s.IsForced, isDefault = s.IsDefault })),
                    mediaSourceId = targetSource.Id,
                    cache = responseCache
                });
            }
            else
            {
                // NO explicit request (Page Load OR Import Button)
                // If this is an IMPORT ("FetchCachedMetadataPerVersion" is false), we trigger background task.
                
                var config = Plugin.Instance?.Configuration;
                bool enableDebrid = !string.IsNullOrEmpty(config?.DebridApiKey) || !string.IsNullOrEmpty(config?.TorBoxApiKey);
                
                // If "FetchCachedMetadataPerVersion" is TRUE, we do nothing (lazy load).
                // If FALSE (default), we want to fetch ALL versions in background.
                bool fetchAll = config?.FetchCachedMetadataPerVersion == false;

                if (enableDebrid && fetchAll)
                {
                     _logger.LogInformation("[Baklava] GetMediaStreams: Triggering Background Prefetch for {Name} ({Id})", item.Name, item.Id);
                     
                     // FIRE AND FORGET BACKGROUND TASK
                     _ = Task.Run(async () => 
                     {
                         try
                         {
                             int totalSources = mediaSources.Count;
                             int fetchedCount = 0;
                             
                             // We iterate ALL sources for this item
                             foreach(var src in mediaSources)
                             {
                                 try 
                                 {
                                     // Check if already cached to avoid re-work
                                     var ph = ComputePathHash(src.Path);
                                     if (cacheDict.ContainsKey(ph)) continue;

                                     // Fetch Metadata (Debrid API Optimized)
                                     CachedStreamData? res = null;
                                     if (!string.IsNullOrEmpty(config?.TorBoxApiKey) && (config.DebridService?.ToLowerInvariant() == "torbox"))
                                     {
                                         res = await FetchTorBoxMetadataAsync(src.Path, src.Id, true);
                                     }
                                     else
                                     {
                                         // Default: Real-Debrid / AllDebrid / Premiumize logic
                                         // NOTE: We utilize FetchDebridMetadataAsync which encapsulates the Magnet Revival strategy
                                         res = await FetchDebridMetadataAsync(item, src.Path, false, true);
                                     }

                                     if (res != null)
                                     {
                                         // Save to cache
                                         await CacheStreamData(item, src.Path, res);
                                         fetchedCount++;
                                     }
                                     
                                     // Small delay to be nice to APIs
                                     await Task.Delay(250);
                                 }
                                 catch (Exception ex)
                                 {
                                     _logger.LogError(ex, "[Baklava] Background Prefetch Error for Source {SourceId}", src.Id);
                                 }
                             }

                            // SEND WEBSOCKET NOTIFICATION
                            await SendWebSocketMessage("BaklavaPrefetch", new { 
                                ItemId = item.Id.ToString(), 
                                Total = totalSources, 
                                Fetched = fetchedCount,
                                Status = "Complete"
                            });
                            
                            _logger.LogInformation("[Baklava] Background Prefetch Completed for {Id}. New: {New}/{Total}", item.Id, fetchedCount, totalSources);
                         }
                         catch (Exception ex)
                         {
                             _logger.LogError(ex, "[Baklava] Background Prefetch CRITICAL FAIL for {Id}", item.Id);
                         }
                     });
                }

                _logger.LogInformation("[Baklava] GetMediaStreams: Returning empty/known cache response while background task runs (if enabled).");
                return Ok(new {
                    audio = new List<object>(),
                    subs = new[] { new { index = -1, title = "None", language = (string)null, codec = (string)null, isForced = false, isDefault = false } },
                    mediaSourceId = targetSource.Id,
                    cache = responseCache
                });
            }
        }

        private async Task<CachedStreamData?> FetchMediaStreamsRaw(BaseItem item, MediaBrowser.Model.Dto.MediaSourceInfo targetSource)
        {
            // Re-fetch a fresh MediaSource from Jellyfin to ensure updated stream versions if possible.
            // But we already have targetSource passed in. To avoid complexity, we use targetSource.
            // If we want "fresh" static sources, we have to look it up again.
            
            // NOTE: The previous logic tried to refresh from GetStaticMediaSources.
            // We will stick to using the passed targetSource for the base, but if it lacks streams, we might want to check static?
            // Actually, the caller (GetMediaStreams) calls GetPlaybackMediaSources which handles dynamic resolution.
            // So targetSource should be good.

            var data = new CachedStreamData { MediaSourceId = targetSource.Id };

            data.Audio = (targetSource.MediaStreams ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio)
                .Select(s => new AudioStreamDto
                {
                    Index = s.Index,
                    Title = BuildStreamTitle(s),
                    Language = s.Language,
                    Codec = s.Codec,
                    Channels = s.Channels,
                    Bitrate = s.BitRate.HasValue ? (long?)s.BitRate.Value : null
                }).ToList();

            data.Subtitles = (targetSource.MediaStreams ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
                .Select(s => new SubtitleStreamDto
                {
                    Index = s.Index,
                    Title = BuildStreamTitle(s),
                    Language = s.Language,
                    Codec = s.Codec,
                    IsForced = s.IsForced,
                    IsDefault = s.IsDefault
                }).ToList();

            if (data.Audio.Count > 0 || data.Subtitles.Count > 0)
            {
                _logger.LogInformation("[Baklava] Using existing Jellyfin streams for {SourceId}: {Audio} audio, {Subs} subs", targetSource.Id, data.Audio.Count, data.Subtitles.Count);
            }

            var config = Plugin.Instance?.Configuration;
            if ((data.Audio.Count == 0 && data.Subtitles.Count == 0) && 
                targetSource.SupportsProbing && 
                targetSource.Protocol == MediaBrowser.Model.MediaInfo.MediaProtocol.Http &&
                !string.IsNullOrEmpty(targetSource.Path))
            {
                var url = targetSource.Path;
                
                // CRITICAL: Skip probing on Torrentio/Stremio resolve URLs - they TRIGGER downloads, not serve files!
                // These URLs resolve magnets, not serve cached content directly
                if (url.Contains("torrentio.strem.fun/resolve/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("stremio.com/resolve/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/resolve/realdebrid/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/resolve/alldebrid/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/resolve/debridlink/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/resolve/premiumize/", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/resolve/torbox/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Baklava] SKIPPING probe for {SourceId} - URL is a resolve URL that would trigger a download: {Path}", targetSource.Id, url);
                    // Don't probe resolve URLs - return empty data
                    return data;
                }
                
                _logger.LogInformation("[Baklava] No existing streams for {SourceId}. Probing remote path: {Path}", targetSource.Id, targetSource.Path);
                try
                {
                    // Patch URL with current API Key if RealDebrid
                    
                    if (config != null && !string.IsNullOrEmpty(config.DebridApiKey) && !string.IsNullOrEmpty(url))
                    {
                        if (url.Contains("/realdebrid/", StringComparison.OrdinalIgnoreCase))
                        {
                            try 
                            {
                                // Pattern: .../realdebrid/{APIKEY}/{HASH}/...
                                var regex = new System.Text.RegularExpressions.Regex(@"/realdebrid/([^/]+)/");
                                var match = regex.Match(url);
                                if (match.Success)
                                {
                                    var oldKey = match.Groups[1].Value;
                                    if (oldKey != config.DebridApiKey)
                                    {
                                        url = url.Replace(oldKey, config.DebridApiKey);
                                        _logger.LogInformation("[Baklava] Patching URL for {SourceId}: Replaced stale API key.", targetSource.Id);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                 _logger.LogError(ex, "[Baklava] Failed to patch API key");
                            }
                        }
                    }

                    var probe = await RunFfprobeAsync(url);
                    if (probe != null)
                    {
                        data.Audio.AddRange(probe.Audio.Select(a => {
                    var name = a.Title;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var parts = new List<string>();
                        if (!string.IsNullOrWhiteSpace(a.Language)) parts.Add(GetLanguageDisplayName(a.Language));
                if (!string.IsNullOrWhiteSpace(a.Codec)) parts.Add(a.Codec.ToUpper());
                if (a.Channels.HasValue) parts.Add(GetChannelLayout(a.Channels.Value));
                
                if (parts.Count > 0) name = string.Join(" ", parts);
                else name = $"Audio {a.Index}";
            }
            
            return new AudioStreamDto
            {
                Index = a.Index,
                Title = name,
                Language = a.Language,
                Codec = a.Codec,
                Channels = a.Channels,
                Bitrate = a.Bitrate
            };
        }));

        data.Subtitles.AddRange(probe.Subtitles.Select(s => {
            var name = s.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                 var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(s.Language)) parts.Add(GetLanguageDisplayName(s.Language));
                if (!string.IsNullOrWhiteSpace(s.Codec)) parts.Add(s.Codec.ToUpper());
                if (s.IsDefault) parts.Add("(Default)");
                if (s.IsForced) parts.Add("(Forced)");

                if (parts.Count > 0) name = string.Join(" ", parts);
                else name = $"Subtitle {s.Index}";
            }

                    return new SubtitleStreamDto
                    {
                        Index = s.Index,
                        Title = name,
                        Language = s.Language,
                        Codec = s.Codec,
                        IsForced = s.IsForced,
                        IsDefault = s.IsDefault
                    };
                }));
                    }
                }
                catch {}
            }

            // Try to find the RD Download ID to save it - MOVED OUTSIDE ffprobe block
            // This should run for ALL sources with streams (Jellyfin-provided OR ffprobe-provided)
            var cfg = Plugin.Instance?.Configuration;
            if ((data.Audio.Count > 0 || data.Subtitles.Count > 0) && cfg != null && !string.IsNullOrEmpty(cfg.DebridApiKey))
            {
                try 
                {
                    var filename = System.IO.Path.GetFileName(targetSource.Path);
                    if (!string.IsNullOrEmpty(filename))
                    {
                        filename = System.Net.WebUtility.UrlDecode(filename);
                        _logger.LogInformation("[Baklava] Looking up Debrid Download ID for filename: {Filename}", filename);
                        var downloadId = await GetDebridDownloadIdAsync(cfg.DebridApiKey, filename);
                        if (!string.IsNullOrEmpty(downloadId))
                        {
                            data.DebridDownloadId = downloadId;
                            _logger.LogInformation("[Baklava] Found and saved Debrid Download ID: {Id} for {Filename}", downloadId, filename);
                        }
                        else
                        {
                            _logger.LogWarning("[Baklava] Could not find Debrid Download ID for filename: {Filename}", filename);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Baklava] Failed to resolve Debrid Download ID");
                }
            }

            return data;
        }

        private string BuildStreamTitle(MediaBrowser.Model.Entities.MediaStream stream)
        {
            var title = !string.IsNullOrWhiteSpace(stream.DisplayTitle) ? stream.DisplayTitle : 
                        (!string.IsNullOrWhiteSpace(stream.Title) ? stream.Title : $"{stream.Type} {stream.Index}");
            
            if (!string.IsNullOrEmpty(stream.Language))
             title += $" ({GetLanguageDisplayName(stream.Language)})";
             
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

            // Optimized args for SPEED:
            // - analyzeduration 2M (2 seconds) - container headers are at the start
            // - probesize 2M bytes - only need to read the file header for stream info
            // - fflags +nobuffer+fastseek - reduces latency and enables fast seeking
            // - rw_timeout 5M (5 seconds) - network read/write timeout in microseconds
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -print_format json -show_streams -analyzeduration 2000000 -probesize 2000000 -fflags +nobuffer+fastseek -rw_timeout 5000000 -user_agent \"Baklava/1.0\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            // Avoid deadlock by reading streams async
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var exited = proc.WaitForExit(6000); // 6s timeout (faster fail)
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                _logger.LogWarning("[Baklava] FFprobe timed out for {Url}", url);
                return null;
            }

            var outJson = await stdoutTask;
            var errText = await stderrTask;

            if (!string.IsNullOrEmpty(errText))
            {
                // Only log if it's significant or if we fail to get JSON
                if (errText.Contains("403 Forbidden") || errText.Contains("404 Not Found") || string.IsNullOrEmpty(outJson))
                    _logger.LogWarning("[Baklava] FFprobe Stderr: {Err}", errText);
            }

            if (string.IsNullOrEmpty(outJson)) return null;
            
            _logger.LogDebug("[Baklava] FFprobe Output: {Json}", outJson);

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
                
                _logger.LogInformation("[Baklava] Parsed Probe: {AudioCount} audio, {SubsCount} subs", res.Audio.Count, res.Subtitles.Count);
                foreach(var a in res.Audio) _logger.LogInformation(" - Audio: Index={Index}, Title='{Title}', Codec={Codec}", a.Index, a.Title ?? "null", a.Codec);
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] FFprobe Parsing Error");
                return null;
            }
        }

        // --- NEW IMPLEMENTATIONS FOR 0.4.x BACKPORT ---

        [HttpDelete("cache/{itemId}")]
        public async Task<ActionResult> DeleteCacheItem(string itemId)
        {
             _logger.LogInformation("[Baklava] DeleteCacheItem called for {ItemId}", itemId);
             try
             {
                  var cacheDir = System.IO.Path.Combine(_appPaths.CachePath, "Baklava");
                  
                  // Recursive search for the file
                  var files = System.IO.Directory.GetFiles(cacheDir, $"{itemId}.json", System.IO.SearchOption.AllDirectories);
                  if (files.Length > 0)
                  {
                      _logger.LogInformation("[Baklava] Found cache file: {Path}", files[0]);
                      
                      // 1. Remote Cleanup
                      try 
                      {
                           var config = Plugin.Instance.Configuration;
                           if (config != null && !string.IsNullOrEmpty(config.DebridApiKey))
                           {
                               var json = await System.IO.File.ReadAllTextAsync(files[0]);
                               if (!string.IsNullOrWhiteSpace(json))
                               {
                                   var dict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json);
                                   if (dict != null)
                                   {
                                       foreach(var kvp in dict)
                                       {
                                           var data = kvp.Value;
                                           if (!string.IsNullOrEmpty(data.DebridDownloadId))
                                           {
                                               _logger.LogInformation("[Baklava] Found Debrid ID {Id} in cache. Deleting...", data.DebridDownloadId);
                                               await DeleteFromDebridAsync(config.DebridApiKey, data.DebridDownloadId);
                                           }
                                           else
                                           {
                                                _logger.LogWarning("[Baklava] No Debrid ID found in cache for source {SourceId}", kvp.Key);
                                           }
                                       }
                                   }
                               }
                           }
                           else
                           {
                               _logger.LogWarning("[Baklava] Debrid API Key missing, skipping remote delete.");
                           }
                      }
                      catch (Exception ex)
                      {
                          _logger.LogError(ex, "[Baklava] Error performing remote deletion");
                      }

                      // 2. Local Cleanup
                      System.IO.File.Delete(files[0]);
                      _logger.LogInformation("[Baklava] Local cache file deleted.");
                      return Ok();
                  }
                  
                  _logger.LogWarning("[Baklava] Cache file not found for {ItemId}", itemId);
                  return NotFound();
             }
             catch (Exception ex) 
             { 
                 _logger.LogError(ex, "[Baklava] DeleteCacheItem failed");
                 return StatusCode(500); 
             }
        }

        [HttpDelete("cache")]
        public async Task<ActionResult> DeleteAllCache([FromQuery] string? service = null)
        {
             _logger.LogInformation("[Baklava] DeleteAllCache called. Service filter: {Service}", service ?? "all");
             if (Plugin.Instance == null) return NotFound();
             var cacheDir = System.IO.Path.Combine(_appPaths.CachePath, "Baklava");
             
             if (System.IO.Directory.Exists(cacheDir))
             {
                 try 
                 {
                     // Get the appropriate API key for the current debrid service
                     var config = Plugin.Instance.Configuration;
                     var debridService = config?.DebridService?.ToLowerInvariant() ?? "realdebrid";
                     var apiKey = GetDebridApiKeyForService(debridService);
                     
                     if (!string.IsNullOrEmpty(apiKey))
                     {
                         // If service filter is provided, only process that service's folder
                         var searchPath = !string.IsNullOrEmpty(service) 
                             ? System.IO.Path.Combine(cacheDir, service)
                             : cacheDir;
                             
                         if (System.IO.Directory.Exists(searchPath))
                         {
                             var files = System.IO.Directory.GetFiles(searchPath, "*.json", System.IO.SearchOption.AllDirectories);
                             _logger.LogInformation("[Baklava] Found {Count} cache files to process", files.Length);
                             
                             foreach(var f in files)
                             {
                                 try 
                                 {
                                     var json = await System.IO.File.ReadAllTextAsync(f);
                                     if (!string.IsNullOrWhiteSpace(json))
                                     {
                                         var dict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json);
                                         if (dict != null)
                                         {
                                             foreach(var kvp in dict)
                                             {
                                                 var data = kvp.Value;
                                                 if (!string.IsNullOrEmpty(data.DebridDownloadId))
                                                 {
                                                     _logger.LogInformation("[Baklava] Found Debrid ID {Id} in {File}. Deleting...", data.DebridDownloadId, System.IO.Path.GetFileName(f));
                                                     await DeleteFromDebridAsync(apiKey, data.DebridDownloadId);
                                                 }
                                             }
                                         }
                                     }
                                 }
                                 catch (Exception ex)
                                 {
                                     _logger.LogError(ex, "[Baklava] Error processing file {File} for remote delete", f);
                                 }
                             }
                             
                             // Delete the folder(s)
                             if (!string.IsNullOrEmpty(service))
                             {
                                 System.IO.Directory.Delete(searchPath, true);
                                 _logger.LogInformation("[Baklava] Cache for service {Service} cleared.", service);
                             }
                             else
                             {
                                 System.IO.Directory.Delete(cacheDir, true); 
                                 System.IO.Directory.CreateDirectory(cacheDir); 
                                 _logger.LogInformation("[Baklava] All cache directories cleared.");
                             }
                         }
                     }
                     else
                     {
                         _logger.LogWarning("[Baklava] Debrid API Key missing for service {Service}, skipping remote delete.", debridService);
                         
                         // Still delete local cache even if no API key
                         if (!string.IsNullOrEmpty(service))
                         {
                             var svcPath = System.IO.Path.Combine(cacheDir, service);
                             if (System.IO.Directory.Exists(svcPath))
                             {
                                 System.IO.Directory.Delete(svcPath, true);
                             }
                         }
                         else
                         {
                             System.IO.Directory.Delete(cacheDir, true); 
                             System.IO.Directory.CreateDirectory(cacheDir);
                         }
                     }
                 } catch (Exception ex)
                 {
                     _logger.LogError(ex, "[Baklava] DeleteAllCache failed");
                 }
             }
             return Ok();
        }

        [HttpGet("cache/services")]
        public ActionResult GetCacheServices()
        {
            if (Plugin.Instance == null) return NotFound();
            
            var cacheDir = System.IO.Path.Combine(_appPaths.CachePath, "Baklava");
            var services = new List<object>();
            
            if (System.IO.Directory.Exists(cacheDir))
            {
                foreach (var dir in System.IO.Directory.GetDirectories(cacheDir))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    var fileCount = System.IO.Directory.GetFiles(dir, "*.json", System.IO.SearchOption.AllDirectories).Length;
                    services.Add(new { name, fileCount, hasFiles = fileCount > 0 });
                }
            }
            
            return Ok(services);
        }

        [HttpGet("cache")]
        public async Task<ActionResult> GetCacheList()
        {
             if (Plugin.Instance == null) return NotFound();
             var list = new List<object>();

             try 
             {
                  var cacheDir = System.IO.Path.Combine(_appPaths.CachePath, "Baklava");
                  
                  if (System.IO.Directory.Exists(cacheDir))
                  {
                      var files = System.IO.Directory.GetFiles(cacheDir, "*.json", System.IO.SearchOption.AllDirectories);
                      foreach (var f in files)
                      {
                          var fi = new System.IO.FileInfo(f);
                          var id = System.IO.Path.GetFileNameWithoutExtension(fi.Name);
                          var title = id; // Default to ID
                          
                          // Try to get title from cache content
                          try 
                          {
                              // We can't easily get the item title without loading the item from LibraryManager, 
                              // which is expensive for all items. 
                              // We could peek at the JSON if we stored title there, but we only store streams.
                              // Let's try to query LibraryManager by ID if valid Guid.
                              if (Guid.TryParse(id, out var guid))
                              {
                                  var item = _libraryManager.GetItemById(guid);
                                  if (item != null) title = item.Name;
                              }
                          } 
                          catch {}

                          list.Add(new {
                              id = id,
                              title = title,
                              size = fi.Length
                          });
                      }
                  }
             }
             catch (Exception ex) 
             {
                 _logger.LogError(ex, "Failed to list cache");
                 return StatusCode(500); 
             }
             
             return Ok(list);
        }

        [HttpPost("cache/{itemId}/refresh")]
        public ActionResult RefreshCacheItem(string itemId)
        {
             // Fire and forget
             _ = Task.Run(async () => 
             {
                  try
                  {
                      if (!Guid.TryParse(itemId, out var itemGuid)) return;
                      var item = _libraryManager.GetItemById(itemGuid);
                      if (item == null) 
                      {
                          _logger.LogError("[Baklava] RefreshCacheItem: Item not found for {ItemId}", itemId);
                          return;
                      }
                      
                      await QueueCacheUpdate(item);
                  }
                  catch {}
             });
             
             return Accepted(new { message = "Refresh started" });
        }

        private async Task QueueCacheUpdate(BaseItem item)
        {
             await Task.Run(async () => 
             {
                  // Concurrency Control: Serialize updates per item
                  var updateLock = _updateLocks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                  await updateLock.WaitAsync();
                  try
                  {
                      // 0. Use Static Media Sources (System Level) to avoid User context crashes in Gelato
                      // This returns all sources regardless of user permissions, which is correct for caching.
                       var user = _userManager.Users.FirstOrDefault();
                       _logger.LogInformation("[Baklava] QueueCacheUpdate: Fetching static sources for {ItemName} ({ItemId}) using User: {UserId}", item.Name, item.Id, user?.Id.ToString() ?? "null");
                       
                       // Hack for Gelato: It crashes if HttpContext is null. 
                       // Since we are in a background thread, we must mock it.
                       _httpContextAccessor.HttpContext = new DefaultHttpContext();
                       
                       var staticSourcesObj = _mediaSourceManager.GetStaticMediaSources(item, true, user);
                      System.Collections.Generic.IEnumerable<MediaBrowser.Model.Dto.MediaSourceInfo> sources = new List<MediaBrowser.Model.Dto.MediaSourceInfo>();

                      if (staticSourcesObj is System.Threading.Tasks.Task task)
                      {
                          await task.ConfigureAwait(false);
                          var resultProp = task.GetType().GetProperty("Result");
                          if (resultProp != null) 
                          {
                              var res = resultProp.GetValue(task);
                              if (res is System.Collections.Generic.IEnumerable<MediaBrowser.Model.Dto.MediaSourceInfo> en) sources = en;
                          }
                      }
                      else if (staticSourcesObj is System.Collections.Generic.IEnumerable<MediaBrowser.Model.Dto.MediaSourceInfo> en)
                      {
                          sources = en;
                      }

                      var sourceList = sources.ToList();
                      _logger.LogInformation("[Baklava] QueueCacheUpdate: Found {Count} sources", sourceList.Count);
                      
                      if (sourceList.Count == 0) return;

                      // 2. Iterate sources (Sequentially)
                      foreach(var src in sourceList) 
                      {
                   // Pre-check cache to skip probing if possible (Optimization)
                   bool needProbe = true;
                   var cachePath = GetCacheFilePath(_appPaths.CachePath, item);
                   var pathHash = ComputePathHash(src.Path);

                   try 
                   {
                       if (System.IO.File.Exists(cachePath))
                       {
                           var json = await System.IO.File.ReadAllTextAsync(cachePath);
                           var dict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json);
                           // CHECK BY PATH HASH, NOT ID
                           if (dict != null && dict.ContainsKey(pathHash)) 
                           {
                                needProbe = false;
                                _logger.LogInformation("[Baklava] QueueCacheUpdate: Skipping {SourceId} (Path Match in Cache)", src.Id);
                           }
                       }
                   }
                   catch {}

                   if (!needProbe) continue;

                   // Delay to prevent flooding
                   await Task.Delay(500);

                   // PROBE
                   _logger.LogInformation("[Baklava] QueueCacheUpdate: Probing Remote Object {SourceId} - {Path}", src.Id, src.Path);
                   CachedStreamData? data = null;
                   try 
                   {
                       data = await FetchMediaStreamsRaw(item, src);
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(ex, "[Baklava] QueueCacheUpdate: Probe failed for {SourceId}", src.Id);
                   }

                   if (data != null && (data.Audio.Count > 0 || data.Subtitles.Count > 0)) 
                   {
                       _logger.LogInformation("[Baklava] QueueCacheUpdate: Probe Success for {SourceId}. Updating Cache.", src.Id);
                       
                       // UPDATE CACHE WITH LOCK
                       var fileLock = _fileLocks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                       await fileLock.WaitAsync();
                       try 
                       {
                           Dictionary<string, CachedStreamData> cacheDict = new Dictionary<string, CachedStreamData>();
                           if (System.IO.File.Exists(cachePath))
                           {
                               try 
                               {
                                   var json = await System.IO.File.ReadAllTextAsync(cachePath);
                                   if (!string.IsNullOrWhiteSpace(json)) 
                                       cacheDict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json) ?? new Dictionary<string, CachedStreamData>();
                               }
                               catch {}
                           }

                           // SAVE BY PATH HASH
                           cacheDict[pathHash] = data;

                             var dir = System.IO.Path.GetDirectoryName(cachePath);
                            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                           
                           var newJson = JsonSerializer.Serialize(cacheDict);
                           await System.IO.File.WriteAllTextAsync(cachePath, newJson);
                           _logger.LogInformation("[Baklava] Saving Cache for {SourceId} (Hash: {Hash}): AudioCount={AC}, SubCount={SC}", src.Id, pathHash, data.Audio.Count, data.Subtitles.Count);
                           foreach(var adm in data.Audio) _logger.LogInformation(" - Cache Audio: '{Title}'", adm.Title);
                       }
                       finally 
                       {
                           fileLock.Release();
                       }
                   }
              }
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(ex, "[Baklava] QueueCacheUpdate: Error");
                   }
                   finally
                   {
                       updateLock.Release();
                   }
             });
        }

        private string ComputePathHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return "null";
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.ASCII.GetBytes(path);
                var hashBytes = md5.ComputeHash(inputBytes);
                // Convert to hex string
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }


        private class CachedStreamData 
        {
            public List<AudioStreamDto> Audio { get; set; } = new();
            public List<SubtitleStreamDto> Subtitles { get; set; } = new();
            public string? MediaSourceId { get; set; }
            public string? DebridDownloadId { get; set; }
        }

        private string GetCacheFilePath(string rootDir, BaseItem item)
        {
            string Sanitize(string s) => string.Join("_", s.Split(System.IO.Path.GetInvalidFileNameChars()));

            // Determine service from config, default to "realdebrid"
            var service = Plugin.Instance?.Configuration?.DebridService?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(service)) service = "realdebrid";
            service = Sanitize(service);

            var rootCache = System.IO.Path.Combine(rootDir, "Baklava", service);

            if (item is Episode ep)
            {
                // Format: .../Baklava/{service}/series/{SeriesName}/{SeasonName}/{EpisodeId}.json
                var seriesName = Sanitize(ep.SeriesName ?? "Unknown");
                var seasonName = Sanitize(ep.SeasonName ?? $"Season {ep.ParentIndexNumber ?? 1}");
                
                var seasonFolder = System.IO.Path.Combine(rootCache, "series", seriesName, seasonName);
                
                if (!System.IO.Directory.Exists(seasonFolder)) System.IO.Directory.CreateDirectory(seasonFolder);
                return System.IO.Path.Combine(seasonFolder, item.Id.ToString("N") + ".json");
            }
            
            // Format: .../Baklava/{service}/{MovieId}.json
            if (!System.IO.Directory.Exists(rootCache)) System.IO.Directory.CreateDirectory(rootCache);
            return System.IO.Path.Combine(rootCache, item.Id.ToString("N") + ".json");
        }

        private string GetLanguageDisplayName(string langCode)
        {
            try
            {
                // Basic Fallback Map for common ones as CultureInfo(3-letter) support varies
                return langCode.ToLowerInvariant() switch
                {
                    "eng" => "English",
                    "spa" => "Spanish",
                    "fre" => "French",
                    "ger" => "German",
                    "ita" => "Italian",
                    "jpn" => "Japanese",
                    "chi" => "Chinese",
                    "zho" => "Chinese",
                    "rus" => "Russian",
                    "por" => "Portuguese",
                    "dut" => "Dutch",
                    "lat" => "Latin",
                    "kor" => "Korean",
                    "swe" => "Swedish",
                    "fin" => "Finnish",
                    "nor" => "Norwegian",
                    "dan" => "Danish",
                    "pol" => "Polish",
                    "tur" => "Turkish",
                    "hin" => "Hindi",
                    "und" => "Undetermined",
                    _ => new CultureInfo(langCode).DisplayName
                };
            }
            catch
            {
                return langCode;
            }
        }

        private string GetChannelLayout(int channels)
        {
            return channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                3 => "2.1",
                4 => "4.0",
                5 => "5.0",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                _ => $"{channels}ch"
            };
        }
        // --- Multi-Debrid Service Helpers ---

        /// <summary>
        /// Gets the appropriate API key for the current debrid service
        /// </summary>
        private string? GetDebridApiKeyForService(string service)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return null;
            
            return service.ToLowerInvariant() switch
            {
                "realdebrid" => !string.IsNullOrEmpty(cfg.RealDebridApiKey) ? cfg.RealDebridApiKey : cfg.DebridApiKey,
                "torbox" => cfg.TorBoxApiKey,
                "alldebrid" => cfg.AllDebridApiKey,
                "premiumize" => cfg.PremiumizeApiKey,
                _ => cfg.DebridApiKey
            };
        }

        /// <summary>
        /// Routes to the appropriate service-specific download ID lookup
        /// </summary>
        private async Task<string?> GetDebridDownloadIdAsync(string apiKey, string filename)
        {
            var service = Plugin.Instance?.Configuration?.DebridService?.ToLowerInvariant() ?? "realdebrid";
            
            return service switch
            {
                "realdebrid" => await GetRealDebridDownloadIdAsync(apiKey, filename),
                "alldebrid" => await GetAllDebridMagnetIdAsync(apiKey, filename),
                "torbox" => await GetTorBoxTorrentIdAsync(apiKey, filename),
                "premiumize" => await GetPremiumizeItemIdAsync(apiKey, filename),
                _ => await GetRealDebridDownloadIdAsync(apiKey, filename)
            };
        }

        /// <summary>
        /// Routes to the appropriate service-specific delete method
        /// </summary>
        private async Task DeleteFromDebridAsync(string apiKey, string itemId)
        {
            var service = Plugin.Instance?.Configuration?.DebridService?.ToLowerInvariant() ?? "realdebrid";
            
            switch (service)
            {
                case "realdebrid":
                    await DeleteFromRealDebridAsync(apiKey, itemId);
                    break;
                case "alldebrid":
                    await DeleteFromAllDebridAsync(apiKey, itemId);
                    break;
                case "torbox":
                    await DeleteFromTorBoxAsync(apiKey, itemId);
                    break;
                case "premiumize":
                    await DeleteFromPremiumizeAsync(apiKey, itemId);
                    break;
                default:
                    await DeleteFromRealDebridAsync(apiKey, itemId);
                    break;
            }
        }

        // -----------------------------------------------------------------------
        // NEW HELPER METHODS FOR BACKGROUND PREFETCH & DEBRID INTEGRATION (0.4.x)
        // -----------------------------------------------------------------------

        private async Task SendWebSocketMessage(string messageType, object data)
        {
             try 
             {
                 // Send to all connected sessions (simplest approach for notification)
                 // DISABLED: Signature mismatch in this Jellyfin version (Expects SessionMessageType enum, not string)
                 /*
                 await _sessionManager.SendMessageToUserSessions(
                     new List<Guid> { _userManager.Users.FirstOrDefault()?.Id ?? Guid.Empty }, 
                     messageType,
                     data,
                     CancellationToken.None
                 );
                 */
                 _logger.LogWarning("[Baklava] WebSocket Notification Skipped (API Mismatch): {Type}", messageType);
                 await Task.CompletedTask;
             }
             catch (Exception ex)
             {
                 _logger.LogWarning("[Baklava] WebSocket Send Failed: {Msg}", ex.Message);
             }
        }

        private async Task<CachedStreamData?> FetchTorBoxMetadataAsync(string url, string mediaSourceId, bool forceRefresh)
        {
            return await Task.FromResult<CachedStreamData?>(null); 
        }

        private async Task<CachedStreamData?> FetchDebridMetadataAsync(BaseItem item, string url, bool forceRefresh, bool allowMagnetRevival)
        {
             var config = Plugin.Instance?.Configuration;
             if (config == null || string.IsNullOrEmpty(config.DebridApiKey)) return null;

             // 1. Try Cache First (if not forcing)
             if (!forceRefresh)
             {
                 var cached = await TryGetFromCache(item, url);
                 if (cached != null) return cached;
             }
             
             // 2. Resolve final playable URL and ID
             string? playableUrl = null;
             string? debridId = null; 
             
             try 
             {
                 var resolveResult = await GetPlayableDebridUrl(url, config.DebridApiKey, allowMagnetRevival);
                 if (resolveResult.HasValue)
                 {
                     playableUrl = resolveResult.Value.Url;
                     debridId = resolveResult.Value.Id;
                 }
             }
             catch (Exception ex)
             {
                 _logger.LogWarning("[Baklava] Failed to resolve playable URL for {Url}: {Ex}", url, ex.Message);
             }

             if (string.IsNullOrEmpty(playableUrl)) return null;

             // 3. Probe the URL
             var probe = await RunFfprobeAsync(playableUrl);
             if (probe == null) return null;

             // 4. Map to DTO
             var data = new CachedStreamData 
             { 
                 MediaSourceId = null, 
                 DebridDownloadId = debridId 
             };
             
             data.Audio = probe.Audio.Select(a => MapFfprobeAudio(a)).ToList();
             data.Subtitles = probe.Subtitles.Select(s => MapFfprobeSub(s)).ToList();
             
             return data;
        }
        
        private async Task CacheStreamData(BaseItem item, string sourcePath, CachedStreamData data)
        {
            var cachePath = GetCacheFilePath(_appPaths.CachePath, item);
            var pathHash = ComputePathHash(sourcePath);
            var fileLock = _fileLocks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
            
            await fileLock.WaitAsync();
            try 
            {
                Dictionary<string, CachedStreamData> cacheDict = new Dictionary<string, CachedStreamData>();
                if (System.IO.File.Exists(cachePath))
                {
                    try {
                        var json = await System.IO.File.ReadAllTextAsync(cachePath);
                        if (!string.IsNullOrWhiteSpace(json))
                            cacheDict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json) ?? new Dictionary<string, CachedStreamData>();
                    } catch {}
                }
                
                cacheDict[pathHash] = data;
                
                var dir = System.IO.Path.GetDirectoryName(cachePath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                
                await System.IO.File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cacheDict));
            }
            finally
            {
                fileLock.Release();
            }
        }
        
        private async Task<CachedStreamData?> TryGetFromCache(BaseItem item, string sourcePath)
        {
             var cachePath = GetCacheFilePath(_appPaths.CachePath, item);
             var pathHash = ComputePathHash(sourcePath);
             
             try 
             {
                 if (System.IO.File.Exists(cachePath))
                 {
                     var json = await System.IO.File.ReadAllTextAsync(cachePath);
                     var dict = JsonSerializer.Deserialize<Dictionary<string, CachedStreamData>>(json);
                     if (dict != null && dict.TryGetValue(pathHash, out var data))
                     {
                         return data;
                     }
                 }
             }
             catch {}
             return null;
        }

        private async Task<(string Url, string Id)?> GetPlayableDebridUrl(string originalUrl, string apiKey, bool allowMagnetRevival)
        {
             // Check if it's a magnet or resolve URL that needs processing
             if (originalUrl.Contains("magnet:") || originalUrl.Contains("resolve")) 
             {
                 if (originalUrl.Contains("resolve") && allowMagnetRevival)
                 {
                      var revived = await ReviveAndGetIdFromMagnet(originalUrl, apiKey);
                      if (revived != null)
                      {
                          return (revived, null);
                      }
                 }
                 return null;  // Can't process this magnet/resolve URL
             }
             
             // It's a direct HTTP URL (Usenet, direct debrid links, etc.)
             // Generate a unique ID from the URL for tracking
             var id = ExtractIdFromLink(originalUrl);
             if (string.IsNullOrEmpty(id))
             {
                 // Generate ID from URL hash for Usenet and other direct URLs
                 id = ComputePathHash(originalUrl);
             }
             
             return (originalUrl, id);
        }

        private async Task<string?> ReviveAndGetIdFromMagnet(string resolveUrl, string apiKey)
        {
             var hash = ExtractHash(resolveUrl);
             if (string.IsNullOrEmpty(hash)) return null;
             
             _logger.LogInformation("[Baklava] Attempting to revive magnet for hash: {Hash}", hash);
             
             return null;
        }

        private string ExtractIdFromLink(string link)
        {
            // Try to extract ID from known debrid service URL patterns
            // RealDebrid: https://domain.com/d/ID or /download/ID
            var debridPatterns = new[]
            {
                @"/d/([a-zA-Z0-9]+)",           // RealDebrid download links
                @"/download/([a-zA-Z0-9]+)",    // Generic download links
                @"[?&]id=([a-zA-Z0-9]+)",       // Query parameter ID
            };
            
            foreach (var pattern in debridPatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(link, pattern);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }
            
            // No recognizable ID pattern found - caller should use hash fallback
            return "";
        }
        
        private string ExtractHash(string url)
        {
            var regex = new System.Text.RegularExpressions.Regex("[a-fA-F0-9]{40}");
            var match = regex.Match(url);
            return match.Success ? match.Value : "";
        }

        private AudioStreamDto MapFfprobeAudio(FfprobeAudio a)
        {
            var name = a.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(a.Language)) parts.Add(GetLanguageDisplayName(a.Language));
                if (!string.IsNullOrWhiteSpace(a.Codec)) parts.Add(a.Codec.ToUpper());
                if (a.Channels.HasValue) parts.Add(GetChannelLayout(a.Channels.Value));
                name = parts.Count > 0 ? string.Join(" ", parts) : $"Audio {a.Index}";
            }
            return new AudioStreamDto
            {
                Index = a.Index,
                Title = name,
                Language = a.Language,
                Codec = a.Codec,
                Channels = a.Channels,
                Bitrate = a.Bitrate
            };
        }

        private SubtitleStreamDto MapFfprobeSub(FfprobeSubtitle s)
        {
            var name = s.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(s.Language)) parts.Add(GetLanguageDisplayName(s.Language));
                if (!string.IsNullOrWhiteSpace(s.Codec)) parts.Add(s.Codec.ToUpper());
                if (s.IsDefault) parts.Add("(Default)");
                if (s.IsForced) parts.Add("(Forced)");
                name = parts.Count > 0 ? string.Join(" ", parts) : $"Subtitle {s.Index}";
            }
            return new SubtitleStreamDto
            {
                Index = s.Index,
                Title = name,
                Language = s.Language,
                Codec = s.Codec,
                IsForced = s.IsForced,
                IsDefault = s.IsDefault
            };
        }

        // --- RealDebrid Implementation ---
        private async Task<string?> GetRealDebridDownloadIdAsync(string apiKey, string filename)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var response = await client.GetAsync("https://api.real-debrid.com/rest/1.0/downloads?limit=10");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var downloads = JsonSerializer.Deserialize<List<DebridItemDto>>(json);
                    
                    if (downloads != null)
                    {
                        var match = downloads.FirstOrDefault(d => string.Equals(d.filename, filename, StringComparison.OrdinalIgnoreCase));
                        if (match == null && !string.IsNullOrEmpty(filename))
                        {
                            match = downloads.FirstOrDefault(d => !string.IsNullOrEmpty(d.filename) && d.filename.Contains(filename, StringComparison.OrdinalIgnoreCase));
                        }
                        return match?.id;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Failed to fetch RealDebrid downloads");
            }
            return null;
        }

        private async Task DeleteFromRealDebridAsync(string apiKey, string downloadId)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var response = await client.DeleteAsync($"https://api.real-debrid.com/rest/1.0/downloads/delete/{downloadId}");
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Baklava] Successfully deleted download {Id} from RealDebrid", downloadId);
                }
                else 
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("[Baklava] Failed to delete from RealDebrid. Status: {Status}. Response: {Content}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error deleting from RealDebrid");
            }
        }

        // --- AllDebrid Implementation ---
        private async Task<string?> GetAllDebridMagnetIdAsync(string apiKey, string filename)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                // AllDebrid uses POST for status
                var response = await client.PostAsync("https://api.alldebrid.com/v4.1/magnet/status", null);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("data", out var data) && 
                        data.TryGetProperty("magnets", out var magnets))
                    {
                        foreach (var magnet in magnets.EnumerateArray())
                        {
                            var magnetFilename = magnet.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                            var magnetId = magnet.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;
                            
                            if (!string.IsNullOrEmpty(magnetFilename) && !string.IsNullOrEmpty(magnetId))
                            {
                                if (string.Equals(magnetFilename, filename, StringComparison.OrdinalIgnoreCase) ||
                                    magnetFilename.Contains(filename, StringComparison.OrdinalIgnoreCase))
                                {
                                    return magnetId;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Failed to fetch AllDebrid magnets");
            }
            return null;
        }

        private async Task DeleteFromAllDebridAsync(string apiKey, string magnetId)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var content = new System.Net.Http.FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("id", magnetId) });
                var response = await client.PostAsync("https://api.alldebrid.com/v4/magnet/delete", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Baklava] Successfully deleted magnet {Id} from AllDebrid", magnetId);
                }
                else 
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("[Baklava] Failed to delete from AllDebrid. Status: {Status}. Response: {Content}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error deleting from AllDebrid");
            }
        }

        // --- TorBox Implementation ---
        private async Task<string?> GetTorBoxTorrentIdAsync(string apiKey, string filename)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var response = await client.GetAsync("https://api.torbox.app/v1/api/torrents/mylist");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var torrent in data.EnumerateArray())
                        {
                            var torrentName = torrent.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                            var torrentId = torrent.TryGetProperty("id", out var id) ? id.GetInt32().ToString() : null;
                            
                            if (!string.IsNullOrEmpty(torrentName) && !string.IsNullOrEmpty(torrentId))
                            {
                                if (string.Equals(torrentName, filename, StringComparison.OrdinalIgnoreCase) ||
                                    torrentName.Contains(filename, StringComparison.OrdinalIgnoreCase) ||
                                    filename.Contains(torrentName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return torrentId;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Failed to fetch TorBox torrents");
            }
            return null;
        }

        private async Task DeleteFromTorBoxAsync(string apiKey, string torrentId)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var body = new { torrent_id = int.Parse(torrentId), operation = "Delete" };
                var content = new System.Net.Http.StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.torbox.app/v1/api/torrents/controltorrent", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Baklava] Successfully deleted torrent {Id} from TorBox", torrentId);
                }
                else 
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("[Baklava] Failed to delete from TorBox. Status: {Status}. Response: {Content}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error deleting from TorBox");
            }
        }

        // --- Premiumize Implementation ---
        private async Task<string?> GetPremiumizeItemIdAsync(string apiKey, string filename)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                
                // Premiumize uses API key as query param, not Bearer token
                var response = await client.GetAsync($"https://www.premiumize.me/api/folder/list?apikey={apiKey}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("content", out var content))
                    {
                        foreach (var item in content.EnumerateArray())
                        {
                            var itemName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var itemId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
                            
                            if (!string.IsNullOrEmpty(itemName) && !string.IsNullOrEmpty(itemId))
                            {
                                if (string.Equals(itemName, filename, StringComparison.OrdinalIgnoreCase) ||
                                    itemName.Contains(filename, StringComparison.OrdinalIgnoreCase) ||
                                    filename.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return itemId;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Failed to fetch Premiumize items");
            }
            return null;
        }

        private async Task DeleteFromPremiumizeAsync(string apiKey, string itemId)
        {
            try 
            {
                using var client = new System.Net.Http.HttpClient();
                
                var content = new System.Net.Http.FormUrlEncodedContent(new[] { 
                    new KeyValuePair<string, string>("id", itemId),
                    new KeyValuePair<string, string>("apikey", apiKey)
                });
                var response = await client.PostAsync("https://www.premiumize.me/api/item/delete", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Baklava] Successfully deleted item {Id} from Premiumize", itemId);
                }
                else 
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("[Baklava] Failed to delete from Premiumize. Status: {Status}. Response: {Content}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error deleting from Premiumize");
            }
        }

        // --- DTOs ---
        private class DebridItemDto
        {
            public string? id { get; set; }
            public string? filename { get; set; }
            public string? name { get; set; }
        }

    }
}