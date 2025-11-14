using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/requests")]
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
            
            // Log first request details for debugging
            if (requests.Count > 0)
            {
                var first = requests[0];
                _logger.LogInformation($"[RequestsController] First request - Id: {first.Id}, Username: {first.Username}, Title: {first.Title}, Status: {first.Status}");
            }
            
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

            // Normalize and ensure media type fields are set
            request.ItemType = (request.ItemType ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(request.TmdbMediaType))
            {
                request.TmdbMediaType = request.ItemType == "series" ? "tv" : "movie";
            }

            // Server-side validation: if we have an IMDB id, prefer TMDB's tv_results mapping
            // This avoids saving a movie tmdbId when the IMDB maps to a TV show.
            try
            {
                if (!string.IsNullOrEmpty(request.ImdbId))
                {
                    var cfg = Plugin.Instance?.Configuration;
                    var apiKey = cfg?.TmdbApiKey;
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var tmdbFindUrl = $"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(request.ImdbId)}?api_key={Uri.EscapeDataString(apiKey)}&external_source=imdb_id";
                        using (var http = new HttpClient())
                        {
                            var resp = http.GetAsync(tmdbFindUrl).GetAwaiter().GetResult();
                            if (resp.IsSuccessStatusCode)
                            {
                                var content = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                using (var doc = System.Text.Json.JsonDocument.Parse(content))
                                {
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("tv_results", out var tvResults) && tvResults.GetArrayLength() > 0)
                                    {
                                        var first = tvResults[0];
                                        if (first.TryGetProperty("id", out var idProp))
                                        {
                                            request.TmdbId = idProp.GetRawText().Trim('"');
                                            request.TmdbMediaType = "tv";
                                            request.ItemType = "series";
                                        }
                                    }
                                    else if (root.TryGetProperty("movie_results", out var movieResults) && movieResults.GetArrayLength() > 0)
                                    {
                                        var first = movieResults[0];
                                        if (first.TryGetProperty("id", out var idProp))
                                        {
                                            request.TmdbId = idProp.GetRawText().Trim('"');
                                            request.TmdbMediaType = "movie";
                                            request.ItemType = "movie";
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RequestsController] TMDB find check failed for imdbId={ImdbId}", request.ImdbId);
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

            // Send Discord notification if webhook is configured
            _ = SendDiscordNotification(request);

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

            // Update fields. If an admin approves, create a separate admin-owned
            // approved copy so admin and user can delete independently.
            if (!string.IsNullOrEmpty(update.Status))
            {
                // Always update the original request's status so the requester sees it as approved
                request.Status = update.Status;
            }

            if (!string.IsNullOrEmpty(update.ApprovedBy))
            {
                request.ApprovedBy = update.ApprovedBy;
            }

            // If approving and ApprovedBy is provided and the approver is different
            // from the original requester, create an admin-owned approved copy.
            // Same logic for rejected - create admin-owned rejected copy.
            try
            {
                if (!string.IsNullOrEmpty(update.Status) && update.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(update.ApprovedBy) && !string.Equals(update.ApprovedBy, request.Username, StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = Plugin.Instance?.Configuration;

                    // Avoid creating duplicate admin-approved entries for the same approver + tmdb
                    var existingAdminCopy = cfg?.Requests?.FirstOrDefault(r =>
                        r.Username == update.ApprovedBy &&
                        r.TmdbId == request.TmdbId &&
                        r.Status != null && r.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
                    );

                    if (existingAdminCopy == null)
                    {
                        var adminCopy = new MediaRequest
                        {
                            Id = $"{update.ApprovedBy}_{request.TmdbId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                            Username = update.ApprovedBy,
                            UserId = string.Empty,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Title = request.Title,
                            Year = request.Year,
                            Img = request.Img,
                            ImdbId = request.ImdbId,
                            TmdbId = request.TmdbId,
                            JellyfinId = request.JellyfinId,
                            ItemType = request.ItemType,
                            TmdbMediaType = request.TmdbMediaType,
                            Status = "approved",
                            ApprovedBy = update.ApprovedBy
                        };

                        if (cfg?.Requests == null) cfg.Requests = new List<MediaRequest>();
                        cfg.Requests.Add(adminCopy);
                    }
                }
                else if (!string.IsNullOrEmpty(update.Status) && update.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(update.ApprovedBy) && !string.Equals(update.ApprovedBy, request.Username, StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = Plugin.Instance?.Configuration;

                    // Avoid creating duplicate admin-rejected entries for the same rejecter + tmdb
                    var existingAdminCopy = cfg?.Requests?.FirstOrDefault(r =>
                        r.Username == update.ApprovedBy &&
                        r.TmdbId == request.TmdbId &&
                        r.Status != null && r.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                    );

                    if (existingAdminCopy == null)
                    {
                        var adminCopy = new MediaRequest
                        {
                            Id = $"{update.ApprovedBy}_{request.TmdbId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                            Username = update.ApprovedBy,
                            UserId = string.Empty,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Title = request.Title,
                            Year = request.Year,
                            Img = request.Img,
                            ImdbId = request.ImdbId,
                            TmdbId = request.TmdbId,
                            JellyfinId = request.JellyfinId,
                            ItemType = request.ItemType,
                            TmdbMediaType = request.TmdbMediaType,
                            Status = "rejected",
                            ApprovedBy = update.ApprovedBy
                        };

                        if (cfg?.Requests == null) cfg.Requests = new List<MediaRequest>();
                        cfg.Requests.Add(adminCopy);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RequestsController] Could not create admin copy for approved request {RequestId}", id);
            }

            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation($"[RequestsController] Updated request {id}: status={request.Status}");

            // If the request was approved, trigger Gelato metadata refresh server-side.
            try
            {
                if (!string.IsNullOrEmpty(request.Status) && request.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                {
                    // Run in background so we don't block the HTTP response.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var cfg = Plugin.Instance?.Configuration;
                            string baseUrl = cfg?.GelatoBaseUrl;
                            if (string.IsNullOrEmpty(baseUrl))
                            {
                                baseUrl = $"{Request.Scheme}://{Request.Host.Value}";
                            }

                            var type = (request.TmdbMediaType ?? request.ItemType ?? "movie");
                            if (type == "series") type = "tv";
                            if (type == "tv" || type == "movie")
                            {
                                var imdb = request.ImdbId;
                                if (!string.IsNullOrEmpty(imdb))
                                {
                                    var url = baseUrl.TrimEnd('/') + $"/gelato/meta/{type}/{Uri.EscapeDataString(imdb)}";
                                    using var http = new HttpClient();
                                    if (!string.IsNullOrEmpty(cfg?.GelatoAuthHeader))
                                    {
                                        // Allow configuration to specify either a full header name and value
                                        // (for example: "X-Emby-Token: <token>") or just a token/value which
                                        // will be applied to the Authorization header.
                                        var header = cfg.GelatoAuthHeader;
                                        var idx = header.IndexOf(':');
                                        if (idx > -1)
                                        {
                                            var name = header.Substring(0, idx).Trim();
                                            var value = header.Substring(idx + 1).Trim();
                                            try { http.DefaultRequestHeaders.Remove(name); } catch { }
                                            http.DefaultRequestHeaders.Add(name, value);
                                        }
                                        else
                                        {
                                            try { http.DefaultRequestHeaders.Remove("Authorization"); } catch { }
                                            http.DefaultRequestHeaders.Add("Authorization", header);
                                        }
                                    }
                                    _logger.LogInformation("[RequestsController] Calling Gelato at {Url} for approved request {RequestId}", url, id);
                                    var resp = await http.GetAsync(url).ConfigureAwait(false);
                                    _logger.LogInformation("[RequestsController] Gelato responded {Status} for {RequestId}", resp.StatusCode, id);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[RequestsController] Error calling Gelato for approved request {RequestId}", id);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RequestsController] Failed to trigger Gelato for request {RequestId}", id);
            }

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
        /// <summary>
        /// Clean up invalid requests (undefined/empty IDs)
        /// </summary>
        [HttpPost("cleanup")]
        public ActionResult CleanupRequests()
        {
            _logger.LogInformation("[RequestsController] Cleanup called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest("Configuration not available");
            }

            var requests = config.Requests ?? new List<MediaRequest>();
            var before = requests.Count;
            
            // Remove requests with undefined/null/empty IDs
            var validRequests = requests.Where(r => 
                !string.IsNullOrEmpty(r.ImdbId) && r.ImdbId != "undefined" &&
                !string.IsNullOrEmpty(r.TmdbId) && r.TmdbId != "undefined"
            ).ToList();
            
            var removed = before - validRequests.Count;
            
            if (removed > 0)
            {
                config.Requests = validRequests;
                Plugin.Instance.SaveConfiguration();
                _logger.LogInformation($"[RequestsController] Cleaned up {removed} invalid requests");
            }
            
            return Ok(new { removed, remaining = validRequests.Count });
        }

        /// <summary>
        /// Send Discord notification for new media request
        /// </summary>
        private async System.Threading.Tasks.Task SendDiscordNotification(MediaRequest request)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var webhookUrl = config?.DiscordWebhookUrl;

                // Skip if webhook URL is not configured
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("[RequestsController] Discord webhook not configured, skipping notification");
                    return;
                }

                // Build Discord embed message
                var mediaType = request.ItemType == "series" ? "Series" : "Movie";
                var title = $"{request.Title ?? "Unknown"} ({request.Year ?? "N/A"})";
                var color = request.ItemType == "series" ? 3447003 : 15844367; // Blue for series, gold for movies

                // Extract actual URL from CSS url() format if present
                var imageUrl = request.Img;
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    // Handle url("...") or url('...') format
                    if (imageUrl.StartsWith("url("))
                    {
                        imageUrl = imageUrl.Substring(4).TrimEnd(')').Trim('"', '\'');
                    }
                }

                // Build JSON manually to ensure proper formatting for Discord API
                var embedJson = new StringBuilder();
                embedJson.Append("{");
                embedJson.AppendFormat("\"title\":\"{0}\",", title.Replace("\"", "\\\""));
                embedJson.AppendFormat("\"description\":\"Requested by: **{0}**\",", (request.Username ?? "Unknown").Replace("\"", "\\\""));
                embedJson.AppendFormat("\"color\":{0},", color);
                embedJson.Append("\"fields\":[");
                embedJson.AppendFormat("{{\"name\":\"Type\",\"value\":\"{0}\",\"inline\":true}},", mediaType);
                embedJson.AppendFormat("{{\"name\":\"Status\",\"value\":\"{0}\",\"inline\":true}},", (request.Status?.ToUpper() ?? "PENDING"));
                embedJson.AppendFormat("{{\"name\":\"TMDB ID\",\"value\":\"{0}\",\"inline\":true}}", request.TmdbId ?? "N/A");
                embedJson.Append("],");

                // Only add thumbnail if image URL is present
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    embedJson.AppendFormat("\"thumbnail\":{{\"url\":\"{0}\"}},", imageUrl.Replace("\"", "\\\""));
                }

                embedJson.AppendFormat("\"timestamp\":\"{0}\"", DateTimeOffset.UtcNow.ToString("o"));
                embedJson.Append("}");

                var payloadJson = $"{{\"content\":\"📢 **New Media Request**\",\"embeds\":[{embedJson}]}}";

                using var http = new HttpClient();
                var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                _logger.LogInformation("[RequestsController] Sending Discord notification for request: {RequestId}", request.Id);
                _logger.LogInformation("[RequestsController] Discord payload: {Payload}", payloadJson);

                var response = await http.PostAsync(webhookUrl, content).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[RequestsController] Discord notification sent successfully for request: {RequestId}", request.Id);
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.LogWarning("[RequestsController] Discord notification failed with status {StatusCode}: {Response}",
                        response.StatusCode, responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RequestsController] Error sending Discord notification for request: {RequestId}", request?.Id);
            }
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

        [JsonPropertyName("jellyfinId")]
        public string JellyfinId { get; set; }

        [JsonPropertyName("itemType")]
        public string ItemType { get; set; }

    [JsonPropertyName("tmdbMediaType")]
    public string TmdbMediaType { get; set; }

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
