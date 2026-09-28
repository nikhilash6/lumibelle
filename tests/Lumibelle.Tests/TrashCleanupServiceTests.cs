using System.Threading.Channels;
using lumibelle.Models;
using lumibelle.Services.Assets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class TrashCleanupServiceTests
{
    [Fact]
    public async Task WorkerRunsOnStartupAndHourlyAndSurvivesFailedPass()
    {
        var store = new CleanupProbe(); var clock = new TimerClock();
        using var service = new ImageTrashCleanupService(store, clock, NullLogger<ImageTrashCleanupService>.Instance);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        Assert.Equal(1, await store.Calls.Reader.ReadAsync(ct));
        Assert.Equal(TimeSpan.FromHours(1), clock.Timer!.Period);
        clock.Timer.Tick();
        Assert.Equal(2, await store.Calls.Reader.ReadAsync(ct));
        clock.Timer.Tick();
        Assert.Equal(3, await store.Calls.Reader.ReadAsync(ct));
        await service.StopAsync(ct);
        Assert.True(clock.Timer.Disposed);
    }

    private sealed class TimerClock : TimeProvider
    {
        public ProbeTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Timer = new(callback, state, period);
    }
    private sealed class ProbeTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period => period;
        public bool Disposed { get; private set; }
        public void Tick() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class CleanupProbe : IImageTrashStore
    {
        private int _count;
        public Channel<int> Calls { get; } = Channel.CreateUnbounded<int>();
        public Task<IReadOnlyList<ImageTrashIssue>> CleanupExpiredAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _count); Calls.Writer.TryWrite(count);
            if (count == 2) throw new IOException("Mock temporary failure");
            return Task.FromResult<IReadOnlyList<ImageTrashIssue>>([]);
        }
        public Task<ImageTrashLibrary> ListTrashAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenTrashImageAsync(Guid projectId, Guid trashId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssetLibrary> RestoreImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImagePurgeResult> PurgeImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
