# H3 preview performance

Use **AI settings → Video models → MiniMax H3 Ref2VA → Advanced**, then **Save MiniMax H3**.
Defaults are Server default attention, Sol-Attn off, and Fast lossless compression.
These settings apply to newly captured normal H3 batches. One more take and transfer
retries keep their batch's captured choices. Image generation, Refine, and Rework
keep their existing graphs.

## Attention choices

| Choice | Graph adapter | Requirement |
| --- | --- | --- |
| Server default | None | Existing ComfyUI launch configuration; also respects `--use-sage-attention` |
| PyTorch | `ModelAttentionBackend` | Native `pytorch attention` option |
| Comfy Kitchen | `ModelAttentionBackend` | Native `comfy kitchen attention` option and matching dependencies |
| SageAttention | `PathchSageAttentionKJ` | KJNodes and a compatible `sageattention` installation |

Explicit Sage uses `auto` with compilation disabled. Install dependencies into
ComfyUI's own Python environment, restart ComfyUI, and refresh Video models. Lumibelle
does not install anything or require a Sage launch flag for this choice. The unusual
`Pathch` spelling is the upstream node ID. Follow [KJNodes setup](https://github.com/kijai/ComfyUI-KJNodes)
and [SageAttention installation instructions](https://github.com/thu-ml/SageAttention).
GPU, CUDA, PyTorch, and kernel compatibility still need execution testing.

The dense adapter follows the existing model/LoRA/shift patches. Optional Sol-Attn
follows it. The adapter does not change the text encoder, sampler, scheduler, shifts,
steps, references, dimensions, duration, or audio settings. Node/input contracts are
checked again before submission; an unavailable requested mode blocks generation.

Sol-Attn is experimental and may change results. Profile `h3-performance-v1` pins
adaptive tau 1.3, schedule 0.2–1.0, minimum 12,288 tokens, 256 extra tokens, no forced
dense blocks, and `exact_kv_and_rows`. Ineligible portions use the dense backend.
Verbose native diagnostics are enabled. A catalog check establishes the node contract,
not actual sparse execution or acceleration.

Implementation references, checked September 7, 2026:
[native dense selector](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_model_advanced.py),
[KJ Sage patch](https://github.com/kijai/ComfyUI-KJNodes/blob/main/nodes/model_optimization_nodes.py),
[native sparse attention](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_sparse_attention.py).

## Lossless compression

Both modes use `SaveAnimatedWEBP`, `lossless=true`, and `quality=80`. Fast selects
`method=fastest` (libwebp 0); Compact selects `method=default` (4). The existing
24-frame segmentation, frame dimensions, logical frame count, metadata stripping,
and native integer-millisecond timing remain unchanged. Static frames may coalesce;
the archive manifest still records every logical frame. Older snapshots without a
performance profile continue using Compact and no attention override, even after
global defaults change.

The initial local 24-frame CPU test measured 8.2 seconds for Compact and 2.4 seconds
for Fast, with identical decoded RGB pixels and approximately 23% larger files.
This measures encoding only, not total generation speed. Size and time depend on
content, CPU, and libwebp version.

A subsequent full 362-frame, 832×480 archive check on this computer (Pillow 12.3.0,
libwebp 1.6.0) measured **121.6 → 29.3 seconds** across all 16 segments. Compact
produced 145,077,100 bytes; Fast produced 181,578,728 bytes (25.2% larger). Every
decoded RGB pixel, logical frame, and frame delay matched. The existing archive
was only read; this was CPU re-encoding, not a new H3 generation.

Reproduce pixel/timing checks with Python and `Pillow==12.3.0`:

```powershell
python tests/media/check_h3_archive_compression.py
# Optional: read and re-encode all segments from an existing 362-frame take.
python tests/media/check_h3_archive_compression.py --archive-directory "<take directory>" --frame-count 362
```

The check holds encodings in memory and leaves originals untouched. It exercises
362-frame landscape/portrait sequences, moving transparent deltas, duplicate frames,
static segments, and the final short segment. It reports encoder time separately
from decoded-pixel and timing validation.

## Timings and a manual GPU comparison

Take review → **Performance and timings** shows the captured configuration and
observed preparation, sampling, decoding/MP4, archive, and transfer/staging-save times.
Measurements use a monotonic clock and observed ComfyUI node boundaries, exclude
queue waiting, and are not GPU profiling. Transfer includes validation and staging;
final library publication is excluded. Incomplete observation, reconnects, and old
recovered jobs show unavailable measurements instead of fabricated durations. The
request and timing receipts are retained with the durable AI job.

Keep Server default until matched comparisons justify changing it:

1. Choose representative dialogue and substantial-motion shots. Save the exact
   submitted graph, references, crops, seed, dimensions, frame count, and sampling
   settings from the job's `operations/*/request.json` receipt. The graph is in its
   `workflow.prompt` property; receipts live under `App_Data/ai-jobs/<job ID>`.
2. In a deliberate ComfyUI benchmark, repeat that graph with a unique output prefix
   and the same seed. Change only the dense adapter and then Sol on/off; leave model
   files, LoRAs, shifts, audio, steps, and reference order fixed. Ordinary One more
   take deliberately chooses a new seed, so it is not a matched benchmark.
3. Avoid cached sampler outputs when measuring execution. Separate cold model
   loading from warm runs and log the server revision, launch flags, dependencies,
   hardware, and whether native verbose diagnostics report sparse or dense paths.
   Use a profiler if kernel verification is needed; node presence alone is not proof.
4. Compare observed timings and total completion time alongside identity, motion,
   dialogue clarity, lip sync, audio artifacts, and reference fidelity. Measure both
   short and long shots; record failures and dense fallbacks as well as successes.

Automated validation uses mocked providers. Live matched GPU quality/performance
comparisons remain a manual evaluation; this implementation makes no measured claim
about a particular attention backend being faster on the author's hardware.
