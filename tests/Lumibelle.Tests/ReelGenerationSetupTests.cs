using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public void ReelPresetsPreserveAuthoredContentOverridesAndLegacyWireFormat()
    {
        var draft = ReferenceReelTests.Recipe();
        var legacy = JsonSerializer.SerializeToElement(draft, AtomicJsonFile.Options);
        Assert.False(legacy.TryGetProperty("generationSetup", out _));
        Assert.False(legacy.TryGetProperty("outputOverrides", out _));
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance";
        draft.LookId = Guid.NewGuid(); draft.Aspect = "9:16";
        var before = draft.Copy();
        draft.OutputOverrides = new() { TakeCount = 2, Resolution = VideoResolution.Quick };
        var preset = new GenerationSetup { Name = "Shared setup", Version = 1, Settings = new() {
            TakeCount = 4, Seed = 123, NativeResolution = true, SaveLosslessFrames = true, Loras = [VideoLora()]
        } };
        ReelGenerationSetups.Apply(draft, preset);
        Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.UseGuidance, draft.UseGuidance);
        Assert.Equal(before.LookId, draft.LookId); Assert.Equal(before.Aspect, draft.Aspect);
        Assert.Equal(before.Duration, draft.Duration); Assert.Equal(before.Voice, draft.Voice);
        Assert.Equal(2, ReelGenerationSetups.TakeCount(draft)); Assert.Equal(VideoResolution.Quick, VideoResolutions.Selected(draft));
        Assert.Equal(preset.Id, draft.GenerationSetup!.Id); Assert.Equal(123, draft.GenerationSetup.Settings.Seed);
        preset.Settings.TakeCount = 1;
        Assert.Equal(4, draft.GenerationSetup.Settings.TakeCount);
        var frozen = draft.Copy();
        preset.Version++; preset.Settings.GenerationPreset = "turbo8"; preset.Settings.Loras = [];
        ReelGenerationSetups.Apply(draft, preset);
        Assert.Single(frozen.Loras!); Assert.Empty(draft.Loras!);
        Assert.Equal(1, frozen.GenerationSetup!.Version); Assert.Equal(2, draft.GenerationSetup.Version);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task ReelCaptureFreezesSharedPresetAndRejectsChangedOrArchivedPreset(bool archive, bool upscale)
    {
        using var f = await QueuedVideoFixture.Create(this);
        await f.CaptureReel(environment: true);
        var store = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var preset = (await store.SaveAsync(new GenerationSetup { Name = "Reel preset", Settings = new() { Seed = 91, TakeCount = 2,
            Resolution = upscale ? VideoResolution.Preview : VideoResolution.Quick, UpscalePreview = upscale } }, 0, _ct)).Setups.Single();
        var draft = (await f.Assets.LoadAsync(f.Project.Id, _ct)).ReelDrafts.Single().Copy();
        ReelGenerationSetups.Apply(draft, preset);
        draft = await f.Assets.SaveDraftAsync(f.Project.Id, draft, draft.Revision, _ct);
        var capture = new AiReelCapture(f.Assets, f.Assets, f.Settings, f.Generator,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Preferences, generationSetups: store);
        var submission = await capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision, 2, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(91, submission.Batch!.Candidates[0].Seed);
        Assert.Equal(preset.Id, request.Snapshot.Reel!.Recipe.GenerationSetup!.Id);
        Assert.Equal(VideoResolutions.Size(draft), (request.Snapshot.Width, request.Snapshot.Height));
        Assert.Equal(upscale, request.Snapshot.PreviewUpscale is not null);
        preset.Archived = archive; preset.Settings.TakeCount = 3;
        await store.SaveAsync(preset, preset.Version, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision, 2, _ct));
        Assert.Equal(2, request.Snapshot.Reel.Recipe.GenerationSetup.Settings.TakeCount);
        AiVideoJobPolicy.Validate(request);
    }
}
