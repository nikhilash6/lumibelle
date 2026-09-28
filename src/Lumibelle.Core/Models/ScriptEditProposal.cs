namespace lumibelle.Models;

public sealed record ScriptEditProposal(int Version, List<ScriptEditOperation> Operations)
{
    // Derived review metadata. Omitted for untouched historical proposals; Parse never
    // accepts model-supplied notes as evidence that an operation was normalized.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NormalizationNotes { get; init; }
}
public sealed record ScriptEditOperation(string Kind, Guid? StartId = null, Guid? EndId = null,
    int? StartOffset = null, int? EndOffset = null, Guid? AnchorId = null, string? Side = null, List<ScriptBlock>? Blocks = null);
