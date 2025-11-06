/**
 * Shared utilities for media server UI
 * Provides common DOM helpers, event utilities, and API functions
 */
(function() {
    'use strict';

    window.MediaServerUI = window.MediaServerUI || {};

    // ============================================
    // DOM HELPERS
    // ============================================

    /**
     * Safely emit an event on an element
     */
    window.MediaServerUI.emitEvent = function(element, eventName, bubbles = true) {
        try {
            const event = new Event(eventName, { bubbles });
            element.dispatchEvent(event);
        } catch (e) {
            // Fallback for older browsers
            const event = document.createEvent('HTMLEvents');
            event.initEvent(eventName, bubbles, false);
            element.dispatchEvent(event);
        }
    };

    /**
     * Safely query selector with error handling
     */
    window.MediaServerUI.qs = function(selector, context = document) {
        try {
            return context.querySelector(selector);
        } catch (e) {
            console.warn('[MediaServerUI] Invalid selector:', selector);
            return null;
        }
    };

    /**
     * Safely query all with error handling
     */
    window.MediaServerUI.qsa = function(selector, context = document) {
        try {
            return context.querySelectorAll(selector);
        } catch (e) {
            console.warn('[MediaServerUI] Invalid selector:', selector);
            return [];
        }
    };

    /**
     * Get computed background image URL from element or style
     */
    window.MediaServerUI.getBackgroundImage = function(element) {
        if (!element) return '';
        
        // Try inline style
        const inline = element.style?.backgroundImage;
        if (inline && inline !== 'none') {
            return inline.replace(/^url\(["']?/, '').replace(/["']?\)$/, '');
        }
        
        // Try data attributes
        const dataAttrs = ['data-src', 'data-image', 'src'];
        for (const attr of dataAttrs) {
            const value = element.getAttribute(attr);
            if (value) return value;
        }
        
        // Try parsing style attribute
        const styleStr = element.getAttribute('style') || '';
        const match = styleStr.match(/background-image:\s*url\(([^)]+)\)/i);
        if (match) {
            return match[1].replace(/^["']|["']$/g, '');
        }
        
        return '';
    };

    /**
     * Set background image with validation
     */
    window.MediaServerUI.setBackgroundImage = function(element, url, minHeight = '240px') {
        if (!element) return;
        
        if (url) {
            element.style.backgroundImage = `url('${url}')`;
            element.style.minHeight = minHeight;
        } else {
            element.style.background = '#222';
            element.style.minHeight = minHeight;
        }
    };

    /**
     * Debounce function for resize/scroll events
     */
    window.MediaServerUI.debounce = function(func, delay = 300) {
        let timeoutId;
        return function(...args) {
            clearTimeout(timeoutId);
            timeoutId = setTimeout(() => func(...args), delay);
        };
    };

    /**
     * Throttle function for frequent events
     */
    window.MediaServerUI.throttle = function(func, limit = 300) {
        let lastCall = 0;
        return function(...args) {
            const now = Date.now();
            if (now - lastCall >= limit) {
                lastCall = now;
                func(...args);
            }
        };
    };

    // ============================================
    // TMDB API HELPERS
    // ============================================

    const TMDB_API_KEY = '53e8a159d4635813b94f8c5876c604be';
    const TMDB_BASE_URL = 'https://api.themoviedb.org/3';

    /**
     * Genre ID to name mapping
     */
    window.MediaServerUI.TMDB_GENRES = {
        28: 'Action', 12: 'Adventure', 16: 'Animation', 35: 'Comedy', 80: 'Crime',
        99: 'Documentary', 18: 'Drama', 10751: 'Family', 14: 'Fantasy', 36: 'History',
        27: 'Horror', 10402: 'Music', 9648: 'Mystery', 10749: 'Romance', 878: 'Science Fiction',
        10770: 'TV Movie', 53: 'Thriller', 10752: 'War', 37: 'Western'
    };

    /**
     * Parse Jellyfin ID to extract provider info and type
     * Enhanced to detect TV shows from card context
     */
    window.MediaServerUI.parseJellyfinId = function(jellyfinId, cardElement) {
        const result = {
            tmdbId: null,
            imdbId: null,
            itemType: 'movie'
        };

        if (!jellyfinId) return result;

        // Check data attributes on card first - these might have explicit type info
        if (cardElement) {
            result.tmdbId = cardElement.dataset.tmdbid || cardElement.dataset.tmdb || 
                           cardElement.getAttribute('data-tmdbid') || cardElement.getAttribute('data-tmdb');
            result.imdbId = cardElement.dataset.imdbid || cardElement.dataset.imdb || 
                           cardElement.getAttribute('data-imdbid') || cardElement.getAttribute('data-imdb');
            
            // Check card's type info - Jellyfin uses data-type or lookups that indicate Series
            const cardType = cardElement.dataset.type || cardElement.getAttribute('data-type') || '';
            const cardClass = cardElement.className || '';
            if (cardType.toLowerCase().includes('series') || cardClass.includes('Series') || cardClass.includes('series')) {
                result.itemType = 'series';
            }
        }

        // Parse Jellyfin ID for type markers
        if (jellyfinId.includes('gelato') || jellyfinId.includes('series') || jellyfinId.includes('tvdb')) {
            result.itemType = 'series';
        }

        // Extract TMDB ID from ID string
        if (!result.tmdbId) {
            const tmdbMatch = jellyfinId.match(/tmdb[_-](\d+)/i);
            if (tmdbMatch) result.tmdbId = tmdbMatch[1];
        }

        // Extract IMDB ID
        if (!result.imdbId && /^tt\d+$/.test(jellyfinId)) {
            result.imdbId = jellyfinId;
        }

        // Treat as TMDB ID if just numbers
        if (!result.tmdbId && /^\d+$/.test(jellyfinId)) {
            result.tmdbId = jellyfinId;
        }

        // Debug logging for TV shows that fail
        if (result.itemType === 'series' && !result.tmdbId && !result.imdbId) {
            console.log('[parseJellyfinId] Series without IDs - will search by title:', jellyfinId);
        }

        return result;
    };

    /**
     * Fetch from TMDB with error handling
     */
    window.MediaServerUI.fetchTMDB = async function(endpoint, params = {}) {
        try {
            const url = new URL(`${TMDB_BASE_URL}${endpoint}`);
            url.searchParams.append('api_key', TMDB_API_KEY);
            
            for (const [key, value] of Object.entries(params)) {
                if (value) url.searchParams.append(key, value);
            }

            const res = await fetch(url.toString());
            if (!res.ok) throw new Error(`TMDB API error: ${res.status}`);
            
            return await res.json();
        } catch (err) {
            console.error('[MediaServerUI.fetchTMDB]', err);
            return null;
        }
    };

    /**
     * Get TMDB data by various identifiers
     */
    window.MediaServerUI.getTMDBData = async function(tmdbId, imdbId, itemType, title, year) {
        const mediaType = itemType === 'series' ? 'tv' : 'movie';
        let data = null;

        // Try TMDB ID first
        if (tmdbId) {
            data = await window.MediaServerUI.fetchTMDB(`/${mediaType}/${tmdbId}`);
            if (data) return data;
        }

        // Try IMDB ID
        if (imdbId) {
            const result = await window.MediaServerUI.fetchTMDB('/find/' + imdbId, { external_source: 'imdb_id' });
            if (result) {
                if (itemType === 'series' && result.tv_results?.length) {
                    return result.tv_results[0];
                }
                if (result.movie_results?.length) {
                    return result.movie_results[0];
                }
            }
        }

        // Fallback: Search by title - try the specified type first, then try the other type if not found
        if (title) {
            const params = { query: title };
            if (year) {
                params[itemType === 'series' ? 'first_air_date_year' : 'year'] = year;
            }
            
            const result = await window.MediaServerUI.fetchTMDB(`/search/${mediaType}`, params);
            if (result?.results?.length) {
                return result.results[0];
            }

            // If searching as series didn't work, try as movie (and vice versa)
            if (itemType === 'series') {
                console.log('[getTMDBData] Series search failed for ' + title + ', trying movie search');
                const movieResult = await window.MediaServerUI.fetchTMDB('/search/movie', { query: title, year: year });
                if (movieResult?.results?.length) {
                    return movieResult.results[0];
                }
            } else {
                console.log('[getTMDBData] Movie search failed for ' + title + ', trying series search');
                const tvParams = { query: title };
                if (year) tvParams.first_air_date_year = year;
                const tvResult = await window.MediaServerUI.fetchTMDB('/search/tv', tvParams);
                if (tvResult?.results?.length) {
                    return tvResult.results[0];
                }
            }
        }

        return null;
    };

    /**
     * Fetch TMDB credits and reviews
     */
    window.MediaServerUI.fetchTMDBCreditsAndReviews = async function(mediaType, movieId) {
        if (!movieId) return { credits: null, reviews: [] };

        try {
            const [creditsRes, reviewsRes] = await Promise.all([
                window.MediaServerUI.fetchTMDB(`/${mediaType}/${movieId}/credits`),
                window.MediaServerUI.fetchTMDB(`/${mediaType}/${movieId}/reviews`)
            ]);

            let reviews = reviewsRes?.results || [];

            // Fetch additional pages if needed
            if (reviews.length < 20 && reviewsRes?.total_pages > 1) {
                const additionalPages = Math.min(reviewsRes.total_pages - 1, 1);
                for (let page = 2; page <= additionalPages + 1; page++) {
                    const pageData = await window.MediaServerUI.fetchTMDB(
                        `/${mediaType}/${movieId}/reviews`,
                        { page }
                    );
                    if (pageData?.results) {
                        reviews = reviews.concat(pageData.results);
                    }
                }
            }

            return {
                credits: creditsRes,
                reviews: reviews.slice(0, 20)
            };
        } catch (err) {
            console.error('[MediaServerUI.fetchTMDBCreditsAndReviews]', err);
            return { credits: null, reviews: [] };
        }
    };

    // ============================================
    // JELLYFIN API HELPERS
    // ============================================

    /**
     * Get Jellyfin API client from window
     */
    window.MediaServerUI.getApiClient = function() {
        return window.ApiClient || null;
    };

    /**
     * Intercept Jellyfin's PlaybackManager to capture MediaStreams when fetched
     * This hooks into the actual playback flow where track data is available
     */
    window.MediaServerUI.interceptPlaybackMediaStreams = function(callback) {
        if (window.MediaServerUI._streamsIntercepted) return;
        window.MediaServerUI._streamsIntercepted = true;

        try {
            const pm = window.PlaybackManager || window.MediaPlayer;
            if (!pm) return;

            // Store original getPlaybackMediaSource
            const origGetPlaybackMediaSource = pm.getPlaybackMediaSource;
            
            if (typeof origGetPlaybackMediaSource === 'function') {
                pm.getPlaybackMediaSource = function(item, user, mediaSourceId) {
                    const result = origGetPlaybackMediaSource.call(this, item, user, mediaSourceId);
                    
                    // If result is a promise, chain the callback
                    if (result && typeof result.then === 'function') {
                        result.then(mediaSource => {
                            if (mediaSource?.MediaStreams) {
                                callback(mediaSource, item);
                            }
                        }).catch(() => {});
                    }
                    
                    return result;
                };
            }
        } catch (err) {
            console.warn('[interceptPlaybackMediaStreams]', err);
        }
    };

    /**
     * Parse media source streams into track options
     */
    window.MediaServerUI.parseMediaSourceStreams = function(mediaSource) {
        const result = {
            audioTracks: [],
            subtitleTracks: []
        };

        if (!mediaSource?.MediaStreams) return result;

        mediaSource.MediaStreams.forEach((stream, idx) => {
            if (stream.Type === 'Audio') {
                const lang = stream.Language || stream.DisplayLanguage || 'Unknown';
                const codec = stream.Codec ? ` (${stream.Codec.toUpperCase()})` : '';
                const title = `${lang}${codec}`;
                result.audioTracks.push({
                    index: stream.Index || idx,
                    title: title,
                    language: stream.Language,
                    codec: stream.Codec,
                    channels: stream.Channels
                });
            } else if (stream.Type === 'Subtitle') {
                const lang = stream.Language || stream.DisplayLanguage || 'Unknown';
                const codec = stream.Codec ? ` (${stream.Codec})` : '';
                const title = `${lang}${codec}`;
                result.subtitleTracks.push({
                    index: stream.Index || idx,
                    title: title,
                    language: stream.Language,
                    codec: stream.Codec,
                    isExternal: stream.IsExternal || false
                });
            }
        });

        return result;
    };

    /**
     * Format genres for display
     */
    window.MediaServerUI.formatGenres = function(genres, genreIds) {
        if (genres?.length > 0) {
            return genres.map(g => g.name || g).join(', ');
        }
        if (genreIds?.length > 0) {
            return genreIds
                .map(id => window.MediaServerUI.TMDB_GENRES[id] || 'Unknown')
                .filter(g => g !== 'Unknown')
                .join(', ');
        }
        return '';
    };

    /**
     * Format runtime
     */
    window.MediaServerUI.formatRuntime = function(minutes) {
        if (!minutes) return '';
        const hours = Math.floor(minutes / 60);
        const mins = minutes % 60;
        return `${hours}h ${mins}m`;
    };

    /**
     * Format rating
     */
    window.MediaServerUI.formatRating = function(rating) {
        return rating ? `${Math.round(rating * 10) / 10}/10` : 'N/A';
    };

    // ============================================
    // STREAM/TRACK PROBING
    // ============================================

    /**
     * Probe MediaStreams using PlaybackInfo API (like Jellyfin does)
     * This is the ONLY way to get the actual audio/subtitle tracks
     */
    window.MediaServerUI.probeItemStreams = async function(itemId, mediaSourceId) {
        if (!itemId) {
            console.warn('[probeItemStreams] No itemId');
            return null;
        }

        try {
            if (!window.ApiClient) {
                console.warn('[probeItemStreams] No ApiClient');
                return null;
            }
            
            // Call PlaybackInfo with IsPlayback: false (probe mode, not actual playback)
            const userId = window.ApiClient.getCurrentUserId();
            const deviceId = window.ApiClient.deviceId();
            
            // Get a basic device profile
            const deviceProfile = {
                Name: 'Web',
                MaxStreamingBitrate: 140000000,
                MusicStreamingTranscodingBitrate: 128000,
                TimelineOffsetSeconds: 0,
                TranscodingProfiles: [],
                DirectPlayProfiles: [],
                CodecProfiles: [],
                SubtitleProfiles: [],
                ContainerProfiles: []
            };

            // Call the PlaybackInfo endpoint
            const response = await window.ApiClient.ajax({
                url: window.ApiClient.getUrl('Items/' + itemId + '/PlaybackInfo', {
                    userId: userId,
                    IsPlayback: false,  // KEY: Not actual playback, just probing
                    AutoOpenLiveStream: false
                }),
                type: 'POST',
                data: JSON.stringify({
                    DeviceProfile: deviceProfile,
                    MediaSourceId: mediaSourceId
                }),
                contentType: 'application/json',
                dataType: 'json'
            });

            if (response?.MediaSources?.length > 0) {
                return response.MediaSources[0];
            }

            return null;
        } catch (err) {
            console.error('[probeItemStreams] Error:', err);
            return null;
        }
    };

    /**
     * Extract audio/subtitle tracks from MediaSource
     */
    window.MediaServerUI.extractStreams = function(mediaSource) {
        const result = { audio: [], subs: [] };

        if (!mediaSource?.MediaStreams?.length) {
            return result;
        }

        mediaSource.MediaStreams.forEach((stream) => {
            if (stream.Type === 'Audio') {
                const lang = stream.Language ? ` (${stream.Language})` : '';
                const codec = stream.Codec ? ` [${stream.Codec}]` : '';
                result.audio.push({
                    index: stream.Index,
                    title: (stream.DisplayTitle || stream.Title || `Audio ${stream.Index}`) + lang + codec,
                    language: stream.Language,
                    codec: stream.Codec
                });
            } else if (stream.Type === 'Subtitle') {
                const lang = stream.Language ? ` (${stream.Language})` : '';
                const codec = stream.Codec ? ` [${stream.Codec}]` : '';
                result.subs.push({
                    index: stream.Index,
                    title: (stream.DisplayTitle || stream.Title || `Subtitle ${stream.Index}`) + lang + codec,
                    language: stream.Language,
                    codec: stream.Codec
                });
            }
        });

        return result;
    };
    
    console.log('[MediaServerUI] Shared utilities loaded');

})();
