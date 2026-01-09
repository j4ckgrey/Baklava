#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Baklava;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/metadata")]
    [Produces("application/json")]
    public class LibraryController : ControllerBase
    {
        private readonly ILogger<LibraryController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        public LibraryController(
            ILogger<LibraryController> logger,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
        }

        [HttpGet("library-status")]
        public ActionResult CheckLibraryStatus(
            [FromQuery] string? imdbId,
            [FromQuery] string? tmdbId,
            [FromQuery] string itemType,
            [FromQuery] string? jellyfinId)
        {
            _logger.LogInformation("[Baklava.Library] CheckLibraryStatus: imdbId={ImdbId}, tmdbId={TmdbId}, itemType={ItemType}, jellyfinId={JellyfinId}",
                imdbId ?? "null", tmdbId ?? "null", itemType ?? "null", jellyfinId ?? "null");
            
            try
            {
                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(jellyfinId))
                {
                    return BadRequest(new { error = "Either imdbId, tmdbId, or jellyfinId is required" });
                }

                var inLibrary = false;
                string? foundImdbId = imdbId;
                string? foundTmdbId = tmdbId;
                
                try
                {
                    // Fast path: Check by Jellyfin ID
                    if (!string.IsNullOrEmpty(jellyfinId) && Guid.TryParse(jellyfinId, out var jfGuid))
                    {
                        var itemById = _libraryManager.GetItemById(jfGuid);
                        if (itemById != null)
                        {
                            var itemTypeName = itemById.GetType().Name;
                            if ((itemType == "series" && itemTypeName == "Series") || (itemType == "movie" && itemTypeName == "Movie") || string.IsNullOrEmpty(itemType))
                            {
                                inLibrary = true;
                                if (itemById.ProviderIds != null)
                                {
                                    itemById.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                    itemById.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);
                                }
                            }
                        }
                    }

                    // Slow path: Search library by Provider ID
                    if (!inLibrary && (!string.IsNullOrEmpty(imdbId) || !string.IsNullOrEmpty(tmdbId)))
                    {
                        var query = new InternalItemsQuery { Recursive = true };
                        if (itemType == "series") query.IncludeItemTypes = new[] { BaseItemKind.Series };
                        else if (itemType == "movie") query.IncludeItemTypes = new[] { BaseItemKind.Movie };

                        var allItems = _libraryManager.GetItemList(query);
                        var foundItem = allItems.FirstOrDefault(item =>
                        {
                            var providerIds = item.ProviderIds;
                            if (providerIds == null) return false;
                            if (imdbId != null && providerIds.TryGetValue("Imdb", out var itemImdb) && itemImdb == imdbId) return true;
                            if (tmdbId != null && providerIds.TryGetValue("Tmdb", out var itemTmdb) && itemTmdb == tmdbId) return true;
                            return false;
                        });

                        if (foundItem != null)
                        {
                            inLibrary = true;
                            if (foundItem.ProviderIds != null)
                            {
                                if (string.IsNullOrEmpty(foundImdbId)) foundItem.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                if (string.IsNullOrEmpty(foundTmdbId)) foundItem.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Baklava.Library] Error querying library items");
                }

                var config = Plugin.Instance?.Configuration;
                var requests = config?.Requests ?? new List<MediaRequest>();
                
                var existingRequest = requests.FirstOrDefault(r =>
                    r.ItemType == itemType &&
                    (
                        ((foundImdbId != null && !string.IsNullOrEmpty(r.ImdbId) && r.ImdbId == foundImdbId) || 
                         (foundTmdbId != null && !string.IsNullOrEmpty(r.TmdbId) && r.TmdbId == foundTmdbId)) ||
                        (inLibrary && !string.IsNullOrEmpty(jellyfinId) && !string.IsNullOrEmpty(r.JellyfinId) && r.JellyfinId == jellyfinId)
                    )
                );

                string actualUsername = null;
                if (existingRequest != null && !string.IsNullOrEmpty(existingRequest.UserId))
                {
                    try
                    {
                        var userId = Guid.Parse(existingRequest.UserId);
                        var user = _userManager.GetUserById(userId);
                        actualUsername = user?.Username ?? existingRequest.Username;
                    }
                    catch
                    {
                        actualUsername = existingRequest.Username;
                    }
                }

                return Ok(new
                {
                    inLibrary,
                    existingRequest = existingRequest != null ? new
                    {
                        id = existingRequest.Id,
                        status = existingRequest.Status,
                        username = actualUsername ?? existingRequest.Username,
                        title = existingRequest.Title
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Library] Error checking library status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
