using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public interface IAiTextRepairService
{
    Task<AiTextRepairOffer?> InspectAsync(Guid sourceId, CancellationToken ct = default);
    Task<AiJobSubmission> CaptureAsync(Guid sourceId, Guid id, Guid tab, Guid? previousRepairId = null, CancellationToken ct = default);
    Task ConnectReviewAsync(Guid repairId, CancellationToken ct = default);
}

public sealed class AiTextRepairService(IAiJobStore jobs, AiTextJobCapture capture,
    IProductionStore production, IShotStore shots, IAssetStore assets, IAssetReelStore reels,
    IProjectStore projects) : IAiTextRepairService
{
    public async Task<AiTextRepairOffer?> InspectAsync(Guid sourceId, CancellationToken ct = default)
    {
        var queue = await jobs.ReadAsync(ct);
        var job = queue.Jobs.FirstOrDefault(j => j.Id == sourceId);
        if (job is null || !AiTextRepairs.Supports(job.Kind) || job.State is AiJobState.Waiting or AiJobState.Running) return null;
        var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
        // Avoid loading large original vision snapshots for successful ordinary
        // requests. The label is only a prefilter; captured metadata is authoritative.
        if (result?.Error is null && !job.TargetName.StartsWith(AiTextRepairs.LabelPrefix, StringComparison.Ordinal)) return null;
        var request = await ReadRequestAsync(job, ct);
        if (request.Repair is null && (result?.Error is null || string.IsNullOrWhiteSpace(result.Raw))) return null;
        var existing = await FindChildAsync(queue.Jobs, job, ct);
        var issue = AiTextRepairs.Eligibility(job, request, result);
        if (issue is null)
        {
            try { AiTextRepairs.Capture(job, request, result!); }
            catch (WorkspaceStoreException e) { issue = e.Message; }
        }
        return new(job, request.Model.Name, issue, existing) { IsRepair = request.Repair is not null };
    }

    public async Task<AiJobSubmission> CaptureAsync(Guid sourceId, Guid id, Guid tab, Guid? previousRepairId = null, CancellationToken ct = default)
    {
        var queue = await jobs.ReadAsync(ct);
        var job = queue.Jobs.SingleOrDefault(j => j.Id == sourceId)
            ?? throw new WorkspaceStoreException("The failed request is unavailable.");
        if (!AiTextRepairs.Supports(job.Kind)) throw new WorkspaceStoreException("This operation has no text-only repair contract.");
        var previous = await FindChildAsync(queue.Jobs, job, ct);
        if (previous is not null && (previousRepairId != previous.Id || !AiTextRepairs.CanRepeat(previous)) ||
            previous is null && previousRepairId is not null)
            throw new WorkspaceStoreException("A repair already exists or changed. Review it before explicitly requesting another correction.");
        if (queue.Jobs.Any(j => j.LocksTarget && AiJobLocks.Key(j) == AiJobLocks.Key(job)))
            throw new WorkspaceStoreException("Wait for the active request on this target before fixing a saved response.");
        var request = await ReadRequestAsync(job, ct);
        var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct)
            ?? throw new WorkspaceStoreException("The failed response is unavailable.");
        var repair = AiTextRepairs.Capture(job, request, result) with { ReviewPredecessorJobId = previousRepairId };
        return await capture.RepairAsync(id, tab, job, request, repair, ct);
    }

    private async Task<AiTextJobRequest> ReadRequestAsync(AiJobHeader job, CancellationToken ct)
    {
        try { return AiTextJobHandler.Read(job, await jobs.ReadSnapshotAsync(job.Id, ct)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new WorkspaceStoreException("The saved text request cannot be read as a repair source. Its files have not been changed.", e); }
    }

    private async Task<AiJobHeader?> FindChildAsync(IReadOnlyList<AiJobHeader> queue, AiJobHeader source, CancellationToken ct)
    {
        // Relationship comes from captured metadata, never a display-name guess.
        foreach (var candidate in queue.Where(j => j.Id != source.Id && j.Kind == source.Kind && j.Target == source.Target &&
            j.CreatedUtc >= source.CreatedUtc && j.TargetName.StartsWith(AiTextRepairs.LabelPrefix, StringComparison.Ordinal)).OrderByDescending(j => j.CreatedUtc).ThenBy(j => j.Id))
        {
            var request = await ReadRequestAsync(candidate, ct);
            if (request.Repair?.SourceJobId == source.Id) return candidate;
        }
        return null;
    }

    public async Task ConnectReviewAsync(Guid repairId, CancellationToken ct = default)
    {
        var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == repairId)
            ?? throw new WorkspaceStoreException("The queued repair is unavailable. Retry enqueueing the same saved request.");
        var request = await ReadRequestAsync(job, ct);
        var repair = request.Repair ?? throw new WorkspaceStoreException("This is not a validation repair.");
        if (job.CancelRequested || job.State == AiJobState.Cancelled)
            return; // Its raw result remains inspectable, but never replace a live review pointer.
        var project = job.Target.ProjectId!.Value;
        // Only review pointers change here. Never accept or apply repaired content.
        // These guards complement (not replace) normal media/target checks at Apply.
        if (request.Kind == AiJobKind.PromptComposition)
        {
            var context = request.Payload<PromptCompositionRequest>();
            var doc = await production.LoadAsync(project, ct);
            var current = doc.Compositions.SingleOrDefault(c => c.Id == context.CompositionId)
                ?? throw new WorkspaceStoreException("The original composition is unavailable. The repair remains in AI activity.");
            if (current.AppliedJobId == job.Id || current.ReviewJobId == job.Id) return;
            var library = await assets.LoadAsync(project, ct);
            var coverage = await shots.LoadAsync(project, ct);
            var info = await projects.GetAsync(project, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
            if (current.Archived || !AiTextRepairs.ReviewPointerMatches(current.ReviewJobId, job.Id, repair) ||
                current.Prompt != context.CurrentPrompt || current.RevisionNotes != context.RevisionNotes ||
                context.ContextFingerprint != ProductionPolicy.ContextFingerprint(current, library, coverage, info))
                throw new WorkspaceStoreException("The prompt, references or review selection changed after the failed request. Your edits are preserved; inspect/copy the repair from AI activity or compose with the current inputs.");
            var next = current.Copy(); next.ReviewJobId = job.Id;
            await production.SaveAsync(project, next, current.Version, ct);
        }
        else if (request.Kind == AiJobKind.ReelComposition)
        {
            var context = request.Payload<ReelCompositionRequest>();
            var library = await assets.LoadAsync(project, ct);
            var current = library.ReelDrafts.SingleOrDefault(d => d.Id == context.Draft.Id)
                ?? throw new WorkspaceStoreException("The original reel recipe is unavailable. The repair remains in AI activity.");
            if (current.ResolvedJobs.Contains(job.Id) || current.PendingJobId == job.Id) return;
            if (!AiTextRepairs.ReviewPointerMatches(current.PendingJobId, job.Id, repair) ||
                ReferenceReels.Fingerprint(current) != context.Baseline)
                throw new WorkspaceStoreException("The reel recipe or its review selection changed after the failed request. Your edits are preserved; inspect/copy the repair or compose again.");
            var next = current.Copy(); next.PendingJobId = job.Id;
            await reels.SaveDraftAsync(project, next, current.Revision, ct);
        }
    }
}
