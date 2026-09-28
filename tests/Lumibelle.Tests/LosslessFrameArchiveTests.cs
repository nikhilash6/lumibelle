using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task TransparentDeltaPixelsPreserveThePreviousComposedFrame()
    {
        // Pillow's lossless animation encoder replaces unchanged pixels inside the
        // second/third frame rectangle with transparency. RGB-only decoding loses them.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures/comfy-lossless-delta.webp");
        for (var index = 0; index < 3; index++)
        {
            await using var stream = await LosslessFrameArchive.OpenFrameAsync(path, index, 40, 32, _ct);
            using var image = await Image.LoadAsync<Rgb24>(stream, _ct);
            for (var y = 0; y < 32; y++) for (var x = 0; x < 40; x++)
            {
                var expected = new Rgb24((byte)(x * 5), (byte)(y * 7), (byte)((x + y) * 3));
                if (index >= 1 && (x, y) == (4, 4)) expected = new(255, 0, 255);
                if (index >= 1 && (x, y) == (35, 25)) expected = new(0, 255, 0);
                if (index >= 2 && (x, y) == (8, 7)) expected = new(0, 0, 255);
                if (index >= 2 && (x, y) == (31, 24)) expected = new(255, 0, 0);
                Assert.Equal(expected, image[x, y]);
            }
        }
    }

    [Fact]
    public async Task NativeComfyOutputPreservesCoalescedFramesAndStaticSegments()
    {
        Directory.CreateDirectory(_root);
        for (var segment = 0; segment < 2; segment++)
        {
            var target = Path.Combine(_root, LosslessFrameArchive.FileName(segment));
            var indices = await LosslessFrameArchive.CleanAsync(Path.Combine(AppContext.BaseDirectory, $"Fixtures/comfy-lossless-{segment}.webp"), target, 32, 32, segment == 0 ? 24 : 15, _ct);
            Assert.Equal(segment == 0 ? Enumerable.Repeat(0, 12).Concat(Enumerable.Repeat(1, 12)) : Enumerable.Repeat(0, 15), indices);
            for (var i = 0; i < indices.Count; i++)
            {
                await using var source = await LosslessFrameArchive.OpenFrameAsync(target, indices[i], 32, 32, _ct);
                using var frame = await Image.LoadAsync<Rgb24>(source, _ct);
                Assert.Equal(segment == 0 && i < 12 ? new Rgb24(255, 0, 0) : new Rgb24(0, 0, 255), frame[15, 15]);
            }
        }
    }

    [Fact]
    public async Task LosslessWebpCleaningPreservesEveryPixelAndStripsAncillaryMetadata()
    {
        Directory.CreateDirectory(_root);
        var bytes = await MockFrameArchive.WebpAsync(32, 32, 8, ct: _ct);
        using var original = Image.Load<Rgb24>(bytes);
        using var stream = new MemoryStream(); stream.Write(bytes);
        var metadata = Encoding.UTF8.GetBytes("secret workflow metadata");
        stream.Write("EXIF"u8); var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)metadata.Length); stream.Write(size); stream.Write(metadata);
        if (metadata.Length % 2 != 0) stream.WriteByte(0);
        var withMetadata = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(withMetadata.AsSpan(4), (uint)withMetadata.Length - 8);
        var source = Path.Combine(_root, "comfy.webp"); var target = Path.Combine(_root, "archive.webp");
        await File.WriteAllBytesAsync(source, withMetadata, _ct);
        var indices = await LosslessFrameArchive.CleanAsync(source, target, 32, 32, 8, _ct);
        Assert.Equal(Enumerable.Range(0, 8), indices);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(target, _ct)));
        using var cleaned = await Image.LoadAsync<Rgb24>(target, _ct);
        for (var i = 0; i < 8; i++)
        {
            await using var selected = await LosslessFrameArchive.OpenFrameAsync(target, i, 32, 32, _ct);
            using var png = await Image.LoadAsync<Rgb24>(selected, _ct);
            Assert.Equal("PNG", png.Metadata.DecodedImageFormat!.Name); Assert.Single(png.Frames);
            for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            { Assert.Equal(original.Frames[i][x, y], cleaned.Frames[i][x, y]); Assert.Equal(original.Frames[i][x, y], png[x, y]); }
        }
    }

    [Fact]
    public async Task FullLengthWebpArchivesKeepFrameNumberingAcrossSegments()
    {
        Directory.CreateDirectory(_root);
        var frames = await MockFrameArchive.WriteAsync(_root, 362, 32, 32, _ct);
        Assert.Equal(16, frames.Select(f => f.FileName).Distinct().Count());
        foreach (var group in frames.GroupBy(f => f.FileName))
        {
            var indices = await LosslessFrameArchive.ValidateAsync(Path.Combine(_root, group.Key), 32, 32, group.Count(), _ct);
            Assert.Equal(group.Select(f => f.ArchiveFrameIndex), indices);
        }
        foreach (var i in new[] { 0, 23, 24, 359, 360, 361 })
        {
            var frame = frames[i];
            await using var source = await LosslessFrameArchive.OpenFrameAsync(Path.Combine(_root, frame.FileName), frame.ArchiveFrameIndex, 32, 32, _ct);
            using var image = await Image.LoadAsync<Rgb24>(source, _ct);
            Assert.Equal(new Rgb24((byte)i, (byte)(i / 256), 200), image[0, 0]);
        }
    }

    [Theory]
    [InlineData("lossy")]
    [InlineData("incomplete")]
    [InlineData("size")]
    [InlineData("truncated")]
    public async Task InvalidArchivesAreNotPublished(string problem)
    {
        Directory.CreateDirectory(_root);
        var bytes = await MockFrameArchive.WebpAsync(32, 32, problem == "incomplete" ? 7 : 8, lossy: problem == "lossy", ct: _ct);
        if (problem == "truncated") bytes = bytes[..^1];
        var source = Path.Combine(_root, "input.webp"); var target = Path.Combine(_root, "archive.webp");
        await File.WriteAllBytesAsync(source, bytes, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => LosslessFrameArchive.CleanAsync(source, target, problem == "size" ? 64 : 32, 32, 8, _ct));
        Assert.False(File.Exists(target)); Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void MissingWebpCapabilitiesBlockGenerationWithoutPngFallback()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-contract.json")))!;
        Assert.True(ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), new()).StandardReady);
        root["SaveAnimatedWEBP"]!["input"]!["required"]!.AsObject().Remove("lossless");
        var result = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), new());
        Assert.True(result.StandardReady); Assert.True(result.Ready(Ready()));
        var archived = Ready(); archived.SaveLosslessFrames = true;
        Assert.False(result.Ready(archived)); Assert.Contains("SaveAnimatedWEBP.lossless", result.Issue(archived));
    }
}
