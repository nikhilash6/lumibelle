using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public class ScriptRevisionTests
{
    private static ScriptBlock B(string text, ScriptBlockKind kind = ScriptBlockKind.Action) => ScriptBlock.Create(kind, text);
    [Fact]
    public void SectionTargetRetainsIdentityAfterReorderingAndReadsLatestText()
    {
        var first = B("INT. ONE", ScriptBlockKind.Scene); var second = B("INT. TWO", ScriptBlockKind.Scene);
        var doc = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [first, B("Old"), second, B("Other")] };
        var target = ScriptAssistantTarget.From(doc, ScriptScope.Scene, new(first.Id, 0, first.Id, 0));
        doc = doc with { Blocks = [second, doc.Blocks[3], first with { Spans = [new("INT. RENAMED")] }, B("New action")] };
        Assert.Equal("INT. RENAMED", target.Capture(doc).Name);
        Assert.Equal("New action", target.Capture(doc).OriginalBlocks[1].Text);
        Assert.Throws<WorkspaceStoreException>(() => target.Capture(doc with { Blocks = [second] }));
        Assert.Throws<WorkspaceStoreException>(() => target.Capture(doc with { Blocks = [first with { Kind = ScriptBlockKind.Act }] }));
    }
    [Fact]
    public void PassageTargetRejectsChangedMembershipAndInvalidOffsets()
    {
        var scene = B("INT. ROOM", ScriptBlockKind.Scene); var a = B("One word"); var b = B("Two words");
        var doc = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [scene, a, b] };
        var target = ScriptAssistantTarget.From(doc, ScriptScope.Passage, new(a.Id, 4, b.Id, 3));
        Assert.Equal("word", ScriptStructure.TargetBlocks(target.Capture(doc))[0].Text);
        Assert.Throws<WorkspaceStoreException>(() => target.Capture(doc with { Blocks = [scene, a, B("Inserted"), b] }));
        Assert.Throws<WorkspaceStoreException>(() => target.Capture(doc with { Blocks = [scene, a with { Spans = [new("X")] }, b] }));
    }
    [Fact]
    public void ComparisonIgnoresIdsAndMarksOnlyChangedWords()
    {
        var result = ScreenplayComparison.Compare([B("The mouse waits.")], [B("The mouse smiles.")]);
        Assert.Equal("waits", string.Concat(result.Before[0].Spans.Where(s => s.Changed).Select(s => s.Text)));
        Assert.Equal("smiles", string.Concat(result.After[0].Spans.Where(s => s.Changed).Select(s => s.Text)));
        Assert.DoesNotContain(ScreenplayComparison.Compare([B("Same")], [B("Same")]).After[0].Spans, s => s.Changed);
    }
    [Fact]
    public void ComparisonRetainsRepeatedDialogueUnicodeAndEmphasis()
    {
        var old = new[] { B("Hej 🐰!", ScriptBlockKind.Dialogue), B("Hej 🐰!", ScriptBlockKind.Dialogue) };
        var changed = old[1] with { Id = Guid.NewGuid(), Spans = [new("Hej "), new("🐰", Bold: true), new("!")] };
        var result = ScreenplayComparison.Compare(old, [B("Hej 🐰!", ScriptBlockKind.Dialogue), changed]);
        Assert.Equal("Unchanged", result.After[0].Change); Assert.Equal("Emphasis changed", result.After[1].Change);
        Assert.Contains(result.After[1].Spans, s => s.Bold && s.Changed);
        Assert.Contains(result.After[1].Spans, s => s.Text == "🐰");
        Assert.Equal("Hej 🐰!", result.After[1].Text);
        Assert.Contains("Element changed", ScreenplayComparison.Compare([B("Hello")], [B("Hello", ScriptBlockKind.Dialogue)]).After[0].Change);
    }
    [Fact]
    public void PassageComparisonExcludesSurroundingTextAndContinuationIsOnlyAddition()
    {
        var block = B("prefix old suffix");
        var target = new ScriptTarget(ScriptScope.Passage, "Passage", [block], 7, 10);
        var result = ScreenplayComparison.Compare(target, [B("new")], WritingOperation.Revise);
        Assert.Equal("old", result.Before[0].Text); Assert.Equal("new", result.After[0].Text);
        result = ScreenplayComparison.Compare(target, [B("More")], WritingOperation.Continue);
        Assert.Empty(result.Before); Assert.Equal("Added", result.After[0].Change);
    }
    [Fact]
    public void LargeComparisonUsesBoundedBlockFallback()
    {
        var blocks = Enumerable.Range(0, 2000).Select(i => B($"Line {i}")).ToArray();
        var result = ScreenplayComparison.Compare(blocks, blocks.Reverse().ToArray());
        Assert.True(result.Simplified); Assert.Equal(2000, result.After.Count);
        Assert.Equal("Line 1999", result.After[0].Text);
    }
    [Fact]
    public void ApplicationReportsExactInsertedPassageOffsetsAndLeavesSurroundingsIntact()
    {
        var scene = B("INT. ROOM", ScriptBlockKind.Scene); var action = B("prefix old suffix"); var last = B("Surrounding block");
        var doc = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [scene, action, last] };
        var target = ScriptStructure.Capture(doc, ScriptScope.Passage, new(action.Id, 7, action.Id, 10));
        var run = new AssistantRun { Operation = WritingOperation.Revise, Status = AssistantRunStatus.Completed, Target = target, Proposal = [B("new")] };
        var result = ScriptProposals.ApplyWithChanges(doc, run);
        Assert.Equal("prefix new suffix", result.Document.Blocks[1].Text);
        Assert.Equal(action.Id, result.Document.Blocks[1].Id);
        Assert.Equal(new ScriptChangeRange(action.Id, 7, 10), Assert.Single(result.Changes));
        Assert.Equal(ScriptStructure.Fingerprint([last]), ScriptStructure.Fingerprint([result.Document.Blocks[2]])); Assert.Equal("prefix old suffix", action.Text);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.ApplyWithChanges(result.Document, run));
    }
    [Fact]
    public void ContinuationRangesUseActualNewBlockIdsAndDoNotHighlightTheTarget()
    {
        var scene = B("INT. ROOM", ScriptBlockKind.Scene); var action = B("Old");
        var doc = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [scene, action] };
        var target = ScriptStructure.Capture(doc, ScriptScope.Scene, new(action.Id, 0, action.Id, 0));
        var run = new AssistantRun { Operation = WritingOperation.Continue, Status = AssistantRunStatus.Completed, Target = target, Proposal = [B("New addition")] };
        var result = ScriptProposals.ApplyWithChanges(doc, run);
        Assert.Equal(ScriptStructure.Fingerprint([action]), ScriptStructure.Fingerprint([result.Document.Blocks[1]]));
        Assert.Equal(new ScriptChangeRange(result.Document.Blocks[2].Id, 0, 12), Assert.Single(result.Changes));
        Assert.NotEqual(run.Proposal[0].Id, result.Changes[0].BlockId);
    }
}
