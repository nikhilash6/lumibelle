using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class ComfyAccessLifecycleTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private static ComfyExecutionOptions Options => new(new Dictionary<string, ComfyNodeStage>(),
        "Rejected", "Execution failed", "Pre-submit timeout", "Timeout", "Unreadable", "Connection failed");
    private static object Workflow(string id) => new { client_id = id, prompt = new { test = new { class_type = "Test", inputs = new { } } } };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task Drain(IAsyncEnumerable<ComfyExecutionUpdate> updates)
    { await foreach (var _ in updates) { } }

    private static async Task<(FileAiJobStore Store, AiJobContext Context)> Context(ComfyAccessFixture f)
    {
        var store = new FileAiJobStore(new StorageTestEnvironment(Path.Combine(f.Root, "jobs")), TimeProvider.System);
        var request = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant, AiBackend.ComfyUI,
            new(Guid.NewGuid()), "QA", "Access submission", Guid.NewGuid(), new { prompt = "test" });
        await store.EnqueueAsync(request, Ct);
        var job = await store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct);
        Assert.NotNull(job);
        return (store, new(job, false, store, TimeProvider.System, (_, _) => { }, _ => { }, Ct));
    }

    [Theory]
    [InlineData(401)] [InlineData(403)]
    public async Task RejectedSubmissionRetiresTheIntentAndAllowsAnotherJob(int status)
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        var (store, context) = await Context(f);
        var calls = 0;
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            calls++; Assert.Equal("/prompt", request.RequestUri!.AbsolutePath);
            ComfyAccessTestHttp.AssertPair(request, "id", "secret");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        http.BaseAddress = new("https://comfy.example/");
        var transport = new ComfyJobExecution(TestComfy.Monitor());
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => Drain(transport.ExecuteAsync(context, "one", http, Workflow, Options, Ct)));
        Assert.True(error.DefinitelyNotSubmitted); Assert.Equal(1, calls);
        var execution = await context.ExecutionAsync(Ct);
        Assert.False(execution.MayBeRunning); Assert.Equal(AiRemoteState.Cancelled, Assert.Single(execution.Submissions).State);
        // Publish the coordinator's resulting failure state, then exercise the
        // real provider admission gate, not a fabricated in-memory queue.
        await store.UpdateAsync(context.Job.Id, j => j with
        {
            State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain,
            RemoteUnconfirmed = execution.MayBeRunning, FinishedUtc = DateTimeOffset.UtcNow, Error = error.Message
        }, Ct);
        var next = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant, AiBackend.ComfyUI,
            new(Guid.NewGuid()), "QA", "Next", Guid.NewGuid(), new { prompt = "next" });
        await store.EnqueueAsync(next, Ct);
        Assert.Equal(next.Id, (await store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct))!.Id);
    }

    [Fact]
    public async Task UndecryptableCredentialsBeforePostDoNotLeaveAnUncertainSubmission()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        f.Protector.FailUnprotect = true;
        var (_, context) = await Context(f);
        var sends = 0;
        using var http = ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        { sends++; return Task.FromResult(Json("{}")); });
        http.BaseAddress = new("https://comfy.example/");
        var transport = new ComfyJobExecution(TestComfy.Monitor());
        await Assert.ThrowsAsync<ComfyAccessException>(() => Drain(transport.ExecuteAsync(context, "one", http, Workflow, Options, Ct)));
        Assert.Equal(0, sends); Assert.False((await context.ExecutionAsync(Ct)).MayBeRunning);
    }

    [Theory]
    [InlineData("redirect")] [InlineData("html")] [InlineData("network")]
    public async Task AmbiguousSubmissionFailuresStillRequireReconciliation(string scenario)
    {
        using var f = new ComfyAccessFixture();
        var (_, context) = await Context(f);
        using var http = ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        {
            if (scenario == "network") throw new HttpRequestException("Connection lost");
            if (scenario == "html") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("unexpected", Encoding.UTF8, "text/html") });
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new("https://team.cloudflareaccess.com/cdn-cgi/access/login/app");
            return Task.FromResult(redirect);
        });
        http.BaseAddress = new("https://comfy.example/");
        var transport = new ComfyJobExecution(TestComfy.Monitor());
        await Assert.ThrowsAnyAsync<Exception>(() => Drain(transport.ExecuteAsync(context, "one", http, Workflow, Options, Ct)));
        Assert.True((await context.ExecutionAsync(Ct)).MayBeRunning);
    }

    [Fact]
    public async Task AuthenticationFailureWhilePollingPreservesAcceptedJobAndRecoveryDoesNotResubmit()
    {
        using var f = new ComfyAccessFixture();
        var (_, context) = await Context(f);
        var prompt = Guid.NewGuid().ToString("D"); var posts = 0; var denyHistory = true;
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            { posts++; return Task.FromResult(Json(JsonSerializer.Serialize(new { prompt_id = prompt }))); }
            if (denyHistory) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(Json(JsonSerializer.Serialize(new Dictionary<string, object>
            { [prompt] = new { status = new { completed = true, status_str = "success" }, outputs = new { } } })));
        });
        http.BaseAddress = new("https://comfy.example/");
        var transport = new ComfyJobExecution(TestComfy.Monitor());
        await Assert.ThrowsAsync<ComfyAccessException>(() => Drain(transport.ExecuteAsync(context, "one", http, Workflow, Options, Ct)));
        Assert.True((await context.ExecutionAsync(Ct)).MayBeRunning);
        Assert.Equal(prompt, Assert.Single((await context.ExecutionAsync(Ct)).Submissions).PromptId);
        denyHistory = false;
        await Drain(transport.ObserveAsync(context, "one", http, ct: Ct));
        Assert.False((await context.ExecutionAsync(Ct)).MayBeRunning); Assert.Equal(1, posts);
    }

    [Fact]
    public async Task DeniedRemoteCancellationCompletesTheDirectMonitorWithoutClaimingTheJobStopped()
    {
        using var f = new ComfyAccessFixture();
        var prompt = Guid.NewGuid().ToString("D");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancellations = 0;
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prompt")
                return Task.FromResult(Json(JsonSerializer.Serialize(new { prompt_id = prompt })));
            if (request.RequestUri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            { cancellations++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
            Assert.StartsWith("/history/", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json("{}"));
        });
        http.BaseAddress = new("https://comfy.example/");
        async Task Read()
        {
            // Deliberately no WithCancellation on enumeration: the monitor must
            // complete its own channel when best-effort remote cancellation fails.
            await foreach (var update in TestComfy.Monitor().ExecuteAsync(http, Workflow, Options, cancellation.Token, cancellation.Token))
                if (update.PromptId is not null) cancellation.Cancel();
        }
        var error = await Assert.ThrowsAsync<AiCancellationException>(() => Read().WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Contains("Stopped waiting", error.Message); Assert.Contains("may continue", error.Message);
        Assert.Equal(1, cancellations);
    }
}
