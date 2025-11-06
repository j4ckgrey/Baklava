(function () {
    'use strict';

    console.log('[MyJellyfinPlugin] Search toggle script loading...');

    const SEARCH_STORAGE_KEY = 'jellyfin_local_search_enabled';
    const SEARCH_PREFIX = 'local:';
    let globeIcon = null;

    // Inject CSS for globe icon
    function injectSearchToggleCSS() {
        if (document.getElementById('search-toggle-styles')) return;
        const style = document.createElement('style');
        style.id = 'search-toggle-styles';
        style.textContent = `
            .search-globe-icon {
                font-family: 'Material Icons' !important;
                font-size: 26px !important;
                color: rgba(255, 255, 255, 0.7) !important;
                cursor: pointer !important;
                transition: color 0.2s ease, opacity 0.25s ease !important;
                user-select: none !important;
                width: 26px !important;
                height: 26px !important;
                display: flex !important;
                align-items: flex-end !important;
                justify-content: center !important;
                border-radius: 4px !important;
                padding: 8px !important;
                margin-left: 8px !important;
                position: relative !important;
                background: transparent !important;
                align-self: flex-end !important;
                opacity: 0;
                animation: fadeInGlobe 0.25s ease forwards;
            }
            @keyframes fadeInGlobe { to { opacity: 1; } }
            .search-globe-icon:hover {
                color: rgba(255, 255, 255, 0.9) !important;
                background: rgba(255, 255, 255, 0.1) !important;
            }
            .search-globe-icon.local-mode {
                color: rgba(255, 255, 255, 0.85) !important;
            }
            .search-globe-icon.local-mode::after {
                content: '' !important;
                position: absolute !important;
                top: 50% !important;
                left: 3px !important;
                right: 3px !important;
                height: 2px !important;
                background: #ff4444 !important;
                transform: translateY(-50%) rotate(-45deg) !important;
                border-radius: 1px !important;
                pointer-events: none !important;
            }
            @media screen and (max-width: 768px) {
                .search-globe-icon {
                    font-size: 22px !important;
                    width: 22px !important;
                    height: 22px !important;
                    padding: 6px !important;
                    margin-left: 6px !important;
                }
            }
        `;
        document.head.appendChild(style);
    }

    // Detect admin user
    function isUserAdmin() {
        try {
            const userKeys = Object.keys(localStorage).filter(k =>
                k.toLowerCase().includes('user-') && localStorage.getItem(k)
            );
            let latest = null, latestDate = null;
            for (const k of userKeys) {
                try {
                    const u = JSON.parse(localStorage.getItem(k));
                    if (u?.LastActivityDate) {
                        const d = new Date(u.LastActivityDate);
                        if (!latestDate || d > latestDate) {
                            latestDate = d;
                            latest = u;
                        }
                    }
                } catch {}
            }
            return latest?.Policy?.IsAdministrator === true;
        } catch { return false; }
    }

    // Toggle state getter/setter
    function getSearchToggleState() {
        const stored = localStorage.getItem(SEARCH_STORAGE_KEY);
        // Default to global search (false) when no preference is stored
        return stored === null ? false : stored === 'true';
    }

    function setSearchToggleState(state) {
        localStorage.setItem(SEARCH_STORAGE_KEY, String(state));
    }

    // Trigger search results refresh without rebuilding search bar
    function refreshSearchResults(query) {
        if (!query) return;
        const q = encodeURIComponent(query);
        try {
            if (window.AppRouter?.show) AppRouter.show(`search.html?query=${q}`);
            else if (window.Emby?.Page?.show) Emby.Page.show(`search.html?query=${q}`);
            else window.location.hash = `#!/search.html?query=${q}`;
        } catch {
            window.location.hash = `#!/search.html?query=${q}`;
        }
    }

    function updateGlobeVisual() {
        if (globeIcon) {
            globeIcon.classList.toggle('local-mode', getSearchToggleState());
        }
    }

    function createGlobeIcon() {
        const icon = document.createElement('span');
        icon.className = 'search-globe-icon material-icons';
        icon.textContent = 'public';
        icon.classList.toggle('local-mode', getSearchToggleState());
        icon.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const newState = !getSearchToggleState();
            setSearchToggleState(newState);
            updateGlobeVisual();

            const query = document.getElementById('searchTextInput')?.value?.trim() ||
                          new URL(window.location).searchParams.get('query') || '';
            refreshSearchResults(query);
        });
        return icon;
    }

    // Intercept search API to add/remove local: prefix
    function interceptSearchAPI() {
        const patchURL = (u) => {
            const keys = ['searchTerm', 'SearchTerm', 'searchQuery', 'query', 'q'];
            const isLocal = getSearchToggleState();
            for (const k of keys) {
                const v = u.searchParams.get(k);
                if (!v) continue;
                if (isLocal && !v.startsWith(SEARCH_PREFIX)) {
                    u.searchParams.set(k, SEARCH_PREFIX + v);
                    return true;
                } else if (!isLocal && v.startsWith(SEARCH_PREFIX)) {
                    u.searchParams.set(k, v.slice(SEARCH_PREFIX.length));
                    return true;
                }
            }
            return false;
        };

        const origOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function (m, url, ...a) {
            if (url && (url.includes('/Search') || url.includes('/Items'))) {
                try {
                    const u = new URL(url, window.location.origin);
                    if (patchURL(u)) url = u.toString();
                } catch {}
            }
            return origOpen.call(this, m, url, ...a);
        };

        const origFetch = window.fetch;
        window.fetch = function (i, init) {
            if (typeof i === 'string' && (i.includes('/Search') || i.includes('/Items'))) {
                try {
                    const u = new URL(i, window.location.origin);
                    if (patchURL(u)) i = u.toString();
                } catch {}
            }
            return origFetch.call(this, i, init);
        };
    }

    // Attach globe to search input container
    function attachGlobe(force = false) {
        const searchInput = document.getElementById('searchTextInput');
        if (!searchInput) return;
        const container = searchInput.closest('.inputContainer');
        if (!container) return;

        const existing = container.parentElement.querySelector('.search-globe-icon');
        if (existing && !force) return;

        const icon = globeIcon || createGlobeIcon();
        globeIcon = icon;
        updateGlobeVisual();

        if (!icon) return;
        if (existing && existing !== icon) existing.remove();
        if (!icon.isConnected) {
            container.parentElement.insertBefore(icon, container.nextSibling);
        }
    }

    // Initialize
    function init() {
        try {
            console.log('[MyJellyfinPlugin] init() called');
            injectSearchToggleCSS();
            interceptSearchAPI();
            attachGlobe();

            const obs = new MutationObserver(() => {
                const searchInput = document.getElementById('searchTextInput');
                if (searchInput && !document.querySelector('.search-globe-icon')) {
                    attachGlobe(true);
                }
            });
            obs.observe(document.documentElement, { childList: true, subtree: true });
            console.log('[MyJellyfinPlugin] init() completed successfully');
        } catch (error) {
            console.error('[MyJellyfinPlugin] ERROR in init():', error);
        }
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', init);
    else init();
    
    console.log('[MyJellyfinPlugin] Search toggle script loaded');
})();
