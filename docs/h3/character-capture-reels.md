# Character capture reels

## All angles → face

In a character asset's Reference reels workspace, open Assist and choose **All angles → face** under **Framing preset**. Selecting it sets **10 requested seconds**, **Arms**, **Cut**, and the existing **Silent** voice mode. It does not change the selected pictures, aspect ratio, instructions or authored prompt/use-guidance text. Compose or revise the pair before generating. The existing Requested seconds field remains in the output footer.

This is an opt-in preset, not a migration of every character reel. Existing five-second recipes, framing presets, environment presets, saved generations and captured jobs keep their settings. New ordinary character recipes still start with the previous defaults.

The attached reference-capture brief is the design basis: first establish the front, use the camera to show both profiles and a genuine rear view, perform one small movement only after the camera stops, and finish on a settled face close-up. The vision composer is asked to use the actual supplied images, not invent observations.

## Requested duration versus generated duration

Lumibelle's existing H3 frame grid rounds a 10-second request to **243 frames at 24 fps: 10.125 seconds**. This feature does not change that global policy, trim the result afterwards, or submit a 240-frame latent outside the application's existing frame grid. The prompt and capture timings use the generated frame count.

Default Arms + Cut plan:

| Interval | Intended capture |
| --- | --- |
| 0–0.250 s | Brief frontal full-body hold. |
| 0.250–6.125 s | Camera travels a complete horizontal circle: front, anatomical right profile, back, anatomical left profile, front, through the intervening three-quarter views. |
| 6.125–8.125 s | Camera stops at the front; raise and lower both arms. |
| 8.125–10.125 s | One cut to a stationary frontal face close-up. |

The person remains stationary during the orbit, with head and gaze toward the original front, rather than following the camera. Intermediate profile and rear-view milestones are placed on the output frame grid. They express prompting intent, not guaranteed camera control.

**Movement** can be None, Arms or Knee. None gives the extra two seconds back to angular coverage. Knee replaces the arm action with a small foot lift and return; it does not add a second movement. **Push-in** replaces the final cut with one second of approach and one settled final second, keeping one continuous shot.

At **5 to less than 8 requested seconds**, the preset switches to five approximately equal held views with clean cuts: front, right profile, back, left profile and face. It omits articulation and push-in, while retaining those settings for a later longer duration. This is principal-view coverage, not a continuous orbit. At **8 to less than 9 requested seconds**, use an orbit and the final two-second close-up, without articulation. At **9–15 requested seconds**, articulation is available. Longer durations extend the opening coverage; the close-up and optional articulation still receive two generated seconds each. Preset composition requires at least five requested seconds; use Custom for a shorter authored plan.

## Pictures and Assist

All selected pictures and reel keyframes are observations of one subject. Assist receives the actual numbered picture list and the same duration-aware view plan used by the UI and deterministic preset helper. It is instructed to choose a suitable face reference, a minimally foreshortened body reference and one complete outfit, then describe how each picture contributes. It must not merge incompatible outfits, infer identity or demographics from photos, apply beautification, or mistake photographs for successive animation frames.

Use existing picture guidance or Instructions for explicit overrides, for example: `Use Picture 1 for the face and Picture 2 for body proportions and the outfit.` There are no new reference-number selectors that could become stale when pictures are reordered. The vision composer, not the deterministic template helper, resolves these free-form instructions and image-specific details.

Retained appearance is distinguished from new studio lighting, movement and conservative completion of unseen angles. Review the generated rear view, both profiles, proportions, accessories and outfit continuity before accepting the reel. A plausible generated unseen angle is not new evidence about the person.

Assist still returns the application's JSON pair, `prompt` and `useGuidance`. The generation prompt keeps the six H3 section headings; no additional top-level `id` field is introduced. Numbered shots correspond to real cuts, not successive phases of a continuous orbit.

## Voice and reuse

The source brief suggests quiet studio ambience without dialogue. This implementation deliberately maps the preset to Lumibelle's existing **Silent** mode, which means no audible soundtrack, rather than adding another audio mode. Selecting the preset preserves any remembered sample line, voice description and recording selection. Explicitly choosing New voice or Existing recording afterwards retains those modes' existing exact-dialogue and voice-mapping rules; the default visual capture does not force speech.

Reuse directions copies the capture movement and close-up settings together with the framing and Instructions. It does not change the destination's pictures, voice, duration or prompt text. Capture presets with no custom Instructions can also be selected for reuse. Settings participate in recipe copies and prompt-review fingerprints, so a delayed Assist result cannot automatically overwrite a recipe whose duration or capture options have since changed.

For coverage of a long reel in shorter target shots, select keyframes across the reel. Direct video references use only the start, up to the generated target shot's length; they do not necessarily include the late face close-up.

## Compatibility and checks

`CharacterCapture` is appended to the framing enum. The new nullable recipe fields `captureArticulation` and `captureCloseUp` are omitted when absent, leaving old serialized recipe fingerprints unchanged. No persisted version bump, runtime dependency or queue change is required. Older application versions are not expected to understand newly saved CharacterCapture recipes; avoid downgrading a workspace containing those recipes.

Tests are in `CharacterReelCaptureTests.cs` and `CharacterReelCaptureComponentTests.cs`. They cover selection defaults, old wire shape, option copies and stale-result guards, duration thresholds, cut versus push-in, voice modes, UI events, and 243-frame single/shared H3 workflow construction. Run them and the existing reel suite:

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj --filter "FullyQualifiedName~Reel|FullyQualifiedName~CharacterCaptureSettings"
```

Before release, verify the full Razor build and a real ten-second generation. A successful prompt-contract test cannot guarantee image-model identity, camera orbit or timing adherence.

## Focused capture variants

Six additional, separately selectable character modes are documented in [Character reference capture variants](character-capture-variants.md): identity-first neutral turntable, silhouette reveal, pose-expansion, bend/waist/drape, sitting-to-standing and face-priority. They keep the original All angles → face option and do not change its optional arm/knee movement or cut/push-in controls.
