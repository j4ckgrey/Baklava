using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    // Support both the explicit plugin route and the legacy "myplugin" route
    // so the Jellyfin admin UI can find the configuration endpoint regardless
    // of how the client requests it.
    [Route("api/baklava/config")]
    [Produces("application/json")]
    public class ConfigController : ControllerBase
    {
        private readonly ILogger<ConfigController> _logger;

        public ConfigController(ILogger<ConfigController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public ActionResult<object> GetConfig()
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Configuration not available");

            // Only return the TMDB API key to administrators. Non-admin callers will receive
            // only non-sensitive configuration so we don't leak secrets to user-facing pages.
            var user = HttpContext.User;
            var isAdmin = user?.IsInRole("Administrator") ?? false;

            if (isAdmin)
            {
                return Ok(new { 
                    defaultTmdbId = cfg.DefaultTmdbId, 
                    tmdbApiKey = cfg.TmdbApiKey,
                    enableSearchFilter = cfg.EnableSearchFilter,
                    forceTVClientLocalSearch = cfg.ForceTVClientLocalSearch,
                    disableNonAdminRequests = cfg.DisableNonAdminRequests,
                    enableAutoImport = cfg.EnableAutoImport,
                    disableModal = cfg.DisableModal,
                    showReviewsCarousel = cfg.ShowReviewsCarousel,
                    versionUi = cfg.VersionUi,
                    audioUi = cfg.AudioUi,
                    subtitleUi = cfg.SubtitleUi,
                    debridService = cfg.DebridService,
                    debridApiKey = cfg.DebridApiKey,
                    realDebridApiKey = cfg.RealDebridApiKey,
                    torboxApiKey = cfg.TorBoxApiKey,
                    alldebridApiKey = cfg.AllDebridApiKey,
                    premiumizeApiKey = cfg.PremiumizeApiKey,
                    enableDebridMetadata = cfg.EnableDebridMetadata,
                    enableFallbackProbe = cfg.EnableFallbackProbe,
                    fetchCachedMetadataPerVersion = cfg.FetchCachedMetadataPerVersion,
                    fetchAllNonCachedMetadata = cfg.FetchAllNonCachedMetadata,
                    enableExternalSubtitles = cfg.EnableExternalSubtitles
                });
            }

            return Ok(new { 
                defaultTmdbId = cfg.DefaultTmdbId,
                disableNonAdminRequests = cfg.DisableNonAdminRequests,
                enableAutoImport = cfg.EnableAutoImport,
                disableModal = cfg.DisableModal,
                showReviewsCarousel = cfg.ShowReviewsCarousel,
                versionUi = cfg.VersionUi,
                audioUi = cfg.AudioUi,
                subtitleUi = cfg.SubtitleUi
            });
        }

        [HttpPut]
        [Authorize]
        public ActionResult SetConfig([FromBody] ConfigDto dto)
        {
            _logger.LogInformation("[ConfigController] PUT request received");
            
            // Basic admin check
            var user = HttpContext.User;
            var isAdmin = user?.IsInRole("Administrator") ?? false;
            
            _logger.LogInformation("[ConfigController] User admin check: {IsAdmin}, User: {User}", isAdmin, user?.Identity?.Name ?? "anonymous");
            
            if (!isAdmin)
            {
                _logger.LogWarning("[ConfigController] PUT rejected - user is not admin");
                return Forbid();
            }

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger.LogError("[ConfigController] Configuration not available");
                return BadRequest("Configuration not available");
            }

            _logger.LogInformation("[ConfigController] Updating config");

            cfg.DefaultTmdbId = dto?.defaultTmdbId?.Trim();
            cfg.TmdbApiKey = dto?.tmdbApiKey?.Trim();
            
            // Update search filter settings
            if (dto.enableSearchFilter.HasValue)
            {
                cfg.EnableSearchFilter = dto.enableSearchFilter.Value;
            }
            if (dto.forceTVClientLocalSearch.HasValue)
            {
                cfg.ForceTVClientLocalSearch = dto.forceTVClientLocalSearch.Value;
            }
            if (dto.disableNonAdminRequests.HasValue)
            {
                cfg.DisableNonAdminRequests = dto.disableNonAdminRequests.Value;
            }
            if (dto.enableAutoImport.HasValue)
            {
                cfg.EnableAutoImport = dto.enableAutoImport.Value;
            }
            if (dto.disableModal.HasValue)
            {
                cfg.DisableModal = dto.disableModal.Value;
            }
            if (!string.IsNullOrWhiteSpace(dto.versionUi))
            {
                cfg.VersionUi = dto.versionUi.Trim();
            }
            if (!string.IsNullOrWhiteSpace(dto.audioUi))
            {
                cfg.AudioUi = dto.audioUi.Trim();
            }
            if (!string.IsNullOrWhiteSpace(dto.subtitleUi))
            {
                cfg.SubtitleUi = dto.subtitleUi.Trim();
            }
            
            // Show/hide reviews carousel
            if (dto.showReviewsCarousel.HasValue)
            {
                cfg.ShowReviewsCarousel = dto.showReviewsCarousel.Value;
            }

            // Update Debrid settings
            if (!string.IsNullOrWhiteSpace(dto.debridService))
            {
                cfg.DebridService = dto.debridService.Trim();
            }
            if (dto.debridApiKey != null)
            {
                cfg.DebridApiKey = dto.debridApiKey.Trim();
            }
            if (dto.enableDebridMetadata.HasValue)
            {
                cfg.EnableDebridMetadata = dto.enableDebridMetadata.Value;
            }
            if (dto.enableFallbackProbe.HasValue)
            {
                cfg.EnableFallbackProbe = dto.enableFallbackProbe.Value;
            }
            if (dto.fetchCachedMetadataPerVersion.HasValue)
            {
                cfg.FetchCachedMetadataPerVersion = dto.fetchCachedMetadataPerVersion.Value;
            }
            if (dto.fetchAllNonCachedMetadata.HasValue)
            {
                cfg.FetchAllNonCachedMetadata = dto.fetchAllNonCachedMetadata.Value;
            }
            if (dto.enableExternalSubtitles.HasValue)
            {
                cfg.EnableExternalSubtitles = dto.enableExternalSubtitles.Value;
            }
            // Multi-debrid API keys
            if (dto.realDebridApiKey != null)
            {
                cfg.RealDebridApiKey = dto.realDebridApiKey.Trim();
            }
            if (dto.torboxApiKey != null)
            {
                cfg.TorBoxApiKey = dto.torboxApiKey.Trim();
            }
            if (dto.alldebridApiKey != null)
            {
                cfg.AllDebridApiKey = dto.alldebridApiKey.Trim();
            }
            if (dto.premiumizeApiKey != null)
            {
                cfg.PremiumizeApiKey = dto.premiumizeApiKey.Trim();
            }
            
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("[ConfigController] Configuration saved.");
            return Ok();
        }
    }

    public class ConfigDto
    {
        public string defaultTmdbId { get; set; }
        public string tmdbApiKey { get; set; }
        public bool? enableSearchFilter { get; set; }
        public bool? forceTVClientLocalSearch { get; set; }
        public bool? disableNonAdminRequests { get; set; }
        public bool? enableAutoImport { get; set; }
        public bool? disableModal { get; set; }
        public bool? showReviewsCarousel { get; set; }
        public string versionUi { get; set; }
        public string audioUi { get; set; }
        public string subtitleUi { get; set; }
        public string debridService { get; set; }
        public string debridApiKey { get; set; }
        public string realDebridApiKey { get; set; }
        public string torboxApiKey { get; set; }
        public string alldebridApiKey { get; set; }
        public string premiumizeApiKey { get; set; }
        public bool? enableDebridMetadata { get; set; }
        public bool? enableFallbackProbe { get; set; }
        public bool? fetchCachedMetadataPerVersion { get; set; }
        public bool? fetchAllNonCachedMetadata { get; set; }
        public bool? enableExternalSubtitles { get; set; }
    }
}
