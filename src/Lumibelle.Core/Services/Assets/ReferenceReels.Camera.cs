using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed record CameraReelPreset(ReelFraming Framing, string Label, double Seconds, string Summary,
    bool UsesDirection = false, string? RequiredInstructions = null);

public static partial class ReferenceReels
{
    public const string LongReelKeyframeHint = "Use keyframes to reference all angles. Direct video references use only the start of this reel, up to the generated shot’s length.";
    public static IReadOnlyList<CameraReelPreset> EnvironmentPresets { get; } = Array.AsReadOnly<CameraReelPreset>([
        new(ReelFraming.EnvironmentTurn, "Full-room survey", 15, "Sweep through all four sides of the room in a broad 360° turn, briefly hold each side, then return to the opening view.", true),
        new(ReelFraming.EnvironmentHalfTurn, "Half-turn reveal", 10, "Make a broad 180° turn from one position, pause at the side view, then hold the space behind the opening camera.", true),
        new(ReelFraming.EnvironmentHeldViews, "Held viewpoints", 15, "Cut between four distinctly different sides of one room: opening, quarter-turn, opposite and remaining side.", true),
        new(ReelFraming.EnvironmentPan, "Short reveal", 6, "Make a broad 90° pan to a different part of the room; opening objects leave the frame, then hold the new view.", true),
        new(ReelFraming.EnvironmentPullBack, "Pull back", 6, "Travel backwards into a substantially wider view; foreground objects become smaller as more of the room enters the frame."),
        new(ReelFraming.EnvironmentApproach, "Move toward a feature", 6, "Travel toward the named feature until it fills much more of the view, then stop without turning around.",
            RequiredInstructions: "Name the destination in Instructions, for example: toward the window."),
        new(ReelFraming.EnvironmentArc, "Arc around a feature", 6, "Travel in a broad 90° arc around the named feature to a distinct side view, with strong background parallax, then hold.", true,
            "Name the feature in Instructions, for example: around the monitor."),
        new(ReelFraming.EnvironmentPath, "Custom path", 10, "Follow your route with visible progress from the starting view to the specified destination, then hold.",
            RequiredInstructions: "Describe the custom camera path in Instructions: starting view, route and destination.")
    ]);

    public static IReadOnlyList<CameraReelPreset> CameraPresets(ReferenceReelDraft draft) =>
        IsEnvironment(draft) ? EnvironmentPresets : IsProp(draft) ? PropPresets : [];
    public static CameraReelPreset? CameraPreset(ReferenceReelDraft draft) => CameraPresets(draft).FirstOrDefault(p => p.Framing == draft.Framing);
    public static string? CompositionInstructionsIssue(ReferenceReelDraft draft) => CapturePreset(draft) is { } capture && draft.Duration < capture.MinimumSeconds
        ? $"{capture.Label} needs at least {capture.MinimumSeconds.ToString("0", CultureInfo.InvariantCulture)} requested seconds. Choose Custom for a shorter authored plan."
        : string.IsNullOrWhiteSpace(draft.Instructions) ? CameraPreset(draft)?.RequiredInstructions : null;
    public static string? DurationAdvice(ReferenceReelDraft draft) => CameraPreset(draft) is { } preset && draft.Duration < preset.Seconds
        ? $"Suggested duration: {preset.Seconds:0} seconds. A shorter reel gives the camera less time to move and settle." : null;
    public static bool ShowKeyframeHint(ReferenceReelDraft draft) => (IsCameraReel(draft) || IsCharacterCapture(draft)) && draft.Duration >= 10;
    public static string Label(ReferenceReelDraft draft) => Label(draft.Framing) +
        (CameraPreset(draft)?.UsesDirection == true ? $" · {draft.CameraDirection ?? ReelCameraDirection.Right}" : "");

    public static void SelectCameraPreset(ReferenceReelDraft draft, ReelFraming framing)
    {
        if (!IsCameraReel(draft)) throw new WorkspaceStoreException("Choose an environment or prop recipe.");
        var preset = CameraPresets(draft).SingleOrDefault(p => p.Framing == framing)
            ?? throw new WorkspaceStoreException(IsProp(draft) ? "Choose a prop camera preset." : "Choose an environment camera preset.");
        draft.Framing = framing;
        draft.Duration = preset.Seconds;
        draft.CameraDirection ??= ReelCameraDirection.Right;
        draft.CheckedInputs = null;
    }

    public static void ReuseDirections(ReferenceReelDraft target, ReferenceReelDraft source)
    {
        ValidateProfile(source);
        if (OwnerCategory(target) != OwnerCategory(source)) throw new WorkspaceStoreException("Choose directions for the same kind of asset.");
        target.PresetVersion = source.PresetVersion;
        target.Framing = source.Framing;
        target.CameraDirection = source.CameraDirection;
        target.CaptureArticulation = source.CaptureArticulation;
        target.CaptureCloseUp = source.CaptureCloseUp;
        target.Instructions = source.Instructions;
        target.CheckedInputs = null;
    }

    public static string Views(ReferenceReelDraft draft)
    {
        if (IsVoiceReference(draft)) return VoiceReferenceViews(draft);
        if (IsCharacterCapture(draft)) return CharacterCaptureViews(draft);
        if (CameraPreset(draft) is not { } preset) return Views(draft.Framing);
        if (IsProp(draft)) return PropViews(draft, preset);
        var frames = H3Policy.Frames(draft.Duration);
        var direction = (draft.CameraDirection ?? ReelCameraDirection.Right).ToString().ToLowerInvariant();
        var exitEdge = direction == "right" ? "left" : "right";
        var pace = draft.Duration <= preset.Seconds ? "at fast speed" : "at a steady speed spread across the allotted movement time";
        static string Seconds(int frames) => (frames / 24d).ToString("0.###", CultureInfo.InvariantCulture);
        static string Timestamp(int frames) => TimeSpan.FromSeconds(frames / 24d).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        var opening = (PictureCount(draft) > 0 ? "Use the composition in <Picture 1> for the opening view only. " : "Establish an opening view of the referenced environment. ") + "Keep the same physical environment as the viewpoint changes. ";
        if (preset.Framing == ReelFraming.EnvironmentHeldViews)
        {
            var views = new[] { "the opening view", $"the approximately 90° {direction} quarter-turn view", "the opposite view at approximately 180°",
                $"the remaining side at approximately 270° {direction} from the opening view" };
            var coverage = new[] { "Establish the opening composition.",
                $"Show the continuation beyond the opening image's {direction} edge, with a distinctly different composition.",
                "Show the space behind the opening camera, facing away from the opening composition.",
                "Show the remaining side of the room, completing coverage of four different directions." };
            return string.Join("\n\n", views.Select((view, i) => $"[Shot {i + 1}] " +
                (i == 0 ? opening : $"At {Timestamp(frames * i / 4)}, the camera cuts to {view}. ") +
                $"Hold a static wide view of {view} from the same camera position at unchanged eye level and focal length. " +
                $"{coverage[i]} Keep the room's physical geometry, furniture placement and lighting consistent across cuts while their screen positions change. No camera movement within this view."));
        }
        // Holds shrink with short authored durations; all timing remains on the output's frame grid.
        var finalHold = Math.Min(24, frames / 5);
        var pause = Math.Min(12, frames / 10);
        if (preset.Framing is ReelFraming.EnvironmentTurn or ReelFraming.EnvironmentHalfTurn)
        {
            var legs = preset.Framing == ReelFraming.EnvironmentTurn ? 4 : 2;
            var travel = frames - finalHold - pause * (legs - 1);
            List<string> phases = [$"[Shot 1] {opening}One continuous shot. The camera pans {direction} with large amplitude {pace} through approximately {legs * 90}°. " +
                $"It rotates horizontally from one fixed position, keeping eye level, horizon and focal length unchanged. Starting objects move across the image and leave through the {exitEdge} edge as new areas enter through the {direction} edge. No camera translation or orbit."];
            var coverage = new[] { $"The lens now faces the adjacent {direction}-hand continuation, approximately perpendicular to the opening direction.",
                "The lens now faces the space behind the opening camera, opposite the opening direction.",
                "The lens now faces the remaining side of the room, showing another distinct composition.",
                "Only after passing all three other sides does the lens return near the opening composition." };
            var start = 0;
            for (var i = 1; i <= legs; i++)
            {
                var stop = travel * i / legs + pause * (i - 1);
                var end = stop + (i == legs ? finalHold : pause);
                phases.Add($"From {Seconds(start)} to {Seconds(stop)} seconds, pan {direction} with large amplitude {pace} through the next approximately 90°, revealing the adjacent space. " +
                    $"At approximately {i * 90}° from the opening direction, hold the camera still from {Seconds(stop)} to {Seconds(end)} seconds so this view is sharp and readable. {coverage[i - 1]}");
                start = end;
            }
            phases.Add(legs == 4 ? "Finish near the opening composition, preserving all structures revealed during the turn." : "Finish looking toward the opposite side of the space.");
            return string.Join("\n", phases);
        }
        var move = preset.Framing switch
        {
            ReelFraming.EnvironmentPan => $"The camera pans {direction} with large amplitude {pace} through approximately 90°, rotating horizontally from a fixed position with a level horizon and unchanged focal length. " +
                $"Opening objects move across the composition and leave through the {exitEdge} edge while the previously out-of-frame {direction}-hand continuation enters the view. " +
                $"End facing approximately perpendicular to the starting direction, with the newly revealed part of the room dominating the final composition. No camera travel or orbit.",
            ReelFraming.EnvironmentPullBack => $"The camera pulls back with large amplitude {pace} along a plausible clear route. " +
                "Foreground objects become substantially smaller while more floor and surrounding room enter the frame. End in a distinctly wider view from farther away. Keep the viewing direction and focal length steady; move the camera rather than zooming out.",
            ReelFraming.EnvironmentApproach => $"The camera pushes in with large amplitude {pace} toward the feature named in the author's instructions along a plausible clear route. " +
                "The feature grows substantially in the frame as nearer surroundings pass out through the edges. End close enough to reveal new surface detail, looking toward the same destination. Stop before reaching it; do not turn around or pass through furniture or walls.",
            ReelFraming.EnvironmentArc => $"The camera performs an arc shot to the {direction} with large amplitude {pace}, travelling through approximately 90° around the stationary feature named in the author's instructions. " +
                "Turn the lens to keep that feature framed while its apparent angle changes substantially and the background shifts through strong parallax. End at a distinct side-on viewpoint showing a different face of the feature and its surroundings. Follow a plausible clear route; do not rotate the feature, spin the camera in place or pass through furniture or walls.",
            _ => "Follow the author's starting view, route and destination in one continuous camera movement, using the extent and speed they specify. " +
                "Show visible progress along that route through changes in perspective, occlusion and framing, arriving at the specified destination before the final hold. Fit the route to the available movement time along a plausible clear path through the space."
        };
        return $"[Shot 1] {opening}One continuous shot. Begin moving immediately. {move} " +
            $"Complete the move by {Seconds(frames - finalHold)} seconds, then hold the final view completely still until {Seconds(frames)} seconds for a sharp reference frame.";
    }
}
