using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> PickAssetsAsync(Guid id, Guid tab, Guid projectId, Shot shot,
        string prompt, string directingNotes, string instructions, bool replaceExisting, string catalogueFingerprint,
        TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        // Freeze the local editor's inputs before crossing any await. Server-side
        // storage supplies the catalogue, never an unchecked browser candidate list.
        shot = shot.Copy();
        var source = await (shots ?? throw new WorkspaceStoreException("Shot storage is unavailable.")).LoadAsync(projectId, ct);
        if (!source.Shots.Any(s => s.Id == shot.Id)) throw new WorkspaceStoreException("The source shot was removed. Restore it before selecting references.");
        var library = await assets.LoadAsync(projectId, ct);
        var catalogue = AssetPickCatalog.Capture(library);
        var actual = AssetPickCatalog.Hash(catalogue);
        if (catalogueFingerprint != actual)
            throw new WorkspaceStoreException("The asset catalogue changed. Reopen references to use the current descriptions before requesting suggestions.");
        var request = new AssetPickRequest(1, projectId, shot, prompt, directingNotes, instructions, replaceExisting,
            catalogue, AssetPickCatalog.ContextFingerprint(projectId, shot, prompt, directingNotes), actual);
        return await BuildAsync(id, tab, AiJobKind.AssetPicking, new(projectId, ShotId: shot.Id),
            shot.Title + " · Suggest references", request, model, followsDefault, Copy(await settings.LoadAsync(ct)),
            AssetPicker.Profile, AssetPicker.BuildMessages(request), .2f, null, ct);
    }
}
