# Character reel speech

Speech is independent of the character framing preset. Scene and camera prose remain
English; `ReferenceReelDraft.Language` and `Line` are the exact spoken language label
and words. These fields are never translated or refilled when settings change.

## Workflow

In a character asset's Reference reels, use the existing New voice or Existing
recording mode. The new speech controls provide:

- **Spoken language:** eleven upstream stable languages, Swedish (experimental), and
  Other with a free-text language field. Existing custom labels and ISO/BCP-47 labels
  are retained; recognizing an alias does not rewrite the saved label.
- **Sample passage:** Custom text (the default for old recipes) or Conversational
  range. Every mode keeps Exact dialogue visible and editable.
- **Passage length:** Suggested for clip duration, or approximately 5, 8, 10 or 15
  seconds. Automatic suggestions use the actual H3 frame-derived duration. A longer
  manually chosen passage gets a warning, not a hard language or generation limit.
- **Use suggested passage:** the only action that replaces exact words. Language,
  duration, framing and length changes leave authored words and prompt texts intact.
  An outdated rendered suggestion is rejected if the words, language, duration or
  passage settings have since changed.

Use Compose pair / Revise pair after changing speech. The ordinary input fingerprint
marks the existing prompt for review, and stale Assist proposals cannot overwrite a
newer recipe. There is no additional LLM or speech-synthesis request to choose the
built-in words.

The catalogue has four independently authored native-script passages per language.
They request recognition, a question, restrained emphasis and a settled closing.
They are not measured recordings, native-speaker-reviewed material or claims of
phonetic balance. Spoken speed varies by language, voice, delivery and checkpoint.
Leave space to begin and finish; inspect words, pronunciation, rhythm and identity
separately. For clips below five generated seconds, use shorter custom wording.
Unsupported languages have no fallback passage: the application never substitutes
English under a different language label.

## Voice source and framing

**New voice:** the existing optional voice description sets intended register,
texture, accent and delivery. **Existing recording:** choose a recording and excerpt
using the existing controls; it supplies identity, and the optional description is
delivery direction. Import a sample into the existing voice library first. The
recording's words, background sound and language are not automatically inherited.
Cross-language voice matching is permitted, not guaranteed.

The opt-in **Voice reference** framing preset starts at 10 requested seconds and
uses a stationary, near-frontal head-and-shoulders view with an unobstructed mouth.
Selecting it enables speech when coming from Silent, preferring a remembered
recording when present. It selects Conversational range suggestions only when no
speech settings exist, without replacing existing dialogue. Existing explicit voice
source choices, pictures, aspect ratio, descriptions and prompt text are retained.

10 requested seconds still generate 243 frames at 24 fps (10.125 seconds). The
prompt targets a brief opening margin and natural ending, not a guaranteed speech
length. The preset does not claim side, rear or body coverage. Explicitly selecting
Silent still suppresses all speech and reference audio. Existing visual-capture
presets remain silent by default and keep remembered speech settings for later.

## Language capability labels

Source checked 2026-09-18:
https://github.com/MiniMax-AI/MiniMax-H3#system-overview

The upstream repository lists stable generated-dialogue support for Arabic, Chinese,
English, French, German, Italian, Japanese, Korean, Portuguese, Russian and Spanish,
and describes other languages as supported to varying degrees. Swedish is outside
that documented stable set. This is not a whitelist, a prompt-input language list,
a validation of a community/quantized checkpoint, or a claim about MiniMax Speech.

## Persistence and execution

`Speech` is an optional, null-omitted recipe property. Existing recipes do not acquire
it merely by being displayed. Existing enum values are unchanged; the voice framing
value is appended. Catalogue version 1 and the chosen passage setting are captured
with the recipe. Exact words are stored in `Line`, not regenerated from the catalogue
on retry or regeneration. Keep version 1 supported if introducing a future catalogue.

The same speech directions feed deterministic presets and the existing Assist pair
composer. The six-section format, exact `<d>[Language] words</d>` validation, speaker
mapping, prepared audio inputs, durable queue, per-candidate seeds and shared-workflow
submission path are unchanged. No credentials or audio bytes enter speech metadata.

## Tests

`ReelSpeechTests`, `ReelSpeechComponentTests`, and `ReelSpeechPersistenceTests` cover
the catalogue, frame-derived suggestions, explicit application, stale proposals,
legacy serialization, custom languages, silence, voice identity, captured workflows
and storage. A real recording/checkpoint test is still required for speech quality
and timing; a passing structural test is not evidence of either.
