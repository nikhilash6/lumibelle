using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ContentProbePolicyTests
{
    [Theory]
    [InlineData("NO", true, ContentProbeOutcome.Refused)]
    [InlineData(" \nno\t", true, ContentProbeOutcome.Refused)]
    [InlineData("No problem. Here is a scene.", true, ContentProbeOutcome.ResponseReceived)]
    [InlineData("NO!", true, ContentProbeOutcome.ResponseReceived)]
    [InlineData("The character said NO.", true, ContentProbeOutcome.ResponseReceived)]
    [InlineData("NO", false, ContentProbeOutcome.ResponseReceived)]
    [InlineData("", true, ContentProbeOutcome.Inconclusive)]
    public void MarkerOnlyClassifiesTheWholeCompletedAnswerWhenEnabled(string text, bool marker, ContentProbeOutcome expected)
    {
        var request = ContentProbeFixture.Request(ContentProbeFixture.Test() with { UseRefusalMarker = marker });
        Assert.Equal(expected, ContentProbePolicy.Outcome(request, new(text, true, "stop")));
    }
    [Theory]
    [InlineData("length")]
    [InlineData("local_output_limit")]
    [InlineData("tool_calls")]
    [InlineData(null)]
    public void IncompleteOrAbnormalCompletionIsNeverAPassingResponseOrARefusal(string? finish)
    {
        var request = ContentProbeFixture.Request();
        Assert.Equal(ContentProbeOutcome.Inconclusive, ContentProbePolicy.Outcome(request, new("NO", true, finish)));
        Assert.Equal(ContentProbeOutcome.Inconclusive, ContentProbePolicy.Outcome(request, new("NO", false, "stop")));
    }
    [Fact]
    public void ApiRefusalAndProviderBlockingAreDistinctFromEmptyOutput()
    {
        var request = ContentProbeFixture.Request(ContentProbeFixture.Test() with { UseRefusalMarker = false });
        Assert.Equal(ContentProbeOutcome.Refused, ContentProbePolicy.Outcome(request, new("", true, "stop", "I decline.")));
        Assert.Equal(ContentProbeOutcome.ProviderBlocked, ContentProbePolicy.Outcome(request, new("", true, "content_filter")));
        Assert.Equal(ContentProbeOutcome.Inconclusive, ContentProbePolicy.Outcome(request, new("", true, "stop")));
    }
    [Theory]
    [InlineData("What the SHIT?", "shit", true)]
    [InlineData("shithead", "shit", false)]
    [InlineData("f**k", "fuck", false)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("anything", ".*", false)]
    [InlineData("Det är öppet.", "ÖPPET", true)]
    public void PhraseChecksAreLiteralUnmaskedAndDoNotMatchPartialWords(string text, string phrase, bool expected) =>
        Assert.Equal(expected, ContentProbePolicy.ContainsPhrase(text, phrase));

    [Fact]
    public void StartersAreStableNonGraphicCopiesAndDefinitionsDoNotExposeSharedLists()
    {
        var first = ContentProbeBuiltIns.All(new()); var second = ContentProbeBuiltIns.All(new());
        Assert.Equal(6, first.Count); Assert.Equal(first.Select(p => p.Id), second.Select(p => p.Id));
        Assert.All(first, ContentProbePolicy.Validate);
        var profanity = first.Single(p => p.RequiredPhrases.Count == 2);
        ((string[])profanity.RequiredPhrases)[0] = "modified";
        Assert.DoesNotContain("modified", ContentProbeBuiltIns.All(new()).SelectMany(p => p.RequiredPhrases));
    }
    [Fact]
    public void SavedPromptAndExactQueueIdentityAreValidated()
    {
        var request = ContentProbeFixture.Request();
        var header = ContentProbeFixture.Header(request);
        Assert.Equal(request.Model, ContentProbePolicy.Read(header, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)).Model);
        Assert.Throws<WorkspaceStoreException>(() => ContentProbePolicy.ValidateRequest(request with { SubmittedPrompt = "Changed" }));
        Assert.Throws<WorkspaceStoreException>(() => ContentProbePolicy.ValidateRequest(request with { Version = 99 }));
        Assert.Throws<WorkspaceStoreException>(() => ContentProbePolicy.Read(header with { Id = Guid.NewGuid() }, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)));
        Assert.StartsWith("/settings/ai/probes?jobId=", header.ReviewUrl);
        Assert.NotEqual(header.Target.LockKey(header.Kind), new AiJobTarget(ModelKey: Guid.NewGuid().ToString("N")).LockKey(header.Kind));
    }
    [Fact]
    public void SummariesUseOnlyExplicitRatingsAndNeverPoolRevisedConfigurations()
    {
        var request = ContentProbeFixture.Request(); var result = new ContentProbeResult("A scene", true, "stop");
        ContentProbeRow Row(ContentProbeRequest r, int? score) => new(ContentProbeFixture.Header(r), r, result,
            new(r.JobId, 1, score, OutputFingerprint: ContentProbePolicy.OutputFingerprint(result)));
        var first = Row(request, 5);
        var second = Row(request with { JobId = Guid.NewGuid(), Iteration = 2, Seed = 99 }, 3);
        var unrated = Row(request with { JobId = Guid.NewGuid() }, null);
        var changed = Row(request with { JobId = Guid.NewGuid(), Model = request.Model with { Temperature = 1 } }, 1);
        var summaries = ContentProbePolicy.Summaries([first, second, unrated, changed]);
        Assert.Equal(2, summaries.Count);
        var original = summaries.Single(s => s.Responses == 3);
        Assert.Equal(2, original.Rated); Assert.Equal(4d, original.Average);
        Assert.NotEqual(ContentProbePolicy.ComparisonKey(request), ContentProbePolicy.ComparisonKey(request with { Probe = request.Probe with { Revision = 2 } }));
        Assert.Null(ContentProbePolicy.CurrentScore(first with { Result = result with { Raw = "Different response" } }));
        Assert.Null(ContentProbePolicy.CurrentScore(first with { Job = first.Job with { State = AiJobState.Cancelled } }));
    }
}
