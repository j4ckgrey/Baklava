/**
 * Select to Cards - Standalone
 * Converts playback version/audio/subtitle dropdowns to card carousels
 * All utilities inlined - no dependencies on shared-utils.js
 */
(function () {
    'use strict';
    
    console.log('[SelectToCards] Loading standalone version...');

    // ============================================
    // UTILITY FUNCTIONS (Inlined)
    // ============================================

    function throttle(func, limit = 300) {
        let lastCall = 0;
        return function(...args) {
            const now = Date.now();
            if (now - lastCall >= limit) {
                lastCall = now;
                func(...args);
            }
        };
    }

    function emitEvent(element, eventName, bubbles = true) {
        try {
            const event = new Event(eventName, { bubbles });
            element.dispatchEvent(event);
        } catch (e) {
            const event = document.createEvent('HTMLEvents');
            event.initEvent(eventName, bubbles, false);
            element.dispatchEvent(event);
        }
    }

    async function probeItemStreams(itemId, mediaSourceId) {
        console.log('[SelectToCards.probeItemStreams] Called with itemId:', itemId, 'mediaSourceId:', mediaSourceId);
        
        if (!itemId) {
            console.warn('[SelectToCards.probeItemStreams] No itemId');
            return null;
        }

        try {
            if (!window.ApiClient) {
                console.warn('[SelectToCards.probeItemStreams] No ApiClient');
                return null;
            }
            
            const params = new URLSearchParams();
            params.append('itemId', itemId);
            if (mediaSourceId) params.append('mediaSourceId', mediaSourceId);

            const url = window.ApiClient.getUrl('api/myplugin/metadata/streams') + '?' + params.toString();
            console.log('[SelectToCards.probeItemStreams] Fetching URL:', url);
            
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            console.log('[SelectToCards.probeItemStreams] Backend response:', response);

            if (response) {
                const result = {
                    Id: response.mediaSourceId,
                    MediaStreams: [
                        ...response.audio.map(a => ({
                            Type: 'Audio',
                            Index: a.index,
                            DisplayTitle: a.title,
                            Title: a.title,
                            Language: a.displayLanguage || a.language,
                            Codec: a.codec,
                            Channels: a.channels,
                            BitRate: a.bitrate
                        })),
                        ...response.subs.map(s => ({
                            Type: 'Subtitle',
                            Index: s.index,
                            DisplayTitle: s.title,
                            Title: s.title,
                            Language: s.displayLanguage || s.language,
                            Codec: s.codec,
                            IsForced: s.isForced,
                            IsDefault: s.isDefault
                        }))
                    ]
                };
                console.log('[SelectToCards.probeItemStreams] Returning formatted result:', result);
                return result;
            }

            console.warn('[SelectToCards.probeItemStreams] No response from backend');
            return null;
        } catch (err) {
            console.error('[SelectToCards.probeItemStreams] Error:', err);
            return null;
        }
    }

    function extractStreams(mediaSource) {
        const result = { audio: [], subs: [] };

        if (!mediaSource?.MediaStreams?.length) {
            console.warn('[SelectToCards.extractStreams] No MediaStreams in source:', mediaSource);
            return result;
        }

        console.log('[SelectToCards.extractStreams] Processing', mediaSource.MediaStreams.length, 'streams');

        mediaSource.MediaStreams.forEach((stream) => {
            if (stream.Type === 'Audio') {
                const lang = stream.Language ? ` (${stream.Language})` : '';
                const codec = stream.Codec ? ` [${stream.Codec}]` : '';
                result.audio.push({
                    index: stream.Index,
                    title: (stream.DisplayTitle || stream.Title || `Audio ${stream.Index}`) + lang + codec,
                    language: stream.Language,
                    codec: stream.Codec
                });
            } else if (stream.Type === 'Subtitle') {
                const lang = stream.Language ? ` (${stream.Language})` : '';
                const codec = stream.Codec ? ` [${stream.Codec}]` : '';
                result.subs.push({
                    index: stream.Index,
                    title: (stream.DisplayTitle || stream.Title || `Subtitle ${stream.Index}`) + lang + codec,
                    language: stream.Language,
                    codec: stream.Codec
                });
            }
        });

        console.log('[SelectToCards.extractStreams] Extracted', result.audio.length, 'audio and', result.subs.length, 'subtitle streams');
        return result;
    }

    // ============================================
    // SELECT MONITORING
    // ============================================

    function monitorSelectAccess(select) {
        if (select._monitored) return;
        select._monitored = true;
        
        const originalValue = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value');
        const originalIndex = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'selectedIndex');

        Object.defineProperty(select, 'value', {
            get: function() { return originalValue.get.call(this); },
            set: function(val) {
                if (this._isUserAction) {
                    this._userLockedValue = val;
                    this._userLockedIndex = null;
                }
                originalValue.set.call(this, this._userLockedValue !== null && 
                    (this.classList.contains('selectAudio') || this.classList.contains('selectSubtitles')) && 
                    !this._isUserAction ? this._userLockedValue : val);
            }
        });
        
        Object.defineProperty(select, 'selectedIndex', {
            get: function() { return originalIndex.get.call(this); },
            set: function(idx) {
                if (this._isUserAction) {
                    this._userLockedIndex = idx;
                    this._userLockedValue = null;
                }
                originalIndex.set.call(this, this._userLockedIndex !== null && 
                    (this.classList.contains('selectAudio') || this.classList.contains('selectSubtitles')) && 
                    !this._isUserAction ? this._userLockedIndex : idx);
            }
        });
    }

    // ============================================
    // UI STYLING
    // ============================================

    function ensureStyle() {
        if (document.getElementById('emby-select-cards-style')) return;
        const style = document.createElement('style');
        style.id = 'emby-select-cards-style';
        style.textContent = `
            form.trackSelections { max-width: none !important; width: 100% !important; }
            form.trackSelections .selectContainer { display: none !important; }
            
            .emby-select-cards {
                display: flex !important; flex-wrap: nowrap !important; overflow-x: auto !important; overflow-y: hidden !important;
                gap: 8px !important; padding: 8px 0 !important; scroll-behavior: smooth !important;
                scrollbar-width: none !important; -ms-overflow-style: none !important;
            }
            .emby-select-cards::-webkit-scrollbar { display: none !important; }
            
            .emby-select-wrapper { position: relative !important; padding: 0 !important; }
            
            .carousel-label {
                font-size: 1.1em !important; font-weight: 500 !important; color: rgba(255,255,255,0.9) !important;
                margin-bottom: 8px !important; margin-top: 16px !important; text-align: center !important;
            }
            
            .emby-select-card {
                flex: 0 0 auto !important; background: rgba(255,255,255,0.1) !important;
                border: 1px solid rgba(255,255,255,0.2) !important; border-radius: 6px !important;
                padding: 12px 16px !important; width: 120px !important; height: 100px !important;
                display: flex !important; align-items: center !important; justify-content: center !important;
                text-align: center !important; cursor: pointer !important; transition: all 0.2s ease !important;
                user-select: none !important; font-size: 14px !important; color: rgba(255,255,255,0.8) !important;
            }
            .emby-select-card.audio-card, .emby-select-card.subtitle-card {
                height: 70px !important; padding: 4px 12px !important; font-size: 12px !important;
            }
            .emby-select-card.placeholder { cursor: default !important; opacity: 0.5 !important; }
            .emby-select-card.placeholder:hover { background: rgba(255,255,255,0.1) !important; transform: none !important; }
            .emby-select-card:hover { background: rgba(255,255,255,0.15) !important; border-color: rgba(255,255,255,0.3) !important; transform: translateY(-1px) !important; }
            .emby-select-card.selected { background: #00a4dc !important; color: #fff !important; }
            .emby-select-card.disabled { background: rgba(255,255,255,0.05) !important; border-color: rgba(255,255,255,0.1) !important; color: rgba(255,255,255,0.4) !important; cursor: not-allowed !important; }
            
            .emby-select-pagination { display: flex !important; justify-content: center !important; gap: 8px !important; margin-top: 12px !important; padding: 8px 0 !important; }
            .emby-select-pagination-dot { width: 8px !important; height: 8px !important; border-radius: 50% !important; background: rgba(255,255,255,0.3) !important; border: none !important; cursor: pointer !important; transition: all 0.2s ease !important; padding: 0 !important; }
            .emby-select-pagination-dot:hover { background: rgba(255,255,255,0.5) !important; transform: scale(1.2) !important; }
            .emby-select-pagination-dot.active { background: #00a4dc !important; width: 24px !important; border-radius: 4px !important; }
            
            .emby-select-arrow {
                position: absolute !important; top: 50% !important; transform: translateY(-50%) !important;
                width: 40px !important; height: 40px !important; border-radius: 50% !important;
                background: rgba(128, 128, 128, 0.7) !important; border: none !important;
                color: #fff !important; font-size: 20px !important; font-weight: bold !important;
                cursor: pointer !important; z-index: 10 !important; transition: all 0.2s ease !important;
                display: flex !important; align-items: center !important; justify-content: center !important;
                opacity: 0 !important; pointer-events: none !important;
            }
            .emby-select-arrow.left { left: -20px !important; }
            .emby-select-arrow.right { right: -20px !important; }
            .emby-select-wrapper:hover .emby-select-arrow { opacity: 1 !important; pointer-events: auto !important; }
            .emby-select-arrow:hover { background: rgba(128, 128, 128, 0.9) !important; transform: translateY(-50%) scale(1.1) !important; }
            .emby-select-arrow:disabled { opacity: 0.3 !important; cursor: not-allowed !important; }
            
            .formatter-display { 
                margin-bottom: 16px !important; padding: 12px 16px !important; 
                background: rgba(255,255,255,0.1) !important; border: 1px solid rgba(255,255,255,0.2) !important; 
                border-radius: 6px !important; text-align: center !important; color: rgba(255,255,255,0.8) !important;
            }
        `;
        document.head.appendChild(style);
    }

    // ============================================
    // UI CONTROLS
    // ============================================

    function createArrows(container) {
        const wrapper = container.parentElement;
        if (!wrapper || wrapper.querySelector('.emby-select-arrow')) return;
        
        const leftArrow = document.createElement('button');
        leftArrow.className = 'emby-select-arrow left';
        leftArrow.innerHTML = '‹';
        leftArrow.setAttribute('aria-label', 'Previous');
        leftArrow.addEventListener('click', () => container.scrollBy({ left: -container.offsetWidth * 0.8, behavior: 'smooth' }));
        
        const rightArrow = document.createElement('button');
        rightArrow.className = 'emby-select-arrow right';
        rightArrow.innerHTML = '›';
        rightArrow.setAttribute('aria-label', 'Next');
        rightArrow.addEventListener('click', () => container.scrollBy({ left: container.offsetWidth * 0.8, behavior: 'smooth' }));
        
        const updateArrows = () => {
            leftArrow.disabled = container.scrollLeft <= 0;
            rightArrow.disabled = container.scrollLeft >= container.scrollWidth - container.offsetWidth - 1;
        };
        
        container.addEventListener('scroll', throttle(updateArrows, 100));
        updateArrows();
        
        wrapper.appendChild(leftArrow);
        wrapper.appendChild(rightArrow);
    }
    
    function createPagination(container) {
        const wrapper = container.parentElement;
        if (!wrapper || wrapper.querySelector('.emby-select-pagination')) return;
        
        const cards = container.querySelectorAll('.emby-select-card:not(.placeholder)');
        if (cards.length === 0) return;
        
        const paginationContainer = document.createElement('div');
        paginationContainer.className = 'emby-select-pagination';
        
        const cardsPerPage = 6;
        const pageCount = Math.ceil(cards.length / cardsPerPage);
        
        for (let i = 0; i < pageCount; i++) {
            const dot = document.createElement('button');
            dot.className = 'emby-select-pagination-dot' + (i === 0 ? ' active' : '');
            dot.setAttribute('aria-label', `Page ${i + 1}`);
            dot.addEventListener('click', () => {
                const cardWidth = cards[0].offsetWidth + 8;
                container.scrollTo({ left: i * cardsPerPage * cardWidth, behavior: 'smooth' });
                paginationContainer.querySelectorAll('.emby-select-pagination-dot').forEach((d, idx) => {
                    d.classList.toggle('active', idx === i);
                });
            });
            paginationContainer.appendChild(dot);
        }
        
        container.addEventListener('scroll', throttle(() => {
            const cardWidth = cards[0].offsetWidth + 8;
            const currentPage = Math.round(container.scrollLeft / (cardsPerPage * cardWidth));
            paginationContainer.querySelectorAll('.emby-select-pagination-dot').forEach((d, idx) => {
                d.classList.toggle('active', idx === currentPage);
            });
        }, 100));
        
        wrapper.appendChild(paginationContainer);
    }

    function updateFormatterDisplay(select, optionValue) {
        const form = select.closest('form.trackSelections');
        if (!form) return;
        
        let formatterDiv = form.querySelector('.formatter-display');
        if (!formatterDiv) {
            formatterDiv = document.createElement('div');
            formatterDiv.className = 'formatter-display';
            const firstWrapper = form.querySelector('.emby-select-wrapper');
            if (firstWrapper) form.insertBefore(formatterDiv, firstWrapper);
            else form.appendChild(formatterDiv);
        }
        
        const selectedOption = optionValue ? Array.from(select.options).find(o => o.value === optionValue) : null;
        if (selectedOption) {
            const cutIndex = selectedOption.textContent.indexOf('(cut)');
            formatterDiv.textContent = cutIndex !== -1 ? selectedOption.textContent.substring(0, cutIndex).trim() : 'No format information available';
        } else {
            formatterDiv.textContent = 'Loading format information...';
        }
    }

    // ============================================
    // CARD CREATION
    // ============================================

    function createDummyCard(cardType) {
        const card = document.createElement('div');
        card.className = 'emby-select-card placeholder ' + cardType;
        card.innerHTML = '<span style="font-size:48px;">🚫</span>';
        card.tabIndex = -1;
        return card;
    }

    function createCard(option, select, container) {
        const card = document.createElement('div');
        let cardClass = 'emby-select-card';
        if (select.classList.contains('selectAudio')) cardClass += ' audio-card';
        else if (select.classList.contains('selectSubtitles')) cardClass += ' subtitle-card';
        
        card.className = cardClass + (option.selected ? ' selected' : '') + (option.disabled ? ' disabled' : '');
        card.tabIndex = option.disabled ? -1 : 0;
        card.dataset.value = option.value;

        const textSpan = document.createElement('span');
        const fullText = option.textContent || option.value;
        const cutIndex = fullText.indexOf('(cut)');
        textSpan.textContent = cutIndex !== -1 ? fullText.substring(cutIndex + 5).trim() : fullText;
        textSpan.style.cssText = 'display:block;width:100%;word-wrap:break-word;white-space:normal;line-height:1.3;padding:4px;';
        card.appendChild(textSpan);

        card.addEventListener('click', () => !option.disabled && handleSelection(select, option.value));
        
        card.addEventListener('keydown', ev => {
            if (option.disabled) return;
            if (['Enter', ' '].includes(ev.key)) {
                ev.preventDefault();
                handleSelection(select, option.value);
            } else if (['ArrowRight', 'ArrowDown'].includes(ev.key)) {
                ev.preventDefault();
                const cards = Array.from(container.querySelectorAll('.emby-select-card:not(.disabled):not(.placeholder)'));
                const idx = cards.indexOf(card);
                if (idx !== -1 && idx + 1 < cards.length) cards[idx + 1].focus();
            } else if (['ArrowLeft', 'ArrowUp'].includes(ev.key)) {
                ev.preventDefault();
                const cards = Array.from(container.querySelectorAll('.emby-select-card:not(.disabled):not(.placeholder)'));
                const idx = cards.indexOf(card);
                if (idx > 0) cards[idx - 1].focus();
            }
        });

        return card;
    }

    // ============================================
    // CARD POPULATION
    // ============================================

    function populateCards(select) {
        const container = select._embyCardsContainer;
        if (!container || select.options.length === 0) return;

        if (select._cardsPopulated) return;
        select._cardsPopulated = true;

        container.innerHTML = '';

        Array.from(select.options).forEach(option => {
            container.appendChild(createCard(option, select, container));
        });

        const wrapper = container.parentElement;
        if (wrapper) {
            wrapper.querySelectorAll('.emby-select-arrow').forEach(arrow => arrow.remove());
            wrapper.querySelectorAll('.emby-select-pagination').forEach(pag => pag.remove());
        }

        setTimeout(() => {
            createArrows(container);
            createPagination(container);
            const selectedCard = container.querySelector('.emby-select-card.selected');
            if (selectedCard && select.classList.contains('selectSource')) {
                updateFormatterDisplay(select, selectedCard.dataset.value);
            }
            
            // Auto-trigger selection for the first version to load audio/subtitle
            if (select.classList.contains('selectSource') && selectedCard) {
                console.log('[SelectToCards] Auto-triggering stream fetch for first version');
                setTimeout(() => {
                    handleSelection(select, selectedCard.dataset.value);
                }, 200);
            }
        }, 0);
    }

    // ============================================
    // SELECTION HANDLING
    // ============================================

    function handleSelection(select, value) {
        console.log('[SelectToCards.handleSelection] Called for', select.className, 'with value:', value);
        
        select._isUserAction = true;
        Array.from(select.options).forEach(o => o.selected = o.value === value);
        select._isUserAction = false;
        
        const container = select._embyCardsContainer;
        if (container) {
            Array.from(container.children).forEach(card => {
                card.classList.toggle('selected', card.dataset.value === value);
            });
            if (select.classList.contains('selectSource')) {
                updateFormatterDisplay(select, value);
                setTimeout(async () => {
                    const form = select.closest('form.trackSelections');
                    if (form) {
                        console.log('[SelectToCards] Clearing audio/subtitle selects and fetching streams...');
                        
                        // Clear options from audio/subtitle selects
                        form.querySelectorAll('select.detailTrackSelect:not(.selectSource)').forEach(s => {
                            s.innerHTML = '';
                            s._cardsPopulated = false;
                            if (s._embyCardsContainer) {
                                const cardType = s.classList.contains('selectAudio') ? 'audio-card' : 'subtitle-card';
                                s._embyCardsContainer.innerHTML = '';
                                s._embyCardsContainer.appendChild(createDummyCard(cardType));
                            }
                        });

                        // Probe streams for selected media source
                        if (value) {
                            try {
                                let itemId = null;
                                
                                // Try multiple sources to find itemId
                                const itemInput = form.querySelector('[name*="itemId"], [name*="Id"]');
                                if (itemInput) itemId = itemInput.value;
                                
                                if (!itemId && window.__currentPlaybackItemId) itemId = window.__currentPlaybackItemId;
                                if (!itemId && window.__itemId) itemId = window.__itemId;
                                if (!itemId && window.__mediaInfo) itemId = window.__mediaInfo.Id;
                                
                                // Try to get from the select element's options
                                if (!itemId) {
                                    const selectedOption = select.options[select.selectedIndex];
                                    if (selectedOption && selectedOption.getAttribute('data-id')) {
                                        itemId = selectedOption.getAttribute('data-id');
                                    }
                                }
                                
                                // Try to extract from the value itself (mediaSourceId often contains itemId)
                                if (!itemId && value) {
                                    // Check if value looks like a GUID
                                    if (value.match(/^[a-f0-9]{32}$/i)) {
                                        itemId = value;
                                    }
                                }
                                
                                // Last resort: try to find from DOM context
                                if (!itemId) {
                                    const playbackManager = form.closest('[data-itemid]');
                                    if (playbackManager) itemId = playbackManager.getAttribute('data-itemid');
                                }
                                
                                console.log('[SelectToCards] Detected itemId:', itemId, 'mediaSourceId:', value);
                                
                                if (!itemId) {
                                    console.error('[SelectToCards] Could not determine itemId!');
                                    console.log('[SelectToCards] Available context:', {
                                        formInputs: Array.from(form.querySelectorAll('input, select')).map(i => ({name: i.name, value: i.value?.substring(0, 50)})),
                                        windowVars: { __itemId: window.__itemId, __mediaInfo: window.__mediaInfo }
                                    });
                                    return;
                                }
                                
                                console.log('[SelectToCards] Probing streams for itemId:', itemId, 'mediaSourceId:', value);
                                const mediaSource = await probeItemStreams(itemId, value);
                                
                                if (mediaSource) {
                                    console.log('[SelectToCards] Got mediaSource, extracting streams...');
                                    const { audio, subs } = extractStreams(mediaSource);
                                    
                                    console.log('[SelectToCards] Extracted', audio.length, 'audio and', subs.length, 'subtitle tracks');
                                    
                                    const audioSel = form.querySelector('select.selectAudio');
                                    if (audioSel && audio.length > 0) {
                                        console.log('[SelectToCards] Populating', audio.length, 'audio tracks');
                                        audio.forEach(t => {
                                            const opt = document.createElement('option');
                                            opt.value = String(t.index);
                                            opt.textContent = t.title;
                                            audioSel.appendChild(opt);
                                        });
                                        audioSel.disabled = false;
                                        populateCards(audioSel);
                                    } else {
                                        console.warn('[SelectToCards] No audio tracks found');
                                    }
                                    
                                    const subSel = form.querySelector('select.selectSubtitles');
                                    if (subSel && subs.length > 0) {
                                        console.log('[SelectToCards] Populating', subs.length, 'subtitle tracks');
                                        subs.forEach(t => {
                                            const opt = document.createElement('option');
                                            opt.value = String(t.index);
                                            opt.textContent = t.title;
                                            subSel.appendChild(opt);
                                        });
                                        subSel.disabled = false;
                                        populateCards(subSel);
                                    } else {
                                        console.warn('[SelectToCards] No subtitle tracks found');
                                    }
                                } else {
                                    console.error('[SelectToCards] Failed to get mediaSource');
                                }
                            } catch (err) {
                                console.error('[SelectToCards.handleSelection] Error:', err);
                            }
                        }
                    }
                }, 100);
            }
        }
        
        emitEvent(select, 'input');
        emitEvent(select, 'change');
    }

    // ============================================
    // INITIALIZATION
    // ============================================

    function initSelects() {
        const form = document.querySelector('form.trackSelections');
        if (!form || form._selectToCardsInitialized) return;
        // Ensure plugin styles are injected before manipulating the DOM
        try { ensureStyle(); } catch (e) { console.warn('[SelectToCards] ensureStyle failed', e); }
        
        form._selectToCardsInitialized = true;
        console.log('[SelectToCards] Initializing track selections form');
        
        // DIAGNOSTIC: Log all select elements
        const allSelects = form.querySelectorAll('select');
        console.log('[SelectToCards] Found', allSelects.length, 'select elements:');
        allSelects.forEach((sel, idx) => {
            console.log(`  [${idx}] classes:`, sel.className, 'options:', sel.options.length, 'disabled:', sel.disabled);
        });
        
        // Try to capture itemId from form context
        try {
            // Method 1: Check for hidden inputs
            const itemIdInput = form.querySelector('input[name*="itemId"], input[name*="Id"], input[type="hidden"]');
            if (itemIdInput?.value) {
                window.__currentPlaybackItemId = itemIdInput.value;
                console.log('[SelectToCards] Captured itemId from input:', itemIdInput.value);
            }
            
            // Method 2: Check data attributes on form or parent
            const itemIdAttr = form.getAttribute('data-itemid') || form.closest('[data-itemid]')?.getAttribute('data-itemid');
            if (itemIdAttr && !window.__currentPlaybackItemId) {
                window.__currentPlaybackItemId = itemIdAttr;
                console.log('[SelectToCards] Captured itemId from attribute:', itemIdAttr);
            }
            
            // Method 3: Extract from URL or recent API calls
            // Check if there was a recent Items/ API call we can parse
            if (!window.__currentPlaybackItemId) {
                // Try to get from the most recent Jellyfin request
                const urlMatch = document.location.href.match(/[?&]id=([a-f0-9]+)/i);
                if (urlMatch) {
                    window.__currentPlaybackItemId = urlMatch[1];
                    console.log('[SelectToCards] Captured itemId from URL:', urlMatch[1]);
                }
            }
            
            // Check all form inputs for debugging
            const allInputs = form.querySelectorAll('input');
            console.log('[SelectToCards] All form inputs:', Array.from(allInputs).map(i => ({name: i.name, value: i.value?.substring(0, 50)})));
        } catch (e) {
            console.warn('[SelectToCards] Could not capture itemId:', e);
        }
        
        form.querySelectorAll('.selectContainer').forEach(c => c.style.display = 'none');        const selects = form.querySelectorAll('select.detailTrackSelect');
        console.log('[SelectToCards] Processing', selects.length, 'select elements');
        
        selects.forEach((select, idx) => {
            // Skip selectVideo - we don't want a Video Quality carousel
            if (select.classList.contains('selectVideo')) {
                console.log('[SelectToCards] Skipping selectVideo carousel');
                return;
            }
            
            monitorSelectAccess(select);
            
            // Determine label based on class
            let label = 'Unknown';
            if (select.classList.contains('selectSource')) label = 'Version';
            else if (select.classList.contains('selectVideo')) label = 'Video Quality';
            else if (select.classList.contains('selectAudio')) label = 'Audio';
            else if (select.classList.contains('selectSubtitles')) label = 'Subtitles';
            
            // Try to get label from previous sibling if it exists
            if (select.previousElementSibling?.textContent) {
                label = select.previousElementSibling.textContent;
            }
            
            console.log(`[SelectToCards] [${idx}] Creating carousel for:`, label, 'class:', select.className);
            
            const labelDiv = document.createElement('div');
            labelDiv.className = 'carousel-label';
            labelDiv.textContent = label;
            
            const wrapper = document.createElement('div');
            wrapper.className = 'emby-select-wrapper';
            
            const cardsContainer = document.createElement('div');
            cardsContainer.className = 'emby-select-cards';
            cardsContainer.setAttribute('role', 'listbox');
            cardsContainer.setAttribute('aria-label', label);
            
            wrapper.appendChild(cardsContainer);
            select._embyCardsContainer = cardsContainer;
            
            const parent = select.closest('.selectContainer')?.parentElement || form;
            parent.appendChild(labelDiv);
            parent.appendChild(wrapper);
            
            // Wait for options to be populated, then create cards
            // Jellyfin populates the selects asynchronously
            const checkAndPopulate = () => {
                if (select.options.length > 0) {
                    console.log(`[SelectToCards] Options populated for ${label}:`, select.options.length);
                    if (select.classList.contains('selectSource') || select.classList.contains('selectVideo')) {
                        populateCards(select);
                    } else {
                        // Audio/Subtitle start with dummy cards until version is selected
                        const cardType = select.classList.contains('selectAudio') ? 'audio-card' : 'subtitle-card';
                        cardsContainer.appendChild(createDummyCard(cardType));
                    }
                } else {
                    // Not populated yet, check again soon
                    setTimeout(checkAndPopulate, 100);
                }
            };
            
            // Start checking
            setTimeout(checkAndPopulate, 50);
        });
        
        console.log('[SelectToCards] Initialization complete');
    }

    // Start monitoring for playback UI
    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType === 1) {
                    if (node.matches && node.matches('form.trackSelections')) {
                        initSelects();
                    } else if (node.querySelector) {
                        const form = node.querySelector('form.trackSelections');
                        if (form) initSelects();
                    }
                }
            }
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
    
    // CRITICAL: Hook into ApiClient to capture the itemId from API calls
    if (window.ApiClient && window.ApiClient.ajax) {
        const originalAjax = window.ApiClient.ajax;
        window.ApiClient.ajax = function(options) {
            // Check if this is an Items request that might have the itemId
            if (options.url && options.url.includes('/Items/')) {
                const match = options.url.match(/\/Items\/([a-f0-9-]+)/i);
                if (match && match[1]) {
                    const extractedId = match[1].replace(/-/g, '');
                    if (extractedId.length === 32) {
                        window.__currentPlaybackItemId = extractedId;
                        console.log('[SelectToCards] Intercepted itemId from API call:', extractedId);
                    }
                }
            }
            return originalAjax.apply(this, arguments);
        };
    }
    
    // Check if form already exists
    if (document.querySelector('form.trackSelections')) {
        // Ensure styles are present before initializing an existing form
        try { ensureStyle(); } catch (e) { console.warn('[SelectToCards] ensureStyle failed', e); }
        initSelects();
    }

    console.log('[SelectToCards] Standalone version loaded');
})();
