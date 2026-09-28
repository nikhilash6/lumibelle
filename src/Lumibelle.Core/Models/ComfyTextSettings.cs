namespace lumibelle.Models;

// Explicit compatibility declaration, not a filename guess or a text benchmark result.
public enum ComfyVisionInput { Disabled, SingleImage, ImageBatch }

public sealed record ComfyTextModelSettings(int MaxOutputTokens, float Temperature)
{
    // Default omission keeps legacy settings and request snapshots unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ComfyVisionInput VisionInput { get; init; }
}
