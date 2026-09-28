using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AtomicPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.AtomicPublication", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_root, "queue.json");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    [Fact]
    public async Task BriefWindowsReaderLockRetriesPublicationWithoutReplacingTheVisibleSnapshotEarly()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AtomicJsonFile.WriteAsync(PathName, new Payload(1), _ct);
        using var reader = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var save = AtomicJsonFile.WriteAsync(PathName, new Payload(2), _ct);
        await Task.Delay(50, _ct); Assert.False(save.IsCompleted);
        Assert.Equal(1, (await AtomicJsonFile.ReadAsync<Payload>(PathName, _ct))!.Value);
        reader.Dispose(); await save;
        Assert.Equal(2, (await AtomicJsonFile.ReadAsync<Payload>(PathName, _ct))!.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public async Task PersistentWindowsLockStillFailsAndKeepsTheOldDocument()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AtomicJsonFile.WriteAsync(PathName, new Payload(1), _ct);
        using var reader = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AtomicJsonFile.WriteAsync(PathName, new Payload(2), _ct));
        Assert.Equal(1, (await AtomicJsonFile.ReadAsync<Payload>(PathName, _ct))!.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public async Task CancellationDuringPublicationRetryKeepsThePreviousDocument()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AtomicJsonFile.WriteAsync(PathName, new Payload(1), _ct);
        using var reader = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var save = AtomicJsonFile.WriteAsync(PathName, new Payload(2), cancel.Token);
        await Task.Delay(40, _ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(1, (await AtomicJsonFile.ReadAsync<Payload>(PathName, _ct))!.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public async Task BriefWindowsDenialRetriesTheReadInsteadOfReportingAnUnreadableFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AtomicJsonFile.WriteAsync(PathName, new Payload(1), _ct);
        var holder = new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var read = AtomicJsonFile.ReadAsync<Payload>(PathName, _ct);
        await Task.Delay(50, _ct); Assert.False(read.IsCompleted);
        await holder.DisposeAsync();
        Assert.Equal(1, (await read)!.Value);
    }
    [Fact]
    public async Task PersistentWindowsDenialStillReportsAnUnreadableFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AtomicJsonFile.WriteAsync(PathName, new Payload(1), _ct);
        using var holder = new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => AtomicJsonFile.ReadAsync<Payload>(PathName, _ct));
        Assert.StartsWith("Couldn’t read the AI activity list.", error.Message);
    }
    [Theory]
    [InlineData("ai-jobs/7b1f/progress.json", "Couldn’t read a saved AI request.")]
    [InlineData("script.json", "Couldn’t read the script.")]
    [InlineData("script-history/7b1f.json", "Couldn’t read some saved project data.")]
    public async Task UnreadableDocumentsAreNamedForPeopleAndKeepTheirPath(string relative, string start)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "{ not json", _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => AtomicJsonFile.ReadAsync<Payload>(path, _ct));
        Assert.StartsWith(start, error.Message); Assert.DoesNotContain(".json", error.Message);
        Assert.Equal(path, error.Data["path"]);
    }
    [Fact]
    public async Task FlushingForPublicationKeepsFileAndFolderContentsIntact()
    {
        var media = Path.Combine(_root, "stage", "video.mp4.tmp"); var nested = Path.Combine(_root, "stage", "frames", "0001.png");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        await File.WriteAllBytesAsync(media, [1, 2, 3], _ct); await File.WriteAllBytesAsync(nested, [4, 5], _ct);
        DurableFile.Flush(media);
        DurableFile.FlushDirectory(Path.Combine(_root, "stage"));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(media, _ct));
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(nested, _ct));
    }
    public sealed record Payload(int Value);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
