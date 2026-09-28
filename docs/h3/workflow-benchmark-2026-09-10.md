# H3 workflow study — September 10, 2026

Status: **completed for the bounded scope below**. Nine GPU cases completed,
producing 21 clips; total worker time including four excluded attempts was
92.2 minutes. This study adds research tooling and findings only.
Production settings, queued generation packages, public APIs and the existing
LBH refinement adapter are unchanged. Saved-take refinement is excluded; the
experimental second passes below run within the benchmark execution without any
Lumibelle companion node.

## Integration shortlist

These are recommendations for a later implementation pass, bounded by the tested
73-frame/141-frame cases. They do not change any production default.

| Option | Evidence | Recommendation |
| --- | --- | --- |
| Native Turbo8 | Fastest tested direct control; 73 frames, one seed | Retain as the simple speed option; it needs neither upscaler nor Flow nodes. |
| Standard progressive 0.80 | Around 19–20% less sampling time across two seeds; comfortable at 73 frames | First optional integration candidate for faster Standard generation. Require Plus learned handoff and Flow in one execution. Do not label it a low-VRAM mode. |
| Standard progressive 0.70 | About 28% less sampling time than Standard; comfortable in one 73-frame seed | Promising additional tuning option, with less evidence than 0.80. No clear quality penalty on this gentle shot, but broader quality equivalence is unverified. |
| Preview + learned upscale only | Both implementations work at 73 and 141 frames; softer/rougher fine detail than the finish | Useful optional fast-output path when its detail is acceptable. Support LBH or Plus through their respective contracts; standalone wall time is still unverified. |
| Separate direction-guided finish | Nonzero guidance executed, but no decisive quality benefit on the matched short clip | Keep experimental; insufficient evidence to add it as a default finish. |
| Eight-step post-upscale finish | Sharper reconstructed detail, but adds about 115s at 73 frames and 291s at 141; long case is tight | Defer a public later-refinement feature as requested. These same-execution tests do not depend on Lumibelle companion nodes. |

LBH and Plus share a registration ID with different inputs. Do not load both into
one production node registry. The benchmark imported the pinned packages under
separate Python module names and selected each class explicitly. Preserve the
existing LBH adapter; a future Plus adapter must validate its own node contract.

No test supports pooling the second GPU's memory, automatic parameter fallback,
or guarantees for longer progressive clips. The 0.70 sweep was evaluated last.

## Setup and evidence

See [the harness README](../../tools/h3_benchmark/README.md) for pinned revisions,
the isolated container command and measurement definitions. The benchmark uses
only the 20 GB RTX 4000 Ada, with SageAttention, native dynamic VRAM management,
FP16 learned upscaling, upscaler offloading and system-RAM trajectory storage.
The second GPU contributes no memory or compute to the test.

Local evidence is retained under `artifacts/h3-benchmark/`: `preflight.json`
contains checkpoint and core-source hashes; each result contains the executed
call manifest, harness snapshot, memory samples, stage measurements, detailed
ComfyUI log, MP4s and lossless decoded-frame archives. The call manifests are
receipts for direct Python node execution, not ComfyUI API prompt graphs.
`comparison.json` and `comparison.md` are generated from those receipts;
`clips.html` provides full-video playback with per-output time, resolution, frame
count/rate, seed and VRAM, plus expandable stage details. `quality-review.md` records review
scope. These ignored artifacts must be copied separately when sharing the study.

The frozen 73-frame shot is a gentle head turn, using one existing adult character
reference, seed 20260910 and 24 fps. Final output is 1344 × 768. Two-pass previews
use 832 × 480. The reference, prompt and encoder settings stay fixed within each
matched comparison. No speech was requested, so dialogue synchronization cannot
be assessed from this scene.

Each case starts a fresh worker, including model loading, with potentially warm
OS file caches. Branches within a comparison reuse the first pass and process;
their timings have cache/order effects. They are not independent warm/cold repeats.
The host remained interactive, including Lumibelle use and CPU frame review;
CPU preparation/saving measurements may also reflect that contention.
The controller checks both ComfyUI and Lumibelle's durable queue and yields by
terminating only its isolated worker if production work arrives. Interrupted
attempts are retained but excluded from performance and quality comparisons.

## Completed controls

Seconds below sum recorded pipeline stages. Worker wall time additionally includes
Python/ComfyUI startup and controller overhead. Saving includes MP4 encoding and
lossless frame archives. Device peaks include baseline use and are sampled lower
bounds; free headroom is observed, not a guaranteed operating margin.

| Configuration | Prep s | Sample s | Decode s | Save s | Stage sum s | Worker s | Peak GiB | Minimum free GiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Standard20 (`standard-04`) | 98.03 | 345.83 | 31.47 | 25.60 | 500.92 | 510.95 | 17.18 | 2.82 |
| Native Turbo8 (`turbo8-01`) | 98.57 | 167.13 | 30.48 | 26.29 | 322.47 | 331.99 | 17.43 | 2.57 |

Both qualify as **comfortable** under the provisional 2 GiB observed-headroom
criterion. Peak worker RSS was approximately 22.05 GiB in both; minimum Linux host
available memory was 5.87 GiB for Standard and 5.98 GiB for Turbo. Existing swap
use stayed near 3.4 MiB. Host memory pressure matters independently of GPU memory.

Turbo8 reduced worker wall time by about 35% and sampling time by about 52% in
this pair. All 73 frames of both clips were inspected in ordered contact sheets,
with full-resolution checks and browser playback. Both preserve recognizable
identity and a coherent head turn. Turbo has glossier skin and stronger facial
idealization; neither demonstrates a decisive general quality advantage on this
gentle scene. Minor background/framing drift remains in both.

Encoded dimensions, frame count, rate, audio presence and archive frame counts
passed. Neither completed control logged an OOM or tiled-decode retry. Audio has
not been perceptually reviewed, and lip sync is untested. These results support
Turbo8 as a speed control, not a universal quality recommendation.

## Short LBH versus Plus comparison

`comparison-02` completed in 656.17 worker seconds, including both branches and
five diagnostic/final encodes. Both upscalers produced **identical video latents**;
both matched second passes also produced identical video latents. All eight
corresponding lossless WebP archives match byte-for-byte, covering every raw and
refined frame. Decoded audio hashes match the preview for all outputs. MP4 file
hashes differ, which does not imply different decoded images.

| Branch | Prep s | Sample s | Upscale s | Decode s | Save s | Stage sum s | Peak GiB | Minimum free GiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| LBH + experimental finish | 76.92 | 252.57 | 7.58 | 30.24 | 23.85 | 391.17 | 17.43 | 2.57 |
| Plus + experimental finish | 76.92 | 253.31 | 1.47 | 16.81 | 23.19 | 371.70 | 17.43 | 2.57 |

The first pass took 137.48 seconds; second passes took 115.09 and 115.83 seconds.
The shorter preparation versus direct controls includes a smaller reference-image
encoding target under the same `match` setting. Plus ran second and its decode
was warm, so its lower upscaling/decoding time is **not evidence of a package speed
advantage**. A standalone pipeline timing would need a separate repeat.

The 17.43 GiB whole-run peak occurred during reference conditioning. Refinement
peaked around 15.43 GiB, leaving 4.57 GiB free, but that does not make the whole
workflow a lower-memory configuration. No OOM, explicit retry or native tiled
decode fallback occurred. Both pass the provisional comfortable margin.

All 73 refined and raw-upscaled frames were inspected, including full-resolution
end frames. The turn is coherent and settles into a side view. Framing and
identity are stable, with modest facial idealization and minor background drift.
The raw upscale is softer in hair, face and cloth detail; the second pass sharpens
them without an obvious temporal discontinuity. There is no quality distinction
between LBH and Plus at this unchunked length because their decoded pixels match.
The preview was checked at a representative frame; complete-preview playback and
perceptual audio review are not established by these contact sheets.

For an upscale-only option, recorded pipeline stages sum to 262.75 seconds with
LBH and 255.90 seconds with Plus, including their shared preview generation and
only that raw output's decode/save. Both observed 17.43 GiB peak / 2.57 GiB free.
Their decodes ran after refinement, so these are branch accounting figures rather
than independently measured standalone upscale-only wall times.

## Longer LBH versus Plus comparison

`long-01` completed all five outputs in 1240.16 worker seconds. At 141 frames
(5.875 seconds), LBH's temporal chunking activated: 42 latent frames were processed
in segments of 42 and 25, including the five-frame overlap context. Plus processed
the full 42. The largest LBH segment therefore is not smaller at this duration.

| Branch | Prep s | Sample s | Upscale s | Decode s | Save s | Stage sum s | Peak GiB | Minimum free GiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| LBH + experimental finish | 61.58 | 529.08 | 4.81 | 38.71 | 44.15 | 678.34 | 18.30 | 1.69 |
| Plus + experimental finish | 61.58 | 528.56 | 2.30 | 32.60 | 43.79 | 668.84 | 18.30 | 1.69 |

Both finished without OOM or a fallback, but qualify as **tight**. The shared
first pass took 237.84 seconds. Refinement took 291.24/290.72 seconds and caused
the 18.30 GiB device peak. Upscaling itself peaked at 16.47/16.44 GiB, leaving
3.53/3.56 GiB free. This case demonstrates no useful upscaler memory advantage
from chunking. Peak process RAM was 22.02 GiB; minimum host available RAM 6.48 GiB.
The worker includes all diagnostics; per-branch times retain the earlier cache
and execution-order limitations.

Upscale-only pipeline sums are 380.82/378.03 seconds, with 17.05 GiB peak and
2.94 GiB observed free across the selected stages. This retains a comfortable
margin for the observed raw-output stages; the full multi-branch worker remains
tight because it also executes refinement. A standalone upscale-only process
could have different residency/cache behavior and has not been timed separately.

All 141 frames of both refined and both raw-upscaled clips were inspected in
ordered sheets. The refined clips preserve recognizable identity and coherent
head motion, with no obvious seam or flash around frames 96–119. Latent boundary
32 maps approximately to decoded frame 107 under H3's temporal compression,
with a broader neighborhood affected by overlap and VAE context.

Outputs are no longer pixel-identical. Mean absolute RGB difference is 0.43/255
for raw upscales and 0.63/255 after refinement; these are descriptive differences,
not quality scores. No decisive LBH/Plus visual advantage was found on this shot.
Raw upscaling leaves rougher headband/cloth detail and a softer face. The second
pass reconstructs these details and slightly changes expression. Both preserve
the original audio latent and decoded PCM exactly. Dimensions, frame count,
rate and archives pass; perceptual audio remains unverified.

The measurements support compatibility at this tested length, not a general
memory claim for longer clips. LBH's chunking may matter at much greater temporal
lengths, which have not been tested.

## Direction-guided finish

`guided-01` completed in 636.08 worker seconds, including both finishes and
diagnostic encodes. The first pass recorded 20 trajectory samples, occupying
65,894,400 bytes (62.84 MiB) in system RAM. Both finishes received the exact same
upscaled video latent and seed. Unguided video latents match the earlier LBH/Plus
result. The guided finish recorded eight nonzero direction corrections and exact
locked audio. No OOM, fallback or sampling-instrumentation failure was observed.

Unguided refinement took 115.71 seconds and guided refinement 116.20 seconds;
both observed the same 15.43 GiB refinement peak, with 4.57 GiB free. Whole-run
peak was 17.05 GiB (2.94 GiB free), again set by preparation. Paired pipeline-stage
sums were 368.28 and 358.61 seconds, but the second branch's warmer decode makes
these unsuitable for claiming guidance is faster.

All 73 guided frames and the full-resolution last frame were reviewed against
the unguided result. Composition, head-turn timing and likeness remain very close.
There are subtle texture/tone changes, with no decisive identity, motion or
flicker advantage on this scene. Mean absolute RGB difference is 1.85 levels out
of 255 across frames; this measures change, not quality. Direction guidance
remains an optional experimental treatment, not a supported default based on
this sample alone.

## Progressive generation at 0.80 source scale

`progressive-01` completed in 394.69 worker seconds. Its sampled resolution was
1088 × 608 after alignment, then 1344 × 768. The full 20-step schedule executed
13 low-resolution evaluations, one exact handoff probe and seven high-resolution
evaluations: **21 actual model evaluations**, not 20. The learned transfer used
the pinned FP16 upscaler on CUDA, returned it to CPU afterward, and took 1.88
seconds. Seven nonzero direction corrections were recorded. No fallback or OOM
occurred; dimensions, 73-frame count, 24 fps, audio presence and archives passed.

| Prep s | Sampling excluding upscale s | Upscale s | Decode s | Save s | Stage sum s | Peak GiB | Minimum free GiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 62.94 | 276.25 | 1.88 | 21.22 | 24.12 | 386.41 | 17.18 | 2.82 |

Sampling including the transfer was about 19.6% shorter than Standard's measured
sampling phase. Worker wall time was 22.8% lower, but preparation/cache variability
also contributes to that number. Whole-run GPU peak and observed free headroom
matched Standard; this is a speed result, not demonstrated whole-run VRAM savings.
Peak process RSS was 22.05 GiB and minimum host available RAM 6.51 GiB.

All 73 frames and the full-resolution last frame were reviewed. The recognizable
subject turns smoothly into profile and remains stable, with no obvious frame
break or exposure flicker. Framing differs from the direct control; the headband
is close to/cut by the top edge at the start, and fine detail is somewhat soft.
The clip is promising as an optional faster Standard workflow, but it does not
reproduce the direct control's composition.

### Second seed

The matched seed 20260911 pair also completed with 73 frames, unchanged reference,
prompt and model settings. Standard's sampling phase was 345.87 seconds, almost
identical to the first seed's 345.83. Its lower whole-worker time demonstrates
why preparation/cache variation must be separated from sampler speed.

| Configuration | Prep s | Sample s | Upscale s | Decode s | Save s | Stage sum s | Worker s | Peak GiB | Minimum free GiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Standard20 | 59.77 | 345.87 | 0 | 23.61 | 28.98 | 458.23 | 467.11 | 17.18 | 2.82 |
| Progressive 0.80 | 59.94 | 278.63 | 1.89 | 26.28 | 22.69 | 389.43 | 398.06 | 17.55 | 2.44 |

Progressive sampling including learned transfer was **18.9% shorter**; whole-worker
time was **14.8% shorter**. Together with the first seed, this supports a repeatable
sampling-speed benefit around 19–20% for this scene and configuration. The same
13 low + one probe + seven high evaluations and nonzero guidance were verified.
Both passed the comfortable memory margin without OOM or fallback. Progressive
did not improve peak VRAM; its observed peak was slightly higher in this repeat.

All 73 frames and full-size last frames of both repeats were inspected. Both
preserve recognizable identity and coherent motion. Progressive uses a wider
composition and turns in the opposite screen direction toward its generated
doorway; its headband fits better than the tight Standard framing in this seed.
Fine detail remains soft and faces somewhat idealized. No obvious temporal break
was found, but differing compositions prevent a simple pixelwise quality ranking.
This is an optional speed treatment, not a guarantee of the same shot more quickly.

## Progressive generation at 0.70 source scale

The final `progressive-070` run used seed 20260910, aligned source resolution
928 × 544 and final 1344 × 768. It executed the same 13 low + one probe + seven
high evaluations with a learned FP16 transfer and nonzero direction corrections.
The upscaler returned to CPU after transfer. No fallback or OOM occurred.

| Prep s | Sample s | Upscale s | Decode s | Save s | Stage sum s | Worker s | Peak GiB | Minimum free GiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 57.27 | 247.87 | 1.89 | 27.12 | 28.28 | 362.43 | 371.63 | 17.30 | 2.69 |

Sampling including transfer was 27.8% shorter than the first-seed Standard
control and 10.2% shorter than progressive 0.80 with the same seed. Worker time
was only 5.8% below that 0.80 run, illustrating the contribution of preparation,
decoding and archiving. Observed VRAM remains comfortable, without demonstrating
a whole-run memory improvement over Standard.

All 73 frames and the full-resolution last frame were inspected. Identity is
recognizable, the head turn is coherent and no obvious discontinuity or exposure
flash appears. Fine detail remains soft and composition differs again from both
the direct control and 0.80. No decisive quality penalty was found on this gentle
shot, but one seed is insufficient to establish quality equivalence for a default.

## Validation and limits

- Ten CPU tests pass, covering instrumentation endpoints/failures, branch timing,
  diagnostic-stage exclusion, progressive time accounting, execution evidence,
  interrupted-run presentation and exact pixel comparisons. All 26 native node
  call contracts bind in the pinned image with CUDA disabled. Python compilation
  and PowerShell parsing pass.
- Completed manifests retain identical model settings, node pins and checkpoint /
  reference fingerprints. Final clips validate dimensions, frame counts, 24 fps,
  audio-stream presence and lossless frame archives. Paired locked audio matches
  in latent and decoded PCM form. Guided/progressive paths recorded actual nonzero
  corrections and learned transfers where requested.
- Three attempts yielded to production work. One early attempt was stopped after
  detecting a legacy-offloading bootstrap mismatch, then the worker was corrected
  to reproduce native DynamicVRAM startup. None produced a completed comparison
  clip. They are preserved, counted in worker time and excluded from performance
  recommendations; the setup stop was not an OOM measurement.
- No completed case needed a memory-saving retry or logged an OOM/tiled-decode
  fallback. NVML peaks are sampled lower bounds; PyTorch allocated/reserved
  counters cover a different allocation scope and are exported per stage.
  Handoff GPU subphases and PCIe transfer volumes are not independently measured.
- The clip page was checked on desktop and at narrow width without horizontal
  overflow. Timing disclosures respond to Enter. Browser playback/looping was
  verified, alongside all-frame visual review of the compared final and raw
  outputs. This is not a perceptual audio evaluation or an objective quality score.
- The study uses one reference and gentle motion, with no dialogue. Speech sync,
  harder motion, longer progressive durations, other output resolutions and broad
  seed coverage remain unverified. There is no universal quality ranking or VRAM
  guarantee. Continuum, tiled resampling, above-native output and additional
  acceleration combinations remain deferred.

The isolated benchmark container was stopped after validation. Lumibelle,
production ComfyUI and the clip viewer each returned HTTP 200 afterward. The
workflows, receipts, metrics and clips remain in the local artifact directory.
