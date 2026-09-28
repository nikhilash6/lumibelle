using lumibelle.Models;

namespace lumibelle.Services.Shots;

public static class TakeLanguages
{
    // Empty is master, "*" is an explicit gallery-only all-languages filter.
    public static bool Matches(ShotTake take, string? language, bool includeSilent = false)
    {
        if (language == "*") return true;
        if (string.IsNullOrEmpty(language)) return take.Snapshot.Dub is null;
        return take.Snapshot.Dub is { } dub
            ? string.Equals(dub.Target.Code, language, StringComparison.OrdinalIgnoreCase)
            : includeSilent && take.Snapshot.Shot.Dialogue.Count == 0;
    }
    public static IReadOnlyList<ProjectLanguage> Choices(IEnumerable<ShotTake> takes) => takes
        .Where(t => t.Snapshot.Dub is not null).Select(t => t.Snapshot.Dub!.Target)
        .DistinctBy(l => l.Code, StringComparer.OrdinalIgnoreCase)
        .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
