using lumibelle.Services;
using lumibelle.Services.Story;
using Microsoft.Data.Sqlite;

namespace Lumibelle.Tests;

public sealed class SqliteMediaIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Lumibelle.MediaIndexTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;
    private ApplicationPaths Paths => new(root);
    private string Manifest => Path.Combine(root, "assets.json");
    private int reads;
    public sealed record Row(string Value);

    private async Task<IReadOnlyDictionary<string, Row>> Read(CancellationToken token)
    {
        Interlocked.Increment(ref reads);
        return await AtomicJsonFile.ReadAsync<Dictionary<string, Row>>(Manifest, token) ?? [];
    }
    private Task Write(string key, string value) => AtomicJsonFile.WriteAsync(Manifest, new Dictionary<string, Row> { [key] = new(value) }, ct);

    [Fact]
    public async Task ConcurrentColdLookupsBuildOnceAndFreshInstanceReusesPersistentRows()
    {
        await Write("image", "original");
        var first = new SqliteMediaIndex(Paths);
        var second = new SqliteMediaIndex(Paths);
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => (i % 2 == 0 ? first : second).FindAsync(Manifest, "image", Read, ct)));
        Assert.All(results, row => Assert.Equal("original", row?.Value));
        Assert.Equal(1, reads);
        Assert.Null(await new SqliteMediaIndex(Paths).FindAsync(Manifest, "absent", Read, ct));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ChangedAndMissingManifestsNeverServeOldRows()
    {
        await Write("old", "old");
        var index = new SqliteMediaIndex(Paths);
        Assert.NotNull(await index.FindAsync(Manifest, "old", Read, ct));
        await Write("new", "new version");
        Assert.Null(await index.FindAsync(Manifest, "old", Read, ct));
        Assert.Equal("new version", (await index.FindAsync(Manifest, "new", Read, ct))?.Value);
        File.Delete(Manifest);
        Assert.Null(await index.FindAsync(Manifest, "new", Read, ct));
        await Write("restored", "restored version");
        Assert.NotNull(await index.FindAsync(Manifest, "restored", Read, ct));
        Assert.Null(await index.FindAsync(Manifest, "new", Read, ct));
    }

    [Fact]
    public async Task FreshProcessVerifiesContentEvenWhenRestoredFileHasSameLengthAndTimestamp()
    {
        await Write("image", "before");
        var index = new SqliteMediaIndex(Paths);
        Assert.Equal("before", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
        var timestamp = File.GetLastWriteTimeUtc(Manifest);
        var length = new FileInfo(Manifest).Length;
        // In-place restore preserves creation time too.
        await File.WriteAllTextAsync(Manifest, (await File.ReadAllTextAsync(Manifest, ct)).Replace("before", "after!"), ct);
        File.SetLastWriteTimeUtc(Manifest, timestamp);
        Assert.Equal(length, new FileInfo(Manifest).Length);
        Assert.Equal("after!", (await new SqliteMediaIndex(Paths).FindAsync(Manifest, "image", Read, ct))?.Value);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task BrokenSourceStillFailsAndIsNeverReplacedByCachedContent()
    {
        await Write("image", "original");
        var index = new SqliteMediaIndex(Paths);
        await index.FindAsync(Manifest, "image", Read, ct);
        await File.WriteAllTextAsync(Manifest, "not JSON", ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => index.FindAsync(Manifest, "image", Read, ct));
        Assert.Equal("not JSON", await File.ReadAllTextAsync(Manifest, ct));
    }

    [Fact]
    public async Task DamagedDatabaseAndPayloadRebuildFromSource()
    {
        await Write("image", "original");
        var index = new SqliteMediaIndex(Paths);
        await index.FindAsync(Manifest, "image", Read, ct);
        await File.WriteAllTextAsync(index.DatabasePath, "not a database", ct);
        Assert.Equal("original", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
        using (var connection = new SqliteConnection($"Data Source={index.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE entries SET payload = 'broken JSON'";
            command.ExecuteNonQuery();
        }
        Assert.Equal("original", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task UnwritableCacheFallsBackAndCancelledRebuildCanRetry()
    {
        await Write("image", "original");
        await File.WriteAllTextAsync(Path.Combine(root, "cache"), "blocks the cache directory", ct);
        var index = new SqliteMediaIndex(Paths);
        Assert.Equal("original", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
        File.Delete(Path.Combine(root, "cache"));
        index = new SqliteMediaIndex(Paths);
        using var cancellation = new CancellationTokenSource();
        async Task<IReadOnlyDictionary<string, Row>> Cancel(CancellationToken token)
        {
            var rows = await Read(token);
            cancellation.Cancel();
            return rows;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.FindAsync(Manifest, "image", Cancel, cancellation.Token));
        Assert.Equal("original", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
    }

    [Fact]
    public async Task ConcurrentManifestChangeCannotPublishMixedVersionRows()
    {
        await Write("image", "before");
        var index = new SqliteMediaIndex(Paths);
        var changed = false;
        async Task<IReadOnlyDictionary<string, Row>> RacingRead(CancellationToken token)
        {
            var rows = await Read(token);
            if (!changed) { changed = true; await Write("image", "after version"); }
            return rows;
        }
        Assert.Equal("after version", (await index.FindAsync(Manifest, "image", RacingRead, ct))?.Value);
        Assert.Equal("after version", (await index.FindAsync(Manifest, "image", Read, ct))?.Value);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
