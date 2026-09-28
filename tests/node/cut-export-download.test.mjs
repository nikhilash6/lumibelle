import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const root = new URL('../../src/Lumibelle.UI/wwwroot/', import.meta.url);
async function fixture({ desktop = false, status = 200, nativeError = null } = {}) {
    const calls = [], downloads = [], requests = [];
    const document = {
        addEventListener() {}, removeEventListener() {},
        body: { append(anchor) { anchor.connected = true; } },
        createElement(tag) {
            assert.equal(tag, 'a');
            return { connected: false,
                click() { assert.equal(this.connected, true); downloads.push({ name: this.download, url: this.href }); },
                remove() { this.connected = false; }
            };
        }
    };
    const context = vm.createContext({ window: {}, document,
        fetch: async (url, options) => { requests.push({ url, options }); return { ok: status >= 200 && status < 300 }; },
        Blob: class { constructor() { throw new Error('Do not buffer media in a Blob.'); } },
        URL, setTimeout, clearTimeout
    });
    const bridge = new vm.SourceTextModule(await readFile(new URL('host-bridge.js', root), 'utf8'), { context });
    await bridge.link(() => { throw new Error('Unexpected bridge import'); }); await bridge.evaluate();
    if (desktop) bridge.namespace.mount({
        async invokeMethodAsync(...args) { calls.push(args); if (nativeError) throw nativeError; }
    });
    const shots = new vm.SourceTextModule(await readFile(new URL('shots-studio.js', root), 'utf8'), {
        context, importModuleDynamically: async specifier => {
            assert.equal(specifier, './host-bridge.js'); return bridge;
        }
    });
    await shots.link(() => { throw new Error('Unexpected static import'); }); await shots.evaluate();
    return { shots: context.window.lumibelleShots, bridge: bridge.namespace, calls, downloads, requests };
}

const resource = '/downloads/projects/11111111-1111-1111-1111-111111111111/cuts/22222222-2222-2222-2222-222222222222.mp4';
test('desktop download awaits SaveResource with the original logical URL and no fetch/blob', async () => {
    const f = await fixture({ desktop: true });
    await f.shots.downloadResource('cut.mp4', resource);
    assert.deepEqual(f.calls, [['SaveResource', 'cut.mp4', resource]]);
    assert.equal(f.requests.length, 0); assert.equal(f.downloads.length, 0);
});
test('native save failures propagate to the caller', async () => {
    const f = await fixture({ desktop: true, nativeError: new Error('Disk full') });
    await assert.rejects(f.shots.downloadResource('cut.mp4', resource), /Disk full/);
    assert.equal(f.calls.length, 1);
});
test('browser checks HEAD then starts a connected native browser download without reading the body', async () => {
    const f = await fixture();
    await f.shots.downloadResource('cut.mp4', resource);
    assert.equal(f.requests.length, 1);
    assert.equal(f.requests[0].options.method, 'HEAD'); assert.equal(f.requests[0].options.cache, 'no-store');
    assert.deepEqual(f.downloads, [{ name: 'cut.mp4', url: resource }]);
});
test('expired artifacts show an actionable error without starting a download', async () => {
    const f = await fixture({ status: 404 });
    await assert.rejects(f.shots.downloadResource('cut.mp4', resource), /unavailable or expired/);
    assert.equal(f.downloads.length, 0);
});
test('repeat downloads reuse the same artifact URL', async () => {
    const f = await fixture();
    await f.shots.downloadResource('cut.mp4', resource); await f.shots.downloadResource('cut.mp4', resource);
    assert.deepEqual(f.downloads.map(d => d.url), [resource, resource]);
});
test('unmounted desktop bridge cannot silently accept a save', async () => {
    const f = await fixture({ desktop: true }); f.bridge.unmount();
    assert.equal(f.bridge.isDesktop(), false);
    assert.throws(() => f.bridge.saveResource('cut.mp4', resource), /bridge is unavailable/);
});
