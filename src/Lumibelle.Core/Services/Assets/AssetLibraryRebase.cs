using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

// Reconcile store-owned media changes with the last saved baseline before saving
// author edits. Competing changes to the same asset/image remain explicit conflicts.
public static class AssetLibraryRebase
{
    private static bool Same<T>(T a, T b) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(b, AtomicJsonFile.Options));
    private static ReferenceAsset Metadata(ReferenceAsset a) => a with { Images = [], Name = a.Name.Trim(), UpdatedUtc = default };
    public static bool HasMediaChanges(AssetLibrary baseline, AssetLibrary saved) =>
        !Same(baseline.Voices, saved.Voices) || !Same(baseline.VoiceTrash, saved.VoiceTrash) ||
        !Same(baseline.Reels, saved.Reels) || !Same(baseline.ReelDrafts, saved.ReelDrafts) || !Same(baseline.ReelTrash, saved.ReelTrash) || !Same(baseline.ReelPublications, saved.ReelPublications) ||
        !Same(baseline.ImagePublications, saved.ImagePublications) || !Same(baseline.Trash, saved.Trash) ||
        !baseline.Assets.SelectMany(a => a.Images.Select(i => $"{a.Id}/{i.Id}")).Order(StringComparer.Ordinal)
            .SequenceEqual(saved.Assets.SelectMany(a => a.Images.Select(i => $"{a.Id}/{i.Id}")).Order(StringComparer.Ordinal));
    public static AssetLibrary Merge(AssetLibrary baseline, AssetLibrary draft, AssetLibrary saved)
    {
        if (baseline.ProjectId != draft.ProjectId || saved.ProjectId != draft.ProjectId || saved.Revision < baseline.Revision)
            throw new WorkspaceConflictException();
        var merged = new List<ReferenceAsset>();
        foreach (var current in saved.Assets)
        {
            var original = baseline.Assets.SingleOrDefault(a => a.Id == current.Id);
            var local = draft.Assets.SingleOrDefault(a => a.Id == current.Id);
            if (original is null)
            {
                if (local is not null && !Same(local, current)) throw new WorkspaceConflictException();
                merged.Add(current); continue;
            }
            if (local is null) throw new WorkspaceConflictException();
            var localChanged = !Same(Metadata(original), Metadata(local));
            if (localChanged && !Same(Metadata(original), Metadata(current)) && !Same(Metadata(local), Metadata(current))) throw new WorkspaceConflictException();
            var images = new List<AssetImage>();
            foreach (var image in current.Images)
            {
                var before = original.Images.SingleOrDefault(i => i.Id == image.Id);
                var edited = local.Images.SingleOrDefault(i => i.Id == image.Id);
                if (before is null)
                {
                    if (edited is not null && !Same(edited, image)) throw new WorkspaceConflictException();
                    images.Add(image); continue;
                }
                if (edited is null) throw new WorkspaceConflictException();
                var changed = !Same(before, edited);
                if (changed && !Same(before, image) && !Same(edited, image)) throw new WorkspaceConflictException();
                images.Add(changed ? edited : image);
            }
            foreach (var removed in original.Images.Where(i => current.Images.All(c => c.Id != i.Id)))
                if (local.Images.SingleOrDefault(i => i.Id == removed.Id) is { } edited && !Same(edited, removed)) throw new WorkspaceConflictException();
            if (local.Images.Any(i => original.Images.All(o => o.Id != i.Id) && current.Images.All(c => c.Id != i.Id))) throw new WorkspaceConflictException();
            merged.Add((localChanged ? local : current) with { Images = images });
        }
        foreach (var local in draft.Assets.Where(a => saved.Assets.All(s => s.Id != a.Id)))
        {
            var before = baseline.Assets.SingleOrDefault(a => a.Id == local.Id);
            if (before is null) { if (local.Images.Count != 0) throw new WorkspaceConflictException(); merged.Add(local); }
            else if (!Same(before, local)) throw new WorkspaceConflictException();
        }
        // Reconcile order separately from metadata and media publication. A new
        // background asset must not undo a local move, or conceal a competing move.
        var common = baseline.Assets.Select(a => a.Id).Intersect(draft.Assets.Select(a => a.Id)).Intersect(saved.Assets.Select(a => a.Id)).ToHashSet();
        var beforeOrder = baseline.Assets.Select(a => a.Id).Where(common.Contains).ToArray();
        var localOrder = draft.Assets.Select(a => a.Id).Where(common.Contains).ToArray();
        var savedOrder = saved.Assets.Select(a => a.Id).Where(common.Contains).ToArray();
        if (!beforeOrder.SequenceEqual(localOrder))
        {
            if (!beforeOrder.SequenceEqual(savedOrder) && !localOrder.SequenceEqual(savedOrder)) throw new WorkspaceConflictException();
            var positions = draft.Assets.Select((a, index) => (a.Id, index)).ToDictionary(x => x.Id, x => x.index);
            merged = merged.OrderBy(a => positions.GetValueOrDefault(a.Id, int.MaxValue)).ToList();
        }
        return (saved with { Assets = merged }).Copy();
    }
}
