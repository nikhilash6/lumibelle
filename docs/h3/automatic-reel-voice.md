# Automatic voice selection for reel references

Adding a character reel in **Shots → Manage references** now also chooses its voice when that character has no selected voice. Switching an existing reel to **Selected-keyframe RefMod** or explicitly attaching a new RefMod reference uses the same rule. The voice remains an ordinary Audio input; it is never encoded into the visual RefMod.

## Source choice

- A generated reel made with **Existing recording** supplies the original recording and the exact start/duration captured in that reel's generation recipe. It does not use the generated reel's imitation or the recording's subsequently edited default excerpt. The current shot's character and speaker replace the source recipe's speaker label.
- A **New voice** reel, or an imported character reel without generation provenance, supplies its own soundtrack when present. The selected reel audio excerpt is retained.
- A character with an existing selected recording, reel voice, default, vocalizations-only choice, or **None** keeps that choice. Unselected can be filled. Existing legacy voice/soundtrack inputs are preserved, including when a speaker label is already in use.
- Silent source reels do not automatically supply audio. The existing no-dialogue/non-speaking-character defaults remain unchanged. Environment references and the visual reference picker inside reel authoring do not use this automatic character-voice rule; reel recipes choose voice separately for all visual modes.

This is a pending reference edit, not a new generation-time resolver. Opening a saved shot, copying another shot's references, preparing previews, cache repair, and retrying an already-captured request do not silently select different audio. There is no retrospective migration of existing RefMods. For an existing visual-only RefMod with no voice choice, switching its visual mode to Keyframes and back to RefMod invokes the rule; an explicit None is still respected.

## Visible and editable

The reference card shows the associated character voice and its actual `<Audio N>` label, even when the recording is independent of the reel soundtrack. Copy usage snippet includes that Audio label. Multiple visual reels for the same character refer to the same voice rather than creating one audio input per look.

The automatic-selection notice identifies the chosen source. **Character voices** retains the existing preview, source selector, excerpt controls and speaker mapping. Choose **None** there to opt out. If the character name cannot be mapped unambiguously to the current dialogue, the voice source is selected but the existing validation still requires explicit speaker/vocalization confirmation.

Use **Apply changes** to retain the voice and visual reference together; Cancel leaves the saved shot untouched. Adding an audio reference changes the prompt input contract: review or recompose the prompt so the `<Audio N>` reference and speaker mapping are present. Source dialogue is not copied to the target shot.

## Missing media and limits

A missing original recording is shown as unavailable, not silently replaced by the generated reel audio or a different default. Restore it in Assets or explicitly choose another voice. The normal Apply and generation checks reject unavailable or invalid recordings/excerpts.

The existing three-audio-input, total-duration, distinct-voice and per-speaker checks still apply. Automatic selection does not bypass limits or silently discard another voice. Adjust excerpts or choose None when necessary.

## Verification

Run the new policy, component and workflow/composer tests, then the existing reference/voice tests:

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~AutomaticReelVoice|FullyQualifiedName~CharacterVoice|FullyQualifiedName~UnifiedReferenceEditor|FullyQualifiedName~RefMod"
```

The new bUnit tests stub the RefMod builder and storage/JavaScript services; they do not exercise ComfyUI or generate audio. Perform an application smoke test with both an original-recording reel and a generated-new-voice reel: add the reel, select RefMod, check the separate Audio mapping, apply/recompose, and inspect the actual submitted workflow. No acoustic similarity or GPU result is asserted by these source changes.
