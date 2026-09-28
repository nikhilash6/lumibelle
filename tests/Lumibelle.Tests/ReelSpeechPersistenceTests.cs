using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task ReelSpeechSettingsAndExactWordsSurviveDraftRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley");
        await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var draft = ReferenceReels.NewDraft(owner);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        draft.Language = "Swedish"; draft.Line = "Min egen svenska text, inte katalogens.";
        draft.Speech = draft.Speech! with { Length = ReelSpeechLength.Short };
        draft.VoiceDescription = "Warm and lightly raspy.";
        var expected = ReferenceReels.InputsFingerprint(draft);
        var saved = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var reopened = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single(d => d.Id == saved.Id);
        Assert.Equal(expected, ReferenceReels.InputsFingerprint(reopened));
        Assert.Equal(draft.Speech, reopened.Speech); Assert.Equal(draft.Line, reopened.Line);
        Assert.Equal(draft.Language, reopened.Language); Assert.Equal(draft.VoiceDescription, reopened.VoiceDescription);
        Assert.Equal(ReelFraming.CharacterVoiceReference, reopened.Framing);
    }

    [Theory]
    [InlineData("unchanged")] [InlineData("language")] [InlineData("duration")] [InlineData("passage")] [InlineData("dialogue")]
    public async Task ChangedReelSpeechRejectsAnEarlierAssistPairWithoutLosingAuthorText(string change)
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        using var image = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, owner.Id, image, new("face.png", [], AssetImageOrigin.Imported), library.Revision, ct);
        var draft = ReferenceReels.NewDraft(owner); ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        draft.Images = [new() { AssetId = owner.Id, MediaId = library.Assets.Single().Images.Single().Id, InferUsage = true }];
        var jobId = Guid.NewGuid();
        draft.PendingJobId = jobId;
        draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var inputs = await ProductionInputs.CaptureAsync(project.Id, ReferenceReels.Inputs(draft), store, ct);
        var request = new ReelCompositionRequest(project.Id, draft.Copy(), ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), inputs.Select(i => i.Identity).ToArray())
        { ImageGuidance = ShotReferences.Resolve(ReferenceReels.Inputs(draft), library, new()) };
        var pair = ReferenceReels.Preset(draft);
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelComposition, Backend = AiBackend.OpenRouter,
            Target = new(project.Id, owner.Id, ReelId: draft.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Completed };
        switch (change)
        {
            case "language": draft.Language = "French"; break;
            case "duration": draft.Duration = 8; break;
            case "passage": draft.Speech = draft.Speech! with { Length = ReelSpeechLength.Short }; break;
            case "dialogue": draft.Line = "New author wording."; break;
        }
        if (change != "unchanged") await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct);
        Assert.Equal(change == "unchanged", await store.ApplyPairAsync(job, request, pair, true, ct));
        var current = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single(d => d.Id == draft.Id);
        Assert.Equal(draft.Line, current.Line); Assert.Equal(draft.Language, current.Language);
        Assert.Equal(draft.Speech, current.Speech); Assert.Equal(draft.Duration, current.Duration);
        Assert.Equal(change == "unchanged" ? pair.Prompt : "", current.Prompt);
    }
}
