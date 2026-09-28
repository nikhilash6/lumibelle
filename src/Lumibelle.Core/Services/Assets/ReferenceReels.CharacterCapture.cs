using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public static bool IsCharacterCapture(ReferenceReelDraft draft) => CapturePreset(draft) is not null;
    public static ReelArticulation SelectedArticulation(ReferenceReelDraft draft) => draft.CaptureArticulation ?? ReelArticulation.Arms;
    public static ReelCloseUpTransition SelectedCloseUp(ReferenceReelDraft draft) => draft.CaptureCloseUp ?? ReelCloseUpTransition.Cut;

    public static void SelectCharacterPreset(ReferenceReelDraft draft, ReelFraming framing)
    {
        if (draft.PresetVersion != Profile || !Framings(draft).Contains(framing))
            throw new WorkspaceStoreException("Choose a character framing preset.");
        if (draft.Framing == framing) return;
        draft.Framing = framing;
        draft.CaptureArticulation = null;
        draft.CaptureCloseUp = null;
        if (CapturePreset(draft) is { } preset)
        {
            draft.Duration = preset.Seconds;
            if (framing == ReelFraming.CharacterCapture)
            {
                draft.CaptureArticulation = ReelArticulation.Arms;
                draft.CaptureCloseUp = ReelCloseUpTransition.Cut;
            }
            // A visual-capture brief starts without speech. Keep the remembered recording
            // and sample line so an explicit subsequent voice-mode change can reuse them.
            draft.VoiceMode = ReelVoiceMode.Silent;
        }
        else if (IsVoiceReference(draft))
        {
            draft.Duration = 10;
            if (draft.VoiceMode == ReelVoiceMode.Silent)
                draft.VoiceMode = draft.Voice is null ? ReelVoiceMode.NewVoice : ReelVoiceMode.ExistingRecording;
            // Selecting a speaking preset is opt-in, but choosing its words is separate.
            draft.Speech ??= new();
        }
        // Selecting a preset never rewrites an authored prompt pair or its references.
        draft.CheckedInputs = null;
    }

    private static void ValidateCharacterCaptureOptions(ReferenceReelDraft draft)
    {
        if (draft.CaptureArticulation is { } articulation && !Enum.IsDefined(articulation) ||
            draft.CaptureCloseUp is { } transition && !Enum.IsDefined(transition))
            throw new WorkspaceStoreException("Choose a supported capture movement and close-up transition.");
        if (!HasCharacterCaptureOptions(draft) && (draft.CaptureArticulation is not null || draft.CaptureCloseUp is not null))
            throw new WorkspaceStoreException("Capture movement and close-up options belong to the All angles → face preset.");
    }

    public static string CharacterCaptureSummary(ReferenceReelDraft draft)
    {
        if (CapturePreset(draft) is { } preset && !HasCharacterCaptureOptions(draft) &&
            !(draft.Framing == ReelFraming.CharacterNeutralTurntable && draft.Duration < 8)) return preset.Summary;
        if (draft.Duration < 8) return "Five held principal views: front, right profile, back, left profile and face. No articulation.";
        var movement = draft.Duration < 9 ? ReelArticulation.None : SelectedArticulation(draft);
        var action = movement switch
        {
            ReelArticulation.Arms => "raise and lower both arms after the camera stops, then ",
            ReelArticulation.Knee => "lift and replace one foot after the camera stops, then ",
            _ => "no articulation, then "
        };
        return "One complete camera orbit around a stationary person; " + action +
            (SelectedCloseUp(draft) == ReelCloseUpTransition.Cut ? "cut to a held face close-up." : "push in to a settled face close-up without a cut.");
    }

    // The extra frames introduced by H3 duration snapping belong to the opening coverage.
    // Reserve exactly 48 frames for each optional movement and final close-up segment.
    private static string CharacterCaptureViews(ReferenceReelDraft draft)
    {
        if (!HasCharacterCaptureOptions(draft)) return CharacterStudyViews(draft);
        ValidateProfile(draft);
        var frames = H3Policy.Frames(draft.Duration);
        if (CompositionInstructionsIssue(draft) is { } issue) throw new WorkspaceStoreException(issue);
        static string Seconds(int frame) => (frame / 24d).ToString("0.###", CultureInfo.InvariantCulture);
        static string Timestamp(int frame) => TimeSpan.FromSeconds(frame / 24d).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        const string face = "Frame the full face from above the hairline to below the chin, with the jaw and ears readable where visible. Keep the same hair, accessories and outfit, a neutral expression and unobstructed facial features.";
        if (draft.Duration < 8)
        {
            var views = new[] { "frontal full-body view", "right-profile full-body view", "rear full-body view", "left-profile full-body view", "frontal face close-up" };
            return string.Join("\n\n", views.Select((view, i) => $"[Shot {i + 1}] " +
                (i == 0 ? "Begin with a " : $"At {Timestamp(frames * i / 5)}, make a clean cut to a ") +
                $"{view}. Hold the camera stationary until {Seconds(frames * (i + 1) / 5)} seconds. " +
                (i == 4 ? face : "Keep the entire person visible with margins above the hair and below the feet. ") +
                "Preserve one person, outfit, studio, lighting and relaxed pose. Right and left are the person's anatomical sides. " +
                "No articulation, camera movement, dissolves or morphing. These are five principal viewpoints, not a continuous orbit."));
        }
        var articulate = draft.Duration >= 9 && SelectedArticulation(draft) != ReelArticulation.None;
        var closeUp = frames - 48;
        var orbitEnd = closeUp - (articulate ? 48 : 0);
        const int frontHold = 6;
        int OrbitPoint(int quarter) => frontHold + (orbitEnd - frontHold) * quarter / 4;
        List<string> phases = [
            $"[Shot 1] Hold a relaxed frontal full-body view from 0 to {Seconds(frontHold)} seconds. " +
            $"From {Seconds(frontHold)} to {Seconds(orbitEnd)} seconds the CAMERA travels once around the stationary person in a complete 360-degree horizontal orbit, " +
            "with large amplitude at a brisk, even pace that completes the circle within this interval. " +
            "Keep camera distance, height and full-body framing approximately constant. This is camera travel around the person, not a pan from a fixed position or a rotating subject. " +
            "Pass front → front-right three-quarter → right profile → rear-right three-quarter → full back → rear-left three-quarter → left profile → front-left three-quarter → front. " +
            $"Reach the right profile by {Seconds(OrbitPoint(1))} seconds, show the full back clearly at {Seconds(OrbitPoint(2))} seconds halfway through the orbit, " +
            $"reach the left profile by {Seconds(OrbitPoint(3))} seconds, then return to the frontal viewpoint and stop by {Seconds(orbitEnd)} seconds. " +
            "Right and left are the person's anatomical sides. The person does not rotate with the camera; the head remains aligned with the torso and the gaze faces the original front of the studio, not the moving lens."
        ];
        if (articulate)
        {
            var movement = SelectedArticulation(draft) == ReelArticulation.Arms
                ? "The person lifts both arms outward to approximately 45 degrees below shoulder height, briefly pauses with relaxed hands, then lowers both arms to the starting position."
                : "The person shifts weight onto the left leg, gently bends the right knee and lifts the right foot a short distance from the floor, then places it back down and returns to a balanced stance.";
            phases.Add($"From {Seconds(orbitEnd)} to {Seconds(closeUp)} seconds keep the camera stationary at the frontal viewpoint. {movement} " +
                "Keep the entire person and all moving limbs in frame. Complete only this movement; no marching, dancing, waving, overhead stretching or additional turn.");
        }
        else phases.Add("No articulation: keep the starting relaxed stance throughout the full-body coverage.");
        if (SelectedCloseUp(draft) == ReelCloseUpTransition.Cut)
            phases.Add($"[Shot 2] At {Timestamp(closeUp)}, make one clean cut to a frontal face close-up. {face} Hold the camera still until {Seconds(frames)} seconds.");
        else
            phases.Add($"From {Seconds(closeUp)} to {Seconds(frames - 24)} seconds, continue the same shot without a cut: push the camera closer and adjust its height toward eye level. " +
                $"{face} Finish moving by {Seconds(frames - 24)} seconds and hold a settled face close-up until {Seconds(frames)} seconds.");
        return string.Join("\n\n", phases);
    }

    private const string CaptureStudio = "Place the person alone in an uncluttered light-neutral studio with a matte floor and a subtle contact shadow. " +
        "Use broad soft illumination that keeps the face, sides and back readable, with natural skin and fabric appearance and enough depth of field for the whole body. ";
    private const string CapturePose = "Use a relaxed stance with feet slightly apart, hands clear of the hips and a neutral expression. ";
    private const string CaptureAppearanceDetails = "Keep the selected glasses and other accessories, and let the hair remain settled. " + CaptureFidelity;
    private const string CaptureFidelity = "In full-body views leave margins above the hair, below the feet and beside any arm movement. Retain observed proportions and natural asymmetries; " +
        "do not add slimming, muscular enhancement, glamour makeup, skin smoothing or a different hairstyle. " +
        "Continue unseen hair and clothing conservatively without inventing distinctive markings, logos, fastenings or accessories.";
    private const string CaptureAppearance = CaptureStudio + CapturePose + CaptureAppearanceDetails;
    private static string CharacterCaptureAppearance(ReferenceReelDraft draft) => draft.Framing switch
    {
        ReelFraming.CharacterSeatedToStanding => CaptureStudio + "Begin seated upright on one plain backless stool, with feet on the floor and a neutral expression. Follow the seated-to-standing plan below; the stool is newly generated staging, not part of the retained appearance. " + CaptureAppearanceDetails,
        ReelFraming.CharacterDrapeCheck => CaptureStudio + CapturePose + "Keep the selected glasses and other accessories; let hair and clothing respond naturally to the subtle lean and settle again in the held views. " + CaptureFidelity,
        _ => CaptureAppearance
    };

    private static ReelPromptPair CharacterCapturePreset(ReferenceReelDraft draft, AssetLibrary? library)
    {
        ValidateComposition(draft);
        if (!HasVisualReferences(draft)) throw new WorkspaceStoreException("Select visual references before building the preset.");
        var seconds = H3Policy.Seconds(draft.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        var details = PictureGuidance(draft, library);
        var references = string.Join("\n", details.Select((g, i) => $"<Picture {i + 1}>: partially_preserved. " +
            (string.IsNullOrWhiteSpace(g) ? "Use the visible identity and appearance of the same person." : g) +
            " Inherit only the indicated appearance, not the source pose, crop, background or lighting.")) + VideoDefinitions(draft);
        var audio = draft.VoiceMode == ReelVoiceMode.ExistingRecording
            ? $"\n<Audio 1> supplies voice identity for {draft.Speaker} (S1), not its words or background sound." : "";
        var speech = draft.VoiceMode == ReelVoiceMode.Silent ? "No dialogue or vocal performance." :
            $"Speak once, beginning near the start and continuing naturally across any cut without restarting. {draft.Speaker} (S1) says: <d>[{draft.Language}] {draft.Line}</d>";
        if (draft.VoiceMode != ReelVoiceMode.Silent && !string.IsNullOrWhiteSpace(draft.VoiceDescription))
            speech += draft.VoiceMode == ReelVoiceMode.NewVoice ? $"\nIntended voice: {draft.VoiceDescription}" :
                $"\nPreserve the voice identity from <Audio 1>. Delivery direction: {draft.VoiceDescription}";
        var name = string.IsNullOrWhiteSpace(draft.Speaker) ? "the person in the selected visual references" : draft.Speaker;
        var coverage = CharacterCaptureSummary(draft);
        var prompt = $"subject_definitions:\n<Subject 1> is {name}, one person throughout. All visual references are observations of this one subject, not successive frames or separate people.\n{references}{audio}\n\n" +
            $"summary:\nA {draft.Aspect} neutral character-reference capture lasting {seconds} seconds. {coverage}\n\n" +
            "retention_analysis:\nPreserve the selected identity, proportions, hair and accessories. Use one consistent outfit, honoring explicit outfit-reference guidance rather than mixing incompatible garments. " +
            "Use fully_preserved only for specific retained elements. The studio, lighting, choreography and any unseen continuation are newly_generated, not observed evidence.\n\n" +
            $"detailed_description:\nExactly {seconds} seconds total. {CharacterCaptureAppearance(draft)}\n\n{CharacterCaptureViews(draft)}\n\n{speech}\n\n" +
            "overall_soundscape:\n" + (draft.VoiceMode == ReelVoiceMode.Silent
                ? "Silent reference. No speech, ambient sound or audible soundtrack."
                : "Only the specified natural speaking voice, faint breathing and minimal clothing sound. No distracting ambience.") +
            "\n\nnon_diegetic_music:\nNo non-diegetic music.";
        var introduction = HasCharacterCaptureOptions(draft)
            ? $"Visual reference of {name}, intended to show frontal appearance, both profiles, the rear, body proportions, outfit detail and facial identity. "
            : $"Visual reference of {name}. Intended coverage: {CapturePreset(draft)!.Purpose} ";
        var inspection = HasCharacterCaptureOptions(draft) ? "Inspect the actual back, profiles, proportions, accessories and outfit continuity before accepting the reel. "
            : "Inspect the actual generated views, proportions, accessories and outfit continuity before accepting the reel; missing coverage is not verified by this plan. ";
        var guidance = introduction +
            "These are intended uses, not verified output. Any unseen angle is generated interpretation, not new evidence about the person. " +
            inspection +
            (draft.VoiceMode == ReelVoiceMode.Silent ? "Visual reference only. " : "When its soundtrack is enabled, use it for voice identity and natural timbre. ") +
            (draft.Framing == ReelFraming.CharacterSeatedToStanding
                ? "Do not copy the studio, lighting, stool, pose, camera motion, cuts, timing or words into later shots. Follow the target shot's action and dialogue."
                : "Do not copy the studio, lighting, pose, camera orbit, cuts, articulation, timing or words into later shots. Follow the target shot's action and dialogue.");
        if (draft.VoiceMode != ReelVoiceMode.Silent && !string.IsNullOrWhiteSpace(draft.VoiceDescription))
            guidance += $"\nIntended voice direction (not verified output): {draft.VoiceDescription}";
        var retained = string.Join("\n", details.Concat(VideoGuidance(draft)).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct());
        if (retained.Length > 0) guidance += "\nIntended appearance details from the source visual references:\n" + retained;
        var pair = new ReelPromptPair(prompt, guidance);
        ValidatePair(pair, draft);
        return pair;
    }

    private const string CharacterReferenceInstructions =
        "This is a reference-capture brief, not a cinematic scene, fashion commercial, beauty montage or exercise routine. " +
        "Treat all supplied photographs and selected keyframes as different observations of ONE person. Use only the actual <Picture N> and <Video N> identifiers in the supplied order; never invent additional references. " +
        "Resolve face, body and outfit references before writing. Honor explicit overrides in Instructions and captured image guidance. Otherwise select the clearest suitable face for facial structure, hair and persistent accessories; " +
        "a minimally foreshortened full-body view for proportions; and the clearest complete outfit in a full-body view for ONE canonical outfit. " +
        "Use side and rear photos for their visible details. Other photos may inform identity without contributing clothes. Where references disagree, follow the chosen reference for that attribute instead of averaging or combining incompatible garments. " +
        "Describe only useful visible features: facial geometry, hairstyle, proportions and supported accessories. Do not infer a person's name from the photos, ethnicity, exact age, height or weight. " +
        "The supplied character name is author metadata, not an identification from images. " +
        "The photographs' poses, crops, backgrounds and lighting are not mandatory. Do not reproduce them as a slideshow, literal first/last frames or separate characters. " +
        "Define one <Subject 1>, its selected appearance and canonical outfit, and how each reference contributes. Define the studio separately. " +
        "In retention_analysis map every reference to retained attributes: partially_preserved when only selected information is inherited, fully_preserved only for specific unchanged elements, " +
        "and newly_generated for the studio and new movement. Do not describe unseen details as observed facts. ";

    private const string CharacterCaptureInstructions = CharacterReferenceInstructions + CaptureAppearance + " " +
        "Use the supplied presetViews: its timestamps already follow the generated frame count. At requested durations of at least 8 seconds reserve the final two generated seconds for the close-up. " +
        "At requested durations of at least 9 seconds use only the selected two-second articulation unless None; otherwise omit articulation. " +
        "The remaining opening interval is full-body coverage: a brief frontal view and one complete CAMERA orbit through the person's anatomical right profile, full back, left profile and front, including intervening three-quarter views. " +
        "The full back must be clearly visible halfway through the orbit, with a frontal return before any limb movement or close-up. " +
        "State direction, full 360-degree extent and a pace that finishes within the allocated time. Do not soften this into a small turn or call a short full-circle orbit slow. " +
        "Keep full-body distance and framing steady. The person stays stationary, head aligned with torso, gaze toward the original front rather than tracking the lens. " +
        "Stop the camera before the one selected arm or knee movement; do not combine competing rotations, marching, waving or additional actions. " +
        "A Cut transition creates one explicit later shot. PushIn continues the existing shot, reaching eye level and settling for the final second. " +
        "Below 8 requested seconds use exactly five held principal views with clean cuts: front, anatomical right profile, back, anatomical left profile and frontal face close-up. " +
        "Omit articulation and push-in in this shorter plan; it is principal-view coverage, not a continuous orbit. " + CharacterReferenceOutputInstructions;

    private const string CharacterReferenceOutputInstructions =
        "Use [Shot 1] only for the opening shot and consecutive shot labels only at real cuts, with At MM:SS.mmm on later shots. Movement phases within one continuous shot are not separate shots. " +
        "Write all choices as resolved concrete instructions, without placeholders or alternative actions. Explicit author directions may override the default plan, but resolve conflicts into one coherent capture rather than appending contradictory movements. " +
        "Sound follows the selected VoiceMode: the preset starts Silent, so no speech, ambience or vocal performance; do not manufacture dialogue from the sample-line field in that mode. " +
        "Keep the existing prompt/useGuidance JSON pair and six-section format. In useGuidance distinguish intended coverage from verified output and recommend inspecting the views actually generated, proportions and outfit continuity before reuse.";
}
