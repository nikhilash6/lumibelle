using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

internal static class ScriptFixtures
{
    public static ScriptDocument Document(Guid? id = null) => new() { ProjectId = id ?? Guid.NewGuid(), Brief = new() { Idea = "A small mouse finds a key." },
        Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. KITCHEN — NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "A mouse appears in a saucepan."),
            ScriptBlock.Create(ScriptBlockKind.Character, "MOUSE"), ScriptBlock.Create(ScriptBlockKind.Dialogue, "You called?")] };
    public static ApprovedScriptSnapshot Approved(List<ScriptBlock> blocks, Guid? project = null) => new(Guid.NewGuid(), project ?? Guid.NewGuid(), 1,
        DateTimeOffset.UtcNow, "2–3 minutes", "h3-practical-v1", blocks);
}
internal sealed class FakeScriptStore : IScriptStore
{
    public ScriptDocument Document { get; set; } = ScriptFixtures.Document();
    public ApprovedScriptSnapshot? Approved { get; set; }
    public Exception? SaveError { get; set; }
    public Exception? LoadError { get; set; }
    public Func<Task>? BeforeSave { get; set; }
    public List<ScriptRecovery> Versions { get; } = [];
    public Task<ScriptDocument> LoadAsync(Guid id, CancellationToken cancellationToken = default) => LoadError is null
        ? Task.FromResult(Document.Copy()) : Task.FromException<ScriptDocument>(LoadError);
    public async Task<ScriptDocument> SaveAsync(ScriptDocument doc, long expectedRevision, string? recoveryReason = null, CancellationToken cancellationToken = default)
    {
        if (BeforeSave is not null) await BeforeSave();
        if (SaveError is not null) throw SaveError;
        if (expectedRevision != Document.Revision) throw new WorkspaceConflictException();
        if (recoveryReason is not null) Versions.Add(new(Guid.NewGuid(), recoveryReason, DateTimeOffset.UtcNow, Document.Copy()));
        return Document = doc.Copy() with { Revision = expectedRevision + 1, UpdatedUtc = DateTimeOffset.UtcNow, ApprovedSnapshotId = Document.ApprovedSnapshotId };
    }
    public Task<IReadOnlyList<ScriptRecovery>> ListRecoveryAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScriptRecovery>>(Versions);
    public Task<ScriptDocument> RestoreAsync(Guid id, Guid recoveryId, long expectedRevision, CancellationToken cancellationToken = default) =>
        SaveAsync(Versions.Single(r => r.Id == recoveryId).Document, expectedRevision, "Before restore", cancellationToken);
    public Task<ScriptDocument> ApproveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        if (expectedRevision != Document.Revision) throw new WorkspaceConflictException();
        Approved = ScriptFixtures.Approved(Document.Copy().Blocks, id);
        return Task.FromResult(Document = Document with { Revision = expectedRevision + 1, ApprovedSnapshotId = Approved.Id });
    }
    public Task<ScriptSourceSnapshot?> CaptureSourceAsync(Guid id, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<ScriptSourceSnapshot?>(new(Guid.NewGuid(), id, Document.Revision, DateTimeOffset.UtcNow, Document.Copy().Blocks));
    public Task<ScriptSourceSnapshot?> LoadSourceAsync(Guid id, Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<ScriptSourceSnapshot?>(Approved);
    public Task<ApprovedScriptSnapshot?> LoadApprovedAsync(Guid id, Guid? snapshotId = null, CancellationToken cancellationToken = default) => Task.FromResult(Approved);
}
internal sealed class FakeAssistantHistory : IAssistantHistoryStore
{
    public List<AssistantRun> Runs { get; } = [];
    public Exception? SaveError { get; set; }
    public Task<AssistantHistory> LoadAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new AssistantHistory { ProjectId = id, Runs = [.. Runs] });
    public Task<AssistantRun> SaveRunAsync(Guid id, AssistantRun run, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var i = Runs.FindIndex(r => r.Id == run.Id); var saved = run with { Revision = run.Revision + 1 };
        if (i < 0) Runs.Add(saved); else Runs[i] = saved; return Task.FromResult(saved);
    }
}
