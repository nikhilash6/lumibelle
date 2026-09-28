"""Build a local, auditable timing table and clip index from benchmark receipts."""
import argparse
import csv
import html
import json
from datetime import datetime, timezone
from fractions import Fraction
from pathlib import Path

from metrics import GIB, atomic_json, memory_classification


def read_json(path, default=None):
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else default


def stage_category(name):
    node_type = name.rsplit('/', 1)[-1]
    application_nodes = {
        'SamplerCustomAdvanced': 'sampling',
        'LTXVSeparateAVLatent': 'upscaling', 'MinimaxH3LatentUpscaler3D': 'upscaling', 'LTXVConcatAVLatent': 'upscaling',
        'VAEDecode': 'decoding', 'VAEDecodeAudio': 'decoding', 'CreateVideo': 'decoding',
        'SaveVideo': 'saving', 'RebatchImages': 'saving', 'SaveAnimatedWEBP': 'saving',
    }
    if node_type in application_nodes:
        return application_nodes[node_type]
    if "sampling" in name:
        return "sampling"
    if name.endswith("/upscale"):
        return "upscaling"
    if "/decode-" in name:
        return "decoding"
    if "/encode-" in name or "/archive-" in name:
        return "saving"
    return "preparation"


def candidate_rows(run_name, manifest, measurements, controller_status, flow_metrics=None, include_diagnostics=False):
    if controller_status != "complete" or measurements.get("status") != "complete":
        return []
    outputs = {x["label"]:x for x in manifest.get("outputs",[])}
    rows = []
    for label, output in outputs.items():
        if not include_diagnostics and (label == "preview" or label.endswith("-upscaled")):
            continue
        # Common loading/conditioning/base sampling plus just this branch's
        # stages. A raw output includes its own upscale, never its later finish.
        upscale_stage = label.removesuffix("-upscaled") + "/upscale" if label.endswith("-upscaled") else None
        stages = [s for s in measurements["stages"] if manifest.get('case') == 'preview-app' or "/" not in s["name"]
                  or s["name"].startswith(label+"/") or s["name"] == upscale_stage]
        times = {name:0. for name in ("preparation","sampling","upscaling","decoding","saving")}
        for stage in stages:
            times[stage_category(stage["name"])] += stage["seconds"]
        # Progressive transfer runs inside the sampler call. Separate only the
        # reported learned-upscaler time, without double-counting its enclosing
        # handoff or low/high-stage wall events. Their memory peaks are not
        # independently instrumented: the base-sampling peak includes them all.
        progressive = {}
        if manifest.get("case") == "progressive":
            for event in (flow_metrics or {}).get("events", []):
                kind, fields = event.get("kind"), event.get("fields", {})
                if kind in ("low_stage_wall", "high_stage_wall", "handoff_transfer_wall", "handoff_learned_upscale_wall"):
                    milliseconds = fields.get("elapsed_ms")
                    if isinstance(milliseconds, (int, float)) and milliseconds >= 0:
                        progressive[kind] = progressive.get(kind, 0.) + milliseconds / 1000
            learned = progressive.get("handoff_learned_upscale_wall", 0.)
            if learned > times["sampling"]:
                raise ValueError("Progressive upscaler time exceeds its enclosing sampler stage")
            times["sampling"] -= learned
            times["upscaling"] += learned
        observed = [s["sampled_minimum_device_free"] for s in stages if s.get("sampled_minimum_device_free") is not None]
        free = min(observed) if observed else None
        peaks = [s["sampled_peak_device_used"] for s in stages if s.get("sampled_peak_device_used") is not None]
        rows.append(dict(run=run_name,label=label,seed=manifest.get("seed"),frames=manifest["frames"],seconds=times,
            stage_total_seconds=sum(times.values()),pipeline_sampled_peak_device_used=max(peaks) if peaks else None,
            pipeline_minimum_device_free=free,classification=memory_classification(free),
            whole_run_peak_device_used=measurements.get("sampled_peak_device_used"),
            whole_run_minimum_device_free=measurements.get("sampled_minimum_device_free"),
            whole_run_peak_process_rss=measurements.get("peak_process_rss"),
            whole_run_minimum_host_available=measurements.get("minimum_host_available"),
            whole_run_elapsed_seconds=measurements.get("elapsed_seconds"),
            torch_counter_stages=sum(bool(s.get("torch_counters_available")) for s in stages),
            torch_stage_count=len(stages),clip=f"results/{run_name}/{output['file']}",
            progressive_wall_seconds=progressive,
            locked_audio_exact=manifest.get("checks",{}).get(label+"_audio_exact"),
            validation=output["validation"]))
    return rows


def fmt(value, divisor=1):
    return "—" if value is None else f"{value/divisor:.2f}"


def duration(value):
    if value is None:
        return "—"
    seconds = round(value)
    return f"{seconds // 60}m {seconds % 60:02d}s" if seconds >= 60 else f"{value:.1f}s"


def workflow_name(manifest, label):
    if manifest.get('case') == 'preview-app':
        return manifest['name']
    if label == "preview":
        return "Standard 20 · preview"
    if label.endswith("-upscaled"):
        provider = "LBH" if label.startswith("lbh") else "Plus"
        return f"{provider} · upscale only"
    if manifest.get("case") == "progressive":
        inputs = next((c["inputs"] for c in manifest.get("calls", []) if c["stage"] == "progressive-patch"), {})
        scale = inputs.get("source_scale")
        return f"Progressive {scale:.0%} · Standard 20" if scale is not None else "Progressive · Standard 20"
    return {"standard":"Standard · 20 steps", "turbo8":"Turbo · 8 steps", "lbh":"LBH · 8-step finish",
            "plus":"Plus · 8-step finish", "guided":"Plus · flow-guided finish",
            "unguided":"Plus · unguided finish"}.get(label, label)


def clip_card(run, manifest, output, row):
    escape = lambda value: html.escape(str(value), quote=True)
    label = output["label"]
    path = escape(f"results/{run['Name']}/{output['file']}")
    validation = output.get("validation", {})
    fps = validation.get("fps")
    frames = validation.get("frames", manifest.get("frames"))
    try:
        clip_seconds = float(frames / Fraction(str(fps)))
    except (TypeError, ValueError, ZeroDivisionError):
        clip_seconds = None
    resolution = f"{validation['width']} × {validation['height']}" if validation.get("width") and validation.get("height") else "—"
    facts = [("Pipeline time", duration(row["stage_total_seconds"]) if row else "Not finalized"),
             ("Resolution", resolution), ("Clip length", f"{clip_seconds:.2f}s" if clip_seconds is not None else "—"),
             ("Frames / rate", f"{frames or '—'} / {fps or '—'} fps"), ("Seed", manifest.get("seed", "—"))]
    if row:
        facts.append(("Peak VRAM", f"{fmt(row['pipeline_sampled_peak_device_used'], GIB)} GiB"))
    facts_html = "".join(f"<div><dt>{escape(k)}</dt><dd>{escape(v)}</dd></div>" for k, v in facts)
    details = ""
    if row:
        timing = "".join(f"<div><dt>{escape(k.title())}</dt><dd>{escape(duration(v))} <small>({v:.2f}s)</small></dd></div>" for k, v in row["seconds"].items())
        paired = manifest.get("case") in ("comparison", "long", "guided")
        shared = "Includes the shared first-pass preparation and sampling, followed by only this output’s stages. Other branches and their diagnostic encodes are excluded. Branch order and warm caches affect timings." if paired else "Sum of this output’s measured stages, from preparation through saving."
        diagnostic = " No refinement is included." if label.endswith("-upscaled") else ""
        details = f'''<details><summary>Timing &amp; memory details</summary>
<dl class="timings">{timing}</dl><p>{shared}{diagnostic} Saving includes MP4 encoding and lossless frame archives.</p>
<dl class="timings"><div><dt>Whole worker, all outputs</dt><dd>{duration(run.get('Seconds'))}</dd></div>
<div><dt>Minimum free VRAM · this pipeline</dt><dd>{fmt(row['pipeline_minimum_device_free'], GIB)} GiB · {escape(row['classification'])}</dd></div>
<div><dt>Whole-run peak VRAM</dt><dd>{fmt(row['whole_run_peak_device_used'], GIB)} GiB</dd></div>
<div><dt>Whole-run peak process RAM</dt><dd>{fmt(row['whole_run_peak_process_rss'], GIB)} GiB</dd></div></dl>
<p>Pipeline time excludes worker startup. Each worker starts fresh; model loading is included, while disk caches can be warm. Sampled VRAM peaks include device baseline and may miss brief allocations. Comfortable means at least 2 GiB observed free; below that is tight. RAM covers the Linux worker.</p>
<a href="results/{escape(run['Name'])}/manifest.json">Workflow manifest</a> · <a href="results/{escape(run['Name'])}/measurements.json">Measurements</a></details>'''
    else:
        details = f'<p class="pending">Run status: {escape(run["Status"])}. Performance results are not finalized.</p>'
    status = row["classification"] if row else run["Status"]
    badge = "VRAM: " + status if row else status
    return f'''<article><header><div><h2>{escape(workflow_name(manifest, label))}</h2>
<p class="run-name">{escape(run['Name'])} / {escape(label)}</p></div><span class="badge {escape(status)}">{escape(badge)}</span></header>
<video controls loop preload="metadata" src="{path}"></video><dl class="facts">{facts_html}</dl>{details}
<a class="open-clip" href="{path}">Open clip ↗</a></article>'''


def generate(root):
    budget = read_json(root/"budget.json",{"Runs":[],"ConsumedSeconds":0})
    notes = read_json(root/"attempt-notes.json",{})
    rows, attempts, clips, completed_runs, stage_rows, output_rows = [], [], [], [], [], []
    for run in budget["Runs"]:
        directory = root/"results"/run["Name"]
        manifest = read_json(directory/"manifest.json",{})
        measurements = read_json(directory/"measurements.json",{})
        flow = read_json(directory/"first-pass-flow-metrics.json",{})
        rows.extend(candidate_rows(run["Name"],manifest,measurements,run["Status"],flow))
        clip_rows = {r["label"]:r for r in candidate_rows(run["Name"],manifest,measurements,run["Status"],flow,include_diagnostics=True)}
        output_rows.extend(clip_rows.values())
        if run["Status"] == "complete" and measurements.get("status") == "complete":
            completed_runs.append(dict(run=run["Name"], worker_seconds=run["Seconds"],
                baseline_device_used=measurements.get("baseline",{}).get("used"),
                peak_device_used=measurements.get("sampled_peak_device_used"),
                minimum_device_free=measurements.get("sampled_minimum_device_free"),
                peak_process_rss=measurements.get("peak_process_rss"),
                minimum_host_available=measurements.get("minimum_host_available"),
                peak_host_swap_used=measurements.get("peak_host_swap_used")))
        for stage in measurements.get("stages", []):
            stage_rows.append(dict(run=run["Name"], controller_status=run["Status"], **{
                key:stage.get(key) for key in ("name", "seconds", "baseline_device_used", "sampled_peak_device_used",
                    "sampled_minimum_device_free", "peak_process_rss", "torch_peak_allocated", "torch_peak_reserved",
                    "torch_counters_available", "allocator_backend", "error")}))
        attempts.append(dict(run=run["Name"],case=run["Case"],status=run["Status"],
            seconds=run["Seconds"],error=manifest.get("error"),
            note=notes.get(run["Name"]),
            last_finished_stage=measurements.get("stages",[{}])[-1].get("name") if measurements.get("stages") else None))
        for output in manifest.get("outputs",[]):
            clips.append(clip_card(run, manifest, output, clip_rows.get(output["label"])))
    atomic_json(root/"comparison.json",dict(candidates=rows,outputs=output_rows,completed_runs=completed_runs,
        attempts=attempts,execution_seconds=budget["ConsumedSeconds"]))
    if stage_rows:
        with (root/"stage-measurements.csv").open("w",encoding="utf-8",newline="") as destination:
            writer=csv.DictWriter(destination,fieldnames=list(stage_rows[0]))
            writer.writeheader()
            writer.writerows(stage_rows)
    lines = ["# H3 benchmark measurements", "",
        f"Worker execution time, including failures and interruptions: **{budget['ConsumedSeconds']/60:.1f} minutes**.","",
        "Completed configurations only. Times are sums of recorded pipeline stages; preview and raw-upscale diagnostic encodes are excluded. Branches share their measured first pass and have warm-process/order effects. These are not independent end-to-end stopwatch runs. Progressive learned-upscaler time is separated from its enclosing sampling stage using Flow's wall-time event; subphase GPU peaks are not independently measured.","",
        "| Run / output | Frames | Prepare s | Sample s | Upscale s | Decode s | Save s | Stage sum s | Peak GiB | Free GiB | Margin |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"]
    for row in rows:
        lines.append("| "+" | ".join([f"{row['run']} / {row['label']}",str(row["frames"]),
            *[fmt(row["seconds"][k]) for k in ("preparation","sampling","upscaling","decoding","saving")],
            fmt(row["stage_total_seconds"]),fmt(row["pipeline_sampled_peak_device_used"],GIB),
            fmt(row["pipeline_minimum_device_free"],GIB),row["classification"]])+" |")
    if not rows:
        lines.extend(["", "No completed configuration yet. No speed, quality or memory recommendation can be made from interrupted setup attempts."])
    lines.extend(["", "NVML peaks are sampled lower bounds and include baseline device usage. PyTorch counters describe a different allocation scope; see each stage receipt for allocated/reserved values and availability. Whole-run RAM/GPU extrema, including diagnostic stages, are retained in comparison.json.","",
        "## Whole-run memory", "",
        "One row per completed worker, including all branches and diagnostics. Memory columns use GiB except swap (MiB). Host RAM/swap are Linux/WSL observations. Stage-level device/RSS/PyTorch counters are exported as bytes in stage-measurements.csv; unavailable allocator counters must not be interpreted as zero device use. Offloading configuration and native model-staging messages are preserved in the manifest and detailed log; PCIe transfer volume is not measured.", "",
        "| Run | Worker s | GPU baseline | GPU peak | GPU minimum free | Process RSS peak | Host minimum available | Host swap peak MiB |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"])
    for run in completed_runs:
        lines.append("| "+" | ".join([run["run"],fmt(run["worker_seconds"]),
            *[fmt(run[key],GIB) for key in ("baseline_device_used", "peak_device_used", "minimum_device_free",
                "peak_process_rss", "minimum_host_available")],fmt(run["peak_host_swap_used"],1024**2)])+" |")
    lines.extend(["",
        "## Attempts", "", "| Run | Case | Status | Worker s | Last completed stage |", "| --- | --- | --- | ---: | --- |"])
    for row in attempts:
        lines.append(f"| {row['run']} | {row['case']} | {row['status']} | {row['seconds']:.1f} | {row['last_finished_stage'] or '—'} |")
    for row in attempts:
        if row["note"]:
            lines.extend(["",f"**{row['run']}:** {row['note']}"])
    lines.extend(["", "## Quality review", "", "Inspect every complete clip and record findings in quality-review.md. No automatic metric in this report establishes identity, natural motion or synchronization.", ""])
    (root/"comparison.md").write_text("\n".join(lines),encoding="utf-8")
    page = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>H3 benchmark clips</title><style>
*{box-sizing:border-box}body{font:15px/1.5 system-ui;background:#16191e;color:#eee;margin:24px}h1{font-size:28px;margin-bottom:8px}h2{font-size:18px;margin:0}a{color:#b9d5ff}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,480px),1fr));gap:24px;align-items:start}article{min-width:0;background:#232831;padding:18px;border:1px solid #353e4b;border-radius:12px}header{display:flex;justify-content:space-between;gap:12px;align-items:start;margin-bottom:14px}header>div{min-width:0}.run-name{margin:4px 0 0;font-size:12px;overflow-wrap:anywhere}video{width:100%;max-height:60vh;display:block;background:#111;border-radius:6px}p,dt{color:#b6bfcb}p{max-width:100ch}.facts{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px 12px;margin:18px 0}.facts dt{font-size:12px}.facts dd{font-size:16px;font-weight:600;margin:2px 0 0;overflow-wrap:anywhere}.badge{border:1px solid #546272;border-radius:999px;padding:3px 9px;font-size:11px;white-space:nowrap}.comfortable{color:#b6e9d4;border-color:#47735f}.tight{color:#ffdb9d;border-color:#94703c}details{border-top:1px solid #3b4451;padding:12px 0;font-size:13px}summary{cursor:pointer;color:#b9d5ff;width:fit-content}summary:focus-visible,button:focus-visible,a:focus-visible{outline:2px solid #b9d5ff;outline-offset:4px}.timings{margin:12px 0}.timings>div{display:flex;justify-content:space-between;gap:12px;padding:4px 0}.timings dd{margin:0;text-align:right}.timings small{color:#b6bfcb}.open-clip{display:inline-block;font-size:13px;margin-top:4px}button{font:inherit;border:1px solid #546272;background:#293444;color:#eee;border-radius:6px;padding:8px 12px;cursor:pointer}.toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:10px;margin:20px 0}.updated{font-size:12px;color:#b6bfcb}.pending{color:#ffdb9d}@media(max-width:550px){body{margin:14px}.grid{gap:16px}article{padding:14px}.facts{grid-template-columns:repeat(2,minmax(0,1fr))}header{flex-wrap:wrap}}
</style><h1>H3 benchmark clips</h1><p>Compare generation time, output size and memory alongside each clip. Paired outputs share a measured first pass; expand the details for the breakdown. Upscale-only clips have no second sampling pass.</p>
<div class="toolbar"><button onclick="document.querySelectorAll('video').forEach(v=>{v.currentTime=0;v.muted=true;v.play().catch(()=>{})})">Play all from start (muted)</button><button onclick="document.querySelectorAll('video').forEach(v=>v.pause())">Pause all</button><button onclick="location.reload()">Refresh page</button><a href="comparison.md">Full measurements</a></div>
<p class="updated">Updated '''+datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")+''' · RTX 4000 Ada · 20 GiB · one job at a time</p><div class="grid">'''+"\n".join(clips)+"</div></html>"
    (root/"clips.html").write_text(page,encoding="utf-8")
    print(f"{len(rows)} completed candidates, {len(attempts)} attempts: {root/'comparison.md'}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root",type=Path,default=Path(__file__).resolve().parents[2]/"artifacts/h3-benchmark")
    generate(parser.parse_args().root)
