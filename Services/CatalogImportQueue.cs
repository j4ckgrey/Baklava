#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Baklava.Services
{
    /// <summary>
    /// Persistent queue for catalog imports.
    /// Stores pending imports in a JSON file so they survive server restarts.
    /// Imports are processed by CatalogImportProcessorTask in a fresh DI scope.
    /// </summary>
    public class CatalogImportQueue
    {
        private readonly ILogger<CatalogImportQueue> _logger;
        private readonly string _queueFilePath;
        private readonly object _lock = new();
        private ConcurrentDictionary<string, QueuedCatalog> _queues;

        public CatalogImportQueue(ILogger<CatalogImportQueue> logger, IApplicationPaths applicationPaths)
        {
            _logger = logger;
            
            // Store queue in plugin data folder
            var dataPath = Path.Combine(applicationPaths.PluginsPath, "configurations", "Baklava");
            Directory.CreateDirectory(dataPath);
            _queueFilePath = Path.Combine(dataPath, "import_queue.json");
            
            _queues = LoadQueue();
            _logger.LogInformation("[CatalogImportQueue] Initialized with {Count} pending imports", _queues.Count);
        }

        /// <summary>
        /// Queue a catalog for import. Overwrites any existing queue for the same catalogId.
        /// </summary>
        public void QueueImport(string catalogId, Guid collectionId, List<string> imdbIds, string mediaType, string catalogName)
        {
            if (imdbIds.Count == 0)
            {
                _logger.LogDebug("[CatalogImportQueue] No items to queue for {CatalogId}", catalogId);
                return;
            }

            var queue = new QueuedCatalog
            {
                CatalogId = catalogId,
                CollectionId = collectionId,
                ImdbIds = imdbIds,
                MediaType = mediaType,
                CatalogName = catalogName,
                QueuedAt = DateTime.UtcNow,
                ProcessedCount = 0
            };

            _queues[catalogId] = queue;
            SaveQueue();
            
            _logger.LogInformation("[CatalogImportQueue] Queued {Count} items for catalog '{Name}' ({CatalogId})", 
                imdbIds.Count, catalogName, catalogId);
        }

        /// <summary>
        /// Get the next pending catalog to process, or null if queue is empty.
        /// </summary>
        public QueuedCatalog? PeekNext()
        {
            return _queues.Values
                .OrderBy(q => q.QueuedAt)
                .FirstOrDefault();
        }

        /// <summary>
        /// Get a specific catalog queue by ID.
        /// </summary>
        public QueuedCatalog? GetQueue(string catalogId)
        {
            return _queues.TryGetValue(catalogId, out var queue) ? queue : null;
        }

        /// <summary>
        /// Get all pending queues.
        /// </summary>
        public IReadOnlyList<QueuedCatalog> GetAllQueues()
        {
            return _queues.Values.OrderBy(q => q.QueuedAt).ToList();
        }

        /// <summary>
        /// Update progress for a catalog import.
        /// </summary>
        public void UpdateProgress(string catalogId, int processedCount, int successCount, int failedCount)
        {
            if (_queues.TryGetValue(catalogId, out var queue))
            {
                queue.ProcessedCount = processedCount;
                queue.SuccessCount = successCount;
                queue.FailedCount = failedCount;
                queue.LastUpdated = DateTime.UtcNow;
                SaveQueue();
            }
        }

        /// <summary>
        /// Mark a catalog import as complete and remove from queue.
        /// </summary>
        public void MarkComplete(string catalogId)
        {
            if (_queues.TryRemove(catalogId, out var removed))
            {
                SaveQueue();
                _logger.LogInformation("[CatalogImportQueue] Completed import for catalog '{Name}' ({CatalogId}). Success: {Success}, Failed: {Failed}", 
                    removed.CatalogName, catalogId, removed.SuccessCount, removed.FailedCount);
            }
        }

        /// <summary>
        /// Remove a specific IMDB ID from a catalog's queue (after successful import).
        /// </summary>
        public void RemoveImdbId(string catalogId, string imdbId)
        {
            if (_queues.TryGetValue(catalogId, out var queue))
            {
                queue.ImdbIds.Remove(imdbId);
                if (queue.ImdbIds.Count == 0)
                {
                    MarkComplete(catalogId);
                }
                else
                {
                    SaveQueue();
                }
            }
        }

        /// <summary>
        /// Check if there are any pending imports.
        /// </summary>
        public bool HasPending => _queues.Count > 0;

        /// <summary>
        /// Get count of pending catalogs.
        /// </summary>
        public int PendingCount => _queues.Count;

        private ConcurrentDictionary<string, QueuedCatalog> LoadQueue()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_queueFilePath))
                    {
                        var json = File.ReadAllText(_queueFilePath);
                        var list = JsonSerializer.Deserialize<List<QueuedCatalog>>(json);
                        if (list != null)
                        {
                            return new ConcurrentDictionary<string, QueuedCatalog>(
                                list.ToDictionary(q => q.CatalogId, q => q));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CatalogImportQueue] Failed to load queue file, starting fresh");
                }
                
                return new ConcurrentDictionary<string, QueuedCatalog>();
            }
        }

        private void SaveQueue()
        {
            lock (_lock)
            {
                try
                {
                    var list = _queues.Values.ToList();
                    var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_queueFilePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[CatalogImportQueue] Failed to save queue file");
                }
            }
        }
    }

    /// <summary>
    /// Represents a catalog pending import.
    /// </summary>
    public class QueuedCatalog
    {
        public string CatalogId { get; set; } = string.Empty;
        public Guid CollectionId { get; set; }
        public List<string> ImdbIds { get; set; } = new();
        public string MediaType { get; set; } = "movie";
        public string CatalogName { get; set; } = string.Empty;
        public DateTime QueuedAt { get; set; }
        public DateTime? LastUpdated { get; set; }
        public int ProcessedCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        
        /// <summary>
        /// Total items originally queued.
        /// </summary>
        public int TotalCount => ProcessedCount + ImdbIds.Count;
    }
}
