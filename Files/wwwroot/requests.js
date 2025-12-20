/**
 * Media Requests - Consolidated
 * Combines: request-manager.js, requests-menu.js, requests-header-button.js
 * Manages user media requests with header button, dropdown menu, and full page views
 */
(function () {
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

    function normalizeRequest(r) {
        return {
            Id: r.Id || r.id || r.requestId || '',
            Username: r.Username || r.username || '',
            UserId: r.UserId || r.userId || '',
            Title: r.Title || r.title || '',
            Year: r.Year || r.year || '',
            Img: r.Img || r.img || '',
            ImdbId: r.ImdbId || r.imdbId || '',
            TmdbId: r.TmdbId || r.tmdbId || '',
            ItemType: r.ItemType || r.itemType || r.itemtype || '',
            JellyfinId: r.JellyfinId || r.jellyfinId || '',
            Status: r.Status || r.status || 'pending',
            ApprovedBy: r.ApprovedBy || r.approvedBy || '',
            Timestamp: r.Timestamp || r.timestamp || r.requestedAt || 0
        };
    }

    async function fetchAllRequests() {
        try {
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: window.ApiClient.getUrl(API_BASE),
                dataType: 'json'
            });
            const list = Array.isArray(response) ? response : [];
            return list.map(normalizeRequest);
        } catch (err) {
            console.error('[Requests.fetchAllRequests] Error:', err);
            return [];
        }
    }

    async function saveRequest(item) {
        const username = await getCurrentUsername();
        const userId = window.ApiClient.getCurrentUserId();

        const request = {
            Title: item.title,
            Year: item.year,
            Img: item.img,
            ImdbId: item.imdbId,
            TmdbId: item.tmdbId,
            ItemType: item.itemType,
            JellyfinId: item.jellyfinId,
            Status: 'pending',
            Username: username,
            UserId: userId,
            Timestamp: Date.now()
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
        const payload = { Status: status };
        if (approvedBy) payload.ApprovedBy = approvedBy;

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
            width: 100px;
            cursor: pointer;
            text-align: center;
            color: #ccc;
            position: relative;
            flex-shrink: 0;
        `;

        const imgDiv = document.createElement('div');
        imgDiv.style.cssText = `
            width: 100%;
            height: 150px;
            background-size: cover;
            background-position: center;
            border-radius: 6px;
            margin-bottom: 8px;
        `;
        imgDiv.style.backgroundImage = request.Img;
        card.appendChild(imgDiv);

        if (adminView && request.Username) {
            const userBadge = document.createElement('div');
            userBadge.textContent = request.Username;
            userBadge.style.cssText = `
                position: absolute;
                top: 8px;
                left: 8px;
                background: rgba(30, 144, 255, 0.9);
                color: #fff;
                padding: 4px 8px;
                border-radius: 4px;
                font-size: 11px;
                font-weight: 600;
                max-width: 85px;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            `;
            card.appendChild(userBadge);
        }

        if (request.Status === 'pending') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'pending';
            statusBadge.textContent = 'Pending';
            statusBadge.style.cssText = `
                position: absolute;
                top: 8px;
                right: 8px;
                background: rgba(255, 152, 0, 0.9);
                color: #fff;
                padding: 4px 8px;
                border-radius: 4px;
                font-size: 11px;
                font-weight: 600;
            `;
            card.appendChild(statusBadge);
        } else if (request.Status === 'approved') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'approved';
            statusBadge.textContent = 'Approved';
            statusBadge.style.cssText = `
                position: absolute;
                top: 8px;
                right: 8px;
                background: rgba(76, 175, 80, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 4px;
                font-size: 11px;
                font-weight: 600;
            `;
            card.appendChild(statusBadge);
        } else if (request.Status === 'rejected') {
            const statusBadge = document.createElement('div');
            statusBadge.className = 'request-status-badge';
            statusBadge.dataset.status = 'rejected';
            statusBadge.textContent = 'Rejected';
            statusBadge.style.cssText = `
                position: absolute;
                top: 8px;
                right: 8px;
                background: rgba(244, 67, 54, 0.95);
                color: #fff;
                padding: 4px 8px;
                border-radius: 4px;
                font-size: 11px;
                font-weight: 600;
            `;
            card.appendChild(statusBadge);

            // Add delete button for rejected requests
            if (adminView) {
                const deleteBtn = document.createElement('button');
                deleteBtn.innerHTML = '×';
                deleteBtn.style.cssText = `
                    position: absolute;
                    bottom: 40px;
                    right: 8px;
                    width: 28px;
                    height: 28px;
                    border-radius: 50%;
                    background: rgba(244, 67, 54, 0.9);
                    color: #fff;
                    border: none;
                    font-size: 24px;
                    line-height: 24px;
                    cursor: pointer;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    padding: 0;
                    transition: background 0.2s;
                `;
                deleteBtn.addEventListener('mouseover', () => {
                    deleteBtn.style.background = 'rgba(244, 67, 54, 1)';
                });
                deleteBtn.addEventListener('mouseout', () => {
                    deleteBtn.style.background = 'rgba(244, 67, 54, 0.9)';
                });
                deleteBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    if (confirm(`Delete rejected request for "${request.Title}"?`)) {
                        await deleteRequest(request.Id);
                    }
                });
                card.appendChild(deleteBtn);
            }
        }

        card.addEventListener('click', () => {
            openRequestModal(request, adminView);
        });

        return card;
    }

    // Create a placeholder/dummy card matching the request card size for empty states
    function createPlaceholderCard() {
        const card = document.createElement('div');
        card.className = 'request-card placeholder-card';
        card.style.cssText = `
            display: inline-block;
            width: 100px;
            margin: 10px;
            cursor: default;
            text-align: center;
            color: #666;
            position: relative;
        `;

        const imgDiv = document.createElement('div');
        imgDiv.style.cssText = `
            width: 100%;
            height: 150px;
            border-radius: 6px;
            margin-bottom: 8px;
            box-sizing: border-box;
            display: flex;
            align-items: center;
            justify-content: center;
            border: 2px dashed rgba(150,150,150,0.6);
            background: linear-gradient(180deg, rgba(255,255,255,0.02), rgba(0,0,0,0.02));
        `;

        const inner = document.createElement('div');
        inner.style.cssText = 'width:60%;height:60%;border-radius:4px;';
        imgDiv.appendChild(inner);
        card.appendChild(imgDiv);

        return card;
    }

    function openRequestModal(request, adminView) {
        const currentUserName = currentUsername;
        const isOwnRequest = request.Username === currentUserName;

        document.dispatchEvent(new CustomEvent('openDetailsModal', {
            detail: {
                item: {
                    Name: request.Title,
                    ProductionYear: request.Year,
                    tmdbId: request.TmdbId,
                    imdbId: request.ImdbId,
                    jellyfinId: request.JellyfinId,
                    itemType: request.ItemType
                },
                isRequestMode: true,
                requestId: request.Id,
                requestUsername: request.Username,
                requestStatus: request.Status,
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
                    <hr style="margin: 20px 0; height: 1px; background: rgba(255,255,255,0.1); border: none;">
                </div>

                <div class="dropdown-approved">
                    <h3 style="color: #4caf50; margin-bottom: 10px;">Approved</h3>
                    <div class="dropdown-approved-container" style="display: flex; flex-wrap: wrap; gap: 15px; min-height: 50px;"></div>
                    <hr style="margin: 20px 0; height: 1px; background: rgba(255,255,255,0.1); border: none;">
                </div>

                <div class="dropdown-rejected">
                    <h3 style="color: #f44336; margin-bottom: 10px;">Rejected</h3>
                    <div class="dropdown-rejected-container" style="display: flex; flex-wrap: wrap; gap: 15px; min-height: 50px;"></div>
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
            console.log('[Requests] Fetching requests...');
            const requests = await fetchAllRequests();
            const adminView = await checkAdmin();

            console.log('[Requests] Current User:', currentUsername, 'IsAdmin:', adminView);
            console.log('[Requests] Total Requests Fetched:', requests.length);
            if (requests.length > 0) {
                console.log('[Requests] Sample Request:', requests[0]);
            }

            // Normalize username for comparison
            const normUser = (currentUsername || '').toLowerCase();

            // Helper to safely access properties (handles PascalCase vs camelCase checks)
            const getStatus = (r) => r.Status || r.status;
            const getItemType = (r) => r.ItemType || r.itemType || r.itemtype; // just in case
            const getUsername = (r) => r.Username || r.username;

            // Generic filtering helper
            const isOwner = (r) => (getUsername(r) || '').toLowerCase() === normUser;

            // For admins: show all non-approved requests (pending) in movies/series lists,
            // but show only admin-owned approved copies in the Approved row.
            let filteredRequests;
            if (adminView) {
                // non-approved (pending/rejected/etc.) for lists
                filteredRequests = requests.filter(r => getStatus(r) !== 'approved');
            } else {
                filteredRequests = requests.filter(r => isOwner(r));
            }

            console.log('[Requests] Filtered Requests (pre-split):', filteredRequests.length);

            // Approved requests: Admins see ALL approved requests (to verify they worked/debug).
            const approved = requests.filter(r => getStatus(r) === 'approved');

            // Rejected requests: Admins see ALL rejected requests.
            const rejected = requests.filter(r => getStatus(r) === 'rejected');

            const movies = filteredRequests.filter(r => getItemType(r) === 'movie' && getStatus(r) !== 'approved' && getStatus(r) !== 'rejected');
            const series = filteredRequests.filter(r => (getItemType(r) === 'series' || getItemType(r) === 'tv') && getStatus(r) !== 'approved' && getStatus(r) !== 'rejected');

            console.log('[Requests] Counts -> Movies:', movies.length, 'Series:', series.length, 'Approved:', approved.length, 'Rejected:', rejected.length);


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

            // Approved section
            const approvedContainer = dropdown.querySelector('.dropdown-approved-container');
            if (approvedContainer) {
                if (approved && approved.length > 0) {
                    approvedContainer.innerHTML = '';
                    for (const req of approved) {
                        approvedContainer.appendChild(await createRequestCard(req, adminView));
                    }
                } else {
                    approvedContainer.innerHTML = '';
                    approvedContainer.appendChild(createPlaceholderCard());
                }
            }

            // Rejected section
            const rejectedContainer = dropdown.querySelector('.dropdown-rejected-container');
            if (rejectedContainer) {
                if (rejected && rejected.length > 0) {
                    rejectedContainer.innerHTML = '';
                    for (const req of rejected) {
                        rejectedContainer.appendChild(await createRequestCard(req, adminView));
                    }
                } else {
                    rejectedContainer.innerHTML = '';
                    rejectedContainer.appendChild(createPlaceholderCard());
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
    // HEADER BUTTON
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

            console.log('[Requests.updateNotificationBadge] Fetched', requests.length, 'total requests. Current User:', username);

            let pendingCount = 0;

            if (adminView) {
                // For admins: count all pending requests (from any user)
                pendingCount = requests.filter(r => r.Status === 'pending').length;
            } else {
                // For regular users: count only their own pending requests
                // Use case-insensitive check here too
                const normUser = (username || '').toLowerCase();
                const getStatus = (r) => r.Status || r.status;
                const getUsername = (r) => r.Username || r.username;
                const isOwner = (r) => (getUsername(r) || '').toLowerCase() === normUser;
                pendingCount = requests.filter(r => getStatus(r) === 'pending' && isOwner(r)).length;
            }

            console.log('[Requests.updateNotificationBadge] Pending count:', pendingCount, '(admin:', adminView, ')');

            // Always show the badge with the count (including 0)
            badge.textContent = pendingCount > 99 ? '99+' : pendingCount.toString();
            badge.style.display = 'flex';
        } catch (err) {
            console.error('[Requests.updateNotificationBadge] Error:', err);
            badge.style.display = 'none';
        }
    }

    function addRequestsButton() {
        // Skip if button already exists
        if (document.querySelector('.headerRequestsButton')) {
            return;
        }

        // Use legacy header only
        const headerRight = document.querySelector('.headerRight');
        if (!headerRight) {
            console.log('[Requests] No header found, will retry');
            setTimeout(addRequestsButton, 500);
            return;
        }

        console.log('[Requests] Adding button to legacy header');

        const btn = document.createElement('button');
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.setAttribute('data-role', 'requests-button');
        btn.className = 'headerButton headerButtonRight headerRequestsButton paper-icon-button-light';
        btn.title = 'Media Requests';
        btn.style.position = 'relative'; // Needed for badge positioning
        btn.innerHTML = '<span class="material-icons list_alt" aria-hidden="true"></span>';

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
        `;
        btn.appendChild(badge);

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

        console.log('[Requests] Button added successfully');

        // Update badge immediately and then periodically
        // Wait a bit for ApiClient to be fully ready
        setTimeout(() => {
            updateNotificationBadge();
        }, 1000);
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
                <div class="verticalSection" style="margin-top:3em;">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Approved</h2>
                    <div class="requests-approved-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
                <div class="verticalSection" style="margin-top:3em;">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Rejected</h2>
                    <div class="requests-rejected-panel">
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
            const adminView = await checkAdmin();

            // Normalize username
            const normUser = (currentUsername || '').toLowerCase();

            // Helper accessors
            const getStatus = (r) => r.Status || r.status;
            const getItemType = (r) => r.ItemType || r.itemType || r.itemtype;
            const getUsername = (r) => r.Username || r.username;
            const isOwner = (r) => (getUsername(r) || '').toLowerCase() === normUser;

            // Admins: show all non-approved/non-rejected requests in lists, but only their own approved/rejected copies
            let filteredRequests;
            if (adminView) {
                filteredRequests = requests.filter(r => getStatus(r) !== 'approved' && getStatus(r) !== 'rejected');
            } else {
                filteredRequests = requests.filter(r => isOwner(r));
            }

            const approved = requests.filter(r => getStatus(r) === 'approved');
            const rejected = requests.filter(r => getStatus(r) === 'rejected');

            const movies = filteredRequests.filter(r => getItemType(r) === 'movie' && getStatus(r) !== 'approved' && getStatus(r) !== 'rejected');
            const series = filteredRequests.filter(r => (getItemType(r) === 'series' || getItemType(r) === 'tv') && getStatus(r) !== 'approved' && getStatus(r) !== 'rejected');

            moviesContainer.innerHTML = '';
            seriesContainer.innerHTML = '';
            const approvedContainer = page.querySelector('.requests-approved-panel .itemsContainer');
            const rejectedContainer = page.querySelector('.requests-rejected-panel .itemsContainer');
            if (approvedContainer) approvedContainer.innerHTML = '';
            if (rejectedContainer) rejectedContainer.innerHTML = '';

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

            // Populate approved row
            if (approvedContainer) {
                if (approved.length === 0) {
                    approvedContainer.appendChild(createPlaceholderCard());
                } else {
                    for (const req of approved) {
                        approvedContainer.appendChild(await createRequestCard(req, adminView));
                    }
                }
            }

            // Populate rejected row
            if (rejectedContainer) {
                if (rejected.length === 0) {
                    rejectedContainer.appendChild(createPlaceholderCard());
                } else {
                    for (const req of rejected) {
                        rejectedContainer.appendChild(await createRequestCard(req, adminView));
                    }
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
                // Reload both dropdown and page if visible
                if (dropdownMenu && dropdownMenu.style.display === 'block') {
                    await loadDropdownRequests();
                }
                if (document.getElementById('requestsPage')?.style.display === 'block') {
                    await loadAndDisplayRequestsPage();
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

        // Add header button with retry logic
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => {
                addRequestsButton();
                // Retry a few times in case header loads late
                setTimeout(addRequestsButton, 500);
                setTimeout(addRequestsButton, 1500);
                setTimeout(addRequestsButton, 3000);
            });
        } else {
            addRequestsButton();
            setTimeout(addRequestsButton, 500);
            setTimeout(addRequestsButton, 1500);
            setTimeout(addRequestsButton, 3000);
        }

        // Watch for header being added dynamically
        const headerObserver = new MutationObserver(() => {
            if (!document.querySelector('.mui-requests-button, .headerRequestsButton')) {
                addRequestsButton();
            }
        });

        headerObserver.observe(document.body, {
            childList: true,
            subtree: true
        });
    }

    // Start initialization
    init();

    // Expose global interface for external access
    window.RequestsHeaderButton = {
        show: showDropdown,
        hide: hideDropdown,
        reload: loadDropdownRequests
    };
})();
