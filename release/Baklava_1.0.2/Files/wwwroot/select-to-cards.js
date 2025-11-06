(function () {
    'use strict';
    
    function init() {
        const UI = window.MediaServerUI;
        if (!UI) {
            console.warn('[SelectToCards] MediaServerUI not ready, retrying in 100ms');
            setTimeout(init, 100);
            return;
        }
        
        console.log('[SelectToCards] Initializing...');

    // ========================================
    // Select to Cards Converter
    // ========================================

    // Monitor select values to prevent overriding user choices
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


    // Inject CSS for card UI
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
            
            .emby-select-wrapper { position: relative !important; padding: 0 60px !important; }
            
            .emby-select-arrow {
                position: absolute !important; top: 50% !important; transform: translateY(-50%) !important;
                width: 40px !important; height: 40px !important; background: rgba(0,0,0,0.6) !important;
                border: 1px solid rgba(255,255,255,0.3) !important; border-radius: 50% !important;
                color: #fff !important; font-size: 20px !important; cursor: pointer !important;
                transition: all 0.2s ease !important; z-index: 10 !important; display: flex !important;
                align-items: center !important; justify-content: center !important;
            }
            .emby-select-arrow:hover { background: rgba(0,0,0,0.8) !important; transform: translateY(-50%) scale(1.1) !important; }
            .emby-select-arrow:active { transform: translateY(-50%) scale(0.95) !important; }
            .emby-select-arrow.left { left: 10px !important; }
            .emby-select-arrow.right { right: 10px !important; }
            .emby-select-arrow:disabled { opacity: 0.3 !important; cursor: not-allowed !important; }
            
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
            
            .formatter-display { 
                margin-bottom: 16px !important; padding: 12px 16px !important; 
                background: rgba(255,255,255,0.1) !important; border: 1px solid rgba(255,255,255,0.2) !important; 
                border-radius: 6px !important; text-align: center !important; color: rgba(255,255,255,0.8) !important;
            }
        `;
        document.head.appendChild(style);
    }


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
        
        container.addEventListener('scroll', UI.throttle(updateArrows, 100));
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
        
        container.addEventListener('scroll', UI.throttle(() => {
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


    function getSelectClass(select) {
        if (select.classList.contains('selectSource')) return 'selectSource';
        if (select.classList.contains('selectAudio')) return 'selectAudio';
        return 'selectSubtitles';
    }

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


    function populateCards(select) {
        const container = select._embyCardsContainer;
        if (!container || select.options.length === 0) return;

        // Mark as populated to prevent duplicates
        if (select._cardsPopulated) return;
        select._cardsPopulated = true;

        // Clear ALL existing cards (including placeholders and duplicates)
        container.innerHTML = '';

        // Add real cards
        Array.from(select.options).forEach(option => {
            container.appendChild(createCard(option, select, container));
        });

        // Remove old pagination and arrows before creating new ones
        const wrapper = container.parentElement;
        if (wrapper) {
            wrapper.querySelectorAll('.emby-select-arrow').forEach(arrow => arrow.remove());
            wrapper.querySelectorAll('.emby-select-pagination').forEach(pag => pag.remove());
        }

        // Setup controls
        setTimeout(() => {
            createArrows(container);
            createPagination(container);
            const selectedCard = container.querySelector('.emby-select-card.selected');
            if (selectedCard && select.classList.contains('selectSource')) {
                updateFormatterDisplay(select, selectedCard.dataset.value);
            }
            
            // Auto-trigger selection for the first version to load audio/subtitle
            if (select.classList.contains('selectSource') && selectedCard) {
                setTimeout(() => {
                    handleSelection(select, selectedCard.dataset.value);
                }, 100);
            }
        }, 0);
    }


    function handleSelection(select, value) {
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
                        // Clear options from audio/subtitle selects (not the carousels!)
                        form.querySelectorAll('select.detailTrackSelect:not(.selectSource)').forEach(s => {
                            // Clear the select options
                            s.innerHTML = '';
                            // Reset populated flag so it can be populated again
                            s._cardsPopulated = false;
                            // Reset carousel to dummy card
                            if (s._embyCardsContainer) {
                                const cardType = s.classList.contains('selectAudio') ? 'audio-card' : 'subtitle-card';
                                s._embyCardsContainer.innerHTML = '';
                                s._embyCardsContainer.appendChild(createDummyCard(cardType));
                            }
                        });

                        // Probe streams for selected media source
                        if (UI?.probeItemStreams && value) {
                            try {
                                let itemId = null;
                                const itemInput = form.querySelector('[name*="itemId"], [name*="Id"]');
                                if (itemInput) itemId = itemInput.value;
                                if (!itemId && window.__itemId) itemId = window.__itemId;
                                if (!itemId && window.__mediaInfo) itemId = window.__mediaInfo.Id;
                                if (!itemId) itemId = value;
                                
                                const mediaSource = await UI.probeItemStreams(itemId, value);
                                if (mediaSource && UI.extractStreams) {
                                    const { audio, subs } = UI.extractStreams(mediaSource);
                                    
                                    const audioSel = form.querySelector('select.selectAudio');
                                    if (audioSel && audio.length > 0) {
                                        audio.forEach(t => {
                                            const opt = document.createElement('option');
                                            opt.value = String(t.index);
                                            opt.textContent = t.title;
                                            audioSel.appendChild(opt);
                                        });
                                        audioSel.disabled = false;
                                        populateCards(audioSel);
                                    }
                                    
                                    const subSel = form.querySelector('select.selectSubtitles');
                                    if (subSel && subs.length > 0) {
                                        subs.forEach(t => {
                                            const opt = document.createElement('option');
                                            opt.value = String(t.index);
                                            opt.textContent = t.title;
                                            subSel.appendChild(opt);
                                        });
                                        subSel.disabled = false;
                                        populateCards(subSel);
                                    }
                                }
                            } catch (err) {
                                console.error('[handleSelection]', err);
                            }
                        }
                    }
                }, 100);
            }
        }
        
        UI.emitEvent(select, 'input');
        UI.emitEvent(select, 'change');
    }



    function initSelects() {
        const form = document.querySelector('form.trackSelections');
        if (!form || form.dataset.carouselsBuilt === 'true') return;
        
        ensureStyle();
        form.dataset.carouselsBuilt = 'true';
        
        // Hide original selects
        form.querySelectorAll('.selectContainer').forEach(c => c.style.display = 'none');
        
        // Build formatter display
        const formatterDiv = document.createElement('div');
        formatterDiv.className = 'formatter-display';
        formatterDiv.textContent = 'Loading format information...';
        form.appendChild(formatterDiv);
        
        // Build all 3 carousels with dummy placeholders
        ['selectSource', 'selectAudio', 'selectSubtitles'].forEach(className => {
            const labelText = className === 'selectSource' ? 'Version' : className === 'selectAudio' ? 'Audio' : 'Subtitles';
            const cardType = className === 'selectAudio' ? 'audio-card' : className === 'selectSubtitles' ? 'subtitle-card' : '';
            
            const wrapper = document.createElement('div');
            wrapper.className = 'emby-select-wrapper';
            wrapper.dataset.select = className;
            
            const label = document.createElement('div');
            label.className = 'carousel-label';
            label.textContent = labelText;
            wrapper.appendChild(label);
            
            const container = document.createElement('div');
            container.className = 'emby-select-cards scrollSlider focuscontainer-x itemsContainer animatedScrollX';
            container.dataset.for = className;
            container.appendChild(createDummyCard(cardType));
            wrapper.appendChild(container);
            form.appendChild(wrapper);
        });
    }

    // Watch for selects with options and populate carousels
    setInterval(() => {
        const form = document.querySelector('form.trackSelections');
        if (!form) return;
        
        form.querySelectorAll('select.detailTrackSelect').forEach(select => {
            if (select.classList.contains('selectVideo')) return;
            
            const selectClass = getSelectClass(select);
            const carousel = form.querySelector(`.emby-select-cards[data-for="${selectClass}"]`);
            if (!carousel) return;
            
            if (!select._embyCardsContainer) {
                select._embyCardsContainer = carousel;
                monitorSelectAccess(select);
            }
            
            if (select.options.length > 0 && !select._cardsPopulated) {
                populateCards(select);
            }
        });
    }, 100);

    // Initialize on page load
    initSelects();
    
    // Watch for form appearing
    const observer = new MutationObserver(() => {
        initSelects();
    });
    observer.observe(document.body, { childList: true, subtree: true });

    // Listen for page navigation (when titles are clicked)
    window.addEventListener('viewshow', () => {
        setTimeout(initSelects, 0);
        setTimeout(initSelects, 50);
        setTimeout(initSelects, 100);
        setTimeout(initSelects, 200);
    });

    // Listen for hashchange (backup)
    window.addEventListener('hashchange', () => {
        setTimeout(initSelects, 0);
        setTimeout(initSelects, 50);
        setTimeout(initSelects, 100);
    });
    }
    
    // Start initialization with retry logic
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

