# Baklava - Jellyfin Media Request & Search Enhancement Plugin

<p align="center">
  <img src="Baklava.png" alt="Baklava Logo" width="200"/>
</p>

A comprehensive Jellyfin plugin that adds intelligent media request management, enhanced search capabilities with local/global toggle, and seamless integration with external search providers.

## ✨ Features

### 🎬 Media Request System
- **User Requests**: Allow users to request movies and TV series
- **Admin Approval Workflow**: Approve or deny requests through an intuitive interface
- **Request Tracking**: Monitor pending and approved requests
- **Responsive UI**: Optimized for all screen sizes with adaptive card layouts

### 🔍 Enhanced Search
- **Search Toggle**: Easy switch between local (Jellyfin library) and global (external sources) search
- **Visual Indicator**: Globe icon with slash overlay for local search mode
- **Smart Defaults**: Global search by default for discovery, configurable local search enforcement
- **TV Client Support**: Automatic local search enforcement for TV clients (Android TV, Fire TV, etc.)

### 🎯 Server-Side Processing
- **SearchActionFilter**: Intelligent request interception and routing
- **Prefix Handling**: Automatic "local:" prefix management
- **Gelato Integration**: Seamless handoff to external search providers
- **Configurable**: Enable/disable features through plugin settings

## 📦 Installation

### Via Jellyfin Plugin Repository (Recommended)
1. Open Jellyfin Dashboard
2. Navigate to **Plugins** → **Repositories**
3. Add repository URL: `https://raw.githubusercontent.com/j4ckgrey/Baklava/main/manifest.json`
4. Go to **Catalog** and install **Baklava**
5. Restart Jellyfin
## ⚙️ Configuration

## ⚠️ Prerequisites

Before installing Baklava make sure the following Jellyfin plugins are installed and configured on your server:

- **Gelato** — external search provider used by Baklava for global discovery: https://github.com/lostb1t/Gelato
- **File Transformation** — required for certain media handling and transformations used by Baklava: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation

Install and verify these plugins are working before installing Baklava. Failure to have these available may cause limited functionality.

### Plugin Settings

Navigate to **Dashboard** → **Plugins** → **Baklava** to configure:

#### Search Filter Settings
- **Enable Search Filter**: Toggle server-side search prefix handling
- **Force TV Client Local Search**: Automatically enforce local search for TV clients (Android TV, Fire TV, etc.)

#### TMDB Integration
- **TMDB API Key**: For metadata lookups and poster images
- **Default TMDB ID**: Default ID for config page testing

## 🚀 Usage

### Search Toggle

The search toggle appears as a globe icon (🌐) next to the search bar:

- **Globe (no slash)**: Global search mode - searches external sources via Gelato
- **Globe with slash (🚫)**: Local search mode - searches only your Jellyfin library

**To use:**
1. Type your search query
2. Click the globe icon to toggle between local and global
3. Results refresh automatically on toggle

### Media Requests

#### For Users:
1. Browse or search for media
2. Click "Request" button on items not in your library
3. Track your requests in the Requests dropdown
4. Get notified when requests are approved

#### For Admins:
1. Open the Requests dropdown (bell icon)
2. View pending requests organized by Movies/Series
3. Click a request to see details
4. Approve or Deny with one click
5. Approved items move to the "Approved" section

### TV Client Behavior

When **Force TV Client Local Search** is enabled (default: ON):
- Android TV, Fire TV, and other TV clients automatically use local search
- The "local:" prefix is added server-side
- No user interaction needed - transparent enforcement

##  Troubleshooting

### Search toggle not appearing
- Clear browser cache
- Check browser console for JavaScript errors
- Verify plugin is loaded: Dashboard → Plugins

### TV client not using local search
- Enable "Force TV Client Local Search" in settings
- Check server logs for "✓ Detected TV client" messages
- Verify filter order (Baklava should be order 0)

### Requests not saving
- Check file permissions on plugin data directory
- Verify TMDB API key is valid
- Check server logs for errors

## 📧 Support

- **Issues**: https://github.com/j4ckgrey/Baklava/issues
- **Discussions**: https://github.com/j4ckgrey/Baklava/discussions

---

License and acknowledgments above.
