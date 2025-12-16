using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Baklava.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Baklava.Services
{
    public class BaklavaMediaSourceManagerDecorator : IMediaSourceManager
    {
        private readonly IMediaSourceManager _inner;
        private readonly ILogger<BaklavaMediaSourceManagerDecorator> _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IHttpClientFactory _httpClientFactory;

        public BaklavaMediaSourceManagerDecorator(
            IMediaSourceManager inner,
            ILogger<BaklavaMediaSourceManagerDecorator> logger,
            IApplicationPaths appPaths,
            IHttpClientFactory httpClientFactory)
        {
            _inner = inner;
            _logger = logger;
            _appPaths = appPaths;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken ct)
        {
            // Call inner manager (which likely includes Gelato)
            var sources = await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct);

            // Fetch External Subtitles logic
            try 
            {
                if (item is not Video) return sources;

                // Check User Config via Baklava/Gelato logic
                // If Gelato is present, we use Baklava helper to find config.
                // Assuming Baklava logic: fetch and append.
                
                // Get User ID
                var userId = user?.Id ?? Guid.Empty;
                
                // Verify if we should fetch (Config check in Helper or here?)
                // Helper GetGelatoStremioUrl checks generic Gelato.xml. 
                // We assume if configured, we want subs.
                var stremioUrl = SubtitlesHelper.GetGelatoStremioUrl(_appPaths, userId);
                if (string.IsNullOrEmpty(stremioUrl)) return sources;

                // For each source? Usually sources[0] is the main one.
                // Subtitles attach to the MediaSource.
                foreach (var source in sources)
                {
                    if (source.Protocol != MediaProtocol.Http && source.Protocol != MediaProtocol.File) continue;
                    
                    // Fetch subtitles
                    var externalSubs = await FetchStremioSubtitlesAsync(item, source, stremioUrl, ct);
                    if (externalSubs != null && externalSubs.Any())
                    {
                        var streams = source.MediaStreams?.ToList() ?? new List<MediaStream>();
                        
                        // Remove existing bad external subs? (Optional, if Gelato added them poorly)
                        // Heuristic: Remove external subs with "Unknown" language or duplicates?
                        // For now, let's just Append. The client can choose.
                        
                        var index = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;
                        if (index < 0) index = 0; // handle -1

                        foreach (var sub in externalSubs)
                        {
                            sub.Index = index++;
                            streams.Add(sub);
                        }
                        
                        source.MediaStreams = streams;
                        _logger.LogInformation("[Baklava] Injected {Count} external subtitles for {Item} (User: {User})", externalSubs.Count, item.Name, user?.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Error injecting external subtitles");
            }

            return sources;
        }

        private async Task<List<MediaStream>> FetchStremioSubtitlesAsync(BaseItem item, MediaSourceInfo source, string baseUrl, CancellationToken ct)
        {
            try
            {
                string id = "";
                string type = "movie";

                var imdb = item.GetProviderId("Imdb");
                var tmdb = item.GetProviderId("Tmdb");

                if (item is MediaBrowser.Controller.Entities.TV.Episode ep)
                {
                    type = "series";
                    var seriesImdb = ep.Series?.GetProviderId("Imdb");
                    if (!string.IsNullOrEmpty(seriesImdb))
                    {
                         id = $"{seriesImdb}:{ep.ParentIndexNumber ?? 1}:{ep.IndexNumber ?? 1}";
                    }
                    else if (!string.IsNullOrEmpty(imdb))
                    {
                         // Fallback if episode has imdb
                         id = imdb; 
                    }
                }
                else
                {
                    id = imdb;
                }

                // If no IMDB, fallback to TMDB format if Stremio supports it?
                // Stremio/Cinemeta usually needs IMDb. 
                if (string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(tmdb)) id = $"tmdb:{tmdb}"; 

                if (string.IsNullOrEmpty(id)) return null;

                // Video Hash logic (if available)? 
                // Gelato does: Uri u = new Uri(source.Path); string filename = ...
                // Add filename to URL: /subtitles/{type}/{id}/{videoHash}.json
                // Assuming standard Stremio addon: /subtitles/{type}/{id}.json 
                // OR /subtitles/{type}/{id}/{extra}.json

                // SubtitlesHelper logic (from MetadataController):
                // url = $"{baseUrl}/subtitles/{type}/{id}.json";
                // But Gelato logic uses `filename`? 
                
                // Let's use the MetadataController logic:
                // "/subtitles/{type}/{id}.json"
                // But appending ".json" might need extra param if video hash is required.
                // OpenSubtitles V3 addon usually requires video hash/filename.
                // But standard Cinemeta-based subs might not.
                // Let's emulate MetadataController's successful logic.
                
                // Construct URL
                // If baseUrl ends with manifest.json, stripped.
                var url = $"{baseUrl}/subtitles/{type}/{id}.json";

                // Check for filename param if addon supports it (some do via path)
                // Using just ID is safest common denominator.
                
                using var client = _httpClientFactory.CreateClient();
                var resp = await client.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = await JsonSerializer.DeserializeAsync<StremioSubtitleResponse>(stream, options, ct);
                
                if (result?.Subtitles == null) return null;

                var list = new List<MediaStream>();
                foreach (var s in result.Subtitles)
                {
                    string langName = s.Lang ?? "Unknown";
                    try { 
                        if (!string.IsNullOrEmpty(s.Lang)) 
                            langName = new CultureInfo(s.Lang).DisplayName; 
                    } catch { }

                    string title = $"[EXTERNAL] {langName}" + (!string.IsNullOrEmpty(s.id) ? $" - {s.id}" : "");

                    list.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Title = title,
                        DisplayTitle = title,
                        Language = s.Lang ?? "und",
                        Codec = "srt", // Assumption
                        IsExternal = true,
                        SupportsExternalStream = true, 
                        Path = s.Url, 
                        DeliveryMethod = SubtitleDeliveryMethod.External,
                        DeliveryUrl = s.Url
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Baklava] Failed to fetch stremio subs: {Ex}", ex.Message);
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


        // Forwarding Methods
        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null) 
            => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) 
            => _inner.GetMediaStreams(itemId);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) 
            => _inner.GetMediaAttachments(itemId);

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) 
            => _inner.OpenLiveStream(request, cancellationToken);
            
        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
            => _inner.OpenLiveStreamInternal(request, cancellationToken);

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStream(id, cancellationToken);
            
        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
            
        public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);
        
        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);
        
        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
            => _inner.GetRecordingStreamMediaSources(info, cancellationToken);
            
        public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);
        
        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStreamMediaInfo(id, cancellationToken);
            
        public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);
        
        public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);
        
        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
            => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
            
        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
            => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
            
        public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

        public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string? liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
            => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);
        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);
    }
}
