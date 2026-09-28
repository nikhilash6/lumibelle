# H3 acceleration comparison — 11 September 2026

Larry v4 is the leading candidate for a future explicit acceleration profile.
It shortened the complete pipeline by **31–34%** versus Standard on the two
tested scenes, and the user judged both clips acceptable. PDD is promising for
motion without dialogue, but its dialogue clip was strongly distorted. Neither
finding establishes a general quality ranking from a single seed.

The user ended the study after reviewing the first round. **No production
controls, generation defaults, models, or projects were changed.** Second-seed,
warm-process, Native-resolution, and 243-frame follow-ups were not run.

## Frozen comparison

- RTX 4000 Ada, 20 GiB, one isolated worker; the RTX 3080 was not used.
- Production image `sha256:b3ae356d86c36d82e9900f30ec8891e28ebb1a9e27dbe2bd3f1debe78c33789f`,
  ComfyUI 0.35.0, PyTorch 2.11.0+cu130, comfy-kitchen 0.2.33, comfy-aimdo 0.5.3.
- Current pruned W4A8 Ref2VA model and W4A8 encoder, int8 video VAE, FP32 audio VAE;
  `--use-sage-attention --disable-smart-memory`, dynamic VRAM initialization.
- 141 frames, 24 fps, seed `20260911`; 832 × 480 sampling followed by FP16
  Upscaler-Plus to 1344 × 768. No refinement, trajectory capture, or style LoRAs.
- Two copied approved image references: face and white/black/gold armor. Dialogue
  additionally used a copied 4.248-second voice excerpt, stereo PCM16 at 32 kHz.
- Neutral prompts: a head turn and “The package is here. We can leave now.”;
  a full-body walk, turn, and wave with footsteps and no speech. No screenplay
  text was included.
- Controls came from Lumibelle's actual workflow builder. Benchmark-only graph
  patches were applied to those controls, then validated again by native ComfyUI.

Exact package revisions, weights, reference hashes, settings, workflows, per-stage
measurements, output checksums, and all attempt receipts are in
[the evidence directory](benchmarks/2026-09-11-acceleration/results.json).

## Results

Times are seconds for the complete generation pipeline, including preparation,
sampling, upscaling, decoding, lossless archiving, and MP4 saving. Process startup
and offline output validation are excluded here and included in worker receipts.
Every successful clip passed dimension, frame-count, frame-rate, audio-presence,
and logical lossless-archive validation.

| Recipe | Dialogue total | Motion total | Dialogue sampler node | Motion sampler node | Observed minimum free VRAM | User review: dialogue | User review: motion |
| --- | ---: | ---: | ---: | ---: | --- | --- | --- |
| Standard 20-step | 400.95 | 426.32 | 255.21 | 248.45 | 2.44–2.84 GiB | OK | Good |
| Existing Turbo 4-step | 254.72 | 264.31 | 91.39 | 91.72 | 2.75–2.84 GiB | OK | Poor; weird interiors |
| Existing Turbo 8-step | 294.41 | 307.70 | 135.51 | 132.60 | 2.75–2.81 GiB | OK | Poor; weird interiors |
| Standard + Spectrum | 335.81 | 354.20 | 179.45 | 177.60 | 2.81–2.84 GiB | OK | Poor; weird interiors |
| Alibaba PDD Ref2VA, 8 evaluations | 289.68 | 295.98 | 126.88 | 124.34 | 2.75–2.81 GiB | Strongly distorted | Good |
| Larry v4 step600 EMA, 6 steps | 276.37 | 282.09 | 116.96 | 113.88 | 2.75–2.84 GiB | OK | Good |
| Standard + Sol-Attn | Failed | Failed | — | — | No completed comparison | Not assessable | Not assessable |
| Standard + FirstBlockCache Safe | Failed | Failed | — | — | No completed comparison | Not assessable | Not assessable |

The sampler-node timings also include model activation/loading; they are not
pure denoiser timings. For the first Standard dialogue control, progress reported
about 215 seconds of denoising inside the 255-second sampler node. Fresh processes
were used throughout, but filesystem/kernel caches were not reset. The first
control's preparation and initial upscaler load differ from later runs; these
single observations are not statistically controlled cold-cache measurements.

The twelve successful clips used 17.15–17.56 GiB sampled device peaks and
21.82–24.03 GiB peak worker RSS. All met the provisional 2 GiB free-headroom
threshold at the tested size. Device samples are lower bounds at 100 ms intervals;
PyTorch allocated/reserved counters describe this process and are unavailable
when reported as unsupported/zero by cudaMallocAsync. The measurements include
conditioning, loading, sampling, the learned upscaler, VAE decode, and saving.

## Recipe and failure evidence

- **Spectrum:** pinned default recipe with debug telemetry, RAM history and RAM
  offline archive, audio blending zero, offline smoothing replay enabled. Both
  runs recorded **11 actual transformer calls, 9 forecasts, 20 transformer-free
  replay calls, and zero fallbacks**. Its 16–17% total saving did not compensate
  for the user's motion-scene quality concern.
- **Larry:** dedicated pruned-model LoRA loader, v4 step600 EMA, strength 1,
  `low_vram=false`, custom Turbo sampler, simple schedule, six steps. No additional
  acceleration patches. Most promising overall in this limited comparison.
- **PDD:** dedicated Ref2VA LoRA/head-bank loader, strengths 1/1, eight supplied
  sigma intervals, Euler, video/audio shifts 12/3, strict off-grid and partition
  checks. The apparent motion quality and 28–31% total saving do not establish
  suitability for speech. The source of the dialogue distortion was not diagnosed.
- **Sol:** both application-built graphs passed static validation but failed
  before sampling with `BlockSparseAttention.execute() missing 1 required positional
  argument: 'selection'`. This is a node-input integration failure, not evidence
  against the underlying acceleration method. No graph workaround was tested.
- **FirstBlockCache:** both Safe-preset workers terminated with exit 134 and
  `c10::AcceleratorError: CUDA error: invalid argument` in
  `CUDAMallocAsyncAllocator::free_impl`. The log reported zero cached steps out
  of one observed model call. This does not establish an OOM or a universal
  incompatibility; no allocator, precision, or attention workaround was attempted.
- One initial baseline attempt failed before generation because the new harness
  omitted required executor cache arguments. The correction and successful retry
  have distinct receipts; no failed time was erased.

The controller accounted for **17 worker attempts / 72.33 minutes**, including
five failures. CPU-only contract validation attempts are stored separately. No
production work interrupted a run. The isolated container was stopped afterward.

## Quality assessment and next implementation scope

Quality labels above are the user's direct review of the complete clips, recorded
separately from objective output validation. Likeness, exact spoken-word accuracy,
voice similarity, and lip synchronization were not individually scored; no
automated perceptual metric or audio transcript was produced.

A future implementation can prioritize an **optional Larry 6-step profile**,
retaining Standard and current defaults. PDD should remain an explicit experimental
choice, with its observed dialogue limitation visible. Spectrum and the existing
Turbo profiles should not be promoted as equivalent-quality motion replacements
on this evidence. Other GPUs, seeds, longer clips, Native generation, and stacking
remain unverified.

The full local archive is `artifacts/h3-acceleration-20260911/`, including clips,
references, downloaded packages/weights, raw logs, memory samples, and the
comparison page. These large/private inputs remain ignored. Serve it manually:

```powershell
python -m http.server 8791 --bind 127.0.0.1 --directory artifacts/h3-acceleration-20260911
```

The committed evidence contains no source reference media, project documents,
model weights, or production credentials. Checksums connect it to the retained
local files.

Validation at delivery: 20 benchmark CPU tests passed; the .NET exporter built
with zero warnings/errors. The local page passed paired-playback, column sorting,
and 390-pixel mobile overflow checks with twelve available clips and no JavaScript
errors. Desktop/mobile screenshots were inspected and retained in the local
archive. The application test suites and live GPU follow-ups were not rerun for
this tooling-only commit.
