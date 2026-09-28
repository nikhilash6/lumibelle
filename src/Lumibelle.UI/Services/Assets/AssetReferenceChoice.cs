using lumibelle.Models;

namespace lumibelle.Services.Assets;

// Browser selections only. Setup bindings retain the existing persisted formats.
public abstract record AssetReferenceChoice(ReferenceAsset Asset)
{
    public sealed record Image(ReferenceAsset Owner, AssetImage Media) : AssetReferenceChoice(Owner);
    public sealed record Reel(ReferenceAsset Owner, AssetReferenceReel Media) : AssetReferenceChoice(Owner);
    public Guid Id => this switch { Image i => i.Media.Id, Reel r => r.Media.Id, _ => throw new InvalidOperationException() };
    public Guid? LookId => this switch { Image i => i.Media.LookId, Reel r => r.Media.LookId, _ => null };
    public DateTimeOffset CreatedUtc => this switch { Image i => i.Media.CreatedUtc, Reel r => r.Media.CreatedUtc, _ => default };
}

public sealed record ReferenceSelection(Shot Inputs, IReadOnlyDictionary<Guid, Guid> ReelSources)
{
    // Visit-local assistance guards, not new persisted reference identities.
    public string? AssistanceCatalogueFingerprint { get; init; }
    public string? AssistanceDirectionFingerprint { get; init; }
    // Reels put in place of another reel of the same asset, keeping the attachment's settings.
    public IReadOnlyList<ReelSwap> Swaps { get; init; } = [];
}
public sealed record ReelSwap(Guid From, Guid To, bool OtherShots);
