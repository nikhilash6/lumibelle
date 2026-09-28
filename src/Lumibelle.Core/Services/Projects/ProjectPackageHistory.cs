using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

internal static class ProjectPackageHistory
{
    // Materialize terminal Script responses, not their execution records. Do not call
    // QueuedAssistantHistoryStore.LoadAsync under the project lock: its legacy store
    // can publish interrupted-state repairs. Export never writes to the source.
    internal static async Task<AssistantHistory?> CaptureAsync(Guid project, AssistantHistory? saved, IAiJobStore? jobs, CancellationToken ct)
    {
        if (jobs is null) return saved;
        var runs = (saved?.Runs ?? []).ToDictionary(r => r.Id);
        var queue = await jobs.ReadAsync(ct);
        foreach (var job in queue.Jobs.Where(j => j.Target.ProjectId == project && j.Kind == AiJobKind.ScriptAssistant &&
            !j.LocksTarget && !j.RemoteUnconfirmed && j.State is not (AiJobState.Waiting or AiJobState.Running)))
        {
            ct.ThrowIfCancellationRequested();
            var request = AiTextJobHandler.Read(job, await jobs.ReadSnapshotAsync(job.Id, ct));
            var payload = request.Payload<ScriptAssistantRequest>();
            if (payload.Script.ProjectId != project) throw new WorkspaceStoreException("A Script request contains another project's source; export was cancelled.");
            var run = payload.Run;
            if (run.Id != job.Id || run.JobId is { } linked && linked != job.Id)
                throw new WorkspaceStoreException("A Script request has inconsistent identities; it cannot be exported safely.");
            var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
            var update = result is { Complete: true } ? result.Read<AssistantUpdate>() : null;
            var cancelled = job.CancelRequested || job.State == AiJobState.Cancelled;
            var status = cancelled ? AssistantRunStatus.Cancelled : result is { Complete: true } ? AssistantRunStatus.Completed : AssistantRunStatus.Failed;
            var error = cancelled ? "Request cancelled. Partial output is kept for reference." : result?.Error ?? update?.ValidationError ?? job.Error;
            if (status == AssistantRunStatus.Completed && update is null) error ??= "No validated screenplay was saved. Inspect the response before editing.";
            run = run with { JobId = job.Id, Revision = 0, Status = status, Output = result?.Raw ?? "", Proposal = update?.Blocks,
                Edits = update?.Edits, Error = error, Applied = false, Rejected = false, AppliedTarget = null };
            runs[run.Id] = Merge(run, runs.GetValueOrDefault(run.Id));
        }
        return saved is null && runs.Count == 0 ? null : (saved ?? new() { ProjectId = project }) with {
            Runs = runs.Values.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToList()
        };
    }
    internal static AssistantRun Merge(AssistantRun captured, AssistantRun? reviewed)
    {
        if (reviewed is null) return captured;
        if (captured.Id != reviewed.Id || captured.JobId != reviewed.JobId)
            throw new WorkspaceStoreException("A Script response conflicts with its saved review; nothing was exported.");
        return captured with { Revision = reviewed.Revision, Applied = reviewed.Applied, Rejected = reviewed.Rejected, AppliedTarget = reviewed.AppliedTarget };
    }
    internal static AssistantHistory Portable(AssistantHistory history) => history with {
        Runs = history.Runs.Select(r => r with { JobId = null,
            Status = r.Status == AssistantRunStatus.Running ? AssistantRunStatus.Interrupted : r.Status,
            Error = r.Status == AssistantRunStatus.Running ? "Request was not transferred with this project." : r.Error }).ToList()
    };
}
