// Inline playback belongs to the visible shot. Browsing takes never selects one for production.
export function attach(root) {
    const pause = () => root.querySelector('video')?.pause();
    const panel = root.closest('[data-workspace-panel]');
    const visibility = () => { if (document.hidden || panel?.hidden) pause(); };
    const observer = new MutationObserver(visibility);
    if (panel) observer.observe(panel, { attributes: true, attributeFilter: ['hidden'] });
    document.addEventListener('visibilitychange', visibility);
    const review = event => { if (event.target.closest('button')) pause(); };
    root.addEventListener('click', review);
    return { dispose() { pause(); observer.disconnect(); document.removeEventListener('visibilitychange', visibility); root.removeEventListener('click', review); } };
}
