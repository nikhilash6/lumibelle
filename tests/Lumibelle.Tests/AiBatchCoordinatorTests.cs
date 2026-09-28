using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests
{
    [Fact]
    public async Task LastCandidateAppendIsCompletedBeforeTheNextWaitingJob()
    {
        var request = Request(kind: AiJobKind.ImageEdit, target: new(Guid.NewGuid(), Guid.NewGuid()));
        request = request with { Batch = AiBatchDefinition.Create(request.Id, 1, 11) };
        var root = await Store.EnqueueAsync(request, _ct); var waiting = await Store.EnqueueAsync(Request(), _ct);
        var handler = new FakeHandler(); var queue = await Start(handler); var first = await handler.Next(_ct);
        var command = Guid.NewGuid();
        await queue.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct);
        first.Done.SetResult(AiJobOutcome.BatchCheckpoint(1));
        var second = await handler.Next(_ct);
        Assert.Equal(root.Id, second.Context.Job.Id); Assert.False(second.Context.Recovering);
        Assert.Equal(2, second.Context.Job.Batch!.Candidates.Count);
        second.Done.SetResult(AiJobOutcome.BatchCheckpoint(2));
        Assert.Equal(waiting.Id, (await handler.Next(_ct)).Context.Job.Id);
        await State(root.Id, j => j.State == AiJobState.Completed);
        handler.ValidateExtension = _ => throw new WorkspaceStoreException("The original input is now in Trash");
        Assert.Equal(2, (await queue.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct)).Candidate.Number);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => queue.ExtendBatchAsync(root.Id, Guid.NewGuid(), root.OriginTabId, _ct));
        Assert.Equal(2, (await Store.ReadAsync(_ct)).Jobs.Count);
    }
    [Fact]
    public async Task RecoveryCannotSubmitButCanReleaseNeverSubmittedCandidatesToNormalExecution()
    {
        var request = Request(kind: AiJobKind.ImageCreate, target: new(Guid.NewGuid(), Guid.NewGuid()));
        var root = await Store.EnqueueAsync(request with { Batch = AiBatchDefinition.Create(request.Id, 2) }, _ct);
        await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct); // process exits before submitting
        var handler = new FakeHandler(); await Start(handler); var recovery = await handler.Next(_ct);
        Assert.True(recovery.Context.Recovering);
        await Assert.ThrowsAsync<AiGenerationException>(() => recovery.Context.BeginRemoteAsync("candidate", "http://comfy.test"));
        recovery.Done.SetResult(AiJobOutcome.BatchCheckpoint(0));
        var normal = await handler.Next(_ct); Assert.False(normal.Context.Recovering); Assert.Equal(root.Id, normal.Context.Job.Id);
        normal.Done.SetResult(AiJobOutcome.BatchCheckpoint(2));
        await State(root.Id, j => j.State == AiJobState.Completed);
    }
    [Fact]
    public async Task ImpossibleCompletionCountBecomesAnInspectableFailure()
    {
        var request = Request(kind: AiJobKind.ImageCreate, target: new(Guid.NewGuid(), Guid.NewGuid()));
        var root = await Store.EnqueueAsync(request with { Batch = AiBatchDefinition.Create(request.Id, 1) }, _ct);
        var handler = new FakeHandler(); await Start(handler); var call = await handler.Next(_ct);
        call.Done.SetResult(AiJobOutcome.BatchCheckpoint(8));
        var failed = await State(root.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.Contains("accounting", failed.Error); Assert.False(failed.HoldsProvider);
    }
}
