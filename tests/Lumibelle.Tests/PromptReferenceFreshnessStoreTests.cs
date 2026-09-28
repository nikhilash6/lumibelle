using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

// Uses the existing disk-backed production fixtures; no inference is involved.
public sealed partial class ShotTests
{
    [Fact]
    public async Task PromptReferenceFreshnessSurvivesNoOpSavesReferenceChangesRevertAndReload()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "freshness-jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        Assert.Equal(PromptReferenceState.Missing, PromptReferenceFreshness.Compare(c, null).State);
        c.Prompt = H3Policy.Compile(c.Shot);
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, null).State);
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        var baseline = c.Accepted!.ReferenceFingerprint; var acceptedId = c.Accepted.Id;
        Assert.True(PromptReferenceFreshness.IsFingerprint(baseline));
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var shots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        string Fingerprint(ProductionComposition value) => PromptReferenceFreshness.Fingerprint(value.Shot, ShotReferences.Resolve(value.Shot, library, shots));
        for (var i = 0; i < 3; i++) {
            c.Seed = i + 10; c.TakeCount = 1 + i;
            c = (await store.SaveAsync(f.Project.Id, c.Copy(), c.Version, _ct)).Compositions[0];
            Assert.Equal(acceptedId, c.Accepted!.Id); Assert.Equal(baseline, c.Accepted.ReferenceFingerprint);
            Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(c, Fingerprint(c)).State);
        }
        c.Shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Mira", Duration = 3 }];
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        Assert.Equal(PromptReferenceState.ReferencesChanged, PromptReferenceFreshness.Compare(c, Fingerprint(c)).State);
        c = (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0];
        Assert.Equal(PromptReferenceState.ReferencesChanged, PromptReferenceFreshness.Compare(c, Fingerprint(c)).State);
        c.Shot.Voices.Clear(); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(c, Fingerprint(c)).State);
        Assert.Equal(acceptedId, c.Accepted!.Id); Assert.Single(c.History);
    }

    [Fact]
    public async Task PromptReferenceFreshnessManualReviewAcceptsFreeformButNotEmptyDrafts()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock,
            new FileAiJobStore(Path.Combine(_root, "review-jobs"), _clock));
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = H3Policy.Compile(c.Shot); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        var first = c.Accepted!;
        c.Prompt = ""; c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct));
        var afterFailure = (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0];
        Assert.Equal(first.Id, afterFailure.Accepted!.Id); Assert.Single(afterFailure.History);
        c.Prompt = "Keep the lighting soft.";
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        Assert.NotEqual(first.Id, c.Accepted!.Id); Assert.Equal(first.ReferenceFingerprint, c.Accepted.ReferenceFingerprint);
        Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(c, c.Accepted.ReferenceFingerprint).State);
    }

    [Fact]
    public async Task PromptReferenceFreshnessSharedShotContentCarriesTheBaselineAcrossSetups()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock,
            new FileAiJobStore(Path.Combine(_root, "shared-freshness-jobs"), _clock));
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = H3Policy.Compile(c.Shot); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        var sibling = c.Copy(); sibling.Id = Guid.NewGuid(); sibling.Name = "HD setup"; sibling.Shot.Resolution = VideoResolution.Native;
        var document = await store.SaveAsync(f.Project.Id, sibling, 0, _ct);
        var stamps = document.Compositions.Select(x => x.Accepted!.ReferenceFingerprint).Distinct().ToArray();
        Assert.Single(stamps); Assert.Equal(c.Accepted!.ReferenceFingerprint, stamps[0]);
        Assert.Equal(stamps[0], Assert.Single(document.ShotContent).History.Single().ReferenceFingerprint);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PromptReferenceFreshnessAppliedAiResultCapturesTheRequestReferences(bool automatic)
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "apply-freshness-jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct); var shots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var model = new TextModelReference(AiBackend.OpenRouter, "test/model", "Test model");
        var effective = ShotVideoDefaults.Capture(c.Shot, f.Project);
        var payload = new PromptCompositionRequest(f.Project.Id, c.Id, c.Version,
            ProductionPolicy.ContextFingerprint(c, library, shots, f.Project), c.SourceFingerprint,
            effective, "Scene context", [], ShotReferences.Resolve(effective, library, shots), [], [], "", "", "", model);
        var request = new AiTextJobRequest(2, AiJobKind.PromptComposition, model, false, new(), ProductionPolicy.Profile, .7f, 42,
            JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options),
            [new("system", [new(Text: "Return the prompt.")]), new("user", [new(Text: "Captured request.")])]);
        var id = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.PromptComposition, model.Backend,
            new(f.Project.Id, ShotId: c.ShotId, CompositionId: c.Id), "Test", "Compose", Guid.NewGuid(), request), _ct);
        c.ReviewJobId = id;
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var result = new PromptCompositionResult(H3Policy.Compile(effective), "Preserve the intended identity.");
        await jobs.WriteArtifactAsync(id, AiJobArtifact.Result, new AiTextJobResult("{}", true, "stop",
            JsonSerializer.SerializeToElement(result, AtomicJsonFile.Options)), _ct);
        await jobs.UpdateAsync(id, j => j with { State = AiJobState.Completed }, _ct);
        c = (await store.ApplyResultAsync(f.Project.Id, id, automatic, _ct)).Compositions[0];
        Assert.Equal(id, c.AppliedJobId);
        Assert.Equal(PromptReferenceFreshness.Fingerprint(payload.Shot, payload.Guidance), c.Accepted!.ReferenceFingerprint);
        Assert.Equal(result.Prompt, c.Prompt);
    }
}
