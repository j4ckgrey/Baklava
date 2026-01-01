#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Baklava.Tasks
{
    /// <summary>
    /// Scheduled task to sync Stremio catalogs with Jellyfin collections.
    /// Runs every 12 hours by default.
    /// Directly calls the same services used by CatalogController to bypass auth.
    /// </summary>
    public sealed class CatalogSyncTask : IScheduledTask
    {
        private readonly ILogger<CatalogSyncTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MediaBrowser.Controller.Configuration.IServerConfigurationManager _configManager;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CatalogSyncTask(
            ILogger<CatalogSyncTask> logger,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IHttpClientFactory httpClientFactory,
            IServiceScopeFactory scopeFactory,
            MediaBrowser.Controller.Configuration.IServerConfigurationManager configManager,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _httpClientFactory = httpClientFactory;
            _scopeFactory = scopeFactory;
            _configManager = configManager;
            _httpContextAccessor = httpContextAccessor;
        }

        public string Name => "Update Catalogs";
        public string Key => "BaklavaCatalogSync";
        public string Description => "Syncs all Stremio catalog collections with their source catalogs. Adds new items from the catalog.";
        public string Category => "Baklava";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks
                }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Baklava] CatalogSyncTask starting...");
            
            // CRITICAL: Clear HttpContext to avoid ObjectDisposedException
            if (_httpContextAccessor != null) _httpContextAccessor.HttpContext = null;

            // Find all BoxSets with Stremio provider ID
            var collections = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OfType<BoxSet>()
              .Where(b => b.ProviderIds.ContainsKey("Stremio"))
              .ToList();

            if (collections.Count == 0)
            {
                _logger.LogInformation("[Baklava] No Stremio collections found to sync.");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[Baklava] Found {Count} Stremio collections to sync.", collections.Count);

            var cfg = Plugin.Instance?.Configuration;
            var maxItems = cfg?.CatalogMaxItems ?? 500;
            var done = 0;

            // Get Gelato aiostreams URL
            var aiostreamsUrl = GetGelatoAiostreamsBaseUrl();
            if (string.IsNullOrEmpty(aiostreamsUrl))
            {
                _logger.LogWarning("[Baklava] Cannot sync - Gelato aiostreams URL not configured.");
                progress.Report(100);
                return;
            }

            foreach (var collection in collections)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Extract catalogId from providerId (format: "Stremio.{catalogId}" or "Stremio.{catalogId}.{type}")
                    var providerId = collection.ProviderIds["Stremio"];
                    var idPart = providerId.StartsWith("Stremio.")
                        ? providerId.Substring(8)
                        : providerId;

                    string catalogId = idPart;
                    string? specificType = null;

                    // Parse Stremio.{catalogId}.{type}
                    var parts = idPart.Split('.');
                    if (parts.Length >= 2)
                    {
                        var potentialType = parts.Last();
                        if (potentialType == "movie" || potentialType == "series")
                        {
                            specificType = potentialType;
                            catalogId = string.Join(".", parts.Take(parts.Length - 1));
                        }
                    }

                    // Determine type to try
                    var type = specificType ?? (catalogId.Contains("series") ? "series" : "movie");

                    _logger.LogInformation("[Baklava] Syncing collection '{Name}' (CatalogId: {CatalogId}, SpecificType: {SpecificType})",
                        collection.Name, catalogId, specificType ?? "null");

                     await SyncCatalogDirectly(collection, catalogId, type, aiostreamsUrl, maxItems, specificType, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Baklava] Failed to sync collection '{Name}'", collection.Name);
                }

                done++;
                progress.Report((done / (double)collections.Count) * 100);
            }

            _logger.LogInformation("[Baklava] CatalogSyncTask completed.");
        }

        private async Task SyncCatalogDirectly(BoxSet collection, string catalogId, string type, string aiostreamsUrl, int maxItems, string? forcedType, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
            var collectionManager = scope.ServiceProvider.GetRequiredService<ICollectionManager>();

            // If forcedType is set, ONLY try that type. Otherwise try heuristic + fallback.
            var typesToTry = forcedType != null 
                ? new[] { forcedType }
                : new[] { type, type == "movie" ? "series" : "movie" };
            
            foreach (var tryType in typesToTry)
            {
                var items = await FetchCatalogItems(aiostreamsUrl, catalogId, tryType, maxItems, ct).ConfigureAwait(false);
                
                if (items.Count > 0)
                {
                    _logger.LogInformation("[Baklava] Found {Count} items with type '{Type}' for catalog {CatalogId}", 
                        items.Count, tryType, catalogId);
                    
                    await ProcessCatalogItems(collection, items, libraryManager, collectionManager, tryType, catalogId, maxItems, ct)
                        .ConfigureAwait(false);
                    return;
                }
            }
            
            _logger.LogWarning("[Baklava] No items found in catalog {CatalogId} with either type", catalogId);
        }

        private async Task<List<CatalogItem>> FetchCatalogItems(string aiostreamsUrl, string catalogId, string type, int maxItems, CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(cfg?.CatalogImportTimeout > 0 ? cfg.CatalogImportTimeout : 300);

            // Add auth header if configured (same as CatalogController)
            if (!string.IsNullOrEmpty(cfg?.GelatoAuthHeader))
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

            var items = new List<CatalogItem>();
            var skip = 0;

            while (items.Count < maxItems)
            {
                ct.ThrowIfCancellationRequested();

                var encodedId = Uri.EscapeDataString(catalogId);
                var url = (skip > 0)
                    ? $"{aiostreamsUrl}/catalog/{type}/{encodedId}/skip={skip}.json"
                    : $"{aiostreamsUrl}/catalog/{type}/{encodedId}.json";

                _logger.LogDebug("[Baklava] Fetching catalog URL: {Url}", url);

                try
                {
                    var response = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var page = JsonSerializer.Deserialize<CatalogResponse>(response);

                if (page?.Metas == null || page.Metas.Count == 0)
                        break;

                    // Strictly limit the number of items added to respect maxItems
                    var remaining = maxItems - items.Count;
                    if (remaining <= 0) break;

                    var batch = page.Metas.Take(remaining).ToList();

                    items.AddRange(batch.Select(m => new CatalogItem
                    {
                        Id = m.Id,
                        ImdbId = GetImdbId(m.Id),
                        Name = m.Name
                    }));

                    skip += page.Metas.Count; // Increment skip by full page count to maintain pagination alignment
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("[Baklava] Failed to fetch catalog page for type {Type}: {Msg}", type, ex.Message);
                    break;
                }
            }
            
            return items;
        }

        private async Task ProcessCatalogItems(BoxSet collection, List<CatalogItem> items, ILibraryManager libraryManager, ICollectionManager collectionManager, string type, string catalogId, int maxItems, CancellationToken ct)
        {
            _logger.LogInformation("[Baklava] Fetched {Count} items from catalog {CatalogId}", items.Count, catalogId);

            // Get existing items in collection
            var linkedIds = collection.LinkedChildren
                .Where(lc => lc.ItemId.HasValue)
                .Select(lc => lc.ItemId!.Value)
                .ToArray();

            var existingImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (linkedIds.Length > 0)
            {
                var existingItems = libraryManager.GetItemList(new InternalItemsQuery { ItemIds = linkedIds });
                foreach (var item in existingItems)
                {
                    if (item.ProviderIds.TryGetValue("Imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
                        existingImdbIds.Add(imdbId);
                }
            }

            // Find missing items
            var missing = items.Where(i =>
                !string.IsNullOrEmpty(i.ImdbId) && !existingImdbIds.Contains(i.ImdbId)).ToList();

            _logger.LogInformation("[Baklava] Collection has {Existing} items, catalog has {Total}, {Missing} missing.",
                existingImdbIds.Count, items.Count, missing.Count);
            
            // Update stored catalog total if changed
            var catalogTotal = items.Count.ToString();
            if (!collection.ProviderIds.TryGetValue("Stremio.CatalogTotal", out var storedTotal) || storedTotal != catalogTotal)
            {
                collection.ProviderIds["Stremio.CatalogTotal"] = catalogTotal;
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("[Baklava] Updated catalog total: {Total}", catalogTotal);
            }

            if (missing.Count == 0)
            {
                _logger.LogInformation("[Baklava] Collection '{Name}' is already up to date.", collection.Name);
                return;
            }

            // Strict Limit Check
            if (linkedIds.Length >= maxItems)
            {
                _logger.LogInformation("[Baklava] Collection has reached max items limit ({Limit}). Stopping sync.", maxItems);
                return;
            }

            // Import missing items with rate limiting for series
            var mediaType = type.Equals("series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
            var importedIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();
            var failed = 0;
            
            // Limit the number of items we will try to add
            var itemsToProcess = missing.Take(maxItems - linkedIds.Length).ToList();
            
            _logger.LogInformation("[Baklava] Starting batch import for {Count} items (type: {Type})...", itemsToProcess.Count, mediaType);

            var isSeries = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
            var cfg = Plugin.Instance?.Configuration;
            
            // For SERIES: Process sequentially with delays to prevent rate limiting
            // Each series import triggers episode metadata fetches, which can overwhelm APIs
            if (isSeries)
            {
                var seriesDelay = cfg?.SeriesImportDelayMs ?? 2000;
                _logger.LogInformation("[Baklava] Processing {Count} series SEQUENTIALLY with {Delay}ms delay between each to prevent rate limiting", 
                    itemsToProcess.Count, seriesDelay);
                
                for (int i = 0; i < itemsToProcess.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = itemsToProcess[i];
                    
                    try
                    {
                        var imdbId = item.ImdbId!;
                        _logger.LogInformation("[Baklava] [{Current}/{Total}] Processing series: {Name} ({ImdbId})", 
                            i + 1, itemsToProcess.Count, item.Name ?? imdbId, imdbId);

                        // Check if already in library
                        var libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                            Recursive = true,
                            Limit = 1,
                            IncludeItemTypes = new[] { BaseItemKind.Series }
                        }).FirstOrDefault();

                        if (libraryItem == null)
                        {
                            var importedIdStr = await ImportItemViaReflection(imdbId, mediaType).ConfigureAwait(false);
                            
                            if (!string.IsNullOrEmpty(importedIdStr) && Guid.TryParse(importedIdStr, out var newGuid))
                            {
                                importedIds.Add(newGuid);
                                _logger.LogInformation("[Baklava] ✓ Successfully imported series: {Name}", item.Name ?? imdbId);
                            }
                            else
                            {
                                failed++;
                                _logger.LogWarning("[Baklava] ✗ Failed to import series: {Name}", item.Name ?? imdbId);
                            }
                        }
                        else
                        {
                            importedIds.Add(libraryItem.Id);
                            _logger.LogDebug("[Baklava] ✓ Series already exists: {Name}", item.Name ?? imdbId);
                        }
                        
                        // Delay between series imports (except after the last one)
                        if (i < itemsToProcess.Count - 1)
                        {
                            _logger.LogDebug("[Baklava] Waiting {Delay}ms before next series...", seriesDelay);
                            await Task.Delay(seriesDelay, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Baklava] Failed to process series {ImdbId}: {Message}", item.ImdbId, ex.Message);
                        failed++;
                    }
                }
            }
            else
            {
                // For MOVIES: Use parallel processing (but limit parallelism to avoid rate limits)
                var maxParallel = cfg?.MaxParallelMovieImports ?? 2;
                _logger.LogInformation("[Baklava] Processing {Count} movies with {Parallel} parallel imports", 
                    itemsToProcess.Count, maxParallel);
                
                await Parallel.ForEachAsync(itemsToProcess, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct }, async (item, token) =>
                {
                    try
                    {
                        var imdbId = item.ImdbId!;

                        // Check if already in library
                        var libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                            Recursive = true,
                            Limit = 1,
                            IncludeItemTypes = new[] { BaseItemKind.Movie }
                        }).FirstOrDefault();

                        if (libraryItem == null)
                        {
                            var importedIdStr = await ImportItemViaReflection(imdbId, mediaType).ConfigureAwait(false);
                            
                            if (!string.IsNullOrEmpty(importedIdStr) && Guid.TryParse(importedIdStr, out var newGuid))
                            {
                                importedIds.Add(newGuid);
                            }
                            else
                            {
                                failed++;
                            }
                        }
                        else
                        {
                            importedIds.Add(libraryItem.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Baklava] Failed to process movie {ImdbId}", item.ImdbId);
                        failed++;
                    }
                }).ConfigureAwait(false);
            }

            if (!importedIds.IsEmpty)
            {
                try 
                {
                    _logger.LogInformation("[Baklava] Adding {Count} items to collection '{Name}' in one batch...", importedIds.Count, collection.Name);
                    await collectionManager.AddToCollectionAsync(collection.Id, importedIds.Distinct().ToArray()).ConfigureAwait(false);
                    _logger.LogInformation("[Baklava] Batch add successful.");
                }
                catch (Exception ex)
                {
                     _logger.LogError(ex, "[Baklava] Failed to batch add items to collection.");
                }
            }

            _logger.LogInformation("[Baklava] Import complete. Success: {Success}, Failed: {Failed}", importedIds.Count, failed);
        }

        private async Task<string?> ImportItemViaReflection(string imdbId, string type)
        {
            try
            {
                // Resolve Gelato Assemblies and Types
                var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");
                
                if (gelatoAssembly == null)
                {
                    _logger.LogError("[Baklava] Gelato assembly not found");
                    return null;
                }

                var managerType = gelatoAssembly.GetType("Gelato.GelatoManager");
                var pluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                var metaTypeEnum = gelatoAssembly.GetType("Gelato.StremioMediaType");
                
                if (managerType == null || pluginType == null || metaTypeEnum == null)
                {
                    _logger.LogError("[Baklava] Required Gelato types not found");
                    return null;
                }
                
                // Get Singleton Manager Instance (to steal dependencies from)
                using var scope = _scopeFactory.CreateScope();
                var singletonManager = scope.ServiceProvider.GetService(managerType);
                if (singletonManager == null)
                {
                    _logger.LogError("[Baklava] GelatoManager service not found");
                    return null;
                }

                // --- FRANKENSTEIN START ---
                // Create Transient GelatoManager using manual constructor injection
                // We steal singletons from the existing manager and inject FRESH scoped services (Repository/Library)
                
                object managerToUse = singletonManager;
                object? transientManager = null;

                try 
                {
                    var ctors = managerType.GetConstructors();
                    var ctor = ctors.FirstOrDefault(); 

                    if (ctor != null)
                    {
                        var args = new List<object>();
                        var parameters = ctor.GetParameters();

                        foreach (var param in parameters)
                        {
                            object? arg = null;
                            var paramType = param.ParameterType;

                            // 1. Inject FRESH scoped dependencies
                            if (paramType.Name.Contains("IItemRepository") || paramType.Name.Contains("GelatoItemRepository") || 
                                paramType.Name.Contains("ILibraryManager") || paramType.Name.Contains("ICollectionManager"))
                            {
                                 // Special handling for GelatoItemRepository to ensure fresh inner repo
                                 if (paramType.Name.Contains("GelatoItemRepository"))
                                 {
                                     try 
                                     {
                                         var innerRepo = scope.ServiceProvider.GetService(typeof(MediaBrowser.Controller.Persistence.IItemRepository));
                                         if (innerRepo != null)
                                         {
                                             var repoCtors = paramType.GetConstructors();
                                             foreach (var rCtor in repoCtors)
                                             {
                                                 var rParams = rCtor.GetParameters();
                                                 var rArgs = new List<object>();
                                                 bool canSatisfy = true;
                                                 foreach(var rp in rParams)
                                                 {
                                                     if (rp.ParameterType.IsInstanceOfType(innerRepo))
                                                         rArgs.Add(innerRepo);
                                                     else 
                                                     {
                                                         var pService = scope.ServiceProvider.GetService(rp.ParameterType);
                                                         if (pService != null) rArgs.Add(pService);
                                                         else { canSatisfy = false; break; }
                                                     }
                                                 }
                                                 if (canSatisfy) { arg = rCtor.Invoke(rArgs.ToArray()); break; }
                                             }
                                         }
                                     }
                                     catch (Exception ex) { _logger.LogWarning("[Baklava] Failed to manually construct GelatoItemRepository in SyncTask: {Msg}", ex.Message); }
                                 }

                                if (arg == null)
                                {
                                    var resolveType = paramType.Name.Contains("GelatoItemRepository") 
                                        ? typeof(MediaBrowser.Controller.Persistence.IItemRepository) 
                                        : paramType;

                                    arg = scope.ServiceProvider.GetService(resolveType);
                                }
                            }

                            // 2. Steal from Singleton if not resolved
                            if (arg == null)
                            {
                                var fieldName = "_" + param.Name;
                                var field = managerType.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                                
                                if (field == null)
                                    field = managerType.GetField(param.Name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                                
                                if (field != null)
                                    arg = field.GetValue(singletonManager);
                            }

                            // 3. Fallback resolve
                            if (arg == null)
                                arg = scope.ServiceProvider.GetService(paramType);

                            args.Add(arg!);
                        }

                        if (args.Count == parameters.Length)
                        {
                            transientManager = ctor.Invoke(args.ToArray());
                            _logger.LogDebug("[Baklava] Frankenstein Transient GelatoManager created for SyncTask.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Baklava] Failed to create transient GelatoManager in SyncTask, using singleton.");
                }

                if (transientManager != null) managerToUse = transientManager;
                // --- FRANKENSTEIN END ---

                object metaResult = null;

                 // 1. Try to Fetch Full Metadata via StremioProvider
                try 
                {
                    object pluginInstance = null;
                    var instanceProp = pluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (instanceProp != null) pluginInstance = instanceProp.GetValue(null);

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
                                    var metaTypeValForFetch = Enum.Parse(metaTypeEnum, type.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie", true);
                                    var getMetaMethod = stremioProvider.GetType().GetMethod("GetMetaAsync", new[] { typeof(string), metaTypeEnum });
                                    
                                    if (getMetaMethod != null)
                                    {
                                        var task = (Task)getMetaMethod.Invoke(stremioProvider, new object[] { imdbId, metaTypeValForFetch });
                                        await task.ConfigureAwait(false);
                                        
                                        var resultProp = task.GetType().GetProperty("Result");
                                        metaResult = resultProp?.GetValue(task);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Baklava] Failed to fetch full meta for {Id} in SyncTask: {Msg}", imdbId, ex.Message);
                }

                // 2. Fallback: Create stub StremioMeta object
                if (metaResult == null)
                {
                     var stremioMetaType = gelatoAssembly.GetType("Gelato.StremioMeta");
                    if (stremioMetaType == null) return null;
                    
                    metaResult = Activator.CreateInstance(stremioMetaType);
                    if (metaResult == null) return null;

                    // Set ID and ImdbId
                    stremioMetaType.GetProperty("Id")?.SetValue(metaResult, imdbId);
                    stremioMetaType.GetProperty("ImdbId")?.SetValue(metaResult, imdbId);
                    
                    // Set Type
                    var metaTypeVal = Enum.Parse(metaTypeEnum, type.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie", true);
                    stremioMetaType.GetProperty("Type")?.SetValue(metaResult, metaTypeVal);
                    
                    if (type.Equals("tv", StringComparison.OrdinalIgnoreCase))
                    {
                        stremioMetaType.GetProperty("Videos")?.SetValue(metaResult, null);
                    }
                }

                // Determine Parent Folder
                var isSeries = type.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var folderMethodName = isSeries ? "TryGetSeriesFolder" : "TryGetMovieFolder";
                var getFolderMethod = managerType.GetMethod(folderMethodName, new Type[] { typeof(Guid) });
                
                var parentFolder = getFolderMethod?.Invoke(managerToUse, new object[] { Guid.Empty });
                if (parentFolder == null)
                {
                    _logger.LogError("[Baklava] Root folder not found for {Type}", type);
                    return null;
                }

                // Insert Meta
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                if (insertMetaMethod == null) return null;
                
                var insertTask = (Task)insertMetaMethod.Invoke(managerToUse, new object[] {
                    parentFolder,
                    metaResult,
                    Guid.Empty, // userId
                    true, // allowRemoteRefresh (Enabled for initial fetch)
                    true, // refreshItem
                    false, // queueRefreshItem
                    System.Threading.CancellationToken.None
                })!;
                
                await insertTask.ConfigureAwait(false);
                
                // Return Item.Id
                var resultTuple = insertTask.GetType().GetProperty("Result")?.GetValue(insertTask);
                var itemField = resultTuple?.GetType().GetField("Item1"); 
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
                _logger.LogError(ex, "[Baklava] Reflection import failed for {ImdbId}", imdbId);
                return null;
            }
        }

        private string? GetGelatoAiostreamsBaseUrl()
        {
            try
            {
                // Read Gelato's config from the XML file (same as CatalogController)
                var pluginsPath = _configManager.ApplicationPaths.PluginsPath;
                
                // Gelato config is at: plugins/configurations/Gelato.xml
                var gelatoConfigPath = System.IO.Path.Combine(pluginsPath, "configurations", "Gelato.xml");

                _logger.LogDebug("[Baklava] Looking for Gelato config at {Path}", gelatoConfigPath);

                if (!System.IO.File.Exists(gelatoConfigPath))
                {
                    _logger.LogWarning("[Baklava] Gelato config file not found at {Path}", gelatoConfigPath);
                    return null;
                }

                var xmlContent = System.IO.File.ReadAllText(gelatoConfigPath);
                var xmlDoc = System.Xml.Linq.XDocument.Parse(xmlContent);
                var urlElement = xmlDoc.Descendants("Url").FirstOrDefault();

                if (urlElement == null || string.IsNullOrEmpty(urlElement.Value))
                {
                    _logger.LogWarning("[Baklava] Gelato config has no URL configured");
                    return null;
                }

                var manifestUrl = urlElement.Value.Trim();
                
                // Remove /manifest.json to get the base URL
                var baseUrl = manifestUrl.Replace("/manifest.json", "").TrimEnd('/');
                
                _logger.LogInformation("[Baklava] Using Gelato aiostreams URL: {Url}", baseUrl);
                return baseUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava] Failed to read Gelato configuration");
            }
            return null;
        }

        private static string? GetImdbId(string? stremioId)
        {
            if (string.IsNullOrEmpty(stremioId)) return null;
            if (stremioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                return stremioId.Split(':')[0];
            return null;
        }

        private class CatalogResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("metas")]
            public List<CatalogMeta>? Metas { get; set; }
        }

        private class CatalogMeta
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string Id { get; set; } = "";
            
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string? Name { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("type")]
            public string? Type { get; set; }
        }

        private class CatalogItem
        {
            public string Id { get; set; } = "";
            public string? ImdbId { get; set; }
            public string? Name { get; set; }
        }
    }
}
