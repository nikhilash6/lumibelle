using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace lumibelle.Services.AI;

/// <summary>One fresh text request per probe/profile/repetition, through the shared provider queue.</summary>
public sealed class ContentProbeJobHandler(IAiProviderRegistry providers, IHttpClientFactory clients,
    ComfyJobExecution comfy, TimeProvider clock, ICodexClient? codex = null) : IAiJobHandler
{
    private const string RemoteOperation = "writing-probe";
    // ComfyJobExecution owns RemoteOperation/Output (the raw Comfy history JSON).
    private const string ResultOperation = "writing-probe-result";
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ContentProbe];

    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
        RunAsync(context, ContentProbePolicy.Read(context.Job, snapshot), false, ct);

    public async Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = ContentProbePolicy.Read(context.Job, snapshot);
        if (await context.ReadAsync<ContentProbeResult>(AiJobArtifact.Result, ct) is { Complete: true })
            return AiJobOutcome.Complete();
        if (await context.ReadOperationAsync<ContentProbeResult>(ResultOperation, AiOperationArtifact.Output, ct) is { Complete: true } saved)
        {
            await PersistAsync(context, () => context.SaveResultAsync(saved), ct);
            return AiJobOutcome.Complete();
        }
        if (request.Model.Backend != AiBackend.ComfyUI)
            return AiJobOutcome.Attention("This test was interrupted. Its partial response is inspectable. Repeat the captured test explicitly to make another request.", AiJobRecovery.GenerateAgain);
        // Observe the original durable Comfy receipt. Never submit in recovery.
        return await RunAsync(context, request, true, ct);
    }

    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
        context.Job.Backend == AiBackend.ComfyUI ? comfy.CancelAsync(context, ComfyClient, ct) : Task.FromResult(true);

    private async Task<AiJobOutcome> RunAsync(AiJobContext context, ContentProbeRequest request, bool recovering, CancellationToken ct)
    {
        using var timeout = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var text = new StringBuilder(); var refusal = new StringBuilder();
        string? finish = null, returnedModel = null, responseId = null;
        OpenRouterRequestUsage? usage = null;
        var lastCheckpoint = clock.GetUtcNow() - TimeSpan.FromSeconds(2);
        var viewerLimitReached = false;
        ContentProbeResult Result(bool complete = false) => new(text.ToString(), complete, finish, refusal.ToString(), returnedModel, responseId, usage);
        try
        {
            if (!recovering)
            {
                await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking captured test configuration…")), checkpoint: true);
                var check = await providers.CheckAsync(request.Model.Backend, request.Settings, cancellationToken: linked.Token);
                if (TextModelPolicy.Issue(request.Model, request.Settings, check) is { } issue) throw new AiGenerationException(issue);
            }
            await foreach (var update in GenerateAsync(context, request, timeout, recovering, linked.Token))
            {
                timeout.Observe(update);
                if (update.Progress is { } progress) await context.ReportAsync(new(progress));
                if (update.OpenRouterUsage is { } observed) usage = OpenRouterUsageParser.Merge(usage, observed);
                if (update.Response is not { } response) continue;
                returnedModel = response.ModelId ?? returnedModel;
                responseId = response.ResponseId ?? responseId;
                if (response.FinishReason is { } reason && (finish is null || finish == "stop")) finish = reason.ToString();
                if (response.RawRepresentation is StreamingChatCompletionUpdate raw && !string.IsNullOrEmpty(raw.RefusalUpdate))
                    refusal.Append(raw.RefusalUpdate.AsSpan(0, Math.Min(raw.RefusalUpdate.Length, Math.Max(0, 4000 - refusal.Length))));
                var chunk = response.Text;
                if (!string.IsNullOrEmpty(chunk))
                {
                    var remaining = ContentProbePolicy.MaximumResponseCharacters - text.Length;
                    text.Append(chunk.AsSpan(0, Math.Min(chunk.Length, remaining)));
                    if (chunk.Length > remaining)
                    {
                        finish = "local_output_limit"; viewerLimitReached = true;
                        // Preserve a bounded partial response, then stop observing this
                        // request. It must not become a rated refusal or successful test.
                        await context.SaveResultAsync(Result());
                        linked.Cancel();
                        throw new AiGenerationException("The response exceeded the test viewer's 1,000,000-character safety limit. The saved partial output is inconclusive.");
                    }
                }
                if (clock.GetUtcNow() - lastCheckpoint >= TimeSpan.FromSeconds(2))
                {
                    lastCheckpoint = clock.GetUtcNow();
                    try { await context.SaveResultAsync(Result(), checkpoint: false); }
                    catch (WorkspaceStoreException)
                    {
                        // Keep observing an already-paid stream after a failed checkpoint.
                    }
                    if (text.Length > 0 || refusal.Length > 0)
                        await context.ReportAsync(new(new(GenerationPhase.Generating, "Receiving test response…", Current: text.Length, Unit: "characters")));
                }
            }
            linked.Token.ThrowIfCancellationRequested();
            timeout.Stop();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (request.Model.Backend == AiBackend.ComfyUI)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
                try { await comfy.CancelAsync(context, ComfyClient, cancel.Token); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
            }
            throw new AiGenerationException(viewerLimitReached
                ? "The response exceeded the test viewer's 1,000,000-character safety limit. The saved partial output is inconclusive."
                : timeout.Expired ? timeout.Message : "The test provider timed out. Any saved partial output is inconclusive; repeat explicitly.");
        }
        var result = Result(true);
        await PersistAsync(context, () => context.SaveOperationAsync(ResultOperation, AiOperationArtifact.Output, result, ct), ct);
        await PersistAsync(context, () => context.SaveResultAsync(result), ct);
        // Completed means the request finished, never that its content passed.
        return AiJobOutcome.Complete();
    }

    private async IAsyncEnumerable<ProgressingChatUpdate> GenerateAsync(AiJobContext context, ContentProbeRequest request,
        TextInactivityWatchdog timeout, bool recovering, [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.Model.Backend == AiBackend.ComfyUI)
        {
            using var http = ComfyClient(request.Model.ComfyUrl!);
            var limit = request.Model.MaxOutputTokens ?? request.Settings.MaxOutputTokens;
            var temperature = request.Model.Temperature ?? request.Settings.Temperature;
            var atLimit = await context.ReadOperationAsync<ProbeTokenLimit>(RemoteOperation, AiOperationArtifact.TestResult, ct)
                is { Reached: true };
            var updates = recovering
                ? comfy.ObserveAsync(context, RemoteOperation, http, ct: ct, onProviderCompleted: timeout.Stop)
                : comfy.ExecuteAsync(context, RemoteOperation, http,
                    client => ComfyChatClient.BuildWorkflow(request.Model.Model, "[user]\n" + request.SubmittedPrompt + "\n\n[assistant]\n",
                        limit, temperature, request.Seed, client), ComfyChatClient.ExecutionOptions, ct, onProviderCompleted: timeout.Stop);
            await foreach (var update in updates)
            {
                if (!atLimit && update.Progress.Unit == "tokens" && update.Progress.Current >= limit)
                {
                    atLimit = true;
                    await context.SaveOperationAsync(RemoteOperation, AiOperationArtifact.TestResult, new ProbeTokenLimit(true), ct);
                }
                yield return new(Progress: update.Progress);
                if (!update.Complete || update.Job is not { } job) continue;
                if (!ComfyChatClient.TryReadText(job, out var output))
                    throw new AiJobRecoveryException("The completed ComfyUI test has no text output. Repeat explicitly after checking its saved request.", AiJobRecovery.GenerateAgain);
                yield return new(new ChatResponseUpdate(ChatRole.Assistant, output)
                { ModelId = request.Model.Model, ResponseId = update.PromptId, FinishReason = atLimit ? Microsoft.Extensions.AI.ChatFinishReason.Length : Microsoft.Extensions.AI.ChatFinishReason.Stop });
            }
            yield break;
        }
        if (recovering) throw new AiGenerationException("Recovery cannot submit a new writing test.");
        if (request.Model.Backend == AiBackend.Codex)
        {
            if (request.Codex is null || codex is null) throw new AiGenerationException("Codex is not configured for this test.");
            await context.SaveOperationAsync(RemoteOperation, AiOperationArtifact.Request, new CodexReceipt(), ct);
            await foreach (var update in codex.GenerateAsync(new(request.Settings.Codex, request.Codex,
                Path.Combine(context.Directory, "codex"), [new("user", [new(Text: request.SubmittedPrompt)])]), ct))
            {
                if (update.ThreadId is not null)
                    await context.SaveOperationAsync(RemoteOperation + (update.Complete ? "/complete" : update.TurnId is null ? "/thread" : "/turn"),
                        AiOperationArtifact.Request, new CodexReceipt(update.ThreadId, update.TurnId, update.Complete), ct);
                if (update.Progress is not null || update.Activity) yield return new(Progress: update.Progress, Activity: update.Activity);
                if (update.Text is not null) yield return new(new ChatResponseUpdate(ChatRole.Assistant, update.Text) { ModelId = request.Codex.Model });
                if (update.Complete) yield return new(new ChatResponseUpdate(ChatRole.Assistant, "") { ModelId = request.Codex.Model, FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop });
            }
            yield break;
        }
        using var client = await providers.CreateAsync(request.Model.Backend, request.Model.Model, request.Settings, ct);
        var options = TextGenerationOptions.Create(request.Model.Backend, request.Settings, seed: request.Seed, selection: request.Model);
        await using var stream = PromptEnhancer.Stream(client, [new(ChatRole.User, request.SubmittedPrompt)], options, ct).GetAsyncEnumerator(ct);
        while (await PromptEnhancer.MoveNext(stream, ct, timeout)) yield return stream.Current;
    }
    private HttpClient ComfyClient(string url)
    {
        var client = clients.CreateClient("ComfyUI");
        client.BaseAddress = new Uri(AiProviderRegistry.NormalizeComfyUrl(url) + "/");
        client.Timeout = Timeout.InfiniteTimeSpan; return client;
    }
    private async Task PersistAsync(AiJobContext context, Func<Task> write, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await write(); return; }
            catch (WorkspaceStoreException)
            {
                await context.ReportAsync(new(new(GenerationPhase.Saving, "Test output received; waiting to save it without generating again…")));
                await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            }
        }
    }
    public sealed record ProbeTokenLimit(bool Reached);
}
