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

        public CatalogSyncTask(
            ILogger<CatalogSyncTask> logger,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IHttpClientFactory httpClientFactory,
            IServiceScopeFactory scopeFactory,
            MediaBrowser.Controller.Configuration.IServerConfigurationManager configManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _httpClientFactory = httpClientFactory;
            _scopeFactory = scopeFactory;
            _configManager = configManager;
        }

        public string Name => "Sync Stremio Catalogs";
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

                    // Check for new format: catalogId.type
                    var lastDotIndex = idPart.LastIndexOf('.');
                    if (lastDotIndex > 0)
                    {
                        var potentialType = idPart.Substring(lastDotIndex + 1);
                        if (potentialType == "movie" || potentialType == "series")
                        {
                            catalogId = idPart.Substring(0, lastDotIndex);
                            specificType = potentialType;
                        }
                    }

                    // Determine type to try
                    // If specific type is encoded in ID, use ONLY that. 
                    // Otherwise rely on heuristic or try both.
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

                    items.AddRange(page.Metas.Select(m => new CatalogItem
                    {
                        Id = m.Id,
                        ImdbId = GetImdbId(m.Id),
                        Name = m.Name
                    }));

                    skip += page.Metas.Count;
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

            // Import missing items (same logic as CatalogController)
            var mediaType = type.Equals("series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
            var imported = 0;
            var failed = 0;

            foreach (var item in missing.Take(maxItems - linkedIds.Length))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var imdbId = item.ImdbId!;

                    // CHECK 1: Already in library (but not in collection)?
                    var libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                    {
                        HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                        Recursive = true,
                        Limit = 1,
                        IncludeItemTypes = new[] { mediaType.Equals("tv") ? BaseItemKind.Series : BaseItemKind.Movie }
                    }).FirstOrDefault();

                    // If not in library, import it
                    if (libraryItem == null)
                    {
                        var importedIdStr = await ImportItemViaReflection(imdbId, mediaType).ConfigureAwait(false);
                        
                        if (string.IsNullOrEmpty(importedIdStr))
                        {
                            _logger.LogWarning("[Baklava] Import failed for {ImdbId}", imdbId);
                            failed++;
                            continue;
                        }
                        
                        // Fetch it again after import
                        libraryItem = libraryManager.GetItemList(new InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { "Imdb", imdbId } },
                            Recursive = true,
                            Limit = 1
                        }).FirstOrDefault();
                    }

                    if (libraryItem == null)
                    {
                        _logger.LogWarning("[Baklava] Could not find item by IMDB {ImdbId} even after import", imdbId);
                        failed++;
                        continue;
                    }

                    // Add to collection
                    _logger.LogInformation("[Baklava] Adding '{Name}' to collection", libraryItem.Name);
                    await collectionManager.AddToCollectionAsync(collection.Id, new[] { libraryItem.Id }).ConfigureAwait(false);
                    imported++;

                    // 2 second delay to let Jellyfin fully release file locks
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Baklava] Failed to process item {ImdbId}", item.ImdbId);
                    failed++;
                }
            }

            _logger.LogInformation("[Baklava] Import complete. Success: {Success}, Failed: {Failed}", imported, failed);
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
                
                // Get Manager Instance
                using var scope = _scopeFactory.CreateScope();
                var manager = scope.ServiceProvider.GetService(managerType);
                if (manager == null)
                {
                    _logger.LogError("[Baklava] GelatoManager service not found");
                    return null;
                }

                // Get Configuration to fetch Meta
                var pluginInstance = pluginType.GetProperty("Instance")?.GetValue(null);
                if (pluginInstance == null) return null;
                
                var config = pluginInstance.GetType().GetMethod("GetConfig")?.Invoke(pluginInstance, new object[] { Guid.Empty });
                if (config == null) return null;
                
                var stremioProvider = config.GetType().GetField("stremio")?.GetValue(config);
                if (stremioProvider == null) return null;

                // Fetch Meta
                var metaTypeVal = Enum.Parse(metaTypeEnum, type.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie", true);
                var getMetaMethod = stremioProvider.GetType().GetMethod("GetMetaAsync", new Type[] { typeof(string), metaTypeEnum });
                
                if (getMetaMethod == null)
                {
                    _logger.LogError("[Baklava] GetMetaAsync method not found");
                    return null;
                }

                var metaTask = (Task)getMetaMethod.Invoke(stremioProvider, new object[] { imdbId, metaTypeVal })!;
                await metaTask.ConfigureAwait(false);
                
                var metaResult = metaTask.GetType().GetProperty("Result")?.GetValue(metaTask);
                if (metaResult == null)
                {
                    _logger.LogWarning("[Baklava] Meta not found for {ImdbId}", imdbId);
                    return null;
                }

                // Determine Parent Folder
                var isSeries = type.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var folderMethodName = isSeries ? "TryGetSeriesFolder" : "TryGetMovieFolder";
                var getFolderMethod = managerType.GetMethod(folderMethodName, new Type[] { typeof(Guid) });
                
                var parentFolder = getFolderMethod?.Invoke(manager, new object[] { Guid.Empty });
                if (parentFolder == null)
                {
                    _logger.LogError("[Baklava] Root folder not found for {Type}", type);
                    return null;
                }

                // Insert Meta
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                if (insertMetaMethod == null)
                {
                    _logger.LogError("[Baklava] InsertMeta method not found");
                    return null;
                }
                
                var insertTask = (Task)insertMetaMethod.Invoke(manager, new object[] {
                    parentFolder,
                    metaResult,
                    Guid.Empty, // userId
                    true, // allowRemoteRefresh
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
