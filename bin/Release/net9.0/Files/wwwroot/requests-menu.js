(function() {
    'use strict';

    let initialized = false;

    function addRequestsMenuItem() {
        // Find the user menu section
        const userMenuSection = document.querySelector('.verticalSection .headerUsername');
        if (!userMenuSection) return false;

        const container = userMenuSection.parentElement;
        if (!container) return false;

        // Check if already added
        if (container.querySelector('.lnkMediaRequests')) return true;

        // Create the requests menu item
        const requestsLink = document.createElement('a');
        requestsLink.className = 'emby-button lnkMediaRequests listItem-border';
        requestsLink.href = '#';
        requestsLink.style.cssText = 'display: block; margin: 0px; padding: 0px;';
        
        requestsLink.innerHTML = `
            <div class="listItem">
                <span class="material-icons listItemIcon listItemIcon-transparent movie_filter" aria-hidden="true"></span>
                <div class="listItemBody">
                    <div class="listItemBodyText">Media Requests</div>
                </div>
            </div>
        `;

        // Add click handler
        requestsLink.addEventListener('click', (e) => {
            e.preventDefault();
            showRequestsPage();
            // Close the user menu
            const userMenuButton = document.querySelector('.headerUserButton');
            if (userMenuButton) userMenuButton.click();
        });

        // Insert after Quick Connect (or at the end)
        const quickConnect = container.querySelector('.lnkQuickConnectPreferences');
        if (quickConnect) {
            quickConnect.parentNode.insertBefore(requestsLink, quickConnect.nextSibling);
        } else {
            container.appendChild(requestsLink);
        }

        console.log('[RequestsMenu] Added menu item');
        return true;
    }

    function createRequestsPage() {
        // Remove old requests page if exists
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
                <div class="verticalSection" style="margin-top:3em;">
                    <h2 class="sectionTitle sectionTitle-cards padded-left">Series Requests</h2>
                    <div class="requests-series-panel">
                        <div class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" style="white-space:nowrap;overflow-x:auto;"></div>
                    </div>
                </div>
            </div>
        `;
        
        document.body.appendChild(requestsPage);
        console.log('[RequestsMenu] Created requests page');
    }

    function showRequestsPage() {
        // Hide all other pages
        document.querySelectorAll('.page').forEach(p => {
            if (p.id !== 'requestsPage') {
                p.style.display = 'none';
            }
        });
        
        // Get or create requests page
        let requestsPage = document.getElementById('requestsPage');
        if (!requestsPage) {
            createRequestsPage();
            requestsPage = document.getElementById('requestsPage');
        }
        
        // Show requests page
        requestsPage.style.display = 'flex';
        requestsPage.style.flexDirection = 'column';
        
        // Trigger request loading
        setTimeout(() => {
            const event = new CustomEvent('requestsTabOpened');
            document.dispatchEvent(event);
        }, 100);
        
        console.log('[RequestsMenu] Showing requests page');
    }

    function hideRequestsPage() {
        const requestsPage = document.getElementById('requestsPage');
        if (requestsPage) {
            requestsPage.style.display = 'none';
        }
    }

    function init() {
        if (initialized) return;
        
        console.log('[RequestsMenu] Initializing...');
        
        // Try to add menu item periodically
        const checker = setInterval(() => {
            if (addRequestsMenuItem()) {
                clearInterval(checker);
            }
        }, 500);

        // Stop trying after 30 seconds
        setTimeout(() => clearInterval(checker), 30000);

        // Watch for menu re-renders
        const observer = new MutationObserver(() => {
            addRequestsMenuItem();
        });

        observer.observe(document.body, { childList: true, subtree: true });

        // Listen for Jellyfin's viewshow event to hide requests page when navigating
        document.addEventListener('viewshow', (e) => {
            // When any other view is shown, hide the requests page
            hideRequestsPage();
        });

        initialized = true;
        console.log('[RequestsMenu] Initialized');
    }

    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        setTimeout(init, 500);
    } else {
        window.addEventListener('DOMContentLoaded', () => setTimeout(init, 500));
    }
})();
