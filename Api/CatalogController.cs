using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying; // Added
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/catalogs")]
    [Produces("application/json")]
    [Authorize]
    public class CatalogController : ControllerBase
    {
        private readonly ILogger<CatalogController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILibraryManager _libraryManager; // Added
        private readonly ICollectionManager _collectionManager; // Added

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CatalogController(
            ILogger<CatalogController> logger,
            IHttpClientFactory httpClientFactory,
            IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager,
            IServiceScopeFactory scopeFactory,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager) // Added
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _serverApplicationHost = serverApplicationHost;
            _configurationManager = configurationManager;
            _scopeFactory = scopeFactory;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager; // Added
        }

        /// <summary>
        /// Get all catalogs from the configured aiostreams manifest
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<CatalogDto>>> GetCatalogs()
        {
            _logger.LogInformation("[CatalogController] GET catalogs called");

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                return BadRequest("Plugin configuration not available");
            }

            // Get the aiostreams manifest URL from Gelato's configuration
            var manifestUrl = GetGelatoManifestUrl();
            if (string.IsNullOrEmpty(manifestUrl))
            {
                return BadRequest("Gelato aiostreams URL not configured. Please configure Gelato with a valid aiostreams manifest.");
            }

            try
            {
                _logger.LogInformation("[CatalogController] Fetching manifest from {Url}", manifestUrl);

                using var http = CreateHttpClient(cfg);
                http.Timeout = TimeSpan.FromSeconds(15); // Reasonable timeout for external request
                
                var response = await http.GetAsync(manifestUrl).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[CatalogController] Failed to fetch manifest: {Status}", response.StatusCode);
                    return StatusCode((int)response.StatusCode, $"Failed to fetch manifest: {response.StatusCode}");
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<StremioManifestDto>(content, JsonOptions);

                if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
                {
                    _logger.LogInformation("[CatalogController] No catalogs found in manifest");
                    return Ok(new List<CatalogDto>());
                }

                // Filter out search-only catalogs
                var catalogs = manifest.Catalogs
                    .Where(c => !IsSearchOnly(c))
                    .Select(c => new CatalogDto
                    {
                        Id = c.Id,
                        Name = c.Name ?? c.Id,
                        Type = c.Type,
                        AddonName = ExtractAddonName(c.Id, manifest.Name),
                        IsSearchCapable = c.Extra?.Any(e =>
                            string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)) ?? false,
                        ItemCount = -1, // Not fetched to avoid timeouts with many catalogs
                        SourceUrl = ConstructSourceUrl(c.Id)
                    })
                    .ToList();

                // Check for existing collections
                foreach (var cat in catalogs)
                {
                    try
                    {
                        var providerId = $"Stremio.{cat.Id}";
                        var existing = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                            Recursive = true,
                            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } },
                            Limit = 1
                        }).FirstOrDefault();

                        if (existing != null)
                        {
                            cat.ExistingCollectionId = existing.Id.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to check existing collection for {Id}: {Msg}", cat.Id, ex.Message);
                    }
                }

                _logger.LogInformation("[CatalogController] Found {Count} catalogs", catalogs.Count);

                // Note: Skipping item count fetching as it times out with many catalogs
                // Counts can be fetched on-demand if needed via the /count endpoint

                return Ok(catalogs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Error fetching catalogs");
                return StatusCode(500, $"Error fetching catalogs: {ex.Message}");
            }
        }

        /// <summary>
        /// Preview changes for a catalog update
        /// </summary>
        [HttpPost("{catalogId}/preview-update")]
        public async Task<ActionResult<PreviewUpdateResponse>> PreviewUpdate(
            string catalogId,
            [FromQuery] string type = "movie",
            [FromBody] CreateLibraryRequest request = null)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Plugin configuration not available");

            var maxItems = request?.MaxItems ?? cfg.CatalogMaxItems;
            
            // 1. Get existing collection
            var providerId = $"Stremio.{catalogId}";
            var collection = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
                HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } },
                Limit = 1
            }).FirstOrDefault() as MediaBrowser.Controller.Entities.Movies.BoxSet;

            if (collection == null)
            {
                return NotFound("Collection not found. Please create it first.");
            }

            // 2. Fetch Catalog Items
            var aiostreamsBaseUrl = GetGelatoAiostreamsBaseUrl();
            if (string.IsNullOrEmpty(aiostreamsBaseUrl)) return BadRequest("Gelato aiostreams URL not configured");

            using var http = CreateHttpClient(cfg);
            var stremioType = (type.Equals("series", StringComparison.OrdinalIgnoreCase) || 
                               type.Equals("tvshows", StringComparison.OrdinalIgnoreCase) || 
                               type.Equals("tv", StringComparison.OrdinalIgnoreCase)) 
                               ? "series" : "movie";

            List<StremioMetaDto> catalogItems;
            try
            {
                catalogItems = await FetchCatalogItemsAsync(http, aiostreamsBaseUrl, stremioType, catalogId, maxItems).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch catalog items for preview");
                return StatusCode(500, "Failed to fetch catalog items: " + ex.Message);
            }

            // 3. Compare
            // Get existing IMDB IDs
            var linkedIds = collection.LinkedChildren
                .Where(lc => lc.ItemId.HasValue)
                .Select(lc => lc.ItemId.Value)
                .ToArray();
                
            var existingImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (linkedIds.Length > 0)
            {
                var existingItems = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ItemIds = linkedIds
                });
                
                foreach(var i in existingItems)
                {
                    if (i.ProviderIds.TryGetValue("Imdb", out var id) && !string.IsNullOrEmpty(id))
                    {
                        existingImdbIds.Add(id);
                    }
                }
            }

            var catalogImdbIds = catalogItems
                .Select(GetImdbId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingCount = 0; // In catalog, not in collection
            foreach (var item in catalogItems)
            {
                var id = GetImdbId(item);
                if (!string.IsNullOrEmpty(id) && !existingImdbIds.Contains(id))
                {
                    missingCount++;
                }
            }

            var totalCount = catalogItems.Count;
            var existingCount = catalogItems.Count - missingCount;
            
            // Items in collection but NOT in catalog (Removed/Extra)
            var removedCount = 0;
            foreach(var existingId in existingImdbIds)
            {
                if (!catalogImdbIds.Contains(existingId))
                {
                    removedCount++;
                }
            }

            return Ok(new PreviewUpdateResponse
            {
                TotalCatalogItems = totalCount,
                ExistingItems = existingCount,
                NewItems = missingCount,
                RemovedItems = removedCount,
                CollectionName = collection.Name
            });
        }


        [HttpGet("{catalogId}/count")]
        public async Task<ActionResult<int>> GetCatalogCount(string catalogId, [FromQuery] string type = "movie")
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Plugin configuration not available");

            var gelatoBaseUrl = GetGelatoBaseUrl(cfg);
            using var http = CreateHttpClient(cfg);

            try
            {
                var count = await GetCatalogItemCountAsync(http, gelatoBaseUrl, type, catalogId).ConfigureAwait(false);
                return Ok(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Error getting count for {CatalogId}", catalogId);
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }



        /// <summary>
        /// Create a new Jellyfin library from a catalog
        /// </summary>
        /// <summary>
        /// Create a Jellyfin Collection (BoxSet) from a Stremio Catalog
        /// </summary>
        [HttpPost("{catalogId}/library")] // Keeping endpoint name for frontend compatibility
        public async Task<ActionResult<CreateLibraryResponse>> CreateLibraryFromCatalog(
            string catalogId,
            [FromQuery] string type = "movie",
            [FromBody] CreateLibraryRequest request = null)
        {
            var collectionName = request?.LibraryName ?? catalogId;
            _logger.LogInformation("[CatalogController] Creating collection '{Name}' from catalog {CatalogId}", collectionName, catalogId);

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Plugin configuration not available");

            var maxItems = request?.MaxItems ?? cfg.CatalogMaxItems;
            
            // Get URLs: aiostreams for catalog
            var aiostreamsBaseUrl = GetGelatoAiostreamsBaseUrl();
            if (string.IsNullOrEmpty(aiostreamsBaseUrl))
            {
                return BadRequest("Gelato aiostreams URL not configured");
            }

            // Restore HttpClient for fetching catalog items
            using var http = CreateHttpClient(cfg);

            try
            {
                // Normalize type for Stremio API (must be singular 'movie' or 'series')
                var stremioType = (type.Equals("series", StringComparison.OrdinalIgnoreCase) || 
                                   type.Equals("tvshows", StringComparison.OrdinalIgnoreCase) || 
                                   type.Equals("tv", StringComparison.OrdinalIgnoreCase)) 
                                   ? "series" : "movie";

                // 1. Fetch Items from Catalog
                var items = await FetchCatalogItemsAsync(http, aiostreamsBaseUrl, stremioType, catalogId, maxItems).ConfigureAwait(false);
                
                if (items == null || items.Count == 0)
                {
                    _logger.LogWarning("[CatalogController] No items found in catalog {CatalogId} with type {Type} (URL base: {Url})", catalogId, stremioType, aiostreamsBaseUrl);
                    
                    return Ok(new CreateLibraryResponse 
                    { 
                        Success = true, 
                        LibraryName = collectionName, 
                        Message = $"Collection created, but no items found in catalog ({stremioType})." 
                    });
                }

                _logger.LogInformation("[CatalogController] Found {Count} items in catalog {CatalogId} ({Type}). Importing max {Max}...", items.Count, catalogId, stremioType, maxItems);

                // 2. Start Background Process: Create Collection FIRST, then Import & Add each item one by one
                _ = Task.Run(async () => 
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedProvider = scope.ServiceProvider;
                    var libraryManager = scopedProvider.GetRequiredService<ILibraryManager>();
                    var collectionManager = scopedProvider.GetRequiredService<ICollectionManager>();
                    
                    var successCount = 0;
                    var failedCount = 0;

                    try
                    {
                        // STEP 1: Create or find collection FIRST
                        var providerId = $"Stremio.{catalogId}";
                        
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                            Recursive = true,
                            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } }
                        };
                        
                        var collection = libraryManager.GetItemList(query).FirstOrDefault() as MediaBrowser.Controller.Entities.Movies.BoxSet;

                        // Delete old collections with same name
                        if (collection == null)
                        {
                            var allBoxSets = libraryManager.GetItemList(new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                                Recursive = true
                            }).OfType<MediaBrowser.Controller.Entities.Movies.BoxSet>()
                              .Where(b => b.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                              .ToList();
                            
                            foreach (var old in allBoxSets)
                            {
                                _logger.LogInformation("[CatalogController] Deleting old collection '{Name}'", old.Name);
                                libraryManager.DeleteItem(old, new DeleteOptions { DeleteFileLocation = true });
                            }
                        }

                        // Create new collection
                        if (collection == null)
                        {
                            _logger.LogInformation("[CatalogController] Creating collection '{Name}' FIRST", collectionName);
                            var created = await collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                            {
                                Name = collectionName,
                                ProviderIds = new Dictionary<string, string> { { "Stremio", providerId } }
                            }).ConfigureAwait(false);
                            
                            collection = libraryManager.GetItemById(created.Id) as MediaBrowser.Controller.Entities.Movies.BoxSet;
                            _logger.LogInformation("[CatalogController] Collection created: {Id}", collection?.Id);
                        }
                        else
                        {
                            _logger.LogInformation("[CatalogController] Using existing collection: {Id}. Syncing items (additive)...", collection.Id);
                            // DO NOT CLEAR ITEMS - We want to Additive Sync
                            // Check what is already in the collection to skip
                            // var existing = ... (Removed)
                        }

                        if (collection == null)
                        {
                            _logger.LogError("[CatalogController] Failed to create collection!");
                            return;
                        }

                        // PRE-FETCH: Get existing IMDB IDs in the collection to skip processing
                        var linkedIds = collection.LinkedChildren
                            .Where(lc => lc.ItemId.HasValue)
                            .Select(lc => lc.ItemId.Value)
                            .ToArray();
                            
                        var existingImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        
                        if (linkedIds.Length > 0)
                        {
                            var existingItems = libraryManager.GetItemList(new InternalItemsQuery
                            {
                                ItemIds = linkedIds
                            });
                            
                            foreach(var i in existingItems)
                            {
                                if (i.ProviderIds.TryGetValue("Imdb", out var id) && !string.IsNullOrEmpty(id))
                                {
                                    existingImdbIds.Add(id);
                                }
                            }
                        }

                        _logger.LogInformation("[CatalogController] Collection has {Count} existing items ({ImdbCount} with IMDB). Examining catalog...", linkedIds.Length, existingImdbIds.Count);

                        // STEP 2: Import each item and add to collection ONE BY ONE
                        foreach (var item in items.Take(maxItems))
                        {
                            try
                            {
                                var imdbId = GetImdbId(item);
                                if (string.IsNullOrEmpty(imdbId))
                                {
                                    failedCount++;
                                    continue;
                                }

                                // CHECK 1: Is it already in the collection?
                                if (existingImdbIds.Contains(imdbId))
                                {
                                    // Already exists, skip
                                    // _logger.LogDebug("[CatalogController] Msg: Item {ImdbId} already in collection, skipping.", imdbId);
                                    continue;
                                }

                                var mediaType = (type.Equals("series", StringComparison.OrdinalIgnoreCase) || type.Equals("tvshows", StringComparison.OrdinalIgnoreCase)) ? "tv" : "movie";
                                
                                // CHECK 2: Is it already in the LIBRARY (but not in collection)?
                                var libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                                {
                                    HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                                    Recursive = true,
                                    Limit = 1,
                                    IncludeItemTypes = new[] { mediaType.Equals("tv") ? BaseItemKind.Series : BaseItemKind.Movie }
                                }).FirstOrDefault();

                                // If not in library, Import it
                                if (libraryItem == null)
                                {
                                    // Import via Gelato
                                    var importedIdStr = await ImportItemViaReflection(imdbId, mediaType, scopedProvider).ConfigureAwait(false);
                                    
                                    if (string.IsNullOrEmpty(importedIdStr))
                                    {
                                        _logger.LogWarning("[CatalogController] Import failed for {ImdbId}", imdbId);
                                        failedCount++;
                                        continue;
                                    }
                                    
                                    // Fetch it again
                                    libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                                    {
                                        HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                                        Recursive = true,
                                        Limit = 1
                                    }).FirstOrDefault();
                                }

                                if (libraryItem == null)
                                {
                                    _logger.LogWarning("[CatalogController] Could not find item by IMDB {ImdbId} even after import or lookup", imdbId);
                                    failedCount++;
                                    continue;
                                }

                                // Add to collection
                                _logger.LogInformation("[CatalogController] Adding '{Name}' to collection", libraryItem.Name);
                                await collectionManager.AddToCollectionAsync(collection.Id, new[] { libraryItem.Id }).ConfigureAwait(false);
                                
                                successCount++;
                                
                                // Longer delay to let Jellyfin release file lock
                                await Task.Delay(500).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[CatalogController] Failed to process item {Id}", item.Id);
                                failedCount++;
                            }
                        }

                        _logger.LogInformation("[CatalogController] Import complete. Success: {Success}, Failed: {Failed}", successCount, failedCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[CatalogController] Background import failed");
                    }

                });

                return Ok(new CreateLibraryResponse 
                { 
                    Success = true, 
                    LibraryName = collectionName, 
                    Message = $"Collection '{collectionName}' is being populated with {Math.Min(items.Count, maxItems)} items in background." 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process catalog {CatalogId}", catalogId);
                return StatusCode(500, "Internal error: " + ex.Message);
            }
        }

        /// <summary>
        /// Preview changes for a catalog update
        /// </summary>


        private async Task<string> ImportItemViaReflection(string imdbId, string type, IServiceProvider serviceProvider)
        {
            try
            {
                // 1. Resolve Gelato Assemblies and Types
                var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");
                
                if (gelatoAssembly == null)
                {
                    _logger.LogError("Gelato assembly not found");
                    return null;
                }

                var managerType = gelatoAssembly.GetType("Gelato.GelatoManager");
                var pluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                var metaTypeEnum = gelatoAssembly.GetType("Gelato.StremioMediaType");

                if (managerType == null || pluginType == null || metaTypeEnum == null)
                {
                    _logger.LogError("Required Gelato types not found. Manager: {M}, Plugin: {P}, Enum: {E}", 
                        managerType != null, pluginType != null, metaTypeEnum != null);
                    return null;
                }

                // 2. Get Manager Instance
                // Strategy A: Try DI (Preferred)
                var manager = serviceProvider.GetService(managerType);
                
                // Strategy B: Fallback to Static Instance (More robust for cross-plugin)
                if (manager == null)
                {
                    var gelatoPlugin = pluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                    if (gelatoPlugin != null)
                    {
                        var managerField = pluginType.GetField("_manager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        manager = managerField?.GetValue(gelatoPlugin);
                    }
                }

                if (manager == null)
                {
                    _logger.LogError("GelatoManager service not found in DI or Plugin Instance.");
                    return null;
                }

                // 3. Get Configuration to fetch Meta
                // GelatoPlugin.Instance.GetConfig(Guid.Empty)
                var pluginInstance = pluginType.GetProperty("Instance").GetValue(null);
                var config = pluginInstance.GetType().GetMethod("GetConfig").Invoke(pluginInstance, new object[] { Guid.Empty });
                
                // config.stremio
                var stremioProvider = config.GetType().GetField("stremio").GetValue(config);

                // 4. Fetch Meta
                // StremioMediaType enum
                var metaTypeVal = Enum.Parse(metaTypeEnum, type.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie", true);
                
                // GetMetaAsync
                var getMetaMethod = stremioProvider.GetType().GetMethod("GetMetaAsync", new Type[] { typeof(string), metaTypeEnum });
                
                if (getMetaMethod == null)
                {
                    _logger.LogError("GetMetaAsync method not found in GelatoStremioProvider");
                    return null;
                }

                // Task<StremioMeta>
                var metaTask = (Task)getMetaMethod.Invoke(stremioProvider, new object[] { imdbId, metaTypeVal });
                await metaTask.ConfigureAwait(false);
                
                // Get result from Task
                var metaResult = metaTask.GetType().GetProperty("Result").GetValue(metaTask);
                if (metaResult == null)
                {
                    _logger.LogWarning("Meta not found for {ImdbId}", imdbId);
                    return null;
                }

                // 5. Determine Parent Folder
                // manager.TryGetSeriesFolder / TryGetMovieFolder
                // public Folder? TryGetSeriesFolder(Guid userId)
                var isSeries = type.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var folderMethodName = isSeries ? "TryGetSeriesFolder" : "TryGetMovieFolder";
                var getFolderMethod = managerType.GetMethod(folderMethodName, new Type[] { typeof(Guid) });
                
                var parentFolder = getFolderMethod.Invoke(manager, new object[] { Guid.Empty });
                if (parentFolder == null)
                {
                    _logger.LogError("Root folder not found for {Type}", type);
                    return null;
                }

                // 6. Insert Meta
                // public Task<(BaseItem? Item, bool Created)> InsertMeta(...)
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                
                var insertTask = (Task)insertMetaMethod.Invoke(manager, new object[] {
                    parentFolder,
                    metaResult,
                    Guid.Empty, // userId
                    true, // allowRemoteRefresh
                    true, // refreshItem
                    false, // queueRefreshItem
                    System.Threading.CancellationToken.None
                });
                
                await insertTask.ConfigureAwait(false);
                
                // Return Item.Id
                // Result tuple: (BaseItem Item, bool Created)
                // C# tuple is ValueTuple
                var resultTuple = insertTask.GetType().GetProperty("Result").GetValue(insertTask);
                
                // Access Item field (ValueTuple uses Item1, Item2 naming)
                var itemField = resultTuple.GetType().GetField("Item1"); 
                var item = itemField?.GetValue(resultTuple);
                
                if (item != null)
                {
                    var idProp = item.GetType().GetProperty("Id");
                    return idProp?.GetValue(item)?.ToString();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reflection import failed for {ImdbId}", imdbId);
                return null;
            }
        }

        private static string SanitizeFolderName(string name)
        {
            // Remove invalid path characters
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            return sanitized.Replace(" ", "_").Replace(".", "_");
        }

        #region Helper Methods

        private HttpClient CreateHttpClient(PluginConfiguration cfg)
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(cfg.CatalogImportTimeout > 0 ? cfg.CatalogImportTimeout : 300);

            // Add auth header if configured
            if (!string.IsNullOrEmpty(cfg.GelatoAuthHeader))
            {
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

            return http;
        }

        private string GetGelatoBaseUrl(PluginConfiguration cfg)
        {
            var url = cfg.GelatoBaseUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = $"{Request.Scheme}://{Request.Host.Value}";
            }
            return url.TrimEnd('/');
        }

        /// <summary>
        /// Get localhost-based URL for internal API calls (avoids Docker/network issues)
        /// </summary>
        private string GetLocalBaseUrl()
        {
            // Try to get the local URL from the server application host
            try
            {
                // GetSmartApiUrl returns a URL that works for internal communication
                var localUrl = _serverApplicationHost.GetSmartApiUrl(Request.HttpContext.Connection.LocalIpAddress);
                if (!string.IsNullOrEmpty(localUrl))
                {
                    return localUrl.TrimEnd('/');
                }
            }
            catch
            {
                // Fallback if method fails
            }

            // Fallback: use the request's scheme and host
            return $"{Request.Scheme}://{Request.Host.Value}".TrimEnd('/');
        }

        /// <summary>
        /// Read Gelato's plugin configuration to get the aiostreams manifest URL
        /// </summary>
        private string GetGelatoManifestUrl()
        {
            try
            {
                // Gelato stores its config in Jellyfin's plugin configurations folder
                var configDir = _configurationManager.ApplicationPaths.PluginsPath;
                var gelatoConfigPath = System.IO.Path.Combine(configDir, "configurations", "Gelato.xml");

                _logger.LogDebug("[CatalogController] Looking for Gelato config at {Path}", gelatoConfigPath);

                if (!System.IO.File.Exists(gelatoConfigPath))
                {
                    _logger.LogWarning("[CatalogController] Gelato config file not found at {Path}", gelatoConfigPath);
                    return null;
                }

                var xmlContent = System.IO.File.ReadAllText(gelatoConfigPath);
                
                // Parse the XML to find the Url element
                var xmlDoc = System.Xml.Linq.XDocument.Parse(xmlContent);
                var urlElement = xmlDoc.Descendants("Url").FirstOrDefault();
                
                if (urlElement == null || string.IsNullOrEmpty(urlElement.Value))
                {
                    _logger.LogWarning("[CatalogController] Gelato config has no URL configured");
                    return null;
                }

                var manifestUrl = urlElement.Value.Trim();
                
                // Gelato's config already contains the full manifest URL (ending with /manifest.json)
                _logger.LogDebug("[CatalogController] Found Gelato manifest URL: {Url}", manifestUrl);
                
                return manifestUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Failed to read Gelato configuration");
                return null;
            }
        }

        /// <summary>
        /// Get just the aiostreams base URL (without /manifest.json) for fetching catalogs
        /// </summary>
        private string GetGelatoAiostreamsBaseUrl()
        {
            var manifestUrl = GetGelatoManifestUrl();
            if (string.IsNullOrEmpty(manifestUrl)) return null;
            
            // Remove /manifest.json to get the base URL
            return manifestUrl.Replace("/manifest.json", "").TrimEnd('/');
        }

        private async Task<int> GetCatalogItemCountAsync(HttpClient http, string baseUrl, string type, string catalogId)
        {
            var url = $"{baseUrl}/catalog/{type.ToLowerInvariant()}/{Uri.EscapeDataString(catalogId)}";
            var resp = await http.GetAsync(url).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return -1;

            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var catalogResp = JsonSerializer.Deserialize<StremioCatalogResponseDto>(content, JsonOptions);

            return catalogResp?.Metas?.Count ?? 0;
        }

        private async Task<List<StremioMetaDto>> FetchCatalogItemsAsync(HttpClient http, string baseUrl, string type, string catalogId, int maxItems)
        {
            var items = new List<StremioMetaDto>();
            var skip = 0;
            var pageSize = 100; // Stremio typically returns up to 100 items per page

            while (items.Count < maxItems)
            {
                // Stremio v3 protocol uses .json extension and path-based skip
                var typeStr = type.ToLowerInvariant();
                var encodedId = Uri.EscapeDataString(catalogId);
                
                var url = (skip > 0) 
                    ? $"{baseUrl}/catalog/{typeStr}/{encodedId}/skip={skip}.json"
                    : $"{baseUrl}/catalog/{typeStr}/{encodedId}.json";

                var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var catalogResp = JsonSerializer.Deserialize<StremioCatalogResponseDto>(content, JsonOptions);

                if (catalogResp?.Metas == null || catalogResp.Metas.Count == 0) break;

                items.AddRange(catalogResp.Metas);
                skip += catalogResp.Metas.Count;

                // Optimization: If we got 0 items, break (handled above).
                // DO NOT break on count < 100 because some addons return small batches (e.g. 20)
            }

            return items.Take(maxItems).ToList();
        }

        private static bool IsSearchOnly(StremioCatalogDto catalog)
        {
            // A search-only catalog typically has a required "search" extra
            var searchExtra = catalog.Extra?.FirstOrDefault(e =>
                string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase));

            return searchExtra?.IsRequired == true;
        }

        private static string ExtractAddonName(string catalogId, string manifestName)
        {
            // Catalog IDs often contain addon hints like "088e3b0.tmdb.top"
            // The manifest name is more reliable
            if (!string.IsNullOrEmpty(manifestName)) return manifestName;

            // Try to extract from catalog ID
            if (catalogId.Contains("tmdb", StringComparison.OrdinalIgnoreCase))
                return "The Movie Database";
            if (catalogId.Contains("mediafusion", StringComparison.OrdinalIgnoreCase))
                return "MediaFusion";
            if (catalogId.Contains("comet", StringComparison.OrdinalIgnoreCase))
                return "Comet";

            return "Unknown";
        }

        private static string GetImdbId(StremioMetaDto meta)
        {
            // ImdbId is often in the Id field directly
            if (!string.IsNullOrEmpty(meta.ImdbId)) return meta.ImdbId;
            if (meta.Id?.StartsWith("tt", StringComparison.OrdinalIgnoreCase) == true) return meta.Id;
            return null;
        }

        private static string ConstructSourceUrl(string catalogId)
        {
            if (string.IsNullOrEmpty(catalogId)) return null;

            // Parse catalog ID like "4fbe3b0.mdblist.87667" or "4fbe3b0.trakt.list"
            var parts = catalogId.Split('.');
            if (parts.Length < 2) return null;

            var source = parts.Length >= 2 ? parts[1]?.ToLowerInvariant() : null;
            var listId = parts.Length >= 3 ? parts[2] : null;

            return source switch
            {
                "mdblist" when !string.IsNullOrEmpty(listId) => $"https://mdblist.com/lists/{listId}",
                "trakt" when !string.IsNullOrEmpty(listId) => $"https://trakt.tv/lists/{listId}",
                "tmdb" => "https://www.themoviedb.org/",
                "tvdb" => "https://thetvdb.com/",
                "imdb" => "https://www.imdb.com/",
                "mediafusion" => "https://mediafusion.com/",
                _ => null
            };
        }

        #endregion
    }

    #region DTOs

    public class CatalogDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("itemCount")]
        public int ItemCount { get; set; }

        [JsonPropertyName("addonName")]
        public string AddonName { get; set; }

        [JsonPropertyName("isSearchCapable")]
        public bool IsSearchCapable { get; set; }

        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; }

        [JsonPropertyName("existingCollectionId")]
        public string ExistingCollectionId { get; set; }
    }

    public class CreateCollectionRequest
    {
        [JsonPropertyName("maxItems")]
        public int MaxItems { get; set; } = 100;

        [JsonPropertyName("collectionName")]
        public string CollectionName { get; set; }
    }

    public class CreateCollectionResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("collectionId")]
        public string CollectionId { get; set; }

        [JsonPropertyName("itemCount")]
        public int ItemCount { get; set; }

        [JsonPropertyName("collectionName")]
        public string CollectionName { get; set; }
    }

    public class ImportRequest
    {
        [JsonPropertyName("maxItems")]
        public int MaxItems { get; set; } = 100;
    }

    public class ImportResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("importedCount")]
        public int ImportedCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }
    }

    public class CreateLibraryRequest
    {
        [JsonPropertyName("libraryName")]
        public string LibraryName { get; set; }

        [JsonPropertyName("maxItems")]
        public int MaxItems { get; set; } = 100;
    }

    public class CreateLibraryResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("libraryName")]
        public string LibraryName { get; set; }

        [JsonPropertyName("libraryPath")]
        public string LibraryPath { get; set; }

        [JsonPropertyName("itemCount")]
        public int ItemCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    public class PreviewUpdateResponse
    {
        [JsonPropertyName("totalCatalogItems")]
        public int TotalCatalogItems { get; set; }

        [JsonPropertyName("existingItems")]
        public int ExistingItems { get; set; }

        [JsonPropertyName("newItems")]
        public int NewItems { get; set; }

        [JsonPropertyName("removedItems")]
        public int RemovedItems { get; set; }

        [JsonPropertyName("collectionName")]
        public string CollectionName { get; set; }
    }

    // Stremio manifest DTOs (simplified versions matching Gelato)
    public class StremioManifestDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("catalogs")]
        public List<StremioCatalogDto> Catalogs { get; set; }
    }

    public class StremioCatalogDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("extra")]
        public List<StremioExtraDto> Extra { get; set; }
    }

    public class StremioExtraDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }

    public class StremioCatalogResponseDto
    {
        [JsonPropertyName("metas")]
        public List<StremioMetaDto> Metas { get; set; }
    }

    public class StremioMetaDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("imdb_id")]
        public string ImdbId { get; set; }
    }

    #endregion
}
