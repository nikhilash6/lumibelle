using System.Security.Cryptography;
using System.Text.Json;

namespace lumibelle.Models;

public enum GuidanceScope { CharacterIdentity, AssetFeatures, Look, Image, ImageDescription }
public sealed record GuidanceTarget(Guid ProjectId, Guid AssetId, GuidanceScope Scope, Guid? LookId = null, Guid? ImageId = null);
public sealed record GuidanceContext(GuidanceTarget Target, string AssetName, AssetCategory Category, string IdentityNotes,
    string Name, string Notes, string Guidance, IReadOnlyList<AssetSourceEvidence> Evidence, Guid? ImageLookId = null)
{
    public GuidanceContext Capture() => this with { Evidence = Array.AsReadOnly(Evidence.ToArray()) };
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    public string Label => Target.Scope switch { GuidanceScope.CharacterIdentity => "Character identity", GuidanceScope.Look => "This look", GuidanceScope.Image => "This image", GuidanceScope.ImageDescription => "Visual description", _ => "Asset features" };
    public static GuidanceContext? From(GuidanceTarget target, ReferenceAsset? asset)
    {
        if (asset is null || asset.Id != target.AssetId) return null;
        if (target.Scope == GuidanceScope.Look)
        {
            var look = asset.Looks.FirstOrDefault(l => l.Id == target.LookId && !l.Archived);
            return look is null ? null : new(target, asset.Name, asset.Category, asset.Description, look.Name, look.Description, look.PreservationGuidance, look.Evidence.ToArray());
        }
        if (target.Scope is GuidanceScope.Image or GuidanceScope.ImageDescription)
        {
            var image = asset.Images.FirstOrDefault(i => i.Id == target.ImageId);
            return image is null ? null : new(target, asset.Name, asset.Category, asset.Description, image.Name ?? "Reference image", "",
                target.Scope == GuidanceScope.ImageDescription ? image.VisualDescription ?? "" : image.PreservationGuidance,
                asset.Evidence.ToArray(), image.LookId);
        }
        return new(target, asset.Name, asset.Category, asset.Description, asset.Name, asset.Description, asset.PreservationGuidance, asset.Evidence.ToArray());
    }
}
public sealed record GuidanceRequest(GuidanceContext Context, TextModelReference Model, AssetImageReference? InspectionImage = null, bool FollowsDefault = false);
