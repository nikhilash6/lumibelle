using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> TranslateShotAsync(Guid id, Guid tab, Guid projectId, Guid sourceTakeId,
        string targetLanguage, string instructions, TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        var request = await (dubbing ?? throw new WorkspaceStoreException("Language-variant storage is unavailable.")).CaptureAsync(projectId, sourceTakeId, targetLanguage, instructions, ct);
        return await BuildAsync(id, tab, AiJobKind.ShotTranslation, new(projectId, ShotId: request.ShotId, TakeId: sourceTakeId),
            "Translate dialogue · " + request.Target.Name, request, model, followsDefault, Copy(await settings.LoadAsync(ct)),
            ShotDubbing.Profile, ShotDubbing.Messages(request), .3f, null, ct);
    }
}
