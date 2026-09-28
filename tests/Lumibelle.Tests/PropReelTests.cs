using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class PropReelTests
{
    private static ReferenceAsset Owner() => new() { Id = Guid.NewGuid(), Category = AssetCategory.Prop,
        Name = "Armchair", Description = "A green velvet armchair with brass feet.", PreservationGuidance = "Keep the buttoned back and curved arms." };

    [Fact]
    public void DefaultsAreSilentOrbitsWithOnlyPropPresets()
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        Assert.Equal(ReferenceReels.PropProfile, draft.PresetVersion); Assert.True(ReferenceReels.IsProp(draft)); Assert.True(ReferenceReels.IsCameraReel(draft));
        Assert.False(ReferenceReels.IsEnvironment(draft));
        Assert.Equal(ReelFraming.PropOrbit, draft.Framing); Assert.Equal(15, draft.Duration); Assert.Equal("1:1", draft.Aspect);
        Assert.Equal(ReelCameraDirection.Right, draft.CameraDirection);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode); Assert.Null(draft.Voice); Assert.Null(draft.LookId);
        Assert.Empty(draft.Speaker); Assert.Empty(draft.Line); Assert.Empty(draft.Images); Assert.Empty(draft.Prompt);
        Assert.Equal(ReferenceReels.PropPresets.Select(p => p.Framing), ReferenceReels.Framings(draft));
        Assert.Equal("360° orbit · Right", ReferenceReels.Label(draft));
        Assert.Equal("Prop reference reel", ReferenceReels.Inputs(draft).Description);
        Assert.True(ReferenceReels.Supports(AssetCategory.Prop)); Assert.False(ReferenceReels.Supports(AssetCategory.Reference));
    }

    [Fact]
    public void RecipesStaySilentAndBoundToTheirKindOfAsset()
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        ReferenceReels.ValidateOwner(draft, owner);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(draft, owner with { Category = AssetCategory.Environment }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(draft, owner with { Category = AssetCategory.Character }));
        var environment = ReferenceReels.NewDraft(owner with { Category = AssetCategory.Environment });
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(environment, owner));
        var character = ReferenceReels.NewDraft(owner with { Category = AssetCategory.Character });
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(character, owner));

        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { VoiceMode = ReelVoiceMode.NewVoice }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { LookId = Guid.NewGuid() }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { Speaker = "Armchair" }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { Framing = ReelFraming.EnvironmentTurn }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { Framing = ReelFraming.ContinuousTurn }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentArc));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ReuseDirections(draft, environment));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ReuseDirections(environment, draft));
        var other = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(other, ReelFraming.PropRise);
        ReferenceReels.ReuseDirections(draft, other);
        Assert.Equal(ReelFraming.PropRise, draft.Framing);
    }

    [Fact]
    public void CustomMoveRequiresCompositionInstructionsButManualGenerationIsIndependent()
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), InferUsage = true }];
        var authored = ReferenceReels.Preset(draft);
        draft.Framing = ReelFraming.PropCustom;
        Assert.Contains("camera move in Instructions", Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft)).Message);
        draft.Prompt = authored.Prompt; draft.UseGuidance = authored.UseGuidance;
        ReferenceReels.Validate(draft, true);
        draft.Instructions = "Start low at the front, circle to the left side and end looking down at the seat.";
        ReferenceReels.ValidateComposition(draft);
        var custom = ReferenceReels.Views(draft);
        Assert.Contains("extent and speed they specify", custom); Assert.DoesNotContain("large amplitude", custom);
    }

    [Theory]
    [InlineData(ReelCameraDirection.Right)] [InlineData(ReelCameraDirection.Left)]
    public void OrbitsCircleAStationaryPropWhileTheTurntableTurnsIt(ReelCameraDirection direction)
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.CameraDirection = direction; var name = direction.ToString().ToLowerInvariant();
        var orbit = ReferenceReels.Views(draft);
        Assert.Contains($"orbits {name} around the stationary prop with large amplitude at fast speed through approximately 360°", orbit);
        Assert.Contains("The prop itself never rotates", orbit); Assert.Contains("prop's rear", orbit);
        Assert.Contains("Only after passing both sides and the rear", orbit); Assert.Contains("Finish at the front view", orbit);
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.PropHalfOrbit);
        var half = ReferenceReels.Views(draft);
        Assert.Contains("through approximately 180°", half); Assert.Contains("Finish facing the prop's rear.", half);
        Assert.DoesNotContain("remaining side", half);
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.PropTurntable);
        var turntable = ReferenceReels.Views(draft);
        Assert.Contains("locked off", turntable); Assert.Contains($"turns {name} on a concealed turntable", turntable);
        Assert.Contains("360° rotation", turntable); Assert.DoesNotContain("orbit", turntable);
        draft.Duration = 14;
        Assert.Contains("at a steady speed spread across the allotted movement time", ReferenceReels.Views(draft));
    }

    [Fact]
    public void HeldAnglesCutBetweenFourSidesOfTheProp()
    {
        var draft = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(draft, ReelFraming.PropHeldViews);
        Assert.False(ReferenceReels.CameraPreset(draft)!.UsesDirection);
        var held = ReferenceReels.Views(draft);
        foreach (var view in new[] { "the front view", "a three-quarter view", "a side view in profile", "the rear view" }) Assert.Contains(view, held);
        Assert.Contains("At 00:03.041, the camera cuts to", held);
        Assert.Contains("No camera movement within this view", held);
        Assert.Contains("Four held angles", ReferenceReels.Preset(draft with { Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), InferUsage = true }] }).Prompt);
    }

    public static IEnumerable<object[]> PresetsAndDirections() => ReferenceReels.PropPresets
        .SelectMany(p => Enum.GetValues<ReelCameraDirection>().Select(d => new object[] { p.Framing, d }));

    [Theory, MemberData(nameof(PresetsAndDirections))]
    public void EveryPresetProducesOneCapturedReelWithTheSameCameraPlan(ReelFraming framing, ReelCameraDirection direction)
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        ReferenceReels.SelectCameraPreset(draft, framing); draft.CameraDirection = direction;
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid(), Notes = "Front view", InferUsage = true, Crop = new() { X = 0, Y = 0, Width = .5, Height = 1 } }];
        draft.Instructions = "Start at the front and finish on the buttoned back.";
        var preset = ReferenceReels.CameraPreset(draft)!;
        Assert.Equal(preset.Seconds, draft.Duration);
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner] };
        var views = ReferenceReels.Views(draft);
        var pair = ReferenceReels.Preset(draft, library);
        ReferenceReels.ValidatePair(pair, draft);
        Assert.Contains(views, pair.Prompt);
        if (preset.UsesDirection) Assert.Contains(direction.ToString().ToLowerInvariant(), views);
        var shots = Regex.Matches(views, @"\[Shot (\d+)\]").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(framing == ReelFraming.PropHeldViews ? ["1", "2", "3", "4"] : new[] { "1" }, shots);
        Assert.Contains("the same prop throughout", pair.Prompt); Assert.Contains("buttoned back", pair.Prompt);
        Assert.Contains("Complete unseen sides plausibly", pair.Prompt); Assert.Contains("No people, hands", pair.Prompt);
        Assert.Contains("buttoned back", pair.UseGuidance); Assert.Contains("not verified output", pair.UseGuidance);
        Assert.DoesNotContain("large amplitude", pair.UseGuidance); Assert.DoesNotContain("room", pair.Prompt);
        Assert.DoesNotContain("<Audio", pair.Prompt);

        var inputs = ReferenceReels.Inputs(draft); Assert.Empty(inputs.Dialogue); Assert.Empty(inputs.Voices);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), [])
            { ImageGuidance = ShotReferences.Resolve(inputs, library, new()) };
        var messages = ReferenceReels.Messages(request, [new byte[] { 1, 2 }]);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(views, context.RootElement.GetProperty("presetViews").GetString());
        Assert.Equal(owner.Description, context.RootElement.GetProperty("prop").GetProperty("visualNotes").GetString());
        Assert.Contains("Compose a prop reference reel", messages[0].Text); Assert.Contains("within this one reel", messages[0].Text);
        Assert.Contains("the prop never rotates", messages[0].Text); Assert.DoesNotContain("character reference", messages[0].Text);
        Assert.Contains("No non-diegetic music.", messages[0].Text);

        draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        inputs = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(request.ProjectId, 0, inputs, pair.Prompt, H3Policy.Fingerprint(inputs), "http://localhost:8188", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion) {
            Reel = new(draft.Copy(), request.Character) { Owner = owner }, OutputPolicy = new(true),
            Preset = H3Presets.Capture(inputs, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(inputs, settings) };
        var captured = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image-1.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(captured);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(captured with { Snapshot = snapshot with { Profile = ReferenceReels.EnvironmentProfile } }));
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(captured with { Snapshot = snapshot with { Reel = snapshot.Reel! with { Owner = owner with { Category = AssetCategory.Environment } } } }));
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(snapshot, 42, "client", [new("image-1.png", false)]));
        Assert.Contains("prop reference reel", graph); Assert.DoesNotContain("LoadAudio", graph);
    }
}
