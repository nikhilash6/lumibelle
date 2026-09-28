using System.Numerics;
using lumibelle.Models;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace lumibelle.Services.Assets;

public static partial class RegionalImageEdits
{
    public static void ValidateColour(RegionalColourSettings? colour)
    {
        if (colour is null) return;
        if (colour.Version != 1 || colour.Strength is < 0 or > 100 || colour.Brightness is < -50 or > 50 ||
            colour.Warmth is < -50 or > 50 || colour.Tint is < -50 or > 50)
            throw new AiGenerationException("The saved colour adjustments are invalid.");
        if (colour.MatchArea is not null) ComfyReferenceImageEditor.ValidateCrop(colour.MatchArea);
    }

    private sealed record ColourMatch(Vector3 Offset, string? Message = null, bool Matched = false);

    private static ColourMatch MatchColour(Image<Rgba32> original, Image<Rgba32> edited, Rectangle crop,
        bool[] edit, bool[] protect, RegionalColourSettings? settings, int fringe, CancellationToken ct)
    {
        if (settings?.MatchOriginal != true) return new(Vector3.Zero);
        if (fringe > 16) return new(Vector3.Zero, "The model result is too small for a reliable colour match. Use the manual adjustments.");
        var chosen = settings.MatchArea is { } area ? ImageGeometry.CropPixels(original.Width, original.Height, area) : (Rectangle?)null;
        var samples = new List<Vector3>();
        // Bounded, deterministic sampling in source coordinates, after the exact output mapping.
        // Never sample padding, transparent/clipped pixels, or hidden pixels and their resampling fringe.
        var bounds = chosen is { } box ? Rectangle.Intersect(crop, box) : crop;
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(bounds.Width * (double)bounds.Height / 65536)));
        fringe = Math.Max(1, fringe);
        for (var y = bounds.Top; y < bounds.Bottom; y += stride)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = bounds.Left; x < bounds.Right; x += stride)
            {
                // With no author-selected area, only untouched context is a colour reference.
                // A chosen area may be inside the edit but must have remained visually unchanged.
                if (chosen is null && edit[y * original.Width + x]) continue;
                var excluded = false;
                for (var dy = -fringe; dy <= fringe && !excluded; dy++)
                    for (var dx = -fringe; dx <= fringe; dx++)
                    {
                        var px = x + dx; var py = y + dy;
                        if (px < crop.Left || px >= crop.Right || py < crop.Top || py >= crop.Bottom ||
                            protect[py * original.Width + px] || chosen is null && edit[py * original.Width + px])
                        { excluded = true; break; }
                    }
                if (excluded) continue;
                var a = original[x, y]; var b = edited[x - crop.X, y - crop.Y];
                static bool Useful(Rgba32 p) => p.A == 255 && p.R is > 4 and < 251 && p.G is > 4 and < 251 && p.B is > 4 and < 251;
                if (!Useful(a) || !Useful(b)) continue;
                samples.Add(new(a.R - b.R, a.G - b.G, a.B - b.B));
            }
        }
        var fallback = "No reliable colour match. Choose an area that stayed the same in both images, or use the manual adjustments.";
        if (samples.Count < 16) return new(Vector3.Zero, fallback);
        static float Median(IEnumerable<float> values)
        { var sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
        var offset = new Vector3(Median(samples.Select(p => p.X)), Median(samples.Select(p => p.Y)), Median(samples.Select(p => p.Z)));
        // Reject inconsistent correspondences instead of matching the overall image histogram:
        // that could counteract an intentional change of clothing, hair or background.
        var inliers = samples.Count(p => Vector3.Abs(p - offset) is var d && Math.Max(d.X, Math.Max(d.Y, d.Z)) <= 12);
        if (inliers < 16 || inliers < samples.Count * .6 || Math.Max(Math.Abs(offset.X), Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z))) > 64)
            return new(Vector3.Zero, fallback);
        return new(offset, chosen is null ? "Matched using surrounding context. Check the Combined preview." : "Matched using your selected area. Check the Combined preview.", true);
    }

    private static Vector3 ColourOffset(RegionalColourSettings? settings, Vector3 automatic) => settings is null ? Vector3.Zero :
        automatic * (settings.Strength / 100f) + new Vector3(settings.Brightness * 2.55f) +
            new Vector3(settings.Warmth, 0, -settings.Warmth) + new Vector3(settings.Tint * .5f, -settings.Tint, settings.Tint * .5f);

    private static Rgba32 CorrectColour(Rgba32 pixel, Vector3 offset)
    {
        if (offset == Vector3.Zero) return pixel;
        static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
        return new(Channel(pixel.R + offset.X), Channel(pixel.G + offset.Y), Channel(pixel.B + offset.Z), pixel.A);
    }
}
