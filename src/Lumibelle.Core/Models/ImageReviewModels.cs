namespace lumibelle.Models;

// Page-local viewer state. Durable batch identities are supplied by the shared queue.
public sealed record ImageReviewEntry(AssetImageReference Reference, string Label, bool IsTake = false,
    ImageCropRegion? SourceCrop = null);

public sealed record ImageReviewDiscardResult(string? Error, Guid? TrashId = null);

public sealed class ImageReviewSession(Guid projectId, Guid assetId, bool isBatch, int requestedCount = 1)
{
    public bool IsRegional { get; set; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool IsEditBatch { get; init; } = true;
    public AiJobHeader? Job { get; set; }
    public IReadOnlyList<AiJobHeader> ObservedJobs { get; set; } = [];
    public Guid ProjectId { get; } = projectId;
    public Guid AssetId { get; } = assetId;
    public bool IsBatch { get; } = isBatch;
    public int RequestedCount { get; private set; } = requestedCount;
    public Dictionary<int, string> StoppedCandidates { get; } = [];
    public void AddCandidate() { RequestedCount = checked(RequestedCount + 1); PendingCandidates.Add(RequestedCount); }
    public void EnsureCandidateCount(int count) { while (RequestedCount < count) AddCandidate(); }
    public List<ImageReviewEntry> Entries { get; } = [];
    public HashSet<Guid> HiddenTakeIds { get; } = [];
    public HashSet<int> CompletedCandidates { get; } = [];
    public HashSet<int> PendingCandidates { get; } = isBatch ? [.. Enumerable.Range(1, requestedCount)] : [];
    public bool IsRunning { get; set; }
    public bool ReviewOffered { get; set; }
    public bool CancellationRequested { get; set; }
    public bool WasCancelled { get; set; }
    public int CurrentCandidate { get; set; } = 1;
    public GenerationProgress? Progress { get; set; }
    public string? Status { get; set; }
}
