/**
 * Simple Tracks - Minimal version/audio/subtitle handler for dropdown mode
 * Populates audio and subtitle selects when version changes
 */
(function () {
    'use strict';
    
    if (window.SimpleTracksLoaded) {
        console.log('[SimpleTracks] Already loaded');
        return;
    }
    window.SimpleTracksLoaded = true;
    
    console.log('[SimpleTracks] Loading...');

    let currentItemId = null;

    function captureItemId() {
        const urlMatch = window.location.href.match(/[?&]id=([a-f0-9]+)/i);
        if (urlMatch) {
            currentItemId = urlMatch[1];
            console.log('[SimpleTracks] Captured itemId from URL:', currentItemId);
            return currentItemId;
        }
        return null;
    }

    async function fetchStreams(itemId, mediaSourceId) {
        console.log('[SimpleTracks] Fetching streams for itemId:', itemId, 'mediaSourceId:', mediaSourceId);
        
        try {
            const params = new URLSearchParams({ itemId });
            if (mediaSourceId) params.append('mediaSourceId', mediaSourceId);
            params.append('_t', Date.now().toString());

            const url = window.ApiClient.getUrl('api/baklava/metadata/streams') + '?' + params;
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            return response;
        } catch (err) {
            console.error('[SimpleTracks] Error fetching streams:', err);
            return null;
        }
    }

    async function loadTracksForVersion(versionSelect, mediaSourceId) {
        console.log('[SimpleTracks] Loading tracks for version:', mediaSourceId);
        
        const form = versionSelect.closest('form.trackSelections');
        if (!form) return;
        
        const audioSelect = form.querySelector('select.selectAudio');
        const subtitleSelect = form.querySelector('select.selectSubtitles');
        
        // Enable the selects (they might be disabled by Jellyfin)
        if (audioSelect) {
            audioSelect.disabled = false;
            audioSelect.innerHTML = '';
        }
        if (subtitleSelect) {
            subtitleSelect.disabled = false;
            subtitleSelect.innerHTML = '';
        }
        
        // Show the containers
        const audioContainer = form.querySelector('.selectAudioContainer');
        const subtitleContainer = form.querySelector('.selectSubtitlesContainer');
        if (audioContainer) audioContainer.classList.remove('hide');
        if (subtitleContainer) subtitleContainer.classList.remove('hide');
        
        // Get itemId if needed
        if (!currentItemId) {
            captureItemId();
        }
        
        if (!currentItemId) {
            console.warn('[SimpleTracks] No itemId available');
            return;
        }
        
        // Fetch streams
        const streams = await fetchStreams(currentItemId, mediaSourceId);
        
        if (!streams) {
            console.error('[SimpleTracks] Failed to fetch streams');
            return;
        }
        
        console.log('[SimpleTracks] Received streams:', streams);
        
        // Populate audio tracks
        if (audioSelect) {
            if (streams.audio && streams.audio.length > 0) {
                streams.audio.forEach((track, idx) => {
                    const option = document.createElement('option');
                    option.value = String(track.index);
                    option.textContent = track.title;
                    if (idx === 0) option.selected = true;
                    audioSelect.appendChild(option);
                });
                console.log('[SimpleTracks] Populated', streams.audio.length, 'audio tracks');
            } else {
                console.log('[SimpleTracks] No audio tracks found in response');
            }
        } else {
            console.warn('[SimpleTracks] No audio select element found');
        }
        
        // Populate subtitle tracks
        if (subtitleSelect) {
            if (streams.subs && streams.subs.length > 0) {
                streams.subs.forEach((track, idx) => {
                    const option = document.createElement('option');
                    option.value = String(track.index);
                    option.textContent = track.title;
                    if (idx === 0) option.selected = true;
                    subtitleSelect.appendChild(option);
                });
                console.log('[SimpleTracks] Populated', streams.subs.length, 'subtitle tracks');
            } else {
                console.log('[SimpleTracks] No subtitle tracks found in response');
            }
        } else {
            console.warn('[SimpleTracks] No subtitle select element found');
        }
    }

    function updateFilenameDisplay(versionSelect) {
        if (!versionSelect) return;
        
        let filenameDiv = versionSelect._filenameDiv;
        
        if (!filenameDiv) {
            filenameDiv = document.createElement('div');
            filenameDiv.className = 'stc-filename';
            const versionContainer = versionSelect.closest('.selectContainer');
            if (versionContainer) {
                versionContainer.parentNode.insertBefore(filenameDiv, versionContainer);
            }
            versionSelect._filenameDiv = filenameDiv;
        }
        
        const selectedOption = Array.from(versionSelect.options).find(opt => opt.selected);
        if (selectedOption) {
            if (!selectedOption.hasAttribute('data-original')) {
                selectedOption.setAttribute('data-original', selectedOption.textContent);
            }
            filenameDiv.textContent = selectedOption.getAttribute('data-original') || selectedOption.textContent;
        }
    }

    function initializeForm() {
        const form = document.querySelector('form.trackSelections');
        if (!form || form._simpleTracksInitialized) return;
        
        console.log('[SimpleTracks] Initializing form');
        form._simpleTracksInitialized = true;
        
        captureItemId();
        
        const versionSelect = form.querySelector('select.selectSource');
        if (!versionSelect) {
            console.warn('[SimpleTracks] No version select found');
            return;
        }
        
        // Watch for when version select gets its options populated
        const selectObserver = new MutationObserver(() => {
            if (versionSelect.options.length > 0 && !versionSelect._simpleTracksPopulated) {
                versionSelect._simpleTracksPopulated = true;
                console.log('[SimpleTracks] Version select populated with', versionSelect.options.length, 'options');
                
                updateFilenameDisplay(versionSelect);
                
                // Auto-load tracks for the first selected version
                const selectedOption = Array.from(versionSelect.options).find(opt => opt.selected);
                if (selectedOption) {
                    console.log('[SimpleTracks] Loading tracks for initial version:', selectedOption.value);
                    setTimeout(() => {
                        loadTracksForVersion(versionSelect, selectedOption.value);
                    }, 100);
                }
            }
        });
        selectObserver.observe(versionSelect, { childList: true });
        
        // Listen for version changes
        versionSelect.addEventListener('change', function() {
            const selectedValue = this.value;
            if (selectedValue) {
                console.log('[SimpleTracks] Version changed to:', selectedValue);
                updateFilenameDisplay(this);
                loadTracksForVersion(this, selectedValue);
            }
        });
        
        // If version select already has options, load immediately
        if (versionSelect.options.length > 0) {
            versionSelect._simpleTracksPopulated = true;
            updateFilenameDisplay(versionSelect);
            const selectedOption = Array.from(versionSelect.options).find(opt => opt.selected) || versionSelect.options[0];
            if (selectedOption) {
                console.log('[SimpleTracks] Loading tracks for initial version (already populated):', selectedOption.value);
                setTimeout(() => {
                    loadTracksForVersion(versionSelect, selectedOption.value);
                }, 100);
            }
        }
    }

    function setupObserver() {
        const observer = new MutationObserver(() => {
            const form = document.querySelector('form.trackSelections');
            if (form && !form._simpleTracksInitialized) {
                setTimeout(initializeForm, 50);
            }
        });
        
        observer.observe(document.body, { childList: true, subtree: true });
        
        // Also try to init immediately if form already exists
        setTimeout(initializeForm, 100);
    }

    // Start when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupObserver);
    } else {
        setupObserver();
    }

    console.log('[SimpleTracks] Loaded');
})();
