using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static ShotImageBinding Picture(string name, Guid? asset = null) => new()
        { AssetId = asset ?? Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = name };

    [Fact]
    public void RepresentsGroupsPicturesAndLinksDialogueWithoutChangingExactWords()
    {
        var shot = Ready();
        shot.Dialogue = [new() { Speaker = "JUNIPER", Text = "Juniper says: You called?" }, new() { Speaker = "juniper", Text = "Yes." }];
        var character = ShotReferences.Characters(shot).Single();
        shot.Images = [Picture("Juniper"), Picture("Bedroom"), Picture("Juniper armor")];
        shot.Images[0].RepresentsId = character.Id; shot.Images[2].RepresentsId = character.Id;
        shot.Description = "JUNIPER moves. A sign reads \"JUNIPER\". ‘Juniper’ is written below. Juniper armor gleams. Bedroom stays still.";
        shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "juniper" }];
        var prompt = H3Policy.Compile(shot);
        Assert.Contains("<Subject 1> is JUNIPER from <Picture 1> and <Picture 3>", prompt);
        Assert.Contains("<Subject 2> is Bedroom from <Picture 2>", prompt);
        Assert.DoesNotContain("<Subject 3>", prompt);
        Assert.Contains("JUNIPER (<Subject 1>) moves", prompt);
        Assert.Contains("\"JUNIPER\"", prompt);
        Assert.Contains("‘Juniper’ is written below", prompt);
        Assert.Contains("Juniper armor (<Subject 1>) gleams", prompt);
        Assert.Contains("<Audio 1> is the voice-timbre reference for <Subject 1> (S1)", prompt);
        Assert.Contains("<Subject 1> (S1) says <d>[English] Juniper says: You called?</d>", prompt);
        Assert.Contains("<Subject 1> (S1) says <d>[English] Yes.</d>", prompt);
        Assert.Equal(prompt, H3Policy.Compile(shot.Copy()));
        shot.Images.Reverse();
        Assert.Contains("<Subject 1> is JUNIPER from <Picture 1> and <Picture 3>", H3Policy.Compile(shot));
    }

    [Fact]
    public void NamesDoNotAutomaticallyAssignPicturesAndAmbiguousAliasesAreNotRewritten()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "JUNIPER", Text = "Hello." }];
        shot.Images = [Picture("Juniper"), Picture("Juniper")]; shot.Description = "Juniper walks past Juniperella.";
        var prompt = H3Policy.Compile(shot);
        Assert.Contains("JUNIPER (S1) says", prompt);
        Assert.DoesNotContain("Juniper (<Subject", prompt);
        Assert.Contains("Juniper walks past Juniperella.", prompt);
    }

    [Fact]
    public void CharacterIdentitySurvivesRenameFirstLineRemovalAndSerialization()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "JUNIPER", Text = "One." }, new() { Speaker = "JUNIPER", Text = "Two." }];
        var id = ShotReferences.Characters(shot).Single().Id;
        shot.Images = [Picture("Juniper")]; shot.Images[0].RepresentsId = id;
        shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "JUNIPER" }];
        ShotReferences.ChangeSpeaker(shot, shot.Dialogue[0].Id, "MOUSE");
        Assert.All(shot.Dialogue, line => Assert.Equal("MOUSE", line.Speaker));
        Assert.Equal("MOUSE", shot.Voices[0].Speaker); Assert.Equal(id, shot.Images[0].RepresentsId);
        shot.Dialogue.RemoveAt(0); shot = shot.Copy();
        Assert.Equal(id, ShotReferences.Characters(shot).Single().Id);
        Assert.Contains("<Subject 1> (S1) says", H3Policy.Compile(shot));
        shot.Dialogue.Clear(); shot.Voices.Clear();
        Assert.Contains("<Subject 1> is MOUSE", H3Policy.Compile(shot));
    }

    [Fact]
    public void SpeakerReassignmentAndBlankDraftDoNotReuseAnotherCharactersId()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "A", Text = "One." }, new() { Speaker = "B", Text = "Two." }];
        ShotReferences.RetainCharacters(shot);
        var a = ShotReferences.Character(shot, "A")!.Id;
        ShotReferences.ChangeSpeaker(shot, shot.Dialogue[0].Id, "B");
        Assert.Equal(a, ShotReferences.Character(shot, "A")!.Id);
        ShotReferences.ChangeSpeaker(shot, shot.Dialogue[0].Id, "");
        ShotReferences.ChangeSpeaker(shot, shot.Dialogue[0].Id, "C");
        Assert.NotEqual(a, ShotReferences.Character(shot, "C")!.Id);
        H3Policy.Validate(shot, true);
        shot.Images = [Picture("A")]; shot.Images[0].RepresentsId = Guid.NewGuid();
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
    }

    [Fact]
    public void GuidanceInheritsOnlyExplicitDefaultsAndRetainsLegacyOrEmptyOverrides()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "test.png", ContentType = "image/png", Width = 32, Height = 32, PreservationGuidance = "White dress." };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", Description = "Not preservation instructions", PreservationGuidance = "Dark hair.", Images = [image] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] };
        var binding = Picture(asset.Name, asset.Id); binding.MediaId = image.Id;
        var inherited = ShotReferences.Resolve(binding, library, new());
        Assert.Null(inherited.Override); Assert.Equal("Dark hair.\nWhite dress.", inherited.Effective);
        binding.Notes = "Existing shot instructions.";
        Assert.Equal(binding.Notes, ShotReferences.Resolve(binding, library, new()).Effective);
        binding.PreservationOverride = "";
        Assert.Empty(ShotReferences.Resolve(binding, library, new()).Effective);
        binding.Notes = ""; binding.PreservationOverride = null;
        Assert.Equal(inherited, ShotReferences.Resolve(binding, library, new()));
        library = library with { Assets = [asset with { PreservationGuidance = "Changed identity." }] };
        Assert.Equal("Dark hair.\nWhite dress.", inherited.Effective);
        Assert.NotEqual(inherited, ShotReferences.Resolve(binding, library, new()));
        binding.MediaId = Guid.NewGuid();
        Assert.Empty(ShotReferences.Resolve(binding, library, new()).ImageDefault);
        binding.MediaId = image.Id;
        library = library with { Assets = [asset with { PreservationGuidance = new('a', 12000), Images = [image with { PreservationGuidance = new('b', 12000) }] }] };
        binding.PreservationOverride = ShotReferences.Resolve(binding, library, new()).Effective;
        var shot = Ready(); shot.Images = [binding]; H3Policy.Validate(shot, true);
    }

    [Fact]
    public void RemovedFrameBindingRemainsVisibleWithItsCapturedNotes()
    {
        var binding = new ShotImageBinding { Kind = ShotImageKind.ContinuityFrame, MediaId = Guid.NewGuid(), Notes = "Hand resting on the door." };
        var shot = Ready(); shot.Images = [binding]; H3Policy.Validate(shot);
        var loaded = ShotCopy.Of(shot);
        Assert.Equal(binding.MediaId, loaded.Images[0].MediaId);
        Assert.Equal(binding.Notes, ShotReferences.Resolve(binding, new() { ProjectId = Guid.NewGuid() }, new()).Effective);
    }

    [Fact]
    public void LegacyOptionalFieldsLoadEmptyWithoutChangingShotFingerprint()
    {
        var shot = Ready(); shot.Images = [Picture("Juniper")];
        var json = JsonSerializer.SerializeToNode(shot, AtomicJsonFile.Options)!;
        json.AsObject().Remove("characters");
        json.AsObject().Remove("turboSteps");
        json.AsObject().Remove("videos"); // Keep this fixture in its original serialized format.
        foreach (var image in json["images"]!.AsArray()) { image!.AsObject().Remove("representsId"); image.AsObject().Remove("preservationOverride"); image.AsObject().Remove("lookId"); image.AsObject().Remove("purpose"); }
        var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(json, AtomicJsonFile.Options)));
        var loaded = json.Deserialize<Shot>(AtomicJsonFile.Options)!;
        Assert.Empty(loaded.Characters); Assert.Null(loaded.Images[0].RepresentsId); Assert.Equal(expected, H3Policy.Fingerprint(loaded));
        var asset = JsonSerializer.Deserialize<ReferenceAsset>("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Old\"}", AtomicJsonFile.Options)!;
        Assert.Empty(asset.PreservationGuidance);
    }

    [Fact]
    public async Task DefaultsSurviveImagePersistenceAssetDeletionAndTrashRecreationWithConflicts()
    {
        var f = Fixture();
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", PreservationGuidance = "Dark hair." };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        await using var bytes = new MemoryStream(AssetStoreTests.Png(32, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, bytes, new("image.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var image = library.Assets[0].Images[0] with { PreservationGuidance = "Gray hoodie." };
        library = await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { Images = [image] }] }, library.Revision, _ct);
        var stale = library.Copy();
        library = await f.Assets.DeleteAssetAsync(f.Project.Id, asset.Id, library.Revision, _ct);
        var binding = Picture(asset.Name, asset.Id); binding.MediaId = image.Id;
        Assert.Equal("Dark hair.\nGray hoodie.", ShotReferences.Resolve(binding, library, new()).Effective);
        Assert.Empty(library.Assets);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Assets.SaveAsync(stale, stale.Revision, _ct));
        library = await f.Assets.RestoreImagesAsync(f.Project.Id, [library.Trash.Single().Id], library.Revision, _ct);
        var loaded = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(asset.Id, loaded.Assets[0].Id); Assert.Equal(image.Id, loaded.Assets[0].Images[0].Id);
        Assert.Equal(asset.PreservationGuidance, loaded.Assets[0].PreservationGuidance);
        Assert.Equal(image.PreservationGuidance, loaded.Assets[0].Images[0].PreservationGuidance);
    }

    [Fact]
    public async Task GenerationCapturesGuidanceAndRejectsUnreviewedDefaultsWithoutSubmitting()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", PreservationGuidance = "Dark hair." };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        await using var bytes = new MemoryStream(AssetStoreTests.Png(32, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, bytes, new("image.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        f.Shot.Images = [Picture(asset.Name, asset.Id)]; f.Shot.Images[0].MediaId = library.Assets[0].Images[0].Id;
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct);
        doc = await f.Shots.SaveAsync(f.Project.Id, [f.Shot], doc.Revision, ct: _ct);
        var reviewed = ShotReferences.Resolve(f.Shot, library, doc);
        library = await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { PreservationGuidance = "New defaults." }] }, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Capture(guidance: reviewed)); Assert.Empty(f.Graphs);
        var request = await f.Capture(2, ShotReferences.Resolve(f.Shot, library, doc)); var context = await f.Claim(request);
        library = await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { PreservationGuidance = "Later edits." }] }, library.Revision, _ct);
        await f.Worker.ExecuteAsync(context, request.Snapshot, _ct);
        await f.Jobs.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        await f.Worker.ValidateExtensionAsync((await f.Jobs.ReadAsync(_ct)).Jobs.Single(), request.Snapshot, _ct);
        await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), request.OriginTabId, _ct);
        var continuation = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(continuation, false), request.Snapshot, _ct);
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Equal(3, doc.Takes.Count);
        Assert.All(doc.Takes, t => { Assert.Equal("New defaults.", t.Snapshot.ReferenceGuidance.Single().Effective); Assert.Contains("New defaults.", t.Snapshot.Prompt); Assert.DoesNotContain("Later edits.", t.Snapshot.Prompt); Assert.Equal(H3Policy.Profile, t.Snapshot.Profile); });
    }
}
