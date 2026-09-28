const id = crypto.randomUUID();
const attempted = new Set();
let reviewOwner = null;

// MudBlazor appends nested dialogs in stacking order. A parent modal behind
// the request's own dialog does not compete for its review; a later modal does.
function behindOwner(dialog, marker) {
    const owner = marker.closest('.mud-dialog');
    return owner && dialog.matches('.mud-dialog') &&
        !!(dialog.compareDocumentPosition(owner) & Node.DOCUMENT_POSITION_FOLLOWING);
}

export function tabId() { return id; }

// Let the closing dialog's own focus trap finish restoring its prior element
// before returning to the request action. Never steal focus from a new dialog.
export function restoreRequestFocus(origin) {
    requestAnimationFrame(() => requestAnimationFrame(() => {
        const button = origin?.querySelector('.request-action-button');
        const dialog = [...document.querySelectorAll('.mud-dialog')].some(d => d.getClientRects().length && getComputedStyle(d).visibility !== 'hidden');
        const active = document.activeElement;
        // The page heading only holds focus from arriving on the page. A review opened
        // from a link returns there on close, which is not a choice to keep focus away.
        const moved = active && active !== document.body && active !== button && !active.matches('main h1') && active.isConnected && active.getClientRects().length && !active.closest('[inert]');
        if (!dialog && !moved && button?.isConnected && button.getClientRects().length && !button.closest('[inert]')) button.focus({ preventScroll: true });
    }));
}

export function isReviewVisible(marker) {
    if (document.visibilityState !== 'visible' || !marker?.isConnected) return false;
    for (let node = marker.parentElement; node; node = node.parentElement) {
        const style = getComputedStyle(node);
        if (node.hidden || node.inert || style.display === 'none' || style.visibility === 'hidden') return false;
        if (node.tagName === 'DETAILS' && !node.open) return false;
    }
    const owner = marker.closest('[role="dialog"], dialog[open]');
    if ([...document.querySelectorAll('[role="dialog"], dialog[open]')].some(dialog =>
        dialog !== owner && !dialog.contains(marker) && !behindOwner(dialog, marker) && dialog.getClientRects().length && getComputedStyle(dialog).visibility !== 'hidden')) return false;
    return (owner ?? marker.parentElement).getClientRects().length > 0;
}

// A broken or still-loading thumbnail must not count as a displayed result.
export function areReviewMediaReady(marker, candidateIds) {
    // A dialog review, or an inline review area such as the Shots Takes view.
    const owner = marker.closest('[role="dialog"], dialog[open], [data-ai-review]');
    return !!owner && candidateIds.every(id => [...owner.querySelectorAll('[data-ai-candidate]')].some(media =>
        media.dataset.aiCandidate === id && media.getClientRects().length > 0 &&
        (media instanceof HTMLImageElement ? media.complete && media.naturalWidth > 0 :
            media instanceof HTMLVideoElement && media.readyState >= 1 && !media.error)));
}

export function tryReview(jobId, originTabId, origin, automatic) {
    if (!origin || !document.contains(origin)) return false;
    if (automatic) {
        if (attempted.has(jobId)) return false;
        attempted.add(jobId);
        // The host can own keyboard focus while an embedded browser stays on
        // screen. Visibility is enough for an author waiting for their result.
        if (originTabId !== id || document.visibilityState !== 'visible') return false;
    }
    if (reviewOwner && reviewOwner !== jobId) return false;
    const anotherDialog = [...document.querySelectorAll('[role="dialog"], dialog[open]')]
        .some(dialog => !dialog.contains(origin) && !behindOwner(dialog, origin) && dialog.getClientRects().length > 0 && getComputedStyle(dialog).visibility !== 'hidden');
    if (anotherDialog && reviewOwner !== jobId) return false;
    reviewOwner = jobId;
    return true;
}

export function closeReview(jobId) {
    attempted.add(jobId);
    if (reviewOwner === jobId) reviewOwner = null;
}
