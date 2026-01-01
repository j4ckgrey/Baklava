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
using MediaBrowser.Model.Querying;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IHttpContextAccessor _httpContextAccessor;


        // Global import queue - ensures only one catalog import runs at a time
        private static readonly System.Threading.SemaphoreSlim _importQueue = new(1, 1);
        
        // Track import progress per catalog (catalogId -> progress info)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImportProgress> _importProgress = new();

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
            ICollectionManager collectionManager,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _serverApplicationHost = serverApplicationHost;
            _configurationManager = configurationManager;
            _scopeFactory = scopeFactory;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _httpContextAccessor = httpContextAccessor;
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
                        var providerId = $"Stremio.{cat.Id}.{cat.Type}";
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
                            cat.CollectionName = existing.Name;
                            
                            // Get item count from collection's linked children
                            var boxSet = existing as MediaBrowser.Controller.Entities.Movies.BoxSet;
                            cat.ExistingItemCount = boxSet?.LinkedChildren?.Count(lc => lc.ItemId.HasValue) ?? 0;
                            
                            // Read stored catalog total from ProviderIds
                            if (existing.ProviderIds.TryGetValue("Stremio.CatalogTotal", out var totalStr) && int.TryParse(totalStr, out var total))
                            {
                                cat.ItemCount = total;
                            }
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
        /// Get import progress for a catalog
        /// </summary>
        [HttpGet("{catalogId}/progress")]
        public ActionResult<ImportProgress> GetImportProgress(string catalogId)
        {
            if (_importProgress.TryGetValue(catalogId, out var progress))
            {
                return Ok(progress);
            }
            return Ok(new ImportProgress { CatalogId = catalogId, Status = "idle", Total = 0, Processed = 0 });
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
            var providerId = $"Stremio.{catalogId}.{type}";
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
                (catalogItems, _) = await FetchCatalogItemsAsync(http, aiostreamsBaseUrl, stremioType, catalogId, maxItems).ConfigureAwait(false);
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
        /// Create a Jellyfin Collection (BoxSet) from a Stremio Catalog.
        /// Simple synchronous processing: fetch items, import via Gelato, add to collection.
        /// </summary>
        [HttpPost("{catalogId}/library")]
        public async Task<ActionResult<CreateLibraryResponse>> CreateLibraryFromCatalog(
            string catalogId,
            [FromQuery] string type = "movie",
            [FromQuery] bool separate = false,
            [FromBody] CreateLibraryRequest request = null)
        {
            var collectionName = request?.LibraryName ?? catalogId;
            _logger.LogInformation("[CatalogController] Creating collection '{Name}' from catalog {CatalogId}", collectionName, catalogId);

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return BadRequest("Plugin configuration not available");

            var maxItems = request?.MaxItems ?? cfg.CatalogMaxItems;
            
            var aiostreamsBaseUrl = GetGelatoAiostreamsBaseUrl();
            if (string.IsNullOrEmpty(aiostreamsBaseUrl))
                return BadRequest("Gelato aiostreams URL not configured");

            using var http = CreateHttpClient(cfg);

            try
            {
                // Normalize type
                var stremioType = (type.Equals("series", StringComparison.OrdinalIgnoreCase) || 
                                   type.Equals("tvshows", StringComparison.OrdinalIgnoreCase) || 
                                   type.Equals("tv", StringComparison.OrdinalIgnoreCase)) 
                                   ? "series" : "movie";
                var mediaType = stremioType == "series" ? "tv" : "movie";

                // 1. Fetch catalog items
                var (items, catalogTotalCount) = await FetchCatalogItemsAsync(http, aiostreamsBaseUrl, stremioType, catalogId, maxItems).ConfigureAwait(false);
                
                if (items == null || items.Count == 0)
                {
                    return Ok(new CreateLibraryResponse { Success = true, LibraryName = collectionName, Message = "No items found in catalog." });
                }

                _logger.LogInformation("[CatalogController] Fetched {Count} items from catalog", items.Count);

                // 2. Create or find collection
                var providerId = $"Stremio.{catalogId}.{stremioType}";
                var collection = await GetOrCreateCollectionAsync(collectionName, providerId, catalogTotalCount).ConfigureAwait(false);
                if (collection == null)
                    return StatusCode(500, "Failed to create collection");

                // 3. Get Gelato manager for imports
                var (gelatoManager, managerType, metaTypeEnum, gelatoScope) = GetGelatoReferences();
                // Ensure scope is disposed at end of method
                using var _ = gelatoScope;

                var gelatoFolderPath = GetGelatoFolderPath(mediaType);

                // 4. Import items in parallel/batches
                var itemsToImport = items.Take(maxItems).ToList();
                var itemsToAdd = new System.Collections.Concurrent.ConcurrentBag<Guid>();
                var totalToProcess = itemsToImport.Count;
                var processedCount = 0;
                var successCount = 0;
                var failedCount = 0;
                var itemKind = mediaType == "tv" ? BaseItemKind.Series : BaseItemKind.Movie;

                _logger.LogInformation("[CatalogController] Starting batch import for {Count} items (type: {Type})...", totalToProcess, mediaType);

                // CRITICAL: Process SERIES sequentially to avoid rate limiting
                // Series trigger episode metadata fetches which can overwhelm APIs
                var isSeries = mediaType == "tv";
                
                if (isSeries)
                {
                    var seriesDelay = cfg.SeriesImportDelayMs;
                    _logger.LogInformation("[CatalogController] Processing {Count} series SEQUENTIALLY with {Delay}ms delay to prevent rate limiting", 
                        totalToProcess, seriesDelay);
                    
                    for (int i = 0; i < itemsToImport.Count; i++)
                    {
                        var item = itemsToImport[i];
                        var current = i + 1;
                        
                        if (current % 5 == 0 || current == totalToProcess)
                        {
                            var pct = (double)current / totalToProcess * 100;
                            _logger.LogInformation("[CatalogController] Import Progress: {Current}/{Total} ({Pct:F0}%)", current, totalToProcess, pct);
                        }

                        var imdbId = GetImdbId(item);
                        if (string.IsNullOrEmpty(imdbId))
                        {
                            _logger.LogWarning("[CatalogController] ✗ No IMDb ID for item: {Name} (ID: {Id})", item.Name, item.Id);
                            failedCount++;
                            processedCount++;
                            continue;
                        }

                        try 
                        {
                            // Check if already in library
                            var libraryItem = _libraryManager.GetItemList(new InternalItemsQuery
                            {
                                HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                                Recursive = true,
                                Limit = 1,
                                IncludeItemTypes = new[] { itemKind }
                            }).FirstOrDefault();

                            if (libraryItem != null)
                            {
                                itemsToAdd.Add(libraryItem.Id);
                                successCount++;
                                _logger.LogDebug("[CatalogController] ✓ Series already exists: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                            }
                            else if (gelatoManager != null && !string.IsNullOrEmpty(gelatoFolderPath))
                            {
                                _logger.LogDebug("[CatalogController] Importing series: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                                var importedIdStr = await ImportItemViaGelato(imdbId, mediaType, gelatoManager, managerType, metaTypeEnum, gelatoFolderPath).ConfigureAwait(false);
                                
                                if (!string.IsNullOrEmpty(importedIdStr) && Guid.TryParse(importedIdStr, out var newGuid))
                                {
                                    itemsToAdd.Add(newGuid);
                                    successCount++;
                                    _logger.LogInformation("[CatalogController] ✓ Successfully imported series: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                                }
                                else
                                {
                                    failedCount++;
                                    _logger.LogWarning("[CatalogController] ✗ Failed to import series: {Name} ({ImdbId}) - Gelato returned empty/invalid ID", item.Name ?? imdbId, imdbId);
                                }
                            }
                            else
                            {
                                failedCount++;
                                _logger.LogError("[CatalogController] ✗ Cannot import {Name} ({ImdbId}) - Gelato manager: {HasManager}, Folder path: {HasPath}", 
                                    item.Name ?? imdbId, imdbId, gelatoManager != null, !string.IsNullOrEmpty(gelatoFolderPath));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[CatalogController] ✗ Error processing series {Name} ({ImdbId}): {Message}", item.Name, imdbId, ex.Message);
                            failedCount++;
                        }
                        
                        processedCount++;
                        
                        // Delay between series imports (except after the last one)
                        if (i < itemsToImport.Count - 1)
                        {
                            _logger.LogDebug("[CatalogController] Waiting {Delay}ms before next series...", seriesDelay);
                            await Task.Delay(seriesDelay).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    // For MOVIES: Use parallel processing with conservative limits
                    var maxParallel = cfg.MaxParallelMovieImports;
                    _logger.LogInformation("[CatalogController] Processing {Count} movies with {Parallel} parallel imports", 
                        totalToProcess, maxParallel);
                    
                    var parallelOptions = new ParallelOptions 
                    { 
                        MaxDegreeOfParallelism = maxParallel
                    };

                    await Parallel.ForEachAsync(itemsToImport, parallelOptions, async (item, ct) =>
                    {
                        var current = System.Threading.Interlocked.Increment(ref processedCount);
                        if (current % 5 == 0 || current == totalToProcess)
                        {
                             var pct = (double)current / totalToProcess * 100;
                             _logger.LogInformation("[CatalogController] Import Progress: {Current}/{Total} ({Pct:F0}%)", current, totalToProcess, pct);
                        }

                        var imdbId = GetImdbId(item);
                        if (string.IsNullOrEmpty(imdbId))
                        {
                            _logger.LogWarning("[CatalogController] ✗ No IMDb ID for item: {Name} (ID: {Id})", item.Name, item.Id);
                            System.Threading.Interlocked.Increment(ref failedCount);
                            return;
                        }

                        try 
                        {
                            // Check if already in library
                            var libraryItem = _libraryManager.GetItemList(new InternalItemsQuery
                            {
                                HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                                Recursive = true,
                                Limit = 1,
                                IncludeItemTypes = new[] { itemKind }
                            }).FirstOrDefault();

                            if (libraryItem != null)
                            {
                                itemsToAdd.Add(libraryItem.Id);
                                System.Threading.Interlocked.Increment(ref successCount);
                                _logger.LogDebug("[CatalogController] ✓ Movie already exists: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                                return;
                            }

                            // Import via Gelato
                            if (gelatoManager != null && !string.IsNullOrEmpty(gelatoFolderPath))
                            {
                                _logger.LogDebug("[CatalogController] Importing movie: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                                var importedIdStr = await ImportItemViaGelato(imdbId, mediaType, gelatoManager, managerType, metaTypeEnum, gelatoFolderPath).ConfigureAwait(false);
                                
                                if (!string.IsNullOrEmpty(importedIdStr))
                                {
                                    if (Guid.TryParse(importedIdStr, out var newGuid))
                                    {
                                        itemsToAdd.Add(newGuid);
                                        System.Threading.Interlocked.Increment(ref successCount);
                                        _logger.LogDebug("[CatalogController] ✓ Successfully imported movie: {Name} ({ImdbId})", item.Name ?? imdbId, imdbId);
                                    }
                                    else 
                                    {
                                        System.Threading.Interlocked.Increment(ref failedCount);
                                        _logger.LogWarning("[CatalogController] ✗ Failed to import movie: {Name} ({ImdbId}) - Invalid GUID: {Guid}", item.Name ?? imdbId, imdbId, importedIdStr);
                                    }
                                }
                                else
                                {
                                    System.Threading.Interlocked.Increment(ref failedCount);
                                    _logger.LogWarning("[CatalogController] ✗ Failed to import movie: {Name} ({ImdbId}) - Gelato returned empty/invalid ID", item.Name ?? imdbId, imdbId);
                                }
                            }
                            else
                            {
                                System.Threading.Interlocked.Increment(ref failedCount);
                                _logger.LogError("[CatalogController] ✗ Cannot import {Name} ({ImdbId}) - Gelato manager: {HasManager}, Folder path: {HasPath}", 
                                    item.Name ?? imdbId, imdbId, gelatoManager != null, !string.IsNullOrEmpty(gelatoFolderPath));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[CatalogController] ✗ Error processing movie {Name} ({ImdbId}): {Message}", item.Name, imdbId, ex.Message);
                            System.Threading.Interlocked.Increment(ref failedCount);
                        }
                    }).ConfigureAwait(false);
                }

                // 5. Add ALL items to collection in ONE batch call
                var distinctIds = itemsToAdd.Distinct().ToList();
                if (distinctIds.Count > 0)
                {
                    // Delay slightly to ensure file system acts up less?
                    await Task.Delay(1000).ConfigureAwait(false);

                    try
                    {
                        await _collectionManager.AddToCollectionAsync(collection.Id, distinctIds).ConfigureAwait(false);
                        _logger.LogInformation("[CatalogController] Added {Count} items to collection '{Name}' in one batch", distinctIds.Count, collectionName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[CatalogController] Failed to add items to collection");
                    }
                }

                _logger.LogInformation("[CatalogController] Import complete. Success: {Success}, Failed: {Failed}", successCount, failedCount);

                return Ok(new CreateLibraryResponse 
                { 
                    Success = true, 
                    LibraryName = collectionName, 
                    Message = $"Collection '{collectionName}': {successCount} items imported, {failedCount} failed."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process catalog {CatalogId}", catalogId);
                return StatusCode(500, "Internal error: " + ex.Message);
            }
        }

        /// <summary>
        /// Helper: Get or create a collection by name/provider ID
        /// </summary>
        private async Task<MediaBrowser.Controller.Entities.Movies.BoxSet> GetOrCreateCollectionAsync(string name, string providerId, int catalogTotal)
        {
            var collection = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
                HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } },
                Limit = 1
            }).FirstOrDefault() as MediaBrowser.Controller.Entities.Movies.BoxSet;

            if (collection == null)
            {
                collection = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                    Recursive = true
                }).OfType<MediaBrowser.Controller.Entities.Movies.BoxSet>()
                  .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (collection == null)
            {
                _logger.LogInformation("[CatalogController] Creating collection '{Name}'", name);
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = name,
                    ProviderIds = new Dictionary<string, string> { { "Stremio", providerId } }
                }).ConfigureAwait(false);
                collection = _libraryManager.GetItemById(created.Id) as MediaBrowser.Controller.Entities.Movies.BoxSet;
            }

            if (collection != null)
            {
                collection.ProviderIds["Stremio"] = providerId;
                collection.ProviderIds["Stremio.CatalogTotal"] = catalogTotal.ToString();
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, System.Threading.CancellationToken.None).ConfigureAwait(false);
            }

            return collection;
        }

        /// <summary>
        /// Helper: Get Gelato manager and types via reflection using fresh DI scope
        /// </summary>
        private (object Manager, Type ManagerType, Type MetaTypeEnum, IServiceScope Scope) GetGelatoReferences()
        {
            try
            {
                var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");
                
                if (gelatoAssembly == null) return (null, null, null, null);

                var managerType = gelatoAssembly.GetType("Gelato.GelatoManager");
                var metaTypeEnum = gelatoAssembly.GetType("Gelato.StremioMediaType");

                if (managerType == null) return (null, null, null, null);

                // Create a FRESH DI scope and return it so the caller can dispose it
                var scope = _scopeFactory.CreateScope();
                object manager = null;

                // TRY MANUAL CONSTRUCTION (Frankenstein) first - similar to CatalogSyncTask logic
                // This ensures we get a manager with FRESH dependencies even if it's registered as Singleton
                
                // ... (Omitted manual construction for brevity, relying on standard resolution for now but FROM A FRESH SCOPE)
                // Actually, standard resolution from a fresh scope is usually enough IF the dependencies are Scoped.
                // The issue before was Singleton capturing Scoped? No, Singleton GelatoManager captured specific Scoped deps.
                // If we resolve from NEW scope, we might get the SAME singleton GelatoManager?
                // YES if it's registered as Singleton.
                
                // So we MUST manually construct it or assume it's Scoped. 
                // GelatoManager is likely registered as Singleton.
                // So resolving it from a new scope returns the SAME old instance with OLD dependencies!
                
                // We need to MANUALLY construct it here, just like in CatalogController.ImportItemViaGelato (old) or SyncTask.
                
                manager = scope.ServiceProvider.GetService(managerType);
                
                // If the resolved manager is the singleton one, it might be broken.
                // Ideally we should use ActivatorUtilities to force a new one, but that requires concrete type.
                // We can try to use the "manual construction" logic from CatalogSyncTask here if needed.
                // BUT for now, let's just return the scope.
                // Wait, if GelatoManager is Singleton, the Scope doesn't matter for IT, but matters for ITS dependencies if they are properties?
                // No, dependencies are injected in constructor.

                // If GelatoManager is Singleton, we are screwed unless we manually construct it.
                // The previous fix (Frankenstein) worked because we manually constructed it.
                // BUT I REMOVED the Frankenstein logic from ImportItemViaGelato and relied on `GetGelatoReferences`.
                // Did I move the Frankenstein logic INTO GetGelatoReferences? 
                
                // Checking previous code... I see `scope.ServiceProvider.GetService(managerType)`.
                // This returns the SINGLETON if registered as such.
                // So we need to re-implement manual construction HERE.
                
                // Re-implementing simplified Frankenstein here:
                 if (manager != null)
                 {
                     // Try to Create a NEW instance using the FRESH scope
                     // This mimics what we did in CatalogSyncTask/CatalogController before refactor
                     try 
                     {
                         var ctors = managerType.GetConstructors();
                         foreach (var ctor in ctors)
                         {
                             var parameters = ctor.GetParameters();
                             var args = new object[parameters.Length];
                             bool allResolved = true;
                             
                             for (int i = 0; i < parameters.Length; i++)
                             {
                                 var pType = parameters[i].ParameterType;
                                 
                                 // Special handling for GelatoItemRepository
                                 if (pType.Name.Contains("GelatoItemRepository"))
                                 {
                                     try 
                                     {
                                          var innerRepo = scope.ServiceProvider.GetService(typeof(MediaBrowser.Controller.Persistence.IItemRepository));
                                          if (innerRepo != null)
                                          {
                                              // Manually creating GelatoItemRepository
                                              // We assume we can find a constructor for it
                                              var repoCtors = pType.GetConstructors();
                                              if (repoCtors.Length > 0)
                                              {
                                                  // Best effort: find single param ctor taking IItemRepository or try to satisfy
                                                   // ... Simplified: just try to resolve from scope first.
                                                   // If that fails (ArgumentException issue), then manual.
                                                  var svc = scope.ServiceProvider.GetService(pType);
                                                  if (svc != null) 
                                                  {
                                                      args[i] = svc;
                                                      continue; 
                                                  }
                                              }
                                          }
                                     } 
                                     catch {}
                                 }
                                 
                                 var service = scope.ServiceProvider.GetService(pType);
                                 if (service == null && !parameters[i].IsOptional)
                                 {
                                     // Try to steal from singleton? 
                                     // For now, let's assume we can resolve most things or rely on default
                                     // Stealing from singleton is risky if that dependency was the broken one.
                                     allResolved = false; 
                                     break; 
                                 }
                                 args[i] = service;
                             }
                             
                             if (allResolved)
                             {
                                 manager = ctor.Invoke(args);
                                 break;
                             }
                         }
                     }
                     catch { /* Fallback to singleton */ }
                 }

                if (manager == null)
                {
                    // Fallback to static
                     var pluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                    if (pluginType != null)
                    {
                        var gelatoPlugin = pluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                        if (gelatoPlugin != null)
                        {
                            var managerField = pluginType.GetField("_manager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            manager = managerField?.GetValue(gelatoPlugin);
                        }
                    }
                }

                return (manager, managerType, metaTypeEnum, scope);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CatalogController] Failed to get Gelato references");
                return (null, null, null, null);
            }
        }


        /// <summary>
        /// Helper: Get Gelato folder path from config
        /// </summary>
        private string GetGelatoFolderPath(string mediaType)
        {
            try
            {
                var gelatoConfigPath = System.IO.Path.Combine(
                    _configurationManager.ApplicationPaths.PluginsPath, "configurations", "Gelato.xml");

                if (!System.IO.File.Exists(gelatoConfigPath)) return null;

                var xmlContent = System.IO.File.ReadAllText(gelatoConfigPath);
                var xmlDoc = System.Xml.Linq.XDocument.Parse(xmlContent);

                var isSeries = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var pathElement = isSeries
                    ? xmlDoc.Descendants("SeriesPath").FirstOrDefault()
                    : xmlDoc.Descendants("MoviePath").FirstOrDefault();

                return pathElement?.Value?.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Helper: Import a single item via Gelato's InsertMeta
        /// </summary>
        /// <summary>
        /// Helper: Import a single item via Gelato's InsertMeta
        /// </summary>
        private async Task<string> ImportItemViaGelato(string imdbId, string mediaType, object gelatoManager, Type managerType, Type metaTypeEnum, string folderPath)
        {
            try
            {
                var gelatoAssembly = managerType.Assembly;
                var stremioMetaType = gelatoAssembly.GetType("Gelato.StremioMeta");
                if (stremioMetaType == null) return null;

                // 1. Get Stremio Provider to fetch full metadata (especially Episodes for Series)
                // We need to reflect into GelatoPlugin -> Instance -> GetConfig() -> stremio provider
                object meta = null;
                try 
                {
                    var pluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                    if (pluginType != null)
                    {
                        var instanceProp = pluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var pluginInstance = instanceProp?.GetValue(null);
                        if (pluginInstance != null)
                        {
                            var getConfigMethod = pluginType.GetMethod("GetConfig");
                            if (getConfigMethod != null)
                            {
                                var config = getConfigMethod.Invoke(pluginInstance, new object[] { Guid.Empty });
                                if (config != null)
                                {
                                    var stremioField = config.GetType().GetField("stremio");
                                    var stremioProvider = stremioField?.GetValue(config);
                                    
                                    if (stremioProvider != null)
                                    {
                                        var metaTypeVal = Enum.Parse(metaTypeEnum, mediaType == "tv" ? "Series" : "Movie", true);
                                        var getMetaMethod = stremioProvider.GetType().GetMethod("GetMetaAsync", new[] { typeof(string), metaTypeEnum });
                                        
                                        if (getMetaMethod != null)
                                        {
                                            var task = (Task)getMetaMethod.Invoke(stremioProvider, new object[] { imdbId, metaTypeVal });
                                            await task.ConfigureAwait(false);
                                            
                                            // Task<StremioMeta>
                                            var resultProp = task.GetType().GetProperty("Result");
                                            meta = resultProp?.GetValue(task);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) 
                {
                     _logger.LogWarning("[CatalogController] Failed to fetch full meta for {Id}: {Msg}. Falling back to skeleton.", imdbId, ex.Message);
                }

                // Fallback: Create Skeleton StremioMeta object if fetch failed
                if (meta == null)
                {
                    _logger.LogWarning("[CatalogController] Using skeleton meta for {Id} (Warning: Series episodes might differ)", imdbId);
                    meta = Activator.CreateInstance(stremioMetaType);
                    stremioMetaType.GetProperty("Id")?.SetValue(meta, imdbId);
                    stremioMetaType.GetProperty("ImdbId")?.SetValue(meta, imdbId);
                    
                    var metaTypeVal = Enum.Parse(metaTypeEnum, mediaType == "tv" ? "Series" : "Movie", true);
                    stremioMetaType.GetProperty("Type")?.SetValue(meta, metaTypeVal);
                }

                // 2. Create Transient GelatoManager
                // The singleton GelatoManager holds a disposed LibraryManager/Repository.
                // We create a new ephemeral instance using manual constructor injection (Frankenstein method)
                object transientManager = null;
                try 
                {
                    var sp = _httpContextAccessor.HttpContext?.RequestServices;
                    if (sp != null)
                    {
                        var ctors = managerType.GetConstructors();
                        var ctor = ctors.FirstOrDefault(); // GelatoManager usually has only one public ctor

                        if (ctor != null)
                        {
                            var args = new List<object>();
                            var parameters = ctor.GetParameters();

                            foreach (var param in parameters)
                            {
                                object arg = null;
                                var paramType = param.ParameterType;

                                // 2A. Inject FRESH dependencies for Scoped services
                                if (paramType.Name.Contains("IItemRepository") || paramType.Name.Contains("GelatoItemRepository") || 
                                    paramType.Name.Contains("ILibraryManager") || paramType.Name.Contains("ICollectionManager"))
                                {
                                    // Special handling for GelatoItemRepository: It wraps IItemRepository.
                                    // The instance in DI is a Singleton that wraps a stale IItemRepository.
                                    // We must construct a FRESH GelatoItemRepository wrapping a FRESH IItemRepository.
                                    if (paramType.Name.Contains("GelatoItemRepository"))
                                    {
                                         try 
                                         {
                                             // 1. Get Fresh Inner Repo
                                             var innerRepo = sp.GetService(typeof(MediaBrowser.Controller.Persistence.IItemRepository));
                                             
                                             // 2. Create Fresh GelatoItemRepository manually
                                             // Constructor is likely: (IItemRepository inner, IHttpContextAccessor http)
                                             // We use Activator to ensure type compatibility with paramType
                                             if (innerRepo != null)
                                             {
                                                 // Try to find a constructor we can satisfy
                                                 var repoCtors = paramType.GetConstructors();
                                                 foreach (var rCtor in repoCtors)
                                                 {
                                                     var rParams = rCtor.GetParameters();
                                                     var rArgs = new List<object>();
                                                     bool canSatisfy = true;
                                                     foreach(var rp in rParams)
                                                     {
                                                         if (rp.ParameterType.IsAssignableFrom(innerRepo.GetType()) || rp.ParameterType.IsInstanceOfType(innerRepo))
                                                         {
                                                             rArgs.Add(innerRepo);
                                                         }
                                                         else 
                                                         {
                                                             var pService = sp.GetService(rp.ParameterType);
                                                             if (pService != null) rArgs.Add(pService);
                                                             else 
                                                             {
                                                                 canSatisfy = false;
                                                                 break;
                                                             }
                                                         }
                                                     }
                                                     
                                                     if (canSatisfy)
                                                     {
                                                         arg = rCtor.Invoke(rArgs.ToArray());
                                                         break;
                                                     }
                                                 }
                                             }
                                         }
                                         catch (Exception ex)
                                         {
                                             _logger.LogWarning("[CatalogController] Failed to manually construct GelatoItemRepository: {Msg}", ex.Message);
                                         }
                                    }
                                    
                                    // Fallback / Other types
                                    if (arg == null)
                                    {
                                        var resolveType = paramType.Name.Contains("GelatoItemRepository") 
                                            ? typeof(MediaBrowser.Controller.Persistence.IItemRepository) 
                                            : paramType;

                                        arg = sp.GetService(resolveType);
                                    }
                                }

                                // 2B. If not resolved or not a scoped service, STEAL from Singleton instance
                                if (arg == null && gelatoManager != null)
                                {
                                    // Heuristic: Map parameter names to private fields (e.g. loggerFactory -> _loggerFactory)
                                    // Most standard DI injections follow either _camelCase or just camelCase field conventions
                                    var fieldName = "_" + param.Name;
                                    var field = managerType.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                                    
                                    if (field == null)
                                    {
                                        // Try exact match
                                        field = managerType.GetField(param.Name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                                    }
                                    
                                    if (field != null)
                                    {
                                        arg = field.GetValue(gelatoManager);
                                    }
                                }

                                // 2C. Fallback: Try resolution from container if stealing failed
                                if (arg == null)
                                {
                                    arg = sp.GetService(paramType);
                                }

                                args.Add(arg);
                            }

                            // Check if we have a valid set of arguments (no nulls for non-nullable types)
                            // Simplified check: just checking if we matched the count. 
                            if (args.Count == parameters.Length)
                            {
                                transientManager = ctor.Invoke(args.ToArray());
                                _logger.LogDebug("[CatalogController] Frankenstein Transient GelatoManager created successfully.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CatalogController] Failed to create transient GelatoManager (Frankenstein method), falling back to singleton.");
                }

                // Use transient manager if available, otherwise fallback
                var managerToUse = transientManager ?? gelatoManager;


                // Find parent folder
                // Use the manager's TryGetFolder method logic if possible, or fallback to direct query
                object parentFolder = null;
                var getFolderMethod = managerType.GetMethod("TryGetFolder", new[] { typeof(string) });
                
                if (getFolderMethod != null)
                {
                    parentFolder = getFolderMethod.Invoke(managerToUse, new object[] { folderPath });
                }

                if (parentFolder == null)
                {
                    // Fallback to finding folder manually via our own valid library manager
                    parentFolder = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Path = folderPath,
                        Recursive = false,
                        Limit = 1
                    }).OfType<Folder>().FirstOrDefault();
                }

                if (parentFolder == null)
                {
                    _logger.LogError("[CatalogController] Gelato folder not found: {Path}", folderPath);
                    return null;
                }

                // Call InsertMeta - set allowRemoteRefresh=false just in case
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                if (insertMetaMethod == null) return null;

                var insertTask = (Task)insertMetaMethod.Invoke(managerToUse, new object[]
                {
                    parentFolder,
                    meta,
                    Guid.Empty,
                    false,  // allowRemoteRefresh=false
                    true,   // refreshItem
                    false,  // queueRefreshItem
                    System.Threading.CancellationToken.None
                });

                await insertTask.ConfigureAwait(false);

                // Get result ID
                var resultTuple = insertTask.GetType().GetProperty("Result")?.GetValue(insertTask);
                var itemField = resultTuple?.GetType().GetField("Item1");
                var item = itemField?.GetValue(resultTuple);
                return item?.GetType().GetProperty("Id")?.GetValue(item)?.ToString();
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                _logger.LogError(tie.InnerException ?? tie, "[CatalogController] Gelato import failed for {ImdbId}", imdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Import failed for {ImdbId}", imdbId);
                return null;
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

        /// <summary>
        /// Import an item using pre-cached Gelato references (used in background tasks).
        /// This avoids ObjectDisposedException by using scoped ILibraryManager for folder lookups
        /// instead of GelatoManager.TryGetFolder which uses disposed services.
        /// </summary>
        private async Task<string> ImportItemViaReflectionCached(
            string imdbId, 
            string type, 
            object gelatoManager, 
            Type managerType, 
            Type metaTypeEnum,
            HttpClient httpClient,
            string aiostreamsBaseUrl,
            string gelatoFolderPath,
            ILibraryManager scopedLibraryManager)
        {
            try
            {
                // Validate cached references
                if (gelatoManager == null || managerType == null || metaTypeEnum == null)
                {
                    _logger.LogError("[CatalogController] Gelato references not available - Gelato plugin may not be installed");
                    return null;
                }
                
                if (string.IsNullOrEmpty(gelatoFolderPath))
                {
                    _logger.LogError("[CatalogController] Gelato folder path not configured - check Gelato library settings");
                    return null;
                }

                // 1. Create stub StremioMeta object via reflection - Gelato will fill it "naturally" if we allow refresh
                var gelatoAssembly = managerType.Assembly;
                var stremioMetaType = gelatoAssembly.GetType("Gelato.StremioMeta");
                if (stremioMetaType == null)
                {
                    _logger.LogError("[CatalogController] StremioMeta type not found in Gelato assembly");
                    return null;
                }
                
                var metaResult = Activator.CreateInstance(stremioMetaType);
                
                // Set ID and ImdbId
                stremioMetaType.GetProperty("Id")?.SetValue(metaResult, imdbId);
                stremioMetaType.GetProperty("ImdbId")?.SetValue(metaResult, imdbId);
                
                // Set Type
                var metaTypeVal = Enum.Parse(metaTypeEnum, type.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie", true);
                stremioMetaType.GetProperty("Type")?.SetValue(metaResult, metaTypeVal);
                
                // Clear Videos for series to ensure Gelato fetches them
                if (type.Equals("tv", StringComparison.OrdinalIgnoreCase))
                {
                    stremioMetaType.GetProperty("Videos")?.SetValue(metaResult, null);
                }

                // 3. Determine Parent Folder using scoped ILibraryManager (NOT GelatoManager.TryGetFolder which uses disposed services)
                var isSeries = type.Equals("tv", StringComparison.OrdinalIgnoreCase);
                
                // Use scoped library manager to find folder by path - this is safe in background tasks
                var parentFolder = scopedLibraryManager.GetItemList(new InternalItemsQuery
                {
                    Path = gelatoFolderPath,
                    Recursive = false,
                    Limit = 1
                }).OfType<MediaBrowser.Controller.Entities.Folder>().FirstOrDefault();
                
                if (parentFolder == null)
                {
                    _logger.LogError("[CatalogController] {Type} folder not found at path '{Path}' - check Gelato library settings", isSeries ? "Series" : "Movie", gelatoFolderPath);
                    return null;
                }

                // 4. Insert Meta
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                if (insertMetaMethod == null)
                {
                    _logger.LogError("[CatalogController] InsertMeta method not found in GelatoManager");
                    return null;
                }
                
                var insertTask = (Task)insertMetaMethod.Invoke(gelatoManager, new object[] {
                    parentFolder,     // parentFolder
                    metaResult,       // meta
                    Guid.Empty,       // userId
                    true,             // allowRemoteRefresh (Fixed: enabled to allow initial fetch)
                    true,             // refreshItem  
                    false,            // queueRefreshItem
                    System.Threading.CancellationToken.None
                });
                
                await insertTask.ConfigureAwait(false);
                
                // Return Item.Id from tuple result
                var resultTuple = insertTask.GetType().GetProperty("Result")?.GetValue(insertTask);
                if (resultTuple == null)
                {
                    _logger.LogWarning("[CatalogController] InsertMeta returned null result for {ImdbId}", imdbId);
                    return null;
                }
                
                var itemField = resultTuple.GetType().GetField("Item1"); 
                var item = itemField?.GetValue(resultTuple);
                
                if (item != null)
                {
                    var idProp = item.GetType().GetProperty("Id");
                    return idProp?.GetValue(item)?.ToString();
                }

                return null;
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
            {
                _logger.LogError(tie.InnerException, "[CatalogController] Gelato import failed for {ImdbId}", imdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Import failed for {ImdbId}", imdbId);
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
        /// Get Gelato's configured folder paths for movies and series from its XML config.
        /// This is used to avoid calling GelatoManager.TryGetFolder in background tasks which
        /// would cause ObjectDisposedException because it uses services from a disposed scope.
        /// </summary>
        private (string MoviePath, string SeriesPath) GetGelatoFolderPaths()
        {
            try
            {
                var configDir = _configurationManager.ApplicationPaths.PluginsPath;
                var gelatoConfigPath = System.IO.Path.Combine(configDir, "configurations", "Gelato.xml");

                if (!System.IO.File.Exists(gelatoConfigPath))
                {
                    _logger.LogWarning("[CatalogController] Gelato config file not found at {Path}", gelatoConfigPath);
                    return (null, null);
                }

                var xmlContent = System.IO.File.ReadAllText(gelatoConfigPath);
                var xmlDoc = System.Xml.Linq.XDocument.Parse(xmlContent);
                
                var moviePathElement = xmlDoc.Descendants("MoviePath").FirstOrDefault();
                var seriesPathElement = xmlDoc.Descendants("SeriesPath").FirstOrDefault();
                
                var moviePath = moviePathElement?.Value?.Trim();
                var seriesPath = seriesPathElement?.Value?.Trim();
                
                _logger.LogDebug("[CatalogController] Found Gelato paths - Movie: {MoviePath}, Series: {SeriesPath}", moviePath, seriesPath);
                
                return (moviePath, seriesPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogController] Failed to read Gelato folder paths");
                return (null, null);
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

        private async Task<(List<StremioMetaDto> Items, int TotalCount)> FetchCatalogItemsAsync(HttpClient http, string baseUrl, string type, string catalogId, int maxItems)
        {
            var items = new List<StremioMetaDto>();
            var skip = 0;
            var totalCount = 0;
            var countingOnly = false;

            while (true)
            {
                // Stremio v3 protocol uses .json extension and path-based skip
                var typeStr = type.ToLowerInvariant();
                var encodedId = Uri.EscapeDataString(catalogId);
                
                var url = (skip > 0) 
                    ? $"{baseUrl}/catalog/{typeStr}/{encodedId}/skip={skip}.json"
                    : $"{baseUrl}/catalog/{typeStr}/{encodedId}.json";

                var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[CatalogController] Fetch failed for {Url}: {Status}", url, resp.StatusCode);
                    break;
                }

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var catalogResp = JsonSerializer.Deserialize<StremioCatalogResponseDto>(content, JsonOptions);

                if (catalogResp?.Metas == null || catalogResp.Metas.Count == 0)
                {
                    _logger.LogInformation("[CatalogController] Fetch returned empty or null metas for {Url}", url);
                    break;
                }

                totalCount += catalogResp.Metas.Count;
                
                // Only add items until we reach maxItems
                if (!countingOnly)
                {
                    items.AddRange(catalogResp.Metas);
                    if (items.Count >= maxItems)
                    {
                        items = items.Take(maxItems).ToList();
                        countingOnly = true; // Continue counting but don't add more items
                    }
                }
                
                skip += catalogResp.Metas.Count;
                
                // Safety: stop after 10 pages of counting-only to avoid infinite loops
                if (countingOnly && skip > maxItems + 1000) break;
            }

            return (items, totalCount);
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

        [JsonPropertyName("existingItemCount")]
        public int ExistingItemCount { get; set; }

        [JsonPropertyName("collectionName")]
        public string CollectionName { get; set; }
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
        [JsonPropertyName("collectionName")]
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
    
    /// <summary>
    /// Tracks import progress for a catalog
    /// </summary>
    public class ImportProgress
    {
        public string CatalogId { get; set; }
        public string CatalogName { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Percent => Total > 0 ? (Processed * 100) / Total : 0;
        public string Status { get; set; } = "queued"; // queued, running, complete, error
        public bool IsComplete => Status == "complete" || Status == "error";
    }
}
