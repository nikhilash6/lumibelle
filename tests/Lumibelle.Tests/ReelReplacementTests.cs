using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static ReferenceVideoMedia ReelMedia(int size, bool audio = true, int frames = 120) =>
        new(Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadRight(64, 'A'), 1000, size, size, frames, 24, frames / 24d, audio);

    [Fact]
    public async Task ReplacingAReelMovesShotsToItKeepsTheirChoicesAndReviewedPrompts()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var old = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley turn", Media = ReelMedia(384, frames: 240) };
        var sharp = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley turn", Media = ReelMedia(1024) };
        var silent = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley silent", Media = ReelMedia(1024, audio: false) };
        foreach (var reel in new[] { old, sharp, silent }) library = await f.Assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        Shot Titled(string title) { var s = Ready(); s.Title = title; return s; }
        var framed = Titled("Framed"); var full = Titled("Full"); var other = Titled("Other");
        await f.Shots.SaveAsync(f.Project.Id, [framed, full, other], 0, ct: _ct);
        var media = new ReplacementMedia();
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) },
            _clock, new FileAiJobStore(Path.Combine(_root, "replacement-jobs"), _clock), media);
        var d = await store.InitializeAsync(f.Project.Id, _ct);
        ProductionComposition For(ProductionDocument document, Shot shot) => document.Compositions.Single(c => c.ShotId == shot.Id);

        // Keyframes with a crop and note, the reel's soundtrack for the character's voice.
        var c = For(d, framed); var binding = new ShotVideoBinding { Media = old.Media, Name = "Custom name", Description = "Custom guidance", OwnerAssetId = owner.Id,
            OwnerCategory = AssetCategory.Character, Visuals = ReelVisuals.Keyframes, UseSoundtrack = true, AudioExcerpt = new(4, 3),
            Keyframes = new() { Frames = [new() { Frame = new(old.Media.Id, old.Media.Sha256, 24, 1), Crop = new() { X = .1, Y = .1, Width = .5, Height = .5 }, Notes = "Face" },
                new() { Frame = new(old.Media.Id, old.Media.Sha256, 72, 3) }] } };
        c.Shot.Videos = [binding];
        c.Shot.CharacterVoices = [new() { AssetId = owner.Id, CharacterName = owner.Name, Source = CharacterVoiceSource.Reel, SpeakerConfirmed = true,
            SourceName = binding.Name, ReelBindingId = binding.Id, ReelMediaId = old.Media.Id, Excerpt = binding.AudioExcerpt }];
        d = await store.SaveAsync(f.Project.Id, c, c.Version, _ct);
        // A full reel under a reviewed prompt.
        c = For(d, full); c.Shot.Videos = [new() { Media = old.Media, Name = old.Name, OwnerAssetId = owner.Id, OwnerCategory = AssetCategory.Character }];
        c.Prompt = "Riley turns to the window.";
        d = await store.SaveAsync(f.Project.Id, c, c.Version, _ct);
        d = await store.AcceptAsync(f.Project.Id, For(d, full).Id, For(d, full).Version, ct: _ct);
        var coverage = await f.Shots.LoadAsync(f.Project.Id, _ct);
        PromptReferenceState State(ProductionComposition value) => PromptReferenceFreshness.Compare(value,
            PromptReferenceFreshness.Fingerprint(value.Shot, ShotReferences.Resolve(value.Shot, library, coverage))).State;
        Assert.Equal(PromptReferenceState.Current, State(For(d, full)));
        var versions = d.Compositions.ToDictionary(x => x.ShotId, x => x.Version);

        var replacement = new ReelReplacement(f.Assets, store, f.Shots, media, new FakeAiSettingsStore(), null!, _clock);
        var silentPlan = await replacement.PlanAsync(f.Project.Id, old.Id, silent.Id, _ct);
        Assert.Equal(["Framed", "Full"], silentPlan.Shots.Select(s => s.Title));
        Assert.Contains("silent", silentPlan.Shots[0].Issue); Assert.Null(silentPlan.Shots[1].Issue);
        Assert.Equal(d.Revision, (await store.LoadAsync(f.Project.Id, _ct)).Revision);

        var result = await replacement.ApplyAsync(f.Project.Id, old.Id, sharp.Id, _ct);
        Assert.Equal(2, result.Ready);
        d = await store.LoadAsync(f.Project.Id, _ct);
        var moved = Assert.Single(For(d, framed).Shot.Videos);
        Assert.Equal(sharp.Media, moved.Media); Assert.Equal(binding.Id, moved.Id);
        Assert.Equal(("Custom name", "Custom guidance", ReelVisuals.Keyframes, true), (moved.Name, moved.Description, moved.EffectiveVisuals, moved.UseSoundtrack));
        Assert.Equal(new ReelAudioExcerpt(2, 3), moved.AudioExcerpt);
        Assert.Equal([new ReelFrameIdentity(sharp.Media.Id, sharp.Media.Sha256, 24, 1), new(sharp.Media.Id, sharp.Media.Sha256, 72, 3)],
            moved.Keyframes!.Frames.Select(k => k.Frame));
        Assert.Equal((new ImageCropRegion { X = .1, Y = .1, Width = .5, Height = .5 }, "Face"), (moved.Keyframes.Frames[0].Crop, moved.Keyframes.Frames[0].Notes));
        var voice = Assert.Single(For(d, framed).Shot.CharacterVoices!);
        Assert.Equal((sharp.Media.Id, moved.AudioExcerpt), (voice.ReelMediaId, voice.Excerpt));
        CharacterVoices.Validate(For(d, framed).Shot);
        Assert.Equal(sharp.Media, Assert.Single(For(d, full).Shot.Videos).Media);
        Assert.Equal(PromptReferenceState.Current, State(For(d, full)));
        Assert.Equal(2, For(d, full).History.Count);
        // Open editors of the changed shots must reload; the other shot is untouched.
        Assert.True(For(d, framed).Version > versions[framed.Id] && For(d, full).Version > versions[full.Id]);
        Assert.Equal(versions[other.Id], For(d, other).Version);
        Assert.Empty((await replacement.PlanAsync(f.Project.Id, old.Id, sharp.Id, _ct)).Shots);
    }

    [Fact]
    public async Task ReelReplacementRejectsAnotherAssetsReelAndKeyframesThatCollapse()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var stranger = owner with { Id = Guid.NewGuid(), Name = "Mira" };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner, stranger] }, 0, _ct);
        var old = new AssetReferenceReel { AssetId = owner.Id, Name = "Long", Media = ReelMedia(384) };
        var shortReel = new AssetReferenceReel { AssetId = owner.Id, Name = "Short", Media = ReelMedia(1024, frames: 48) };
        var theirs = new AssetReferenceReel { AssetId = stranger.Id, Name = "Mira", Media = ReelMedia(1024) };
        foreach (var reel in new[] { old, shortReel, theirs }) library = await f.Assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var media = new ReplacementMedia();
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) },
            _clock, new FileAiJobStore(Path.Combine(_root, "replacement-reject-jobs"), _clock), media);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        // Both keyframes lie past the end of the shorter reel.
        c.Shot.Videos = [new() { Media = old.Media, Name = old.Name, OwnerAssetId = owner.Id, Visuals = ReelVisuals.Keyframes,
            Keyframes = new() { Frames = [new() { Frame = new(old.Media.Id, old.Media.Sha256, 96, 4) }, new() { Frame = new(old.Media.Id, old.Media.Sha256, 110, 110 / 24d) }] } }];
        await store.SaveAsync(f.Project.Id, c, c.Version, _ct);
        var replacement = new ReelReplacement(f.Assets, store, f.Shots, media, new FakeAiSettingsStore(), null!, _clock);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => replacement.PlanAsync(f.Project.Id, old.Id, theirs.Id, _ct));
        var result = await replacement.ApplyAsync(f.Project.Id, old.Id, shortReel.Id, _ct);
        Assert.Equal(0, result.Ready); Assert.Contains("same frame", Assert.Single(result.Shots).Issue);
        Assert.Equal(old.Media, (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Shot.Videos[0].Media);
    }

    [Fact]
    public async Task ReplacingARefModReelCapturesTheNewReelsFramesOnTheSameCanvas()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var old = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley turn", Media = ReelMedia(384) };
        var sharp = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley turn", Media = ReelMedia(1024) };
        foreach (var reel in new[] { old, sharp }) library = await f.Assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var media = new ReplacementMedia(); var settings = new FakeAiSettingsStore();
        var refMods = new ReelRefModPreparation(f.Assets, media, new ReelRefModStore(f.Files), settings);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) },
            _clock, new FileAiJobStore(Path.Combine(_root, "replacement-refmod-jobs"), _clock), media);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Shot.Videos = [new() { Media = old.Media, Name = old.Name, OwnerAssetId = owner.Id, Visuals = ReelVisuals.RefMod,
            Keyframes = new() { Frames = [new() { Frame = new(old.Media.Id, old.Media.Sha256, 12, .5) }, new() { Frame = new(old.Media.Id, old.Media.Sha256, 60, 2.5) }] } }];
        c.Shot = await refMods.CaptureAsync(f.Project.Id, c.Shot, _ct);
        var before = c.Shot.Videos[0].RefMod!;
        await store.SaveAsync(f.Project.Id, c, c.Version, _ct);

        var result = await new ReelReplacement(f.Assets, store, f.Shots, media, settings, refMods, _clock).ApplyAsync(f.Project.Id, old.Id, sharp.Id, _ct);
        Assert.Equal(1, result.Ready);
        var moved = (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Shot.Videos[0];
        Assert.Equal(sharp.Media, moved.Media);
        Assert.True(ReelRefMods.Matches(moved, moved.RefMod));
        Assert.NotEqual(before.Recipe.Selection, moved.RefMod!.Recipe.Selection);
        Assert.Equal((before.Recipe.Width, before.Recipe.Height), (moved.RefMod.Recipe.Width, moved.RefMod.Recipe.Height));
        ReferenceVideos.Validate((await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Shot, true);
    }

    private sealed class ReplacementMedia : IReferenceVideoStore
    {
        public Task<AssetMedia> OpenFrameAsync(Guid p, ReelFrameIdentity frame, H3Settings s, CancellationToken ct = default)
        {
            using var image = new Image<Rgb24>(64, 64, new Rgb24((byte)frame.Index, 90, 160)); var png = new MemoryStream();
            image.SaveAsPng(png); png.Position = 0;
            return Task.FromResult(new AssetMedia(png, "image/png", DateTimeOffset.UtcNow));
        }
        public Task<ReelFrameCatalog> FrameCatalogAsync(Guid p, ReferenceVideoMedia m, H3Settings s, CancellationToken ct = default) =>
            Task.FromResult(new ReelFrameCatalog(m.Sha256, false, Enumerable.Range(0, m.Frames).Select(i => i / m.Fps).ToArray()));
        public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream c, string n, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
        public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
