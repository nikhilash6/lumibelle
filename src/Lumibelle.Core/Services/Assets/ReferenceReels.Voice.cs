using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public static ReelPromptPair Preset(ReferenceReelDraft d, AssetLibrary? library = null)
    {
        if (IsVoiceReference(d)) return VoiceReferencePreset(d, library);
        var pair = PresetCore(d, library);
        var delivery = ReelSpeech.DeliveryInstructions(d);
        if (delivery.Length == 0 || pair.Prompt.Length == 0) return pair;
        // Decorate the same deterministic visual plan for every speaking character
        // preset. Never derive or replace exact dialogue from the current catalogue.
        var boundary = pair.Prompt.IndexOf("\n\noverall_soundscape:", StringComparison.Ordinal);
        if (boundary < 0) throw new WorkspaceStoreException("The reel prompt has no soundscape section.");
        pair = pair with { Prompt = pair.Prompt.Insert(boundary, "\n\n" + delivery),
            UseGuidance = pair.UseGuidance + "\n" + ReelSpeech.UseGuidance(d) };
        ValidatePair(pair, d);
        return pair;
    }

    public static bool IsVoiceReference(ReferenceReelDraft draft) =>
        draft.PresetVersion == Profile && draft.Framing == ReelFraming.CharacterVoiceReference;

    private static string VoiceReferenceViews(ReferenceReelDraft draft)
    {
        var seconds = H3Policy.Seconds(draft.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        return $"[Shot 1] One continuous locked-off near-frontal head-and-shoulders view from 0 to {seconds} seconds. " +
            "Keep the full head, jaw and mouth unobstructed, with stable soft lighting and natural skin detail. " +
            "The person remains comfortably still, looking near the lens, with restrained natural expressions and breathing. " +
            "No camera orbit, profile cut, full-body tour or required limb movement. " +
            (draft.VoiceMode == ReelVoiceMode.Silent ? "No speech or vocalization." :
                "Mouth movements follow the exact spoken dialogue; no exaggerated acting or additional words.");
    }

    private const string VoiceReferenceInstructions =
        "This is an opt-in voice-reference capture, not one of the silent body-coverage studies. " +
        "Use the supplied single near-frontal head-and-shoulders view, with a visible mouth and restrained natural expression. " +
        "No required orbit, profile cuts or articulation. Do not claim rear, profile or full-body coverage. " +
        "Follow the chosen VoiceMode; an explicit Silent choice still produces no dialogue. " +
        "Voice descriptions define a new voice, but only direct delivery when an existing recording supplies identity. " +
        "Use guidance must distinguish intended vocal identity from verified output and ask the author to inspect the actual speech before reuse. ";

    private static ReelPromptPair VoiceReferencePreset(ReferenceReelDraft d, AssetLibrary? library)
    {
        ValidateComposition(d);
        if (!HasVisualReferences(d)) throw new WorkspaceStoreException("Select visual references before building the preset.");
        var seconds = H3Policy.Seconds(d.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        var refs = string.Join("\n", PictureGuidance(d, library).Select((g, i) =>
            $"<Picture {i + 1}>: partially_preserved. " + (string.IsNullOrWhiteSpace(g) ? "Use the visible identity of the same person." : g) +
            " Preserve only the indicated appearance, not source pose, crop, background or lighting.")) + VideoDefinitions(d);
        var audio = d.VoiceMode == ReelVoiceMode.ExistingRecording
            ? $"\n<Audio 1> supplies voice identity for {d.Speaker} (S1), not its words or background sound." : "";
        var speech = d.VoiceMode == ReelVoiceMode.Silent ? "No dialogue or vocal performance." :
            $"{ReelSpeech.DeliveryInstructions(d)}\n{d.Speaker} (S1) says: <d>[{d.Language}] {d.Line}</d>";
        if (d.VoiceMode != ReelVoiceMode.Silent && !string.IsNullOrWhiteSpace(d.VoiceDescription))
            speech += d.VoiceMode == ReelVoiceMode.NewVoice ? $"\nIntended voice: {d.VoiceDescription}" :
                $"\nPreserve the voice identity from <Audio 1>. Delivery direction: {d.VoiceDescription}";
        var name = string.IsNullOrWhiteSpace(d.Speaker) ? "the person in the selected visual references" : d.Speaker;
        var prompt = $"subject_definitions:\n<Subject 1> is {name}, the same single person throughout.\n{refs}{audio}\n\n" +
            $"summary:\nA {d.Aspect} head-and-shoulders voice-reference reel lasting {seconds} seconds.\n\n" +
            "retention_analysis:\nPreserve facial structure, hairstyle, selected accessories and one consistent outfit. " +
            "The studio, lighting and delivery are newly_generated direction, not evidence from the visual references.\n\n" +
            $"detailed_description:\nExactly {seconds} seconds total. A plain light-neutral studio with even soft lighting; no other people, props or visible text. " +
            $"Preserve observed proportions and natural skin texture without glamour retouching.\n\n{VoiceReferenceViews(d)}\n\n{speech}\n\n" +
            "overall_soundscape:\n" + (d.VoiceMode == ReelVoiceMode.Silent
                ? "Silent reference. No speech, ambient sound or audible soundtrack."
                : "Only the specified clean natural voice and faint breathing. No music, additional speakers or distracting ambience.") +
            "\n\nnon_diegetic_music:\nNo non-diegetic music.";
        var guidance = $"Near-frontal head-and-shoulders reference of {name}; no intended profile, rear or full-body coverage. " +
            "These are intended uses, not verified output. Inspect the generated face and mouth movements. " +
            (d.VoiceMode == ReelVoiceMode.Silent ? "Visual reference only; no voice reference was requested. " :
                "When its soundtrack is enabled, use it for voice identity and natural timbre. " + ReelSpeech.UseGuidance(d) + " ") +
            "Do not carry the studio, framing, expressions, timing, background sound or sample words into later shots. Follow the target shot's action and dialogue.";
        var pair = new ReelPromptPair(prompt, guidance);
        ValidatePair(pair, d);
        return pair;
    }
}
