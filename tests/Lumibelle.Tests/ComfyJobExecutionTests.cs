using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ComfyJobExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.JobTransportTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _promptId = Guid.NewGuid().ToString("D");
    private FileAiJobStore Store => new(new StorageTestEnvironment(_root), TimeProvider.System);
    private ComfyJobExecution Transport => new(TestComfy.Monitor());
    private static ComfyExecutionOptions Options => new(new Dictionary<string, ComfyNodeStage>(), "Graph rejected", "Execution failed", "Pre-submit timeout", "Timed out", "Unreadable response", "Connection failed");
    private static object Workflow(string clientId) => new { client_id = clientId, prompt = new Dictionary<string, object> { ["1"] = new { class_type = "Test", inputs = new { seed = 123 } } } };
    private static HttpClient Client(HttpMessageHandler handler, string server = "http://comfy.test:8188/") => new(handler, false) { BaseAddress = new(server) };
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private string History => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        [_promptId] = new { status = new { completed = true, status_str = "success" }, outputs = new Dictionary<string, object> { ["2"] = new { text = new[] { "Saved output" } } } }
    });
    private async Task<AiJobContext> CreateContext()
    {
        var request = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant, AiBackend.ComfyUI, new(Guid.NewGuid()), "QA", "Transport", Guid.NewGuid(), new { prompt = "Captured" });
        await Store.EnqueueAsync(request, _ct); var job = await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        return Context(job!, false);
    }
    private AiJobContext Context(AiJobHeader job, bool recovering) => new(job, recovering, Store, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
    private async Task<AiJobContext> Recover(AiJobContext previous)
    {
        var job = await Store.UpdateAsync(previous.Job.Id, j => j with { LeaseId = Guid.NewGuid(), Recovery = AiJobRecovery.CheckStatus }, _ct);
        return Context(job, true);
    }
    [Fact]
    public async Task IntentPrecedesSubmissionAndDurableOutputCanReopenWithoutNetwork()
    {
        var context = await CreateContext(); var posts = 0;
        using var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++; var intent = (await context.ExecutionAsync(ct)).Submissions.Single();
                Assert.Equal(AiRemoteState.Submitting, intent.State);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(intent.ClientId, body.RootElement.GetProperty("client_id").GetString());
                Assert.NotNull(await context.ReadOperationAsync<ComfySavedOperation>("text", AiOperationArtifact.Request, ct));
                return Json($$"""{"prompt_id":"{{_promptId}}"}""");
            }
            Assert.EndsWith("/history/" + _promptId, request.RequestUri!.AbsolutePath); return Json(History);
        });
        using var http = Client(handler); var updates = new List<ComfyExecutionUpdate>();
        var clock = new TimerClock(); using var inactivity = new TextInactivityWatchdog(5, _ct, clock); var completions = 0;
        await foreach (var update in Transport.ExecuteAsync(context, "text", http, Workflow, Options, inactivity.Token,
            onProviderCompleted: () => { completions++; inactivity.Stop(); clock.Advance(100); })) updates.Add(update);
        Assert.Equal(1, completions); Assert.False(inactivity.Expired);
        Assert.Equal(1, posts); Assert.True(updates.Last().Complete);
        Assert.Equal(_promptId, (await context.ExecutionAsync(_ct)).Submissions.Single().PromptId);
        Assert.False((await context.ExecutionAsync(_ct)).MayBeRunning);
        var reopened = await Recover(context);
        using var noNetwork = new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("Saved results need no network"));
        using var disconnected = Client(noNetwork); var recovered = new List<ComfyExecutionUpdate>();
        await foreach (var update in Transport.ObserveAsync(reopened, "text", disconnected, ct: _ct)) recovered.Add(update);
        Assert.Single(recovered); Assert.Contains("Saved output", recovered.Single().Job!.Value.ToString());
        Assert.Empty(noNetwork.Requests);
    }
    [Theory] [InlineData(400, false)] [InlineData(500, true)] [InlineData(200, true)]
    public async Task RejectedGraphsAreDistinctFromUncertainAcceptance(int status, bool uncertain)
    {
        var context = await CreateContext(); using var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(Json("{}", (HttpStatusCode)status)));
        using var http = Client(handler);
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in Transport.ExecuteAsync(context, "text", http, Workflow, Options, _ct)) { } });
        Assert.Equal(uncertain, (await context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.Single(handler.Requests); Assert.Equal("POST", handler.Requests.Single().Method);
    }
    [Fact]
    public async Task LostAcceptanceStaysInspectableWithoutResubmissionOrUnownedCancellation()
    {
        var context = await CreateContext(); using var lost = new ScriptedHttpHandler((_, _) => throw new HttpRequestException("Receipt lost"));
        using var http = Client(lost);
        await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in Transport.ExecuteAsync(context, "text", http, Workflow, Options, _ct)) { } });
        var reopened = await Recover(context);
        using var disconnected = new ScriptedHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/queue" ? "{\"queue_running\":[],\"queue_pending\":[]}" : "{}"));
        });
        using var recoveryHttp = Client(disconnected);
        await Assert.ThrowsAsync<AiGenerationException>(async () => { await foreach (var _ in Transport.ObserveAsync(reopened, "text", recoveryHttp, ct: _ct)) { } });
        Assert.False(await Transport.CancelAsync(reopened, _ => Client(disconnected), _ct)); Assert.All(disconnected.Requests, r => Assert.Equal("GET", r.Method));
        var captured = await reopened.ReadOperationAsync<ComfySavedOperation>("text", AiOperationArtifact.Request, _ct);
        Assert.Equal(123, captured!.Workflow.GetProperty("prompt").GetProperty("1").GetProperty("inputs").GetProperty("seed").GetInt32());
    }
    [Fact]
    public async Task CancellationChecksAbsenceOfOwnedJobAndNeverInterruptsOtherWork()
    {
        var context = await CreateContext(); await context.BeginRemoteAsync("text", "http://comfy.test:8188"); await context.AcceptRemoteAsync("text", _promptId);
        var stillRunning = true; var otherId = Guid.NewGuid().ToString("D");
        // Simulate an old server without per-job cancellation. Dequeue accepts only the
        // exact owned ID; the global interrupt endpoint must never be used.
        using var fallback = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/queue")
            {
                var body = await request.Content!.ReadAsStringAsync(ct); Assert.Contains(_promptId, body); Assert.DoesNotContain(otherId, body); return Json("{}");
            }
            if (request.Method == HttpMethod.Post) return Json("{}", HttpStatusCode.NotFound);
            return Json(stillRunning ? $$"""{"queue_running":[[0,"{{_promptId}}"]],"queue_pending":[[1,"{{otherId}}"]]}"""
                : $$"""{"queue_running":[[0,"{{otherId}}"]],"queue_pending":[]}""");
        });
        Assert.False(await Transport.CancelAsync(context, _ => Client(fallback), _ct));
        stillRunning = false; Assert.True(await Transport.CancelAsync(context, _ => Client(fallback), _ct));
        Assert.False((await context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.DoesNotContain(fallback.Requests, r => r.Path.Contains("interrupt", StringComparison.Ordinal));
    }
    [Fact]
    public async Task CompletedCancellationRecheckMakesNoMutationAndReleasesTheReceipt()
    {
        var context = await CreateContext();
        await context.BeginRemoteAsync("video", "http://comfy.test:8188");
        await context.AcceptRemoteAsync("video", _promptId);
        using var handler = new ScriptedHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/queue", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"queue_running\":[[0,\"other-job\"]],\"queue_pending\":[]}"));
        });
        Assert.True(await Transport.CancelAsync(context, _ => Client(handler), _ct));
        Assert.Equal(AiRemoteState.Cancelled, (await context.ExecutionAsync(_ct)).Submissions.Single().State);
        Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task ChangedServerAndMalformedQueuesCannotConfirmCancellation()
    {
        var context = await CreateContext(); await context.BeginRemoteAsync("text", "http://comfy.test:8188"); await context.AcceptRemoteAsync("text", _promptId);
        using var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(Json("{}")));
        await Assert.ThrowsAsync<AiGenerationException>(() => Transport.CancelAsync(context, _ => Client(handler, "http://different.test:8188"), _ct));
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<AiGenerationException>(() => Transport.CancelAsync(context, _ => Client(handler), _ct));
        Assert.True((await context.ExecutionAsync(_ct)).MayBeRunning);
    }
    [Fact]
    public async Task OutputPublicationFailureRecoversFromHistoryWithoutGeneratingAgain()
    {
        var context = await CreateContext(); var posts = 0; string? outputPath = null;
        using var handler = new ScriptedHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++; outputPath = Path.Combine(Directory.GetDirectories(Path.Combine(context.Directory, "operations")).Single(), "output.json");
                return Task.FromResult(Json($$"""{"prompt_id":"{{_promptId}}"}"""));
            }
            if (posts == 1 && outputPath is not null && !Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath); // make publication fail after history reports completion
                outputPath = null;
            }
            return Task.FromResult(Json(History));
        });
        using var http = Client(handler);
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(async () => { await foreach (var _ in Transport.ExecuteAsync(context, "text", http, Workflow, Options, _ct)) { } });
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.False((await context.ExecutionAsync(_ct)).MayBeRunning);
        Directory.Delete(Path.Combine(Directory.GetDirectories(Path.Combine(context.Directory, "operations")).Single(), "output.json"));
        var reopened = await Recover(context); var updates = new List<ComfyExecutionUpdate>();
        await foreach (var update in Transport.ObserveAsync(reopened, "text", http, ct: _ct)) updates.Add(update);
        Assert.True(updates.Single().Complete); Assert.Equal(1, posts);
    }
    [Theory] [InlineData("queue", true)] [InlineData("history", true)] [InlineData("changed-graph", false)] [InlineData("another-client", false)] [InlineData("duplicate", false)]
    public async Task LostReceiptsRequireUniqueClientAndExactWorkflowMatch(string scenario, bool resolves)
    {
        var context = await CreateContext();
        using (var lost = new ScriptedHttpHandler((_, _) => throw new HttpRequestException("Lost receipt")))
        using (var http = Client(lost))
            await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in Transport.ExecuteAsync(context, "text", http, Workflow, Options, _ct)) { } });
        var saved = (await context.ReadOperationAsync<ComfySavedOperation>("text", AiOperationArtifact.Request, _ct))!;
        var originalGraph = saved.Workflow.GetProperty("prompt");
        var graph = scenario == "changed-graph" ? JsonSerializer.SerializeToElement(new { changed = true }) : originalGraph;
        object[] row = [0, _promptId, graph, new { client_id = scenario == "another-client" ? Guid.NewGuid().ToString() : saved.ClientId }, Array.Empty<object>()];
        var rows = new List<object[]> { row };
        if (scenario == "duplicate") rows.Add([1, Guid.NewGuid().ToString(), graph, new { client_id = saved.ClientId }, Array.Empty<object>()]);
        using var provider = new ScriptedHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath == "/queue") return Task.FromResult(Json(JsonSerializer.Serialize(new { queue_running = scenario == "history" ? [] : rows, queue_pending = Array.Empty<object>() })));
            if (request.RequestUri.AbsolutePath == "/history") return Task.FromResult(Json(scenario == "history" ? JsonSerializer.Serialize(new Dictionary<string, object> { [_promptId] = new { prompt = row } }) : "{}"));
            return Task.FromResult(Json(History));
        });
        using var recoveryHttp = Client(provider); var recovery = await Recover(context); var updates = new List<ComfyExecutionUpdate>();
        async Task Read() { await foreach (var update in Transport.ObserveAsync(recovery, "text", recoveryHttp, ct: _ct)) updates.Add(update); }
        if (resolves)
        {
            await Read(); Assert.True(updates.Single().Complete);
            Assert.Equal(_promptId, (await recovery.ExecutionAsync(_ct)).Submissions.Single().PromptId);
        }
        else
        {
            await Assert.ThrowsAsync<AiGenerationException>(Read); Assert.Empty(updates);
            Assert.Null((await recovery.ExecutionAsync(_ct)).Submissions.Single().PromptId);
        }
        Assert.All(provider.Requests, r => Assert.Equal("GET", r.Method));
    }
    [Fact]
    public async Task PreparedOperationIsImmutableAndProgressIsThrottledSeparately()
    {
        var initial = await CreateContext(); var clock = new ProgressClock();
        var context = new AiJobContext(initial.Job, false, Store, clock, (_, _) => { }, _ => { }, _ct);
        await context.SaveOperationAsync("candidate", AiOperationArtifact.Request, new { seed = 1, prompt = "Captured" }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => context.SaveOperationAsync("candidate", AiOperationArtifact.Request, new { seed = 2, prompt = "Changed" }, _ct));
        var revision = (await Store.ReadAsync(_ct)).Revision;
        for (var i = 1; i <= 10; i++) await context.ReportAsync(new(new(GenerationPhase.Generating, "Tokens", i, 100, "tokens")));
        Assert.Equal(1, (await Store.ReadArtifactAsync<AiJobProgress>(context.Job.Id, AiJobArtifact.Progress, _ct))!.Progress.Current);
        clock.Now += TimeSpan.FromSeconds(2); await context.ReportAsync(new(new(GenerationPhase.Generating, "Tokens", 11, 100, "tokens")));
        Assert.Equal(11, (await Store.ReadArtifactAsync<AiJobProgress>(context.Job.Id, AiJobArtifact.Progress, _ct))!.Progress.Current);
        Assert.Equal(revision, (await Store.ReadAsync(_ct)).Revision);
        Assert.Equal(1, (await context.ReadOperationAsync<JsonElement>("candidate", AiOperationArtifact.Request, _ct)).GetProperty("seed").GetInt32());
    }
    private sealed class ProgressClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public async Task FailedExecutionPersistsItsErrorAndReleasesRemoteReservation()
    {
        var context = await CreateContext();
        using var provider = new ScriptedHttpHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post
            ? $$"""{"prompt_id":"{{_promptId}}"}"""
            : JsonSerializer.Serialize(new Dictionary<string, object> { [_promptId] = new { status = new { completed = false, status_str = "error", messages = new[] { "GPU allocation failed" } }, outputs = new { } } }))));
        using var http = Client(provider);
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(async () => { await foreach (var _ in Transport.ExecuteAsync(context, "text", http, Workflow, Options, _ct)) { } });
        Assert.Equal(AiJobRecovery.GenerateAgain, failure.Recovery); Assert.False((await context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.Contains("GPU allocation failed", (await context.ReadOperationAsync<JsonElement>("text", AiOperationArtifact.Output, _ct)).ToString());
        var reopened = await Recover(context);
        using var offline = new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("Failed output is already saved")); using var disconnected = Client(offline);
        await Assert.ThrowsAsync<AiJobRecoveryException>(async () => { await foreach (var _ in Transport.ObserveAsync(reopened, "text", disconnected, ct: _ct)) { } });
        Assert.Empty(offline.Requests);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
