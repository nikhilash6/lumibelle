import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import vm from 'node:vm';

const root = new URL('../../src/Lumibelle.UI/wwwroot/', import.meta.url);
const timeCode = await readFile(new URL('cut-time.js', root), 'utf8');
const playerCode = await readFile(new URL('cut-player.js', root), 'utf8');
const { markFrame } = await import(`data:text/javascript,${encodeURIComponent(timeCode)}`);
const clip = (id = 'a', extra = {}) => ({ id, takeId: `take-${id}`, shotId: `shot-${id}`, shotTitle: id,
    takeLabel: 'Take 1', startFrame: 5, endFrameExclusive: 20, frameCount: 40, fps: 24, ...extra });
const copy = value => JSON.parse(JSON.stringify(value));
const gate = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
async function until(check) {
    for (let i = 0; i < 200; i++) { if (check()) return; await delay(5); }
    assert.fail('Timed out waiting for player state');
}

// A mark must preserve the exact visible frame. Dragging uses a different,
// deliberately clamping policy; never silently reuse that policy for these buttons.
test('start mark uses the displayed zero-based frame without mutating its clip', () => {
    const c = clip(); const marked = markFrame(c, 10, 'start');
    assert.equal(marked.startFrame, 10); assert.equal(marked.endFrameExclusive, 20);
    assert.equal(marked.id, c.id); assert.equal(marked.takeId, c.takeId); assert.equal(c.startFrame, 5);
});
test('end mark includes the displayed frame by converting to an exclusive boundary', () => {
    assert.equal(markFrame(clip(), 10, 'end').endFrameExclusive, 11);
});
test('marks can restore the first and last frames from outside the old trim', () => {
    assert.equal(markFrame(clip(), 0, 'start').startFrame, 0);
    assert.equal(markFrame(clip(), 39, 'end').endFrameExclusive, 40);
});
test('one-frame clips are allowed at either existing boundary', () => {
    assert.equal(markFrame(clip(), 19, 'start').startFrame, 19);
    assert.equal(markFrame(clip(), 5, 'end').endFrameExclusive, 6);
});
test('crossed boundaries and no-op marks are rejected', () => {
    for (const [frame, edge] of [[20, 'start'], [39, 'start'], [0, 'end'], [4, 'end'], [5, 'start'], [19, 'end']])
        assert.equal(markFrame(clip(), frame, edge), null);
});
test('invalid frame indices and unknown edges cannot become edits', () => {
    for (const frame of [-1, 40, 100, 1.5, NaN, Infinity, null, undefined, '10'])
        for (const edge of ['start', 'end']) assert.equal(markFrame(clip(), frame, edge), null);
    assert.equal(markFrame(null, 10, 'start'), null);
    assert.equal(markFrame(clip(), 10, 'middle'), null);
});

class Element {
    handlers = new Map(); dataset = {}; attributes = new Map(); hidden = true; disabled = false;
    textContent = ''; currentTime = 0; readyState = 2; seeking = false; paused = true;
    addEventListener(name, fn, options = {}) {
        const items = this.handlers.get(name) ?? []; items.push({ fn, signal: options.signal }); this.handlers.set(name, items);
    }
    removeEventListener(name, fn) { this.handlers.set(name, (this.handlers.get(name) ?? []).filter(h => h.fn !== fn)); }
    async emit(name, data = {}) { await Promise.all((this.handlers.get(name) ?? []).filter(h => !h.signal?.aborted).map(h => h.fn({ target: this, ...data }))); }
    setAttribute(k, v) { this.attributes.set(k, String(v)); }
    removeAttribute(k) { this.attributes.delete(k); }
    pause() { this.paused = true; }
    async play() { this.paused = false; await this.emit('playing'); }
    load() {}
}
async function fixture(t, { clips = [clip(), clip('b')], selected = 'a', fetchFrame, invoke } = {}) {
    const elements = new Map(), videos = [new Element(), new Element()], calls = [], requests = [], frames = [];
    const get = selector => { if (!elements.has(selector)) elements.set(selector, new Element()); return elements.get(selector); };
    const element = new Element(); element.querySelector = get; element.querySelectorAll = () => videos;
    let player, version = 7, unavailable = [], previewSelection = 0, callbacks;
    const f = { element, videos, calls, requests, frames, get, button: name => get(`[data-action=${name}]`),
        async click(name) { await this.button(name).emit('click'); },
        async source(frame) { const input = this.button('source-frame'); input.value = String(frame + 1); await input.emit('input'); },
        async shown(frame, id) { await until(() => element.dataset.frameReady === 'true' && Number(element.dataset.frameIndex) === frame && (!id || element.dataset.clipId === id)); },
        update(next = clips, id = selected, missing = unavailable, reveal = previewSelection) {
            clips = copy(next); selected = id; unavailable = missing; previewSelection = reveal; version++;
            player.update(clips, selected, version, unavailable, previewSelection);
        },
        get clips() { return copy(clips); }, get version() { return version; }, get callbacks() { return callbacks; }
    };
    const context = vm.createContext({ console, AbortController, DOMException, setTimeout, clearTimeout,
        setInterval: () => 0, clearInterval() {}, requestAnimationFrame: () => 0, cancelAnimationFrame() {},
        document: new Element(), window: new Element(),
        Image: class { async decode() {} },
        URL: { createObjectURL: () => `blob:${frames.push(true)}`, revokeObjectURL() {} },
        fetch: async (url, options) => { requests.push({ url, options }); return fetchFrame ? fetchFrame(url, options) : { ok: true, blob: async () => ({}) }; },
        attachTimeline: (_root, _project, cb) => { callbacks = cb; return { update() {}, position() {}, restoreView() {}, view: () => ({}), dispose() {} }; }
    });
    const timing = new vm.SourceTextModule(timeCode, { context }); await timing.link(() => {}); await timing.evaluate();
    const position = new vm.SourceTextModule('export const key=()=>"cut", read=()=>({}), write=()=>{};', { context });
    await position.link(() => {}); await position.evaluate();
    const timeline = new vm.SourceTextModule('export const attach=globalThis.attachTimeline;', { context });
    await timeline.link(() => {}); await timeline.evaluate();
    const module = new vm.SourceTextModule(playerCode, { context });
    await module.link(name => ({ './cut-time.js': timing, './workspace-position.js': position, './cut-timeline.js': timeline })[name]);
    await module.evaluate();
    player = module.namespace.attach(element, {
        async invokeMethodAsync(...args) {
            calls.push(copy(args));
            if (invoke) await invoke(args, f);
            if (args[0] === 'SelectClip') { selected = args[1]; player.update(clips, selected, version, unavailable, previewSelection); }
            if (args[0] === 'TrimClip') {
                // Model the existing parent callback: pause, check the version and
                // publish an acknowledged edit. The JavaScript never edits optimistically.
                player.pause();
                if (version !== args[4]) return;
                clips = clips.map(c => c.id === args[1] ? { ...c, startFrame: args[2], endFrameExclusive: args[3] } : c);
                version++; player.update(clips, selected, version, unavailable, previewSelection);
            }
        }
    }, 'project');
    f.player = player;
    t.after(() => player.dispose());
    player.update(clips, selected, version, unavailable, previewSelection);
    return f;
}

test('loading frames disable both marks, including programmatically delivered clicks', async t => {
    const load = gate(), f = await fixture(t, { fetchFrame: () => load.promise });
    assert.equal(f.button('mark-start').disabled, true); assert.equal(f.button('mark-end').disabled, true);
    await f.click('mark-end'); assert.equal(f.calls.length, 0);
    load.resolve({ ok: true, blob: async () => ({}) }); await f.shown(5);
    assert.equal(f.button('mark-start').disabled, true); assert.equal(f.button('mark-end').disabled, false);
});
test('marking the visible clip selects it even when a different clip is selected', async t => {
    const f = await fixture(t, { selected: 'b' }); await f.shown(5, 'a');
    f.player.seek(5 / 24); await f.shown(10, 'a');
    const version = f.version; await f.click('mark-end');
    await until(() => f.clips[0].endFrameExclusive === 11); await f.shown(10, 'a');
    assert.deepEqual(f.calls, [['SelectClip', 'a'], ['TrimClip', 'a', 5, 11, version]]);
    assert.equal(f.clips[1].endFrameExclusive, 20);
    assert.equal(f.button('mark-end').disabled, true);
    assert.equal(f.get('[data-source-review]').hidden, false);
});
test('the full-source slider and frame arrows recover previously trimmed frames', async t => {
    const f = await fixture(t); await f.shown(5);
    await f.click('preview-selected'); await f.shown(5);
    await f.source(0); await f.shown(0);
    assert.equal(f.button('previous').disabled, true); assert.equal(f.button('mark-end').disabled, true);
    await f.click('mark-start'); await until(() => f.clips[0].startFrame === 0); await f.shown(0);
    await f.source(38); await f.shown(38); await f.click('next'); await f.shown(39);
    assert.equal(f.button('next').disabled, true); assert.equal(f.button('mark-start').disabled, true);
    await f.click('mark-end'); await until(() => f.clips[0].endFrameExclusive === 40); await f.shown(39);
    assert.equal(f.get('[data-source-position]').textContent, '40 / 40');
    assert.equal(f.get('[data-trim-target]').textContent, 'Trim visible clip · a · frame 40');
});
test('preview selected clip explicitly follows selection rather than the old playhead', async t => {
    const f = await fixture(t, { selected: 'b' }); await f.shown(5, 'a');
    await f.click('preview-selected'); await f.shown(5, 'b');
    await f.source(8); await f.shown(8, 'b'); await f.click('mark-start');
    await until(() => f.clips[1].startFrame === 8);
    assert.equal(f.clips[0].startFrame, 5);
});
test('duplicate takes trim only the previewed clip occurrence', async t => {
    const f = await fixture(t, { clips: [clip(), clip('repeat', { takeId: 'take-a', shotId: 'shot-a' })], selected: 'repeat' });
    await f.shown(5, 'a'); await f.click('preview-selected'); await f.shown(5, 'repeat');
    await f.source(10); await f.shown(10, 'repeat'); await f.click('mark-end');
    await until(() => f.clips[1].endFrameExclusive === 11);
    assert.equal(f.clips[0].endFrameExclusive, 20);
    assert.equal(f.calls.filter(c => c[0] === 'TrimClip').length, 1);
});
test('failed exact frames cannot be marked and retry restores the controls', async t => {
    let failed = true; const f = await fixture(t, { fetchFrame: async () => ({ ok: !failed, blob: async () => ({}) }) });
    await until(() => !f.button('retry-frame').hidden);
    await f.click('mark-end'); assert.equal(f.calls.length, 0);
    failed = false; await f.click('retry-frame'); await f.shown(5);
    assert.equal(f.button('mark-end').disabled, false);
});
test('a late, obsolete frame response cannot enable marks for the wrong frame', async t => {
    const old = gate(); const f = await fixture(t, { fetchFrame: url => url.endsWith('/frames/0') ? old.promise : Promise.resolve({ ok: true, blob: async () => ({}) }) });
    await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    await f.source(0); await until(() => f.requests.some(r => r.url.endsWith('/frames/0')));
    await f.source(12); await f.shown(12);
    old.resolve({ ok: true, blob: async () => ({}) }); await delay(20);
    assert.equal(Number(f.element.dataset.frameIndex), 12);
    await f.click('mark-end'); await until(() => f.clips[0].endFrameExclusive === 13);
});
test('pending marks reject repeat clicks, seeking and playback until acknowledged', async t => {
    const hold = gate(); const f = await fixture(t, { invoke: args => args[0] === 'TrimClip' ? hold.promise : undefined });
    await f.shown(5); await f.click('mark-end'); await until(() => f.calls.length === 1);
    assert.equal(f.get('.cut-timeline').inert, true);
    await f.click('mark-end'); await f.click('next'); await f.click('play'); await f.source(12);
    assert.equal(f.calls.length, 1); assert.equal(f.element.dataset.playing, 'false');
    assert.equal(Number(f.element.dataset.frameIndex), 5);
    hold.resolve(); await until(() => f.clips[0].endFrameExclusive === 6); await f.shown(5);
    assert.equal(f.get('.cut-timeline').inert, false);
});
test('failed interop does not edit optimistically and can be retried', async t => {
    let failed = true; const f = await fixture(t, { invoke: args => { if (failed && args[0] === 'TrimClip') throw new Error('Disconnected'); } });
    await f.shown(5); await f.click('mark-end'); await until(() => !f.get('[data-mark-error]').hidden);
    assert.equal(f.clips[0].endFrameExclusive, 20);
    failed = false; await f.click('mark-end'); await until(() => f.clips[0].endFrameExclusive === 6);
});
test('a document change while selecting the visible clip prevents a stale trim request', async t => {
    const hold = gate(); const f = await fixture(t, { selected: 'b', invoke: args => args[0] === 'SelectClip' ? hold.promise : undefined });
    await f.shown(5); await f.click('mark-end'); await until(() => f.calls.length === 1);
    f.update([clip('a', { endFrameExclusive: 30 }), clip('b')]); hold.resolve();
    await until(() => !f.get('[data-mark-error]').hidden);
    assert.equal(f.calls.filter(c => c[0] === 'TrimClip').length, 0);
    assert.equal(f.clips[0].endFrameExclusive, 30);
});
test('take replacement invalidates source review and loads the new take', async t => {
    const f = await fixture(t); await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    await f.source(30); await f.shown(30);
    f.update([clip('a', { takeId: 'replacement', startFrame: 2 }), clip('b')], 'a', [], 1);
    assert.equal(f.button('mark-end').disabled, true); await f.shown(2, 'a');
    assert.equal(f.element.dataset.frameTakeId, 'replacement'); assert.equal(f.get('[data-source-review]').hidden, true);
});
test('removal, missing takes and an empty cut invalidate the marked target', async t => {
    const f = await fixture(t); await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    f.update(f.clips, 'a', ['take-a']); await f.click('mark-end'); assert.equal(f.calls.length, 0);
    assert.equal(f.button('preview-selected').disabled, true);
    f.update([clip('b')], 'b', []); await f.shown(5, 'b');
    assert.equal(f.get('[data-source-review]').hidden, true);
    f.update([], null); assert.equal(f.get('.cut-frame-tools').hidden, true);
    await f.click('mark-end'); assert.equal(f.calls.length, 0);
});
test('undo-like updates keep the reviewed source frame even outside the restored trim', async t => {
    const f = await fixture(t); await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    await f.source(30); await f.shown(30); await f.click('mark-end'); await until(() => f.clips[0].endFrameExclusive === 31);
    await f.shown(30); f.update([clip(), clip('b')]); await f.shown(30);
    assert.equal(f.clips[0].endFrameExclusive, 20); assert.equal(f.button('mark-end').disabled, false);
});
test('playing disables marks; pausing resolves the current video time to an exact frame', async t => {
    const f = await fixture(t); await f.shown(5); await f.click('play');
    await until(() => f.videos.some(v => !v.paused));
    assert.equal(f.button('mark-end').disabled, true);
    f.videos.find(v => !v.paused).currentTime = 12 / 24;
    await f.click('play'); await f.shown(12); await f.click('mark-end');
    await until(() => f.clips[0].endFrameExclusive === 13);
});
test('provisional trim-drag previews cannot be used as authoritative mark targets', async t => {
    const f = await fixture(t); await f.shown(5);
    f.callbacks.pause(); f.callbacks.preview(clip('a', { startFrame: 10 }), 10); await f.shown(10);
    await f.click('mark-end'); assert.equal(f.calls.length, 0);
    assert.equal(f.button('mark-end').disabled, true);
    f.callbacks.restore(); await f.shown(5);
    assert.equal(f.button('mark-end').disabled, false);
});
test('Play cut exits full-source review and clamps playback to the kept range', async t => {
    const f = await fixture(t); await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    await f.source(0); await f.shown(0); assert.equal(f.button('play').textContent, 'Play cut');
    await f.click('play'); await until(() => f.videos.some(v => !v.paused));
    assert.equal(f.videos.find(v => !v.paused).currentTime, 5 / 24);
    assert.equal(f.get('[data-source-review]').hidden, true);
});
test('disposal during selection never sends a later trim', async t => {
    const hold = gate(); const f = await fixture(t, { selected: 'b', invoke: args => args[0] === 'SelectClip' ? hold.promise : undefined });
    await f.shown(5); await f.click('mark-end'); await until(() => f.calls.length === 1);
    f.player.dispose(); hold.resolve(); await delay(20);
    assert.equal(f.calls.filter(c => c[0] === 'TrimClip').length, 0);
});

// Continuous source scrubbing must keep rendering frames instead of hiding the current
// preview and resetting the fetch debounce after every input event.
test('continuous source scrubbing delivers a preview before pointer release', async t => {
    const f = await fixture(t);
    await f.shown(5); await f.click('preview-selected'); await f.shown(5);
    const requestsBefore = f.requests.length;
    for (let i = 6; i <= 30; i++) { await f.source(i); await delay(15); }
    const during = { requests: f.requests.length - requestsBefore,
        frameReady: f.element.dataset.frameReady, hidden: f.get('.cut-paused-frame').hidden };
    console.log('REVIEW continuous drag state:', JSON.stringify(during));
    assert.ok(during.requests > 0, 'No frames were requested during 375 ms of continuous dragging');
    await f.shown(30);
});
