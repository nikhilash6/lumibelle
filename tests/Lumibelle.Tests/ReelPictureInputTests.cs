using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelKeyframesReachAssistAndGenerationAsOwnedPictures(bool environment)
    {
        var f = Fixture(); var media = new ReelPictureMedia();
        var assets = new FileAssetStore(f.Files, _clock, referenceVideos: media);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = environment ? "Room" : "Riley", Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        var library = await assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(32, 16));
        library = await assets.AddImageAsync(f.Project.Id, owner.Id, png, new("opening.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var source = new AssetReferenceReel { AssetId = owner.Id, Name = "Source coverage", UseGuidance = "Preserve the blue material.",
            Media = new(Guid.NewGuid(), new('A', 64), 100, 32, 16, 72, 24, 3, true), Keyframes = new() { Frames = [] } };
        source.Keyframes.Frames.Add(new() { Frame = new(source.Media.Id, source.Media.Sha256, 24, 1), Notes = "Window side", Crop = new() { Width = .5 } });
        source.Keyframes.Frames.Add(new() { Frame = new(source.Media.Id, source.Media.Sha256, 48, 2), Notes = "Door side" });
        library = await assets.SaveReelAsync(f.Project.Id, source, library.Revision, _ct);
        var draft = ReferenceReels.NewDraft(owner); draft.Duration = 3; draft.VoiceMode = ReelVoiceMode.Silent;
        draft.Images = [new() { AssetId = owner.Id, MediaId = library.Assets[0].Images[0].Id, Name = "Opening" }];
        draft.KeyframeReels = [new() { Media = source.Media, Name = source.Name, Description = source.UseGuidance, UseSoundtrack = false,
            Visuals = ReelVisuals.Keyframes, Keyframes = ShotCopy.Of(source.Keyframes) }];
        var pair = ReferenceReels.Preset(draft, library);
        var jobId = Guid.NewGuid(); draft.PendingJobId = jobId;
        draft = await assets.SaveDraftAsync(f.Project.Id, draft, 0, _ct);
        var shot = ReferenceReels.Inputs(draft); var pictures = await ProductionInputs.CaptureAsync(f.Project.Id, shot, assets, _ct, media);
        Assert.Equal(new[] { draft.Images[0].Id, source.Keyframes.Frames[0].Id, source.Keyframes.Frames[1].Id }, pictures.Select(p => p.Identity.BindingId));
        using (var cropped = SixLabors.ImageSharp.Image.Load(pictures[1].Bytes)) Assert.Equal(16, cropped.Width);
        var request = new ReelCompositionRequest(f.Project.Id, draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), pictures.Select(p => p.Identity).ToArray());
        var messages = ReferenceReels.Messages(request, pictures.Select(p => p.Bytes).ToArray());
        Assert.Equal(3, messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.Contains("Window side", messages[1].Text); Assert.Contains("Door side", messages[1].Text);
        Assert.Contains("<Picture 3>", pair.Prompt); Assert.DoesNotContain("<Video", pair.Prompt); Assert.DoesNotContain("<Audio", pair.Prompt);
        // A saved selection is independent of the reel's gallery entry and later defaults.
        await assets.TrashReelAsync(f.Project.Id, source.Id, (await assets.LoadAsync(f.Project.Id, _ct)).Revision, _ct);
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelComposition, Backend = AiBackend.OpenRouter, State = AiJobState.Completed,
            Target = new(f.Project.Id, owner.Id, ReelId: draft.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
        Assert.True(await assets.ApplyPairAsync(job, request, pair, true, _ct));
        draft = (await assets.LoadAsync(f.Project.Id, _ct)).ReelDrafts.Single();
        var generator = new Lumibelle.Testing.MockVideoGenerator(assets, f.Shots, media);
        var capture = new AiReelCapture(assets, assets, new FakeAiSettingsStore(), generator,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, new FakeProjectAiPreferencesStore(), referenceVideos: media);
        var submission = await capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision, 1, _ct);
        var captured = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(3, captured.Inputs.Count); Assert.All(captured.Inputs, i => Assert.Equal(VideoInputKind.Image, i.EffectiveKind));
        Assert.Equal(pictures.Select(p => p.Identity.Sha256), captured.Inputs.Select(i => i.Sha256));
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(captured.Snapshot, 42, "test", captured.Inputs.Select(i => new PreparedVideoInput(i.FileName, i.Audio)).ToArray()));
        Assert.DoesNotContain("LoadVideo", graph); Assert.DoesNotContain("VideoCondition", graph); Assert.DoesNotContain("LoadAudio", graph);
        library = await assets.LoadAsync(f.Project.Id, _ct);
        Assert.Single(library.Assets[0].Images); Assert.Empty(library.Reels); Assert.Single(library.ReelTrash);
        var invalid = draft.Copy(); invalid.KeyframeReels![0].UseSoundtrack = true;
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(invalid));
        invalid = draft.Copy(); invalid.KeyframeReels![0].Visuals = ReelVisuals.None;
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(invalid));
        invalid = draft.Copy(); invalid.Images = Enumerable.Range(0, 8).Select(_ => new ShotImageBinding { AssetId = owner.Id, MediaId = draft.Images[0].MediaId }).ToList();
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(invalid));
    }

    private sealed class ReelPictureMedia : IReferenceVideoStore
    {
        public Task<AssetMedia> OpenFrameAsync(Guid p, ReelFrameIdentity frame, H3Settings settings, CancellationToken ct = default)
            => Task.FromResult(new AssetMedia(new MemoryStream(AssetStoreTests.Png(32, 16)), "image/png", DateTimeOffset.UnixEpoch));
        public async Task PrepareAsync(Guid p, Shot shot, string directory, List<PreparedVideoInput> inputs, H3Settings h, CancellationToken ct = default)
        {
            foreach (var frame in ResolvedReferences.For(shot).Pictures.Where(p => p.Keyframe is not null)) {
                await using var source = await OpenFrameAsync(p, frame.Keyframe!.Frame, h, ct);
                var name = $"keyframe-{inputs.Count:D2}.png";
                await File.WriteAllBytesAsync(Path.Combine(directory, name), await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content, frame.Keyframe.Crop, ct), ct);
                inputs.Add(new(name, false));
            }
        }
        public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream c, string n, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
    }
}
