/**
 * Initialization wrapper for MyJellyfinPlugin
 * Ensures scripts load in correct order and DOM is ready
 */
(function() {
    'use strict';
    
    console.log('[MyJellyfinPlugin] Initializing...');
    
    // Queue for scripts waiting for MediaServerUI
    window.__MyJellyfinPluginQueue = window.__MyJellyfinPluginQueue || [];
    
    // Helper to run a function when MediaServerUI is ready
    window.whenMediaServerUIReady = function(fn) {
        if (window.MediaServerUI && window.MediaServerUI.ready) {
            // Already ready, execute immediately
            try {
                fn();
            } catch (error) {
                console.error('[MyJellyfinPlugin] Error executing queued function:', error);
            }
        } else {
            // Queue for later
            window.__MyJellyfinPluginQueue.push(fn);
        }
    };
    
    // Process queue when MediaServerUI becomes ready
    window.markMediaServerUIReady = function() {
        if (window.MediaServerUI) {
            window.MediaServerUI.ready = true;
            console.log('[MyJellyfinPlugin] MediaServerUI ready, processing queue (' + window.__MyJellyfinPluginQueue.length + ' items)');
            
            while (window.__MyJellyfinPluginQueue.length > 0) {
                const fn = window.__MyJellyfinPluginQueue.shift();
                try {
                    fn();
                } catch (error) {
                    console.error('[MyJellyfinPlugin] Error processing queue:', error);
                }
            }
        }
    };
})();
