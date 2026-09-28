# Prompt enhancement profiles

The five supplied documents are preserved here unchanged. Runtime profiles are embedded from `Services/AI/PromptProfiles`; Downloads and internet access are not required at runtime. The Krea Edit TXT is the recurring instruction from its companion guide. Evidence notes are documentation only.

Profiles: `krea-create-v1`, `krea-edit-v1`, `klein-create-v1`, `klein-edit-v1`. Klein Create is derived from BFL's official model-family guidance, checked 2026-09-05:
https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/flux2-models.md

For OpenRouter, the application wrapper follows each guide’s native output: one prompt paragraph, or `NEEDS_INPUT:` / `NEEDS_SETUP:` followed by a concise question or requirement. The parser maps these to the structured application result `Prompt`, `NeedsInput`, or `NeedsSetup`; only `Prompt` can be applied. Complete JSON objects containing `kind` and `text` are also accepted. ComfyUI retains the JSON output envelope because its TextGenerate preview does not report a token stop reason. Plain-text responses require a normal provider stop; missing completion signals, truncated responses, and malformed JSON remain non-applicable. Actual application reference limits override general guide limits, without promising quality beyond the documented envelope. No guide is inserted into an image encoder's internal system template.

OpenRouter image capabilities come from `architecture.input_modalities`; images are sent as ordered private byte content, not public media URLs:
https://openrouter.ai/docs/guides/overview/multimodal/image-understanding

Text-only enhancement is supported by both existing providers. Optional image inspection is OpenRouter-only. Checking availability does not perform generation. Enhancement preview and Undo are visit-local; generated images retain the final submitted prompt in existing provenance.
