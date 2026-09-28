using System.Diagnostics;
using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact] public async Task ReelFrameWorkersAreBoundedAndCancelledExtractionsCanRetry()
    {
        var f = Fixture(); var tools = new FrameWorkerTools(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        List<ReelFrameIdentity> frames = [];
        for (var i = 0; i < 3; i++) {
            await using var input = new MemoryStream([1, 2, 3]);
            var media = await store.ImportAsync(f.Project.Id, input, "clip.mp4", new(), _ct);
            var catalog = await store.FrameCatalogAsync(f.Project.Id, media, new(), _ct);
            frames.Add(new(media.Id, catalog.Source, 12, catalog.Timestamps[12]));
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var first = store.PrepareFramesAsync(f.Project.Id, [frames[0]], new(), cancel.Token);
        await tools.OneStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        var second = store.PrepareFramesAsync(f.Project.Id, [frames[1]], new(), _ct);
        var third = store.PrepareFramesAsync(f.Project.Id, [frames[2]], new(), _ct);
        try {
            await tools.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Equal(2, tools.Started);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await tools.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Equal(2, tools.MaximumActive);
        }
        finally { tools.Release.TrySetResult(); }
        await Task.WhenAll(second, third);
        var mediaRoot = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos");
        Assert.Empty(Directory.GetDirectories(mediaRoot, "extract-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(mediaRoot, frames[0].MediaId.ToString("D")), "frame-*.png"));
        await store.PrepareFramesAsync(f.Project.Id, [frames[0]], new(), _ct); // Retry a cancelled decode.
        var decodes = tools.Started;
        await store.PrepareFramesAsync(f.Project.Id, frames, new(), _ct);
        Assert.Equal(decodes, tools.Started); Assert.Equal(0, tools.Active);
    }
    private sealed class FrameWorkerTools : IProductionMediaTools
    {
        public int Active, Started, MaximumActive;
        public TaskCompletionSource OneStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThreeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(32, 32, 72, 24, false));
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<double>> ReelFrameTimesAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult<IReadOnlyList<double>>(Enumerable.Range(0, 72).Select(i => i / 24d).ToArray());
        public async Task ExtractReelFramesAsync(string path, IReadOnlyList<int> indices, string directory, int maxEdge, H3Settings settings, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref Active); var started = Interlocked.Increment(ref Started);
            lock (Release) MaximumActive = Math.Max(MaximumActive, active);
            if (started == 1) OneStarted.SetResult(); if (started == 2) TwoStarted.SetResult(); if (started == 3) ThreeStarted.SetResult();
            try {
                Directory.CreateDirectory(directory); await Release.Task.WaitAsync(ct);
                for (var i = 0; i < indices.Count; i++) { using var image = new Image<Rgb24>(32, 32); await image.SaveAsPngAsync(Path.Combine(directory, $"{i:D6}.png"), ct); }
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private async Task FfmpegFixture(params string[] arguments)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var errors = process.StandardError.ReadToEndAsync(_ct); await process.WaitForExitAsync(_ct); Assert.True(process.ExitCode == 0, await errors);
    }
    [Theory] [InlineData(30)] [InlineData(240)]
    public async Task ReelFrameMediaExtractsExactIndicesAndOnlyPreparesPicturesAndAudio(int fps)
    {
        var f = Fixture(); var tools = new ProductionMediaTools(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        var source = Path.Combine(_root, "reference.mp4");
        await FfmpegFixture("-f", "lavfi", "-i", $"testsrc2=size=96x64:rate={fps}:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=32000:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        await using var input = File.OpenRead(source); var media = await store.ImportAsync(f.Project.Id, input, "clip.mp4", new(), _ct);
        var catalog = await store.FrameCatalogAsync(f.Project.Id, media, new(), _ct);
        Assert.Equal(fps * 3, catalog.Timestamps.Count); Assert.False(catalog.Lossless);
        var frame = new ReelFrameIdentity(media.Id, catalog.Source, fps * 2, catalog.Timestamps[fps * 2]);
        await using (var png = await store.OpenFrameAsync(f.Project.Id, frame, new(), _ct))
        { using var image = await Image.LoadAsync<Rgb24>(png.Content, _ct); Assert.Equal(96, image.Width); Assert.Equal(64, image.Height); }
        var picks = await store.SuggestFramesAsync(f.Project.Id, media, 3, new(), _ct); Assert.InRange(picks.Frames.Count, 1, 3);
        var again = await store.SuggestFramesAsync(f.Project.Id, media, 3, new() { Ffmpeg = "not-installed", Ffprobe = "not-installed" }, _ct);
        Assert.Equal(picks.Frames.Select(f => f.Frame), again.Frames.Select(f => f.Frame));
        var shot = Ready() with { Duration = 1, Videos = [new() { Media = media, Visuals = ReelVisuals.Keyframes, UseSoundtrack = true, AudioExcerpt = new(.5, 2),
            Keyframes = new() { Frames = [new() { Frame = frame, Crop = new() { Width = .5 } }] } }] };
        var folder = Path.Combine(_root, "prepared-keyframes"); Directory.CreateDirectory(folder); List<PreparedVideoInput> prepared = [];
        await store.PrepareAsync(f.Project.Id, shot, folder, prepared, new(), _ct);
        Assert.Equal(new[] { VideoInputKind.Image, VideoInputKind.Audio }, prepared.Select(i => i.EffectiveKind)); Assert.Empty(Directory.GetFiles(folder, "*.mp4"));
        using (var image = await Image.LoadAsync(Path.Combine(folder, prepared[0].FileName), _ct)) Assert.Equal(48, image.Width);
        Assert.InRange(await tools.AudioDurationAsync(Path.Combine(folder, prepared[1].FileName), new(), _ct), 1.99, 2.01);
        var capture = await ProductionInputs.CaptureAsync(f.Project.Id, shot, f.Assets, _ct, store);
        Assert.Equal(capture.Single().Identity.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, prepared[0].FileName), _ct))));
        shot.Videos[0].Visuals = ReelVisuals.None; prepared.Clear();
        await store.PrepareAsync(f.Project.Id, shot, folder, prepared, new(), _ct); Assert.Equal(VideoInputKind.Audio, prepared.Single().EffectiveKind);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.OpenFrameAsync(f.Project.Id, frame with { Seconds = .01 }, new(), _ct));
    }
    [Fact] public async Task ReelFrameMediaPreservesVariableTimestampsAndDisplayGeometry()
    {
        var f = Fixture(); var tools = new ProductionMediaTools(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        var source = Path.Combine(_root, "variable.mp4");
        await FfmpegFixture("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=30:duration=3", "-vf", "select='not(eq(mod(n,5),0))',setsar=2", "-fps_mode", "vfr", "-c:v", "libx264", "-threads", "1", source);
        await using var input = File.OpenRead(source); var media = await store.ImportAsync(f.Project.Id, input, "clip.mp4", new(), _ct);
        var originalTimes = await tools.ReelFrameTimesAsync(source, new(), _ct);
        Assert.True(originalTimes[0] > .03);
        var relativeTimes = originalTimes.Select(t => t - originalTimes[0]).ToArray();
        var mediaFolder = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", media.Id.ToString("D"));
        // Existing v1 caches and saved selections retain their relative timestamp identities.
        await AtomicJsonFile.WriteAsync(Path.Combine(mediaFolder, "frame-times-v1.json"), new ReelFrameCatalog(media.Sha256, false, relativeTimes), _ct);
        var catalog = await store.FrameCatalogAsync(f.Project.Id, media, new(), _ct);
        Assert.Equal(originalTimes[0], catalog.PlaybackOrigin);
        Assert.Equal(relativeTimes, catalog.Timestamps);
        var cached = await store.FrameCatalogAsync(f.Project.Id, media, new() { Ffprobe = "not-installed" }, _ct);
        Assert.Equal(catalog.PlaybackOrigin, cached.PlaybackOrigin);
        Assert.Equal(catalog.Timestamps, cached.Timestamps);
        var intervals = catalog.Timestamps.Zip(catalog.Timestamps.Skip(1), (a, b) => Math.Round(b - a, 3)).Distinct().ToArray(); Assert.True(intervals.Length > 1);
        var frame = new ReelFrameIdentity(media.Id, catalog.Source, 10, catalog.Timestamps[10]);
        await using var png = await store.OpenFrameAsync(f.Project.Id, frame, new(), _ct); using var image = await Image.LoadAsync(png.Content, _ct);
        Assert.Equal(192, image.Width); Assert.Equal(64, image.Height);
        var rotated = Path.Combine(_root, "rotated.mp4"); await FfmpegFixture("-display_rotation", "90", "-i", source, "-c", "copy", rotated);
        await tools.ExtractReelFramesAsync(rotated, [10], Path.Combine(_root, "rotated-png"), 0, new(), _ct);
        using var turned = await Image.LoadAsync(Path.Combine(_root, "rotated-png", "000000.png"), _ct);
        Assert.Equal(64, turned.Width); Assert.Equal(192, turned.Height); // Autorotation and SAR are honored together.
    }
    [Fact] public async Task ReelFrameArchivePublicationIsImmutableRetryableAndIndependentOfTheRun()
    {
        var f = Fixture(); var tools = new ReferenceMediaFake(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var input = new MemoryStream([1, 2, 3, 4]); var media = await store.ImportAsync(f.Project.Id, input, "clip.mp4", new(), _ct);
        var directory = Path.Combine(_root, "archive-source"); Directory.CreateDirectory(directory);
        var frames = await MockFrameArchive.WriteAsync(directory, media.Frames, media.Width, media.Height, _ct);
        var take = new ShotTake { Snapshot = ReferenceSnapshot(Ready() with { Duration = 5 }) with { FrameCount = media.Frames, OutputPolicy = new(true) }, Width = media.Width, Height = media.Height, Frames = frames };
        await store.PublishArchiveAsync(f.Project.Id, media, take, directory, _ct);
        await store.PublishArchiveAsync(f.Project.Id, media, take, directory, _ct); // Idempotent save retry.
        var archiveFolder = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", media.Id.ToString("D"), "lossless");
        var segment = Path.Combine(archiveFolder, frames[0].FileName);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(segment, _ct));
        File.Delete(segment); // An incomplete local transfer is repairable without inference.
        await store.PublishArchiveAsync(f.Project.Id, media, take, directory, _ct);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(segment, _ct)));
        var sourceSegment = Path.Combine(directory, frames[0].FileName);
        using (var altered = await Image.LoadAsync<Rgba32>(sourceSegment, _ct)) {
            altered.Frames.RootFrame[1, 1] = new(10, 200, 30);
            await altered.SaveAsync(sourceSegment, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossless }, _ct);
        }
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishArchiveAsync(f.Project.Id, media, take, directory, _ct));
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(segment, _ct)));
        var catalog = await store.FrameCatalogAsync(f.Project.Id, media, new(), _ct); Assert.True(catalog.Lossless);
        Directory.Delete(directory, true);
        await using (var png = await store.OpenFrameAsync(f.Project.Id, new(media.Id, catalog.Source, 37, 37 / 24d), new(), _ct))
        { using var image = await Image.LoadAsync<Rgb24>(png.Content, _ct); Assert.Equal(new Rgb24(37, 0, 200), image[0, 0]); }
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Media = media };
        library = await f.Assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        var picks = new ReelKeyframeSet { Frames = [new() { Frame = new(media.Id, catalog.Source, 37, 37 / 24d), Notes = "Keep the doorway" }] };
        library = await f.Assets.SaveKeyframesAsync(f.Project.Id, reel, picks, library.Revision, _ct);
        var saved = library.Reels.Single(); var attachment = new ShotVideoBinding { Media = media, Visuals = ReelVisuals.Keyframes, Keyframes = ShotCopy.Of(saved.Keyframes) };
        var changed = ShotCopy.Of(picks); changed.Frames[0].Notes = "New guidance";
        library = await f.Assets.SaveKeyframesAsync(f.Project.Id, saved, changed, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveKeyframesAsync(f.Project.Id, saved, picks, library.Revision, _ct));
        library = await f.Assets.TrashReelAsync(f.Project.Id, reel.Id, library.Revision, _ct);
        Assert.Equal("Keep the doorway", attachment.Keyframes!.Frames[0].Notes);
        await using var retained = await store.OpenFrameAsync(f.Project.Id, attachment.Keyframes.Frames[0].Frame, new(), _ct); Assert.True(retained.Content.Length > 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SuggestFramesAsync(f.Project.Id, media, 3, new(), cancelled.Token));
    }
}
