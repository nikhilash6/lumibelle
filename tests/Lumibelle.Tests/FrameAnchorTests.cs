using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(ShotImageUse.FirstFrame, "first", "last", "begins", "ends")]
    [InlineData(ShotImageUse.LastFrame, "last", "first", "ends", "begins")]
    public void SingleAnchorAddsOnlyItsEndpointAndKeepsTheAuthoredAction(ShotImageUse use, string endpoint, string other, string verb, string otherVerb)
    {
        var shot = Ready(); shot.Images = [Picture("Doorway")]; shot.Images[0].Use = use;
        var prompt = H3Policy.Compile(shot);
        Assert.Contains($"<Picture 1> defines the {endpoint} frame", prompt);
        Assert.DoesNotContain($"defines the {other} frame", prompt);
        Assert.Contains($"shot {verb}", prompt);
        Assert.DoesNotContain($"shot {otherVerb}", prompt);
        Assert.Contains(shot.Description, prompt);
        Assert.DoesNotContain("<Subject", prompt);
    }

    [Theory]
    [InlineData(ShotImageUse.FirstFrame)]
    [InlineData(ShotImageUse.LastFrame)]
    public void AnchorIsExclusiveAndKeepsExactImageIdentity(ShotImageUse use)
    {
        var (shot, library, asset) = ReferenceFixture();
        var binding = ReferenceSetups.Bind(asset, asset.Images[1], shot);
        var id = binding.Id; var media = binding.MediaId;
        binding.Use = use; shot.Images = [binding];
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
        binding.Purpose = null; binding.RepresentsId = null;
        H3Policy.Validate(shot); ShotLooks.Validate(shot, library);
        Assert.Equal(id, binding.Id); Assert.Equal(media, binding.MediaId); Assert.NotNull(binding.LookId);
        var duplicate = binding with { Id = Guid.NewGuid(), MediaId = asset.Images[0].Id };
        shot.Images.Add(duplicate);
        Assert.Contains("only one", Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot)).Message);
    }

    [Fact]
    public void AnchorGuidanceExcludesAssetAndLookDefaultsButRetainsImageAndExplicitOverrides()
    {
        var (shot, library, asset) = ReferenceFixture();
        asset.Images[1] = asset.Images[1] with { PreservationGuidance = "Keep the window on the left." };
        var b = ReferenceSetups.Bind(asset, asset.Images[1], shot);
        b.Use = ShotImageUse.FirstFrame; b.RepresentsId = null; b.Purpose = null;
        var resolved = ShotReferences.Resolve(b, library, new());
        Assert.Empty(resolved.AssetDefault); Assert.Empty(resolved.LookDefault);
        Assert.Equal("Keep the window on the left.", resolved.Effective);
        b.PreservationOverride = ""; Assert.Empty(ShotReferences.Resolve(b, library, new()).Effective);
        b.PreservationOverride = "Keep the open door.";
        Assert.Equal(b.PreservationOverride, ShotReferences.Resolve(b, library, new()).Effective);
        b.PreservationOverride = null; Assert.Equal(resolved, ShotReferences.Resolve(b, library, new()));
    }

    [Fact]
    public void AnchorsUseStandalonePicturesAndPreserveDialogueAndAudioAcrossReordering()
    {
        var shot = Ready(); shot.Description = "The traveler crosses the room.";
        shot.Dialogue = [new() { Speaker = "Traveler", Text = "The package is ready." }];
        var speaker = ShotReferences.Characters(shot).Single();
        shot.Images = [Picture("Room"), Picture("Traveler"), Picture("Room")];
        shot.Images[0].Use = ShotImageUse.FirstFrame;
        shot.Images[1].RepresentsId = speaker.Id; shot.Images[1].Purpose = ShotReferencePurpose.Identity;
        shot.Images[2].Use = ShotImageUse.LastFrame;
        shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = speaker.Name }];
        var prompt = H3Policy.Compile(shot);
        Assert.Contains("<Subject 1> is Traveler from <Picture 2>", prompt);
        Assert.DoesNotContain("<Subject 2>", prompt);
        Assert.Contains("<Picture 1> defines the first frame", prompt);
        Assert.Contains("<Picture 3> defines the last frame", prompt);
        Assert.Contains("[keyframe completion + reference generation + audio reference]", prompt);
        Assert.Contains("<Picture 1> ([Shot 1] first frame): fully_preserved", prompt);
        Assert.Contains("The shot begins from <Picture 1>", prompt);
        Assert.Contains("The shot ends on <Picture 3>", prompt);
        Assert.Contains("<d>[English] The package is ready.</d>", prompt);
        Assert.Contains("<Audio 1>", prompt);
        var captured = Snapshot(Guid.NewGuid(), shot);
        shot.Images.Reverse(); shot.Images[0].Crop = new() { Width = .5 };
        Assert.Contains("The shot begins from <Picture 3>", H3Policy.Compile(shot));
        Assert.Contains("The shot ends on <Picture 1>", H3Policy.Compile(shot));
        Assert.Equal(prompt, captured.Prompt); Assert.Equal(prompt, H3Policy.Compile(captured.Shot));
        Assert.NotEqual(H3Policy.Fingerprint(shot), captured.Fingerprint);
        var restored = JsonSerializer.Deserialize<VideoSnapshot>(JsonSerializer.Serialize(captured, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(captured.Fingerprint, H3Policy.Fingerprint(restored.Shot));
        Assert.Equal(captured.Prompt, H3Policy.Compile(restored.Shot));
    }

    [Fact]
    public void OrdinaryReferencesKeepLegacyPromptAndOptionalUseSerialization()
    {
        var shot = Ready(); shot.Images = [Picture("Room")];
        var prompt = H3Policy.Compile(shot);
        var expected = "subject_definitions:\n<Subject 1> is Room from <Picture 1>." + Environment.NewLine +
            "\nsummary:\n[reference generation] The arrival" + Environment.NewLine +
            "\nretention_analysis:" + Environment.NewLine + "<Subject 1> from <Picture 1>: reference role: Appearance. " + Environment.NewLine +
            "\ndetailed_description:" + Environment.NewLine + "[Shot 1] One continuous camera take lasting 1.625 seconds, without camera cuts or scene transitions. The camera holds as the mouse looks up." + Environment.NewLine +
            "\noverall_soundscape:\n" + shot.Atmosphere + Environment.NewLine + "\nnon_diegetic_music:\n" + shot.Music + Environment.NewLine;
        Assert.Equal(expected, prompt);
        var fingerprint = H3Policy.Fingerprint(shot);
        var json = JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
        Assert.DoesNotContain("\"use\"", json);
        Assert.Equal(fingerprint, H3Policy.Fingerprint(JsonSerializer.Deserialize<Shot>(json, AtomicJsonFile.Options)!));
        Assert.Equal(3, (int)ShotImageUse.ContinuityState);
        shot.Images[0].Use = ShotImageUse.FirstFrame;
        Assert.NotEqual(fingerprint, H3Policy.Fingerprint(shot));
        shot.Images[0].Use = null; Assert.Equal(fingerprint, H3Policy.Fingerprint(shot));
    }

    [Fact]
    public void ManualBindingsRetainCharacterPhasesWithoutReadingPreferredSelections()
    {
        var (shot, library, asset) = ReferenceFixture();
        shot.Images = asset.Images.Select(i => ReferenceSetups.Bind(asset, i, shot)).ToList();
        Assert.Equal(new[] { ShotReferencePurpose.Identity, ShotReferencePurpose.StartingLook, ShotReferencePurpose.EndingLook }, shot.Images.Select(b => b.Purpose!.Value));
        ShotLooks.Validate(shot, library);
        var before = ReferenceSetups.Hash(shot);
        library.Assets[0] = asset with { PreferredIdentityReferences = [] };
        Assert.Equal(before, ReferenceSetups.Hash(shot));
    }

    [Fact]
    public async Task AnchorQueueRecoveryAndAddedCandidatesKeepCapturedRolesAndPrompt()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Quiet room", Category = AssetCategory.Environment };
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        library = await f.Assets.SaveAsync(library with { Assets = [asset] }, library.Revision, _ct);
        using var bytes = new MemoryStream(AssetStoreTests.Png(32, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, bytes, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        f.Shot.Images = [ReferenceSetups.Bind(library.Assets[0], library.Assets[0].Images[0], f.Shot)];
        f.Shot.Images[0].Use = ShotImageUse.FirstFrame;
        f.Shot.Images[0].PreservationOverride = "Keep the doorway on the left.";
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var submission = await f.Capture(); var context = await f.Claim(submission);
        var persisted = await f.Jobs.ReadSnapshotAsync(submission.Id, _ct);
        var captured = persisted.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot;
        f.Shot.Images[0].Use = ShotImageUse.LastFrame; f.Shot.Images[0].PreservationOverride = "Later edit";
        f.Adapter.FailTransfer = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, persisted, _ct));
        f.Adapter.FailTransfer = false;
        await f.Jobs.ExtendBatchAsync(submission.Id, Guid.NewGuid(), submission.OriginTabId, _ct);
        await f.Worker.ExecuteAsync(context, await f.Jobs.ReadSnapshotAsync(submission.Id, _ct), _ct);
        var takes = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes;
        Assert.Equal(2, takes.Count);
        Assert.All(takes, take => { Assert.Equal(ShotImageUse.FirstFrame, take.Snapshot.Shot.Images[0].Use); Assert.Equal(captured.Prompt, take.Snapshot.Prompt); });
    }
}
