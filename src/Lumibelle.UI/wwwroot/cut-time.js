export const duration = c => (c.endFrameExclusive - c.startFrame) / c.fps;
export const totalTime = clips => clips.reduce((n, c) => n + duration(c), 0);
export function locate(clips, seconds) {
    let offset = Math.max(0, seconds);
    for (let i = 0; i < clips.length; i++) {
        const d = duration(clips[i]);
        if (offset < d || i === clips.length - 1) return { index: i, offset: Math.min(offset, d) };
        offset -= d;
    }
    return { index: 0, offset: 0 };
}
export function atTime(clips, seconds) {
    if (!clips.length) return null;
    const { index, offset } = locate(clips, seconds), c = clips[index];
    return { clipId: c.id, takeId: c.takeId, frame: Math.min(c.endFrameExclusive - 1, c.startFrame + Math.floor(offset * c.fps + 1e-5)) };
}
export function timeOf(clips, position) {
    let time = 0;
    for (const c of clips) {
        if (c.id === position?.clipId) return time + (Math.max(c.startFrame, Math.min(c.endFrameExclusive - 1, position.frame)) - c.startFrame) / c.fps;
        time += duration(c);
    }
    return 0;
}
export function preservePosition(previous, next, position) {
    if (!next.length) return null;
    const same = next.find(c => c.id === position?.clipId);
    if (same) return { clipId: same.id, takeId: same.takeId, frame: same.takeId === position.takeId ? Math.max(same.startFrame, Math.min(same.endFrameExclusive - 1, position.frame)) : same.startFrame };
    const oldIndex = previous.findIndex(c => c.id === position?.clipId);
    const following = previous.slice(Math.max(0, oldIndex + 1)).map(c => next.find(n => n.id === c.id)).find(Boolean);
    const preceding = previous.slice(0, Math.max(0, oldIndex)).reverse().map(c => next.find(n => n.id === c.id)).find(Boolean);
    const c = following ?? preceding ?? next[0];
    return { clipId: c.id, takeId: c.takeId, frame: !following && preceding ? c.endFrameExclusive - 1 : c.startFrame };
}
export function trimEdge(clip, edge, frame) {
    const c = { ...clip };
    if (edge === 'start') c.startFrame = Math.max(0, Math.min(c.endFrameExclusive - 1, Math.round(frame)));
    else c.endFrameExclusive = Math.max(c.startFrame + 1, Math.min(c.frameCount, Math.round(frame)));
    return c;
}


// A mark refers to a displayed source frame, not an exclusive boundary.
// Reject crossed boundaries and no-ops rather than silently clamping to a different frame.
export function markFrame(clip, frame, edge) {
    if (!clip || !Number.isInteger(frame) || frame < 0 || frame >= clip.frameCount) return null;
    if (edge === 'start' && frame < clip.endFrameExclusive && frame !== clip.startFrame)
        return { ...clip, startFrame: frame };
    if (edge === 'end' && frame >= clip.startFrame && frame + 1 !== clip.endFrameExclusive)
        return { ...clip, endFrameExclusive: frame + 1 };
    return null;
}
