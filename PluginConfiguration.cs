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


        // Allow non-admin users to directly import streams
        public bool EnableAutoImport { get; set; } = false;

        // Show TMDB reviews carousel on item details pages
        public bool ShowReviewsCarousel { get; set; } = true;
        
        // Playback UI selection per track type: 'carousel' or 'dropdown'
        public string VersionUi { get; set; } = "carousel";
        public string AudioUi { get; set; } = "carousel";
        public string SubtitleUi { get; set; } = "carousel";

        // Debrid API integration for stream metadata
        // Service type: 'realdebrid', 'alldebrid', 'premiumize', etc.
        public string DebridService { get; set; } = "realdebrid";
        
        // API key for the selected debrid service
        public string DebridApiKey { get; set; } = string.Empty;
        
        // Enable cached stream metadata lookup (recommended)
        // When enabled, fetches audio/subtitle info from debrid cache without triggering downloads
        public bool EnableDebridMetadata { get; set; } = true;
        
        // Fallback to traditional probing if debrid metadata fails
        // WARNING: When enabled, will download/cache the stream to your debrid account for ffprobe analysis
        public bool EnableFallbackProbe { get; set; } = false;



        // If enabled, only fetches metadata for the specific version selected by the user.
        // Reduces initial loading time but audio/subs for other versions won't be pre-populated.
        public bool FetchCachedMetadataPerVersion { get; set; } = false;

        // If enabled, tries to fetch (and potentially download/probe) all non-cached streams.
        // WARNING: Costly operation.
        public bool FetchAllNonCachedMetadata { get; set; } = false;

        // Enable fetching external subtitles from Stremio
        public bool EnableSubs { get; set; } = true;
    }
}
