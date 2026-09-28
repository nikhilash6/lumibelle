// Pure clipboard/event-adapter tests. Browser permissions and Blazor interop need the manual checks too.
import test, { beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const code = await readFile(new URL('../../src/Lumibelle.UI/wwwroot/asset-clipboard.js', import.meta.url), 'utf8');
const api = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));
const token = 'lumibelle-asset:v1:eyJ2ZXJzaW9uIjoxfQ==';
let root, listeners, calls, streams, disposed, dialogs, selectedText, failPaste;
const tick = async () => { await new Promise(setImmediate); await new Promise(setImmediate); };
function target(editable = false) { return { closest: selector => editable && selector.includes('input') ? {} : null }; }
function event(data, editable = false) { return { target: target(editable), clipboardData: data, prevented: false, preventDefault() { this.prevented = true; } }; }
function data(text = '', files = [], custom = '') { return { files, getData: type => type === 'application/x-lumibelle-asset' ? custom : text, setData(type, value) { this[type] = value; } }; }
function emit(kind, ev) { for (const fn of listeners.get(kind) || []) fn(ev); }
const imported = () => calls.filter(c => c[0].startsWith('Paste'));
const state = overrides => ({ disabled: false, token, destinationKey: 'episode-two/character', canPasteImage: true, ...overrides });
const button = attribute => ({ disabled: false, closest() { return this; }, hasAttribute: name => name === attribute });
beforeEach(() => {
    listeners = new Map(); calls = []; streams = []; disposed = []; dialogs = []; selectedText = ''; failPaste = false;
    root = { isConnected: true, handlers: new Map(), getClientRects: () => [1], addEventListener(k, f) { this.handlers.set(k, f); }, removeEventListener(k) { this.handlers.delete(k); } };
    globalThis.document = { addEventListener(k, f) { if (!listeners.has(k)) listeners.set(k, new Set()); listeners.get(k).add(f); },
        removeEventListener(k, f) { listeners.get(k)?.delete(f); }, querySelectorAll: () => dialogs };
    globalThis.window = { getSelection: () => ({ toString: () => selectedText }), location: { href: 'https://lumi.test/projects', origin: 'https://lumi.test' } };
    Object.defineProperty(globalThis, 'navigator', { value: { clipboard: {} }, configurable: true });
    globalThis.DotNet = { createJSStreamReference(blob) { const value = { blob, length: blob.size }; streams.push(value); return value; }, disposeJSObjectReference: value => disposed.push(value) };
    api.attach(root, { async invokeMethodAsync(...args) { calls.push(args); if (failPaste && args[0].startsWith('Paste')) throw Error('disconnected'); } });
    api.update(root, state());
});
afterEach(() => { api.detach(root); });

test('internal token wins over pixels to preserve metadata', () => {
    assert.deepEqual(api.clipboardPayload(data(token, [new Blob(['x'], { type: 'image/png' })])), { kind: 'asset', value: token });
});
test('custom clipboard type also works without plain text', () => {
    assert.equal(api.clipboardPayload(data('', [], token)).value, token);
});
test('arbitrary text, URLs and HTML are not fetched or imported', () => {
    for (const text of ['hello', 'https://example.test/asset.png', '/etc/passwd', '<img src=x>']) assert.equal(api.clipboardPayload(data(text)), null);
});
test('token-size limit rejects before interop', () => {
    assert.throws(() => api.clipboardPayload(data(token + 'x'.repeat(2048))), /too large/);
});
test('PNG, JPEG and WebP payloads keep their original Blob', () => {
    for (const type of ['image/png', 'image/jpeg', 'image/webp']) {
        const image = new Blob(['pixels'], { type }); const payload = api.clipboardPayload(data('', [image]));
        assert.equal(payload.kind, 'image'); assert.equal(payload.value, image);
    }
});
test('unsupported, empty and oversized image files are rejected', () => {
    for (const file of [{ size: 12, type: 'video/mp4' }, { size: 0, type: 'image/png' }, { size: 25 * 1024 * 1024 + 1, type: 'image/png' }])
        assert.throws(() => api.clipboardPayload(data('', [file])));
});
test('multiple pasted files are rejected as a group', () => {
    assert.throws(() => api.clipboardPayload(data('', [{ type: 'image/png', size: 1 }, { type: 'image/png', size: 2 }])), /one image/);
});
test('editable fields keep native copy and paste', async () => {
    const copy = event(data(), true), paste = event(data(token), true);
    emit('copy', copy); emit('paste', paste); await tick();
    assert.equal(copy.prevented, false); assert.equal(paste.prevented, false); assert.equal(imported().length, 0);
});
test('selected text is copied normally rather than replaced with an asset token', () => {
    selectedText = 'Some notes'; const copy = event(data()); emit('copy', copy); assert.equal(copy.prevented, false);
});
test('Ctrl+C writes internal and interoperable plain-text formats', () => {
    const copy = event(data()); emit('copy', copy); assert.equal(copy.prevented, true);
    assert.equal(copy.clipboardData['text/plain'], token); assert.equal(copy.clipboardData['application/x-lumibelle-asset'], token);
});
test('missing token leaves normal copy alone', () => {
    api.update(root, state({ token: null })); const copy = event(data()); emit('copy', copy); assert.equal(copy.prevented, false);
});
test('Ctrl+V sends exact target captured at event time', async () => {
    const paste = event(data(token)); emit('paste', paste); api.update(root, state({ destinationKey: 'different/project' })); await tick();
    assert.equal(paste.prevented, true); assert.deepEqual(imported(), [['PasteAssetReferenceAsync', token, 'episode-two/character']]);
});
test('external image streams its bytes rather than a base64 JSON payload', async () => {
    const blob = new Blob(['binary image'], { type: 'image/png' }); emit('paste', event(data('', [blob]))); await tick();
    assert.equal(streams.length, 1); assert.equal(streams[0].blob, blob); assert.equal(imported()[0][0], 'PasteImageAsync');
    assert.equal(imported()[0][1], streams[0]); assert.deepEqual(disposed, streams);
});
test('external image without selected destination reports a message and opens no stream', async () => {
    api.update(root, state({ canPasteImage: false })); emit('paste', event(data('', [new Blob(['x'], { type: 'image/png' })]))); await tick();
    assert.equal(streams.length, 0); assert.equal(imported().length, 0); assert.match(calls[0][1], /select an asset/);
});
test('hidden, disconnected or blocked components do not capture page clipboard events', async () => {
    api.update(root, state({ disabled: true })); const disabled = event(data(token)); emit('paste', disabled);
    api.update(root, state()); root.isConnected = false; const detached = event(data(token)); emit('paste', detached);
    root.isConnected = true; root.getClientRects = () => []; const hidden = event(data(token)); emit('paste', hidden); await tick();
    assert.equal(imported().length, 0); assert.equal(disabled.prevented || detached.prevented || hidden.prevented, false);
});
test('another dialog keeps clipboard focus instead of importing into the underlying gallery', async () => {
    dialogs = [{ isConnected: true, getClientRects: () => [1], contains: () => false }];
    const paste = event(data(token)); emit('paste', paste); await tick(); assert.equal(paste.prevented, false); assert.equal(imported().length, 0);
});
test('parallel paste events do not issue duplicate imports', async () => {
    emit('paste', event(data(token))); emit('paste', event(data(token))); await tick(); assert.equal(imported().length, 1);
});
test('interop failure disposes the image stream and leaves a usable retry path', async () => {
    failPaste = true; emit('paste', event(data('', [new Blob(['x'], { type: 'image/png' })]))); await tick();
    assert.equal(disposed.length, 1); assert.ok(calls.some(c => c[0] === 'ClipboardStatusAsync' && c[1].includes('could not finish')));
    failPaste = false; emit('paste', event(data(token))); await tick(); assert.equal(imported().length, 2);
});
test('reattach replaces listeners and detach removes them', async () => {
    api.attach(root, { async invokeMethodAsync(...args) { calls.push(args); } }); api.update(root, state());
    assert.equal(listeners.get('copy').size, 1); assert.equal(listeners.get('paste').size, 1);
    api.detach(root); emit('paste', event(data(token))); await tick(); assert.equal(imported().length, 0); assert.equal(root.handlers.size, 0);
});
test('Paste button prefers an internal token over clipboard image formats', async () => {
    navigator.clipboard.read = async () => [{ types: ['image/png', 'text/plain'], getType: async t => new Blob([t === 'text/plain' ? token : 'pixels'], { type: t }) }];
    await root.handlers.get('click')({ target: button('data-reuse-paste') });
    assert.equal(imported()[0][0], 'PasteAssetReferenceAsync'); assert.equal(streams.length, 0);
});
test('Copy button invokes native clipboard write directly', async () => {
    let value; navigator.clipboard.writeText = async text => { value = text; };
    await root.handlers.get('click')({ target: button('data-reuse-copy') }); assert.equal(value, token);
});
test('clipboard permission refusal gives keyboard and secure-origin guidance', async () => {
    navigator.clipboard.read = async () => { const e = Error('denied'); e.name = 'NotAllowedError'; throw e; };
    await root.handlers.get('click')({ target: button('data-reuse-paste') }); assert.match(calls.at(-1)[1], /Ctrl\+C \/ Ctrl\+V/); assert.match(calls.at(-1)[1], /HTTPS or localhost/);
});
