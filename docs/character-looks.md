# Character looks

A character owns shared identity notes, preservation defaults, voice recordings, and per-workflow LoRAs. Named looks describe wardrobe, hair, makeup, or other appearance changes. Existing projects start with no looks and unassigned images; loading a project never classifies or rewrites its content.

## Assets

Select the character and choose **Edit details** at the top right of the gallery header. In the details modal, use **+ Add** beside the look selector, or review nested look suggestions in **Extract from script**. The **…** menu contains Edit, Duplicate, Archive/Unarchive, and archived-look visibility. **Look details** expands the description, preservation guidance, and script evidence. Extraction keeps separate Create/Merge/Skip decisions for characters and their looks. A look can only merge into its selected character. Approved-script evidence stays attached to accepted looks.

The look selector filters images and chooses the Create/import destination. All images and General / unassigned use an unassigned destination. Each image has one optional look assignment; individual and bulk assignment preserve its ID, file, approval status, cover status, and generation provenance.

Create drafts are remembered independently for each character/look during the current page visit. A new draft starts from the character and look descriptions. Switching back restores the author's draft, including an intentionally empty prompt.

Edit separates its source image from **Target look**. Starting an edit defaults to the source look. Changing the target leaves the instruction, source, additional references, and crops intact. Enhancement receives the target appearance separately from reference background notes and requires explicit application.

Duplicate copies look descriptions, preservation guidance, and evidence into a new ID; it copies no images. Archive keeps images and existing references inspectable. Unarchive before assigning new appearance references or generating into that look.

## Shots

**Draft shots** can suggest appearances from accepted character looks. Review, correct, or clear these suggestions before adding shots. Appearance entries also support characters without dialogue.

Choose **One look**, or **Transition: starting → ending**, then describe the continuous change in the shot's action. Actual reference selection remains manual. Use **Represents** to connect each character picture to the same stable shot character, then choose its purpose:

| Purpose | Guidance |
| --- | --- |
| Identity | Shared character preservation only. Excludes look and image wardrobe guidance. An explicit shot override remains available. |
| Current look | The matching look and image guidance for a shot with one appearance. |
| Starting look | Matching appearance guidance for the beginning of a transformation. |
| Ending look | Matching appearance guidance for the result of a transformation. |

General images can supply identity references across looks. Multiple references and both sides of a transformation share one H3 subject label. Look descriptions appear in the compiled prompt even without images; the editor warns about missing appearance references. The nine-image limit still applies across assets and saved continuity frames.

Changing a look assignment keeps existing shot references. Incompatible purposes, reassigned images, archived looks, or unavailable media are shown for explicit repair. **Refresh reference defaults** loads reviewed changes to character, look, and image guidance. Renaming a speaker keeps its stable appearance and reference links.

## Saved output and recovery

Image batches capture the target appearance and ordered source/reference look contexts. Shot runs capture appearances and phase-specific reference guidance with compiler profile `h3-single-take-v3`. **One more take** retains that original context while checking that referenced identities and look membership remain usable. It never imports updated notes into the captured prompt.

Image organization is separate from generation provenance. Reassigning images or editing a look does not change saved prompts, reference context, or video takes. Submitted look details remain available in image review, video review, and Trash. A selected video take becomes outdated when its current appearance context changes.

Look collections follow the existing asset revision/lock and atomic publication rules. Trashed images retain their original owner/look definitions. Restoration recreates a missing look but retains a current definition with the same ID. Shot undo and recovery preserve appearance entries without rewriting take snapshots.

Validation uses mocked text/image/video providers. `tests/browser/character-looks.spec.js` exercises reviewed extraction, manual organization, enhancement, cross-look edits, One more take, shot planning, transition references, generation, changed defaults, mobile layout, and keyboard focus.
