#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Baklava.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    /// <summary>
    /// API Controller for caching and retrieving Gelato streams
    /// </summary>
    [ApiController]
    [Route("api/baklava/streams")]
    [Produces("application/json")]
    [Authorize]
    public class StreamCacheController : ControllerBase
    {
        private readonly ILogger<StreamCacheController> _logger;
        private readonly BaklavaDbService _dbService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Web-compatible audio codecs
        private static readonly HashSet<string> WebCompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "aac", "mp4a", "opus", "vorbis", "mp3", "mpeg"
        };

        // Incompatible audio codecs that require transcoding
        private static readonly HashSet<string> IncompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "ac3", "eac3", "dd", "dd+", "ddp", "dolby", "dolbydigital",
            "truehd", "dts", "dts-hd", "dtshd", "dts-ma", "atmos"
        };

        public StreamCacheController(
            ILogger<StreamCacheController> logger,
            BaklavaDbService dbService,
            IHttpClientFactory httpClientFactory,
            MediaBrowser.Controller.Library.ILibraryManager libraryManager)
        {
            _logger = logger;
            _dbService = dbService;
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Check if audio codec string from Stremio is web-compatible
        /// </summary>
        private bool IsAudioWebCompatible(string? audioString)
        {
            if (string.IsNullOrEmpty(audioString))
            {
                // No audio info = assume compatible (better than blocking)
                return true;
            }

            var audioLower = audioString.ToLowerInvariant();

            // Check for incompatible codecs first
            if (IncompatibleAudioCodecs.Any(codec => audioLower.Contains(codec)))
            {
                _logger.LogDebug("[StreamCache] Audio '{Audio}' is incompatible", audioString);
                return false;
            }

            // Check for compatible codecs
            if (WebCompatibleAudioCodecs.Any(codec => audioLower.Contains(codec)))
            {
                _logger.LogDebug("[StreamCache] Audio '{Audio}' is compatible", audioString);
                return true;
            }

            // Unknown codec = assume incompatible (safer)
            _logger.LogDebug("[StreamCache] Audio '{Audio}' is unknown, marking as incompatible", audioString);
            return false;
        }

        /// <summary>
        /// Cache streams for an item by fetching DIRECTLY from Gelato's internal provider
        /// POST /api/baklava/streams/cache
        /// </summary>
        [HttpPost("cache")]
        public async Task<ActionResult<CacheStreamsResponse>> CacheStreams([FromBody] CacheStreamsRequest request)
        {
            if (string.IsNullOrEmpty(request.ItemId))
            {
                return BadRequest("ItemId is required");
            }

            if (string.IsNullOrEmpty(request.StremioId) && string.IsNullOrEmpty(request.ImdbId) && string.IsNullOrEmpty(request.TmdbId))
            {
                return BadRequest("At least one of StremioId, ImdbId, or TmdbId is required");
            }

            _logger.LogInformation("[StreamCache] Caching streams for item {ItemId}", request.ItemId);

            try
            {
                // Fetch streams DIRECTLY from Gelato's internal provider
                var streams = await FetchStreamsFromGelatoProvider(request);
                
                if (streams == null || streams.Count == 0)
                {
                    return Ok(new CacheStreamsResponse
                    {
                        Success = false,
                        Message = "No streams found from Gelato",
                        StreamCount = 0
                    });
                }

                // Save to database
                var success = await _dbService.SaveStreamsAsync(
                    request.ItemId,
                    request.StremioId,
                    request.ImdbId,
                    request.TmdbId,
                    request.ItemType ?? "unknown",
                    request.UserId,
                    streams
                );

                return Ok(new CacheStreamsResponse
                {
                    Success = success,
                    Message = success ? "Streams cached successfully" : "Failed to cache streams",
                    StreamCount = streams.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error caching streams for {ItemId}", request.ItemId);
                return StatusCode(500, new CacheStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    StreamCount = 0
                });
            }
        }

        /// <summary>
        /// Fetch RAW streams from Gelato and return all fields for inspection
        /// POST /api/baklava/streams/raw
        /// </summary>
        [HttpPost("raw")]
        public async Task<ActionResult<object>> GetRawStreams([FromBody] CacheStreamsRequest request)
        {
            if (string.IsNullOrEmpty(request.StremioId) && string.IsNullOrEmpty(request.ImdbId) && string.IsNullOrEmpty(request.TmdbId))
            {
                return BadRequest("At least one of StremioId, ImdbId, or TmdbId is required");
            }

            _logger.LogInformation("[StreamCache] Fetching RAW streams for inspection");

            try
            {
                var streams = await FetchStreamsFromGelatoProvider(request);
                
                return Ok(new
                {
                    Success = true,
                    StreamCount = streams.Count,
                    Streams = streams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error fetching raw streams");
                return StatusCode(500, new
                {
                    Success = false,
                    Message = $"Error: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Get cached streams for an item
        /// GET /api/baklava/streams/{itemId}
        /// </summary>
        [HttpGet("{itemId}")]
        public async Task<ActionResult<GetStreamsResponse>> GetStreams(
            string itemId, 
            [FromQuery] string? userId = null,
            [FromQuery] bool directPlayOnly = false)
        {
            _logger.LogInformation("[StreamCache] Getting streams for item {ItemId}, DirectPlayOnly={DirectPlayOnly}", itemId, directPlayOnly);

            try
            {
                var streams = await _dbService.GetStreamsAsync(itemId, userId);

                if (streams == null || streams.Count == 0)
                {
                    return NotFound(new GetStreamsResponse
                    {
                        Success = false,
                        Message = "No cached streams found",
                        Streams = new List<StreamInfo>()
                    });
                }

                // Filter by WebCompatible if directPlayOnly is enabled
                if (directPlayOnly)
                {
                    var originalCount = streams.Count;
                    streams = streams.Where(s => s.WebCompatible == true).ToList();
                    _logger.LogInformation(
                        "[StreamCache] Filtered for direct play: {Original} total -> {Filtered} compatible",
                        originalCount,
                        streams.Count
                    );
                }

                return Ok(new GetStreamsResponse
                {
                    Success = true,
                    Message = directPlayOnly 
                        ? $"Streams retrieved (direct play only: {streams.Count} compatible)" 
                        : "Streams retrieved successfully",
                    Streams = streams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error getting streams for {ItemId}", itemId);
                return StatusCode(500, new GetStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Streams = new List<StreamInfo>()
                });
            }
        }

        /// <summary>
        /// Smart cache: Get streams with background update if new streams available
        /// GET /api/baklava/streams/{itemId}/smart
        /// </summary>
        [HttpGet("{itemId}/smart")]
        public async Task<ActionResult<SmartGetStreamsResponse>> GetStreamsSmartAsync(
            string itemId,
            [FromQuery] string? stremioId = null,
            [FromQuery] string? imdbId = null,
            [FromQuery] string? tmdbId = null,
            [FromQuery] string? itemType = null,
            [FromQuery] string? userId = null)
        {
            _logger.LogInformation("[StreamCache] Smart get streams for item {ItemId}", itemId);

            try
            {
                // 1. Get cached streams first (fast response)
                var cachedStreams = await _dbService.GetStreamsAsync(itemId, userId);

                // 2. Fetch fresh streams from Gelato
                List<StreamInfo> freshStreams;
                try
                {
                    freshStreams = await FetchStreamsFromGelato(new CacheStreamsRequest
                    {
                        ItemId = itemId,
                        StremioId = stremioId,
                        ImdbId = imdbId,
                        TmdbId = tmdbId,
                        ItemType = itemType,
                        UserId = userId
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StreamCache] Failed to fetch fresh streams, returning cached only");
                    
                    // Return cached even if fetch fails
                    return Ok(new SmartGetStreamsResponse
                    {
                        Success = true,
                        Message = "Returning cached streams (fetch failed)",
                        Streams = cachedStreams ?? new List<StreamInfo>(),
                        FromCache = true,
                        HasNewStreams = false,
                        NewStreamsCount = 0
                    });
                }

                // 3. Compare cached vs fresh
                var comparison = _dbService.CompareStreams(cachedStreams, freshStreams);

                // 4. If we have cached streams, return them immediately
                if (cachedStreams != null && cachedStreams.Count > 0)
                {
                    // Start background update if there are new streams
                    if (comparison.HasNewStreams)
                    {
                        _logger.LogInformation(
                            "[StreamCache] Found {NewCount} new streams for {ItemId}, updating in background",
                            comparison.NewStreams.Count,
                            itemId
                        );

                        // Fire and forget background merge
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _dbService.MergeStreamsAsync(
                                    itemId,
                                    stremioId,
                                    imdbId,
                                    tmdbId,
                                    itemType ?? "unknown",
                                    userId,
                                    comparison.NewStreams
                                );
                                _logger.LogInformation(
                                    "[StreamCache] Background merge completed for {ItemId}",
                                    itemId
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[StreamCache] Background merge failed for {ItemId}", itemId);
                            }
                        });
                    }

                    return Ok(new SmartGetStreamsResponse
                    {
                        Success = true,
                        Message = comparison.HasNewStreams 
                            ? $"Returning {cachedStreams.Count} cached streams, updating {comparison.NewStreams.Count} new in background"
                            : "Returning cached streams (up to date)",
                        Streams = cachedStreams,
                        FromCache = true,
                        HasNewStreams = comparison.HasNewStreams,
                        NewStreamsCount = comparison.NewStreams.Count
                    });
                }

                // 5. No cache, save fresh streams and return them
                _logger.LogInformation("[StreamCache] No cache found, saving {Count} fresh streams", freshStreams.Count);
                
                await _dbService.SaveStreamsAsync(
                    itemId,
                    stremioId,
                    imdbId,
                    tmdbId,
                    itemType ?? "unknown",
                    userId,
                    freshStreams
                );

                return Ok(new SmartGetStreamsResponse
                {
                    Success = true,
                    Message = "Fetched and cached new streams",
                    Streams = freshStreams,
                    FromCache = false,
                    HasNewStreams = false,
                    NewStreamsCount = 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error in smart get streams for {ItemId}", itemId);
                return StatusCode(500, new SmartGetStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Streams = new List<StreamInfo>(),
                    FromCache = false,
                    HasNewStreams = false,
                    NewStreamsCount = 0
                });
            }
        }

        /// <summary>
        /// Compare streams and return diff without saving
        /// POST /api/baklava/streams/{itemId}/compare
        /// </summary>
        [HttpPost("{itemId}/compare")]
        public async Task<ActionResult<CompareStreamsResponse>> CompareStreams(
            string itemId,
            [FromBody] CacheStreamsRequest request,
            [FromQuery] string? userId = null)
        {
            _logger.LogInformation("[StreamCache] Comparing streams for item {ItemId}", itemId);

            try
            {
                // Get cached streams
                var cachedStreams = await _dbService.GetStreamsAsync(itemId, userId);

                // Fetch fresh streams
                var freshStreams = await FetchStreamsFromGelato(request);

                // Compare
                var comparison = _dbService.CompareStreams(cachedStreams, freshStreams);

                return Ok(new CompareStreamsResponse
                {
                    Success = true,
                    Message = $"Comparison complete: {comparison.TotalCached} cached, {comparison.TotalNew} new",
                    TotalCached = comparison.TotalCached,
                    TotalFresh = comparison.TotalFresh,
                    TotalNew = comparison.TotalNew,
                    HasNewStreams = comparison.HasNewStreams,
                    NewStreams = comparison.NewStreams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error comparing streams for {ItemId}", itemId);
                return StatusCode(500, new CompareStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    TotalCached = 0,
                    TotalFresh = 0,
                    TotalNew = 0,
                    HasNewStreams = false,
                    NewStreams = new List<StreamInfo>()
                });
            }
        }

        /// <summary>
        /// Save probed media streams (audio and subtitles) for an item
        /// POST /api/baklava/streams/probed
        /// </summary>
        [HttpPost("probed")]
        public async Task<ActionResult<SaveProbedStreamsResponse>> SaveProbedStreams([FromBody] SaveProbedStreamsRequest request)
        {
            if (string.IsNullOrEmpty(request.ItemId))
            {
                return BadRequest("ItemId is required");
            }

            if (request.Streams == null || request.Streams.Count == 0)
            {
                return BadRequest("At least one stream is required");
            }

            _logger.LogInformation("[StreamCache] Saving {Count} probed streams for item {ItemId}", 
                request.Streams.Count, request.ItemId);

            try
            {
                var success = await _dbService.SaveProbedStreamsAsync(
                    request.ItemId,
                    request.StremioId,
                    request.StreamSourceId,
                    request.Streams
                );

                return Ok(new SaveProbedStreamsResponse
                {
                    Success = success,
                    Message = success ? "Probed streams saved successfully" : "Failed to save probed streams",
                    StreamCount = request.Streams.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error saving probed streams for {ItemId}", request.ItemId);
                return StatusCode(500, new SaveProbedStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    StreamCount = 0
                });
            }
        }

        /// <summary>
        /// Get cached probed streams for an item
        /// GET /api/baklava/streams/probed/{itemId}
        /// </summary>
        [HttpGet("probed/{itemId}")]
        public async Task<ActionResult<GetProbedStreamsResponse>> GetProbedStreams(
            string itemId, 
            [FromQuery] string? streamSourceId = null)
        {
            _logger.LogInformation("[StreamCache] Getting probed streams for item {ItemId}", itemId);

            try
            {
                var streams = await _dbService.GetProbedStreamsAsync(itemId, streamSourceId);

                if (streams == null || streams.Count == 0)
                {
                    return NotFound(new GetProbedStreamsResponse
                    {
                        Success = false,
                        Message = "No cached probed streams found",
                        Streams = new List<ProbedStreamInfo>()
                    });
                }

                return Ok(new GetProbedStreamsResponse
                {
                    Success = true,
                    Message = "Probed streams retrieved successfully",
                    Streams = streams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error getting probed streams for {ItemId}", itemId);
                return StatusCode(500, new GetProbedStreamsResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Streams = new List<ProbedStreamInfo>()
                });
            }
        }

        /// <summary>
        /// Fetch streams from Gelato's API
        /// </summary>
        private async Task<List<StreamInfo>> FetchStreamsFromGelato(CacheStreamsRequest request)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || string.IsNullOrEmpty(cfg.GelatoBaseUrl))
            {
                _logger.LogError("[StreamCache] Gelato base URL not configured");
                throw new InvalidOperationException("Gelato base URL not configured in Baklava settings");
            }

            var gelatoUrl = cfg.GelatoBaseUrl.TrimEnd('/');
            
            // Build Stremio ID (e.g., "tt1234567" for movies, "tt1234567:1:1" for episodes)
            var stremioId = request.StremioId;
            if (string.IsNullOrEmpty(stremioId))
            {
                // Try to construct from ImdbId or TmdbId
                stremioId = request.ImdbId ?? $"tmdb:{request.TmdbId}";
            }

            // Determine media type
            var mediaType = request.ItemType?.ToLower() ?? "movie";
            if (mediaType == "episode")
            {
                mediaType = "series"; // Gelato uses "series" for episode streams
            }

            // Call Gelato's stream endpoint
            // Format: /gelato/stream/{type}/{id}
            var url = $"{gelatoUrl}/gelato/stream/{mediaType}/{stremioId}";

            _logger.LogInformation("[StreamCache] Fetching streams from Gelato: {Url}", url);

            using var httpClient = _httpClientFactory.CreateClient();
            
            // Add auth header if configured
            if (!string.IsNullOrEmpty(cfg.GelatoAuthHeader))
            {
                if (cfg.GelatoAuthHeader.Contains(":"))
                {
                    // Full header format "HeaderName: Value"
                    var parts = cfg.GelatoAuthHeader.Split(':', 2);
                    httpClient.DefaultRequestHeaders.Add(parts[0].Trim(), parts[1].Trim());
                }
                else
                {
                    // Bare token - use as Authorization header
                    httpClient.DefaultRequestHeaders.Add("Authorization", cfg.GelatoAuthHeader);
                }
            }

            var response = await httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StreamCache] Gelato request failed: {StatusCode}", response.StatusCode);
                throw new HttpRequestException($"Gelato returned {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var gelatoResponse = JsonSerializer.Deserialize<GelatoStreamsResponse>(content, JsonOptions);

            // Convert Gelato streams to our StreamInfo format
            var streamInfos = new List<StreamInfo>();
            
            if (gelatoResponse?.Streams != null)
            {
                foreach (var stream in gelatoResponse.Streams)
                {
                    var isWebCompatible = IsAudioWebCompatible(stream.Audio);
                    
                    streamInfos.Add(new StreamInfo
                    {
                        StreamUrl = stream.Url,
                        InfoHash = stream.InfoHash,
                        FileIdx = stream.FileIdx,
                        Title = stream.Title,
                        Name = stream.Name,
                        Quality = stream.Quality,
                        Subtitle = stream.Subtitle,
                        Audio = stream.Audio,
                        BingeGroup = stream.BehaviorHints?.BingeGroup,
                        Filename = stream.BehaviorHints?.Filename,
                        VideoSize = stream.BehaviorHints?.VideoSize,
                        VideoHash = stream.BehaviorHints?.VideoHash,
                        Sources = stream.Sources != null && stream.Sources.Count > 0 
                            ? string.Join(",", stream.Sources) 
                            : null,
                        WebCompatible = isWebCompatible
                    });
                    
                    _logger.LogInformation(
                        "[StreamCache] Stream '{Title}' - Audio: '{Audio}' - WebCompatible: {Compatible}",
                        stream.Title ?? stream.Name,
                        stream.Audio ?? "unknown",
                        isWebCompatible
                    );
                }
            }

            _logger.LogInformation("[StreamCache] Fetched {Count} streams from Gelato", streamInfos.Count);
            return streamInfos;
        }

        /// <summary>
        /// Fetch streams DIRECTLY from Gelato's internal StremioProvider
        /// </summary>
        private async Task<List<StreamInfo>> FetchStreamsFromGelatoProvider(CacheStreamsRequest request)
        {
            // Try to get Gelato plugin directly from its static Instance property
            var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Gelato");

            if (gelatoAssembly == null)
            {
                _logger.LogError("[StreamCache] Gelato assembly not found");
                throw new InvalidOperationException("Gelato assembly not found");
            }

            var gelatoPluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
            if (gelatoPluginType == null)
            {
                _logger.LogError("[StreamCache] GelatoPlugin type not found");
                throw new InvalidOperationException("GelatoPlugin type not found");
            }

            var gelatoInstanceProperty = gelatoPluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var gelatoInstance = gelatoInstanceProperty?.GetValue(null);

            if (gelatoInstance == null)
            {
                _logger.LogError("[StreamCache] Gelato instance not found");
                throw new InvalidOperationException("Gelato instance not found");
            }

            // Get user ID (use request.UserId or default)
            var userId = Guid.Empty;
            if (!string.IsNullOrEmpty(request.UserId))
            {
                Guid.TryParse(request.UserId, out userId);
            }

            // Get config method
            var getConfigMethod = gelatoInstance.GetType().GetMethod("GetConfig");
            if (getConfigMethod == null)
            {
                _logger.LogError("[StreamCache] GetConfig method not found");
                throw new InvalidOperationException("GetConfig method not found in Gelato");
            }

            var config = getConfigMethod.Invoke(gelatoInstance, new object[] { userId });
            if (config == null)
            {
                _logger.LogError("[StreamCache] Gelato config is null");
                throw new InvalidOperationException("Gelato config is null");
            }

            _logger.LogInformation("[StreamCache] Got Gelato config, checking for stremio field");

            // Get stremio provider from config (it's a field, not a property)
            var stremioField = config.GetType().GetField("stremio", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (stremioField == null)
            {
                _logger.LogError("[StreamCache] Stremio field not found in config");
                throw new InvalidOperationException("Stremio field not found");
            }

            var stremioProvider = stremioField.GetValue(config);

            if (stremioProvider == null)
            {
                _logger.LogError("[StreamCache] Stremio provider is null in config");
                throw new InvalidOperationException("Stremio provider not found");
            }

            _logger.LogInformation("[StreamCache] Got stremio provider: {Type}", stremioProvider.GetType().Name);

            // Build Stremio ID
            var stremioId = request.StremioId ?? request.ImdbId ?? $"tmdb:{request.TmdbId}";
            var mediaType = request.ItemType?.ToLower() == "episode" ? "series" : "movie";

            _logger.LogInformation("[StreamCache] Fetching streams from Gelato provider for {MediaType}/{Id}", mediaType, stremioId);

            // Determine StremioMediaType enum value
            var stremioMediaTypeEnum = gelatoAssembly.GetType("Gelato.StremioMediaType");
            if (stremioMediaTypeEnum == null)
            {
                _logger.LogError("[StreamCache] StremioMediaType enum not found");
                throw new InvalidOperationException("StremioMediaType enum not found");
            }

            var mediaTypeValue = mediaType == "movie" 
                ? Enum.Parse(stremioMediaTypeEnum, "Movie") 
                : Enum.Parse(stremioMediaTypeEnum, "Series");

            // Call GetStreamsAsync(string id, StremioMediaType mediaType)
            var getStreamsMethod = stremioProvider.GetType().GetMethod("GetStreamsAsync", 
                new[] { typeof(string), stremioMediaTypeEnum });
            if (getStreamsMethod == null)
            {
                _logger.LogError("[StreamCache] GetStreamsAsync method not found");
                throw new InvalidOperationException("GetStreamsAsync method not found");
            }

            // Invoke GetStreamsAsync
            var streamsTask = (System.Threading.Tasks.Task)getStreamsMethod.Invoke(stremioProvider, new object[] { stremioId, mediaTypeValue })!;
            await streamsTask.ConfigureAwait(false);

            var resultProperty = streamsTask.GetType().GetProperty("Result");
            var streams = resultProperty?.GetValue(streamsTask) as System.Collections.IList;

            if (streams == null || streams.Count == 0)
            {
                _logger.LogWarning("[StreamCache] No streams returned from Gelato provider");
                return new List<StreamInfo>();
            }

            _logger.LogInformation("[StreamCache] Got {Count} streams from Gelato provider", streams.Count);

            // Convert to StreamInfo list
            var streamInfos = new List<StreamInfo>();

            foreach (var stream in streams)
            {
                var streamType = stream.GetType();
                
                var title = streamType.GetProperty("Title")?.GetValue(stream) as string;
                var name = streamType.GetProperty("Name")?.GetValue(stream) as string;
                var url = streamType.GetProperty("Url")?.GetValue(stream) as string;
                var infoHash = streamType.GetProperty("InfoHash")?.GetValue(stream) as string;
                var fileIdx = streamType.GetProperty("FileIdx")?.GetValue(stream) as int?;
                var quality = streamType.GetProperty("Quality")?.GetValue(stream) as string;
                var subtitle = streamType.GetProperty("Subtitle")?.GetValue(stream) as string;
                var audio = streamType.GetProperty("Audio")?.GetValue(stream) as string;
                var sources = streamType.GetProperty("Sources")?.GetValue(stream) as System.Collections.Generic.List<string>;
                
                var behaviorHints = streamType.GetProperty("BehaviorHints")?.GetValue(stream);
                string? bingeGroup = null;
                string? filename = null;
                long? videoSize = null;
                string? videoHash = null;

                if (behaviorHints != null)
                {
                    var hintsType = behaviorHints.GetType();
                    bingeGroup = hintsType.GetProperty("BingeGroup")?.GetValue(behaviorHints) as string;
                    filename = hintsType.GetProperty("Filename")?.GetValue(behaviorHints) as string;
                    videoSize = hintsType.GetProperty("VideoSize")?.GetValue(behaviorHints) as long?;
                    videoHash = hintsType.GetProperty("VideoHash")?.GetValue(behaviorHints) as string;
                }

                // Check web compatibility
                var isWebCompatible = IsAudioWebCompatible(audio);

                streamInfos.Add(new StreamInfo
                {
                    StreamUrl = url,
                    InfoHash = infoHash,
                    FileIdx = fileIdx,
                    Title = title,
                    Name = name,
                    Quality = quality,
                    Subtitle = subtitle,
                    Audio = audio,
                    BingeGroup = bingeGroup,
                    Filename = filename,
                    VideoSize = videoSize,
                    VideoHash = videoHash,
                    Sources = sources != null && sources.Count > 0 ? string.Join(",", sources) : null,
                    WebCompatible = isWebCompatible
                });

                _logger.LogInformation(
                    "[StreamCache] Stream '{Title}' - Audio: '{Audio}' - WebCompatible: {Compatible}",
                    title ?? name ?? "unknown",
                    audio ?? "unknown",
                    isWebCompatible
                );
            }

            return streamInfos;
        }

        #region DTOs

        public class CacheStreamsRequest
        {
            public string ItemId { get; set; } = string.Empty;
            public string? StremioId { get; set; }
            public string? ImdbId { get; set; }
            public string? TmdbId { get; set; }
            public string? ItemType { get; set; } // movie, episode, series
            public string? UserId { get; set; }
        }

        public class CacheStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int StreamCount { get; set; }
        }

        public class GetStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<StreamInfo> Streams { get; set; } = new();
        }

        public class CleanupResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int DeletedCount { get; set; }
        }

        // Gelato API response models
        public class GelatoStreamsResponse
        {
            public List<GelatoStream>? Streams { get; set; }
        }

        public class GelatoStream
        {
            public string? Url { get; set; }
            public string? Title { get; set; }
            public string? Name { get; set; }
            public string? Quality { get; set; }
            public string? Subtitle { get; set; }
            public string? Audio { get; set; }
            public string? InfoHash { get; set; }
            public int? FileIdx { get; set; }
            public List<string>? Sources { get; set; }
            public GelatoBehaviorHints? BehaviorHints { get; set; }
        }

        public class GelatoBehaviorHints
        {
            public string? BingeGroup { get; set; }
            public string? VideoHash { get; set; }
            public long? VideoSize { get; set; }
            public string? Filename { get; set; }
        }

        public class SaveProbedStreamsRequest
        {
            public string ItemId { get; set; } = string.Empty;
            public string? StremioId { get; set; }
            public string? StreamSourceId { get; set; }
            public List<ProbedStreamInfo> Streams { get; set; } = new();
        }

        public class SaveProbedStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int StreamCount { get; set; }
        }

        public class GetProbedStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<ProbedStreamInfo> Streams { get; set; } = new();
        }

        public class SmartGetStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<StreamInfo> Streams { get; set; } = new();
            public bool FromCache { get; set; }
            public bool HasNewStreams { get; set; }
            public int NewStreamsCount { get; set; }
        }

        public class CompareStreamsResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int TotalCached { get; set; }
            public int TotalFresh { get; set; }
            public int TotalNew { get; set; }
            public bool HasNewStreams { get; set; }
            public List<StreamInfo> NewStreams { get; set; } = new();
        }

        public class DeleteCacheResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int DeletedCount { get; set; }
        }

        #endregion

        #region Manual Cache Deletion

        /// <summary>
        /// Delete all cached streams for a specific item
        /// DELETE /api/baklava/streams/{itemId}
        /// </summary>
        [HttpDelete("{itemId}")]
        public async Task<ActionResult<DeleteCacheResponse>> DeleteItemStreams(string itemId)
        {
            try
            {
                _dbService.EnsureConnection();
                using var cmd = _dbService._connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM Streams WHERE ItemId = @itemId";
                cmd.Parameters.AddWithValue("@itemId", itemId);
                
                var deletedCount = await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("[StreamCache] Deleted {Count} streams for item {ItemId}", deletedCount, itemId);
                
                return Ok(new DeleteCacheResponse
                {
                    Success = true,
                    Message = $"Deleted {deletedCount} cached streams",
                    DeletedCount = deletedCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error deleting streams for {ItemId}", itemId);
                return StatusCode(500, new DeleteCacheResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        /// <summary>
        /// Delete all cached probed streams for a specific item
        /// DELETE /api/baklava/streams/probed/{itemId}
        /// </summary>
        [HttpDelete("probed/{itemId}")]
        public async Task<ActionResult<DeleteCacheResponse>> DeleteItemProbedStreams(string itemId)
        {
            try
            {
                _dbService.EnsureConnection();
                using var cmd = _dbService._connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM ProbedStreams WHERE ItemId = @itemId";
                cmd.Parameters.AddWithValue("@itemId", itemId);
                
                var deletedCount = await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("[StreamCache] Deleted {Count} probed streams for item {ItemId}", deletedCount, itemId);
                
                return Ok(new DeleteCacheResponse
                {
                    Success = true,
                    Message = $"Deleted {deletedCount} cached probed streams",
                    DeletedCount = deletedCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error deleting probed streams for {ItemId}", itemId);
                return StatusCode(500, new DeleteCacheResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        /// <summary>
        /// Delete ALL cached streams (admin only)
        /// DELETE /api/baklava/streams/all
        /// </summary>
        [HttpDelete("all")]
        public async Task<ActionResult<DeleteCacheResponse>> DeleteAllStreams()
        {
            try
            {
                _dbService.EnsureConnection();
                
                // Delete all streams
                using var cmd1 = _dbService._connection!.CreateCommand();
                cmd1.CommandText = "DELETE FROM Streams";
                var deletedStreams = await cmd1.ExecuteNonQueryAsync();
                
                // Delete all probed streams
                using var cmd2 = _dbService._connection!.CreateCommand();
                cmd2.CommandText = "DELETE FROM ProbedStreams";
                var deletedProbed = await cmd2.ExecuteNonQueryAsync();
                
                var totalDeleted = deletedStreams + deletedProbed;
                
                _logger.LogWarning("[StreamCache] Deleted ALL cache: {StreamCount} streams, {ProbedCount} probed streams", 
                    deletedStreams, deletedProbed);
                
                return Ok(new DeleteCacheResponse
                {
                    Success = true,
                    Message = $"Deleted {deletedStreams} streams and {deletedProbed} probed streams",
                    DeletedCount = totalDeleted
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamCache] Error deleting all cache");
                return StatusCode(500, new DeleteCacheResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        #endregion
    }
}
