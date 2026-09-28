using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private sealed record QueuedTestPreparation(string Version, CacheBaseline Cache);

    public async Task<AiModelTestJobResult> RunQueuedTestAsync(AiJobContext context, AiModelTestJobRequest request,
        ComfyJobExecution execution, CancellationToken ct)
    {
        if (request.Model.Backend == AiBackend.OpenRouter) return await RunOpenRouterTestAsync(context, request, ct);
        using var inactivity = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, context.Clock);
        ct = inactivity.Token;
        const string operation = "model-test";
        using var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(NormalizeComfyUrl(request.Settings.ComfyUrl) + "/"); http.Timeout = Timeout.InfiniteTimeSpan;
        var submitted = (await context.ExecutionAsync(ct)).Submissions.Any(s => s.Operation == operation);
        var retainedPreparation = await context.ReadOperationAsync<QueuedTestPreparation>(operation, AiOperationArtifact.TestPreparation, ct);
        QueuedTestPreparation preparation;
        if (!submitted && retainedPreparation is null)
        {
            if (context.Recovering) throw new AiJobRecoveryException("The app stopped before this test was submitted. Run a new test explicitly.", AiJobRecovery.GenerateAgain);
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking the captured model and ComfyUI queue…")), true);
            var catalog = await PrepareVerificationAsync(http, request.Model.Model, ct, ct);
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Clearing ComfyUI model cache…")), true);
            preparation = new(catalog.Version, await ClearCachesAndReadBaselineAsync(http, catalog.Memory, ct, ct));
            await context.SaveOperationAsync(operation, AiOperationArtifact.TestPreparation, preparation, ct);
        }
        else preparation = retainedPreparation ?? throw new WorkspaceStoreException("The original benchmark environment could not be recovered.");
        // A resumed observer cannot claim to have measured peaks or timing during
        // the period when Lumibelle was offline. Preserve the verified response.
        var recovered = submitted || context.Recovering || retainedPreparation is not null;
        var memory = !recovered && preparation.Cache.Baseline is { } baseline ? new ComfyMemoryTracker(baseline, preparation.Cache.ClearConfirmed) : null;
        await using var sampler = memory is null ? null : new ComfyMemorySampler(http, memory, ct);
        var tokens = new TokenRateTracker(); var complete = false; string? response = null;
        var lastCheckpoint = DateTimeOffset.MinValue;
        var updates = submitted ? execution.ObserveAsync(context, operation, http, ct: ct, onProviderCompleted: inactivity.Stop)
            : execution.ExecuteAsync(context, operation, http, client => ComfyChatClient.BuildWorkflow(request.Model.Model,
                request.Test.Prompt, request.Test.MaxOutputTokens, request.Advanced ? request.Settings.Temperature : .7f, request.Seed, client), ComfyChatClient.ExecutionOptions, ct, onProviderCompleted: inactivity.Stop);
        await foreach (var update in updates.WithCancellation(ct))
        {
            inactivity.Observe(update.Progress); if (update.Complete) inactivity.Stop();
            tokens.Observe(update.Progress); await context.ReportAsync(new(update.Progress)); complete |= update.Complete;
            if (request.Advanced && update.Complete && update.Job is { } output) response = ComfyChatClient.TryReadText(output, out var text) ? text : "";
            if (!complete && context.Clock.GetUtcNow() - lastCheckpoint >= TimeSpan.FromSeconds(1))
            {
                lastCheckpoint = context.Clock.GetUtcNow();
                try { await context.SaveResultAsync(new AiModelTestJobResult(null, response, PartialBenchmark: memory?.Build(request.Test.MaxOutputTokens, tokens.GeneratedTokens, tokens.TokensPerSecond, request.Advanced))); }
                catch (WorkspaceStoreException) { /* Keep observing the submitted workflow if a checkpoint fails. */ }
            }
        }
        if (!complete) throw new AiJobRecoveryException("The test did not return a completed response. Check its saved remote job.", AiJobRecovery.CheckStatus);
        if (sampler is not null) await sampler.StopAsync();
        var benchmark = memory?.Build(request.Test.MaxOutputTokens, tokens.GeneratedTokens, tokens.TokensPerSecond, request.Advanced) ??
            new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, request.Test.MaxOutputTokens,
                recovered ? null : tokens.GeneratedTokens, recovered ? null : tokens.TokensPerSecond, preparation.Cache.ClearConfirmed, request.Advanced);
        return new(new(NormalizeComfyUrl(request.Model.ComfyUrl!), preparation.Version, request.Model.Model, DateTimeOffset.UtcNow, [benchmark]),
            request.Advanced ? response ?? "" : null, recovered);
    }
}
