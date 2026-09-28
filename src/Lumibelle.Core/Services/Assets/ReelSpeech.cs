using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed record ReelSpeechLanguage(string Code, string Name, string NativeName, bool UpstreamStable);
public sealed record ReelSpeechSuggestion(string Language, string CurrentLine, double RequestedSeconds,
    double GeneratedSeconds, ReelSpeechSettings Settings, ReelSpeechLength Length, string Text);

public static partial class ReelSpeech
{
    public const int CatalogueVersion = 1;
    public const string SupportSource = "https://github.com/MiniMax-AI/MiniMax-H3#system-overview";
    public const string SupportChecked = "2026-09-18";
    public const string RangeDelivery = "Speak in a natural conversational voice. Begin with mild recognition, give the question a curious inflection, " +
        "then become gently reassuring. Use restrained emphasis and natural pauses while keeping the same voice throughout. " +
        "Do not whisper, shout, sing or perform exaggerated emotions unless the author explicitly requests them.";
    public const string EnglishSceneInstructions = "Write scene, camera, delivery and use-guidance prose in English. The selected spoken language applies only to dialogue. " +
        "Keep the exact authored dialogue once in its original language; never translate, paraphrase, repeat or extend it to fill the clip. " +
        "Delivery instructions are not spoken words. A reference recording supplies voice identity, not the words or background sound to reproduce. " +
        "A recording may be in a different language; do not change the requested dialogue language to match it or promise cross-language voice fidelity. ";

    public static ReelSpeechLanguage? FindLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var text = language.Trim();
        var found = Languages.FirstOrDefault(l => string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l.NativeName, text, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Code, text, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return found;
        // Recognize ordinary BCP-47 input without rewriting the saved language label.
        var code = text.Split('-')[0];
        return text.Contains('-') ? Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) : null;
    }

    public static string SupportNotice(string? language) => FindLanguage(language) is { UpstreamStable: true }
        ? "Listed by upstream H3 as a stable dialogue language. This checkpoint and voice have not been verified."
        : "Experimental dialogue language: outside upstream H3's documented stable set. Custom languages are allowed; review the generated speech.";

    public static void Validate(ReferenceReelDraft draft)
    {
        if (draft.Speech is not { } speech) return;
        if (ReferenceReels.IsCameraReel(draft)) throw new WorkspaceStoreException("Environment and prop reels do not use speech presets.");
        if (speech.CatalogueVersion != CatalogueVersion || !Enum.IsDefined(speech.Preset) || !Enum.IsDefined(speech.Length))
            throw new WorkspaceStoreException("Choose a supported speech preset and passage length.");
    }

    public static double NominalSeconds(ReelSpeechLength length) => length switch
    {
        ReelSpeechLength.Short => 5, ReelSpeechLength.Medium => 8,
        ReelSpeechLength.Long => 10, ReelSpeechLength.Extended => 15,
        _ => throw new WorkspaceStoreException("Choose a concrete passage length.")
    };
    public static string LengthLabel(ReelSpeechLength length) => length == ReelSpeechLength.Automatic ? "Suggested for clip duration" :
        $"About {NominalSeconds(length).ToString("0", CultureInfo.InvariantCulture)} seconds";
    public static ReelSpeechLength RecommendedLength(double requestedSeconds)
    {
        var seconds = H3Policy.Seconds(requestedSeconds);
        return seconds >= 15 ? ReelSpeechLength.Extended : seconds >= 10 ? ReelSpeechLength.Long :
            seconds >= 8 ? ReelSpeechLength.Medium : ReelSpeechLength.Short;
    }

    public static ReelSpeechSuggestion? Suggest(ReferenceReelDraft draft)
    {
        Validate(draft);
        if (draft.VoiceMode == ReelVoiceMode.Silent || draft.Speech is not { Preset: ReelSpeechPreset.ConversationalRange } settings ||
            FindLanguage(draft.Language) is not { } language) return null;
        var seconds = H3Policy.Seconds(draft.Duration);
        // Very short authored clips remain supported; do not invent a fit for them.
        if (seconds < 5) return null;
        var length = settings.Length == ReelSpeechLength.Automatic ? RecommendedLength(draft.Duration) : settings.Length;
        return new(draft.Language, draft.Line, draft.Duration, seconds, settings, length,
            Passages[language.Code][(int)length - (int)ReelSpeechLength.Short]);
    }

    public static bool UseSuggestion(ReferenceReelDraft draft, ReelSpeechSuggestion expected)
    {
        // An old rendered suggestion must not overwrite newly typed text or a changed
        // language/duration. This comparison is local; it performs no inference.
        if (Suggest(draft) is not { } current || current != expected)
            throw new WorkspaceStoreException("The speech settings or dialogue changed. Review the updated suggestion before using it.");
        if (draft.Line == current.Text) return false;
        draft.Line = current.Text;
        draft.CheckedInputs = null;
        return true;
    }

    public static string DeliveryInstructions(ReferenceReelDraft draft)
    {
        Validate(draft);
        if (draft.VoiceMode == ReelVoiceMode.Silent || ReferenceReels.IsCameraReel(draft) ||
            draft.Speech is null && !ReferenceReels.IsVoiceReference(draft)) return "";
        var frames = H3Policy.Frames(draft.Duration);
        static string S(int frame) => (frame / 24d).ToString("0.###", CultureInfo.InvariantCulture);
        var range = draft.Speech?.Preset == ReelSpeechPreset.ConversationalRange ? RangeDelivery + " " : "";
        return range + $"Speak the exact dialogue in {draft.Language}. Leave a brief settling moment before speech and a natural ending; " +
            $"aim to speak between {S(6)} and {S(frames - 12)} seconds. This is intended pacing, not a measured speech duration. " +
            "Do not rush, truncate words, repeat the passage or add words to fill time. Keep voice identity consistent across any cuts. " +
            (draft.VoiceMode == ReelVoiceMode.ExistingRecording
                ? "Preserve the voice identity from <Audio 1>. Any voice description is delivery direction, not a replacement identity."
                : "Use the optional voice description to establish the intended voice and delivery.");
    }

    public static string CompositionInstructions(ReferenceReelDraft draft) => draft.VoiceMode == ReelVoiceMode.Silent
        ? "Write scene, camera and use-guidance prose in English. Ignore remembered speech settings and sample dialogue in Silent mode."
        : EnglishSceneInstructions + DeliveryInstructions(draft);

    public static string UseGuidance(ReferenceReelDraft draft) => DeliveryInstructions(draft).Length == 0 ? "" :
        $"Intended spoken language: {draft.Language}. Review correct words, pronunciation, natural rhythm and voice similarity separately. " +
        "This is intended voice-reference material, not verified output. Do not copy these sample words or delivery into later shots unless requested.";
}
