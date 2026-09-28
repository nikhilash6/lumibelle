using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class AiQueuePriorityTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.PriorityTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FileAiJobStore _store;
    private readonly AiJobCoordinator _queue;
    public AiQueuePriorityTests()
    {
        _store = new(_root, TimeProvider.System);
        _queue = new(_store, new FakeAiSettingsStore(), [], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
    }
    private static AiJobSubmission Request(AiBackend backend = AiBackend.ComfyUI) =>
        AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant, backend, new(Guid.NewGuid()),
            "Priority QA", "Captured request", Guid.NewGuid(), new { prompt = "Original input", seed = 42 });
    private Task<AiJobHeader> Add(AiBackend backend = AiBackend.ComfyUI) => _store.EnqueueAsync(Request(backend), _ct);
    private async Task<AiJobHeader> Read(Guid id) => (await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == id);
    private async Task<Guid[]> Order(AiBackend backend = AiBackend.ComfyUI) =>
        AiQueueOrder.Waiting((await _store.ReadAsync(_ct)).Jobs, backend).Select(j => j.Id).ToArray();

    [Fact]
    public async Task LegacyHeadersOmitPriorityAndRoundTripAsNormal()
    {
        var job = await Add();
        var legacy = JsonSerializer.Serialize(job, AtomicJsonFile.Options);
        Assert.DoesNotContain("\"priority\"", legacy);
        Assert.False(JsonSerializer.Deserialize<AiJobHeader>(legacy, AtomicJsonFile.Options)!.Priority);
        var flagged = JsonSerializer.Serialize(job with { Priority = true }, AtomicJsonFile.Options);
        Assert.True(JsonSerializer.Deserialize<AiJobHeader>(flagged, AtomicJsonFile.Options)!.Priority);
    }

    [Theory]
    [InlineData(AiBackend.ComfyUI)]
    [InlineData(AiBackend.OpenRouter)]
    [InlineData(AiBackend.Codex)]
    public async Task EachProviderClaimsPriorityBeforeNormal(AiBackend backend)
    {
        var normal = await Add(backend); var urgent = await Add(backend);
        await _queue.SetPriorityAsync(urgent.Id, true, _ct);
        var claimed = await _store.ClaimNextAsync(backend, 1, _ct);
        Assert.Equal(urgent.Id, claimed!.Id); Assert.True(claimed.Priority);
        Assert.Equal(AiJobState.Waiting, (await Read(normal.Id)).State);
        Assert.Null(await _store.ClaimNextAsync(backend, 1, _ct));
    }

    [Fact]
    public async Task PriorityIsStableWithinSavedOrderNotFlaggingOrder()
    {
        var a = await Add(); var b = await Add(); var c = await Add(); var d = await Add();
        await _queue.SetPriorityAsync(d.Id, true, _ct); await _queue.SetPriorityAsync(b.Id, true, _ct);
        Assert.Equal(new[] { b.Id, d.Id, a.Id, c.Id }, await Order());
        Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id }, (await _store.ReadAsync(_ct)).Jobs.Select(j => j.Id).ToArray());
        await _queue.SetPriorityAsync(b.Id, false, _ct);
        Assert.Equal(new[] { d.Id, a.Id, b.Id, c.Id }, await Order());
    }

    [Theory]
    [InlineData(AiBackend.ComfyUI)]
    [InlineData(AiBackend.OpenRouter)]
    [InlineData(AiBackend.Codex)]
    public async Task RemovingPriorityRestoresNormalQueueOrder(AiBackend backend)
    {
        var a = await Add(backend); var b = await Add(backend);
        await _queue.SetPriorityAsync(b.Id, true, _ct); await _queue.SetPriorityAsync(b.Id, false, _ct);
        Assert.Equal(new[] { a.Id, b.Id }, await Order(backend));
        Assert.Equal(a.Id, (await _store.ClaimNextAsync(backend, 1, _ct))!.Id);
    }

    [Fact]
    public async Task PriorityChangesOnlySchedulingMetadataAndSurvivesReload()
    {
        var request = Request(); var job = await _store.EnqueueAsync(request, _ct);
        await _store.WriteArtifactAsync(job.Id, AiJobArtifact.Result, new { raw = "Saved response" }, _ct);
        var directory = _store.DirectoryFor(job.Id);
        var snapshot = await File.ReadAllBytesAsync(Path.Combine(directory, "request.json"), _ct);
        var result = await File.ReadAllBytesAsync(Path.Combine(directory, "result.json"), _ct);
        await _queue.SetPriorityAsync(job.Id, true, _ct);
        var reload = new FileAiJobStore(_root, TimeProvider.System);
        var current = (await reload.ReadAsync(_ct)).Jobs.Single();
        Assert.True(current.Priority); Assert.Equal(job.RequestFingerprint, current.RequestFingerprint);
        Assert.Equal(job.Version + 1, current.Version); Assert.Equal(job.CreatedUtc, current.CreatedUtc);
        Assert.Equal(job.Target, current.Target); Assert.Equal(job.ComfyControl, current.ComfyControl);
        Assert.Equal(snapshot, await File.ReadAllBytesAsync(Path.Combine(directory, "request.json"), _ct));
        Assert.Equal(result, await File.ReadAllBytesAsync(Path.Combine(directory, "result.json"), _ct));
        Assert.True((await reload.EnqueueAsync(request, _ct)).Priority); // Lost enqueue acknowledgement is still idempotent.
        Assert.True(JsonElement.DeepEquals(await reload.ReadSnapshotAsync(job.Id, _ct), request.Snapshot));
    }

    [Fact]
    public async Task DuplicateExplicitValuesDoNotCreateExtraRevisions()
    {
        var job = await Add(); await _queue.SetPriorityAsync(job.Id, true, _ct);
        var before = await _store.ReadAsync(_ct);
        await _queue.SetPriorityAsync(job.Id, true, _ct);
        Assert.Equal(before.Revision, (await _store.ReadAsync(_ct)).Revision);
        Assert.Equal(before.Jobs.Single().Version, (await Read(job.Id)).Version);
        await _queue.SetPriorityAsync(job.Id, false, _ct); before = await _store.ReadAsync(_ct);
        await _queue.SetPriorityAsync(job.Id, false, _ct);
        Assert.Equal(before.Revision, (await _store.ReadAsync(_ct)).Revision);
    }

    [Theory]
    [InlineData(AiJobState.Running)]
    [InlineData(AiJobState.Completed)]
    [InlineData(AiJobState.Cancelled)]
    [InlineData(AiJobState.NeedsAttention)]
    public async Task StalePriorityCommandCannotModifyNonwaitingWork(AiJobState state)
    {
        var job = await Add();
        var before = await _store.UpdateAsync(job.Id, j => j with { State = state,
            LeaseId = state == AiJobState.Running ? Guid.NewGuid() : null,
            StartedUtc = state == AiJobState.Running ? DateTimeOffset.UtcNow : null }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => _queue.SetPriorityAsync(job.Id, true, _ct));
        Assert.Equal(before, await Read(job.Id));
    }

    [Fact]
    public async Task LocallyCancelledWaitingRequestCannotBePrioritizedOrRevived()
    {
        var job = await Add(); await _store.UpdateAsync(job.Id, j => j with { CancelRequested = true }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => _queue.SetPriorityAsync(job.Id, true, _ct));
        Assert.Empty(await Order()); Assert.True((await Read(job.Id)).CancelRequested);
    }

    [Theory]
    [InlineData(AiBackend.ComfyUI)]
    [InlineData(AiBackend.OpenRouter)]
    [InlineData(AiBackend.Codex)]
    public async Task PriorityNeverBypassesPause(AiBackend backend)
    {
        var a = await Add(backend); var b = await Add(backend);
        await _store.SetPausedAsync(backend, true, _ct); await _queue.SetPriorityAsync(b.Id, true, _ct);
        Assert.Null(await _store.ClaimNextAsync(backend, 1, _ct));
        Assert.Contains(backend, (await _store.ReadAsync(_ct)).Paused);
        await _store.SetPausedAsync(backend, false, _ct);
        Assert.Equal(b.Id, (await _store.ClaimNextAsync(backend, 1, _ct))!.Id);
        Assert.Equal(AiJobState.Waiting, (await Read(a.Id)).State);
    }

    [Fact]
    public async Task PriorityNeverBypassesAnUnconfirmedComfyReceipt()
    {
        var blocked = await Add(); var urgent = await Add();
        await _store.UpdateAsync(blocked.Id, j => j with { State = AiJobState.NeedsAttention,
            RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, _ct);
        await _queue.SetPriorityAsync(urgent.Id, true, _ct);
        Assert.Null(await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        Assert.True((await Read(blocked.Id)).RemoteUnconfirmed);
        await _store.UpdateAsync(blocked.Id, j => j with { RemoteUnconfirmed = false }, _ct);
        Assert.Equal(urgent.Id, (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
    }

    [Fact]
    public async Task ProvidersRemainIndependentAndConcurrencyIsRespected()
    {
        var local = await Add(); var running = await Add(AiBackend.OpenRouter);
        await _store.ClaimNextAsync(AiBackend.OpenRouter, 2, _ct);
        var normal = await Add(AiBackend.OpenRouter); var urgent = await Add(AiBackend.OpenRouter);
        await _queue.SetPriorityAsync(urgent.Id, true, _ct);
        Assert.Equal(local.Id, (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
        Assert.Equal(urgent.Id, (await _store.ClaimNextAsync(AiBackend.OpenRouter, 2, _ct))!.Id);
        Assert.Null(await _store.ClaimNextAsync(AiBackend.OpenRouter, 2, _ct));
        Assert.Equal(AiJobState.Running, (await Read(running.Id)).State);
        Assert.Equal(AiJobState.Waiting, (await Read(normal.Id)).State);
    }

    [Fact]
    public async Task RunNextPromotesOneRequestAheadOfOtherPriorityWork()
    {
        var a = await Add(); var b = await Add(); var remote = await Add(AiBackend.OpenRouter); var c = await Add();
        await _queue.SetPriorityAsync(b.Id, true, _ct);
        await _store.MoveAsync(c.Id, 0, first: true, ct: _ct);
        Assert.Equal(new[] { c.Id, b.Id, a.Id }, await Order());
        Assert.True((await Read(c.Id)).Priority); Assert.False((await Read(a.Id)).Priority);
        Assert.Equal(c.Version + 1, (await Read(c.Id)).Version);
        Assert.Equal(remote.Id, (await _store.ReadAsync(_ct)).Jobs[2].Id);
        var revision = (await _store.ReadAsync(_ct)).Revision;
        await _store.MoveAsync(c.Id, 0, first: true, ct: _ct);
        Assert.Equal(revision, (await _store.ReadAsync(_ct)).Revision);
    }

    [Fact]
    public async Task ArrowsOnlyReorderWithinTheirBandWithoutChangingFlags()
    {
        var a = await Add(); var b = await Add(); var c = await Add(); var d = await Add(); var e = await Add();
        await _queue.SetPriorityAsync(b.Id, true, _ct); await _queue.SetPriorityAsync(d.Id, true, _ct);
        await _store.MoveAsync(a.Id, int.MaxValue, ct: _ct);
        Assert.Equal(new[] { b.Id, d.Id, c.Id, e.Id, a.Id }, await Order());
        await _store.MoveAsync(d.Id, int.MinValue, ct: _ct);
        Assert.Equal(new[] { d.Id, b.Id, c.Id, e.Id, a.Id }, await Order());
        Assert.Equal(new[] { b.Id, d.Id }.Order().ToArray(), (await _store.ReadAsync(_ct)).Jobs.Where(j => j.Priority).Select(j => j.Id).Order().ToArray());
        Assert.False((await Read(a.Id)).Priority); Assert.False((await Read(c.Id)).Priority);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    public async Task MovingSingleWaitingRequestIsANoop(int offset)
    {
        var job = await Add(); var revision = (await _store.ReadAsync(_ct)).Revision;
        await _store.MoveAsync(job.Id, offset, ct: _ct);
        Assert.Equal(revision, (await _store.ReadAsync(_ct)).Revision);
        Assert.False((await Read(job.Id)).Priority);
    }

    [Fact]
    public async Task RunNextDoesNotModifyOrInterruptClaimedWork()
    {
        var job = await Add(); var claimed = await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => _store.MoveAsync(job.Id, 0, first: true, ct: _ct));
        Assert.Equal(claimed, await Read(job.Id));
    }

    [Fact]
    public async Task DisplayOrderMatchesDispatchButPreservesOtherSlots()
    {
        var a = await Add(); var router = await Add(AiBackend.OpenRouter); var b = await Add(); var c = await Add();
        var active = a with { State = AiJobState.Running, Priority = true };
        var cancelled = b with { Id = Guid.NewGuid(), CancelRequested = true, Priority = true };
        var urgent = c with { Priority = true };
        AiJobHeader[] raw = [active, b, router, cancelled, urgent];
        var display = AiQueueOrder.ForDisplay(raw);
        Assert.Same(active, display[0]); Assert.Same(router, display[2]); Assert.Same(cancelled, display[3]);
        Assert.Equal(new[] { c.Id, b.Id }, display.Where(AiQueueOrder.IsWaiting).Where(j => j.Backend == AiBackend.ComfyUI).Select(j => j.Id).ToArray());
        Assert.Same(b, raw[1]); Assert.Same(urgent, raw[4]);
        Assert.Equal(AiQueueOrder.Waiting(raw, AiBackend.ComfyUI).Select(j => j.Id).ToArray(),
            AiQueueOrder.Waiting(display, AiBackend.ComfyUI).Select(j => j.Id).ToArray());
    }

    [Fact]
    public async Task PrioritySurvivesRetainedPauseAndResumeOfTheSameRequest()
    {
        var normal = await Add(); var job = await Add(); await _queue.SetPriorityAsync(job.Id, true, _ct);
        await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct); await _store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        Assert.True((await Read(job.Id)).Priority); Assert.True((await Read(job.Id)).ComfyControl!.PauseRequested);
        // Simulate the existing pause reconciliation having confirmed the remote stop.
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Waiting, LeaseId = null,
            StartedUtc = null, Recovery = AiJobRecovery.CheckStatus }, _ct);
        Assert.Null(await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        await _store.SetPausedAsync(AiBackend.ComfyUI, false, _ct);
        var resumed = await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        Assert.Equal(job.Id, resumed!.Id); Assert.True(resumed.Priority); Assert.False(resumed.ComfyControl!.PauseRequested);
        Assert.Equal(AiJobState.Waiting, (await Read(normal.Id)).State);
    }

    [Fact]
    public async Task RetainedNormalRequestYieldsToPriorityAfterPause()
    {
        var generation = await Add(); await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        await _store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        var retained = await _store.UpdateAsync(generation.Id,
            j => ComfyQueuePolicy.Suspended(j, false, true, DateTimeOffset.UtcNow), _ct);
        var text = await Add(); await _queue.SetPriorityAsync(text.Id, true, _ct);
        Assert.False(ComfyQueuePolicy.ShouldReconcile(retained, [], DateTimeOffset.UtcNow));
        await _store.SetPausedAsync(AiBackend.ComfyUI, false, _ct);
        Assert.Equal(text.Id, (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
        Assert.Equal(AiJobState.Waiting, (await Read(generation.Id)).State);
        Assert.Equal(retained.ComfyControl, (await Read(generation.Id)).ComfyControl);
        Assert.Equal(generation.RequestFingerprint, (await Read(generation.Id)).RequestFingerprint);
    }

    [Fact]
    public async Task RetryOutputKeepsPriorityAndImmutableInput()
    {
        var normal = await Add(); var job = await Add(); await _queue.SetPriorityAsync(job.Id, true, _ct);
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.RetryOutput }, _ct);
        var queued = await _store.RequeueAsync(job.Id, _ct);
        Assert.True(queued.Priority); Assert.Equal(job.RequestFingerprint, queued.RequestFingerprint);
        Assert.Equal(new[] { job.Id, normal.Id }, await Order());
    }

    [Fact]
    public async Task PriorityIsNotInheritedByNewBatchContinuations()
    {
        var id = Guid.NewGuid();
        var request = AiJobSubmission.Create(id, AiJobKind.ImageCreate, AiBackend.ComfyUI, new(Guid.NewGuid(), Guid.NewGuid()),
            "QA", "Batch", Guid.NewGuid(), new { prompt = "Exact input" }) with { Batch = AiBatchDefinition.Create(id, 1, 10) };
        var root = await _store.EnqueueAsync(request, _ct); await _queue.SetPriorityAsync(root.Id, true, _ct);
        var appended = await _store.ExtendBatchAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        Assert.True(appended.Job.Priority); Assert.False(appended.Continuation); // Same request keeps its flag.
        await _store.UpdateAsync(root.Id, j => j with { State = AiJobState.Completed }, _ct);
        var child = await _store.ExtendBatchAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        Assert.True(child.Continuation); Assert.False(child.Job.Priority); Assert.True((await Read(root.Id)).Priority);
        Assert.Equal(root.Batch!.Candidates[0], (await Read(root.Id)).Batch!.Candidates[0]);
        Assert.Equal(3, child.Candidate.Number);
    }

    [Fact]
    public async Task ClaimAndPriorityCommandAreSerializedWithoutRepeatingARequest()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var job = await Add();
            async Task<bool> Promote()
            {
                try { await _queue.SetPriorityAsync(job.Id, true, _ct); return true; }
                catch (WorkspaceStoreException) { return false; }
            }
            var claim = _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct); var promote = Promote();
            var claimed = await claim; var promoted = await promote;
            Assert.Equal(job.Id, claimed!.Id); Assert.Equal(promoted, (await Read(job.Id)).Priority);
            Assert.Equal(AiJobState.Running, (await Read(job.Id)).State);
            Assert.Null(await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
            await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed }, _ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        _queue.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return ValueTask.CompletedTask;
    }
}
