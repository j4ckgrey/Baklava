# Baklava - Jellyfin Media Request & Search Enhancement Plugin

<p align="center">
  <!-- Logo removed from repository; image intentionally omitted -->
  <strong>Baklava</strong>
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

### 📱 Responsive Design
- **Mobile Optimized**: Cards automatically resize for smaller screens
  - Desktop: 140px × 210px cards
  - Tablet: 110px × 165px cards
  - Mobile: 90px × 135px cards
- **Full-Height Modals**: Request window uses available vertical space (40px margins top/bottom)
- **Touch-Friendly**: Optimized for touch interfaces

## 📦 Installation

### Via Jellyfin Plugin Repository (Recommended)
1. Open Jellyfin Dashboard
2. Navigate to **Plugins** → **Repositories**
3. Add repository URL: `https://raw.githubusercontent.com/j4ckgrey/jellyfin-plugin-baklava/main/manifest.json`
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

#### Gelato Integration
- **Gelato Base URL**: URL where Gelato is accessible from Jellyfin server (e.g., `http://localhost:8096`)
- **Gelato Auth Header**: Authentication header for Gelato API

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

## 🔧 Technical Details

### Architecture

```
Client Request
     ↓
SearchActionFilter (Order: 0)
     ↓
   [TV Client?] → Add "local:" prefix
     ↓
   [Has "local:" prefix?]
     ↓              ↓
   YES            NO
     ↓              ↓
Gelato Filter → Gelato Filter
     ↓              ↓
Jellyfin Search   External Search
```

### Filter Order
- **Baklava SearchActionFilter**: Order 0 (runs first)
- **Gelato SearchActionFilter**: Order 2 (runs after Baklava)

### API Endpoints

- `GET /Baklava/Requests` - List all requests (admin) or user's requests
- `POST /Baklava/Requests` - Create new request
- `PATCH /Baklava/Requests/{id}` - Approve/deny request
- `DELETE /Baklava/Requests/{id}` - Delete request

## 🛠️ Development

### Building from Source

```bash
git clone https://github.com/j4ckgrey/jellyfin-plugin-baklava.git
cd jellyfin-plugin-baklava
dotnet publish -c Release
```

Output: `bin/Release/net9.0/publish/`

### Project Structure
```
Baklava/
├── Api/                    # API controllers
│   ├── ConfigController.cs
│   ├── MetadataController.cs
│   └── RequestsController.cs
├── Configuration/          # Plugin configuration
│   └── configPage.html
├── Files/wwwroot/          # Web assets
│   ├── custom.css
│   ├── search-toggle.js
│   ├── requests.js
│   └── ...
├── Filters/                # MVC action filters
│   └── SearchActionFilter.cs
├── Services/               # Background services
│   └── StartupService.cs
├── Plugin.cs               # Main plugin class
└── PluginConfiguration.cs  # Configuration model
```

### Dependencies
- Jellyfin.Model 10.*
- Jellyfin.Controller 10.*
- .NET 9.0

## 🐛 Troubleshooting

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

## 📝 Changelog

### v0.1.0 (2025-11-09)
- Initial release
- Media request management system
- Search toggle functionality
- TV client local search enforcement
- Responsive UI for all screen sizes
- Server-side search prefix handling
- Gelato integration

## 🤝 Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.

## 🙏 Acknowledgments

- **Jellyfin** - For the amazing media server platform
- **Gelato** ([lostb1t/Gelato](https://github.com/lostb1t/Gelato)) - External search provider integration
- **TMDB** - Movie and TV metadata

## 📧 Support

- **Issues**: [GitHub Issues](https://github.com/j4ckgrey/jellyfin-plugin-baklava/issues)
- **Discussions**: [GitHub Discussions](https://github.com/j4ckgrey/jellyfin-plugin-baklava/discussions)

---

**Made with ❤️ for the Jellyfin community**
