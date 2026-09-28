using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptPolishTests
{
    private static AssistantRun Run(ScriptDocument doc, ScriptTarget target, ScriptEditProposal edits) => new()
    { Operation = WritingOperation.Revise, EditFormat = 1, Status = AssistantRunStatus.Completed, Target = target, Edits = edits, Proposal = ScriptEdits.Materialize(target, edits), SourceFingerprint = ScriptStructure.ContextFingerprint(doc) };
    [Fact]
    public void ConsecutiveFocusedEditsPreserveAllUntouchedBlocksAndIds()
    {
        var doc = ScriptFixtures.Document(); var target = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var first = Run(doc, target, new(1, [new("replace", doc.Blocks[1].Id, Blocks: [ScriptBlock.Create(ScriptBlockKind.Action, "First detail.")])]));
        var after = ScriptProposals.Apply(doc, first);
        Assert.Equal(doc.Blocks[1].Id, after.Blocks[1].Id);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Skip(2)), ScriptStructure.Fingerprint(after.Blocks.Skip(2)));
        var second = Run(after, ScriptStructure.Capture(after, ScriptScope.Document, null), new(1, [new("replace", doc.Blocks[3].Id, Blocks: [ScriptBlock.Create(ScriptBlockKind.Dialogue, "Second detail.")])]));
        var final = ScriptProposals.Apply(after, second);
        Assert.Equal("First detail.", final.Blocks[1].Text); Assert.Equal("Second detail.", final.Blocks[3].Text);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(final, first));
    }
    [Fact]
    public void PassageEditsKeepUnselectedTextAndFormattingAndRejectOverreach()
    {
        var doc = ScriptFixtures.Document(); doc.Blocks[1] = doc.Blocks[1] with { Spans = [new("Å ", true), new("rabbit"), new("!\n🐇", false, true)] };
        var block = doc.Blocks[1]; var target = ScriptStructure.Capture(doc, ScriptScope.Passage, new(block.Id, 2, block.Id, 8));
        var edits = new ScriptEditProposal(1, [new("replace", block.Id, StartOffset: 2, EndOffset: 8, Blocks: [ScriptBlock.Create(ScriptBlockKind.Action, "mouse")])]);
        var result = ScriptProposals.Apply(doc, Run(doc, target, edits));
        Assert.Equal("Å mouse!\n🐇", result.Blocks[1].Text); Assert.True(result.Blocks[1].Spans[0].Bold); Assert.True(result.Blocks[1].Spans[^1].Italic);
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Materialize(target, new(1, [edits.Operations[0] with { StartOffset = 0 }])));
    }
    [Fact]
    public void InsertDeleteAndMoveUseOriginalBoundaries()
    {
        var doc = ScriptFixtures.Document(); var t = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var edits = new ScriptEditProposal(1, [new("move", doc.Blocks[2].Id, doc.Blocks[3].Id, AnchorId: doc.Blocks[1].Id, Side: "before")]);
        var result = ScriptEdits.Materialize(t, edits);
        Assert.Equal(new[] { doc.Blocks[0].Id, doc.Blocks[2].Id, doc.Blocks[3].Id, doc.Blocks[1].Id }, result.Select(b => b.Id));
        var inserted = ScriptBlock.Create(ScriptBlockKind.Action, "New action.");
        result = ScriptEdits.Materialize(t, new(1, [new("delete", doc.Blocks[2].Id, doc.Blocks[3].Id), new("insert", AnchorId: doc.Blocks[1].Id, Side: "after", Blocks: [inserted])]));
        Assert.Equal(new[] { doc.Blocks[0].Id, doc.Blocks[1].Id, inserted.Id }, result.Select(b => b.Id));
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Materialize(t, new(1, [new("delete", doc.Blocks[1].Id), new("delete", doc.Blocks[1].Id)])));
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Materialize(t, new(1, [new("delete", Guid.NewGuid())])));
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse("[]", t));
    }
    [Fact]
    public void OutlineMovesWholeSectionsAndInsertsAtExplicitPosition()
    {
        var doc = ScriptFixtures.Document(); var a = ScriptBlock.Create(ScriptBlockKind.Act, "Act one"); var b = ScriptBlock.Create(ScriptBlockKind.Act, "Act two");
        doc = doc with { Blocks = [a, .. doc.Blocks, b, ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. TWO"), ScriptBlock.Create(ScriptBlockKind.Action, "End.")] };
        var scene = doc.Blocks[1].Id;
        var moved = ScriptOutline.Move(doc, scene, b.Id, "start");
        Assert.Equal(b.Id, ScriptStructure.Sections(moved.Blocks).Single(s => s.Id == scene).ActId);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Skip(1).Take(4)), ScriptStructure.Fingerprint(moved.Blocks.Skip(2).Take(4)));
        var added = ScriptOutline.Insert(doc, ScriptBlockKind.Scene, scene, "after", out var id);
        Assert.Equal(5, added.Blocks.FindIndex(x => x.Id == id));
        added = ScriptOutline.Insert(doc, ScriptBlockKind.Act, scene, "default", out id);
        Assert.Equal(5, added.Blocks.FindIndex(x => x.Id == id));
    }
    [Fact]
    public void MovingActsKeepsUnassignedScenesBeforeTheFirstAct()
    {
        var doc = ScriptFixtures.Document(); var first = ScriptBlock.Create(ScriptBlockKind.Act, "First"); var last = ScriptBlock.Create(ScriptBlockKind.Act, "Last");
        doc = doc with { Blocks = [.. doc.Blocks, first, ScriptBlock.Create(ScriptBlockKind.Scene, "ONE"), last, ScriptBlock.Create(ScriptBlockKind.Scene, "TWO")] };
        var moved = ScriptOutline.Move(doc, last.Id, null, "before");
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Take(4)), ScriptStructure.Fingerprint(moved.Blocks.Take(4)));
        Assert.Equal(last.Id, moved.Blocks[4].Id);
        Assert.Null(ScriptStructure.Sections(moved.Blocks).First().ActId);
        var target = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var malformed = JsonSerializer.Serialize(new { version = 1, operations = new[] { new { kind = "replace", startId = doc.Blocks[1].Id, blocks = new[] { new { unexpected = "missing kind and spans" } } } } });
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(malformed, target));
    }
    [Theory]
    [InlineData(AiBackend.Codex)] [InlineData(AiBackend.OpenRouter)] [InlineData(AiBackend.ComfyUI)]
    public void EveryProviderReceivesExplicitWritingContextWithoutBrief(AiBackend backend)
    {
        var doc = ScriptFixtures.Document(); var target = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var request = new ScriptAssistantRequest(new() { Backend = backend, Model = "chosen", Operation = WritingOperation.Revise, EditFormat = 1, Target = target, Instructions = "Change one line." }, doc, []);
        var text = string.Join("\n", ScriptAssistant.BuildMessages(request).Select(m => m.Text));
        Assert.Contains("CURRENT SCREENPLAY", text); Assert.Contains(doc.Blocks[1].Id.ToString(), text);
        Assert.DoesNotContain(doc.Brief.Idea, text); Assert.DoesNotContain("h3-practical", text); Assert.DoesNotContain("2–3 minutes", text);
        var encoded = JsonSerializer.Serialize(doc, AtomicJsonFile.Options); Assert.DoesNotContain("brief", encoded);
    }
    [Fact]
    public async Task UndoBackToSavedContentNeedsNoSave()
    {
        var store = new FakeScriptStore(); using var session = new ScriptEditingSession(store, TimeProvider.System);
        await session.LoadAsync(store.Document.ProjectId); var original = session.Document.Copy();
        session.Edit(original with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "Changed")] }); Assert.True(session.Dirty);
        session.Edit(original); Assert.False(session.Dirty);
        Assert.True(await session.SaveAsync()); Assert.Equal(0, store.Document.Revision);
    }
}
