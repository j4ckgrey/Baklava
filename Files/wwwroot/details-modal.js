/**
 * Details Modal - Standalone
 * Shows TMDB metadata modal when clicking on search results
 * All utilities inlined - no dependencies on shared-utils.js
 */
(function() {
    'use strict';
    
    console.log('[DetailsModal] Loading standalone version...');

    // ============================================
    // UTILITY FUNCTIONS (Inlined)
    // ============================================

    const TMDB_GENRES = {
        28: 'Action', 12: 'Adventure', 16: 'Animation', 35: 'Comedy', 80: 'Crime',
        99: 'Documentary', 18: 'Drama', 10751: 'Family', 14: 'Fantasy', 36: 'History',
        27: 'Horror', 10402: 'Music', 9648: 'Mystery', 10749: 'Romance', 878: 'Science Fiction',
        10770: 'TV Movie', 53: 'Thriller', 10752: 'War', 37: 'Western'
    };

    function qs(selector, context = document) {
        try {
            return context.querySelector(selector);
        } catch (e) {
            console.warn('[DetailsModal] Invalid selector:', selector);
            return null;
        }
    }

    function qsa(selector, context = document) {
        try {
            return context.querySelectorAll(selector);
        } catch (e) {
            console.warn('[DetailsModal] Invalid selector:', selector);
            return [];
        }
    }

    function getBackgroundImage(element) {
        if (!element) return '';
        
        const inline = element.style?.backgroundImage;
        if (inline && inline !== 'none') {
            return inline.replace(/^url\(["']?/, '').replace(/["']?\)$/, '');
        }
        
        const dataAttrs = ['data-src', 'data-image', 'src'];
        for (const attr of dataAttrs) {
            const value = element.getAttribute(attr);
            if (value) return value;
        }
        
        const styleStr = element.getAttribute('style') || '';
        const match = styleStr.match(/background-image:\s*url\(([^)]+)\)/i);
        if (match) {
            return match[1].replace(/^["']|["']$/g, '');
        }
        
        return '';
    }

    function setBackgroundImage(element, url, minHeight = '240px') {
        if (!element) return;
        
        if (url) {
            element.style.backgroundImage = `url('${url}')`;
            element.style.minHeight = minHeight;
        } else {
            element.style.background = '#222';
            element.style.minHeight = minHeight;
        }
    }

    function parseJellyfinId(jellyfinId, cardElement) {
        const result = {
            tmdbId: null,
            imdbId: null,
            itemType: 'movie'
        };

        if (!jellyfinId) return result;

        if (cardElement) {
            result.tmdbId = cardElement.dataset.tmdbid || cardElement.dataset.tmdb || 
                           cardElement.getAttribute('data-tmdbid') || cardElement.getAttribute('data-tmdb');
            result.imdbId = cardElement.dataset.imdbid || cardElement.dataset.imdb || 
                           cardElement.getAttribute('data-imdbid') || cardElement.getAttribute('data-imdb');
            
            const cardType = cardElement.dataset.type || cardElement.getAttribute('data-type') || '';
            const cardClass = cardElement.className || '';
            if (cardType.toLowerCase().includes('series') || cardClass.includes('Series') || cardClass.includes('series')) {
                result.itemType = 'series';
            }
        }

        if (jellyfinId.includes('gelato') || jellyfinId.includes('series') || jellyfinId.includes('tvdb')) {
            result.itemType = 'series';
        }

        if (!result.tmdbId) {
            const tmdbMatch = jellyfinId.match(/tmdb[_-](\d+)/i);
            if (tmdbMatch) result.tmdbId = tmdbMatch[1];
        }

        if (!result.imdbId && /^tt\d+$/.test(jellyfinId)) {
            result.imdbId = jellyfinId;
        }

        if (!result.tmdbId && /^\d+$/.test(jellyfinId)) {
            result.tmdbId = jellyfinId;
        }

        return result;
    }

    async function getTMDBData(tmdbId, imdbId, itemType, title, year) {
        try {
            const params = new URLSearchParams();
            if (tmdbId) params.append('tmdbId', tmdbId);
            if (imdbId) params.append('imdbId', imdbId);
            if (itemType) params.append('itemType', itemType);
            if (title) params.append('title', title);
            if (year) params.append('year', year);
            params.append('includeCredits', 'false');
            params.append('includeReviews', 'false');

            const url = window.ApiClient.getUrl('api/myplugin/metadata/tmdb') + '?' + params.toString();
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            return response?.main || response;
        } catch (e) {
            console.error('[DetailsModal.getTMDBData] Error:', e);
            return null;
        }
    }

    async function fetchTMDBCreditsAndReviews(mediaType, movieId) {
        if (!movieId) return { credits: null, reviews: [] };

        try {
            const params = new URLSearchParams();
            params.append('tmdbId', movieId);
            params.append('itemType', mediaType === 'tv' ? 'series' : 'movie');
            params.append('includeCredits', 'true');
            params.append('includeReviews', 'true');

            const url = window.ApiClient.getUrl('api/myplugin/metadata/tmdb') + '?' + params.toString();
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            return {
                credits: response?.credits || null,
                reviews: response?.reviews?.results?.slice(0, 20) || []
            };
        } catch (err) {
            console.error('[DetailsModal.fetchTMDBCreditsAndReviews] Error:', err);
            return { credits: null, reviews: [] };
        }
    }

    // Populate audio/subtitle streams for the item (uses plugin API)
    async function populateStreams(modal) {
        try {
            const itemId = modal.dataset.itemId;
            if (!itemId) return;

            const url = window.ApiClient.getUrl('api/myplugin/metadata/streams') + '?itemId=' + encodeURIComponent(itemId);
            const resp = await window.ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' });
            if (!resp) return;

            const infoEl = qs('#item-detail-info', modal);
            if (!infoEl) return;

            // Create container
            const streamsContainer = document.createElement('div');
            streamsContainer.className = 'details-streams';
            streamsContainer.style.cssText = 'margin-top:12px;color:#ccc;';

            if (Array.isArray(resp.audio) && resp.audio.length > 0) {
                const audioDiv = document.createElement('div');
                audioDiv.innerHTML = '<h3 style="color:#fff;margin:6px 0">Audio Tracks</h3>';
                const ul = document.createElement('ul');
                ul.style.cssText = 'padding-left:18px;margin:6px 0;color:#ccc;';
                resp.audio.forEach(a => {
                    const li = document.createElement('li');
                    li.textContent = (a.title || `Audio ${a.index}`) + (a.language ? ` (${a.language})` : '') + (a.codec ? ` [${a.codec}]` : '');
                    ul.appendChild(li);
                });
                audioDiv.appendChild(ul);
                streamsContainer.appendChild(audioDiv);
            }

            if (Array.isArray(resp.subs) && resp.subs.length > 0) {
                const subsDiv = document.createElement('div');
                subsDiv.innerHTML = '<h3 style="color:#fff;margin:6px 0">Subtitles</h3>';
                const ul2 = document.createElement('ul');
                ul2.style.cssText = 'padding-left:18px;margin:6px 0;color:#ccc;';
                resp.subs.forEach(s => {
                    const li = document.createElement('li');
                    li.textContent = (s.title || `Subtitle ${s.index}`) + (s.language ? ` (${s.language})` : '') + (s.codec ? ` [${s.codec}]` : '') + (s.isDefault ? ' (default)' : '') + (s.isForced ? ' (forced)' : '');
                    ul2.appendChild(li);
                });
                subsDiv.appendChild(ul2);
                streamsContainer.appendChild(subsDiv);
            }

            if (streamsContainer.children.length > 0) {
                infoEl.appendChild(streamsContainer);
            }
        } catch (e) {
            console.error('[DetailsModal.populateStreams] Error:', e);
        }
    }

    function formatGenres(genres, genreIds) {
        if (genres?.length > 0) {
            return genres.map(g => g.name || g).join(', ');
        }
        if (genreIds?.length > 0) {
            return genreIds
                .map(id => TMDB_GENRES[id] || 'Unknown')
                .filter(g => g !== 'Unknown')
                .join(', ');
        }
        return '';
    }

    function formatRuntime(minutes) {
        if (!minutes) return '';
        const hours = Math.floor(minutes / 60);
        const mins = minutes % 60;
        return `${hours}h ${mins}m`;
    }

    function formatRating(rating) {
        return rating ? `${Math.round(rating * 10) / 10}/10` : 'N/A';
    }

    function escapeHtml(s) {
        return String(s || '').replace(/[&<>"']/g, function(c) {
            return ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c];
        });
    }

    // ============================================
    // MODAL FUNCTIONS
    // ============================================
    
    function createModal() {
        const overlay = document.createElement('div');
        overlay.className = 'item-detail-modal-overlay';
        overlay.id = 'item-detail-modal-overlay';
        overlay.innerHTML = '<div class="item-detail-modal" role="dialog" aria-modal="true" aria-labelledby="item-detail-title" style="position:relative;">'
            + '<div id="item-detail-loading-overlay" style="position:absolute;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.95);display:flex;align-items:center;justify-content:center;z-index:9999;border-radius:8px;">'
                + '<div style="text-align:center;">'
                    + '<div style="width:40px;height:40px;border:3px solid #555;border-top:3px solid #1e90ff;border-radius:50%;animation:spin 1s linear infinite;margin:0 auto 10px;"></div>'
                    + '<div style="color:#aaa;font-size:14px;">Loading…</div>'
                + '</div>'
            + '</div>'
            + '<div class="left" id="item-detail-image" aria-hidden="true" style="position:relative;">'
                + '<div id="item-detail-requester" style="display:none;position:absolute;top:20px;left:50%;transform:translateX(-50%);background:rgba(30,144,255,0.95);color:#fff;padding:6px 12px;border-radius:4px;font-size:12px;font-weight:600;white-space:nowrap;"></div>'
            + '</div>'
            + '<div class="right" style="min-width:0;max-height:calc(100vh - 80px);">'
                + '<div style="display:flex;justify-content:space-between;align-items:center;padding-bottom:15px;border-bottom:2px solid #333;margin-bottom:15px;">'
                    + '<h2 id="item-detail-title" style="margin:0;">Loading…</h2>'
                    + '<div style="display:flex;gap:10px;">'
                        + '<button id="item-detail-approve" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#4caf50;color:#fff;cursor:pointer;display:none;font-size:13px;">Approve</button>'
                        + '<button id="item-detail-import" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#1e90ff;color:#fff;cursor:pointer;display:none;font-size:13px;">Import</button>'
                        + '<button id="item-detail-request" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#ff9800;color:#fff;cursor:pointer;display:none;font-size:13px;">Request</button>'
                        + '<button id="item-detail-remove" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#f44336;color:#fff;cursor:pointer;display:none;font-size:13px;">Remove</button>'
                        + '<button id="item-detail-open" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#4caf50;color:#fff;cursor:pointer;display:none;font-size:13px;">Open</button>'
                        + '<button id="item-detail-close" style="width:32px;height:32px;padding:0;border:none;border-radius:4px;background:#555;color:#fff;cursor:pointer;font-size:18px;line-height:1;">✕</button>'
                    + '</div>'
                + '</div>'
                + '<div class="modal-body" style="overflow:auto;min-width:0;max-height:calc(100vh - 160px);">'
                    + '<div id="item-detail-meta"></div>'
                    + '<div id="item-detail-overview" style="margin-top:12px;line-height:1.6;"></div>'
                    + '<div id="item-detail-info" style="margin-top:20px;"></div>'
                + '</div>'
                + '<div id="item-detail-reviews" style="margin-top:30px;"></div>'
            + '</div>'
            + '<style>@keyframes spin { to { transform: rotate(360deg); } }</style>'
            + '<div id="review-popup" style="display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.9);z-index:10000;">'
                + '<div style="max-width:800px;max-height:80vh;margin:10vh auto;background:#1a1a1a;border-radius:8px;display:flex;flex-direction:column;">'
                    + '<div style="padding:20px 30px;border-bottom:2px solid #333;display:flex;justify-content:space-between;">'
                        + '<h3 style="margin:0;color:#fff;">Review</h3>'
                        + '<button id="close-review-popup" style="background:#555;border:none;color:#fff;padding:8px 16px;cursor:pointer;">Close</button>'
                    + '</div>'
                    + '<div id="review-popup-content" style="flex:1;overflow-y:auto;padding:30px;color:#ccc;"></div>'
                + '</div>'
            + '</div>'
            + '</div>';
        document.body.appendChild(overlay);
        attachEventListeners(overlay);
        return overlay;
    }

    function attachEventListeners(overlay) {
        const closeBtn = qs('#item-detail-close', overlay);
        const importBtn = qs('#item-detail-import', overlay);
        const requestBtn = qs('#item-detail-request', overlay);
        const approveBtn = qs('#item-detail-approve', overlay);
        const removeBtn = qs('#item-detail-remove', overlay);
        const openBtn = qs('#item-detail-open', overlay);
        const reviewPopup = qs('#review-popup', overlay);
        const closeReviewBtn = qs('#close-review-popup', overlay);

        overlay.addEventListener('click', ev => ev.target === overlay && hideModal());
        closeBtn.addEventListener('click', hideModal);
        
        importBtn.addEventListener('click', async () => {
            const title = qs('#item-detail-title', overlay).textContent;
            hideModal();
            if (title) {
                window.location.hash = '#/search.html?query=' + encodeURIComponent(title);
            }
        });
        
        requestBtn.addEventListener('click', () => {
            console.log('[DetailsModal] Request button clicked');
            requestBtn.disabled = true;
            requestBtn.textContent = 'Pending';
            requestBtn.style.background = '#888';
            const item = {
                title: qs('#item-detail-title', overlay).textContent,
                year: qs('#item-detail-meta', overlay).textContent,
                img: qs('#item-detail-image', overlay).style.backgroundImage,
                imdbId: overlay.dataset.imdbId,
                tmdbId: overlay.dataset.tmdbId,
                itemType: overlay.dataset.itemType,
                jellyfinId: overlay.dataset.itemId,
                status: 'pending'
            };
            console.log('[DetailsModal] Dispatching mediaRequest event:', item);
            document.dispatchEvent(new CustomEvent('mediaRequest', { detail: item }));
        });
        
        openBtn.addEventListener('click', () => {
            const id = overlay.dataset.itemId;
            if (id) { hideModal(); window.location.hash = '#/details?id=' + encodeURIComponent(id); }
        });

        approveBtn.addEventListener('click', async () => {
            console.log('[DetailsModal] Approve button clicked');
            const requestId = overlay.dataset.requestId;
            const tmdbId = overlay.dataset.tmdbId;
            const imdbId = overlay.dataset.imdbId;
            const itemType = overlay.dataset.itemType;
            
            if (requestId && window.RequestManager) {
                await window.RequestManager.updateStatus(requestId, 'approved');
                if (window.RequestsHeaderButton) await window.RequestsHeaderButton.reload();
                
                if (imdbId) {
                    const gelatoType = itemType === 'series' ? 'series' : 'movie';
                    // Use ApiClient.getUrl so requests go through Jellyfin and include auth
                    const gelatoUrl = window.ApiClient.getUrl(`gelato/meta/${gelatoType}/${imdbId}`);
                    
                    approveBtn.textContent = 'Fetching...';
                    approveBtn.style.background = '#2196f3';
                    
                    try {
                        await window.ApiClient.ajax({ type: 'GET', url: gelatoUrl });
                        approveBtn.textContent = 'Refreshing Library...';
                        
                        try {
                            await window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('Library/Refresh') });
                            await new Promise(resolve => setTimeout(resolve, 2000));
                            if (window.LibraryMenu?.refresh) await window.LibraryMenu.refresh();
                            window.Emby?.Page?.triggerPageReload?.();
                            approveBtn.textContent = 'Added ✓';
                            approveBtn.style.background = '#4caf50';
                        } catch (refreshErr) {
                            approveBtn.textContent = 'Fetched ✓';
                            approveBtn.style.background = '#4caf50';
                        }
                        
                        setTimeout(() => hideModal(), 1500);
                    } catch (err) {
                        console.error('[DetailsModal] Error:', err);
                        approveBtn.textContent = 'Error';
                        approveBtn.style.background = '#f44336';
                        setTimeout(() => hideModal(), 2000);
                    }
                } else {
                    approveBtn.textContent = 'Approved';
                    approveBtn.style.background = '#4caf50';
                    setTimeout(() => hideModal(), 1000);
                }
            }
        });

        removeBtn.addEventListener('click', async () => {
            const requestId = overlay.dataset.requestId;
            if (requestId && window.RequestManager) {
                await window.RequestManager.deleteRequest(requestId);
                if (window.RequestsHeaderButton) await window.RequestsHeaderButton.reload();
                hideModal();
            }
        });

        // Hover effects
        importBtn.addEventListener('mouseenter', () => importBtn.style.background = '#1c7ed6');
        importBtn.addEventListener('mouseleave', () => importBtn.style.background = '#1e90ff');
        requestBtn.addEventListener('mouseenter', () => requestBtn.style.background = '#f57c00');
        requestBtn.addEventListener('mouseleave', () => requestBtn.style.background = '#ff9800');
        approveBtn.addEventListener('mouseenter', () => approveBtn.style.background = '#45a049');
        approveBtn.addEventListener('mouseleave', () => approveBtn.style.background = '#4caf50');
        removeBtn.addEventListener('mouseenter', () => removeBtn.style.background = '#d32f2f');
        removeBtn.addEventListener('mouseleave', () => removeBtn.style.background = '#f44336');
        openBtn.addEventListener('mouseenter', () => openBtn.style.background = '#45a049');
        openBtn.addEventListener('mouseleave', () => openBtn.style.background = '#4caf50');
        closeBtn.addEventListener('mouseenter', () => closeBtn.style.background = '#666');
        closeBtn.addEventListener('mouseleave', () => closeBtn.style.background = '#555');

        closeReviewBtn.addEventListener('click', () => reviewPopup.style.display = 'none');
        reviewPopup.addEventListener('click', e => e.target === reviewPopup && (reviewPopup.style.display = 'none'));
        document.addEventListener('keydown', ev => ev.key === 'Escape' && hideModal());
    }
    
    async function switchButton(importBtn, requestBtn, openBtn, inLibrary) {
        const userId = window.ApiClient.getCurrentUserId();
        const user = await window.ApiClient.getUser(userId);
        const isAdmin = user?.Policy?.IsAdministrator;
        
        if (inLibrary) {
            importBtn.style.display = 'none';
            requestBtn.style.display = 'none';
            openBtn.style.display = 'block';
        } else {
            openBtn.style.display = 'none';
            if (isAdmin) {
                importBtn.style.display = 'block';
                requestBtn.style.display = 'none';
            } else {
                importBtn.style.display = 'none';
                requestBtn.style.display = 'block';
            }
        }
    }

    function getModal() { return qs('#item-detail-modal-overlay') || createModal(); }
    function showModal(modal) { modal.classList.add('open'); document.body.style.overflow = 'hidden'; }
    function hideLoading(modal) { 
        const overlay = qs('#item-detail-loading-overlay', modal);
        if (overlay) overlay.style.display = 'none';
    }
    function hideModal() { 
        const m = qs('#item-detail-modal-overlay'); 
        if (m) { 
            m.classList.remove('open'); 
            document.body.style.overflow = '';
            qs('#item-detail-title', m).textContent = 'Loading…';
            qs('#item-detail-meta', m).textContent = '';
            qs('#item-detail-overview', m).textContent = '';
            qs('#item-detail-info', m).innerHTML = '';
            qs('#item-detail-reviews', m).innerHTML = '';
            qs('#item-detail-image', m).style.backgroundImage = '';
            qs('#item-detail-import', m).style.display = 'none';
            qs('#item-detail-request', m).style.display = 'none';
            qs('#item-detail-approve', m).style.display = 'none';
            qs('#item-detail-remove', m).style.display = 'none';
            qs('#item-detail-open', m).style.display = 'none';
            const loadingOverlay = qs('#item-detail-loading-overlay', m);
            if (loadingOverlay) loadingOverlay.style.display = 'flex';
        } 
    }

    function populateFromCard(anchor, id, modal) {
        const card = anchor.closest('.card') || anchor.closest('[data-id]');
        const title = anchor.getAttribute('title') || anchor.textContent.trim() || qs('.cardText-first a', card)?.textContent || 'Untitled';
        const year = qs('.cardText-secondary bdi', card)?.textContent || '';
        const imgContainer = qs('.cardImageContainer', card);
        const bgImage = getBackgroundImage(imgContainer);
        const isSeriesCard = card?.className?.includes('Series') || card?.parentElement?.className?.includes('series');

        qs('#item-detail-title', modal).textContent = title;
        qs('#item-detail-meta', modal).textContent = year || '';
        qs('#item-detail-overview', modal).textContent = '';
        setBackgroundImage(qs('#item-detail-image', modal), bgImage);
        modal.dataset.itemId = id;

        fetchMetadata(id, card, modal, title, year, isSeriesCard).catch(() => {
            const loading = qs('#item-detail-loading', modal);
            if (loading) loading.style.display = 'none';
            qs('#item-detail-overview', modal).textContent = 'Could not fetch details.';
        });
    }

    async function fetchMetadata(jellyfinId, card, modal, title, year, forceSeries) {
        try {
            let { tmdbId, imdbId, itemType } = parseJellyfinId(jellyfinId, card);
            if (forceSeries && itemType === 'movie') {
                itemType = 'series';
            }
            const tmdbData = await getTMDBData(tmdbId, imdbId, itemType, title, year);

            if (!tmdbData) {
                qs('#item-detail-info', modal).innerHTML = 'Could not find metadata.';
                return;
            }

            const displayTitle = tmdbData.title || tmdbData.name;
            if (displayTitle) qs('#item-detail-title', modal).textContent = displayTitle;
            if (tmdbData.overview) qs('#item-detail-overview', modal).textContent = tmdbData.overview;
            if (tmdbData.poster_path) setBackgroundImage(qs('#item-detail-image', modal), 'https://image.tmdb.org/t/p/w500' + tmdbData.poster_path);

            // Detect type from response (name=TV, title=movie)
            const actualType = (tmdbData.name && !tmdbData.title) ? 'series' : 
                              (tmdbData.title && !tmdbData.name) ? 'movie' :
                              (tmdbData.number_of_seasons) ? 'series' : 'movie';
            
            const tmdbIdFromResponse = tmdbData.id;
            let imdbIdFromResponse = imdbId || tmdbData.imdb_id;
            
            // Fetch external IDs if needed
            if (!imdbIdFromResponse && tmdbIdFromResponse) {
                try {
                    const params = new URLSearchParams();
                    params.append('tmdbId', tmdbIdFromResponse);
                    params.append('mediaType', actualType === 'series' ? 'tv' : 'movie');
                    
                    const url = window.ApiClient.getUrl('api/myplugin/metadata/external-ids') + '?' + params.toString();
                    const externalData = await window.ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' });
                    
                    if (externalData?.imdb_id) {
                        imdbIdFromResponse = externalData.imdb_id;
                    }
                } catch (err) {
                    console.warn('[DetailsModal] Could not fetch external IDs:', err);
                }
            }
            
            modal.dataset.imdbId = imdbIdFromResponse;
            modal.dataset.tmdbId = tmdbIdFromResponse;
            modal.dataset.itemType = actualType;
            
            const { credits, reviews } = await fetchTMDBCreditsAndReviews(actualType === 'series' ? 'tv' : 'movie', tmdbData.id);
            if (credits) populateCredits(modal, tmdbData, credits);
            if (reviews.length > 0) {
                populateReviews(modal, reviews);
                await populateStreams(modal);
            }
            
            if (window.LibraryStatus?.check) {
                const existingRequest = await window.LibraryStatus.checkRequest(imdbIdFromResponse, tmdbIdFromResponse, actualType);
                
                if (existingRequest) {
                    console.log('[DetailsModal] Item found in requests');
                    
                    let currentUsername = 'Unknown';
                    let isAdmin = false;
                    try {
                        const userId = window.ApiClient.getCurrentUserId();
                        const user = await window.ApiClient.getUser(userId);
                        currentUsername = user?.Name || 'Unknown';
                        isAdmin = user?.Policy?.IsAdministrator || false;
                    } catch (err) {
                        console.warn('[DetailsModal] Error getting user via getUser, trying getCurrentUser:', err);
                        try {
                            const user = await window.ApiClient.getCurrentUser();
                            currentUsername = user?.Name || 'Unknown';
                            isAdmin = user?.Policy?.IsAdministrator || false;
                        } catch (fallbackErr) {
                            console.error('[DetailsModal] Fallback error:', fallbackErr);
                        }
                    }
                    
                    const isOwnRequest = existingRequest.username === currentUsername;
                    
                    modal.dataset.requestId = existingRequest.id;
                    modal.dataset.isRequestMode = 'true';
                    
                    const requesterEl = qs('#item-detail-requester', modal);
                    if (requesterEl) {
                        requesterEl.textContent = 'Requested by: ' + existingRequest.username;
                        requesterEl.style.display = 'block';
                    }
                    
                    const importBtn = qs('#item-detail-import', modal);
                    const requestBtn = qs('#item-detail-request', modal);
                    const openBtn = qs('#item-detail-open', modal);
                    const approveBtn = qs('#item-detail-approve', modal);
                    const removeBtn = qs('#item-detail-remove', modal);
                    
                    // Hide all buttons initially
                    if (importBtn) importBtn.style.display = 'none';
                    if (requestBtn) requestBtn.style.display = 'none';
                    if (openBtn) openBtn.style.display = 'none';
                    if (approveBtn) approveBtn.style.display = 'none';
                    if (removeBtn) removeBtn.style.display = 'none';
                    
                    // Clear any previous status messages
                    const existingMsg = qs('.request-status-msg', modal);
                    if (existingMsg) existingMsg.remove();
                    
                    console.log('[DetailsModal] Request status:', existingRequest.status, 'isAdmin:', isAdmin, 'isOwn:', isOwnRequest);
                    
                    if (isAdmin) {
                        // Admin viewing a request
                        if (existingRequest.status === 'pending') {
                            // Pending - show approve + reject
                            if (approveBtn) {
                                approveBtn.style.display = 'block';
                                approveBtn.textContent = 'Approve & Import';
                            }
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Reject Request';
                            }
                        } else {
                            // Approved - show import + remove
                            if (importBtn) {
                                importBtn.style.display = 'block';
                                importBtn.textContent = 'Import Now';
                            }
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Remove Request';
                            }
                        }
                    } else {
                        // Non-admin user viewing a request
                        if (existingRequest.status === 'pending') {
                            // Pending request
                            if (isOwnRequest) {
                                // User's own pending request - show cancel button
                                if (removeBtn) {
                                    removeBtn.style.display = 'block';
                                    removeBtn.textContent = 'Cancel Request';
                                }
                                if (approveBtn) approveBtn.style.display = 'none';
                                if (openBtn) openBtn.style.display = 'none';
                            } else {
                                // Someone else's pending request - show nothing
                                if (approveBtn) approveBtn.style.display = 'none';
                                if (removeBtn) removeBtn.style.display = 'none';
                                if (openBtn) openBtn.style.display = 'none';
                                // Show status message
                                const statusMsg = document.createElement('div');
                                statusMsg.textContent = 'Already requested by ' + existingRequest.username;
                                statusMsg.style.cssText = 'color: #ffa500; padding: 10px; text-align: center; background: rgba(255,165,0,0.1); border-radius: 4px; margin: 10px 0;';
                                const buttonsDiv = qs('.item-detail-buttons', modal);
                                if (buttonsDiv && !qs('.request-status-msg', modal)) {
                                    statusMsg.className = 'request-status-msg';
                                    buttonsDiv.appendChild(statusMsg);
                                }
                            }
                        } else {
                            // Approved request - don't show "Open" unless it's actually in library!
                            // Check if it's actually imported
                            const actuallyInLibrary = await window.LibraryStatus.check(imdbIdFromResponse, tmdbIdFromResponse, actualType);
                            if (actuallyInLibrary) {
                                if (openBtn) openBtn.style.display = 'block';
                            } else {
                                // Approved but not imported yet - show status
                                const statusMsg = document.createElement('div');
                                statusMsg.textContent = 'Request approved - awaiting import';
                                statusMsg.style.cssText = 'color: #4caf50; padding: 10px; text-align: center; background: rgba(76,175,80,0.1); border-radius: 4px; margin: 10px 0;';
                                const buttonsDiv = qs('.item-detail-buttons', modal);
                                if (buttonsDiv && !qs('.request-status-msg', modal)) {
                                    statusMsg.className = 'request-status-msg';
                                    buttonsDiv.appendChild(statusMsg);
                                }
                            }
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (isOwnRequest) {
                                if (removeBtn) {
                                    removeBtn.style.display = 'block';
                                    removeBtn.textContent = 'Remove Request';
                                }
                            } else {
                                if (removeBtn) removeBtn.style.display = 'none';
                            }
                        }
                    }
                    
                    hideLoading(modal);
                    return;
                }
                
                const inLibrary = await window.LibraryStatus.check(imdbIdFromResponse, tmdbIdFromResponse, actualType);
                const importBtn = qs('#item-detail-import', modal);
                const requestBtn = qs('#item-detail-request', modal);
                const openBtn = qs('#item-detail-open', modal);
                
                const approveBtn = qs('#item-detail-approve', modal);
                const removeBtn = qs('#item-detail-remove', modal);
                const requesterEl = qs('#item-detail-requester', modal);
                if (approveBtn) approveBtn.style.display = 'none';
                if (removeBtn) removeBtn.style.display = 'none';
                if (requesterEl) requesterEl.style.display = 'none';
                
                await switchButton(importBtn, requestBtn, openBtn, inLibrary);
            }
            
            hideLoading(modal);

        } catch (err) {
            qs('#item-detail-info', modal).innerHTML = '<div style="color:#ff6b6b;">Error fetching.</div>';
            hideLoading(modal);
        }
    }

    function populateCredits(modal, data, credits) {
        let html = '';
        const genreStr = formatGenres(data.genres, data.genre_ids);
        if (genreStr) html += '<div><strong style="color:#1e90ff;">Genre:</strong> ' + genreStr + '</div>';
        if (data.vote_average) html += '<div><strong style="color:#1e90ff;">Rating:</strong> ' + formatRating(data.vote_average) + '</div>';
        if (data.runtime) html += '<div><strong style="color:#1e90ff;">Runtime:</strong> ' + formatRuntime(data.runtime) + '</div>';

        if (credits.crew) {
            const directors = credits.crew.filter(c => c.job === 'Director');
            if (directors.length) html += '<div style="margin-top:12px;"><strong style="color:#1e90ff;">Director:</strong> ' + directors.map(d => d.name).join(', ') + '</div>';
        }

        if (credits.cast?.length) {
            const topCast = credits.cast.slice(0, 12);
            html += '<div style="margin-top:12px;"><strong style="color:#1e90ff;">Cast:</strong></div>';
            html += '<div class="cast-grid">';
            topCast.forEach(c => {
                const actor = escapeHtml(c.name);
                const role = escapeHtml(c.character || '');
                const profile = c.profile_path ? 'https://image.tmdb.org/t/p/w92' + c.profile_path : '';
                const img = profile ? ('<img class="cast-photo" src="' + profile + '" alt="' + actor + '">') : '<div class="cast-photo" aria-hidden="true"></div>';
                html += '<div class="cast-item">' + img + '<div class="cast-meta"><div class="cast-actor">' + actor + '</div><div class="cast-role">' + role + '</div></div></div>';
            });
            html += '</div>';
        }

        qs('#item-detail-info', modal).innerHTML = html;
    }

    function populateReviews(modal, reviews) {
        if (!reviews.length) return;
        const reviewsDiv = qs('#item-detail-reviews', modal);
        let html = '<strong style="color:#1e90ff;">Reviews:</strong><div class="reviews-carousel-wrapper" style="position:relative;margin-top:10px;padding:0 50px;"><button class="carousel-prev" style="position:absolute;left:0;top:50%;z-index:10;background:rgba(128,128,128,0.7);border:none;color:#fff;width:40px;height:40px;border-radius:50%;cursor:pointer;transform:translateY(-50%);transition:all 0.2s ease;opacity:0;pointer-events:none;">‹</button><button class="carousel-next" style="position:absolute;right:0;top:50%;z-index:10;background:rgba(128,128,128,0.7);border:none;color:#fff;width:40px;height:40px;border-radius:50%;cursor:pointer;transform:translateY(-50%);transition:all 0.2s ease;opacity:0;pointer-events:none;">›</button><div class="reviews-carousel" style="display:flex;gap:15px;transition:transform 0.3s ease;">';

        reviews.forEach(review => {
            const author = review.author || 'Anonymous';
            const content = review.content || '';
            const isTrunc = content.length > 200;
            const text = isTrunc ? content.substring(0, 200) + '...' : content;
            html += '<div class="review-card" style="' + (isTrunc ? 'cursor:pointer;' : '') + '"><div style="font-weight:bold;color:#fff;">' + author + '</div><div style="color:#ccc;font-size:13px;margin-top:8px;">' + text + '</div></div>';
        });

        html += '</div></div><div class="carousel-dots" style="display:flex;justify-content:center;gap:8px;margin-top:15px;"></div>';
        reviewsDiv.innerHTML = html;

        const carousel = qs('.reviews-carousel', reviewsDiv);
        const prevBtn = qs('.carousel-prev', reviewsDiv);
        const nextBtn = qs('.carousel-next', reviewsDiv);
        const wrapper = qs('.reviews-carousel-wrapper', reviewsDiv);
        
        // Add hover effects for arrows
        const showArrows = () => {
            prevBtn.style.opacity = '1';
            prevBtn.style.pointerEvents = 'auto';
            nextBtn.style.opacity = '1';
            nextBtn.style.pointerEvents = 'auto';
        };
        const hideArrows = () => {
            prevBtn.style.opacity = '0';
            prevBtn.style.pointerEvents = 'none';
            nextBtn.style.opacity = '0';
            nextBtn.style.pointerEvents = 'none';
        };
        
        wrapper.addEventListener('mouseenter', showArrows);
        wrapper.addEventListener('mouseleave', hideArrows);
        
        // Add hover effects for individual arrows
        [prevBtn, nextBtn].forEach(btn => {
            btn.addEventListener('mouseenter', () => {
                btn.style.background = 'rgba(128,128,128,0.9)';
                btn.style.transform = 'translateY(-50%) scale(1.1)';
            });
            btn.addEventListener('mouseleave', () => {
                btn.style.background = 'rgba(128,128,128,0.7)';
                btn.style.transform = 'translateY(-50%)';
            });
        });
        let idx = 0;

        for (let i = 0; i < reviews.length; i++) {
            const dot = document.createElement('button');
            dot.style.cssText = 'width:10px;height:10px;border-radius:50%;background:' + (i === 0 ? '#1e90ff' : '#555') + ';border:none;cursor:pointer;padding:0;';
            dot.addEventListener('click', () => { idx = i; update(); });
            qs('.carousel-dots', reviewsDiv).appendChild(dot);
        }

        function update() {
            const firstCard = qs('.review-card', carousel);
            if (firstCard) {
                const cardWidth = firstCard.offsetWidth;
                const gap = 15;
                const offset = idx * (cardWidth + gap);
                carousel.style.transform = 'translateX(-' + offset + 'px)';
            }
            qsa('button', qs('.carousel-dots', reviewsDiv)).forEach((d, i) => d.style.background = i === idx ? '#1e90ff' : '#555');
        }

        prevBtn.addEventListener('click', () => { if (idx > 0) { idx--; update(); } });
        nextBtn.addEventListener('click', () => { if (idx < reviews.length - 1) { idx++; update(); } });
        update();

        qsa('.review-card', reviewsDiv).forEach((card, i) => {
            if (reviews[i] && reviews[i].content.length > 200) {
                card.addEventListener('click', () => {
                    const popup = qs('#review-popup');
                    qs('#review-popup-content', popup).innerHTML = '<h3 style="color:#fff;margin-bottom:15px;">Review by ' + reviews[i].author + '</h3>' + reviews[i].content;
                    popup.style.display = 'block';
                });
            }
        });
    }

    // Event listeners for opening modal from search results
    document.addEventListener('click', ev => {
        try {
            const hash = window.location.hash;
            const isSearchPage = hash.includes('#/search');
            if (!isSearchPage) return;
            
            const anchor = ev.target.closest('a[href*="#/details"]');
            if (!anchor) return;
            
            if (ev.button !== 0 || ev.ctrlKey) return;
            
            console.log('[DetailsModal] Intercepting click');
            ev.preventDefault();
            ev.stopPropagation();

            let id = anchor.dataset.id;
            if (!id && anchor.href) {
                const hashPart = anchor.href.split('#')[1] || '';
                if (hashPart.includes('?')) {
                    try { 
                        const params = new URLSearchParams(hashPart.split('?')[1]);
                        id = params.get('id');
                    } catch (e) {}
                }
            }
            
            if (!id) return;

            console.log('[DetailsModal] Opening for ID:', id);
            const modal = getModal();
            populateFromCard(anchor, id, modal);
            showModal(modal);
        } catch (e) { console.error('[DetailsModal] Error:', e); }
    }, true);

    // Listen for request card clicks
    document.addEventListener('openDetailsModal', async (ev) => {
        try {
            const { item, isRequestMode, requestId, requestUsername, isAdmin } = ev.detail || {};
            if (!item) return;
            
            console.log('[DetailsModal] Opening modal for request');
            
            const modal = getModal();
            const loadingEl = qs('#item-detail-loading-overlay', modal);
            if (loadingEl) loadingEl.style.display = 'flex';
            
            qs('#item-detail-title', modal).textContent = item.Name || 'Loading...';
            qs('#item-detail-meta', modal).textContent = item.ProductionYear || '';
            qs('#item-detail-overview', modal).textContent = '';
            
            modal.dataset.itemId = item.jellyfinId || item.tmdbId || item.imdbId;
            modal.dataset.tmdbId = item.tmdbId;
            modal.dataset.imdbId = item.imdbId;
            modal.dataset.jellyfinId = item.jellyfinId;
            modal.dataset.itemType = item.itemType;
            modal.dataset.requestId = requestId;
            modal.dataset.isRequestMode = isRequestMode;
            
            const tmdbData = await getTMDBData(item.tmdbId, item.imdbId, item.itemType, item.Name, item.ProductionYear);
            
            if (tmdbData) {
                const displayTitle = tmdbData.title || tmdbData.name;
                if (displayTitle) qs('#item-detail-title', modal).textContent = displayTitle;
                
                const requesterEl = qs('#item-detail-requester', modal);
                if (requestUsername && requesterEl) {
                    requesterEl.textContent = requestUsername;
                    requesterEl.style.display = 'block';
                } else if (requesterEl) {
                    requesterEl.style.display = 'none';
                }
                
                if (tmdbData.overview) qs('#item-detail-overview', modal).textContent = tmdbData.overview;
                if (tmdbData.poster_path) setBackgroundImage(qs('#item-detail-image', modal), 'https://image.tmdb.org/t/p/w500' + tmdbData.poster_path);
                
                const { credits, reviews } = await fetchTMDBCreditsAndReviews(item.itemType === 'series' ? 'tv' : 'movie', tmdbData.id);
                if (credits) populateCredits(modal, tmdbData, credits);
                if (reviews?.length > 0) {
                    populateReviews(modal, reviews);
                    await populateStreams(modal);
                }
            }
            
            const importBtn = qs('#item-detail-import', modal);
            const requestBtn = qs('#item-detail-request', modal);
            const openBtn = qs('#item-detail-open', modal);
            const approveBtn = qs('#item-detail-approve', modal);
            const removeBtn = qs('#item-detail-remove', modal);
            
            if (isRequestMode) {
                const { requestStatus, isOwnRequest } = ev.detail || {};
                
                if (importBtn) importBtn.style.display = 'none';
                if (requestBtn) requestBtn.style.display = 'none';
                
                if (isAdmin) {
                    if (requestStatus === 'pending') {
                        if (approveBtn) approveBtn.style.display = 'block';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    } else {
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    }
                } else {
                    if (requestStatus === 'pending') {
                        if (isOwnRequest) {
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Cancel';
                            }
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        } else {
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (removeBtn) removeBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        }
                    } else {
                        if (openBtn) openBtn.style.display = 'block';
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (isOwnRequest) {
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Remove';
                            }
                        } else {
                            if (removeBtn) removeBtn.style.display = 'none';
                        }
                    }
                }
            }
            
            if (loadingEl) loadingEl.style.display = 'none';
            showModal(modal);
        } catch (e) { 
            console.error('[DetailsModal] Error opening request modal:', e);
            const loadingEl = qs('#item-detail-loading-overlay', getModal());
            if (loadingEl) loadingEl.style.display = 'none';
        }
    });

    window.addEventListener('hashchange', hideModal);
    window.addEventListener('popstate', hideModal);
    document.addEventListener('visibilitychange', () => { if (document.hidden) hideModal(); });

    console.log('[DetailsModal] Standalone version loaded');
})();
