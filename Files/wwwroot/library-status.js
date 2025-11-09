(function() {
    'use strict';
    
    window.LibraryStatus = {
        /**
         * Check if item is in library (now using optimized backend endpoint)
         */
        async check(imdbId, tmdbId, itemType, jellyfinId) {
            if (!imdbId && !tmdbId && !jellyfinId) return false;
            
            try {
                const params = new URLSearchParams();
                if (imdbId) params.append('imdbId', imdbId);
                if (tmdbId) params.append('tmdbId', tmdbId);
                if (jellyfinId) params.append('jellyfinId', jellyfinId);
                if (itemType) params.append('itemType', itemType);

                const url = window.ApiClient.getUrl('api/baklava/metadata/library-status') + '?' + params.toString();
                const response = await window.ApiClient.ajax({
                    type: 'GET',
                    url: url,
                    dataType: 'json'
                });

                return response?.inLibrary || false;
            } catch (err) {
                console.error('[LibraryStatus.check] Error:', err);
                return false;
            }
        },
        
        /**
         * Check if item is already requested (now using optimized backend endpoint)
         */
        async checkRequest(imdbId, tmdbId, itemType, jellyfinId) {
            if (!imdbId && !tmdbId && !jellyfinId) return null;
            
            try {
                const params = new URLSearchParams();
                if (imdbId) params.append('imdbId', imdbId);
                if (tmdbId) params.append('tmdbId', tmdbId);
                if (jellyfinId) params.append('jellyfinId', jellyfinId);
                if (itemType) params.append('itemType', itemType);

                const url = window.ApiClient.getUrl('api/baklava/metadata/library-status') + '?' + params.toString();
                const response = await window.ApiClient.ajax({
                    type: 'GET',
                    url: url,
                    dataType: 'json'
                });

                return response?.existingRequest || null;
            } catch (err) {
                console.error('[LibraryStatus.checkRequest] Error:', err);
                return null;
            }
        }
    };
})();

