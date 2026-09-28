using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ContentProbeJobTests
{
    private static ContentProbeJobHandler Handler(ContentProbeFixture f) => new(f.Providers,
        new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"))),
        new ComfyJobExecution(TestComfy.Monitor()), TimeProvider.System);

    [Fact]
    public async Task DispatchUsesOneFreshConversationAndFrozenProfileWithoutAutoRating()
    {
        using var f = new ContentProbeFixture(); var profile = ContentProbeFixture.Profile() with { Temperature = 0, ReasoningEffort = "none" };
        f.Settings.Value = f.Settings.Value with { TextModelProfiles = [profile] };
        var probe = ContentProbeBuiltIns.All(new()).First();
        var plan = await f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [probe.Id], [profile], 1, 0, 0, f.Ct);
        var submission = Assert.Single(plan); var context = await f.ClaimAsync(submission);
        f.Settings.Value = f.Settings.Value with { TextModelProfiles = [], Temperature = 2, MaxOutputTokens = 123 };
        f.Providers.Chat.Text = "NO";
        Assert.Equal(AiJobState.Completed, (await Handler(f).ExecuteAsync(context, submission.Snapshot, f.Ct)).State);
        var call = Assert.Single(f.Providers.Chat.Calls); var message = Assert.Single(call.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(ContentProbePolicy.Prompt(probe), message.Text);
        Assert.Equal(0f, call.Options!.Temperature); Assert.Equal(4096, call.Options.MaxOutputTokens);
        Assert.NotNull(call.Options.RawRepresentationFactory);
        var result = await f.Jobs.ReadArtifactAsync<ContentProbeResult>(submission.Id, AiJobArtifact.Result, f.Ct);
        Assert.True(result!.Complete); Assert.Equal("returned/model", result.ReturnedModel);
        Assert.Equal(ContentProbeOutcome.Refused, ContentProbePolicy.Outcome(ContentProbePolicy.Read(context.Job, submission.Snapshot), result));
        Assert.Null((await f.Probes.LoadReviewAsync(submission.Id, f.Ct)).Score);
        f.Providers.FailChecks = true;
        Assert.Equal(AiJobState.Completed, (await Handler(f).RecoverAsync(f.Context(context.Job, true), submission.Snapshot, f.Ct)).State);
        Assert.Equal(1, f.Providers.Creates); Assert.Single(f.Providers.Chat.Calls);
    }
    [Fact]
    public async Task InterruptedHostedResponseIsInspectableButRecoveryNeverResubmits()
    {
        using var f = new ContentProbeFixture(); var request = ContentProbeFixture.Request();
        var submission = ContentProbeCapture.Submission(request, Guid.NewGuid()); var context = await f.ClaimAsync(submission);
        f.Providers.Chat.Text = "A partial scene"; f.Providers.Chat.FailAfterText = true;
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler(f).ExecuteAsync(context, submission.Snapshot, f.Ct));
        var result = await f.Jobs.ReadArtifactAsync<ContentProbeResult>(submission.Id, AiJobArtifact.Result, f.Ct);
        Assert.NotNull(result); Assert.False(result.Complete); Assert.Equal("A partial scene", result.Raw);
        f.Providers.FailChecks = true;
        var recovered = await Handler(f).RecoverAsync(f.Context(context.Job, true), submission.Snapshot, f.Ct);
        Assert.Equal(AiJobRecovery.GenerateAgain, recovered.Recovery); Assert.Single(f.Providers.Chat.Calls);
    }
    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task AbnormalCompletionsAreRetainedWithoutBecomingPassingResults(string? finish)
    {
        using var f = new ContentProbeFixture(); var request = ContentProbeFixture.Request();
        var submission = ContentProbeCapture.Submission(request, Guid.NewGuid()); var context = await f.ClaimAsync(submission);
        f.Providers.Chat.Text = "NO"; f.Providers.Chat.Finish = finish;
        await Handler(f).ExecuteAsync(context, submission.Snapshot, f.Ct);
        var result = await f.Jobs.ReadArtifactAsync<ContentProbeResult>(submission.Id, AiJobArtifact.Result, f.Ct);
        Assert.Equal(finish == "content_filter" ? ContentProbeOutcome.ProviderBlocked : ContentProbeOutcome.Inconclusive, ContentProbePolicy.Outcome(request, result));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenRouterProfileAndRefusalSignalsTravelThroughTheRealSdk(bool apiRefusal)
    {
        using var f = new ContentProbeFixture();
        var delta = apiRefusal ? new { role = "assistant", content = "", refusal = (string?)"I decline this request." }
            : new { role = "assistant", content = "A scene", refusal = (string?)null };
        var stream = "data: " + JsonSerializer.Serialize(new { id = "chat-1", @object = "chat.completion.chunk", created = 1, model = "reported/model",
            choices = new[] { new { index = 0, delta, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";
        var http = new ScriptedHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.Method == HttpMethod.Post ? new StringContent(stream, Encoding.UTF8, "text/event-stream") :
                new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/key") ? "{}" :
                    """{"data":[{"id":"test/model","name":"Test model","supported_parameters":["temperature","reasoning","max_tokens"]}]}""", Encoding.UTF8, "application/json")
        }));
        var factory = new TestHttpFactory(http);
        var registry = new AiProviderRegistry(factory, f.Settings, TestComfy.Monitor());
        var profile = ContentProbeFixture.Profile() with { Temperature = 0, ReasoningEffort = "none" };
        var request = ContentProbeFixture.Request(model: profile, settings: f.Settings.Value);
        var submission = ContentProbeCapture.Submission(request, Guid.NewGuid()); var context = await f.ClaimAsync(submission);
        var handler = new ContentProbeJobHandler(registry, factory, new ComfyJobExecution(TestComfy.Monitor()), TimeProvider.System);
        await handler.ExecuteAsync(context, submission.Snapshot, f.Ct);
        using var payload = JsonDocument.Parse(Assert.Single(http.Requests, r => r.Method == "POST").Body);
        var body = payload.RootElement;
        Assert.Equal(0f, body.GetProperty("temperature").GetSingle());
        Assert.False(body.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.True(body.TryGetProperty("max_completion_tokens", out var limit) || body.TryGetProperty("max_tokens", out limit));
        Assert.Equal(4096, limit.GetInt32());
        Assert.Equal(1, body.GetProperty("messages").GetArrayLength());
        Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
        var content = body.GetProperty("messages")[0].GetProperty("content");
        var prompt = content.ValueKind == JsonValueKind.String ? content.GetString()
            : string.Concat(content.EnumerateArray().Select(p => p.GetProperty("text").GetString()));
        Assert.Equal(request.SubmittedPrompt, prompt);
        Assert.False(body.TryGetProperty("profileId", out _));
        var result = await f.Jobs.ReadArtifactAsync<ContentProbeResult>(submission.Id, AiJobArtifact.Result, f.Ct);
        Assert.Equal("reported/model", result!.ReturnedModel);
        Assert.Equal(apiRefusal ? ContentProbeOutcome.Refused : ContentProbeOutcome.ResponseReceived, ContentProbePolicy.Outcome(request, result));
        if (apiRefusal) Assert.Contains("decline", result.ApiRefusal);
        await handler.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, f.Ct);
        Assert.Single(http.Requests, r => r.Method == "POST");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalProfileOverridesReachDurableWorkflowAndReportedCeilingsAreInconclusive(bool reachedLimit)
    {
        using var f = new ContentProbeFixture();
        var model = new TextModelReference(AiBackend.ComfyUI, "local.safetensors", "Local profile", "http://comfy.test:8188")
        { ProfileId = Guid.NewGuid(), Temperature = .9f, MaxOutputTokens = 512 };
        f.Settings.Value = f.Settings.Value with { ComfyUrl = model.ComfyUrl!, ComfyTextModelVerifications = [new(model.ComfyUrl!, "test-v1", model.Model, DateTimeOffset.UtcNow)],
            ComfyTextModels = new() { [TextModelPolicy.Key(model)] = new(100, .2f) } };
        f.Providers.Models = [new(model.Model, model.Name)];
        var test = ContentProbeBuiltIns.All(new()).First();
        var plan = await f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [test.Id], [model], 1, 0, 0, f.Ct);
        var submission = Assert.Single(plan); var context = await f.ClaimAsync(submission);
        var promptId = Guid.NewGuid();
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { prompt_id = promptId }), Encoding.UTF8, "application/json") }));
        var handler = new ContentProbeJobHandler(f.Providers, new TestHttpFactory(http), new ComfyJobExecution(new ProbeMonitor(reachedLimit)), TimeProvider.System);
        await handler.ExecuteAsync(context, submission.Snapshot, f.Ct);
        using var body = JsonDocument.Parse(Assert.Single(http.Requests).Body);
        var inputs = body.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs");
        Assert.Equal(512, inputs.GetProperty("max_length").GetInt32());
        Assert.Equal(.9f, inputs.GetProperty("sampling_mode.temperature").GetSingle());
        Assert.False(inputs.GetProperty("thinking").GetBoolean());
        Assert.Contains(ContentProbePolicy.Prompt(test), inputs.GetProperty("prompt").GetString());
        var result = await f.Jobs.ReadArtifactAsync<ContentProbeResult>(submission.Id, AiJobArtifact.Result, f.Ct);
        Assert.Equal(reachedLimit ? "length" : "stop", result!.FinishReason);
        Assert.False((await context.ExecutionAsync(f.Ct)).MayBeRunning);
        await handler.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, f.Ct);
        Assert.Single(http.Requests); Assert.Equal(0, f.Providers.Creates);
    }
    [Fact]
    public async Task CodexProfileUsesCapturedModelDefaultInsteadOfLaterGlobalEffort()
    {
        using var f = new ContentProbeFixture();
        var mock = new Lumibelle.Testing.MockCodexTransport { Text = "A scene." };
        await using var codex = new CodexClient(mock, TimeProvider.System);
        var model = new TextModelReference(AiBackend.Codex, "mock-codex", "Model default") { ProfileId = Guid.NewGuid() };
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, TextEffort = "low" } };
        var connection = await codex.CheckAsync(f.Settings.Value.Codex, f.Ct);
        f.Providers.Models = connection.Models;
        var capture = new ContentProbeCapture(f.Settings, f.Probes, f.Providers, codex);
        var test = ContentProbeBuiltIns.All(new()).First();
        var plan = await capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [test.Id], [model], 1, 0, 0, f.Ct);
        var submission = Assert.Single(plan); var request = submission.Snapshot.Deserialize<ContentProbeRequest>(AtomicJsonFile.Options)!;
        Assert.Null(request.Model.ReasoningEffort);
        Assert.Equal(connection.Models.Single(m => m.Id == model.Model).DefaultEffort, request.Codex!.Effort);
        f.Settings.Value = f.Settings.Value with { Codex = f.Settings.Value.Codex with { TextEffort = "high" } };
        var handler = new ContentProbeJobHandler(f.Providers, new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException())),
            new ComfyJobExecution(TestComfy.Monitor()), TimeProvider.System, codex);
        var context = await f.ClaimAsync(submission);
        await handler.ExecuteAsync(context, submission.Snapshot, f.Ct);
        Assert.Equal(request.Codex.Effort, Assert.Single(mock.Inputs).GetProperty("effort").GetString());
        Assert.Equal(1, mock.Turns); Assert.Equal(0, f.Providers.Creates);
    }
    private sealed class ProbeMonitor(bool reachedLimit) : IComfyExecutionMonitor
    {
        public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory, ComfyExecutionOptions options,
            CancellationToken operationToken, CancellationToken callerToken) => throw new InvalidOperationException("The durable executor owns submission");
        public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId, ComfyExecutionOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return new(new(GenerationPhase.Generating, "Generating", reachedLimit ? 512 : 12, 512, "tokens"), promptId);
            await Task.Yield();
            var job = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> { ["3"] = new { text = new[] { "Local scene." } } },
                status = new { completed = true, status_str = "success" } });
            yield return new(new(GenerationPhase.Completed, "Completed"), promptId, job, true);
        }
    }
}
