/*
 * Select  Cards (minimal carousel)
 * - Mirrors the native version select (select.selectSource)
 * - Builds a simple horizontal card carousel from the select options
 * - Keeps the original select in the DOM and syncs selection both ways
 * Minimal, dependency-free, inspired by simple-tracks.js
 */
(function (){
	'use strict';
	if (window.SelectToCardsLoaded) return; window.SelectToCardsLoaded = true;

    const log = (...args) => console.log('[SelectToCards]', ...args);
    const error = (...args) => console.error('[SelectToCards]', ...args);

    let currentItemId = null;


    function captureItemId() {
        const urlMatch = window.location.href.match(/[?&]id=([a-f0-9]+)/i);
        if (urlMatch) {
            currentItemId = urlMatch[1];
            log('Captured itemId from URL:', currentItemId);
            return currentItemId;
        }
        return null;
    }

    async function fetchStreams(itemId, mediaSourceId) {
        log('Fetching streams for itemId:', itemId, 'mediaSourceId:', mediaSourceId);
        
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
            error('Error fetching streams:', err);
            return null;
        }
    }

    async function loadTracksForVersion(versionSelect, mediaSourceId) {
        log('Loading tracks for version:', mediaSourceId);
        
        const form = versionSelect.closest('form.trackSelections');
        if (!form) return;
        
        const audioSelect = form.querySelector('select.selectAudio');
        const subtitleSelect = form.querySelector('select.selectSubtitles');
        
        // Clear old carousels and reset flags
        const oldAudioCarousel = form.querySelector('#stc-carousel-audio');
        const oldSubCarousel = form.querySelector('#stc-carousel-subtitle');
        if (oldAudioCarousel) oldAudioCarousel.remove();
        if (oldSubCarousel) oldSubCarousel.remove();
        if (audioSelect) audioSelect._stcCarouselBuilt = false;
        if (subtitleSelect) subtitleSelect._stcCarouselBuilt = false;
        
        // Enable the selects
        if (audioSelect) {
            audioSelect.disabled = false;
            audioSelect.innerHTML = '';
            // Rebuild empty carousel immediately
            buildCarouselFromSelect(audioSelect, 'audio');
        }
        if (subtitleSelect) {
            subtitleSelect.disabled = false;
            subtitleSelect.innerHTML = '';
            // Rebuild empty carousel immediately
            buildCarouselFromSelect(subtitleSelect, 'subtitle');
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
            console.warn('[SelectToCards] No itemId available');
            return;
        }
        
        // Fetch streams
        const streams = await fetchStreams(currentItemId, mediaSourceId);
        
        if (!streams) {
            error('Failed to fetch streams');
            return;
        }
        
        log('Received streams:', streams);
        
        // Populate audio tracks
        if (audioSelect && streams.audio && streams.audio.length > 0) {
            streams.audio.forEach((track, idx) => {
                const option = document.createElement('option');
                option.value = String(track.index);
                option.textContent = track.title;
                if (idx === 0) option.selected = true;
                audioSelect.appendChild(option);
            });
            log('Populated', streams.audio.length, 'audio tracks');
            // Build carousel for audio
            setTimeout(() => buildCarouselFromSelect(audioSelect, 'audio'), 50);
        }
        
        // Populate subtitle tracks
        if (subtitleSelect && streams.subs && streams.subs.length > 0) {
            streams.subs.forEach((track, idx) => {
                const option = document.createElement('option');
                option.value = String(track.index);
                option.textContent = track.title;
                if (idx === 0) option.selected = true;
                subtitleSelect.appendChild(option);
            });
            log('Populated', streams.subs.length, 'subtitle tracks');
            // Build carousel for subtitles
            setTimeout(() => buildCarouselFromSelect(subtitleSelect, 'subtitle'), 50);
        }
    }

    function buildCarouselFromSelect(selectElement, type = 'version') {
        try {
            if (!selectElement) return;

            // ensure single wrapper per select
            const form = selectElement.closest('form.trackSelections');
            if (!form) return;

            // Check if already built - but allow rebuild if we now have options
            if (selectElement._stcCarouselBuilt && selectElement.options.length === 0) {
                log('Carousel already built for', type, 'select, skipping empty');
                return;
            }
            
            // Mark as built only if we have options
            if (selectElement.options.length > 0) {
                selectElement._stcCarouselBuilt = true;
            }

            // remove previous carousel if any
            const existingId = 'stc-carousel-' + type;
            const existing = form.querySelector('#' + existingId);
            if (existing) {
                // Also remove separator that follows the carousel
                const nextSibling = existing.nextElementSibling;
                if (nextSibling && nextSibling.classList.contains('stc-separator')) {
                    nextSibling.remove();
                }
                existing.remove();
            }

		const wrapper = document.createElement('div');
		wrapper.id = existingId;
		wrapper.className = 'stc-carousel-wrapper';
		// minimal inline styles so it works without CSS files
		wrapper.style.display = 'flex';
		wrapper.style.flexDirection = 'column';
		wrapper.style.gap = '8px';
		wrapper.style.margin = '8px 0';
		
	// Label for audio/subtitle (not for version)
	if (type === 'audio' || type === 'subtitle') {
		const labelDiv = document.createElement('div');
		labelDiv.className = 'stc-type-label';
		labelDiv.textContent = type === 'audio' ? 'Audio' : 'Subtitles';
		labelDiv.style.fontSize = '16px';
		labelDiv.style.fontWeight = '500';
		labelDiv.style.color = 'rgba(255,255,255,0.9)';
		labelDiv.style.marginBottom = '4px';
		labelDiv.style.textAlign = 'center';
		wrapper.appendChild(labelDiv);
	}		const rail = document.createElement('div');
		rail.className = 'stc-rail';
		rail.style.display = 'flex';
		rail.style.overflowX = 'auto';
		rail.style.scrollBehavior = 'smooth';
		rail.style.padding = '6px 2px';
		rail.style.gap = '8px';
		rail.style.flex = '1 1 auto';
		
		// Determine height based on type
		const cardHeight = type === 'version' ? '100px' : '60px';
		rail.style.minHeight = cardHeight;
		
		// Hide scrollbar by default, show on hover
		rail.style.scrollbarWidth = 'thin';
		rail.style.scrollbarColor = 'transparent transparent';
		rail.addEventListener('mouseenter', () => {
			rail.style.scrollbarColor = 'rgba(255,255,255,0.3) transparent';
		});
		rail.addEventListener('mouseleave', () => {
			rail.style.scrollbarColor = 'transparent transparent';
		});
		// Webkit browsers
		const styleEl = document.createElement('style');
		styleEl.textContent = `
			.stc-rail::-webkit-scrollbar {
				height: 8px;
			}
			.stc-rail::-webkit-scrollbar-track {
				background: transparent;
			}
			.stc-rail::-webkit-scrollbar-thumb {
				background: transparent;
				border-radius: 4px;
			}
			.stc-rail:hover::-webkit-scrollbar-thumb {
				background: rgba(255,255,255,0.3);
			}
		`;
		if (!document.querySelector('#stc-scrollbar-style')) {
			styleEl.id = 'stc-scrollbar-style';
			document.head.appendChild(styleEl);
		}
		rail.style.flex = '1 1 auto';

		// If no options and audio/subtitle, show empty placeholder cards
		if (selectElement.options.length === 0 && (type === 'audio' || type === 'subtitle')) {
			// Create 3 empty dashed cards
			for (let i = 0; i < 3; i++) {
				const emptyCard = document.createElement('div');
				emptyCard.className = 'stc-card stc-empty';
				emptyCard.style.minWidth = '180px';
				emptyCard.style.maxWidth = '180px';
				emptyCard.style.height = '50px';
				emptyCard.style.flex = '0 0 auto';
				emptyCard.style.padding = '12px';
				emptyCard.style.border = '1px dashed rgba(255,255,255,0.15)';
				emptyCard.style.borderRadius = '6px';
				emptyCard.style.background = 'transparent';
				emptyCard.style.display = 'flex';
				emptyCard.style.alignItems = 'center';
				emptyCard.style.justifyContent = 'center';
				rail.appendChild(emptyCard);
			}
		}

		// build cards from options
		Array.from(selectElement.options).forEach(opt => {
		const card = document.createElement('div');
		card.className = 'stc-card';
		card.dataset.value = opt.value;
		
		// Size based on type
		if (type === 'version') {
			card.style.minWidth = '200px';
			card.style.maxWidth = '200px';
			card.style.height = '50px';
		} else {
			// Audio and subtitle cards
			card.style.minWidth = '150px';
			card.style.maxWidth = '150px';
			card.style.minHeight = '35px';
			card.style.height = '35px';
		}			card.style.flex = '0 0 auto';
			card.style.padding = '12px';
			card.style.border = '1px solid rgba(255,255,255,0.15)';
			card.style.borderRadius = '6px';
			card.style.background = 'rgba(255,255,255,0.05)';
			card.style.boxShadow = '0 2px 4px rgba(0,0,0,0.3)';
			card.style.cursor = 'pointer';
			card.style.transition = 'all 0.2s ease';
			card.style.overflow = 'hidden';
		card.style.display = 'flex';
		card.style.flexDirection = 'column';
		card.style.justifyContent = 'center';

		// Get text and split by folder icon if present
		const fullText = opt.getAttribute('data-original') || opt.textContent;
		let displayText = fullText;
		
		// For version cards, show only the part before 📁
		if (type === 'version' && fullText.includes('📁')) {
			const parts = fullText.split('📁');
			displayText = parts[0].trim();
			
			// Store both parts for later use
			if (!opt.hasAttribute('data-info')) {
				opt.setAttribute('data-info', displayText);
			}
			if (!opt.hasAttribute('data-filename')) {
				opt.setAttribute('data-filename', '📁 ' + parts.slice(1).join('📁').trim());
			}
			
			// Update the option text in the select to show the info part
			opt.textContent = displayText;
		} else if (type === 'version') {
			// No folder icon, store original
			if (!opt.hasAttribute('data-info')) {
				opt.setAttribute('data-info', displayText);
			}
			if (!opt.hasAttribute('data-filename')) {
				opt.setAttribute('data-filename', displayText);
			}
		}

	// primary label
	const label = document.createElement('div');
	label.className = 'stc-label';
	label.textContent = displayText;
	// Version: 13px, Audio/Subtitle: 11px
	label.style.fontSize = type === 'version' ? '13px' : '11px';
	label.style.fontWeight = '500';
	label.style.marginBottom = '0';
	label.style.color = 'inherit';
	label.style.whiteSpace = 'normal';
	label.style.overflow = 'visible';
	label.style.textOverflow = 'clip';
	label.style.lineHeight = '1.3';
	label.style.wordBreak = 'break-word';
	label.style.textAlign = 'center';
		card.appendChild(label);			// click -> update select
			card.addEventListener('click', (e) => {
				e.preventDefault();
				if (selectElement.value === card.dataset.value) return;
				selectElement.value = card.dataset.value;
				// trigger native change handlers
				const evt = new Event('change', { bubbles: true });
				selectElement.dispatchEvent(evt);
				updateActiveCard(rail, card.dataset.value);
				// scroll card into view within the rail (not the whole page)
				card.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
			// Update filename display for version select
			if (type === 'version') {
				const form = selectElement.closest('form.trackSelections');
				const filenameDiv = form?.querySelector('.stc-filename');
				const selectedOption = Array.from(selectElement.options).find(opt => opt.value === card.dataset.value);
				if (filenameDiv && selectedOption) {
					// Use data-filename attribute which contains the part after 📁
					const filename = selectedOption.getAttribute('data-filename');
					filenameDiv.textContent = filename || (selectedOption.getAttribute('data-original') || selectedOption.textContent);
				}
					// load audio/subtitle tracks
					loadTracksForVersion(selectElement, card.dataset.value);
				}
			});

			rail.appendChild(card);
			
			// hover effect
			card.addEventListener('mouseenter', () => {
				if (selectElement.value !== card.dataset.value) {
					card.style.background = 'rgba(255,255,255,0.1)';
					card.style.borderColor = 'rgba(255,255,255,0.25)';
				}
			});
			card.addEventListener('mouseleave', () => {
				if (selectElement.value !== card.dataset.value) {
					card.style.background = 'rgba(255,255,255,0.05)';
					card.style.borderColor = 'rgba(255,255,255,0.15)';
				}
			});
		});

		// helper to mark active card
		function updateActiveCard(railEl, value) {
			const cards = railEl.querySelectorAll('.stc-card');
			cards.forEach(c => {
				if (c.dataset.value === String(value)) {
					c.style.borderColor = 'rgba(0,164,220,1)';
					c.style.background = 'rgba(0,164,220,0.15)';
					c.style.boxShadow = '0 4px 12px rgba(0,164,220,0.3)';
				} else {
					c.style.borderColor = 'rgba(255,255,255,0.15)';
					c.style.background = 'rgba(255,255,255,0.05)';
					c.style.boxShadow = '0 2px 4px rgba(0,0,0,0.3)';
				}
			});
		}

		wrapper.appendChild(rail);

		// inject after select container
		const selectContainer = selectElement.closest('.selectContainer') || selectElement.parentNode;
		selectContainer.parentNode.insertBefore(wrapper, selectContainer.nextSibling);
		
		// Add separator line after version and audio carousels (only if it doesn't exist)
		if (type === 'version' || type === 'audio') {
			const existingSeparator = wrapper.nextElementSibling;
			if (!existingSeparator || !existingSeparator.classList.contains('stc-separator')) {
				const separator = document.createElement('div');
				separator.className = 'stc-separator';
				separator.style.width = '100%';
				separator.style.height = '1px';
				separator.style.background = 'rgba(255,255,255,0.1)';
				separator.style.margin = '16px 0';
				wrapper.parentNode.insertBefore(separator, wrapper.nextSibling);
			}
		}
		
		// For version select, add filename display at the TOP of the form
		if (type === 'version') {
			const form = selectElement.closest('form.trackSelections');
			if (form && !form.querySelector('.stc-filename')) {
				const filenameDiv = document.createElement('div');
				filenameDiv.className = 'stc-filename';
				filenameDiv.style.marginBottom = '16px';
				filenameDiv.style.padding = '8px 12px';
				filenameDiv.style.background = 'rgba(0,0,0,0.3)';
				filenameDiv.style.borderRadius = '4px';
				filenameDiv.style.color = 'rgba(255,255,255,0.6)';
				filenameDiv.style.fontSize = '12px';
				filenameDiv.style.textAlign = 'center';
				filenameDiv.style.fontFamily = 'monospace';
				filenameDiv.style.wordBreak = 'break-all';
				filenameDiv.textContent = ''; // Will be filled later
				
			// Insert at the very beginning of the form (first child)
			form.insertBefore(filenameDiv, form.firstChild);
			
			// Update filename from current selection
			const selectedOption = Array.from(selectElement.options).find(opt => opt.selected);
			if (selectedOption) {
				// Use data-filename attribute which contains the part after 📁
				const filename = selectedOption.getAttribute('data-filename');
				filenameDiv.textContent = filename || (selectedOption.getAttribute('data-original') || selectedOption.textContent);
			}
		}
	}		// set initial active
		updateActiveCard(rail, selectElement.value);
		
		// load tracks for initial selection (version only)
		if (type === 'version' && selectElement.value) {
			captureItemId();
			setTimeout(() => {
				loadTracksForVersion(selectElement, selectElement.value);
			}, 100);
		}

        // when select changes externally, sync carousel
        selectElement.addEventListener('change', () => {
            updateActiveCard(rail, selectElement.value);
            // ensure selected card visible within rail only
            const active = Array.from(rail.querySelectorAll('.stc-card')).find(c => c.dataset.value === selectElement.value);
            if (active) active.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
            // Update filename and load tracks (version only)
            if (type === 'version') {
                const form = selectElement.closest('form.trackSelections');
                const filenameDiv = form?.querySelector('.stc-filename');
                const selectedOption = Array.from(selectElement.options).find(opt => opt.value === selectElement.value);
                if (filenameDiv && selectedOption) {
                    // Use data-filename attribute which contains the part after 📁
                    const filename = selectedOption.getAttribute('data-filename');
                    filenameDiv.textContent = filename || (selectedOption.getAttribute('data-original') || selectedOption.textContent);
                }
                loadTracksForVersion(selectElement, selectElement.value);
            }
        });		// expose simple API for debugging
		wrapper._stc = { rail, updateActiveCard };
		log('Carousel built for', type, 'with', selectElement.options.length, 'cards');
        } catch (err) {
            error('Failed to build carousel:', err);
        }
	}

	function initialize() {
		const form = document.querySelector('form.trackSelections');
		if (!form || form._stcInitialized) return;
		form._stcInitialized = true;

		const versionSelect = form.querySelector('select.selectSource');
		const audioSelect = form.querySelector('select.selectAudio');
		const subtitleSelect = form.querySelector('select.selectSubtitles');
		
	if (!versionSelect) return;

	// Build carousels for all selects (CSS will control visibility)
	if (audioSelect) buildCarouselFromSelect(audioSelect, 'audio');
	if (subtitleSelect) buildCarouselFromSelect(subtitleSelect, 'subtitle');

	// watch for version options population
	const mo = new MutationObserver(() => {
		if (versionSelect.options.length > 0 && !versionSelect._stcCarouselBuilt) {
			buildCarouselFromSelect(versionSelect, 'version');
			mo.disconnect(); // stop watching after first build
		}
	});
	mo.observe(versionSelect, { childList: true, subtree: true });

	// If already populated, build immediately
	if (versionSelect.options.length > 0 && !versionSelect._stcCarouselBuilt) {
		buildCarouselFromSelect(versionSelect, 'version');
		mo.disconnect();
	}
}	function setupObserver() {
		const observer = new MutationObserver(() => {
			const form = document.querySelector('form.trackSelections');
			if (form && !form._stcInitialized) {
				initialize();
			}
		});
		observer.observe(document.body, { childList: true, subtree: true });

		// try immediate
		setTimeout(initialize, 100);
	}

	if (document.readyState === 'loading') {
		document.addEventListener('DOMContentLoaded', setupObserver);
	} else {
		setupObserver();
	}

	log('Loaded');
})();

