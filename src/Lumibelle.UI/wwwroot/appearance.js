// Runs before styles in both hosts. UI preferences never enter project data.
(() => {
    if (window.lumibelleAppearance) return;
    const key = 'lumibelle.appearance.v1';
    const normalize = value => ['light', 'dark', 'system'].includes(value) ? value : 'system';
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    let preference = 'system';
    try { preference = normalize(localStorage.getItem(key)); } catch { }
    const snapshot = () => ({ preference, dark: preference === 'dark' || preference === 'system' && media.matches });
    function paint() {
        const state = snapshot();
        document.documentElement.dataset.theme = state.dark ? 'dark' : 'light';
        document.documentElement.style.colorScheme = state.dark ? 'dark' : 'light';
        window.dispatchEvent(new CustomEvent('lumibelle:appearance', { detail: state }));
        return state;
    }
    window.lumibelleAppearance = {
        snapshot,
        set(value) {
            preference = normalize(value);
            try { localStorage.setItem(key, preference); } catch { }
            return paint();
        }
    };
    media.addEventListener('change', () => { if (preference === 'system') paint(); });
    window.addEventListener('storage', event => {
        if (event.key === key || event.key === null) { preference = normalize(event.newValue); paint(); }
    });
    paint();
})();
