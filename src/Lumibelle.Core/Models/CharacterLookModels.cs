namespace lumibelle.Models;

public sealed record CharacterLook
{
    public IReadOnlyList<PreferredImageReference> PreferredAppearanceReferences { get; init; } = [];
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string PreservationGuidance { get; init; } = "";
    public bool Archived { get; init; }
    public IReadOnlyList<AssetSourceEvidence> Evidence { get; init; } = [];
    public CharacterLook Copy() => this with { Evidence = Evidence.ToArray(), PreferredAppearanceReferences = PreferredAppearanceReferences.ToArray() };
}

// Captured generation context is independent of an image's current library classification.
public sealed record AssetLookContext(Guid AssetId, string AssetName, string IdentityNotes, string IdentityGuidance,
    Guid? LookId, string LookName, string Description, string PreservationGuidance);

public sealed record LookExtractionProposal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PreservationGuidance { get; set; } = "";
    public Guid? MatchedLookId { get; set; }
    public ExtractionDecision Decision { get; set; }
    public List<AssetSourceEvidence> Evidence { get; init; } = [];
}

public sealed record ShotAppearance(Guid AssetId, Guid LookId, Guid? EndLookId = null);
public enum ShotReferencePurpose { Identity, CurrentLook, StartingLook, EndingLook }
public sealed record ShotAppearanceContext(Guid CharacterId, AssetLookContext Start, AssetLookContext? End);
