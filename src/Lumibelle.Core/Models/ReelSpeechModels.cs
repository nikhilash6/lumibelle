namespace lumibelle.Models;

public enum ReelSpeechPreset { CustomText, ConversationalRange }
public enum ReelSpeechLength { Automatic, Short, Medium, Long, Extended }

// Optional on the recipe. The exact Language and Line remain authoritative, including
// on retries and regeneration; catalogue suggestions never replace them implicitly.
public sealed record ReelSpeechSettings
{
    public int CatalogueVersion { get; init; } = 1;
    public ReelSpeechPreset Preset { get; init; } = ReelSpeechPreset.ConversationalRange;
    public ReelSpeechLength Length { get; init; } = ReelSpeechLength.Automatic;
}
