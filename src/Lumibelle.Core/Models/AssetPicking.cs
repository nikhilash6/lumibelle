namespace lumibelle.Models;

// This is a text-only catalogue, not an upload manifest. IDs are resolved by the
// application; a model never supplies storage paths, media bytes or new identities.
public sealed record AssetPickLook(Guid Id, string Name, string Description, string Guidance);
public sealed record AssetPickOwner(Guid Id, string Name, AssetCategory Category, string Description,
    string Guidance, IReadOnlyList<AssetPickLook> Looks, Guid? DefaultVoiceId);
public sealed record AssetPickCandidate
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required Guid AssetId { get; init; }
    public required Guid SourceId { get; init; }
    public Guid? MediaId { get; init; }
    public required string Name { get; init; }
    public Guid? LookId { get; init; }
    public string Description { get; init; } = "";
    public string Guidance { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Approved { get; init; }
    public bool Cover { get; init; }
    public bool DefaultVoice { get; init; }
    public double Duration { get; init; }
    public double ExcerptStart { get; init; }
    public double ExcerptDuration { get; init; }
    public bool AudioAvailable { get; init; }
    public string VoiceDescription { get; init; } = "";
    public string Language { get; init; } = "";
    public IReadOnlyList<string> VisualModes { get; init; } = [];
    public string? PreferredVisualMode { get; init; }
    public int KeyframeCount { get; init; }
    public required string SourceFingerprint { get; init; }
}
public sealed record AssetPickCatalogue(Guid ProjectId, IReadOnlyList<AssetPickOwner> Assets,
    IReadOnlyList<AssetPickCandidate> Candidates);
public sealed record AssetPickRequest(int Version, Guid ProjectId, Shot Shot, string Prompt,
    string DirectingNotes, string Instructions, bool ReplaceExisting, AssetPickCatalogue Catalogue,
    string ContextFingerprint, string CatalogueFingerprint);
public sealed record AssetPickItem(string CandidateId, string? VisualMode, string? Speaker,
    string UseHint, string Reason);
public sealed record AssetPickResult(IReadOnlyList<AssetPickItem> Selections,
    IReadOnlyList<string> Missing, string Summary);
public sealed record AssetPickDraft(Shot Inputs, IReadOnlyDictionary<Guid, Guid> ReelSources,
    IReadOnlyList<string> Notices);
// A stage operation is still a local reference-editor draft. Only the existing
// Apply changes action saves it, with the editor's media checks and single Undo.
public sealed record AssetPickStage(string ContextFingerprint, string CatalogueFingerprint, AssetPickDraft Draft, bool ReplaceExisting);
