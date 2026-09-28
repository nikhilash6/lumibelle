using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class AiRefModCapture(IAssetStore assets, IReferenceVideoStore media, ReelRefModStore store,
    IAiSettingsStore settings, IProjectStore projects, ComfyRefModClient remote)
{
    // Preparation is CPU-only. The explicit Build button submits the VAE operation to the
    // existing provider queue, never to a second uncoordinated GPU worker.
    public async Task<RefModPreparedBuild> PrepareAsync(Guid project, ShotVideoBinding binding,
        int width, int height, Guid tab, CancellationToken ct, bool reuse = true)
    {
        var source = ShotCopy.Of(binding);
        if (!ReelRefMods.Canvases.Contains((width, height))) throw new WorkspaceStoreException("Choose a supported RefMod canvas.");
        _ = ReelRefMods.Selection(source);
        var library = await assets.LoadAsync(project, ct);
        var reel = library.Reels.FirstOrDefault(r => r.Media == source.Media)
            ?? throw new WorkspaceStoreException("Restore or select an available asset reel before building a RefMod.");
        if (source.OwnerAssetId is { } owner && owner != reel.AssetId) throw new WorkspaceConflictException();
        var configured = await settings.LoadAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(120));
        await remote.CheckAsync(configured.ComfyUrl, configured.H3.VideoVae, limit.Token);
        var previews = await ReelRefModPreparation.PreparePixelsAsync(media, project, source, configured.H3, width, height, ct);
        var recipe = ReelRefMods.Recipe(source, width, height, configured.H3.VideoVae,
            previews.Select(p => Convert.ToHexString(SHA256.HashData(p))).ToArray());
        var id = Guid.NewGuid();
        var request = new RefModBuildRequest(2, id, project, reel.AssetId, source.Media, ShotCopy.Of(source.Keyframes!), recipe,
            AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl), configured.H3.TimeoutSeconds);
        AiRefModJobHandler.Validate(request);
        var directory = Path.Combine(await store.BuildDirectoryAsync(project, id, ct), "inputs");
        Directory.CreateDirectory(directory);
        for (var i = 0; i < previews.Count; i++) await File.WriteAllBytesAsync(Path.Combine(directory, ReelRefModStore.FrameName(i)), previews[i], ct);
        var name = (await projects.GetAsync(project, ct))?.Name ?? throw new WorkspaceStoreException("Project unavailable.");
        var submission = AiJobSubmission.Create(id, AiJobKind.RefModBuild, AiBackend.ComfyUI,
            new(project, reel.AssetId, ReelId: source.Media.Id), name, source.Name + " · Build visual RefMod", tab, request);
        var existing = reuse ? await store.FindAsync(project, recipe.Key, request.ComfyUrl, ct) : null;
        if (existing is not null && !await remote.ReferenceAvailableAsync(existing, limit.Token)) existing = null;
        return new(submission, previews, existing);
    }
}
