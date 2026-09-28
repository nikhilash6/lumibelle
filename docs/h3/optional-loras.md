# Optional H3 LoRAs

1. Put a compatible model LoRA in ComfyUI's LoRA directory.
2. Open **AI settings → LoRAs**, refresh installed files, and register it with **MiniMax H3 Ref2VA** as its workflow. Choose a name, default strength, optional trigger text, and tags.
3. In **Shots → Generate → Add LoRA**, select the registration. Enable, disable, adjust strength, or reorder the selected stack. Changes save with the shot.
4. Use **Insert trigger into shot description** if needed. No trigger is inserted automatically.

Project LoRA visibility filters apply to H3 too. Registrations describe intended usage; Lumibelle checks the exact server, files, and loader contract but cannot certify weight compatibility. Unsupported LoRA formats need conversion or a compatible file; optional LoRAs use stock `LoraLoaderModelOnly`, without patching the text encoder.

Turbo remains a separate video quality setting. Its required LoRA is applied first at the existing strength, followed by optional LoRAs in the selected order, then the existing sigma-shift and attention patches. Standard sampling, dimensions, audio, and frame archives are unchanged.

New shots have no optional LoRAs; duplicates copy the stack. Each batch captures its effective stack. Editing the shot or library cannot change remaining candidates, retries, or **One more take**. Those operations recheck the captured files and loader on the original server. Restore a missing file before retrying; an enabled LoRA is never silently skipped.

**Refine/Rework** inherits the source take's optional stack, retaining its existing refinement sampling without the preview's Turbo LoRA. Take and Trash details show the applied names, paths, strengths, and server. Old takes retain their original behavior with no optional LoRAs.

Source: [ComfyUI model-only LoRA loader](https://github.com/Comfy-Org/ComfyUI/blob/master/nodes.py).
