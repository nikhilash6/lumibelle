using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class OpenRouterModelTestJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Chunk = "data: {\"id\":\"gen-test\",\"model\":\"actual/model\",\"provider\":\"Test provider\",\"choices\":[{\"delta\":{\"content\":\"Visible reply.\"},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":42,\"completion_tokens_details\":{\"reasoning_tokens\":5},\"prompt_tokens_details\":{\"cached_tokens\":3},\"cost\":0.00000001}}\n\ndata: [DONE]\n\n";

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TestsCaptureExactModelAndSaveNativeUsageWithoutChangingDefaults(bool advanced)
    {
        using var f = new Fixture(); var submission = f.Capture(advanced); await f.Jobs.EnqueueAsync(submission, Ct);
        Assert.Empty(f.Http.Requests); Assert.Equal(AiBackend.OpenRouter, submission.Backend);
        f.Settings.Value = f.Settings.Value with { OpenRouterModel = "changed/default", Temperature = 1.4f, OpenRouterConcurrency = 4 };
        var context = await f.Claim(); var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobState.Completed, outcome.State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.True(result.Saved); Assert.Null(result.Verification); Assert.Equal("Visible reply.", result.Response);
        var measured = Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks);
        Assert.Equal(Fixture.Model.Model, measured.Model); Assert.Equal("actual/model", measured.ResponseModel); Assert.Equal("Test provider", measured.Provider);
        Assert.Equal(42, measured.OutputTokens); Assert.Equal(11, measured.InputTokens); Assert.Equal(5, measured.ReasoningTokens); Assert.Equal(3, measured.CachedTokens);
        Assert.Equal(.00000001m, measured.Cost); Assert.True(measured.Complete); Assert.NotNull(measured.FirstTextSeconds);
        Assert.Equal(37, measured.ReplyTokens); Assert.Equal(37 / measured.ElapsedSeconds, measured.TokensPerSecond); Assert.Equal(advanced, measured.CustomPrompt);
        Assert.Equal("changed/default", f.Settings.Value.OpenRouterModel); Assert.Equal(1.4f, f.Settings.Value.Temperature); Assert.Empty(f.Settings.Value.ComfyTextModelVerifications);
        var post = Assert.Single(f.Http.Requests, r => r.Method == "POST");
        using var body = JsonDocument.Parse(post.Body); var json = body.RootElement;
        Assert.Equal(Fixture.Model.Model, json.GetProperty("model").GetString()); Assert.Equal(advanced ? 96 : 2048, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(advanced ? "Describe your content boundaries. <plain text>" : AiModelTestJobHandler.OpenRouterBenchmarkPrompt, json.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(json.TryGetProperty("temperature", out _)); Assert.False(json.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.DoesNotContain(f.Http.Requests, r => r.Path is "/prompt" or "/free");
    }

    [Fact]
    public async Task LegacyQueuedBenchmarksKeepTheirCapturedPromptAndBudget()
    {
        using var f = new Fixture(); var submission = f.Capture();
        var request = submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(2, request.Version);
        var legacy = request with { Version = 1, Test = new(AiProviderRegistry.StandardBenchmarkPrompt, 256) };
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(legacy, AtomicJsonFile.Options) };
        await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        using var body = JsonDocument.Parse(Assert.Single(f.Http.Requests, r => r.Method == "POST").Body);
        Assert.Equal(256, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(AiProviderRegistry.StandardBenchmarkPrompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Read(context.Job, JsonSerializer.SerializeToElement(legacy with { Version = 2 }, AtomicJsonFile.Options)));
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Read(context.Job, JsonSerializer.SerializeToElement(request with { Version = 1 }, AtomicJsonFile.Options)));
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("reasoning_content")]
    [InlineData("reasoning_details")]
    public async Task ReasoningOnlyExhaustionNeedsAttentionAndNeverReplaysAutomatically(string field)
    {
        var reasoning = field == "reasoning_details" ? "[{\"type\":\"reasoning.text\",\"text\":\"test reasoning marker\"}]" : "\"test reasoning marker\"";
        using var f = new Fixture { Stream = "data: {\"choices\":[{\"delta\":{\"" + field + "\":" + reasoning + "},\"finish_reason\":\"length\"}],\"usage\":{\"completion_tokens\":2048,\"completion_tokens_details\":{\"reasoning_tokens\":2048},\"cost\":0}}\n\ndata: [DONE]\n\n" };
        var submission = f.Capture(); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery);
        Assert.Contains("token limit was reached during reasoning", outcome.Error);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.True(result.Saved); Assert.Equal("", result.Response);
        var measured = Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks);
        Assert.True(measured.ReasoningOnly); Assert.False(measured.HasReply); Assert.Null(measured.FirstTextSeconds);
        Assert.Equal(0, measured.ReplyTokens); Assert.Null(measured.TokensPerSecond);
        Assert.DoesNotContain("test reasoning marker", JsonSerializer.Serialize(result));
        f.NoNetwork = true;
        Assert.Equal(AiJobState.NeedsAttention, (await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct)).State);
        Assert.Single(f.Http.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task EmptyAndWhitespaceRepliesDoNotConfirmGeneration()
    {
        using var f = new Fixture { Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"  \",\"reasoning\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":4}}\n\ndata: [DONE]\n\n" };
        var submission = f.Capture(); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.Contains("no answer text", outcome.Error);
        var measured = Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks);
        Assert.False(measured.HasReply); Assert.False(measured.ReasoningOnly); Assert.Null(measured.TokensPerSecond);
    }

    [Fact]
    public async Task AdvancedTestsAllowExplicitlyLargerBudgetsWithoutChangingDefaults()
    {
        using var f = new Fixture();
        var submission = AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), Fixture.Model, f.Settings.Value, new("A brief reply.", 8192));
        await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(8192, Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks).TokenLimit);
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), Fixture.Model, f.Settings.Value, new("A reply.", 8193)));
    }

    [Fact]
    public async Task SavedPaidOutputCanBeRepublishedAfterSettingsFailureWithoutAnotherRequest()
    {
        using var f = new Fixture(); var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        f.Settings.SaveError = new WorkspaceStoreException("Disk unavailable");
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, Ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        f.Settings.SaveError = null; f.NoNetwork = true;
        Assert.Equal(AiJobState.Completed, (await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct)).State);
        await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct);
        Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks); Assert.Single(f.Http.Requests, r => r.Method == "POST");
    }

    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task RejectedTestsAndInterruptedRecoveryNeverRetryInference(int status)
    {
        using var f = new Fixture { Status = (HttpStatusCode)status }; var submission = f.Capture(); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, Ct));
        f.NoNetwork = true;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct));
        Assert.Equal(AiJobRecovery.GenerateAgain, error.Recovery); Assert.Single(f.Http.Requests, r => r.Method == "POST");
        Assert.Empty(f.Settings.Value.OpenRouterTextModelBenchmarks);
    }

    [Theory] [InlineData("data: {bad json}\n\n")] [InlineData("data: {\"error\":{\"message\":\"private provider diagnostic\"}}\n\n")] [InlineData("")]
    public async Task InterruptedStreamsRetainPartialRepliesAndRequireExplicitNewGeneration(string ending)
    {
        using var f = new Fixture { Stream = Chunk + ending }; var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery); Assert.Equal(AiJobState.NeedsAttention, outcome.State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.Equal("Visible reply.", result.Response); Assert.False(result.OpenRouter!.Complete); Assert.Null(result.OpenRouter.TokensPerSecond);
        Assert.DoesNotContain("private provider diagnostic", result.Error);
        f.NoNetwork = true; await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct);
        Assert.Single(f.Http.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task RefusalIsRetainedAsModelBehaviorAndMissingUsageIsNotInvented()
    {
        using var f = new Fixture { Stream = "data: {\"choices\":[{\"delta\":{\"refusal\":\"I cannot answer that request.\"},\"finish_reason\":\"content_filter\"}],\"usage\":{\"prompt_tokens\":\"bad\",\"completion_tokens\":-1,\"cost\":\"-1\"}}\n\ndata: [DONE]\n\n" };
        var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.Equal("I cannot answer that request.", result.Refusal); Assert.Equal("content_filter", result.OpenRouter!.FinishReason);
        Assert.Null(result.OpenRouter.InputTokens); Assert.Null(result.OpenRouter.OutputTokens); Assert.Null(result.OpenRouter.Cost); Assert.Null(result.OpenRouter.TokensPerSecond);
    }

    [Fact]
    public async Task OutputJournalSurvivesRestartBeforeResultPublication()
    {
        using var f = new Fixture(); var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        await f.Registry.RunQueuedTestAsync(context, submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!, new(TestComfy.Monitor()), Ct);
        f.NoNetwork = true;
        Assert.Equal(AiJobState.Completed, (await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct)).State);
        Assert.Single(f.Settings.Value.OpenRouterTextModelBenchmarks);
        Assert.Equal("Visible reply.", (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!.Response);
    }

    [Fact]
    public async Task TimeoutRetainsAnInspectableResultAndNeverReplaysAutomatically()
    {
        using var f = new Fixture { WaitForCancellation = true }; f.Settings.Value = f.Settings.Value with { TimeoutSeconds = 5 };
        var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery); Assert.Contains("No new text or generation progress", outcome.Error);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.True(result.Saved); Assert.False(result.OpenRouter!.Complete);
        f.NoNetwork = true; await f.Worker.RecoverAsync(f.Context(context.Job, true, Ct), submission.Snapshot, Ct);
        Assert.Single(f.Http.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task ProviderGuardrailBlocksAreDistinctFromModelRefusals()
    {
        using var f = new Fixture { Status = HttpStatusCode.Forbidden, Stream = "{\"error\":{\"code\":403},\"openrouter_metadata\":{\"pipeline\":[{\"type\":\"guardrail\",\"name\":\"content-filter\",\"summary\":\"Blocked by configured account policy\"}]}}" };
        var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var context = await f.Claim();
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.Contains("HTTP 403", outcome.Error);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.Null(result.Refusal); Assert.Equal("content-filter: Blocked by configured account policy", result.GuardrailReport);
        Assert.Equal("http_403", result.OpenRouter!.FinishReason); Assert.Null(result.OpenRouter.Cost);
    }

    [Fact]
    public async Task CancellingAnOpenRouterTestStopsTheStreamWithoutComfyCallsOrAutomaticReplay()
    {
        using var f = new Fixture { WaitForCancellation = true }; var submission = f.Capture(true); await f.Jobs.EnqueueAsync(submission, Ct); var claimed = await f.Claim();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var context = f.Context(claimed.Job, false, cancel.Token);
        var run = f.Worker.ExecuteAsync(context, submission.Snapshot, cancel.Token);
        await f.PostStarted.Task.WaitAsync(Ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(await f.Worker.CancelRemoteAsync(context, submission.Snapshot, Ct));
        f.NoNetwork = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Context(claimed.Job, true, Ct), submission.Snapshot, Ct));
        Assert.Single(f.Http.Requests, r => r.Method == "POST"); Assert.Empty(f.Settings.Value.OpenRouterTextModelBenchmarks);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.OpenRouterTests", Guid.NewGuid().ToString("N"));
        public static readonly TextModelReference Model = new(AiBackend.OpenRouter, "test/model", "Test model");
        public FakeAiSettingsStore Settings { get; } = new();
        public FileAiJobStore Jobs { get; }
        public ScriptedHttpHandler Http { get; }
        public AiModelTestJobHandler Worker { get; }
        public AiProviderRegistry Registry { get; }
        public string Stream = Chunk + Usage;
        public bool NoNetwork, WaitForCancellation;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public TaskCompletionSource PostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture()
        {
            Jobs = new(_root, TimeProvider.System);
            Http = new(async (request, ct) =>
            {
                if (NoNetwork) throw new InvalidOperationException("Recovery must not call the provider.");
                if (request.Method == HttpMethod.Post) PostStarted.TrySetResult();
                if (request.Method == HttpMethod.Post && WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
                return request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/key") ? "{}" : "{\"data\":[{\"id\":\"test/model\",\"name\":\"Test model\"}]}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(Status) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
            });
            var factory = new TestHttpFactory(Http); Registry = new AiProviderRegistry(factory, Settings, TestComfy.Monitor());
            Worker = new(Registry, Settings, factory, new(TestComfy.Monitor()), TimeProvider.System);
        }
        public AiJobSubmission Capture(bool advanced = false) => AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), Model, Settings.Value, advanced ? new("Describe your content boundaries. <plain text>", 96) : null);
        public async Task<AiJobContext> Claim() => Context((await Jobs.ClaimNextAsync(AiBackend.OpenRouter, 1, Ct))!, false, Ct);
        public AiJobContext Context(AiJobHeader job, bool recovering, CancellationToken cancellation) => new(job, recovering, Jobs, TimeProvider.System, (_, _) => { }, _ => { }, cancellation);
        public void Dispose() { Http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
