namespace lumibelle.Models;

/// <summary>A writing task, not an official content or age rating.</summary>
public sealed record ContentProbe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Revision { get; init; } = 1;
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Custom";
    public string Prompt { get; init; } = "";
    public string SuccessCriteria { get; init; } = "";
    public IReadOnlyList<string> RequiredPhrases { get; init; } = [];
    public bool UseRefusalMarker { get; init; } = true;
    public bool Enabled { get; init; } = true;
}

public sealed record ContentProbeLibrary
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public IReadOnlyList<ContentProbe> Custom { get; init; } = [];
    public IReadOnlyList<Guid> DisabledBuiltIns { get; init; } = [];
}

// Separate from AiTextJobRequest: probes have no project target, creative parser,
// automatic application, or shared conversation. Codex stays at the top level so
// the queue's allowance checks can inspect its captured model.
public sealed record ContentProbeRequest(int Version, Guid JobId, Guid BatchId, int Iteration,
    ContentProbe Probe, string SubmittedPrompt, TextModelReference Model, AiSettings Settings,
    long Seed, CodexCapture? Codex = null);

public enum ContentProbeOutcome { ResponseReceived, Refused, ProviderBlocked, Inconclusive }

public sealed record ContentProbeResult(string Raw, bool Complete = false, string? FinishReason = null,
    string ApiRefusal = "", string? ReturnedModel = null, string? ResponseId = null,
    OpenRouterRequestUsage? OpenRouterUsage = null);

public sealed record ContentProbeReview(Guid JobId, long Revision = 0, int? Score = null,
    string Notes = "", string OutputFingerprint = "", DateTimeOffset? UpdatedUtc = null);

public sealed record ContentProbeRow(AiJobHeader Job, ContentProbeRequest Request,
    ContentProbeResult? Result, ContentProbeReview Review);

public sealed record ContentProbeSummary(string Key, string Test, string Category, long ProbeRevision,
    string Configuration, int Responses, int Rated, double? Average, int Refused, int Inconclusive);
