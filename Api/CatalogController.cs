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
        private readonly ILibraryManager _libraryManager; // Added
        private readonly ICollectionManager _collectionManager; // Added
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
            IHttpContextAccessor httpContextAccessor) // Added
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _serverApplicationHost = serverApplicationHost;
            _configurationManager = configurationManager;
            _scopeFactory = scopeFactory;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager; // Added
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
        /// Create a new Jellyfin library from a catalog
        /// </summary>
        /// <summary>
        /// Create a Jellyfin Collection (BoxSet) from a Stremio Catalog
        /// </summary>
        [HttpPost("{catalogId}/library")] // Keeping endpoint name for frontend compatibility
        public async Task<ActionResult<CreateLibraryResponse>> CreateLibraryFromCatalog(
            string catalogId,
            [FromQuery] string type = "movie",
            [FromQuery] bool separate = false,
            [FromBody] CreateLibraryRequest request = null)
        {
            _logger.LogInformation("[CatalogController] Request received - LibraryName: '{LibName}', MaxItems: {Max}, Separate: {Separate}", 
                request?.LibraryName ?? "(null)", request?.MaxItems ?? -1, separate);
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
                var (items, catalogTotalCount) = await FetchCatalogItemsAsync(http, aiostreamsBaseUrl, stremioType, catalogId, maxItems).ConfigureAwait(false);
                
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

                _logger.LogInformation("[CatalogController] Catalog {CatalogId} ({Type}) has {Total} total items. Importing max {Max}...", catalogId, stremioType, catalogTotalCount, maxItems);

                // 2. Start Background Process: Create Collection FIRST, then Import & Add each item one by one
                // IMPORTANT: Cache Gelato references BEFORE entering background task to avoid ObjectDisposedException
                object gelatoManager = null;
                object gelatoPlugin = null;
                Type managerType = null;
                Type metaTypeEnum = null;
                
                try
                {
                    var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Gelato");
                    
                    if (gelatoAssembly != null)
                    {
                        var pluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                        managerType = gelatoAssembly.GetType("Gelato.GelatoManager");
                        metaTypeEnum = gelatoAssembly.GetType("Gelato.StremioMediaType");
                        
                        if (pluginType != null)
                        {
                            gelatoPlugin = pluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                            if (gelatoPlugin != null)
                            {
                                var managerField = pluginType.GetField("_manager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                gelatoManager = managerField?.GetValue(gelatoPlugin);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CatalogController] Failed to cache Gelato references - imports will only work for existing library items");
                }
                
                // IMPORTANT: Create HttpClient BEFORE background task to avoid ObjectDisposedException
                // IHttpClientFactory uses IServiceProvider internally which will be disposed after response
                var backgroundHttpClient = _httpClientFactory.CreateClient();
                backgroundHttpClient.Timeout = TimeSpan.FromSeconds(300);
                
                // CRITICAL: Cache Gelato folder paths BEFORE background task to avoid ObjectDisposedException
                // We'll use these paths with a scoped ILibraryManager inside the background task
                var (cachedMoviePath, cachedSeriesPath) = GetGelatoFolderPaths();
                
                // Also get the aiostreams base URL now while we still have access
                var aiostreamsBaseUrlForImport = aiostreamsBaseUrl;
                
                _ = Task.Run(async () => 
                {
                    // CRITICAL: Clear HttpContext to avoid ObjectDisposedException in background thread
                    if (_httpContextAccessor != null) _httpContextAccessor.HttpContext = null;
                    
                    // Initialize progress tracking
                    var progress = new ImportProgress { CatalogId = catalogId, CatalogName = collectionName, Status = "queued", Total = items.Count, Processed = 0 };
                    _importProgress[catalogId] = progress;
                    
                    // Wait in global queue - only one catalog import at a time
                    _logger.LogInformation("[CatalogController] Catalog '{Name}' queued for import...", collectionName);
                    await _importQueue.WaitAsync().ConfigureAwait(false);
                    
                    progress.Status = "running";
                    
                    try
                    {
                        _logger.LogInformation("[CatalogController] Starting import for catalog '{Name}'", collectionName);
                        
                        using var scope = _scopeFactory.CreateScope();
                        var scopedProvider = scope.ServiceProvider;
                        var libraryManager = scopedProvider.GetRequiredService<ILibraryManager>();
                        var collectionManager = scopedProvider.GetRequiredService<ICollectionManager>();
                        
                        var successCount = 0;
                        var failedCount = 0;

                        try
                        {
                        // STEP 1: Create or find collection FIRST
                        // Format: Stremio.{CatalogId}.{Type}
                        var providerId = $"Stremio.{catalogId}.{stremioType}";
                        
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                            Recursive = true,
                            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } }
                        };
                        
                        var collection = libraryManager.GetItemList(query).FirstOrDefault() as MediaBrowser.Controller.Entities.Movies.BoxSet;

                        // 2. Fallback: Search by Name if ID not found
                        if (collection == null)
                        {
                            var existingByName = libraryManager.GetItemList(new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                                Recursive = true
                            }).OfType<MediaBrowser.Controller.Entities.Movies.BoxSet>()
                              .FirstOrDefault(b => b.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase));
                            
                            if (existingByName != null)
                            {
                                _logger.LogInformation("[CatalogController] Found existing collection by name '{Name}'. Adopting it...", existingByName.Name);
                                collection = existingByName;

                                // Ensure ProviderID is set correctly on adopted collection
                                if (!collection.ProviderIds.TryGetValue("Stremio", out var existingId) || existingId != providerId)
                                {
                                    _logger.LogInformation("[CatalogController] Updating ProviderId for adopted collection to {Id}", providerId);
                                    collection.ProviderIds["Stremio"] = providerId;
                                    await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, System.Threading.CancellationToken.None).ConfigureAwait(false);
                                }
                            }
                        }

                        // 3. Create new collection if still null
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
                        }
                        
                        // Store catalog total in collection ProviderIds for display later
                        if (collection != null)
                        {
                            var catalogTotal = catalogTotalCount.ToString();
                            if (!collection.ProviderIds.TryGetValue("Stremio.CatalogTotal", out var existing) || existing != catalogTotal)
                            {
                                collection.ProviderIds["Stremio.CatalogTotal"] = catalogTotal;
                                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, System.Threading.CancellationToken.None).ConfigureAwait(false);
                                _logger.LogInformation("[CatalogController] Stored catalog total: {Total}", catalogTotal);
                            }
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

                        // Diagnostic counters
                        var failedNoImdb = 0;
                        var failedImportError = 0;
                        var failedNotFound = 0;
                        var skippedExisting = 0;
                        
                        // Collect all items to add, then batch-add at the end
                        var itemsToAdd = new List<Guid>();
                        var totalToProcess = Math.Min(items.Count, maxItems);
                        var processedCount = 0;
                        var lastLoggedPercent = 0;

                        // STEP 2: Process each item - import if needed, collect IDs
                        foreach (var item in items.Take(maxItems))
                        {
                            processedCount++;
                            progress.Processed = processedCount; // Update for polling
                            
                            // Log progress only at 25%, 50%, 75% milestones
                            var currentPercent = (processedCount * 100) / totalToProcess;
                            if (currentPercent >= lastLoggedPercent + 25)
                            {
                                lastLoggedPercent = (currentPercent / 25) * 25; // Round to nearest 25
                                _logger.LogInformation("[CatalogController] Progress: {Percent}% ({Current}/{Total}), {Queued} queued", 
                                    lastLoggedPercent, processedCount, totalToProcess, itemsToAdd.Count);
                            }
                            
                            try
                            {
                                var imdbId = GetImdbId(item);
                                if (string.IsNullOrEmpty(imdbId))
                                {
                                    _logger.LogDebug("[CatalogController] Item '{Name}' (id={Id}) has no IMDB ID, skipping", item.Name, item.Id);
                                    failedNoImdb++;
                                    failedCount++;
                                    continue;
                                }

                                // CHECK 1: Is it already in the collection?
                                if (existingImdbIds.Contains(imdbId))
                                {
                                    // Already exists, skip
                                    skippedExisting++;
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
                                    _logger.LogDebug("[CatalogController] Item '{Name}' ({ImdbId}) not in library, attempting Gelato import...", item.Name, imdbId);
                                    
                                    // Import via Gelato using cached references and scoped library manager
                                    var cachedFolderPath = mediaType.Equals("tv") ? cachedSeriesPath : cachedMoviePath;
                                    var importedIdStr = await ImportItemViaReflectionCached(
                                        imdbId, 
                                        mediaType, 
                                        gelatoManager, 
                                        managerType, 
                                        metaTypeEnum,
                                        backgroundHttpClient,
                                        aiostreamsBaseUrlForImport,
                                        cachedFolderPath,
                                        libraryManager).ConfigureAwait(false);
                                    
                                    if (string.IsNullOrEmpty(importedIdStr))
                                    {
                                        _logger.LogWarning("[CatalogController] Gelato import FAILED for '{Name}' ({ImdbId}) - Check Gelato plugin is installed and configured", item.Name, imdbId);
                                        failedImportError++;
                                        failedCount++;
                                        continue;
                                    }
                                    
                                    _logger.LogDebug("[CatalogController] Gelato import returned ID: {Id}", importedIdStr);
                                    
                                    // Wait for Jellyfin to register the new item
                                    await Task.Delay(500).ConfigureAwait(false);
                                    
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
                                    _logger.LogWarning("[CatalogController] Item '{Name}' ({ImdbId}) not found in library after import attempt", item.Name, imdbId);
                                    failedNotFound++;
                                    failedCount++;
                                    continue;
                                }

                                // Queue for batch addition
                                itemsToAdd.Add(libraryItem.Id);
                                successCount++;
                                _logger.LogDebug("[CatalogController] Queued '{Name}' for collection", libraryItem.Name);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[CatalogController] Failed to process item {Id}", item.Id);
                                failedCount++;
                            }
                        }

                        // STEP 3: Batch add ALL items to collection in ONE call
                        if (itemsToAdd.Count > 0)
                        {
                            _logger.LogInformation("[CatalogController] Adding {Count} items to collection in single batch...", itemsToAdd.Count);
                            
                            try
                            {
                                await collectionManager.AddToCollectionAsync(collection.Id, itemsToAdd).ConfigureAwait(false);
                                _logger.LogInformation("[CatalogController] Batch add completed successfully");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[CatalogController] Batch add failed, items may not be in collection");
                            }
                        }

                        _logger.LogInformation("[CatalogController] Import complete. Success: {Success}, Failed: {Failed} (NoImdb: {NoImdb}, ImportError: {ImportError}, NotFound: {NotFound}), Skipped (existing): {Skipped}", 
                            successCount, failedCount, failedNoImdb, failedImportError, failedNotFound, skippedExisting);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[CatalogController] Background import failed");
                        }
                    }
                    finally
                    {
                        progress.Status = "complete";
                        _importQueue.Release();
                        _logger.LogInformation("[CatalogController] Import queue released for '{Name}'", collectionName);
                        
                        // Clean up progress after a delay (let frontend poll final state)
                        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => _importProgress.TryRemove(catalogId, out ImportProgress? _removed));
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
                    parentFolder,
                    metaResult,
                    Guid.Empty, // userId
                    true, // allowRemoteRefresh
                    true, // refreshItem  
                    false, // queueRefreshItem
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
                if (!resp.IsSuccessStatusCode) break;

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var catalogResp = JsonSerializer.Deserialize<StremioCatalogResponseDto>(content, JsonOptions);

                if (catalogResp?.Metas == null || catalogResp.Metas.Count == 0) break;

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
