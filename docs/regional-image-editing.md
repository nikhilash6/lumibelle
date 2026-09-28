# Protected and regional image editing

In Assets, select an image for editing and choose **Edit area / Protect areas…** beside its source controls. This is optional; ordinary image edits keep their existing behavior.

## Choose what changes

- **Protect areas:** paint pixels to hide from the model and restore from the original. Unprotected pixels inside the context crop can change.
- **Edit an area:** paint the pixels to replace. Adjust the **Context crop** to include the surroundings the model needs. Only the edit selection is pasted back.
- Protection always wins where edit and protection selections overlap. Additional references have protection controls but never supply pixels for the final composite.

The blue edit selection controls replacement pixels; it does not automatically crop the model input. **Model input** shows whether the whole image or a crop will be sent. Choose **Crop input to selection** to fit a rectangle around the selected edit pixels and immediately preview the prepared input. Protection remains applied inside that crop.

Use the brush, rectangle, or eraser, with Undo/Redo. Pan and zoom for detail work; the precise rectangle controls provide keyboard access. **Adjust input crop** switches to drawing its rectangle, which can include extra context around the edit. **Use full image** restores the full input boundary. Crop changes support Undo/Redo too.

**Selections** displays edit pixels in blue, protected pixels in purple, and the context boundary as a dashed line. **Original** displays the original image. **What the model receives** displays the actual prepared image, including opaque gray protection and canvas padding. Image-inspecting Assist uses this same preparation.

Apply changes to return to the edit instruction. Cancel leaves the previous selection unchanged. Disable selection returns that input to ordinary cropping. Masks belong to the exact source image; replacing the source does not transfer its mask.

## Review and save

Generate normally. Regional results remain with their request and appear in **Review regional edits**; they are not automatically added to the gallery. Switch between **Original**, **Model result**, and **Combined**, or compare with the original using a slider or side-by-side view.

The combined image uses the original canvas dimensions. The model result is placed using the captured context crop and canvas mapping. There is no automatic alignment or repositioning: changed poses, framing, or lighting can leave a visible join.

**Edge blend** defaults to zero and supports 0–32 source pixels. It blends only inside the editable boundary. Protected pixels and pixels outside the edit selection remain exactly the original decoded RGBA pixels. Changing the blend makes no AI request.

### Match the colours

In a pending result's review, enable **Match original colours** and compare **Combined** with **Original**. The default is off. **Strength** controls the automatic correction; **Manual adjustments** adds Brightness, Warmth, and Tint. **Reset colour adjustments** returns to the raw result's colours and leaves Edge blend unchanged.

Automatic matching compares corresponding visible pixels in unedited context within the captured input crop. It excludes protected pixels, their resampling fringe, padding, transparency, and clipped colours. It estimates a consistent RGB offset, rather than matching the entire image's colour distribution, so intentional colour changes can remain. Inconsistent or insufficient evidence produces **No reliable colour match**; no automatic correction is applied. Manual controls still work.

For a tight crop with little surrounding context, choose **Choose matching area…** and select a background patch or another area that stayed the same in both images. Drag the selection or use its shape and precise keyboard controls, then **Use matching area**. This does not change the input crop or the replacement selection. **Use surrounding context** returns to automatic context sampling. Avoid choosing intentionally changed content as the reference.

Colour correction applies only to replacement RGB pixels before edge blending. Original protected/untouched pixels and replacement alpha are preserved. It does not align images, repair changed geometry, or model arbitrary contrast/lighting differences. These controls reuse the stored model output locally; no new generation or Assist request is made.

Colour and blend drafts are saved separately for each pending take and survive switching takes, Close, reload, and restart. Saved/discarded results are read-only. **Save to Assets** creates an independent, unapproved PNG. Its metadata records its source, selection, canvas mapping, blend, colour settings and match outcome, and generation. **Discard result** removes it from consideration without publishing an image. **Close** retains the output for later review, including after restarting Lumibelle.

An output with incompatible canvas proportions remains available as **Model result**, with an explanation. It cannot be silently stretched into a combined result. A failed save freezes the chosen blend and colour settings with the staged output for retry; repeated Save cannot publish duplicate images. Retries and **One more** retain their captured source preparation.

## Implementation boundary

The original is normalized for orientation and stored separately from the prepared provider image bundle. Masking happens before any resizing. Inputs are fitted into the workflow canvas with neutral padding. Compositing uses only the inverse captured mapping and copies untouched source pixels without resizing them. Provider-specific masking APIs and additional AI analysis are not used.

Pending results use the existing durable image-operation staging. Review decisions are stored per candidate under its job directory, with a separate review lock and the existing image-publication receipts. Ordinary historical image requests have no regional metadata and keep their prior publication path.
