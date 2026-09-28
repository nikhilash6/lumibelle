using System.Globalization;
using lumibelle.Models;

namespace lumibelle.Services.Shots;

/// <summary>Builds a take-selection proposal without changing shots or the cut.</summary>
public static class CutTakeSelection
{
    // Match the one-decimal MP value shown by TakeDisplay.Badge. Use saved output
    // dimensions, not the generation preset: upscaled/refined takes can differ.
    public static string? ResolutionKey(ShotTake take) => take.Width > 0 && take.Height > 0
        ? ((long)take.Width * take.Height / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture)
        : null;

    public static IReadOnlyList<string> ResolutionChoices(ShotDocument document) => ActiveTakes(document)
        .Select(ResolutionKey)
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .OrderBy(key => double.Parse(key, CultureInfo.InvariantCulture))
        .ToArray();

    /// <summary>
    /// Selects the newest matching saved take per active shot. Null/empty means
    /// any resolution. Language defaults to master; a dub may reuse silent master shots.
    /// A shot without a match is absent from the proposal; master speech is never substituted.
    /// Equal timestamps are broken by take ID so list order cannot change a choice.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> LatestByShot(ShotDocument document, string? resolution = null, string? language = null) => ActiveTakes(document)
        .Where(take => TakeLanguages.Matches(take, language, includeSilent: true))
        .Where(take => string.IsNullOrEmpty(resolution) || ResolutionKey(take) == resolution)
        .GroupBy(take => take.ShotId)
        .ToDictionary(group => group.Key, group => group
            .OrderByDescending(take => take.CreatedUtc)
            .ThenByDescending(take => take.Id)
            .First().Id);

    private static IEnumerable<ShotTake> ActiveTakes(ShotDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var shotIds = document.Shots.Select(shot => shot.Id).ToHashSet();
        // Trash is stored separately; orphan takes must not enter the proposal.
        return document.Takes.Where(take => shotIds.Contains(take.ShotId));
    }
}
