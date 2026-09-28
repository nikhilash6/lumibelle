namespace lumibelle.Models;

public static class AssetImageLocations
{
    // Follow only recorded moves when inspecting history. Submission still checks
    // exact current ownership, since moving may change identity/look guidance.
    public static bool Matches(Guid owner, AssetImage image, AssetImageReference reference) =>
        image.Id == reference.ImageId && (owner == reference.AssetId || image.PreviousAssetIds.Contains(reference.AssetId));

    public static ReferenceAsset? RecordedOwner(AssetLibrary library, AssetImageReference reference) =>
        library.Assets.FirstOrDefault(a => a.Images.Any(i => Matches(a.Id, i, reference)));

    // A missing reference that restoring would make available again: its exact image and
    // owner are recoverable in Trash. Restoring an image moved away before trashing would
    // return it to the other asset and leave this reference unresolved.
    public static TrashedImage? RecoverableTrash(AssetLibrary library, AssetImageReference reference, DateTimeOffset now) =>
        library.Assets.Any(a => a.Id == reference.AssetId && a.Images.Any(i => i.Id == reference.ImageId)) ? null :
        library.Trash.FirstOrDefault(t => t.Asset.Id == reference.AssetId && t.Image.Id == reference.ImageId && t.CanRestore(now));
}
