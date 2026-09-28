using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public void ReelLorasPreserveLegacyFingerprintsAndCopyIndependently()
    {
        var draft = ReferenceReelTests.Recipe();
        var legacy = JsonSerializer.SerializeToNode(draft, AtomicJsonFile.Options)!.AsObject();
        Assert.False(legacy.ContainsKey("loras"));
        var baseline = ReferenceReels.Fingerprint(draft);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy, AtomicJsonFile.Options))), baseline);
        Assert.Equal(baseline, ReferenceReels.Fingerprint(legacy.Deserialize<ReferenceReelDraft>(AtomicJsonFile.Options)!));

        var selections = new[] { VideoLora() };
        draft.Loras = selections;
        Assert.NotEqual(baseline, ReferenceReels.Fingerprint(draft));
        var copy = draft.Copy(); var inputs = ReferenceReels.Inputs(draft);
        selections[0] = selections[0] with { Strength = .2f };
        Assert.Equal(.7f, Assert.Single(copy.Loras!).Strength);
        Assert.Equal(.7f, Assert.Single(inputs.Loras!).Strength);
        ReferenceReels.Validate(copy);
        copy.Loras = [VideoLora() with { Reference = VideoLora().Reference with { Workflow = LoraWorkflow.Krea2 } }];
        Assert.Contains("H3 LoRAs", Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(copy)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelLoraCaptureBuildsOrderedGraphAndOneMoreKeepsItAfterRecipeEdits(bool environment)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var identity = VideoLora(server: f.Settings.Value.ComfyUrl);
        var lighting = VideoLora("h3/styles/film.safetensors", 1.2f, f.Settings.Value.ComfyUrl);
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(identity.Reference), new(lighting.Reference)] };
        var submission = await f.CaptureReel([lighting, identity], environment); var context = await f.Claim(submission);

        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var draft = library.ReelDrafts.Single().Copy(); draft.Loras = null;
        await f.Assets.SaveDraftAsync(f.Project.Id, draft, draft.Revision, _ct);
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [] };
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        await f.Worker.ValidateExtensionAsync(context.Job, submission.Snapshot, _ct);
        await f.Jobs.ExtendBatchAsync(submission.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        var claim = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(claim, false), await f.Jobs.ReadSnapshotAsync(claim.Id, _ct), _ct);

        library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Null(library.ReelDrafts.Single().Loras); Assert.Equal(2, library.Reels.Count);
        Assert.All(library.Reels, reel => {
            Assert.Equal(new[] { lighting, identity }, reel.Generation!.Recipe.Loras);
            Assert.Equal(new[] { 1.2f, .7f }, reel.Generation.Snapshot.AppliedLoras!.Select(l => l.Strength));
        });
        Assert.Equal(2, f.Graphs.Count);
        Assert.All(f.Graphs, graph => {
            var nodes = graph.GetProperty("prompt");
            Assert.Equal(lighting.Reference.FileName, nodes.GetProperty("lora_1").GetProperty("inputs").GetProperty("lora_name").GetString());
            Assert.Equal(1.2f, nodes.GetProperty("lora_1").GetProperty("inputs").GetProperty("strength_model").GetSingle());
            Assert.Equal(identity.Reference.FileName, nodes.GetProperty("lora_2").GetProperty("inputs").GetProperty("lora_name").GetString());
            Assert.Equal(.7f, nodes.GetProperty("lora_2").GetProperty("inputs").GetProperty("strength_model").GetSingle());
        });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ReelLorasRequireAvailableFilesOnlyWhenActive(bool enabled)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var selection = VideoLora(server: f.Settings.Value.ComfyUrl) with { Enabled = enabled };
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(selection.Reference)] };
        f.Generator.OptionalLoraCatalog = _ => new(true, "Ready", []);
        if (enabled)
            Assert.Contains("missing", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.CaptureReel([selection]))).Message);
        else {
            var submission = await f.CaptureReel([selection]);
            Assert.Null(submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.AppliedLoras);
        }
        Assert.Empty(f.Graphs);
    }
}
