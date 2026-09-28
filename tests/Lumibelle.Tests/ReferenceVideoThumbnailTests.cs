using System.Diagnostics;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ReelThumbnailsAreLazyCachedAndDoNotChangeMediaOrRecipes()
    {
        var f = Fixture(); var tools = new ThumbnailTools(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var input = new MemoryStream([1, 2, 3, 4]);
        var media = await store.ImportAsync(f.Project.Id, input, "reel.mp4", new(), _ct);
        Assert.Equal(0, tools.Calls);
        var directory = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", media.Id.ToString());
        var metadata = await File.ReadAllBytesAsync(Path.Combine(directory, "media.json"), _ct);
        var readers = Enumerable.Range(0, 6).Select(async _ => {
            await using var thumb = await store.OpenThumbnailAsync(f.Project.Id, media.Id, new(), _ct);
            Assert.Equal("image/jpeg", thumb!.ContentType);
            using var decoded = await Image.LoadAsync<Rgb24>(thumb.Content, _ct); Assert.Equal(120, decoded.Width);
        });
        await Task.WhenAll(readers); Assert.Equal(1, tools.Calls);
        tools.Fail = true;
        var reopened = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var cached = await reopened.OpenThumbnailAsync(f.Project.Id, media.Id, new(), _ct);
        Assert.NotNull(cached); Assert.Equal(1, tools.Calls); // Also works after restart or unavailable FFmpeg.
        Assert.Equal(metadata, await File.ReadAllBytesAsync(Path.Combine(directory, "media.json"), _ct));
        await store.ValidateAsync(f.Project.Id, [new() { Media = media }], _ct);
        Assert.Null(await store.OpenThumbnailAsync(f.Project.Id, Guid.NewGuid(), new(), _ct));
    }

    [Fact]
    public async Task FailedOrCancelledThumbnailCanRetryAndDoesNotBlockOtherReaders()
    {
        var f = Fixture(); var tools = new ThumbnailTools { BlockFirst = true }; var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var input = new MemoryStream([1, 2, 3, 4]); var media = await store.ImportAsync(f.Project.Id, input, "reel.mp4", new(), _ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var first = store.OpenThumbnailAsync(f.Project.Id, media.Id, new(), cancel.Token);
        await tools.Started.Task.WaitAsync(_ct);
        var second = store.OpenThumbnailAsync(f.Project.Id, media.Id, new(), _ct);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await using (var result = await second) Assert.NotNull(result);
        var directory = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", media.Id.ToString());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp")); Assert.Equal(2, tools.Calls);
        File.Delete(Path.Combine(directory, "thumbnail-v1.jpg")); tools.Fail = true;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.OpenThumbnailAsync(f.Project.Id, media.Id, new(), _ct));
        Assert.False(File.Exists(Path.Combine(directory, "thumbnail-v1.jpg"))); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        tools.Fail = false; await using var retried = await store.OpenThumbnailAsync(f.Project.Id, media.Id, new(), _ct); Assert.NotNull(retried);
    }

    [Theory]
    [InlineData(960, 540, "1/1", 480, 270)]
    [InlineData(540, 960, "1/1", 180, 320)]
    [InlineData(320, 240, "2/1", 480, 180)]
    public async Task ActualReelThumbnailPreservesDisplayAspectAndBounds(int width, int height, string sar, int expectedWidth, int expectedHeight)
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "thumbnail-source.mp4");
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=red:size={width}x{height}:rate=24:duration=2", "-vf", "setsar=" + sar,
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-threads", "1", source }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync(_ct); await process.WaitForExitAsync(_ct); Assert.True(process.ExitCode == 0, await errors);
        var target = Path.Combine(_root, "thumb.jpg"); var tools = new ProductionMediaTools();
        await tools.PrepareVideoThumbnailAsync(source, target, new(), _ct);
        using var image = await Image.LoadAsync<Rgb24>(target, _ct);
        Assert.Equal((expectedWidth, expectedHeight), (image.Width, image.Height)); Assert.True(image[0, 0].R > 230);
        Assert.InRange(new FileInfo(target).Length, 1, 1024 * 1024);
    }

    private sealed class ThumbnailTools : IProductionMediaTools
    {
        public int Calls;
        public bool Fail, BlockFirst;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PrepareVideoThumbnailAsync(string source, string target, H3Settings settings, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls); await File.WriteAllBytesAsync(target, [1], ct);
            if (BlockFirst && call == 1) { Started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            if (Fail) throw new WorkspaceStoreException("Missing decoder.");
            using var image = new Image<Rgb24>(120, 80, new(255, 0, 0)); await image.SaveAsJpegAsync(target, ct);
        }
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(160, 96, 124, 24, false));
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
    }
}
