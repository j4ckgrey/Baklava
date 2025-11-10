/*
 * select-to-select.js
 * Alternate UI: use <select> dropdowns instead of card carousels.
 * This file provides a small toggle to switch display modes and keeps
 * the native selects visible when 'selects' mode is chosen.
 */
(function () {
    'use strict';

    console.log('[SelectToSelect] Loading alternative select UI...');

    function initToggle() {
        // Insert a small toggle control near the top of the page (inside the first form.trackSelections)
        const form = document.querySelector('form.trackSelections');
        if (!form) return;

        if (document.getElementById('stc-mode-toggle')) return; // already added

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

        // Place wrapper before the form
        form.parentNode.insertBefore(wrapper, form);

        // Initialize from localStorage
        const mode = localStorage.getItem('baklava_view_mode') || 'cards';
        select.value = mode;
        applyMode(mode);

        select.addEventListener('change', () => {
            const v = select.value;
            localStorage.setItem('baklava_view_mode', v);
            applyMode(v);
        });
    }

    function applyMode(mode) {
        if (mode === 'selects') {
            document.body.classList.add('stc-show-selects');
        } else {
            document.body.classList.remove('stc-show-selects');
        }
    }

    // Run on DOMContentLoaded and on dynamic content changes (MutationObserver)
    function start() {
        initToggle();

        const observer = new MutationObserver((records) => {
            initToggle();
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else start();
})();
