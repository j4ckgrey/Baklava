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

            const url = window.ApiClient.getUrl('api/baklava/metadata/tmdb') + '?' + params.toString();
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

            const url = window.ApiClient.getUrl('api/baklava/metadata/tmdb') + '?' + params.toString();
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

            // Use the Baklava metadata streams endpoint (server-side) — not Gelato path
            const url = window.ApiClient.getUrl('api/baklava/metadata/streams') + '?itemId=' + encodeURIComponent(itemId);
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
                        + '<button id="item-detail-reject" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#ff5722;color:#fff;cursor:pointer;display:none;font-size:13px;">Reject</button>'
                        + '<button id="item-detail-import" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#1e90ff;color:#fff;cursor:pointer;display:none;font-size:13px;">Import</button>'
                        + '<button id="item-detail-request" style="width:100px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#ff9800;color:#fff;cursor:pointer;display:none;font-size:13px;">Request</button>'
                        + '<button id="item-detail-view-requests" style="width:120px;height:32px;padding:6px 12px;border:none;border-radius:4px;background:#9c27b0;color:#fff;cursor:pointer;display:none;font-size:13px;">View Requests</button>'
                        + '<button id="item-detail-remove" title="Remove" style="width:36px;height:36px;padding:0;border:none;border-radius:4px;background:#f44336;color:#fff;cursor:pointer;display:none;font-size:18px;line-height:1;display:flex;align-items:center;justify-content:center;">'
                            + '<span class="material-icons" aria-hidden="true" style="font-size:18px;line-height:1;">delete</span>'
                        + '</button>'
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
                                + '<button id="close-review-popup" title="Close" style="background:#555;border:none;color:#fff;padding:8px 12px;cursor:pointer;border-radius:4px;font-size:16px;">'
                                    + '<span class="material-icons" aria-hidden="true" style="vertical-align:middle;">close</span>'
                                + '</button>'
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
        const rejectBtn = qs('#item-detail-reject', overlay);
        const removeBtn = qs('#item-detail-remove', overlay);
        const openBtn = qs('#item-detail-open', overlay);
        const viewRequestsBtn = qs('#item-detail-view-requests', overlay);
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
            // Immediately mark requested and disable. Keep UI simple and non-flashy.
            requestBtn.disabled = true;
            requestBtn.textContent = 'Requested';
            requestBtn.style.background = '#888';

            // Still dispatch the mediaRequest event for downstream handling if needed
            const item = {
                title: qs('#item-detail-title', overlay).textContent,
                year: qs('#item-detail-meta', overlay).textContent,
                img: qs('#item-detail-image', overlay).style.backgroundImage,
                imdbId: overlay.dataset.imdbId,
                tmdbId: overlay.dataset.tmdbId,
                itemType: overlay.dataset.itemType,
                jellyfinId: overlay.dataset.itemId,
                status: 'requested'
            };
            console.log('[DetailsModal] Dispatching mediaRequest event:', item);
            document.dispatchEvent(new CustomEvent('mediaRequest', { detail: item }));
        });
        
        openBtn.addEventListener('click', () => {
            const id = overlay.dataset.itemId;
            if (id) { hideModal(); window.location.hash = '#/details?id=' + encodeURIComponent(id); }
        });

        viewRequestsBtn.addEventListener('click', () => {
            hideModal();
            // Click the requests header button to open dropdown
            const requestsBtn = document.querySelector('[data-role="requests-button"]');
            if (requestsBtn) {
                requestsBtn.click();
            } else if (window.RequestsHeaderButton && window.RequestsHeaderButton.show) {
                window.RequestsHeaderButton.show();
            }
        });

    approveBtn.addEventListener('click', async () => {
            // Make UI response immediate and fire a single Gelato API call (fire-and-forget),
            // then disable the button and close the modal.
            const requestId = overlay.dataset.requestId;
            const tmdbId = overlay.dataset.tmdbId;
            const imdbId = overlay.dataset.imdbId;
            const itemType = overlay.dataset.itemType || 'movie';

            if (!requestId) {
                // Nothing to do: clear and close quickly
                approveBtn.textContent = 'No-op';
                approveBtn.style.background = '#888';
                setTimeout(() => hideModal(), 400);
                return;
            }

            // Fire server-side proxy to call Gelato (non-blocking), then update UI.
            if (imdbId) {
                try {
                    const isSeries = itemType && itemType.toLowerCase().includes('series');
                    // MetadataController exposes a server-side proxy at /api/gelato/movie/{id} and /api/gelato/tv/{id}
                    const proxyPath = isSeries ? `api/gelato/tv/${encodeURIComponent(imdbId)}` : `api/gelato/movie/${encodeURIComponent(imdbId)}`;
                    const proxyUrl = window.ApiClient.getUrl(proxyPath);

                    // Fire-and-forget: POST to the server proxy which will call Gelato using configured auth.
                    try {
                        window.ApiClient.ajax({ type: 'POST', url: proxyUrl }).catch(() => {});
                    } catch (e) {
                        console.warn('[DetailsModal] gelato proxy call failed', e);
                    }
                } catch (e) {
                    console.error('[DetailsModal] Error preparing gelato proxy call:', e);
                }
            }

            // Immediate UI feedback regardless of network outcome
            approveBtn.disabled = true;
            approveBtn.textContent = 'Approved';
            approveBtn.style.background = '#888';

            // Move the card live into the Approved carousel(s) so admins see it immediately.
            try {
                (function moveCardToApproved(id) {
                    if (!id) return;

                    // Find the existing card in the DOM (could be in movies/series list)
                    const selector = `.request-card[data-request-id="${id}"]`;
                    const orig = document.querySelector(selector);

                    // Append to dropdown approved container
                    const approvedDropdown = document.querySelector('.dropdown-approved-container');
                    if (approvedDropdown) {
                        try {
                            if (orig && orig.parentElement) {
                                // Clone original for dropdown view (so other view can remain intact)
                                const clone = orig.cloneNode(true);
                                // Ensure badge shows Approved
                                const maybeBadge = clone.querySelector('div');
                                // Append clone (event handlers will be refreshed when full reload occurs)
                                approvedDropdown.appendChild(clone);
                            }
                        } catch (e) { /* ignore DOM errors */ }
                    }

                    // Append to requests page approved panel
                    const approvedPage = document.querySelector('.requests-approved-panel .itemsContainer');
                    if (approvedPage) {
                        try {
                            if (orig && orig.parentElement) {
                                const clone2 = orig.cloneNode(true);
                                approvedPage.appendChild(clone2);
                            }
                        } catch (e) { /* ignore */ }
                    }

                    // Remove original from its current parent so it visually moves
                    try { if (orig && orig.parentElement) orig.parentElement.removeChild(orig); } catch (e) { }
                })(requestId);
            } catch (e) {
                console.warn('[DetailsModal] moveCardToApproved failed', e);
            }

            // Also close the dropdown and requests page so admin is returned to main UI
            try { hideModal(); } catch (e) { /* ignore */ }
            try { if (window.RequestsHeaderButton) { const dd = document.querySelector('.requests-dropdown'); if (dd) dd.style.display = 'none'; const back = document.querySelector('.requests-backdrop'); if (back) back.style.display = 'none'; } } catch (e) { }
            try { const rp = document.getElementById('requestsPage'); if (rp) rp.style.display = 'none'; } catch (e) { }

            // Fire-and-forget server update (record ApprovedBy and trigger Gelato)
            if (requestId && window.RequestManager && typeof window.RequestManager.updateStatus === 'function') {
                try {
                    let approver = null;
                    try { const current = await window.ApiClient.getCurrentUser(); approver = current?.Name || null; } catch (err) {
                        try { const user = await window.ApiClient.getUser(window.ApiClient.getCurrentUserId()); approver = user?.Name || null; } catch { approver = null; }
                    }
                    window.RequestManager.updateStatus(requestId, 'approved', approver).catch(() => {});
                } catch (e) { console.warn('[DetailsModal] Failed to update request status:', e); }
            }
            // Close the modal and navigate to the details page for this item so admins
            // can immediately inspect the imported title. Use the itemId stored on
            // the modal (prefers Jellyfin item id when available).
            setTimeout(() => {
                try { hideModal(); } catch (e) { /* ignore */ }
                const targetId = overlay?.dataset?.itemId || overlay?.dataset?.jellyfinId || overlay?.dataset?.tmdbId || overlay?.dataset?.imdbId;
                if (targetId) {
                    // Navigate to Jellyfin details route
                    window.location.hash = '#/details?id=' + encodeURIComponent(targetId);
                }
            }, 250);
        });

        rejectBtn.addEventListener('click', async () => {
            const requestId = overlay.dataset.requestId;

            if (!requestId) {
                rejectBtn.textContent = 'No-op';
                rejectBtn.style.background = '#888';
                setTimeout(() => hideModal(), 400);
                return;
            }

            // Immediate UI feedback
            rejectBtn.disabled = true;
            rejectBtn.textContent = 'Rejected';
            rejectBtn.style.background = '#888';

            // Move card to rejected carousel
            try {
                const selector = `.request-card[data-request-id="${requestId}"]`;
                const orig = document.querySelector(selector);

                // Append to dropdown rejected container
                const rejectedDropdown = document.querySelector('.dropdown-rejected-container');
                if (rejectedDropdown) {
                    try {
                        if (orig && orig.parentElement) {
                            const clone = orig.cloneNode(true);
                            // Update the status badge to show "Rejected"
                            const badges = clone.querySelectorAll('div');
                            for (const badge of badges) {
                                if (badge.textContent === 'Pending') {
                                    badge.textContent = 'Rejected';
                                    badge.style.background = 'rgba(244, 67, 54, 0.95)';
                                }
                            }
                            rejectedDropdown.appendChild(clone);
                        }
                    } catch (e) { /* ignore */ }
                }

                // Append to requests page rejected panel
                const rejectedPage = document.querySelector('.requests-rejected-panel .itemsContainer');
                if (rejectedPage) {
                    try {
                        if (orig && orig.parentElement) {
                            const clone2 = orig.cloneNode(true);
                            // Update the status badge to show "Rejected"
                            const badges = clone2.querySelectorAll('div');
                            for (const badge of badges) {
                                if (badge.textContent === 'Pending') {
                                    badge.textContent = 'Rejected';
                                    badge.style.background = 'rgba(244, 67, 54, 0.95)';
                                }
                            }
                            rejectedPage.appendChild(clone2);
                        }
                    } catch (e) { /* ignore */ }
                }

                // Remove original from its current parent
                try { if (orig && orig.parentElement) orig.parentElement.removeChild(orig); } catch (e) { }
            } catch (e) {
                console.warn('[DetailsModal] moveCardToRejected failed', e);
            }

            // Close modal and dropdown
            try { hideModal(); } catch (e) { }
            try { 
                const dd = document.querySelector('.requests-dropdown'); 
                if (dd) dd.style.display = 'none'; 
                const back = document.querySelector('.requests-backdrop'); 
                if (back) back.style.display = 'none';
            } catch (e) { }

            // Update server status
            if (requestId && window.RequestManager && typeof window.RequestManager.updateStatus === 'function') {
                try {
                    let rejecter = null;
                    try { 
                        const current = await window.ApiClient.getCurrentUser(); 
                        rejecter = current?.Name || null; 
                    } catch (err) {
                        try { 
                            const user = await window.ApiClient.getUser(window.ApiClient.getCurrentUserId()); 
                            rejecter = user?.Name || null; 
                        } catch { rejecter = null; }
                    }
                    window.RequestManager.updateStatus(requestId, 'rejected', rejecter).catch(() => {});
                } catch (e) { 
                    console.warn('[DetailsModal] Failed to update request status:', e); 
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
        requestBtn.addEventListener('mouseenter', () => { if (!requestBtn.disabled) requestBtn.style.background = '#f57c00'; });
        requestBtn.addEventListener('mouseleave', () => { if (!requestBtn.disabled) requestBtn.style.background = '#ff9800'; });
        approveBtn.addEventListener('mouseenter', () => { if (!approveBtn.disabled) approveBtn.style.background = '#45a049'; });
        approveBtn.addEventListener('mouseleave', () => { if (!approveBtn.disabled) approveBtn.style.background = '#4caf50'; });
        rejectBtn.addEventListener('mouseenter', () => { if (!rejectBtn.disabled) rejectBtn.style.background = '#e64a19'; });
        rejectBtn.addEventListener('mouseleave', () => { if (!rejectBtn.disabled) rejectBtn.style.background = '#ff5722'; });
        removeBtn.addEventListener('mouseenter', () => removeBtn.style.background = '#d32f2f');
        removeBtn.addEventListener('mouseleave', () => removeBtn.style.background = '#f44336');
        openBtn.addEventListener('mouseenter', () => openBtn.style.background = '#45a049');
        openBtn.addEventListener('mouseleave', () => openBtn.style.background = '#4caf50');
        viewRequestsBtn.addEventListener('mouseenter', () => viewRequestsBtn.style.background = '#7b1fa2');
        viewRequestsBtn.addEventListener('mouseleave', () => viewRequestsBtn.style.background = '#9c27b0');
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
            qs('#item-detail-reject', m).style.display = 'none';
            qs('#item-detail-view-requests', m).style.display = 'none';
            qs('#item-detail-remove', m).style.display = 'none';
            qs('#item-detail-open', m).style.display = 'none';
            const loadingOverlay = qs('#item-detail-loading-overlay', m);
            if (loadingOverlay) loadingOverlay.style.display = 'flex';
        } 
    }

    function populateFromCard(anchor, id, modal) {
        console.log('[DetailsModal.populateFromCard] Starting - ID:', id);
        const card = anchor.closest('.card') || anchor.closest('[data-id]');
        const title = anchor.getAttribute('title') || anchor.textContent.trim() || qs('.cardText-first a', card)?.textContent || 'Untitled';
        const year = qs('.cardText-secondary bdi', card)?.textContent || '';
        const imgContainer = qs('.cardImageContainer', card);
        const bgImage = getBackgroundImage(imgContainer);
        const isSeriesCard = card?.className?.includes('Series') || card?.parentElement?.className?.includes('series');

        console.log('[DetailsModal.populateFromCard] Card analysis:', {
            title,
            year,
            isSeriesCard,
            cardClass: card?.className,
            parentClass: card?.parentElement?.className,
            itemId: id
        });

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
        console.log('[DetailsModal.fetchMetadata] Starting with:', { jellyfinId, title, year, forceSeries });
        
        try {
            let { tmdbId, imdbId, itemType } = parseJellyfinId(jellyfinId, card);
            console.log('[DetailsModal.fetchMetadata] Parsed Jellyfin ID:', { tmdbId, imdbId, itemType });
            
            if (forceSeries && itemType === 'movie') {
                console.log('[DetailsModal.fetchMetadata] Forcing series type');
                itemType = 'series';
            }
            
            console.log('[DetailsModal.fetchMetadata] Calling getTMDBData with:', { tmdbId, imdbId, itemType, title, year });
            const tmdbData = await getTMDBData(tmdbId, imdbId, itemType, title, year);

            if (!tmdbData) {
                console.error('[DetailsModal.fetchMetadata] No TMDB data returned');
                qs('#item-detail-info', modal).innerHTML = 'Could not find metadata.';
                return;
            }

            console.log('[DetailsModal.fetchMetadata] TMDB data received:', {
                id: tmdbData.id,
                imdb_id: tmdbData.imdb_id,
                title: tmdbData.title,
                name: tmdbData.name,
                hasSeasons: !!tmdbData.number_of_seasons
            });

            const displayTitle = tmdbData.title || tmdbData.name;
            if (displayTitle) qs('#item-detail-title', modal).textContent = displayTitle;
            if (tmdbData.overview) qs('#item-detail-overview', modal).textContent = tmdbData.overview;
            if (tmdbData.poster_path) setBackgroundImage(qs('#item-detail-image', modal), 'https://image.tmdb.org/t/p/w500' + tmdbData.poster_path);

            // Detect type from response (name=TV, title=movie)
            const actualType = (tmdbData.name && !tmdbData.title) ? 'series' : 
                              (tmdbData.title && !tmdbData.name) ? 'movie' :
                              (tmdbData.number_of_seasons) ? 'series' : 'movie';
            
            console.log('[DetailsModal.fetchMetadata] Detected actual type:', actualType);
            
            const tmdbIdFromResponse = tmdbData.id;
            let imdbIdFromResponse = imdbId || tmdbData.imdb_id;
            
            console.log('[DetailsModal] TMDB IDs before external fetch - tmdbId:', tmdbIdFromResponse, 'imdbId:', imdbIdFromResponse);
            
            // Fetch external IDs if needed
            if (!imdbIdFromResponse && tmdbIdFromResponse) {
                console.log('[DetailsModal.fetchMetadata] Fetching external IDs for TMDB ID:', tmdbIdFromResponse);
                try {
                    const params = new URLSearchParams();
                    params.append('tmdbId', tmdbIdFromResponse);
                    params.append('mediaType', actualType === 'series' ? 'tv' : 'movie');
                    
                    const url = window.ApiClient.getUrl('api/baklava/metadata/external-ids') + '?' + params.toString();
                    const externalData = await window.ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' });
                    
                    console.log('[DetailsModal.fetchMetadata] External IDs response:', externalData);
                    
                    if (externalData?.imdb_id) {
                        imdbIdFromResponse = externalData.imdb_id;
                        console.log('[DetailsModal.fetchMetadata] Found IMDB ID from external IDs:', imdbIdFromResponse);
                    }
                } catch (err) {
                    console.warn('[DetailsModal] Could not fetch external IDs:', err);
                }
            }
            
            console.log('[DetailsModal] FINAL IDs - Setting modal.dataset - tmdbId:', tmdbIdFromResponse, 'imdbId:', imdbIdFromResponse, 'itemType:', actualType);
            
            modal.dataset.imdbId = imdbIdFromResponse || '';
            modal.dataset.tmdbId = tmdbIdFromResponse || '';
            modal.dataset.itemType = actualType;
            
            const { credits, reviews } = await fetchTMDBCreditsAndReviews(actualType === 'series' ? 'tv' : 'movie', tmdbData.id);
            if (credits) populateCredits(modal, tmdbData, credits);
            if (reviews.length > 0) {
                populateReviews(modal, reviews);
                await populateStreams(modal);
            }
            
            if (window.LibraryStatus?.check) {
                const existingRequest = await window.LibraryStatus.checkRequest(imdbIdFromResponse, tmdbIdFromResponse, actualType, modal.dataset.itemId || null);
                
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
                    const rejectBtn = qs('#item-detail-reject', modal);
                    const removeBtn = qs('#item-detail-remove', modal);
                    const viewRequestsBtn = qs('#item-detail-view-requests', modal);
                    
                    // Hide all buttons initially
                    if (importBtn) importBtn.style.display = 'none';
                    if (requestBtn) requestBtn.style.display = 'none';
                    if (openBtn) openBtn.style.display = 'none';
                    if (approveBtn) approveBtn.style.display = 'none';
                    if (rejectBtn) rejectBtn.style.display = 'none';
                    if (removeBtn) removeBtn.style.display = 'none';
                    if (viewRequestsBtn) viewRequestsBtn.style.display = 'none';
                    
                    // Clear any previous status messages
                    const existingMsg = qs('.request-status-msg', modal);
                    if (existingMsg) existingMsg.remove();
                    
                    console.log('[DetailsModal] Request status:', existingRequest.status, 'isAdmin:', isAdmin, 'isOwn:', isOwnRequest);
                    
                    // Check if item is in library
                    const inLibrary = await window.LibraryStatus.check(imdbIdFromResponse, tmdbIdFromResponse, actualType, modal.dataset.itemId || null);
                    
                    if (inLibrary) {
                        // Item is in library - just show Open button
                        if (openBtn) openBtn.style.display = 'block';
                        // Update request status display but don't show action buttons
                        if (requesterEl) {
                            let statusText = existingRequest.status.charAt(0).toUpperCase() + existingRequest.status.slice(1);
                            requesterEl.textContent = `${statusText} - in library`;
                            requesterEl.style.display = 'block';
                        }
                    } else if (isAdmin) {
                        // Admin viewing a request - item NOT in library
                        if (existingRequest.status === 'pending') {
                            // Pending - show approve + reject
                            if (approveBtn) {
                                approveBtn.style.display = 'block';
                                approveBtn.textContent = 'Approve';
                            }
                            if (rejectBtn) {
                                rejectBtn.style.display = 'block';
                                rejectBtn.textContent = 'Reject';
                            }
                        } else if (existingRequest.status === 'approved') {
                            // Approved but not imported - show import + delete
                            if (importBtn) {
                                importBtn.style.display = 'block';
                                importBtn.textContent = 'Import';
                            }
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Delete';
                            }
                        } else if (existingRequest.status === 'rejected') {
                            // Rejected - just show delete
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Delete';
                            }
                        }
                    } else {
                        // Non-admin user viewing a request
                        if (existingRequest.status === 'pending') {
                            if (isOwnRequest) {
                                // User's own pending request - show "View Requests" button to open dropdown
                                if (viewRequestsBtn) {
                                    viewRequestsBtn.style.display = 'block';
                                }
                            } else {
                                // Someone else's pending request - show nothing (just close button)
                            }
                        } else if (existingRequest.status === 'approved') {
                            // Approved - show status message
                            const statusMsg = document.createElement('div');
                            statusMsg.textContent = 'Request approved - awaiting import';
                            statusMsg.style.cssText = 'color: #4caf50; padding: 10px; text-align: center; background: rgba(76,175,80,0.1); border-radius: 4px; margin: 10px 0;';
                            statusMsg.className = 'request-status-msg';
                            const modalRight = qs('.right', modal);
                            if (modalRight) modalRight.insertBefore(statusMsg, modalRight.firstChild.nextSibling);
                            
                            if (isOwnRequest && removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Cancel Request';
                            }
                        } else if (existingRequest.status === 'rejected') {
                            // Rejected - show status message
                            const statusMsg = document.createElement('div');
                            statusMsg.textContent = 'Request rejected';
                            statusMsg.style.cssText = 'color: #f44336; padding: 10px; text-align: center; background: rgba(244,67,54,0.1); border-radius: 4px; margin: 10px 0;';
                            statusMsg.className = 'request-status-msg';
                            const modalRight = qs('.right', modal);
                            if (modalRight) modalRight.insertBefore(statusMsg, modalRight.firstChild.nextSibling);
                            
                            if (isOwnRequest && removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Delete';
                            }
                        }
                    }
                    
                    hideLoading(modal);
                    return;
                }
                
                // No existing request - check library status
                const inLibrary = await window.LibraryStatus.check(imdbIdFromResponse, tmdbIdFromResponse, actualType, modal.dataset.itemId || null);
                const importBtn = qs('#item-detail-import', modal);
                const requestBtn = qs('#item-detail-request', modal);
                const openBtn = qs('#item-detail-open', modal);
                
                const approveBtn = qs('#item-detail-approve', modal);
                const rejectBtn = qs('#item-detail-reject', modal);
                const removeBtn = qs('#item-detail-remove', modal);
                const requesterEl = qs('#item-detail-requester', modal);
                if (approveBtn) approveBtn.style.display = 'none';
                if (rejectBtn) rejectBtn.style.display = 'none';
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
        let html = '<strong style="color:#1e90ff;">Reviews:</strong><div class="reviews-carousel-wrapper" style="position:relative;margin-top:10px;padding:0 50px;"><button class="carousel-prev" style="position:absolute;left:0;top:50%;z-index:10;background:rgba(30,144,255,0.8);border:none;color:#fff;width:40px;height:40px;border-radius:50%;cursor:pointer;transform:translateY(-50%);">‹</button><button class="carousel-next" style="position:absolute;right:0;top:50%;z-index:10;background:rgba(30,144,255,0.8);border:none;color:#fff;width:40px;height:40px;border-radius:50%;cursor:pointer;transform:translateY(-50%);">›</button><div class="reviews-carousel" style="display:flex;gap:15px;transition:transform 0.3s ease;">';

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
            const rejectBtn = qs('#item-detail-reject', modal);
            const removeBtn = qs('#item-detail-remove', modal);
            
            if (isRequestMode) {
                const { requestStatus, isOwnRequest } = ev.detail || {};
                
                if (importBtn) importBtn.style.display = 'none';
                if (requestBtn) requestBtn.style.display = 'none';
                
                if (isAdmin) {
                    if (requestStatus === 'pending') {
                        // Pending - show approve + reject
                        if (approveBtn) approveBtn.style.display = 'block';
                        if (rejectBtn) rejectBtn.style.display = 'block';
                        if (removeBtn) removeBtn.style.display = 'none';
                        if (openBtn) openBtn.style.display = 'none';
                    } else if (requestStatus === 'approved') {
                        // Approved - show import + delete
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (rejectBtn) rejectBtn.style.display = 'none';
                        if (importBtn) importBtn.style.display = 'block';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    } else if (requestStatus === 'rejected') {
                        // Rejected - show delete only
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (rejectBtn) rejectBtn.style.display = 'none';
                        if (importBtn) importBtn.style.display = 'none';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    }
                } else {
                    // Non-admin user
                    if (requestStatus === 'pending') {
                        if (isOwnRequest) {
                            // Own pending - show cancel
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Cancel';
                            }
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (rejectBtn) rejectBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        } else {
                            // Someone else's pending - show nothing
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (rejectBtn) rejectBtn.style.display = 'none';
                            if (removeBtn) removeBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        }
                    } else if (requestStatus === 'approved' || requestStatus === 'rejected') {
                        // Approved/rejected - for approved show open if in library, for rejected show nothing
                        if (requestStatus === 'approved') {
                            if (openBtn) openBtn.style.display = 'block';
                        } else {
                            // rejected - hide open button
                            if (openBtn) openBtn.style.display = 'none';
                        }
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (rejectBtn) rejectBtn.style.display = 'none';
                        if (isOwnRequest) {
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.title = 'Remove';
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
