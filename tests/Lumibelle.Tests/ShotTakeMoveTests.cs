using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task MovingTakesPreservesCapturedInputsMediaAndDestinationSelection()
    {
        var f = Fixture(); var first = Ready(); var second = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        await AddTake(f.Project.Id, f.Shots, first);
        await AddTake(f.Project.Id, f.Shots, first, 2);
        var doc = await AddTake(f.Project.Id, f.Shots, second);
        var ids = doc.Takes.Take(2).Select(t => t.Id).ToArray();
        doc.Shots[0].SelectedTakeId = ids[0]; doc.Shots[1].SelectedTakeId = doc.Takes[2].Id;
        doc = await f.Shots.SaveAsync(f.Project.Id, doc.Shots, doc.Revision, ct: _ct);
        var original = doc.Copy();
        doc = await f.Shots.MoveTakesAsync(f.Project.Id, ids, second.Id, doc.Revision, _ct);
        Assert.Null(doc.Shots[0].SelectedTakeId);
        Assert.Equal(original.Shots[1].SelectedTakeId, doc.Shots[1].SelectedTakeId);
        Assert.All(doc.Takes, t => Assert.Equal(second.Id, t.ShotId));
        foreach (var take in doc.Takes.Where(t => ids.Contains(t.Id)))
        {
            var expected = ShotCopy.Of(original.Takes.Single(t => t.Id == take.Id)); expected.ShotId = second.Id;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(take));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "shots", "takes", take.Directory, "video.mp4"), _ct));
        }
        Assert.Equal(original.TakePublications, doc.TakePublications);
        Assert.Equal(doc.Revision, (await f.Shots.MoveTakesAsync(f.Project.Id, ids, second.Id, doc.Revision, _ct)).Revision);
        doc = await f.Shots.DiscardAsync(f.Project.Id, ids[0], ShotTrashKind.Take, doc.Revision, _ct);
        doc = await f.Shots.RestoreAsync(f.Project.Id, [doc.Trash.Single(t => t.Take?.Id == ids[0]).Id], doc.Revision, _ct);
        Assert.Equal(second.Id, doc.Takes.Single(t => t.Id == ids[0]).ShotId);
        doc = await f.Shots.MoveTakesAsync(f.Project.Id, ids, first.Id, doc.Revision, _ct);
        Assert.Equal(2, doc.Takes.Count(t => t.ShotId == first.Id));
    }

    [Fact]
    public async Task MovingTakesRejectsStaleOrMissingSelectionsAtomically()
    {
        var f = Fixture(); var first = Ready(); var second = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, first); var id = doc.Takes[0].Id;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Shots.MoveTakesAsync(f.Project.Id, [id], second.Id, doc.Revision - 1, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.MoveTakesAsync(f.Project.Id, [id], Guid.NewGuid(), doc.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.MoveTakesAsync(f.Project.Id, [id, Guid.NewGuid()], second.Id, doc.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.MoveTakesAsync(f.Project.Id, [id, id], second.Id, doc.Revision, _ct));
        Assert.Equal(JsonSerializer.Serialize(doc), JsonSerializer.Serialize(await f.Shots.LoadAsync(f.Project.Id, _ct)));
    }

    [Fact]
    public async Task MovedTakeRegenerationPublishesToCurrentOwnerAndRecoveryKeepsItsReceipt()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct); var take = Assert.Single(doc.Takes);
        var destination = Ready(); doc.Shots.Add(destination);
        doc = await f.Shots.SaveAsync(f.Project.Id, doc.Shots, doc.Revision, ct: _ct);
        doc = await f.Shots.MoveTakesAsync(f.Project.Id, [take.Id], destination.Id, doc.Revision, _ct);
        await f.Worker.RecoverAsync(f.Context(context.Job, true), original.Snapshot, _ct);
        Assert.Equal(destination.Id, Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes).ShotId);
        await f.Jobs.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var repeat = await f.CaptureService.CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, take.Id, VideoResolution.Quick, take.Seed, _ct);
        var request = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(destination.Id, repeat.Target.ShotId);
        Assert.Equal(destination.Id, request.OutputShotId);
        Assert.Equal(take.Snapshot.Shot.Id, request.Snapshot.Shot.Id);
        Assert.Equal(take.Snapshot.Prompt, request.Snapshot.Prompt);
        var next = await f.Claim(repeat); await f.Worker.ExecuteAsync(next, repeat.Snapshot, _ct);
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, doc.Takes.Count); Assert.All(doc.Takes, t => Assert.Equal(destination.Id, t.ShotId));
        await f.Worker.RecoverAsync(f.Context(next.Job, true), repeat.Snapshot, _ct);
        Assert.Equal(2, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Count);
    }

    [Fact]
    public async Task MovingTakeKeepsExistingCutClipsPlayableAndExportable()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        var doc = await f.Shots.LoadAsync(f.ProjectId, _ct); var destination = Ready(); doc.Shots.Add(destination);
        doc = await f.Shots.SaveAsync(f.ProjectId, doc.Shots, doc.Revision, ct: _ct);
        await f.Shots.MoveTakesAsync(f.ProjectId, [doc.Takes[0].Id], destination.Id, doc.Revision, _ct);
        var cut = await f.Cuts.SaveAsync(f.ProjectId, f.Cut.Clips, f.Cut.Revision, _ct);
        await exporter.ExportAsync(f.ProjectId, cut.Revision, _ct);
        Assert.Equal(2, f.Media.Segments.Count);
        Assert.All(f.Media.CapturedInputs, bytes => Assert.Equal(new byte[] { 1, 2, 3 }, bytes));
    }
}
