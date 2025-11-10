/*
 * select-to-select.js
 * Alternate UI: use <select> dropdowns instead of card carousels.
 */
(function () {
    'use strict';

    console.log('[SelectToSelect] Loading alternative select UI...');

    let currentItemId = null;

    // Parse version text same as cards
    function parseVersionText(text) {
        const qualityMatch = text.match(/(\d{3,4}p)/i);
        const quality = qualityMatch ? qualityMatch[1] : 'Unknown';
        const tokenRegex = /WEB-?DL|WEB|NF|DV|HEVC|HDR10\+?|HDR10|HDR|BLURAY|BLU-?RAY|BDRIP|BRRIP|X264|X265|H264|H265|UHD|REMUX/ig;
        const tokens = (text.match(tokenRegex) || []).map(t => t.toUpperCase());
        const versionLabel = tokens.join(' ') || 'Standard';
        return `${quality} - ${versionLabel}`;
    }

    // Format stream title (simplified from cards)
    function formatStreamTitle(stream, isAudio) {
        if (!stream) return 'Unknown';
        const lang = stream.Language || stream.DisplayLanguage || 'Unknown';
        const codec = stream.Codec?.toUpperCase() || '';
        const channels = stream.Channels ? `${stream.Channels}.0` : '';
        const parts = [lang];
        if (codec) parts.push(codec);
        if (channels) parts.push(channels);
        return parts.join(' | ');
    }

    // Fetch streams from API (same endpoint as cards)
    async function fetchStreams(itemId, mediaSourceId) {
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
            console.error('[SelectToSelect] Error fetching streams:', err);
            return null;
        }
    }

    // Populate audio/subtitle selects from fetched streams
    async function populateTrackSelects(mediaSourceId) {
        if (!currentItemId) return;
        
        const streams = await fetchStreams(currentItemId, mediaSourceId);
        if (!streams) return;

        const audioSelect = document.querySelector('select#selectAudioTrack');
        const subtitleSelect = document.querySelector('select#selectSubtitleTrack');

        // Populate audio
        if (audioSelect && streams.audioStreams) {
            const currentValue = audioSelect.value;
            audioSelect.innerHTML = '';
            streams.audioStreams.forEach(stream => {
                const option = document.createElement('option');
                option.value = stream.Index;
                option.textContent = formatStreamTitle(stream, true);
                if (stream.Index == currentValue) option.selected = true;
                audioSelect.appendChild(option);
            });
        }

        // Populate subtitle
        if (subtitleSelect && streams.subtitleStreams) {
            const currentValue = subtitleSelect.value;
            subtitleSelect.innerHTML = '<option value="-1">None</option>';
            streams.subtitleStreams.forEach(stream => {
                const option = document.createElement('option');
                option.value = stream.Index;
                option.textContent = formatStreamTitle(stream, false);
                if (stream.Index == currentValue) option.selected = true;
                subtitleSelect.appendChild(option);
            });
        }
    }

    function enhanceSelects() {
        const versionSelect = document.querySelector('select#selectMediaSource');
        if (versionSelect && !versionSelect._stcFilename) {
            // Create filename display (use stc-filename class to match cards)
            const filenameDiv = document.createElement('div');
            filenameDiv.className = 'stc-filename';
            filenameDiv.style.cssText = 'margin: 8px 0; padding: 8px 12px; background: rgba(0,0,0,0.3); border-radius: 4px; color: rgba(255,255,255,0.6); font-size: 12px; text-align: center; font-family: monospace;';
            
            const container = versionSelect.closest('.selectContainer');
            if (container) container.parentNode.insertBefore(filenameDiv, container);
            
            versionSelect._stcFilename = filenameDiv;
            
            // Update filename on change
            const updateFilename = () => {
                const selectedOption = versionSelect.options[versionSelect.selectedIndex];
                if (selectedOption) {
                    // Show original filename in the stc-filename div
                    const originalText = selectedOption.getAttribute('data-original') || selectedOption.textContent;
                    filenameDiv.textContent = originalText;
                }
            };
            
            // Parse and format version options - keep original in data attribute, show parsed in option text
            Array.from(versionSelect.options).forEach(option => {
                if (!option.hasAttribute('data-original')) {
                    // Store the original filename
                    option.setAttribute('data-original', option.textContent);
                    // Set the parsed version as the visible text
                    option.textContent = parseVersionText(option.textContent);
                }
            });
            
            updateFilename();
            
            // When version changes, fetch and populate audio/subtitle tracks
            versionSelect.addEventListener('change', () => {
                updateFilename();
                const mediaSourceId = versionSelect.value;
                populateTrackSelects(mediaSourceId);
            });
        }
    }

    function initToggle() {
        const form = document.querySelector('form.trackSelections');
        if (!form) return;
        if (document.getElementById('stc-mode-toggle')) return;

        // Extract itemId from form or URL
        if (!currentItemId) {
            const urlParams = new URLSearchParams(window.location.search);
            currentItemId = urlParams.get('id');
        }

        const wrapper = document.createElement('div');
        wrapper.id = 'stc-mode-toggle';
        wrapper.style.cssText = 'display:flex;gap:8px;align-items:center;margin:6px 0;';

        const label = document.createElement('span');
        label.textContent = 'View:';
        label.style.color = 'rgba(255,255,255,0.85)';

        const select = document.createElement('select');
        select.style.cssText = 'background:rgba(0,0,0,0.35);color:#fff;padding:4px;border-radius:4px;border:1px solid rgba(255,255,255,0.08);';
        const optCards = document.createElement('option'); optCards.value = 'cards'; optCards.textContent = 'Cards';
        const optSelects = document.createElement('option'); optSelects.value = 'selects'; optSelects.textContent = 'Selects';
        select.appendChild(optCards); select.appendChild(optSelects);

        wrapper.appendChild(label);
        wrapper.appendChild(select);
        form.parentNode.insertBefore(wrapper, form);

        const mode = localStorage.getItem('baklava_view_mode') || 'cards';
        select.value = mode;
        applyMode(mode);

        select.addEventListener('change', () => {
            localStorage.setItem('baklava_view_mode', select.value);
            applyMode(select.value);
        });
    }

    function applyMode(mode) {
        if (mode === 'selects') {
            document.body.classList.add('stc-show-selects');
            enhanceSelects();
        } else {
            document.body.classList.remove('stc-show-selects');
        }
    }

    function start() {
        initToggle();
        const observer = new MutationObserver(() => {
            initToggle();
            if (document.body.classList.contains('stc-show-selects')) {
                enhanceSelects();
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else start();
})();
