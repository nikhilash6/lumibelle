using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<AssetLibrary> FinishExtractionAsync(Guid projectId, ExtractionReviewInput review, long expectedRevision, CancellationToken cancellationToken = default)
    {
        // Own the mutable proposal collections before the first asynchronous operation.
        var captured = JsonSerializer.Deserialize<ExtractionReviewInput>(JsonSerializer.Serialize(review, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        if (captured.Id == Guid.Empty || captured.ApprovedScriptId == Guid.Empty || captured.SceneIds is not { Count: > 0 } ||
            captured.SceneIds.Distinct().Count() != captured.SceneIds.Count || captured.Proposals is null ||
            captured.Proposals.Any(p => p is null || !Enum.IsDefined(p.Decision) || p.Looks is null || p.Evidence is null || p.Looks.Any(l => l is null || !Enum.IsDefined(l.Decision) || l.Evidence is null)))
            throw new WorkspaceStoreException("The extraction review is incomplete. Select script scenes and review the proposals.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(captured, AtomicJsonFile.Options)));
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        if (current.ExtractionReviews.FirstOrDefault(r => r.Id == captured.Id) is { } previous)
        {
            if (previous.RequestFingerprint != fingerprint) throw new WorkspaceStoreException("This review was already finished with different decisions. Start a new review to make further changes.");
            return current;
        }
        EnsureRevision(current, expectedRevision);
        var source = await new FileScriptStore(files, clock).LoadSourceAsync(projectId, captured.ApprovedScriptId, cancellationToken)
            ?? throw new WorkspaceStoreException("The captured saved script is unavailable.");
        var scenes = ExtractionCoverage.Scenes(source).Where(s => captured.SceneIds.Contains(s.SceneId)).ToArray();
        if (scenes.Length != captured.SceneIds.Count) throw new WorkspaceStoreException("A reviewed scene does not belong to the captured source.");
        var evidence = captured.Proposals.SelectMany(p => p.Evidence.Concat(p.Looks.SelectMany(l => l.Evidence)));
        if (evidence.Any(e => e is null || e.ApprovedScriptId != source.Id || e.SceneId is null || !captured.SceneIds.Contains(e.SceneId.Value)))
            throw new WorkspaceStoreException("Proposal evidence must belong to the reviewed script scenes.");
        var updated = AssetExtractionApply.Apply(current, captured.Proposals, clock.GetUtcNow());
        var record = new AssetExtractionReview(captured.Id, clock.GetUtcNow(), source.Id, scenes, ExtractionCoverage.Summary(captured.Proposals), fingerprint);
        return await PublishAsync(directory, updated with { ExtractionReviews = [.. current.ExtractionReviews, record] }, current.Revision, cancellationToken);
    }

    private static void ValidateExtractionReviews(AssetLibrary library)
    {
        if (library.ExtractionReviews is null || library.ExtractionReviews.Select(r => r?.Id).Distinct().Count() != library.ExtractionReviews.Count ||
            library.ExtractionReviews.Any(r => r is null || r.Id == Guid.Empty || r.ReviewedUtc == default || r.ApprovedScriptId == Guid.Empty ||
                r.RequestFingerprint?.Length != 64 || r.Scenes is not { Count: > 0 } || r.Decisions is null ||
                new[] { r.Decisions.Created, r.Decisions.Merged, r.Decisions.Skipped, r.Decisions.LooksCreated, r.Decisions.LooksMerged, r.Decisions.LooksSkipped }.Any(c => c < 0) ||
                r.Scenes.Any(s => s is null || s.SceneId == Guid.Empty || s.Title is null || s.Fingerprint?.Length != 64) || r.Scenes.Select(s => s.SceneId).Distinct().Count() != r.Scenes.Count))
            throw new WorkspaceStoreException("The extraction review records are invalid. They have not been replaced.");
    }
}
