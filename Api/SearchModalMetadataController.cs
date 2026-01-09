#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Baklava.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/metadata")]
    [Produces("application/json")]
    public class SearchModalMetadataController : ControllerBase
    {
        private readonly ILogger<SearchModalMetadataController> _logger;
        private readonly ILibraryManager _libraryManager;

        public SearchModalMetadataController(
            ILogger<SearchModalMetadataController> logger,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        [HttpGet("search")]
        public async Task<ActionResult> GetSearchMetadata(
            [FromQuery] string? tmdbId,
            [FromQuery] string? imdbId,
            [FromQuery] string? itemType) // "movie" or "series"
        {
            _logger.LogInformation("[Baklava.SearchMetadata] GetSearchMetadata: tmdbId={TmdbId}, imdbId={ImdbId}, itemType={ItemType}", 
                tmdbId ?? "null", imdbId ?? "null", itemType ?? "null");
            
            try
            {
                // 1. Determine Stremio ID
                string? stremioId = null;
                if (!string.IsNullOrEmpty(imdbId))
                {
                    stremioId = imdbId;
                }
                else if (!string.IsNullOrEmpty(tmdbId))
                {
                    stremioId = $"tmdb:{tmdbId}";
                }

                if (string.IsNullOrEmpty(stremioId))
                {
                    return BadRequest(new { error = "Either imdbId or tmdbId is required" });
                }

                // 2. Determine Stremio MediaType enum
                // Gelato.Common.StremioMediaType: Unknown=0, Movie=1, Series=2
                object? mediaTypeEnum = null;
                if (string.Equals(itemType, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    mediaTypeEnum = 1; // StremioMediaType.Movie
                }
                else if (string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                         string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase))
                {
                    mediaTypeEnum = 2; // StremioMediaType.Series
                }
                else
                {
                    return BadRequest(new { error = "Invalid or missing itemType (must be 'movie' or 'series')" });
                }

                // 3. Reflect into Gelato to get metadata
                var gelatoData = await FetchMetadataFromGelato(stremioId, mediaTypeEnum);
                
                if (gelatoData != null)
                {
                     // Debug: Log the properties of the result for troubleshooting
                     try 
                     {
                         var json = JsonSerializer.Serialize(gelatoData);
                         _logger.LogInformation("[Baklava.SearchMetadata] Gelato response: {Json}", json);

                         // Robust Mapping
                         using var doc = JsonDocument.Parse(json);
                         var root = doc.RootElement;
                         
                         var mapped = new Dictionary<string, object?>();
                         
                         // Helper to find property case-insensitive
                         JsonElement? FindProp(string[] names) 
                         {
                             foreach(var name in names) 
                             {
                                 if (root.TryGetProperty(name, out var prop)) return prop;
                                 if (root.TryGetProperty(name.ToLowerInvariant(), out var propLower)) return propLower;
                             }
                             return null;
                         }

                         var nameProp = FindProp(new[] { "Name", "Title" });
                         mapped["name"] = nameProp?.GetString();

                         var descProp = FindProp(new[] { "Description", "Overview", "Plot" });
                         mapped["description"] = descProp?.GetString();

                         var yearProp = FindProp(new[] { "Year", "ProductionYear", "ReleaseInfo" });
                         mapped["year"] = yearProp?.ToString(); // Could be int or string

                         var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
                         mapped["background"] = backProp?.GetString();

                         var logoProp = FindProp(new[] { "Logo", "LogoImage" });
                         mapped["logo"] = logoProp?.GetString();

                         var ratingProp = FindProp(new[] { "ImdbRating", "Rating", "CommunityRating" });
                         mapped["imdbRating"] = ratingProp?.ToString();

                         var genresProp = FindProp(new[] { "Genres", "Genre" });
                         if (genresProp.HasValue && genresProp.Value.ValueKind == JsonValueKind.Array)
                         {
                             mapped["genres"] = genresProp.Value.EnumerateArray().Select(x => x.GetString()).ToList();
                         }

                         var runtimeProp = FindProp(new[] { "Runtime", "Duration" });
                         mapped["runtime"] = runtimeProp?.ToString();

                         return Ok(mapped);
                     }
                     catch(Exception ex)
                     {
                         _logger.LogError(ex, "[Baklava.SearchMetadata] Error mapping Gelato response");
                         return Ok(gelatoData); // Fallback
                     }
                }

                return NotFound(new { error = "No metadata found in Gelato" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.SearchMetadata] Error getting metadata from Gelato");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task<object?> FetchMetadataFromGelato(string id, object mediaTypeEnum)
        {
            try
            {
                var gelatoPlugin = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");

                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("[Baklava.SearchMetadata] Gelato plugin assembly not found");
                    return null;
                }

                // Get GelatoPlugin.Instance
                var gelatoPluginType = gelatoPlugin.GetType("Gelato.GelatoPlugin");
                var instanceProp = gelatoPluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var gelatoInstance = instanceProp?.GetValue(null);

                if (gelatoInstance == null)
                {
                    _logger.LogWarning("[Baklava.SearchMetadata] Gelato plugin instance is null");
                    return null;
                }

                // Get Config
                var getConfigMethod = gelatoInstance.GetType().GetMethod("GetConfig");
                var config = getConfigMethod?.Invoke(gelatoInstance, new object[] { Guid.Empty }); // Guid.Empty for system config

                if (config == null) return null;

                // Get Stremio Provider ("stremio" field)
                var stremioProviderField = config.GetType().GetField("stremio");
                var stremioProvider = stremioProviderField?.GetValue(config);

                if (stremioProvider == null) return null;

                // Resolve StremioMediaType by finding the method first
                var getMetaMethod = stremioProvider.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetMetaAsync" && m.GetParameters().Length == 2);
                
                if (getMetaMethod == null)
                {
                     _logger.LogError("[Baklava.SearchMetadata] GetMetaAsync method not found on StremioProvider");
                     return null;
                }

                var paramType = getMetaMethod.GetParameters()[1].ParameterType;
                var mediaTypeEnumValue = Enum.ToObject(paramType, mediaTypeEnum);

                var task = getMetaMethod.Invoke(stremioProvider, new object[] { id, mediaTypeEnumValue }) as Task;
                if (task == null) return null;

                await task.ConfigureAwait(false);

                var resultProp = task.GetType().GetProperty("Result");
                var result = resultProp?.GetValue(task);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.SearchMetadata] Reflection error fetching from Gelato");
                return null;
            }
        }
    }
}
