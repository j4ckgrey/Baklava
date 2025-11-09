/**
 * Select to Cards - Simplified & Reliable
 * Converts playback version/audio/subtitle dropdowns to card carousels
 * Rewritten for simplicity, reliability, and faster initialization
 */
(function () {
    'use strict';
    
    console.log('[SelectToCards] Loading simplified version...');

    // ============================================
    // STATE & CONFIGURATION
    // ============================================
    
    let initialized = false;
    let currentItemId = null;
    let streamCache = new Map(); // Cache streams per mediaSourceId to avoid re-fetching
    
    // Clear cache when navigating away or player stops
    function clearStreamCache() {
        console.log('[SelectToCards] Clearing stream cache');
        streamCache.clear();
        // Don't clear currentItemId here - we might still need it
    }
    
    // ============================================
    // UTILITY FUNCTIONS
    // ============================================

    function debounce(func, wait) {
        let timeout;
        return function executedFunction(...args) {
            clearTimeout(timeout);
            timeout = setTimeout(() => func(...args), wait);
        };
    }

    function emitEvent(element, eventName) {
        const event = new Event(eventName, { bubbles: true, cancelable: true });
        element.dispatchEvent(event);
    }

    async function fetchStreams(itemId, mediaSourceId, forceRefresh = false) {
        const cacheKey = `${itemId}_${mediaSourceId}`;
        
        // Return cached result if available and fresh (under 3 seconds old) and not forced
        if (!forceRefresh && streamCache.has(cacheKey)) {
            const cached = streamCache.get(cacheKey);
            if (Date.now() - cached.timestamp < 3000) {
                console.log('[SelectToCards] Using cached streams for', cacheKey);
                return cached.data;
            }
        }
        
        console.log('[SelectToCards] Fetching streams for itemId:', itemId, 'mediaSourceId:', mediaSourceId);
        
        try {
            const params = new URLSearchParams({ itemId });
            if (mediaSourceId) params.append('mediaSourceId', mediaSourceId);
            // Add timestamp to force bypass any HTTP cache
            params.append('_t', Date.now().toString());

            const url = window.ApiClient.getUrl('api/baklava/metadata/streams') + '?' + params;
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            // Cache the result
            streamCache.set(cacheKey, {
                data: response,
                timestamp: Date.now()
            });

            return response;
        } catch (err) {
            console.error('[SelectToCards] Error fetching streams:', err);
            return null;
        }
    }

    function formatStreamTitle(stream, isAudio) {
        // Get language - convert to full name and capitalize first letter
        let langCode = stream.language || stream.displayLanguage || '';
        let lang = 'Unknown';
        
        if (langCode) {
            // Map common language codes to full names
            const langMap = {
                'eng': 'English',
                'spa': 'Spanish',
                'fre': 'French',
                'fra': 'French',
                'ger': 'German',
                'deu': 'German',
                'ita': 'Italian',
                'por': 'Portuguese',
                'rus': 'Russian',
                'jpn': 'Japanese',
                'kor': 'Korean',
                'chi': 'Chinese',
                'zho': 'Chinese',
                'ara': 'Arabic',
                'hin': 'Hindi',
                'tur': 'Turkish',
                'pol': 'Polish',
                'dut': 'Dutch',
                'nld': 'Dutch',
                'swe': 'Swedish',
                'nor': 'Norwegian',
                'dan': 'Danish',
                'fin': 'Finnish',
                'gre': 'Greek',
                'ell': 'Greek',
                'heb': 'Hebrew',
                'cze': 'Czech',
                'ces': 'Czech',
                'hun': 'Hungarian',
                'rum': 'Romanian',
                'ron': 'Romanian',
                'tha': 'Thai',
                'vie': 'Vietnamese',
                'ind': 'Indonesian',
                'may': 'Malay',
                'msa': 'Malay',
                'ukr': 'Ukrainian',
                'bul': 'Bulgarian',
                'hrv': 'Croatian',
                'srp': 'Serbian',
                'slv': 'Slovenian',
                'cat': 'Catalan'
            };
            
            const lowerCode = langCode.toLowerCase();
            if (langMap[lowerCode]) {
                lang = langMap[lowerCode];
            } else {
                // Capitalize first letter of unknown language
                lang = langCode.charAt(0).toUpperCase() + langCode.slice(1).toLowerCase();
            }
        }
        
        const codec = (stream.codec || '').toUpperCase();
        const type = isAudio 
            ? (stream.channels === 2 ? 'Stereo' : stream.channels ? `${stream.channels}ch` : codec)
            : codec;
        
        return { lang, type };
    }

    // ============================================
    // UI STYLING
    // ============================================

    function injectStyles() {
        if (document.getElementById('select-to-cards-style')) return;
        
        const style = document.createElement('style');
        style.id = 'select-to-cards-style';
        style.textContent = `
            /* Hide original select containers */
            form.trackSelections .selectContainer { display: none !important; }
            form.trackSelections { max-width: none !important; width: 100% !important; }
            
            /* Carousel wrapper */
            .stc-wrapper {
                position: relative;
                margin: 8px 0;
            }
            
            /* Control bar for arrows and label */
            .stc-controls {
                display: flex;
                align-items: center;
                justify-content: space-between;
                margin-bottom: 8px;
                min-height: 32px;
            }
            
            /* Carousel label (only for audio and subtitle) */
            .stc-label {
                font-size: 1.1em;
                font-weight: 500;
                color: rgba(255,255,255,0.9);
                text-align: center;
                flex: 1;
            }
            
            /* Navigation arrows */
            .stc-arrow {
                width: 32px;
                height: 32px;
                background: rgba(128,128,128,0.5);
                border: none;
                color: #fff;
                font-size: 20px;
                cursor: pointer;
                display: flex;
                align-items: center;
                justify-content: center;
                transition: background 0.15s;
            }
            
            .stc-arrow:hover:not(:disabled) {
                background: rgba(128,128,128,0.8);
            }
            
            .stc-arrow:disabled {
                opacity: 0.3;
                cursor: default;
            }
            
            .stc-arrow.stc-arrow-left { order: -1; }
            .stc-arrow.stc-arrow-right { order: 1; }
            
            /* Cards container */
            .stc-cards {
                display: flex;
                gap: 12px;
                overflow-x: auto;
                scroll-behavior: smooth;
                padding: 8px 4px;
                scrollbar-width: none;
                -ms-overflow-style: none;
                position: relative;
            }
            .stc-cards::-webkit-scrollbar { display: none; }
            
            /* Individual card */
            .stc-card {
                flex: 0 0 auto;
                width: 200px;
                height: 50px;
                background: rgba(255,255,255,0.08);
                border: 2px solid rgba(255,255,255,0.15);
                border-radius: 8px;
                padding: 16px;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                text-align: center;
                cursor: pointer;
                transition: all 0.15s ease;
                color: rgba(255,255,255,0.85);
                user-select: none;
            }
            
            .stc-card:hover:not(.stc-placeholder) {
                background: rgba(255,255,255,0.12);
                border-color: rgba(255,255,255,0.3);
                transform: translateY(-2px);
            }
            
            .stc-card.stc-selected {
                background: #00a4dc !important;
                border-color: #00a4dc !important;
                color: #fff !important;
            }
            
            .stc-card.stc-placeholder {
                opacity: 0.5;
                cursor: default;
            }
            
            .stc-card.stc-loading {
                border-style: dashed;
                border-color: rgba(255,255,255,0.2);
                background: rgba(100,150,255,0.1);
                position: relative;
                overflow: hidden;
            }
            
            .stc-card.stc-loading::after {
                content: '';
                position: absolute;
                top: 0;
                left: -100%;
                width: 100%;
                height: 100%;
                background: linear-gradient(90deg, transparent, rgba(255,255,255,0.1), transparent);
                animation: shimmer 1.5s infinite;
            }
            
            @keyframes shimmer {
                to { left: 100%; }
            }
            
            .stc-card.stc-empty {
                border-style: dashed;
                border-color: rgba(255,255,255,0.15);
                background: transparent;
                cursor: default;
            }
            
            /* Track cards (audio/subtitle) - smaller & compact */
            .stc-card.stc-track {
                width: 80px;
                height: 30px;
                padding: 8px;
            }
            
            .stc-card-lang {
                font-size: 14px;
                font-weight: 700;
                margin-bottom: 4px;
            }
            
            .stc-card-type {
                font-size: 11px;
                opacity: 0.85;
            }
            
            .stc-card-title {
                font-size: 13px;
                line-height: 1.3;
                word-break: break-word;
            }
            
            /* Loading state */
            .stc-loading {
                color: rgba(255,255,255,0.6);
                font-size: 13px;
                padding: 24px;
                text-align: center;
            }
            
            /* Format display */
            .stc-format {
                margin-bottom: 16px;
                padding: 12px 16px;
                background: rgba(255,255,255,0.08);
                border: 1px solid rgba(255,255,255,0.15);
                border-radius: 6px;
                text-align: center;
                color: rgba(255,255,255,0.8);
                font-size: 14px;
            }
            
            /* Filename display */
            .stc-filename {
                margin-top: 8px;
                padding: 8px 12px;
                background: rgba(0,0,0,0.3);
                border-radius: 4px;
                color: rgba(255,255,255,0.6);
                font-size: 12px;
                text-align: center;
                font-family: monospace;
            }
            
            /* Separator line after carousels */
            .stc-separator {
                margin: 20px 0;
                height: 1px;
                background: rgba(255,255,255,0.1);
                border: none;
            }
        `;
        
        document.head.appendChild(style);
    }

    // ============================================
    // CARD CREATION
    // ============================================

    function createCard(option, type, select) {
        const card = document.createElement('div');
        const isTrack = type === 'audio' || type === 'subtitle';
        
        card.className = `stc-card ${isTrack ? 'stc-track' : ''}`;
        card.dataset.value = option.value;
        card.tabIndex = 0;
        
        if (option.selected) {
            card.classList.add('stc-selected');
        }
        
        if (option.disabled) {
            card.style.opacity = '0.4';
            card.style.cursor = 'not-allowed';
            card.tabIndex = -1;
        }
        
        // Build card content
        if (isTrack && option._meta) {
            const { lang, type: trackType } = formatStreamTitle(option._meta, type === 'audio');
            card.innerHTML = `
                <div class="stc-card-lang">${lang}</div>
                <div class="stc-card-type">${trackType}</div>
            `;
        } else {
            // Version cards - show compact title
            const text = option.textContent || option.value;
            const cleanText = text.includes('(cut)') ? text.split('(cut)')[1].trim() : text;
            card.innerHTML = `<div class="stc-card-title">${cleanText}</div>`;
        }
        
        // Handle click
        if (!option.disabled) {
            card.addEventListener('click', () => selectCard(select, option.value, type));
        }
        
        return card;
    }

    function createPlaceholderCard(message = 'No tracks') {
        const card = document.createElement('div');
        card.className = 'stc-card stc-track stc-placeholder';
        card.innerHTML = `<div style="font-size:12px;color:rgba(255,255,255,0.5);">${message}</div>`;
        card.tabIndex = -1;
        return card;
    }

    function createLoadingCard() {
        const card = document.createElement('div');
        card.className = 'stc-card stc-track stc-loading';
        card.tabIndex = -1;
        return card;
    }

    function createEmptyCard() {
        const card = document.createElement('div');
        card.className = 'stc-card stc-track stc-empty';
        card.tabIndex = -1;
        return card;
    }

    function createArrows(controlsDiv, cardsContainer) {
        const leftArrow = document.createElement('button');
        leftArrow.className = 'stc-arrow stc-arrow-left';
        leftArrow.innerHTML = '‹';
        leftArrow.setAttribute('aria-label', 'Previous');
        
        const rightArrow = document.createElement('button');
        rightArrow.className = 'stc-arrow stc-arrow-right';
        rightArrow.innerHTML = '›';
        rightArrow.setAttribute('aria-label', 'Next');
        
        const updateArrows = () => {
            leftArrow.disabled = cardsContainer.scrollLeft <= 0;
            rightArrow.disabled = cardsContainer.scrollLeft >= cardsContainer.scrollWidth - cardsContainer.offsetWidth - 1;
        };
        
        leftArrow.addEventListener('click', () => {
            cardsContainer.scrollBy({ left: -300, behavior: 'smooth' });
            setTimeout(updateArrows, 100);
        });
        
        rightArrow.addEventListener('click', () => {
            cardsContainer.scrollBy({ left: 300, behavior: 'smooth' });
            setTimeout(updateArrows, 100);
        });
        
        cardsContainer.addEventListener('scroll', debounce(updateArrows, 100));
        
        controlsDiv.appendChild(leftArrow);
        controlsDiv.appendChild(rightArrow);
        
        setTimeout(updateArrows, 100);
    }

    // ============================================
    // SELECTION HANDLING
    // ============================================

    function selectCard(select, value, type) {
        console.log('[SelectToCards] Selecting', type, 'value:', value);
        
        // Update select element
        Array.from(select.options).forEach(opt => {
            opt.selected = opt.value === value;
        });
        
        // Update card UI
        const wrapper = select._stcWrapper;
        if (wrapper) {
            wrapper.querySelectorAll('.stc-card').forEach(card => {
                card.classList.toggle('stc-selected', card.dataset.value === value);
            });
            
            // Update filename display if version select
            if (type === 'version') {
                const selectedOption = Array.from(select.options).find(opt => opt.value === value);
                const filenameDiv = wrapper.querySelector('.stc-filename');
                if (selectedOption && filenameDiv) {
                    filenameDiv.textContent = selectedOption.textContent;
                }
            }
        }
        
        // Emit change events
        emitEvent(select, 'change');
        emitEvent(select, 'input');
        
        // If version changed, load new audio/subtitle tracks
        if (type === 'version') {
            loadTracksForVersion(select, value);
        }
    }

    async function loadTracksForVersion(versionSelect, mediaSourceId) {
        console.log('[SelectToCards] Loading tracks for version:', mediaSourceId);
        
        const form = versionSelect.closest('form.trackSelections');
        if (!form) return;
        
        const audioSelect = form.querySelector('select.selectAudio');
        const subtitleSelect = form.querySelector('select.selectSubtitles');
        
        // Clear existing tracks immediately
        if (audioSelect) {
            audioSelect.innerHTML = '';
            if (audioSelect._stcCards) {
                audioSelect._stcCards.innerHTML = '';
                // Add 3 loading cards
                for (let i = 0; i < 3; i++) {
                    audioSelect._stcCards.appendChild(createLoadingCard());
                }
            }
        }
        
        if (subtitleSelect) {
            subtitleSelect.innerHTML = '';
            if (subtitleSelect._stcCards) {
                subtitleSelect._stcCards.innerHTML = '';
                // Add 3 loading cards
                for (let i = 0; i < 3; i++) {
                    subtitleSelect._stcCards.appendChild(createLoadingCard());
                }
            }
        }
        
        // Try to get itemId if we don't have it
        if (!currentItemId) {
            console.log('[SelectToCards] No currentItemId, attempting to capture...');
            captureItemId();
        }
        
        // Fetch streams
        if (!currentItemId) {
            console.warn('[SelectToCards] No itemId available after capture attempt');
            if (audioSelect?._stcCards) {
                audioSelect._stcCards.innerHTML = '';
                for (let i = 0; i < 3; i++) {
                    audioSelect._stcCards.appendChild(createEmptyCard());
                }
            }
            if (subtitleSelect?._stcCards) {
                subtitleSelect._stcCards.innerHTML = '';
                for (let i = 0; i < 3; i++) {
                    subtitleSelect._stcCards.appendChild(createEmptyCard());
                }
            }
            return;
        }
        
        console.log('[SelectToCards] Fetching streams with itemId:', currentItemId, 'mediaSourceId:', mediaSourceId);
        const streams = await fetchStreams(currentItemId, mediaSourceId);
        
        if (!streams) {
            console.error('[SelectToCards] Failed to fetch streams');
            if (audioSelect?._stcCards) {
                audioSelect._stcCards.innerHTML = '';
                for (let i = 0; i < 3; i++) {
                    audioSelect._stcCards.appendChild(createEmptyCard());
                }
            }
            if (subtitleSelect?._stcCards) {
                subtitleSelect._stcCards.innerHTML = '';
                for (let i = 0; i < 3; i++) {
                    subtitleSelect._stcCards.appendChild(createEmptyCard());
                }
            }
            return;
        }
        
        // Populate audio tracks
        if (audioSelect && streams.audio && streams.audio.length > 0) {
            streams.audio.forEach((track, idx) => {
                const option = document.createElement('option');
                option.value = String(track.index);
                option.textContent = track.title;
                option._meta = track;
                if (idx === 0) option.selected = true;
                audioSelect.appendChild(option);
            });
            populateCarousel(audioSelect, 'audio');
        } else if (audioSelect?._stcCards) {
            audioSelect._stcCards.innerHTML = '';
            // Add 3 empty state cards
            for (let i = 0; i < 3; i++) {
                audioSelect._stcCards.appendChild(createEmptyCard());
            }
        }
        
        // Populate subtitle tracks
        if (subtitleSelect && streams.subs && streams.subs.length > 0) {
            streams.subs.forEach((track, idx) => {
                const option = document.createElement('option');
                option.value = String(track.index);
                option.textContent = track.title;
                option._meta = track;
                if (idx === 0) option.selected = true;
                subtitleSelect.appendChild(option);
            });
            populateCarousel(subtitleSelect, 'subtitle');
        } else if (subtitleSelect?._stcCards) {
            subtitleSelect._stcCards.innerHTML = '';
            // Add 3 empty state cards
            for (let i = 0; i < 3; i++) {
                subtitleSelect._stcCards.appendChild(createEmptyCard());
            }
        }
    }

    // ============================================
    // CAROUSEL CREATION
    // ============================================

    function populateCarousel(select, type) {
        if (!select) return;
        
        // Get or create wrapper
        let wrapper = select._stcWrapper;
        let cardsContainer = select._stcCards;
        let controlsDiv = select._stcControls;
        
        if (!wrapper) {
            wrapper = document.createElement('div');
            wrapper.className = 'stc-wrapper';
            
            // Create controls div above carousel
            controlsDiv = document.createElement('div');
            controlsDiv.className = 'stc-controls';
            
            // Add label only for audio and subtitle tracks
            if (type === 'audio' || type === 'subtitle') {
                const labelDiv = document.createElement('div');
                labelDiv.className = 'stc-label';
                labelDiv.textContent = type === 'audio' ? 'Audio Track' : 'Subtitles';
                controlsDiv.appendChild(labelDiv);
            }
            
            wrapper.appendChild(controlsDiv);
            
            cardsContainer = document.createElement('div');
            cardsContainer.className = 'stc-cards';
            
            wrapper.appendChild(cardsContainer);
            
            // Insert after the select's container
            const selectContainer = select.closest('.selectContainer');
            if (selectContainer) {
                selectContainer.parentNode.insertBefore(wrapper, selectContainer.nextSibling);
            } else {
                const form = select.closest('form');
                if (form) form.appendChild(wrapper);
            }
            
            select._stcWrapper = wrapper;
            select._stcCards = cardsContainer;
            select._stcControls = controlsDiv;
            
            createArrows(controlsDiv, cardsContainer);
        }
        
        // Clear and populate cards
        cardsContainer.innerHTML = '';
        
        if (select.options.length === 0) {
            cardsContainer.appendChild(createPlaceholderCard());
            return;
        }
        
        Array.from(select.options).forEach(option => {
            cardsContainer.appendChild(createCard(option, type, select));
        });
        
        // Add filename display if version select
        if (type === 'version' && select.options.length > 0) {
            const selectedOption = Array.from(select.options).find(opt => opt.selected) || select.options[0];
            if (selectedOption && selectedOption.textContent) {
                // Remove existing filename if any
                const existingFilename = wrapper.querySelector('.stc-filename');
                if (existingFilename) {
                    existingFilename.remove();
                }
                
                const filenameDiv = document.createElement('div');
                filenameDiv.className = 'stc-filename';
                filenameDiv.textContent = selectedOption.textContent;
                wrapper.appendChild(filenameDiv);
            }
        }
        
        // Add separator line after each carousel
        const existingSeparator = wrapper.nextElementSibling;
        if (!existingSeparator || !existingSeparator.classList.contains('stc-separator')) {
            const separator = document.createElement('hr');
            separator.className = 'stc-separator';
            wrapper.parentNode.insertBefore(separator, wrapper.nextSibling);
        }
        
        // Auto-scroll to selected card
        setTimeout(() => {
            const selectedCard = cardsContainer.querySelector('.stc-selected');
            if (selectedCard) {
                selectedCard.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
            }
        }, 100);
    }

    // ============================================
    // INITIALIZATION
    // ============================================

    function captureItemId() {
        // Try multiple sources to get the current item ID
        
        // 1. From URL
        const urlMatch = window.location.href.match(/[?&]id=([a-f0-9-]+)/i);
        if (urlMatch) {
            currentItemId = urlMatch[1].replace(/-/g, '');
            console.log('[SelectToCards] Captured itemId from URL:', currentItemId);
            return;
        }
        
        // 2. From form
        const form = document.querySelector('form.trackSelections');
        if (form) {
            const itemIdInput = form.querySelector('input[name*="itemId"], input[name*="Id"]');
            if (itemIdInput?.value) {
                currentItemId = itemIdInput.value.replace(/-/g, '');
                console.log('[SelectToCards] Captured itemId from form input:', currentItemId);
                return;
            }
        }
        
        // 3. From global window variables
        if (window.__currentPlaybackItemId) {
            currentItemId = window.__currentPlaybackItemId;
            console.log('[SelectToCards] Using window.__currentPlaybackItemId:', currentItemId);
            return;
        }
        
        console.warn('[SelectToCards] Could not capture itemId');
    }

    function initializeForm() {
        const form = document.querySelector('form.trackSelections');
        if (!form || form._stcInitialized) return;
        
        console.log('[SelectToCards] Initializing form');
        form._stcInitialized = true;
        
        captureItemId();
        
        // Find all select elements (but skip selectVideo)
        const selects = Array.from(form.querySelectorAll('select.detailTrackSelect'))
            .filter(sel => !sel.classList.contains('selectVideo'));
        
        if (selects.length === 0) {
            console.warn('[SelectToCards] No select elements found');
            return;
        }
        
        console.log('[SelectToCards] Found', selects.length, 'select elements');
        
        // Process each select
        selects.forEach(select => {
            let type = 'unknown';
            if (select.classList.contains('selectSource')) type = 'version';
            else if (select.classList.contains('selectAudio')) type = 'audio';
            else if (select.classList.contains('selectSubtitles')) type = 'subtitle';
            
            console.log('[SelectToCards] Processing select:', type, 'options:', select.options.length);
            
            // Watch for options being added dynamically
            const selectObserver = new MutationObserver(() => {
                if (select.options.length > 0 && !select._stcCardsPopulated) {
                    console.log('[SelectToCards] Options added to', type, 'select, populating carousel');
                    select._stcCardsPopulated = true;
                    populateCarousel(select, type);
                    
                    if (type === 'version') {
                        const selectedOption = Array.from(select.options).find(opt => opt.selected);
                        if (selectedOption) {
                            setTimeout(() => {
                                loadTracksForVersion(select, selectedOption.value);
                            }, 100);
                        }
                    }
                }
            });
            
            selectObserver.observe(select, { childList: true });
            
            // For version select, populate immediately if options exist OR wait for them
            if (type === 'version') {
                if (select.options.length > 0) {
                    console.log('[SelectToCards] Version has options, populating now');
                    select._stcCardsPopulated = true;
                    populateCarousel(select, type);
                    
                    // Auto-load tracks for the first selected version
                    const selectedOption = Array.from(select.options).find(opt => opt.selected);
                    if (selectedOption) {
                        setTimeout(() => {
                            loadTracksForVersion(select, selectedOption.value);
                        }, 100);
                    }
                } else {
                    console.log('[SelectToCards] Version select waiting for options (observer active)...');
                }
            }
            // For audio/subtitle, create empty carousel with placeholder
            else if (type === 'audio' || type === 'subtitle') {
                populateCarousel(select, type);
            }
        });
    }

    // ============================================
    // OBSERVERS & HOOKS
    // ============================================

    function setupObservers() {
        // Watch for form additions
        const observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    if (node.nodeType !== 1) continue;
                    
                    if (node.matches?.('form.trackSelections') || node.querySelector?.('form.trackSelections')) {
                        setTimeout(initializeForm, 50);
                    }
                }
            }
        });
        
        observer.observe(document.body, { childList: true, subtree: true });
        
        // Also check on modal open events
        document.addEventListener('openDetailsModal', () => {
            setTimeout(initializeForm, 100);
        });
        
        // Clear cache when navigating away (detect page changes)
        let lastUrl = window.location.href;
        const urlCheckInterval = setInterval(() => {
            const currentUrl = window.location.href;
            if (currentUrl !== lastUrl) {
                // Only log if we actually had a cache to clear
                if (streamCache.size > 0) {
                    console.log('[SelectToCards] URL changed, clearing cache');
                }
                clearStreamCache();
                lastUrl = currentUrl;
            }
        }, 2000); // Check every 2 seconds instead of 1
        
        // Listen for player stop/exit events
        document.addEventListener('playbackstop', () => {
            console.log('[SelectToCards] Playback stopped, clearing cache');
            clearStreamCache();
        });
        
        document.addEventListener('playbackerror', () => {
            console.log('[SelectToCards] Playback error, clearing cache');
            clearStreamCache();
        });
        
        // Watch for modal/dialog closures (when user exits playback UI)
        const modalObserver = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                for (const node of mutation.removedNodes) {
                    if (node.nodeType !== 1) continue;
                    
                    // If the details modal or player UI is removed, clear cache
                    if (node.id === 'item-detail-modal-overlay' || 
                        node.classList?.contains('videoPlayerContainer') ||
                        node.querySelector?.('form.trackSelections')) {
                        console.log('[SelectToCards] Player UI removed, clearing cache');
                        clearStreamCache();
                    }
                }
            }
        });
        
        modalObserver.observe(document.body, { childList: true, subtree: true });
        
        // Watch for API calls to capture itemId
        if (window.ApiClient?.ajax) {
            const originalAjax = window.ApiClient.ajax;
            window.ApiClient.ajax = function(options) {
                // Try to extract itemId from API calls
                if (options?.url) {
                    const match = options.url.match(/\/Items\/([a-f0-9-]+)/i);
                    if (match) {
                        const extractedId = match[1].replace(/-/g, '');
                        if (extractedId.length === 32) {
                            // If itemId changed, clear cache
                            if (currentItemId && currentItemId !== extractedId) {
                                console.log('[SelectToCards] Item changed, clearing cache');
                                clearStreamCache();
                            }
                            currentItemId = extractedId;
                            console.log('[SelectToCards] Intercepted itemId from API:', currentItemId);
                        }
                    }
                }
                
                return originalAjax.apply(this, arguments);
            };
        }
    }

    // ============================================
    // STARTUP
    // ============================================

    function init() {
        if (initialized) return;
        initialized = true;
        
        console.log('[SelectToCards] Initializing...');
        
        injectStyles();
        setupObservers();
        
        // Check if form already exists
        if (document.querySelector('form.trackSelections')) {
            initializeForm();
        }
    }

    // Start on DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    console.log('[SelectToCards] Simplified version loaded');
})();
