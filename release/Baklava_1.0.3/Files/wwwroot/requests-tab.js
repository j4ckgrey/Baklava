(function() {
    'use strict';

    const MAX_ATTEMPTS = 50;
    let requestsButton = null;

    function addRequestsTab() {
        const slider = document.querySelector('.emby-tabs-slider');
        if (!slider) return false;

        // Avoid reinsertion
        if (slider.querySelector('.emby-tab-button[data-requests-tab]')) return true;

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'emby-button');
        btn.className = 'emby-tab-button emby-button';
        btn.dataset.requestsTab = 'true';

        const fg = document.createElement('div');
        fg.className = 'emby-button-foreground';
        fg.textContent = 'Requests';
        btn.appendChild(fg);

        slider.appendChild(btn);
        requestsButton = btn;

        // Listen for all tab clicks to hide requests page
        slider.addEventListener('click', (e) => {
            const clickedTab = e.target.closest('.emby-tab-button');
            if (clickedTab && !clickedTab.dataset.requestsTab) {
                const requestsPage = document.getElementById('requestsPage');
                if (requestsPage) {
                    requestsPage.style.display = 'none';
                }
            }
        });

        // Create the panel when button is clicked
        btn.addEventListener('click', () => {
            // Hide all existing tab content
            document.querySelectorAll('.page').forEach(p => p.style.display = 'none');
            
            // Remove active class from all tabs
            document.querySelectorAll('.emby-tab-button').forEach(t => t.classList.remove('emby-tab-button-active'));
            
            // Make this tab active
            btn.classList.add('emby-tab-button-active');
            
            // Show or create requests panel
            let requestsPage = document.getElementById('requestsPage');
            if (!requestsPage) {
                requestsPage = document.createElement('div');
                requestsPage.id = 'requestsPage';
                requestsPage.className = 'page type-interior';
                requestsPage.setAttribute('data-role', 'page');
                requestsPage.innerHTML = `
                    <div class="content-primary">
                        <h1 class="pageTitle" style="margin-bottom: 2em;">Media Requests</h1>
                        <div class="requests-tab-panel" style="padding:1em; margin-top: 2em;"></div>
                    </div>
                `;
                document.body.appendChild(requestsPage);
            }
            
            requestsPage.style.display = 'block';
            
            // Trigger request loading
            setTimeout(() => {
                const event = new CustomEvent('requestsTabOpened');
                document.dispatchEvent(event);
            }, 100);
        });

        return true;
    }

    function init() {
        let attempts = 0;
        const checker = setInterval(() => {
            attempts++;
            if (addRequestsTab() || attempts >= MAX_ATTEMPTS) {
                clearInterval(checker);
            }
        }, 200);

        // Watch for navigation changes and re-add tab if needed
        const observer = new MutationObserver(() => {
            const slider = document.querySelector('.emby-tabs-slider');
            if (slider && !slider.querySelector('.emby-tab-button[data-requests-tab]')) {
                addRequestsTab();
            }
            
            // Hide requests page when navigating away from home
            const requestsPage = document.getElementById('requestsPage');
            if (requestsPage && requestsPage.style.display !== 'none') {
                // Check if we're still on home page with tabs
                if (!document.querySelector('.emby-tabs-slider')) {
                    requestsPage.style.display = 'none';
                }
            }
        });

        observer.observe(document.body, { childList: true, subtree: true });
        
        // Also listen for viewshow events (Jellyfin's navigation)
        document.addEventListener('viewshow', () => {
            const requestsPage = document.getElementById('requestsPage');
            if (requestsPage) {
                requestsPage.style.display = 'none';
            }
        });
    }

    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        setTimeout(init, 500);
    } else {
        window.addEventListener('DOMContentLoaded', () => setTimeout(init, 500));
    }
})();
