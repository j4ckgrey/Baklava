(function() {
    'use strict';
    
    window.LibraryStatus = {
        async check(imdbId, tmdbId, itemType) {
            if (!imdbId && !tmdbId) return false;
            
            const opts = {
                IncludeItemTypes: itemType === 'series' ? 'Series' : 'Movie',
                Recursive: true,
                Fields: 'ProviderIds,Type',
                Limit: 5000
            };
            
            const result = await window.ApiClient.getItems(window.ApiClient.getCurrentUserId(), opts);
            if (!result?.Items?.length) return false;
            
            return result.Items.some(item => {
                const pids = item.ProviderIds || {};
                return (imdbId && pids.Imdb === imdbId) || (tmdbId && pids.Tmdb === tmdbId);
            });
        },
        
        async checkRequest(imdbId, tmdbId, itemType) {
            try {
                let response = await window.ApiClient.ajax({
                    type: 'GET',
                    url: window.ApiClient.getUrl('api/myplugin/requests'),
                    dataType: 'json'
                });
                
                // Parse Response object if needed
                if (response && response.constructor && response.constructor.name === 'Response') {
                    response = await response.json();
                }
                
                // Ensure response is an array
                let requests = [];
                if (Array.isArray(response)) {
                    requests = response;
                } else if (response && typeof response === 'object') {
                    requests = response.requests || response.data || [];
                }
                
                // Find matching request
                const match = requests.find(r => {
                    if (r.itemType !== itemType) return false;
                    return (imdbId && r.imdbId === imdbId) || (tmdbId && r.tmdbId === tmdbId);
                });
                
                return match || null;
            } catch (err) {
                console.error('[LibraryStatus] Error checking request:', err);
                return null;
            }
        }
    };
})();

