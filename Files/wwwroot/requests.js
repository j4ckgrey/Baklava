/**
 * Media Requests - Consolidated
 * Combines: request-manager.js, requests-menu.js, requests-header-button.js
 * Manages user media requests with header button, dropdown menu, and full page views
 */
(function() {
    'use strict';
    

    // ============================================
    // SHARED STATE & CONFIGURATION
    // ============================================
    
    const API_BASE = 'api/baklava/requests';
    let isAdmin = false;
    let currentUsername = '';
    let isLoadingRequests = false;
    
    // UI References
    let dropdownMenu = null;
    let backdrop = null;

    // ============================================
    // UTILITY FUNCTIONS
    // ============================================

    async function checkAdmin() {
        if (!window.ApiClient) {
            console.warn('[Requests] ApiClient not available yet');
            return false;
        }
        
        try {
            const userId = window.ApiClient.getCurrentUserId();
            const user = await window.ApiClient.getUser(userId);
            isAdmin = user?.Policy?.IsAdministrator || false;
            currentUsername = user?.Name || 'Unknown';
            return isAdmin;
        } catch (err) {
            console.error('[Requests] Error checking admin status (trying fallback):', err);
            // Try getCurrentUser as fallback
            try {
                const user = await window.ApiClient.getCurrentUser();
                isAdmin = user?.Policy?.IsAdministrator || false;
                currentUsername = user?.Name || 'Unknown';
                return isAdmin;
            } catch (fallbackErr) {
                console.error('[Requests] Fallback error:', fallbackErr);
                return false;
            }
        }
    }

    async function getCurrentUsername() {
        if (!window.ApiClient) {
            console.warn('[Requests.getCurrentUsername] ApiClient not available yet');
            return 'Unknown';
        }
        
        try {
            const userId = window.ApiClient.getCurrentUserId();
            const user = await window.ApiClient.getUser(userId);
            const username = user?.Name || 'Unknown';
            return username;
        } catch (err) {
            console.error('[Requests.getCurrentUsername] Error:', err);
            // Try to get from current user context as fallback
            try {
                const currentUser = await window.ApiClient.getCurrentUser();
                const username = currentUser?.Name || 'Unknown';
                return username;
            } catch (fallbackErr) {
                console.error('[Requests.getCurrentUsername] Fallback error:', fallbackErr);
                return 'Unknown';
            }
        }
    }

    // ============================================
    // API FUNCTIONS
    // ============================================

    async function fetchAllRequests() {
        try {
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: window.ApiClient.getUrl(API_BASE),
                dataType: 'json'
            });
            if (response[0]) {
            }
            return Array.isArray(response) ? response : [];
        } catch (err) {
            console.error('[Requests.fetchAllRequests] Error:', err);
            return [];
        }
    }

    async function saveRequest(item) {
        const username = await getCurrentUsername();
        const userId = window.ApiClient.getCurrentUserId();
        
        const request = {
            title: item.title,
            year: item.year,
            img: item.img,
            imdbId: item.imdbId,
            tmdbId: item.tmdbId,
            itemType: item.itemType,
            jellyfinId: item.jellyfinId,
            status: 'pending',
            username: username,
            userId: userId,
            requestedAt: new Date().toISOString()
        };

        await window.ApiClient.ajax({
            type: 'POST',
            url: window.ApiClient.getUrl(API_BASE),
            data: JSON.stringify(request),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    async function updateRequestStatus(requestId, status, approvedBy) {
        const payload = { status };
        if (approvedBy) payload.approvedBy = approvedBy;

        await window.ApiClient.ajax({
            type: 'PUT',
            url: window.ApiClient.getUrl(`${API_BASE}/${requestId}`),
            data: JSON.stringify(payload),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    async function deleteRequest(requestId) {
        await window.ApiClient.ajax({
            type: 'DELETE',
            url: window.ApiClient.getUrl(`${API_BASE}/${requestId}`)
        });
    }

    // ============================================
    // CARD CREATION
    // ============================================

    async function createRequestCard(request, adminView) {
        const card = document.createElement('div');
        card.className = 'request-card';
        // Support different casing coming from server/client payloads and avoid undefined dataset
        card.dataset.requestId = request.Id || request.id || request.requestId || '';
        card.style.cssText = `
            display: inline-block;
            width: 140px;
            cursor: pointer;
            color: #ccc;
            position: relative;
            flex-shrink: 0;
            transition: transform 0.2s ease, box-shadow 0.2s ease;
            border-radius: 8px;
            overflow: hidden;
            background: rgba(30, 30, 30, 0.5);
        `;

        const imgDiv = document.createElement('div');
        imgDiv.style.cssText = `
            width: 100%;
            height: 210px;
            background-size: cover;
            background-position: center;
            position: relative;
            background-color: #1a1a1a;
        `;
        imgDiv.style.backgroundImage = request.img;

        // Add overlay gradient for better badge visibility
        const overlay = document.createElement('div');
        overlay.style.cssText = `
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            height: 60px;
            background: linear-gradient(to bottom, rgba(0,0,0,0.7) 0%, transparent 100%);
            pointer-events: none;
        `;
        imgDiv.appendChild(overlay);
        card.appendChild(imgDiv);

        // Status badge (top-right)
        if (request.status === 'pending') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'pending';
            statusBadge.textContent = 'Pending';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(255, 152, 0, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        } else if (request.status === 'approved') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'approved';
            statusBadge.textContent = '✓ Approved';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(76, 175, 80, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        } else if (request.status === 'rejected') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'rejected';
            statusBadge.textContent = '✗ Rejected';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(244, 67, 54, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        }

        // Username badge (bottom-left, only in admin view)
        if (adminView && request.username) {
            const userBadge = document.createElement('div');
            userBadge.textContent = request.username;
            userBadge.title = `Requested by ${request.username}`;
            userBadge.style.cssText = `
                position: absolute;
                bottom: 86px;
                left: 6px;
                background: rgba(30, 144, 255, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                max-width: 120px;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(userBadge);
        }

        // Title section at bottom
        const titleDiv = document.createElement('div');
        titleDiv.style.cssText = `
            padding: 8px;
            background: rgba(20, 20, 20, 0.9);
            min-height: 40px;
            display: flex;
            flex-direction: column;
            justify-content: center;
        `;

        const titleText = document.createElement('div');
        titleText.textContent = request.title || 'Unknown';
        titleText.title = request.title || 'Unknown';
        titleText.style.cssText = `
            font-size: 12px;
            font-weight: 600;
            color: #fff;
            overflow: hidden;
            text-overflow: ellipsis;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
            line-height: 1.3;
            margin-bottom: 2px;
        `;
        titleDiv.appendChild(titleText);

        if (request.year) {
            const yearText = document.createElement('div');
            yearText.textContent = request.year;
            yearText.style.cssText = `
                font-size: 10px;
                color: #999;
                font-weight: 500;
            `;
            titleDiv.appendChild(yearText);
        }

        card.appendChild(titleDiv);

        // Admin action buttons
        if (adminView) {
            if (request.status === 'pending') {
                const actionsDiv = document.createElement('div');
                actionsDiv.style.cssText = `
                    padding: 8px;
                    background: rgba(15, 15, 15, 0.9);
                    display: flex;
                    gap: 8px;
                `;

                const approveBtn = document.createElement('button');
                approveBtn.textContent = 'Approve';
                approveBtn.className = 'raised button-submit emby-button';
                approveBtn.style.cssText = `
                    flex: 1;
                    padding: 6px;
                    background: rgba(76, 175, 80, 0.9);
                    font-size: 11px;
                    font-weight: 600;
                `;
                approveBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    await updateRequestStatus(request.id, 'approved', currentUsername);
                    await loadDropdownRequests();
                });

                const rejectBtn = document.createElement('button');
                rejectBtn.textContent = 'Reject';
                rejectBtn.className = 'raised button-cancel emby-button';
                rejectBtn.style.cssText = `
                    flex: 1;
                    padding: 6px;
                    background: rgba(244, 67, 54, 0.9);
                    font-size: 11px;
                    font-weight: 600;
                `;
                rejectBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    await updateRequestStatus(request.id, 'rejected', currentUsername);
                    await loadDropdownRequests();
                });

                actionsDiv.appendChild(approveBtn);
                actionsDiv.appendChild(rejectBtn);
                card.appendChild(actionsDiv);
            } else if (request.status === 'approved' || request.status === 'rejected') {
                const deleteDiv = document.createElement('div');
                deleteDiv.style.cssText = `
                    padding: 8px;
                    background: rgba(15, 15, 15, 0.9);
                `;

                const deleteBtn = document.createElement('button');
                deleteBtn.textContent = 'Delete';
                deleteBtn.className = 'raised emby-button';
                deleteBtn.style.cssText = `
                    width: 100%;
                    padding: 6px;
                    background: rgba(150, 150, 150, 0.8);
                    font-size: 11px;
                    font-weight: 600;
                `;
                deleteBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    if (confirm(`Delete request for "${request.title}"?`)) {
                        await deleteRequest(request.id);
                        await loadDropdownRequests();
                    }
                });

                deleteDiv.appendChild(deleteBtn);
                card.appendChild(deleteDiv);
            }
        }

        // Hover effects
        card.addEventListener('mouseenter', () => {
            card.style.transform = 'translateY(-4px) scale(1.02)';
            card.style.boxShadow = '0 8px 16px rgba(0,0,0,0.6)';
        });

        card.addEventListener('mouseleave', () => {
            card.style.transform = 'translateY(0) scale(1)';
            card.style.boxShadow = 'none';
        });

        // Only open modal for non-admin or when not clicking action buttons
        if (!adminView) {
            card.addEventListener('click', () => {
                openRequestModal(request, adminView);
            });
        }

        return card;
    }

    // Create a placeholder/dummy card matching the request card size for empty states
    function createPlaceholderCard() {
        const card = document.createElement('div');
        card.className = 'request-card placeholder-card';
        card.style.cssText = `
            display: inline-block;
            width: 140px;
            cursor: default;
            color: #666;
            position: relative;
            flex-shrink: 0;
            border-radius: 8px;
            overflow: hidden;
            background: rgba(30, 30, 30, 0.3);
            border: 2px dashed rgba(100, 100, 100, 0.3);
        `;

        const imgDiv = document.createElement('div');
        imgDiv.style.cssText = `
            width: 100%;
            height: 210px;
            box-sizing: border-box;
            display: flex;
            align-items: center;
            justify-content: center;
            background: linear-gradient(135deg, rgba(255,255,255,0.02) 0%, rgba(0,0,0,0.05) 100%);
        `;

        const icon = document.createElement('div');
        icon.innerHTML = '📋';
        icon.style.cssText = `
            font-size: 48px;
            opacity: 0.2;
        `;
        imgDiv.appendChild(icon);
        card.appendChild(imgDiv);

        const titleDiv = document.createElement('div');
        titleDiv.style.cssText = `
            padding: 8px;
            background: rgba(20, 20, 20, 0.5);
            min-height: 40px;
            display: flex;
            align-items: center;
            justify-content: center;
        `;

        const emptyText = document.createElement('div');
        emptyText.textContent = 'No requests';
        emptyText.style.cssText = `
            font-size: 11px;
            color: #666;
            font-weight: 500;
        `;
        titleDiv.appendChild(emptyText);
        card.appendChild(titleDiv);

        return card;
    }

    function openRequestModal(request, adminView) {
        const currentUserName = currentUsername;
        const isOwnRequest = request.username === currentUserName;

        document.dispatchEvent(new CustomEvent('openDetailsModal', {
            detail: {
                item: {
                    Name: request.title,
                    ProductionYear: request.year,
                    tmdbId: request.tmdbId,
                    imdbId: request.imdbId,
                    jellyfinId: request.jellyfinId,
                    itemType: request.itemType
                },
                isRequestMode: true,
                requestId: request.id,
                requestUsername: request.username,
                requestStatus: request.status,
                isAdmin: adminView,
                isOwnRequest: isOwnRequest
            }
        }));
    }

    // ============================================
    // HEADER DROPDOWN MENU
    // ============================================

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

    function createDropdown() {
        if (dropdownMenu) return dropdownMenu;
        
        dropdownMenu = document.createElement('div');
        dropdownMenu.className = 'requests-dropdown';
        dropdownMenu.style.cssText = `
            position: fixed;
            top: 40px;
            bottom: 40px;
            right: 20px;
            left: 20px;
            max-width: 800px;
            margin: 0 auto;
            background: #181818;
            border: 1px solid #333;
            border-radius: 8px;
            box-shadow: 0 4px 16px rgba(0,0,0,0.5);
            z-index: 10000;
            display: none;
            overflow: hidden;
            padding: 20px;
            max-height: 80vh;
        `;
        
        dropdownMenu.innerHTML = `
                <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; border-bottom: 2px solid #333; padding-bottom: 10px;">
                <h2 style="margin: 0; color: #fff;">Media Requests</h2>
                <button class="close-dropdown" title="Close" style="background: #555; border: none; color: #fff; padding: 8px 10px; border-radius: 4px; cursor: pointer; display:flex;align-items:center;justify-content:center;">
                    <span class="material-icons" aria-hidden="true" style="font-size:18px;line-height:1;">close</span>
                </button>
            </div>
            <div class="dropdown-content" style="overflow-y: auto; max-height: calc(100% - 80px);">
                <div class="dropdown-movies">
                    <h3 style="color: #1e90ff; margin-bottom: 10px;">Movie Requests</h3>
                    <div class="dropdown-movies-container" style="display: flex; flex-wrap: wrap; gap: 15px; min-height: 50px;"></div>
                    <hr style="margin: 20px 0; height: 1px; background: rgba(255,255,255,0.1); border: none;">
                </div>

                <div class="dropdown-series">
                    <h3 style="color: #1e90ff; margin-bottom: 10px;">Series Requests</h3>
                    <div class="dropdown-series-container" style="display: flex; flex-wrap: wrap; gap: 15px; min-height: 50px;"></div>
                </div>
            </div>
        `;
        
        document.body.appendChild(dropdownMenu);
        
        dropdownMenu.querySelector('.close-dropdown').addEventListener('click', hideDropdown);
        
        return dropdownMenu;
    }

    async function loadDropdownRequests() {
        if (isLoadingRequests) return;
        isLoadingRequests = true;

        const dropdown = createDropdown();
        const moviesContainer = dropdown.querySelector('.dropdown-movies-container');
        const seriesContainer = dropdown.querySelector('.dropdown-series-container');

        moviesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';
        seriesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';

        try {
            const requests = await fetchAllRequests();
            const adminView = await checkAdmin();

            // Filter requests based on user type
            let filteredRequests;
            if (adminView) {
                filteredRequests = requests; // Admins see all requests in popup
            } else {
                filteredRequests = requests.filter(r => r.username === currentUsername);
            }

            // Split by item type - include ALL statuses (pending, approved, rejected)
            const movies = filteredRequests.filter(r => r.itemType === 'movie');
            const series = filteredRequests.filter(r => r.itemType === 'series');

            moviesContainer.innerHTML = '';
            seriesContainer.innerHTML = '';

            if (movies.length === 0) {
                moviesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of movies) {
                    moviesContainer.appendChild(await createRequestCard(req, adminView));
                }
            }

            if (series.length === 0) {
                seriesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of series) {
                    seriesContainer.appendChild(await createRequestCard(req, adminView));
                }
            }

            // Update notification badge after loading requests
            updateNotificationBadge();
        } catch (err) {
            console.error('[Requests.loadDropdownRequests] Error:', err);
            moviesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
            seriesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
        } finally {
            isLoadingRequests = false;
        }
    }

    function showDropdown() {
        createBackdrop();
        createDropdown();
        loadDropdownRequests();
        backdrop.style.display = 'block';
        dropdownMenu.style.display = 'block';
        // Prevent background scrolling
        document.body.style.overflow = 'hidden';
    }

    function hideDropdown() {
        if (backdrop) backdrop.style.display = 'none';
        if (dropdownMenu) dropdownMenu.style.display = 'none';
        // Restore background scrolling
        document.body.style.overflow = '';
    }

    function toggleDropdown(btn) {
        if (dropdownMenu && dropdownMenu.style.display === 'block') {
            hideDropdown();
        } else {
            showDropdown();
        }
    }

    // ============================================
    // USER MENU INTEGRATION
    // ============================================

    async function updateNotificationBadge() {
        const badge = document.querySelector('.requests-notification-badge');
        if (!badge) return;

        // Check if ApiClient is ready
        if (!window.ApiClient) {
            console.warn('[Requests.updateNotificationBadge] ApiClient not ready yet');
            return;
        }

        try {
            const requests = await fetchAllRequests();
            const username = await getCurrentUsername();
            const adminView = await checkAdmin();

            console.log('[Requests.updateNotificationBadge] Fetched', requests.length, 'total requests');

            let pendingCount = 0;

            if (adminView) {
                // For admins: count all pending requests (from any user)
                pendingCount = requests.filter(r => r.status === 'pending').length;
            } else {
                // For regular users: count only their own pending requests
                pendingCount = requests.filter(r => r.status === 'pending' && r.username === username).length;
            }

            console.log('[Requests.updateNotificationBadge] Pending count:', pendingCount, '(admin:', adminView, ')');

            // Show badge only if there are pending requests
            if (pendingCount > 0) {
                badge.textContent = pendingCount > 99 ? '99+' : pendingCount.toString();
                badge.style.display = 'flex';
            } else {
                badge.style.display = 'none';
            }
        } catch (err) {
            console.error('[Requests.updateNotificationBadge] Error:', err);
            badge.style.display = 'none';
        }
    }

    function addBadgeToUserButton() {
        // Find the user button
        const userButton = document.querySelector('.headerUserButton');
        if (!userButton) {
            setTimeout(addBadgeToUserButton, 500);
            return;
        }

        // Check if badge already exists
        if (document.querySelector('.requests-notification-badge')) {
            return;
        }

        console.log('[Requests] Adding badge to user button');

        // Make sure user button has relative positioning
        userButton.style.position = 'relative';

        // Add notification badge element
        const badge = document.createElement('span');
        badge.className = 'requests-notification-badge';
        badge.style.cssText = `
            position: absolute;
            top: 3px;
            right: 2px;
            background: #f44336;
            color: #fff;
            border-radius: 50%;
            min-width: 16px;
            height: 16px;
            font-size: 10px;
            font-weight: 700;
            display: none;
            align-items: center;
            justify-content: center;
            padding: 1px;
            line-height: 1;
            z-index: 10;
            box-shadow: 0 2px 4px rgba(0,0,0,0.4);
        `;
        userButton.appendChild(badge);

        console.log('[Requests] Badge added successfully');

        // Update badge immediately
        setTimeout(() => {
            updateNotificationBadge();
        }, 1000);

        // Update badge periodically
        setInterval(() => {
            updateNotificationBadge();
        }, 30000); // Every 30 seconds
    }

    function addMenuItemToUserMenu() {
        // Wait for user menu to be available
        const checkForMenu = () => {
            // Try multiple selectors to find the user menu
            let userMenus = [];

            // Try different common Jellyfin menu patterns
            const selectors = [
                '.headerUserButtonRound + div[data-role="controlgroup"]',
                '.headerUserButton + div[data-role="controlgroup"]',
                'div[data-role="controlgroup"]',
                '.mainDrawer-scrollContainer .verticalSection'
            ];

            for (const selector of selectors) {
                const found = document.querySelectorAll(selector);
                if (found.length > 0) {
                    userMenus = Array.from(found);
                    console.log('[Requests] Found user menu with selector:', selector);
                    break;
                }
            }

            // Also check for main drawer menu items
            const drawerButtons = document.querySelectorAll('.navMenuOption');
            if (drawerButtons.length > 0) {
                console.log('[Requests] Found drawer menu buttons');
            }

            if (userMenus.length === 0 && drawerButtons.length === 0) {
                setTimeout(checkForMenu, 500);
                return;
            }

            // Check if menu item already exists
            if (document.querySelector('.baklava-requests-menu-item')) {
                return;
            }

            console.log('[Requests] Adding menu item to user menu');

            // Add to controlgroup menus if found
            userMenus.forEach(menu => {
                const menuItem = document.createElement('button');
                menuItem.className = 'emby-button baklava-requests-menu-item';
                menuItem.setAttribute('is', 'emby-button');
                menuItem.setAttribute('data-role', 'button');
                menuItem.style.cssText = `
                    display: flex;
                    align-items: center;
                    width: 100%;
                    padding: 0.8em 1em;
                    border: none;
                    background: transparent;
                    color: inherit;
                    text-align: left;
                    cursor: pointer;
                    font-size: inherit;
                `;

                const icon = document.createElement('span');
                icon.className = 'material-icons';
                icon.setAttribute('aria-hidden', 'true');
                icon.textContent = 'list_alt';
                icon.style.cssText = `
                    margin-right: 0.5em;
                    font-size: 1.5em;
                `;

                const text = document.createElement('span');
                text.textContent = 'Media Requests';

                menuItem.appendChild(icon);
                menuItem.appendChild(text);

                // Add click handler
                menuItem.addEventListener('click', (e) => {
                    e.preventDefault();
                    e.stopPropagation();

                    // Close the user menu
                    const backdrop = document.querySelector('.dockedMenuBackdrop');
                    if (backdrop) {
                        backdrop.click();
                    }

                    // Open requests popup for all users
                    setTimeout(() => {
                        showDropdown();
                    }, 100);
                });

                // Insert at the top of the menu
                const firstChild = menu.firstChild;
                if (firstChild) {
                    menu.insertBefore(menuItem, firstChild);
                } else {
                    menu.appendChild(menuItem);
                }
            });

            // If we found drawer buttons, add there too
            if (drawerButtons.length > 0 && userMenus.length === 0) {
                const drawerContainer = drawerButtons[0].parentElement;
                if (drawerContainer && !drawerContainer.querySelector('.baklava-requests-menu-item')) {
                    const menuItem = document.createElement('a');
                    menuItem.className = 'navMenuOption baklava-requests-menu-item';
                    menuItem.setAttribute('is', 'emby-linkbutton');
                    menuItem.href = '#';
                    menuItem.style.cssText = `
                        display: flex;
                        align-items: center;
                        padding: 0.8em 1em;
                        text-decoration: none;
                        color: inherit;
                    `;

                    const icon = document.createElement('span');
                    icon.className = 'material-icons navMenuOptionIcon';
                    icon.setAttribute('aria-hidden', 'true');
                    icon.textContent = 'list_alt';

                    const text = document.createElement('span');
                    text.className = 'navMenuOptionText';
                    text.textContent = 'Media Requests';

                    menuItem.appendChild(icon);
                    menuItem.appendChild(text);

                    menuItem.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();

                        // Close drawer
                        const backdrop = document.querySelector('.dockedMenuBackdrop');
                        if (backdrop) {
                            backdrop.click();
                        }

                        // Open requests popup for all users
                        setTimeout(() => {
                            showDropdown();
                        }, 100);
                    });

                    // Insert after first menu item
                    if (drawerButtons[0].nextSibling) {
                        drawerContainer.insertBefore(menuItem, drawerButtons[0].nextSibling);
                    } else {
                        drawerContainer.appendChild(menuItem);
                    }
                }
            }

            console.log('[Requests] Menu item added successfully');
        };

        // Initial check
        checkForMenu();

        // Watch for menu being recreated
        const observer = new MutationObserver(() => {
            setTimeout(checkForMenu, 100);
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        // Also listen for clicks on user button to trigger menu check
        document.addEventListener('click', (e) => {
            const target = e.target.closest('.headerUserButton, .headerUserButtonRound');
            if (target) {
                setTimeout(checkForMenu, 200);
            }
        });
    }

    // ============================================
    // ADMIN REQUESTS PAGE
    // ============================================

    function createAdminRequestsPage() {
        const oldPage = document.getElementById('adminRequestsPage');
        if (oldPage) oldPage.remove();

        const adminPage = document.createElement('div');
        adminPage.id = 'adminRequestsPage';
        adminPage.className = 'page type-interior';
        adminPage.setAttribute('data-role', 'page');
        adminPage.style.cssText = 'display:none;';

        adminPage.innerHTML = `
            <div class="skinHeader focuscontainer-x padded-top padded-left padded-right padded-bottom-page">
                <div class="flex align-items-center flex-grow headerTop">
                    <div class="flex align-items-center flex-grow">
                        <h1 class="pageTitle">Manage Media Requests</h1>
                    </div>
                </div>
            </div>
            <div class="padded-left padded-right padded-top padded-bottom-page">
                <div class="verticalSection">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Movie Requests</h2>
                    <div class="admin-requests-movies-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
                <div class="verticalSection" style="margin-top:2em;">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Series Requests</h2>
                    <div class="admin-requests-series-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
            </div>
        `;

        document.body.appendChild(adminPage);
    }

    function showAdminRequestsPage() {
        document.querySelectorAll('.page').forEach(p => {
            if (p.id !== 'adminRequestsPage') {
                p.style.display = 'none';
            }
        });

        let adminPage = document.getElementById('adminRequestsPage');
        if (!adminPage) {
            createAdminRequestsPage();
            adminPage = document.getElementById('adminRequestsPage');
        }

        adminPage.style.display = 'block';
        loadAndDisplayAdminRequestsPage();
    }

    async function createAdminRequestCard(request) {
        const card = document.createElement('div');
        card.className = 'request-card admin-request-card';
        card.dataset.requestId = request.id || '';
        card.style.cssText = `
            display: inline-block;
            width: 140px;
            position: relative;
            flex-shrink: 0;
            border-radius: 8px;
            overflow: visible;
            background: rgba(30, 30, 30, 0.5);
            transition: transform 0.2s ease, box-shadow 0.2s ease;
        `;

        const imgDiv = document.createElement('div');
        imgDiv.style.cssText = `
            width: 100%;
            height: 210px;
            background-size: cover;
            background-position: center;
            position: relative;
            background-color: #1a1a1a;
            border-radius: 8px 8px 0 0;
        `;
        imgDiv.style.backgroundImage = request.img;

        // Add overlay gradient
        const overlay = document.createElement('div');
        overlay.style.cssText = `
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            height: 60px;
            background: linear-gradient(to bottom, rgba(0,0,0,0.7) 0%, transparent 100%);
            pointer-events: none;
        `;
        imgDiv.appendChild(overlay);
        card.appendChild(imgDiv);

        // Status badge (top-right)
        if (request.status === 'pending') {
            const statusBadge = document.createElement('div');
            statusBadge.textContent = 'Pending';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(255, 152, 0, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        } else if (request.status === 'approved') {
            const statusBadge = document.createElement('div');
            statusBadge.textContent = '✓ Approved';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(76, 175, 80, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        } else if (request.status === 'rejected') {
            const statusBadge = document.createElement('div');
            statusBadge.textContent = '✗ Rejected';
            statusBadge.style.cssText = `
                position: absolute;
                top: 6px;
                right: 6px;
                background: rgba(244, 67, 54, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.5px;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(statusBadge);
        }

        // Username badge (bottom-left)
        if (request.username) {
            const userBadge = document.createElement('div');
            userBadge.textContent = request.username;
            userBadge.title = `Requested by ${request.username}`;
            userBadge.style.cssText = `
                position: absolute;
                bottom: 86px;
                left: 6px;
                background: rgba(30, 144, 255, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 12px;
                font-size: 10px;
                font-weight: 700;
                max-width: 120px;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                box-shadow: 0 2px 8px rgba(0,0,0,0.3);
                z-index: 2;
            `;
            card.appendChild(userBadge);
        }

        // Title section
        const titleDiv = document.createElement('div');
        titleDiv.style.cssText = `
            padding: 8px;
            background: rgba(20, 20, 20, 0.9);
            min-height: 40px;
        `;

        const titleText = document.createElement('div');
        titleText.textContent = request.title || 'Unknown';
        titleText.title = request.title || 'Unknown';
        titleText.style.cssText = `
            font-size: 12px;
            font-weight: 600;
            color: #fff;
            overflow: hidden;
            text-overflow: ellipsis;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
            line-height: 1.3;
            margin-bottom: 2px;
        `;
        titleDiv.appendChild(titleText);

        if (request.year) {
            const yearText = document.createElement('div');
            yearText.textContent = request.year;
            yearText.style.cssText = `
                font-size: 10px;
                color: #999;
                font-weight: 500;
            `;
            titleDiv.appendChild(yearText);
        }

        card.appendChild(titleDiv);

        // Action buttons for pending requests
        if (request.status === 'pending') {
            const actionsDiv = document.createElement('div');
            actionsDiv.style.cssText = `
                padding: 8px;
                background: rgba(15, 15, 15, 0.9);
                display: flex;
                gap: 8px;
            `;

            const approveBtn = document.createElement('button');
            approveBtn.textContent = 'Approve';
            approveBtn.className = 'raised button-submit emby-button';
            approveBtn.style.cssText = `
                flex: 1;
                padding: 6px;
                background: rgba(76, 175, 80, 0.9);
                font-size: 11px;
                font-weight: 600;
            `;
            approveBtn.addEventListener('click', async (e) => {
                e.stopPropagation();
                await updateRequestStatus(request.id, 'approved', currentUsername);
            });

            const rejectBtn = document.createElement('button');
            rejectBtn.textContent = 'Reject';
            rejectBtn.className = 'raised button-cancel emby-button';
            rejectBtn.style.cssText = `
                flex: 1;
                padding: 6px;
                background: rgba(244, 67, 54, 0.9);
                font-size: 11px;
                font-weight: 600;
            `;
            rejectBtn.addEventListener('click', async (e) => {
                e.stopPropagation();
                await updateRequestStatus(request.id, 'rejected', currentUsername);
            });

            actionsDiv.appendChild(approveBtn);
            actionsDiv.appendChild(rejectBtn);
            card.appendChild(actionsDiv);
        } else {
            // Delete button for approved/rejected
            const deleteDiv = document.createElement('div');
            deleteDiv.style.cssText = `
                padding: 8px;
                background: rgba(15, 15, 15, 0.9);
            `;

            const deleteBtn = document.createElement('button');
            deleteBtn.textContent = 'Delete';
            deleteBtn.className = 'raised emby-button';
            deleteBtn.style.cssText = `
                width: 100%;
                padding: 6px;
                background: rgba(150, 150, 150, 0.8);
                font-size: 11px;
                font-weight: 600;
            `;
            deleteBtn.addEventListener('click', async (e) => {
                e.stopPropagation();
                if (confirm(`Delete request for "${request.title}"?`)) {
                    await deleteRequest(request.id);
                    await loadAndDisplayAdminRequestsPage();
                }
            });

            deleteDiv.appendChild(deleteBtn);
            card.appendChild(deleteDiv);
        }

        // Hover effects
        card.addEventListener('mouseenter', () => {
            card.style.transform = 'translateY(-4px) scale(1.02)';
            card.style.boxShadow = '0 8px 16px rgba(0,0,0,0.6)';
        });
        card.addEventListener('mouseleave', () => {
            card.style.transform = 'translateY(0) scale(1)';
            card.style.boxShadow = 'none';
        });

        return card;
    }

    async function loadAndDisplayAdminRequestsPage() {
        const page = document.getElementById('adminRequestsPage');
        if (!page) return;

        const moviesContainer = page.querySelector('.admin-requests-movies-panel .itemsContainer');
        const seriesContainer = page.querySelector('.admin-requests-series-panel .itemsContainer');

        moviesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';
        seriesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';

        try {
            const requests = await fetchAllRequests();

            // Admin page shows ALL requests from all users
            const movies = requests.filter(r => r.itemType === 'movie');
            const series = requests.filter(r => r.itemType === 'series');

            moviesContainer.innerHTML = '';
            seriesContainer.innerHTML = '';

            if (movies.length === 0) {
                moviesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of movies) {
                    moviesContainer.appendChild(await createAdminRequestCard(req));
                }
            }

            if (series.length === 0) {
                seriesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of series) {
                    seriesContainer.appendChild(await createAdminRequestCard(req));
                }
            }
        } catch (err) {
            console.error('[Requests.loadAdminRequestsPage] Error:', err);
            moviesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
            seriesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
        }
    }

    // ============================================
    // FULL REQUESTS PAGE
    // ============================================

    function createRequestsPage() {
        const oldPage = document.getElementById('requestsPage');
        if (oldPage) oldPage.remove();

        const requestsPage = document.createElement('div');
        requestsPage.id = 'requestsPage';
        requestsPage.className = 'page type-interior';
        requestsPage.setAttribute('data-role', 'page');
        requestsPage.style.cssText = 'display:none;';
        
        requestsPage.innerHTML = `
            <div class="skinHeader focuscontainer-x padded-top padded-left padded-right padded-bottom-page">
                <div class="flex align-items-center flex-grow headerTop">
                    <div class="flex align-items-center flex-grow">
                        <h1 class="pageTitle">Media Requests</h1>
                    </div>
                </div>
            </div>
            <div class="padded-left padded-right padded-top padded-bottom-page">
                <div class="verticalSection">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Movie Requests</h2>
                    <div class="requests-movies-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
                <div class="verticalSection" style="margin-top:2em;">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Series Requests</h2>
                    <div class="requests-series-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
            </div>
        `;
        
        document.body.appendChild(requestsPage);
    }

    function showRequestsPage() {
        document.querySelectorAll('.page').forEach(p => {
            if (p.id !== 'requestsPage') {
                p.style.display = 'none';
            }
        });
        
        let requestsPage = document.getElementById('requestsPage');
        if (!requestsPage) {
            createRequestsPage();
            requestsPage = document.getElementById('requestsPage');
        }
        
        requestsPage.style.display = 'block';
        loadAndDisplayRequestsPage();
    }

    async function loadAndDisplayRequestsPage() {
        const page = document.getElementById('requestsPage');
        if (!page) return;

        const moviesContainer = page.querySelector('.requests-movies-panel .itemsContainer');
        const seriesContainer = page.querySelector('.requests-series-panel .itemsContainer');

        moviesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';
        seriesContainer.innerHTML = '<div style="color: #999; padding: 20px;">Loading...</div>';

        try {
            const requests = await fetchAllRequests();

            // This page is for regular users only - they see only their own requests
            // Admins are routed to the admin page
            const filteredRequests = requests.filter(r => r.username === currentUsername);

            // Split by item type - include ALL statuses (pending, approved, rejected)
            const movies = filteredRequests.filter(r => r.itemType === 'movie');
            const series = filteredRequests.filter(r => r.itemType === 'series');

            moviesContainer.innerHTML = '';
            seriesContainer.innerHTML = '';

            if (movies.length === 0) {
                moviesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of movies) {
                    moviesContainer.appendChild(await createRequestCard(req, false));
                }
            }

            if (series.length === 0) {
                seriesContainer.appendChild(createPlaceholderCard());
            } else {
                for (const req of series) {
                    seriesContainer.appendChild(await createRequestCard(req, false));
                }
            }
        } catch (err) {
            console.error('[Requests.loadRequestsPage] Error:', err);
            moviesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
            seriesContainer.innerHTML = '<div style="color: #f44336; padding: 20px;">Error loading requests</div>';
        }
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    // Use a flag to prevent duplicate event listeners
    if (!window._baklavaRequestsInitialized) {
        window._baklavaRequestsInitialized = true;
        
        document.addEventListener('mediaRequest', async (e) => {
            const item = e.detail;
            try {
                await saveRequest(item);
                // Update badge immediately after saving
                updateNotificationBadge();
                // Reload dropdown, user page, and admin page if visible
                if (dropdownMenu && dropdownMenu.style.display === 'block') {
                    await loadDropdownRequests();
                }
                if (document.getElementById('requestsPage')?.style.display === 'block') {
                    await loadAndDisplayRequestsPage();
                }
                if (document.getElementById('adminRequestsPage')?.style.display === 'block') {
                    await loadAndDisplayAdminRequestsPage();
                }
            } catch (err) {
                console.error('[Requests] Error saving request:', err);
            }
        });
    }

    // ============================================
    // EXPORTS
    // ============================================

    window.RequestManager = {
        updateStatus: async (requestId, status, approvedBy) => {
            await updateRequestStatus(requestId, status, approvedBy);
            // Update badge immediately
            updateNotificationBadge();
            if (dropdownMenu && dropdownMenu.style.display === 'block') {
                await loadDropdownRequests();
            }
            if (document.getElementById('requestsPage')?.style.display === 'block') {
                await loadAndDisplayRequestsPage();
            }
            if (document.getElementById('adminRequestsPage')?.style.display === 'block') {
                await loadAndDisplayAdminRequestsPage();
            }
        },
        deleteRequest: async (requestId) => {
            await deleteRequest(requestId);
            // Update badge immediately
            updateNotificationBadge();
            if (dropdownMenu && dropdownMenu.style.display === 'block') {
                await loadDropdownRequests();
            }
            if (document.getElementById('requestsPage')?.style.display === 'block') {
                await loadAndDisplayRequestsPage();
            }
            if (document.getElementById('adminRequestsPage')?.style.display === 'block') {
                await loadAndDisplayAdminRequestsPage();
            }
        }
    };

    window.RequestsHeaderButton = {
        reload: async () => {
            if (dropdownMenu && dropdownMenu.style.display === 'block') {
                await loadDropdownRequests();
            }
            if (document.getElementById('requestsPage')?.style.display === 'block') {
                await loadAndDisplayRequestsPage();
            }
            if (document.getElementById('adminRequestsPage')?.style.display === 'block') {
                await loadAndDisplayAdminRequestsPage();
            }
        }
    };

    // ============================================
    // INITIALIZATION
    // ============================================

    function init() {

        // Wait for ApiClient to be ready before checking admin
        const waitForApiClient = () => {
            if (window.ApiClient) {
                checkAdmin();
            } else {
                setTimeout(waitForApiClient, 100);
            }
        };
        waitForApiClient();

        // Add badge to user button and menu item with retry logic
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => {
                addBadgeToUserButton();
                addMenuItemToUserMenu();
                // Retry a few times in case elements load late
                setTimeout(addBadgeToUserButton, 500);
                setTimeout(addBadgeToUserButton, 1500);
                setTimeout(addBadgeToUserButton, 3000);
            });
        } else {
            addBadgeToUserButton();
            addMenuItemToUserMenu();
            setTimeout(addBadgeToUserButton, 500);
            setTimeout(addBadgeToUserButton, 1500);
            setTimeout(addBadgeToUserButton, 3000);
        }
    }

    // Start initialization
    init();
    
    // Expose global interface for external access
    window.RequestsHeaderButton = {
        show: showDropdown,
        hide: hideDropdown,
        reload: loadDropdownRequests,
        showAdmin: showAdminRequestsPage
    };
})();
