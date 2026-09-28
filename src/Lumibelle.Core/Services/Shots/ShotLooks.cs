using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class ShotLooks
{
    public static Guid? ExpectedLook(ShotAppearance? appearance, ShotReferencePurpose? purpose) => purpose switch
    {
        ShotReferencePurpose.CurrentLook or ShotReferencePurpose.StartingLook => appearance?.LookId,
        ShotReferencePurpose.EndingLook => appearance?.EndLookId,
        _ => null
    };
    public static string? Issue(Shot shot, AssetLibrary library)
    {
        var characters = ShotReferences.Characters(shot);
        if (characters.Where(c => c.Appearance is not null).GroupBy(c => c.Appearance!.AssetId).Any(g => g.Count() > 1))
            return "Keep one appearance entry per character asset. Use a transition for a look change.";
        foreach (var character in characters.Where(c => c.Appearance is not null))
        {
            var appearance = character.Appearance!;
            var asset = library.Assets.FirstOrDefault(a => a.Id == appearance.AssetId && a.Category == AssetCategory.Character);
            if (asset is null) return $"{character.Name}: restore or choose the character asset.";
            foreach (var id in new Guid?[] { appearance.LookId, appearance.EndLookId }.Where(id => id is not null))
            {
                var look = LookPolicy.Find(asset, id);
                if (look is null) return $"{character.Name}: choose an available look.";
                if (look.Archived) return $"{character.Name}: unarchive {look.Name} or choose another look.";
            }
            if (appearance.LookId == appearance.EndLookId) return $"{character.Name}: choose distinct starting and ending looks.";
        }
        foreach (var binding in shot.Images)
        {
            var character = characters.FirstOrDefault(c => c.Id == binding.RepresentsId);
            var appearance = character?.Appearance;
            if (binding.Kind == ShotImageKind.AssetImage)
            {
                var asset = library.Assets.FirstOrDefault(a => a.Id == binding.AssetId);
                var image = asset?.Images.FirstOrDefault(i => i.Id == binding.MediaId);
                if (image is null)
                {
                    var moved = AssetImageLocations.RecordedOwner(library, new(binding.AssetId, binding.MediaId));
                    return moved is not null ? $"{binding.Name}: moved to {moved.Name}. Reselect it from that asset and review its character/look assignment."
                        : $"{binding.Name}: restore or replace the image reference.";
                }
                if (!binding.InferUsage && character is null && !ReferenceSetups.IsAnchor(binding) && characters.Any(c => c.Appearance?.AssetId == asset!.Id))
                    return $"{binding.Name}: choose Represents to link this image to the character appearance.";
                if (image.LookId != binding.LookId) return $"{binding.Name}: image look assignment changed. Review and use its current assignment, or replace the reference.";
                if (LookPolicy.Find(asset!, binding.LookId)?.Archived == true && binding.Purpose != ShotReferencePurpose.Identity)
                    return $"{binding.Name}: unarchive the reference look or use it only for identity.";
                if (appearance is not null && binding.AssetId != appearance.AssetId) return $"{binding.Name}: this reference belongs to another asset. Repair Represents or replace the image.";
            }
            if (!binding.InferUsage && appearance is not null && binding.Purpose is null) return $"{binding.Name}: choose Identity or an appearance purpose.";
            if (binding.Purpose is { } purpose)
            {
                if (character is null) return $"{binding.Name}: choose the character this reference represents.";
                if (purpose == ShotReferencePurpose.Identity) continue;
                if (appearance is null || purpose == ShotReferencePurpose.CurrentLook && appearance.EndLookId is not null ||
                    purpose is ShotReferencePurpose.StartingLook or ShotReferencePurpose.EndingLook && appearance.EndLookId is null)
                    return $"{binding.Name}: the reference purpose does not match the character's appearance mode.";
                if (binding.Kind == ShotImageKind.AssetImage && binding.LookId != ExpectedLook(appearance, purpose))
                    return $"{binding.Name}: this image belongs to another look. Replace it or use it only for identity.";
            }
        }
        return null;
    }
    public static void Validate(Shot shot, AssetLibrary assets) { if (Issue(shot, assets) is { } issue) throw new WorkspaceStoreException(issue); }
    public static IReadOnlyList<ShotAppearanceContext> Capture(Shot shot, AssetLibrary assets) => ShotReferences.Characters(shot).Where(c => c.Appearance is not null).Select(c =>
    {
        var a = c.Appearance!;
        var asset = assets.Assets.FirstOrDefault(x => x.Id == a.AssetId) ?? throw new WorkspaceStoreException("The character asset is unavailable.");
        return new ShotAppearanceContext(c.Id, LookPolicy.Capture(asset, a.LookId), a.EndLookId is { } end ? LookPolicy.Capture(asset, end) : null);
    }).ToArray();
    public static IEnumerable<string> Warnings(Shot shot)
    {
        foreach (var c in ShotReferences.Characters(shot).Where(c => c.Appearance is not null))
        {
            foreach (var purpose in c.Appearance!.EndLookId is null ? new[] { ShotReferencePurpose.CurrentLook } : [ShotReferencePurpose.StartingLook, ShotReferencePurpose.EndingLook])
                if (!shot.Images.Any(b => b.RepresentsId == c.Id && b.Purpose == purpose)) yield return $"{c.Name}: no visual reference assigned for {Label(purpose).ToLowerInvariant()}.";
        }
    }
    public static string Label(ShotReferencePurpose purpose) => purpose switch { ShotReferencePurpose.Identity => "Identity", ShotReferencePurpose.CurrentLook => "Current look", ShotReferencePurpose.StartingLook => "Starting look", _ => "Ending look" };
}
