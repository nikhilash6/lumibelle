using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Services.Shots;

public static class ShotReferences
{
    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Older shots have only speaker names. Derive repeatable IDs without modifying a loaded draft.
    public static IReadOnlyList<ShotCharacter> Characters(Shot shot)
    {
        var characters = shot.Characters.ToList();
        foreach (var line in shot.Dialogue.Where(d => !string.IsNullOrWhiteSpace(d.Speaker)))
            if (!characters.Any(c => SameName(c.Name, line.Speaker)))
            {
                var id = line.Id;
                if (characters.Any(c => c.Id == id))
                    id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{line.Id}:{line.Speaker.Trim().ToUpperInvariant()}"))[..16]);
                characters.Add(new(id, line.Speaker.Trim()));
            }
        return characters;
    }
    public static ShotCharacter? Character(Shot shot, string speaker) => Characters(shot).FirstOrDefault(c => SameName(c.Name, speaker));
    public static void RetainCharacters(Shot shot) => shot.Characters = Characters(shot).ToList();

    public static void ChangeSpeaker(Shot shot, Guid lineId, string name)
    {
        RetainCharacters(shot);
        var line = shot.Dialogue.Single(d => d.Id == lineId);
        var current = Character(shot, line.Speaker);
        var target = Character(shot, name);
        name = name.Trim();
        if (current is not null && name.Length > 0 && (target is null || target.Id == current.Id))
        {
            // Renaming a speaker retains visual bindings and voice assignments.
            shot.Characters[shot.Characters.FindIndex(c => c.Id == current.Id)] = current with { Name = name };
            foreach (var d in shot.Dialogue.Where(d => SameName(d.Speaker, current.Name))) d.Speaker = name;
            foreach (var v in shot.Voices.Where(v => SameName(v.Speaker, current.Name))) v.Speaker = name;
        }
        else line.Speaker = name; // Choosing another existing speaker changes this line only.
        RetainCharacters(shot);
    }

    public static ShotReferenceGuidance Resolve(ShotImageBinding binding, AssetLibrary assets, ShotDocument shots)
    {
        var assetDefault = "";
        var imageDefault = "";
        var lookDefault = "";
        if (binding.Kind == ShotImageKind.AssetImage)
        {
            var asset = assets.Assets.FirstOrDefault(a => a.Id == binding.AssetId);
            var image = asset?.Images.FirstOrDefault(i => i.Id == binding.MediaId);
            var trash = assets.Trash.FirstOrDefault(t => t.Asset.Id == binding.AssetId && t.Image.Id == binding.MediaId);
            assetDefault = (asset ?? trash?.Asset)?.PreservationGuidance ?? "";
            imageDefault = (image ?? trash?.Image)?.PreservationGuidance ?? "";
            var owner = asset ?? trash?.Asset;
            var look = owner is null ? null : LookPolicy.Find(owner, binding.LookId);
            if (look is not null) lookDefault = string.Join("\n", new[] { look.Description, look.PreservationGuidance }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        // Existing per-shot notes remain an override. An explicit empty override suppresses defaults.
        return new(binding.Id, ReferenceSetups.IsAnchor(binding) ? "" : assetDefault, binding.Purpose == ShotReferencePurpose.Identity ? "" : imageDefault,
            binding.PreservationOverride ?? (string.IsNullOrWhiteSpace(binding.Notes) ? null : binding.Notes))
            { LookDefault = binding.Purpose == ShotReferencePurpose.Identity || ReferenceSetups.IsAnchor(binding) ? "" : lookDefault,
                Phase = binding.Purpose is { } purpose ? ShotLooks.Label(purpose) : "" };
    }
    public static IReadOnlyList<ShotReferenceGuidance> Resolve(Shot shot, AssetLibrary assets, ShotDocument shots) =>
        shot.Images.Select(b => Resolve(b, assets, shots)).ToArray();

    public static bool SameEffective(IReadOnlyList<ShotReferenceGuidance> a, IReadOnlyList<ShotReferenceGuidance> b) =>
        a.Select(x => (x.BindingId, x.Effective, x.Phase)).SequenceEqual(b.Select(x => (x.BindingId, x.Effective, x.Phase)));
}
