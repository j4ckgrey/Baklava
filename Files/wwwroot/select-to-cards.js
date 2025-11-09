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

    // Wait for a predicate to become true (polling). Returns true if predicate satisfied within timeout.
    function waitFor(predicate, timeout = 8000, interval = 100) {
        return new Promise(resolve => {
            const start = Date.now();
            const check = () => {
                try {
                    if (typeof predicate === 'function' && predicate()) return resolve(true);
                } catch (e) { /* ignore predicate errors */ }
                if (Date.now() - start >= timeout) return resolve(false);
                setTimeout(check, interval);
            };
            check();
        });
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

            const url = window.ApiClient.getUrl('api/baklava/metadata/streams') + '?' + params.toString();
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

        // Helper to derive a friendly type string from codec/title/channels
        function deriveAudioType(s) {
            // Prefer codec if present
            if (s.Codec) {
                const c = s.Codec.toLowerCase();
                const map = { aac: 'AAC', ac3: 'AC3', eac3: 'E-AC3', dts: 'DTS', opus: 'Opus', mp3: 'MP3', flac: 'FLAC' };
                if (map[c]) return map[c];
                return s.Codec.toUpperCase();
            }
            if (s.Channels) {
                return (s.Channels === 2 ? 'Stereo' : `${s.Channels}ch`);
            }
            // fallback to title/content hints
            const t = (s.DisplayTitle || s.Title || '').toLowerCase();
            if (t.includes('aac')) return 'AAC';
            if (t.includes('ac3')) return 'AC3';
            if (t.includes('dts')) return 'DTS';
            if (t.includes('opus')) return 'Opus';
            return '';
        }

        function deriveSubtitleType(s) {
            if (s.Codec) {
                const c = s.Codec.toLowerCase();
                const map = { srt: 'SRT', ass: 'ASS', webvtt: 'VTT', vtt: 'VTT', 'subrip': 'SRT' };
                if (map[c]) return map[c];
                return s.Codec.toUpperCase();
            }
            const t = (s.DisplayTitle || s.Title || '').toLowerCase();
            if (t.includes('.srt') || t.includes('srt')) return 'SRT';
            if (t.includes('.ass') || t.includes('ass')) return 'ASS';
            if (t.includes('vtt') || t.includes('webvtt')) return 'VTT';
            return '';
        }

        mediaSource.MediaStreams.forEach((stream) => {
            if (stream.Type === 'Audio') {
                const dispLang = stream.DisplayTitle || stream.Language || stream.Title || '';
                const type = deriveAudioType(stream);
                result.audio.push({
                    index: stream.Index,
                    title: String(dispLang),
                    language: stream.Language,
                    displayLanguage: dispLang,
                    codec: stream.Codec,
                    channels: stream.Channels,
                    type: type
                });
            } else if (stream.Type === 'Subtitle') {
                const dispLang = stream.DisplayTitle || stream.Language || stream.Title || '';
                const type = deriveSubtitleType(stream);
                result.subs.push({
                    index: stream.Index,
                    title: String(dispLang),
                    language: stream.Language,
                    displayLanguage: dispLang,
                    codec: stream.Codec,
                    isForced: stream.IsForced,
                    isDefault: stream.IsDefault,
                    type: type
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
            
                /* Remove large horizontal padding so arrows can overlay cards without pushing them
                    The arrows will be absolutely positioned over the cards container. */
                .emby-select-wrapper { position: relative !important; padding: 0 !important; }

            .emby-select-arrow {
                position: absolute !important; /* top set by JS for precise centering */
                width: 36px !important; height: 36px !important; border-radius: 50% !important; border: none !important;
                background: rgba(0,0,0,0.45) !important; color: #fff !important; display: flex !important; align-items: center !important; justify-content: center !important;
                cursor: pointer !important; z-index: 20 !important; font-size: 20px !important; line-height: 1 !important;
                box-shadow: 0 2px 8px rgba(0,0,0,0.4) !important;
            }
            .emby-select-arrow.left { left: 8px !important; }
            .emby-select-arrow.right { right: 8px !important; }
            .emby-select-arrow:disabled { opacity: 0.35 !important; cursor: default !important; }
            
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
            /* Smaller cards for audio/subtitle since language is compact */
            .emby-select-card.audio-card, .emby-select-card.subtitle-card {
                height: 60px !important; padding: 6px 8px !important; font-size: 13px !important;
                width: 88px !important; display: flex !important; flex-direction: column !important; justify-content: center !important; align-items: center !important;
            }
            .emby-select-card.placeholder { cursor: default !important; opacity: 0.5 !important; }
            .emby-select-card.placeholder:hover { background: rgba(255,255,255,0.1) !important; transform: none !important; }
            .emby-select-card:hover { background: rgba(255,255,255,0.15) !important; border-color: rgba(255,255,255,0.3) !important; transform: translateY(-1px) !important; }
            .emby-select-card.selected { background: #00a4dc !important; color: #fff !important; }
            .emby-select-card.disabled { background: rgba(255,255,255,0.05) !important; border-color: rgba(255,255,255,0.1) !important; color: rgba(255,255,255,0.4) !important; cursor: not-allowed !important; }
            
            .emby-select-pagination { display: flex !important; justify-content: center !important; gap: 8px !important; padding: 8px 0 !important; }
            .emby-select-pagination-dot { width: 8px !important; height: 8px !important; border-radius: 50% !important; background: rgba(255,255,255,0.3) !important; border: none !important; cursor: pointer !important; transition: all 0.2s ease !important; padding: 0 !important; }
            .emby-select-pagination-dot:hover { background: rgba(255,255,255,0.5) !important; transform: scale(1.2) !important; }
            .emby-select-pagination-dot.active { background: #00a4dc !important; width: 24px !important; border-radius: 4px !important; }
            
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
        console.log('[SelectToCards] createArrows for container', container);

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

        // Update enabled/disabled state based on scroll position
        const updateArrows = () => {
            leftArrow.disabled = container.scrollLeft <= 0;
            rightArrow.disabled = container.scrollLeft >= container.scrollWidth - container.offsetWidth - 1;
        };

        // Compute the desired vertical position so arrows align to the card center
        const computeAndSetArrowTop = () => {
            try {
                const wrapperRect = wrapper.getBoundingClientRect();
                // Prefer a visible, non-placeholder card as the anchor
                let anchor = container.querySelector('.emby-select-card:not(.placeholder)');
                if (!anchor) anchor = container.querySelector('.emby-select-card');

                if (anchor) {
                    const cardRect = anchor.getBoundingClientRect();
                    const cardCenter = (cardRect.top - wrapperRect.top) + (cardRect.height / 2);
                    const halfArrow = (leftArrow.offsetHeight || 36) / 2;
                    const topPx = Math.max(0, Math.round(cardCenter - halfArrow));
                    leftArrow.style.top = topPx + 'px';
                    rightArrow.style.top = topPx + 'px';
                } else {
                    // fallback: center to the container
                    const cRect = container.getBoundingClientRect();
                    const center = (cRect.top - wrapperRect.top) + (cRect.height / 2);
                    const halfArrow = (leftArrow.offsetHeight || 36) / 2;
                    const topPx = Math.max(0, Math.round(center - halfArrow));
                    leftArrow.style.top = topPx + 'px';
                    rightArrow.style.top = topPx + 'px';
                }
            } catch (e) { /* ignore measurement errors */ }
        };

        // Observers/listeners to keep arrows positioned when layout changes
        container.addEventListener('scroll', throttle(updateArrows, 100));
        const ro = (typeof ResizeObserver !== 'undefined') ? new ResizeObserver(throttle(() => { computeAndSetArrowTop(); updateArrows(); }, 100)) : null;
        if (ro) ro.observe(container);
        window.addEventListener('resize', throttle(() => { computeAndSetArrowTop(); updateArrows(); }, 120));

        try {
            const mo = new MutationObserver(throttle(() => { computeAndSetArrowTop(); updateArrows(); }, 120));
            mo.observe(container, { childList: true, subtree: true, attributes: true });
        } catch (e) { /* ignore */ }

        // Insert arrows, then compute initial position after layout
        wrapper.appendChild(leftArrow);
        wrapper.appendChild(rightArrow);
        // give browser a tick to render and populate sizes
        setTimeout(() => { computeAndSetArrowTop(); updateArrows(); }, 40);
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
        // Use a neutral, text-based placeholder instead of an emoji
        card.innerHTML = '<div style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;font-size:14px;color:rgba(255,255,255,0.6);">No tracks</div>';
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

        // Audio/Subtitle cards show two lines: language (big) and type (small)
        if (card.classList.contains('audio-card') || card.classList.contains('subtitle-card')) {
            const top = document.createElement('div');
            top.className = 'card-top';
            top.style.cssText = 'font-weight:700;font-size:14px;line-height:1;padding:0;margin:0;text-align:center;';
            const bottom = document.createElement('div');
            bottom.className = 'card-bottom';
            bottom.style.cssText = 'font-size:11px;opacity:0.9;margin-top:6px;text-align:center;';

            // Try to parse display-friendly language and type from option text/value
            // option._meta can be attached by populate code; fallback to option.textContent/value
            const meta = option._meta || {};
            const lang = (meta.displayLanguage || option.textContent || option.value || '').toString().trim();
            const type = (meta.type || meta.codec || '').toString().trim();

            top.textContent = (lang || '').toUpperCase();
            bottom.textContent = type || '';
            card.appendChild(top);
            card.appendChild(bottom);
        } else {
            const textSpan = document.createElement('span');
            const fullText = option.textContent || option.value;
            const cutIndex = fullText.indexOf('(cut)');
            textSpan.textContent = cutIndex !== -1 ? fullText.substring(cutIndex + 5).trim() : fullText;
            textSpan.style.cssText = 'display:block;width:100%;word-wrap:break-word;white-space:normal;line-height:1.3;padding:4px;';
            card.appendChild(textSpan);
        }

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
                                            // Attach meta for card rendering (displayLanguage and type)
                                            // Prefer the raw language code (t.language) for compact display like 'ENG'.
                                            opt._meta = { displayLanguage: (t.language || t.displayLanguage || t.title), type: (t.type || t.codec), language: t.language };
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
                                            opt._meta = { displayLanguage: (t.language || t.displayLanguage || t.title), type: (t.type || t.codec), language: t.language };
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
        if (!form) return;
        // Ensure plugin styles are injected before manipulating the DOM
        try { ensureStyle(); } catch (e) { console.warn('[SelectToCards] ensureStyle failed', e); }

        console.log('[SelectToCards] Initializing track selections (idempotent)');

        // DIAGNOSTIC: Log all select elements briefly
        try {
            const allSelects = form.querySelectorAll('select');
            console.log('[SelectToCards] Found', allSelects.length, 'select elements');
        } catch (e) { /* ignore */ }

        // Try to capture itemId from form context (best-effort)
        try {
            const itemIdInput = form.querySelector('input[name*="itemId"], input[name*="Id"], input[type="hidden"]');
            if (itemIdInput?.value) window.__currentPlaybackItemId = itemIdInput.value;
            const itemIdAttr = form.getAttribute('data-itemid') || form.closest('[data-itemid]')?.getAttribute('data-itemid');
            if (itemIdAttr && !window.__currentPlaybackItemId) window.__currentPlaybackItemId = itemIdAttr;
            if (!window.__currentPlaybackItemId) {
                const urlMatch = document.location.href.match(/[?&]id=([a-f0-9]+)/i);
                if (urlMatch) window.__currentPlaybackItemId = urlMatch[1];
            }
            if (window.__currentPlaybackItemId) console.log('[SelectToCards] Captured itemId:', window.__currentPlaybackItemId);
        } catch (e) { /* ignore */ }

        // Hide original select containers
        form.querySelectorAll('.selectContainer').forEach(c => c.style.display = 'none');

        const selects = form.querySelectorAll('select.detailTrackSelect');
        if (!selects || selects.length === 0) return;

        selects.forEach((select, idx) => {
            try {
                // Skip if we've already attached to this select
                if (select._embyCardsContainer) return;

                // Skip selectVideo - we don't want a Video Quality carousel
                if (select.classList.contains('selectVideo')) return;

                monitorSelectAccess(select);

                // Determine label
                let label = 'Unknown';
                if (select.classList.contains('selectSource')) label = 'Version';
                else if (select.classList.contains('selectVideo')) label = 'Video Quality';
                else if (select.classList.contains('selectAudio')) label = 'Audio';
                else if (select.classList.contains('selectSubtitles')) label = 'Subtitles';
                if (select.previousElementSibling?.textContent) label = select.previousElementSibling.textContent;

                const labelDiv = document.createElement('div');
                labelDiv.className = 'carousel-label';
                labelDiv.textContent = label;

                const wrapper = document.createElement('div');
                wrapper.className = 'emby-select-wrapper';

                const cardsContainer = document.createElement('div');
                cardsContainer.className = 'emby-select-cards';
                cardsContainer.setAttribute('role', 'listbox');
                cardsContainer.setAttribute('aria-label', label);
                // Add a lightweight loading indicator while options populate
                const loadingEl = document.createElement('div');
                loadingEl.className = 'emby-select-loading';
                loadingEl.textContent = 'Loading…';
                loadingEl.style.cssText = 'color:rgba(255,255,255,0.6);font-size:13px;padding:12px;';
                cardsContainer.appendChild(loadingEl);

                wrapper.appendChild(cardsContainer);
                select._embyCardsContainer = cardsContainer;

                const parent = select.closest('.selectContainer')?.parentElement || form;
                parent.appendChild(labelDiv);
                parent.appendChild(wrapper);

                // Wait for options (best-effort) and then populate
                (async () => {
                    let ready = await waitFor(() => select.options && select.options.length > 0, 6000, 100);
                    if (!ready) {
                        // Try a few short backoff retries before giving up (covers slow network / probe delays)
                        const backoffs = [200, 600, 1800];
                        for (const b of backoffs) {
                            await new Promise(r => setTimeout(r, b));
                            if (select.options && select.options.length > 0) { ready = true; break; }
                        }
                    }

                    // Remove loading indicator
                    try { if (loadingEl && loadingEl.parentElement) loadingEl.parentElement.removeChild(loadingEl); } catch (e) {}

                    if (!ready) {
                        console.warn('[SelectToCards] Options did not populate in time for select', select.className);
                        // fallback: show a dummy card for audio/subtitles
                        if (!select.classList.contains('selectSource') && !select.classList.contains('selectVideo')) {
                            const cardType = select.classList.contains('selectAudio') ? 'audio-card' : 'subtitle-card';
                            cardsContainer.appendChild(createDummyCard(cardType));
                        }
                        return;
                    }

                    console.log('[SelectToCards] Options populated for', label, select.options.length);
                    if (select.classList.contains('selectSource') || select.classList.contains('selectVideo')) {
                        populateCards(select);
                    } else {
                        const cardType = select.classList.contains('selectAudio') ? 'audio-card' : 'subtitle-card';
                        cardsContainer.appendChild(createDummyCard(cardType));
                    }
                })();
            } catch (e) { console.warn('[SelectToCards] init select error', e); }
        });

        // Observe the form for new selects/options (only once)
        try {
            if (!form._selectCardsObserver) {
                const formObserver = new MutationObserver(mutations => {
                    for (const m of mutations) {
                        if (m.type === 'childList' || m.type === 'subtree' || m.type === 'attributes') {
                            const selects = document.querySelectorAll('form.trackSelections select.detailTrackSelect');
                            for (const s of selects) if (!s._embyCardsContainer) initSelects();
                        }
                    }
                });
                formObserver.observe(form, { childList: true, subtree: true, attributes: true });
                form._selectCardsObserver = formObserver;
            }
        } catch (e) { console.warn('[SelectToCards] form observer failed', e); }

        console.log('[SelectToCards] Initialization scheduled for', selects.length, 'select(s)');
    }

    // Start monitoring for playback UI
    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType !== 1) continue;

                // If a trackSelections form is added anywhere, init
                try {
                    if (node.matches && node.matches('form.trackSelections')) {
                        initSelects();
                        continue;
                    }
                } catch (e) { /* ignore match errors */ }

                // If a subtree contains the form, init
                try {
                    if (node.querySelector) {
                        const form = node.querySelector('form.trackSelections');
                        if (form) { initSelects(); continue; }

                        // If the details modal overlay is added or nested, schedule init
                        if ((node.matches && node.matches('#item-detail-modal-overlay')) || node.querySelector('#item-detail-modal-overlay')) {
                            setTimeout(initSelects, 80);
                            continue;
                        }
                    }
                } catch (e) { /* ignore */ }
            }
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });

    // Also listen for the details modal open event (used by our DetailsModal) and trigger init
    try {
        document.addEventListener('openDetailsModal', () => setTimeout(initSelects, 80));
    } catch (e) { /* ignore */ }

    // If modal overlay already exists, observe its class changes to detect when it's opened
    try {
        const overlay = document.querySelector('#item-detail-modal-overlay');
        if (overlay && window.MutationObserver) {
            const overlayObs = new MutationObserver(muts => {
                for (const m of muts) {
                    if (m.type === 'attributes' && overlay.classList.contains('open')) {
                        setTimeout(initSelects, 80);
                        break;
                    }
                }
            });
            overlayObs.observe(overlay, { attributes: true, attributeFilter: ['class'] });
        }
    } catch (e) { /* ignore */ }
    
    // CRITICAL: Hook into ApiClient to capture the itemId from API calls
    if (window.ApiClient && window.ApiClient.ajax) {
        const originalAjax = window.ApiClient.ajax;
        window.ApiClient.ajax = function(options) {
            try {
                if (options && options.url && options.url.includes('/Items/')) {
                    const match = options.url.match(/\/Items\/([a-f0-9-]+)/i);
                    if (match && match[1]) {
                        const extractedId = match[1].replace(/-/g, '');
                        if (extractedId.length === 32) {
                            window.__currentPlaybackItemId = extractedId;
                            console.log('[SelectToCards] Intercepted itemId from API call:', extractedId);
                        }
                    }
                }
            } catch (e) { /* ignore */ }

            const res = originalAjax.apply(this, arguments);

            try {
                Promise.resolve(res).then(() => {
                    try {
                        const url = options && options.url ? options.url : '';
                        if (url && (/\/Items\/|Playback|PlaybackInfo|Sessions\/PlaybackInfo/i).test(url)) {
                            setTimeout(() => { try { initSelects(); } catch (e) {} }, 120);
                        }
                    } catch (e) { /* ignore */ }
                }).catch(() => {});
            } catch (e) { /* ignore */ }

            return res;
        };

        // Also listen to history changes as an extra safety for SPA navigation
        try {
            window.addEventListener('hashchange', () => setTimeout(initSelects, 200));
            window.addEventListener('popstate', () => setTimeout(initSelects, 200));
        } catch (e) {}

        // Hook into fetch (if present) so we detect navigation and data loads
        try {
            if (window.fetch) {
                const _fetch = window.fetch.bind(window);
                window.fetch = function(input, init) {
                    const url = (typeof input === 'string') ? input : (input && input.url) || '';
                    const res = _fetch(input, init);
                    Promise.resolve(res).then(() => {
                        try {
                            if (url && (/\/Items\/|Playback|PlaybackInfo|Sessions\/PlaybackInfo/i).test(url)) {
                                setTimeout(() => { try { initSelects(); } catch (e) {} }, 120);
                            }
                        } catch (e) {}
                    }).catch(() => {});
                    return res;
                };
            }
        } catch (e) { /* ignore */ }

        // Hook into XMLHttpRequest as well
        try {
            const origOpen = XMLHttpRequest.prototype.open;
            const origSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url) {
                this._url_for_select_cards = url;
                return origOpen.apply(this, arguments);
            };
            XMLHttpRequest.prototype.send = function(body) {
                const onload = () => {
                    try {
                        const url = this._url_for_select_cards || '';
                        if (url && (/\/Items\/|Playback|PlaybackInfo|Sessions\/PlaybackInfo/i).test(url)) {
                            setTimeout(() => { try { initSelects(); } catch (e) {} }, 120);
                        }
                    } catch (e) {}
                };
                this.addEventListener('load', onload);
                return origSend.apply(this, arguments);
            };
        } catch (e) { /* ignore */ }

        // Wrap history APIs so SPA navigation triggers init
        try {
            const _push = history.pushState;
            history.pushState = function() { const res = _push.apply(this, arguments); setTimeout(initSelects, 200); return res; };
            const _replace = history.replaceState;
            history.replaceState = function() { const res = _replace.apply(this, arguments); setTimeout(initSelects, 200); return res; };
        } catch (e) { /* ignore */ }
    }
    
    // Check if form already exists
    if (document.querySelector('form.trackSelections')) {
        // Ensure styles are present before initializing an existing form
        try { ensureStyle(); } catch (e) { console.warn('[SelectToCards] ensureStyle failed', e); }
        initSelects();
    }

    console.log('[SelectToCards] Standalone version loaded');
})();