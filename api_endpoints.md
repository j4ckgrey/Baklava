# Baklava API Endpoints

Complete reference for all REST API endpoints in the Baklava plugin.

---

## Configuration

### Get Configuration
**Endpoint:** `GET /api/baklava/config`  
**Auth:** Optional (different data returned for admin vs non-admin)  
**Description:** Retrieves Baklava plugin configuration settings.

**Response:**
- Admin users: All configuration including sensitive keys (TMDB API key, Trakt client ID, etc.)
- Non-admin users: Public configuration only (review source, UI settings, etc.)

**Example:**
```bash
curl http://localhost:8096/api/baklava/config \
  -H "X-Emby-Token: YOUR_API_KEY"
```

---

### Update Configuration
**Endpoint:** `PUT /api/baklava/config`  
**Auth:** Required (Administrator only)  
**Description:** Updates plugin configuration settings.

**Request Body:**
```json
{
  "defaultTmdbId": "550",
  "tmdbApiKey": "your-api-key",
  "enableSearchFilter": true,
  "showReviewsCarousel": true,
  "reviewSource": "tmdb"
}
```

**Example:**
```bash
curl -X PUT http://localhost:8096/api/baklava/config \
  -H "Content-Type: application/json" \
  -H "X-Emby-Token: YOUR_API_KEY" \
  -d '{"reviewSource":"tmdb"}'
```

---

## Metadata & External IDs

### Get TMDB Metadata
**Endpoint:** `GET /api/baklava/metadata/tmdb`  
**Auth:** Optional  
**Query Parameters:**
- `tmdbId` (required): TMDB ID
- `itemType` (optional): "movie" or "tv"

**Description:** Fetches metadata from TMDB for a specific ID.

**Example:**
```bash
curl "http://localhost:8096/api/baklava/metadata/tmdb?tmdbId=550&itemType=movie" \
  -H "X-Emby-Token: YOUR_API_KEY"
```

---

### Check Library Status
**Endpoint:** `GET /api/baklava/metadata/library-status`  
**Auth:** Optional  
**Query Parameters:**
- `tmdbId` (required): TMDB ID
- `imdbId` (optional): IMDB ID

**Description:** Checks if an item exists in the Jellyfin library.

**Response:**
```json
{
  "exists": true,
  "itemId": "abc123",
  "name": "Fight Club"
}
```

---

### Get External IDs
**Endpoint:** `GET /api/baklava/metadata/external-ids`  
**Auth:** Optional  
**Query Parameters:**
- `imdbId` or `tmdbId` (one required)
- `itemType` (optional): "movie" or "tv"

**Description:** Converts between IMDB and TMDB IDs using TMDB API.

**Example:**
```bash
curl "http://localhost:8096/api/baklava/metadata/external-ids?imdbId=tt0137523" \
  -H "X-Emby-Token: YOUR_API_KEY"
```

---

### Get Streams
**Endpoint:** `GET /api/baklava/metadata/streams`  
**Auth:** Optional  
**Query Parameters:**
- `tmdbId` or `imdbId` (one required)
- `itemType` (required): "movie" or "series"
- `season` (optional): For TV shows
- `episode` (optional): For TV shows

**Description:** Fetches available streams from Gelato for a specific media item.

---

## Reviews

### Get Reviews for Item
**Endpoint:** `GET /api/baklava/reviews/{itemId}`  
**Auth:** Optional  
**Query Parameters:**
- `forceRefresh` (optional, default: false): Skip cache and fetch fresh reviews

**Description:** Retrieves reviews for a Jellyfin item from configured source (TMDB, Trakt, or IMDB).

**Example:**
```bash
curl http://localhost:8096/api/baklava/reviews/abc123 \
  -H "X-Emby-Token: YOUR_API_KEY"
```

**Response:**
```json
{
  "reviews": [
    {
      "author": "John Doe",
      "content": "Great movie!",
      "rating": 8,
      "created_at": "2023-01-01T00:00:00Z"
    }
  ],
  "source": "tmdb"
}
```

---

### Sync Reviews
**Endpoint:** `POST /api/baklava/reviews/sync`  
**Auth:** Required (Administrator)  
**Description:** Background task to sync reviews for all items in library.

---

## Requests (Jellyseerr-like System)

### Get All Requests
**Endpoint:** `GET /api/baklava/requests`  
**Auth:** Optional  
**Description:** Retrieves all media requests from users.

**Response:**
```json
[
  {
    "id": "user_12345_1234567890",
    "username": "john",
    "title": "Inception",
    "itemType": "movie",
    "tmdbId": "27205",
    "imdbId": "tt1375666",
    "status": "pending",
    "createdAt": "2023-01-01T00:00:00Z"
  }
]
```

---

### Create Request
**Endpoint:** `POST /api/baklava/requests`  
**Auth:** Optional  
**Description:** Creates a new media request.

**Request Body:**
```json
{
  "username": "john",
  "title": "Inception",
  "itemType": "movie",
  "tmdbId": "27205",
  "imdbId": "tt1375666"
}
```

---

### Update Request
**Endpoint:** `PUT /api/baklava/requests/{id}`  
**Auth:** Required (Administrator)  
**Description:** Updates request status (approve, deny, etc.).

**Request Body:**
```json
{
  "status": "approved",
  "adminNote": "Added to library"
}
```

---

### Delete Request
**Endpoint:** `DELETE /api/baklava/requests/{id}`  
**Auth:** Required (Administrator)  
**Description:** Deletes a specific request.

---

### Cleanup Old Requests
**Endpoint:** `POST /api/baklava/requests/cleanup`  
**Auth:** Required (Administrator)  
**Query Parameters:**
- `daysOld` (optional, default: 30): Delete requests older than X days

**Description:** Removes old completed or denied requests.

---

## Catalogs (Stremio/aiostreams)

### Get All Catalogs
**Endpoint:** `GET /api/baklava/catalogs`  
**Auth:** Required  
**Description:** Lists all available Stremio catalogs from configured aiostreams manifest.

**Response:**
```json
[
  {
    "id": "top",
    "name": "Top",
    "type": "movie",
    "addonName": "TMDB",
    "isSearchCapable": false,
    "itemCount": 100,
    "existingCollectionId": "abc123"
  }
]
```

---

### Get Import Progress
**Endpoint:** `GET /api/baklava/catalogs/{catalogId}/progress`  
**Auth:** Required  
**Description:** Gets real-time progress of catalog import operation.

**Response:**
```json
{
  "catalogId": "top",
  "status": "importing",
  "total": 100,
  "processed": 45,
  "message": "Importing item 45/100"
}
```

---

### Preview Catalog Update
**Endpoint:** `POST /api/baklava/catalogs/{catalogId}/preview-update`  
**Auth:** Required (Administrator)  
**Query Parameters:**
- `type` (optional, default: "movie"): "movie" or "series"

**Request Body:**
```json
{
  "maxItems": 100
}
```

**Description:** Shows what would be added/removed without making changes.

**Response:**
```json
{
  "toAdd": 15,
  "toRemove": 3,
  "unchanged": 82,
  "preview": [...]
}
```

---

### Get Catalog Item Count
**Endpoint:** `GET /api/baklava/catalogs/{catalogId}/count`  
**Auth:** Required  
**Query Parameters:**
- `type` (required): "movie" or "series"

**Description:** Returns the total number of items in a catalog.

---

### Create/Update Library from Catalog
**Endpoint:** `POST /api/baklava/catalogs/{catalogId}/library`  
**Auth:** Required (Administrator)  
**Query Parameters:**
- `type` (optional, default: "movie"): "movie" or "series"

**Request Body:**
```json
{
  "collectionName": "Top Movies",
  "maxItems": 100,
  "syncMode": "replace"
}
```

**Description:** Imports catalog items into Jellyfin library as a collection.

---

## Stream Caching

### Cache Streams
**Endpoint:** `POST /api/baklava/streams/cache`  
**Auth:** Required  
**Description:** Fetches and caches streams from Gelato for an item.

**Request Body:**
```json
{
  "itemId": "abc123",
  "stremioId": "tt1375666",
  "imdbId": "tt1375666",
  "tmdbId": "27205",
  "itemType": "movie",
  "userId": "user-guid"
}
```

**Response:**
```json
{
  "success": true,
  "message": "Streams cached successfully",
  "streamCount": 15
}
```

---

### Get Cached Streams
**Endpoint:** `GET /api/baklava/streams/{itemId}`  
**Auth:** Required  
**Query Parameters:**
- `userId` (optional): User-specific cache

**Description:** Retrieves cached streams for an item with complete data including:
- Probed streams (video/audio codec information)
- External subtitles (cached from Gelato)
- Web compatibility flag

**Response:**
```json
{
  "success": true,
  "message": "Streams retrieved successfully",
  "streams": [
    {
      "streamUrl": "http://...",
      "infoHash": "abc123",
      "title": "1080p BluRay",
      "quality": "1080p",
      "name": "Fight Club (1999) 1080p",
      "stremioId": "tt1375666",
      "webCompatible": true,
      "probedStreams": [
        {
          "streamType": "Video",
          "codec": "h264",
          "profile": "High",
          "level": 4.1
        },
        {
          "streamType": "Audio",
          "codec": "aac",
          "channels": 6,
          "language": "eng"
        }
      ],
      "externalSubtitles": [
        {
          "url": "http://jellyfin:8096/Plugins/Baklava/Subtitle/Proxy/...",
          "language": "eng",
          "title": "English"
        }
      ]
    }
  ]
}
```

---

### Smart Get Streams (with Background Update)
**Endpoint:** `GET /api/baklava/streams/{itemId}/smart`  
**Auth:** Required  
**Query Parameters:**
- `stremioId` (optional): Stremio ID
- `imdbId` (optional): IMDB ID
- `tmdbId` (optional): TMDB ID
- `itemType` (optional): "movie" or "series"
- `userId` (optional): User-specific cache

**Description:** Intelligent stream retrieval with complete extended data that:
1. Returns cached streams immediately (fast response)
2. Fetches fresh streams from Gelato in parallel
3. Compares cached vs fresh streams
4. Updates cache in background if new streams found
5. Client doesn't wait for new streams to be saved
6. Includes probed streams, subtitles, and web compatibility

**Response:**
```json
{
  "success": true,
  "message": "Returning 10 cached streams, updating 3 new in background",
  "streams": [...],
  "fromCache": true,
  "hasNewStreams": true,
  "newStreamsCount": 3
}
```

**Example:**
```bash
curl "http://localhost:8096/api/baklava/streams/abc123/smart?imdbId=tt1375666&itemType=movie" \
  -H "X-Emby-Token: YOUR_KEY"
```

**Use Case:**
Perfect for Gelato integration - returns cached data instantly while ensuring cache stays fresh.

---

### Compare Streams (Preview)
**Endpoint:** `POST /api/baklava/streams/{itemId}/compare`  
**Auth:** Required  
**Query Parameters:**
- `userId` (optional): User-specific cache

**Request Body:**
```json
{
  "itemId": "abc123",
  "stremioId": "tt1375666",
  "imdbId": "tt1375666",
  "itemType": "movie"
}
```

**Description:** Compares cached streams with fresh ones from Gelato without saving. Useful for:
- Preview what would be updated
- Check if cache needs refresh
- Debugging cache freshness

**Response:**
```json
{
  "success": true,
  "message": "Comparison complete: 10 cached, 3 new",
  "totalCached": 10,
  "totalFresh": 13,
  "totalNew": 3,
  "hasNewStreams": true,
  "newStreams": [...]
}
```

---

### Cleanup Expired Streams
**Endpoint:** `DELETE /api/baklava/streams/cleanup`  
**Auth:** Required (Administrator)  
**Description:** Removes expired stream cache entries (older than 7 days).

**Response:**
```json
{
  "success": true,
  "message": "Cleaned up 42 expired streams",
  "deletedCount": 42
}
```

---

## Probed Streams (NEW)

### Save Probed Streams
**Endpoint:** `POST /api/baklava/streams/probed`  
**Auth:** Required  
**Description:** Saves probed audio and subtitle streams from media files.

**Request Body:**
```json
{
  "itemId": "abc123",
  "stremioId": "tt1375666",
  "streamSourceId": "source-123",
  "streams": [
    {
      "streamType": "Audio",
      "streamIndex": 0,
      "codec": "aac",
      "language": "eng",
      "title": "English 5.1",
      "channels": 6,
      "sampleRate": 48000,
      "bitRate": 384000,
      "isDefault": true
    },
    {
      "streamType": "Subtitle",
      "streamIndex": 2,
      "codec": "subrip",
      "language": "eng",
      "title": "English",
      "isDefault": false,
      "isForced": false,
      "isExternal": false
    }
  ]
}
```

**Response:**
```json
{
  "success": true,
  "message": "Probed streams saved successfully",
  "streamCount": 2
}
```

---

### Get Probed Streams
**Endpoint:** `GET /api/baklava/streams/probed/{itemId}`  
**Auth:** Required  
**Query Parameters:**
- `streamSourceId` (optional): Filter by specific stream source

**Description:** Retrieves cached probed audio/subtitle information for an item.

**Response:**
```json
{
  "success": true,
  "message": "Probed streams retrieved successfully",
  "streams": [
    {
      "streamType": "Audio",
      "streamIndex": 0,
      "codec": "aac",
      "language": "eng",
      "channels": 6,
      "sampleRate": 48000,
      "isDefault": true
    }
  ]
}
```

---

### Cleanup Expired Probed Streams
**Endpoint:** `DELETE /api/baklava/streams/probed/cleanup`  
**Auth:** Required (Administrator)  
**Description:** Removes expired probed stream cache entries.

**Response:**
```json
{
  "success": true,
  "message": "Cleaned up 15 expired probed streams",
  "deletedCount": 15
}
```

---

## External Subtitles (NEW)

### Fetch External Subtitles
**Endpoint:** `POST /Plugins/Baklava/Subtitle/Fetch`  
**Auth:** Optional  
**Query Parameters:**
- `itemId` (required): Jellyfin item ID

**Description:** Fetches external subtitles from Gelato and caches them in Baklava database. Uses reflection to access Gelato's subtitle cache.

**Response:**
```json
{
  "success": true,
  "message": "Fetched 3 external subtitles",
  "subtitles": [
    {
      "id": "eng_1",
      "url": "http://jellyfin:8096/Plugins/Baklava/Subtitle/Proxy/...",
      "language": "eng",
      "title": "English",
      "cachedAt": "2026-01-02T10:00:00Z"
    }
  ]
}
```

---

### Proxy External Subtitle
**Endpoint:** `GET /Plugins/Baklava/Subtitle/Proxy/{itemId}/{subtitleId}`  
**Auth:** Optional  

**Description:** Proxies external subtitle files from original source through Baklava. This ensures:
- Subtitles remain available even if original source is slow/unreachable
- Baklava acts as a caching layer
- Proper content-type headers are set

**Example:**
```bash
curl "http://localhost:8096/Plugins/Baklava/Subtitle/Proxy/abc123/eng_1" \
  -H "X-Emby-Token: YOUR_KEY"
```

---

### List External Subtitles
**Endpoint:** `GET /Plugins/Baklava/Subtitle/List/{itemId}`  
**Auth:** Optional  

**Description:** Lists all cached external subtitles for an item.

**Response:**
```json
{
  "itemId": "abc123",
  "subtitles": [
    {
      "id": "eng_1",
      "url": "http://jellyfin:8096/Plugins/Baklava/Subtitle/Proxy/abc123/eng_1",
      "language": "eng",
      "title": "English"
    }
  ]
}
```

---

## Web Compatibility Filter (NEW)

### Analyze Stream Compatibility
**Endpoint:** `POST /Plugins/Baklava/WebCompat/AnalyzeStreams/{itemId}`  
**Auth:** Optional  

**Description:** Analyzes all streams for an item to determine web browser compatibility. Checks:
- **Video codecs**: H.264, VP8, VP9, AV1 (compatible) | HEVC, others (incompatible)
- **Audio codecs**: AAC, Opus, Vorbis, MP3 (compatible) | Dolby, TrueHD, DTS, Atmos (incompatible)

Results are cached in the database for fast subsequent lookups.

**Response:**
```json
{
  "itemId": "abc123",
  "totalStreams": 10,
  "compatibleStreams": 7,
  "results": [
    {
      "streamId": 1,
      "stremioId": "tt1375666",
      "title": "1080p BluRay",
      "quality": "1080p",
      "webCompatible": true,
      "reason": "Compatible"
    },
    {
      "streamId": 2,
      "title": "4K Dolby Atmos",
      "quality": "2160p",
      "webCompatible": false,
      "reason": "Incompatible audio codec: atmos"
    }
  ]
}
```

---

### Get Web-Compatible Streams Only
**Endpoint:** `GET /Plugins/Baklava/WebCompat/GetCompatibleStreams/{itemId}`  
**Auth:** Optional  

**Description:** Returns only streams that can be directly played in web browsers without transcoding. Automatically analyzes streams if not yet checked. Perfect for web clients to avoid showing versions that require transcoding.

**Response:**
```json
{
  "itemId": "abc123",
  "totalStreams": 10,
  "compatibleCount": 7,
  "streams": [
    {
      "streamUrl": "http://...",
      "title": "1080p BluRay",
      "quality": "1080p",
      "webCompatible": true,
      "probedStreams": [
        {
          "streamType": "Video",
          "codec": "h264"
        },
        {
          "streamType": "Audio",
          "codec": "aac"
        }
      ]
    }
  ]
}
```

**Example:**
```bash
curl "http://localhost:8096/Plugins/Baklava/WebCompat/GetCompatibleStreams/abc123" \
  -H "X-Emby-Token: YOUR_KEY"
```

**Use Case:**
- Web players can request only playable versions
- Avoids transcoding server load
- Better user experience (no incompatible versions shown)

---

## Authentication

All endpoints support Jellyfin's standard authentication methods:

1. **API Key Header:**
   ```
   X-Emby-Token: YOUR_API_KEY
   ```

2. **Query Parameter:**
   ```
   ?api_key=YOUR_API_KEY
   ```

3. **Session Cookie:**
   Standard browser session after login

---

## Rate Limiting

Currently no rate limiting is enforced at the plugin level. However:
- External API calls (TMDB, Trakt, IMDB) are cached to reduce load
- Stream data is cached for 7 days
- Background tasks are throttled to prevent overload

---

## Error Responses

All endpoints return standard HTTP status codes:

- `200 OK`: Success
- `400 Bad Request`: Invalid parameters
- `401 Unauthorized`: Authentication required
- `403 Forbidden`: Insufficient permissions
- `404 Not Found`: Resource not found
- `500 Internal Server Error`: Server error

Error response format:
```json
{
  "error": "Error message",
  "details": "Additional information"
}
```

---

## Scheduled Tasks

Baklava includes scheduled tasks accessible via Jellyfin's Dashboard:

1. **Stream Cache Cleanup** - Daily at 3 AM
   - Removes expired stream cache entries
   - Removes expired probed stream data

2. **Catalog Sync** - Manual trigger only
   - Syncs catalog metadata with database

---

## WebSocket/Real-time Updates

Currently not implemented. Consider polling endpoints like:
- `/api/baklava/catalogs/{catalogId}/progress` for import status
- `/api/baklava/requests` for new requests

---

## Changelog

### v0.8.0 (Current)
- **External Subtitles System**: Fetch, cache, and proxy external subtitles from Gelato
- **Web Compatibility Filter**: Analyze and filter streams for direct web playback
- **Extended Stream Data**: All stream endpoints now include:
  - Probed streams (video/audio codec info)
  - External subtitles
  - Web compatibility flag
- **WebCompatible column**: Added to Streams table for fast filtering
- **Subtitle Proxy**: Baklava acts as caching proxy for external subtitles

### v0.7.0
- Added probed streams endpoints
- Added stream caching system
- Database-backed persistence for all features

### v0.6.0
- Added catalog management
- Added requests system
- Added reviews support

---

## Support & Feedback

For issues or feature requests, check the plugin's repository or Jellyfin forums.
