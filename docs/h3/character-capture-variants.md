# Character reference capture variants

The character reel **Framing preset** selector now offers six focused capture modes alongside **All angles → face**. They implement the six variations in the supplied likeness-recovery brief. They are separate authoring plans, not evidence that a particular movement improves model accuracy.

## Modes

| Mode | Default requested seconds | Minimum for preset composition | Intended sequence |
| --- | ---: | ---: | --- |
| Identity-first neutral turntable | 8 | 5 | Front hold, one complete camera orbit around a stationary person, front hold, face close-up. No articulation. |
| Silhouette reveal | 10 | 8 | Brief neutral front, raise arms 30–45° away from the torso, then hold front, right profile, back and left profile with arms clear; face close-up. No full T-pose. |
| Pose-expansion reference | 10 | 10 | In one stationary frontal view, raise/lower the arms, then lift/replace one foot. Return to neutral, then held right profile, back, left profile and face. |
| Bend / waist / drape check | 10 | 8 | Front, stationary right profile with one subtle forward waist bend and return, back, face. No left-profile claim. |
| Sitting-to-standing reference | 8 | 6 | Seated front, seated three-quarter and controlled rise, standing front, standing right profile, face. One plain backless stool. No rear-view claim. |
| Face-priority reference | 8 | 6 | Brief full-body front; head-and-shoulders front, right profile, left profile, both three-quarter views; final frontal close-up. The body view is context only. |

The brief gives an eight-second neutral-turntable example and suggests a separate six-to-eight-second seated clip. The other defaults, minimums and frame allocations above are implementation choices to make the proposed sequences explicit and avoid silently squeezing multiple actions into a very short reel. They are not H3 model requirements. Use **Custom** and author a different pair when a different choreography is required.

Selecting a mode sets its default requested duration and the existing **Silent** voice mode. Pictures, crops, aspect ratio, instructions, sample line, remembered recording and authored prompt/use-guidance text remain unchanged. Selecting an already-selected mode does not reset edits. Ordinary new character recipes still use the previous five-second defaults; no existing recipe is migrated automatically. Environment modes are unchanged.

## Workflow

Open a character's **Reference reels**, open **Assist**, choose a **Framing preset**, and compose or revise the pair. The panel shows that mode's purpose, summary and **Capture timing**. Review the prompt and guidance before generating.

The new modes have their own fixed choreography and final face cut. The **Movement** and **Face close-up** option selectors remain exclusive to the original **All angles → face** preset; hidden arm/knee or push-in settings cannot accidentally affect a study mode. Use Instructions to direct Assist or choose Custom for an authored alternative.

The existing **Clips** count still requests several seeded takes of the **same selected mode**. It does not choose a different capture type for each output. To compare the brief's recommended neutral, silhouette, face-priority and seated/pose passes, generate those modes separately. No automatic multi-mode queue, additional AI planning call or new provider workflow is introduced.

## Timing and readable motion

Plans use the existing H3 frame grid and 24 fps. Eight requested seconds is 192 frames; ten requested seconds is 243 frames, or 10.125 seconds. The prompt duration, cut timestamps and shared workflow all use the actual generated frame count.

The default eight-second neutral plan follows the brief's allocation: front from 0–1 seconds, orbit from 1–5, front hold from 5–6, and face from 6–8. The camera passes the person's anatomical right profile, full back and left profile before returning to front. The person does not rotate or follow the lens. The orbit is described as an even pace that fits its interval, not an implausibly slow full circle. At 5 to less than 8 requested seconds, this mode uses five held principal views with cuts instead, explicitly not an orbit.

All other new modes use stationary camera views with clean cuts. The silhouette mode raises the arms before the side/back views. Pose-expansion completes its two small movements sequentially before any cut. Drape check bends and returns within its profile view. Sitting-to-standing shows the rise within the three-quarter view, with no simultaneous camera movement or cut. Face-priority devotes only its first second to the full body. Each of these plans reserves the last 48 frames for a settled frontal face close-up.

Minimums gate **preset composition**, not ordinary draft persistence or validation of a complete manually authored prompt. Invalid/too-short settings remain editable and receive the existing actionable composition warning. Changing the preset or requested duration never silently rewrites a saved prompt.

## Reference interpretation and intended-use guidance

The vision composer receives the real pictures, captured image guidance, chosen mode/goal and the same concrete frame-derived plan shown in the UI. It treats the photographs as observations of one person; chooses face, body and one consistent outfit references; respects explicit instructions and picture roles; and distinguishes visible evidence from conservative completion of unseen areas. No additional identity, ethnicity, exact age, height or weight is inferred from the photographs.

The output remains the existing `prompt` / `useGuidance` JSON pair and six H3 sections. The intermediate structured plan in the supplied brief is an illustrative strategy, not a new persisted response format here. There is no second mandatory LLM pass.

Guidance is specific to the chosen mode. Seated and face-priority reels do not claim rear/full-body coverage they do not show; drape check does not claim a left profile. The generated stool is staging, not a reusable identity attribute. Guidance must not transfer the source pose, camera motion, cuts, timing, studio, stool or spoken words into later shots.

These are intended views, not verified reconstruction. Inspect the actual face, proportions, visible sides, clothing and movement before accepting a reel. A generated unseen angle is not new evidence. For a shorter target shot, use selected keyframes to include useful late views that would otherwise lie beyond the consumed portion of a direct video reference.

## Persistence and compatibility

The six framing enum values are appended after `CharacterCapture`; all existing numeric values remain unchanged. No new recipe fields are added by this extension. The two nullable options from the earlier ten-second patch remain exclusive to that combined preset and are cleared when selecting or reusing a fixed study mode.

Copies and saved recipes retain the selected mode. Reusing directions transfers the mode and Instructions, but retains the destination's pictures, voice, duration and authored pair. Timing is recalculated for that destination duration. Mode/duration changes participate in the existing fingerprints and stale-Assist-result checks. Older application versions are not expected to understand newly saved framing enum values; do not downgrade a workspace containing those recipes without a compatible backup.

## Tests

`CharacterReelStudyTests.cs` covers defaults, enum compatibility, frame intervals and cut timestamps, mode-specific choreography, too-short composition, picture context, voice choices, saved-request/shared-workflow round trips, direction reuse and stale-pair rejection. `CharacterReelStudyComponentTests.cs` covers the new purpose/summary display, the shorter neutral fallback and the retained combined-preset controls. Existing reel/capture tests remain in place.

```powershell
# New extension tests only:
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj --filter "FullyQualifiedName~CharacterReelStudy|FullyQualifiedName~CharacterStudyChanges"

# Existing and new reel tests, including the previous ten-second patch:
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj --filter "FullyQualifiedName~Reel|FullyQualifiedName~CharacterCaptureSettings|FullyQualifiedName~CharacterStudyChanges"
```

Run the full Razor build and a real ComfyUI capture before treating the feature as validated. Prompt-contract tests cannot guarantee adherence to identity, pose, camera or timing instructions.
