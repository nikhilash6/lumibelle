using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

internal sealed class ContentProbeFixture : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "Lumibelle.ProbeTests", Guid.NewGuid().ToString("N"));
    internal readonly FakeAiSettingsStore Settings = new() { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
    internal readonly ProbeProviders Providers = new();
    internal FileAiJobStore Jobs { get; }
    internal FileContentProbeStore Probes { get; }
    internal ContentProbeCapture Capture => new(Settings, Probes, Providers);
    internal CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static TextModelReference Profile(string name = "Careful") => new(AiBackend.OpenRouter, "test/model", name)
    { ProfileId = Guid.NewGuid(), Temperature = .35f, ReasoningEffort = "high", MaxOutputTokens = 4096 };
    internal static ContentProbe Test(string name = "Dialogue") => new() { Name = name, Prompt = "Write a short dialogue.", SuccessCriteria = "A dialogue, not advice." };
    internal static ContentProbeRequest Request(ContentProbe? probe = null, TextModelReference? model = null, AiSettings? settings = null)
    {
        probe ??= Test(); model ??= Profile(); settings ??= new() { HasOpenRouterKey = true };
        return new(1, Guid.NewGuid(), Guid.NewGuid(), 1, probe, ContentProbePolicy.Prompt(probe), model, settings, 12345);
    }
    internal static AiJobHeader Header(ContentProbeRequest request, AiJobState state = AiJobState.Completed) => new()
    {
        Id = request.JobId, Kind = AiJobKind.ContentProbe, Backend = request.Model.Backend,
        Target = new(ModelKey: request.JobId.ToString("N")), ProjectName = "Text model tests", TargetName = request.Probe.Name,
        OriginTabId = Guid.NewGuid(), RequestFingerprint = new('A', 64), CreatedUtc = DateTimeOffset.UtcNow,
        State = state, LeaseId = state == AiJobState.Running ? Guid.NewGuid() : null,
        StartedUtc = state == AiJobState.Running ? DateTimeOffset.UtcNow : null
    };
    internal ContentProbeFixture()
    {
        Jobs = new(Path.Combine(Root, "ai-jobs"), TimeProvider.System);
        Probes = new(new ApplicationPaths(Root), Jobs, TimeProvider.System);
    }
    internal AiJobContext Context(AiJobHeader header, bool recovering = false) => new(header, recovering, Jobs,
        TimeProvider.System, (_, _) => { }, _ => { }, Ct);
    internal async Task<AiJobContext> ClaimAsync(AiJobSubmission submission)
    {
        await Jobs.EnqueueAsync(submission, Ct);
        var header = await Jobs.ClaimNextAsync(submission.Backend, 1, Ct);
        Assert.NotNull(header); Assert.Equal(submission.Id, header.Id);
        return Context(header);
    }
    internal async Task<ContentProbeRow> CompletedAsync(ContentProbeRequest? request = null, ContentProbeResult? result = null)
    {
        request ??= Request(); result ??= new("A complete dialogue.", true, "stop");
        var context = await ClaimAsync(ContentProbeCapture.Submission(request, Guid.NewGuid()));
        await context.SaveResultAsync(result);
        var job = await Jobs.UpdateAsync(request.JobId, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, Ct);
        return new(job, request, result, await Probes.LoadReviewAsync(job.Id, Ct));
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

internal sealed class ProbeProviders : IAiProviderRegistry
{
    internal readonly ProbeChat Chat = new();
    internal int Creates, Checks;
    internal IReadOnlyList<AiModel> Models = [new("test/model", "Test model", Catalog: new(SupportsReasoning: true)
    { SupportedParameters = ["temperature", "reasoning", "max_tokens"], SupportedReasoningEfforts = ["high", "low"], ReasoningMandatory = false })];
    internal bool FailChecks;
    public Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default)
    { Creates++; return Task.FromResult<IChatClient>(Chat); }
    public Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
    { Checks++; if (FailChecks) throw new InvalidOperationException("No catalog access expected"); return Task.FromResult(new AiConnectionCheck(true, "Available", Models, "test-v1")); }
    public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe runs must not benchmark or clear caches");
    public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings, ComfyTextModelTestRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe runs must not use the model benchmark path");
}
internal sealed class ProbeChat : IChatClient
{
    internal string Text = "A short dialogue.";
    internal string? Finish = "stop";
    internal bool FailAfterText;
    internal readonly List<(ChatMessage[] Messages, ChatOptions? Options)> Calls = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToArray(), options)); cancellationToken.ThrowIfCancellationRequested();
        yield return new(ChatRole.Assistant, Text) { ModelId = "returned/model", ResponseId = "response-id" };
        await Task.Yield();
        if (FailAfterText) throw new AiGenerationException("Simulated interrupted stream");
        if (Finish is not null) yield return new(ChatRole.Assistant, "") { FinishReason = new ChatFinishReason(Finish) };
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
