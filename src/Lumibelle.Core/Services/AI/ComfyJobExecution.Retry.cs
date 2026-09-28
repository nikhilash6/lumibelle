using System.Net;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class ComfyJobExecution
{
    private async Task<bool> RetryStoppedAsync(AiJobContext context, string operation, AiRemoteSubmission submission,
        HttpClient http, CancellationToken ct)
    {
        if (context.Job.ComfyControl is not { PauseRequested: false } control || submission.RetryId == control.RetryId)
            return false;
        var output = await context.ReadOperationAsync<JsonElement?>(operation, AiOperationArtifact.Output, ct);
        if (output is { } local && IsComplete(local) && !IsFailed(local)) return false;

        // A recovered receipt must point at the exact owned workflow. Do not use a
        // filename, prompt similarity, queue position, or the current running job.
        if (submission.PromptId is null)
        {
            var saved = await context.ReadOperationAsync<ComfySavedOperation>(operation, AiOperationArtifact.Request, ct)
                ?? throw new AiGenerationException("The captured workflow is missing.");
            submission = await FindReceiptAsync(context, submission, saved, http, ct) ?? submission;
        }
        if (submission.PromptId is { } promptId)
        {
            // Queue first, then history: a job completing between these reads is
            // still found. A second queue read closes the pending/running handoff.
            using (var before = await GetJsonAsync(http, "queue", ct))
                if (Queued(before.RootElement, promptId)) return false;
            using var history = await GetJsonAsync(http, "history/" + Uri.EscapeDataString(promptId), ct);
            if (history.RootElement.ValueKind != JsonValueKind.Object)
                throw new AiGenerationException("ComfyUI history is unreadable; retry has not been submitted.");
            if (history.RootElement.TryGetProperty(promptId, out var terminal))
            {
                if (IsComplete(terminal) && !IsFailed(terminal)) return false;
                if (!IsFailed(terminal)) throw new AiGenerationException("The owned ComfyUI job is not yet terminal. Retry will check again.");
                if (output is null) await SaveOutputAsync(context, operation, terminal.Clone(), ct);
            }
            using (var after = await GetJsonAsync(http, "queue", ct))
                if (Queued(after.RootElement, promptId)) return false;
        }
        else
        {
            // An explicit user retry can authorize a missing legacy receipt only
            // after a second complete, readable queue/history lookup. Ambiguous
            // matches fail closed. A cleared server history cannot prove whether
            // old computation completed; the UI explains that before authorization.
            var saved = await context.ReadOperationAsync<ComfySavedOperation>(operation, AiOperationArtifact.Request, ct);
            if (saved is null) throw new AiGenerationException("The captured workflow is missing.");
            if (await FindReceiptAsync(context, submission, saved, http, ct) is not null) return false;
        }
        await context.FinishRemoteAsync(operation, cancelled: true);
        var retired = (await context.ExecutionAsync(ct)).Submissions.Single(s => s.Operation == operation);
        var retry = await context.BeginComfyRetryAsync(operation, retired, ct);
        await context.ReportAsync(new(new(GenerationPhase.Submitting, "Restarting the retained ComfyUI workflow…")), checkpoint: true);
        // Saving intent precedes this call. Lost acceptance of THIS attempt is
        // reconciled through its new client ID, not replayed under the same token.
        ct.ThrowIfCancellationRequested();
        using var response = await SubmitAsync(context, operation, http, retry.Workflow, ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            {
                await context.FinishRemoteAsync(operation, cancelled: true);
                throw new AiJobRecoveryException(retry.Options.RejectedMessage, AiJobRecovery.GenerateAgain);
            }
            throw new AiGenerationException(retry.Options.RejectedMessage);
        }
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!receipt.RootElement.TryGetProperty("prompt_id", out var value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var id))
            throw new AiGenerationException("The retry acceptance was not received. Check or retry this saved request; no duplicate was submitted automatically.");
        await context.AcceptRemoteAsync(operation, id.ToString("D"));
        return true;
    }
}
