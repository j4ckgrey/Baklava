using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    // GET endpoint
    [Route("/MyPlugin/Requests", "GET", Summary = "Get all requests")]
    public class GetRequests : IReturn<List<MediaRequest>>
    {
    }

    // POST endpoint
    [Route("/MyPlugin/Requests", "POST", Summary = "Add a new request")]
    public class AddRequest : IReturn<MediaRequest>
    {
        public MediaRequest Request { get; set; }
    }

    // DELETE endpoint
    [Route("/MyPlugin/Requests/{Id}", "DELETE", Summary = "Delete a request")]
    public class DeleteRequest : IReturnVoid
    {
        public string Id { get; set; }
    }

    // Service implementation
    [Authenticated]
    public class RequestsService : IService
    {
        private readonly ILogger<RequestsService> _logger;

        public RequestsService(ILogger<RequestsService> logger)
        {
            _logger = logger;
        }

        public object Get(GetRequests request)
        {
            _logger.LogInformation("[RequestsService] GET /MyPlugin/Requests called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsService] Plugin configuration is null");
                return new List<MediaRequest>();
            }

            var requests = config.Requests ?? new List<MediaRequest>();
            _logger.LogInformation("[RequestsService] Returning {Count} requests", requests.Count);
            return requests;
        }

        public async Task<object> Post(AddRequest request)
        {
            _logger.LogInformation("[RequestsService] POST /MyPlugin/Requests called");
            
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                throw new ResourceNotFoundException("Plugin not initialized");
            }

            // Read request body
            MediaRequest mediaRequest = request.Request;
            
            if (Request.InputStream != null && Request.InputStream.CanRead)
            {
                using var reader = new StreamReader(Request.InputStream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();
                _logger.LogInformation("[RequestsService] Received JSON: {Json}", json);
                mediaRequest = JsonSerializer.Deserialize<MediaRequest>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }

            if (config.Requests == null)
            {
                config.Requests = new List<MediaRequest>();
            }

            // Generate unique ID if not provided
            if (string.IsNullOrEmpty(mediaRequest.Id))
            {
                mediaRequest.Id = $"{mediaRequest.Username}_{mediaRequest.TmdbId ?? mediaRequest.ImdbId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            }

            config.Requests.Add(mediaRequest);
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("[RequestsService] Saved request {Id}, total: {Count}", mediaRequest.Id, config.Requests.Count);
            return mediaRequest;
        }

        public object Delete(DeleteRequest request)
        {
            _logger.LogInformation("[RequestsService] DELETE /MyPlugin/Requests/{Id} called", request.Id);
            
            var config = Plugin.Instance?.Configuration;
            if (config == null || config.Requests == null)
            {
                throw new ResourceNotFoundException("Request not found");
            }

            var mediaRequest = config.Requests.FirstOrDefault(r => r.Id == request.Id);
            if (mediaRequest == null)
            {
                throw new ResourceNotFoundException($"Request {request.Id} not found");
            }

            config.Requests.Remove(mediaRequest);
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("[RequestsService] Deleted request {Id}, remaining: {Count}", request.Id, config.Requests.Count);
            return true;
        }
    }
}
