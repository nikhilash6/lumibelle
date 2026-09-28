using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record RegionalReviewCandidate(Guid JobId, AiImageStaging Image, RegionalReviewState State, int Number);
public sealed record RegionalReviewBatch(AiImageJobRequest Request, IReadOnlyList<RegionalReviewCandidate> Candidates);

public sealed class RegionalImageReview(IAiJobStore jobs, IAssetStore assets)
{
    public async Task<RegionalReviewBatch> LoadAsync(Guid project, Guid root, CancellationToken ct = default)
    {
        var queue = await jobs.ReadAsync(ct);
        var header = queue.Jobs.SingleOrDefault(j => j.Id == root && j.Target.ProjectId == project)
            ?? throw new WorkspaceStoreException("This image request is unavailable.");
        var request = AiImageJobHandler.Read(header, await jobs.ReadSnapshotAsync(root, ct));
        if (request.Regional is null) throw new WorkspaceStoreException("This batch has no regional edit.");
        var library = await assets.LoadAsync(project, ct);
        var candidates = new List<RegionalReviewCandidate>();
        foreach (var job in queue.Jobs.Where(j => j.Batch?.RootId == root))
            foreach (var candidate in job.Batch!.Candidates)
                if (await jobs.ReadOperationAsync<AiImageStaging>(job.Id, "candidate/" + candidate.Id.ToString("D"), AiOperationArtifact.Image, ct) is { } image)
                {
                    if (image.CandidateId != candidate.Id || image.Metadata.AiJobId != job.Id) throw new WorkspaceStoreException("The staged image belongs to another request.");
                    var state = await AtomicJsonFile.ReadAsync<RegionalReviewState>(StatePath(job.Id, candidate.Id), ct) ?? new();
                    if (library.ImagePublications.Any(p => p.JobId == job.Id && p.ImageId == candidate.Id)) state = state with { Saved = true };
                    candidates.Add(new(job.Id, image, state, candidate.Number));
                }
        return new(request, candidates.OrderBy(c => c.Number).ToArray());
    }
    public async Task<AssetLibrary> SaveAsync(Guid project, Guid root, Guid candidateId, int blend, CancellationToken ct = default)
        => await SaveAsync(project, root, candidateId, blend, null, ct);

    public async Task<RegionalReviewState> SaveDraftAsync(Guid project, Guid root, Guid candidateId, int blend,
        RegionalColourSettings? colour, CancellationToken ct = default)
    {
        RegionalImageEdits.ValidateColour(colour);
        if (blend is < 0 or > 32) throw new AiGenerationException("Edge blend must be between 0 and 32 pixels.");
        using var gate = await ProjectFiles.LockAsync(Path.Combine(jobs.DirectoryFor(root), "regional-review"), ct);
        var candidate = (await LoadAsync(project, root, ct)).Candidates.Single(c => c.Image.CandidateId == candidateId);
        if (candidate.State.Saved || candidate.State.Discarded || candidate.State.SaveBlend is not null)
            throw new WorkspaceStoreException("This result was already saved, discarded or is awaiting a save retry. Reopen its review to see the current state.");
        var state = candidate.State with { DraftBlend = blend, DraftColour = colour };
        await AtomicJsonFile.WriteAsync(StatePath(candidate.JobId, candidateId), state, ct);
        return state;
    }

    public async Task<AssetLibrary> SaveAsync(Guid project, Guid root, Guid candidateId, int blend,
        RegionalColourSettings? colour, CancellationToken ct = default)
    {
        // A review lock is separate from queue execution and held through publication.
        using var gate = await ProjectFiles.LockAsync(Path.Combine(jobs.DirectoryFor(root), "regional-review"), ct);
        var batch = await LoadAsync(project, root, ct);
        var candidate = batch.Candidates.Single(c => c.Image.CandidateId == candidateId);
        if (candidate.State.Saved) return await assets.LoadAsync(project, ct);
        if (candidate.State.Discarded) throw new WorkspaceStoreException("This result was discarded in another review.");
        var path = StatePath(candidate.JobId, candidateId);
        RegionalImageEdits.ValidateColour(colour);
        if (blend is < 0 or > 32) throw new AiGenerationException("Edge blend must be between 0 and 32 pixels.");
        // Freeze every local adjustment before publishing. A failed save repeats the same bytes,
        // including historical save intents whose absent colour settings mean no correction.
        var state = candidate.State.SaveBlend is not null ? candidate.State : candidate.State with { SaveBlend = blend, SaveColour = colour };
        await AtomicJsonFile.WriteAsync(path, state, ct);
        var capture = batch.Request.Regional!;
        var result = await RegionalImageEdits.CompositeAsync(capture, candidate.Image.Bytes, state.SaveBlend!.Value, state.SaveColour, ct);
        var metadata = candidate.Image.Metadata with { Edit = candidate.Image.Metadata.Edit! with { Regional = new(capture.Selection, capture.Canvas, state.SaveBlend.Value, state.SaveColour, result.ColourMatched) } };
        using var content = new MemoryStream(result.Png, writable: false);
        await assets.PublishGeneratedImageAsync(project, new(candidate.JobId, candidateId, batch.Request.AssetId,
            new("regional-edit.png", batch.Request.Tags, AssetImageOrigin.Edited, metadata, batch.Request.Look?.LookId)), content, ct);
        await AtomicJsonFile.WriteAsync(path, state with { Saved = true }, ct);
        return await assets.LoadAsync(project, ct);
    }
    public async Task DiscardAsync(Guid project, Guid root, Guid candidateId, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Path.Combine(jobs.DirectoryFor(root), "regional-review"), ct);
        var batch = await LoadAsync(project, root, ct);
        var candidate = batch.Candidates.Single(c => c.Image.CandidateId == candidateId);
        if (candidate.State.Saved) throw new WorkspaceStoreException("This result is already in Assets. Use its normal Trash action.");
        await AtomicJsonFile.WriteAsync(StatePath(candidate.JobId, candidateId), candidate.State with { Discarded = true }, ct);
    }
    private string StatePath(Guid job, Guid candidate) => Path.Combine(jobs.DirectoryFor(job), "regional-review-" + candidate.ToString("N") + ".json");
}
