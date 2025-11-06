(function() {
    'use strict';
    
    console.log('[DetailsModal] Script loading...');
    
    function init() {
        const UI = window.MediaServerUI;
        if (!UI) {
            console.warn('[DetailsModal] MediaServerUI not ready, retrying in 100ms');
            setTimeout(init, 100);
            return;
        }
        
        console.log('[DetailsModal] Initializing...');
    
    // Small helper to escape HTML when injecting names/roles
    function escapeHtml(s) {
        return String(s || '').replace(/[&<>"']/g, function(c) {
            return ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c];
        });
    }
    
    // Create the modal element
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
        const closeBtn = UI.qs('#item-detail-close', overlay);
        const importBtn = UI.qs('#item-detail-import', overlay);
        const requestBtn = UI.qs('#item-detail-request', overlay);
        const approveBtn = UI.qs('#item-detail-approve', overlay);
        const removeBtn = UI.qs('#item-detail-remove', overlay);
        const openBtn = UI.qs('#item-detail-open', overlay);
        const reviewPopup = UI.qs('#review-popup', overlay);
        const closeReviewBtn = UI.qs('#close-review-popup', overlay);

        overlay.addEventListener('click', ev => ev.target === overlay && hideModal());
        closeBtn.addEventListener('click', hideModal);
        
        importBtn.addEventListener('click', async () => {
            importBtn.disabled = true;
            const imdbId = overlay.dataset.imdbId;
            const tmdbId = overlay.dataset.tmdbId;
            const itemType = overlay.dataset.itemType || 'movie';
            
            await window.ApiClient.addItemToLibrary({ tmdbId, imdbId, type: itemType });
            await new Promise(r => setTimeout(r, 500));
            await window.ApiClient.refreshLibrary();
            
            if (window.LibraryStatus && window.LibraryStatus.check) {
                const inLibrary = await window.LibraryStatus.check(imdbId, tmdbId, itemType);
                await switchButton(importBtn, requestBtn, openBtn, inLibrary);
            }
            importBtn.disabled = false;
        });
        
        requestBtn.addEventListener('click', () => {
            requestBtn.disabled = true;
            requestBtn.textContent = 'Pending';
            requestBtn.style.background = '#888';
                const item = {
                    title: UI.qs('#item-detail-title', overlay).textContent,
                    year: UI.qs('#item-detail-meta', overlay).textContent,
                    img: UI.qs('#item-detail-image', overlay).style.backgroundImage,
                    imdbId: overlay.dataset.imdbId,
                    tmdbId: overlay.dataset.tmdbId,
                    itemType: overlay.dataset.itemType,
                    status: 'pending'
                };

                // Dispatch custom event for your Requests tab script
                document.dispatchEvent(new CustomEvent('mediaRequest', { detail: item }));
        });
        
        openBtn.addEventListener('click', () => {
            const id = overlay.dataset.itemId;
            if (id) { hideModal(); window.location.hash = '#/details?id=' + encodeURIComponent(id); }
        });

        approveBtn.addEventListener('click', async () => {
            const requestId = overlay.dataset.requestId;
            if (requestId && window.RequestManager) {
                await window.RequestManager.updateStatus(requestId, 'approved');
                if (window.RequestsHeaderButton) await window.RequestsHeaderButton.reload();
                hideModal();
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

    function getModal() { return UI.qs('#item-detail-modal-overlay') || createModal(); }
    function showModal(modal) { modal.classList.add('open'); document.body.style.overflow = 'hidden'; }
    function hideLoading(modal) { 
        const overlay = UI.qs('#item-detail-loading-overlay', modal);
        if (overlay) overlay.style.display = 'none';
    }
    function hideModal() { 
        const m = UI.qs('#item-detail-modal-overlay'); 
        if (m) { 
            m.classList.remove('open'); 
            document.body.style.overflow = '';
            UI.qs('#item-detail-title', m).textContent = 'Loading…';
            UI.qs('#item-detail-meta', m).textContent = '';
            UI.qs('#item-detail-overview', m).textContent = '';
            UI.qs('#item-detail-info', m).innerHTML = '';
            UI.qs('#item-detail-reviews', m).innerHTML = '';
            UI.qs('#item-detail-image', m).style.backgroundImage = '';
            UI.qs('#item-detail-import', m).style.display = 'none';
            UI.qs('#item-detail-request', m).style.display = 'none';
            UI.qs('#item-detail-approve', m).style.display = 'none';
            UI.qs('#item-detail-remove', m).style.display = 'none';
            UI.qs('#item-detail-open', m).style.display = 'none';
            const loadingOverlay = UI.qs('#item-detail-loading-overlay', m);
            if (loadingOverlay) loadingOverlay.style.display = 'flex';
        } 
    }

    function populateFromCard(anchor, id, modal) {
        const card = anchor.closest('.card') || anchor.closest('[data-id]');
        const title = anchor.getAttribute('title') || anchor.textContent.trim() || UI.qs('.cardText-first a', card)?.textContent || 'Untitled';
        const year = UI.qs('.cardText-secondary bdi', card)?.textContent || '';
        const imgContainer = UI.qs('.cardImageContainer', card);
        const bgImage = UI.getBackgroundImage(imgContainer);
        const isSeriesCard = card?.className?.includes('Series') || card?.parentElement?.className?.includes('series');

        UI.qs('#item-detail-title', modal).textContent = title;
        UI.qs('#item-detail-meta', modal).textContent = year || '';
        UI.qs('#item-detail-overview', modal).textContent = '';
        UI.setBackgroundImage(UI.qs('#item-detail-image', modal), bgImage);
        modal.dataset.itemId = id;

        fetchMetadata(id, card, modal, title, year, isSeriesCard).catch(() => {
            UI.qs('#item-detail-loading', modal).style.display = 'none';
            UI.qs('#item-detail-overview', modal).textContent = 'Could not fetch details.';
        });
    }

    async function fetchMetadata(jellyfinId, card, modal, title, year, forceSeries) {
        try {
            let { tmdbId, imdbId, itemType } = UI.parseJellyfinId(jellyfinId, card);
            if (forceSeries && itemType === 'movie') {
                itemType = 'series';
            }
            const tmdbData = await UI.getTMDBData(tmdbId, imdbId, itemType, title, year);

            if (!tmdbData) {
                UI.qs('#item-detail-info', modal).innerHTML = 'Could not find metadata.';
                return;
            }

            const displayTitle = tmdbData.title || tmdbData.name;
            if (displayTitle) UI.qs('#item-detail-title', modal).textContent = displayTitle;
            if (tmdbData.overview) UI.qs('#item-detail-overview', modal).textContent = tmdbData.overview;
            if (tmdbData.poster_path) UI.setBackgroundImage(UI.qs('#item-detail-image', modal), 'https://image.tmdb.org/t/p/w500' + tmdbData.poster_path);

            // Detect actual type from TMDB response
            const actualType = (tmdbData.name && tmdbData.number_of_seasons) ? 'series' : 'movie';
            
            // Get IDs from TMDB response
            const tmdbIdFromResponse = tmdbData.id;
            let imdbIdFromResponse = imdbId || tmdbData.imdb_id;
            
            // If we don't have IMDB ID yet, try to fetch external IDs
            if (!imdbIdFromResponse && tmdbIdFromResponse) {
                const externalIdsUrl = `https://api.themoviedb.org/3/${actualType === 'series' ? 'tv' : 'movie'}/${tmdbIdFromResponse}/external_ids?api_key=53e8a159d4635813b94f8c5876c604be`;
                const externalRes = await fetch(externalIdsUrl);
                if (externalRes.ok) {
                    const externalData = await externalRes.json();
                    imdbIdFromResponse = externalData.imdb_id;
                }
            }
            
            const { credits, reviews } = await UI.fetchTMDBCreditsAndReviews(actualType === 'series' ? 'tv' : 'movie', tmdbData.id);
            if (credits) populateCredits(modal, tmdbData, credits);
            if (reviews.length > 0) populateReviews(modal, reviews);
            
            if (window.LibraryStatus && window.LibraryStatus.check) {
                // First check if item is already requested
                const existingRequest = await window.LibraryStatus.checkRequest(imdbIdFromResponse, tmdbIdFromResponse, actualType);
                
                if (existingRequest) {
                    // Item is requested - switch modal to request mode
                    console.log('[DetailsModal] Item found in requests, switching to request mode');
                    
                    // Get current username to determine if it's own request
                    let currentUsername = 'Unknown';
                    try {
                        const userId = window.ApiClient.getCurrentUserId();
                        const user = await window.ApiClient.getUser(userId);
                        currentUsername = user?.Name || 'Unknown';
                    } catch (err) {
                        console.error('[DetailsModal] Error getting username:', err);
                    }
                    
                    // Get admin status
                    let isAdmin = false;
                    try {
                        const userId = window.ApiClient.getCurrentUserId();
                        const user = await window.ApiClient.getUser(userId);
                        isAdmin = user?.Policy?.IsAdministrator || false;
                    } catch (err) {
                        console.error('[DetailsModal] Error checking admin:', err);
                    }
                    
                    const isOwnRequest = existingRequest.username === currentUsername;
                    
                    // Store request data on modal
                    modal.dataset.requestId = existingRequest.id;
                    modal.dataset.isRequestMode = 'true';
                    
                    // Show requester badge on poster
                    const requesterEl = UI.qs('#item-detail-requester', modal);
                    if (requesterEl) {
                        requesterEl.textContent = existingRequest.username;
                        requesterEl.style.display = 'block';
                    }
                    
                    // Handle buttons based on request status
                    const importBtn = UI.qs('#item-detail-import', modal);
                    const requestBtn = UI.qs('#item-detail-request', modal);
                    const openBtn = UI.qs('#item-detail-open', modal);
                    const approveBtn = UI.qs('#item-detail-approve', modal);
                    const removeBtn = UI.qs('#item-detail-remove', modal);
                    
                    // Hide Import/Request buttons
                    if (importBtn) importBtn.style.display = 'none';
                    if (requestBtn) requestBtn.style.display = 'none';
                    
                    if (isAdmin) {
                        // Admin behavior
                        if (existingRequest.status === 'pending') {
                            if (approveBtn) approveBtn.style.display = 'block';
                            if (removeBtn) removeBtn.style.display = 'block';
                            if (openBtn) openBtn.style.display = 'none';
                        } else {
                            // Approved
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (removeBtn) removeBtn.style.display = 'block';
                            if (openBtn) openBtn.style.display = 'none';
                        }
                    } else {
                        // Non-admin behavior
                        if (existingRequest.status === 'pending') {
                            if (isOwnRequest) {
                                if (removeBtn) {
                                    removeBtn.style.display = 'block';
                                    removeBtn.textContent = 'Cancel';
                                }
                                if (approveBtn) approveBtn.style.display = 'none';
                                if (openBtn) openBtn.style.display = 'none';
                            } else {
                                // Others' pending: no buttons
                                if (approveBtn) approveBtn.style.display = 'none';
                                if (removeBtn) removeBtn.style.display = 'none';
                                if (openBtn) openBtn.style.display = 'none';
                            }
                        } else {
                            // Approved request
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
                    
                    // Continue to hide loading and show modal
                    hideLoading(modal);
                    return;
                }
                
                // Not requested - show normal library status buttons
                const inLibrary = await window.LibraryStatus.check(imdbIdFromResponse, tmdbIdFromResponse, actualType);
                const importBtn = UI.qs('#item-detail-import', modal);
                const requestBtn = UI.qs('#item-detail-request', modal);
                const openBtn = UI.qs('#item-detail-open', modal);
                
                // Hide request-specific buttons
                const approveBtn = UI.qs('#item-detail-approve', modal);
                const removeBtn = UI.qs('#item-detail-remove', modal);
                const requesterEl = UI.qs('#item-detail-requester', modal);
                if (approveBtn) approveBtn.style.display = 'none';
                if (removeBtn) removeBtn.style.display = 'none';
                if (requesterEl) requesterEl.style.display = 'none';
                
                await switchButton(importBtn, requestBtn, openBtn, inLibrary);
                modal.dataset.imdbId = imdbIdFromResponse;
                modal.dataset.tmdbId = tmdbIdFromResponse;
                modal.dataset.itemType = actualType;
            }
            
            hideLoading(modal);

        } catch (err) {
            UI.qs('#item-detail-info', modal).innerHTML = '<div style="color:#ff6b6b;">Error fetching.</div>';
            hideLoading(modal);
        }
    }

    function populateCredits(modal, data, credits) {
        let html = '';
        const genreStr = UI.formatGenres(data.genres, data.genre_ids);
        if (genreStr) html += '<div><strong style="color:#1e90ff;">Genre:</strong> ' + genreStr + '</div>';
        if (data.vote_average) html += '<div><strong style="color:#1e90ff;">Rating:</strong> ' + UI.formatRating(data.vote_average) + '</div>';
        if (data.runtime) html += '<div><strong style="color:#1e90ff;">Runtime:</strong> ' + UI.formatRuntime(data.runtime) + '</div>';

        if (credits.crew) {
            const directors = credits.crew.filter(c => c.job === 'Director');
            if (directors.length) html += '<div style="margin-top:12px;"><strong style="color:#1e90ff;">Director:</strong> ' + directors.map(d => d.name).join(', ') + '</div>';
        }

        if (credits.cast && credits.cast.length) {
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

        UI.qs('#item-detail-info', modal).innerHTML = html;
    }

    function populateReviews(modal, reviews) {
        if (!reviews.length) return;
        const reviewsDiv = UI.qs('#item-detail-reviews', modal);
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

        const carousel = UI.qs('.reviews-carousel', reviewsDiv);
        const prevBtn = UI.qs('.carousel-prev', reviewsDiv);
        const nextBtn = UI.qs('.carousel-next', reviewsDiv);
        let idx = 0;

        for (let i = 0; i < reviews.length; i++) {
            const dot = document.createElement('button');
            dot.style.cssText = 'width:10px;height:10px;border-radius:50%;background:' + (i === 0 ? '#1e90ff' : '#555') + ';border:none;cursor:pointer;padding:0;';
            dot.addEventListener('click', () => { idx = i; update(); });
            UI.qs('.carousel-dots', reviewsDiv).appendChild(dot);
        }

        function update() {
            const firstCard = UI.qs('.review-card', carousel);
            if (firstCard) {
                const cardWidth = firstCard.offsetWidth;
                const gap = 15;
                const offset = idx * (cardWidth + gap);
                carousel.style.transform = 'translateX(-' + offset + 'px)';
            }
            UI.qsa('button', UI.qs('.carousel-dots', reviewsDiv)).forEach((d, i) => d.style.background = i === idx ? '#1e90ff' : '#555');
        }

        prevBtn.addEventListener('click', () => { if (idx > 0) { idx--; update(); } });
        nextBtn.addEventListener('click', () => { if (idx < reviews.length - 1) { idx++; update(); } });
        update();

        UI.qsa('.review-card', reviewsDiv).forEach((card, i) => {
            if (reviews[i] && reviews[i].content.length > 200) {
                card.addEventListener('click', () => {
                    const popup = UI.qs('#review-popup');
                    UI.qs('#review-popup-content', popup).innerHTML = '<h3 style="color:#fff;margin-bottom:15px;">Review by ' + reviews[i].author + '</h3>' + reviews[i].content;
                    popup.style.display = 'block';
                });
            }
        });
    }

    document.addEventListener('click', ev => {
        try {
            if (!window.location.hash.includes('#/search')) return;
            const anchor = ev.target.closest('a[href*="#/details"]');
            if (!anchor || ev.button !== 0 || ev.ctrlKey) return;
            ev.preventDefault();
            ev.stopPropagation();

            let id = anchor.dataset.id;
            if (!id && anchor.href.includes('?')) {
                try { id = new URLSearchParams(anchor.href.split('?')[1]).get('id'); } catch (e) {}
            }
            if (!id) return;

            const modal = getModal();
            populateFromCard(anchor, id, modal);
            showModal(modal);
        } catch (e) { console.error('[DetailsModal]', e); }
    }, true);

    // Listen for request card clicks
    document.addEventListener('openDetailsModal', async (ev) => {
        try {
            const { item, isRequestMode, requestId, requestUsername, isAdmin } = ev.detail || {};
            if (!item) return;
            
            console.log('[DetailsModal] Opening modal for request:', requestId, 'by', requestUsername);
            
            const modal = getModal();
            const titleEl = UI.qs('#item-detail-title', modal);
            const metaEl = UI.qs('#item-detail-meta', modal);
            const imageEl = UI.qs('#item-detail-image', modal);
            const overviewEl = UI.qs('#item-detail-overview', modal);
            const importBtn = UI.qs('#item-detail-import', modal);
            const requestBtn = UI.qs('#item-detail-request', modal);
            const openBtn = UI.qs('#item-detail-open', modal);
            const loadingEl = UI.qs('#item-detail-loading-overlay', modal);
            
            // Show loading
            if (loadingEl) loadingEl.style.display = 'flex';
            
            // Set basic info
            if (titleEl) titleEl.textContent = item.Name || 'Loading...';
            if (metaEl) metaEl.textContent = item.ProductionYear || '';
            if (overviewEl) overviewEl.textContent = '';
            
            // Store data on modal
            modal.dataset.tmdbId = item.tmdbId;
            modal.dataset.imdbId = item.imdbId;
            modal.dataset.itemType = item.itemType;
            modal.dataset.requestId = requestId;
            modal.dataset.isRequestMode = isRequestMode;
            
            // Fetch full metadata from TMDB
            const tmdbData = await UI.getTMDBData(
                item.tmdbId,
                item.imdbId,
                item.itemType,
                item.Name,
                item.ProductionYear
            );
            
            if (tmdbData) {
                // Update with full data
                const displayTitle = tmdbData.title || tmdbData.name;
                if (displayTitle && titleEl) {
                    titleEl.textContent = displayTitle;
                }
                
                // Show requester badge on poster (left side, centered at top)
                const requesterEl = UI.qs('#item-detail-requester', modal);
                if (requestUsername && requesterEl) {
                    requesterEl.textContent = requestUsername;
                    requesterEl.style.display = 'block';
                } else if (requesterEl) {
                    requesterEl.style.display = 'none';
                }
                
                if (tmdbData.overview && overviewEl) overviewEl.textContent = tmdbData.overview;
                
                if (tmdbData.poster_path && imageEl) {
                    UI.setBackgroundImage(imageEl, 'https://image.tmdb.org/t/p/w500' + tmdbData.poster_path);
                }
                
                // Fetch credits and reviews (same as regular search results)
                const { credits, reviews } = await UI.fetchTMDBCreditsAndReviews(
                    item.itemType === 'series' ? 'tv' : 'movie',
                    tmdbData.id
                );
                
                // Populate credits with images (same function used for search results)
                if (credits) {
                    populateCredits(modal, tmdbData, credits);
                }
                
                // Populate reviews carousel (same function used for search results)
                if (reviews && reviews.length > 0) {
                    populateReviews(modal, reviews);
                }
            }
            
            // Show appropriate buttons based on request mode
            const approveBtn = UI.qs('#item-detail-approve', modal);
            const removeBtn = UI.qs('#item-detail-remove', modal);
            
            if (isRequestMode) {
                const { requestStatus, isOwnRequest } = ev.detail || {};
                
                // Hide Import/Request buttons in request mode
                if (importBtn) importBtn.style.display = 'none';
                if (requestBtn) requestBtn.style.display = 'none';
                
                if (isAdmin) {
                    // Admin behavior
                    if (requestStatus === 'pending') {
                        // Pending: Show Approve + Remove
                        if (approveBtn) approveBtn.style.display = 'block';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    } else {
                        // Approved: Show Remove only
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (removeBtn) removeBtn.style.display = 'block';
                        if (openBtn) openBtn.style.display = 'none';
                    }
                } else {
                    // Non-admin behavior
                    if (requestStatus === 'pending') {
                        if (isOwnRequest) {
                            // Own pending request: Show Remove (as Cancel)
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Cancel';
                            }
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        } else {
                            // Others' pending request: No buttons
                            if (approveBtn) approveBtn.style.display = 'none';
                            if (removeBtn) removeBtn.style.display = 'none';
                            if (openBtn) openBtn.style.display = 'none';
                        }
                    } else {
                        // Approved request
                        if (openBtn) openBtn.style.display = 'block';
                        if (approveBtn) approveBtn.style.display = 'none';
                        if (isOwnRequest) {
                            // Own approved: Show Remove too
                            if (removeBtn) {
                                removeBtn.style.display = 'block';
                                removeBtn.textContent = 'Remove';
                            }
                        } else {
                            // Others' approved: No Remove
                            if (removeBtn) removeBtn.style.display = 'none';
                        }
                    }
                }
            } else {
                // Regular search mode - this should never happen now since we redirect in fetchMetadata
                // But keep as fallback
                if (approveBtn) approveBtn.style.display = 'none';
                if (removeBtn) removeBtn.style.display = 'none';
                
                const requesterEl = UI.qs('#item-detail-requester', modal);
                if (requesterEl) requesterEl.style.display = 'none';
            }
            
            // Hide loading
            if (loadingEl) loadingEl.style.display = 'none';
            
            showModal(modal);
        } catch (e) { 
            console.error('[DetailsModal] Error opening request modal:', e);
            const loadingEl = UI.qs('#item-detail-loading-overlay', getModal());
            if (loadingEl) loadingEl.style.display = 'none';
        }
    });

    window.addEventListener('hashchange', hideModal);
    window.addEventListener('popstate', hideModal);
    document.addEventListener('visibilitychange', () => { if (document.hidden) hideModal(); });
    }
    
    // Start initialization with retry logic
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

