using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task OutputOverridesSurviveReloadAndPresetEditsWithoutChangingOtherShots()
    {
        var f = Fixture();
        await f.Shots.SaveAsync(f.Project.Id, [Ready(), Ready()], 0, ct: _ct);
        var library = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock,
            new FileAiJobStore(Path.Combine(_root, "jobs"), _clock), generationSetups: library);
        var initial = await store.InitializeAsync(f.Project.Id, _ct);
        var one = initial.Compositions[0]; var otherId = initial.Compositions[1].Id;
        var preset = (await library.LoadAsync(_ct)).Setups.Single();
        one.OutputOverrides = new() { TakeCount = 3, Resolution = VideoResolution.Detail };
        await store.SaveAsync(f.Project.Id, one, one.Version, _ct);
        var loaded = await store.LoadAsync(f.Project.Id, _ct);
        one = loaded.Compositions.Single(c => c.Id == one.Id);
        Assert.Equal(3, one.TakeCount); Assert.Equal(VideoResolution.Detail, VideoResolutions.Selected(one.Shot));
        Assert.Equal(preset.Version, (await library.LoadAsync(_ct)).Setups.Single().Version);
        Assert.Equal(preset.Settings.TakeCount, loaded.Compositions.Single(c => c.Id == otherId).TakeCount);

        // Editing another shared setting must not promote the effective output.
        one.Seed = 42;
        await store.SaveAsync(f.Project.Id, one, one.Version, _ct);
        preset = (await library.LoadAsync(_ct)).Setups.Single();
        Assert.Equal(42, preset.Settings.Seed); Assert.Equal(1, preset.Settings.TakeCount);
        Assert.Null(preset.Settings.Resolution);
        preset.Settings.TakeCount = 2; preset.Settings.Resolution = VideoResolution.Quick;
        await library.SaveAsync(preset, preset.Version, _ct);
        loaded = await store.LoadAsync(f.Project.Id, _ct);
        one = loaded.Compositions.Single(c => c.Id == one.Id);
        Assert.Equal(3, one.TakeCount); Assert.Equal(VideoResolution.Detail, VideoResolutions.Selected(one.Shot));
        var other = loaded.Compositions.Single(c => c.Id == otherId);
        Assert.Equal(2, other.TakeCount); Assert.Equal(VideoResolution.Quick, VideoResolutions.Selected(other.Shot));

        // Explicit promotion removes the override and updates the shared defaults.
        one.OutputOverrides = null;
        await store.SaveAsync(f.Project.Id, one, one.Version, _ct);
        loaded = await store.LoadAsync(f.Project.Id, _ct);
        Assert.All(loaded.Compositions, c => Assert.Equal(3, c.TakeCount));
        Assert.All(loaded.Compositions, c => Assert.Equal(VideoResolution.Detail, VideoResolutions.Selected(c.Shot)));
    }

    [Fact]
    public async Task GenerationCapturesEffectiveOutputAndResetReturnsToPresetDefaults()
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var library = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock,
            new FileAiJobStore(Path.Combine(_root, "jobs"), _clock), generationSetups: library);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions.Single();
        c.Prompt = H3Policy.Compile(source);
        c.OutputOverrides = new() { TakeCount = 2, Resolution = VideoResolution.Quick };
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions.Single();
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions.Single();
        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, new FakeAiSettingsStore(),
            new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), store);
        var submission = await capture.CaptureCompositionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            c.Id, c.Version, c.TakeCount, c.Seed, _ct, c.GenerationSetupVersion);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(VideoResolution.Quick, VideoResolutions.Selected(request.Snapshot.Shot));
        Assert.Equal(2, submission.Batch!.Candidates.Count);
        AiVideoJobPolicy.Validate(request);

        var preset = (await library.LoadAsync(_ct)).Setups.Single();
        Assert.Equal(1, preset.Settings.TakeCount); Assert.Null(preset.Settings.Resolution);
        c.OutputOverrides = null; preset.Settings.Apply(c);
        await store.SaveAsync(f.Project.Id, c, c.Version, _ct);
        c = (await store.LoadAsync(f.Project.Id, _ct)).Compositions.Single();
        Assert.Null(c.OutputOverrides); Assert.Equal(1, c.TakeCount);
        Assert.Equal(VideoResolution.Preview, VideoResolutions.Selected(c.Shot));
        // Captured requests keep the output selected when they were queued.
        Assert.Equal(VideoResolution.Quick, VideoResolutions.Selected(request.Snapshot.Shot));
    }
}
