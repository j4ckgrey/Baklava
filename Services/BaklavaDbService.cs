using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;

#nullable enable
namespace Baklava.Services
{
    /// <summary>
    /// Database service for Baklava plugin - stores reviews and metadata
    /// </summary>
    public class BaklavaDbService : IDisposable
    {
        private readonly ILogger<BaklavaDbService> _logger;
        private readonly string _dbPath;
        public SqliteConnection? _connection; // Public for deletion endpoints

        public BaklavaDbService(ILogger<BaklavaDbService> logger, IApplicationPaths appPaths)
        {
            _logger = logger;
            _dbPath = System.IO.Path.Combine(appPaths.DataPath, "baklava.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();

                var createTableCmd = _connection.CreateCommand();
                createTableCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Reviews (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        ImdbId TEXT,
                        TmdbId TEXT,
                        ItemType TEXT NOT NULL,
                        Source TEXT NOT NULL,
                        ReviewsJson TEXT NOT NULL,
                        CachedAt TEXT NOT NULL,
                        UNIQUE(ItemId, Source)
                    );

                    CREATE INDEX IF NOT EXISTS idx_reviews_itemid ON Reviews(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_reviews_imdbid ON Reviews(ImdbId);
                    CREATE INDEX IF NOT EXISTS idx_reviews_tmdbid ON Reviews(TmdbId);
                    CREATE INDEX IF NOT EXISTS idx_reviews_source ON Reviews(Source);

                    CREATE TABLE IF NOT EXISTS ItemMetadata (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL UNIQUE,
                        ImdbId TEXT,
                        TmdbId TEXT,
                        TvdbId TEXT,
                        ItemType TEXT NOT NULL,
                        Name TEXT,
                        OriginalTitle TEXT,
                        Overview TEXT,
                        Tagline TEXT,
                        Year INTEGER,
                        PremiereDate TEXT,
                        EndDate TEXT,
                        Status TEXT,
                        OfficialRating TEXT,
                        CommunityRating REAL,
                        CriticRating REAL,
                        Runtime INTEGER,
                        Genres TEXT,
                        Studios TEXT,
                        Tags TEXT,
                        BackdropUrl TEXT,
                        PosterUrl TEXT,
                        LogoUrl TEXT,
                        ParentId TEXT,
                        CollectionId TEXT,
                        CollectionName TEXT,
                        SeasonNumber INTEGER,
                        EpisodeNumber INTEGER,
                        SeriesId TEXT,
                        SeriesName TEXT,
                        Path TEXT,
                        FileName TEXT,
                        Container TEXT,
                        VideoCodec TEXT,
                        AudioCodec TEXT,
                        Width INTEGER,
                        Height INTEGER,
                        AspectRatio TEXT,
                        Framerate REAL,
                        Bitrate INTEGER,
                        FileSize INTEGER,
                        IsPlayed INTEGER DEFAULT 0,
                        PlaybackPositionTicks INTEGER DEFAULT 0,
                        IsFavorite INTEGER DEFAULT 0,
                        DateCreated TEXT,
                        DateModified TEXT,
                        LastUpdated TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_metadata_itemid ON ItemMetadata(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_metadata_imdbid ON ItemMetadata(ImdbId);
                    CREATE INDEX IF NOT EXISTS idx_metadata_tmdbid ON ItemMetadata(TmdbId);
                    CREATE INDEX IF NOT EXISTS idx_metadata_collectionid ON ItemMetadata(CollectionId);
                    CREATE INDEX IF NOT EXISTS idx_metadata_seriesid ON ItemMetadata(SeriesId);
                    CREATE INDEX IF NOT EXISTS idx_metadata_type ON ItemMetadata(ItemType);

                    CREATE TABLE IF NOT EXISTS Collections (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CollectionId TEXT NOT NULL UNIQUE,
                        Name TEXT NOT NULL,
                        Overview TEXT,
                        Path TEXT,
                        ParentId TEXT,
                        ItemCount INTEGER DEFAULT 0,
                        BackdropUrl TEXT,
                        PosterUrl TEXT,
                        SourceCatalogId TEXT,
                        DateCreated TEXT,
                        LastUpdated TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_collections_id ON Collections(CollectionId);
                    CREATE INDEX IF NOT EXISTS idx_collections_catalogid ON Collections(SourceCatalogId);

                    CREATE TABLE IF NOT EXISTS LibraryFolders (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FolderId TEXT NOT NULL UNIQUE,
                        Name TEXT NOT NULL,
                        Path TEXT NOT NULL,
                        LibraryType TEXT NOT NULL,
                        ParentId TEXT,
                        IsVirtual INTEGER DEFAULT 0,
                        ItemCount INTEGER DEFAULT 0,
                        DateCreated TEXT,
                        LastUpdated TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_folders_id ON LibraryFolders(FolderId);
                    CREATE INDEX IF NOT EXISTS idx_folders_type ON LibraryFolders(LibraryType);

                    CREATE TABLE IF NOT EXISTS People (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        PersonName TEXT NOT NULL,
                        Role TEXT,
                        Type TEXT NOT NULL,
                        ImageUrl TEXT,
                        SortOrder INTEGER
                    );

                    CREATE INDEX IF NOT EXISTS idx_people_itemid ON People(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_people_name ON People(PersonName);
                    CREATE INDEX IF NOT EXISTS idx_people_type ON People(Type);

                    CREATE TABLE IF NOT EXISTS ProviderIds (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        ProviderName TEXT NOT NULL,
                        ProviderId TEXT NOT NULL,
                        UNIQUE(ItemId, ProviderName)
                    );

                    CREATE INDEX IF NOT EXISTS idx_providerids_itemid ON ProviderIds(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_providerids_provider ON ProviderIds(ProviderName);

                    CREATE TABLE IF NOT EXISTS UserData (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId TEXT NOT NULL,
                        ItemId TEXT NOT NULL,
                        Rating REAL,
                        Played INTEGER DEFAULT 0,
                        PlayCount INTEGER DEFAULT 0,
                        IsFavorite INTEGER DEFAULT 0,
                        Likes INTEGER,
                        PlaybackPositionTicks INTEGER DEFAULT 0,
                        LastPlayedDate TEXT,
                        DateCreated TEXT,
                        UNIQUE(UserId, ItemId)
                    );

                    CREATE INDEX IF NOT EXISTS idx_userdata_userid ON UserData(UserId);
                    CREATE INDEX IF NOT EXISTS idx_userdata_itemid ON UserData(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_userdata_favorite ON UserData(IsFavorite);

                    CREATE TABLE IF NOT EXISTS Catalogs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CatalogId TEXT NOT NULL UNIQUE,
                        Name TEXT NOT NULL,
                        Type TEXT NOT NULL,
                        ItemCount INTEGER DEFAULT 0,
                        AddonName TEXT,
                        SourceUrl TEXT,
                        IsSearchCapable INTEGER DEFAULT 0,
                        CollectionName TEXT,
                        CollectionId TEXT,
                        LastSynced TEXT,
                        CreatedAt TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_catalogs_catalogid ON Catalogs(CatalogId);
                    CREATE INDEX IF NOT EXISTS idx_catalogs_type ON Catalogs(Type);

                    CREATE TABLE IF NOT EXISTS Streams (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        StremioId TEXT,
                        ImdbId TEXT,
                        TmdbId TEXT,
                        ItemType TEXT NOT NULL,
                        UserId TEXT,
                        StreamUrl TEXT,
                        InfoHash TEXT,
                        FileIdx INTEGER,
                        Title TEXT,
                        Name TEXT,
                        Quality TEXT,
                        Subtitle TEXT,
                        Audio TEXT,
                        BingeGroup TEXT,
                        Filename TEXT,
                        VideoSize INTEGER,
                        VideoHash TEXT,
                        Sources TEXT,
                        StreamIndex INTEGER,
                        WebCompatible INTEGER DEFAULT NULL,
                        CachedAt TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_streams_itemid ON Streams(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_streams_stremioid ON Streams(StremioId);
                    CREATE INDEX IF NOT EXISTS idx_streams_imdbid ON Streams(ImdbId);
                    CREATE INDEX IF NOT EXISTS idx_streams_tmdbid ON Streams(TmdbId);
                    CREATE INDEX IF NOT EXISTS idx_streams_userid ON Streams(UserId);

                    CREATE TABLE IF NOT EXISTS ProbedStreams (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        StremioId TEXT,
                        StreamSourceId TEXT,
                        StreamType TEXT NOT NULL,
                        StreamIndex INTEGER NOT NULL,
                        Codec TEXT,
                        Language TEXT,
                        Title TEXT,
                        DisplayTitle TEXT,
                        IsDefault INTEGER DEFAULT 0,
                        IsForced INTEGER DEFAULT 0,
                        Channels INTEGER,
                        ChannelLayout TEXT,
                        SampleRate INTEGER,
                        BitRate INTEGER,
                        BitDepth INTEGER,
                        Profile TEXT,
                        Level REAL,
                        IsExternal INTEGER DEFAULT 0,
                        Path TEXT,
                        CachedAt TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_probedstreams_itemid ON ProbedStreams(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_probedstreams_streamid ON ProbedStreams(StreamSourceId);
                    CREATE INDEX IF NOT EXISTS idx_probedstreams_type ON ProbedStreams(StreamType);

                    CREATE TABLE IF NOT EXISTS CatalogItems (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CatalogId TEXT NOT NULL,
                        ImdbId TEXT NOT NULL,
                        TmdbId TEXT,
                        ItemType TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Year TEXT,
                        Poster TEXT,
                        Background TEXT,
                        Overview TEXT,
                        Rating TEXT,
                        Genres TEXT,
                        Status TEXT NOT NULL DEFAULT 'pending',
                        JellyfinItemId TEXT,
                        ImportedAt TEXT NOT NULL,
                        SyncedAt TEXT,
                        ErrorMessage TEXT,
                        RetryCount INTEGER DEFAULT 0,
                        UNIQUE(CatalogId, ImdbId)
                    );

                    CREATE INDEX IF NOT EXISTS idx_catalogitems_catalogid ON CatalogItems(CatalogId);
                    CREATE INDEX IF NOT EXISTS idx_catalogitems_imdbid ON CatalogItems(ImdbId);
                    CREATE INDEX IF NOT EXISTS idx_catalogitems_status ON CatalogItems(Status);
                    CREATE INDEX IF NOT EXISTS idx_catalogitems_type ON CatalogItems(ItemType);

                    CREATE TABLE IF NOT EXISTS ExternalSubtitles (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemId TEXT NOT NULL,
                        SubtitleId TEXT,
                        Url TEXT NOT NULL,
                        Language TEXT,
                        Title TEXT,
                        CachedAt TEXT NOT NULL,
                        UNIQUE(ItemId, SubtitleId, Language)
                    );

                    CREATE INDEX IF NOT EXISTS idx_externalsubtitles_itemid ON ExternalSubtitles(ItemId);
                    CREATE INDEX IF NOT EXISTS idx_externalsubtitles_language ON ExternalSubtitles(Language);
                ";

                createTableCmd.ExecuteNonQuery();
                _logger.LogInformation("[BaklavaDb] Database initialized at {Path}", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to initialize database, will retry on first use");
                // Don't throw - let the plugin load and retry on first database operation
                try { _connection?.Close(); } catch { }
                _connection = null;
            }
        }

        public void EnsureConnection() // Public for deletion endpoints
        {
            if (_connection != null && _connection.State == ConnectionState.Open)
                return;

            try
            {
                _connection?.Close();
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to open database connection");
                throw;
            }
        }

        /// <summary>
        /// Store or update reviews for an item
        /// </summary>
        public async Task<bool> SaveReviewsAsync(string itemId, string? imdbId, string? tmdbId, string itemType, string source, string reviewsJson)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO Reviews (ItemId, ImdbId, TmdbId, ItemType, Source, ReviewsJson, CachedAt)
                    VALUES (@itemId, @imdbId, @tmdbId, @itemType, @source, @reviewsJson, @cachedAt)
                    ON CONFLICT(ItemId, Source) DO UPDATE SET
                        ReviewsJson = @reviewsJson,
                        CachedAt = @cachedAt,
                        ImdbId = @imdbId,
                        TmdbId = @tmdbId,
                        ItemType = @itemType
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@imdbId", imdbId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@tmdbId", tmdbId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@itemType", itemType);
                cmd.Parameters.AddWithValue("@source", source);
                cmd.Parameters.AddWithValue("@reviewsJson", reviewsJson);
                cmd.Parameters.AddWithValue("@cachedAt", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInformation("[BaklavaDb] Saved reviews for {ItemId} from {Source}", itemId, source);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save reviews for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Get cached reviews for an item from a specific source
        /// </summary>
        public async Task<string?> GetReviewsAsync(string itemId, string source)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT ReviewsJson, CachedAt 
                    FROM Reviews 
                    WHERE ItemId = @itemId AND Source = @source
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@source", source);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var cachedAt = DateTime.Parse(reader.GetString(1));
                    // Cache valid for 24 hours
                    if (DateTime.UtcNow - cachedAt < TimeSpan.FromHours(24))
                    {
                        _logger.LogInformation("[BaklavaDb] Found cached reviews for {ItemId} from {Source}", itemId, source);
                        return reader.GetString(0);
                    }
                    else
                    {
                        _logger.LogInformation("[BaklavaDb] Cached reviews expired for {ItemId}", itemId);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get reviews for {ItemId}", itemId);
                return null;
            }
        }

        /// <summary>
        /// Store or update item metadata
        /// </summary>
        public async Task<bool> SaveItemMetadataAsync(string itemId, string? imdbId, string? tmdbId, string itemType, string name)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO ItemMetadata (ItemId, ImdbId, TmdbId, ItemType, Name, LastUpdated)
                    VALUES (@itemId, @imdbId, @tmdbId, @itemType, @name, @lastUpdated)
                    ON CONFLICT(ItemId) DO UPDATE SET
                        ImdbId = @imdbId,
                        TmdbId = @tmdbId,
                        ItemType = @itemType,
                        Name = @name,
                        LastUpdated = @lastUpdated
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@imdbId", imdbId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@tmdbId", tmdbId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@itemType", itemType);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@lastUpdated", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save metadata for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Get all items that need review updates (older than 7 days)
        /// </summary>
        public async Task<List<(string ItemId, string? ImdbId, string? TmdbId, string ItemType)>> GetItemsNeedingReviewUpdateAsync()
        {
            var items = new List<(string, string?, string?, string)>();
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT m.ItemId, m.ImdbId, m.TmdbId, m.ItemType
                    FROM ItemMetadata m
                    LEFT JOIN Reviews r ON m.ItemId = r.ItemId
                    WHERE r.ItemId IS NULL 
                       OR datetime(r.CachedAt) < datetime('now', '-7 days')
                ";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    items.Add((
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3)
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get items needing updates");
            }
            return items;
        }

        /// <summary>
        /// Save or update catalog information
        /// </summary>
        public async Task<bool> SaveCatalogAsync(string catalogId, string name, string type, int itemCount, 
            string? addonName, string? sourceUrl, bool isSearchCapable, string? collectionName, string? collectionId)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO Catalogs (CatalogId, Name, Type, ItemCount, AddonName, SourceUrl, IsSearchCapable, CollectionName, CollectionId, LastSynced, CreatedAt)
                    VALUES (@catalogId, @name, @type, @itemCount, @addonName, @sourceUrl, @isSearchCapable, @collectionName, @collectionId, @lastSynced, @createdAt)
                    ON CONFLICT(CatalogId) DO UPDATE SET
                        Name = @name,
                        Type = @type,
                        ItemCount = @itemCount,
                        AddonName = @addonName,
                        SourceUrl = @sourceUrl,
                        IsSearchCapable = @isSearchCapable,
                        CollectionName = @collectionName,
                        CollectionId = @collectionId,
                        LastSynced = @lastSynced
                ";

                cmd.Parameters.AddWithValue("@catalogId", catalogId);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@itemCount", itemCount);
                cmd.Parameters.AddWithValue("@addonName", addonName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@sourceUrl", sourceUrl ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@isSearchCapable", isSearchCapable ? 1 : 0);
                cmd.Parameters.AddWithValue("@collectionName", collectionName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@collectionId", collectionId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@lastSynced", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save catalog {CatalogId}", catalogId);
                return false;
            }
        }

        /// <summary>
        /// Get all catalogs from database
        /// </summary>
        public async Task<List<(string CatalogId, string Name, string Type, int ItemCount, string? CollectionId)>> GetAllCatalogsAsync()
        {
            var catalogs = new List<(string, string, string, int, string?)>();
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT CatalogId, Name, Type, ItemCount, CollectionId
                    FROM Catalogs
                    ORDER BY Name
                ";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    catalogs.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get catalogs");
            }
            return catalogs;
        }

        #region Complete Metadata Management

        /// <summary>
        /// Save complete item metadata to Baklava database - full alternative to Jellyfin
        /// </summary>
        public async Task<bool> SaveCompleteItemMetadataAsync(CompleteItemMetadata metadata)
        {
            try
            {
                EnsureConnection();

                using var transaction = _connection!.BeginTransaction();
                
                try
                {
                    // 1. Save main metadata
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO ItemMetadata (
                                ItemId, ImdbId, TmdbId, TvdbId, ItemType, Name, OriginalTitle, Overview, Tagline,
                                Year, PremiereDate, EndDate, Status, OfficialRating, CommunityRating, CriticRating,
                                Runtime, Genres, Studios, Tags, BackdropUrl, PosterUrl, LogoUrl,
                                ParentId, CollectionId, CollectionName, SeasonNumber, EpisodeNumber,
                                SeriesId, SeriesName, Path, FileName, Container, VideoCodec, AudioCodec,
                                Width, Height, AspectRatio, Framerate, Bitrate, FileSize,
                                DateCreated, DateModified, LastUpdated
                            ) VALUES (
                                @itemId, @imdbId, @tmdbId, @tvdbId, @itemType, @name, @originalTitle, @overview, @tagline,
                                @year, @premiereDate, @endDate, @status, @officialRating, @communityRating, @criticRating,
                                @runtime, @genres, @studios, @tags, @backdropUrl, @posterUrl, @logoUrl,
                                @parentId, @collectionId, @collectionName, @seasonNumber, @episodeNumber,
                                @seriesId, @seriesName, @path, @fileName, @container, @videoCodec, @audioCodec,
                                @width, @height, @aspectRatio, @framerate, @bitrate, @fileSize,
                                @dateCreated, @dateModified, @lastUpdated
                            )
                            ON CONFLICT(ItemId) DO UPDATE SET
                                ImdbId = @imdbId, TmdbId = @tmdbId, TvdbId = @tvdbId, ItemType = @itemType,
                                Name = @name, OriginalTitle = @originalTitle, Overview = @overview, Tagline = @tagline,
                                Year = @year, PremiereDate = @premiereDate, EndDate = @endDate, Status = @status,
                                OfficialRating = @officialRating, CommunityRating = @communityRating, CriticRating = @criticRating,
                                Runtime = @runtime, Genres = @genres, Studios = @studios, Tags = @tags,
                                BackdropUrl = @backdropUrl, PosterUrl = @posterUrl, LogoUrl = @logoUrl,
                                ParentId = @parentId, CollectionId = @collectionId, CollectionName = @collectionName,
                                SeasonNumber = @seasonNumber, EpisodeNumber = @episodeNumber,
                                SeriesId = @seriesId, SeriesName = @seriesName, Path = @path, FileName = @fileName,
                                Container = @container, VideoCodec = @videoCodec, AudioCodec = @audioCodec,
                                Width = @width, Height = @height, AspectRatio = @aspectRatio, Framerate = @framerate,
                                Bitrate = @bitrate, FileSize = @fileSize,
                                DateCreated = @dateCreated, DateModified = @dateModified, LastUpdated = @lastUpdated
                        ";

                        cmd.Parameters.AddWithValue("@itemId", metadata.ItemId);
                        cmd.Parameters.AddWithValue("@imdbId", metadata.ImdbId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@tmdbId", metadata.TmdbId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@tvdbId", metadata.TvdbId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@itemType", metadata.ItemType);
                        cmd.Parameters.AddWithValue("@name", metadata.Name ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@originalTitle", metadata.OriginalTitle ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@overview", metadata.Overview ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@tagline", metadata.Tagline ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@year", metadata.Year ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@premiereDate", metadata.PremiereDate?.ToString("o") ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@endDate", metadata.EndDate?.ToString("o") ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", metadata.Status ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@officialRating", metadata.OfficialRating ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@communityRating", metadata.CommunityRating ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@criticRating", metadata.CriticRating ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@runtime", metadata.Runtime ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@genres", metadata.Genres != null ? string.Join("|", metadata.Genres) : (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@studios", metadata.Studios != null ? string.Join("|", metadata.Studios) : (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@tags", metadata.Tags != null ? string.Join("|", metadata.Tags) : (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@backdropUrl", metadata.BackdropUrl ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@posterUrl", metadata.PosterUrl ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@logoUrl", metadata.LogoUrl ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@parentId", metadata.ParentId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@collectionId", metadata.CollectionId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@collectionName", metadata.CollectionName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@seasonNumber", metadata.SeasonNumber ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@episodeNumber", metadata.EpisodeNumber ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@seriesId", metadata.SeriesId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@seriesName", metadata.SeriesName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@path", metadata.Path ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@fileName", metadata.FileName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@container", metadata.Container ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@videoCodec", metadata.VideoCodec ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@audioCodec", metadata.AudioCodec ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@width", metadata.Width ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@height", metadata.Height ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@aspectRatio", metadata.AspectRatio ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@framerate", metadata.Framerate ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@bitrate", metadata.Bitrate ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@fileSize", metadata.FileSize ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@dateCreated", metadata.DateCreated?.ToString("o") ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@dateModified", metadata.DateModified?.ToString("o") ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@lastUpdated", DateTime.UtcNow.ToString("o"));

                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 2. Save provider IDs
                    if (metadata.ProviderIds != null)
                    {
                        // Delete existing provider IDs
                        using (var delCmd = _connection.CreateCommand())
                        {
                            delCmd.Transaction = transaction;
                            delCmd.CommandText = "DELETE FROM ProviderIds WHERE ItemId = @itemId";
                            delCmd.Parameters.AddWithValue("@itemId", metadata.ItemId);
                            await delCmd.ExecuteNonQueryAsync();
                        }

                        // Insert new provider IDs
                        foreach (var provider in metadata.ProviderIds)
                        {
                            using var cmd = _connection.CreateCommand();
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT INTO ProviderIds (ItemId, ProviderName, ProviderId)
                                VALUES (@itemId, @providerName, @providerId)
                            ";
                            cmd.Parameters.AddWithValue("@itemId", metadata.ItemId);
                            cmd.Parameters.AddWithValue("@providerName", provider.Key);
                            cmd.Parameters.AddWithValue("@providerId", provider.Value);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    // 3. Save people (cast/crew)
                    if (metadata.People != null)
                    {
                        // Delete existing people
                        using (var delCmd = _connection.CreateCommand())
                        {
                            delCmd.Transaction = transaction;
                            delCmd.CommandText = "DELETE FROM People WHERE ItemId = @itemId";
                            delCmd.Parameters.AddWithValue("@itemId", metadata.ItemId);
                            await delCmd.ExecuteNonQueryAsync();
                        }

                        // Insert new people
                        foreach (var person in metadata.People)
                        {
                            using var cmd = _connection.CreateCommand();
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT INTO People (ItemId, PersonName, Role, Type, ImageUrl, SortOrder)
                                VALUES (@itemId, @personName, @role, @type, @imageUrl, @sortOrder)
                            ";
                            cmd.Parameters.AddWithValue("@itemId", metadata.ItemId);
                            cmd.Parameters.AddWithValue("@personName", person.Name);
                            cmd.Parameters.AddWithValue("@role", person.Role ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@type", person.Type);
                            cmd.Parameters.AddWithValue("@imageUrl", person.ImageUrl ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@sortOrder", person.SortOrder);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    transaction.Commit();
                    _logger.LogDebug("[BaklavaDb] Saved complete metadata for {ItemId} ({Name})", metadata.ItemId, metadata.Name);
                    return true;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save complete metadata for {ItemId}", metadata.ItemId);
                return false;
            }
        }

        /// <summary>
        /// Save or update collection information
        /// </summary>
        public async Task<bool> SaveCollectionAsync(CollectionInfo collection)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO Collections (
                        CollectionId, Name, Overview, Path, ParentId, ItemCount,
                        BackdropUrl, PosterUrl, SourceCatalogId, DateCreated, LastUpdated
                    ) VALUES (
                        @collectionId, @name, @overview, @path, @parentId, @itemCount,
                        @backdropUrl, @posterUrl, @sourceCatalogId, @dateCreated, @lastUpdated
                    )
                    ON CONFLICT(CollectionId) DO UPDATE SET
                        Name = @name, Overview = @overview, Path = @path, ParentId = @parentId,
                        ItemCount = @itemCount, BackdropUrl = @backdropUrl, PosterUrl = @posterUrl,
                        SourceCatalogId = @sourceCatalogId, DateCreated = @dateCreated, LastUpdated = @lastUpdated
                ";

                cmd.Parameters.AddWithValue("@collectionId", collection.CollectionId);
                cmd.Parameters.AddWithValue("@name", collection.Name);
                cmd.Parameters.AddWithValue("@overview", collection.Overview ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@path", collection.Path ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@parentId", collection.ParentId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@itemCount", collection.ItemCount);
                cmd.Parameters.AddWithValue("@backdropUrl", collection.BackdropUrl ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@posterUrl", collection.PosterUrl ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@sourceCatalogId", collection.SourceCatalogId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@dateCreated", collection.DateCreated?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@lastUpdated", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save collection {CollectionId}", collection.CollectionId);
                return false;
            }
        }

        #endregion

        #region Stream Caching

        /// <summary>
        /// Save streams for an item from Gelato/Stremio
        /// </summary>
        public async Task<bool> SaveStreamsAsync(
            string itemId, 
            string? stremioId,
            string? imdbId, 
            string? tmdbId, 
            string itemType, 
            string? userId,
            List<StreamInfo> streams)
        {
            try
            {
                EnsureConnection();

                // Delete old streams for this item+user combination
                using (var deleteCmd = _connection!.CreateCommand())
                {
                    deleteCmd.CommandText = @"
                        DELETE FROM Streams 
                        WHERE ItemId = @itemId 
                        AND (UserId = @userId OR (@userId IS NULL AND UserId IS NULL))
                    ";
                    deleteCmd.Parameters.AddWithValue("@itemId", itemId);
                    deleteCmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                // Insert new streams
                for (int i = 0; i < streams.Count; i++)
                {
                    var stream = streams[i];
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO Streams (
                            ItemId, StremioId, ImdbId, TmdbId, ItemType, UserId,
                            StreamUrl, InfoHash, FileIdx, Title, Name, Quality, 
                            Subtitle, Audio, BingeGroup, Filename, VideoSize, VideoHash,
                            Sources, StreamIndex, WebCompatible, CachedAt
                        ) VALUES (
                            @itemId, @stremioId, @imdbId, @tmdbId, @itemType, @userId,
                            @streamUrl, @infoHash, @fileIdx, @title, @name, @quality,
                            @subtitle, @audio, @bingeGroup, @filename, @videoSize, @videoHash,
                            @sources, @streamIndex, @webCompatible, @cachedAt
                        )
                    ";

                    cmd.Parameters.AddWithValue("@itemId", itemId);
                    cmd.Parameters.AddWithValue("@stremioId", stremioId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@imdbId", imdbId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@tmdbId", tmdbId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@itemType", itemType);
                    cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamUrl", stream.StreamUrl ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@infoHash", stream.InfoHash ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fileIdx", stream.FileIdx.HasValue ? stream.FileIdx.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@title", stream.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", stream.Name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@quality", stream.Quality ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@subtitle", stream.Subtitle ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@audio", stream.Audio ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@bingeGroup", stream.BingeGroup ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@filename", stream.Filename ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@videoSize", stream.VideoSize.HasValue ? stream.VideoSize.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@videoHash", stream.VideoHash ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@sources", stream.Sources ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamIndex", i);
                    cmd.Parameters.AddWithValue("@webCompatible", stream.WebCompatible.HasValue ? (stream.WebCompatible.Value ? 1 : 0) : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@cachedAt", DateTime.UtcNow.ToString("o"));

                    await cmd.ExecuteNonQueryAsync();
                }

                _logger.LogInformation("[BaklavaDb] Saved {Count} streams for {ItemId}", streams.Count, itemId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save streams for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Get cached streams for an item
        /// </summary>
        public async Task<List<StreamInfo>?> GetStreamsAsync(string itemId, string? userId = null)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        StreamUrl, InfoHash, FileIdx, Title, Name, Quality,
                        Subtitle, Audio, BingeGroup, Filename, VideoSize, VideoHash,
                        Sources, StreamIndex, CachedAt, StremioId, WebCompatible
                    FROM Streams 
                    WHERE ItemId = @itemId 
                    AND (UserId = @userId OR (@userId IS NULL AND UserId IS NULL))
                    ORDER BY StreamIndex
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);

                var streams = new List<StreamInfo>();
                using var reader = await cmd.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    var stremioId = reader.IsDBNull(15) ? null : reader.GetString(15);
                    var webCompat = reader.IsDBNull(16) ? (bool?)null : reader.GetInt32(16) == 1;
                    
                    streams.Add(new StreamInfo
                    {
                        StreamUrl = reader.IsDBNull(0) ? null : reader.GetString(0),
                        InfoHash = reader.IsDBNull(1) ? null : reader.GetString(1),
                        FileIdx = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        Title = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Name = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Subtitle = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Audio = reader.IsDBNull(7) ? null : reader.GetString(7),
                        BingeGroup = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Filename = reader.IsDBNull(9) ? null : reader.GetString(9),
                        VideoSize = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        VideoHash = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Sources = reader.IsDBNull(12) ? null : reader.GetString(12),
                        StremioId = stremioId,
                        WebCompatible = webCompat,
                        ProbedStreams = null, // Will be populated below
                        ExternalSubtitles = null // Will be populated below
                    });
                }

                if (streams.Count > 0)
                {
                    // Fetch probed streams for each stream
                    foreach (var stream in streams)
                    {
                        stream.ProbedStreams = await GetProbedStreamsAsync(itemId, stream.StremioId);
                    }
                    
                    // Fetch external subtitles for the item
                    var externalSubtitles = await GetExternalSubtitlesAsync(itemId);
                    if (externalSubtitles != null && externalSubtitles.Count > 0)
                    {
                        // Convert Api.ExternalSubtitleInfo to Services.ExternalSubtitleInfo
                        var convertedSubs = externalSubtitles.Select(s => new Baklava.Services.ExternalSubtitleInfo
                        {
                            Id = 0, // Will be assigned from DB if needed
                            ItemId = itemId,
                            SubtitleId = s.Id,
                            Url = s.Url,
                            Language = s.Language,
                            Title = s.Title,
                            CachedAt = s.CachedAt
                        }).ToList();
                        
                        // Add external subtitles to all streams (they're item-level, not stream-specific)
                        foreach (var stream in streams)
                        {
                            stream.ExternalSubtitles = convertedSubs;
                        }
                    }

                    _logger.LogInformation("[BaklavaDb] Found {Count} cached streams for {ItemId} with probed data and subtitles", streams.Count, itemId);
                    return streams;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get streams for {ItemId}", itemId);
                return null;
            }
        }

        /// <summary>
        /// Compare fresh streams with cached streams and identify new ones
        /// </summary>
        public StreamComparisonResult CompareStreams(List<StreamInfo>? cachedStreams, List<StreamInfo> freshStreams)
        {
            var result = new StreamComparisonResult();

            if (cachedStreams == null || cachedStreams.Count == 0)
            {
                // No cache, all fresh streams are new
                result.NewStreams = freshStreams;
                result.HasNewStreams = freshStreams.Count > 0;
                _logger.LogDebug("[BaklavaDb] No cached streams, all {Count} are new", freshStreams.Count);
                return result;
            }

            // Create a hash set of cached stream identifiers for fast lookup
            var cachedIdentifiers = new HashSet<string>();
            foreach (var cached in cachedStreams)
            {
                var identifier = GetStreamIdentifier(cached);
                cachedIdentifiers.Add(identifier);
            }

            // Find new streams not in cache
            var newStreams = new List<StreamInfo>();
            foreach (var fresh in freshStreams)
            {
                var identifier = GetStreamIdentifier(fresh);
                if (!cachedIdentifiers.Contains(identifier))
                {
                    newStreams.Add(fresh);
                }
            }

            result.NewStreams = newStreams;
            result.CachedStreams = cachedStreams;
            result.HasNewStreams = newStreams.Count > 0;
            result.TotalCached = cachedStreams.Count;
            result.TotalFresh = freshStreams.Count;
            result.TotalNew = newStreams.Count;

            _logger.LogDebug(
                "[BaklavaDb] Stream comparison: {Cached} cached, {Fresh} fresh, {New} new", 
                cachedStreams.Count, 
                freshStreams.Count, 
                newStreams.Count
            );

            return result;
        }

        /// <summary>
        /// Merge new streams into existing cache without deleting old ones
        /// </summary>
        public async Task<bool> MergeStreamsAsync(
            string itemId,
            string? stremioId,
            string? imdbId,
            string? tmdbId,
            string itemType,
            string? userId,
            List<StreamInfo> newStreams)
        {
            try
            {
                EnsureConnection();

                if (newStreams.Count == 0)
                {
                    _logger.LogDebug("[BaklavaDb] No new streams to merge for {ItemId}", itemId);
                    return true;
                }

                // Get current max index
                int maxIndex = 0;
                using (var queryCmd = _connection!.CreateCommand())
                {
                    queryCmd.CommandText = @"
                        SELECT COALESCE(MAX(StreamIndex), -1) 
                        FROM Streams 
                        WHERE ItemId = @itemId 
                        AND (UserId = @userId OR (@userId IS NULL AND UserId IS NULL))
                    ";
                    queryCmd.Parameters.AddWithValue("@itemId", itemId);
                    queryCmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
                    
                    var result = await queryCmd.ExecuteScalarAsync();
                    maxIndex = result != null ? Convert.ToInt32(result) : -1;
                }

                // Insert new streams with indices starting after existing ones
                var expiresAt = DateTime.UtcNow.AddDays(7);
                int startIndex = maxIndex + 1;

                for (int i = 0; i < newStreams.Count; i++)
                {
                    var stream = newStreams[i];
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO Streams (
                            ItemId, StremioId, ImdbId, TmdbId, ItemType, UserId,
                            StreamUrl, InfoHash, FileIdx, Title, Name, Quality, 
                            Subtitle, Audio, BingeGroup, Filename, VideoSize, VideoHash,
                            Sources, StreamIndex, WebCompatible, CachedAt
                        ) VALUES (
                            @itemId, @stremioId, @imdbId, @tmdbId, @itemType, @userId,
                            @streamUrl, @infoHash, @fileIdx, @title, @name, @quality,
                            @subtitle, @audio, @bingeGroup, @filename, @videoSize, @videoHash,
                            @sources, @streamIndex, @webCompatible, @cachedAt
                        )
                    ";

                    cmd.Parameters.AddWithValue("@itemId", itemId);
                    cmd.Parameters.AddWithValue("@stremioId", stremioId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@imdbId", imdbId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@tmdbId", tmdbId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@itemType", itemType);
                    cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamUrl", stream.StreamUrl ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@infoHash", stream.InfoHash ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fileIdx", stream.FileIdx.HasValue ? stream.FileIdx.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@title", stream.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", stream.Name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@quality", stream.Quality ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@subtitle", stream.Subtitle ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@audio", stream.Audio ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@bingeGroup", stream.BingeGroup ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@filename", stream.Filename ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@videoSize", stream.VideoSize.HasValue ? stream.VideoSize.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@videoHash", stream.VideoHash ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@sources", stream.Sources ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamIndex", startIndex + i);
                    cmd.Parameters.AddWithValue("@webCompatible", stream.WebCompatible.HasValue ? (stream.WebCompatible.Value ? 1 : 0) : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@cachedAt", DateTime.UtcNow.ToString("o"));

                    await cmd.ExecuteNonQueryAsync();
                }

                _logger.LogInformation("[BaklavaDb] Merged {Count} new streams for {ItemId}", newStreams.Count, itemId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to merge streams for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Generate a unique identifier for a stream based on its key properties
        /// </summary>
        private string GetStreamIdentifier(StreamInfo stream)
        {
            // Use multiple properties to create a unique identifier
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(stream.InfoHash))
            {
                parts.Add($"ih:{stream.InfoHash}");
                if (stream.FileIdx.HasValue)
                    parts.Add($"idx:{stream.FileIdx}");
            }
            else if (!string.IsNullOrEmpty(stream.StreamUrl))
            {
                parts.Add($"url:{stream.StreamUrl}");
            }

            if (!string.IsNullOrEmpty(stream.BingeGroup))
                parts.Add($"bg:{stream.BingeGroup}");
            
            if (!string.IsNullOrEmpty(stream.Filename))
                parts.Add($"fn:{stream.Filename}");
            
            if (stream.VideoSize.HasValue)
                parts.Add($"vs:{stream.VideoSize}");

            if (!string.IsNullOrEmpty(stream.VideoHash))
                parts.Add($"vh:{stream.VideoHash}");

            return parts.Count > 0 ? string.Join("|", parts) : Guid.NewGuid().ToString();
        }



        /// <summary>
        /// Save probed media streams (audio and subtitles) for an item
        /// </summary>
        public async Task<bool> SaveProbedStreamsAsync(
            string itemId,
            string? stremioId,
            string? streamSourceId,
            List<ProbedStreamInfo> streams)
        {
            try
            {
                EnsureConnection();

                // Delete old probed streams for this item+source
                using (var deleteCmd = _connection!.CreateCommand())
                {
                    deleteCmd.CommandText = @"
                        DELETE FROM ProbedStreams 
                        WHERE ItemId = @itemId 
                        AND (StreamSourceId = @streamSourceId OR (@streamSourceId IS NULL AND StreamSourceId IS NULL))
                    ";
                    deleteCmd.Parameters.AddWithValue("@itemId", itemId);
                    deleteCmd.Parameters.AddWithValue("@streamSourceId", streamSourceId ?? (object)DBNull.Value);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                // Insert new probed streams
                foreach (var stream in streams)
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO ProbedStreams (
                            ItemId, StremioId, StreamSourceId, StreamType, StreamIndex,
                            Codec, Language, Title, DisplayTitle, IsDefault, IsForced,
                            Channels, ChannelLayout, SampleRate, BitRate, BitDepth,
                            Profile, Level, IsExternal, Path, CachedAt
                        ) VALUES (
                            @itemId, @stremioId, @streamSourceId, @streamType, @streamIndex,
                            @codec, @language, @title, @displayTitle, @isDefault, @isForced,
                            @channels, @channelLayout, @sampleRate, @bitRate, @bitDepth,
                            @profile, @level, @isExternal, @path, @cachedAt
                        )
                    ";

                    cmd.Parameters.AddWithValue("@itemId", itemId);
                    cmd.Parameters.AddWithValue("@stremioId", stremioId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamSourceId", streamSourceId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@streamType", stream.StreamType);
                    cmd.Parameters.AddWithValue("@streamIndex", stream.StreamIndex);
                    cmd.Parameters.AddWithValue("@codec", stream.Codec ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@language", stream.Language ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@title", stream.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@displayTitle", stream.DisplayTitle ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@isDefault", stream.IsDefault ? 1 : 0);
                    cmd.Parameters.AddWithValue("@isForced", stream.IsForced ? 1 : 0);
                    cmd.Parameters.AddWithValue("@channels", stream.Channels.HasValue ? stream.Channels.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@channelLayout", stream.ChannelLayout ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@sampleRate", stream.SampleRate.HasValue ? stream.SampleRate.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@bitRate", stream.BitRate.HasValue ? stream.BitRate.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@bitDepth", stream.BitDepth.HasValue ? stream.BitDepth.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@profile", stream.Profile ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@level", stream.Level.HasValue ? stream.Level.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@isExternal", stream.IsExternal ? 1 : 0);
                    cmd.Parameters.AddWithValue("@path", stream.Path ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@cachedAt", DateTime.UtcNow.ToString("o"));

                    await cmd.ExecuteNonQueryAsync();
                }

                _logger.LogInformation("[BaklavaDb] Saved {Count} probed streams for {ItemId}", streams.Count, itemId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save probed streams for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Get cached probed streams for an item
        /// </summary>
        public async Task<List<ProbedStreamInfo>?> GetProbedStreamsAsync(string itemId, string? streamSourceId = null)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        StreamType, StreamIndex, Codec, Language, Title, DisplayTitle,
                        IsDefault, IsForced, Channels, ChannelLayout, SampleRate,
                        BitRate, BitDepth, Profile, Level, IsExternal, Path
                    FROM ProbedStreams 
                    WHERE ItemId = @itemId 
                    AND (StreamSourceId = @streamSourceId OR (@streamSourceId IS NULL AND StreamSourceId IS NULL))
                    ORDER BY StreamType, StreamIndex
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@streamSourceId", streamSourceId ?? (object)DBNull.Value);

                var streams = new List<ProbedStreamInfo>();
                using var reader = await cmd.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    streams.Add(new ProbedStreamInfo
                    {
                        StreamType = reader.GetString(0),
                        StreamIndex = reader.GetInt32(1),
                        Codec = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Language = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                        DisplayTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                        IsDefault = reader.GetInt32(6) == 1,
                        IsForced = reader.GetInt32(7) == 1,
                        Channels = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        ChannelLayout = reader.IsDBNull(9) ? null : reader.GetString(9),
                        SampleRate = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        BitRate = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        BitDepth = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        Profile = reader.IsDBNull(13) ? null : reader.GetString(13),
                        Level = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                        IsExternal = reader.GetInt32(15) == 1,
                        Path = reader.IsDBNull(16) ? null : reader.GetString(16)
                    });
                }

                if (streams.Count > 0)
                {
                    _logger.LogInformation("[BaklavaDb] Found {Count} cached probed streams for {ItemId}", streams.Count, itemId);
                    return streams;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get probed streams for {ItemId}", itemId);
                return null;
            }
        }

        #endregion

        #region Catalog Item Staging

        /// <summary>
        /// Save catalog item to staging (before Jellyfin sync)
        /// </summary>
        public async Task<bool> SaveCatalogItemAsync(CatalogItemInfo item)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO CatalogItems (
                        CatalogId, ImdbId, TmdbId, ItemType, Title, Year, Poster, Background,
                        Overview, Rating, Genres, Status, JellyfinItemId, ImportedAt, SyncedAt,
                        ErrorMessage, RetryCount
                    ) VALUES (
                        @catalogId, @imdbId, @tmdbId, @itemType, @title, @year, @poster, @background,
                        @overview, @rating, @genres, @status, @jellyfinItemId, @importedAt, @syncedAt,
                        @errorMessage, @retryCount
                    )
                ";

                cmd.Parameters.AddWithValue("@catalogId", item.CatalogId);
                cmd.Parameters.AddWithValue("@imdbId", item.ImdbId);
                cmd.Parameters.AddWithValue("@tmdbId", item.TmdbId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@itemType", item.ItemType);
                cmd.Parameters.AddWithValue("@title", item.Title);
                cmd.Parameters.AddWithValue("@year", item.Year ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@poster", item.Poster ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@background", item.Background ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@overview", item.Overview ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@rating", item.Rating ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@genres", item.Genres ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@status", item.Status);
                cmd.Parameters.AddWithValue("@jellyfinItemId", item.JellyfinItemId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@importedAt", item.ImportedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@syncedAt", item.SyncedAt?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@retryCount", item.RetryCount);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save catalog item {ImdbId}", item.ImdbId);
                return false;
            }
        }

        /// <summary>
        /// Get catalog items by status (for syncing)
        /// </summary>
        public async Task<List<CatalogItemInfo>> GetCatalogItemsByStatusAsync(string status, int limit = 100)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, CatalogId, ImdbId, TmdbId, ItemType, Title, Year, Poster, Background,
                           Overview, Rating, Genres, Status, JellyfinItemId, ImportedAt, SyncedAt,
                           ErrorMessage, RetryCount
                    FROM CatalogItems
                    WHERE Status = @status
                    ORDER BY ImportedAt ASC
                    LIMIT @limit
                ";

                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@limit", limit);

                var items = new List<CatalogItemInfo>();
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    items.Add(new CatalogItemInfo
                    {
                        Id = reader.GetInt32(0),
                        CatalogId = reader.GetString(1),
                        ImdbId = reader.GetString(2),
                        TmdbId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        ItemType = reader.GetString(4),
                        Title = reader.GetString(5),
                        Year = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Poster = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Background = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Overview = reader.IsDBNull(9) ? null : reader.GetString(9),
                        Rating = reader.IsDBNull(10) ? null : reader.GetString(10),
                        Genres = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Status = reader.GetString(12),
                        JellyfinItemId = reader.IsDBNull(13) ? null : reader.GetString(13),
                        ImportedAt = DateTime.Parse(reader.GetString(14)),
                        SyncedAt = reader.IsDBNull(15) ? null : DateTime.Parse(reader.GetString(15)),
                        ErrorMessage = reader.IsDBNull(16) ? null : reader.GetString(16),
                        RetryCount = reader.GetInt32(17)
                    });
                }

                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get catalog items by status {Status}", status);
                return new List<CatalogItemInfo>();
            }
        }

        /// <summary>
        /// Update catalog item status after sync attempt
        /// </summary>
        public async Task<bool> UpdateCatalogItemStatusAsync(int id, string status, string? jellyfinItemId = null, string? errorMessage = null)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    UPDATE CatalogItems
                    SET Status = @status,
                        JellyfinItemId = @jellyfinItemId,
                        SyncedAt = @syncedAt,
                        ErrorMessage = @errorMessage,
                        RetryCount = CASE WHEN @status = 'failed' THEN RetryCount + 1 ELSE RetryCount END
                    WHERE Id = @id
                ";

                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@jellyfinItemId", jellyfinItemId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@syncedAt", status == "synced" ? DateTime.UtcNow.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to update catalog item {Id}", id);
                return false;
            }
        }

        /// <summary>
        /// Get catalog items for a specific catalog (for UI)
        /// </summary>
        public async Task<List<CatalogItemInfo>> GetCatalogItemsByCatalogIdAsync(string catalogId)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, CatalogId, ImdbId, TmdbId, ItemType, Title, Year, Poster, Background,
                           Overview, Rating, Genres, Status, JellyfinItemId, ImportedAt, SyncedAt,
                           ErrorMessage, RetryCount
                    FROM CatalogItems
                    WHERE CatalogId = @catalogId
                    ORDER BY Title ASC
                ";

                cmd.Parameters.AddWithValue("@catalogId", catalogId);

                var items = new List<CatalogItemInfo>();
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    items.Add(new CatalogItemInfo
                    {
                        Id = reader.GetInt32(0),
                        CatalogId = reader.GetString(1),
                        ImdbId = reader.GetString(2),
                        TmdbId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        ItemType = reader.GetString(4),
                        Title = reader.GetString(5),
                        Year = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Poster = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Background = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Overview = reader.IsDBNull(9) ? null : reader.GetString(9),
                        Rating = reader.IsDBNull(10) ? null : reader.GetString(10),
                        Genres = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Status = reader.GetString(12),
                        JellyfinItemId = reader.IsDBNull(13) ? null : reader.GetString(13),
                        ImportedAt = DateTime.Parse(reader.GetString(14)),
                        SyncedAt = reader.IsDBNull(15) ? null : DateTime.Parse(reader.GetString(15)),
                        ErrorMessage = reader.IsDBNull(16) ? null : reader.GetString(16),
                        RetryCount = reader.GetInt32(17)
                    });
                }

                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get catalog items for {CatalogId}", catalogId);
                return new List<CatalogItemInfo>();
            }
        }

        #endregion

        #region External Subtitles

        /// <summary>
        /// Save external subtitles for an item to the database
        /// </summary>
        public async Task<bool> SaveExternalSubtitlesAsync(string itemId, List<Api.ExternalSubtitleInfo> subtitles)
        {
            try
            {
                EnsureConnection();

                // Delete old subtitles for this item
                using (var deleteCmd = _connection!.CreateCommand())
                {
                    deleteCmd.CommandText = "DELETE FROM ExternalSubtitles WHERE ItemId = @itemId";
                    deleteCmd.Parameters.AddWithValue("@itemId", itemId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                // Insert new subtitles
                foreach (var sub in subtitles)
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO ExternalSubtitles (ItemId, SubtitleId, Url, Language, Title, CachedAt)
                        VALUES (@itemId, @subId, @url, @lang, @title, @cachedAt)
                    ";

                    cmd.Parameters.AddWithValue("@itemId", itemId);
                    cmd.Parameters.AddWithValue("@subId", sub.Id ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@url", sub.Url);
                    cmd.Parameters.AddWithValue("@lang", sub.Language ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@title", sub.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@cachedAt", DateTime.UtcNow.ToString("o"));

                    await cmd.ExecuteNonQueryAsync();
                }

                _logger.LogInformation("[BaklavaDb] Saved {Count} external subtitles for {ItemId}", subtitles.Count, itemId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to save external subtitles for {ItemId}", itemId);
                return false;
            }
        }

        /// <summary>
        /// Get cached external subtitles for an item
        /// </summary>
        public async Task<List<Api.ExternalSubtitleInfo>?> GetExternalSubtitlesAsync(string itemId)
        {
            try
            {
                EnsureConnection();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT SubtitleId, Url, Language, Title, CachedAt
                    FROM ExternalSubtitles
                    WHERE ItemId = @itemId
                    ORDER BY Language
                ";

                cmd.Parameters.AddWithValue("@itemId", itemId);

                var subtitles = new List<Api.ExternalSubtitleInfo>();
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    subtitles.Add(new Api.ExternalSubtitleInfo
                    {
                        Id = reader.IsDBNull(0) ? null : reader.GetString(0),
                        Url = reader.GetString(1),
                        Language = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Title = reader.IsDBNull(3) ? null : reader.GetString(3),
                        CachedAt = DateTime.Parse(reader.GetString(4))
                    });
                }

                return subtitles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaDb] Failed to get external subtitles for {ItemId}", itemId);
                return null;
            }
        }

        #endregion

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    /// <summary>
    /// Stream information structure for caching
    /// </summary>
    public class StreamInfo
    {
        public string? StreamUrl { get; set; }
        public string? InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Quality { get; set; }
        public string? Subtitle { get; set; }
        public string? Audio { get; set; }
        public string? BingeGroup { get; set; }
        public string? Filename { get; set; }
        public long? VideoSize { get; set; }
        public string? VideoHash { get; set; }
        public string? Sources { get; set; }
        
        // Extended data
        public bool? WebCompatible { get; set; }
        public string? StremioId { get; set; }
        public List<ProbedStreamInfo>? ProbedStreams { get; set; }
        public List<Baklava.Services.ExternalSubtitleInfo>? ExternalSubtitles { get; set; }
    }

    /// <summary>
    /// Complete item metadata model - Baklava's alternative to Jellyfin's item structure
    /// </summary>
    public class CompleteItemMetadata
    {
        public string ItemId { get; set; } = string.Empty;
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }
        public string? TvdbId { get; set; }
        public string ItemType { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? OriginalTitle { get; set; }
        public string? Overview { get; set; }
        public string? Tagline { get; set; }
        public int? Year { get; set; }
        public DateTime? PremiereDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? Status { get; set; }
        public string? OfficialRating { get; set; }
        public float? CommunityRating { get; set; }
        public float? CriticRating { get; set; }
        public long? Runtime { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Studios { get; set; }
        public List<string>? Tags { get; set; }
        public string? BackdropUrl { get; set; }
        public string? PosterUrl { get; set; }
        public string? LogoUrl { get; set; }
        public string? ParentId { get; set; }
        public string? CollectionId { get; set; }
        public string? CollectionName { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string? SeriesId { get; set; }
        public string? SeriesName { get; set; }
        public string? Path { get; set; }
        public string? FileName { get; set; }
        public string? Container { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? AspectRatio { get; set; }
        public float? Framerate { get; set; }
        public int? Bitrate { get; set; }
        public long? FileSize { get; set; }
        public DateTime? DateCreated { get; set; }
        public DateTime? DateModified { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
        public List<PersonInfo>? People { get; set; }
    }

    public class PersonInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Role { get; set; }
        public string Type { get; set; } = string.Empty; // Actor, Director, Writer, etc.
        public string? ImageUrl { get; set; }
        public int SortOrder { get; set; }
    }

    public class CollectionInfo
    {
        public string CollectionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Overview { get; set; }
        public string? Path { get; set; }
        public string? ParentId { get; set; }
        public int ItemCount { get; set; }
        public string? BackdropUrl { get; set; }
        public string? PosterUrl { get; set; }
        public string? SourceCatalogId { get; set; }
        public DateTime? DateCreated { get; set; }
    }

    /// <summary>
    /// Probed stream information (audio and subtitle tracks)
    /// </summary>
    public class ProbedStreamInfo
    {
        public string StreamType { get; set; } = string.Empty; // "Audio" or "Subtitle"
        public int StreamIndex { get; set; }
        public string? Codec { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
        public string? DisplayTitle { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        
        // Audio-specific
        public int? Channels { get; set; }
        public string? ChannelLayout { get; set; }
        public int? SampleRate { get; set; }
        public int? BitRate { get; set; }
        public int? BitDepth { get; set; }
        
        // Profile/Level
        public string? Profile { get; set; }
        public double? Level { get; set; }
        
        // External subtitle info
        public bool IsExternal { get; set; }
        public string? Path { get; set; }
    }

    /// <summary>
    /// Result of comparing cached streams vs fresh streams
    /// </summary>
    public class StreamComparisonResult
    {
        public List<StreamInfo>? CachedStreams { get; set; }
        public List<StreamInfo> NewStreams { get; set; } = new();
        public bool HasNewStreams { get; set; }
        public int TotalCached { get; set; }
        public int TotalFresh { get; set; }
        public int TotalNew { get; set; }
    }

    /// <summary>
    /// Catalog item for staging before Jellyfin sync
    /// </summary>
    public class CatalogItemInfo
    {
        public int Id { get; set; }
        public string CatalogId { get; set; } = string.Empty;
        public string ImdbId { get; set; } = string.Empty;
        public string? TmdbId { get; set; }
        public string ItemType { get; set; } = string.Empty; // "movie" or "series"
        public string Title { get; set; } = string.Empty;
        public string? Year { get; set; }
        public string? Poster { get; set; }
        public string? Background { get; set; }
        public string? Overview { get; set; }
        public string? Rating { get; set; }
        public string? Genres { get; set; } // JSON array
        public string Status { get; set; } = "pending"; // pending, synced, failed
        public string? JellyfinItemId { get; set; }
        public DateTime ImportedAt { get; set; }
        public DateTime? SyncedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// External subtitle info
    /// </summary>
    public class ExternalSubtitleInfo
    {
        public int Id { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string? SubtitleId { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Language { get; set; }
        public string? Title { get; set; }
        public DateTime CachedAt { get; set; }
    }
}
