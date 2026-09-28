using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.Story;

public static partial class ScriptEdits
{
    private static string FocusedJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new WorkspaceStoreException("The focused-edit response is empty. Your script is unchanged.");
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var lines = text.Split('\n');
        var opening = lines[0].Trim();
        if (lines.Length < 3 || opening != "```" && !opening.Equals("```json", StringComparison.OrdinalIgnoreCase) ||
            lines[^1].Trim() != "```" || lines.Skip(1).Take(lines.Length - 2).Any(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)))
            throw new WorkspaceStoreException("Use one complete JSON code block or a plain JSON object, without surrounding commentary. Your script is unchanged.");
        return string.Join('\n', lines[1..^1]);
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Match the case-insensitive serializer: do not silently accept last-key-wins
            // IDs, operation kinds, text or formatting flags under either spelling.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new WorkspaceStoreException("A focused-edit JSON object repeats a property name. Remove the ambiguity; your script is unchanged.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    // Only whole SINGLE-source-block replacement + ONE insert after that same block.
    // Never run at Apply time: saved accepted proposals retain their exact operations.
    private static ScriptEditProposal NormalizeAdjacentInsertions(ScriptTarget target, ScriptEditProposal edits)
    {
        ScriptStructure.ValidateTarget(target);
        if (target.Scope == ScriptScope.Passage) return edits;
        var original = target.OriginalBlocks;
        var indices = original.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var consumed = new int[original.Count];
        var consumer = new int[original.Count];
        var anchored = new int[original.Count];
        var destinations = new int[original.Count + 1];
        int Index(Guid? id) => id is { } key && indices.TryGetValue(key, out var index) ? index
            : throw new WorkspaceStoreException("An edit refers to a block outside its captured target.");

        // Examine the ORIGINAL plan in full. Normalizing one pair must not hide a
        // competing replacement, move, or an equivalent before/after insertion point.
        for (var n = 0; n < edits.Operations.Count; n++)
        {
            var op = edits.Operations[n];
            if (op is null || op.Kind is not ("replace" or "insert" or "delete" or "move"))
                throw new WorkspaceStoreException("Unknown edit operation.");
            if (op.Kind is "replace" or "insert")
            {
                if (op.Blocks is not { Count: > 0 }) throw new WorkspaceStoreException("An insertion or replacement needs screenplay blocks.");
                ScriptStructure.ValidateBlocks(op.Blocks);
            }
            if (op.Kind != "insert")
            {
                var a = Index(op.StartId); var z = Index(op.EndId ?? op.StartId);
                if (z < a) throw new WorkspaceStoreException("An edit range is reversed.");
                for (var i = a; i <= z; i++) { consumed[i]++; consumer[i] = n; }
                if (op.Kind == "replace") destinations[a]++;
            }
            if (op.Kind is "insert" or "move")
            {
                if (op.Side is not ("before" or "after")) throw new WorkspaceStoreException("Choose before or after for an insertion.");
                var anchor = Index(op.AnchorId);
                anchored[anchor]++;
                destinations[anchor + (op.Side == "after" ? 1 : 0)]++;
            }
        }

        var replacements = new Dictionary<int, ScriptEditOperation>();
        var removed = new HashSet<int>();
        var notes = new List<string>();
        for (var n = 0; n < edits.Operations.Count; n++)
        {
            var insert = edits.Operations[n];
            if (insert.Kind != "insert" || insert.Side != "after" || insert.StartId is not null || insert.EndId is not null ||
                insert.StartOffset is not null || insert.EndOffset is not null) continue;
            var at = Index(insert.AnchorId);
            if (consumed[at] != 1 || anchored[at] != 1 || destinations[at] != 1 || destinations[at + 1] != 1) continue;
            var replacementIndex = consumer[at];
            var replace = edits.Operations[replacementIndex];
            if (replace.Kind != "replace" || replace.StartId != insert.AnchorId ||
                (replace.EndId ?? replace.StartId) != replace.StartId || replace.AnchorId is not null || replace.Side is not null ||
                (replace.StartOffset ?? 0) != 0 || (replace.EndOffset ?? original[at].Text.Length) != original[at].Text.Length) continue;

            replacements.Add(replacementIndex, replace with {
                Blocks = replace.Blocks!.Concat(insert.Blocks!).Select(b => b.Copy()).ToList()
            });
            removed.Add(n);
            notes.Add($"Combined response operations {replacementIndex + 1} and {n + 1}: a whole-block replacement and its following insertion. Proposed text and formatting are unchanged.");
        }
        if (removed.Count == 0) return edits;
        return edits with {
            Operations = edits.Operations.Select((op, n) => (op, n)).Where(x => !removed.Contains(x.n))
                .Select(x => replacements.GetValueOrDefault(x.n, x.op)).ToList(),
            NormalizationNotes = notes.ToArray()
        };
    }
}
