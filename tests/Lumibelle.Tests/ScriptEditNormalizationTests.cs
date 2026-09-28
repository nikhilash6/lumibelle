using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ScriptEditNormalizationTests
{
    private static ScriptDocument Document() => ScriptFixtures.Document() with { Blocks = [
        ScriptBlock.Create(ScriptBlockKind.Act, "ACT ONE"),
        ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY"),
        ScriptBlock.Create(ScriptBlockKind.Character, "Riley"),
        ScriptBlock.Create(ScriptBlockKind.Dialogue, "你们还能这样？"),
        ScriptBlock.Create(ScriptBlockKind.Character, "GUARD"),
        ScriptBlock.Create(ScriptBlockKind.Dialogue, "We used to have a dungeon."),
        ScriptBlock.Create(ScriptBlockKind.Action, "The guard points toward the plaza.")
    ] };
    private static ScriptTarget Target(ScriptDocument doc) => ScriptStructure.Capture(doc, ScriptScope.Document, null);
    private static JsonArray Blocks(params ScriptBlock[] blocks) => JsonSerializer.SerializeToNode(blocks.Select(b => new {
        kind = b.Kind.ToString(), spans = b.Spans.Select(s => new { text = s.Text, bold = s.Bold, italic = s.Italic })
    }))!.AsArray();
    private static JsonObject Replace(ScriptBlock source, params ScriptBlock[] blocks) => new() {
        ["kind"] = "replace", ["startId"] = source.Id.ToString(),
        ["blocks"] = Blocks(blocks.Length == 0 ? [ScriptBlock.Create(source.Kind, "光……灭了。")] : blocks)
    };
    private static JsonObject Insert(ScriptBlock source, string side = "after") => new() {
        ["kind"] = "insert", ["anchorId"] = source.Id.ToString(), ["side"] = side,
        ["blocks"] = Blocks(ScriptBlock.Create(ScriptBlockKind.Action, "She gives the staff one experimental shake."))
    };
    private static string Json(params JsonObject[] operations) => new JsonObject {
        ["version"] = 1, ["operations"] = new JsonArray(operations.Select(o => o.DeepClone()).ToArray())
    }.ToJsonString();
    private static string Content(IEnumerable<ScriptBlock> blocks) => JsonSerializer.Serialize(blocks.Select(b => new { b.Kind, b.Spans }));

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void ReportedPairNormalizesWithoutDependingOnOperationOrder(bool insertionFirst, bool explicitEnd, bool explicitOffsets)
    {
        var doc = Document(); var target = Target(doc); var block = doc.Blocks[3];
        var before = JsonSerializer.Serialize(target, AtomicJsonFile.Options);
        var replace = Replace(block); var insert = Insert(block);
        if (explicitEnd) replace["endId"] = block.Id.ToString();
        if (explicitOffsets) { replace["startOffset"] = 0; replace["endOffset"] = block.Text.Length; }
        var raw = insertionFirst ? Json(insert, replace) : Json(replace, insert);
        var proposal = ScriptEdits.Parse(raw, target);
        var operation = Assert.Single(proposal.Operations);
        Assert.Equal("replace", operation.Kind); Assert.Equal(block.Id, operation.StartId);
        Assert.Equal(new[] { "光……灭了。", "She gives the staff one experimental shake." }, operation.Blocks!.Select(b => b.Text));
        Assert.Single(proposal.NormalizationNotes!);
        var result = ScriptEdits.Materialize(target, proposal);
        Assert.Equal(block.Id, result[3].Id);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Take(3)), ScriptStructure.Fingerprint(result.Take(3)));
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Skip(4)), ScriptStructure.Fingerprint(result.Skip(5)));
        Assert.Equal(before, JsonSerializer.Serialize(target, AtomicJsonFile.Options));
        Assert.Equal("你们还能这样？", doc.Blocks[3].Text);
    }

    [Fact]
    public void MultiOutputReplacementAppendsAfterAllReplacementBlocksAndPreservesRichSpans()
    {
        var doc = Document(); var block = doc.Blocks[3];
        var spoken = new ScriptBlock { Kind = ScriptBlockKind.Dialogue, Spans = [new("  光", true), new("……灭了。\r\n🐇  ", false, true)] };
        var beat = ScriptBlock.Create(ScriptBlockKind.Action, "  She waits.  ");
        var insert = Insert(block);
        insert["blocks"] = Blocks(new ScriptBlock { Kind = ScriptBlockKind.Action, Spans = [new("She "), new("shakes", true, true), new(" it.\n")] });
        var parsed = ScriptEdits.Parse(Json(Replace(block, spoken, beat), insert), Target(doc));
        var result = ScriptEdits.Materialize(Target(doc), parsed);
        Assert.Equal(Content([spoken, beat]), Content(result.Skip(3).Take(2)));
        Assert.Equal("She shakes it.\n", result[5].Text);
        Assert.True(result[5].Spans[1].Bold); Assert.True(result[5].Spans[1].Italic);
        Assert.Equal(block.Id, result[3].Id);
        Assert.Equal(result.Count, result.Select(b => b.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(ScriptScope.Document)] [InlineData(ScriptScope.Scene)] [InlineData(ScriptScope.Act)]
    public void NormalizationStillHonorsSectionBoundaries(ScriptScope scope)
    {
        var doc = Document(); var block = doc.Blocks[3];
        var target = ScriptStructure.Capture(doc, scope, new(block.Id, 0, block.Id, 1));
        var parsed = ScriptEdits.Parse(Json(Replace(block), Insert(block)), target);
        var result = ScriptEdits.Materialize(target, parsed);
        Assert.Equal(target.OriginalBlocks[0].Id, result[0].Id);
        Assert.Single(parsed.NormalizationNotes!);
    }

    [Fact]
    public void LastBlockAndSeveralIndependentPairsAreSupported()
    {
        var doc = Document(); var first = doc.Blocks[3]; var last = doc.Blocks[^1];
        var parsed = ScriptEdits.Parse(Json(Replace(first), Insert(first), Replace(last), Insert(last)), Target(doc));
        Assert.Equal(2, parsed.Operations.Count); Assert.Equal(2, parsed.NormalizationNotes!.Count);
        var result = ScriptEdits.Materialize(Target(doc), parsed);
        Assert.Equal(doc.Blocks.Count + 2, result.Count);
        Assert.Equal("She gives the staff one experimental shake.", result[^1].Text);
    }

    [Fact]
    public void MaterializeRemainsStrictAndDoesNotRepairAlreadySavedOperationsAtApplyTime()
    {
        var doc = Document(); var block = doc.Blocks[3];
        var edits = new ScriptEditProposal(1, [
            new("replace", block.Id, Blocks: [ScriptBlock.Create(block.Kind, "New words.")]),
            new("insert", AnchorId: block.Id, Side: "after", Blocks: [ScriptBlock.Create(ScriptBlockKind.Action, "New action.")])
        ]);
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Materialize(Target(doc), edits));
    }

    [Theory]
    [InlineData("before")] [InlineData("multi-first")] [InlineData("multi-last")]
    [InlineData("partial-start")] [InlineData("partial-end")]
    [InlineData("extra-insert-fields")] [InlineData("extra-replace-fields")]
    [InlineData("duplicate-insert")] [InlineData("same-gap-alias")]
    [InlineData("next-replacement")] [InlineData("previous-gap")]
    [InlineData("duplicate-replacement")] [InlineData("overlap-delete")]
    [InlineData("deleted-anchor")] [InlineData("moved-anchor")]
    [InlineData("move-to-replaced")]
    public void AmbiguousOrUnsupportedCombinationsStillFail(string variant)
    {
        var doc = Document(); var target = Target(doc); var block = doc.Blocks[3];
        var replace = Replace(block); var insert = Insert(block); var operations = new List<JsonObject> { replace, insert };
        switch (variant)
        {
            case "before": insert["side"] = "before"; break;
            case "multi-first": replace["endId"] = doc.Blocks[4].Id.ToString(); break;
            case "multi-last": replace["startId"] = doc.Blocks[2].Id.ToString(); replace["endId"] = block.Id.ToString(); break;
            case "partial-start": replace["startOffset"] = 1; break;
            case "partial-end": replace["endOffset"] = block.Text.Length - 1; break;
            case "extra-insert-fields": insert["startId"] = block.Id.ToString(); break;
            case "extra-replace-fields": replace["anchorId"] = block.Id.ToString(); break;
            case "duplicate-insert": operations.Add(Insert(block)); break;
            case "same-gap-alias": operations.Add(Insert(doc.Blocks[4], "before")); break;
            case "next-replacement": operations.Add(Replace(doc.Blocks[4])); break;
            case "previous-gap": operations.Add(Insert(doc.Blocks[2])); break;
            case "duplicate-replacement": operations.Add(Replace(block)); break;
            case "overlap-delete": operations.Add(new() { ["kind"] = "delete", ["startId"] = block.Id.ToString() }); break;
            case "deleted-anchor": replace["kind"] = "delete"; replace.Remove("blocks"); break;
            case "moved-anchor": replace["kind"] = "move"; replace.Remove("blocks"); replace["anchorId"] = doc.Blocks[0].Id.ToString(); replace["side"] = "before"; break;
            case "move-to-replaced": insert["kind"] = "move"; insert.Remove("blocks"); insert["startId"] = doc.Blocks[5].Id.ToString(); break;
        }
        var before = JsonSerializer.Serialize(target);
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(Json(operations.ToArray()), target));
        Assert.Equal(before, JsonSerializer.Serialize(target));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PassageNeverExpandsIntoFollowingBlocks(bool fullBlockSelection)
    {
        var doc = Document(); var block = doc.Blocks[3];
        var start = fullBlockSelection ? 0 : 1; var end = fullBlockSelection ? block.Text.Length : block.Text.Length - 1;
        var target = ScriptStructure.Capture(doc, ScriptScope.Passage, new(block.Id, start, block.Id, end));
        var replace = Replace(block); replace["startOffset"] = start; replace["endOffset"] = end;
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(Json(replace, Insert(block)), target));
        var valid = ScriptEdits.Parse(Json(replace), target);
        Assert.Null(valid.NormalizationNotes);
        Assert.Equal(block.Text[..start] + "光……灭了。" + block.Text[end..], ScriptEdits.Materialize(target, valid)[0].Text);
    }

    [Fact]
    public void NormalizationCannotSmuggleNewSceneBoundariesIntoAScene()
    {
        var doc = Document(); var block = doc.Blocks[3];
        var target = ScriptStructure.Capture(doc, ScriptScope.Scene, new(block.Id, 0, block.Id, 1));
        var insert = Insert(block); insert["blocks"] = Blocks(ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. ELSEWHERE - NIGHT"));
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(Json(Replace(block), insert), target));
    }

    [Theory]
    [InlineData("unknown-id")] [InlineData("reversed-range")] [InlineData("invalid-side")]
    [InlineData("unknown-kind")] [InlineData("missing-blocks")] [InlineData("null-blocks")]
    [InlineData("empty-blocks")] [InlineData("invalid-block-kind")] [InlineData("invalid-span")]
    [InlineData("noncanonical-blocks")] [InlineData("missing-kind")]
    public void InvalidInputIsNeverPartiallyRecovered(string variant)
    {
        var doc = Document(); var block = doc.Blocks[3]; var replace = Replace(block); var insert = Insert(block);
        switch (variant)
        {
            case "unknown-id": insert["anchorId"] = Guid.NewGuid().ToString(); break;
            case "reversed-range": replace["endId"] = doc.Blocks[2].Id.ToString(); break;
            case "invalid-side": insert["side"] = "beside"; break;
            case "unknown-kind": insert["kind"] = "append"; break;
            case "missing-kind": insert.Remove("kind"); break;
            case "noncanonical-blocks": insert["Blocks"] = insert["blocks"]!.DeepClone(); insert.Remove("blocks"); break;
            case "missing-blocks": insert.Remove("blocks"); break;
            case "null-blocks": insert["blocks"] = null; break;
            case "empty-blocks": insert["blocks"] = new JsonArray(); break;
            case "invalid-block-kind": insert["blocks"]![0]!["kind"] = "Unknown"; break;
            case "invalid-span": insert["blocks"]![0]!["spans"]![0]!["bold"] = "true"; break;
        }
        Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(Json(replace, insert), Target(doc)));
    }

    [Theory]
    [InlineData("")] [InlineData("\n")] [InlineData("```json\n")] [InlineData("```JSON\r\n")]
    public void PlainAndSingleFencedJsonUseTheSameParser(string wrapper)
    {
        var doc = Document(); var raw = Json(Replace(doc.Blocks[3]), Insert(doc.Blocks[3]));
        var formatted = wrapper.StartsWith("```") ? wrapper + raw + "\n```" : wrapper + raw;
        Assert.Single(ScriptEdits.Parse(formatted, Target(doc)).NormalizationNotes!);
    }

    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("{\"version\":1,\"operations\":[null]}")]
    [InlineData("{\"version\":1,\"operations\":null}")] [InlineData("{\"version\":1,\"operations\":[")]
    [InlineData("{\"version\":2,\"operations\":[]}")] [InlineData("{\"operations\":[]}")]
    [InlineData("```json\n{\"version\":1,\"operations\":[]}\n```\nIgnored prose")]
    [InlineData("```python\n{\"version\":1,\"operations\":[]}\n```")]
    [InlineData("{\"version\":1,\"operations\":[]} {\"version\":1,\"operations\":[]}")]
    public void MalformedOrMultipleResponsesAreRejectedRatherThanGuessed(string raw)
        => Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(raw, Target(Document())));

    [Theory]
    [InlineData("root")] [InlineData("operation")] [InlineData("text")] [InlineData("formatting")]
    public void DuplicatePropertiesIncludingCaseVariantsAreNotLastKeyWins(string location)
    {
        var doc = Document(); var block = doc.Blocks[3]; var raw = Json(Replace(block), Insert(block));
        raw = location switch {
            "root" => raw.Replace("\"version\":1", "\"version\":1,\"Version\":1"),
            "operation" => raw.Replace("\"kind\":\"replace\"", "\"kind\":\"replace\",\"Kind\":\"delete\""),
            "text" => raw.Replace("\"text\":", "\"text\":\"discarded\",\"text\":"),
            _ => raw.Replace("\"bold\":false", "\"bold\":true,\"Bold\":false")
        };
        Assert.Contains("repeats a property", Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(raw, Target(doc))).Message);
    }

    [Fact]
    public void ResponseCountLimitAppliesBeforeCombiningAndEmptyOperationsRemainValid()
    {
        var doc = Document(); var raw = new JsonObject { ["version"] = 1,
            ["operations"] = new JsonArray(Enumerable.Range(0, 1001).Select(_ => (JsonNode)Replace(doc.Blocks[3])).ToArray()) }.ToJsonString();
        Assert.Contains("1,000", Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(raw, Target(doc))).Message);
        var none = ScriptEdits.Parse("{\"version\":1,\"operations\":[]}", Target(doc));
        Assert.Null(none.NormalizationNotes); Assert.Empty(none.Operations);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks), ScriptStructure.Fingerprint(ScriptEdits.Materialize(Target(doc), none)));
        Assert.Empty(ScriptEdits.Materialize(new(ScriptScope.Document, "Empty", []), none));
    }

    [Fact]
    public void NotesAreLocallyDerivedAndPersistWithoutChangingHistoricalJson()
    {
        var doc = Document(); var root = JsonNode.Parse(Json(Replace(doc.Blocks[3]), Insert(doc.Blocks[3])))!;
        root["normalizationNotes"] = new JsonArray("Forged claim");
        var parsed = ScriptEdits.Parse(root.ToJsonString(), Target(doc));
        Assert.DoesNotContain("Forged", Assert.Single(parsed.NormalizationNotes!));
        var saved = JsonSerializer.Deserialize<ScriptEditProposal>(JsonSerializer.Serialize(parsed, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(parsed.NormalizationNotes!.ToArray(), saved.NormalizationNotes!.ToArray());
        Assert.Equal(Content(ScriptEdits.Materialize(Target(doc), parsed)), Content(ScriptEdits.Materialize(Target(doc), saved)));
        var valid = ScriptEdits.Parse("{\"version\":1,\"operations\":[],\"normalizationNotes\":[\"Forged\"]}", Target(doc));
        Assert.Null(valid.NormalizationNotes);
        Assert.DoesNotContain("normalizationNotes", JsonSerializer.Serialize(new ScriptEditProposal(1, []), AtomicJsonFile.Options));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FullOffsetsUseOriginalUtf16LengthNotUnicodeScalarCount(bool utf16Length)
    {
        var doc = Document(); doc.Blocks[3] = doc.Blocks[3] with { Spans = [new("光🐇。", true, true)] };
        var block = doc.Blocks[3]; var replace = Replace(block);
        replace["startOffset"] = 0; replace["endOffset"] = utf16Length ? block.Text.Length : block.Text.Length - 1;
        if (!utf16Length) Assert.Throws<WorkspaceStoreException>(() => ScriptEdits.Parse(Json(replace, Insert(block)), Target(doc)));
        else {
            var parsed = ScriptEdits.Parse(Json(replace, Insert(block)), Target(doc));
            Assert.Single(parsed.NormalizationNotes!); Assert.Equal("光……灭了。", ScriptEdits.Materialize(Target(doc), parsed)[3].Text);
        }
        Assert.Equal("光🐇。", block.Text);
    }

    [Fact]
    public void ValidIndependentEditsRemainSeparateWithNoNormalizationNotice()
    {
        var doc = Document(); var raw = Json(Replace(doc.Blocks[3]), Insert(doc.Blocks[5]));
        var parsed = ScriptEdits.Parse(raw, Target(doc));
        Assert.Equal(2, parsed.Operations.Count); Assert.Null(parsed.NormalizationNotes);
        var result = ScriptEdits.Materialize(Target(doc), parsed);
        Assert.Equal("光……灭了。", result[3].Text); Assert.Equal(doc.Blocks[5].Id, result[5].Id);
        Assert.Equal("She gives the staff one experimental shake.", result[6].Text);
        Assert.Equal(doc.Blocks[^1].Id, result[^1].Id);
    }

    [Fact]
    public void ReprocessingCreatesASeparateProposalAndStillRequiresAnUnchangedTargetAndSingleAcceptance()
    {
        var doc = Document(); var target = Target(doc);
        var raw = Json(Replace(doc.Blocks[3]), Insert(doc.Blocks[3]));
        var source = new AssistantRun { Status = AssistantRunStatus.Completed, Operation = WritingOperation.Revise, EditFormat = 1,
            JobId = Guid.NewGuid(), Target = target, Output = raw, Error = "An insertion or move is anchored to another edited block.",
            SourceFingerprint = ScriptStructure.ContextFingerprint(doc), SourceRevision = doc.Revision };
        var original = JsonSerializer.Serialize(source);
        var recovered = ScriptProposals.CorrectJson(source, source.Output, Guid.NewGuid());
        Assert.NotEqual(source.Id, recovered.Id); Assert.Equal(source.Id, recovered.CorrectedFromRunId); Assert.Null(recovered.JobId);
        Assert.Equal(raw, recovered.Output); Assert.Null(recovered.Error); Assert.False(recovered.Applied);
        Assert.Equal(original, JsonSerializer.Serialize(source)); Assert.Single(recovered.Edits!.NormalizationNotes!);
        var applied = ScriptProposals.Apply(doc, recovered);
        Assert.Equal("光……灭了。", applied.Blocks[3].Text);
        Assert.Contains(recovered.Id, applied.AppliedProposalIds);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(applied, recovered));
        var changed = doc.Copy(); changed.Blocks[3] = changed.Blocks[3] with { Spans = [new("Edited since capture")] };
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(changed, recovered));
        Assert.Equal("你们还能这样？", doc.Blocks[3].Text);
    }

    [Theory]
    [InlineData(AiBackend.OpenRouter)] [InlineData(AiBackend.Codex)] [InlineData(AiBackend.ComfyUI)]
    public void QueuedParserAndEveryProviderUseTheNewRulesWithoutAlteringRawOutput(AiBackend backend)
    {
        var doc = Document(); var target = Target(doc);
        var run = new AssistantRun { Operation = WritingOperation.Revise, EditFormat = 1, Target = target, Backend = backend, Model = "test" };
        var script = new ScriptAssistantRequest(run, doc, []);
        var messages = ScriptAssistant.BuildMessages(script);
        var instructions = string.Join("\n", messages.Select(m => m.Text));
        Assert.Contains("simultaneously, NOT sequentially", instructions);
        Assert.Contains("emit ONE replace", instructions); Assert.Contains("SAME boundary", instructions);
        Assert.DoesNotContain("15 seconds", instructions);
        var request = new AiTextJobRequest(2, AiJobKind.ScriptAssistant, new(backend, "test", "Test"), false, new(),
            ScriptEdits.Profile, .7f, 42, JsonSerializer.SerializeToElement(script, AtomicJsonFile.Options), messages.Select(AiTextMessage.Capture).ToArray());
        var raw = Json(Replace(doc.Blocks[3]), Insert(doc.Blocks[3]));
        var result = AiTextResults.Parse(request, raw, ChatFinishReason.Stop.ToString());
        Assert.Null(result.Error); Assert.Equal(raw, result.Raw);
        Assert.Single(result.Read<AssistantUpdate>()!.Edits!.NormalizationNotes!);
        var truncated = AiTextResults.Parse(request, raw, "length");
        Assert.NotNull(truncated.Error); Assert.Null(truncated.Value); Assert.Equal(raw, truncated.Raw);
    }
}
