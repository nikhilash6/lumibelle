namespace lumibelle.Models;

public enum H3AttentionBackend { ServerDefault, PyTorch, Kitchen, Sage }
public enum H3ArchiveCompression { Fast, Compact }

public sealed record H3PerformancePreferences
{
    public H3AttentionBackend Attention { get; set; }
    public bool SolAttention { get; set; }
    public H3ArchiveCompression ArchiveCompression { get; set; } = H3ArchiveCompression.Fast;
}

// Explicitly captured for new batches. A missing profile means the original graph,
// regardless of defaults introduced later in H3Settings.
public sealed record H3PerformanceProfile(
    string Version, H3AttentionBackend Attention, string Adapter, bool SolAttention,
    double Tau, double StartPercent, double EndPercent, int MinimumTokens, int ExtraTokens,
    string DenseBlocks, string SinkConditioning, string ArchiveMethod, int ArchiveQuality);

public sealed record H3PerformanceCapabilities
{
    public string? PyTorchIssue { get; init; } = "Refresh video models to check PyTorch attention.";
    public string? KitchenIssue { get; init; } = "Refresh video models to check Comfy Kitchen attention.";
    public string? SageIssue { get; init; } = "Refresh video models to check the optional KJNodes Sage patch.";
    public string? SolIssue { get; init; } = "Refresh video models to check native Sol-Attn.";
    public string? FastArchiveIssue { get; init; } = "Refresh video models to check fast lossless compression.";
}

public sealed record VideoTakeTimings
{
    public double? PreparationSeconds { get; init; }
    public double? SamplingSeconds { get; init; }
    public double? UpscalingSeconds { get; init; }
    public double? DecodingSeconds { get; init; }
    public double? ArchiveSeconds { get; init; }
    public double? TransferSaveSeconds { get; init; }
    public bool PartialObservation { get; init; }
}
