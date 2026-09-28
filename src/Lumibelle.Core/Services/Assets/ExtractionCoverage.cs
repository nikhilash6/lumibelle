using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class ExtractionCoverage
{
    // Deliberately excludes approval/block identity and emphasis. Scene identity is tracked separately.
    public static string Fingerprint(IEnumerable<ScriptBlock> blocks) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(blocks.Select(b => new { b.Kind, b.Text }))));

    public static IReadOnlyList<ReviewedAssetScene> Scenes(ScriptSourceSnapshot source) =>
        ScriptStructure.Sections(source.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene)
            .Select(s => new ReviewedAssetScene(s.Id, s.Title, Fingerprint(source.Blocks.Skip(s.Start).Take(s.Count)))).ToArray();

    public static SceneCoverageState State(ReviewedAssetScene scene, IEnumerable<AssetExtractionReview> reviews)
    {
        var last = reviews.Reverse().OrderByDescending(r => r.ReviewedUtc).SelectMany(r => r.Scenes).FirstOrDefault(s => s.SceneId == scene.SceneId);
        return last is null ? SceneCoverageState.NotReviewed : last.Fingerprint == scene.Fingerprint ? SceneCoverageState.Current : SceneCoverageState.Changed;
    }

    public static string Label(SceneCoverageState state) => state switch
    {
        SceneCoverageState.Current => "Up to date", SceneCoverageState.Changed => "Changed since review", _ => "Not reviewed"
    };

    public static ExtractionDecisionSummary Summary(IEnumerable<AssetExtractionProposal> proposals)
    {
        var assets = proposals.ToArray(); var looks = assets.Where(p => p.Decision != ExtractionDecision.Skip).SelectMany(p => p.Looks).ToArray();
        return new(assets.Count(p => p.Decision == ExtractionDecision.Create), assets.Count(p => p.Decision == ExtractionDecision.Merge), assets.Count(p => p.Decision == ExtractionDecision.Skip),
            looks.Count(p => p.Decision == ExtractionDecision.Create), looks.Count(p => p.Decision == ExtractionDecision.Merge), looks.Count(p => p.Decision == ExtractionDecision.Skip));
    }
}
