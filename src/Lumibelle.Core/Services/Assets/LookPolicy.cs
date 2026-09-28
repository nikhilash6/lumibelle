using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class LookPolicy
{
    public static bool Invalid(ReferenceAsset asset) => InvalidPreferred(asset.PreferredIdentityReferences) || asset.Looks is null ||
        asset.Category != AssetCategory.Character && (asset.Looks.Count > 0 || asset.PreferredIdentityReferences.Count > 0) ||
        asset.Looks.Any(l => l is null || l.Id == Guid.Empty || string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 200 ||
            l.Description is null || l.Description.Length > 12000 || l.PreservationGuidance is null || l.PreservationGuidance.Length > 12000 ||
            l.Evidence is null || l.Evidence.Any(e => e is null || e.Label is null || e.Excerpt is null) || InvalidPreferred(l.PreferredAppearanceReferences)) ||
        asset.Looks.Select(l => l.Id).Distinct().Count() != asset.Looks.Count ||
        asset.Looks.Select(l => l.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != asset.Looks.Count;

    private static bool InvalidPreferred(IReadOnlyList<PreferredImageReference>? items) => items is null || items.Count > 9 ||
        items.Any(i => i is null || i.ImageId == Guid.Empty || i.LookId == Guid.Empty || i.Name is null) || items.Select(i => i.ImageId).Distinct().Count() != items.Count;

    public static void ValidatePreferredChanges(ReferenceAsset? before, ReferenceAsset next, AssetLibrary library)
    {
        void Check(IReadOnlyList<PreferredImageReference> old, IReadOnlyList<PreferredImageReference> selected, Guid? look, bool identity)
        {
            // An unchanged unavailable default stays visible for explicit repair.
            if (old.SequenceEqual(selected)) return;
            if (next.Category != AssetCategory.Character && selected.Count > 0) throw new WorkspaceStoreException("Preferred references belong to characters and their looks.");
            foreach (var entry in selected)
            {
                var image = next.Images.FirstOrDefault(i => i.Id == entry.ImageId);
                if (image is null || image.LookId != entry.LookId || !identity && image.LookId != look ||
                    Find(next, image.LookId)?.Archived == true || lumibelle.Services.Shots.ReferenceSetups.IsContinuity(next.Id, image.Id, library))
                    throw new WorkspaceStoreException("Choose active images in the expected look for preferred references. Continuity frames remain individual shot choices.");
            }
        }
        Check(before?.PreferredIdentityReferences ?? [], next.PreferredIdentityReferences, null, true);
        foreach (var look in next.Looks) Check(before?.Looks.FirstOrDefault(l => l.Id == look.Id)?.PreferredAppearanceReferences ?? [], look.PreferredAppearanceReferences, look.Id, false);
    }

    public static CharacterLook? Find(ReferenceAsset asset, Guid? id) => asset.Looks.FirstOrDefault(l => l.Id == id);
    public static AssetLookContext Capture(ReferenceAsset asset, Guid? lookId)
    {
        var look = Find(asset, lookId);
        if (lookId is not null && look is null) throw new WorkspaceStoreException("That look no longer belongs to this character.");
        return new(asset.Id, asset.Name, asset.Description, asset.PreservationGuidance, look?.Id, look?.Name ?? "General / unassigned", look?.Description ?? "", look?.PreservationGuidance ?? "");
    }
    public static void ValidateTarget(AssetLibrary library, AssetLookContext? context, bool requireCurrent = false)
    {
        if (context is null) return;
        var asset = library.Assets.FirstOrDefault(a => a.Id == context.AssetId) ?? throw new WorkspaceStoreException("The target character or asset is unavailable.");
        var current = Capture(asset, context.LookId);
        if (Find(asset, context.LookId)?.Archived == true) throw new WorkspaceStoreException("This look is archived. Unarchive it or choose another target look.");
        if (requireCurrent && current != context) throw new WorkspaceStoreException("Character or look notes changed. Refresh and review the context before trying again.");
    }
    public static string SeedPrompt(ReferenceAsset asset, Guid? lookId) =>
        string.Join("\n\n", new[] { asset.Description, Find(asset, lookId)?.Description }.Where(t => !string.IsNullOrWhiteSpace(t)));

    public static void ValidateReferences(AssetLibrary library, IReadOnlyList<AssetReferenceLook> contexts)
    {
        if (contexts is null || contexts.Any(c => c is null || c.Reference is null || c.Context is null) || contexts.Select(c => c.Reference).Distinct().Count() != contexts.Count)
            throw new WorkspaceStoreException("Reference look context is invalid.");
        foreach (var captured in contexts)
        {
            var asset = library.Assets.FirstOrDefault(a => a.Id == captured.Reference.AssetId);
            var image = asset?.Images.FirstOrDefault(i => i.Id == captured.Reference.ImageId);
            if (asset is null || image is null || captured.Context.AssetId != asset.Id || image.LookId != captured.Context.LookId)
                throw new WorkspaceStoreException("A reference is unavailable or its look assignment changed. Review the inputs and start a new batch.");
            if (Find(asset, image.LookId)?.Archived == true)
                throw new WorkspaceStoreException("A reference look is archived. Unarchive it or replace the image.");
        }
    }

    public static string UniqueName(string original, IEnumerable<CharacterLook> looks, string suffix)
    {
        var names = looks.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var ending = n == 1 ? $" ({suffix})" : $" ({suffix} {n})";
            var name = original[..Math.Min(original.Length, 200 - ending.Length)] + ending;
            if (!names.Contains(name)) return name;
        }
    }

    public static IReadOnlyList<CharacterLook> RestoreLooks(ReferenceAsset active, ReferenceAsset archived, Guid? lookId)
    {
        if (lookId is null || Find(active, lookId) is not null) return active.Looks;
        var saved = Find(archived, lookId) ?? throw new WorkspaceStoreException("The saved look definition is missing.");
        // Preserve existing identities even when an author has reused a display name.
        var name = saved.Name;
        if (active.Looks.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))) name = UniqueName(name, active.Looks, "restored");
        return [.. active.Looks, saved.Copy() with { Name = name }];
    }
}
