using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

/// <summary>Display-only comparison. Line endings and literal tags remain part of the text.</summary>
public static partial class PromptComparison
{
    [GeneratedRegex(@"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Lines();

    public static ScreenplayChanges Compare(string before, string after)
        => ScreenplayComparison.Compare(Blocks(before), Blocks(after));

    private static ScriptBlock[] Blocks(string text) => Lines().Matches(text)
        .Select(line => ScriptBlock.Create(ScriptBlockKind.Action, line.Value)).ToArray();
}
