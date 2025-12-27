/**
 * Baklava Catalogs Management
 * Displays aiostreams catalogs from Gelato and allows creating collections or importing to library
 */

(function () {
    'use strict';

    // Only run on plugin config page
    // NOTE: Disabled - catalogs management is now handled in configPage.html directly
    if (true || !window.location.href.includes('configurationpage?name=Baklava')) {
        return;
    }

    const CATALOGS_API = '/api/baklava/catalogs';

    // State
    let catalogs = [];
    let isLoading = false;

    /**
     * Initialize the catalogs section
     */
    function init() {
        // Wait for the config page to be fully loaded
        const checkInterval = setInterval(() => {
            const configPage = document.querySelector('.mainAnimatedPages');
            if (configPage) {
                clearInterval(checkInterval);
                setTimeout(injectCatalogsSection, 500);
            }
        }, 100);
    }

    /**
     * Inject the catalogs section into the config page
     */
    function injectCatalogsSection() {
        // Find the plugin configuration container
        const container = document.querySelector('.pluginConfigurationPage, .page, form');
        if (!container) {
            console.warn('[Baklava Catalogs] Could not find config container');
            return;
        }

        // Check if already injected
        if (document.getElementById('baklava-catalogs-section')) {
            return;
        }

        // Create the catalogs section
        const section = document.createElement('div');
        section.id = 'baklava-catalogs-section';
        section.className = 'verticalSection';
        section.innerHTML = `
            <div class="sectionTitleContainer flex align-items-center">
                <h2 class="sectionTitle">Catalogs</h2>
                <div style="display: flex; align-items: center; gap: 10px; margin-left: auto;">
                    <label style="font-size: 0.9em; color: var(--theme-text-secondary-color);">Max items:</label>
                    <input id="baklava-max-items" type="number" min="1" max="500" value="100" 
                           style="width: 70px; padding: 4px 8px; border: 1px solid var(--theme-primary-color); 
                                  border-radius: 4px; background: var(--theme-background-color); 
                                  color: var(--theme-text-color);" />
                    <button id="baklava-catalogs-refresh" type="button" class="emby-button button-flat" title="Refresh">
                        <span class="material-icons refresh"></span>
                    </button>
                </div>
            </div>
            <div class="fieldDescription">
                Manage aiostreams catalogs from Gelato. Create collections or import items directly to your library.
            </div>
            <div id="baklava-catalogs-loading" class="hide" style="text-align: center; padding: 20px;">
                <div class="loading-spinner"></div>
                <p>Loading catalogs...</p>
            </div>
            <div id="baklava-catalogs-error" class="hide" style="color: var(--theme-error-color); padding: 10px;">
            </div>
            <div id="baklava-catalogs-list" class="paperList" style="margin-top: 10px;">
            </div>
        `;

        // Insert after the last section or at the end
        container.appendChild(section);

        // Add event listeners
        document.getElementById('baklava-catalogs-refresh').addEventListener('click', loadCatalogs);

        // Add styles
        addStyles();

        // Initial load
        loadCatalogs();
    }

    /**
     * Add custom styles for the catalogs section
     */
    function addStyles() {
        if (document.getElementById('baklava-catalogs-styles')) return;

        const style = document.createElement('style');
        style.id = 'baklava-catalogs-styles';
        style.textContent = `
            .baklava-catalog-item {
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 12px 16px;
                margin-bottom: 8px;
                background: rgba(0,0,0,0.2);
                border-radius: 8px;
            }
            .baklava-catalog-info {
                flex: 1;
            }
            .baklava-catalog-name {
                font-size: 1.1em;
                font-weight: 500;
                display: flex;
                align-items: center;
                gap: 8px;
            }
            .baklava-catalog-type {
                font-size: 0.75em;
                padding: 2px 8px;
                border-radius: 4px;
                background: var(--theme-primary-color);
                color: white;
                text-transform: uppercase;
            }
            .baklava-catalog-meta {
                font-size: 0.85em;
                color: var(--theme-text-secondary-color);
                margin-top: 4px;
            }
            .baklava-catalog-actions {
                display: flex;
                gap: 8px;
            }
            .baklava-catalog-actions button {
                padding: 6px 12px;
                font-size: 0.85em;
            }
            .baklava-catalog-count {
                font-weight: 600;
            }
            .loading-spinner {
                width: 24px;
                height: 24px;
                border: 3px solid rgba(255,255,255,0.3);
                border-radius: 50%;
                border-top-color: var(--theme-primary-color);
                animation: spin 1s ease-in-out infinite;
                margin: 0 auto;
            }
            @keyframes spin {
                to { transform: rotate(360deg); }
            }
        `;
        document.head.appendChild(style);
    }

    /**
     * Load catalogs from the API
     */
    async function loadCatalogs() {
        if (isLoading) return;

        isLoading = true;
        showLoading(true);
        hideError();

        try {
            const response = await window.ApiClient.fetch({
                url: CATALOGS_API,
                type: 'GET',
                dataType: 'json'
            });

            catalogs = response || [];
            renderCatalogs();
        } catch (error) {
            console.error('[Baklava Catalogs] Failed to load catalogs:', error);
            showError('Failed to load catalogs. Make sure Gelato is configured correctly.');
        } finally {
            isLoading = false;
            showLoading(false);
        }
    }

    /**
     * Render the catalogs list
     */
    function renderCatalogs() {
        const list = document.getElementById('baklava-catalogs-list');
        if (!list) return;

        if (catalogs.length === 0) {
            list.innerHTML = '<div style="padding: 16px; text-align: center; color: var(--theme-text-secondary-color);">No catalogs found. Make sure Gelato is configured with a valid aiostreams manifest.</div>';
            return;
        }

        list.innerHTML = catalogs.map(catalog => `
            <div class="baklava-catalog-item" data-catalog-id="${escapeHtml(catalog.id)}" data-catalog-type="${escapeHtml(catalog.type)}">
                <div class="baklava-catalog-info">
                    <div class="baklava-catalog-name">
                        <span class="material-icons">${catalog.type === 'series' ? 'tv' : 'movie'}</span>
                        ${escapeHtml(catalog.name)}
                        <span class="baklava-catalog-type">${escapeHtml(catalog.type)}</span>
                    </div>
                    <div class="baklava-catalog-meta">
                        <span class="baklava-catalog-count">${catalog.itemCount >= 0 ? catalog.itemCount + ' titles' : 'Loading...'}</span>
                        ${catalog.addonName ? ` • Source: ${escapeHtml(catalog.addonName)}` : ''}
                    </div>
                </div>
                <div class="baklava-catalog-actions">
                    <button type="button" class="raised emby-button baklava-create-collection" ${catalog.itemCount === 0 ? 'disabled' : ''}>
                        <span class="material-icons">video_library</span>
                        <span>Create Collection</span>
                    </button>
                    <button type="button" class="raised emby-button baklava-import-catalog" ${catalog.itemCount === 0 ? 'disabled' : ''}>
                        <span class="material-icons">download</span>
                        <span>Import</span>
                    </button>
                </div>
            </div>
        `).join('');

        // Add click handlers
        list.querySelectorAll('.baklava-create-collection').forEach(btn => {
            btn.addEventListener('click', handleCreateCollection);
        });

        list.querySelectorAll('.baklava-import-catalog').forEach(btn => {
            btn.addEventListener('click', handleImportCatalog);
        });
    }

    /**
     * Handle create collection button click
     */
    async function handleCreateCollection(event) {
        const item = event.target.closest('.baklava-catalog-item');
        const catalogId = item.dataset.catalogId;
        const catalogType = item.dataset.catalogType;

        const catalog = catalogs.find(c => c.id === catalogId);
        const catalogName = catalog ? catalog.name : catalogId;

        // Get max items from input
        const maxItemsInput = document.getElementById('baklava-max-items');
        const maxItems = parseInt(maxItemsInput?.value) || 100;

        // Confirm action
        const confirmed = await showConfirmDialog(
            'Create Collection',
            `Create a collection from "${catalogName}"? This will import up to ${maxItems} items from the catalog.`
        );

        if (!confirmed) return;

        const btn = event.target.closest('button');
        const originalContent = btn.innerHTML;
        btn.disabled = true;
        btn.innerHTML = '<span class="loading-spinner" style="width:16px;height:16px;"></span>';

        try {
            const response = await window.ApiClient.fetch({
                url: `${CATALOGS_API}/${encodeURIComponent(catalogId)}/collection?type=${catalogType}`,
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ maxItems: maxItems, collectionName: catalogName })
            });

            if (response && response.success) {
                showToast(`Collection "${response.collectionName}" created with ${response.itemCount} items!`, 'success');
            } else {
                showToast('Failed to create collection', 'error');
            }
        } catch (error) {
            console.error('[Baklava Catalogs] Create collection error:', error);
            showToast('Failed to create collection: ' + (error.message || 'Unknown error'), 'error');
        } finally {
            btn.disabled = false;
            btn.innerHTML = originalContent;
        }
    }

    /**
     * Handle import catalog button click
     */
    async function handleImportCatalog(event) {
        const item = event.target.closest('.baklava-catalog-item');
        const catalogId = item.dataset.catalogId;
        const catalogType = item.dataset.catalogType;

        const catalog = catalogs.find(c => c.id === catalogId);
        const catalogName = catalog ? catalog.name : catalogId;

        // Get max items from input
        const maxItemsInput = document.getElementById('baklava-max-items');
        const maxItems = parseInt(maxItemsInput?.value) || 100;

        // Confirm action
        const confirmed = await showConfirmDialog(
            'Import to Library',
            `Import items from "${catalogName}" to your library? This will add up to ${maxItems} items.`
        );

        if (!confirmed) return;

        const btn = event.target.closest('button');
        const originalContent = btn.innerHTML;
        btn.disabled = true;
        btn.innerHTML = '<span class="loading-spinner" style="width:16px;height:16px;"></span>';

        try {
            const response = await window.ApiClient.fetch({
                url: `${CATALOGS_API}/${encodeURIComponent(catalogId)}/import?type=${catalogType}`,
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ maxItems: maxItems })
            });

            if (response && response.success) {
                showToast(`Imported ${response.importedCount} items! (${response.failedCount} failed)`, 'success');
            } else {
                showToast('Failed to import catalog', 'error');
            }
        } catch (error) {
            console.error('[Baklava Catalogs] Import error:', error);
            showToast('Failed to import catalog: ' + (error.message || 'Unknown error'), 'error');
        } finally {
            btn.disabled = false;
            btn.innerHTML = originalContent;
        }
    }

    /**
     * Show/hide loading indicator
     */
    function showLoading(show) {
        const el = document.getElementById('baklava-catalogs-loading');
        if (el) {
            el.classList.toggle('hide', !show);
        }
    }

    /**
     * Show error message
     */
    function showError(message) {
        const el = document.getElementById('baklava-catalogs-error');
        if (el) {
            el.textContent = message;
            el.classList.remove('hide');
        }
    }

    /**
     * Hide error message
     */
    function hideError() {
        const el = document.getElementById('baklava-catalogs-error');
        if (el) {
            el.classList.add('hide');
        }
    }

    /**
     * Escape HTML to prevent XSS
     */
    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    /**
     * Show confirmation dialog
     */
    function showConfirmDialog(title, message) {
        return new Promise((resolve) => {
            // Use Jellyfin's built-in confirm dialog if available
            if (window.Dashboard && window.Dashboard.confirm) {
                window.Dashboard.confirm(message, title, resolve);
            } else if (window.confirm) {
                resolve(window.confirm(message));
            } else {
                resolve(true);
            }
        });
    }

    /**
     * Show toast notification
     */
    function showToast(message, type = 'info') {
        // Try to use Jellyfin's toast system
        if (window.Dashboard && window.Dashboard.alert) {
            window.Dashboard.alert(message);
        } else if (window.require) {
            try {
                const toast = window.require('toast');
                toast({ text: message });
            } catch (e) {
                console.log('[Baklava Catalogs]', type, ':', message);
            }
        } else {
            console.log('[Baklava Catalogs]', type, ':', message);
        }
    }

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
