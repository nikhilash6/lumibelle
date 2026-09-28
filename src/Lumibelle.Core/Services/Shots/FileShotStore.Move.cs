using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class FileShotStore
{
    public async Task<ShotDocument> MoveTakesAsync(Guid projectId, IReadOnlyCollection<Guid> takeIds,
        Guid destinationShotId, long expectedRevision, CancellationToken ct = default)
    {
        var ids = takeIds.ToHashSet();
        if (ids.Count == 0 || ids.Count != takeIds.Count || ids.Contains(Guid.Empty))
            throw new WorkspaceStoreException("Choose distinct takes to move.");
        var dir = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct);
        Revision(document, expectedRevision);
        if (!document.Shots.Any(s => s.Id == destinationShotId))
            throw new WorkspaceStoreException("The destination shot is no longer available.");
        var takes = document.Takes.Where(t => ids.Contains(t.Id)).ToArray();
        if (takes.Length != ids.Count)
            throw new WorkspaceStoreException("The selected takes changed. Refresh and choose them again.");
        if (takes.All(t => t.ShotId == destinationShotId)) return document;
        foreach (var shot in document.Shots.Where(s => s.Id != destinationShotId && s.SelectedTakeId is { } id && ids.Contains(id)))
            shot.SelectedTakeId = null;
        // Ownership is editable; captured inputs and publication receipts are provenance.
        // Media lives under the take ID, so no file or generation request is moved.
        foreach (var take in takes) take.ShotId = destinationShotId;
        return await Publish(dir, document, ct);
    }
}
