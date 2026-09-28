using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiJobContext
{
    private async Task<string> OperationForAsync(string operation, AiOperationArtifact artifact, CancellationToken ct)
    {
        // Candidate staging and publication identities never move. Only the remote
        // workflow's immutable request/output/timing artifacts have attempt versions.
        if (Job.Backend != AiBackend.ComfyUI || artifact is not (AiOperationArtifact.Request or AiOperationArtifact.Output or AiOperationArtifact.Timings))
            return operation;
        return (await ExecutionAsync(ct)).Submissions.SingleOrDefault(s => s.Operation == operation)?.ArtifactOperation ?? operation;
    }

    internal async Task<bool> IsComfyStopRequestedAsync(CancellationToken ct)
    {
        var job = (await _store.ReadAsync(ct)).Jobs.Single(j => j.Id == Job.Id);
        return job.LeaseId == Job.LeaseId && !job.CancelRequested && job.ComfyControl?.PauseRequested == true;
    }

    // This is deliberately not an escape hatch on BeginRemoteAsync. Recovery may
    // replay ONLY a captured ComfyUI workflow, under explicit durable permission,
    // after the transport has reconciled and retired the previous remote attempt.
    internal async Task<ComfySavedOperation> BeginComfyRetryAsync(string operation, AiRemoteSubmission expected, CancellationToken ct)
    {
        Cancellation.ThrowIfCancellationRequested();
        await _journalGate.WaitAsync(ct);
        try
        {
            var job = await CurrentAsync(ct);
            if (job.Backend != AiBackend.ComfyUI || job.ComfyControl is not { PauseRequested: false } control ||
                (await _store.ReadAsync(ct)).Paused.Contains(AiBackend.ComfyUI))
                throw new AiGenerationException("Retry requires explicit authorization and an unpaused ComfyUI queue.");
            var execution = await ExecutionAsync(ct);
            var previous = execution.Submissions.SingleOrDefault(s => s.Operation == operation);
            if (previous is null || previous != expected || previous.MayBeRunning || execution.MayBeRunning || previous.RetryId == control.RetryId)
                throw new AiGenerationException("The prior attempt is not confirmed stopped, or this retry authorization was already used. Check the request before retrying again.");
            var previousOperation = previous.ArtifactOperation ?? operation;
            var saved = await _store.ReadOperationAsync<ComfySavedOperation>(Job.Id, previousOperation, AiOperationArtifact.Request, ct)
                ?? throw new AiGenerationException("The captured workflow is unavailable; it cannot be recreated safely.");
            if (saved.ServerUrl != previous.ServerUrl || saved.ClientId != previous.ClientId || saved.Options is null ||
                saved.Workflow.ValueKind != JsonValueKind.Object || !saved.Workflow.TryGetProperty("prompt", out var graph) || graph.ValueKind != JsonValueKind.Object)
                throw new AiGenerationException("The captured workflow and retired receipt do not match.");

            var attemptOperation = ComfyQueuePolicy.AttemptOperation(operation, control.RetryId);
            var retry = await _store.ReadOperationAsync<ComfySavedOperation>(Job.Id, attemptOperation, AiOperationArtifact.Request, ct);
            if (retry is null)
            {
                var client = Guid.NewGuid().ToString("D");
                var workflow = JsonNode.Parse(saved.Workflow.GetRawText())!.AsObject();
                workflow["client_id"] = client;
                // A captured client-supplied prompt ID, when present, belongs to the
                // old attempt; never ask the server to overwrite its history entry.
                if (workflow.ContainsKey("prompt_id")) workflow["prompt_id"] = Guid.NewGuid().ToString("D");
                retry = new(saved.ServerUrl, client, JsonSerializer.SerializeToElement(workflow, AtomicJsonFile.Options), saved.Options);
            }
            else
            {
                // A crash after saving the next workflow but before its intent must
                // reuse the same client ID, rather than conflict with immutable data.
                var originalBody = JsonNode.Parse(saved.Workflow.GetRawText())!.AsObject();
                var retryBody = JsonNode.Parse(retry.Workflow.GetRawText())!.AsObject();
                originalBody.Remove("client_id"); retryBody.Remove("client_id");
                originalBody.Remove("prompt_id"); retryBody.Remove("prompt_id");
                if (retry.ServerUrl != saved.ServerUrl || !Guid.TryParse(retry.ClientId, out _) ||
                    !JsonNode.DeepEquals(originalBody, retryBody) ||
                    !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.Options), JsonSerializer.SerializeToElement(retry.Options)))
                    throw new WorkspaceStoreException("The staged retry does not match its captured workflow.");
            }
            await _store.WriteOperationAsync(Job.Id, Job.LeaseId!.Value, previousOperation, AiOperationArtifact.RetryReceipt, previous, ct);
            await _store.WriteOperationAsync(Job.Id, Job.LeaseId.Value, attemptOperation, AiOperationArtifact.Request, retry, ct);
            await CurrentAsync(ct);
            var next = previous with { ClientId = retry.ClientId, PromptId = null, State = AiRemoteState.Submitting,
                StartedUtc = _clock.GetUtcNow(), RetryId = control.RetryId, ArtifactOperation = attemptOperation };
            // The pointer is the commit point. Old requests, results, and receipts
            // remain immutable and independently inspectable after every retry.
            await WriteAsync(AiJobArtifact.Execution, execution with
            { Submissions = execution.Submissions.Select(s => s.Operation == operation ? next : s).ToArray() }, ct);
            return retry;
        }
        finally { _journalGate.Release(); }
    }
}
