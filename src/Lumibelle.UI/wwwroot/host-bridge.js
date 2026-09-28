let receiver;
const click = async event => {
    const link = event.target.closest('a');
    if (!link || event.defaultPrevented) return;
    if (link.origin === location.origin && link.target === '_blank') link.target = '_self';
    if (link.hasAttribute('download')) {
        event.preventDefault();
        const host = receiver;
        const name = link.download || link.pathname.split('/').pop();
        // Review recovery copies contain UTF-8 JSON, not a stored media resource.
        if (/^data:application\/json[;,]/i.test(link.href)) {
            const text = await (await fetch(link.href)).text();
            await host.invokeMethodAsync('SaveText', name, text);
        } else {
            await host.invokeMethodAsync('SaveResource', name, link.pathname + link.search);
        }
    } else if (link.origin !== location.origin && /^https?:$/.test(link.protocol)) {
        event.preventDefault(); receiver.invokeMethodAsync('OpenExternal', link.href);
    }
};
export function mount(reference) { receiver = reference; document.addEventListener('click', click, true); }
export function isDesktop() { return !!receiver; }
export function saveText(name, text) { return receiver.invokeMethodAsync('SaveText', name, text); }
export function unmount() { document.removeEventListener('click', click, true); receiver = null; }
export function download(name, url) { const a = document.createElement('a'); a.href = url; a.download = name; document.body.append(a); a.click(); a.remove(); }

// Programmatic resource downloads must await the native bridge directly. A detached
// Blob anchor neither reaches its document click handler nor names a stored resource.
export function saveResource(name, url) {
    if (!receiver) throw new Error('The desktop save bridge is unavailable.');
    return receiver.invokeMethodAsync('SaveResource', name, url);
}
