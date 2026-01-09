using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baklava.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/webcompat")]
    public class WebCompatController : ControllerBase
    {
        private readonly BaklavaDbService _dbService;
        private readonly ILogger<WebCompatController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;

        // Web-compatible audio codecs
        private static readonly HashSet<string> WebAudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aac", "mp4a",
            "opus",
            "vorbis",
            "mp3", "mpeg"
        };

        // Incompatible audio codecs that require transcoding
        private static readonly HashSet<string> IncompatibleAudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ac3", "eac3", "dolby", "dolbydigital",
            "truehd", "dts", "dts-hd", "dtshd",
            "atmos"
        };

        public WebCompatController(
            BaklavaDbService dbService, 
            ILogger<WebCompatController> logger,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager)
        {
            _dbService = dbService;
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
        }

        /// <summary>
        /// Get web-compatible MediaSources (versions) for an item
        /// GET /api/baklava/webcompat/versions/{itemId}
        /// </summary>
        [HttpGet("versions/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetWebCompatibleVersions(string itemId, [FromQuery] bool filterWebOnly = false)
        {
            try
            {
                _logger.LogInformation("[Baklava] GetWebCompatibleVersions: ItemId={ItemId}, FilterWebOnly={FilterWebOnly}", itemId, filterWebOnly);

                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    return BadRequest(new { error = "Invalid itemId" });
                }

                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                // Get all MediaSources from Jellyfin (includes Gelato sources)
                var mediaSourceResult = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, true, CancellationToken.None);
                var allSources = mediaSourceResult.ToList();

                _logger.LogInformation("[Baklava] Got {Count} total MediaSources from Jellyfin", allSources.Count);

                if (!filterWebOnly)
                {
                    // Return all sources without filtering
                    return Ok(new { MediaSources = allSources });
                }

                // Get cached streams from database to check WebCompatible flags
                var cachedStreams = await _dbService.GetStreamsAsync(itemId);
                
                if (cachedStreams == null || cachedStreams.Count == 0)
                {
                    _logger.LogWarning("[Baklava] No cached streams found in DB, cannot filter by web compatibility. Returning all sources.");
                    return Ok(new { MediaSources = allSources });
                }

                // Create a lookup by stream URL/InfoHash to match MediaSources with cached streams
                var webCompatLookup = new Dictionary<string, bool>();
                foreach (var stream in cachedStreams)
                {
                    var key = !string.IsNullOrEmpty(stream.InfoHash) 
                        ? stream.InfoHash 
                        : stream.StreamUrl ?? "";
                    
                    if (!string.IsNullOrEmpty(key) && stream.WebCompatible.HasValue)
                    {
                        webCompatLookup[key] = stream.WebCompatible.Value;
                    }
                }

                // Filter MediaSources based on cached WebCompatible flags
                var compatibleSources = allSources.Where(source =>
                {
                    // Try to match by Path (which contains infoHash or URL)
                    var path = source.Path ?? "";
                    
                    // Extract infoHash from path if it's a torrent stream
                    // Format: http://127.0.0.1:port/gelato/stream?ih=INFOHASH&...
                    if (path.Contains("/gelato/stream?ih="))
                    {
                        var ihStart = path.IndexOf("ih=") + 3;
                        var ihEnd = path.IndexOf("&", ihStart);
                        if (ihEnd == -1) ihEnd = path.Length;
                        var infoHash = path.Substring(ihStart, ihEnd - ihStart);
                        
                        if (webCompatLookup.TryGetValue(infoHash, out var isCompatible))
                        {
                            _logger.LogDebug("[Baklava] MediaSource {Name} matched by InfoHash: {Compatible}", source.Name, isCompatible);
                            return isCompatible;
                        }
                    }
                    
                    // Try direct URL match
                    if (webCompatLookup.TryGetValue(path, out var isCompat))
                    {
                        _logger.LogDebug("[Baklava] MediaSource {Name} matched by URL: {Compatible}", source.Name, isCompat);
                        return isCompat;
                    }
                    
                    // No match found - exclude to be safe
                    _logger.LogDebug("[Baklava] MediaSource {Name} not found in cache, excluding", source.Name);
                    return false;
                }).ToList();

                _logger.LogInformation("[Baklava] Filtered to {Count}/{Total} web-compatible sources", compatibleSources.Count, allSources.Count);

                // If filtering leaves us with 0 sources, return all sources so transcoding is still possible
                if (compatibleSources.Count == 0 && allSources.Count > 0)
                {
                    _logger.LogWarning("[Baklava] No compatible sources found, returning all {Count} sources for transcoding fallback", allSources.Count);
                    return Ok(new { MediaSources = allSources });
                }

                return Ok(new { MediaSources = compatibleSources });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error getting web-compatible versions");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private bool HasWebCompatibleAudio(MediaSourceInfo source)
        {
            var audioStreams = source.MediaStreams?
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio)
                .ToList();

            if (audioStreams == null || !audioStreams.Any())
            {
                _logger.LogWarning("[Baklava] 🟢 Source {SourceId} has no audio streams", source.Id);
                return false;
            }

            foreach (var audio in audioStreams)
            {
                var codec = audio.Codec?.ToLowerInvariant() ?? "";
                
                _logger.LogDebug("[Baklava] 🟢 Checking audio: Codec={Codec}, Index={Index}", codec, audio.Index);

                // If codec contains incompatible strings, skip this audio track
                if (IncompatibleAudioCodecs.Any(bad => codec.Contains(bad)))
                {
                    continue; // This audio track is incompatible, check next one
                }

                // If codec contains compatible strings, this source is good
                if (WebAudioCodecs.Any(good => codec.Contains(good)))
                {
                    _logger.LogInformation("[Baklava] 🟢 Source {SourceId} has compatible audio: {Codec}", source.Id, codec);
                    return true;
                }
            }

            _logger.LogWarning("[Baklava] 🟢 Source {SourceId} has NO compatible audio tracks", source.Id);
            return false;
        }

        /// <summary>
        /// Analyze all cached streams for an item and update WebCompatible flags
        /// POST /api/baklava/webcompat/analyze/{itemId}
        /// </summary>
        [HttpPost("analyze/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> AnalyzeStreams(string itemId)
        {
            try
            {
                _logger.LogInformation("🟢 Analyzing web compatibility for ItemId: {ItemId}", itemId);

                _dbService.EnsureConnection();
                var conn = _dbService._connection;

                // Get all streams for this item with their IDs
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, ItemId, StremioId FROM Streams WHERE ItemId = @ItemId";
                cmd.Parameters.AddWithValue("@ItemId", itemId);

                var streamIds = new List<(int Id, string? StremioId)>();
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        streamIds.Add((
                            reader.GetInt32(0),
                            reader.IsDBNull(2) ? null : reader.GetString(2)
                        ));
                    }
                }

                if (!streamIds.Any())
                {
                    return NotFound(new { message = "No cached streams found for item" });
                }

                int compatible = 0;
                int incompatible = 0;

                foreach (var (streamId, stremioId) in streamIds)
                {
                    // Get probed streams for this stream
                    var probedStreams = await _dbService.GetProbedStreamsAsync(itemId, stremioId);
                    
                    bool isCompatible = true;

                    // Check audio streams for incompatible codecs
                    if (probedStreams != null)
                    {
                        var audioStreams = probedStreams.Where(ps => 
                            ps.StreamType?.Equals("Audio", StringComparison.OrdinalIgnoreCase) == true);

                        foreach (var audio in audioStreams)
                        {
                            var codec = audio.Codec?.ToLowerInvariant() ?? "";
                            
                            // Check if it contains incompatible codec
                            if (IncompatibleAudioCodecs.Any(bad => codec.Contains(bad)))
                            {
                                isCompatible = false;
                                break;
                            }
                        }
                    }

                    // Update database
                    await UpdateStreamCompatibility(streamId, isCompatible);
                    
                    if (isCompatible)
                        compatible++;
                    else
                        incompatible++;
                }

                _logger.LogInformation("🟢 Analysis complete: {Compatible} compatible, {Incompatible} incompatible", 
                    compatible, incompatible);

                return Ok(new
                {
                    itemId,
                    totalStreams = streamIds.Count,
                    compatible,
                    incompatible
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🟢 Error analyzing streams for ItemId: {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get only web-compatible cached streams for an item
        /// GET /api/baklava/webcompat/streams/{itemId}
        /// </summary>
        [HttpGet("streams/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetCompatibleStreams(string itemId)
        {
            try
            {
                _logger.LogInformation("🟢 Fetching web-compatible streams for ItemId: {ItemId}", itemId);

                _dbService.EnsureConnection();
                var conn = _dbService._connection;

                // Get all streams for this item
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, ItemId, StremioId, StreamUrl, Title, Name, Quality, WebCompatible
                    FROM Streams 
                    WHERE ItemId = @ItemId
                    ORDER BY Quality DESC";
                cmd.Parameters.AddWithValue("@ItemId", itemId);

                var allStreams = new List<(int Id, string ItemId, string? StremioId, string? StreamUrl, 
                    string? Title, string? Name, string? Quality, int? WebCompatible)>();
                
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        allStreams.Add((
                            reader.GetInt32(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            reader.IsDBNull(7) ? null : (int?)reader.GetInt64(7)
                        ));
                    }
                }
                
                _logger.LogInformation("🟢 Found {Count} total cached streams", allStreams.Count);
                
                if (!allStreams.Any())
                {
                    _logger.LogWarning("🟢 No cached streams found for ItemId: {ItemId}", itemId);
                    return NotFound(new { message = "No cached streams found. Please fetch streams first." });
                }

                var compatibleStreams = new List<object>();

                foreach (var stream in allStreams)
                {
                    int? webCompat = stream.WebCompatible;
                    
                    // If WebCompatible is null, analyze on-the-fly
                    if (webCompat == null)
                    {
                        var probedStreams = await _dbService.GetProbedStreamsAsync(itemId, stream.StremioId);
                        bool isCompatible = true;

                        if (probedStreams != null)
                        {
                            var audioStreams = probedStreams.Where(ps => 
                                ps.StreamType?.Equals("Audio", StringComparison.OrdinalIgnoreCase) == true);

                            foreach (var audio in audioStreams)
                            {
                                var codec = audio.Codec?.ToLowerInvariant() ?? "";
                                if (IncompatibleAudioCodecs.Any(bad => codec.Contains(bad)))
                                {
                                    isCompatible = false;
                                    break;
                                }
                            }
                        }

                        webCompat = isCompatible ? 1 : 0;
                        await UpdateStreamCompatibility(stream.Id, isCompatible);
                    }

                    // Add to results if compatible
                    if (webCompat == 1)
                    {
                        var probedStreams = await _dbService.GetProbedStreamsAsync(itemId, stream.StremioId);
                        var externalSubs = await _dbService.GetExternalSubtitlesAsync(itemId);

                        compatibleStreams.Add(new
                        {
                            Id = stream.Id,
                            ItemId = stream.ItemId,
                            StremioId = stream.StremioId,
                            StreamUrl = stream.StreamUrl,
                            Title = stream.Title,
                            Name = stream.Name,
                            Quality = stream.Quality,
                            WebCompatible = webCompat,
                            ProbedStreams = probedStreams?.Select(ps => new
                            {
                                ps.StreamType,
                                ps.Codec,
                                ps.Language,
                                ps.Title,
                                ps.Channels,
                                ps.SampleRate,
                                ps.BitRate
                            }),
                            ExternalSubtitles = externalSubs?.Select(es => new
                            {
                                DisplayName = es.Title,
                                es.Language,
                                es.Url
                            })
                        });
                    }
                }

                _logger.LogInformation("🟢 Found {Count} web-compatible streams", compatibleStreams.Count);

                return Ok(new
                {
                    itemId,
                    totalStreams = allStreams.Count,
                    compatibleCount = compatibleStreams.Count,
                    streams = compatibleStreams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🟢 Error fetching compatible streams for ItemId: {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private async Task UpdateStreamCompatibility(int streamId, bool isCompatible)
        {
            try
            {
                _dbService.EnsureConnection();
                var conn = _dbService._connection;

                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE Streams 
                    SET WebCompatible = @WebCompatible 
                    WHERE Id = @Id";
                cmd.Parameters.AddWithValue("@WebCompatible", isCompatible ? 1 : 0);
                cmd.Parameters.AddWithValue("@Id", streamId);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🟢 Failed to update WebCompatible flag for stream {StreamId}", streamId);
            }
        }
    }
}
