using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class AssetExtractionApply
{
    public static AssetLibrary Apply(AssetLibrary library, IEnumerable<AssetExtractionProposal> proposals, DateTimeOffset now)
    {
        var assets = library.Copy().Assets;
        foreach (var p in proposals.Where(p => p.Decision != ExtractionDecision.Skip))
        {
            if (!Enum.IsDefined(p.Decision) || !Enum.IsDefined(p.Category) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Description) || p.Looks is null || p.Looks.Any(l => l is null || l.Name is null || l.Evidence is null)) throw new WorkspaceStoreException("Review each asset name, description, and proposed look.");
            var index = p.Decision == ExtractionDecision.Merge ? assets.FindIndex(a => a.Id == p.MatchedAssetId) : -1;
            if (p.Decision == ExtractionDecision.Merge && index < 0) throw new WorkspaceStoreException("Choose an existing merge target.");
            var asset = index >= 0 ? assets[index] : new ReferenceAsset { Id = Guid.NewGuid(), Name = p.Name.Trim(), CreatedUtc = now };
            var looks = asset.Looks.ToList();
            foreach (var l in p.Looks.Where(l => l.Decision != ExtractionDecision.Skip))
            {
                if (p.Category != AssetCategory.Character || !Enum.IsDefined(l.Decision)) throw new WorkspaceStoreException("Only characters can own looks.");
                var li = l.Decision == ExtractionDecision.Merge ? looks.FindIndex(x => x.Id == l.MatchedLookId) : -1;
                if (l.Decision == ExtractionDecision.Merge && li < 0) throw new WorkspaceStoreException("Choose a look within the selected character.");
                var previous = li >= 0 ? looks[li] : new CharacterLook();
                var next = previous with { Name = l.Name.Trim(), Description = l.Description, PreservationGuidance = l.PreservationGuidance, Evidence = previous.Evidence.Concat(l.Evidence).Distinct().ToArray() };
                if (li >= 0) looks[li] = next; else looks.Add(next);
            }
            asset = asset with { Category = p.Category, Name = p.Name.Trim(), Description = p.Description, Looks = looks,
                PreservationGuidance = p.PreservationGuidance ?? asset.PreservationGuidance,
                SuggestedImageTags = asset.SuggestedImageTags.Union(p.SuggestedTags, StringComparer.OrdinalIgnoreCase).ToList(),
                Evidence = asset.Evidence.Concat(p.Evidence).Distinct().ToList(), UpdatedUtc = now };
            if (LookPolicy.Invalid(asset)) throw new WorkspaceStoreException("Look names must be unique within the character, and descriptions must be valid.");
            if (index >= 0) assets[index] = asset; else assets.Add(asset);
        }
        return library with { Assets = assets };
    }
}
