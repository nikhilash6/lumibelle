using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed record CharacterCapturePresetInfo(ReelFraming Framing, string Label, double Seconds,
    double MinimumSeconds, string Goal, string Summary, string Purpose);

internal sealed record CharacterCaptureView(int StartFrame, int EndFrameExclusive, string Description);

public static partial class ReferenceReels
{
    // These are authoring plans, not claims that any particular pose improves model fidelity.
    // New enum values are appended; existing recipes and the combined capture keep their identities.
    public static IReadOnlyList<CharacterCapturePresetInfo> CharacterCapturePresets { get; } = Array.AsReadOnly<CharacterCapturePresetInfo>([
        new(ReelFraming.CharacterCapture, "All angles → face", 10, 5, "all_angles_face",
            "Full-body angular coverage, an optional small movement, then a face close-up.",
            "Frontal appearance, both profiles, the rear, body proportions, outfit detail and facial identity."),
        new(ReelFraming.CharacterNeutralTurntable, "Identity-first neutral turntable", 8, 5, "identity_preservation",
            "Front hold → camera orbit around a stationary person → front hold → settled face close-up. No articulation.",
            "Facial likeness, natural body proportions, side and rear silhouettes, hair volume and outfit continuity."),
        new(ReelFraming.CharacterSilhouetteReveal, "Silhouette reveal", 10, 8, "silhouette_reveal",
            "Raise the arms gently, then hold front, right profile, back and left profile views before a face close-up.",
            "Shoulder width, torso depth, waist and hip outline, limb proportions and facial likeness with the arms clear of the torso."),
        new(ReelFraming.CharacterPoseExpansion, "Pose-expansion reference", 10, 10, "pose_expansion",
            "Front hold → arms out and back → one small foot lift and return → held profile, back and opposite profile → face.",
            "Arm and leg proportions, balance and posture across a neutral stance and two small, sequential pose changes."),
        new(ReelFraming.CharacterDrapeCheck, "Bend / waist / drape check", 10, 8, "waist_drape_check",
            "Front → right profile with one gentle waist bend and return → back → face. Camera still during the bend.",
            "Torso depth, back shape, clothing drape and hair fall during a subtle lean, plus a stable face reference. This plan does not include a left-profile view."),
        new(ReelFraming.CharacterSeatedToStanding, "Sitting-to-standing reference", 8, 6, "seated_reference",
            "Seated front → seated three-quarter and slow stand → standing front → right profile → face. One plain stool.",
            "Seated versus standing posture, leg bend, hip relationship and outfit drape, plus facial likeness. This plan does not include rear or left-profile coverage. The stool is staging, not part of the person's identity."),
        new(ReelFraming.CharacterFacePriority, "Face-priority reference", 8, 6, "face_priority",
            "Brief full-body front → head-and-shoulders front, both profiles and both three-quarter views → held frontal face.",
            "Facial structure, eyes, nose, lips, jawline, hairline, ears and hairstyle from held head views. The brief full-body front is context only, not full-body or rear coverage.")
    ]);

    public static CharacterCapturePresetInfo? CapturePreset(ReferenceReelDraft draft) => draft.PresetVersion == Profile
        ? CharacterCapturePresets.FirstOrDefault(p => p.Framing == draft.Framing) : null;
    public static bool HasCharacterCaptureOptions(ReferenceReelDraft draft) => draft.PresetVersion == Profile && draft.Framing == ReelFraming.CharacterCapture;

    private static string CaptureSeconds(int frame) => (frame / 24d).ToString("0.###", CultureInfo.InvariantCulture);
    private static string CaptureTimestamp(int frame) => TimeSpan.FromSeconds(frame / 24d).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    private const string CaptureFaceView = "Hold a stationary frontal face close-up, from above the hairline to below the chin. " +
        "Keep the jaw and ears readable where visible, the same hair and accessories, a neutral expression and natural skin detail.";
    private const string CaptureFramingMargins = "Keep the entire person visible with margins above the hair, below the feet and beside the hands. ";
    private const string CaptureViewContinuity = "Keep one person, one outfit and consistent lighting across clean cuts; no dissolves or morphing. " +
        "Right and left refer to the person's anatomical sides. Reposition the camera between views, not the person. ";

    // Contiguous integer frame intervals are used by both deterministic prompts and Assist.
    // Only actual camera cuts become new [Shot N] entries. Actions within a view stay in that view.
    internal static IReadOnlyList<CharacterCaptureView> CharacterStudyPlan(ReferenceReelDraft draft)
    {
        ValidateComposition(draft);
        var frames = H3Policy.Frames(draft.Duration);
        var bodyEnd = frames - 48;
        if (draft.Framing == ReelFraming.CharacterNeutralTurntable)
        {
            if (draft.Duration < 8)
            {
                var names = new[] { "frontal full-body", "right-profile full-body", "rear full-body", "left-profile full-body", "frontal face close-up" };
                return names.Select((name, i) => new CharacterCaptureView(frames * i / 5, frames * (i + 1) / 5,
                    $"Hold a stationary {name} view. " + (i == 4 ? CaptureFaceView : CaptureFramingMargins) +
                    "Keep the same relaxed pose with hands clear of the hips. No articulation or camera movement. " +
                    "These are five held principal views, not a continuous orbit.")).ToArray();
            }
            var orbitEnd = bodyEnd - 24;
            int Point(int quarter) => 24 + (orbitEnd - 24) * quarter / 4;
            return [new(0, bodyEnd,
                "Hold a frontal full-body view from 0 to 1 seconds. " +
                $"From 1 to {CaptureSeconds(orbitEnd)} seconds the CAMERA travels one complete 360-degree horizontal orbit around the stationary person at an even pace that completes the circle in this interval. " +
                "Keep the radius, camera height and full-body framing constant. Pass front → front-right three-quarter → right profile → rear-right three-quarter → full back → rear-left three-quarter → left profile → front-left three-quarter → front. " +
                $"Reach the right profile at {CaptureSeconds(Point(1))} seconds, the full back at {CaptureSeconds(Point(2))} seconds halfway through the orbit, and the left profile at {CaptureSeconds(Point(3))} seconds. " +
                $"Return to the front by {CaptureSeconds(orbitEnd)} seconds, then hold that frontal view until {CaptureSeconds(bodyEnd)} seconds. " +
                "No articulation. The person does not rotate with or track the camera; the head stays aligned with the torso and the gaze remains toward the original front of the studio. " + CaptureFramingMargins),
                new(bodyEnd, frames, CaptureFaceView)];
        }
        if (draft.Framing == ReelFraming.CharacterFacePriority)
        {
            var names = new[] { "frontal", "right-profile", "left-profile", "right three-quarter", "left three-quarter" };
            List<CharacterCaptureView> views = [new(0, 24, "Brief stationary frontal full-body view for overall proportions. No pose change. " + CaptureFramingMargins)];
            for (var i = 0; i < names.Length; i++)
                views.Add(new(24 + (bodyEnd - 24) * i / names.Length, 24 + (bodyEnd - 24) * (i + 1) / names.Length,
                    $"Hold a stationary {names[i]} head-and-shoulders view. " +
                    "Keep the head aligned with the torso and facing the original front; the camera's viewpoint changes between cuts. " +
                    "Preserve facial geometry, eyes, nose, lips, jawline, hairline, visible ears, hair volume and accessories without retouching. " +
                    "Keep the whole head and visible jaw unobstructed, with a neutral expression. No articulation."));
            views.Add(new(bodyEnd, frames, CaptureFaceView));
            return views;
        }
        int[] weights = draft.Framing switch
        {
            ReelFraming.CharacterSilhouetteReveal => [3, 2, 2, 2],
            ReelFraming.CharacterPoseExpansion => [5, 1, 1, 1],
            ReelFraming.CharacterDrapeCheck => [2, 5, 2],
            ReelFraming.CharacterSeatedToStanding => [2, 7, 2, 2],
            _ => throw new WorkspaceStoreException("Choose a supported character study preset.")
        };
        var total = weights.Sum(); var used = 0;
        List<CharacterCaptureView> result = [];
        for (var i = 0; i < weights.Length; i++)
        {
            var start = bodyEnd * used / total;
            used += weights[i]; var end = bodyEnd * used / total;
            result.Add(new(start, end, StudyView(draft.Framing, i, start, end)));
        }
        result.Add(new(bodyEnd, frames, CaptureFaceView));
        return result;
    }

    private static string StudyView(ReelFraming framing, int view, int start, int end)
    {
        const string still = "Keep the camera stationary throughout this view. ";
        const string arms = "Hold both arms about 30–45 degrees away from the torso with relaxed hands and feet slightly apart. No full T-pose. ";
        return framing switch
        {
            ReelFraming.CharacterSilhouetteReveal => still + CaptureFramingMargins + (view switch
            {
                0 => "Start in a neutral frontal stance and hold until 0.5 seconds. From 0.5 to 1.5 seconds gently raise both arms outward, then hold the open silhouette for the rest of this view. " + arms,
                1 => "Right-profile full-body view. " + arms,
                2 => "Full-body back view with waist, hips and clothing unobscured. " + arms,
                _ => "Left-profile full-body view. " + arms
            }),
            ReelFraming.CharacterPoseExpansion => still + CaptureFramingMargins + (view switch
            {
                0 => "Frontal full-body view. Hold a relaxed neutral stance until 0.5 seconds. " +
                    "From 0.5 to 2.5 seconds gently raise both arms 30–45 degrees away from the torso, pause, then lower them completely to the starting position. " +
                    "Only after the arms return, from 2.5 to 4.5 seconds shift weight to the left leg, gently bend the right knee and lift the right foot a short distance from the floor, then replace it and regain a balanced stance. " +
                    "Hold the neutral stance for the rest of this view. The two movements are sequential, never simultaneous. No walking, marching, dancing or spinning.",
                1 => "Right-profile full-body view in the original relaxed stance. No further articulation.",
                2 => "Full-body back view in the original relaxed stance. No further articulation.",
                _ => "Left-profile full-body view in the original relaxed stance. No further articulation."
            }),
            ReelFraming.CharacterDrapeCheck => still + CaptureFramingMargins + (view switch
            {
                0 => "Frontal full-body view in a neutral upright stance.",
                1 => $"Right-profile full-body view. Hold upright until {CaptureSeconds(start + 12)} seconds. " +
                    $"From {CaptureSeconds(start + 12)} to {CaptureSeconds(start + 36)} seconds bend gently forward at the waist by about 15 degrees with feet planted; keep the head naturally aligned. " +
                    $"Briefly hold the subtle lean until {CaptureSeconds(start + 48)} seconds, then return fully upright by {CaptureSeconds(start + 72)} seconds and hold for the rest of the view. " +
                    "Let hair and clothing settle naturally to reveal torso depth and drape. No deep fold, squat, twist or outfit change.",
                _ => "Full-body back view, upright again, with hair and clothing settled. No further articulation."
            }),
            ReelFraming.CharacterSeatedToStanding => still + CaptureFramingMargins + (view switch
            {
                0 => "Frontal full-body view of the person seated upright on one plain backless stool. Feet rest on the floor and hands rest naturally without hiding the torso or thighs.",
                1 => $"Right three-quarter full-body view of the same seated person and stool. Hold seated until {CaptureSeconds(start + (end - start) / 6)} seconds. " +
                    $"Then stand slowly and naturally, completing the rise by {CaptureSeconds(end - (end - start) / 6)} seconds, and hold upright. " +
                    "Show the transition without a cut or moving camera. Keep feet visible and movement controlled; the stool remains stationary behind the person.",
                2 => "Standing frontal full-body view in a relaxed stance. Keep the same stool behind the person without blocking the legs; do not remove or transform it.",
                _ => "Standing right-profile full-body view. Preserve the upright posture, proportions, clothing drape and stationary stool. No further movement."
            }),
            _ => throw new WorkspaceStoreException("Choose a supported character study preset.")
        };
    }

    private static string CharacterStudyViews(ReferenceReelDraft draft) => CaptureViewContinuity + "\n\n" +
        string.Join("\n\n", CharacterStudyPlan(draft).Select((view, i) => $"[Shot {i + 1}] " +
            (i == 0 ? "" : $"At {CaptureTimestamp(view.StartFrame)}, make a clean cut to the next view. ") +
            view.Description + $" This view ends at {CaptureSeconds(view.EndFrameExclusive)} seconds."));

    private static string CharacterStudyInstructions(ReferenceReelDraft draft)
    {
        var preset = CapturePreset(draft) ?? throw new WorkspaceStoreException("Choose a character capture preset.");
        var modeRules = draft.Framing switch
        {
            ReelFraming.CharacterNeutralTurntable => "At eight or more requested seconds keep the subject stationary through one complete camera orbit with the supplied front holds. Below eight seconds use the supplied five held principal views instead; do not describe them as a continuous orbit. No articulation. Use an even pace that fits the allocated interval, not an implausibly slow full circle. ",
            ReelFraming.CharacterSilhouetteReveal => "After the initial arm raise, keep the arms 30–45 degrees clear of the torso in every held body view. This is not a full T-pose, and there is no knee lift or camera orbit. ",
            ReelFraming.CharacterPoseExpansion => "Complete the arm movement and lower the arms before the foot lift. Keep the camera stationary during both actions. Return to neutral before the held side/back views. Do not merge the two gestures, add walking, or add an orbit. ",
            ReelFraming.CharacterDrapeCheck => "Use one subtle waist bend and return in a stationary right-profile view. Preserve clothing drape and hair fall; no deep fold, twist or additional articulation. Do not add a left-profile view or a camera orbit to this plan. ",
            ReelFraming.CharacterSeatedToStanding => "Begin seated, with exactly one plain stationary stool. Include the slow rise in the three-quarter view with no simultaneous camera movement or cut. Do not force a standing starting stance, an orbit or rear coverage. The stool is staging, not a reusable identity attribute. ",
            ReelFraming.CharacterFacePriority => "Spend most of the reel in the specified held head views with a neutral expression. The short frontal body view is context only; do not promise multi-angle body or rear coverage. No limb articulation or full-body orbit. ",
            _ => throw new WorkspaceStoreException("Choose a supported character study preset.")
        };
        return CharacterReferenceInstructions + CharacterCaptureAppearance(draft) + " " +
            $"Selected capture mode: {preset.Label}. Capture goal: {preset.Goal}. Intended purpose: {preset.Purpose} " +
            "Follow the supplied presetViews and frame-derived timings for THIS mode only. Do not combine the six variations into one reel or replace this plan with the All angles → face choreography. " +
            "Prefer readable holds and minimal controlled movement. Complete one action at a time and never move the camera while the person articulates. " +
            modeRules + "Use guidance must describe only the selected mode's planned coverage and limits, not every variation's benefits. " +
            CharacterReferenceOutputInstructions;
    }
}
