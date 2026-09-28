# Shot prompt/reference freshness

The Shots outline flags missing, changed-reference, and unreviewed prompts across
the project. **Prompt status** filters the outline independently of the text
search. The count covers the whole project, not just the current scene or setup.
The Prompt button, prompt editor, and bulk-generation checklist show the same
reference status. The bulk warning is advisory and does not silently remove
selected shots or change the queue/capture path.

## States and resolution

- **Prompt missing**: the current prompt is empty or whitespace. Compose or write it.
- **References changed**: the current reference-content signature differs from
  the prompt's reviewed signature. Revise/apply a composed prompt, or choose
  **Mark reviewed** after checking that the existing text still fits.
- **Review prompt**: there is an unreviewed/manual draft, or the reviewed revision
  has no saved reference baseline. This is not a claim that references
  definitely changed. **Mark reviewed** records a baseline without an AI call.
- A matching reviewed prompt has no warning badge.

The outline's status badge opens that shot's prompt directly. **Mark reviewed**
stays in the dialog footer while the prompt scrolls. A missing scene, direction,
or duration does not prevent reviewing a nonempty prompt; generation still
requires them. The separate **Choose scene**, **Add direction**, or **Set duration**
action opens the Shot tab and focuses the missing field.

Merely generating an AI proposal does not clear the status. Applying the validated
proposal does. A failed request, opening a dialog, saving identical values, and a
RefMod cache rebuild do not approve a prompt. Manual edits remain drafts until
reviewed. Review uses an optimistic version check and flushes pending browser
text before saving. Free-form prompts can be accepted as written; template and
dialogue deviations remain advisory. Empty drafts stay saveable but cannot be
accepted or generated. Actual unavailable media and invalid generation settings
remain blocking.

This is advisory: no new generation block, auto-recomposition, or AI activity
notification is added. Existing generation validation and captured jobs are
unchanged. The badge is not a guarantee that the model/settings are available or
that the prompt remains consistent with every aspect of a subsequently edited story.

## What is compared

`PromptReferenceFreshness` hashes a versioned semantic projection of the resolved
references. Arrays retain their actual conditioning/label order. It includes image
identities, crops, reference names/roles/usage, effective guidance and phases;
selected reel-frame identities, order, crops and notes; video/RefMod representation;
RefMod source-pixel hashes and canvas; and enabled audio sources, excerpts and
speaker mappings. Overridden/unused guidance and disabled reel audio do not enter
the signature. Null crops and explicit full-image crops compare equally.

Save times, document/setup versions, generated binding/frame IDs, keyframe-set
bookkeeping versions, global generation profiles, resolution, seeds, take counts,
and unused media fields are not reference changes. RefMod server filenames,
build IDs, receipt data, recipe keys and VAE filenames are excluded: they are
cache/encoding details, not the appearance shown to the prompt author.

Reverting A → B → A restores the matching reference state without another AI call.
Unrelated library saves cannot dirty all shot prompts. Only effective guidance
for the selected image references participates. This uses application-managed
media identities; it does not rehash every image file during outline rendering
or detect arbitrary out-of-band overwrites of a file under an unchanged identity.

## Persistence

`CompositionPromptRevision.ReferenceFingerprint` is recorded by
`FileProductionStore.AcceptAsync` and `ApplyResultAsync` only, not `SaveAsync`.
History already belongs to shared shot content, so the marker follows the shot
across generation setups. Project export/import carries the markers inside the
existing production history.

Revisions accepted before the marker existed were given their baseline by a
one-time migration on 2026-09-24, derived from matching context fingerprints,
retained takes or the original composition requests. The Shots page no longer
reads AI job history to display status. A revision without a baseline shows
**Review prompt**, not a fabricated comparison. While a project's prompts load,
status reads **Checking…** and the outline shows no badges.

## Regression tests

Run with .NET 10:

```sh
dotnet build src/Lumibelle.Web/Lumibelle.Web.csproj -c Release
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~PromptReference"
```

Tests cover meaningful reference edits, no-op save/serialization, binding-ID
churn, full-image crops, reverting selections, RefMod rebuilds, inactive fields,
voice mappings, generation-setting independence, shared shot history, missing
baselines, strict acceptance and visible badge/filter states.

### Manual check

1. Open a project with an AI-composed/applied prompt and select **All shots**.
2. Save its references unchanged: no new badge. Change one crop: **References changed**.
3. Cancel an un-applied reference edit: saved status stays unchanged. Revert an
   applied crop to its original value: the changed-reference badge disappears.
4. Change output resolution or sampling preset: no changed-reference badge.
5. Compose a new prompt. Until the validated response is applied, retain the old
   status. Apply it: clear the reference warning.
6. Change a reference that does not require different wording; review the text
   and click **Accept prompt**. No AI request is made.
7. Reload and switch global generation setups: retain the shot's status.
8. With **References changed** selected, a repaired shot leaves the filtered list;
   the current editor may remain open. **Show all shots** clears both filters.

9. Open **Bulk operations → Generate takes…**: flagged shots carry the same warning beside their
   title. Reviewing a prompt updates its badge; no additional request is queued.
