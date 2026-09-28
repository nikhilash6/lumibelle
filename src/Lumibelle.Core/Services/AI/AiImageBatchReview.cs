using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class AiImageBatchReview
{
    public static ImageReviewSession Update(ImageReviewSession? session, AiImageJobRequest request,
        IReadOnlyList<AiJobHeader> jobs, AssetLibrary library, AiJobProgress? progress)
    {
        var related = jobs.Where(j => j.Batch?.RootId == request.BatchId).OrderBy(j => j.Batch!.Candidates[0].Number).ToArray();
        var candidates = related.SelectMany(j => j.Batch!.Candidates.Select(c => (Job: j, Candidate: c))).OrderBy(c => c.Candidate.Number).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("This review has no captured batch.");
        session ??= new(request.ProjectId, request.AssetId, true, candidates.Length) { Id = request.BatchId, IsEditBatch = request.Edit is not null };
        session.IsRegional = request.Regional is not null;
        session.EnsureCandidateCount(candidates.Length);
        session.Entries.Clear(); session.CompletedCandidates.Clear(); session.PendingCandidates.Clear(); session.StoppedCandidates.Clear();
        foreach (var item in request.Inputs.Select((input, i) => (Input: input, Index: i)))
            session.Entries.Add(new(item.Input.Reference, item.Index == 0 ? "Source" : $"Reference {item.Index + 1}", SourceCrop: item.Input.Crop));
        foreach (var (job, candidate) in candidates)
        {
            if (library.ImagePublications.Any(p => p.JobId == job.Id && p.ImageId == candidate.Id && p.AssetId == request.AssetId))
            {
                session.CompletedCandidates.Add(candidate.Number);
                session.Entries.Add(new(new(request.AssetId, candidate.Id), $"Take {candidate.Number}", true));
                if (library.Assets.Any(a => a.Id == request.AssetId && a.Images.Any(i => i.Id == candidate.Id))) session.HiddenTakeIds.Remove(candidate.Id);
                else session.HiddenTakeIds.Add(candidate.Id);
            }
            else
            {
                session.PendingCandidates.Add(candidate.Number);
                if (job.CancelRequested || job.State == AiJobState.Cancelled) session.StoppedCandidates[candidate.Number] = "Cancelled";
                else if (job.State is AiJobState.Completed or AiJobState.NeedsAttention) session.StoppedCandidates[candidate.Number] = "Not generated";
            }
        }
        session.ObservedJobs = related;
        session.Job = related.FirstOrDefault(j => j.State is AiJobState.Waiting or AiJobState.Running) ?? related[^1];
        session.IsRunning = session.Job.State is AiJobState.Waiting or AiJobState.Running;
        session.CancellationRequested = session.Job.CancelRequested && session.IsRunning;
        session.WasCancelled = session.Job.CancelRequested;
        session.Status = session.Job.Error ?? (session.Job.CancelRequested ? "Generation cancelled. Completed takes remain available." : null);
        session.Progress = progress?.Progress;
        session.CurrentCandidate = progress?.Candidate ?? session.Job.Batch!.Candidates[0].Number;
        return session;
    }
}
