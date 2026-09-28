using System.Diagnostics;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Mp4OnlyTakePublishesExtractsExactFramesAndSurvivesTrash(bool removeArchive)
    {
        var f = Fixture(); var s = PresetSnapshot("standard", removeArchive) with { ProjectId = f.Project.Id, Width = 32, Height = 32 };
        var doc = await f.Shots.SaveAsync(f.Project.Id, [s.Shot], 0, ct: _ct);
        var run = Guid.NewGuid(); var directory = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run, _ct), "candidate-1");
        Directory.CreateDirectory(directory);
        // Frame five alone is red. Encoding an actual changing timeline catches seek/index errors.
        for (var i = 0; i < s.FrameCount; i++)
        { using var image = new Image<Rgb24>(32, 32, i == 5 ? new(255, 0, 0) : new(0, 0, 255)); await image.SaveAsPngAsync(Path.Combine(directory, $"input-{i:D3}.png"), _ct); }
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-framerate", "24", "-i", Path.Combine(directory, "input-%03d.png"),
            "-f", "lavfi", "-i", "anullsrc=r=32000:cl=stereo", "-frames:v", s.FrameCount.ToString(), "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv444p", "-c:a", "aac", "-shortest", Path.Combine(directory, "video.mp4") }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!) { var error = process.StandardError.ReadToEndAsync(_ct); await process.WaitForExitAsync(_ct); Assert.True(process.ExitCode == 0, await error); }
        foreach (var file in Directory.GetFiles(directory, "input-*.png")) File.Delete(file);
        var frames = removeArchive ? await Lumibelle.Testing.MockFrameArchive.WriteAsync(directory, s.FrameCount, 32, 32, _ct) : [];
        var take = new ShotTake { ShotId = s.Shot.Id, RunId = run, Candidate = 1, Snapshot = s, Width = 32, Height = 32, Frames = frames, Bytes = new FileInfo(Path.Combine(directory, "video.mp4")).Length + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) };
        doc = await f.Shots.PublishTakeAsync(f.Project.Id, take, directory, _ct); take = doc.Takes.Single();
        if (removeArchive)
        {
            var original = ShotCopy.Of(take); var receipt = doc.TakePublications.Single();
            doc.Shots.Single().SelectedTakeId = take.Id;
            doc = await f.Shots.SaveAsync(f.Project.Id, doc.Shots, doc.Revision, ct: _ct);
            doc = await f.Shots.RemoveFrameArchivesAsync(f.Project.Id, [take.Id], doc.Revision, _ct); take = doc.Takes.Single();
            Assert.NotNull(take.FrameArchiveRemoval!.CompletedUtc); Assert.Equal(receipt, doc.TakePublications.Single());
            Assert.Equal(take.Id, doc.Shots.Single().SelectedTakeId);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original.Snapshot), System.Text.Json.JsonSerializer.Serialize(take.Snapshot));
            var folder = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "shots", "takes", take.Directory);
            Assert.Empty(Directory.GetFiles(folder, "*.webp")); Assert.True(File.Exists(Path.Combine(folder, "video.mp4")));
            Assert.Equal(new FileInfo(Path.Combine(folder, "video.mp4")).Length, take.Bytes);
            doc = await f.Shots.PublishTakeAsync(f.Project.Id, original, directory, _ct);
            Assert.False(doc.Takes.Single().HasLosslessFrames); // A delayed completion cannot bring archives back.
            doc.Shots.Single().SelectedTakeId = null;
            doc = await f.Shots.SaveAsync(f.Project.Id, doc.Shots, doc.Revision, ct: _ct);
        }
        Assert.Empty(take.Frames); Assert.Equal(s.FrameCount, CutClip.From(s.Shot, take).EndFrameExclusive);
        await using (var frame = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, 5, ct: _ct))
        { using var image = await Image.LoadAsync<Rgb24>(frame!.Content, _ct); Assert.True(image[0, 0].R > 240); Assert.True(image[0, 0].B < 15); }
        await using (var frame = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, 6, ct: _ct))
        { using var image = await Image.LoadAsync<Rgb24>(frame!.Content, _ct); Assert.True(image[0, 0].B > 240); }
        var copied = await f.Assets.SaveDerivedImageAsync(f.Project.Id, FrameCopy(take), 0, _ct);
        Assert.False(copied.Library.Assets.Single().Images.Single().Source!.Frame!.Lossless);
        doc = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, doc.Revision, _ct);
        doc = await f.Shots.RestoreAsync(f.Project.Id, [doc.Trash.Single().Id], doc.Revision, _ct);
        Assert.False(doc.Takes.Single().HasLosslessFrames);
        var bad = take with { FrameArchiveRemoval = null, Snapshot = s with { OutputPolicy = new(true), Shot = s.Shot with { SaveLosslessFrames = true } } };
        Assert.Throws<WorkspaceStoreException>(() => FileShotStore.Validate(new() { ProjectId = f.Project.Id, Takes = [bad] }, f.Project.Id));
    }
    [Fact]
    public async Task ExtractionDeduplicatesAndCancelsOnlyAfterLastReaderLeaves()
    {
        var media = new BlockingFrameTools(); var reader = new TakeFrameReader(media);
        var take = new ShotTake { Snapshot = PresetSnapshot("standard"), Width = 32, Height = 32 };
        using var first = new CancellationTokenSource(); using var second = new CancellationTokenSource();
        var a = reader.OpenAsync(_root, take, 5, first.Token); var b = reader.OpenAsync(_root, take, 5, second.Token);
        await media.Started.Task.WaitAsync(_ct); Assert.Equal(1, media.Calls);
        first.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); Assert.False(media.Cancelled.Task.IsCompleted);
        media.Complete.TrySetResult([1, 2, 3]); await using (var stream = await b) Assert.Equal(3, stream.Length);
        var other = new BlockingFrameTools(); var r = new TakeFrameReader(other);
        using var cancel = new CancellationTokenSource(); var pending = r.OpenAsync(_root, take, 0, cancel.Token);
        await other.Started.Task.WaitAsync(_ct); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await other.Cancelled.Task.WaitAsync(_ct);
    }
    private sealed class BlockingFrameTools : IProductionMediaTools
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> ExtractFrameAsync(string path, int index, int width, int height, H3Settings settings, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); try { return await Complete.Task.WaitAsync(ct); } catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; } }
        public Task<double> AudioDurationAsync(string path, H3Settings s, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings s, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings s, CancellationToken ct) => throw new NotSupportedException();
    }
}
