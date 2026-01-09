#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baklava.Services;
using Microsoft.Extensions.Logging;

namespace Baklava.Helpers
{
    /// <summary>
    /// Helper class to integrate Baklava stream caching with Gelato
    /// This allows automatic caching of streams when Gelato fetches them
    /// </summary>
    public class GelatoStreamCacheHelper
    {
        private readonly ILogger<GelatoStreamCacheHelper> _logger;
        private readonly BaklavaDbService _dbService;

        public GelatoStreamCacheHelper(
            ILogger<GelatoStreamCacheHelper> logger,
            BaklavaDbService dbService)
        {
            _logger = logger;
            _dbService = dbService;
        }

        /// <summary>
        /// Cache streams from Gelato format to Baklava database
        /// This should be called after Gelato's SyncStreams
        /// </summary>
        public async Task<bool> CacheGelatoStreamsAsync(
            string itemId,
            string? stremioId,
            string? imdbId,
            string? tmdbId,
            string itemType,
            string? userId,
            List<GelatoStreamData> gelatoStreams)
        {
            try
            {
                if (gelatoStreams == null || gelatoStreams.Count == 0)
                {
                    _logger.LogDebug("[GelatoStreamCache] No streams to cache for {ItemId}", itemId);
                    return false;
                }

                // Convert Gelato stream format to Baklava StreamInfo format
                var streamInfos = gelatoStreams.Select(gs => new StreamInfo
                {
                    StreamUrl = gs.Url,
                    InfoHash = gs.InfoHash,
                    FileIdx = gs.FileIdx,
                    Title = gs.Title,
                    Name = gs.Name,
                    Quality = gs.Quality,
                    Subtitle = gs.Subtitle,
                    Audio = gs.Audio,
                    BingeGroup = gs.BingeGroup,
                    Filename = gs.Filename,
                    VideoSize = gs.VideoSize,
                    VideoHash = gs.VideoHash,
                    Sources = gs.Sources != null && gs.Sources.Count > 0 
                        ? string.Join(",", gs.Sources) 
                        : null
                }).ToList();

                var success = await _dbService.SaveStreamsAsync(
                    itemId,
                    stremioId,
                    imdbId,
                    tmdbId,
                    itemType,
                    userId,
                    streamInfos
                );

                if (success)
                {
                    _logger.LogInformation(
                        "[GelatoStreamCache] Successfully cached {Count} streams for item {ItemId}",
                        streamInfos.Count,
                        itemId
                    );
                }
                else
                {
                    _logger.LogWarning(
                        "[GelatoStreamCache] Failed to cache streams for item {ItemId}",
                        itemId
                    );
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GelatoStreamCache] Error caching streams for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Retrieve cached streams from Baklava database
        /// This can be used as a fallback when Jellyfin's cache expires
        /// </summary>
        public async Task<List<GelatoStreamData>?> GetCachedStreamsAsync(
            string itemId,
            string? userId = null)
        {
            try
            {
                var streamInfos = await _dbService.GetStreamsAsync(itemId, userId);

                if (streamInfos == null || streamInfos.Count == 0)
                {
                    _logger.LogDebug("[GelatoStreamCache] No cached streams found for {ItemId}", itemId);
                    return null;
                }

                // Convert Baklava StreamInfo format back to Gelato format
                var gelatoStreams = streamInfos.Select(si => new GelatoStreamData
                {
                    Url = si.StreamUrl,
                    InfoHash = si.InfoHash,
                    FileIdx = si.FileIdx,
                    Title = si.Title,
                    Name = si.Name,
                    Quality = si.Quality,
                    Subtitle = si.Subtitle,
                    Audio = si.Audio,
                    BingeGroup = si.BingeGroup,
                    Filename = si.Filename,
                    VideoSize = si.VideoSize,
                    VideoHash = si.VideoHash,
                    Sources = !string.IsNullOrEmpty(si.Sources) 
                        ? si.Sources.Split(',').ToList() 
                        : null
                }).ToList();

                _logger.LogInformation(
                    "[GelatoStreamCache] Retrieved {Count} cached streams for item {ItemId}",
                    gelatoStreams.Count,
                    itemId
                );

                return gelatoStreams;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GelatoStreamCache] Error retrieving cached streams for {ItemId}", itemId);
                return null;
            }
        }
    }

    /// <summary>
    /// Gelato stream data structure
    /// Mirrors the structure used by Gelato plugin
    /// </summary>
    public class GelatoStreamData
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
        public string? BingeGroup { get; set; }
        public string? VideoHash { get; set; }
        public long? VideoSize { get; set; }
        public string? Filename { get; set; }
    }
}
