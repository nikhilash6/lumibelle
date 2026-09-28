using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class EnvironmentReelTests
{
    private static ReferenceAsset Owner() => new() { Id = Guid.NewGuid(), Category = AssetCategory.Environment,
        Name = "Study", Description = "A desk beneath the window.", PreservationGuidance = "Keep the brick wall opposite the desk." };
    [Fact]
    public void DefaultsAreEmptySilentWideAndDoNotChangeCharacterRecipes()
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        Assert.Equal(ReelFraming.EnvironmentTurn, draft.Framing); Assert.Equal("16:9", draft.Aspect);
        Assert.Equal(15, draft.Duration); Assert.Equal(362 / 24d, H3Policy.Seconds(draft.Duration));
        Assert.Equal(ReelCameraDirection.Right, draft.CameraDirection);
        Assert.Empty(draft.Images); Assert.Empty(draft.Prompt); Assert.Empty(draft.UseGuidance);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode); Assert.Null(draft.Voice); Assert.Empty(draft.Line); Assert.Null(draft.LookId);
        var character = ReferenceReels.NewDraft(owner with { Category = AssetCategory.Character });
        var old = new ReferenceReelDraft { Id = character.Id, AssetId = owner.Id, Name = owner.Name + " reference", Speaker = owner.Name };
        Assert.True(character.SaveLosslessFrames);
        Assert.Equal(ReferenceReels.Fingerprint(old), ReferenceReels.Fingerprint(character with { SaveLosslessFrames = null }));
        Assert.Equal(0, (int)ReelFraming.ContinuousTurn); Assert.Equal(3, (int)ReelFraming.Custom); Assert.Equal(4, (int)ReelFraming.SideRearFace);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.NewDraft(owner with { Category = AssetCategory.Reference }));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SilentReelInstructionsRequireExplicitSoundSectionsAndRetainInvalidResponses(bool environment)
    {
        var owner = Owner() with { Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        var draft = ReferenceReels.NewDraft(owner); draft.VoiceMode = ReelVoiceMode.Silent;
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid() }];
        var payload = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), []);
        var instructions = ReferenceReels.Messages(payload, [new byte[] { 1 }])[0].Text!;
        Assert.Contains("No non-diegetic music.", instructions);
        Assert.Contains("Do not leave either sound section empty", instructions);
        var pair = ReferenceReels.Preset(draft);
        var broken = pair with { Prompt = pair.Prompt.Replace("No non-diegetic music.", "") };
        var request = new AiTextJobRequest(2, AiJobKind.ReelComposition, new(AiBackend.OpenRouter, "mock", "Mock"),
            true, new(), draft.PresetVersion, .5f, 1, JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), []);
        var raw = JsonSerializer.Serialize(broken, AtomicJsonFile.Options);
        var result = AiTextResults.Parse(request, raw, "stop");
        Assert.Equal("Complete the non_diegetic_music section.", result.Error);
        Assert.Equal(broken, result.Read<ReelPromptPair>()); Assert.Equal(raw, result.Raw);
        Assert.Null(AiTextResults.Parse(request, JsonSerializer.Serialize(pair, AtomicJsonFile.Options), "stop").Error);
    }
    [Theory]
    [InlineData(ReelFraming.EnvironmentTurn, 1)] [InlineData(ReelFraming.EnvironmentTurn, 2)]
    [InlineData(ReelFraming.EnvironmentPan, 1)] [InlineData(ReelFraming.EnvironmentPath, 2)]
    public void EnvironmentPairsCaptureViewsCropsAndContextWithNoAudio(ReelFraming framing, int count)
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        draft.Framing = framing; draft.Instructions = "Travel from the doorway toward the desk — café.\nKeep the window visible.";
        draft.Images = Enumerable.Range(1, count).Select(n => new ShotImageBinding { AssetId = owner.Id, MediaId = Guid.NewGuid(),
            Notes = $"View {n}", PreservationOverride = $"Preserve brickwork {n}", InferUsage = true, Crop = new() { X = 0, Y = 0, Width = .5, Height = 1 } }).ToList();
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner] };
        var pair = ReferenceReels.Preset(draft, library); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        Assert.Contains("15.083 seconds", pair.Prompt); Assert.Contains(ReferenceReels.Views(draft), pair.Prompt);
        Assert.Contains("brick wall", pair.Prompt); Assert.Contains("brick wall", pair.UseGuidance);
        Assert.Contains("Complete unseen areas plausibly", pair.Prompt); Assert.Contains("not verified output", pair.UseGuidance);
        Assert.DoesNotContain("<d>", pair.Prompt); Assert.DoesNotContain("<Audio", pair.Prompt); Assert.DoesNotContain("(S1)", pair.Prompt);
        var inputs = ReferenceReels.Inputs(draft); Assert.Empty(inputs.Dialogue); Assert.Empty(inputs.Voices);
        Assert.Null(inputs.SceneId); Assert.Null(inputs.ApprovedScriptId); Assert.Equal(draft.Images[0].Crop, inputs.Images[0].Crop);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), [])
            { ImageGuidance = ShotReferences.Resolve(inputs, library, new()) };
        var messages = ReferenceReels.Messages(request, [new byte[] { 1, 2 }]);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(owner.Description, context.RootElement.GetProperty("environment").GetProperty("visualNotes").GetString());
        Assert.Contains("unseen areas", messages[0].Text); Assert.DoesNotContain("character reference", messages[0].Text);
        Assert.Equal(count, context.RootElement.GetProperty("draft").GetProperty("images").GetArrayLength());
        Assert.Contains(draft.Instructions, context.RootElement.GetProperty("draft").GetProperty("instructions").GetString());
        var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(request.ProjectId, 0, inputs, pair.Prompt, H3Policy.Fingerprint(inputs), "http://localhost:8188", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion) {
            Reel = new(draft.Copy(), request.Character) { Owner = owner }, OutputPolicy = new(inputs.SaveLosslessFrames),
            Preset = H3Presets.Capture(inputs, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(inputs, settings) };
        var captured = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, Enumerable.Range(1, count).Select(n => new AiVideoInput($"image-{n}.png", false, 4, new('A', 64))).ToArray());
        AiVideoJobPolicy.Validate(captured);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(captured with { Snapshot = snapshot with { Profile = ReferenceReels.Profile } }));
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(snapshot, 42, "client", captured.Inputs.Select(i => new PreparedVideoInput(i.FileName, false)).ToArray()));
        Assert.Contains("environment reference reel", graph); Assert.DoesNotContain("LoadAudio", graph);
        var bad = pair with { Prompt = pair.Prompt.Replace("15.083 seconds", "15 seconds") };
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidatePair(bad, draft));
    }
    [Fact]
    public void PathRequiresCompositionInstructionsButManualGenerationIsIndependent()
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), InferUsage = true }];
        var authored = ReferenceReels.Preset(draft);
        draft.Framing = ReelFraming.EnvironmentPath;
        Assert.Contains("custom camera path", Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft)).Message);
        draft.Prompt = authored.Prompt; draft.UseGuidance = authored.UseGuidance;
        ReferenceReels.Validate(draft, true);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { VoiceMode = ReelVoiceMode.NewVoice }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { LookId = Guid.NewGuid() }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { Framing = ReelFraming.ThreeAngles }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(draft, Owner() with { Id = draft.AssetId, Category = AssetCategory.Character }));
    }

    public static IEnumerable<object[]> PresetsAndDirections() => ReferenceReels.EnvironmentPresets
        .SelectMany(p => Enum.GetValues<ReelCameraDirection>().Select(d => new object[] { p.Framing, d }));

    [Theory]
    [InlineData(ReelCameraDirection.Right, "left", "right")]
    [InlineData(ReelCameraDirection.Left, "right", "left")]
    public void RevealsDescribeScreenTravelAndDifferentDestinations(ReelCameraDirection direction, string exit, string entry)
    {
        var draft = ReferenceReels.NewDraft(Owner());
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentPan); draft.CameraDirection = direction;
        var pan = ReferenceReels.Views(draft);
        Assert.Contains($"pans {entry} with large amplitude at fast speed", pan);
        Assert.Contains($"leave through the {exit} edge", pan);
        Assert.Contains($"out-of-frame {entry}-hand continuation enters", pan);
        Assert.Contains("perpendicular to the starting direction", pan);
        Assert.Contains("by 5.583 seconds", pan); Assert.Contains("until 6.583 seconds", pan);

        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentTurn);
        var turn = ReferenceReels.Views(draft);
        Assert.Contains("space behind the opening camera", turn);
        Assert.Contains("remaining side of the room", turn);
        Assert.Contains("Only after passing all three other sides", turn);

        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentArc);
        var arc = ReferenceReels.Views(draft);
        Assert.Contains($"arc shot to the {entry} with large amplitude at fast speed", arc);
        Assert.Contains("approximately 90° around the stationary feature", arc);
        Assert.Contains("distinct side-on viewpoint", arc); Assert.Contains("strong parallax", arc);
        Assert.DoesNotContain("modest", arc); Assert.DoesNotContain("45°", arc);
    }

    [Theory]
    [InlineData(ReelFraming.EnvironmentPullBack, "become substantially smaller", "distinctly wider view")]
    [InlineData(ReelFraming.EnvironmentApproach, "grows substantially", "new surface detail")]
    public void CameraTravelHasObservableScaleChangeAndAdaptsPaceToLongerDuration(ReelFraming framing, string scale, string destination)
    {
        var draft = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(draft, framing);
        var standard = ReferenceReels.Views(draft);
        Assert.Contains("with large amplitude at fast speed", standard);
        Assert.Contains(scale, standard); Assert.Contains(destination, standard);
        draft.Duration = 12;
        var longer = ReferenceReels.Views(draft);
        Assert.Contains("at a steady speed spread across the allotted movement time", longer);
        Assert.DoesNotContain("at fast speed", longer);
        Assert.Contains(scale, longer); Assert.Contains(destination, longer);
    }

    [Fact]
    public void CustomPathKeepsAuthoredExtentAndSpeedAndHeldViewsCoverDifferentSides()
    {
        var draft = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentPath);
        draft.Instructions = "Move gently from the doorway to the rug at slow speed.";
        var custom = ReferenceReels.Views(draft);
        Assert.Contains("extent and speed they specify", custom);
        Assert.Contains("arriving at the specified destination", custom);
        Assert.DoesNotContain("large amplitude", custom); Assert.DoesNotContain("fast speed", custom);
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentHeldViews);
        var held = ReferenceReels.Views(draft);
        Assert.Contains("space behind the opening camera", held);
        Assert.Contains("four different directions", held);
        Assert.Contains("screen positions change", held);
    }

    [Theory, MemberData(nameof(PresetsAndDirections))]
    public void EveryPresetProducesOneCapturedReelWithTheSameCameraPlan(ReelFraming framing, ReelCameraDirection direction)
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        ReferenceReels.SelectCameraPreset(draft, framing); draft.CameraDirection = direction;
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid(), InferUsage = true }];
        draft.Instructions = "Use the desk as the feature. Keep the brick wall.";
        var preset = ReferenceReels.CameraPreset(draft)!;
        Assert.Equal(preset.Seconds, draft.Duration);
        var views = ReferenceReels.Views(draft);
        var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        Assert.Contains(views, pair.Prompt);
        Assert.DoesNotContain("[Pan", views);
        if (preset.UsesDirection) Assert.Contains(direction.ToString().ToLowerInvariant(), views);
        var shots = System.Text.RegularExpressions.Regex.Matches(views, @"\[Shot (\d+)\]").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(framing == ReelFraming.EnvironmentHeldViews ? ["1", "2", "3", "4"] : new[] { "1" }, shots);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), []);
        var messages = ReferenceReels.Messages(request, []);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(views, context.RootElement.GetProperty("presetViews").GetString());
        Assert.Contains("within this one reel", messages[0].Text);
        Assert.Contains("resolve conflicts", messages[0].Text);
        Assert.Contains("Preserve physical positions and relationships within the room while screen positions change", pair.Prompt);
        Assert.Contains("Preserve physical positions and relationships within the room while screen positions change", messages[0].Text);
        Assert.Contains("movement type, amplitude and speed separately", messages[0].Text);
        Assert.Contains("Do not soften broad coverage", messages[0].Text);
        Assert.Contains("physical relationships for retention and use guidance", messages[0].Text);
        Assert.DoesNotContain("with large amplitude", pair.UseGuidance);
        Assert.Empty(ReferenceReels.Inputs(draft).Voices);
        draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        var inputs = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(request.ProjectId, 0, inputs, pair.Prompt, H3Policy.Fingerprint(inputs), "http://localhost:8188", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion) {
            Reel = new(draft.Copy(), request.Character) { Owner = owner }, OutputPolicy = new(true),
            Preset = H3Presets.Capture(inputs, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(inputs, settings) };
        var captured = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image-1.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(captured);
        var graph = ComfyH3Video.BuildWorkflow(snapshot, 42, "client", [new("image-1.png", false)]);
        var graphJson = JsonSerializer.Serialize(graph);
        Assert.DoesNotContain("LoadAudio", graphJson); Assert.DoesNotContain("LoadVideo", graphJson);
        using var graphDoc = JsonDocument.Parse(graphJson);
        Assert.Equal(H3Policy.Frames(draft.Duration), graphDoc.RootElement.GetProperty("prompt").GetProperty("5").GetProperty("inputs").GetProperty("length").GetInt32());
    }

    [Theory]
    [InlineData(2)] [InlineData(6)] [InlineData(10)] [InlineData(15)]
    public void TimingsUseActualDurationAndHoldsFitEvenShortReels(double duration)
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Duration = duration;
        var total = H3Policy.Frames(duration) / 24d;
        foreach (var framing in new[] { ReelFraming.EnvironmentTurn, ReelFraming.EnvironmentHalfTurn }) {
            draft.Framing = framing;
            var intervals = System.Text.RegularExpressions.Regex.Matches(ReferenceReels.Views(draft), @"[Ff]rom (\d+(?:\.\d+)?) to (\d+(?:\.\d+)?) seconds")
                .Select(m => (Start: double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), End: double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.Equal(framing == ReelFraming.EnvironmentTurn ? 8 : 4, intervals.Length);
            Assert.Equal(0, intervals[0].Start);
            Assert.InRange(Math.Abs(intervals[^1].End - total), 0, .0005);
            for (var i = 0; i < intervals.Length; i++) {
                Assert.True(intervals[i].End > intervals[i].Start);
                if (i > 0) Assert.Equal(intervals[i - 1].End, intervals[i].Start);
            }
            Assert.InRange(intervals[^1].End - intervals[^1].Start, .001, 1.001);
        }
        draft.Framing = ReelFraming.EnvironmentHeldViews;
        var cuts = System.Text.RegularExpressions.Regex.Matches(ReferenceReels.Views(draft), @"At (\d\d:\d\d\.\d\d\d)")
            .Select(m => TimeSpan.ParseExact(m.Groups[1].Value, @"mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture).TotalSeconds).ToArray();
        Assert.Equal(3, cuts.Length);
        Assert.True(cuts[0] > 0 && cuts[0] < cuts[1] && cuts[1] < cuts[2] && cuts[2] < total);
        Assert.InRange(Math.Abs(cuts[0] - H3Policy.Frames(duration) / 4 / 24d), 0, .001);
    }

    [Fact]
    public void SelectionReuseAndReloadPreserveAuthoredInputs()
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Prompt = "My prompt"; draft.UseGuidance = "My guidance";
        draft.Instructions = "Around the desk"; draft.CameraDirection = ReelCameraDirection.Left;
        draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid() }];
        draft.CheckedInputs = ReferenceReels.InputsFingerprint(draft);
        var before = draft.Copy();
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentArc);
        Assert.Equal(6, draft.Duration); Assert.Null(draft.CheckedInputs);
        Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.UseGuidance, draft.UseGuidance);
        Assert.Equal(before.Instructions, draft.Instructions); Assert.Equal(before.Images, draft.Images);
        Assert.Equal(ReelCameraDirection.Left, draft.CameraDirection);
        var source = ReferenceReels.NewDraft(Owner()); source.Instructions = "Saved directions";
        ReferenceReels.ReuseDirections(draft, source);
        Assert.Equal(source.Framing, draft.Framing); Assert.Equal(source.CameraDirection, draft.CameraDirection);
        Assert.Equal(6, draft.Duration); Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.Images, draft.Images);
        Assert.Equal(source.Instructions, draft.Instructions);
        var reload = JsonSerializer.Deserialize<ReferenceReelDraft>(JsonSerializer.Serialize(draft, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(reload));
        Assert.Equal(ReferenceReels.Views(draft), ReferenceReels.Views(reload));
        Assert.NotNull(ReferenceReels.DurationAdvice(draft)); Assert.False(ReferenceReels.ShowKeyframeHint(draft));
        draft.Duration = 10; Assert.True(ReferenceReels.ShowKeyframeHint(draft));
        Assert.Equal(15, source.Duration);
    }

    [Theory]
    [InlineData(ReelFraming.EnvironmentApproach)] [InlineData(ReelFraming.EnvironmentArc)] [InlineData(ReelFraming.EnvironmentPath)]
    public void FeatureAndCustomPresetsRequireDirectionsForComposition(ReelFraming framing)
    {
        var draft = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(draft, framing);
        Assert.NotNull(ReferenceReels.CompositionInstructionsIssue(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft));
        draft.Instructions = "toward the window";
        ReferenceReels.ValidateComposition(draft);
        draft.CameraDirection = (ReelCameraDirection)99;
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(draft));
    }
}
