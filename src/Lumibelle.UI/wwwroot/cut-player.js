import { key as positionKey, read as readPosition, write as writePosition } from './workspace-position.js';
import { duration, totalTime, locate, atTime, timeOf, preservePosition, markFrame } from './cut-time.js';
import { attach as attachTimeline } from './cut-timeline.js';
export { locate } from './cut-time.js';

// Timing, preloads and transient archive previews stay in the browser.
export function attach(root, dotnet, project) {
    const videos = [...root.querySelectorAll('video')], controllers = new Map(), prepared = new Map();
    const still = root.querySelector('.cut-paused-frame'), position = root.querySelector('[data-position]');
    const message = root.querySelector('[data-message]'), error = root.querySelector('[data-error]');
    const buffering = root.querySelector('.cut-buffering'), play = root.querySelector('[data-action=play]');
    const retryFrame = root.querySelector('[data-action=retry-frame]'), events = new AbortController();
    const frameTools = root.querySelector('.cut-frame-tools'), trimTarget = root.querySelector('[data-trim-target]');
    const markStart = root.querySelector('[data-action=mark-start]'), markEnd = root.querySelector('[data-action=mark-end]');
    const previewSelected = root.querySelector('[data-action=preview-selected]'), returnCut = root.querySelector('[data-action=return-cut]');
    const sourceReview = root.querySelector('[data-source-review]'), sourceFrame = root.querySelector('[data-action=source-frame]');
    const sourcePosition = root.querySelector('[data-source-position]'), markError = root.querySelector('[data-mark-error]');
    const timelineRoot = root.querySelector('.cut-timeline');
    const placeKey = positionKey(project, 'cut'), savedPosition = readPosition(placeKey);
    let restored = false, positionTimer, lastPosition;
    const savePosition = () => {
        if (!restored || disposed) return;
        const value = { position: atTime(clips, time), timeline: timeline.view() }, json = JSON.stringify(value);
        if (json !== lastPosition) { lastPosition = json; writePosition(placeKey, value); }
    };
    let clips = [], selected = null, version = 0, missing = [], active = videos[0], index = 0, time = 0, previewSelection = 0;
    let desired = false, disposed = false, transitioning = false, epoch = 0, tickId, volume = 1, muted = false, finished = false, failedTarget = null;
    let frameEpoch = 0, frameAbort, frameTimer, frameUrl, previewing = false, pendingFrame = null;
    let shownFrame = null, sourcePreview = null, marking = false;
    const sourceClip = () => clips.find(c => c.id === sourcePreview?.clipId && c.takeId === sourcePreview.takeId);
    const markedClip = () => !desired && !transitioning && !previewing && !marking && !disposed && root.dataset.frameReady === 'true' && shownFrame &&
        clips.find(c => c.id === shownFrame.clipId && c.takeId === shownFrame.takeId && !missing.includes(c.takeId));
    function trimControls() {
        const c = markedClip(), source = sourceClip(), chosen = clips.find(c => c.id === selected);
        frameTools.hidden = clips.length === 0;
        markStart.disabled = !c || !markFrame(c, shownFrame.frame, 'start');
        markEnd.disabled = !c || !markFrame(c, shownFrame.frame, 'end');
        markStart.title = c && shownFrame.frame >= c.endFrameExclusive ? 'Move the end later before setting the start here.' : 'Keep the visible frame as the first frame.';
        markEnd.title = c && shownFrame.frame < c.startFrame ? 'Move the start earlier before setting the end here.' : 'Keep the visible frame as the last frame (inclusive).';
        trimTarget.textContent = c ? `Trim visible clip · ${c.shotTitle} · frame ${shownFrame.frame + 1}` :
            marking ? 'Applying trim…' : desired ? 'Pause on a frame to set trim points.' : 'Load a preview frame to set trim points.';
        previewSelected.disabled = !chosen || missing.includes(chosen.takeId) || marking || previewing;
        returnCut.hidden = !source; returnCut.disabled = marking;
        sourceReview.hidden = !source;
        sourceFrame.disabled = !source || missing.includes(source.takeId) || marking;
        if (source) {
            sourceFrame.max = String(source.frameCount); sourceFrame.value = String(sourcePreview.frame + 1);
            sourcePosition.textContent = `${sourcePreview.frame + 1} / ${source.frameCount}`;
            sourceFrame.setAttribute('aria-valuetext', `Frame ${sourcePreview.frame + 1} of ${source.frameCount}`);
        }
        root.querySelector('[data-action=previous]').disabled = !clips.length || marking || !!source && (sourceFrame.disabled || sourcePreview.frame === 0);
        root.querySelector('[data-action=next]').disabled = !clips.length || marking || !!source && (sourceFrame.disabled || sourcePreview.frame === source.frameCount - 1);
        timelineRoot.inert = marking;
    }
    const before = i => clips.slice(0, i).reduce((n, c) => n + duration(c), 0);
    const total = () => totalTime(clips);
    const format = n => `${Math.floor(n / 60)}:${Math.floor(n % 60).toString().padStart(2, '0')}.${Math.floor(n * 1000 % 1000).toString().padStart(3, '0')}`;
    const on = (el, name, fn) => el.addEventListener(name, fn, { signal: events.signal });
    const canPlay = () => clips.length > 0 && missing.length === 0;
    const status = () => {
        play.textContent = desired ? 'Pause' : sourcePreview ? 'Play cut' : finished ? 'Replay' : failedTarget ? 'Resume' : 'Play';
        play.disabled = !canPlay() || marking; root.querySelector('[data-action=beginning]').disabled = !canPlay() || marking;
        root.dataset.playing = String(desired); trimControls();
    };
    const showPosition = follow => {
        const pos = atTime(clips, time);
        position.textContent = `${format(time)} / ${format(total())}`;
        timeline.position(time, pos?.clipId, follow); root.dataset.position = String(time);
    };
    const cancelFrame = () => { shownFrame = null; root.dataset.frameReady = 'false'; trimControls(); frameEpoch++; clearTimeout(frameTimer); frameAbort?.abort(); still.hidden = true; retryFrame.hidden = true; };
    function showFrame(c, frame, trim = false) {
        if (!c) return;
        // A new source position replaces the pending target, but it must not cancel an
        // already scheduled fetch and restart the 45 ms debounce on every input event.
        pendingFrame = { c, frame, trim };
        if (sourcePreview) {
            if (frameTimer) return;
            frameEpoch++;
            frameAbort?.abort();
            root.dataset.frameReady = 'false';
            trimControls();
        } else {
            cancelFrame();
        }
        videos.forEach(v => { v.pause(); v.hidden = true; });
        if (missing.includes(c.takeId)) { buffering.hidden = true; error.textContent = 'This take is unavailable. Restore it, replace it, or remove the clip.'; error.hidden = false; return; }
        buffering.hidden = false; buffering.textContent = 'Loading lossless frame…'; error.hidden = true;
        frameTimer = setTimeout(async () => {
            frameTimer = null;
            const token = frameEpoch; const target = pendingFrame;
            const controller = new AbortController(); frameAbort = controller; let url;
            try {
                const r = await fetch(`/media/projects/${project}/takes/${target.c.takeId}/frames/${target.frame}`, { signal: controller.signal });
                if (!r.ok) throw new Error('Frame unavailable');
                url = URL.createObjectURL(await r.blob());
                const image = new Image(); image.src = url; await image.decode();
                if (disposed || token !== frameEpoch || desired) { URL.revokeObjectURL(url); return; }
                if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = url;
                still.src = url; still.alt = `Lossless frame ${target.frame + 1} · ${target.c.shotTitle}`; still.hidden = false;
                root.dataset.frameIndex = target.frame; root.dataset.frameTakeId = target.c.takeId; root.dataset.clipId = target.c.id; buffering.hidden = true;
                shownFrame = { clipId: target.c.id, takeId: target.c.takeId, frame: target.frame }; root.dataset.frameReady = 'true'; trimControls();
                message.textContent = finished ? 'End of cut' : `${target.trim ? 'Trim preview · ' : ''}${target.c.shotTitle} · frame ${target.frame + 1} / ${target.c.frameCount}`;
            } catch {
                if (url) URL.revokeObjectURL(url);
                if (!disposed && token === frameEpoch) { buffering.hidden = true; error.textContent = 'The exact archived frame could not load.'; error.hidden = false; retryFrame.hidden = false; }
            } finally {
                if (!disposed && token === frameEpoch && pendingFrame !== target)
                    showFrame(pendingFrame.c, pendingFrame.frame, pendingFrame.trim);
            }
        }, 45);
    }
    function pause(exact = true) {
        if (desired && !transitioning && clips[index]) time = Math.min(before(index) + duration(clips[index]), before(index) + Math.max(0, active.currentTime - clips[index].startFrame / clips[index].fps));
        desired = false; epoch++; transitioning = false; videos.forEach(v => v.pause()); status(); showPosition(false);
        if (exact && clips.length && !previewing && !sourcePreview) { const p = atTime(clips, time); showFrame(clips.find(c => c.id === p.clipId), p.frame); }
    }
    function seek(seconds) {
        pause(false); sourcePreview = null; previewing = false; finished = false; failedTarget = null;
        const p = atTime(clips, seconds); time = p ? timeOf(clips, p) : 0;
        showPosition(false); status(); if (p) showFrame(clips.find(c => c.id === p.clipId), p.frame);
    }
    function previewSource(c, frame) {
        if (!c || missing.includes(c.takeId) || !Number.isInteger(frame)) return;
        pause(false); previewing = false; finished = false; failedTarget = null; markError.hidden = true;
        sourcePreview = { clipId: c.id, takeId: c.takeId, frame: Math.max(0, Math.min(c.frameCount - 1, frame)) };
        // The cut playhead stays inside its retained range; the source cursor can go beyond it.
        time = timeOf(clips, sourcePreview); showPosition(false); status(); showFrame(c, sourcePreview.frame, true);
    }
    async function mark(edge) {
        const c = markedClip(), frame = shownFrame?.frame, edit = markFrame(c, frame, edge), editVersion = version;
        if (!edit) return;
        marking = true; markError.hidden = true;
        sourcePreview = { clipId: c.id, takeId: c.takeId, frame };
        time = timeOf(clips, sourcePreview); showPosition(false); status();
        try {
            // Playback intentionally does not follow editor selection. Target the visible clip,
            // including its specific occurrence when the same take is used more than once.
            if (selected !== c.id) await dotnet.invokeMethodAsync('SelectClip', c.id);
            if (disposed) return;
            if (version !== editVersion || !clips.some(n => n.id === c.id && n.takeId === c.takeId))
                throw new Error('The cut changed. Preview the frame again before setting a trim point.');
            await dotnet.invokeMethodAsync('TrimClip', c.id, edit.startFrame, edit.endFrameExclusive, editVersion);
        } catch {
            if (!disposed) { markError.textContent = 'The trim could not be applied. Check the connection and cut, then try again.'; markError.hidden = false; }
        } finally { marking = false; if (!disposed) status(); }
    }
    function step(delta) {
        if (marking) return;
        const source = sourceClip();
        if (source) { previewSource(source, sourcePreview.frame + delta); return; }
        const p = atTime(clips, time); if (!p) return;
        const i = clips.findIndex(c => c.id === p.clipId), c = clips[i];
        if (delta < 0 && p.frame === c.startFrame && i > 0) seek(before(i) - .5 / clips[i - 1].fps);
        else if (delta > 0 && p.frame === c.endFrameExclusive - 1 && i + 1 < clips.length) seek(before(i + 1));
        else seek(timeOf(clips, { ...p, frame: p.frame + delta }));
    }
    const wait = (v, names, condition, signal) => new Promise((resolve, reject) => {
        let timer;
        const cleanup = () => { clearTimeout(timer); names.forEach(n => v.removeEventListener(n, check)); v.removeEventListener('error', failed); signal.removeEventListener('abort', aborted); };
        const check = () => { if (condition()) { cleanup(); resolve(); } };
        const failed = () => { cleanup(); reject(new Error('The take could not be loaded. Retry playback, or restore missing media.')); };
        const aborted = () => { cleanup(); reject(new DOMException('Cancelled', 'AbortError')); };
        names.forEach(n => v.addEventListener(n, check)); v.addEventListener('error', failed); signal.addEventListener('abort', aborted);
        timer = setTimeout(failed, 30000); if (signal.aborted) aborted(); else if (v.error) failed(); else check();
    });
    const prepare = (v, i, offset = 0) => {
        const old = prepared.get(v); if (old?.index === i && old.offset === offset) return old.promise;
        controllers.get(v)?.abort(); const controller = new AbortController(); controllers.set(v, controller);
        v.pause(); v.muted = true; const clip = clips[i];
        const promise = (async () => {
            v.src = `/media/projects/${project}/takes/${clip.takeId}`; v.load();
            await wait(v, ['loadedmetadata'], () => v.readyState >= 1, controller.signal);
            v.currentTime = Math.min((clip.endFrameExclusive - .5) / clip.fps, clip.startFrame / clip.fps + offset);
            await wait(v, ['seeked', 'loadeddata', 'canplay'], () => !v.seeking && v.readyState >= 2, controller.signal); return true;
        })().catch(e => { if (e.name === 'AbortError') return false; throw e; });
        promise.catch(() => {}); prepared.set(v, { index: i, offset, promise }); return promise;
    };
    const preload = () => { if (index + 1 < clips.length && !disposed) prepare(videos.find(v => v !== active), index + 1).catch(() => {}); };
    const fail = text => { desired = false; transitioning = false; videos.forEach(v => v.pause()); buffering.hidden = true; error.textContent = text; error.hidden = false; status(); };
    async function activate(i, offset = 0) {
        const token = ++epoch; cancelFrame(); previewing = false;
        transitioning = true; finished = false; error.hidden = true; buffering.textContent = 'Buffering…'; buffering.hidden = false;
        videos.forEach(v => v.pause()); const next = videos.find(v => v !== active);
        try {
            const ready = await prepare(next, i, offset); if (!ready || disposed || token !== epoch) return;
            active.hidden = true; active.setAttribute('aria-hidden', 'true'); active.muted = true;
            active = next; index = i; active.hidden = false; active.removeAttribute('aria-hidden'); active.volume = volume; active.muted = muted;
            time = before(i) + offset; buffering.hidden = true; transitioning = false; failedTarget = null;
            root.dataset.clipId = clips[i].id; message.textContent = `${i + 1} / ${clips.length} · ${clips[i].shotTitle}`;
            if (desired) await active.play(); if (disposed || token !== epoch) return;
            preload(); status(); showPosition(true);
        } catch (e) {
            if (!disposed && token === epoch) { failedTarget = { index: i, offset }; fail(e.name === 'NotAllowedError' ? 'Press Resume to allow playback with sound.' : `Clip ${i + 1}: ${e.message}`); }
        }
    }
    function advance() {
        if (transitioning || !desired || disposed) return;
        active.pause(); active.muted = true;
        if (index + 1 < clips.length) void activate(index + 1);
        else { desired = false; finished = true; time = total(); status(); showPosition(true); const c = clips.at(-1); showFrame(c, c.endFrameExclusive - 1); }
    }
    function resume(beginning = false) {
        if (!canPlay() || marking) return;
        sourcePreview = null;
        desired = true; previewing = false; status(); error.hidden = true;
        if (beginning || finished) { prepared.clear(); void activate(0); }
        else if (failedTarget) { const t = failedTarget; prepared.clear(); void activate(t.index, t.offset); }
        else { const p = locate(clips, time); prepared.clear(); void activate(p.index, p.offset); }
    }
    const timeline = attachTimeline(timelineRoot, project, {
        pause: () => { pause(false); sourcePreview = null; status(); }, seek, step,
        select: id => { selected = id; dotnet.invokeMethodAsync('SelectClip', id).catch(() => {}); },
        preview: (c, frame) => { previewing = true; showFrame(c, frame, true); },
        restore: originalTime => { previewing = false; seek(originalTime ?? time); },
        trim: (c, v) => dotnet.invokeMethodAsync('TrimClip', c.id, c.startFrame, c.endFrameExclusive, v),
        order: (id, beforeId, v) => dotnet.invokeMethodAsync('ReorderClip', id, beforeId, v)
    });
    const tick = () => {
        if (disposed) return;
        if (desired && !transitioning && active.readyState >= 1 && clips[index]) {
            time = before(index) + Math.max(0, Math.min(duration(clips[index]), active.currentTime - clips[index].startFrame / clips[index].fps));
            showPosition(true); if (active.currentTime >= clips[index].endFrameExclusive / clips[index].fps) advance();
        }
        tickId = requestAnimationFrame(tick);
    };
    videos.forEach(v => {
        on(v, 'ended', () => { if (v === active) advance(); });
        on(v, 'waiting', () => { if (v === active && desired) buffering.hidden = false; });
        on(v, 'playing', () => { if (v === active && !transitioning) buffering.hidden = true; });
        on(v, 'error', () => { if (v === active && desired && !transitioning) { failedTarget = { index, offset: Math.max(0, time - before(index)) }; fail('This take is unavailable. Retry playback or restore the take.'); } });
    });
    on(previewSelected, 'click', () => {
        if (marking || previewing) return;
        const c = clips.find(c => c.id === selected); if (!c) return;
        const frame = shownFrame?.clipId === c?.id && shownFrame?.takeId === c?.takeId ? shownFrame.frame : c?.startFrame;
        previewSource(c, frame);
    });
    on(sourceFrame, 'input', e => { if (!marking) previewSource(sourceClip(), Number(e.target.value) - 1); });
    on(returnCut, 'click', () => { if (!marking) seek(time); });
    on(markStart, 'click', () => void mark('start')); on(markEnd, 'click', () => void mark('end'));
    on(play, 'click', () => desired ? pause() : resume());
    on(root.querySelector('[data-action=beginning]'), 'click', () => resume(true));
    on(root.querySelector('[data-action=previous]'), 'click', () => step(-1)); on(root.querySelector('[data-action=next]'), 'click', () => step(1));
    on(document, 'visibilitychange', () => { if (document.hidden) { pause(); message.textContent = 'Paused while this tab is hidden'; } });
    on(root.querySelector('[data-action=volume]'), 'input', e => { volume = Number(e.target.value); active.volume = volume; });
    on(root.querySelector('[data-action=mute]'), 'click', e => { muted = !muted; active.muted = muted; e.target.textContent = muted ? 'Unmute' : 'Mute'; e.target.setAttribute('aria-pressed', String(muted)); });
    on(root.querySelector('[data-action=fullscreen]'), 'click', () => (document.fullscreenElement ? document.exitFullscreen() : root.requestFullscreen()).catch(() => {}));
    on(retryFrame, 'click', () => { if (pendingFrame) showFrame(pendingFrame.c, pendingFrame.frame, pendingFrame.trim); });
    on(root, 'keydown', e => { if (e.code === 'Space' && !e.target.closest('button, input, select, [role=slider]')) { e.preventDefault(); desired ? pause() : resume(); } });
    positionTimer = setInterval(savePosition, 500);
    on(window, 'pagehide', savePosition);
    tickId = requestAnimationFrame(tick);
    return {
        pause: () => pause(), seek,
        update(next, selectedId, nextVersion, unavailable, nextPreviewSelection = 0) {
            const changed = JSON.stringify(clips) !== JSON.stringify(next) || JSON.stringify(missing) !== JSON.stringify(unavailable); let p;
            const reveal = previewSelection !== nextPreviewSelection; previewSelection = nextPreviewSelection;
            if (changed || reveal) { pause(false); p = preservePosition(clips, next, atTime(clips, time)); controllers.forEach(c => c.abort()); prepared.clear(); }
            clips = next.map(c => ({ ...c })); selected = selectedId; version = nextVersion; missing = unavailable;
            timeline.update(clips, selected, version, missing); status();
            const restoring = !restored; restored = true;
            if (restoring && savedPosition.position) {
                const candidate = clips.find(c => c.id === savedPosition.position.clipId && c.takeId === savedPosition.position.takeId);
                if (candidate && Number.isFinite(savedPosition.position.frame)) p = { ...savedPosition.position, frame: Math.min(candidate.endFrameExclusive - 1, Math.max(candidate.startFrame, savedPosition.position.frame)) };
            }
            if (reveal) { const c = clips.find(c => c.id === selected); if (c) p = { clipId: c.id, takeId: c.takeId, frame: c.startFrame }; }
            if (reveal || !sourceClip() || missing.includes(sourceClip().takeId)) sourcePreview = null;
            if (changed || reveal) {
                failedTarget = null; finished = false; previewing = false; time = p ? timeOf(clips, p) : 0; showPosition(false);
                if (sourceClip()) previewSource(sourceClip(), sourcePreview.frame);
                else if (p) showFrame(clips.find(c => c.id === p.clipId), p.frame);
                else { cancelFrame(); videos.forEach(v => v.hidden = true); buffering.hidden = true; message.textContent = 'Add takes to begin your cut'; }
            }
            status();
            if (restoring) {
                timeline.restoreView(savedPosition.timeline);
                lastPosition = JSON.stringify({ position: atTime(clips, time), timeline: timeline.view() });
            }
        },
        dispose() {
            savePosition(); clearInterval(positionTimer); disposed = true; epoch++; desired = false; events.abort(); timeline.dispose(); cancelAnimationFrame(tickId); cancelFrame();
            if (frameUrl) URL.revokeObjectURL(frameUrl);
            controllers.forEach(c => c.abort()); videos.forEach(v => { v.pause(); v.removeAttribute('src'); v.load(); }); controllers.clear(); prepared.clear();
        }
    };
}
