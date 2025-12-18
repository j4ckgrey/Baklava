using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using MediaBrowser.Common.Configuration;
using Baklava.Helpers;

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
        private readonly ISessionManager _sessionManager;
        private readonly IApplicationPaths _appPaths;

        // Concurrency Lock for Item Cache Files
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
        private SemaphoreSlim GetItemLock(string itemId) => _fileLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        
        // Cache for resolved Debrid IDs to avoid spamming the /downloads endpoint
        private static readonly ConcurrentDictionary<string, string> _idCache = new();
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        
        // Simple in-memory cache
        private static readonly Dictionary<string, (DateTime Expiry, string Data)> _cache = new();
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromMinutes(10);

        public MetadataController(
            ILogger<MetadataController> logger, 
            ILibraryManager libraryManager, 
            IMediaSourceManager mediaSourceManager,
            IUserManager userManager,
            ISessionManager sessionManager,
            IApplicationPaths appPaths) // Added
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _appPaths = appPaths;
        }

        #region TMDB Metadata

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
                    return BadRequest(new { error = "TMDB API key not configured" });

                var mediaType = (itemType == "series" || itemType == "tv") ? "tv" : "movie";
                JsonDocument? mainData = null;

                // 1. Try TMDB ID
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    mainData = await FetchTMDBAsync($"/{mediaType}/{tmdbId}", apiKey);
                }

                // 2. Try IMDB ID
                if (mainData == null && !string.IsNullOrEmpty(imdbId))
                {
                    var findResult = await FetchTMDBAsync($"/find/{imdbId}", apiKey, new Dictionary<string, string> { { "external_source", "imdb_id" } });
                    if (findResult != null)
                    {
                        var root = findResult.RootElement;
                        JsonElement results = default;
                        if (mediaType == "tv" && root.TryGetProperty("tv_results", out var tvResults) && tvResults.GetArrayLength() > 0)
                            results = tvResults[0];
                        else if (root.TryGetProperty("movie_results", out var movieResults) && movieResults.GetArrayLength() > 0)
                            results = movieResults[0];

                        if (results.ValueKind != JsonValueKind.Undefined)
                        {
                            var id = results.GetProperty("id").GetInt32().ToString();
                            mainData = await FetchTMDBAsync($"/{mediaType}/{id}", apiKey);
                        }
                    }
                }

                // 3. Fallback: Search by title
                if (mainData == null && !string.IsNullOrEmpty(title))
                {
                    mainData = await SearchTMDBAsync(title, year, mediaType, apiKey);
                    // Try alternate type if failed (movie vs tv)
                    if (mainData == null)
                    {
                        var altType = mediaType == "tv" ? "movie" : "tv";
                        mainData = await SearchTMDBAsync(title, year, altType, apiKey);
                        if (mainData != null) mediaType = altType;
                    }
                }

                if (mainData != null)
                {
                    return await BuildCompleteResponse(mainData, mediaType, apiKey, includeCredits, includeReviews);
                }

                return NotFound(new { error = "No metadata found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataController] Error getting TMDB metadata");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task<JsonDocument?> SearchTMDBAsync(string title, string? year, string mediaType, string apiKey)
        {
            var searchParams = new Dictionary<string, string> { { "query", title } };
            if (!string.IsNullOrEmpty(year))
                searchParams[mediaType == "tv" ? "first_air_date_year" : "year"] = year;

            var searchResult = await FetchTMDBAsync($"/search/{mediaType}", apiKey, searchParams);
            if (searchResult?.RootElement.TryGetProperty("results", out var results) == true && results.GetArrayLength() > 0)
            {
                var id = results[0].GetProperty("id").GetInt32().ToString();
                return await FetchTMDBAsync($"/{mediaType}/{id}", apiKey);
            }
            return null;
        }

        [HttpGet("external-ids")]
        public async Task<ActionResult> GetExternalIds([FromQuery] string tmdbId, [FromQuery] string mediaType)
        {
            var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
            if (string.IsNullOrEmpty(apiKey)) return BadRequest(new { error = "TMDB API key not configured" });

            var result = await FetchTMDBAsync($"/{mediaType}/{tmdbId}/external_ids", apiKey);
            return result != null ? Content(result.RootElement.GetRawText(), "application/json") : NotFound(new { error = "External IDs not found" });
        }

        #endregion

        #region Library Status

        [HttpGet("library-status")]
        public ActionResult CheckLibraryStatus(
            [FromQuery] string? imdbId, [FromQuery] string? tmdbId,
            [FromQuery] string itemType, [FromQuery] string? jellyfinId)
        {
            try
            {
                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(jellyfinId))
                    return BadRequest(new { error = "One ID is required" });

                bool inLibrary = false;
                
                // 1. Check by Jellyfin ID directly
                if (!string.IsNullOrEmpty(jellyfinId) && Guid.TryParse(jellyfinId, out var guid))
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item != null)
                    {
                        inLibrary = true;
                        imdbId ??= item.GetProviderId("Imdb");
                        tmdbId ??= item.GetProviderId("Tmdb");
                    }
                }

                // 2. Check by Provider IDs if not found yet
                if (!inLibrary && (!string.IsNullOrEmpty(imdbId) || !string.IsNullOrEmpty(tmdbId)))
                {
                    var query = new InternalItemsQuery { Recursive = true };
                    query.IncludeItemTypes = itemType switch 
                    { 
                        "series" or "tv" => new[] { BaseItemKind.Series },
                        "movie" => new[] { BaseItemKind.Movie },
                        _ => null 
                    };

                    var items = _libraryManager.GetItemList(query);
                    var found = items.FirstOrDefault(i => 
                        (imdbId != null && i.GetProviderId("Imdb") == imdbId) || 
                        (tmdbId != null && i.GetProviderId("Tmdb") == tmdbId));

                    if (found != null)
                    {
                        inLibrary = true;
                        jellyfinId = found.Id.ToString();
                        imdbId ??= found.GetProviderId("Imdb");
                        tmdbId ??= found.GetProviderId("Tmdb");
                    }
                }

                // 3. Check Requests
                var requests = Plugin.Instance?.Configuration?.Requests ?? new List<MediaRequest>();
                var request = requests.FirstOrDefault(r => 
                    r.ItemType == itemType && (
                        (imdbId != null && r.ImdbId == imdbId) ||
                        (tmdbId != null && r.TmdbId == tmdbId) ||
                        (inLibrary && jellyfinId != null && r.JellyfinId == jellyfinId)
                    ));

                string? username = null;
                if (request?.UserId != null && Guid.TryParse(request.UserId, out var uid))
                {
                    username = _userManager.GetUserById(uid)?.Username;
                }

                return Ok(new
                {
                    inLibrary,
                    jellyfinId,
                    existingRequest = request != null ? new { 
                        id = request.Id, 
                        status = request.Status, 
                        username = username ?? request.Username, 
                        title = request.Title 
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking library status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        #endregion

        #region Media Streams

        [HttpGet("streams")]
        public async Task<ActionResult> GetMediaStreams([FromQuery] string itemId, [FromQuery] string? mediaSourceId)
        {
            if (!Guid.TryParse(itemId, out var guid)) return BadRequest(new { error = "Invalid itemId" });
            
            var item = _libraryManager.GetItemById(guid);
            if (item == null) return NotFound(new { error = "Item not found" });

            // 1. Get Jellyfin Media Sources
            _logger.LogInformation("[Baklava] GetMediaStreams called for ItemId: {ItemId}", itemId);
            
            var sources = _mediaSourceManager.GetStaticMediaSources(item, false); 
            // Filter stub sources
            var validSources = sources.Where(ms => !ms.Path?.StartsWith("gelato://stub/") ?? true).ToList();

            var targetSource = (!string.IsNullOrEmpty(mediaSourceId) ? validSources.FirstOrDefault(s => s.Id == mediaSourceId) : validSources.FirstOrDefault()) 
                               ?? validSources.FirstOrDefault();

            // FALLBACK: If we only have invalid/stub sources, use the first one just to return valid JSON (empty streams)
            // instead of 404, which might break the UI for newly imported items.
            if (targetSource == null && sources.Count > 0)
            {
                 targetSource = sources.FirstOrDefault();
                 _logger.LogWarning("[Baklava] Only stub/invalid sources found for ItemId: {ItemId}. Using stub source to prevent 404.", itemId);
            }

            if (targetSource == null) 
            {
                _logger.LogWarning("[Baklava] No valid media sources found for ItemId: {ItemId}", itemId);
                return NotFound(new { error = "No media sources found" });
            }

            _logger.LogInformation("[Baklava] Using MediaSource: {SourceId} Path: {Path}", targetSource.Id, targetSource.Path);

            // 2. Extract Streams from Source
            var audioStreams = new List<StreamDto>();
            var subStreams = new List<StreamDto>();

            if (targetSource.MediaStreams != null)
            {
                audioStreams.AddRange(targetSource.MediaStreams
                    .Where(s => s.Type == MediaStreamType.Audio)
                    .Select(MapStream));
                    
                subStreams.AddRange(targetSource.MediaStreams
                    .Where(s => s.Type == MediaStreamType.Subtitle)
                    .Select(MapSubStream));
            }
            
            _logger.LogInformation("[Baklava] Initial Streams - Audio: {AudioCount}, Subs: {SubCount}", audioStreams.Count, subStreams.Count);

            // 3. Optional: Fetch Debrid Metadata (if enabled and no streams)
            var config = Plugin.Instance?.Configuration;
            bool isDebridEnabled = config?.EnableDebridMetadata ?? false;
            bool isProbingEnabled = config?.EnableFallbackProbe ?? false;
            bool isHttp = targetSource.Protocol == MediaProtocol.Http;
            bool hasPath = !string.IsNullOrEmpty(targetSource.Path);

            // Capture User ID for notification
            Guid userId = Guid.Empty;
            var claim = User?.FindFirst(ClaimTypes.NameIdentifier);
            if (claim != null) Guid.TryParse(claim.Value, out userId);

            // BATCH PREFETCH: Fire and forget metadata fetch for all other sources to populate cache
            if (isDebridEnabled && !config.FetchCachedMetadataPerVersion)
            {
                _ = Task.Run(async () => 
                {
                    var total = 0;
                    var cached = 0;
                    var newFetched = 0;
                    var failed = 0;

                    foreach (var s in validSources)
                    {
                        if (s.Id == targetSource.Id) continue; // Skip current
                        if (string.IsNullOrEmpty(s.Path)) continue;
                
                        total++;
                        // Only allow magnet revival (fetching non-cached) if explicitly enabled
                        var res = await FetchDebridMetadataAsync(item, s.Path, allowMagnetRevival: config.FetchAllNonCachedMetadata);
                        if (res.HasValue) 
                        {
                            if (res.Value.IsCached) cached++; else newFetched++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    if (total > 0) 
                    {
                        _logger.LogInformation("[Baklava] Background Prefetch Summary for {ItemId}: Processed {Total} sources. Cached: {Cached}. New: {New}. Failed/Unresolvable: {Failed}. User: {UserId}", itemId, total, cached, newFetched, failed, userId);
                        
                        // Notify User via WebSocket
                        /* Build fix: SendWebSocketMessage signature mismatch. Disabling temporarily to deploy critical fixes.
                        if (userId != Guid.Empty)
                        {
                            try 
                            {
                                var msgData = new 
                                { 
                                    ItemId = itemId,
                                    Total = total,
                                    Cached = cached,
                                    Fetched = newFetched,
                                    Failed = failed,
                                    Success = true
                                };
                                
                                // Manually iterate sessions and use SendWebSocketMessage which is likely supported
                                var sessions = _sessionManager.Sessions.Where(s => s.UserId.Equals(userId)).ToArray();
                                if (sessions.Length > 0)
                                {
                                    _sessionManager.SendWebSocketMessage("BaklavaPrefetch", () => msgData, sessions, CancellationToken.None);
                                }
                            }
                            catch (Exception ex) 
                            { 
                                _logger.LogWarning(ex, "[Baklava] Error sending BaklavaPrefetch WebSocket message to user {UserId}.", userId);
                            }
                        }
                        */
                    }
                });
            }

            // We fetch debrid metadata if enabled and valid path
            if (isDebridEnabled && isHttp && hasPath) 
            {
                 // Only fetch if we don't have streams OR if we specifically want to augment
                 // Current logic: if Jellyfin has streams, trust them. If not, try Debrid.
                 if (audioStreams.Count == 0) 
                 {
                     _logger.LogDebug("[Baklava] Fetching Debrid metadata for: {Path}", targetSource.Path);
                     // Selected version: Always allow revival (unless restricted logic applies, but usually selected = want it)
                     var debridData = await FetchDebridMetadataAsync(item, targetSource.Path!, allowMagnetRevival: true);
                     if (debridData.HasValue)
                     {
                         _logger.LogDebug("[Baklava] Debrid found Audio: {AC}, Subs: {SC}", debridData.Value.Audio.Count, debridData.Value.Subtitles.Count);
                         audioStreams.AddRange(debridData.Value.Audio);
                         subStreams.AddRange(debridData.Value.Subtitles);
                     }
                     else
                     {
                         _logger.LogDebug("[Baklava] Debrid metadata returned no results");
                     }
                 }
            }
            
            // 4. Fallback Probe (ONLY if explicitly enabled)
            // User explicitly requested to avoid this if possible
            if (audioStreams.Count == 0 && isProbingEnabled && isHttp && hasPath)
            {
                 _logger.LogDebug("[Baklava] Audio empty, probing fallback enabled. Running ffprobe...");
                 
                 // Check Cache for Probe Result first!
                 // effectively reusing the caching mechanism for probe results
                 var probeCache = await TryGetFromCache(item, targetSource.Path!);
                 if (probeCache != null)
                 {
                      _logger.LogDebug("[Baklava] Found cached probe data");
                      audioStreams.AddRange(probeCache.Value.Audio);
                      subStreams.AddRange(probeCache.Value.Subtitles);
                 }
                 else 
                 {
                     var probeData = await RunFfprobeAsync(targetSource.Path!);
                     if (probeData != null)
                     {
                          _logger.LogDebug("[Baklava] FFprobe found Audio: {AC}, Subs: {SC}", probeData.Audio.Count, probeData.Subtitles.Count);
                          var probeAudio = probeData.Audio.Select(MapFfprobeAudio).ToList();
                          var probeSubs = probeData.Subtitles.Select(MapFfprobeSub).ToList();
                          
                          audioStreams.AddRange(probeAudio);
                          subStreams.AddRange(probeSubs);
                          
                          // Cache the probe results!
                          _logger.LogDebug("[Baklava] Probe successful. Saving discovered metadata to cache to avoid future probing.");
                          await CacheStreamData(item, targetSource.Path!, probeAudio, probeSubs);
                     }
                     else
                     {
                          _logger.LogWarning("[Baklava] FFprobe failed or returned no streams");
                     }
                 }
            }

            // 5. Fetch External Subtitles from Stremio (Gelato)
            var configData = Plugin.Instance?.Configuration;
            if (configData?.EnableExternalSubtitles == true)
            {
                 var externalSubs = await FetchStremioSubtitlesAsync(item, userId);
                 if (externalSubs != null && externalSubs.Any())
                 {
                     _logger.LogInformation("[Baklava] Found {Count} external subtitles", externalSubs.Count);
                     subStreams.AddRange(externalSubs);
                 }
            }

            // Verify return structure matches client expectations (camelCase)
            return Ok(new 
            { 
                audio = audioStreams.Select(a => new {
                    index = a.Index,
                    title = a.Title,
                    language = a.Language,
                    codec = a.Codec,
                    channels = a.Channels,
                    isExternal = a.IsExternal
                }), 
                subs = subStreams.Select(s => new {
                    index = s.Index,
                    title = s.Title,
                    language = s.Language,
                    codec = s.Codec,
                    isForced = s.IsForced ?? false,
                    isDefault = s.IsDefault ?? false,
                    isExternal = s.IsExternal,
                    externalUrl = s.ExternalUrl
                }), 
                mediaSourceId = targetSource.Id 
            });
        }

        #endregion

        private async Task<List<StreamDto>?> FetchStremioSubtitlesAsync(BaseItem item, Guid userId)
        {
            try 
            {
                var baseUrl = SubtitlesHelper.GetGelatoStremioUrl(_appPaths, userId);
                if (string.IsNullOrEmpty(baseUrl)) return null;

                // Determine Type and ID
                string type = "movie";
                string id = "";

                var imdb = item.GetProviderId("Imdb");
                var tmdb = item.GetProviderId("Tmdb");

                if (string.IsNullOrEmpty(imdb) && string.IsNullOrEmpty(tmdb)) return null;

                id = !string.IsNullOrEmpty(imdb) ? imdb : $"tmdb:{tmdb}";

                if (item is Episode ep)
                {
                    type = "series";
                     // Format: tt123:1:1
                    if (!string.IsNullOrEmpty(imdb))
                    {
                        id = $"{imdb}:{ep.ParentIndexNumber ?? 1}:{ep.IndexNumber ?? 1}";
                    }
                    else
                    {
                         // TMDB for episodes? Stremio usually expects IMDB or specific format
                         // Fallback might be tricky without map, but let's try standard TMDB format if supported
                         // Or try to resolve show IMDB?
                         // If we have show IMDB use that.
                         var showImdb = ep.Series?.GetProviderId("Imdb");
                         if (!string.IsNullOrEmpty(showImdb))
                         {
                             id = $"{showImdb}:{ep.ParentIndexNumber ?? 1}:{ep.IndexNumber ?? 1}";
                         }
                         else
                         {
                             // Fallback to TMDB id logic if addon supports it (Cinemeta does)
                             // Warning: Might fail if addon demands IMDB
                         }
                    }
                }
                else if (item is Series)
                {
                    return null; // No subs for series container
                }

                var url = $"{baseUrl}/subtitles/{type}/{id}.json";
                _logger.LogDebug("[Baklava] Fetching external subtitles from: {Url}", url);

                using var client = new HttpClient();
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) 
                {
                    _logger.LogWarning("[Baklava] Stremio Subs Fetch Failed. Status: {Status} URL: {Url}", resp.StatusCode, url);
                    if ((int)resp.StatusCode >= 500)
                    {
                         try {
                              var content = await resp.Content.ReadAsStringAsync();
                              _logger.LogWarning("[Baklava] Response Content: {Content}", content);
                         } catch {}
                    }
                    return null;
                }

                using var stream = await resp.Content.ReadAsStreamAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = await JsonSerializer.DeserializeAsync<StremioSubtitleResponse>(stream, options);

                if (result?.Subtitles == null) return null;

                return result.Subtitles.Select((s, i) => {
                    string langName = s.Lang ?? "Unknown";
                    try { 
                        if (!string.IsNullOrEmpty(s.Lang)) 
                            langName = new System.Globalization.CultureInfo(s.Lang).DisplayName; 
                    } catch { }
                    
                    return new StreamDto
                    {
                        Index = -1000 - i,
                        Title = $"[EXTERNAL] {langName}" + (!string.IsNullOrEmpty(s.id) ? $" - {s.id}" : ""),
                        Language = s.Lang ?? "und",
                        Codec = "srt",
                        IsExternal = true,
                        ExternalUrl = s.Url
                    };
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Stremio subtitles");
                return null;
            }
        }

        private class StremioSubtitleResponse
        {
            public List<StremioSubtitle> Subtitles { get; set; } = new();
        }

        private class StremioSubtitle
        {
            public string? Url { get; set; }
            public string? Lang { get; set; }
            public string? id { get; set; }
        }

        #region Helpers & Data Fetching

        private StreamDto MapStream(MediaStream s) => new StreamDto 
        { 
            Index = s.Index, 
            Title = s.DisplayTitle ?? s.Title, 
            Language = s.Language, 
            Codec = s.Codec, 
            Channels = s.Channels, 
            Bitrate = s.BitRate 
        };

        private StreamDto MapSubStream(MediaStream s) => new StreamDto 
        { 
            Index = s.Index, 
            Title = s.DisplayTitle ?? s.Title, 
            Language = s.Language, 
            Codec = s.Codec, 
            IsForced = s.IsForced, 
            IsDefault = s.IsDefault 
        };



        #region Cache Management Endpoints

        [HttpGet("cache")]
        public async Task<ActionResult> GetCacheList()
        {
            if (Plugin.Instance == null) return NotFound();
             var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
             var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
             if (!System.IO.Directory.Exists(cacheDir)) return Ok(new List<object>());

                 var files = System.IO.Directory.GetFiles(cacheDir, "*.json", System.IO.SearchOption.AllDirectories);
                 var result = new List<object>();

                 foreach (var file in files)
                 {
                     var filename = System.IO.Path.GetFileNameWithoutExtension(file);
                     // Filename is ItemId
                     if (!Guid.TryParse(filename, out var guid)) continue;
                     
                     var item = _libraryManager.GetItemById(guid);
                     var title = item?.Name;
                     
                     // Fallback: Try to get title from folder structure
                     if (string.IsNullOrEmpty(title))
                     {
                         var parentDir = System.IO.Directory.GetParent(file);
                         if (parentDir != null)
                         {
                             // Expected: Movies/{Title} ({Year}) OR Shows/{Title}/Season {S}
                             // If Season folder, go up one more for Show name, but we want "Show - Season"
                             if (parentDir.Name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
                             {
                                 var showDir = parentDir.Parent;
                                 var showName = showDir?.Name ?? "Unknown Show";
                                 title = $"{showName} - {parentDir.Name}";
                             }
                             else
                             {
                                 // Movies folder or root
                                 title = parentDir.Name;
                                 if (title == "Baklava" || title == "Movies" || title == "Shows") 
                                 {
                                     // Root folder file: Try to read cached JSON for Title
                                     try 
                                     {
                                         var jsonContent = System.IO.File.ReadAllText(file);
                                         // Quick regex extraction to avoid full deserialize if possible, or just deserialize
                                         // Since we might need full robust check, deserializing head is fine.
                                         // But wait, the file is a Dict<string, CachedMetadata>. Title is inside values.
                                         using var doc = JsonDocument.Parse(jsonContent);
                                         foreach(var prop in doc.RootElement.EnumerateObject())
                                         {
                                              if (prop.Value.TryGetProperty("Title", out var t)) { title = t.GetString(); break; }
                                              if (prop.Value.TryGetProperty("title", out var t2)) { title = t2.GetString(); break; } // Case sensitivity check
                                         }
                                     } 
                                     catch {}
                                     
                                     if (title == "Baklava" || title == "Movies" || title == "Shows" || title == null) title = "Unknown Item";
                                 }
                             }
                         }
                     }
                     
                     if (item is Episode ep && !string.IsNullOrEmpty(ep.SeriesName))
                     {
                         title = $"{ep.SeriesName} - S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00} - {ep.Name}";
                     }
                     else if (title == null) title = "Unknown Item";
                     
                     var info = new System.IO.FileInfo(file);
                     result.Add(new { 
                         id = filename,
                         title = title,
                         size = info.Length,
                         lastModified = info.LastWriteTime
                     });
                 }

             return Ok(result.OrderBy(x => ((dynamic)x).title));
        }

        [HttpDelete("cache/{itemId}")]
        public async Task<ActionResult> DeleteCacheItem(string itemId)
        {
             if (Plugin.Instance == null) return NotFound();
             var sem = GetItemLock(itemId);
             await sem.WaitAsync();
             try 
             {
                  var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
                  var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
                  
                  // Recursive search for the file since it's now in subfolders
                  var files = System.IO.Directory.GetFiles(cacheDir, $"{itemId}.json", System.IO.SearchOption.AllDirectories);
                  if (files.Length > 0)
                  {
                      // 1. Debrid Cleanup: Delete torrents from RD
                      try 
                      {
                          _logger.LogInformation("[Baklava] Deleting cache for item: {Id}, Path: {Path}", itemId, files[0]);
                          var json = await System.IO.File.ReadAllTextAsync(files[0]);
                          var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                          var dict = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(json, options);
                          
                          if (dict == null)
                          {
                               _logger.LogWarning("[Baklava] Failed to deserialize cache file for deletion: {Id}", itemId);
                          }
                          else 
                          {
                              var torrentIds = dict.Values
                                  .Where(v => !string.IsNullOrEmpty(v.TorrentId))
                                  .Select(v => v.TorrentId!)
                                  .Distinct()
                                  .ToList();
                                  
                              _logger.LogInformation("[Baklava] Found {Count} torrent IDs to delete for item {Id}", torrentIds.Count, itemId);

                              var config = Plugin.Instance.Configuration;
                              if (config != null && !string.IsNullOrEmpty(config.DebridApiKey))
                              {
                                  using var client = new HttpClient();
                                  
                                  foreach(var torrentId in torrentIds)
                                  {
                                      await DeleteFromDebridAsync(client, config.DebridService, config.DebridApiKey, torrentId);
                                  }
                              }
                          }
                      }
                      catch (Exception ex)
                      {
                          _logger.LogError(ex, "[Baklava] Error during Debrid torrent cleanup");
                          // Continue to delete local file anyway
                      }

                      // 2. Local Cleanup
                      System.IO.File.Delete(files[0]);
                      // Also delete the parent directory if it's empty
                      var parentDir = System.IO.Path.GetDirectoryName(files[0]);
                      if (parentDir != null && System.IO.Directory.Exists(parentDir) && !System.IO.Directory.EnumerateFileSystemEntries(parentDir).Any())
                      {
                          System.IO.Directory.Delete(parentDir);
                      }
                      return Ok();
                  }
                  return NotFound();
             }
             finally { sem.Release(); }
        }

        [HttpDelete("cache")]
        public async Task<ActionResult> DeleteAllCache()
        {
             if (Plugin.Instance == null) return NotFound();
             var config = Plugin.Instance.Configuration;
             var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
             var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
             
             if (System.IO.Directory.Exists(cacheDir))
             {
                 var files = System.IO.Directory.GetFiles(cacheDir, "*.json", System.IO.SearchOption.AllDirectories);
                 
                 // 1. Collect all Torrent IDs
                 var torrentIdsToDelete = new HashSet<string>();
                 foreach (var f in files)
                 {
                     try 
                     {
                         // Basic read to extract ID (avoid full deserialization overhead if possible, but safety first)
                         var json = await System.IO.File.ReadAllTextAsync(f);
                         if (json.Contains("TorrentId")) // Optimization check
                         {
                             var dict = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(json);
                             if (dict != null)
                             {
                                 foreach(var v in dict.Values) 
                                     if (!string.IsNullOrEmpty(v.TorrentId)) torrentIdsToDelete.Add(v.TorrentId!);
                             }
                         }
                     }
                     catch {} // Ignore read errors, we just want to collect what we can
                 }

                 // 2. Delete from Debrid
                 if (torrentIdsToDelete.Count > 0 && !string.IsNullOrEmpty(config?.DebridApiKey))
                 {
                      using var client = new HttpClient();
                      client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.DebridApiKey);
                      // TODO: Parallelize? Real-Debrid might have rate limits. Sequential is safer.
                      foreach(var tid in torrentIdsToDelete)
                      {
                          await DeleteFromDebridAsync(client, config.DebridService, config.DebridApiKey, tid);
                      }
                 }

                 // 3. Local Cleanup (Delete files)
                 foreach(var f in files) 
                 {
                     try { System.IO.File.Delete(f); } catch {}
                 }
                 
                 // 4. Cleanup Empty Dirs (Recursive-ish: simply delete cacheDir content or delete recursive)
                 // Safest is to just delete the Baklava directory and recreate it
                 try 
                 {
                     System.IO.Directory.Delete(cacheDir, true);
                     System.IO.Directory.CreateDirectory(cacheDir);
                 }
                 catch (Exception ex)
                 {
                      _logger.LogError(ex, "[Baklava] Error cleaning up cache directory");
                      return StatusCode(500, "Error cleaning local cache");
                 }
             }
             
             return Ok();
        }

        [HttpPost("cache/{itemId}/refresh")]
        public async Task<ActionResult> RefreshCacheItem(string itemId)
        {
             if (Plugin.Instance == null) return NotFound();
             var sem = GetItemLock(itemId);
             await sem.WaitAsync();
             try 
             {
                  var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
                  var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
                  
                  var files = System.IO.Directory.GetFiles(cacheDir, $"{itemId}.json", System.IO.SearchOption.AllDirectories);
                  if (files.Length == 0) return NotFound();
                  var file = files[0];

                 var json = await System.IO.File.ReadAllTextAsync(file);
                 var dict = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(json);
                 if (dict == null) return BadRequest("Invalid cache file");

                 var urlsToRefresh = dict.Values.Select(v => v.OriginalUrl).Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
                 
                 // Close lock before re-fetching because FetchDebridMetadataAsync uses the lock too!
                 sem.Release(); 
                 
                 // Fire and forget refresh or wait? User expects visual feedback.
                 // We will wait for a few, but maybe strictly we should just do it.
                 // "FetchDebridMetadataAsync" does: check cache -> if miss -> fetch.
                 // But cache is present! So it will return HIT.
                 // We must BYPASS cache.
                 
                 var freshCount = 0;
                 foreach (var url in urlsToRefresh)
                 {
                      // We need a force-fetch logic
                      await ForceFetchMetadataAsync(itemId, url!);
                      freshCount++;
                 }
                 
                 return Ok(new { refreshed = freshCount });
             }
             catch (Exception ex)
             {
                 _logger.LogError(ex, "Failed to refresh cache for {ItemId}", itemId);
                 return StatusCode(500, ex.Message);
             }
             finally 
             { 
                 // If we released early, don't release again
                 if (sem.CurrentCount == 0) sem.Release(); 
             }
        }
        
        // Private helper that BYPASSES cache read but DOES write cache
        private async Task ForceFetchMetadataAsync(string itemId, string url)
        {
             if (!Guid.TryParse(itemId, out var guid)) return;
             var item = _libraryManager.GetItemById(guid);
             if (item == null) return;

             await FetchDebridMetadataAsync(item, url, forceRefresh: true, allowMagnetRevival: true);
        }

        #endregion

        private async Task DeleteFromDebridAsync(HttpClient client, string service, string apiKey, string id)
        {
            try 
            {
                _logger.LogInformation("[Baklava] Deleting from {Service}: {Id}", service, id);
                HttpResponseMessage? res = null;

                switch (service?.ToLower())
                {
                    case "alldebrid":
                        // Agent is required. 
                        res = await client.GetAsync($"https://api.alldebrid.com/v4/magnet/delete?agent=baklava&apikey={apiKey}&id={id}");
                        break;
                        
                    case "premiumize":
                         // POST form-data
                         var content = new FormUrlEncodedContent(new[] 
                         {
                             new KeyValuePair<string, string>("apikey", apiKey),
                             new KeyValuePair<string, string>("id", id)
                         });
                         res = await client.PostAsync("https://www.premiumize.me/api/transfer/delete", content);
                         break;
                         
                    case "debridlink":
                    case "debrid-link":
                        // Bearer auth required
                        var reqDL = new HttpRequestMessage(HttpMethod.Delete, $"https://debrid-link.com/api/v2/seedbox/torrents/{id}");
                        reqDL.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                        res = await client.SendAsync(reqDL);
                        break;
                        
                    case "torbox":
                         // Bearer auth + JSON body
                         var reqTB = new HttpRequestMessage(HttpMethod.Post, "https://torbox.app/v1/api/torrents/controltorrent");
                         reqTB.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                         // Manual JSON string to avoid System.Text.Json import issues or overhead? 
                         // We are using System.Text.Json at top level
                         var jsonBody = JsonSerializer.Serialize(new { torrent_id = id, operation = "delete" });
                         reqTB.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                         res = await client.SendAsync(reqTB);
                         break;

                    case "realdebrid":
                    default:
                        // Real-Debrid: Use downloads/delete (Download ID stored in cache)
                        // Requires Bearer
                        var reqRD = new HttpRequestMessage(HttpMethod.Delete, $"https://api.real-debrid.com/rest/1.0/downloads/delete/{id}");
                        reqRD.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                        res = await client.SendAsync(reqRD);
                        break;
                }

                if (res != null)
                {
                    if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        var body = await res.Content.ReadAsStringAsync();
                        _logger.LogWarning("[Baklava] Failed to delete from {Service} ({Id}). Status: {Status}. Body: {Body}", service, id, res.StatusCode, body);
                    }
                    else
                    {
                         _logger.LogInformation("[Baklava] Successfully deleted from {Service}: {Id}", service, id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error executing Debrid deletion for {Service}", service);
            }
        }

        private StreamDto MapFfprobeAudio(FfprobeAudio a) => new StreamDto
        {
            Index = a.Index, Title = a.Title, Language = a.Language, Codec = a.Codec, Channels = a.Channels, Bitrate = a.Bitrate
        };
        
        private StreamDto MapFfprobeSub(FfprobeSubtitle s) => new StreamDto
        {
            Index = s.Index, Title = s.Title, Language = s.Language, Codec = s.Codec, IsForced = s.IsForced, IsDefault = s.IsDefault
        };

        private class StreamDto
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public int? Channels { get; set; }
            public double? Bitrate { get; set; }
            public bool? IsForced { get; set; }
            public bool? IsDefault { get; set; }
            public bool? IsExternal { get; set; }
            public string? ExternalUrl { get; set; }
        }

        private class CachedMetadata
        {
            public List<StreamDto> Audio { get; set; } = new();
            public List<StreamDto> Subtitles { get; set; } = new();
            public string? OriginalUrl { get; set; }
            public string? TorrentId { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
        }

        // Return tuple with IsCached flag
        private async Task<(List<StreamDto> Audio, List<StreamDto> Subtitles, bool IsCached)?> FetchDebridMetadataAsync(BaseItem item, string url, bool forceRefresh = false, bool allowMagnetRevival = true)
        {
            try
            {
                var uri = new Uri(url);
                var config = Plugin.Instance?.Configuration;
                _logger.LogDebug("[Baklava] FetchDebridMetadataAsync. Checking configuration for service: {Service}", config?.DebridService);
                
                // Unified cache check (skip if forced)
                if (!forceRefresh)
                {
                    var cachedMetadata = await TryGetFromCache(item, url);
                    if (cachedMetadata != null)
                    {
                        return (cachedMetadata.Value.Audio, cachedMetadata.Value.Subtitles, true);
                    }
                }
                
                
                // Route to appropriate service
                if (config?.DebridService == "torbox")
                {
                    return await FetchTorBoxMetadataAsync(item, url, forceRefresh, allowMagnetRevival);
                }

                // Currently supporting Real-Debrid
                if (config?.DebridService != "realdebrid") return null;

                // Resolve URL if needed (e.g. Torrentio /resolve/ links)
                if (!uri.Host.Contains("real-debrid") && !uri.Host.Contains("rd-net"))
                {
                    try 
                    {
                        _logger.LogDebug("[Baklava] Resolving URL: {Url}", url);
                        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                        using var resClient = new HttpClient(handler); // Default timeout is usually enough
                        using var request = new HttpRequestMessage(HttpMethod.Head, url);
                        
                        // We use HEAD to avoid downloading body, assuming standard 302 redirect
                        var response = await resClient.SendAsync(request);
                        var oldUri = uri;
                        uri = response.RequestMessage.RequestUri; // This contains the final URL after redirects
                        
                        // Special check: if HEAD didn't change URI (some servers ignore HEAD for redirects), try GET with Range 0-0
                        if (uri == oldUri && response.StatusCode != System.Net.HttpStatusCode.Found && response.StatusCode != System.Net.HttpStatusCode.MovedPermanently)
                        {
                             // If it didn't redirect, maybe it's not a redirect? 
                             // Torrentio /resolve/ usually 302s.
                             // But let's verify if the key extraction works on the new URI.
                        }
                        _logger.LogDebug("[Baklava] Resolved URL to: {Url}", uri);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Baklava] Failed to resolve URL: {Url}", url);
                        return null; 
                    }
                }

                if (!uri.Host.Contains("real-debrid") && !uri.Host.Contains("rd-net"))
                {
                    _logger.LogDebug("[Baklava] Resolved URL is not a Real-Debrid URL: {Url}", uri);
                    return null;
                }

                // Extract File ID
                // RD URL: /d/{ID}/{Filename} -> This ID (after /d/) is the DOWNLOAD CODE, not the MediaInfo ID.
                // We need the Resource ID. We can find this by checking the user's downloads history.
                
                string? validId = null;
                var uriStr = uri.ToString();

                // Check Cache first
                if (_idCache.TryGetValue(uriStr, out var cachedId))
                {
                    _logger.LogDebug("[Baklava] Found ID in cache: {Id}", cachedId);
                    validId = cachedId;
                }
                else
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.DebridApiKey);

                    // Option 1: Check Downloads History (Scan pages 1-10)
                    try 
                    {
                        var targetFilename = System.IO.Path.GetFileName(uri.LocalPath);
                        _logger.LogDebug("[Baklava] Scanning Real-Debrid file list (up to 10 pages) for: {Fn}", targetFilename);

                        for (int page = 1; page <= 10; page++)
                        {
                            var downloadsResp = await client.GetAsync($"https://api.real-debrid.com/rest/1.0/downloads?page={page}&limit=100");
                            if (downloadsResp.IsSuccessStatusCode)
                            {
                                var jsonContent = await downloadsResp.Content.ReadAsStringAsync();
                                
                                // RD might return 204 No Content or empty string for empty list/pages
                                if (string.IsNullOrWhiteSpace(jsonContent))
                                {
                                    _logger.LogDebug("[Baklava] Page {Page} returned empty content. Stopping scan.", page);
                                    break;
                                }

                                using var dDoc = JsonDocument.Parse(jsonContent);
                                bool pageHasItems = false;
                                
                                foreach(var d in dDoc.RootElement.EnumerateArray())
                                {
                                    pageHasItems = true;
                                    bool match = false;
                                    string? historyFn = null;
                                    if (d.TryGetProperty("filename", out var hf)) historyFn = hf.GetString();
                                    
                                    // 1. Try to match by download link (ID segment)
                                    if (d.TryGetProperty("download", out var dlLink))
                                    {
                                        if (GetIdFromLink(dlLink.GetString()) == GetIdFromLink(uriStr))
                                            match = true;
                                    }
                                    
                                    // 2. Fallback: Match by Filename
                                    if (!match && !string.IsNullOrEmpty(historyFn))
                                    {
                                        if (string.Equals(historyFn, targetFilename, StringComparison.OrdinalIgnoreCase))
                                            match = true;
                                    }

                                    if (match)
                                    {
                                        validId = d.GetProperty("id").GetString();
                                        _logger.LogDebug("[Baklava] Found matching ID in history (Page {Page}): {Id}", page, validId);
                                        _idCache[uriStr] = validId;
                                        break;
                                    }
                                }
                                
                                if (validId != null) break;
                                if (!pageHasItems) break; // End of list
                            }
                            else
                            {
                                _logger.LogWarning("[Baklava] Download history fetch failed for page {Page} with status: {Status}", page, downloadsResp.StatusCode);
                                break; 
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Baklava] Error checking downloads history");
                    }
                }

                if (validId == null) 
                {
                    // Fallback: Magnet Revival Strategy
                    // We check the ORIGINAL 'url' because specifically Torrentio links contain the hash needed for revival.
                    // 'uri' might be the resolved RD link which lacks the hash.
                    if (url.Contains("/resolve/realdebrid/"))
                    {
                        try 
                        {
                            // Extract Hash from Source URL: .../resolve/realdebrid/APIKEY/HASH/...
                            // Parse original URL safely
                            var sourceUri = new Uri(url);
                            var parts = sourceUri.AbsolutePath.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
                            // Expected path: /resolve/realdebrid/{apikey}/{hash}/...
                            // parts[0]=resolve, [1]=realdebrid, [2]=apikey, [3]=HASH
                            // parts[0]=resolve, [1]=realdebrid, [2]=apikey, [3]=HASH
                            if (parts.Length >= 4)
                            {
                                if (allowMagnetRevival)
                                {
                                    var hash = parts[3];
                                    _logger.LogDebug("[Baklava] History scan failed. Attempting Magnet Revival for Hash: {Hash}", hash);
                                    validId = await ReviveAndGetIdFromMagnet(hash, config.DebridApiKey);
                                }
                                else
                                {
                                    _logger.LogDebug("[Baklava] History scan failed. Magnet Revival skipped (FetchAllNonCachedMetadata=false).");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Baklava] Magnet Revival failed.");
                        }
                    }

                    if (validId == null)
                    {
                        _logger.LogWarning("[Baklava] Could not find MediaInfo ID for URL: {Url}", uri);
                        return null;
                    }
                }

                _logger.LogDebug("[Baklava] Fetching media infos for ID: {Id}", validId);

                using var infoClient = new HttpClient();
                infoClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.DebridApiKey);
                var resp = await infoClient.GetAsync($"https://api.real-debrid.com/rest/1.0/streaming/mediaInfos/{validId}");
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Real-Debrid mediaInfos returned {Status}", resp.StatusCode);
                    return null;
                }
                
                // Helper to extract the CODE from a /d/CODE/ link
                string? GetIdFromLink(string? l)
                {
                    if (string.IsNullOrEmpty(l)) return null;
                    try {
                        var u = new Uri(l);
                        var s = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        for(int i=0; i<s.Length-1; i++) 
                            if(s[i]=="d") return s[i+1];
                        return null;
                    } catch { return null; }
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
                var root = doc.RootElement;
                
                // RD API structure: root -> details -> audio/subtitles
                JsonElement details = root;
                if (root.TryGetProperty("details", out var det)) details = det;

                // Helper to format Audio Title
                string FormatAudioTitle(JsonElement a)
                {
                    var lang = a.TryGetProperty("lang", out var l) ? l.GetString() : "Unknown";
                    var codec = a.TryGetProperty("codec", out var c) ? c.GetString() : "aac";
                    var channels = a.TryGetProperty("channels", out var ch) ? ch.GetDouble() : 0;
                    return $"{lang} ({codec} {channels}ch)";
                }

                // Helper to format Subtitle Title
                string FormatSubTitle(JsonElement s)
                {
                    var lang = s.TryGetProperty("lang", out var l) ? l.GetString() : "Unknown";
                    var codec = s.TryGetProperty("codec", out var c) ? c.GetString() : "srt";
                    return $"{lang} ({codec})";
                }

                var audios = new List<StreamDto>();
                if (details.TryGetProperty("audio", out var audioObj))
                {
                    if (audioObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in audioObj.EnumerateObject())
                            audios.Add(new StreamDto
                            {
                                Index = -1, 
                                Codec = prop.Value.TryGetProperty("codec", out var c) ? c.GetString() : null,
                                Language = prop.Value.TryGetProperty("lang", out var l) ? l.GetString() : null,
                                Title = FormatAudioTitle(prop.Value),
                                Channels = prop.Value.TryGetProperty("channels", out var ch) ? (int?)ch.GetDouble() : null
                            });
                    }
                    else if (audioObj.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in audioObj.EnumerateArray())
                            audios.Add(new StreamDto
                            {
                                Index = -1, 
                                Codec = a.TryGetProperty("codec", out var c) ? c.GetString() : null,
                                Language = a.TryGetProperty("lang", out var l) ? l.GetString() : null,
                                Title = FormatAudioTitle(a),
                                Channels = a.TryGetProperty("channels", out var ch) ? (int?)ch.GetDouble() : null
                            });
                    }
                }

                var subs = new List<StreamDto>();
                if (details.TryGetProperty("subtitles", out var subObj))
                {
                     if (subObj.ValueKind == JsonValueKind.Object)
                     {
                        foreach (var prop in subObj.EnumerateObject())
                             subs.Add(new StreamDto {
                                 Language = prop.Value.TryGetProperty("lang", out var l) ? l.GetString() : null,
                                 Codec = "srt", 
                                 Title = FormatSubTitle(prop.Value)
                             });
                     }
                     else if (subObj.ValueKind == JsonValueKind.Array)
                     {
                        foreach (var s in subObj.EnumerateArray())
                             subs.Add(new StreamDto {
                                 Language = s.TryGetProperty("lang", out var l) ? l.GetString() : null,
                                 Codec = "srt",
                                 Title = FormatSubTitle(s)
                             });
                     }
                }

                // --- CACHE SAVE START ---
                if (Plugin.Instance != null)
                {
                    await CacheStreamData(item, url, audios, subs, validId);
                }
                // --- CACHE SAVE END ---

                return (audios, subs, false); // Not cached, just fetched
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching debrid metadata");
                return null;
            }
        }
        
        // Generate robust cache key
        private string GetCacheKey(string url)
        {
            try
            {
                // 1. Try to extract Hash from URL (Preferred)
                // Torrentio: .../resolve/realdebrid/{apikey}/{hash}/...
                if (url.Contains("/resolve/realdebrid/") || url.Contains("/resolve/antigravity/"))
                {
                     var uri = new Uri(url);
                     var parts = uri.AbsolutePath.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
                     // Expecting hash at index 3 for standard torrentio
                     if (parts.Length >= 4) return parts[3];
                }
            } 
            catch { }

            // 2. Fallback: SHA256 of the entire URL
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var bytes = System.Text.Encoding.UTF8.GetBytes(url);
                var hashBytes = sha.ComputeHash(bytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                 // Last resort: simple hash code
                 return url.GetHashCode().ToString("X");
            }
        }

        // Helper to get formatted path based on item type
        private string GetCacheFilePath(string rootDir, BaseItem item)
        {
            // Sanitize
            string Sanitize(string s) => string.Join("_", s.Split(System.IO.Path.GetInvalidFileNameChars()));

            if (item is Episode ep)
            {
                // Shows/{SeriesName}/Season {S}/{Index} - {Name} [{Id}]/{Id}.json
                // Actually user requested: create a folder with show name -> season folder -> each episode its own json
                // Wait, putting json directly in Season folder is cleaner if we name it nicely.
                // But requested: "inside that each episode its own json".
                // Let's do: Shows/{SeriesName}/Season {S}/{Id}.json
                // This allows deleting season folder to delete all episodes.
                var showFolder = System.IO.Path.Combine(rootDir, "Shows", Sanitize(ep.SeriesName ?? "Unknown"));
                var seasonFolder = System.IO.Path.Combine(showFolder, $"Season {ep.ParentIndexNumber ?? 1}");
                if (!System.IO.Directory.Exists(seasonFolder)) System.IO.Directory.CreateDirectory(seasonFolder);
                return System.IO.Path.Combine(seasonFolder, $"{item.Id}.json");
            }
            else if (item is MediaBrowser.Controller.Entities.Movies.Movie mov)
            {
                // Movies -> Root (as requested: "movies can just drop the json in the main cache folder")
                return System.IO.Path.Combine(rootDir, $"{item.Id}.json");
            }
            
            // Fallback
            return System.IO.Path.Combine(rootDir, $"{item.Id}.json");
        }

        // Helper to extract hash and save cache to {ItemId}.json
        private async Task CacheStreamData(BaseItem item, string url, List<StreamDto> audios, List<StreamDto> subs, string? torrentId = null)
        {
            if (Plugin.Instance == null) return;
            string urlHash = GetCacheKey(url);
            var itemId = item.Id.ToString("N");
            var sem = GetItemLock(itemId); // Get lock for this item

            try
            {
                await sem.WaitAsync();

                var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
                var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
                
                if (!System.IO.Directory.Exists(cacheDir)) System.IO.Directory.CreateDirectory(cacheDir);
                
                var cacheFile = GetCacheFilePath(cacheDir, item);
                _logger.LogDebug("[Baklava] Accessing Cache File: {File} for URL Hash: {Hash}", cacheFile, urlHash);

                // Load existing dict or create new
                Dictionary<string, CachedMetadata> itemCache;
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

                if (System.IO.File.Exists(cacheFile))
                {
                    try 
                    {
                        var existingJson = await System.IO.File.ReadAllTextAsync(cacheFile);
                        itemCache = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(existingJson) ?? new Dictionary<string, CachedMetadata>();
                    }
                    catch 
                    {
                        // Corrupt? Overwrite.
                        itemCache = new Dictionary<string, CachedMetadata>();
                    }
                }
                else
                {
                    itemCache = new Dictionary<string, CachedMetadata>();
                }

                // Update/Add entry
                itemCache[urlHash] = new CachedMetadata { 
                    Audio = audios, 
                    Subtitles = subs, 
                    OriginalUrl = url, 
                    TorrentId = torrentId,
                    Title = item.Name,
                    Year = item.ProductionYear
                };
                
                await System.IO.File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(itemCache, jsonOptions));
                _logger.LogDebug("[Baklava] Item Cache Updated.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Baklava] Failed to save stream cache");
            }
            finally
            {
                sem.Release();
            }
        }

        // Helper to read cache from {ItemId}.json
        private async Task<(List<StreamDto> Audio, List<StreamDto> Subtitles)?> TryGetFromCache(BaseItem item, string url)
        {
           if (Plugin.Instance == null) return null;
            string urlHash = GetCacheKey(url);
            var itemId = item.Id.ToString("N");
            var sem = GetItemLock(itemId);

            try 
            {
                await sem.WaitAsync();

                var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
                var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
                
                // Construct path same way as save
                var cacheFile = GetCacheFilePath(cacheDir, item);

                _logger.LogDebug("[Baklava] Checking Item Cache: {File} for Hash: {Hash}", cacheFile, urlHash);
                
                if (System.IO.File.Exists(cacheFile))
                {
                    try 
                    {
                        var jsonCache = await System.IO.File.ReadAllTextAsync(cacheFile);
                        var itemCache = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(jsonCache);
                        
                        if (itemCache != null && itemCache.TryGetValue(urlHash, out var cachedData))
                        {
                            _logger.LogDebug("[Baklava] Cache HIT! Returning stored metadata.");
                            return (cachedData.Audio, cachedData.Subtitles);
                        }
                    }
                    catch { }
                }
                
                _logger.LogDebug("[Baklava] Cache MISS.");
                return null;
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task<string?> ReviveAndGetIdFromMagnet(string hash, string apiKey)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            
            // 1. Add Magnet
            var addContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("magnet", $"magnet:?xt=urn:btih:{hash}") });
            var addResp = await client.PostAsync("https://api.real-debrid.com/rest/1.0/torrents/addMagnet", addContent);
            if (!addResp.IsSuccessStatusCode) return null;
            
            var addJson = JsonDocument.Parse(await addResp.Content.ReadAsStreamAsync());
            var torrentId = addJson.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(torrentId)) return null;

            try 
            {
                // 2. Select Files (All)
                var selContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("files", "all") });
                await client.PostAsync($"https://api.real-debrid.com/rest/1.0/torrents/selectFiles/{torrentId}", selContent);

                // 3. Get Info to grab the Link
                var infoResp = await client.GetAsync($"https://api.real-debrid.com/rest/1.0/torrents/info/{torrentId}");
                if (!infoResp.IsSuccessStatusCode) return null;
                
                using var infoJson = JsonDocument.Parse(await infoResp.Content.ReadAsStreamAsync());
                if (infoJson.RootElement.TryGetProperty("links", out var links) && links.GetArrayLength() > 0)
                {
                    // Use first link
                    var link = links[0].GetString();
                    
                    // 4. Unrestrict Link to get ID
                    var unrContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", link) });
                    var unrResp = await client.PostAsync("https://api.real-debrid.com/rest/1.0/unrestrict/link", unrContent);
                    if (unrResp.IsSuccessStatusCode)
                    {
                        using var unrJson = JsonDocument.Parse(await unrResp.Content.ReadAsStreamAsync());
                        if (unrJson.RootElement.TryGetProperty("id", out var idEl))
                        {
                            return idEl.GetString();
                        }
                    }
                }
            }
            finally
            {
                // 5. Cleanup: Delete Torrent
                await client.DeleteAsync($"https://api.real-debrid.com/rest/1.0/torrents/delete/{torrentId}");
                _logger.LogDebug("[Baklava] Deleted temporary torrent: {Id}", torrentId);
            }
            return null;
        }

        [HttpPost("cache/probe-all")]
        public async Task<ActionResult> ProbeAllCache()
        {
             if (Plugin.Instance == null) return NotFound();
             var config = Plugin.Instance.Configuration;
             if (string.IsNullOrEmpty(config?.DebridApiKey)) return BadRequest("Debrid API Key not set");

             _logger.LogInformation("[Baklava] Starting PROBE ALL CACHE operation...");
             
             // 1. Get all cache files
             var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "..", "..", "cache"));
             var cacheDir = System.IO.Path.Combine(baseDir, "Baklava");
             if (!System.IO.Directory.Exists(cacheDir)) return Ok(new { count = 0, message = "Cache empty" });

             var files = System.IO.Directory.GetFiles(cacheDir, "*.json", System.IO.SearchOption.AllDirectories);
             var updatedCount = 0;

             _ = Task.Run(async () => 
             {
                 try 
                 {
                     using var client = new HttpClient();
                     foreach (var file in files)
                     {
                         try 
                         {
                             // Extract ItemId from filename
                             var filename = System.IO.Path.GetFileNameWithoutExtension(file);
                             if (!Guid.TryParse(filename, out var guid)) continue;
                             
                             var item = _libraryManager.GetItemById(guid);
                             if (item == null) continue;

                             // Read Cache
                             var json = await System.IO.File.ReadAllTextAsync(file);
                             var dict = JsonSerializer.Deserialize<Dictionary<string, CachedMetadata>>(json);
                             if (dict == null) continue;
                             
                             var keys = dict.Keys.ToList(); // Snapshot keys
                             foreach (var key in keys)
                             {
                                 var entry = dict[key];
                                 if (string.IsNullOrEmpty(entry.OriginalUrl)) continue;

                                 _logger.LogInformation("[Baklava] Probing: {Title} ({Url})", entry.Title ?? item.Name, entry.OriginalUrl);

                                 // 1. Get Playable URL
                                 var playableUrl = await GetPlayableDebridUrl(client, config.DebridService, config.DebridApiKey, entry.OriginalUrl);
                                 if (string.IsNullOrEmpty(playableUrl)) 
                                 {
                                     _logger.LogWarning("[Baklava] Could not get playable URL for probing: {Url}", entry.OriginalUrl);
                                     continue;
                                 }

                                 // 2. FFprobe
                                 var probeResult = await RunFfprobeAsync(playableUrl);
                                 if (probeResult != null)
                                 {
                                     _logger.LogInformation("[Baklava] Probe Success! Audio: {AC}, Subs: {SC}", probeResult.Audio.Count, probeResult.Subtitles.Count);
                                     
                                     // 3. Update Cache
                                     // Map results
                                     var audios = probeResult.Audio.Select(MapFfprobeAudio).ToList();
                                     var subs = probeResult.Subtitles.Select(MapFfprobeSub).ToList();
                                     
                                     await CacheStreamData(item, entry.OriginalUrl, audios, subs, entry.TorrentId);
                                     updatedCount++;
                                 }
                             }
                         }
                         catch (Exception ex)
                         {
                             _logger.LogError(ex, "[Baklava] Error probing cache file: {File}", file);
                         }
                     }
                     _logger.LogInformation("[Baklava] Probe All Cache Completed. Updated {Count} items.", updatedCount);
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "[Baklava] Critical error in Probe All Cache task");
                 }
             });

             return Accepted(new { message = "Probe All Cache started in background. Check logs for progress." });
        }

        private async Task<string?> GetPlayableDebridUrl(HttpClient client, string service, string apiKey, string originalUrl)
        {
            // Support Real-Debrid for now as per revival logic reuse
            if (service?.ToLower() != "realdebrid") return null; 

            try 
            {
                 string? hash = null;
                 if (originalUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                 {
                      var match = System.Text.RegularExpressions.Regex.Match(originalUrl, "xt=urn:btih:([a-zA-Z0-9]+)");
                      if (match.Success) hash = match.Groups[1].Value;
                 }
                 else 
                 {
                      if (originalUrl.Contains("/resolve/realdebrid/") || originalUrl.Contains("/resolve/antigravity/"))
                      {
                           var uri = new Uri(originalUrl);
                           var parts = uri.AbsolutePath.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
                           if (parts.Length >= 4) hash = parts[3];
                      }
                 }

                 if (!string.IsNullOrEmpty(hash))
                 {
                      // Use Revive Logic (returns Download ID)
                      var id = await ReviveAndGetIdFromMagnet(hash, apiKey);
                      if (!string.IsNullOrEmpty(id))
                      {
                          // Get Download Link from Download ID
                          client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                          var infoResp = await client.GetAsync($"https://api.real-debrid.com/rest/1.0/downloads/info/{id}");
                          if (infoResp.IsSuccessStatusCode)
                          {
                               using var infoJson = JsonDocument.Parse(await infoResp.Content.ReadAsStreamAsync());
                               JsonElement root = infoJson.RootElement;
                               
                               // Handle API returning Array instead of Object
                               if (root.ValueKind == JsonValueKind.Array)
                               {
                                   if (root.GetArrayLength() > 0) root = root[0];
                                   else return null;
                               }

                               if (root.TryGetProperty("download", out var dlEl))
                               {
                                   return dlEl.GetString();
                               }
                          }
                      }
                 }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Baklava] Failed to get playable URL for {Url}", originalUrl);
            }

            // Fallback: If we couldn't revive it (e.g. MediaFusion, or already direct link), return originalUrl if it looks like Http
            if (!string.IsNullOrEmpty(originalUrl) && originalUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return originalUrl;

            return null;
        }


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
                tasks.Add(FetchTMDBAsync($"/{mediaType}/{tmdbId}/credits", apiKey));
            if (includeReviews)
                tasks.Add(FetchTMDBAsync($"/{mediaType}/{tmdbId}/reviews", apiKey));

            var results = await Task.WhenAll(tasks);

            var mainRaw = root.GetRawText();
            string creditsRaw = "null";
            string reviewsRaw = "null";

            if (includeCredits && results.Length > 0 && results[0] != null)
                creditsRaw = results[0].RootElement.GetRawText();

            if (includeReviews)
            {
                var reviewsIndex = tasks.Count > 1 ? 1 : 0;
                if (results.Length > reviewsIndex && results[reviewsIndex] != null)
                    reviewsRaw = results[reviewsIndex].RootElement.GetRawText();
            }

            var combined = $"{{\"main\":{mainRaw},\"credits\":{creditsRaw},\"reviews\":{reviewsRaw}}}";
            return Content(combined, "application/json");
        }

        private async Task<JsonDocument?> FetchTMDBAsync(string endpoint, string apiKey, Dictionary<string, string>? queryParams = null)
        {
            try
            {
                var cacheKey = $"{endpoint}?{string.Join("&", queryParams?.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>())}";
                
                lock (_cache)
                {
                    if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
                        return JsonDocument.Parse(cached.Data);
                }

                var builder = new StringBuilder();
                builder.Append(TMDB_BASE).Append(endpoint);
                builder.Append("?api_key=").Append(Uri.EscapeDataString(apiKey));

                if (queryParams != null)
                {
                    foreach (var param in queryParams)
                        builder.Append('&').Append(Uri.EscapeDataString(param.Key))
                               .Append('=').Append(Uri.EscapeDataString(param.Value));
                }

                using var http = new HttpClient();
                var response = await http.GetAsync(builder.ToString());
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();

                lock (_cache)
                {
                    _cache[cacheKey] = (DateTime.UtcNow.Add(CACHE_DURATION), content);
                    
                    // Simple cleanup
                    var expired = _cache.Where(k => k.Value.Expiry <= DateTime.UtcNow).Select(k => k.Key).ToList();
                    foreach (var key in expired) _cache.Remove(key);
                }

                return JsonDocument.Parse(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching from TMDB: {Endpoint}", endpoint);
                return null;
            }
        }

        #endregion

        #region FFprobe Fallback
        
        private class FfprobeAudio { public int Index {get;set;} public string? Title {get;set;} public string? Language {get;set;} public string? Codec {get;set;} public int? Channels {get;set;} public double? Bitrate {get;set;} }
        private class FfprobeSubtitle { public int Index {get;set;} public string? Title {get;set;} public string? Language {get;set;} public string? Codec {get;set;} public bool IsForced {get;set;} public bool IsDefault {get;set;} }
        private class FfprobeResult { public List<FfprobeAudio> Audio {get;set;} = new(); public List<FfprobeSubtitle> Subtitles {get;set;} = new(); }


        private async Task<FfprobeResult?> RunFfprobeAsync(string url)
        {
            var candidates = new[] { "/usr/lib/jellyfin-ffmpeg/ffprobe", "/usr/bin/ffprobe", "ffprobe" };
            var ffprobePath = candidates.FirstOrDefault(c => 
            {
                try { return System.IO.File.Exists(c); } catch { return false; }
            }) ?? "ffprobe";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -analyzeduration 15000000 -probesize 15000000 -seekable 0 \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            _logger.LogInformation("[Baklava] Running FFprobe on URL: {Url}", url);

            try
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;

                if (!proc.WaitForExit(60000)) // Increased to 60s timeout
                {
                    try { proc.Kill(); } catch {}
                    _logger.LogWarning("[Baklava] FFprobe timed out after 60s for URL: {Url}", url);
                    return null;
                }

                var json = await proc.StandardOutput.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(json)) 
                {
                    _logger.LogWarning("[Baklava] FFprobe returned empty output for URL: {Url}", url);
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("streams", out var streams)) 
                {
                    _logger.LogWarning("[Baklava] FFprobe JSON missing 'streams' property. Raw: {Json}", json);
                    return null;
                }

                var res = new FfprobeResult();
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.GetProperty("codec_type").GetString();
                    var index = s.GetProperty("index").GetInt32();
                    var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    
                    string? lang = null;
                    string? title = null;
                    if (s.TryGetProperty("tags", out var tags))
                    {
                         if (tags.TryGetProperty("language", out var l)) lang = l.GetString();
                         if (tags.TryGetProperty("title", out var t)) title = t.GetString();
                    }

                    if (type == "audio")
                    {
                        int? channels = null;
                        if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number) channels = ch.GetInt32();
                        
                        long? bitRate = null;
                        if (s.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String)
                        {
                            if (long.TryParse(br.GetString(), out var brv)) bitRate = brv;
                        }

                        res.Audio.Add(new FfprobeAudio {
                            Index = index,
                            Codec = codec,
                            Language = lang,
                            Title = title,
                            Channels = channels,
                            Bitrate = (double?)bitRate
                        });
                    }
                    else if (type == "subtitle")
                    {
                        res.Subtitles.Add(new FfprobeSubtitle {
                            Index = index,
                            Codec = codec,
                            Language = lang,
                            Title = title,
                            IsForced = false, 
                            IsDefault = false
                        });
                    }
                }
                
                if (res.Audio.Count == 0 && res.Subtitles.Count == 0)
                {
                     _logger.LogWarning("[Baklava] FFprobe returned 0 Audio/Subs. Raw JSON: {Json}", json);
                }

                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FFprobe failed");
                return null;
            }
        }
        private async Task<string> TryResolveUrl(string url)
        {
            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.AllowAutoRedirect = false;
                request.Method = "HEAD";
                request.UserAgent = "Jellyfin-Baklava-Plugin";

                try 
                {
                    using var response = (System.Net.HttpWebResponse)await request.GetResponseAsync();
                    if (response.StatusCode == System.Net.HttpStatusCode.Moved || 
                        response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                        response.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        return response.Headers["Location"] ?? url;
                    }
                }
                catch (System.Net.WebException ex) when (ex.Response is System.Net.HttpWebResponse errorResponse && errorResponse.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    _logger.LogWarning("[Baklava] HEAD method failed (405). Retrying with GET (Range: 0-0) for URL: {Url}", url);
                    
                    // Fallback to GET for servers (like AIOStreams) that block HEAD
                    var getRequest = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    getRequest.AllowAutoRedirect = false;
                    getRequest.Method = "GET";
                    getRequest.UserAgent = "Jellyfin-Baklava-Plugin";
                    // Request only the first byte to minimize data transfer
                    getRequest.Headers.Add("Range", "bytes=0-0");
                    
                    using var getResponse = (System.Net.HttpWebResponse)await getRequest.GetResponseAsync();
                    
                    _logger.LogDebug("[Baklava] GET Retry Status: {Status}", getResponse.StatusCode);
                    if (getResponse.Headers["Location"] != null)
                        _logger.LogDebug("[Baklava] GET Retry Location Header: {Loc}", getResponse.Headers["Location"]);

                     if (getResponse.StatusCode == System.Net.HttpStatusCode.Moved || 
                        getResponse.StatusCode == System.Net.HttpStatusCode.Redirect ||
                        getResponse.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        return getResponse.Headers["Location"] ?? url;
                    }
                }

                return url;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Baklava] URL Resolution failed: {Message}. Using original URL.", ex.Message);
                return url;
            }
        }

        #endregion

        // --- TORBOX IMPLEMENTATION ---

        private async Task<(List<StreamDto> Audio, List<StreamDto> Subtitles, bool IsCached)?> FetchTorBoxMetadataAsync(BaseItem item, string url, bool forceRefresh, bool allowMagnetRevival)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.DebridApiKey)) return null;

            string? hash = ExtractHash(url);
            if (string.IsNullOrEmpty(hash))
            {
                _logger.LogWarning("[Baklava] Could not extract hash from URL for TorBox: {Url}", url);
                return null;
            }

            try 
            {
                // 1. Check Cache
                var files = await CheckTorBoxCache(hash, config.DebridApiKey);
                
                // 2. Fallback Probe (if enabled and not cached)
                if (files == null && allowMagnetRevival)
                {
                     _logger.LogDebug("[Baklava] TorBox CheckCached miss. Attempting Probe (Add Magnet) for hash: {Hash}", hash);
                     files = await ProbeTorBoxStream(hash, config.DebridApiKey);
                }

                if (files != null)
                {
                    var audio = new List<StreamDto>();
                    var subs = new List<StreamDto>();

                    foreach (var f in files)
                    {
                        var ext = System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant();
                        if (string.IsNullOrEmpty(ext)) continue;

                        if (IsAudio(ext))
                        {
                            audio.Add(new StreamDto 
                            { 
                                Codec = ext.TrimStart('.'), 
                                Language = "und", // TorBox doesn't provide lang metadata easily
                                Title = f.Name,
                                IsExternal = true 
                            });
                        }
                        else if (IsSubtitle(ext))
                        {
                            subs.Add(new StreamDto 
                            { 
                                Codec = ext.TrimStart('.'), 
                                Language = ExtractLanguageFromFilename(f.Name), 
                                Title = f.Name,
                                IsExternal = true
                            });
                        }
                    }
                    _logger.LogInformation("[Baklava] TorBox Metadata found. Audio: {AC}, Subs: {SC}", audio.Count, subs.Count);
                    return (audio, subs, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] TorBox Metadata fetch failed.");
            }

            return null;
        }

        private async Task<List<TorBoxFile>?> CheckTorBoxCache(string hash, string apiKey)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            
            // GET /v1/api/torrents/checkcached?hash={hash}&format=object&list_files=true
            var uri = $"https://api.torbox.app/v1/api/torrents/checkcached?hash={hash}&format=object&list_files=true";
            var resp = await client.GetAsync(uri);
            
            if (!resp.IsSuccessStatusCode) return null;

            var json = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            // Response structure: { "data": { "HASH": { "name": "...", "files": [ ... ] } } }
            // OR if not cached: { "data": { "HASH": null } } or similar (need to be careful)
            
            if (json.RootElement.TryGetProperty("data", out var data) && 
                data.TryGetProperty(hash, out var hashData) && 
                hashData.ValueKind != JsonValueKind.Null &&
                hashData.TryGetProperty("files", out var filesElement) &&
                filesElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<TorBoxFile>();
                foreach (var f in filesElement.EnumerateArray())
                {
                    if (f.TryGetProperty("name", out var name) && f.TryGetProperty("size", out var size))
                    {
                        list.Add(new TorBoxFile { Name = name.GetString() ?? "", Size = size.GetInt64() });
                    }
                }
                return list;
            }

            return null;
        }

        private async Task<List<TorBoxFile>?> ProbeTorBoxStream(string hash, string apiKey)
        {
             // Fallback: Add Magnet -> Get Info
             using var client = new HttpClient();
             client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

             var content = new FormUrlEncodedContent(new[] 
             { 
                 new KeyValuePair<string, string>("magnet", $"magnet:?xt=urn:btih:{hash}"),
                 new KeyValuePair<string, string>("seed", "1"),
                 new KeyValuePair<string, string>("allow_zip", "false")
             });

             var resp = await client.PostAsync("https://api.torbox.app/v1/api/torrents/createtorrent", content);
             if (!resp.IsSuccessStatusCode) return null;

             var json = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
             // Response: { "data": { "torrent_id": 12345, ... } }
             
             if (json.RootElement.TryGetProperty("data", out var data) && 
                 data.TryGetProperty("torrent_id", out var idEl))
             {
                 var torrentId = idEl.ToString(); // Could be number or string
                 
                 // Get Info
                 var infoResp = await client.GetAsync($"https://api.torbox.app/v1/api/torrents/mylist?id={torrentId}");
                 if (infoResp.IsSuccessStatusCode)
                 {
                     var infoJson = JsonDocument.Parse(await infoResp.Content.ReadAsStreamAsync());
                     // Parse 'files' from info
                     if (infoJson.RootElement.TryGetProperty("data", out var infoData) && 
                         infoData.TryGetProperty("files", out var filesEl))
                     {
                         // Map files
                         var list = new List<TorBoxFile>();
                         foreach (var f in filesEl.EnumerateArray())
                         {
                              // Need to check specific structure of 'mylist' files
                              // Usually: { "name": "...", "size": ... }
                             if (f.TryGetProperty("name", out var name))
                             {
                                 list.Add(new TorBoxFile { Name = name.GetString() ?? "" });
                             }
                         }
                         
                         // Cleanup? TorBox autoscrubs, but maybe good to delete?
                         // DELETE /v1/api/torrents/controltorrent Payload: { "torrent_id": ID, "operation": "delete" }
                         // Keeping it simple for now, skipping delete to avoid deleting user's actual content if it matched.
                         
                         return list;
                     }
                 }
             }
             return null;
        }

        private string? ExtractHash(string url)
        {
            // 1. Check for Magnet
            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(url, "xt=urn:btih:([a-fA-F0-9]{40})");
                if (match.Success) return match.Groups[1].Value;
            }
            
            // 2. Check for Regex in URL (generic)
            var hashMatch = System.Text.RegularExpressions.Regex.Match(url, "([a-fA-F0-9]{40})");
            if (hashMatch.Success) return hashMatch.Groups[1].Value;

            return null;
        }
        
        private bool IsAudio(string ext) => new[] { ".mp3", ".aac", ".ac3", ".eac3", ".dts", ".flac", ".wav" }.Contains(ext);
        private bool IsSubtitle(string ext) => new[] { ".srt", ".vtt", ".sub", ".ass" }.Contains(ext);
        
        private string ExtractLanguageFromFilename(string filename)
        {
            // Simple heuristic
            if (filename.Contains("eng", StringComparison.OrdinalIgnoreCase)) return "eng";
            if (filename.Contains("jpn", StringComparison.OrdinalIgnoreCase)) return "jpn";
            // ... add more if needed
            return "und";
        }

        private class TorBoxFile
        {
            public string Name { get; set; } = "";
            public long Size { get; set; }
        }
    }
}