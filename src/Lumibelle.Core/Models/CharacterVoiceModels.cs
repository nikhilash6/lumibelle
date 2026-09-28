namespace lumibelle.Models;

public sealed record ReelVoiceSource(Guid ReelId, Guid MediaId, string Sha256, string Name);
public enum CharacterVoiceSource { Unselected, None, Recording, Reel }

// The selected recording and excerpt are a snapshot, including when chosen from a default.
// Legacy setups have no entries and keep their existing voice/soundtrack inputs.
public sealed record CharacterVoiceSelection
{
    public int Version { get; init; } = 1;
    public Guid AssetId { get; set; }
    public string CharacterName { get; set; } = "";
    public CharacterVoiceSource Source { get; set; }
    public bool FromDefault { get; set; }
    public string SourceName { get; set; } = "";
    public string Speaker { get; set; } = "";
    public bool SpeakerConfirmed { get; set; }
    public ShotVoiceBinding? Recording { get; set; }
    public Guid? ReelBindingId { get; set; }
    public Guid? ReelMediaId { get; set; }
    public ReelAudioExcerpt? Excerpt { get; set; }
}
