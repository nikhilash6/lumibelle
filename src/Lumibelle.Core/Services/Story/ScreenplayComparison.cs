using System.Text.RegularExpressions;
using System.Text;
using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed record ComparedSpan(string Text, bool Bold, bool Italic, bool Changed);
public sealed record ComparedBlock(ScriptBlockKind Kind, List<ComparedSpan> Spans, string Change)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}
public sealed record ScreenplayChanges(List<ComparedBlock> Before, List<ComparedBlock> After, bool Simplified);

/// <summary>Compares portable screenplay content without relying on generated block identities.</summary>
public static partial class ScreenplayComparison
{
    private const int BlockBudget = 250_000, WordBudget = 40_000, TotalWordBudget = 500_000;
    [GeneratedRegex(@"\s+|[\p{L}\p{M}\p{N}_]+|[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\s]", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static ScreenplayChanges Compare(ScriptTarget target, IReadOnlyList<ScriptBlock> proposal, WritingOperation operation)
        => Compare(operation == WritingOperation.Continue ? [] : ScriptStructure.TargetBlocks(target), proposal);

    public static ScreenplayChanges Compare(IReadOnlyList<ScriptBlock> before, IReadOnlyList<ScriptBlock> after)
    {
        List<ComparedBlock> old = [], next = [];
        var simplified = (long)before.Count * after.Count > BlockBudget;
        var anchors = simplified ? [] : Matches(before.Select(b => (b.Kind, b.Text)).ToArray(), after.Select(b => (b.Kind, b.Text)).ToArray(), (a, b) => a == b);
        var a = 0; var b = 0; var budget = TotalWordBudget;
        foreach (var (x, y) in anchors.Append((before.Count, after.Count)))
        {
            // Pair replacements between unchanged anchors; unmatched tails are additions/removals.
            while (a < x || b < y)
            {
                var left = a < x ? before[a++] : null;
                var right = b < y ? after[b++] : null;
                Add(left, right);
            }
            if (x < before.Count && y < after.Count) { Add(before[x], after[y]); a = x + 1; b = y + 1; }
        }
        return new(old, next, simplified);

        void Add(ScriptBlock? left, ScriptBlock? right)
        {
            if (left is null) { next.Add(Whole(right!, "Added", true)); return; }
            if (right is null) { old.Add(Whole(left, "Removed", true)); return; }
            var l = Tokens(left); var r = Tokens(right);
            if (left.Kind == right.Kind && l.SequenceEqual(r))
            { old.Add(Whole(left, "Unchanged", false)); next.Add(Whole(right, "Unchanged", false)); return; }
            var cost = (long)l.Count * r.Count;
            if (simplified || cost > Math.Min(WordBudget, budget))
            {
                simplified = true;
                old.Add(Whole(left, "Changed block", true)); next.Add(Whole(right, "Changed block", true)); return;
            }
            budget -= (int)cost;
            var common = Matches(l, r, (u, v) => u == v);
            var sameLeft = common.Select(p => p.Item1).ToHashSet(); var sameRight = common.Select(p => p.Item2).ToHashSet();
            var label = left.Kind != right.Kind ? $"Element changed: {left.Kind} → {right.Kind}" : left.Text == right.Text ? "Emphasis changed" : "Text changed";
            old.Add(new(left.Kind, Compact(l.Select((s, i) => new ComparedSpan(s.Text, s.Bold, s.Italic, !sameLeft.Contains(i)))), label));
            next.Add(new(right.Kind, Compact(r.Select((s, i) => new ComparedSpan(s.Text, s.Bold, s.Italic, !sameRight.Contains(i)))), label));
        }
    }

    private static ComparedBlock Whole(ScriptBlock b, string change, bool changed)
        => new(b.Kind, b.Spans.Select(s => new ComparedSpan(s.Text, s.Bold, s.Italic, changed)).ToList(), change);

    private static List<ComparedSpan> Compact(IEnumerable<ComparedSpan> spans)
    {
        List<ComparedSpan> result = []; ComparedSpan? current = null; var text = new StringBuilder();
        foreach (var span in spans)
        {
            if (current is not null && (current.Bold != span.Bold || current.Italic != span.Italic || current.Changed != span.Changed))
            { result.Add(current with { Text = text.ToString() }); text.Clear(); }
            current = span; text.Append(span.Text);
        }
        if (current is not null) result.Add(current with { Text = text.ToString() });
        return result;
    }

    private static List<ScriptSpan> Tokens(ScriptBlock b)
    {
        var normalized = Compact(b.Spans.Select(s => new ComparedSpan(s.Text, s.Bold, s.Italic, false)));
        return normalized.SelectMany(s => Words().Matches(s.Text).Select(m => new ScriptSpan(m.Value, s.Bold, s.Italic))).ToList();
    }

    private static List<(int, int)> Matches<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal)
    {
        var lengths = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lengths[i, j] = equal(a[i], b[j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        List<(int, int)> result = []; int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (equal(a[x], b[y])) { result.Add((x++, y++)); }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++; else y++;
        }
        return result;
    }
}
