using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Lumibelle.Tests.RegionalImageTests;

namespace Lumibelle.Tests;

public sealed class RegionalColourTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(RegionalImageCapture Capture, byte[] Output)> Fixture(bool tight = false)
    {
        using var original = new Image<Rgba32>(80, 80);
        for (var y = 0; y < 80; y++) for (var x = 0; x < 80; x++) original[x, y] = new((byte)(70 + x / 2), (byte)(90 + y / 2), 120);
        original[4, 4] = new(21, 52, 100, 30);
        original[32, 32] = new(41, 62, 111, 84);
        using var stream = new MemoryStream(); await original.SaveAsPngAsync(stream, Ct);
        var selection = (await Selection(stream.ToArray(), mode: RegionalEditMode.Edit)) with {
            Strokes = [Rect(false, .25, .25, .75, .75), Rect(true, .375, .375, .5, .5)],
            Context = tight ? new() { X = .25, Y = .25, Width = .5, Height = .5 } : new() { X = 0, Y = 0, Width = 1, Height = 1 } };
        stream.Position = 0;
        var bytes = await RegionalImageEdits.OriginalAsync(stream, Ct);
        var crop = ImageGeometry.CropPixels(80, 80, selection.Context);
        var map = new RegionalCanvas(20, 10, crop.Width, crop.Height, crop.Width + 40, crop.Height + 20);
        using var output = new Image<Rgba32>(map.CanvasWidth, map.CanvasHeight, RegionalImageEdits.Cover);
        for (var y = 0; y < crop.Height; y++) for (var x = 0; x < crop.Width; x++)
        {
            var a = original[crop.X + x, crop.Y + y];
            output[map.X + x, map.Y + y] = new((byte)(a.R + 18), (byte)(a.G - 12), (byte)(a.B + 8), a.A);
        }
        // A deliberate new colour must survive the match; protected model pixels aren't evidence.
        output[map.X + 50 - crop.X, map.Y + 50 - crop.Y] = new(168, 68, 118, 92);
        for (var y = 30; y < 40; y++) for (var x = 30; x < 40; x++) output[map.X + x - crop.X, map.Y + y - crop.Y] = RegionalImageEdits.Cover;
        using var raw = new MemoryStream(); await output.SaveAsPngAsync(raw, Ct);
        return (new(bytes, selection, map), raw.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(32)]
    public async Task MatchesContextAndPreservesEveryProtectedAndUntouchedPixel(int blend)
    {
        var (capture, raw) = await Fixture();
        var result = await RegionalImageEdits.CompositeAsync(capture, raw, blend, new() { MatchOriginal = true }, Ct);
        Assert.True(result.ColourMatched); Assert.Contains("surrounding context", result.ColourMessage);
        using var original = Image.Load<Rgba32>(capture.OriginalPng); using var image = Image.Load<Rgba32>(result.Png);
        var masks = RegionalImageEdits.Masks(capture.Selection);
        for (var y = 0; y < 80; y++) for (var x = 0; x < 80; x++)
            if (!masks.Edit[y * 80 + x]) Assert.Equal(original[x, y], image[x, y]);
        if (blend == 0) { Assert.Equal(new Rgba32(150, 80, 110, 92), image[50, 50]); Assert.Equal(original[25, 25], image[25, 25]); }
        Assert.NotEqual(original[50, 50], image[50, 50]);
    }

    [Fact]
    public async Task TightCropRequiresAnExplicitUnchangedMatchingArea()
    {
        var (capture, raw) = await Fixture(tight: true);
        var settings = new RegionalColourSettings { MatchOriginal = true };
        var unavailable = await RegionalImageEdits.CompositeAsync(capture, raw, colour: settings, ct: Ct);
        Assert.False(unavailable.ColourMatched); Assert.Contains("No reliable colour match", unavailable.ColourMessage);
        Assert.Equal(await RegionalImageEdits.CombineAsync(capture, raw, ct: Ct), unavailable.Png);
        var matched = await RegionalImageEdits.CompositeAsync(capture, raw, colour: settings with { MatchArea = new() { X = .25, Y = .25, Width = .1, Height = .2 } }, ct: Ct);
        Assert.True(matched.ColourMatched);
        using var image = Image.Load<Rgba32>(matched.Png);
        Assert.Equal(new Rgba32(150, 80, 110, 92), image[50, 50]);
        var protectedArea = await RegionalImageEdits.CompositeAsync(capture, raw, colour: settings with { MatchArea = new() { X = .375, Y = .375, Width = .125, Height = .125 } }, ct: Ct);
        Assert.False(protectedArea.ColourMatched);
        var outside = await RegionalImageEdits.CompositeAsync(capture, raw, colour: settings with { MatchArea = new() { X = 0, Y = 0, Width = .1, Height = .1 } }, ct: Ct);
        Assert.False(outside.ColourMatched);
    }

    [Fact]
    public async Task StrengthAndManualAdjustmentsComposeBeforeBlendingWithoutChangingAlpha()
    {
        var (capture, raw) = await Fixture();
        var result = await RegionalImageEdits.CompositeAsync(capture, raw, colour: new() { MatchOriginal = true, Strength = 50, Brightness = 2, Warmth = 4, Tint = 2 }, ct: Ct);
        using var image = Image.Load<Rgba32>(result.Png);
        Assert.Equal(new Rgba32(169, 77, 116, 92), image[50, 50]);
        var zero = await RegionalImageEdits.CompositeAsync(capture, raw, colour: new() { MatchOriginal = true, Strength = 0 }, ct: Ct);
        Assert.Equal(await RegionalImageEdits.CombineAsync(capture, raw, ct: Ct), zero.Png);
        var manual = await RegionalImageEdits.CompositeAsync(capture, raw, colour: new() { Brightness = -50, Warmth = -50, Tint = 50 }, ct: Ct);
        using var dark = Image.Load<Rgba32>(manual.Png);
        Assert.Equal(new Rgba32(16, 0, 66, 92), dark[50, 50]);
    }

    [Fact]
    public async Task InvalidAndInconsistentMatchesDoNotSilentlyRemapColours()
    {
        var (capture, raw) = await Fixture();
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.ValidateColour(new() { Version = 2 }));
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.ValidateColour(new() { Strength = 101 }));
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.ValidateColour(new() { Brightness = 51 }));
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.ValidateColour(new() { MatchArea = new() { X = double.NaN, Width = .1, Height = .1 } }));
        using var edited = Image.Load<Rgba32>(raw);
        for (var y = 10; y < 90; y++) for (var x = 20; x < 100; x++) edited[x, y] = new((byte)(x * 23 % 220 + 10), (byte)(y * 19 % 220 + 10), 150);
        using var output = new MemoryStream(); await edited.SaveAsPngAsync(output, Ct);
        var result = await RegionalImageEdits.CompositeAsync(capture, output.ToArray(), colour: new() { MatchOriginal = true }, ct: Ct);
        Assert.False(result.ColourMatched);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RegionalImageEdits.CompositeAsync(capture, raw, colour: new() { MatchOriginal = true }, ct: cancelled.Token));
    }
}
