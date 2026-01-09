/**
 * Reviews Carousel - Standalone
 * Adds a TMDB reviews carousel above "Cast & Crew" section on item details pages
 * Works with both card mode and select mode
 */
(function () {
    'use strict';

    // Prevent double initialization
    if (window._reviewsCarouselInitialized) {
        console.log('[ReviewsCarousel] Already initialized, skipping');
        return;
    }
    window._reviewsCarouselInitialized = true;

    console.log('[ReviewsCarousel] Loading...');

    let currentItemId = null;

    async function fetchTMDBReviews(itemId) {
        try {
            const item = await window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), itemId);
            if (!item) {
                console.warn('[ReviewsCarousel] Could not fetch item');
                return null;
            }

            const tmdbId = item.ProviderIds?.Tmdb;
            const imdbId = item.ProviderIds?.Imdb;
            const itemType = item.Type;

            if (!tmdbId && !imdbId) {
                console.log('[ReviewsCarousel] No TMDB/IMDB ID found');
                return null;
            }

            console.log('[ReviewsCarousel] Fetching reviews for itemType:', itemType, 'tmdbId:', tmdbId, 'imdbId:', imdbId);

            // Use new ReviewsController endpoint
            const url = window.ApiClient.getUrl(`api/baklava/reviews/${itemId}`);
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            const reviews = response?.results || [];
            console.log('[ReviewsCarousel] Found', reviews.length, 'reviews');
            return reviews;
        } catch (err) {
            console.error('[ReviewsCarousel] Error fetching reviews:', err);
            return null;
        }
    }

    function createReviewsCarousel(reviews) {
        if (!reviews || reviews.length === 0) {
            console.log('[ReviewsCarousel] No reviews to display');
            return;
        }

        const castCollapsible = document.querySelector('#castCollapsible');
        if (!castCollapsible) {
            console.warn('[ReviewsCarousel] Could not find #castCollapsible');
            return;
        }

        // Check if already created
        if (document.querySelector('.baklava-reviews-carousel')) {
            console.log('[ReviewsCarousel] Carousel already exists');
            return;
        }

        console.log('[ReviewsCarousel] Creating carousel with', reviews.length, 'reviews');

        // Add responsive styles PROPERLY
        if (!document.getElementById('baklava-reviews-responsive-style')) {
            const style = document.createElement('style');
            style.id = 'baklava-reviews-responsive-style';
            style.textContent = `
                .baklava-reviews-carousel {
                    padding: 1.5rem 0;
                }
                
                .baklava-review-card {
                    min-height: 200px;
                    max-height: 300px;
                    overflow-y: auto;
                    flex: 0 0 auto;
                    width: 320px; /* default desktop card width */
                }
                
                /* Tablet and smaller */
                @media (max-width: 1024px) {
                    .baklava-reviews-carousel {
                    }
                    .baklava-review-card { width: 300px; }
                }
                
                /* Tablet */
                @media (max-width: 768px) {
                    .baklava-reviews-carousel {
                    }
                    .baklava-review-card {
                        min-height: 180px;
                        max-height: 250px;
                        width: 260px;
                    }
                }
                
                /* Mobile */
                @media (max-width: 480px) {
                    .baklava-reviews-carousel {
                    }
                    .baklava-review-card {
                        min-height: 150px;
                        max-height: 200px;
                        width: 200px;
                    }
                }
            `;
            document.head.appendChild(style);
        }

        // Create wrapper
        const wrapper = document.createElement('div');
        wrapper.className = 'baklava-reviews-carousel';

        // Title and navigation row
        const headerRow = document.createElement('div');
        headerRow.style.cssText = 'display: flex; justify-content: space-between; align-items: center; margin-bottom: 1rem;';

        const title = document.createElement('div');
        title.style.cssText = 'font-size: 1.3em; font-weight: 500; color: rgba(255,255,255,0.9);';
        title.textContent = 'Reviews';

        const navButtons = document.createElement('div');
        navButtons.style.cssText = 'display: flex; gap: 10px;';

        const prevBtn = document.createElement('button');
        prevBtn.className = 'baklava-review-prev';
        prevBtn.style.cssText = 'background: rgba(128,128,128,0.5); border: none; color: #fff; width: 36px; height: 36px; border-radius: 50%; cursor: pointer; font-size: 20px; display: flex; align-items: center; justify-content: center;';
        prevBtn.textContent = '‹';

        const nextBtn = document.createElement('button');
        nextBtn.className = 'baklava-review-next';
        nextBtn.style.cssText = prevBtn.style.cssText;
        nextBtn.textContent = '›';

        navButtons.appendChild(prevBtn);
        navButtons.appendChild(nextBtn);
        headerRow.appendChild(title);
        headerRow.appendChild(navButtons);
        wrapper.appendChild(headerRow);

        // Track container
        const trackContainer = document.createElement('div');
        trackContainer.style.cssText = 'position: relative; overflow-x: auto; overflow-y: hidden; width: 100%; scrollbar-width: none; -webkit-overflow-scrolling: touch;';
        trackContainer.setAttribute('style', trackContainer.getAttribute('style') + ' -ms-overflow-style: none;');

        const track = document.createElement('div');
        track.className = 'baklava-review-track';
        track.style.cssText = 'display: flex; gap: 15px; transition: transform 0.3s ease;';

        // Create review cards with FIXED dimensions and click for modal
        reviews.slice(0, 20).forEach((review, idx) => {
            const author = review.author || 'Anonymous';
            const content = review.content || '';
            const text = content.length > 200 ? content.substring(0, 200) + '...' : content;

            const card = document.createElement('div');
            card.className = 'baklava-review-card';
            card.style.cssText = `
                /* width is controlled by responsive CSS rules injected above */
                background: rgba(255,255,255,0.05);
                padding: 1.5rem;
                border-radius: 6px;
                border: 1px solid rgba(255,255,255,0.1);
                cursor: pointer;
                min-width: 0;
                transition: background 0.2s ease;
            `;

            card.addEventListener('mouseenter', () => {
                card.style.background = 'rgba(255,255,255,0.08)';
            });
            card.addEventListener('mouseleave', () => {
                card.style.background = 'rgba(255,255,255,0.05)';
            });

            const authorDiv = document.createElement('div');
            authorDiv.style.cssText = 'font-weight: bold; color: #fff; margin-bottom: 0.75rem; font-size: 1.1em;';
            authorDiv.textContent = author;

            const contentDiv = document.createElement('div');
            contentDiv.style.cssText = 'color: #ccc; font-size: 14px; line-height: 1.6; word-wrap: break-word; overflow-y: auto;';
            contentDiv.textContent = text;

            card.appendChild(authorDiv);
            card.appendChild(contentDiv);

            // Click to open modal with full review
            card.addEventListener('click', () => {
                showReviewModal(author, content);
            });

            track.appendChild(card);
        });

        trackContainer.appendChild(track);
        wrapper.appendChild(trackContainer);

        // Dots indicator
        const dotsContainer = document.createElement('div');
        dotsContainer.className = 'baklava-review-dots';
        dotsContainer.style.cssText = 'display: flex; justify-content: center; gap: 8px; margin-top: 1rem;';

        for (let i = 0; i < reviews.length; i++) {
            const dot = document.createElement('button');
            dot.style.cssText = `width: 10px; height: 10px; border-radius: 50%; background: ${i === 0 ? '#1e90ff' : '#555'}; border: none; cursor: pointer; padding: 0;`;
            dot.addEventListener('click', () => {
                const firstCard = track.querySelector('.baklava-review-card');
                const gap = 15;
                const cardWidth = firstCard ? Math.round(firstCard.getBoundingClientRect().width) : 0;
                const maxIdx = getMaxIndex(cardWidth, gap);
                currentIdx = Math.min(i, maxIdx);
                updateCarousel();
            });
            dotsContainer.appendChild(dot);
        }

        wrapper.appendChild(dotsContainer);
        
        // Insert above cast/crew section (before it, so it appears higher on the page)
        castCollapsible.parentNode.insertBefore(wrapper, castCollapsible);

        // Carousel navigation logic
        let currentIdx = 0;

        function getMaxIndex(cardWidth, gap) {
            const containerWidth = trackContainer.clientWidth || trackContainer.getBoundingClientRect().width;
            if (!cardWidth || cardWidth <= 0) return Math.max(0, reviews.length - 1);
            const visibleCount = Math.max(1, Math.floor(containerWidth / (cardWidth + gap)));
            return Math.max(0, reviews.length - visibleCount);
        }

        function updateCarousel() {
            // Compute pixel offset based on first card width + gap
            const firstCard = track.querySelector('.baklava-review-card');
            if (!firstCard) return;

            const gap = 15; // must match the track gap in px
            const cardRect = firstCard.getBoundingClientRect();
            const cardWidth = Math.round(cardRect.width);
            // Clamp currentIdx so we don't scroll past available cards
            const maxIdx = getMaxIndex(cardWidth, gap);
            if (currentIdx > maxIdx) currentIdx = maxIdx;

            const offsetPx = currentIdx * (cardWidth + gap);

            track.style.transform = `translateX(-${offsetPx}px)`;

            Array.from(dotsContainer.children).forEach((d, i) => {
                d.style.background = i === currentIdx ? '#1e90ff' : '#555';
            });
        }

        prevBtn.addEventListener('click', () => {
            if (currentIdx > 0) {
                currentIdx--;
                updateCarousel();
            }
        });

        nextBtn.addEventListener('click', () => {
            const firstCard = track.querySelector('.baklava-review-card');
            if (!firstCard) return;
            const gap = 15;
            const cardWidth = Math.round(firstCard.getBoundingClientRect().width);
            const maxIdx = getMaxIndex(cardWidth, gap);
            if (currentIdx < maxIdx) {
                currentIdx++;
                updateCarousel();
            }
        });

        console.log('[ReviewsCarousel] Carousel created successfully');
    }

    // Modal for full review (like details modal)
    function showReviewModal(author, content) {
        // Remove existing modal if any
        const existing = document.getElementById('baklava-review-modal');
        if (existing) existing.remove();

        const modal = document.createElement('div');
        modal.id = 'baklava-review-modal';
        modal.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(0,0,0,0.85);
            z-index: 9999;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 2rem;
        `;

        const modalContent = document.createElement('div');
        modalContent.style.cssText = `
            background: rgba(30,30,30,0.98);
            border-radius: 8px;
            padding: 2rem;
            max-width: 800px;
            width: 100%;
            max-height: 80vh;
            overflow-y: auto;
            position: relative;
            border: 1px solid rgba(255,255,255,0.15);
        `;

        const closeBtn = document.createElement('button');
        closeBtn.textContent = '×';
        closeBtn.style.cssText = `
            position: absolute;
            top: 1rem;
            right: 1rem;
            background: none;
            border: none;
            color: #fff;
            font-size: 2rem;
            cursor: pointer;
            width: 40px;
            height: 40px;
            display: flex;
            align-items: center;
            justify-content: center;
            border-radius: 50%;
            transition: background 0.2s ease;
        `;
        closeBtn.addEventListener('mouseenter', () => {
            closeBtn.style.background = 'rgba(255,255,255,0.1)';
        });
        closeBtn.addEventListener('mouseleave', () => {
            closeBtn.style.background = 'none';
        });
        closeBtn.addEventListener('click', () => {
            modal.remove();
        });

        const authorDiv = document.createElement('div');
        authorDiv.style.cssText = 'font-size: 1.5em; font-weight: bold; color: #fff; margin-bottom: 1.5rem; padding-right: 3rem;';
        authorDiv.textContent = author;

        const contentDiv = document.createElement('div');
        contentDiv.style.cssText = 'color: rgba(255,255,255,0.85); font-size: 15px; line-height: 1.8; white-space: pre-wrap; word-wrap: break-word;';
        contentDiv.textContent = content;

        modalContent.appendChild(closeBtn);
        modalContent.appendChild(authorDiv);
        modalContent.appendChild(contentDiv);
        modal.appendChild(modalContent);
        document.body.appendChild(modal);

        // Close on background click
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                modal.remove();
            }
        });

        // Close on ESC key
        const escHandler = (e) => {
            if (e.key === 'Escape') {
                modal.remove();
                document.removeEventListener('keydown', escHandler);
            }
        };
        document.addEventListener('keydown', escHandler);
    }

    function escapeHtml(text) {
        const popup = document.createElement('div');
        popup.className = 'baklava-review-popup';
        popup.style.cssText = 'position: fixed; top: 0; left: 0; width: 100%; height: 100%; background: rgba(0,0,0,0.9); z-index: 10000; display: flex; align-items: center; justify-content: center;';
        popup.innerHTML = '<div style="max-width: 800px; width: 90%; max-height: 80%; background: #1a1a1a; border-radius: 8px; padding: 2rem; overflow-y: auto;"><div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 1rem;"><h3 style="margin: 0; color: #fff;">Review by ' + escapeHtml(review.author) + '</h3><button style="background: #555; border: none; color: #fff; padding: 8px 12px; cursor: pointer; border-radius: 4px; font-size: 16px;">✕</button></div><div style="color: #ccc; line-height: 1.6;">' + escapeHtml(review.content) + '</div></div>';
        
        document.body.appendChild(popup);
        
        popup.querySelector('button').addEventListener('click', () => popup.remove());
        popup.addEventListener('click', (e) => { 
            if (e.target === popup) popup.remove(); 
        });
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function captureItemId() {
        // Try URL params
        const urlParams = new URLSearchParams(window.location.search);
        const urlId = urlParams.get('id');
        if (urlId) {
            currentItemId = urlId;
            console.log('[ReviewsCarousel] Captured itemId from URL params:', currentItemId);
            return urlId;
        }

        // Try URL hash
        const urlMatch = window.location.href.match(/[?&]id=([a-f0-9-]+)/i);
        if (urlMatch) {
            currentItemId = urlMatch[1].replace(/-/g, '');
            console.log('[ReviewsCarousel] Captured itemId from URL match:', currentItemId);
            return currentItemId;
        }

        console.warn('[ReviewsCarousel] No itemId found');
        return null;
    }

    async function initReviewsCarousel() {
        // Check server config first
        try {
            const configUrl = window.ApiClient.getUrl('api/baklava/config');
            const configResponse = await window.ApiClient.ajax({
                type: 'GET',
                url: configUrl,
                dataType: 'json'
            });
            
            if (configResponse?.showReviewsCarousel === false) {
                console.log('[ReviewsCarousel] Reviews disabled in server config');
                return;
            }
        } catch (err) {
            console.warn('[ReviewsCarousel] Could not load config, assuming enabled:', err);
        }

        // Check if reviews are disabled in localStorage (client-side toggle)
        const localToggle = localStorage.getItem('baklava_show_reviews');
        if (localToggle === 'false') {
            console.log('[ReviewsCarousel] Reviews disabled via localStorage');
            return;
        }

        if (!captureItemId()) {
            console.log('[ReviewsCarousel] Could not capture itemId, will retry on navigation');
            return;
        }

        if (!currentItemId) {
            console.log('[ReviewsCarousel] No itemId available');
            return;
        }

        const reviews = await fetchTMDBReviews(currentItemId);
        if (reviews && reviews.length > 0) {
            createReviewsCarousel(reviews);
        }
    }

    // Check if we're on an item details page
    function isDetailsPage() {
        const hash = window.location.hash;
        return hash.includes('/details?') || 
               hash.includes('#!/item/item.html?') ||
               hash.includes('/item?id=');
    }

    function start() {
        console.log('[ReviewsCarousel] Starting...');

        // Try to initialize immediately if on details page
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            if (isDetailsPage()) {
                setTimeout(initReviewsCarousel, 500);
            }
        }

        // Also watch for DOM changes and page navigation - BUT ONLY ON DETAILS PAGES
        let isProcessing = false;
        let lastItemId = null; // Track last processed item to avoid duplicate work
        const observer = new MutationObserver(() => {
            if (isProcessing) return;
            if (!isDetailsPage()) return; // ONLY run on details pages
            
            const castCollapsible = document.querySelector('#castCollapsible');
            const currentItemId = captureItemId();
            
            // Only process if: castCollapsible exists, no carousel yet, and we haven't processed this item
            if (castCollapsible && !document.querySelector('.baklava-reviews-carousel') && currentItemId && currentItemId !== lastItemId) {
                isProcessing = true;
                lastItemId = currentItemId;
                setTimeout(() => {
                    initReviewsCarousel();
                    isProcessing = false;
                }, 100);
            }
        });

        observer.observe(document.body, { 
            childList: true, 
            subtree: true 
        });

        // Listen for hash changes (Jellyfin navigation) - ONLY trigger on details pages
        let hashChangeTimeout = null;
        window.addEventListener('hashchange', () => {
            if (!isDetailsPage()) {
                console.log('[ReviewsCarousel] Not a details page, ignoring');
                lastItemId = null; // Reset when leaving details page
                return;
            }
            console.log('[ReviewsCarousel] Hash changed to details page, will check for reviews');
            lastItemId = null; // Reset for new page
            if (hashChangeTimeout) clearTimeout(hashChangeTimeout);
            hashChangeTimeout = setTimeout(() => {
                const castCollapsible = document.querySelector('#castCollapsible');
                if (castCollapsible && !document.querySelector('.baklava-reviews-carousel')) {
                    initReviewsCarousel();
                }
            }, 1000);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
