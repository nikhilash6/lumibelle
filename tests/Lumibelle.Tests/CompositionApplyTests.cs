using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("unchanged", true)] [InlineData("seed", true)] [InlineData("takes", true)] [InlineData("name", true)]
    [InlineData("prompt", false)] [InlineData("direction", false)] [InlineData("revision notes", false)]
    [InlineData("source", false)] [InlineData("crop", false)] [InlineData("review", false)] [InlineData("shot title", false)]
    public async Task ExplicitCompositionApplyChecksInputsInsteadOfRejectingEveryExtraSave(string change, bool allowed)
    {
        var f = Fixture(); var shot = Ready();
        var source = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(80, 40));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Shot.Images = [ReferenceSetups.Bind(library.Assets[0], library.Assets[0].Images[0], c.Shot)];
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var images = await ProductionInputs.CaptureAsync(f.Project.Id, c.Shot, f.Assets, _ct);
        var request = SoundRequest(ShotVideoDefaults.Capture(c.Shot, f.Project)) with {
            ProjectId = f.Project.Id, CompositionId = c.Id, CompositionVersion = c.Version,
            ContextFingerprint = ProductionPolicy.ContextFingerprint(c, library, source, f.Project),
            Guidance = ShotReferences.Resolve(c.Shot, library, source),
            Images = images.Select(i => i.Identity).ToArray() };
        var snapshot = new AiTextJobRequest(2, AiJobKind.PromptComposition, request.Model, false, new(), ProductionPolicy.Profile, .7f, 1,
            JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), [new("system", [new(Text: "Saved instructions")]),
                new("user", images.Select(i => new AiTextPart(Image: i.Bytes, MediaType: "image/png")).ToArray())]);
        var submission = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: shot.Id, CompositionId: c.Id), "Project", "Compose", Guid.NewGuid(), snapshot);
        await jobs.EnqueueAsync(submission, _ct);
        var proposed = new PromptCompositionResult(H3Policy.Compile(request.Shot), "The chosen room view.");
        var result = AiTextResults.Parse(snapshot, JsonSerializer.Serialize(proposed, AtomicJsonFile.Options), "stop");
        Assert.Null(result.Error);
        await jobs.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, result, _ct);
        await jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed }, _ct);
        c.ReviewJobId = submission.Id;
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        switch (change)
        {
            case "seed": c.Seed = 42; break;
            case "takes": c.TakeCount = 3; break;
            case "name": c.Name = "Renamed setup"; break;
            case "prompt": c.Prompt = "My own newer text"; break;
            case "direction": c.DirectingNotes = "Keep a different angle"; break;
            case "revision notes": c.RevisionNotes = "A new request direction"; break;
            case "crop": c.Shot.Images[0].Crop = new() { Width = .5, Height = 1 }; break;
            case "review": c.ReviewJobId = Guid.NewGuid(); break;
            case "source":
                var latest = await f.Shots.LoadAsync(f.Project.Id, _ct);
                latest.Shots[0].Description = "New authored action";
                await f.Shots.SaveAsync(f.Project.Id, latest.Shots, latest.Revision, ct: _ct); break;
            case "shot title":
                var titled = await f.Shots.LoadAsync(f.Project.Id, _ct);
                titled.Shots[0].Title = "Nelly is busy";
                await f.Shots.SaveAsync(f.Project.Id, titled.Shots, titled.Revision, ct: _ct); break;
        }
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        // Extra revisions are never applied automatically, even when they changed no inputs.
        Assert.Equal(c.Prompt, (await store.ApplyResultAsync(f.Project.Id, submission.Id, true, _ct)).Compositions[0].Prompt);
        if (change == "review")
        {
            // A superseded review is never applied, not even on request.
            await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.ApplyResultAsync(f.Project.Id, submission.Id, false, _ct, acceptChangedInputs: true));
            Assert.Equal(c.Prompt, (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Prompt);
        }
        else if (!allowed)
        {
            // Changed inputs are named for the author and can then be applied anyway.
            var changed = await Assert.ThrowsAsync<AssistedInputsChangedException>(() => store.ApplyResultAsync(f.Project.Id, submission.Id, false, _ct));
            Assert.Contains(change switch
            {
                "prompt" => "the prompt was edited", "direction" => "the Direction for AI changed", "revision notes" => "the revision notes changed",
                "source" => "the shot’s scene, action, dialogue or cast changed", "crop" => "the references, crops or their guidance changed",
                _ => "the shot was renamed"
            }, changed.Changes);
            if (change == "shot title") Assert.Single(changed.Changes);
            Assert.Equal(c.Prompt, (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Prompt);
            var applied = (await store.ApplyResultAsync(f.Project.Id, submission.Id, false, _ct, acceptChangedInputs: true)).Compositions[0];
            Assert.Equal(proposed.Prompt, applied.Prompt); Assert.Equal(submission.Id, applied.AppliedJobId); Assert.Null(applied.ReviewJobId);
        }
        else
        {
            var applied = (await store.ApplyResultAsync(f.Project.Id, submission.Id, false, _ct)).Compositions[0];
            Assert.Equal(proposed.Prompt, applied.Prompt); Assert.Equal(proposed.ReferenceUsage, applied.ReferenceUsage);
            Assert.Equal(submission.Id, applied.AppliedJobId); Assert.Null(applied.ReviewJobId);
            Assert.Equal(c.Seed, applied.Seed); Assert.Equal(c.TakeCount, applied.TakeCount); Assert.Equal(c.Name, applied.Name);
            Assert.Equal(proposed.Prompt, Assert.Single(applied.History).Prompt);
            Assert.Equal(applied.Version, (await store.ApplyResultAsync(f.Project.Id, submission.Id, false, _ct)).Compositions[0].Version);
        }
        var retained = (await jobs.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        Assert.Equal(result.Raw, retained.Raw); Assert.Equal(result.Complete, retained.Complete);
        Assert.Equal(result.FinishReason, retained.FinishReason); Assert.Equal(result.Error, retained.Error);
        Assert.True(JsonElement.DeepEquals(result.Value!.Value, retained.Value!.Value));
        Assert.Single((await jobs.ReadAsync(_ct)).Jobs);
    }
}
