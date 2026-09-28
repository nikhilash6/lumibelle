const watches = new Map();
export function watch(id, reference) {
    unwatch(id);
    const refresh = () => { if (document.visibilityState === 'visible') reference.invokeMethodAsync('RefreshModel').catch(() => {}); };
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', refresh);
    watches.set(id, refresh);
}
export function unwatch(id) {
    const refresh = watches.get(id);
    if (!refresh) return;
    window.removeEventListener('focus', refresh);
    document.removeEventListener('visibilitychange', refresh);
    watches.delete(id);
}
