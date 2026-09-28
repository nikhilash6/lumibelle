const PREFIX = 'lumibelle-asset:v1:';
const MAX_BYTES = 25 * 1024 * 1024;
const MAX_TOKEN = 2048;
const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/webp'];
const bindings = new WeakMap();

export function isEditable(target) {
    return !!target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"]');
}

// Never treat arbitrary text, HTML, file paths or URLs as asset references.
export function clipboardPayload(data) {
    const text = data?.getData?.('application/x-lumibelle-asset') || data?.getData?.('text/plain') || '';
    if (text.startsWith(PREFIX)) {
        if (text.length > MAX_TOKEN) throw new Error('The copied asset reference is too large. Copy it again.');
        return { kind: 'asset', value: text };
    }
    const files = Array.from(data?.files || []);
    if (!files.length) return null;
    if (files.length !== 1) throw new Error('Paste one image at a time. No images were imported.');
    return imagePayload(files[0]);
}

function imagePayload(file) {
    if (!IMAGE_TYPES.includes(file.type)) throw new Error('Paste a PNG, JPEG or WebP image. Other clipboard files are not imported.');
    if (!file.size || file.size > MAX_BYTES) throw new Error('Clipboard images must be no larger than 25 MB.');
    return { kind: 'image', value: file };
}

function visible(element) { return !!element?.isConnected && element.getClientRects().length > 0; }
function allowed(root, event, binding) {
    if (!visible(root) || binding.state.disabled || binding.pending || isEditable(event.target)) return false;
    if (Array.from(document.querySelectorAll('[role="dialog"], dialog[open]')).some(d => visible(d) && !d.contains(root))) return false;
    return true;
}
function status(binding, message) {
    return binding.dotnet.invokeMethodAsync('ClipboardStatusAsync', message).catch(() => {});
}

async function deliver(binding, payload, state) {
    if (!payload || binding.pending || state.disabled) return;
    if (payload.kind === 'image' && !state.canPasteImage) {
        await status(binding, 'Create or select an asset before pasting an external image.'); return;
    }
    binding.pending = true;
    let stream;
    try {
        if (payload.kind === 'asset') {
            await binding.dotnet.invokeMethodAsync('PasteAssetReferenceAsync', payload.value, state.destinationKey);
        } else {
            // Stream bytes, not a base64 JSON message. Server-side validation checks size and actual image format again.
            stream = DotNet.createJSStreamReference(payload.value);
            await binding.dotnet.invokeMethodAsync('PasteImageAsync', stream, state.destinationKey);
        }
    } catch {
        await status(binding, 'The paste could not finish. Check the connection and the asset library before trying again.');
    } finally {
        if (stream) {
            try { DotNet.disposeJSObjectReference(stream); } catch { /* The .NET stream normally disposed it already. */ }
        }
        binding.pending = false;
    }
}

async function readSystemClipboard(binding, state) {
    if (navigator.clipboard?.read) {
        const items = await navigator.clipboard.read();
        // Internal copies can also contain pixels. Preserve their metadata when pasting back into Lumibelle.
        for (const item of items) if (item.types.includes('text/plain')) {
            const text = await (await item.getType('text/plain')).text();
            if (text.startsWith(PREFIX)) {
                return deliver(binding, clipboardPayload({ getData: () => text }), state);
            }
        }
        const images = items.filter(item => item.types.some(type => IMAGE_TYPES.includes(type)));
        if (images.length > 1) throw new Error('Paste one image at a time. No images were imported.');
        if (images.length === 1) {
            const type = IMAGE_TYPES.find(t => images[0].types.includes(t));
            return deliver(binding, imagePayload(await images[0].getType(type)), state);
        }
    } else if (navigator.clipboard?.readText) {
        const text = await navigator.clipboard.readText();
        const payload = clipboardPayload({ getData: () => text });
        if (payload) return deliver(binding, payload, state);
    }
    await status(binding, 'No supported clipboard image or Lumibelle asset was found. Use Ctrl+V for an image copied from another app.');
}

async function imagePng(url) {
    const source = new URL(url, window.location.href);
    if (source.origin !== window.location.origin || !source.pathname.startsWith('/media/projects/')) throw new Error('Invalid image source.');
    const response = await fetch(source.href, { credentials: 'same-origin' });
    if (!response.ok) throw new Error('The selected image is unavailable.');
    const blob = await response.blob();
    if (!IMAGE_TYPES.includes(blob.type) || blob.size > MAX_BYTES) throw new Error('The selected image cannot be copied to the system clipboard.');
    if (blob.type === 'image/png') return blob;
    const image = await createImageBitmap(blob);
    try {
        if (image.width * image.height > 40_000_000) throw new Error('This image is too large for clipboard conversion.');
        const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
        canvas.getContext('2d').drawImage(image, 0, 0);
        return await new Promise((resolve, reject) => canvas.toBlob(result => result ? resolve(result) : reject(new Error('Image conversion failed.')), 'image/png'));
    } finally { image.close(); }
}

export function attach(root, dotnet) {
    detach(root);
    const binding = { dotnet, state: { disabled: true }, pending: false };
    binding.copy = event => {
        if (!allowed(root, event, binding) || window.getSelection()?.toString()) return;
        if (!binding.state.token || !event.clipboardData) return;
        event.preventDefault();
        event.clipboardData.setData('text/plain', binding.state.token);
        event.clipboardData.setData('application/x-lumibelle-asset', binding.state.token);
        void status(binding, 'Item copied. Paste into another asset or project. Use Copy image pixels to paste an image into another app.');
    };
    binding.paste = event => {
        if (!allowed(root, event, binding)) return;
        try {
            const payload = clipboardPayload(event.clipboardData);
            if (!payload) return;
            event.preventDefault();
            void deliver(binding, payload, { ...binding.state });
        } catch (error) { event.preventDefault(); void status(binding, error.message); }
    };
    binding.click = async event => {
        const button = event.target?.closest?.('[data-reuse-copy], [data-reuse-paste], [data-reuse-copy-pixels]');
        if (!button || button.disabled || binding.state.disabled || binding.pending) return;
        const state = { ...binding.state };
        try {
            if (button.hasAttribute('data-reuse-copy')) {
                if (!state.token) return;
                await navigator.clipboard.writeText(state.token);
                await status(binding, 'Item copied. Paste into another asset or project.');
            } else if (button.hasAttribute('data-reuse-copy-pixels')) {
                if (!state.imageUrl) return;
                if (!navigator.clipboard?.write || typeof ClipboardItem === 'undefined') throw new Error('Image clipboard access is not available.');
                const data = { 'image/png': imagePng(state.imageUrl) };
                if (state.token) data['text/plain'] = new Blob([state.token], { type: 'text/plain' });
                // Start the clipboard write in the native click handler, while user activation is still present.
                await navigator.clipboard.write([new ClipboardItem(data)]);
                await status(binding, 'Image copied to the system clipboard.');
            } else await readSystemClipboard(binding, state);
        } catch (error) {
            await status(binding, error.name === 'NotAllowedError' || error instanceof TypeError
                ? 'Clipboard permission is unavailable. Use Ctrl+C / Ctrl+V, or open Lumibelle over HTTPS or localhost.' : error.message);
        }
    };
    bindings.set(root, binding);
    document.addEventListener('copy', binding.copy);
    document.addEventListener('paste', binding.paste);
    root.addEventListener('click', binding.click);
}
export function update(root, state) { const binding = bindings.get(root); if (binding) binding.state = state; }
export function detach(root) {
    const binding = bindings.get(root); if (!binding) return;
    document.removeEventListener('copy', binding.copy); document.removeEventListener('paste', binding.paste);
    root.removeEventListener('click', binding.click); bindings.delete(root);
}
