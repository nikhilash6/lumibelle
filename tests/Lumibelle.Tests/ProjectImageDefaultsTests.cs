using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class TextModelStoreTests
{
    [Fact]
    public async Task ImageDefaultsPersistIndependentlyAndPreserveConcurrentPreferences()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Projects.CreateAsync(new("First"), ct); var other = await Projects.CreateAsync(new("Other"), ct);
        Assert.Null((await Preferences.LoadAsync(first.Id, ct)).ImageDefault);
        await Task.WhenAll(Preferences.SetImageDefaultAsync(first.Id, ImageWorkflow.Flux2Klein9bKv, 0, ct),
            Preferences.SetTextDefaultAsync(first.Id, Cloud, 0, ct),
            Preferences.SetLoraVisibilityAsync(first.Id, new() { HiddenTags = ["test"] }, 0, ct));
        var saved = await Preferences.LoadAsync(first.Id, ct);
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, saved.ImageDefault);
        Assert.Equal(Cloud, saved.TextDefault); Assert.Single(saved.LoraVisibility.HiddenTags);
        Assert.Null((await Preferences.LoadAsync(other.Id, ct)).ImageDefault);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Preferences.SetImageDefaultAsync(first.Id, ImageWorkflow.Krea2, 0, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Preferences.SetImageDefaultAsync(first.Id, (ImageWorkflow)999, 1, ct));
        Assert.Null((await Preferences.SetImageDefaultAsync(first.Id, null, 1, ct)).ImageDefault);
        Assert.Equal(Cloud, (await Preferences.LoadAsync(first.Id, ct)).TextDefault);
    }

    [Fact]
    public async Task ImageDefaultsResolveGlobalProjectAndExplicitRequestWithoutSilentFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var preferences = new FakeProjectAiPreferencesStore(); var id = Guid.NewGuid();
        var settings = new AiSettings { DefaultImageWorkflow = ImageWorkflow.Krea2 };
        Assert.Equal(ImageWorkflow.Krea2, await ProjectImageDefaults.ResolveAsync(id, null, settings, preferences, ct));
        preferences.Values[id] = new() { ProjectId = id, ImageDefault = ImageWorkflow.CodexImages };
        Assert.Equal(ImageWorkflow.CodexImages, await ProjectImageDefaults.ResolveAsync(id, null, settings, preferences, ct));
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, await ProjectImageDefaults.ResolveAsync(id, ImageWorkflow.Flux2Klein9bKv, settings, preferences, ct));
        preferences.LoadError = new WorkspaceStoreException("Unavailable preferences");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ProjectImageDefaults.ResolveAsync(id, null, settings, preferences, ct));
        Assert.Equal(ImageWorkflow.Krea2, await ProjectImageDefaults.ResolveAsync(id, ImageWorkflow.Krea2, settings, preferences, ct));
    }
}

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ImageCaptureUsesProjectDefaultButPreservesExplicitAndCapturedModels(bool editing)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Flux2Klein9bKv, editing);
        var ct = TestContext.Current.CancellationToken;
        f.Settings.Value = f.Settings.Value with { DefaultImageWorkflow = ImageWorkflow.Krea2 };
        var preferences = new FakeProjectAiPreferencesStore();
        preferences.Values[f.Project.Id] = new() { ProjectId = f.Project.Id, ImageDefault = ImageWorkflow.Flux2Klein9bKv };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, preferences: preferences);
        Task<AiJobSubmission> Capture(ImageWorkflow? workflow = null, AiSettings? captured = null) => editing
            ? capture.EditAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = workflow,
                SourceAssetId = f.AssetId, SourceImageId = f.References[0].ImageId, Prompt = "Change the lighting", AspectRatio = "1:1", Seed = 123 }, f.References, ct, captured)
            : capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = workflow, Prompt = "A room", AspectRatio = "1:1", Seed = 123 }, ct, captured);
        var submission = await Capture();
        var original = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, original.Settings.DefaultImageWorkflow);
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, original.Create?.Workflow ?? original.Edit!.Workflow);
        Assert.Equal(ImageWorkflow.Krea2, (await Capture(ImageWorkflow.Krea2)).Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!.Settings.DefaultImageWorkflow);
        Assert.Equal(ImageWorkflow.Krea2, (await Capture(captured: f.Settings.Value)).Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!.Settings.DefaultImageWorkflow);
        preferences.Values[f.Project.Id] = preferences.Values[f.Project.Id] with { ImageDefault = ImageWorkflow.CodexImages };
        // The worker consumes the immutable capture, even after the default has changed.
        await f.Worker.ExecuteAsync(await f.Claim(submission), submission.Snapshot, ct);
        Assert.Single(f.Graphs);
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Last().Generation!.Workflow);
    }
}
