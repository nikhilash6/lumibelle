using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed record ReelReplacementShot(Guid ShotId, string Title, string? Issue);
public sealed record ReelReplacementPlan(IReadOnlyList<ReelReplacementShot> Shots)
{
    public int Ready => Shots.Count(s => s.Issue is null);
}

// Puts one of an asset's reels in place of another in every shot that uses it, such as
// a reel regenerated at a higher resolution. Each shot keeps its reel name, guidance,
// visual mode and voice mapping; keyframes move to the same moments in the new reel.
// Takes and captured requests keep the reel they used.
public sealed class ReelReplacement(IAssetStore assets, IProductionStore production, IShotStore shots,
    IReferenceVideoStore media, IAiSettingsStore settings, ReelRefModPreparation refMods, TimeProvider clock)
{
    public Task<ReelReplacementPlan> PlanAsync(Guid project, Guid from, Guid to, CancellationToken ct = default) =>
        RunAsync(project, from, to, false, ct);
    // Replaces the reel in every shot that can take it, or only in the given shots, in one save,
    // and reports the shots it skipped.
    public Task<ReelReplacementPlan> ApplyAsync(Guid project, Guid from, Guid to, CancellationToken ct = default,
        IReadOnlyCollection<Guid>? shots = null) => RunAsync(project, from, to, true, ct, shots);

    private async Task<ReelReplacementPlan> RunAsync(Guid project, Guid from, Guid to, bool apply, CancellationToken ct,
        IReadOnlyCollection<Guid>? only = null)
    {
        var library = await assets.LoadAsync(project, ct);
        var old = library.Reels.SingleOrDefault(r => r.Id == from)
            ?? throw new WorkspaceStoreException("The reel to replace is unavailable. Restore it from Trash first.");
        var next = library.Reels.SingleOrDefault(r => r.Id == to)
            ?? throw new WorkspaceStoreException("The replacement reel is unavailable. Restore it from Trash first.");
        if (old.AssetId != next.AssetId) throw new WorkspaceStoreException("Replace a reel only with another reel of the same asset.");
        if (old.Media.Id == next.Media.Id) throw new WorkspaceStoreException("Choose a different reel to replace.");
        var document = await production.LoadAsync(project, ct);
        var coverage = await shots.LoadAsync(project, ct);
        ReelFrameCatalog? catalog = null;
        async Task<ReelFrameCatalog> Catalog() =>
            catalog ??= await media.FrameCatalogAsync(project, next.Media, (await settings.LoadAsync(ct)).H3, ct);
        var planned = new List<ReelReplacementShot>(); var replaced = new List<ShotProductionContent>();
        foreach (var shot in coverage.Shots.Where(s => only?.Contains(s.Id) != false))
        {
            if (document.Compositions.FirstOrDefault(c => c.ShotId == shot.Id) is not { } composition ||
                !composition.Shot.Videos.Any(v => v.Media.Id == old.Media.Id)) continue;
            try
            {
                replaced.Add(await ReplaceAsync(project, composition, old, next, Catalog, library, coverage, apply, ct));
                planned.Add(new(shot.Id, shot.Title, null));
            }
            catch (WorkspaceStoreException e) { planned.Add(new(shot.Id, shot.Title, e.Message)); }
        }
        if (apply && replaced.Count > 0) await production.ReplaceShotContentAsync(project, document.Revision, replaced, ct);
        return new(planned);
    }

    private async Task<ShotProductionContent> ReplaceAsync(Guid project, ProductionComposition composition, AssetReferenceReel old,
        AssetReferenceReel next, Func<Task<ReelFrameCatalog>> catalog, AssetLibrary library, ShotDocument coverage, bool apply, CancellationToken ct)
    {
        var edited = composition.Copy(); var shot = edited.Shot;
        var replacement = await ReplaceAsync(shot, old.Media.Id, next.Media, catalog);
        if (apply && replacement.EffectiveVisuals == ReelVisuals.RefMod)
        {
            // Capture only this binding's frames: other references in the shot must not block it.
            var probe = new Shot { Videos = [ShotCopy.Of(replacement) with { UseSoundtrack = false, Speaker = null }] };
            replacement.RefMod = (await refMods.CaptureAsync(project, probe, ct)).Videos[0].RefMod;
        }
        // The same subject in the same shot: a prompt reviewed against the old reel stays reviewed.
        if (Reviewed(composition, library, coverage) && edited.Accepted is { } accepted)
        {
            var revision = accepted with { Id = Guid.NewGuid(), CreatedUtc = clock.GetUtcNow(),
                ReferenceFingerprint = PromptReferenceFreshness.Fingerprint(shot, ShotReferences.Resolve(shot, library, coverage)) };
            edited.History.Add(revision); edited.AcceptedRevisionId = revision.Id;
        }
        return ShotProductionContent.From(edited);
    }

    // Puts the new media in place of a shot's attachment of the old reel, keeping the attachment's
    // identity, name, guidance, visual mode, soundtrack and speaker. A RefMod keeps its canvas and
    // is captured again from the new frames. Throws when the shot cannot take the new reel.
    public static async Task<ShotVideoBinding> ReplaceAsync(Shot shot, Guid oldMedia, ReferenceVideoMedia next, Func<Task<ReelFrameCatalog>> catalog)
    {
        if (shot.Videos.Any(v => v.Media.Id == next.Id)) throw new WorkspaceStoreException("This shot already uses the replacement reel.");
        var index = shot.Videos.FindIndex(v => v.Media.Id == oldMedia);
        if (index < 0) throw new WorkspaceStoreException("This shot no longer uses the reel.");
        var binding = shot.Videos[index];
        if (binding.UseSoundtrack && !next.HasAudio)
            throw new WorkspaceStoreException("This shot uses the reel's soundtrack, and the replacement reel is silent.");
        var replacement = ShotCopy.Of(binding) with { Media = next };
        if (binding.Keyframes is { } frames) replacement.Keyframes = Remap(frames, next, await catalog());
        if (binding.AudioExcerpt is { } excerpt) replacement.AudioExcerpt = Fit(excerpt, next);
        var videos = shot.Videos.ToList(); videos[index] = replacement;
        var voices = ShotCopy.Of(shot.CharacterVoices);
        // A reel voice choice names the binding and its media; the binding keeps its identity.
        foreach (var choice in voices ?? [])
            if (choice.ReelBindingId == binding.Id && choice.ReelMediaId == oldMedia)
            { choice.ReelMediaId = next.Id; if (choice.Excerpt is { } voice) choice.Excerpt = Fit(voice, next); }
        ReferenceVideos.Validate(ShotCopy.Of(shot) with { Videos = videos, CharacterVoices = voices });
        shot.Videos = videos; shot.CharacterVoices = voices;
        return replacement;
    }

    private static bool Reviewed(ProductionComposition composition, AssetLibrary library, ShotDocument coverage)
    {
        try
        {
            var fingerprint = PromptReferenceFreshness.Fingerprint(composition.Shot, ShotReferences.Resolve(composition.Shot, library, coverage));
            return PromptReferenceFreshness.Compare(composition, fingerprint).State == PromptReferenceState.Current;
        }
        catch (Exception e) when (e is WorkspaceStoreException or InvalidOperationException) { return false; }
    }

    // Keyframes keep their crops and notes at the nearest moment of the new reel.
    public static ReelKeyframeSet Remap(ReelKeyframeSet frames, ReferenceVideoMedia media, ReelFrameCatalog catalog)
    {
        var times = catalog.Timestamps;
        if (times.Count == 0) throw new WorkspaceStoreException("The replacement reel's frames are unavailable.");
        var set = new ReelKeyframeSet { Frames = frames.Frames.Select(f =>
        {
            var index = Enumerable.Range(0, times.Count).MinBy(i => Math.Abs(times[i] - f.Frame.Seconds));
            return f with { Frame = new(media.Id, catalog.Source, index, times[index]) };
        }).ToList() };
        if (set.Frames.Select(f => f.Frame.Index).Distinct().Count() != set.Frames.Count)
            throw new WorkspaceStoreException("Two keyframes would land on the same frame of the replacement reel. Replace this reel in the shot's Manage references instead.");
        ReferenceVideos.ValidateKeyframes(media, set);
        return set;
    }
    private static ReelAudioExcerpt Fit(ReelAudioExcerpt excerpt, ReferenceVideoMedia media)
    {
        var duration = Math.Min(excerpt.Duration, media.Duration);
        return excerpt with { Start = Math.Max(0, Math.Min(excerpt.Start, media.Duration - duration)), Duration = duration };
    }
}
