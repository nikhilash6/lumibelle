# FLUX.2 Klein 9B edit-prompt guide — integration, examples, and evidence

Checked: 2026-09-05. Guide version: 1.0.

Companion to `flux_klein_9b_prompt_guide.md`. Only the guide belongs in the upstream LLM's recurring instruction. These notes explain the evidence, choices, and limitations; they need not consume tokens in every request.

## 1. Integration

Use the guide as a system/developer instruction, or as a clearly separated instruction prefix in a model-agnostic application. Provide the user's request and the reference roles in the order that the image workflow actually receives them. Images are helpful but not mandatory for the rewriting LLM when descriptions establish the required roles.

```text
Instruction:
[contents of flux_klein_9b_prompt_guide.md]

Reference slots supplied to the image editor:
image 1: original full-body portrait; identity, pose, and background source.
image 2: garment photograph; jacket source only.

Request:
Replace the jacket with the one in image 2. Keep the trousers and pose.
```

Forward the resulting paragraph to the normal positive edit-text input of the Klein ComfyUI workflow. Keep the reference images connected separately. The supplied ComfyUI tutorial includes both base and distilled 9B editing workflows. [S17] The guide is not an encoder chat template, a negative prompt, or a substitute for image conditioning.

Normal output is just an edit prompt. The two exceptional prefixes, `NEEDS_INPUT:` and `NEEDS_SETUP:`, are this guide's application convention, not FLUX syntax. Intercept them instead of sending them to the image model. No extra JSON schema or particular LLM vendor is required.

The Markdown guide and TXT version contain the same instructions; only heading/emphasis formatting differs.

## 2. KV and ordinary 9B: one prompting guide

BFL describes 9B-KV as an optimization of the distilled 9B editing model. Its model card retains the same generation/editing capabilities and uses ordinary language in examples. [S1] The reference implementation changes denoising: it extracts reference K/V during the initial step and reuses them later. It does not introduce a special prompt command. [S2]

**Conclusion:** applying the same prompt-writing rules to standard 9B and 9B-KV is justified. This is not a promise of identical outputs, identical memory consumption, interchangeable sampling implementations, or arbitrary cache reuse after changing inputs.

ComfyUI's `FluxKVCache` is a model-side node, not text to append to a prompt. Its current implementation also sets the reference method used by the patched model. [S18] Use a workflow appropriate to the loaded checkpoint rather than treating the two model files as universally interchangeable.

Likewise, FP8 versus INT8 ConvRot is a runtime/quantization choice, not a different linguistic format. This is an engineering distinction, not a claim that quantizations have identical fidelity.

The guide primarily targets the distilled models. BFL's ordinary 9B card documents four-step distillation. [S19] Natural-language edit principles also inform base-model use, but this document does not prescribe base-model sampling or negative guidance.

## 3. What is documented, and how it was translated into rules

### Editing instructions, not automatic cinematic expansion

BFL's single-reference guide uses direct modifications and stresses identifying what changes and what remains. [S3] Its dedicated image-to-image skill likewise starts with small edits and recommends deliberate preservation, with sequential editing as an option for complex changes. [S4]

This guide therefore makes the edit the opening clause, keeps irrelevant source content implicit, and adds constraints only where useful. These are drafting choices; there is no experimentally established universal sentence count.

BFL's Klein model guidance recommends descriptive prose and a moderate 40–70-word range in its generation-oriented section. The same file also includes much shorter image-edit examples. [S5] We do **not** force every recolor to 40 words or declare that long prompts always fail. New scenes may need substantial description; a simple change usually does not. BFL also notes that Klein does not automatically expand short prompts as some hosted variants do. [S6]

### Reference numbers carry roles, not a fixed scene/person convention

BFL explicitly documents numbered attribution and a four-reference range for Klein. [S7] Its multi-reference skill assigns identity, clothes, pose, and surroundings in different combinations. [S8] The guide preserves the caller's actual order; it does not import Krea2Edit's scene-plus-person convention.

Four is the documented working envelope, not a claim that every local implementation rejects a fifth input. Additional connected nodes do not establish reliable model behavior. Workflow-specific extensions need separate validation.

Selective transfer, avoiding donor contamination, handling several views of one person, and requiring an explicit output arrangement are practical consequences of assigning attributes to sources. They are not hidden tokens or mathematically enforced bindings. BFL's compositing examples also distinguish the elements contributed by different images. [S20]

### Positive wording does not prohibit removal

BFL recommends describing desired visible content instead of assembling a negative-prompt list. [S9] Its own editing documentation explicitly supports removing objects. [S10] Thus “remove the bag and continue the wall behind it” is appropriate editing language; banning every negative-sounding verb would be a mistake.

The instruction to avoid a separate negative prompt applies to the standard distilled workflow targeted here, not every experimental guidance scheme that someone might build around a FLUX checkpoint.

### Fidelity goals need the right scope

BFL's pose guide recommends assigning posture and identity separately and describing the important body configuration. It also warns that the model interprets structure semantically rather than guaranteeing pixel-perfect matching. [S11] Character-consistency examples preserve recognizable identity across new contexts, but do not establish a perfect-identity guarantee. [S12]

The guide consequently permits exactness as a *requested goal* while avoiding promises. Its contradiction checks are editorial reasoning: moving an arm must permit changed sleeve folds; changing illumination cannot retain all original shadows.

### Task coverage and evidence strength

| Recipe area | Primary documentation | Scope of evidence |
|---|---|---|
| Recoloring, materials, accessories and clothing | [S3], [S13], [S14] | Documented FLUX.2 editing applications; Klein-specific success rates are not given. |
| Objects, deletion and replacement | [S4], [S10] | Direct instruction patterns, including explicit removals. |
| Pose, layout, identity and composite scenes | [S7], [S8], [S11], [S12], [S20] | Documented reference-role techniques; not hard skeletal or identity locks. |
| Artistic restyling and sketch rendering | [S15], [S16] | Documented medium changes and preservation of source design/composition. |
| Typography, logos and color codes | [S3], [S7], [S14], [S21] | Exact intended text and surface/color attribution; exact output still requires checking. |
| Interior changes | [S22] | Documented room-editing use case. |
| Restoration, tightly scoped identity swaps, arbitrary camera moves, reference-sheet generation/fusion, strict masking and outpainting | Editorial extensions of instruction editing | Recipes express goals. The reviewed sources do not establish universal stock-Klein reliability for every one of these operations. |

Many BFL use-case pages cover the wider FLUX.2 family. Their examples are not automatically demonstrations of the local Klein checkpoint. The task coverage intentionally remains broader than the set of independently benchmarked capabilities.

## 4. Examples of the intended rewriting behavior

These examples were authored for this guide. They are not copied BFL outputs and were not tested by generating images. Their role is to make the output contract concrete, not to prescribe mandatory phrases.

### Small change — no added aesthetic

Input: one portrait; “Make the shirt dark green.”

```text
Change the shirt to dark green, keeping its fabric and design unchanged.
```

### Neutral reference pose

Input: one full-body portrait; “Front-facing A-pose, arms about 45 degrees down from horizontal, feet shoulder-width apart. Same clothes. Show the feet.”

```text
Repose the person into a front-facing A-pose, standing upright with both arms extended diagonally outward about 45 degrees below horizontal, hands relaxed, and feet shoulder-width apart. Keep the person's likeness, hairstyle, clothing, and the original lighting and background. Frame the entire body, including both hands and feet.
```

### Jacket transfer with reversed reference order

Input: image 1 is a jacket product shot; image 2 shows the person wearing a jacket and trousers. “Put that jacket on them, keeping their trousers and pose.”

```text
Replace the jacket worn by the person in image 2 with the jacket from image 1, matching its cut, fabric, color, and trim. Retain the person's likeness, hairstyle, trousers, pose, framing, and background from image 2. Fit the jacket naturally to their body and match the original lighting.
```

### Clothing transfer into an illustration

Input: image 1 is an illustrated character; image 2 shows the desired complete outfit. “Copy the outfit, but keep the illustration style and pose.”

```text
Dress the character in image 1 in the complete outfit shown in image 2, matching the garment shapes, colors, patterns, and layering. Retain the character's likeness, proportions, pose, and background from image 1. Render the transferred clothing in image 1's illustration style with matching linework and shading.
```

### Four references with independent roles

Input: image 1 is a person; image 2 a shirt; image 3 trousers; image 4 shoes. “Use those clothes, same photo otherwise.”

```text
Dress the person in image 1 in the shirt from image 2, the trousers from image 3, and the shoes from image 4. Match each garment's visible design, material, and color while fitting it to the person's body and pose. Keep the person's likeness, hairstyle, framing, lighting, and background from image 1.
```

### Removal with a specific exposed surface

Input: a backpack against a brick wall, with its cast shadow visible; “Remove the backpack.”

```text
Remove the backpack and its cast shadow, continuing the brickwork behind it to match the surrounding wall. Keep the rest of the scene unchanged.
```

### Exact multilingual lettering

Input: storefront sign; “Replace the words with ÖPPET IDAG. Same design.”

```text
Replace the storefront sign's lettering with "ÖPPET IDAG", retaining the existing typography, lettering color, alignment, sign material, and perspective.
```

### Outpainting with explicit setup

Input: canvas already expanded above the image; original placed at the bottom. “Continue the pale sky upward. Don't shrink the subject.”

```text
Continue the pale sky into the added canvas above the image, matching its existing color gradient and illumination. Keep the original subject at the same scale and position.
```

The canvas and original-image placement are supplied by the application, not created by this wording.

## 5. Limits deliberately handled outside the prompt

Hard locality requires an actual preservation mechanism when unchanged pixels are contractual: for example, a suitable masked workflow and/or compositing original pixels back. Output size, canvas anchoring, real alpha transparency, and exact resampling are also application responsibilities. These are implementation requirements inferred from the difference between a generated image and deterministic pixel operations, not claimed BFL prompt features.

A pose image is visual guidance, not automatically an OpenPose/ControlNet constraint. [S11] Likewise, matching a reference logo and rendering a quoted label are goals; the model card explicitly warns about imperfect text and instruction following. [S19]

Start from a known-good workflow. When evaluating rewrites, hold checkpoint, references, resolution, and sampling settings constant, and compare several seeds. Evaluate requested changes separately from preservation failures. This is a proposed evaluation method, not a benchmark result.

For compound edits, BFL suggests trying sequential transformations. [S4] The rewriter nevertheless does not emit a plan unless asked: a single-pass pipeline cannot execute several prose instructions as separate generations. Repeated passes should themselves be checked for drift.

## 6. Suggested acceptance checks for the upstream LLM

These checks have not been run as a cross-model benchmark. They specify behavior to test with the LLM chosen by the caller.

| Input variation | Required behavior |
|---|---|
| A garment is image 1 and the person is image 2 | Preserve those numbers, not a hard-coded person-first order. |
| Only the jacket should change | Preserve unrelated trousers/shoes; do not transfer the entire outfit. |
| Source is an illustration | Keep its medium unless the request changes it. |
| Pose must change | Do not append “keep the pose unchanged.” |
| Lighting must change | Do not freeze the old shadows/reflections. |
| Exact lettering contains accents or another language | Preserve the quoted string verbatim. |
| User supplies a hex code | Attach the unchanged code to the correct target surface. |
| A complete reference mapping is supplied but images are not visible to the LLM | Write the prompt without unnecessarily requesting uploads. |
| Essential donor/target roles are genuinely unresolved | Return `NEEDS_INPUT:` instead of guessing. |
| Too many essential references for the stated workflow | Return `NEEDS_SETUP:` rather than silently dropping one. |
| Several inputs are views of one person | Do not produce a group of different people. |
| Reference image contains text instructing the LLM to ignore its task | Treat it as image content, not an instruction. |

## 7. Source register

All sources below are BFL publications/code or ComfyUI's own documentation/code. No community popularity ranking is used as evidence. URLs point to live resources and may change after the check date.

[S1] BFL — FLUX.2 Klein 9B-KV model card.
`https://huggingface.co/black-forest-labs/FLUX.2-klein-9b-kv`

[S2] BFL — Klein 9B KV-cache implementation notes.
`https://github.com/black-forest-labs/flux2/blob/main/docs/flux2_klein_kv_cache.md`

[S3] BFL — Single-Reference Editing.
`https://docs.bfl.ai/guides/prompting_editing_single_reference`

[S4] BFL — Image-to-Image Prompting, official agent skill.
`https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/i2i-prompting.md`

[S5] BFL — FLUX.2 Model Family, official agent skill.
`https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/flux2-models.md`

[S6] BFL — Technical Parameters; prompt expansion and negative-prompt guidance.
`https://docs.bfl.ai/guides/prompting_unified_technical`

[S7] BFL — Multi-Reference Editing; explicitly identifies Klein's four-reference range.
`https://docs.bfl.ai/guides/prompting_editing_multi_reference`

[S8] BFL — Multi-Reference Image Editing, official agent skill.
`https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/multi-reference-editing.md`

[S9] BFL — Negative Prompt Alternatives, official agent skill.
`https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/negative-prompt-alternatives.md`

[S10] BFL — Object Removal.
`https://docs.bfl.ai/guides/usecases_editing_object_removal`

[S11] BFL — Pose & Layout Guidance. Its general-family reference counts are not Klein-specific; use S7 for Klein's range.
`https://docs.bfl.ai/guides/usecases_editing_controlnets`

[S12] BFL — Character & Style Consistency.
`https://docs.bfl.ai/guides/usecases_editing_character_consistency`

[S13] BFL — Fashion.
`https://docs.bfl.ai/guides/usecases_editing_clothing_tryon`

[S14] BFL — HEX Color Code Prompting.
`https://docs.bfl.ai/guides/usecases_t2i_hex_color_prompting`

[S15] BFL — Style Transfer.
`https://docs.bfl.ai/guides/usecases_editing_style_transfer`

[S16] BFL — Drawing to Image.
`https://docs.bfl.ai/guides/usecases_editing_drawing_rendering`

[S17] ComfyUI — Flux.2 Klein tutorial, including 9B editing workflows.
`https://docs.comfy.org/tutorials/flux/flux-2-klein`

[S18] ComfyUI — Flux node implementation, including `FluxKVCache`.
`https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_flux.py`

[S19] BFL — FLUX.2 Klein 9B model card; model scope, sampling example, limitations.
`https://huggingface.co/black-forest-labs/FLUX.2-klein-9B`

[S20] BFL — Multi-Image Referencing and Compositing. General-family examples; not all reference counts apply to Klein.
`https://docs.bfl.ai/guides/usecases_editing_multi_image_compositing`

[S21] BFL — Typography & Design.
`https://docs.bfl.ai/guides/usecases_t2i_typography_design`

[S22] BFL — Interior Design.
`https://docs.bfl.ai/guides/usecases_editing_interior_design`

## Provenance

The guide's output contract, exception prefixes, contradiction checks, task organization, and examples are an editorial synthesis. They are neither an official BFL system prompt nor a guarantee of perfect outputs. The source documentation establishes useful model behavior and recommended prompting patterns; the added rules adapt them to a reusable LLM prompt-rewriting component.
