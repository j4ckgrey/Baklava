using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using Baklava.Api;

namespace Baklava
{
    // Holds settings for your plugin. Add properties here to persist configuration.
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Stored list of requests
        public List<MediaRequest> Requests { get; set; } = new List<MediaRequest>();

        // Optional configuration values used by the API
        // Default TMDB ID to show on the config page
        public string DefaultTmdbId { get; set; } = string.Empty;

        // TMDB API key for metadata lookups
        public string TmdbApiKey { get; set; } = string.Empty;

        // External Subtitles
        public bool EnableExternalSubtitles { get; set; } = false;

        // Gelato integration settings (server-side proxy)
        // Set GelatoBaseUrl to the base URL where Gelato is reachable from the Jellyfin server
        // e.g. http://localhost:8096
        public string GelatoBaseUrl { get; set; } = string.Empty;

        // GelatoAuthHeader supports either a full header like "X-Emby-Token: abc..." or
        // a bare Authorization value which will be used as the Authorization header.
        public string GelatoAuthHeader { get; set; } = string.Empty;

        // Search configuration
        // Enable or disable the search prefix filter functionality
        public bool EnableSearchFilter { get; set; } = true;

        // Force TV clients to use local search only (enabled by default)
        public bool ForceTVClientLocalSearch { get; set; } = true;

        // Disable requests for non-admin users (they get "Open" button instead)
        public bool DisableNonAdminRequests { get; set; } = false;

        // Allow non-admin users to directly import streams
        public bool EnableAutoImport { get; set; } = false;
        public bool DisableModal { get; set; } = false;

        // Show TMDB reviews carousel on item details pages
        public bool ShowReviewsCarousel { get; set; } = true;
        
        // Review source selection: "tmdb", "trakt", or "imdb"
        public string ReviewSource { get; set; } = "tmdb";
        
        // Trakt API Client ID
        public string TraktClientId { get; set; } = string.Empty;
        
        // RapidAPI Key for IMDb reviews (MoviesDatabase API)
        public string RapidApiKey { get; set; } = string.Empty;
        
        // Toggle for Baklava UI Injection (index.html override)
        public bool EnableBaklavaUI { get; set; } = true;

        // Playback UI selection per track type: 'carousel' or 'dropdown'
        public string VersionUi { get; set; } = "carousel";
        public string AudioUi { get; set; } = "carousel";
        public string SubtitleUi { get; set; } = "carousel";

        // Catalog Management Settings
        // Maximum items to import per catalog
        public int CatalogMaxItems { get; set; } = 100;
        
        // Catalog import timeout in seconds
        public int CatalogImportTimeout { get; set; } = 300;
        
        // Delay between series imports (in milliseconds) to prevent rate limiting
        // When importing series, each series triggers episode imports which can cause rate limits
        // Default: 2000ms (2 seconds) between each series
        public int SeriesImportDelayMs { get; set; } = 2000;
        
        // Maximum parallel imports for movies (series are always sequential to prevent rate limiting)
        // Default: 2 (reduced from 4 to be more conservative)
        public int MaxParallelMovieImports { get; set; } = 2;
        
        // Use Baklava as stream/metadata cache middleware (bypasses Jellyfin's cache)
        // When enabled, streams and metadata are permanently cached in Baklava's SQLite database
        public bool UseBaklavaCache { get; set; } = false;
        
        // Use Baklava staging for catalog imports (two-phase import)
        // When enabled, catalog items are saved to Baklava DB first, then synced to Jellyfin gradually
        // This prevents database lock issues during large catalog imports
        public bool UseBaklavaStaging { get; set; } = true;
    }
}
