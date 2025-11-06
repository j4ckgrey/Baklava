using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
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
        public ActionResult<ConfigDto> GetConfig()
        {
            _logger.LogInformation("[ConfigController] GET config called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[ConfigController] Config is null");
                return Ok(new ConfigDto());
            }

            return Ok(new ConfigDto
            {
                TmdbApiKey = config.TmdbApiKey ?? string.Empty,
                GlobalSearchByDefault = config.GlobalSearchByDefault
            });
        }

        [HttpPost]
        public ActionResult UpdateConfig([FromBody] ConfigDto configDto)
        {
            _logger.LogInformation("[ConfigController] POST config called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[ConfigController] Config is null");
                return BadRequest("Plugin configuration not available");
            }

            if (configDto != null)
            {
                config.TmdbApiKey = configDto.TmdbApiKey;
                config.GlobalSearchByDefault = configDto.GlobalSearchByDefault;
                Plugin.Instance.SaveConfiguration();
                
                _logger.LogInformation("[ConfigController] Configuration updated successfully");
            }

            return Ok();
        }
    }

    public class ConfigDto
    {
        [JsonPropertyName("tmdbApiKey")]
        public string TmdbApiKey { get; set; } = string.Empty;

        [JsonPropertyName("globalSearchByDefault")]
        public bool GlobalSearchByDefault { get; set; } = true;
    }
}
