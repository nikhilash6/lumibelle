# Reference reels

Reel, voice and image details use the same [media-details layout and action order](../asset-media-details.md).

Reels can now supply [lightweight keyframe pictures and separate audio](reel-keyframes.md). New requests retain lossless source frames by default; new shot and reel-recipe attachments inherit the asset's **Default reel usage** (Keyframes when unset). RefMod and Full reel are available, and previously saved attachments retain their behavior.

Character audio can be saved as an independent recording and made the character's default across looks. Fresh recipes preselect that default; shot setups choose one voice per character in Manage references. See [character voices](character-voices.md) for Save voice, excerpts, speaker mappings and legacy compatibility.

In **Assets**, select a character, environment or prop and choose **Create → Reference reel** in Asset tools. This gallery uses the same immutable video media as shot references. It does not create screenplay scenes or hidden production shots.

## Create a character reel

1. Choose **Create → Reference reel** in the right-hand **Asset tools** column (a drawer on narrow screens). Setup and prompts share one scrollable form; the mixed gallery stays in the center. Your latest saved recipe is restored, or a fresh recipe is shown without saving until you edit it. Give it a name. Fresh recipes are unassigned; organize completed clips into looks through Edit. An existing associated draft keeps its destination until you choose Use unassigned.
2. Select at least one image or reel in **Manage reel references**. Reel references inherit the asset default and can be overridden locally as Keyframes, RefMod or Full reel. Order and crop the images, and describe the identity, proportions, clothing or details each should supply. The form shows compact numbered crop thumbnails and guidance summaries; select a row to edit that picture in the manager. Nothing is selected automatically.
3. Choose **New voice**, **Existing recording** with an excerpt, or **Silent**. Speaking modes use the character name, language and exact sample line. The default line is “Okay. Here I am. Almost elegant.” Speech spans the cuts once; it is not repeated for each view. **Voice description (optional)** supplies voice qualities and delivery direction to the next Assist composition or revision. For an existing recording, it guides delivery while the recording supplies identity. Silent mode ignores the description but retains it in the recipe. Changing this field preserves authored prompts and marks them for checking; only the exact generation prompt is sent to H3.
4. In **Assist**, choose **Continuous turn**, **Body → face**, **Three angles**, or **Side → rear → face**, and add any instructions. The view description explains the preset. **Custom** gives the assistant no prescribed framing. **Compose pair** uses a vision model to inspect your selected crops and write the generation prompt and matching use guidance. Choosing pictures or a framing preset alone leaves both texts unchanged. You can also write both texts manually without a text model.
5. Review the two editable texts, then **Generate reel**. Default framing is square, requested duration is 5 seconds, and H3's frame grid produces 124 frames / **5.167 seconds**. Durations from 2–15 seconds are supported so the completed reel can be reused as conditioning. Video model settings and generation presets use the configured H3 installation.

**Setup → H3 LoRAs** lets you add registered H3 LoRAs, adjust strength, reorder, disable, or remove them. **Manage LoRAs** opens the shared library; register installed weights for **MiniMax H3 Ref2VA** there. Project visibility filters apply. Optional LoRAs are separate from preset acceleration weights. Adding a LoRA preserves both prompt texts; trigger text can be explicitly inserted into AI instructions. Active files and strengths are checked before enqueue.

**Assist** composes or revises both fields using the project text-model default or a request override. It receives the actual cropped still images, character/look and image guidance, timing, voice settings, current texts, selected preset and instructions. The preset is a starting point; Custom imposes no framing plan. It does not inspect video or audio. A valid initial pair auto-applies only while the saved empty target and captured inputs remain unchanged. Revisions use **Apply changes / Discard**; stale and invalid responses remain available for inspection. If complete JSON fails prompt validation, **Review response** shows the reason and both copyable text fields; it does not apply them automatically. Hiding the tools, switching tabs, or leaving the character retains saved recipes. The prompt editor stays mounted across tool tabs, preserving Undo. Navigating to another character flushes the latest browser typing first.

**Generate reel** stays reachable in the pinned footer. Preparation shows progress and a **Cancel preparation** action; hiding the tools remains possible. A local preparation or validation failure is labelled **Not submitted to ComfyUI**, with **Review prompt** opening the Prompts tab directly. Once enqueued, request progress and **View request** stay available, and generation runs independently of the editor. The latest video request is labelled with its timestamp and state so an older failure is distinguishable from the current preparation attempt.

Opening a reel request from AI activity opens that request once. Closing it (including Escape) clears the request ID from the URL while retaining the asset and view, so changing assets or reloading does not reopen a dismissed request. The same request remains available in AI activity.

Input changes preserve authored text and show **Check prompts**. Use **Assist → Revise pair** to incorporate changed inputs or framing, then review and apply the proposed changes. Selecting Custom does not clear existing writing. Review the exact dialogue, reference labels and displayed generated duration before submitting. Duration validation refers to the generation prompt text, not the Requested seconds input: a reel prompt must use the displayed frame-derived duration, because the preset's cut timestamps are derived from that same frame grid. Ordinary shots are laxer and may also state the authored Requested seconds, since H3 takes the output length from the frame count. Changing Requested seconds recalculates that duration. Rounding that duration to fewer decimals, such as `5.17` for `5.167`, is advisory rather than blocking: the response stays applicable and the difference is reported in **Prompt review notes**. Ordinary production shots still require one continuous shot; reel prompts have a separate profile allowing consecutively numbered views and cuts.

While a reel request is queued or running, Generate is disabled and reads **Queued…** or **Generating…**; **View request** stays available. Once every requested clip has been saved successfully, the unchanged Create recipe resets to fresh defaults with empty pictures, Instructions and prompt texts. Later edits are kept, and failed or cancelled requests retain their recipe. The completed reel still contains the original settings and directions: choose **Generate a variation** to bring them back, or **One more** to repeat the captured request.

### Correct a saved response

If a complete response fails validation, **Review response** shows the issue above two readable text panels, with request details and raw output collapsed below. **Edit response** saves a separate recipe with both returned texts and the captured settings, then opens it for correction. Existing recipe edits and the original request remain available; no AI request is sent. Save failures retain the recovery copy for retry. Generation still validates the corrected prompt. For silent reels, Assist explicitly writes silence under `overall_soundscape` and `No non-diegetic music.` under `non_diegetic_music`.

### Reuse custom directions

Each generated reel retains the exact Instructions and framing preset from its saved recipe. Open **… → Edit details → Directions used for this reel** to read or **Copy directions**, including for older generated reels that captured instructions. Imported clips have no invented directions.

In another reel's **Assist**, expand **Reuse directions from a reel**. Choose a generated reel from the current project, preview its saved directions, then choose **Use these directions**. Compatible presets for the same kind of asset are listed together across assets. Environment and prop reels are available even when they use only a built-in preset. Merely choosing a source does not edit the current recipe. Applying replaces the framing preset, camera direction and Instructions; pictures, crops, voice, timing, destination and both prompt texts remain yours. Compose or revise afterwards so Assist adapts the directions and timing to the new recipe. The saved source reel and its captured retries remain unchanged. Copy directions also lets you reuse the text in another project.

## Create an environment reel

Select an **Environment** asset and choose **Create → Reference reel**. The same mixed gallery and combined right-hand tools are available, including the narrow-screen drawer. Fresh recipes start at **16:9**, **15 requested seconds / 15.083 generated seconds**, with **Full-room survey** selected and lossless frames retained. No pictures are selected automatically. Environment reels have no look, speaker or voice controls and use silent prompts with no audio inputs.

Choose at least one image or reel in **Manage reel references**. Keyframes, RefMod and Full reel modes are available with the same asset defaults and local overrides as shots. When present, Picture 1 establishes the opening view; additional pictures can establish other views of the same place. Use the existing crop, order and guidance controls to explain what each picture contributes. Assist receives cropped Pictures and retained RefMod source stills, plus the environment's visual notes and preservation guidance. Full reels provide their author-written guidance; Assist does not inspect their video or audio. Video-only recipes establish their own opening view without inventing Picture inputs. When a recipe copied with **Create similar** refers to a picture that is now in Trash, **Manage reel references** offers **Restore image** beside it; see [Manual shot references](../shot-reference-editor.md).

In **Assist**, choose a camera preset. Each gathers its coverage within **one generation**:

| Preset | Requested seconds | Coverage |
| --- | ---: | --- |
| Full-room survey | 15 | Broad fixed-position 360° turn through side, opposite and remaining-side views before returning; brief holds at each quarter-turn. |
| Half-turn reveal | 10 | Broad fixed-position 180° turn, pausing at the side view and ending on the space behind the opening camera. |
| Held viewpoints | 15 | Four distinctly different sides of the same space, from one camera position, joined by cuts. |
| Short reveal | 6 | Broad 90° pan; opening objects leave the frame and the newly revealed part of the room fills the final held view. |
| Pull back | 6 | Substantial backward travel with steady viewing direction; foreground objects become smaller in a wider view. |
| Move toward a feature | 6 | Substantial forward travel toward a named feature, ending with more surface detail and no turnaround. |
| Arc around a feature | 6 | Broad 90° arc around a stationary named feature, ending at a distinct side view with strong background parallax. |
| Custom path | 10 | Follow your starting view, route and destination in one continuous shot, then hold. |

Turns, held-view ordering and arcs offer **Left / Right**, initially Right. Feature moves and Custom require Instructions for Assist; manual prompt writing remains available. For example: “toward the window” or “around the monitor.”

Selecting a preset also sets its suggested duration. You can adjust **Requested seconds** beside the preset; a shorter duration shows an advisory. The exact frame-derived duration and expandable **Camera timing** preview update together. Moving presets end with a hold of up to one second; survey intermediate holds last up to half a second, shortened for brief recipes. Held-view cut times are derived from the same frame grid.

Preset changes preserve Instructions and both authored texts, marking **Check prompts**. Compose or revise to incorporate them. Assist uses natural camera language, resolves conflicting author directions into one plan, and preserves visible features and newly revealed geometry across the reel. It distinguishes a fixed-position pan from travel and an arc; starting objects can leave the frame during a turn. The space beyond the pictures is intentionally completed by the model, not reconstructed from verified geometry. Review the generated reel for stable layout and correct its use guidance after watching.

Movement plans specify amplitude separately from pace, following [H3's camera-motion guidance](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_base_en.md#43-camera-motion-motion-type--amplitude--speed). Moving presets request large amplitude at fast speed at their suggested durations; longer durations spread the same move across the available time. Custom paths follow the author's extent and speed. Explicit Instructions can override these defaults. Plans describe visible progress and the ending view as well as angles and timing; returning to the opening frame alone does not establish a complete survey.

Preservation refers to furniture placement and physical relationships within the room. Screen positions, occlusion and framing change with the viewpoint. Assist confines image-relative left/right/centre descriptions to the opening view and describes later use guidance through physical relationships. These wording changes need comparison on generated results; mocked checks cannot establish camera adherence. Existing prompt text is preserved, so **Compose or revise** after choosing a preset to use the revised instructions. Keep the prompt's timeline aligned with the displayed generated duration before experimenting.

For recipes of **10 requested seconds or longer**, assistance and saved-reel details show: **“Use keyframes to reference all angles. Direct video references use only the start of this reel, up to the generated shot’s length.”** For example, a 15-second room survey used directly for a roughly five-second shot supplies only the first roughly five seconds; its later views are omitted. Keyframes selected anywhere in the reel can supply those later angles to the shorter shot. **Full reel** remains available with its existing duration warning.

The presets update the existing environment profile in place. Camera settings and directions are saved with each recipe; captured generation requests, retries and One more retain their captured prompts and inputs. No generation or sampling policy changes are involved.

Environment use guidance preserves architecture, relative placement, materials, furnishings and lighting. Later shots can choose different angles, action and camera movement without copying the reel's camera turn or route. Explicit target-shot instructions may change lighting or other conditions. Generated silent environment reels attach with soundtrack off. The existing import/copy workflow is also available, with environment-specific use guidance and no fabricated generation recipe.

## Create a prop reel

Select a **Prop** asset and choose **Create → Reference reel**. Prop reels share the environment workflow: the same gallery, right-hand tools, reference modes, camera timing preview, direction and duration controls, keyframe hint and import/copy. Fresh recipes start at **1:1**, **15 requested seconds / 15.083 generated seconds**, with **360° orbit** selected and lossless frames retained. Like environment reels they are silent and have no look, speaker or voice controls. A prop recipe cannot be saved against a character or environment, or the reverse.

The difference is the camera. An environment reel turns or travels within a stable space; a prop reel moves around, or presents, one stationary object:

| Preset | Requested seconds | Coverage |
| --- | ---: | --- |
| 360° orbit | 15 | The camera circles the stationary prop at a constant distance and height, briefly holding its front, one side, rear and other side, then returns to the front. |
| Half orbit | 8 | Front, past one side, ending on the rear. |
| Turntable | 12 | A locked-off camera while the prop makes one full turn on a concealed turntable against a plain, evenly lit backdrop. |
| Held angles | 12 | Front, three-quarter, side and rear views from the same distance and height, joined by cuts. |
| Rise to top view | 6 | The camera cranes from eye level to a high angle that shows the prop's top surface. |
| Detail pass | 6 | A close glide along the prop's surfaces toward a named detail, or its most distinctive one. |
| Custom move | 10 | Follows your starting view, path around the prop and end view, then holds. |

Orbits and the turntable offer **Left / Right**, initially Right. Custom move requires Instructions for Assist; manual prompt writing remains available. Presets and Assist keep the prop unhandled and in frame (except during a detail pass), never add people or hands, keep its surroundings consistent with the pictures or use a plain backdrop when they show none, and complete unseen sides as plausible new design rather than verified reconstruction. An orbit never rotates the prop; a turntable never moves the camera.

Prop use guidance describes the object's form, proportions, materials, colours and recognisable details from several sides. Later shots place it with their own position, scale, framing, lighting and action without copying the reel's orbit, rotation, backdrop or timing. In Shots, prop reels appear as **Prop reel** and attach with soundtrack off, like environment reels.

## Review and reuse

Each candidate is saved automatically as its own clip. Open **… → Edit details** to rename it, correct its current use guidance after watching, organize its look, or inspect the captured recipe and applied LoRA stack. **Manage keyframes…** opens the reel's saved picture set. The original generation prompt and use guidance remain unchanged in provenance. Details saves merge edits to different fields across tabs; conflicting corrections to the same field report an error and retain the unsaved form.

The details dialog keeps **Manage keyframes…**, **One more** and **Generate a variation** in its persistent footer alongside Close and Save details. Imported clips offer keyframe editing without generation actions. One more uses the original captured request; Generate a variation saves edited details before opening the recipe copy.

Reels show cached still thumbnails in the reference picker, selected-reference rows and gallery players. Thumbnails come from a frame near the start of the clip, preserve its aspect ratio, and are prepared on demand for existing as well as new media. In the reference picker, Preview and **Add reel** remain separate. In Assets, the card title selects Edit and the thumbnail opens playback. If a thumbnail cannot be prepared, playback and selection remain available. Browsing thumbnails does not analyze keyframes or change the original media.

- **Generate a variation** opens Create → Reference reel with an editable unassigned copy of that candidate's recipe, including its LoRA selections.
- **One more** extends its captured batch with the original prompts, files, hashes, model settings and applied LoRA stack. It does not reread mutable image references or current recipe fields.
- **Regenerate…** opens a resolution choice (Quick / Preview / Detail / Native, with pixel dimensions) from a generated reel's menu or details footer. Native is preselected. Keep source seed is selected initially; New random seed and Custom seed are also available. Generate creates one new reel using the selected seed and the source prompt, duration, model, sampling, LoRAs and output policy. Its PNGs and audio excerpts are copied from the original request's immutable inputs, independently of later library edits and global settings. The current Create draft stays intact. Cancel submits nothing; failed queue submissions retain the captured request for retry. Imported reels cannot use this action.
- Treat same-seed resolution changes as experiments: different dimensions change the noise layout, so motion, composition and voice can differ. This action performs a fresh generation; it does not upscale or refine the existing clip. The resulting reel records its source reel, source seed and original dimensions. **One more** continues to choose a new seed.
- **Replace in shots…** in a reel's menu or details footer puts that reel in place of another reel of the same asset in every shot that uses it. A regenerated reel preselects the reel it came from, so a Quick reel regenerated at Native can upgrade every shot in one step. The dialog lists the shots before anything changes. Each attachment keeps its name, use guidance, visual mode, soundtrack, speaker and character voice. Keyframes keep their crops and notes at the nearest moments of the new reel, audio excerpts are kept within its length, and RefMod frames are captured again from it. A prompt reviewed against the old reel stays reviewed. Shots that cannot take the new reel are listed and left unchanged, for example when they use a soundtrack the new reel lacks or when two keyframes would land on the same frame. All other shots are saved together; open Shots editors for them must reload before saving. Takes, captured requests and reel recipes keep the reel they used, and the old reel stays in the library until you move it to Trash.
- **Import… → Reference reel** accepts an MP4 or copies a take from the same project. Imports have editable use guidance and no invented generation metadata.
- **Move to Trash** unlinks the candidate recoverably. Restore it in **Trash → Reference reels**, including after deleting its owning asset. Recovery is kept out of the Assets gallery. Attached setups retain their media.

If a character, environment or prop is deleted while a reel is still generating, the finished clip is retained in Trash. Restoring it also restores its captured owning asset and character look when needed.

In **Shots → References → Manage references**, browse **All** or **Reels** and add an asset reel. New attachments start with **Keyframes**, using saved picks or an on-demand local suggestion. Choose **Full reel** for video conditioning or **None** for audio alone. Choose character audio once in **Character voices**; environment and legacy reels retain **Use audio**. Keyframe and audio-only sources offer excerpts and speaker mapping. Apply copies immutable media, selected frames and current use guidance into the setup. Later library edits do not rewrite the attachment. **Copy usage snippet** uses its actual `<Picture N>`, `<Video N>` and `<Audio N>` identifiers. Compose receives selected pictures and copied use guidance, not the reel's entire generation prompt or sample dialogue. See [keyframe editing and compatibility](reel-keyframes.md).

**Full reel** conditioning retains the existing frame-grid and target-duration limit. The manager warns when a shortened prefix excludes later views, including part of a camera turn or route. Keyframes can cover the whole clip; their separate audio excerpt is independent of target-shot duration. Local selection uses visual difference and sharpness, without an additional AI description request.

## Reel resolutions

**Generation settings → Resolution** and **Regenerate…** offer the same four canvases as shot takes. Preview remains the default for new reels; regeneration initially selects Native and **Keep source seed**. **New random seed** and **Custom seed** let you explore other candidates with the same captured inputs.

| Choice | Approximate MP | Landscape | Portrait | Square |
| --- | --- | --- | --- | --- |
| Quick | 0.2 | 608 × 352 | 352 × 608 | 448 × 448 |
| Preview | 0.4 | 832 × 480 | 480 × 832 | 640 × 640 |
| Detail | 0.7 | 1120 × 640 | 640 × 1120 | 832 × 832 |
| Native | 1.0 | 1344 × 768 | 768 × 1344 | 992 × 992 |

MP labels are approximate, using one million pixels per MP. Exact dimensions appear in each choice and in reel details. Sizes follow the [stock H3 node's 32-pixel increments](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_minimax_h3.py). These are generation canvases, not post-generation upscales. Changing size preserves the prompt and other authored settings; review prompts before generating. Camera adherence at each size remains an experiment.

The asset card's top-right badge reports the saved media's actual MP, with exact dimensions on hover and in its accessible description. The reference picker's reel cards show the same badge below the asset name, including during playback. Generated native reels also show **Native** when the output dimensions match the captured native setting. Imports show their actual resolution without assuming how they were generated. Duration stays at the top left and character looks remain at the bottom.

Resolution is retained by saved recipes, variations, One more and captured retries. Existing preview/native recipes and captured fingerprints stay unchanged. Quick and Detail dimensions are validated against the captured recipe; changing the capture's size or resolution independently is rejected.

## Persistence

Recipes, candidates, recovery records and publication receipts share the asset-library lock. Recipe writes check their own revision; metadata edits preserve background reel publication. Generation uses version-three typed video captures with an asset destination, and text pairing uses the versioned `character-reel-v1` or `environment-reel-v1` profile. Existing character profile IDs, serialized fields and captured recipes retain their original behavior. Shared preparation, H3 workflow construction, remote execution and download are reused. A completed output is retained through publication failures, and duplicate completion does not resurrect a removed candidate.

Existing assets, shots, setups, takes, manually attached videos and historical requests remain readable. There is no production reset or migration of existing content.
