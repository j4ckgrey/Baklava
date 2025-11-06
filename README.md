# Baklava

A Jellyfin plugin that adds media request management and enhanced search functionality.

## Features

- **Media Request System**: Users can request movies and TV shows
- **Admin Approval Workflow**: Admins can approve or remove requests
- **Request Status Tracking**: Pending/Approved status management
- **Enhanced Search**: Toggle between TMDB search and Jellyfin library
- **Details Modal**: Rich media information with cast, reviews, and posters
- **Request Dropdown Menu**: Quick access to all requests from the header
- **Server-Side Storage**: Requests stored in Jellyfin's plugin configuration

## Installation

1. Add the plugin repository to Jellyfin
2. Install "Baklava" from the catalog
3. Restart Jellyfin
4. Access the plugin configuration from Dashboard → Plugins

## Requirements

- Jellyfin 10.11.0 or higher
- FileTransformation plugin 2.4.2.0 or higher

## Version

Current version: **0.1.0.0**

## API Endpoints

- `GET /api/baklava/requests` - Get all requests
- `POST /api/baklava/requests` - Create new request
- `PUT /api/baklava/requests/{id}` - Update request (approve/status)
- `DELETE /api/baklava/requests/{id}` - Delete request

## License

MIT
