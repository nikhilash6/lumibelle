namespace lumibelle.Models;

public enum TakeRefinementMode { Refine, Rework }
public sealed record H3RefinementPackage(Guid Id, long Bytes, string Sha256, int Width, int Height, int FrameCount)
{
    public const string FileName = "refinement.safetensors";
}
// Source context stays in VideoSnapshot; these are the second pass's settings only.
public sealed record TakeRefinement(Guid ParentTakeId, H3RefinementPackage SourcePackage,
    TakeRefinementMode Mode, int Width, int Height, string Upscaler, long SourceVideoBytes, string SourceVideoSha256)
{
    public string Profile { get; init; } = "h3-refinement-experimental-v1";
    public string UpscalerRevision { get; init; } = "d7c01b9011f2e8439493f6c02c29995a27df276f";
    public int Steps { get; init; } = 20;
    public double Denoise => Mode == TakeRefinementMode.Refine ? .35 : .65;
}
public sealed record RefinementSize(string Name, int Width, int Height, bool Experimental, string? Issue = null)
{
    public long Pixels => (long)Width * Height;
}
