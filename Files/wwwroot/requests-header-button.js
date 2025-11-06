(function() {
    'use strict';
    
    console.log('[RequestsHeaderButton] Initializing...');
    
    let dropdownMenu = null;
    let backdrop = null;
    let isAdmin = false;
    let currentUsername = '';
    
    // Add button to header
    function addRequestsButton() {
        const headerRight = document.querySelector('.headerRight');
        if (!headerRight) {
            console.log('[RequestsHeaderButton] Header not found, retrying...');
            setTimeout(addRequestsButton, 500);
            return;
        }
        
        // Check if button already exists
        if (document.querySelector('.headerRequestsButton')) {
            return;
        }
        
        // Create button
        const btn = document.createElement('button');
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'headerButton headerButtonRight headerRequestsButton paper-icon-button-light';
        btn.title = 'Media Requests';
        btn.innerHTML = '<span class="material-icons list_alt" aria-hidden="true"></span>';
        
        // Insert before user button
        const userButton = headerRight.querySelector('.headerUserButton');
        if (userButton) {
            headerRight.insertBefore(btn, userButton);
        } else {
            headerRight.appendChild(btn);
        }
        
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            toggleDropdown(btn);
        });
        
        console.log('[RequestsHeaderButton] Button added to header');
    }
    
    // Create backdrop overlay
    function createBackdrop() {
        if (backdrop) return backdrop;
        
        backdrop = document.createElement('div');
        backdrop.className = 'requests-backdrop';
        backdrop.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.7);
            z-index: 9999;
            display: none;
        `;
        
        backdrop.addEventListener('click', () => {
            hideDropdown();
        });
        
        document.body.appendChild(backdrop);
        return backdrop;
    }
    
    // Create dropdown menu
    function createDropdown() {
        if (dropdownMenu) return dropdownMenu;
        
        dropdownMenu = document.createElement('div');
        dropdownMenu.className = 'requests-dropdown';
        dropdownMenu.style.cssText = `
            position: fixed;
            top: 60px;
            right: 20px;
            left: 20px;
            max-width: 1400px;
            margin: 0 auto;
            max-height: 70vh;
            background: #181818;
            border: 1px solid #333;
            border-radius: 8px;
            box-shadow: 0 4px 16px rgba(0,0,0,0.5);
            z-index: 10000;
            display: none;
            overflow: hidden;
            padding: 20px;
        `;
        
        dropdownMenu.innerHTML = `
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; border-bottom: 2px solid #333; padding-bottom: 10px;">
                <h2 style="margin: 0; color: #fff;">Media Requests</h2>
                <button class="close-dropdown" style="background: #555; border: none; color: #fff; padding: 8px 16px; border-radius: 4px; cursor: pointer;">Close</button>
            </div>
            <div style="overflow-y: auto; max-height: calc(70vh - 80px); padding-right: 10px;">
                <div class="requests-movies-section" style="margin-bottom: 30px;">
                    <h3 style="color: #aaa; font-size: 16px; margin-bottom: 15px;">Movies</h3>
                    <div class="requests-movies-carousel" style="position: relative;">
                        <button class="carousel-prev movies-prev" style="position: absolute; left: 0; top: 50%; transform: translateY(-50%); z-index: 10; background: rgba(0,0,0,0.7); border: none; color: #fff; width: 40px; height: 40px; border-radius: 50%; cursor: pointer; display: none;">
                            <span class="material-icons" style="font-size: 24px;">chevron_left</span>
                        </button>
                        <div class="requests-movies-grid" style="display: flex; gap: 15px; overflow-x: auto; scroll-behavior: smooth; padding: 10px 0; scrollbar-width: none; -ms-overflow-style: none;">
                        </div>
                        <button class="carousel-next movies-next" style="position: absolute; right: 0; top: 50%; transform: translateY(-50%); z-index: 10; background: rgba(0,0,0,0.7); border: none; color: #fff; width: 40px; height: 40px; border-radius: 50%; cursor: pointer; display: none;">
                            <span class="material-icons" style="font-size: 24px;">chevron_right</span>
                        </button>
                    </div>
                </div>
                <div class="requests-series-section">
                    <h3 style="color: #aaa; font-size: 16px; margin-bottom: 15px;">Series</h3>
                    <div class="requests-series-carousel" style="position: relative;">
                        <button class="carousel-prev series-prev" style="position: absolute; left: 0; top: 50%; transform: translateY(-50%); z-index: 10; background: rgba(0,0,0,0.7); border: none; color: #fff; width: 40px; height: 40px; border-radius: 50%; cursor: pointer; display: none;">
                            <span class="material-icons" style="font-size: 24px;">chevron_left</span>
                        </button>
                        <div class="requests-series-grid" style="display: flex; gap: 15px; overflow-x: auto; scroll-behavior: smooth; padding: 10px 0; scrollbar-width: none; -ms-overflow-style: none;">
                        </div>
                        <button class="carousel-next series-next" style="position: absolute; right: 0; top: 50%; transform: translateY(-50%); z-index: 10; background: rgba(0,0,0,0.7); border: none; color: #fff; width: 40px; height: 40px; border-radius: 50%; cursor: pointer; display: none;">
                            <span class="material-icons" style="font-size: 24px;">chevron_right</span>
                        </button>
                    </div>
                </div>
            </div>
            <style>
                .requests-movies-grid::-webkit-scrollbar,
                .requests-series-grid::-webkit-scrollbar {
                    display: none;
                }
                .carousel-prev:hover,
                .carousel-next:hover {
                    background: rgba(0,0,0,0.9) !important;
                }
                @media (max-width: 768px) {
                    .requests-dropdown {
                        right: 10px !important;
                        left: 10px !important;
                        top: 70px !important;
                        max-height: 60vh !important;
                    }
                }
                @media (max-width: 480px) {
                    .requests-dropdown {
                        right: 5px !important;
                        left: 5px !important;
                        padding: 12px !important;
                        max-height: 50vh !important;
                    }
                }
            </style>
        `;
        
        document.body.appendChild(dropdownMenu);
        
        // Setup carousel navigation
        setupCarousel('.requests-movies-grid', '.movies-prev', '.movies-next');
        setupCarousel('.requests-series-grid', '.series-prev', '.series-next');
        
        // Close button
        dropdownMenu.querySelector('.close-dropdown').addEventListener('click', () => {
            hideDropdown();
        });
        
        // Close when clicking outside (but not when modal is open or was just closed)
        let modalWasOpen = false;
        document.addEventListener('click', (e) => {
            const modalOpen = document.querySelector('.item-detail-modal-overlay.open');
            
            // If modal is currently open, just track it and don't close dropdown
            if (modalOpen) {
                modalWasOpen = true;
                return;
            }
            
            // If modal was just closed in this click, don't close dropdown
            if (modalWasOpen) {
                modalWasOpen = false;
                return;
            }
            
            // Check if clicking on modal overlay specifically
            if (e.target.classList.contains('item-detail-modal-overlay')) {
                return;
            }
            
            if (dropdownMenu.style.display === 'block' && 
                !dropdownMenu.contains(e.target) && 
                !e.target.closest('.headerRequestsButton')) {
                hideDropdown();
            }
        });
        
        return dropdownMenu;
    }
    
    // Setup carousel navigation
    function setupCarousel(gridSelector, prevSelector, nextSelector) {
        const grid = dropdownMenu.querySelector(gridSelector);
        const prevBtn = dropdownMenu.querySelector(prevSelector);
        const nextBtn = dropdownMenu.querySelector(nextSelector);
        
        if (!grid || !prevBtn || !nextBtn) return;
        
        // Show/hide buttons based on scroll position
        const updateButtons = () => {
            const isScrollable = grid.scrollWidth > grid.clientWidth;
            const atStart = grid.scrollLeft <= 0;
            const atEnd = grid.scrollLeft + grid.clientWidth >= grid.scrollWidth - 10;
            
            if (!isScrollable) {
                prevBtn.style.display = 'none';
                nextBtn.style.display = 'none';
            } else {
                prevBtn.style.display = atStart ? 'none' : 'flex';
                nextBtn.style.display = atEnd ? 'none' : 'flex';
            }
        };
        
        // Scroll on button click
        prevBtn.addEventListener('click', () => {
            grid.scrollBy({ left: -300, behavior: 'smooth' });
            setTimeout(updateButtons, 100);
        });
        
        nextBtn.addEventListener('click', () => {
            grid.scrollBy({ left: 300, behavior: 'smooth' });
            setTimeout(updateButtons, 100);
        });
        
        // Update buttons on scroll
        grid.addEventListener('scroll', updateButtons);
        
        // Initial update
        setTimeout(updateButtons, 100);
    }
    
    // Toggle dropdown
    async function toggleDropdown(btn) {
        const menu = createDropdown();
        const bg = createBackdrop();
        
        if (menu.style.display === 'block') {
            hideDropdown();
            return;
        }
        
        // Show backdrop and menu
        bg.style.display = 'block';
        menu.style.display = 'block';
        
        // Load requests
        await loadRequests();
    }
    
    // Hide dropdown
    function hideDropdown() {
        if (dropdownMenu) dropdownMenu.style.display = 'none';
        if (backdrop) backdrop.style.display = 'none';
    }
    
    // Load requests from API
    async function loadRequests() {
        try {
            const userId = window.ApiClient.getCurrentUserId();
            const user = await window.ApiClient.getUser(userId);
            isAdmin = user?.Policy?.IsAdministrator || false;
            currentUsername = user?.Name || 'Unknown';
            
            console.log('[RequestsHeaderButton] Loading requests for user:', currentUsername, 'isAdmin:', isAdmin);
            
            let response = await window.ApiClient.ajax({
                type: 'GET',
                url: window.ApiClient.getUrl('api/baklava/requests'),
                dataType: 'json'
            });
            
            console.log('[RequestsHeaderButton] Raw response:', response);
            console.log('[RequestsHeaderButton] Response type:', typeof response, 'isArray:', Array.isArray(response));
            
            // If response is a Response object, parse it
            if (response && response.constructor && response.constructor.name === 'Response') {
                console.log('[RequestsHeaderButton] Got Response object, parsing JSON...');
                response = await response.json();
                console.log('[RequestsHeaderButton] Parsed response:', response);
            }
            
            // Ensure response is an array
            let requests = [];
            if (Array.isArray(response)) {
                requests = response;
            } else if (response && typeof response === 'object') {
                // If response is wrapped in an object, try to extract array
                requests = response.requests || response.data || [];
            }
            
            console.log('[RequestsHeaderButton] Loaded requests:', requests.length, 'requests:', requests);
            
            const movies = requests.filter(r => r.itemType === 'movie');
            const series = requests.filter(r => r.itemType === 'series');
            
            console.log('[RequestsHeaderButton] Movies:', movies.length, 'Series:', series.length);
            
            renderRequests(movies, '.requests-movies-grid', '.movies-prev', '.movies-next');
            renderRequests(series, '.requests-series-grid', '.series-prev', '.series-next');
            
        } catch (err) {
            console.error('[RequestsHeaderButton] Error loading requests:', err);
        }
    }
    
    // Render requests into grid
    function renderRequests(requests, selector, prevSelector, nextSelector) {
        const grid = dropdownMenu.querySelector(selector);
        if (!grid) return;
        
        grid.innerHTML = '';
        
        if (requests.length === 0) {
            // Create dummy "forbidden" card
            const dummyCard = document.createElement('div');
            dummyCard.style.cssText = `
                position: relative;
                border-radius: 6px;
                overflow: hidden;
                flex-shrink: 0;
            `;
            dummyCard.innerHTML = `
                <div style="width: 140px; height: 210px; background: #1a1a1a; border-radius: 6px; position: relative; display: flex; align-items: center; justify-content: center; border: 2px dashed #333;">
                    <span class="material-icons" style="font-size: 64px; color: #555;">block</span>
                </div>
                <div style="padding: 8px 0; text-align: center;">
                    <div style="font-size: 13px; font-weight: 600; color: #666;">No requests</div>
                </div>
            `;
            grid.appendChild(dummyCard);
        } else {
            requests.forEach(request => {
                const card = createCard(request);
                grid.appendChild(card);
            });
        }
        
        // Update carousel buttons
        setTimeout(() => {
            const prevBtn = dropdownMenu.querySelector(prevSelector);
            const nextBtn = dropdownMenu.querySelector(nextSelector);
            if (prevBtn && nextBtn) {
                const isScrollable = grid.scrollWidth > grid.clientWidth;
                const atStart = grid.scrollLeft <= 0;
                const atEnd = grid.scrollLeft + grid.clientWidth >= grid.scrollWidth - 10;
                
                if (!isScrollable) {
                    prevBtn.style.display = 'none';
                    nextBtn.style.display = 'none';
                } else {
                    prevBtn.style.display = atStart ? 'none' : 'flex';
                    nextBtn.style.display = atEnd ? 'none' : 'flex';
                }
            }
        }, 100);
    }
    
    // Create card element
    function createCard(request) {
        const card = document.createElement('div');
        card.className = 'request-card';
        card.style.cssText = `
            position: relative;
            flex-shrink: 0;
            cursor: pointer;
            border-radius: 6px;
            overflow: hidden;
            transition: transform 0.2s;
        `;
        
        card.addEventListener('mouseenter', () => card.style.transform = 'scale(1.05)');
        card.addEventListener('mouseleave', () => card.style.transform = 'scale(1)');
        
        // Extract poster URL from img field (it's stored as url("..."))
        let posterUrl = '';
        if (request.img) {
            const match = request.img.match(/url\("(.+?)"\)/);
            if (match) {
                posterUrl = match[1];
            }
        }
        
        card.innerHTML = `
            <div style="width: 140px; height: 210px; background: #2a2a2a url('${posterUrl}') center/cover; border-radius: 6px; position: relative;">
                ${isAdmin && request.username ? `
                    <div style="position: absolute; top: 8px; left: 8px; right: 8px; text-align: center; background: rgba(30, 144, 255, 0.9); color: #fff; padding: 4px 8px; border-radius: 4px; font-size: 11px; font-weight: 600;">
                        ${request.username}
                    </div>
                ` : ''}
                <div style="position: absolute; ${isAdmin && request.username ? 'top: 40px;' : 'top: 8px;'} left: 8px; background: ${request.status === 'approved' ? 'rgba(76, 175, 80, 0.9)' : 'rgba(255, 152, 0, 0.9)'}; color: #fff; padding: 4px 8px; border-radius: 4px; font-size: 11px; font-weight: 600;">
                    ${request.status || 'pending'}
                </div>
            </div>
            <div style="padding: 8px 0; text-align: center;">
                <div style="font-size: 13px; font-weight: 600; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">${request.title}</div>
                <div style="font-size: 11px; color: #aaa;">${request.year || ''}</div>
            </div>
        `;
        
        // Click to open modal
        card.addEventListener('click', () => {
            const fakeItem = {
                Id: request.tmdbId || request.imdbId,
                Name: request.title,
                Type: request.itemType === 'series' ? 'Series' : 'Movie',
                ProductionYear: request.year ? parseInt(request.year) : null,
                tmdbId: request.tmdbId,
                imdbId: request.imdbId,
                itemType: request.itemType
            };
            
            const isOwnRequest = request.username === currentUsername;
            
            document.dispatchEvent(new CustomEvent('openDetailsModal', { 
                detail: { 
                    item: fakeItem,
                    isRequestMode: true,
                    requestId: request.id,
                    requestUsername: request.username,
                    requestStatus: request.status || 'pending',
                    isAdmin: isAdmin,
                    isOwnRequest: isOwnRequest
                } 
            }));
            
            // Don't close dropdown - let user interact with modal
            // dropdownMenu.style.display = 'none';
        });
        
        return card;
    }
    
    // Expose reload function
    window.RequestsHeaderButton = {
        reload: loadRequests
    };
    
    // Initialize
    function init() {
        if (window.ApiClient) {
            addRequestsButton();
        } else {
            setTimeout(init, 500);
        }
    }
    
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
    
    console.log('[RequestsHeaderButton] Module loaded');
})();
