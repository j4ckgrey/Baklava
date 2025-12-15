# Baklava 0.3.4.0 Changelog

## Bug Fixes
- **Fixed CheckLibraryStatus endpoint to return Jellyfin ID**: When checking if an item exists in the library, the endpoint now properly returns the `jellyfinId` field in the response. This fixes navigation issues where series accessed from main search would not show episodes or seasons, while the same series accessed from the library view worked correctly.

## Technical Details
- Modified `MetadataController.cs` to include `jellyfinId` in the CheckLibraryStatus response JSON
- The `jellyfinId` is now populated from the found item's ID when a match is detected in the library
- This change improves client-side navigation by providing direct item IDs instead of requiring additional search queries

## Compatibility
- Target ABI: 10.11.0.0
- Requires Jellyfin Server 10.11.0 or higher
