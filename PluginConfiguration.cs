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

        // Debrid Configuration
        public string DebridService { get; set; } = "realdebrid";
        public string DebridApiKey { get; set; } = string.Empty;  // RealDebrid API Key (legacy, kept for compatibility)
        
        // Multi-Debrid API Keys
        public string RealDebridApiKey { get; set; } = string.Empty;
        public string TorBoxApiKey { get; set; } = string.Empty;
        public string AllDebridApiKey { get; set; } = string.Empty;
        public string PremiumizeApiKey { get; set; } = string.Empty;

        // Advanced Debrid Metadata Settings
        public bool EnableDebridMetadata { get; set; } = true;
        public bool EnableFallbackProbe { get; set; } = false;
        public bool FetchCachedMetadataPerVersion { get; set; } = false;
        public bool FetchAllNonCachedMetadata { get; set; } = false;

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
        
        // Playback UI selection per track type: 'carousel' or 'dropdown'
        public string VersionUi { get; set; } = "carousel";
        public string AudioUi { get; set; } = "carousel";
        public string SubtitleUi { get; set; } = "carousel";
    }
}
