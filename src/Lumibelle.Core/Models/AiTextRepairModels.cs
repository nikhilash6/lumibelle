using System.Text.Json;

namespace lumibelle.Models;

// No binary attachments or original conversation. The original task remains in
// AiTextJobRequest.Task solely for the normal local parser and application guards.
public sealed record AiTextRepair(int Version, Guid SourceJobId, Guid RootJobId,
    string SourceRequestFingerprint, string SourceResponseSha256, string SourceProfile,
    string FailedResponse, string ValidationError, JsonElement Contract)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReviewPredecessorJobId { get; init; }
}

public sealed record AiTextRepairOffer(AiJobHeader Source, string ModelName, string? Issue,
    AiJobHeader? ExistingRepair = null)
{
    public bool IsRepair { get; init; }
}
