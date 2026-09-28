using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiJobStoreTests
{
    private AiJobSubmission ImageBatch(int count = 2)
    {
        var request = Request(AiJobKind.ImageEdit, target: new(Guid.NewGuid(), Guid.NewGuid()));
        return request with { Batch = AiBatchDefinition.Create(request.Id, count, 42) };
    }
    private AiJobSubmission ImageBatchFor(AiJobTarget target, AiJobKind kind = AiJobKind.ImageCreate)
    {
        var request = Request(kind, target: target);
        return request with { Batch = AiBatchDefinition.Create(request.Id, 1, 7) };
    }
    [Fact]
    public async Task AnAssetQueuesSeveralImageBatchesUpToItsLimitAlongsideAPromptEnhancement()
    {
        var target = new AiJobTarget(Guid.NewGuid(), Guid.NewGuid());
        await Store.EnqueueAsync(Request(AiJobKind.PromptEnhancement, target: target), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.PromptEnhancement, target: target), _ct));
        var batches = new List<AiJobHeader>();
        for (var i = 0; i < AiJobLocks.MaxActiveImageBatchesPerAsset; i++)
            batches.Add(await Store.EnqueueAsync(ImageBatchFor(target, i % 2 == 0 ? AiJobKind.ImageCreate : AiJobKind.ImageEdit), _ct));
        Assert.Equal(AiJobLocks.MaxActiveImageBatchesPerAsset, AiJobLocks.ActiveImageBatches((await Store.ReadAsync(_ct)).Jobs, target.ProjectId, target.AssetId));
        Assert.Equal(AiJobLocks.ImageLimitMessage, (await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(ImageBatchFor(target), _ct))).Message);
        await Store.EnqueueAsync(ImageBatchFor(target with { AssetId = Guid.NewGuid() }), _ct); // Other assets have their own limit.

        // Extending an active batch adds to it; restarting a finished batch counts as a new active request.
        await Store.ExtendBatchAsync(batches[0].Id, Guid.NewGuid(), batches[0].OriginTabId, _ct);
        await Store.UpdateAsync(batches[1].Id, j => j with { State = AiJobState.Completed }, _ct);
        await Store.EnqueueAsync(ImageBatchFor(target), _ct);
        Assert.Equal(AiJobLocks.ImageLimitMessage, (await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ExtendBatchAsync(batches[1].Id, Guid.NewGuid(), batches[1].OriginTabId, _ct))).Message);
        await Store.UpdateAsync(batches[2].Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true }, _ct);
        var continuation = await Store.ExtendBatchAsync(batches[1].Id, Guid.NewGuid(), batches[1].OriginTabId, _ct);
        Assert.True(continuation.Continuation);
    }
    [Fact]
    public async Task BatchCaptureAndAppendKeepImmutableInputsAndNumberDiscardedCandidates()
    {
        var request = ImageBatch(); var original = request.Batch!.Candidates.ToArray();
        var list = original.ToList(); request = request with { Batch = request.Batch with { Candidates = list } };
        Task<AiJobHeader> enqueue;
        using (await ProjectFiles.LockAsync(Index, _ct))
        { enqueue = Store.EnqueueAsync(request, _ct); list.Clear(); }
        var root = await enqueue;
        var path = Path.Combine(Store.DirectoryFor(root.Id), "request.json"); var bytes = await File.ReadAllBytesAsync(path, _ct);
        var command = Guid.NewGuid();
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Store.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct)));
        Assert.All(replies, reply => { Assert.Equal(3, reply.Candidate.Number); Assert.Equal(replies[0].Candidate, reply.Candidate); Assert.False(reply.Continuation); });
        Assert.Equal(original, replies[0].Job.Batch!.Candidates.Take(2));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, _ct));
        Assert.True(JsonElement.DeepEquals(request.Snapshot, await Store.ReadSnapshotAsync(root.Id, _ct)));
        await Store.UpdateAsync(root.Id, j => j with { State = AiJobState.Completed }, _ct);
        var waiting = await Store.EnqueueAsync(Request(), _ct);
        var extension = await Store.ExtendBatchAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        Assert.True(extension.Continuation); Assert.Equal(4, extension.Candidate.Number); Assert.Single(extension.Job.Batch!.Candidates);
        Assert.Equal(root.Id, extension.Job.Batch.RootId);
        Assert.Equal(waiting.Id, (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
        Assert.True(JsonElement.DeepEquals(request.Snapshot, await Store.ReadSnapshotAsync(extension.Job.Id, _ct)));
        var ack = await Store.ExtendBatchAsync(root.Id, command, Guid.NewGuid(), _ct);
        Assert.Equal(3, ack.Candidate.Number); // no dependence on visible takes or their Trash state
        Assert.Equal(3, (await Store.ReadAsync(_ct)).Jobs.Count);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.UpdateAsync(root.Id, j => j with { Batch = j.Batch! with { Candidates = [] } }, _ct));
    }
    [Fact]
    public async Task FailedExtensionPublicationAndUncertainRemoteStateDoNotAddCandidates()
    {
        var root = await Store.EnqueueAsync(ImageBatch(), _ct); var command = Guid.NewGuid();
        using (var locked = new FileStream(Index, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<Exception>(() => Store.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct));
        Assert.Equal(2, (await Store.ReadAsync(_ct)).Jobs[0].Batch!.Candidates.Count);
        var appended = await Store.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct);
        await Store.UpdateAsync(root.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true, RemoteUnconfirmed = true }, _ct);
        Assert.Equal(appended.Candidate, (await Store.ExtendBatchAsync(root.Id, command, root.OriginTabId, _ct)).Candidate);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ExtendBatchAsync(root.Id, Guid.NewGuid(), root.OriginTabId, _ct));
        await Store.UpdateAsync(root.Id, j => j with { RemoteUnconfirmed = false }, _ct);
        // Improving the composer's prompt does not hold the asset's image batches.
        await Store.EnqueueAsync(Request(AiJobKind.PromptEnhancement, target: root.Target), _ct);
        await Store.ExtendBatchAsync(root.Id, Guid.NewGuid(), root.OriginTabId, _ct);
        Assert.Equal(3, (await Store.ReadAsync(_ct)).Jobs[0].Batch!.Candidates.Count);
        Assert.Equal(2, (await Store.ReadAsync(_ct)).Jobs.Count(j => j.Batch?.RootId == root.Id));
    }
    [Fact]
    public async Task BatchValidationRejectsDuplicatesInvalidSeedsAndNullCandidatesWithoutPublishing()
    {
        var request = ImageBatch(); var batch = request.Batch!;
        foreach (var invalid in new AiBatchDefinition[]
        {
            batch with { Candidates = null! }, batch with { Candidates = [null!] },
            batch with { Candidates = [batch.Candidates[0], batch.Candidates[0] with { Number = 2 }] },
            batch with { Candidates = [batch.Candidates[0] with { Seed = -1 }] },
            batch with { RootId = Guid.NewGuid() }
        }) await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(request with { Batch = invalid }, _ct));
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public void InitialSeedAllowsTheLastRepresentableCandidateWithoutOverflow()
    {
        Assert.Equal(long.MaxValue, AiBatchDefinition.Create(Guid.NewGuid(), 1, long.MaxValue).Candidates[0].Seed);
        var batch = AiBatchDefinition.Create(Guid.NewGuid(), 4, long.MaxValue - 3);
        Assert.Equal(long.MaxValue, batch.Candidates[^1].Seed);
        Assert.Throws<WorkspaceStoreException>(() => AiBatchDefinition.Create(Guid.NewGuid(), 4, long.MaxValue - 2));
    }
}
