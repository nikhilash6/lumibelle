using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed record ScriptChangeRange(Guid BlockId, int Start, int End);
public sealed record ScriptProposalApplication(ScriptDocument Document, List<ScriptChangeRange> Changes);

public static class ScriptProposals
{
    public static AssistantRun CorrectJson(AssistantRun source, string json, Guid sessionId)
    {
        if (source.Status == AssistantRunStatus.Running || source.Operation == WritingOperation.Discuss)
            throw new WorkspaceStoreException("Open a finished screenplay response before correcting its JSON.");
        var edits = source.EditFormat == 1 ? ScriptEdits.Parse(json, source.Target) : null;
        var parsed = edits is null ? ScreenplayJson.Parse(json) : new ScreenplayJsonResult(ScriptEdits.Materialize(source.Target, edits), null);
        if (parsed.Blocks is null) throw new WorkspaceStoreException(parsed.Error!);
        return source with
        {
            Id = Guid.NewGuid(), Revision = 0, JobId = null, CorrectedFromRunId = source.Id,
            SessionId = sessionId, CreatedUtc = DateTimeOffset.UtcNow, Status = AssistantRunStatus.Completed,
            Output = json, Proposal = parsed.Blocks, Edits = edits, Error = null, Applied = false, Rejected = false, AppliedTarget = null,
            Target = source.Target with { OriginalBlocks = source.Target.OriginalBlocks.Select(b => b.Copy()).ToList() }
        };
    }

    public static ScriptDocument Apply(ScriptDocument document, AssistantRun run) => ApplyWithChanges(document, run).Document;

    public static ScriptProposalApplication ApplyWithChanges(ScriptDocument document, AssistantRun run)
    {
        if (run.Status != AssistantRunStatus.Completed || run.Error is not null || run.Rejected || run.Operation == WritingOperation.Discuss || run.Proposal is null)
            throw new WorkspaceStoreException("Only a complete, valid screenplay proposal can be applied.");
        if (document.AppliedProposalIds.Contains(run.Id)) throw new WorkspaceStoreException("This proposal has already been applied.");
        if (run.Edits is not null) return ScriptEdits.Apply(document, run);
        ScriptStructure.ValidateTarget(run.Target);
        if (!ScriptStructure.Matches(document, run.Target)) throw new WorkspaceStoreException("The target changed or was deleted. Select and review a new target before applying.");
        ScriptStructure.ValidateBlocks(run.Proposal);
        var target = run.Target;
        var start = target.Scope == ScriptScope.Document ? 0 : document.Blocks.FindIndex(b => b.Id == target.OriginalBlocks[0].Id);
        var count = target.OriginalBlocks.Count;
        var replacement = run.Proposal.Select(b => b.Copy() with { Id = Guid.NewGuid() }).ToList();
        if (target.Scope == ScriptScope.Passage)
        {
            if (run.Operation == WritingOperation.Continue || replacement.Any(b => b.Kind is ScriptBlockKind.Act or ScriptBlockKind.Scene))
                throw new WorkspaceStoreException("Passage revisions cannot change scene or act structure.");
            var first = target.OriginalBlocks[0]; var last = target.OriginalBlocks[^1];
            replacement[0] = replacement[0] with { Id = first.Id, Kind = first.Kind, Spans = [.. ScriptStructure.Slice(first.Spans, 0, target.StartOffset), .. replacement[0].Spans] };
            replacement[^1] = replacement[^1] with { Spans = [.. replacement[^1].Spans, .. ScriptStructure.Slice(last.Spans, target.EndOffset, last.Text.Length - target.EndOffset)] };
        }
        else if (run.Operation == WritingOperation.Continue) { start += count; count = 0; }
        else
        {
            // Retain an existing scene/act identity when its scoped heading is revised.
            if (target.Scope is ScriptScope.Scene or ScriptScope.Act)
            {
                var original = target.OriginalBlocks[0];
                if (replacement[0].Kind != original.Kind || target.Scope == ScriptScope.Scene && replacement.Skip(1).Any(b => b.Kind is ScriptBlockKind.Scene or ScriptBlockKind.Act))
                    throw new WorkspaceStoreException("A scoped revision must preserve its scene or act heading and boundaries.");
                replacement[0] = replacement[0] with { Id = original.Id };
            }
            // Matching headings keep downstream identities on full-script revisions.
            var available = target.OriginalBlocks.Where(b => b.Kind is ScriptBlockKind.Scene or ScriptBlockKind.Act).ToList();
            for (var i = 0; i < replacement.Count; i++)
            {
                var match = available.FirstOrDefault(b => b.Kind == replacement[i].Kind && b.Text == replacement[i].Text);
                if (match is null || replacement.Take(i).Any(b => b.Id == match.Id)) continue;
                replacement[i] = replacement[i] with { Id = match.Id }; available.Remove(match);
            }
        }
        var blocks = document.Blocks.Select(b => b.Copy()).ToList();
        blocks.RemoveRange(start, count); blocks.InsertRange(start, replacement);
        ScriptStructure.ValidateBlocks(blocks);
        var comparison = ScreenplayComparison.Compare(target, run.Proposal, run.Operation);
        List<ScriptChangeRange> changes = [];
        for (var i = 0; i < replacement.Count; i++)
        {
            var offset = target.Scope == ScriptScope.Passage && i == 0 ? target.StartOffset : 0;
            foreach (var span in comparison.After[i].Spans)
            {
                if (span.Changed && span.Text.Length > 0)
                {
                    if (changes.LastOrDefault() is { } previous && previous.BlockId == replacement[i].Id && previous.End == offset)
                        changes[^1] = previous with { End = offset + span.Text.Length };
                    else changes.Add(new(replacement[i].Id, offset, offset + span.Text.Length));
                }
                offset += span.Text.Length;
            }
            if (comparison.After[i].Change.StartsWith("Element changed", StringComparison.Ordinal))
                changes.Add(new(replacement[i].Id, 0, 0));
        }
        // A pure deletion has no inserted text to decorate. Mark its surviving boundary.
        if (changes.Count == 0 && comparison.Before.Any(b => b.Change != "Unchanged") && replacement.Count > 0)
            changes.Add(new(replacement[0].Id, 0, 0));
        return new(document with { Blocks = blocks, AppliedProposalIds = [.. document.AppliedProposalIds, run.Id] }, changes);
    }
}
