let saveHandler;
let dialogObserver;
let currentDialog;
let previousFocus;
export function registerSave(reference) {
    unregisterSave();
    saveHandler = event => {
        if (currentDialog) {
            if (event.key === 'Escape') {
                event.preventDefault();
                currentDialog.querySelector('[data-dialog-close]')?.click();
                return;
            }
            if (event.key === 'Tab') {
                const focusable = [...currentDialog.querySelectorAll('button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled]), summary')];
                const first = focusable[0], last = focusable.at(-1);
                if (event.shiftKey && (document.activeElement === first || !currentDialog.contains(document.activeElement))) { event.preventDefault(); last?.focus(); }
                else if (!event.shiftKey && (document.activeElement === last || !currentDialog.contains(document.activeElement))) { event.preventDefault(); first?.focus(); }
            }
        }
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault();
            reference.invokeMethodAsync('SaveFromKeyboard');
        }
    };
    document.addEventListener('keydown', saveHandler);
    dialogObserver = new MutationObserver(() => {
        const dialog = document.querySelector('.studio-dialog[role="dialog"]');
        if (dialog === currentDialog) return;
        if (dialog) {
            previousFocus = document.activeElement;
            currentDialog = dialog;
            (dialog.querySelector('[data-dialog-close]') || dialog.querySelector('button'))?.focus();
        } else {
            currentDialog = null;
            if (previousFocus?.isConnected) previousFocus.focus();
            previousFocus = null;
        }
    });
    dialogObserver.observe(document.body, { childList: true, subtree: true });
}
export function unregisterSave() {
    if (saveHandler) document.removeEventListener('keydown', saveHandler);
    saveHandler = undefined;
    dialogObserver?.disconnect();
    dialogObserver = undefined;
    currentDialog = previousFocus = null;
}
export async function downloadText(name, text) {
    const host = await import('./host-bridge.js');
    if (host.isDesktop()) return host.saveText(name, text);
    const url = URL.createObjectURL(new Blob([text], { type: name.endsWith('.md') ? 'text/markdown;charset=utf-8' : 'application/json;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

let drawer, drawerFocus, drawerHandler;
export function syncDrawer(selector) {
    const next = matchMedia('(max-width: 850px)').matches && selector ? document.querySelector(selector) : null;
    if (next === drawer) return;
    if (drawer) { drawer.removeAttribute('role'); drawer.removeAttribute('aria-modal'); document.removeEventListener('keydown', drawerHandler, true); document.body.style.overflow = ''; }
    if (!next) { drawer = null; if (drawerFocus?.isConnected) drawerFocus.focus(); drawerFocus = null; return; }
    drawerFocus = document.activeElement; drawer = next;
    drawer.setAttribute('role', 'dialog'); drawer.setAttribute('aria-modal', 'true'); document.body.style.overflow = 'hidden';
    const focusable = () => [...drawer.querySelectorAll('button:not([disabled]), a[href], select:not([disabled]), textarea:not([disabled]), input:not([disabled]), summary')].filter(e => e.getClientRects().length);
    drawerHandler = event => {
        if (document.querySelector('.script-review-dialog')) return;
        if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); drawer.querySelector('.mobile-close')?.click(); }
        if (event.key === 'Tab') {
            const items = focusable(), first = items[0], last = items.at(-1);
            if (event.shiftKey && (document.activeElement === first || !drawer.contains(document.activeElement))) { event.preventDefault(); last?.focus(); }
            else if (!event.shiftKey && (document.activeElement === last || !drawer.contains(document.activeElement))) { event.preventDefault(); first?.focus(); }
        }
    };
    document.addEventListener('keydown', drawerHandler, true); focusable()[0]?.focus();
}
