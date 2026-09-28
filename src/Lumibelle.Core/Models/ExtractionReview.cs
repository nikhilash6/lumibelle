namespace lumibelle.Models;

public enum SceneCoverageState { NotReviewed, Current, Changed }
public sealed record ReviewedAssetScene(Guid SceneId, string Title, string Fingerprint);
public sealed record ExtractionDecisionSummary(int Created, int Merged, int Skipped, int LooksCreated, int LooksMerged, int LooksSkipped);
public sealed record AssetExtractionReview(Guid Id, DateTimeOffset ReviewedUtc, Guid ApprovedScriptId,
    IReadOnlyList<ReviewedAssetScene> Scenes, ExtractionDecisionSummary Decisions, string RequestFingerprint);
public sealed record ExtractionReviewInput(Guid Id, Guid ApprovedScriptId, IReadOnlyList<Guid> SceneIds,
    IReadOnlyList<AssetExtractionProposal> Proposals);
public sealed record AssetExtractionReviewDraft(IReadOnlyList<AssetExtractionProposal> Proposals);
