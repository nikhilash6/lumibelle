using lumibelle.Models;

namespace lumibelle.Services.Production;

// A reference-picker action, not a resolver or generation-time default. Only the
// pending shot is changed; reopening a saved shot or rebuilding a cache does nothing.
// Handled also means an existing voice was preserved: callers must not run a
// fallback default initializer that could overwrite it.
public sealed record ReelVoiceDefaultResult(bool Handled, string? Notice = null);

public static class ReelVoiceDefaults
{
    public static ReelVoiceDefaultResult Apply(Shot shot, ShotVideoBinding binding, AssetLibrary library)
    {
        if (binding.EffectiveVisuals == ReelVisuals.None || !shot.Videos.Any(v => ReferenceEquals(v, binding))) return new(false);
        var owner = CharacterVoices.Owner(binding, library);
        var asset = library.Assets.FirstOrDefault(a => a.Id == owner && a.Category == AssetCategory.Character);
        if (asset is null) return new(false);

        var existing = shot.CharacterVoices?.FirstOrDefault(c => c.AssetId == asset.Id);
        // This also preserves a selected default, an unavailable selected recording,
        // vocalizations, and explicit None. A second look never selects a second voice.
        if (existing is not null && existing.Source != CharacterVoiceSource.Unselected) return new(true);
        var choice = ShotCopy.Of(existing ?? CharacterVoices.Initial(shot, asset, library));
        bool SameSpeaker(string? speaker) => !string.IsNullOrWhiteSpace(choice.Speaker) &&
            string.Equals(speaker?.Trim(), choice.Speaker.Trim(), StringComparison.OrdinalIgnoreCase);
        // Never replace a legacy voice or remove a different character's input while
        // guessing ownership from its speaker label. Explicit controls can resolve it.
        if (shot.Voices.Any(v => (v.CharacterAssetId ?? v.AssetId) == asset.Id || SameSpeaker(v.Speaker)) ||
            shot.Videos.Any(v => v.UseSoundtrack && (CharacterVoices.Owner(v, library) == asset.Id || SameSpeaker(v.Speaker)))) return new(true);

        var source = library.Reels.FirstOrDefault(r => r.AssetId == asset.Id && r.Media == binding.Media);
        if (source is null || source.Generation?.Recipe.VoiceMode == ReelVoiceMode.Silent) return new(false);
        // Keep the existing policy for a shot without dialogue, or a character known
        // not to speak in it. Selecting a visual reference must not introduce speech.
        if (choice.Source == CharacterVoiceSource.None) return new(false);

        choice.FromDefault = false;
        choice.Recording = null; choice.ReelBindingId = null; choice.ReelMediaId = null; choice.Excerpt = null;
        string notice;
        if (source.Generation?.Recipe is { VoiceMode: ReelVoiceMode.ExistingRecording } recipe)
        {
            if (recipe.Voice is not { } original)
            {
                // Do not quietly choose the character default or generated imitation
                // when provenance says this reel was made with an external recording.
                choice.Source = CharacterVoiceSource.Unselected; choice.SourceName = "";
                CharacterVoices.Set(shot, choice, library);
                return new(true, $"{asset.Name}: this reel has no saved original recording selection. Choose a recording, reel audio or None under Character voices.");
            }
            var recording = library.Voices.FirstOrDefault(v => v.Matches(original));
            choice.Source = CharacterVoiceSource.Recording;
            choice.SourceName = recording?.Name ?? $"Original recording for {source.Name} (unavailable)";
            // Use the exact captured excerpt, not the recording's current default
            // start/duration. Set remaps it to the current shot's character/speaker.
            choice.Recording = original with { };
            notice = recording is null
                ? $"{asset.Name}: the reel's original recording is unavailable. Restore it in Assets or explicitly choose another voice; the generated soundtrack was not substituted."
                : $"{asset.Name}: automatically selected the reel's original recording, {recording.Name}, with its captured excerpt.";
        }
        else
        {
            // A generated new voice (or an imported character reel) has no external
            // source recording. Its own soundtrack remains an ordinary audio input.
            if (!source.Media.HasAudio) return new(false);
            choice.Source = CharacterVoiceSource.Reel;
            choice.SourceName = source.Name;
            choice.ReelBindingId = binding.Id; choice.ReelMediaId = binding.Media.Id;
            choice.Excerpt = ResolvedReferences.Excerpt(binding) with { };
            notice = $"{asset.Name}: automatically selected this reel's audio as a separate voice reference.";
        }
        CharacterVoices.Set(shot, choice, library);
        if (!choice.SpeakerConfirmed) notice += " Choose its dialogue speaker or vocalizations under Character voices; no speaker was guessed.";
        return new(true, notice + " Apply changes, then review or recompose the prompt's Audio mapping.");
    }

    // The recording belongs to the character, not to a Video input. Include it in
    // the reel card/snippet without duplicating its audio input for every look.
    public static ResolvedAudio? Audio(Shot shot, ShotVideoBinding binding, AssetLibrary library)
    {
        var audio = ResolvedReferences.For(shot).Audio;
        var owner = CharacterVoices.Owner(binding, library);
        return audio.FirstOrDefault(a => a.Reel?.Id == binding.Id) ??
            (owner is { } id ? audio.FirstOrDefault(a => a.CharacterAssetId == id) : null);
    }

    public static string Summary(Shot shot, ShotVideoBinding binding, AssetLibrary library) =>
        Audio(shot, binding, library) is { } audio
            ? $"Voice: {audio.SourceName ?? binding.Name} · <Audio {audio.Number}>"
            : "Visual reference only";

    public static string Usage(Shot shot, ShotVideoBinding binding, AssetLibrary library)
    {
        var audio = Audio(shot, binding, library);
        var labels = ResolvedReferences.For(shot).Label(binding);
        if (audio is not null && !labels.Contains($"<Audio {audio.Number}>", StringComparison.Ordinal))
            labels += (labels.Length == 0 ? "" : " · ") + $"<Audio {audio.Number}>";
        var text = $"{labels}: {binding.Description}";
        text += audio is null ? "\nSoundtrack is disabled; use visual information only." :
            $"\n<Audio {audio.Number}> supplies voice identity" +
            (string.IsNullOrWhiteSpace(audio.Speaker) ? audio.CharacterVoice is not null ? " for vocalizations only." : "." : $" for {audio.Speaker}.") +
            " Use the target shot's dialogue, not the recording's words or background sound.";
        if (binding.EffectiveVisuals == ReelVisuals.RefMod) text += "\n" + ReelRefMods.UseGuidance;
        return text;
    }
}
