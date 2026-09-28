using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public const string PropProfile = "prop-reel-v1";
    public const string PropUseGuidance = "Visual prop reference intended to establish the object's form, proportions, materials, colours and details from several sides, including plausible sides completed beyond the source pictures. These are intended uses, not verified output. Reuse the prop in the target shot's own placement, scale, framing, lighting and action; do not copy the reel's camera orbit, turntable rotation, backdrop or timing. Explicit target instructions may change its condition or surroundings.";
    public static bool IsProp(ReferenceReelDraft draft) => draft.PresetVersion == PropProfile;

    public static IReadOnlyList<CameraReelPreset> PropPresets { get; } = Array.AsReadOnly<CameraReelPreset>([
        new(ReelFraming.PropOrbit, "360° orbit", 15, "Circle the stationary prop at a steady distance and height, briefly holding its front, side, rear and other side, then return to the front.", true),
        new(ReelFraming.PropHalfOrbit, "Half orbit", 8, "Travel halfway around the stationary prop from its front past one side, then hold on its rear.", true),
        new(ReelFraming.PropTurntable, "Turntable", 12, "Keep the camera still while the prop makes one full turn on a concealed turntable against a plain backdrop, then hold its front.", true),
        new(ReelFraming.PropHeldViews, "Held angles", 12, "Cut between front, three-quarter, side and rear views of the prop from the same distance and height."),
        new(ReelFraming.PropRise, "Rise to top view", 6, "Crane up from eye level to a high angle that shows the prop's top surface, keeping it centred, then hold."),
        new(ReelFraming.PropDetail, "Detail pass", 6, "Glide close along the prop's surfaces toward a named detail, or its most distinctive one, then hold."),
        new(ReelFraming.PropCustom, "Custom move", 10, "Follow your camera move around the prop from the starting view to the specified end view, then hold.",
            RequiredInstructions: "Describe the camera move in Instructions: starting view, path around the prop and end view.")
    ]);

    private static string PropViews(ReferenceReelDraft draft, CameraReelPreset preset)
    {
        var frames = H3Policy.Frames(draft.Duration);
        var direction = (draft.CameraDirection ?? ReelCameraDirection.Right).ToString().ToLowerInvariant();
        var pace = draft.Duration <= preset.Seconds ? "at fast speed" : "at a steady speed spread across the allotted movement time";
        var opening = (PictureCount(draft) > 0 ? "Use <Picture 1> to establish the prop's opening view only. " : "Establish a clear front view of the referenced prop. ") +
            "Keep the same prop, entirely in frame with a small margin, as the viewpoint changes. ";
        if (preset.Framing == ReelFraming.PropHeldViews)
        {
            var views = new[] { "the front view", "a three-quarter view", "a side view in profile", "the rear view" };
            return string.Join("\n\n", views.Select((view, i) => $"[Shot {i + 1}] " +
                (i == 0 ? opening : $"At {Timestamp(frames * i / 4)}, the camera cuts to {view}. ") +
                $"Hold a static view of {view} from the same distance and camera height, with the prop centred and entirely in frame. " +
                "Keep its proportions, materials and details consistent across cuts. The prop does not move. No camera movement within this view."));
        }
        // Holds shrink with short authored durations; all timing remains on the output's frame grid.
        var finalHold = Math.Min(24, frames / 5);
        var pause = Math.Min(12, frames / 10);
        if (preset.Framing is ReelFraming.PropOrbit or ReelFraming.PropHalfOrbit)
        {
            var legs = preset.Framing == ReelFraming.PropOrbit ? 4 : 2;
            var travel = frames - finalHold - pause * (legs - 1);
            List<string> phases = [$"[Shot 1] {opening}One continuous shot. The camera orbits {direction} around the stationary prop with large amplitude {pace} through approximately {legs * 90}°. " +
                "It travels at a constant distance and height, turning to keep the prop centred while the background shifts behind it through parallax. The prop itself never rotates or moves."];
            var coverage = new[] { "The camera now shows one side of the prop in profile.", "The camera now shows the prop's rear, opposite the opening view.",
                "The camera now shows the prop's remaining side in profile.", "Only after passing both sides and the rear does the camera return to the front view." };
            var start = 0;
            for (var i = 1; i <= legs; i++)
            {
                var stop = travel * i / legs + pause * (i - 1);
                var end = stop + (i == legs ? finalHold : pause);
                phases.Add($"From {Seconds(start)} to {Seconds(stop)} seconds, orbit {direction} with large amplitude {pace} through the next approximately 90° around the prop. " +
                    $"At approximately {i * 90}° from the opening view, hold the camera still from {Seconds(stop)} to {Seconds(end)} seconds so this side is sharp and readable. {coverage[i - 1]}");
                start = end;
            }
            phases.Add(legs == 4 ? "Finish at the front view, preserving every detail revealed during the orbit." : "Finish facing the prop's rear.");
            return string.Join("\n", phases);
        }
        var move = preset.Framing switch
        {
            ReelFraming.PropTurntable => $"The camera stays locked off at a fixed distance and height while the prop turns {direction} on a concealed turntable {pace} through one full 360° rotation, " +
                "presenting its front, one side, its rear, the other side and its front again. It rotates about its own vertical axis without wobbling, sliding or changing shape. The backdrop and lighting stay fixed.",
            ReelFraming.PropRise => $"The camera cranes up with large amplitude {pace} from eye level to a high angle looking down at approximately 60°, keeping the prop centred and at a similar distance. " +
                "Its top surface becomes clearly visible while the surface it stands on fills more of the background. Move the camera rather than zooming.",
            ReelFraming.PropDetail => $"The camera glides close {pace} along the prop's surfaces toward the detail named in the author's instructions, or otherwise its most distinctive detail. " +
                "Material texture, joins, markings and wear become clearly readable; the whole prop may leave the frame. Keep the detail in sharp focus and never touch or pass through the prop.",
            _ => "Follow the author's starting view, path around the prop and end view in one continuous camera move, using the extent and speed they specify. " +
                "Show visible progress through changes in the prop's apparent angle and the background, arriving at the specified end view before the final hold. The prop stays stationary unless the author says otherwise."
        };
        return $"[Shot 1] {opening}One continuous shot. Begin moving immediately. {move} " +
            $"Complete the move by {Seconds(frames - finalHold)} seconds, then hold the final view completely still until {Seconds(frames)} seconds for a sharp reference frame.";

        static string Seconds(int frames) => (frames / 24d).ToString("0.###", CultureInfo.InvariantCulture);
        static string Timestamp(int frames) => TimeSpan.FromSeconds(frames / 24d).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static ReelPromptPair PropPreset(ReferenceReelDraft d, AssetLibrary? library)
    {
        ValidateComposition(d);
        if (!HasVisualReferences(d)) throw new WorkspaceStoreException("Select visual references before building the preset.");
        var seconds = H3Policy.Seconds(d.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        var owner = library?.Assets.FirstOrDefault(a => a.Id == d.AssetId);
        var details = PictureGuidance(d, library);
        var references = string.Join("\n", details.Select((g, n) => $"<Picture {n + 1}>: {(n == 0 ? "Establishes the prop's opening view only." : "An additional view of the same prop.")} Preserve its form, proportions, materials and details. {g}")) + VideoDefinitions(d);
        var structure = d.Framing switch
        {
            ReelFraming.PropHeldViews => "Four held angles of the prop within one generated reel, joined by cuts.",
            ReelFraming.PropTurntable => "One continuous locked-off shot while the prop turns.",
            _ => "One continuous camera move around the stationary prop, with pauses for readable views."
        };
        var stage = d.Framing == ReelFraming.PropTurntable
            ? "The prop stands on a concealed turntable against a plain, evenly lit neutral backdrop with soft shadows."
            : "The prop stands still on a stable surface. Keep its surroundings consistent with the pictures, or use a plain, evenly lit neutral backdrop when they show none.";
        var instructions = string.IsNullOrWhiteSpace(d.Instructions) ? "" : $"\nAuthor directions take precedence over conflicting parts of the default camera plan; follow one coherent plan:\n{d.Instructions}";
        var prompt = $"subject_definitions:\n<Subject 1> is {owner?.Name ?? d.Name}, the same prop throughout.\n{references}\n\nsummary:\nA silent {d.Aspect} prop reference reel lasting {seconds} seconds, showing <Subject 1> from several sides for later shots from different angles.\n\nretention_analysis:\n<Subject 1>: fully_preserved. Preserve the prop's shape, proportions, materials, colours, markings and construction details. Complete unseen sides plausibly as new design consistent with the visible ones, and keep revealed details stable. {owner?.Description} {owner?.PreservationGuidance}\n\ndetailed_description:\nExactly {seconds} seconds total. {structure} {stage} No people, hands or unrelated activity. Preserve the source visual style. Keep exposure stable and the prop in sharp focus.\n\n{Views(d)}{instructions}\n\noverall_soundscape:\nSilent reference. No speech, ambient sound or audible soundtrack.\n\nnon_diegetic_music:\nNo non-diegetic music.";
        var pair = new ReelPromptPair(prompt, PropUseGuidance + "\nIntended prop features: " + string.Join("\n", new[] { owner?.Description, owner?.PreservationGuidance }.Concat(details).Concat(VideoGuidance(d)).Where(t => !string.IsNullOrWhiteSpace(t))));
        ValidatePair(pair, d);
        return pair;
    }

    private static List<ChatMessage> PropMessages(ReelCompositionRequest request, IReadOnlyList<byte[]> images, IReadOnlyList<RefModInspectionFrame> modFrames)
    {
        ValidateComposition(request.Draft);
        var rules = "Compose a prop reference reel. Return ordinary JSON with exactly prompt and useGuidance strings, including both when revising. " +
            "Use six headings with colons, in order: subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music. " +
            "Define the prop as <Subject 1>. Reference every selected <Picture N> and <Video N> in order. If present, the first picture establishes the prop's opening view only; additional pictures show the same object. " +
            "Inspect only the supplied cropped still images. Asset notes and image guidance are author direction. Never claim video or audio inspection. " +
            "Preserve the prop's visible shape, proportions, materials, colours, markings and construction details. Complete unseen sides plausibly; they are new design consistent with the visible ones, not a verified reconstruction. Keep revealed details stable throughout. " +
            "All requested coverage must be generated within this one reel; never propose chaining separate generations. Use the supplied camera plan and timing as a starting point. Explicit author instructions take precedence: resolve conflicts into one coherent plan rather than appending contradictory moves. " +
            "Write camera motion as natural English within the shot, stating movement type, amplitude and speed separately. Retain the plan's 'with large amplitude' and speed wording unless explicit author instructions change them. Do not soften broad coverage into a modest adjustment or a slightly different view. " +
            "An orbit travels around the stationary prop at a constant distance and height, keeping it centred while its apparent angle and the background change through parallax; the prop never rotates. A turntable keeps the camera locked off while the prop rotates about its own vertical axis against a plain backdrop. " +
            "Describe which sides of the prop each held view shows and how the ending view differs from the opening. Keep the whole prop in frame except during a detail pass. " +
            "Held angles uses four distinctly different views of the prop from the same distance and height, with cuts at the supplied times. Other presets use one continuous shot without cuts. Custom moves follow the author's named start, path and end view. " +
            "The prop is never handled, touched or moved by anyone. No people, hands or unrelated activity. Keep its surroundings consistent with the supplied pictures, or use a plain, evenly lit neutral backdrop when they show none. Preserve the source visual style, stable exposure and sharp focus on the prop. " +
            "Retain the supplied settling holds, scaling pacing to the generated duration and explicit author instructions. Number views consecutively starting with [Shot 1] in detailed_description. The first shot has no cut timestamp; subsequent cuts use At MM:SS.mmm. State the supplied generated duration exactly. " +
            "This reel is silent: no dialogue, speaker labels, audio references, ambience or music. " + SoundSectionInstructions +
            "Use guidance must describe the prop's intended form, materials and recognizable details, including newly completed sides. It must be independent of reference numbering and clear that these are intended uses, not verified output. " +
            "Later shots place the prop with their own position, scale, framing, lighting, camera motion and action; do not propagate this reel's orbit, rotation, backdrop or timing. Explicit target-shot instructions may change its condition or surroundings.";
        // Keep the historical request wire shape, but present the captured asset
        // notes under a neutral name to the prop composer.
        var asset = request.Character;
        List<AIContent> content = [new TextContent(JsonSerializer.Serialize(new {
            draft = request.Draft, prop = new { asset.AssetId, Name = asset.AssetName, VisualNotes = asset.IdentityNotes, PreservationGuidance = asset.IdentityGuidance },
            images = request.Images, imageGuidance = request.ImageGuidance, pictures = PictureGuidance(request.Draft, null),
            videoReferences = VideoContext(request.Draft, images.Count, modFrames), presetViews = Views(request.Draft), generatedSeconds = H3Policy.Seconds(request.Draft.Duration)
        }, AtomicJsonFile.Options))];
        foreach (var image in images) content.Add(new DataContent(image, "image/png"));
        return [new(ChatRole.System, rules), new(ChatRole.User, content)];
    }
}
