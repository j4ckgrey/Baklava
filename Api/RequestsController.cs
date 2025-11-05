using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/myplugin/requests")]
    [Produces("application/json")]
    public class RequestsController : ControllerBase
    {
        private readonly ILogger<RequestsController> _logger;

        public RequestsController(ILogger<RequestsController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public ActionResult<List<MediaRequest>> GetRequests()
        {
            _logger.LogInformation("[RequestsController] GET called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsController] Config is null");
                return Ok(new List<MediaRequest>());
            }

            var requests = config.Requests ?? new List<MediaRequest>();
            _logger.LogInformation($"[RequestsController] Returning {requests.Count} requests");
            return Ok(requests);
        }

        [HttpPost]
        public ActionResult<MediaRequest> CreateRequest([FromBody] MediaRequest request)
        {
            _logger.LogInformation("[RequestsController] POST called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsController] Config is null");
                return BadRequest("Plugin configuration not available");
            }

            if (request == null)
            {
                return BadRequest("Request data is required");
            }

            // Auto-generate ID if not provided
            if (string.IsNullOrEmpty(request.Id))
            {
                request.Id = $"{request.Username}_{request.TmdbId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            }

            // Set default status
            if (string.IsNullOrEmpty(request.Status))
            {
                request.Status = "pending";
            }

            // Set timestamp
            request.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Add to list
            if (config.Requests == null)
            {
                config.Requests = new List<MediaRequest>();
            }
            
            config.Requests.Add(request);
            Plugin.Instance.SaveConfiguration();

            _logger.LogInformation($"[RequestsController] Created request: {request.Id}");
            return Ok(request);
        }

        [HttpPut("{id}")]
        public ActionResult UpdateRequest(string id, [FromBody] UpdateRequestDto update)
        {
            _logger.LogInformation($"[RequestsController] PUT called for {id}");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsController] Config is null");
                return BadRequest("Plugin configuration not available");
            }

            var request = config.Requests?.FirstOrDefault(r => r.Id == id);
            if (request == null)
            {
                return NotFound($"Request {id} not found");
            }

            // Update fields
            if (!string.IsNullOrEmpty(update.Status))
            {
                request.Status = update.Status;
            }
            
            if (!string.IsNullOrEmpty(update.ApprovedBy))
            {
                request.ApprovedBy = update.ApprovedBy;
            }

            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation($"[RequestsController] Updated request {id}: status={request.Status}");
            return Ok();
        }

        [HttpDelete("{id}")]
        public ActionResult DeleteRequest(string id)
        {
            _logger.LogInformation($"[RequestsController] DELETE called for {id}");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsController] Config is null");
                return BadRequest("Plugin configuration not available");
            }

            var request = config.Requests?.FirstOrDefault(r => r.Id == id);
            if (request == null)
            {
                return NotFound($"Request {id} not found");
            }

            config.Requests.Remove(request);
            Plugin.Instance.SaveConfiguration();

            _logger.LogInformation($"[RequestsController] Deleted request {id}");
            return Ok();
        }
    }

    public class MediaRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("year")]
        public string Year { get; set; }

        [JsonPropertyName("img")]
        public string Img { get; set; }

        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; }

        [JsonPropertyName("tmdbId")]
        public string TmdbId { get; set; }

        [JsonPropertyName("itemType")]
        public string ItemType { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }
    }

    public class UpdateRequestDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }
    }
}
