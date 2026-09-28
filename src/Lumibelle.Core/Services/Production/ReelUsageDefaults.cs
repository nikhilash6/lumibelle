using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class ReelUsageDefaults
{
    public static bool Valid(ReelVisuals? mode) => mode is null or ReelVisuals.Keyframes or ReelVisuals.RefMod or ReelVisuals.FullReel;

    // Snapshot this value when a reel is selected. Reopening, composing and
    // generating an existing shot or reel recipe must not read a newly changed asset default.
    public static ReelVisuals Initial(ReferenceAsset asset)
    {
        if (!Valid(asset.DefaultReelVisuals)) throw new WorkspaceStoreException("Choose RefMod, keyframes or full reel as the asset's default reel usage.");
        return asset.DefaultReelVisuals ?? ReelVisuals.Keyframes;
    }

    public static string Label(ReelVisuals mode) => mode switch {
        ReelVisuals.Keyframes => "Keyframes", ReelVisuals.RefMod => "RefMod", ReelVisuals.FullReel => "Full reel",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
}
