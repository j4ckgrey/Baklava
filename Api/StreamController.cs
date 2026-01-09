#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baklava.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/metadata")]
    [Produces("application/json")]
    public class StreamController : ControllerBase
    {
        private readonly ILogger<StreamController> _logger;
        private readonly StreamService _streamService;
        private readonly SubtitleService _subtitleService;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;

        public StreamController(
            ILogger<StreamController> logger,
            StreamService streamService,
            SubtitleService subtitleService,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager)
        {
            _logger = logger;
            _streamService = streamService;
            _subtitleService = subtitleService;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
        }

        [HttpGet("streams")]
        public async Task<ActionResult> GetMediaStreams(
            [FromQuery] string itemId,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? client) // "web" = filter out embedded subs
        {
            bool isWebClient = string.Equals(client, "web", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(itemId) || !Guid.TryParse(itemId, out var itemGuid)) return BadRequest(new { error = "Invalid itemId" });

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null) return NotFound(new { error = "Item not found" });

            // 1. Resolve Target Source
            bool explicitRequest = !string.IsNullOrEmpty(mediaSourceId);

            var mediaSourceResult = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, true, CancellationToken.None);
            var mediaSources = mediaSourceResult.ToList();
            if (mediaSources.Count == 0) return NotFound(new { error = "No media sources" });

            // FIX: Ensure Protocol is HTTP for URL paths
            foreach (var ms in mediaSources)
            {
               if (!string.IsNullOrEmpty(ms.Path) && 
                   (ms.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    ms.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                   ms.Protocol != MediaBrowser.Model.MediaInfo.MediaProtocol.Http)
               {
                   _logger.LogWarning("[Baklava.Stream] Fixing Protocol for {Id}. Was {Proto}, setting to Http. Path: {Path}", ms.Id, ms.Protocol, ms.Path);
                   ms.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
               }
            }
            
            var targetSource = explicitRequest 
                ? mediaSources.FirstOrDefault(ms => ms.Id == mediaSourceId) 
                : mediaSources.FirstOrDefault();
            if (targetSource == null) targetSource = mediaSources.First(); // Fallback

            _logger.LogInformation("[Baklava.Stream] GetMediaStreams: ItemId={ItemId}, MediaSourceId={SourceId}, Client={Client}", itemId, targetSource.Id, client ?? "native");

            // 2. Use existing MediaStreams from Jellyfin (no probing)
            var audioStreams = targetSource.MediaStreams
                .Where(s => s.Type == MediaStreamType.Audio)
                .Select(a => new { 
                    index = a.Index, 
                    title = BuildStreamTitle(a), 
                    language = (string?)a.Language, 
                    codec = (string?)a.Codec, 
                    channels = a.Channels, 
                    bitrate = (long?)a.BitRate 
                })
                .ToList();

            _logger.LogInformation("[Baklava.Stream] Found {Count} audio streams in MediaSource from manager.", audioStreams.Count);

            // 3. Get embedded subtitles from Jellyfin
            // For web clients: skip embedded subs - they can only use external Gelato subs or burn-in
            List<object> embeddedSubs;
            if (isWebClient)
            {
                _logger.LogInformation("[Baklava.Stream] Web client - skipping embedded subtitles (use external or burn-in)");
                embeddedSubs = new List<object>();
            }
            else
            {
                embeddedSubs = targetSource.MediaStreams
                    .Where(s => s.Type == MediaStreamType.Subtitle)
                    .Select(s => (object)new {
                        index = s.Index,
                        title = BuildStreamTitle(s),
                        language = (string?)s.Language,
                        codec = (string?)s.Codec,
                        isForced = s.IsForced,
                        isDefault = s.IsDefault,
                        isExternal = s.IsExternal,
                        url = !string.IsNullOrEmpty(s.DeliveryUrl) ? s.DeliveryUrl : (string?)null
                    })
                    .ToList();
                _logger.LogInformation("[Baklava.Stream] Found {Count} embedded subtitle streams.", embeddedSubs.Count);
            }

            // 4. Also fetch external subtitles from Gelato
            var externalSubs = await _subtitleService.FetchExternalSubtitlesAsync(itemId);
            
            var allSubs = new List<object>();
            allSubs.Add(new { index = -1, title = (string?)"None", language = (string?)null, codec = (string?)null, isForced = (bool?)false, isDefault = (bool?)false, isExternal = false, url = (string?)null });
            
            // Add embedded subs first (only for native clients)
            allSubs.AddRange(embeddedSubs);
            
            // Add external subs if available
            if (externalSubs != null && externalSubs.Count > 0)
            {
                var externalSubsFormatted = externalSubs.Select((sub, idx) => new {
                    index = 1000 + idx, // High index to not conflict with embedded
                    title = (string?)sub.Title,
                    language = (string?)sub.Language,
                    codec = (string?)"srt",
                    isForced = (bool?)false,
                    isDefault = (bool?)false,
                    isExternal = true,
                    url = $"/api/baklava/subtitles/proxy?itemId={itemId}&subId={sub.Id}"
                }).ToList();

                allSubs.AddRange(externalSubsFormatted.Cast<object>());
            }
            
            // If no embedded streams found, try probing if applicable (fallback logic)
            if (audioStreams.Count == 0 && embeddedSubs.Count == 0 && targetSource.SupportsProbing && targetSource.Protocol == MediaBrowser.Model.MediaInfo.MediaProtocol.Http)
            {
               _logger.LogWarning("[Baklava.Stream] No streams found in MediaSource. Attempting Backup Probing for {Url}", targetSource.Path);
               var url = targetSource.Path;
               if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
               {
                   var resolved = await _streamService.ResolveStremioStreamAsync(url);
                   if (!string.IsNullOrEmpty(resolved)) url = resolved;
               }

               if (url != null && !url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
               {
                   var probe = await _streamService.RunFfprobeAsync(url);
                   if (probe != null)
                   {
                       // Merge probe results if needed, but for now we follow the original logic which seemed to return 'data' object.
                       // The original code constructed 'data' but returned anonymous object.
                       // We'll stick to returning what we have + probe data if empty.
                       // Implementation note: The original code returned mixed sources.
                       // Here we only probed if empty.
                       
                       // For Audio
                       audioStreams.AddRange(probe.Audio.Select(a => new {
                           index = a.Index,
                           title = a.Title ?? $"Audio {a.Index}",
                           language = a.Language,
                           codec = a.Codec,
                           channels = a.Channels,
                           bitrate = a.Bitrate
                       }));

                       // For Subs (Embedded)
                       allSubs.AddRange(probe.Subtitles.Select(s => new {
                           index = s.Index,
                           title = s.Title ?? $"Subtitle {s.Index}",
                           language = s.Language,
                           codec = s.Codec,
                           isForced = (bool?)s.IsForced,
                           isDefault = (bool?)s.IsDefault,
                           isExternal = false,
                           url = (string?)null
                       }).Cast<object>());
                   }
               }
            }

            return Ok(new {
                audio = audioStreams,
                subs = allSubs,
                mediaSourceId = targetSource.Id,
                url = targetSource.Path
            });
        }

        private string BuildStreamTitle(MediaStream stream)
        {
            var title = !string.IsNullOrWhiteSpace(stream.DisplayTitle) ? stream.DisplayTitle : 
                        (!string.IsNullOrWhiteSpace(stream.Title) ? stream.Title : $"{stream.Type} {stream.Index}");
            
            if (!string.IsNullOrEmpty(stream.Language))
             title += $" ({GetLanguageDisplayName(stream.Language)})";
             
             return title;
        }

        private string GetLanguageDisplayName(string langCode)
        {
            try
            {
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
    }
}
