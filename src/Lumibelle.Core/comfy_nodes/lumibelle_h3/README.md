# Lumibelle H3 companion, protocol v1

Copy this complete `lumibelle_h3` directory into `ComfyUI/custom_nodes/`, then restart ComfyUI. Use ComfyUI's Python environment, which must contain PyTorch and SafeTensors. No extra inference model is required for package capture.

This companion is optional. Normal H3 video generation works with stock ComfyUI's H3 nodes and the required model files; no refinement nodes or upscaler are needed.

In Lumibelle, open **AI settings → Video models → Refresh video models**. When the capture node is available, new batches automatically retain independent refinement data. Without it, takes save normally as MP4 and lossless WebP. The capture choice is fixed for the entire batch, including added takes. Takes without saved refinement data remain playable and can supply frames, but installing the companion later cannot reconstruct that data from compressed video or lossless frames.

For Refine and Rework, also install the supported learned upscaler:

```sh
cd /path/to/ComfyUI/custom_nodes
git clone https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler.git
git -C Comfyui_Minimax_h3_latent_Upscaler checkout --detach d7c01b9011f2e8439493f6c02c29995a27df276f
```

Place `minimax_h3_latent_upscaler_3d_bf16.safetensors` in `ComfyUI/models/latent_upscale_models/`. Restart ComfyUI after installing both node packages. Refresh Video models, select the exact installed upscaler checkpoint, and save. Installing code and restarting your server are explicit setup operations; Lumibelle never installs custom nodes itself.

Both refinement profiles are experimental. They use Standard 20-step sampling with the source Ref2VA base model, `res_multistep`, simple scheduling, and denoise 0.35 (Refine) or 0.65 (Rework). Preview Turbo LoRAs are omitted. Refine locks audio diffusion and retains the original encoded audio stream in the local MP4; Rework regenerates audio. Larger sizes can require substantial GPU memory. Errors never silently lower resolution.

## Data and transfer

- `LumibelleH3CaptureV1` saves fully denoised joint video/audio latents, conditioning, and generation context in a SafeTensors file with a versioned JSON manifest.
- `LumibelleH3LoadV1` validates and loads the package. `LumibelleH3PrepareV1` rebuilds conditioning for the enlarged canvas and applies the audio mask/noise policy.
- Only fixed primitive/tensor types are supported. There is no pickle, `torch.load`, or arbitrary object deserialization in this companion.
- GET `/lumibelle/refinement/v1/{token}` streams one output package. POST `/lumibelle/refinement/v1/upload` accepts a multipart `package` into a random input filename. Tokens are exact lowercase 32-digit hexadecimal IDs, never filesystem paths.
- Server files live under `input/lumibelle/refinement/` and `output/lumibelle/refinement/`. They are transfer copies; Lumibelle keeps the durable package with each local take and copies queued inputs independently. Server transfer copies are not automatically removed in this version. Remove them only when no active or recoverable Lumibelle jobs need them.
- Package size is capped at 8 GiB and header size at 16 MiB. Tensor dimensions, frame alignment, dtypes, conditioning membership and finite values are validated. This does not authenticate arbitrary third-party custom nodes; use a trusted ComfyUI installation.

The complete design and pinned evidence are in Lumibelle's `docs/h3/refinement.md`; CPU tests are in `tests/companion/`.
