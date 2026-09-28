using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class CharacterReelStudyTests
{
    public static IEnumerable<object[]> Modes() => ReferenceReels.CharacterCapturePresets
        .Where(p => p.Framing != ReelFraming.CharacterCapture).Select(p => new object[] { p.Framing });
    public static IEnumerable<object[]> Timings() => ReferenceReels.CharacterCapturePresets
        .Where(p => p.Framing != ReelFraming.CharacterCapture)
        .SelectMany(p => new[] { p.MinimumSeconds, p.Seconds, 15 }.Distinct().Select(s => new object[] { p.Framing, s }));
    private static ReferenceReelDraft Recipe(ReelFraming mode)
    {
        var draft = ReferenceReelTests.Recipe(); ReferenceReels.SelectCharacterPreset(draft, mode); return draft;
    }

    [Theory]
    [InlineData(ReelFraming.CharacterNeutralTurntable, 14, 8, 5)]
    [InlineData(ReelFraming.CharacterSilhouetteReveal, 15, 10, 8)]
    [InlineData(ReelFraming.CharacterPoseExpansion, 16, 10, 10)]
    [InlineData(ReelFraming.CharacterDrapeCheck, 17, 10, 8)]
    [InlineData(ReelFraming.CharacterSeatedToStanding, 18, 8, 6)]
    [InlineData(ReelFraming.CharacterFacePriority, 19, 8, 6)]
    public void StudyDefaultsAreOptInAndDoNotRewriteAuthoredData(ReelFraming mode, int value, double seconds, double minimum)
    {
        Assert.Equal(value, (int)mode); Assert.Equal(13, (int)ReelFraming.CharacterCapture);
        var draft = ReferenceReelTests.Recipe(voice: ReelVoiceMode.ExistingRecording);
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance"; draft.Instructions = "Use Picture 1 for the face.";
        draft.VoiceDescription = "Soft, natural delivery."; draft.Aspect = "9:16";
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        draft.CaptureArticulation = ReelArticulation.Knee; draft.CaptureCloseUp = ReelCloseUpTransition.PushIn;
        draft.VoiceMode = ReelVoiceMode.ExistingRecording; draft.CheckedInputs = ReferenceReels.InputsFingerprint(draft);
        var before = draft.Copy();
        ReferenceReels.SelectCharacterPreset(draft, mode);
        Assert.Equal(seconds, draft.Duration); Assert.Equal(minimum, ReferenceReels.CapturePreset(draft)!.MinimumSeconds);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode); Assert.Null(draft.CheckedInputs);
        Assert.Null(draft.CaptureArticulation); Assert.Null(draft.CaptureCloseUp);
        Assert.False(ReferenceReels.HasCharacterCaptureOptions(draft)); Assert.True(ReferenceReels.IsCharacterCapture(draft));
        Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.UseGuidance, draft.UseGuidance);
        Assert.Equal(before.Instructions, draft.Instructions); Assert.Equal(before.Aspect, draft.Aspect);
        Assert.Equal(before.Voice, draft.Voice); Assert.Equal(before.Line, draft.Line); Assert.Equal(before.VoiceDescription, draft.VoiceDescription);
        Assert.Equal(before.Images.Select(i => i.Id), draft.Images.Select(i => i.Id));
        draft.Duration = 15; var unchanged = ReferenceReels.Fingerprint(draft);
        ReferenceReels.SelectCharacterPreset(draft, mode);
        Assert.Equal(unchanged, ReferenceReels.Fingerprint(draft));
    }

    [Fact]
    public void CatalogueKeepsLegacyFramingsAndEnvironmentScope()
    {
        var character = ReferenceReelTests.Recipe();
        var choices = ReferenceReels.Framings(character);
        Assert.Equal(choices.Count, choices.Distinct().Count());
        Assert.Equal(new[] { ReelFraming.ContinuousTurn, ReelFraming.BodyToFace, ReelFraming.ThreeAngles, ReelFraming.SideRearFace }, choices.Take(4));
        Assert.Equal(ReelFraming.Custom, choices[^1]);
        Assert.Equal(5, character.Duration); Assert.False(ReferenceReels.IsCharacterCapture(character));
        Assert.Contains("5.167 seconds", ReferenceReels.Preset(character).Prompt);
        var environment = ReferenceReels.NewDraft(new() { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Study" });
        var before = ReferenceReels.Fingerprint(environment);
        foreach (var preset in ReferenceReels.CharacterCapturePresets)
        {
            Assert.DoesNotContain(preset.Framing, ReferenceReels.Framings(environment));
            Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.SelectCharacterPreset(environment, preset.Framing));
            Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(environment with { Framing = preset.Framing }));
            Assert.Throws<ArgumentException>(() => ReferenceReels.Views(preset.Framing));
        }
        Assert.Equal(before, ReferenceReels.Fingerprint(environment));
    }

    [Theory]
    [MemberData(nameof(Timings))]
    public void StudyViewsCoverTheExactFrameGridWithConsecutiveCutsAndValidPromptPairs(ReelFraming mode, double seconds)
    {
        var draft = Recipe(mode); draft.Duration = seconds;
        var before = ReferenceReels.Fingerprint(draft);
        var frames = H3Policy.Frames(seconds); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(0, plan[0].StartFrame); Assert.Equal(frames, plan[^1].EndFrameExclusive);
        for (var i = 0; i < plan.Count; i++)
        {
            Assert.True(plan[i].EndFrameExclusive > plan[i].StartFrame);
            if (i > 0) Assert.Equal(plan[i - 1].EndFrameExclusive, plan[i].StartFrame);
        }
        var views = ReferenceReels.Views(draft);
        Assert.Equal(Enumerable.Range(1, plan.Count), Regex.Matches(views, @"\[Shot (\d+)\]").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        var cuts = Regex.Matches(views, @"At (\d\d:\d\d\.\d{3}), make a clean cut");
        Assert.Equal(plan.Count - 1, cuts.Count);
        for (var i = 0; i < cuts.Count; i++)
        {
            var stamp = TimeSpan.ParseExact(cuts[i].Groups[1].Value, @"mm\:ss\.fff", CultureInfo.InvariantCulture);
            Assert.InRange(Math.Abs(stamp.TotalSeconds * 24 - plan[i + 1].StartFrame), 0, .025);
        }
        var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        Assert.Contains(views, pair.Prompt);
        Assert.Contains(H3Policy.Seconds(seconds).ToString("0.###", CultureInfo.InvariantCulture) + " seconds", pair.Prompt);
        Assert.Contains("not verified output", pair.UseGuidance); Assert.Contains("not new evidence", pair.UseGuidance);
        Assert.DoesNotContain("<d>", pair.Prompt); Assert.DoesNotContain("<Audio", pair.Prompt);
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TooShortStudyCanBeSavedButCannotSilentlyComposeAnOverpackedPlan(ReelFraming mode)
    {
        var draft = Recipe(mode); var preset = ReferenceReels.CapturePreset(draft)!;
        draft.Duration = preset.MinimumSeconds - .1;
        ReferenceReels.Validate(draft); // A draft remains editable.
        Assert.Contains(preset.Label, ReferenceReels.CompositionInstructionsIssue(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Preset(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Views(draft));
        // The composition minimum does not invalidate an independently authored, valid prompt.
        var authored = ReferenceReels.Preset(draft with { Framing = ReelFraming.ThreeAngles });
        draft.Prompt = authored.Prompt; draft.UseGuidance = authored.UseGuidance;
        ReferenceReels.Validate(draft, true);
    }

    [Fact]
    public void NeutralEightSecondPlanMatchesFrontOrbitFrontFaceWithoutArticulation()
    {
        var draft = Recipe(ReelFraming.CharacterNeutralTurntable); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(2, plan.Count); Assert.Equal(144, plan[0].EndFrameExclusive); Assert.Equal(192, plan[1].EndFrameExclusive);
        var views = ReferenceReels.Views(draft);
        Assert.Contains("from 0 to 1 seconds", views); Assert.Contains("From 1 to 5 seconds", views);
        Assert.Contains("right profile at 2 seconds", views); Assert.Contains("full back at 3 seconds", views);
        Assert.Contains("left profile at 4 seconds", views); Assert.Contains("hold that frontal view until 6 seconds", views);
        Assert.Contains("At 00:06.000", views); Assert.Contains("No articulation", views);
        Assert.DoesNotContain("lifts both arms", views); Assert.DoesNotContain("bend the right knee", views);
        draft.Duration = 7.9;
        Assert.Equal(5, ReferenceReels.CharacterStudyPlan(draft).Count);
        Assert.DoesNotContain("360-degree", ReferenceReels.Views(draft));
        Assert.Contains("Five held", ReferenceReels.CharacterCaptureSummary(draft));
    }

    [Fact]
    public void SilhouetteHoldsOpenArmsAcrossBodyViewsWithoutAddingAnotherMovement()
    {
        var draft = Recipe(ReelFraming.CharacterSilhouetteReveal); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(5, plan.Count); Assert.True(plan[0].EndFrameExclusive >= 36);
        Assert.All(plan.Take(4), v => { Assert.Contains("30–45 degrees", v.Description); Assert.Contains("camera stationary", v.Description); });
        Assert.Contains("back view", plan[2].Description); Assert.Contains("Left-profile", plan[3].Description);
        Assert.DoesNotContain("foot lift", ReferenceReels.Views(draft)); Assert.DoesNotContain("orbit", ReferenceReels.Views(draft));
    }

    [Fact]
    public void PoseExpansionCompletesBothSmallActionsInOrderBeforeAnyCut()
    {
        var draft = Recipe(ReelFraming.CharacterPoseExpansion); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(5, plan.Count); Assert.True(plan[0].EndFrameExclusive >= 108);
        var front = plan[0].Description;
        Assert.Contains("From 0.5 to 2.5 seconds", front); Assert.Contains("Only after the arms return, from 2.5 to 4.5 seconds", front);
        Assert.Contains("sequential, never simultaneous", front); Assert.Contains("camera stationary", front);
        Assert.All(plan.Skip(1).Take(3), v => Assert.Contains("No further articulation", v.Description));
        Assert.Contains("Right-profile", plan[1].Description); Assert.Contains("back view", plan[2].Description); Assert.Contains("Left-profile", plan[3].Description);
    }

    [Fact]
    public void DrapeCheckCompletesTheSubtleBendInAStationaryProfileView()
    {
        var draft = Recipe(ReelFraming.CharacterDrapeCheck); draft.Duration = 8;
        var plan = ReferenceReels.CharacterStudyPlan(draft); Assert.Equal(4, plan.Count);
        var profile = plan[1]; Assert.True(profile.EndFrameExclusive - profile.StartFrame >= 72);
        Assert.Contains("Right-profile", profile.Description); Assert.Contains("about 15 degrees", profile.Description);
        Assert.Contains("return fully upright", profile.Description); Assert.Contains("camera stationary", profile.Description);
        Assert.Contains("upright again", plan[2].Description);
        var pair = ReferenceReels.Preset(draft);
        Assert.Contains("does not include a left-profile view", pair.UseGuidance);
        Assert.Contains("let hair and clothing respond naturally", pair.Prompt);
        Assert.DoesNotContain("let the hair remain settled", pair.Prompt);
    }

    [Fact]
    public void SittingToStandingStartsSeatedAndDoesNotPromiseRearCoverage()
    {
        var draft = Recipe(ReelFraming.CharacterSeatedToStanding); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(5, plan.Count); Assert.Contains("seated upright", plan[0].Description);
        Assert.Contains("three-quarter", plan[1].Description); Assert.Contains("stand slowly", plan[1].Description);
        Assert.Contains("without a cut or moving camera", plan[1].Description);
        Assert.Contains("Standing frontal", plan[2].Description); Assert.Contains("Standing right-profile", plan[3].Description);
        var pair = ReferenceReels.Preset(draft);
        Assert.Contains("Begin seated upright on one plain backless stool", pair.Prompt);
        Assert.DoesNotContain("Use a relaxed stance", pair.Prompt);
        Assert.Contains("does not include rear or left-profile coverage", pair.UseGuidance);
        Assert.Contains("staging, not part of the person's identity", pair.UseGuidance);
    }

    [Fact]
    public void FacePriorityActuallySpendsTheReelOnTheHeadInsteadOfBodyCoverage()
    {
        var draft = Recipe(ReelFraming.CharacterFacePriority); var plan = ReferenceReels.CharacterStudyPlan(draft);
        Assert.Equal(7, plan.Count); Assert.Equal(24, plan[0].EndFrameExclusive);
        Assert.All(plan.Skip(1).Take(5), view => Assert.Contains("head-and-shoulders", view.Description));
        Assert.Contains("right-profile", plan[2].Description); Assert.Contains("left-profile", plan[3].Description);
        Assert.Contains("right three-quarter", plan[4].Description); Assert.Contains("left three-quarter", plan[5].Description);
        Assert.Equal(48, plan[^1].EndFrameExclusive - plan[^1].StartFrame);
        Assert.Contains("context only, not full-body or rear coverage", ReferenceReels.Preset(draft).UseGuidance);
        Assert.DoesNotContain("rear full-body", ReferenceReels.Views(draft));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AssistGetsOnlyTheSelectedStudyGoalAndKeepsTheActualPictureContext(ReelFraming mode)
    {
        var draft = Recipe(mode); draft.Instructions = "Use Picture 2 for the outfit, Picture 1 for the face.";
        draft.Images[0].PreservationOverride = "Use only facial identity and glasses.";
        draft.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), InferUsage = true, Notes = "Body and outfit" });
        var before = ReferenceReels.Fingerprint(draft);
        var payload = new ReelCompositionRequest(Guid.NewGuid(), draft, before,
            new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        var messages = ReferenceReels.Messages(payload, [new byte[] { 1 }, new byte[] { 2 }]);
        var rules = messages[0].Text!;
        Assert.Contains("Capture goal: " + ReferenceReels.CapturePreset(draft)!.Goal, rules);
        Assert.Contains("observations of ONE person", rules); Assert.Contains("ONE canonical outfit", rules);
        Assert.Contains("Do not describe unseen details as observed facts", rules);
        Assert.DoesNotContain("At requested durations of at least 9 seconds use only the selected", rules);
        Assert.Contains("THIS mode only", rules); Assert.Contains("Return ordinary JSON with exactly prompt and useGuidance", rules);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(ReferenceReels.Views(draft), context.RootElement.GetProperty("presetViews").GetString());
        Assert.Equal(draft.Instructions, context.RootElement.GetProperty("request").GetProperty("draft").GetProperty("instructions").GetString());
        Assert.Contains("facial identity and glasses", context.RootElement.GetProperty("pictures")[0].GetString());
        Assert.Equal(2, messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Theory]
    [InlineData(ReelVoiceMode.Silent)] [InlineData(ReelVoiceMode.NewVoice)] [InlineData(ReelVoiceMode.ExistingRecording)]
    public void StudyPresetAndAssistRespectAnExplicitVoiceMode(ReelVoiceMode mode)
    {
        var draft = Recipe(ReelFraming.CharacterFacePriority); draft.VoiceMode = mode;
        draft.VoiceDescription = "Warm and lightly raspy.";
        if (mode == ReelVoiceMode.ExistingRecording) draft.Voice = ReferenceReelTests.Recipe(voice: mode).Voice;
        var pair = ReferenceReels.Preset(draft); ReferenceReels.ValidatePair(pair, draft);
        Assert.Equal(mode == ReelVoiceMode.Silent ? 0 : 1, Regex.Matches(pair.Prompt, "<d>").Count);
        Assert.Equal(mode == ReelVoiceMode.ExistingRecording, pair.Prompt.Contains("<Audio 1>"));
        Assert.Equal(mode != ReelVoiceMode.Silent, pair.Prompt.Contains(draft.VoiceDescription));
        Assert.Equal(mode != ReelVoiceMode.Silent, pair.UseGuidance.Contains(draft.VoiceDescription));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StudyRecipesRoundTripThroughTheCapturedVideoContractAndSharedWorkflow(ReelFraming mode)
    {
        var draft = Recipe(mode); var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        var shot = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = VideoResolutions.Size(draft);
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 1, shot, draft.Prompt, VideoResolutions.Fingerprint(draft),
            "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(draft.Duration), ReferenceReels.Profile)
        { Reel = new(draft.Copy(), new(draft.AssetId, "Riley", "", "", null, "General", "", "")), OutputPolicy = new(false),
            Preset = H3Presets.Capture(shot, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(shot, settings) };
        var request = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(request);
        var reopened = JsonSerializer.Deserialize<AiVideoJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        AiVideoJobPolicy.Validate(reopened); Assert.Equal(mode, reopened.Snapshot.Reel!.Recipe.Framing);
        Assert.Equal(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(reopened.Snapshot.Reel.Recipe));
        Assert.Null(reopened.Snapshot.Reel.Recipe.CaptureArticulation); Assert.Null(reopened.Snapshot.Reel.Recipe.CaptureCloseUp);
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), 2, 42).Candidates;
        var graph = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(reopened.Snapshot, c.Seed, c.Id.ToString("D"), [new("image.png", false)]), "shared-client")).GetProperty("prompt");
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo");
        Assert.Equal(H3Policy.Frames(draft.Duration), graph.GetProperty("5").GetProperty("inputs").GetProperty("length").GetInt32());
        Assert.Equal(pair.Prompt, graph.GetProperty("5").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.Equal(2, graph.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced"));
        Assert.Null(shot.SceneId); Assert.Null(shot.ApprovedScriptId);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Snapshot = snapshot with { FrameCount = 124 } }));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ReusingStudyDirectionsKeepsTheDestinationTimingVoiceAndAuthoredPair(ReelFraming mode)
    {
        var source = Recipe(mode); source.Instructions = "Keep the glasses."; var before = ReferenceReels.Fingerprint(source);
        var target = ReferenceReelTests.Recipe(voice: ReelVoiceMode.ExistingRecording);
        ReferenceReels.SelectCharacterPreset(target, ReelFraming.CharacterCapture);
        target.CaptureArticulation = ReelArticulation.Knee; target.CaptureCloseUp = ReelCloseUpTransition.PushIn;
        target.Duration = 15; target.VoiceMode = ReelVoiceMode.ExistingRecording; target.Prompt = "Authored prompt"; target.UseGuidance = "Authored guidance";
        var images = target.Images.Select(i => i.Id).ToArray(); var voice = target.Voice;
        ReferenceReels.ReuseDirections(target, source);
        Assert.Equal(mode, target.Framing); Assert.Equal(15, target.Duration); Assert.Equal(ReelVoiceMode.ExistingRecording, target.VoiceMode);
        Assert.Equal(voice, target.Voice); Assert.Equal(images, target.Images.Select(i => i.Id));
        Assert.Equal("Authored prompt", target.Prompt); Assert.Equal("Authored guidance", target.UseGuidance); Assert.Null(target.CheckedInputs);
        Assert.Null(target.CaptureArticulation); Assert.Null(target.CaptureCloseUp);
        ReferenceReels.ValidateProfile(target); Assert.Equal(362, ReferenceReels.CharacterStudyPlan(target)[^1].EndFrameExclusive);
        Assert.Equal(before, ReferenceReels.Fingerprint(source));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(target with { CaptureArticulation = ReelArticulation.Arms }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(target with { CaptureCloseUp = ReelCloseUpTransition.Cut }));
    }
}

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CharacterStudyChangesSurviveSaveAndRejectAStaleAutomaticPair(bool changeDuration)
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var owner = Asset("Riley") with { Category = AssetCategory.Character };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        using var image = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, owner.Id, image, new("face.png", [], AssetImageOrigin.Imported), library.Revision, ct);
        var draft = ReferenceReels.NewDraft(owner); ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterNeutralTurntable);
        draft.Images = [new() { AssetId = owner.Id, MediaId = library.Assets.Single().Images.Single().Id, InferUsage = true }];
        var jobId = Guid.NewGuid(); draft.PendingJobId = jobId; draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var pair = ReferenceReels.Preset(draft);
        var inputs = await ProductionInputs.CaptureAsync(project.Id, ReferenceReels.Inputs(draft), store, ct);
        var request = new ReelCompositionRequest(project.Id, draft.Copy(), ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), inputs.Select(i => i.Identity).ToArray())
        { ImageGuidance = ShotReferences.Resolve(ReferenceReels.Inputs(draft), library, new()) };
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelComposition, Backend = AiBackend.OpenRouter,
            Target = new(project.Id, owner.Id, ReelId: draft.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Completed };
        if (changeDuration) draft.Duration = 10;
        else ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterFacePriority);
        await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct);
        Assert.False(await store.ApplyPairAsync(job, request, pair, true, ct));
        var saved = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single(d => d.Id == draft.Id);
        Assert.Equal(draft.Framing, saved.Framing); Assert.Equal(draft.Duration, saved.Duration);
        Assert.Empty(saved.Prompt); Assert.Empty(saved.UseGuidance);
        Assert.Equal(ReelFraming.CharacterNeutralTurntable, request.Draft.Framing); Assert.Equal(8, request.Draft.Duration);
    }
}
