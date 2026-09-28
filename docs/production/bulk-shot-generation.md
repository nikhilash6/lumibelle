# Bulk shot generation

Use **Bulk operations → Generate takes…** in Shots to queue selected shots with a common H3 preset, resolution and take count. Each request retains its shot's prompt, references, voices and seed. The overrides affect only the captured requests; saved shot and global setup settings stay unchanged.

The currently open shot uses its selected setup. Other shots use their first active setup whose global settings are available. Generation and deletion share the same thumbnail picker, with search and a scene filter. The generation picker reports missing prompts, references and active video jobs beside unavailable shots. **Select all shown** adds eligible visible shots to the selection; **Clear selection** clears the entire selection. Filtering never changes selections, and a count identifies selected shots outside the current view.

The current editor is saved before queueing. Generation settings and selection remain locked while the requests are captured. Successfully queued shots leave the selection, while individual failures retain their shot titles and remain available for retry.

If enqueueing loses its acknowledgement, the chooser retains the exact captured request and retries its identity instead of creating another batch. Resolve that pending acknowledgement before closing or navigating away. Queued work continues through the existing provider queue and can be managed in AI activity.

Validation covers immutable preset and resolution overrides, take counts and seeds, lost-acknowledgement retry, and close protection. Media generation still depends on the capabilities reported by the configured ComfyUI server.
