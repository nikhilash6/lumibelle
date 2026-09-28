namespace lumibelle.Models;

public enum RegionalEditMode { Protect, Edit }
public sealed record MaskPoint(double X, double Y);
// Coordinates and brush diameter are fractions of the oriented source dimensions.
public sealed record MaskStroke(bool Protect, bool Erase, bool Rectangle, double Size, IReadOnlyList<MaskPoint> Points);
public sealed record RegionalImageSelection
{
    public int Version { get; init; } = 1;
    public required AssetImageReference Source { get; init; }
    public required string SourceHash { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public RegionalEditMode Mode { get; init; }
    public ImageCropRegion Context { get; init; } = new() { X = 0, Y = 0, Width = 1, Height = 1 };
    public IReadOnlyList<MaskStroke> Strokes { get; init; } = [];
    public RegionalImageSelection Copy() => this with { Context = Context with { },
        Strokes = Strokes.Select(s => s with { Points = s.Points.ToArray() }).ToArray() };
}
public sealed record RegionalCanvas(int X, int Y, int Width, int Height, int CanvasWidth, int CanvasHeight);
public sealed record RegionalImageCapture(byte[] OriginalPng, RegionalImageSelection Selection, RegionalCanvas Canvas);
// Local review adjustments only: these never enter a provider input bundle.
public sealed record RegionalColourSettings
{
    public int Version { get; init; } = 1;
    public bool MatchOriginal { get; init; }
    public int Strength { get; init; } = 100;
    public int Brightness { get; init; }
    public int Warmth { get; init; }
    public int Tint { get; init; }
    public ImageCropRegion? MatchArea { get; init; }
}
public sealed record RegionalImageProvenance(RegionalImageSelection Selection, RegionalCanvas Canvas, int EdgeBlend,
    RegionalColourSettings? Colour = null, bool ColourMatched = false);
public sealed record RegionalReviewState(bool Discarded = false, bool Saved = false, int? SaveBlend = null,
    RegionalColourSettings? SaveColour = null, int? DraftBlend = null, RegionalColourSettings? DraftColour = null);
