using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.Story;

// Every operation addresses the captured document, never offsets shifted by another edit.
public static partial class ScriptEdits
{
    public const string Profile = "script-focused-v3";
    public const string Instructions = """
        Return only JSON {"version":1,"operations":[...]}. Make only the requested changes.
        All IDs and offsets refer to the original supplied TARGET. Operations are applied simultaneously, NOT sequentially; an earlier operation never creates a new anchor for a later operation.
        Operations:
        - replace: kind, startId, optional endId/startOffset/endOffset, blocks.
        - delete: kind, startId, optional endId.
        - insert: kind, anchorId, side ("before" or "after"), blocks.
        - move: kind, startId, optional endId, anchorId, side ("before" or "after").
        Use JSON double quotes. Each new block has kind and spans:[{text,bold,italic}]. Block kinds are Act, Scene, Action, Character, Dialogue, Parenthetical, Transition. Do not give new blocks IDs. Omit fields that do not apply to the operation.
        Omitted endId means startId. Replacement offsets use the original first/last block's text; omitted offsets mean the whole block.
        When rewriting a whole block AND adding material immediately after it, emit ONE replace whose blocks contains the rewritten block followed by the new blocks, in reading order. Do NOT emit a separate insert anchored to that replaced block.
        Example (replace TARGET_ID with a real original ID): {"kind":"replace","startId":"TARGET_ID","blocks":[{"kind":"Dialogue","spans":[{"text":"Rewritten line."}]},{"kind":"Action","spans":[{"text":"Following action."}]}]}
        Insert and move anchors must survive unchanged: never anchor to any block in a replaced, deleted or moved range. Never move into your own source range.
        Use only one insertion at each original boundary. For consecutive original blocks A and B, "after A" and "before B" name the SAME boundary. Combine ordered additions into one insert's blocks array rather than using multiple operations for that boundary.
        For a passage, use only replace with the supplied passage offsets; preserve all text and formatting outside the selection. Do not expand the passage to add neighboring blocks.
        Preserve existing headings unless asked to change them. Do not return unchanged blocks, overlapping edits, or explanatory text. Empty operations means no change is needed.
        Before returning, check that every ID is in TARGET, each replaced/deleted/moved source block is used only once, every insertion/move anchor survives, and no two operations compete for the same insertion boundary. Fix conflicts by combining the intended content, never by discarding a requested change.
        """;

    public static ScriptEditProposal Parse(string raw, ScriptTarget target)
    {
        try
        {
            using var document = JsonDocument.Parse(FocusedJson(raw));
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                throw new WorkspaceStoreException("Expected a focused-edit object with version and an operations array. Your script is unchanged.");
            // Check the ORIGINAL response count before normalization can reduce it.
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 || operations.GetArrayLength() > 1000)
                throw new WorkspaceStoreException("Use focused-edit version 1 with at most 1,000 operations. Your script is unchanged.");
            List<ScriptEditOperation> parsedOperations = [];
            foreach (var rawOperation in operations.EnumerateArray())
            {
                if (rawOperation.ValueKind != JsonValueKind.Object)
                    throw new WorkspaceStoreException("Each focused edit must be an operation object. Your script is unchanged.");
                if (!rawOperation.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                    kind.GetString() is not ("replace" or "insert" or "delete" or "move"))
                    throw new WorkspaceStoreException("Each focused edit needs a supported lowercase kind. Your script is unchanged.");
                if (kind.GetString() is "replace" or "insert" && !rawOperation.TryGetProperty("blocks", out _))
                    throw new WorkspaceStoreException("An insertion or replacement needs a blocks array. Your script is unchanged.");
                var op = rawOperation.Deserialize<ScriptEditOperation>(AtomicJsonFile.Options) ?? throw new JsonException();
                if (rawOperation.TryGetProperty("blocks", out var blocks))
                {
                    var parsed = ScreenplayJson.Parse(blocks.GetRawText());
                    if (parsed.Blocks is null) throw new WorkspaceStoreException("A focused edit has invalid replacement blocks. " + parsed.Error);
                    op = op with { Blocks = parsed.Blocks.Select(b => b with { Id = Guid.NewGuid() }).ToList() };
                }
                parsedOperations.Add(op);
            }
            // Review notes are generated locally, never trusted from model-authored JSON.
            var edits = NormalizeAdjacentInsertions(target, new(1, parsedOperations));
            Materialize(target, edits); // The strict scope/overlap/anchor validator is unchanged.
            return edits;
        }
        catch (JsonException e)
        { throw new WorkspaceStoreException("The response is not valid focused-edit JSON. Review the complete JSON or request new changes. Your script is unchanged.", e); }
        catch (Exception e) when (e is NullReferenceException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        { throw new WorkspaceStoreException("The response is not a valid focused edit. Review the JSON or request new changes. Your script is unchanged.", e); }
    }

    public static List<ScriptBlock> Materialize(ScriptTarget target, ScriptEditProposal edits)
    {
        ScriptStructure.ValidateTarget(target);
        if (edits.Version != 1 || edits.Operations is null || edits.Operations.Count > 1000)
            throw new WorkspaceStoreException("Unsupported focused edit format.");
        var original = target.OriginalBlocks;
        var indices = original.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var consumed = new HashSet<int>(); var insertions = new Dictionary<int, List<ScriptBlock>>();
        var boundaries = new List<int>();
        int Index(Guid? id) => id is { } key && indices.TryGetValue(key, out var value) ? value
            : throw new WorkspaceStoreException("An edit refers to a block outside its captured target.");
        foreach (var op in edits.Operations)
        {
            if (op is null || op.Kind is not ("replace" or "insert" or "delete" or "move")) throw new WorkspaceStoreException("Unknown edit operation.");
            if (target.Scope == ScriptScope.Passage && op.Kind != "replace") throw new WorkspaceStoreException("Passage edits must replace only the selected text.");
            if (op.Kind is "insert" or "replace")
            {
                if (op.Blocks is not { Count: > 0 }) throw new WorkspaceStoreException("An insertion or replacement needs screenplay blocks.");
                ScriptStructure.ValidateBlocks(op.Blocks);
            }
            int a = -1, z = -1;
            if (op.Kind != "insert")
            {
                a = Index(op.StartId); z = Index(op.EndId ?? op.StartId);
                if (z < a) throw new WorkspaceStoreException("An edit range is reversed.");
                for (var i = a; i <= z; i++) if (!consumed.Add(i)) throw new WorkspaceStoreException("Focused edits overlap. Request a corrected response.");
            }
            if (op.Kind == "replace")
            {
                var from = op.StartOffset ?? 0; var to = op.EndOffset ?? original[z].Text.Length;
                if (from < 0 || from > original[a].Text.Length || to < 0 || to > original[z].Text.Length || a == z && from > to)
                    throw new WorkspaceStoreException("A replacement has invalid text offsets.");
                if (target.Scope == ScriptScope.Passage && (a == 0 && from < target.StartOffset || z == original.Count - 1 && to > target.EndOffset ||
                    op.Blocks!.Any(b => b.Kind is ScriptBlockKind.Act or ScriptBlockKind.Scene)))
                    throw new WorkspaceStoreException("A replacement extends outside the selected passage.");
                var blocks = op.Blocks!.Select(b => b.Copy()).ToList();
                if (blocks[0].Kind == original[a].Kind) blocks[0] = blocks[0] with { Id = original[a].Id };
                if ((from > 0 || target.Scope == ScriptScope.Passage) && blocks[0].Kind != original[a].Kind || to < original[z].Text.Length && blocks[^1].Kind != original[z].Kind)
                    throw new WorkspaceStoreException("A partial replacement must preserve its element type.");
                blocks[0] = blocks[0] with { Spans = [.. ScriptStructure.Slice(original[a].Spans, 0, from), .. blocks[0].Spans] };
                blocks[^1] = blocks[^1] with { Spans = [.. blocks[^1].Spans, .. ScriptStructure.Slice(original[z].Spans, to, original[z].Text.Length - to)] };
                if (!insertions.TryAdd(a, blocks)) throw new WorkspaceStoreException("Multiple edits use the same insertion point.");
            }
            if (op.Kind is "insert" or "move")
            {
                if (op.Side is not ("before" or "after")) throw new WorkspaceStoreException("Choose before or after for an insertion.");
                var anchor = Index(op.AnchorId); boundaries.Add(anchor);
                var at = anchor + (op.Side == "after" ? 1 : 0);
                var blocks = op.Kind == "move" ? original.Skip(a).Take(z - a + 1).Select(b => b.Copy()).ToList() : op.Blocks!.Select(b => b.Copy()).ToList();
                if (!insertions.TryAdd(at, blocks)) throw new WorkspaceStoreException("Multiple edits use the same insertion point.");
            }
        }
        if (boundaries.Any(consumed.Contains)) throw new WorkspaceStoreException("An insertion or move is anchored to another edited block.");
        List<ScriptBlock> result = [];
        for (var i = 0; i <= original.Count; i++) { if (insertions.TryGetValue(i, out var added)) result.AddRange(added); if (i < original.Count && !consumed.Contains(i)) result.Add(original[i].Copy()); }
        ScriptStructure.ValidateBlocks(result);
        if (target.Scope is ScriptScope.Scene or ScriptScope.Act && (result.Count == 0 || result[0].Id != original[0].Id || result[0].Kind != original[0].Kind ||
            target.Scope == ScriptScope.Scene && result.Skip(1).Any(b => b.Kind is ScriptBlockKind.Scene or ScriptBlockKind.Act)))
            throw new WorkspaceStoreException("The edit changes the selected section's boundaries. Choose a wider scope.");
        return result;
    }
    public static ScriptProposalApplication Apply(ScriptDocument doc, AssistantRun run)
    {
        if (!ScriptStructure.Matches(doc, run.Target)) throw new WorkspaceStoreException("The target changed. Request fresh changes against the current script.");
        var updated = Materialize(run.Target, run.Edits!);
        var start = run.Target.Scope == ScriptScope.Document ? 0 : doc.Blocks.FindIndex(b => b.Id == run.Target.OriginalBlocks[0].Id);
        var blocks = doc.Blocks.Select(b => b.Copy()).ToList(); blocks.RemoveRange(start, run.Target.OriginalBlocks.Count); blocks.InsertRange(start, updated);
        ScriptStructure.ValidateBlocks(blocks);
        var prior = run.Target.OriginalBlocks.ToDictionary(b => b.Id);
        var changes = updated.Where(b => !prior.TryGetValue(b.Id, out var old) || ScriptStructure.Fingerprint([b]) != ScriptStructure.Fingerprint([old]))
            .Select(b =>
            {
                if (!prior.TryGetValue(b.Id, out var old)) return new ScriptChangeRange(b.Id, 0, b.Text.Length);
                var from = 0; var end = b.Text.Length; var oldEnd = old.Text.Length;
                while (from < end && from < oldEnd && b.Text[from] == old.Text[from]) from++;
                while (end > from && oldEnd > from && b.Text[end - 1] == old.Text[oldEnd - 1]) { end--; oldEnd--; }
                return new ScriptChangeRange(b.Id, from, end);
            }).ToList();
        if (changes.Count == 0 && updated.Count > 0 && ScriptStructure.Fingerprint(updated) != ScriptStructure.Fingerprint(run.Target.OriginalBlocks)) changes.Add(new(updated[0].Id, 0, 0));
        return new(doc with { Blocks = blocks, AppliedProposalIds = [.. doc.AppliedProposalIds, run.Id] }, changes);
    }
}
