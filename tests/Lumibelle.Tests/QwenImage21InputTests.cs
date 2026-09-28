using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class QwenImage21Tests
{
    [Fact]
    public async Task PreparationPreserves2KReferenceDetailAndAlpha()
    {
        using var source = new MemoryStream(Png(2048, 2048, 91));
        var prepared = await QwenImage21Inputs.PrepareAsync(source, null, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal((2048, 2048), QwenImage21Policy.PngSize(prepared));
        using var decoded = Image.Load<Rgba32>(prepared);
        Assert.Equal((byte)91, decoded[1000, 1000].A);
    }

    [Fact]
    public async Task PreparationCropsWithoutResizingOrChangingTheSource()
    {
        var original = Png(2048, 1024, 127); using var source = new MemoryStream(original, writable: false);
        var prepared = await QwenImage21Inputs.PrepareAsync(source, new() { X = .25, Y = 0, Width = .5, Height = 1 }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal((1024, 1024), QwenImage21Policy.PngSize(prepared));
        Assert.Equal((2048, 1024), QwenImage21Policy.PngSize(original));
        using var decoded = Image.Load<Rgba32>(prepared); Assert.Equal((byte)127, decoded[0, 0].A);
    }

    [Theory]
    [InlineData("png")] [InlineData("jpeg")] [InlineData("webp")]
    public async Task SupportedSourceFormatsBecomePreparedPngs(string format)
    {
        using var image = new Image<Rgba32>(96, 64, new Rgba32(50, 100, 150, 255)); using var source = new MemoryStream();
        switch (format) { case "jpeg": image.SaveAsJpeg(source); break; case "webp": image.SaveAsWebp(source); break; default: image.SaveAsPng(source); break; }
        source.Position = 0;
        var prepared = await QwenImage21Inputs.PrepareAsync(source, null, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal((96, 64), QwenImage21Policy.PngSize(prepared));
    }

    [Fact]
    public async Task ExifOrientationIsAppliedBeforeCropAndMetadataIsRemoved()
    {
        using var image = new Image<Rgba32>(96, 64, new Rgba32(50, 100, 150, 255));
        image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
        using var source = new MemoryStream(); image.SaveAsJpeg(source); source.Position = 0;
        var prepared = await QwenImage21Inputs.PrepareAsync(source, new() { Width = .5, Height = 1 }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal((32, 96), QwenImage21Policy.PngSize(prepared));
        using var decoded = Image.Load<Rgba32>(prepared); Assert.Null(decoded.Metadata.ExifProfile);
    }

    [Fact]
    public async Task PreparationHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var source = new MemoryStream(Png());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QwenImage21Inputs.PrepareAsync(source, null, cancellation.Token));
    }

    [Fact]
    public void OpaqueReferenceNeedsNoAlphaReconstruction()
    {
        Assert.Null(ComfyQwenImage21.AlphaMask(Convert.ToBase64String(Png())));
        var graph = Build(Request(1));
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "JoinImageWithAlpha");
    }

    [Fact]
    public void AlphaTransportUsesExplicitInverseMaskNotTheToolingAuxiliaryOutput()
    {
        using var original = new Image<Rgba32>(96, 64, new Rgba32(1, 2, 3, 255));
        original[0, 0] = new(1, 2, 3, 0); original[1, 0] = new(4, 5, 6, 64);
        using var bytes = new MemoryStream(); original.SaveAsPng(bytes);
        var encoded = Convert.ToBase64String(bytes.ToArray());
        using var mask = Image.Load<L8>(Convert.FromBase64String(ComfyQwenImage21.AlphaMask(encoded)!));
        Assert.Equal((byte)255, mask[0, 0].PackedValue); Assert.Equal((byte)191, mask[1, 0].PackedValue); Assert.Equal((byte)0, mask[2, 0].PackedValue);
        var request = Request(1); request = request with { Inputs = [request.Inputs[0] with { Png = bytes.ToArray() }] };
        var graph = Build(request);
        Assert.Equal("103", Inputs(graph, "4").GetProperty("images.image_1")[0].GetString());
        Assert.Equal("JoinImageWithAlpha", graph.GetProperty("103").GetProperty("class_type").GetString());
        Assert.Equal("100", Inputs(graph, "103").GetProperty("image")[0].GetString());
        Assert.Equal("102", Inputs(graph, "103").GetProperty("alpha")[0].GetString());
        Assert.Equal("ImageToMask", graph.GetProperty("102").GetProperty("class_type").GetString());
        Assert.Equal("red", Inputs(graph, "102").GetProperty("channel").GetString());
        Assert.Equal(0, Inputs(graph, "102").GetProperty("image")[1].GetInt32());
    }

    [Fact]
    public void InvalidEncodedReferenceCannotBeSilentlyIgnored()
    {
        Assert.Throws<AiGenerationException>(() => ComfyQwenImage21.AlphaMask("not-base64"));
        Assert.Throws<AiGenerationException>(() => ComfyQwenImage21.AlphaMask(Convert.ToBase64String([1, 2, 3])));
    }
}
