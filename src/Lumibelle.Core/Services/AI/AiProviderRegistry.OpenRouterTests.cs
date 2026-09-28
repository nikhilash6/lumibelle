using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private sealed record OpenRouterTestReceipt(bool Started);
    private sealed class OpenRouterTestBlockedException(string? report) : Exception
    { public string? Report { get; } = report; }

    private async Task<AiModelTestJobResult> RunOpenRouterTestAsync(AiJobContext context, AiModelTestJobRequest request, CancellationToken ct)
    {
        using var inactivity = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, context.Clock);
        ct = inactivity.Token;
        const string operation = "model-test";
        if (context.Recovering || await context.ReadOperationAsync<OpenRouterTestReceipt>(operation, AiOperationArtifact.Request, ct) is not null)
            throw new AiJobRecoveryException("The OpenRouter test was interrupted. Inspect its saved response; use Generate again explicitly to send another request.", AiJobRecovery.GenerateAgain);
        await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking the captured OpenRouter model…")), true);
        var check = await CheckAsync(AiBackend.OpenRouter, request.Settings, cancellationToken: ct);
        if (!check.Success || !check.Models.Any(m => m.Id == request.Model.Model))
            throw new AiGenerationException(check.Success ? "This model is no longer available on OpenRouter." : check.Message);
        var key = await settingsStore.ReadOpenRouterKeyAsync(ct);
        using var http = clients.CreateClient("OpenRouter"); http.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = request.Model.Model, messages = new[] { new { role = "user", content = request.Test.Prompt } },
                max_tokens = request.Test.MaxOutputTokens, stream = true, provider = new { allow_fallbacks = false } })
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("X-OpenRouter-Metadata", "enabled");
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, new OpenRouterTestReceipt(true), ct);
        var timer = Stopwatch.StartNew(); var text = new StringBuilder(); var refusal = new StringBuilder();
        double? firstText = null; int? inputTokens = null, outputTokens = null, reasoningTokens = null, cachedTokens = null;
        decimal? cost = null; string? responseModel = null, provider = null, generationId = null, finish = null, error = null, guardrails = null;
        var done = false; var sawReasoning = false; var lastCheckpoint = TimeSpan.Zero;
        AiModelTestJobResult Result(bool complete) => new(null, text.ToString(), OpenRouter: new(context.Job.Id, request.Model.Model, DateTimeOffset.UtcNow,
            request.Test.MaxOutputTokens, request.Advanced, timer.Elapsed.TotalSeconds, firstText, inputTokens, outputTokens, reasoningTokens, cachedTokens,
            cost, responseModel, provider, generationId, finish, complete, HasResponseText: !string.IsNullOrWhiteSpace(text.ToString()),
            Refused: refusal.Length > 0 || finish == "content_filter", ReasoningObserved: sawReasoning),
            Refusal: refusal.Length > 0 ? refusal.ToString() : null, Error: error, GuardrailReport: guardrails);
        try
        {
            await context.ReportAsync(new(new(GenerationPhase.Generating, "Waiting for OpenRouter to respond…")), true);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                string? report = null;
                try { using var blocked = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); report = TestGuardrails(blocked.RootElement); }
                catch (JsonException) { }
                throw new OpenRouterTestBlockedException(report);
            }
            if (!response.IsSuccessStatusCode) throw new AiGenerationException(AiErrors.HttpStatus((int)response.StatusCode));
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) { data.AppendLine(line[5..].TrimStart()); continue; }
                if (line.Length != 0 || data.Length == 0) continue;
                var payload = data.ToString().Trim(); data.Clear();
                if (payload == "[DONE]") { done = true; break; }
                using var json = JsonDocument.Parse(payload); var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                guardrails = TestGuardrails(root) ?? guardrails;
                if (root.TryGetProperty("error", out _)) { error = "OpenRouter reported an error during the test. Any partial response is retained."; break; }
                responseModel = TestString(root, "model") ?? responseModel; provider = TestString(root, "provider") ?? provider;
                generationId = TestString(root, "id") ?? generationId;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.ValueKind != JsonValueKind.Object) continue;
                        finish = TestString(choice, "finish_reason") ?? finish;
                        if (finish is "stop" or "length" or "content_filter") inactivity.Stop();
                        var delta = TestObject(choice, "delta");
                        var content = TestString(delta, "content"); var refused = TestString(delta, "refusal");
                        var reasoningActivity = TextStreamActivity.Reasoning(delta);
                        sawReasoning |= reasoningActivity;
                        if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(refused) || reasoningActivity) inactivity.Activity();
                        if (!string.IsNullOrWhiteSpace(content)) firstText ??= timer.Elapsed.TotalSeconds;
                        text.Append(content); refusal.Append(refused);
                    }
                var usage = TestObject(root, "usage");
                inputTokens = TestCount(usage, "prompt_tokens") ?? inputTokens; outputTokens = TestCount(usage, "completion_tokens") ?? outputTokens;
                reasoningTokens = TestCount(TestObject(usage, "completion_tokens_details"), "reasoning_tokens") ?? reasoningTokens;
                if (outputTokens is { } count) inactivity.Observe(new GenerationProgress(GenerationPhase.Generating, "Generating", count, Unit: "tokens"));
                cachedTokens = TestCount(TestObject(usage, "prompt_tokens_details"), "cached_tokens") ?? cachedTokens;
                var previousCost = cost;
                cost = OpenRouterUsageParser.Money(OpenRouterUsageParser.Property(usage, "cost")) ?? cost;
                if (cost != previousCost || timer.Elapsed - lastCheckpoint >= TimeSpan.FromSeconds(1))
                {
                    lastCheckpoint = timer.Elapsed;
                    try { await context.SaveResultAsync(Result(false), checkpoint: cost != previousCost); }
                    catch (WorkspaceStoreException) { /* Continue the paid stream if a partial checkpoint fails. */ }
                    await context.ReportAsync(new(new(GenerationPhase.Generating, firstText is null ? sawReasoning ? "Model is reasoning · waiting for an answer…" : "Waiting for response text…" : "Receiving OpenRouter test response…",
                        Elapsed: timer.Elapsed, Current: text.Length + refusal.Length, Unit: "characters")));
                }
            }
        }
        catch (OpenRouterTestBlockedException blocked)
        { guardrails = blocked.Report; finish = "http_403"; error = "OpenRouter blocked this request (HTTP 403). Account guardrails or model access rules may apply; this is not necessarily a refusal from the model."; }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested) { error = inactivity.Expired ? inactivity.Message : "The OpenRouter test timed out. Any partial response is retained; retry explicitly."; }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException) { error = "The OpenRouter response was interrupted or unreadable. Any partial response is retained; retry explicitly."; }
        inactivity.Stop();
        timer.Stop();
        var complete = done && finish is "stop" or "length" or "content_filter";
        if (!complete) error ??= "The OpenRouter stream did not finish normally. Any partial response is retained; retry explicitly.";
        var result = Result(complete);
        // Retain completed paid output before publishing settings. Recovery only republishes it.
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            try { await context.SaveOperationAsync(operation, AiOperationArtifact.Output, result, context.Cancellation); break; }
            catch (WorkspaceStoreException e) when (e.InnerException is IOException or UnauthorizedAccessException)
            { await context.ReportAsync(new(new(GenerationPhase.Saving, "Test finished · waiting to save its response…"))); await Task.Delay(1000, context.Cancellation); }
        }
        return result;
    }

    private static JsonElement TestObject(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object ? item : default;
    private static string? TestGuardrails(JsonElement root)
    {
        var metadata = TestObject(root, "openrouter_metadata");
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("pipeline", out var pipeline) || pipeline.ValueKind != JsonValueKind.Array) return null;
        var reports = pipeline.EnumerateArray().Where(stage => TestString(stage, "type") == "guardrail")
            .Take(20).Select(stage => $"{TestString(stage, "name") ?? "Guardrail"}: {TestString(stage, "summary") ?? "No summary reported"}");
        var report = string.Join("\n", reports); return report.Length == 0 ? null : report[..Math.Min(report.Length, 12000)];
    }
    private static string? TestString(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int? TestCount(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var count) && count >= 0 ? count : null;
}
