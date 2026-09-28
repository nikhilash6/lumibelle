# Lumibelle

A personal video studio for growing an idea into a film. Built with a shared .NET 10 core and Razor UI, an Interactive Server web host, and a MAUI desktop feasibility host. See [shared hosts and distribution](docs/shared-hosts.md) for architecture, launch/publish commands, and platform status.

For UI work, use the [UX guidelines](docs/ux-guidelines.md). They describe the intended design.

## Run

Install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet restore lumibelle.slnx
dotnet run --project src/Lumibelle.Web --launch-profile http
```

Open [Lumibelle](http://localhost:5183). Create a project, then enter its Script or Assets studio. Projects, writing, asset metadata, and reference images remain available after restarting the app. The app is intended for personal use on your computer. Manual writing and asset editing require no AI backend.

## AI activity

The header drawer keeps **Active**, **Needs attention**, and **History** separate. Results become read when a visible studio review, model-test dialog, or response inspector successfully displays them. A new publication advances its observation version; reading an older result cannot consume a later notification. Opening the drawer alone does not mark results read. **Mark all read** applies to the selected project, or all projects, across pages.

**Clear history** hides finished requests, including failures and unapplied proposals. It preserves responses, review drafts, recovery data, media, and project content. Running, waiting, and remotely unconfirmed requests stay visible. Use **Undo**, or **History → Show cleared → Restore to activity**, to bring entries back without creating unread notifications. Explicitly resuming a cleared request also returns it to Activity.

Expand a provider row for capacity information. ComfyUI reports available/total memory per device, never pooled across GPUs. Available memory may include reusable cached memory and does not predict whether a request will fit ([ComfyUI memory reporting](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy/model_management.py)). These checks only read `/system_stats`; they do not clear caches or unload models. OpenRouter shows the configured key's spending allowance, cap, reset, and usage from `/api/v1/key`. An uncapped key is distinct from account credits ([key limits](https://openrouter.ai/docs/api_reference/limits)). For ChatGPT sign-ins, Codex displays the shared allowance reported by its CLI, for information only. Claude Code does not report its usage; its queue pauses when a usage or rate limit ends a request ([Claude Code provider](docs/claude-code-provider.md)).

Visible expanded panels refresh ComfyUI every five seconds and hosted allowances no more than once per minute, with manual refresh available. Server-side checks are bounded and shared between drawers; failed refreshes retain prior readings with a stale indicator. Changing the server or credential invalidates its display cache.

Queued OpenRouter text requests and model tests show **Reported cost** and expandable token/model details when supplied. Charges come from `usage.cost`, including final usage-only stream events; repeated totals are snapshots, not additional charges ([usage accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting)). Missing costs remain unreported, and historical requests are not backfilled. Observed usage survives parsing failures and recovery without another generation call.

## Unified Shots editor

**Character reference reels** live in Assets, optionally under a look. Use the right-hand tools column to fill paired generation/use prompts from three presets, start with Custom and Assist, or import a clip. Reuse the saved clip in Shots with its own guidance and optional soundtrack. See [Reference reels](docs/h3/reference-reels.md).

**Shots** combines coverage, references, prompt writing and takes in one workspace. **Shot / Takes** switch the center view. Edit the title in the heading, with scene, duration, action/camera, dialogue and an inline take preview in **Shot**. Cast and other supporting details expand below. Browsing the preview does not change the selected production take. Reorder, duplicate and delete remain in **Shot options**; bulk operations are in **Bulk operations**. Manual editing works without AI or video models, and duplicating a shot copies coverage only. The right panel summarizes references and provides **Prompt** and **Generation settings** dialogs. Named generation presets are shared across shots and projects; the prompt and references belong to the shot.

Choose ordered images and crops in **References**, with optional advisory **AI use hints**. Voice recordings retain excerpt controls and speaker mappings. **Compose prompt** examines the actual cropped images and current coverage. A valid initial composition applies automatically if the empty target is unchanged. **Revise with AI** proposes changes for **Apply changes / Discard**. The styled prompt editor and **Exact text** view share the same literal text; direct edits autosave. **Accept prompt** records a manual review without rewriting the text. Prompt-template deviations are advisory: **Generate takes** saves and uses the displayed text without requiring a separate acceptance step, while still validating the actual media and generation settings.

Reference or shot changes preserve your prompt and show **Check prompt**. **Clear prompt** retains references, settings and Direction for AI; Undo restores the draft. The dedicated Takes view shows large previews, newest first, with a selected badge and an optional setup filter. Retries and **One more take** reuse their batch's captured inputs.

The experimental production reset starts empty setups and take history while retaining scripts, assets, settings, shot IDs and coverage. Previous media files remain on disk. Old `/production` links redirect to Shots. See [Unified Shots implementation and validation](docs/production.md).

## Cut studio

Open **Cut** in a project and use **Choose takes** to assemble saved takes. Existing clips default to replacement in place; new shots are inserted in shot order without rearranging your existing clips. Choose **Add another clip** explicitly to repeat a shot. For quick alternatives, select a timeline clip and use the **Take** dropdown or previous/next take buttons above the viewer. Swapping preserves its position and resets the trim to the replacement's full range; Undo restores the previous take and trim. Shot-level production selections remain intact.

The timeline fits the full cut initially; zoom and scroll inside it for precision. Click or drag the playhead to scrub, drag a selected clip's grip to reorder, and drag either edge to trim. The full-source strip below makes excluded footage available again. Both displayed boundary frames are included, and the original media stays intact.

**Play** starts at the playhead; **Play from beginning** previews the whole sequence with original audio. Playback does not change your selected clip. Paused and trim previews use exact archived frames. The next MP4 is preloaded, with buffering and explicit retry if it cannot load. Playback pauses when editing or when the tab is hidden. This is a browser preview, not a rendered export or a guarantee of sample-accurate joins.

Each completed drag is one Undo step; Escape cancels a drag. Focus a trim handle and use Left/Right for one frame, Shift+Left/Right for ten frames, or Home/End for its limits. Numeric frame fields and Move earlier/later remain available without dragging.

Each project saves one sequence in `cut.json`, with autosave, manual Save, Undo/Redo during the page visit, and revision-conflict protection. A clip keeps its exact take until replaced. Restore unavailable takes through Trash and refresh, or remove the affected clips. Cut editing requires no AI generation or media copying. Use **Export MP4** to render the saved sequence with the configured FFmpeg executable.

## Test

```powershell
dotnet build lumibelle.slnx
dotnet test lumibelle.slnx
dotnet test lumibelle.slnx --list-tests
```

`Lumibelle.Tests` uses xUnit v3 and bUnit. The `xunit.v3.mtp-off` package selects the VSTest-compatible variant, with `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` enabling `dotnet test` and Visual Studio Test Explorer. Open the solution and build to discover the tests.

Storage tests create isolated temporary folders and clean them up. Component tests use a fake `IProjectStore` and exercise application behavior; they need neither user projects nor ComfyUI. Test files and local project data are excluded from the web application's compilation and publishing.

### Frontend and browser checks

The Tiptap editor is bundled into `src/Lumibelle.UI/wwwroot/script-editor.js` and runs locally. Normal .NET runs use the checked-in bundle. After editing `src/Lumibelle.UI/Client/script-editor.js`, install Node.js and rebuild:

```powershell
npm ci
npm run build
npm run test:unit
npm test
```

Browser tests use Playwright with installed Microsoft Edge and a separate `Lumibelle.BrowserHost` process for each test, listening on a dynamically assigned loopback port. The fixture builds the host once, isolates queue and settings state between tests, and removes its temporary data after each test. The host uses the real UI and file stores with a temporary library and entirely mocked text/image providers; it never reads your AI credentials or project data. On a machine without Edge, install it with `npx playwright install msedge`. Tests cover the writing-to-assets flow, formatting, selections, undo, paste, conflicts, reconnects, keyboard controls, and mobile layouts. Browser screenshots and reports are ignored.

## Project storage

The Development profile uses the repository's existing `App_Data/Projects` library. Installed editions default to `Lumibelle/Projects` under the current user's local application-data directory. `App_Data` is ignored by Git. Projects live on the computer running Lumibelle, not in browser storage. No data is moved automatically; see [data and host configuration](docs/shared-hosts.md#data-and-credentials) to select an existing library.

Set `Projects:RootDirectory` in `appsettings.json`, or override it for one run:

```powershell
dotnet run --project src/Lumibelle.Web --launch-profile http -- --Projects:RootDirectory="D:\Films\Lumibelle"
```

The equivalent environment variable is `Projects__RootDirectory`. Relative locations resolve against the application content root; absolute locations are used directly. Changing the root selects a different library and does not move existing projects.

Each project has a GUID-named folder containing `project.json`:

```json
{
  "schemaVersion": 1,
  "id": "8d23818b-d03d-48a4-b7f1-f83b2ed56ea2",
  "name": "The garden at the end of the world",
  "description": "A small story about finding a way home.",
  "createdUtc": "2026-09-03T12:00:00+00:00"
}
```

Names may contain Unicode and may repeat. Folder names use stable IDs, so project names never become filesystem paths. Back up or move a project by copying its complete folder, retaining its ID and manifest. To reopen a copied project, place that folder in the configured library and refresh the hub. Choose a new library root before copying if that ID already exists there.

Choose **Edit project** on the project overview to change its name or description. Saving updates the overview and library while keeping the same project ID, creation date, script, and assets. The description can be cleared; a name is required. Cancel discards the draft, and save failures keep your edits available for retry. If another tab changed the details first, copy your draft and reopen the refreshed project before saving again.

Creation writes into a `.creating-<id>` staging directory, then renames the directory within the same library after the manifest is complete. Interrupted staging writes are ignored. Invalid, unreadable, or unsupported manifests produce warnings while healthy projects remain available. Fix those manifests externally and refresh; the application does not silently overwrite them.

## Script studio

Open a project’s **Script** tab to write directly or describe a draft in **Instructions**. **Suggest outline** creates headings and action beats; **Discuss idea** stays exploratory. Script supplies screenplay formatting and editing guidance, without an Idea brief, default duration, genre, tone, or H3 profile. Production direction belongs in Shots.

The screenplay is one continuous document. Scene and optional act headings create the outline. **+ Scene** inserts after the selected scene or at the end of its act. **+ Act** inserts after the selected act and its contents; with no selection, both append. Each row has a drag handle and a menu for insertion, rename, **Move to…**, and deletion. Move scenes across acts or move an entire act with its contents. **Start new act here** groups a scene and subsequent scenes before the next act. New headings are revealed and selected for naming. Populated-act deletion confirms its scene count and remains recoverable.

Use **Ctrl/⌘ + Alt + 1–7** for element type, **Ctrl/⌘ + B/I** for emphasis, and **Ctrl/⌘ + S** to save. Enter after a scene heading creates action, after a character creates dialogue, and after dialogue creates a character. **Split scene at cursor** is in the scene menu. Structural edits and proposal application form one Undo step. **Save** stays visible and is gray when the browser matches the saved script, including after Undo. Autosave preserves newer keystrokes during storage writes. **Script** contains **Export Markdown** and **Recovery**.

Script keeps a dedicated **Assistant** panel with action, scope, Instructions, attached discussions, and compact model options. **Requests** opens discussion and proposal history. On narrow screens the panel uses the existing drawer. Other text editors use **✦ Assist** dialogs with model options inside; Shots places Assist in the H3 prompt toolbar. Manual editing, outlining, saving, and export work without a model.

**Revise** is a focused edit to **Whole script**, **Current scene**, **Current act**, or **Selected passage**. Versioned JSON operations replace ranges, insert, delete, or move captured blocks; unmentioned text, IDs, order, and emphasis are preserved. **Rewrite selected scope** explicitly permits a broad replacement. **Continue** inserts after the target. Only the current script, selected target, Instructions, and explicitly attached discussion enter new requests. Attached exchanges are removable. **Request details** shows the saved source revision, model, and exact input.

**Apply changes / Discard** controls reviewed results. Storage and editor acknowledgement complete before the next request can capture the applied document. Writing remains available while AI runs; changes to the captured target prevent stale application and offer a fresh request. Transport retries reuse their captured request. Invalid, cancelled, or truncated responses remain inspectable and never overwrite writing. **Paste / edit JSON** saves a separate corrected proposal for review without calling a model.

Saved scripts are the source for new asset extraction, shot drafting, and scene assignments. These operations capture an immutable source snapshot without a publishing or approval step. Existing assets and shots retain their captured source; editing Script does not rewrite production. Historical approved snapshots remain readable through the source compatibility loader.

## AI setup

Open **AI settings** from Script or Assets. **Connections**, **Text models**, **Image models** and **Video models** list providers, workflows or sections in a sidebar, each with its own settings; the model tabs end with a **Defaults** entry for settings that apply to all of them, such as the global text model, the default image workflow, request timeouts and local media tools. **Text models** contains a searchable, paginated catalog with provider and starred filters; **LoRAs** holds the LoRA library. Refresh a provider explicitly to discover models. Opening settings never generates text.

Star models to build a shortlist shared by every project. ComfyUI models must pass a test against the current server URL and reported version before starring; OpenRouter requires a valid key and a successful catalog check, with no generation test. Stars, global-default changes, and successful test results save immediately. Each setup form has its own Save and Cancel controls, and saving stays on the page. Form drafts are not included in an unrelated instant save; conflicts and failed saves keep them available for retry.

All text assistance uses one model default per project, set in **Project settings → Text assistance** or explicitly through **Set as project default** in any model menu. Existing projects start with **Use global default**; previous per-studio choices no longer influence new requests. The model chip inside assistance opens configured choices and collapsed reasoning. A temporary override applies to one successfully queued request; failed saves preserve it, and retries keep captured inputs. Choices refresh on page focus and before submission; changed defaults require another click before submitting. Missing or incompatible models never trigger substitution. Manage models preserves the return link to the originating project.

Text-assistance composers share a compact dialog with a persistent footer, becoming a full-width sheet on phones. Closing keeps draft instructions and model overrides. Shot prompts keep **Direction for AI** inside Assist; extraction and shot drafting retain their scene selectors. Image prompt and guidance assistance retain explicit image inspection. Text review, image generation, and video generation remain separate actions.

### Image prompt enhancement

In Assets, write an image prompt or edit instruction, choose an **Enhancement model**, then click **Enhance**. The remembered choice is shared across Create/Edit and Krea/Klein within the project. A review dialog shows the original and an editable suggestion; **Apply** changes only the prompt, and **Undo enhancement** remains available until you type or change its context. Image generation is a separate action.

Edit mode also offers **Inspect reference images**, off by default. With an OpenRouter vision model selected, this sends the actual ordered images with their current individual crops to that provider. ComfyUI enhancement uses text context only. Missing models, unsupported image input, and missing or trashed references produce actionable errors instead of silently substituting inputs. Cancelled, incomplete, or clarification responses cannot be applied; retries are explicit.

Four versioned profiles are embedded in the application; the supplied guides and integration notes are in [prompt-enhancement documentation](docs/prompt-enhancement/README.md). Requests capture their context once, and a suggestion cannot overwrite a changed prompt, workflow, asset, or set of references. Previews and Undo are visit-local; generated images retain the final submitted prompt through existing metadata.

### ComfyUI

1. Start an up-to-date ComfyUI with the built-in `CLIPLoader`, `TextGenerate`, and `PreviewAny` nodes.
2. Install a text-generation encoder supported by those nodes. The initial model is `gemma4_e4b_it_fp8_scaled.safetensors` when installed. Lumibelle lists every exact filename that `CLIPLoader` advertises, including custom and conditioning-only encoders whose compatibility cannot be determined from catalog metadata alone.
3. For reference images, install a Krea 2 Turbo diffusion model, a Qwen3-VL 4B text encoder, and `qwen_image_vae.safetensors`. The defaults prefer `krea2_turbo_int8_convrot.safetensors`, `qwen3vl_4b_fp8_scaled.safetensors`, and `qwen_image_vae.safetensors` when detected.
4. For image editing, install the current [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) and [comfyui-tooling-nodes](https://github.com/Acly/comfyui-tooling-nodes) node packs, restart ComfyUI, and place `krea2_identity_edit_v1_2.safetensors` in the ComfyUI LoRA directory. Lumibelle checks for `ETN_LoadImageBase64`, the Krea 2 Edit nodes, and the current `target_latent` input. Missing edit dependencies disable editing without disabling image creation or manual asset work.
5. Enter the server URL (default `http://127.0.0.1:8188`) in **Connections**, check it, and save. In **Text models**, choose **Refresh ComfyUI**, then **Details & test** on a model. **Test selected model · 256 tokens** runs the normal workflow with a fixed prompt, thinking disabled, and a fresh seed. Successful results save immediately and enable the star. The optional **Advanced model test** accepts a custom message and output limit so you can inspect model behavior; its prompt and response remain transient and are never written to settings, projects, or assistant history. A failed result save can be retried without generating again. Refresh and save the image and Krea 2 Edit LoRA files separately in **Image models**.

The native text-only workflow uses `CLIPLoader` with type `stable_diffusion`, `TextGenerate` with thinking disabled and a fresh seed, and `PreviewAny` to return text. See the [official Gemma 4 workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/llm_gemma4_text_gen.json). Lumibelle neither downloads models nor installs nodes. Before either model test, Lumibelle verifies that ComfyUI has no running or pending work, then asks it to unload models and free cached memory. A busy queue blocks the test instead of disturbing another job. Successful checks are stored in ignored `App_Data/ai-settings.json` and match the normalized ComfyUI URL, reported ComfyUI version, and exact filename. Use **Test again** after replacing a model file without changing its name.

Each successful test records an observed token rate and, when ComfyUI reports it, baseline and peak device VRAM plus active PyTorch allocation. The latest five observations per model are retained. These values help compare models on the same machine; they are approximate runtime measurements rather than model file sizes, and other GPU activity can affect them.

Reference generation uses a local, flattened version of the [official Krea 2 Turbo workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_krea2_turbo_t2i.json): Krea 2 and Qwen3-VL loaders, the exact entered prompt, zeroed negative conditioning, eight Euler/simple sampling steps, VAE decode, and a temporary preview. Prompt enhancement is disabled so saved metadata matches the actual generation input. Lumibelle copies the completed image into the project; it does not depend on the temporary ComfyUI filename afterward.

**FLUX.2 Klein 9B KV** is also available for creation and multi-reference editing. In **AI settings → Image models**, select **FLUX.2 Klein 9B KV**, refresh the catalog, and save; choose it as the default workflow under **Defaults** if you want it for new requests. Each workflow keeps its own file selections. Assets also has an **Image workflow** selector for the current run.

**Optional LoRAs:** in **AI settings → LoRAs**, refresh installed files and register an exact file with a friendly name, workflow assignment, default strength, and optional trigger text. Registrations belong to their ComfyUI server; they organize installed files without downloading them or testing architectural compatibility. Krea’s required identity-edit LoRA remains separate.

Add comma-separated **Tags** to LoRA registrations, then search or filter the library by tag. In the project’s **Settings** tab, **LoRA visibility** applies to every asset and both Create and Edit. Empty filters show all LoRAs. **Only show these tags** matches any listed tag and hides untagged LoRAs; **Hide these tags** always wins, even when an allowed tag also matches. For example, hide `nsfw` in an SFW project. Save/Cancel keeps filter drafts separate from asset edits, and conflicting changes require reloading before retrying. An already selected, enabled, nonzero LoRA excluded by the project blocks generation until disabled, removed, or allowed again. Returning to Assets or opening the searchable LoRA picker reloads the project filters and checks availability. Search by name, path, or tag; choosing a result adds it immediately. Each batch checks the saved project rules when it starts; changes never rewrite earlier images or their recorded LoRAs.

In Assets, expand **LoRAs** to add, enable, reorder, or remove registered LoRAs and adjust their individual strengths. Selections autosave separately for each asset and workflow and are shared between Create and Edit. **Insert trigger into prompt** appends the saved text only when clicked. Changing a library default does not change existing asset strengths. Disabled LoRAs and strength zero do not affect generation; enabled missing files block it until refreshed, repaired, disabled, or removed.

Optional LoRAs use `LoraLoaderModelOnly`: before sampling for Krea creation, after the required identity LoRA and before the reference patch for Krea editing, and before `FluxKVCache` for Klein. Each batch captures the ordered selection once and checks exact files and loader capabilities before submission. Saved image details and Trash retain the applied filenames, names, and strengths. Older records load with no optional LoRAs.

Install these files in the corresponding ComfyUI model directories (subfolders are supported; select the exact reported filenames):

- Diffusion model: `flux-2-klein-9b-kv-fp8.safetensors` in `models/diffusion_models`, from [Black Forest Labs](https://huggingface.co/black-forest-labs/FLUX.2-klein-9b-kv-fp8).
- Text encoder: `qwen_3_8b_fp8mixed.safetensors` in `models/text_encoders`, from [Comfy-Org's Klein 9B files](https://huggingface.co/Comfy-Org/flux2-klein-9B/tree/main/split_files/text_encoders).
- VAE: `flux2-vae.safetensors` in `models/vae`, from [Comfy-Org's FLUX.2 files](https://huggingface.co/Comfy-Org/flux2-dev/tree/main/split_files/vae).

Use current ComfyUI with `FluxKVCache`, `Flux2Scheduler`, `EmptyFlux2LatentImage`, `ReferenceLatent`, and the custom sampler nodes. Editing also uses `ETN_LoadImageBase64` from the existing tooling node pack. Catalog checks inspect the required input contracts and exact installed files; a missing KV model does not fall back to standard Klein, a base checkpoint, 4B, or Krea. Neither opening settings nor refreshing models generates images or installs files.

The adapter flattens the [official Klein 9B KV editing workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_9b_kv_image_edit.json): Qwen3 8B `flux2` encoding, zeroed negative conditioning, `FluxKVCache`, CFG 1, four Euler steps with `Flux2Scheduler`, and a FLUX.2 latent canvas. Each edit reference is scaled to 1 MP with Lanczos, VAE-encoded, and appended to both conditioning chains in order. Creation omits the reference chains. Lumibelle uses the selected output aspect ratio instead of deriving the canvas from image 1, embeds sanitized PNG inputs instead of server filenames, and downloads a temporary preview instead of using `SaveImage`.

Choose **Edit image** to make that take **image 1**, then add references from any asset in the same project. Lumibelle accepts up to eight images total. Reference them by number in the instruction, such as “Keep the person from image 1, wearing the outfit in image 2.” Use **Crop** on any input to submit just part of it; additional references start with their full frame. Klein does not use Krea's LoRA, fidelity, or grounding controls. Switching to Krea retains images and crops; more than two inputs blocks the run until the extra references are removed. The selected workflow, files, source order, crop, and prompt are captured for the request; saved takes retain their workflow and ordered source IDs even if a source is later deleted. Workflow switching and reference changes are disabled during generation.

ComfyUI queues the workflow alongside other jobs. Lumibelle opens a prompt-scoped WebSocket before submission and shows the current workflow stage, elapsed time, generated tokens or image sampling steps, and an approximate time remaining when ComfyUI supplies measurable progress. Model-loading stages show elapsed time because ComfyUI does not publish the console's VRAM details or a loader percentage. Lumibelle continues polling only its submitted job's history as the authoritative completion path, so a missing or dropped WebSocket reduces progress detail without losing the result. The timeout includes time spent queued. Cancel targets only that job. Older servers without the targeted cancellation endpoint have the pending job removed when possible, while an active execution may continue. Lumibelle never sends a global interrupt or clears the queue.

### OpenRouter

1. Enter your own OpenRouter API key in **Connections**, check it, and choose **Save key**.
2. In **Text models**, choose **Refresh OpenRouter**, search for a model, and star it. Optionally choose **Set default**.
3. Select the starred model through the model chip inside **Assist** to use it independently of ComfyUI.

The key check calls `GET /api/v1/key` and model discovery calls `GET /api/v1/models`; neither generates text. Actual requests use OpenRouter's [OpenAI-compatible Chat Completions API](https://openrouter.ai/docs/quickstart) and stream text into the assistant. A model must be explicitly selected. Lumibelle does not automatically switch models/backends or retry paid generation requests.

Default advanced settings are temperature **0.7**, maximum output **2,048 tokens**, and a **300-second** timeout. Authentication, credit, rate-limit, context, model, connection, and execution errors are surfaced. Partial text and instructions remain available after failure or cancellation.

## Assets studio

Create Characters, Environments, and Props manually, or choose **Extract from script** to inspect all or selected scenes of the latest saved screenplay with the configured text model. Without saved scenes, extraction links back to Script studio. Extraction shows the approximate context size and returns editable proposals. Each proposal must be explicitly created, merged into an existing asset, or skipped; extraction never edits accepted assets automatically.

Each asset keeps visual notes, evidence tied to its captured script snapshot and scene IDs, suggested reference tags, and its own image library. Import PNG, JPEG, or WebP files up to 25 MB, or generate one to four local Krea 2 or Klein KV candidates. Image runs are sequential and retain candidates completed before a later failure or cancellation. Prompts, model filenames, aspect ratios, seeds, dimensions, and generation timestamps remain with each generated take.

Choose **Edit image** on any take to use it as the source for a single-image Krea 2 edit. **Crop source** lets you drag the crop to reposition it and pull its edges to zoom; the expandable precise controls provide the same adjustments with sliders. Cropping never changes the stored original. The source is decoded, oriented, cropped, reduced to at most 2 MP, converted to PNG in memory, and embedded directly in the ComfyUI workflow through `ETN_LoadImageBase64`; Lumibelle does not upload it to or leave it in ComfyUI's input directory. Edit instructions can target a different output aspect ratio. The default workflow uses the identity-edit LoRA at strength 1, fidelity 4, 768 px grounding, `fit` mode, and ten Euler/simple steps at CFG 1. Results are stored beside the source as unapproved takes. Their metadata retains the source IDs, normalized crop rectangle, and edit settings even if the source is later deleted.

**Two-image Krea edits:** choose **Edit image** for **Image 1 · Base**, then add one image from any asset in the same project as **Image 2 · Reference**. Adding the second image enables two-image mode; removing it restores single-image editing. For a person-into-scene edit, use the scene as the base and the person as the second reference. Write a plain-English instruction such as “Place this person at the cafe table, holding a coffee.” For clothing transfer, try “Dress the person in the base image in the outfit from the second reference. Preserve their identity and fit the clothing naturally.” This clothing example is practical guidance, not a separate workflow or automatic prompt rewrite.

Krea’s **Advanced** controls expose **Base fidelity** (default 1) and **Second-reference fidelity** (default 4) for two-image edits. Single-image editing keeps **Reference fidelity** (default 4). Both use the existing 0–10 range and shared grounding resolution. The second image is wired into both the pixel/latent and grounded text paths (`source_image_b`, `source_latent_b`, and both encoders’ `image_b`); `ref_boost_a` controls the base. The run checks current node capabilities before submission. Older installations can keep single-image editing while showing an update instruction for two-image support.

Both Krea and Klein support independent, reversible input crops. **View**, **Crop**, and **Remove** are available for each additional reference. Cancelling or clearing a crop affects only that input, never its original file or the output aspect. Switching workflows preserves crops. Saved edit metadata contains ordered source identities, base crop, exact additional-reference crop identities, and the actual Krea fidelity values. Library and Trash previews retain these crops after reloading or restoring a source; viewing alone starts with the original image, while comparisons initially use submitted crops. Existing images need no migration.

As soon as the first edited take is saved, **Review edited takes** opens with its source, ordered additional references, and placeholders for remaining candidates. Takes appear as they are saved without changing your current image or comparison. Live progress and **Cancel remaining** are available inside the review. Closing keeps generation running and does not reopen the modal when later takes arrive. Select a thumbnail row to view one image, or toggle **Compare with** on another row to compare any pair using a slider or side-by-side view. Cropped inputs offer **Full images / Submitted crops**, applying each input’s own saved crop. Details retain the prompt, seed, workflow, and provenance. **Review latest edit** reopens the latest batch during the current page visit; after reloading, library previews show each saved image with its recorded inputs.

**One more take** adds a candidate to the latest edit batch, during generation or after it finishes. Extra takes use the captured prompt, workflow, references, crops, LoRAs, and ComfyUI settings; later sidebar changes do not affect them. Each click adds a placeholder and queues one request after the current candidates. Fixed seeds advance to the next take; blank seeds stay random. Comparison selection stays in place, and missing or trashed inputs must be restored before adding another take.

Takes save automatically. In batch review, **Discard** moves a saved take to Trash immediately, including while later candidates generate. **Undo** restores its original place in the batch. Source images, added references, and approved/cover images are managed from the library. Closing keeps completed discards; the global **Trash** remains available for recovery.

Image deletion from the library also moves images to **Trash**, with snackbar Undo and no confirmation. Trash covers all projects, supports project filtering and pagination, and retains images for **30 days**. Restore selected images without confirmation; **Delete permanently** and **Empty Trash** require confirmation. Empty Trash applies across all projects and captures the exact images displayed in its confirmation, excluding later discards. Failed operations remain visible for explicit retry.

Recorded sources and additional references still in Trash remain viewable and comparable in image review, including submitted crops. Their rows show **In Trash** and **Restore source/reference**. Viewing does not extend retention; images must be restored before they can be used for new generation. Restoring does not reset the comparison. Missing files and permanent deletions show unavailable states.

Deleting an entire asset still asks for confirmation and sends its images to Trash. Restoring an image recreates the original asset if necessary, retaining its ID, name, notes, category, and evidence. Existing covers take precedence over restored covers.

Trash metadata lives alongside active images in each project's `assets.json`; files stay in their original locations until permanent deletion. Membership changes publish atomically under the project lock, and ordinary metadata saves cannot bypass Trash. A hosted cleanup service checks at startup and hourly while Lumibelle runs. Purges publish durable deletion intent before removing files, so interrupted or failed cleanup resumes safely after restart. Existing libraries start with empty Trash; images permanently deleted before this feature cannot be recovered.

Imported and generated images begin as unapproved takes. Select **Use as reference** to make an image available to future shot composition. Several images can be approved for one asset, and one can be the cover. Tags such as `face`, `full body`, `wide view`, or `outfit: red coat` describe what each reference provides; they will let the shot composer choose appropriate H3 inputs without interpreting filenames.

## Project content storage and recovery

Each project folder additionally contains:

```text
script.json                   # typed blocks, revision, historical source pointer
script-assistant.json         # discussion, proposals, request metadata and status
script-history/<id>.json      # recoverable draft snapshots
script-sources/<id>.json      # immutable saved-source captures
script-approved/<id>.json     # historical snapshots, compatibility reader only
assets.json                   # ordered assets, image metadata, tags, and revision
assets/<asset-id>/images/*    # validated project-owned reference images
```

Files are versioned, readable JSON published through same-directory atomic replacement. Existing projects without writing files open as empty workspaces. Corrupt or unsupported documents produce errors and are not replaced. Unpublished temporary files are ignored. Keep complete project folders in backups; chat and proposals may contain copies of source text supplied to the model.

**History** contains snapshots saved before AI application, scene deletion, and restoring an older version. Preview and restore a version there; restoring first snapshots the current writing. Autosave is not a snapshot of every keystroke. Closing/reloading before a successful save can lose unsaved text; the editor warns when navigating away with dirty text. Interrupted generation is marked after application restart and is not automatically resumed.

Global settings are stored in ignored `App_Data/ai-settings.json` beneath the application content root, separately from the project library. API keys are protected with ASP.NET Core Data Protection using application name `Lumibelle` and the hosting account's default persistent key ring. The UI shows only whether a saved key is configured and supports replacement/removal. Moving settings to another account or losing the key ring may require re-entering the key; project writing remains portable. Never commit the settings file or key ring.

## Architecture and verification

Components use `IScriptStore`, `IAssistantHistoryStore`, `IAssetStore`, `IAiSettingsStore`, `IScriptAssistant`, `IAssetExtractor`, `IReferenceImageGenerator`, and `IReferenceImageEditor`. `IAiProviderRegistry` supplies Microsoft.Extensions.AI `IChatClient` instances for OpenRouter and ComfyUI text. Asset extraction reuses that boundary; the ComfyUI image adapters own workflow submission, in-memory source preparation, scoped cancellation, history polling, and download. Prompts, validation, HTTP, and filesystem operations remain outside Razor markup.

The xUnit/bUnit suite covers project, script, assistant, asset and image persistence; conflicts and recovery; context construction; backend contracts and cancellation; sanitized Markdown; settings; autosave; proposal application; reference approval; and retained edits after failures. HTTP tests use fakes; no automated test requires a GPU, ComfyUI, real user projects, or an API key. Browser checks additionally exercise actual keyboard and responsive behavior.

## Direction

The saved screenplay is the dramatic source. Assets and Shots capture saved revisions while keeping their own work independently revisable. The script format has no dependency on a particular video model.
