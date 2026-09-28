using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

namespace lumibelle.Services.Assets;

public sealed record RegionalCompositeResult(byte[] Png, string? ColourMessage, bool ColourMatched);

public static partial class RegionalImageEdits
{
    public const string PlacementInstruction = "Keep the exact input canvas, framing, placement and scale. Opaque gray protection areas and outer canvas padding are placeholders; leave them unchanged. The application restores protected pixels and places the edited region back at its original coordinates.";
    public static readonly Rgba32 Cover = new(128, 128, 128, 255);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static async Task<byte[]> OriginalAsync(Stream source, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<Rgba32>(source, ct);
        image.Mutate(x => x.AutoOrient());
        if ((long)image.Width * image.Height > 40_000_000) throw new AiGenerationException("Regional editing supports images up to 40 million pixels.");
        return await PngAsync(image, ct);
    }
    public static void Validate(RegionalImageSelection selection)
    {
        if (selection.Version != 1 || selection.Source is null || selection.Source.AssetId == Guid.Empty || selection.Source.ImageId == Guid.Empty ||
            selection.SourceHash is null || selection.SourceHash.Length != 64 || !selection.SourceHash.All(Uri.IsHexDigit) ||
            selection.Width < 1 || selection.Height < 1 || (long)selection.Width * selection.Height > 40_000_000 || !Enum.IsDefined(selection.Mode) ||
            selection.Strokes is null || selection.Strokes.Count > 2048)
            throw new AiGenerationException("The saved image selection is invalid. Select the area again.");
        ComfyReferenceImageEditor.ValidateCrop(selection.Context);
        if (selection.Strokes.Sum(s => s?.Points?.Count ?? 0) > 100_000) throw new AiGenerationException("This selection has too many brush points. Simplify it before applying.");
        foreach (var stroke in selection.Strokes)
            if (stroke is null || !double.IsFinite(stroke.Size) || stroke.Size is <= 0 or > 1 || stroke.Points is null || stroke.Points.Count is < 1 or > 8192 ||
                stroke.Rectangle && stroke.Points.Count != 2 || stroke.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X is < 0 or > 1 || p.Y is < 0 or > 1))
                throw new AiGenerationException("A painted selection is invalid.");
    }
    public static (bool[] Edit, bool[] Protect) Masks(RegionalImageSelection selection)
    {
        Validate(selection);
        var w = selection.Width; var h = selection.Height;
        var edit = new bool[w * h]; var protect = new bool[w * h];
        if (selection.Mode == RegionalEditMode.Protect) Array.Fill(edit, true);
        foreach (var stroke in selection.Strokes)
        {
            if (!stroke.Protect && selection.Mode == RegionalEditMode.Protect) continue;
            var mask = stroke.Protect ? protect : edit;
            var points = stroke.Points;
            for (var i = 0; i < (stroke.Rectangle ? 1 : points.Count); i++)
            {
                var a = points[i]; var b = points[stroke.Rectangle ? 1 : Math.Min(i + 1, points.Count - 1)];
                var ax = a.X * w; var ay = a.Y * h; var bx = b.X * w; var by = b.Y * h;
                var radius = stroke.Rectangle ? 0 : stroke.Size * Math.Min(w, h) / 2;
                var left = Math.Clamp((int)Math.Floor(Math.Min(ax, bx) - radius), 0, w - 1);
                var right = Math.Clamp((int)Math.Ceiling(Math.Max(ax, bx) + radius), 0, w);
                var top = Math.Clamp((int)Math.Floor(Math.Min(ay, by) - radius), 0, h - 1);
                var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(ay, by) + radius), 0, h);
                for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
                {
                    var length = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
                    var t = length == 0 ? 0 : Math.Clamp(((x + .5 - ax) * (bx - ax) + (y + .5 - ay) * (by - ay)) / length, 0, 1);
                    var dx = x + .5 - ax - t * (bx - ax); var dy = y + .5 - ay - t * (by - ay);
                    if (stroke.Rectangle || dx * dx + dy * dy <= radius * radius) mask[y * w + x] = !stroke.Erase;
                }
            }
        }
        var crop = ImageGeometry.CropPixels(w, h, selection.Context);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            edit[y * w + x] &= crop.Contains(x, y) && !protect[y * w + x];
        return (edit, protect);
    }
    public static ImageCropRegion CropToSelection(RegionalImageSelection selection)
    {
        Validate(selection);
        if (selection.Mode != RegionalEditMode.Edit) throw new AiGenerationException("Choose Edit an area before cropping to its selection.");
        // Fit the authored blue selection, including its protected parts, independently
        // of the previous input crop. Erased edit pixels no longer contribute bounds.
        var (edit, _) = Masks(selection with { Context = new() { X = 0, Y = 0, Width = 1, Height = 1 },
            Strokes = selection.Strokes.Where(s => !s.Protect).ToArray() });
        var left = selection.Width; var top = selection.Height; var right = 0; var bottom = 0;
        for (var y = 0; y < selection.Height; y++) for (var x = 0; x < selection.Width; x++)
        {
            if (!edit[y * selection.Width + x]) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
        }
        if (right <= left || bottom <= top) throw new AiGenerationException("Select the pixels to edit first, then crop the input to that selection.");
        // CropPixels rounds outward. Keep floating-point round trips on the inner
        // side of each exact pixel boundary so the shortcut cannot gain a row/column.
        static (double Position, double Length) Axis(int start, int end, int size)
        {
            var position = start / (double)size;
            while (Math.Floor(position * size) < start) position = Math.BitIncrement(position);
            var length = (end - start) / (double)size;
            while (Math.Ceiling((position + length) * size) > end) length = Math.BitDecrement(length);
            return (position, length);
        }
        var horizontal = Axis(left, right, selection.Width); var vertical = Axis(top, bottom, selection.Height);
        return new() { X = horizontal.Position, Y = vertical.Position, Width = horizontal.Length, Height = vertical.Length };
    }
    public static RegionalCanvas Canvas(RegionalImageSelection selection, string aspect, int resolution = 1024, QwenImage21Options? qwen = null)
    {
        Validate(selection);
        var crop = ImageGeometry.CropPixels(selection.Width, selection.Height, selection.Context);
        if (qwen is not null) QwenImage21Policy.ValidateOptions(qwen, true);
        var size = qwen is null ? ImageAspectPolicy.Size(aspect, crop.Width, crop.Height, resolution)
            : QwenImage21Policy.EditSize(aspect, crop.Width, crop.Height, qwen);
        var scale = Math.Min(size.Width / (double)crop.Width, size.Height / (double)crop.Height);
        var w = Math.Max(1, (int)Math.Round(crop.Width * scale)); var h = Math.Max(1, (int)Math.Round(crop.Height * scale));
        return new((size.Width - w) / 2, (size.Height - h) / 2, w, h, size.Width, size.Height);
    }
    public static async Task<(byte[] Png, RegionalImageCapture Capture)> PrepareAsync(Stream source, RegionalImageSelection selection, string aspect, CancellationToken ct = default, int resolution = 1024, QwenImage21Options? qwen = null)
    {
        var original = await OriginalAsync(source, ct);
        if (Hash(original) != selection.SourceHash) throw new AiGenerationException("The source image changed. Select the area again.");
        using var image = Image.Load<Rgba32>(original);
        Validate(selection);
        if (image.Width != selection.Width || image.Height != selection.Height) throw new AiGenerationException("The selection dimensions no longer match the source.");
        var (_, protect) = Masks(selection);
        image.ProcessPixelRows(rows => { for (var y = 0; y < rows.Height; y++) { var row = rows.GetRowSpan(y); for (var x = 0; x < row.Length; x++) if (protect[y * image.Width + x]) row[x] = Cover; } });
        image.Mutate(x => x.Crop(ImageGeometry.CropPixels(image.Width, image.Height, selection.Context)));
        var map = Canvas(selection, aspect, resolution, qwen);
        image.Mutate(x => x.Resize(map.Width, map.Height));
        using var canvas = new Image<Rgba32>(map.CanvasWidth, map.CanvasHeight, Cover);
        canvas.Mutate(x => x.DrawImage(image, new Point(map.X, map.Y), 1));
        return (await PngAsync(canvas, ct), new(original, selection, map));
    }
    public static async Task<(byte[] Png, RegionalImageCapture Capture)> PrepareBaseAsync(Stream source, AssetImageReference reference,
        RegionalImageSelection? selection, ImageCropRegion? crop, string aspect, CancellationToken ct = default, int resolution = 1024, QwenImage21Options? qwen = null)
    {
        if (selection is not null) return await PrepareAsync(source, selection, aspect, ct, resolution, qwen);
        // Protecting only an additional reference still uses a reviewable, fixed-placement base.
        var original = await OriginalAsync(source, ct);
        var info = ImageInspector.Inspect(original);
        selection = new() { Source = reference, SourceHash = Hash(original), Width = info.Width, Height = info.Height,
            Context = crop ?? new() { X = 0, Y = 0, Width = 1, Height = 1 } };
        using var stream = new MemoryStream(original);
        return await PrepareAsync(stream, selection, aspect, ct, resolution, qwen);
    }
    public static async Task<byte[]> InputAsync(Stream source, RegionalImageSelection? selection, ImageCropRegion? crop, string aspect,
        CancellationToken ct = default, AssetImageReference? regionalBase = null, int resolution = 1024, QwenImage21Options? qwen = null) =>
        regionalBase is not null ? (await PrepareBaseAsync(source, regionalBase, selection, crop, aspect, ct, resolution, qwen)).Png :
        selection is not null ? (await PrepareAsync(source, selection, aspect, ct, resolution, qwen)).Png :
        qwen is not null ? await QwenImage21Inputs.PrepareAsync(source, crop, ct) : await ComfyReferenceImageEditor.PrepareSourcePngAsync(source, crop, ct);

    public static async Task<byte[]> CombineAsync(RegionalImageCapture captured, byte[] output, int blend = 0, CancellationToken ct = default)
        => (await CompositeAsync(captured, output, blend, null, ct)).Png;

    public static async Task<RegionalCompositeResult> CompositeAsync(RegionalImageCapture captured, byte[] output, int blend = 0,
        RegionalColourSettings? colour = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (blend is < 0 or > 32) throw new AiGenerationException("Edge blend must be between 0 and 32 pixels.");
        ValidateColour(colour);
        var selection = captured.Selection; var map = captured.Canvas;
        if (Hash(captured.OriginalPng) != selection.SourceHash) throw new AiGenerationException("The captured original cannot be verified.");
        using var original = Image.Load<Rgba32>(captured.OriginalPng);
        using var edited = Image.Load<Rgba32>(output); edited.Mutate(x => x.AutoOrient());
        if (map.CanvasWidth < 1 || map.CanvasHeight < 1 || map.Width < 1 || map.Height < 1 || map.X < 0 || map.Y < 0 || map.X + map.Width > map.CanvasWidth || map.Y + map.Height > map.CanvasHeight || original.Width != selection.Width || original.Height != selection.Height)
            throw new AiGenerationException("The saved canvas mapping is invalid.");
        // Allow a one-pixel rounding difference, but never guess a crop or stretch a different aspect.
        if (Math.Abs(edited.Width / (double)edited.Height - map.CanvasWidth / (double)map.CanvasHeight) > 1.0 / edited.Height + 1.0 / map.CanvasHeight)
            throw new AiGenerationException("The model returned different canvas proportions. The raw result is kept, but cannot be placed without stretching. Try a new generation.");
        var left = (int)Math.Round(map.X * edited.Width / (double)map.CanvasWidth);
        var top = (int)Math.Round(map.Y * edited.Height / (double)map.CanvasHeight);
        var right = (int)Math.Round((map.X + map.Width) * edited.Width / (double)map.CanvasWidth);
        var bottom = (int)Math.Round((map.Y + map.Height) * edited.Height / (double)map.CanvasHeight);
        var crop = ImageGeometry.CropPixels(original.Width, original.Height, selection.Context);
        var colourFringe = (int)Math.Ceiling(3 * Math.Max(crop.Width / (double)Math.Max(1, right - left), crop.Height / (double)Math.Max(1, bottom - top)));
        edited.Mutate(x => x.Crop(new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top))).Resize(crop.Width, crop.Height));
        var (mask, protect) = Masks(selection); var w = original.Width; var h = original.Height;
        if (!mask.Any(x => x)) throw new AiGenerationException("Select at least one editable pixel.");
        var match = MatchColour(original, edited, crop, mask, protect, colour, colourFringe, ct);
        var colourOffset = ColourOffset(colour, match.Offset);
        // Manhattan distance inside the editable area. Protected and outside pixels are never blended.
        var distance = blend == 0 ? null : mask.Select(x => x ? blend + 1 : 0).ToArray();
        if (distance is not null)
        {
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var i = y * w + x; if (!mask[i]) continue; distance[i] = Math.Min(distance[i], Math.Min(x == 0 ? 1 : distance[i - 1] + 1, y == 0 ? 1 : distance[i - w] + 1)); }
            for (var y = h - 1; y >= 0; y--) for (var x = w - 1; x >= 0; x--) { var i = y * w + x; if (!mask[i]) continue; distance[i] = Math.Min(distance[i], Math.Min(x == w - 1 ? 1 : distance[i + 1] + 1, y == h - 1 ? 1 : distance[i + w] + 1)); }
        }
        for (var y = crop.Top; y < crop.Bottom; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = crop.Left; x < crop.Right; x++)
            {
                var i = y * w + x; if (!mask[i]) continue;
                var pixel = edited[x - crop.X, y - crop.Y];
                pixel = CorrectColour(pixel, colourOffset);
                if (distance is null || distance[i] > blend) original[x, y] = pixel;
                else { var a = distance[i] / (float)(blend + 1); var old = original[x, y]; original[x, y] = new Rgba32(System.Numerics.Vector4.Lerp(old.ToVector4(), pixel.ToVector4(), a)); }
            }
        }
        return new(await PngAsync(original, ct), match.Message, match.Matched);
    }
    private static async Task<byte[]> PngAsync(Image<Rgba32> image, CancellationToken ct)
    { using var stream = new MemoryStream(); await image.SaveAsPngAsync(stream, new PngEncoder { SkipMetadata = true }, ct); return stream.ToArray(); }
}
