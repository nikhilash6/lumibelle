using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(0, "From Image 1")]
    [InlineData(0, "9:16")]
    [InlineData(1024, "From Image 1")]
    [InlineData(1440, "16:9")]
    [InlineData(2048, "9:16")]
    public async Task QwenRegionalCanvasSurvivesCaptureReviewAndReload(int resolution, string aspect)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.QwenImage21, true);
        await using var media = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, f.References[0].ImageId, ct);
        using var source = new MemoryStream(); await media!.Content.CopyToAsync(source, ct);
        var selection = (await RegionalImageTests.Selection(source.ToArray(), f.References[0])) with
        {
            Context = new() { X = .25, Y = .125, Width = .5, Height = .75 },
            Strokes = [RegionalImageTests.Rect(true, 0, 0, .5, 1)]
        };
        var submission = await f.Capture(regions: [selection], qwen: new(resolution, 17), aspect: aspect);
        var request = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        var canvas = request.Regional!.Canvas;
        var expected = QwenImage21Policy.EditSize(aspect, 40, 30, new(resolution, 17));
        Assert.Equal(expected, (canvas.CanvasWidth, canvas.CanvasHeight));
        Assert.Equal(expected, QwenImage21Policy.PngSize(request.Inputs[0].Png));
        Assert.Equal(expected, QwenImage21Policy.OutputSize(request));
        using (var prepared = Image.Load<Rgba32>(request.Inputs[0].Png))
            Assert.Equal(RegionalImageEdits.Cover, prepared[canvas.X + canvas.Width / 8, canvas.Y + canvas.Height / 2]);

        // Enhancement and generation inspect the same padded/masked crop at the selected resolution.
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var assist = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(null!, f.Settings, f.Assets), null!);
        var context = new PromptEnhancementContext
        {
            ProjectId = f.Project.Id, AssetId = f.AssetId, Prompt = "Change the background", Workflow = ImageWorkflow.QwenImage21,
            IsEdit = true, MaximumReferences = 10, AspectRatio = aspect, QwenImage21 = request.Edit!.QwenImage21, TargetLook = request.Look,
            References = request.Inputs.Select((i, index) => new PromptReference(i.Reference, index == 0 ? "Base" : "Reference", "", i.Crop)
                { Look = i.Context, Region = index == 0 ? selection : null }).ToArray()
        };
        var text = (await assist.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), new(context, new(AiBackend.OpenRouter, "test-model", "Test model"), true), ct))
            .Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var inspected = text.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).ToArray();
        for (var i = 0; i < inspected.Length; i++) Assert.Equal(request.Inputs[i].Png, inspected[i].Image);

        f.OutputPng = await RegionalImageTests.Png(expected.Width, expected.Height, new(0, 210, 70));
        var job = await f.Claim(submission);
        Assert.Equal(1, (await f.Worker.ExecuteAsync(job, submission.Snapshot, ct)).CompletedCandidates);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        Assert.Contains(RegionalImageEdits.PlacementInstruction, f.Graphs.Single().GetRawText());
        var review = new RegionalImageReview(f.Jobs, f.Assets);
        var candidate = Assert.Single((await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates);
        var saved = await review.SaveAsync(f.Project.Id, submission.Id, candidate.Image.CandidateId, 0, ct);
        var image = saved.Assets[0].Images.Single(i => i.Id == candidate.Image.CandidateId);
        Assert.Equal((80, 40), (image.Width, image.Height));
        Assert.Equal(expected, (image.Generation!.QwenImage21!.Width, image.Generation.QwenImage21.Height));
        Assert.Single(image.Generation.Edit!.Regions!);
        Assert.False(QwenImage21Policy.InvalidMetadata(image.Generation, 80, 40));
        await using var published = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, image.Id, ct);
        using var actual = await Image.LoadAsync<Rgba32>(published!.Content, ct);
        using var original = Image.Load<Rgba32>(source.ToArray());
        var mask = RegionalImageEdits.Masks(selection).Edit;
        for (var y = 0; y < 40; y++) for (var x = 0; x < 80; x++)
            Assert.Equal(mask[y * 80 + x] ? new Rgba32(0, 210, 70) : original[x, y], actual[x, y]);
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(await f.Recover(job), submission.Snapshot, ct);
        Assert.Equal(1, f.Posts);
        Assert.Single((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        Assert.True((await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates[0].State.Saved);

        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Regional = null }));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Regional = request.Regional with { Canvas = canvas with { X = canvas.X + 1 } } }));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Inputs = [request.Inputs[0] with { Png = Png(expected.Width + 32, expected.Height) }, request.Inputs[1]] }));
        Assert.True(QwenImage21Policy.InvalidMetadata(image.Generation with { QwenImage21 = image.Generation.QwenImage21 with { Width = expected.Width + 32 } }, 80, 40));
    }
}

public sealed partial class RegionalImageTests
{
    [Fact]
    public async Task QwenProtectionPreservesOriginalAlphaAndRejectsChangedSources()
    {
        var bytes = await Png(83, 57, new(20, 30, 40, 80));
        var selection = (await Selection(bytes)) with { Strokes = [Rect(true, 0, 0, .5, 1)] };
        using var source = new MemoryStream(bytes);
        var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "16:9", Ct, qwen: new(0));
        var canvas = prepared.Capture.Canvas;
        using var result = Image.Load<Rgba32>(await RegionalImageEdits.CombineAsync(prepared.Capture,
            await Png(canvas.CanvasWidth, canvas.CanvasHeight, new(60, 90, 120)), ct: Ct));
        Assert.Equal(new Rgba32(20, 30, 40, 80), result[10, 20]);
        Assert.Equal(new Rgba32(60, 90, 120), result[70, 20]);
        using var changed = new MemoryStream(await Png(83, 57, new(10, 20, 30)));
        await Assert.ThrowsAsync<AiGenerationException>(() => RegionalImageEdits.PrepareAsync(changed, selection, "16:9", Ct, qwen: new(0)));
    }
}
