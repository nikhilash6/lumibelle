const excerptListeners = new WeakMap();
export function stop(player) { if (!player?.pause) return; player.pause(); excerptListeners.get(player)?.(); excerptListeners.delete(player); }
export function position(player) { if (!player || !Number.isFinite(player.currentTime)) throw new Error('Recording not loaded.'); return player.currentTime; }
export async function playExcerpt(player, start, end) {
    stop(player);
    if (!player || !Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end - start < 1 || end - start > 15) throw new Error('Invalid excerpt.');
    if (player.readyState === 0) await new Promise((resolve, reject) => {
        const clean = () => { clearTimeout(timer); player.removeEventListener('loadedmetadata', ready); player.removeEventListener('error', failed); };
        const ready = () => { clean(); resolve(); }, failed = () => { clean(); reject(new Error('Audio unavailable.')); };
        const timer = setTimeout(failed, 15000); player.addEventListener('loadedmetadata', ready); player.addEventListener('error', failed); player.load();
    });
    if (end > player.duration + .1) throw new Error('Excerpt outside recording.');
    const onTime = () => { if (player.currentTime >= end) stop(player); };
    player.addEventListener('timeupdate', onTime);
    excerptListeners.set(player, () => player.removeEventListener('timeupdate', onTime));
    player.currentTime = start;
    try { await player.play(); } catch (e) { stop(player); throw e; }
}
