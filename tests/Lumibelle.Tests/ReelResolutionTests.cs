using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ReelResolutionTests
{
    [Theory]
    [InlineData("16:9")] [InlineData("9:16")] [InlineData("1:1")]
    public void ReelResolutionSizesAreOrderedAlignedAndRetainExistingCanvases(string aspect)
    {
        var recipe = ReferenceReelTests.Recipe(ReelFraming.ContinuousTurn) with { Aspect = aspect };
        var legacyFingerprint = H3Policy.Fingerprint(ReferenceReels.Inputs(recipe));
        Assert.Equal(legacyFingerprint, VideoResolutions.Fingerprint(recipe));
        var areas = new List<int>(); var fingerprints = new HashSet<string>();
        foreach (var resolution in VideoResolutions.Choices)
        {
            VideoResolutions.Select(recipe, resolution); ReferenceReels.Validate(recipe);
            var size = VideoResolutions.Size(recipe); areas.Add(size.Width * size.Height);
            Assert.Equal(0, size.Width % 32); Assert.Equal(0, size.Height % 32);
            Assert.Equal(resolution, VideoResolutions.Selected(recipe.Copy()));
            Assert.True(fingerprints.Add(VideoResolutions.Fingerprint(recipe)));
            if (resolution is VideoResolution.Preview or VideoResolution.Native)
            {
                Assert.Equal(H3Policy.Size(aspect, resolution == VideoResolution.Native), size);
                Assert.Null(recipe.Resolution);
                Assert.Equal(H3Policy.Fingerprint(ReferenceReels.Inputs(recipe)), VideoResolutions.Fingerprint(recipe));
            }
        }
        Assert.Equal(areas.Order(), areas);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(recipe with { Resolution = (VideoResolution)999 }));
    }

    [Fact]
    public void ReelResolutionBadgeUsesMediaDimensionsForImportedAndMismatchedOutputs()
    {
        var recipe = ReferenceReelTests.Recipe(ReelFraming.ContinuousTurn) with { Aspect = "16:9", NativeResolution = true };
        var shot = ReferenceReels.Inputs(recipe);
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 0, shot, "prompt", "hash", "http://localhost:8188", new(), 1344, 768, 124, recipe.PresetVersion);
        var reel = new AssetReferenceReel { Media = new(Guid.NewGuid(), "hash", 100, 1344, 768, 124, 24, 124 / 24d, false) };
        Assert.Equal("1.0 MP", VideoResolutions.Badge(reel)); // Imported resolution is factual, without a generated Native label.
        reel = reel with { Generation = new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 42, snapshot) };
        Assert.Equal("Native · 1.0 MP", VideoResolutions.Badge(reel));
        reel = reel with { Media = reel.Media with { Width = 832, Height = 480 } };
        Assert.Equal("0.4 MP", VideoResolutions.Badge(reel));
        Assert.Equal("832 × 480", VideoResolutions.Dimensions(reel));
    }
}

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(VideoResolution.Quick)] [InlineData(VideoResolution.Detail)]
    public async Task ReelResolutionSurvivesDraftReloadCaptureAndRejectsMismatchedDimensions(VideoResolution resolution)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true, resolution: resolution);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var reloaded = (await f.Assets.LoadAsync(f.Project.Id, _ct)).ReelDrafts.Single();
        Assert.Equal(resolution, VideoResolutions.Selected(reloaded));
        Assert.Equal(VideoResolutions.Size(reloaded), (request.Snapshot.Width, request.Snapshot.Height));
        AiVideoJobPolicy.Validate(request);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Snapshot = request.Snapshot with { Width = request.Snapshot.Width + 32 } }));
        var wrongRecipe = request.Snapshot.Reel!.Recipe with { Resolution = resolution == VideoResolution.Quick ? VideoResolution.Detail : VideoResolution.Quick };
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Snapshot = request.Snapshot with { Reel = request.Snapshot.Reel with { Recipe = wrongRecipe } } }));
    }
}
