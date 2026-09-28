using lumibelle.Models;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Production;

public sealed record ReelFrameCandidate(int Index, double Seconds, double Sharpness, double Contrast, double Luminance, float[] Appearance);
public static class ReelFrameSelector
{
    public const string Version = "diversity-v1";
    public static IReadOnlyList<int> Sample(IReadOnlyList<double> times)
    {
        List<int> result = []; double next = 0;
        for (var i = 0; i < times.Count; i++) if (times[i] + .00001 >= next) { result.Add(i); next = (Math.Floor(times[i] * 8 + .00001) + 1) / 8; }
        if (result.Count > 128) throw new WorkspaceStoreException("Too many reel analysis samples.");
        return result;
    }
    public static ReelFrameCandidate Describe(int index, double seconds, Stream png)
    {
        using var source = Image.Load<Rgb24>(png);
        return Describe(index, seconds, source);
    }
    public static ReelFrameCandidate Describe(int index, double seconds, Image source)
    {
        using var image = source.CloneAs<Rgb24>();
        image.Mutate(x => x.Resize(96, 64));
        var luma = new double[96 * 64]; double sum = 0;
        for (var y = 0; y < 64; y++) for (var x = 0; x < 96; x++)
        { var p = image[x, y]; sum += luma[y * 96 + x] = (.2126 * p.R + .7152 * p.G + .0722 * p.B) / 255; }
        var mean = sum / luma.Length; var contrast = Math.Sqrt(luma.Average(v => (v - mean) * (v - mean)));
        double sharp = 0;
        for (var y = 1; y < 63; y++) for (var x = 1; x < 95; x++)
        { var i = y * 96 + x; var lap = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - 96] - luma[i + 96]; sharp += lap * lap; }
        image.Mutate(x => x.Resize(24, 16)); var appearance = new float[24 * 16 * 3]; var n = 0;
        for (var y = 0; y < 16; y++) for (var x = 0; x < 24; x++)
        { var p = image[x, y]; appearance[n++] = p.R / 255f; appearance[n++] = p.G / 255f; appearance[n++] = p.B / 255f; }
        return new(index, seconds, sharp / (94 * 62), contrast, mean, appearance);
    }
    public static double Difference(ReelFrameCandidate a, ReelFrameCandidate b) => a.Appearance.Zip(b.Appearance, (x, y) => Math.Abs(x - y)).Average();
    public static IReadOnlyList<ReelFrameCandidate> Select(IReadOnlyList<ReelFrameCandidate> candidates, int count)
    {
        if (count is < 1 or > 9) throw new WorkspaceStoreException("Choose between one and nine keyframes.");
        if (candidates.Count == 0) return [];
        var ordered = candidates.OrderBy(c => c.Seconds).ThenBy(c => c.Index).ToArray();
        var usable = ordered.Where(c => c.Contrast >= .015 && c.Luminance is > .025 and < .975).ToArray();
        if (usable.Length == 0) return []; // Blank clips have no useful suggestion; manual picks remain possible.
        var maxSharp = Math.Max(.00001, usable.Max(c => c.Sharpness));
        double Quality(ReelFrameCandidate c)
        {
            var i = Array.IndexOf(ordered, c); var value = Math.Sqrt(c.Sharpness / maxSharp);
            if (i > 0 && i < ordered.Length - 1 && Difference(c, ordered[i - 1]) > .18 && Difference(c, ordered[i + 1]) > .18 && Difference(ordered[i - 1], ordered[i + 1]) < .06) return 0;
            // A dimmer, softer copy of the same composition is usually a fade, not a new view.
            if (usable.Any(other => other.Contrast > c.Contrast * 1.7 && other.Sharpness > c.Sharpness * 2 && NormalizedDifference(c, other) < .12)) value *= .05;
            if (i > 0 && i < ordered.Length - 1 && c.Contrast < Math.Min(ordered[i - 1].Contrast, ordered[i + 1].Contrast) * .8 &&
                Difference(c, ordered[i - 1]) > .08 && Difference(c, ordered[i + 1]) > .08) value *= .05;
            if (i > 0 && Difference(c, ordered[i - 1]) > .25) value *= .4;
            return value;
        }
        var pool = usable.Where(c => Quality(c) > .1).ToList();
        if (pool.Count == 0) pool = usable.ToList();
        List<ReelFrameCandidate> selected = [pool.OrderByDescending(Quality).ThenBy(c => c.Seconds).ThenBy(c => c.Index).First()];
        var span = Math.Max(.125, ordered[^1].Seconds - ordered[0].Seconds);
        while (selected.Count < count)
        {
            var remaining = pool.Where(c => !selected.Contains(c)).Select(c => new { Frame = c, Novelty = selected.Min(s => Difference(c, s)), Coverage = selected.Min(s => Math.Abs(c.Seconds - s.Seconds)) / span }).Where(c => c.Novelty >= .055).ToArray();
            if (remaining.Length == 0) break;
            selected.Add(remaining.OrderByDescending(c => .75 * c.Novelty + .15 * c.Coverage + .1 * Quality(c.Frame)).ThenBy(c => c.Frame.Seconds).ThenBy(c => c.Frame.Index).First().Frame);
        }
        return selected.OrderBy(c => c.Seconds).ThenBy(c => c.Index).ToArray();
    }
    private static double NormalizedDifference(ReelFrameCandidate a, ReelFrameCandidate b)
    {
        var am = a.Appearance.Average(); var bm = b.Appearance.Average();
        var ad = Math.Sqrt(a.Appearance.Average(v => Math.Pow(v - am, 2))); var bd = Math.Sqrt(b.Appearance.Average(v => Math.Pow(v - bm, 2)));
        if (ad < .001 || bd < .001) return 1;
        return a.Appearance.Zip(b.Appearance, (x, y) => Math.Abs((x - am) / ad - (y - bm) / bd)).Average();
    }
}
