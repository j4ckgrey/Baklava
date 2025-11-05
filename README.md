# Baklava - Jellyfin Media Request Plugin

A comprehensive media request management system for Jellyfin with enhanced search features.

## Features

- **🎬 Request System**: Users can request movies and TV shows directly from search results
- **✅ Admin Approval**: Administrators can approve or remove requests with one click
- **📊 Status Tracking**: Track request status (pending/approved) with visual badges
- **🔍 Search Integration**: Enhanced search with local/global toggle and request indicators
- **📋 Request Menu**: Header dropdown showing all media requests with carousel layout
- **📚 Library Integration**: Automatically detects if requested media is already in your library

## Installation

### Via Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard** → **Plugins** → **Repositories**
2. Click **+** to add a new repository
3. Enter this manifest URL:
   ```
   https://raw.githubusercontent.com/j4ckgrey/jellyfin-plugin-baklava/main/manifest.json
   ```
4. Click the new repository in the list to view plugins
5. Find "Baklava" and click **Install**
6. Restart Jellyfin

### Dependencies

- **Jellyfin 10.11.0** or newer
- **FileTransformation Plugin 2.4.2.0** or newer (automatically handles HTML injection)

## Usage

### For Users

1. Search for a movie or TV show in Jellyfin
2. Click on an item to open the details modal
3. Click the **Request** button
4. Your request will show as "Pending" and appear in the admin's request menu

### For Administrators

1. Click the **list icon** (📋) in the top-right header
2. View all pending and approved requests in the dropdown
3. Click on a request to see details
4. Click **Approve** to accept or **Remove** to deny

### Search Toggle

Click the **globe icon** (🌐) next to the search bar to toggle between:
- **Local Search**: Search only your Jellyfin library
- **Global Search**: Search all available media (default)

## Screenshots

### Request Modal
Users can request media with one click, admins see approval buttons.

### Request Menu
Dropdown carousel showing all requests with status badges.

### Search Integration
Search results show if items are already requested.

## Development

### Building from Source

```bash
# Clone the repository
git clone https://github.com/j4ckgrey/jellyfin-plugin-baklava.git
cd jellyfin-plugin-baklava

# Build the plugin
dotnet build -c Release

# Output will be in bin/Release/net9.0/
```

### Project Structure

```
Baklava/
├── Api/
│   └── RequestsController.cs      # REST API for requests
├── Configuration/
│   └── configPage.html            # Plugin settings page
├── Files/
│   └── wwwroot/                   # Injected JavaScript files
│       ├── shared-utils.js        # Shared utilities
│       ├── library-status.js      # Library checking
│       ├── details-modal.js       # Item details modal
│       ├── request-manager.js     # Request API client
│       ├── requests-header-button.js  # Header dropdown
│       ├── search-toggle.js       # Search mode toggle
│       └── custom.css             # Custom styles
├── Model/
│   └── MediaRequest.cs            # Request data model
├── FileTransformations.cs         # HTML injection logic
├── Plugin.cs                      # Main plugin class
└── PluginConfiguration.cs         # Settings model
```

## API Endpoints

The plugin exposes the following REST API:

- `GET /api/baklava/requests` - Get all requests
- `POST /api/baklava/requests` - Create a new request
- `PUT /api/baklava/requests/{id}` - Update request status
- `DELETE /api/baklava/requests/{id}` - Delete a request

## Configuration

The plugin stores all request data in Jellyfin's configuration:
- **Linux**: `/etc/jellyfin/plugins/configurations/Baklava.xml`
- **Windows**: `C:\ProgramData\Jellyfin\Server\config\plugins\configurations\Baklava.xml`
- **Docker**: `{config}/plugins/configurations/Baklava.xml`

## Contributing

Pull requests are welcome! For major changes, please open an issue first to discuss what you would like to change.

## License

MIT License - See [LICENSE](LICENSE) file for details

## Credits

Created by [j4ckgrey](https://github.com/j4ckgrey)

Special thanks to the Jellyfin team and the FileTransformation plugin developers.

## Support

If you encounter any issues:
1. Check Jellyfin logs: **Dashboard** → **Advanced** → **Logs**
2. Open an issue on [GitHub](https://github.com/j4ckgrey/jellyfin-plugin-baklava/issues)
3. Include your Jellyfin version and error logs
