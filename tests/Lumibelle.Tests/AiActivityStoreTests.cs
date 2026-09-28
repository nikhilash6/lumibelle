using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiJobStoreTests
{
    [Fact]
    public async Task ActivityBulkChangesAreVersionCheckedAndKeepArtifactsAndActiveReservations()
    {
        var store = Store;
        var waiting = await store.EnqueueAsync(Request(), _ct);
        var running = (await store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var queued = await store.EnqueueAsync(Request(), _ct);
        var blocked = await store.EnqueueAsync(Request(), _ct);
        blocked = await store.UpdateAsync(blocked.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true, RemoteUnconfirmed = true }, _ct);
        var finished = await store.EnqueueAsync(Request(), _ct);
        finished = await store.UpdateAsync(finished.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.RetryOutput, Unread = true }, _ct);
        await store.WriteArtifactAsync(finished.Id, AiJobArtifact.Result, new AiTextJobResult("Preserved response"), _ct);
        var input = await store.ReadSnapshotAsync(finished.Id, _ct);
        var selected = new[] { running, queued, blocked, finished }.Select(j => new AiActivityObservation(j.Id, j.Version)).ToArray();
        var cleared = Assert.Single(await store.ChangeActivityAsync(selected, AiActivityChange.Clear, _ct));
        Assert.Equal(finished.Id, cleared.Id); Assert.False(cleared.Unread); Assert.NotNull(cleared.ActivityClearedUtc);
        Assert.Equal(4, (await Store.ReadAsync(_ct)).Jobs.Count);
        Assert.True(JsonElement.DeepEquals(input, await store.ReadSnapshotAsync(finished.Id, _ct)));
        Assert.Equal("Preserved response", (await store.ReadArtifactAsync<AiTextJobResult>(finished.Id, AiJobArtifact.Result, _ct))!.Raw);
        Assert.Empty(await store.ChangeActivityAsync([new(finished.Id, finished.Version)], AiActivityChange.Read, _ct));
        var restored = Assert.Single(await store.ChangeActivityAsync([new(cleared.Id, cleared.Version)], AiActivityChange.Restore, _ct));
        Assert.Null(restored.ActivityClearedUtc); Assert.False(restored.Unread);
        var again = Assert.Single(await store.ChangeActivityAsync([new(restored.Id, restored.Version)], AiActivityChange.Clear, _ct));
        var resumed = await store.RequeueAsync(again.Id, _ct);
        Assert.Null(resumed.ActivityClearedUtc); Assert.Equal(AiJobState.Waiting, resumed.State);
        Assert.Empty(await store.ChangeActivityAsync([new(again.Id, again.Version)], AiActivityChange.Clear, _ct));
    }

    [Fact]
    public async Task ADelayedReadCannotConsumeANewerReviewablePublication()
    {
        var store = Store; var job = await store.EnqueueAsync(Request(), _ct);
        job = await store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        var newer = await store.UpdateAsync(job.Id, j => j with { Unread = true, Version = j.Version + 1 }, _ct);
        Assert.Empty(await store.ChangeActivityAsync([new(job.Id, job.Version)], AiActivityChange.Read, _ct));
        Assert.True((await store.ReadAsync(_ct)).Jobs.Single().Unread);
        Assert.False(Assert.Single(await store.ChangeActivityAsync([new(newer.Id, newer.Version)], AiActivityChange.Read, _ct)).Unread);
        Assert.DoesNotContain("activityClearedUtc", JsonSerializer.Serialize(job, lumibelle.Services.Story.AtomicJsonFile.Options));
    }
}
