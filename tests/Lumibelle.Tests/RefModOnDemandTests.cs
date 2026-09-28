using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

// Real local PNG fitting and file persistence; source-frame reads are simulated.
// The queue tests below reuse the existing HTTP/ComfyUI execution test boundary.
internal sealed class RefModOnDemandFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Lumibelle.RefModOnDemand", Guid.NewGuid().ToString("N"));
    public ProjectInfo Project { get; }
    public ProjectFiles Files { get; }
    public FakeAssetStore Assets { get; }
    public FakeAiSettingsStore Settings { get; } = new() { Value = new() { ComfyUrl = "http://not-running.invalid:8188" } };
    public RefModOnDemandMedia Media { get; } = new();
    public ReelRefModStore Store { get; }
    public ReelRefModPreparation Preparation { get; }
    public AssetLibrary Library => Assets.Library;
    public ReferenceAsset Owner => Library.Assets[0];
    public AssetReferenceReel Reel => Library.Reels[0];
    public Shot Shot { get; }
    public RefModOnDemandFixture(int count = 3)
    {
        Project = FakeProjectStore.Project("RefMod on demand") with { Id = Guid.NewGuid() };
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == Project.Id ? Project : null) };
        Directory.CreateDirectory(Path.Combine(Root, "App_Data", "Projects", Project.Id.ToString()));
        Files = new(Options.Create(new ProjectStorageOptions()), new StorageTestEnvironment(Root), projects);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Category = AssetCategory.Character, Name = "Riley" };
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 100, 640, 640, 124, 24, 124 / 24d, true);
        var frames = new ReelKeyframeSet { Frames = Enumerable.Range(0, count).Select(i => new ReelKeyframe {
            Frame = new(media.Id, media.Sha256, i, i / 24d), Notes = "View " + i }).ToList() };
        for (var i = 0; i < count; i++) {
            using var image = new Image<Rgb24>(i % 2 == 0 ? 160 : 96, i % 2 == 0 ? 96 : 160, new Rgb24((byte)(40 + i * 20), 100, 160));
            using var stream = new MemoryStream(); image.SaveAsPng(stream); Media.Pixels[i] = stream.ToArray();
        }
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley angles", Media = media, Keyframes = frames };
        Assets = new(Project.Id) { Library = new() { ProjectId = Project.Id, Assets = [owner], Reels = [reel] } };
        Shot = new() { Title = "Riley speaks", Description = "A held portrait", Duration = 5,
            ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(),
            Dialogue = [new() { Speaker = owner.Name, Language = "English", Text = "Hello." }],
            Videos = [new() { Media = media, Name = reel.Name, OwnerAssetId = owner.Id, OwnerCategory = owner.Category,
                Visuals = ReelVisuals.RefMod, Keyframes = ShotCopy.Of(frames) }] };
        Store = new(Files); Preparation = new(Assets, Media, Store, Settings);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

internal sealed class RefModOnDemandMedia : IReferenceVideoStore
{
    public Dictionary<int, byte[]> Pixels { get; } = [];
    public int Reads, Suggestions, Validations;
    public bool FailReads, ChangedDuringRead;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Hold;
    public async Task PrepareFramesAsync(Guid p, IEnumerable<ReelFrameIdentity> frames, H3Settings h, CancellationToken ct = default)
    {
        if (!frames.Any()) return;
        Entered.TrySetResult();
        if (Hold is not null) await Hold.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
    }
    public Task<AssetMedia> OpenFrameAsync(Guid p, ReelFrameIdentity f, H3Settings h, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Reads++;
        if (FailReads) throw new WorkspaceStoreException("Source frame unavailable");
        return Task.FromResult(new AssetMedia(new MemoryStream(Pixels[f.Index], writable: false), "image/png", DateTimeOffset.UtcNow));
    }
    public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Validations++;
        if (ChangedDuringRead && Reads > 0) throw new WorkspaceStoreException("Source reel changed during capture");
        return Task.CompletedTask;
    }
    public Task<ReelKeyframeSet> SuggestFramesAsync(Guid p, ReferenceVideoMedia m, int count, H3Settings h, CancellationToken ct = default)
    {
        Suggestions++;
        return Task.FromResult(new ReelKeyframeSet { Frames = Pixels.Keys.Order().Take(count)
            .Select(i => new ReelKeyframe { Frame = new(m.Id, m.Sha256, i, i / 24d) }).ToList() });
    }
    public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream c, string n, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
    public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class RefModOnDemandTests
{
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AtomicJsonFile.Options);

    [Fact]
    public async Task CapturedRefModInspectionImagesPassSavedRequestValidation()
    {
        using var f = new RefModOnDemandFixture(3);
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var request = new PromptCompositionRequest(f.Project.Id, Guid.NewGuid(), 1, "context", "source", accepted,
            "Scene", [], [], [], [], "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var frames = await f.Store.InspectionAsync(f.Project.Id, accepted, _ct);
        var messages = PromptComposer.BuildMessages(request, [], frames);
        var snapshot = JsonSerializer.SerializeToElement(new AiTextJobRequest(2, AiJobKind.PromptComposition, request.Model, false,
            new(), ProductionPolicy.Profile, .7f, 42, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options),
            messages.Select(AiTextMessage.Capture).ToArray()), AtomicJsonFile.Options);
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.PromptComposition, Backend = request.Model.Backend,
            Target = new(f.Project.Id, ShotId: accepted.Id, CompositionId: request.CompositionId), ProjectName = "Test",
            TargetName = "Compose", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Waiting,
            CreatedUtc = DateTimeOffset.UtcNow };
        var read = AiTextJobHandler.Read(job, snapshot);
        Assert.Equal(frames.Count, read.Messages.Sum(m => m.Parts.Count(p => p.Image is not null)));
    }

    [Theory] [InlineData(2)] [InlineData(6)] [InlineData(9)]
    public async Task ApplyCapturesSourcesOfflineWithoutABuildReceipt(int count)
    {
        using var f = new RefModOnDemandFixture(count); var before = Json(f.Shot);
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var reference = Assert.Single(accepted.Videos).RefMod!;
        Assert.NotNull(reference); Assert.True(ReelRefMods.Matches(accepted.Videos[0], reference));
        Assert.Equal(before, Json(f.Shot)); Assert.Null(f.Shot.Videos[0].RefMod);
        Assert.Equal(count, reference.Recipe.LatentFrames); Assert.Equal(640, reference.Recipe.Width);
        Assert.Null(await f.Store.FindAsync(f.Project.Id, reference.Recipe.Key, reference.ComfyUrl, _ct));
        Assert.False(Directory.Exists(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "refmod-builds")));
        for (var i = 0; i < count; i++) {
            var bytes = await f.Store.PreviewAsync(f.Project.Id, reference, i, _ct); var info = Image.Identify(bytes);
            Assert.Equal(640, info.Width); Assert.Equal(640, info.Height);
            Assert.Equal(reference.Recipe.FrameHashes[i], Convert.ToHexString(SHA256.HashData(bytes)));
        }
        Assert.Empty(ResolvedReferences.For(accepted).Pictures); Assert.Single(ResolvedReferences.For(accepted).Videos);
        var inspected = await f.Store.InspectionAsync(f.Project.Id, accepted, _ct);
        Assert.Equal(count, inspected.Count); Assert.All(inspected, i => Assert.Equal(1, i.VideoNumber));
    }

    [Fact]
    public async Task AcceptedSelectionIsStableAndDoesNotRepeatFrameReads()
    {
        using var f = new RefModOnDemandFixture();
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var before = Json(accepted); var reads = f.Media.Reads;
        f.Media.FailReads = true; f.Settings.Value = f.Settings.Value with { ComfyUrl = "http://another.invalid:8188" };
        var reopened = await f.Preparation.CaptureAsync(f.Project.Id, accepted, _ct);
        Assert.Equal(before, Json(reopened)); Assert.Equal(reads, f.Media.Reads);
    }

    [Theory] [InlineData("order")] [InlineData("crop")]
    public async Task FrameChangesCaptureANewRecipeAndRetainOldSources(string change)
    {
        using var f = new RefModOnDemandFixture();
        var first = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct); var original = Json(first);
        var prior = first.Videos[0].RefMod!; var oldBytes = await f.Store.PreviewAsync(f.Project.Id, prior, 0, _ct);
        var changed = first.Copy();
        if (change == "order") changed.Videos[0].Keyframes!.Frames.Reverse();
        else changed.Videos[0].Keyframes!.Frames[0].Crop = new() { Width = .5, X = .25 };
        var next = await f.Preparation.CaptureAsync(f.Project.Id, changed, _ct);
        Assert.NotEqual(prior.Recipe.Key, next.Videos[0].RefMod!.Recipe.Key);
        Assert.Equal(original, Json(first)); Assert.Equal(oldBytes, await f.Store.PreviewAsync(f.Project.Id, prior, 0, _ct));
        Assert.True(ReelRefMods.Matches(next.Videos[0], next.Videos[0].RefMod));
    }

    [Fact]
    public async Task ProseEditsAndSeparateVoiceDoNotRebuildVisualInputs()
    {
        using var f = new RefModOnDemandFixture();
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct); var key = accepted.Videos[0].RefMod!.Recipe.Key;
        accepted.Videos[0].Description = "Keep the same coat"; accepted.Videos[0].Keyframes!.Frames[0].Notes = "Sharper face";
        accepted.Voices.Add(new() { AssetId = f.Owner.Id, VoiceId = Guid.NewGuid(), Speaker = "Riley", Start = 2, Duration = 3 });
        var before = Json(accepted); var reads = f.Media.Reads;
        var result = await f.Preparation.CaptureAsync(f.Project.Id, accepted, _ct);
        Assert.Equal(before, Json(result)); Assert.Equal(key, result.Videos[0].RefMod!.Recipe.Key); Assert.Equal(reads, f.Media.Reads);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(10)]
    public async Task UnsupportedFrameCountsFailWithoutFallback(int count)
    {
        using var f = new RefModOnDemandFixture(count); var before = Json(f.Shot);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct));
        Assert.Equal(0, f.Media.Reads); Assert.Equal(before, Json(f.Shot));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrChangingSourcesNeverBecomeAcceptedInputs(bool changed)
    {
        using var f = new RefModOnDemandFixture(); var before = Json(f.Shot);
        f.Media.FailReads = !changed; f.Media.ChangedDuringRead = changed;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct));
        Assert.Equal(before, Json(f.Shot));
        Assert.False(Directory.Exists(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "refmod-previews")));
    }

    [Fact]
    public async Task CancelDuringPreparationDoesNotAcceptTheReference()
    {
        using var f = new RefModOnDemandFixture(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        f.Media.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); var before = Json(f.Shot);
        var task = f.Preparation.CaptureAsync(f.Project.Id, f.Shot, cancellation.Token);
        await f.Media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(before, Json(f.Shot)); Assert.Equal(0, f.Media.Reads);
    }

    [Fact]
    public async Task SourceAcceptanceRejectsCorruptionInsteadOfOverwritingIt()
    {
        using var f = new RefModOnDemandFixture(); var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var reference = accepted.Videos[0].RefMod!;
        var pixels = new List<byte[]>();
        for (var i = 0; i < reference.Recipe.LatentFrames; i++) pixels.Add(await f.Store.PreviewAsync(f.Project.Id, reference, i, _ct));
        var path = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "refmod-previews", reference.Recipe.Key, ReelRefModStore.FrameName(0));
        await File.WriteAllBytesAsync(path, [1, 2, 3], _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.AcceptSourcesAsync(f.Project.Id, reference.Recipe, pixels, _ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, _ct));
    }

    [Theory] [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.FullReel)]
    public async Task NativeModesDoNotPrepareRefMods(ReelVisuals mode)
    {
        using var f = new RefModOnDemandFixture(); f.Shot.Videos[0].Visuals = mode;
        var before = Json(f.Shot); var result = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        Assert.Equal(before, Json(result)); Assert.Equal(0, f.Media.Reads);
    }
}

public sealed partial class ShotTests
{
    [Fact]
    public async Task RefModOnDemandFirstGenerationBuildsWithoutAnyPriorBuildJob()
    {
        using var source = new RefModOnDemandFixture(6);
        var accepted = await source.Preparation.CaptureAsync(source.Project.Id, source.Shot, _ct);
        var binding = accepted.Videos[0]; var snapshot = Snapshot(source.Project.Id, accepted) with { ComfyUrl = CacheServerA };
        var jobs = new FileAiJobStore(Path.Combine(source.Root, "jobs"), TimeProvider.System);
        var id = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI,
            new(source.Project.Id, ShotId: accepted.Id), "Project", "On demand", Guid.NewGuid(), new { snapshot })
            with { Batch = AiBatchDefinition.Create(id, 2, 42) }, _ct);
        var header = (await jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var context = new AiJobContext(header, false, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        using var http = new CacheTransport(); var cache = new ComfyRefModCache(source.Store, new(http), new(http));
        var before = JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options);
        var prepared = await cache.EnsureAsync(context, snapshot, ComfyMultiTakeWorkflow.Operation, _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(6, http.Uploaded.Count);
        Assert.All(http.Requests, r => Assert.Equal(CacheServerA, r.Server));
        Assert.Single((await jobs.ReadAsync(_ct)).Jobs); // Only the user's video job.
        Assert.NotEqual(binding.RefMod!.BuildId, prepared[binding.Id].BuildId);
        Assert.Equal(before, JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options));
        Assert.Equal(binding.RefMod.Recipe.FrameHashes, http.Uploaded.Values.Select(p => Convert.ToHexString(SHA256.HashData(p))));
        var next = await cache.EnsureAsync(context, snapshot, "candidate/" + Guid.NewGuid(), _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(prepared[binding.Id].FileName, next[binding.Id].FileName);
    }
}
