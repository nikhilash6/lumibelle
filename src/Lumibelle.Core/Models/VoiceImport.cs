namespace lumibelle.Models;

public sealed record VoiceImportDraft(Guid Id, Guid AssetId, string FileName, string ContentType, string SuggestedName,
    double Duration, DateTimeOffset CreatedUtc)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelVoiceSource? SourceReel { get; init; }
}
public sealed record VoiceImportReceipt(Guid Id, string Fingerprint);
