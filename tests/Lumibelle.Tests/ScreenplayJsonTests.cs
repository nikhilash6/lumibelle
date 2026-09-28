using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScreenplayJsonTests
{
    [Theory]
    [InlineData("{}", "Expected a JSON array")]
    [InlineData("[]", "array is empty")]
    [InlineData("[null]", "Block 1: expected an object")]
    [InlineData("[{\"kind\":\"Camera\"}]", "Block 1: \"kind\"")]
    [InlineData("[{\"kind\":\"Action\",\"sp,ans\":[]}]", "Block 1: missing or invalid \"spans\"")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[null]}]", "Block 1, span 1: missing or invalid \"text\"")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[{\"text,\":\"A kettle whistles.\"}]}]", "Block 1, span 1: missing or invalid \"text\"")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[{\"text\":\"A kettle whistles.\",\"bold\":\"yes\"}]}]", "Block 1, span 1: \"bold\" must be true or false")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[]}]", "Block 1: text must not be empty")]
    public void InvalidStructureReportsTheSpecificBlockAndField(string json, string error)
    {
        var result = ScreenplayJson.Parse(json);
        Assert.Null(result.Blocks); Assert.Contains(error, result.Error);
    }
    [Fact]
    public void SyntaxErrorsPointToTheOriginalResponseLineIncludingItsWrapper()
    {
        var result = ScreenplayJson.Parse("A draft:\n\n```json\n[\n{\"kind\" \"Action\"}\n]\n```");
        Assert.Null(result.Blocks); Assert.Contains("Invalid JSON at line 5, column 9", result.Error);
        Assert.Contains("expected", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void SyntaxErrorColumnsCountTextInsteadOfUtf8Bytes()
    {
        const string raw = """[{"kind":"Action","spans":[{"text":"森 🌲", "bold":@}]}]""";
        Assert.Contains($"column {raw.IndexOf('@') + 1}:", ScreenplayJson.Parse(raw).Error);
    }
    [Fact]
    public void CorrectingATerminalResponseCreatesAnUnappliedProposalAndKeepsTheCapturedTarget()
    {
        var doc = ScriptFixtures.Document();
        var source = ScriptTests.Proposal(doc, ScriptStructure.Capture(doc, ScriptScope.Document, null), []) with
        { JobId = Guid.NewGuid(), Status = AssistantRunStatus.Cancelled, Error = "Interrupted", Output = "Damaged original response" };
        const string json = """[{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        var correction = ScriptProposals.CorrectJson(source, json, Guid.NewGuid());
        Assert.NotEqual(source.Id, correction.Id); Assert.Null(correction.JobId); Assert.Equal(source.Id, correction.CorrectedFromRunId);
        Assert.Equal(source.SourceFingerprint, correction.SourceFingerprint); Assert.Equal(JsonSerializer.Serialize(source.Target), JsonSerializer.Serialize(correction.Target));
        Assert.NotSame(source.Target.OriginalBlocks, correction.Target.OriginalBlocks);
        Assert.Equal(json, correction.Output); Assert.Null(correction.Error); Assert.False(correction.Applied);
        Assert.Equal("Damaged original response", source.Output); Assert.Equal("Interrupted", source.Error);
        var applied = ScriptProposals.Apply(doc, correction);
        Assert.Equal("A kettle whistles.", Assert.Single(applied.Blocks).Text);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(applied, correction));
        var changed = doc.Copy(); changed.Blocks[1] = ScriptBlock.Create(ScriptBlockKind.Action, "A different draft.");
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(changed, correction));
    }
    [Fact]
    public void CorrectionRejectsBrokenJsonAndRunningOrDiscussionSources()
    {
        var source = new AssistantRun { Status = AssistantRunStatus.Completed, Operation = WritingOperation.Draft };
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.CorrectJson(source, "broken", Guid.NewGuid()));
        const string json = """[{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.CorrectJson(source with { Status = AssistantRunStatus.Running }, json, Guid.NewGuid()));
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.CorrectJson(source with { Operation = WritingOperation.Discuss }, json, Guid.NewGuid()));
    }
}
