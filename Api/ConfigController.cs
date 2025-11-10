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
                    useDropdownsInsteadOfCards = cfg.UseDropdownsInsteadOfCards
                });
            }

            return Ok(new { 
                defaultTmdbId = cfg.DefaultTmdbId,
                disableNonAdminRequests = cfg.DisableNonAdminRequests,
                useDropdownsInsteadOfCards = cfg.UseDropdownsInsteadOfCards
            });
        }

        // Admin-only update: requires authenticated admin user
        [HttpPut]
        [Authorize]
        public ActionResult SetConfig([FromBody] ConfigDto dto)
        {
            // Basic admin check
            var user = HttpContext.User;
            var isAdmin = user?.IsInRole("Administrator") ?? false;
            if (!isAdmin)
            {
                return Forbid();
            }

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Configuration not available");

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
            if (dto.useDropdownsInsteadOfCards.HasValue)
            {
                cfg.UseDropdownsInsteadOfCards = dto.useDropdownsInsteadOfCards.Value;
            }
            
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("[ConfigController] Updated configuration - SearchFilter: {SearchFilter}, ForceTVLocal: {ForceTVLocal}, DisableNonAdminRequests: {DisableNonAdminRequests}", 
                cfg.EnableSearchFilter, cfg.ForceTVClientLocalSearch, cfg.DisableNonAdminRequests);
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
        public bool? useDropdownsInsteadOfCards { get; set; }
    }
}
