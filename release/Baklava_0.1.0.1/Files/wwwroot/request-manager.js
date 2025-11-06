(function() {
    'use strict';
    
    console.log('[RequestManager] Initializing...');
    
    // Update request status
    async function updateStatus(requestId, newStatus) {
        try {
            console.log('[RequestManager] Updating status:', requestId, '->', newStatus);
            
            const userId = window.ApiClient.getCurrentUserId();
            const user = await window.ApiClient.getUser(userId);
            const username = user?.Name || 'Unknown';
            
            await window.ApiClient.ajax({
                type: 'PUT',
                url: window.ApiClient.getUrl(`api/myplugin/requests/${requestId}`),
                data: JSON.stringify({
                    status: newStatus,
                    approvedBy: username
                }),
                contentType: 'application/json'
            });
            
            console.log('[RequestManager] Status updated successfully');
        } catch (err) {
            console.error('[RequestManager] Error updating status:', err);
            alert('Failed to update request status');
        }
    }
    
    // Delete request
    async function deleteRequest(requestId) {
        try {
            console.log('[RequestManager] Deleting request:', requestId);
            
            await window.ApiClient.ajax({
                type: 'DELETE',
                url: window.ApiClient.getUrl(`api/myplugin/requests/${requestId}`)
            });
            
            console.log('[RequestManager] Request deleted successfully');
        } catch (err) {
            console.error('[RequestManager] Error deleting request:', err);
            alert('Failed to delete request');
        }
    }
    
    // Expose API
    window.RequestManager = {
        updateStatus,
        deleteRequest
    };
    
    console.log('[RequestManager] API exposed');
})();
