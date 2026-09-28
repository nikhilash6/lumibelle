"""Native ComfyUI benchmark worker. No Lumibelle companion or production API writes.

Run in the pinned isolated container; all writes belong under /bench.
The explicit call manifest records node IDs, resolved scalar inputs, and stage order.
"""
import argparse
import ast
import asyncio
import copy
import hashlib
import importlib.util
import inspect
import json
import math
import os
from pathlib import Path
import sys
import traceback

from metrics import Measurements, atomic_json

GPU = os.environ.get("H3_BENCHMARK_GPU_UUID", "")
UPSCALER = "minimax_h3_latent_upscaler_3d_fp16.safetensors"
PINS = {"lbh": "d7c01b9011f2e8439493f6c02c29995a27df276f",
        "plus": "db76324d6bbf231bebcb9d794e133ef4d4d9ee87",
        "flow": "d876590674b27c4410ef415ff605bc7d316dcdd8"}
PROMPT = """subject_definitions:
<Subject 1> is the adult woman from <Picture 1>.
summary:
[reference generation] A small head turn in a quiet studio.
retention_analysis:
<Subject 1> from <Picture 1>: preserve her face, long brown hair, white dotted blouse, yellow character headband and identity.
detailed_description:
[Shot 1] One continuous camera take lasting {seconds:.3f} seconds. Medium close-up of <Subject 1> in a softly lit studio. She slowly turns her head toward a doorway and gently smiles. She stays in place. The camera is locked off. Natural, subtle motion. No cuts or scene transitions.
overall_soundscape:
Quiet room ambience, no speech.
non_diegetic_music:
No music.
"""


def hash_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for chunk in iter(lambda: source.read(8*1024*1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_flow(evidence, progressive=False):
    events = evidence.get("events", [])
    guidance = [e.get("fields", {}) for e in events if e.get("kind") == "guidance"]
    if not any(g.get("mode") == "direction" and
               isinstance(g.get("correction_rms"), (int, float)) and
               math.isfinite(g["correction_rms"]) and g["correction_rms"] > 0 for g in guidance):
        raise RuntimeError("No nonzero direction guidance was recorded")
    if progressive:
        kinds = {e.get("kind") for e in events}
        required = {"low_stage_wall", "high_stage_wall", "handoff_learned_upscale_wall"}
        if not required.issubset(kinds) or evidence.get("counters", {}).get("progressive_target_fallbacks", 0):
            raise RuntimeError("Progressive learned handoff did not execute the requested stages")
    return dict(direction_guidance_events=len(guidance), nonzero_direction_guidance=True,
                progressive_learned_handoff=progressive)


def vendor(name):
    directories = list((Path("/bench/vendor") / name).glob("*/__init__.py"))
    if len(directories) != 1:
        raise RuntimeError("Expected exactly one pinned source directory for " + name)
    return directories[0].parent


def import_package(name, directory):
    spec = importlib.util.spec_from_file_location(name, directory / "__init__.py", submodule_search_locations=[str(directory)])
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def arguments():
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", choices=["preflight", "contracts", "standard", "turbo8", "comparison", "guided", "progressive", "long", "preview-app"], required=True)
    parser.add_argument("--name")
    parser.add_argument("--seed", type=int, default=20260910)
    parser.add_argument("--frames", type=int, choices=[73,141])
    parser.add_argument("--seconds-remaining", type=float, default=7200)
    parser.add_argument("--source-scale", type=float, default=.8)
    return parser.parse_args()


def preflight(settings):
    folders = {"Model": "diffusion_models", "Encoder": "text_encoders", "VideoVae": "vae", "AudioVae": "vae", "Turbo8StepLora": "loras"}
    files = [(key, Path("/comfyui/models") / folder / settings[key.lower()[0]+key[1:]]) for key, folder in folders.items()]
    files.append(("upscaler", Path("/comfyui/models/latent_upscale_models") / UPSCALER))
    evidence = dict(pins=PINS, files=[], reference_sha256=hash_file("/bench/reference.jpg"))
    for label, path in files:
        print("HASH " + str(path), flush=True)
        evidence["files"].append(dict(label=label, path=str(path), bytes=path.stat().st_size, sha256=hash_file(path)))
    # Record the actual core source shipped in the immutable image, which has no .git directory.
    evidence["core_sha256"] = {path: hash_file("/comfyui/"+path) for path in
        ["nodes.py", "comfy_extras/nodes_minimax_h3.py", "comfy_extras/nodes_custom_sampler.py", "comfy/model_management.py"]}
    atomic_json("/bench/preflight.json", evidence)


def main():
    opts = arguments()
    if opts.case == "preview-app":
        from app_preview_worker import run
        run(opts)
        return
    settings = json.loads(Path("/bench/h3-settings.json").read_text(encoding="utf-8-sig"))
    if opts.case == "preflight":
        preflight(settings)
        return
    directory = Path("/bench/results") / (opts.name or opts.case)
    if directory.exists():
        raise RuntimeError("Do not overwrite an existing benchmark result")
    directory.mkdir(parents=True)
    atomic_json(directory / "worker.json", dict(pid=os.getpid(),name=opts.name or opts.case))
    # WSL's nvidia-smi can enumerate both devices despite Docker's device request.
    # Pin CUDA itself too; the second card is never benchmark capacity.
    os.environ["CUDA_VISIBLE_DEVICES"] = "" if opts.case == "contracts" else GPU
    sys.path.insert(0, "/comfyui")
    sys.argv = ["h3-benchmark", "--disable-smart-memory", "--disable-metadata",
                "--cpu" if opts.case == "contracts" else "--use-sage-attention"]
    import comfy.options
    comfy.options.enable_args_parsing()
    import cuda_malloc  # Match main.py's allocator selection before importing torch.
    from comfy.cli_args import args, enables_dynamic_vram
    from app.logger import setup_logger
    setup_logger(log_level="INFO", file_outputs=[("DETAIL", str(directory/"comfyui-detail.log"))])
    import comfy_aimdo.control
    if enables_dynamic_vram():
        # Exact startup contract of the pinned image's comfy-aimdo 0.5.3.
        headroom = None if args.reserve_vram is None else int(args.reserve_vram * 1024 ** 3)
        comfy_aimdo.control.init(simple_vram_headroom=headroom, nvml_pressure=not args.disable_nvml_pressure)
    import torch
    if opts.case != "contracts" and (torch.cuda.device_count() != 1 or "RTX 4000 Ada" not in torch.cuda.get_device_name(0)):
        raise RuntimeError("The benchmark must see only the 20 GB RTX 4000 Ada")
    import nodes
    import folder_paths
    import server
    import comfy.model_management as mm
    import comfy.memory_management
    import comfy.model_patcher
    import hook_breaker_ac10a0  # Same core compatibility hook imported by main.py.
    if enables_dynamic_vram():
        initialized = comfy_aimdo.control.init_devices(
            (d.index, int(args.vram_headroom * 1024 ** 3)) for d in mm.get_all_torch_devices())
        if not initialized:
            raise RuntimeError("DynamicVRAM initialization failed; refusing a mismatched fallback")
        comfy_aimdo.control.set_log_info()
        comfy.model_patcher.CoreModelPatcher = comfy.model_patcher.ModelPatcherDynamic
        comfy.memory_management.aimdo_enabled = True
        print("DynamicVRAM enabled and model patcher verified", flush=True)
    from comfy_api.latest import Types
    from comfy.nested_tensor import NestedTensor
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    server.PromptServer(loop)
    loop.run_until_complete(nodes.init_extra_nodes(init_custom_nodes=False, init_api_nodes=False))
    folder_paths.set_input_directory("/bench")
    folder_paths.set_output_directory(str(directory))
    plus = import_package("bench_plus", vendor("plus"))
    flow = import_package("bench_flow", vendor("flow"))
    # Separate module/configuration; never merge LBH mappings into Plus's Comfy registry.
    lbh = import_package("bench_lbh", vendor("lbh")) if opts.case in ("comparison", "long", "contracts") else None
    from bench_plus.nodes import minimax_h3_refine as refinement
    from bench_plus.nodes.minimax_h3_refine import _model_with_refinement_contract

    if opts.case == "contracts":
        # Bind the worker's explicit keyword calls to the installed Python
        # contracts, without loading a model or initializing CUDA.
        evidence = []
        registries = {"nodes":nodes.NODE_CLASS_MAPPINGS,"plus":plus.NODE_CLASS_MAPPINGS,
                      "flow":flow.NODE_CLASS_MAPPINGS,"lbh":lbh.NODE_CLASS_MAPPINGS}
        for call_node in ast.walk(ast.parse(Path(__file__).read_text())):
            if not isinstance(call_node,ast.Call) or not isinstance(call_node.func,ast.Name) or call_node.func.id != "call":
                continue
            node_id = ast.literal_eval(call_node.args[1])
            if any(k.arg is None for k in call_node.keywords):
                continue  # The two upscaler variants are checked explicitly below.
            registry_name = call_node.args[2].value.id if len(call_node.args)>2 else "nodes"
            obj = registries[registry_name][node_id]()
            signature = inspect.signature(getattr(obj,getattr(obj,"FUNCTION","execute")))
            signature.bind(**{k.arg:None for k in call_node.keywords})
            evidence.append(dict(registry=registry_name,node=node_id,signature=str(signature)))
        for name,extra in [("lbh",["enable_temporal_chunking","force_unload"]),
                           ("plus",["keep_proportion","offload_after_upscale"])]:
            obj = registries[name]["MinimaxH3LatentUpscaler3D"]()
            signature = inspect.signature(obj.execute)
            signature.bind(**dict.fromkeys(["latent","model_name","mode","align","device","precision"]+extra))
            evidence.append(dict(registry=name,node="MinimaxH3LatentUpscaler3D",signature=str(signature)))
        atomic_json(directory/"contracts.json",dict(status="passed",cuda_visible_devices="",calls=evidence,pins=PINS))
        print(f"Validated {len(evidence)} node call contracts on CPU",flush=True)
        return

    telemetry = Measurements(directory, torch, GPU, opts.seconds_remaining)
    manifest = dict(case=opts.case, seed=opts.seed, pins=PINS, settings=settings, gpu=GPU,
                    frames=141 if opts.case == "long" else (opts.frames or 73), calls=[], outputs=[], flags=sys.argv[1:],
                    loading="cold-process; shared OS file cache may be warm", companion_nodes=False)
    manifest["runtime"] = dict(python=sys.version, torch=torch.__version__,
        device=torch.cuda.get_device_name(0), allocator=torch.cuda.get_allocator_backend(),
        dynamic_vram=comfy.memory_management.aimdo_enabled, nvml_pressure=not args.disable_nvml_pressure,
        disable_smart_memory=args.disable_smart_memory, use_sage_attention=args.use_sage_attention)
    manifest["harness_sha256"] = {p.name:hash_file(p) for p in Path("/harness").glob("*.py")}
    source_snapshot = directory/"harness"
    source_snapshot.mkdir()
    for filename in ("runner.py", "metrics.py"):
        (source_snapshot/filename).write_bytes((Path("/harness")/filename).read_bytes())
    manifest["preflight"] = json.loads(Path("/bench/preflight.json").read_text())
    metrics_objects = {}

    def record():
        atomic_json(directory / "manifest.json", manifest)
        for name, metric in metrics_objects.items():
            try:
                (directory / (name+"-flow-metrics.json")).write_text(metric.to_json(), encoding="utf-8")
            except Exception as error:
                manifest.setdefault("metrics_errors", []).append(str(error))

    def call(stage, node_id, registry=None, **kwargs):
        cls = (registry or nodes.NODE_CLASS_MAPPINGS)[node_id]
        obj = cls()
        fn = getattr(obj, getattr(obj, "FUNCTION", "execute"))
        def describe(value):
            if value is None or isinstance(value,(str,int,float,bool)):
                return value
            if isinstance(value,dict):
                return {key:describe(item) for key,item in value.items()}
            if isinstance(value,(list,tuple)):
                return [describe(item) for item in value]
            if isinstance(value,torch.Tensor):
                return dict(type="tensor",shape=list(value.shape),dtype=str(value.dtype))
            return dict(type=type(value).__module__+"."+type(value).__name__)
        manifest["calls"].append(dict(stage=stage, node=node_id, inputs={key:describe(value) for key,value in kwargs.items()}))
        record()  # Include the attempted call even if execution is interrupted.
        with telemetry.stage(stage):
            output = fn(**kwargs)
        if hasattr(output, "result"):
            output = output.result
        elif isinstance(output, dict):
            output = output["result"]
        record()
        return output

    def latent_hash(latent):
        parts = latent["samples"].unbind()
        return [hashlib.sha256(x.detach().cpu().contiguous().view(torch.uint8).numpy().tobytes()).hexdigest() for x in parts]

    def decode(label, latent):
        from PIL import Image
        import numpy as np
        images = call(label+"/decode-video", "VAEDecode", samples=latent, vae=video_vae)[0]
        audio = call(label+"/decode-audio", "VAEDecodeAudio", samples=latent, vae=audio_vae)[0]
        expected = (manifest["frames"], 768, 1344, 3) if label != "preview" else (manifest["frames"],480,832,3)
        if tuple(images.shape) != expected or not torch.isfinite(images).all():
            raise RuntimeError("Unexpected decoded shape or non-finite pixels: " + str(tuple(images.shape)))
        with telemetry.stage(label+"/encode-mp4"):
            video = nodes.NODE_CLASS_MAPPINGS["CreateVideo"].execute(images=images, audio=audio, fps=24, bit_depth=8)[0]
            video.save_to(str(directory/(label+".mp4")), format=Types.VideoContainer("mp4"), codec=Types.VideoCodec("h264"))
        with telemetry.stage(label+"/archive-webp"):
            import av
            path = directory / (label+".mp4")
            with av.open(str(path)) as container:
                vs = container.streams.video[0]
                validation = dict(width=vs.width, height=vs.height, frames=sum(1 for _ in container.decode(video=0)), fps=str(vs.average_rate), has_audio=bool(container.streams.audio))
            if (validation["frames"] != manifest["frames"] or not validation["has_audio"] or
                validation["fps"] != "24" or (validation["height"],validation["width"]) != expected[1:3]):
                raise RuntimeError("Incomplete encoded output")
            for start in range(0, len(images), 24):
                chunk = [Image.fromarray(np.clip(x.numpy()*255,0,255).astype(np.uint8)) for x in images[start:start+24].cpu()]
                archive = directory / (label+f"-frames-{start:03d}.webp")
                chunk[0].save(archive, save_all=True, append_images=chunk[1:], duration=1000/24, lossless=True, quality=80, method=0)
                with Image.open(archive) as check:
                    if check.n_frames != len(chunk):
                        raise RuntimeError("Lossless frame archive is incomplete")
                for image in chunk:
                    image.close()
            manifest["outputs"].append(dict(label=label, file=label+".mp4", sha256=hash_file(path),
                latent_sha256=latent_hash(latent), validation=validation,
                audio_sample_rate=audio["sample_rate"],audio_samples=audio["waveform"].shape[-1],
                decoded_audio_sha256=hashlib.sha256(audio["waveform"].detach().cpu().contiguous().numpy().tobytes()).hexdigest()))
        record()
        del images, audio, video

    status = "complete"
    try:
        with torch.inference_mode():
            reference = call("reference-load", "LoadImage", image="reference.jpg")[0]
            base = call("load-h3", "UNETLoader", unet_name=settings["model"], weight_dtype="default")[0]
            clip = call("load-encoder", "CLIPLoader", clip_name=settings["encoder"], type="minimax", device="default")[0]
            video_vae = call("load-video-vae", "VAELoader", vae_name=settings["videoVae"])[0]
            audio_vae = call("load-audio-vae", "VAELoader", vae_name=settings["audioVae"])[0]
            two_pass = opts.case in ("comparison", "guided", "long")
            width,height = (832,480) if two_pass else (1344,768)
            prompt = PROMPT.format(seconds=manifest["frames"]/24)
            manifest["prompt"] = prompt
            positive, empty = call("reference-conditioning", "MiniMaxH3ReferenceToVideo", clip=clip, vae=video_vae,
                audio_vae=audio_vae, prompt=prompt, width=width,height=height,length=manifest["frames"],
                ref_image_size="match",ref_images={"ref_image_0":reference})
            del clip, reference
            sampling_model = base
            schedule_model = base
            steps,sampler_name = 20,"res_multistep"
            if opts.case == "turbo8":
                sampling_model = call("turbo-lora", "LoraLoaderModelOnly",model=base,lora_name=settings["turbo8StepLora"],strength_model=1.)[0]
                sampling_model = call("turbo-shift","MiniMaxH3SigmaShift", model=sampling_model,shift_video=12.,shift_audio=3.)[0]
                schedule_model = sampling_model
                steps,sampler_name=8,"euler"
            trajectory = None
            if opts.case in ("guided", "progressive"):
                trajectory = call("trajectory","H3FlowTrajectory", flow.NODE_CLASS_MAPPINGS,storage="system_ram",max_runs=1)[0]
            if opts.case == "guided":
                sampling_model, metric = call("capture-patch","H3TrajectoryCapture",flow.NODE_CLASS_MAPPINGS,
                    model=sampling_model,trajectory=trajectory,capture_forecasts=False)
            elif opts.case == "progressive":
                provider = call("learned-provider","MinimaxH3LatentUpscaler3DProvider",plus.NODE_CLASS_MAPPINGS,
                    model_name=UPSCALER,device="cuda",precision="fp16",offload_after_upscale=True)[0]
                sampling_model,metric = call("progressive-patch","H3ProgressiveTargetInputHandoff",flow.NODE_CLASS_MAPPINGS,
                    model=sampling_model,trajectory=trajectory,source_mode="scale",source_scale=opts.source_scale,
                    source_width=832,source_height=480,handoff_coordinate=.35,handoff_selection="fixed",guidance_mode="direction",
                    direction_weight=.25,acceleration_weight=0.,consistency_weight=0.,low_frequency_cutoff=.25,temporal_weight=0.,
                    handoff_transfer="learned_3d",learned_upscaler=provider)
            else:
                sampling_model,metric = call("metrics-patch","H3RuntimeMetricsProbe",flow.NODE_CLASS_MAPPINGS,model=sampling_model)
            metrics_objects["first-pass"] = metric
            noise = call("base-noise","RandomNoise",noise_seed=opts.seed)[0]
            sampler = call("base-sampler","KSamplerSelect",sampler_name=sampler_name)[0]
            sigmas = call("base-schedule","BasicScheduler",model=schedule_model,scheduler="simple",steps=steps,denoise=1.)[0]
            guider = call("base-guider","BasicGuider",model=sampling_model,conditioning=positive)[0]
            sampled = call("base-sampling","SamplerCustomAdvanced",noise=noise,guider=guider,sampler=sampler,sigmas=sigmas,latent_image=empty)[1]
            if opts.case == "progressive":
                manifest.setdefault("checks", {})["progressive_execution"] = verify_flow(json.loads(metric.to_json()), progressive=True)
            if not two_pass:
                decode(opts.case, sampled)
            if two_pass:
                source_video, source_audio = sampled["samples"].unbind()
                source_hashes=latent_hash(sampled)
                manifest["preview_video_latent_shape"] = list(source_video.shape)
                manifest["lbh_chunking_expected"] = bool(source_video.shape[2] > 32)
                if source_video.shape[2] > 32:
                    # Pinned FP16 checkpoint has temporal kernel/overlap 5.
                    # Record the actual segment extents used by LBH, including
                    # padding/overlap, rather than assuming each is 32 frames.
                    temporal = source_video.shape[2]
                    manifest["lbh_temporal_segments"] = [dict(start=start,
                        input_length=min(temporal+10,min(temporal,start+37)+5)-max(0,max(0,start-5)-5))
                        for start in range(0,temporal,32)]
                branches = ["lbh", "plus"] if opts.case in ("comparison","long") else ["unguided","guided"]
                upscaled_branches, refined_branches = {}, {}
                # Finish GPU sampling/upscaling before any diagnostic decode.
                # A preview decode would change model residency at the handoff.
                for branch in branches:
                    registry = lbh.NODE_CLASS_MAPPINGS if branch=="lbh" else plus.NODE_CLASS_MAPPINGS
                    kwargs = dict(latent={"samples":source_video},model_name=UPSCALER,
                        mode={"mode":"target dimensions","width":1344,"height":768},align=32,device="cuda",precision="fp16")
                    kwargs.update(dict(enable_temporal_chunking=True,force_unload=True) if branch=="lbh"
                                  else dict(keep_proportion=False,offload_after_upscale=True))
                    upscaled = call(branch+"/upscale","MinimaxH3LatentUpscaler3D",registry,**kwargs)[0]
                    if tuple(upscaled["samples"].shape[-2:]) != (48,84):
                        raise RuntimeError("Upscaler did not return exact target dimensions")
                    clean = {"samples":NestedTensor([upscaled["samples"],source_audio.clone()])}
                    upscaled_branches[branch] = clean
                    manifest.setdefault("upscaled_latent_sha256",{})[branch] = latent_hash(clean)
                for branch in branches:
                    clean = upscaled_branches[branch]
                    # Independent reference blocks remain unchanged for both implementations.
                    cond = copy.deepcopy(positive)
                    refined_model = call(branch+"/shift","MiniMaxH3SigmaShift",model=base,shift_video=12.,shift_audio=3.)[0]
                    schedule = call(branch+"/schedule","BasicScheduler",model=refined_model,scheduler="simple",steps=8,denoise=.35)[0]
                    if branch=="guided":
                        refined_model,ref_metric = call(branch+"/guidance","H3FlowAlignedRegenerate",flow.NODE_CLASS_MAPPINGS,
                            model=refined_model,trajectory=trajectory,guidance_mode="direction",direction_weight=.35,
                            acceleration_weight=0.,consistency_weight=0.,low_frequency_cutoff=.25,temporal_weight=0.,
                            source_conditioning=positive)
                    else:
                        refined_model,ref_metric = call(branch+"/metrics","H3RuntimeMetricsProbe",flow.NODE_CLASS_MAPPINGS,model=refined_model)
                    metrics_objects[branch]=ref_metric
                    with telemetry.stage(branch+"/refine-sampling"):
                        refined = refinement.run_h3_refinement(clean,model=_model_with_refinement_contract(refined_model),
                            positive=cond,negative=None,noise=nodes.NODE_CLASS_MAPPINGS["RandomNoise"].execute(noise_seed=opts.seed+1)[0],
                            sampler=nodes.NODE_CLASS_MAPPINGS["KSamplerSelect"].execute(sampler_name="res_multistep")[0],
                            sigmas=schedule,cfg=1.,lock_audio=True)
                    if latent_hash(refined)[1] != source_hashes[1]:
                        raise RuntimeError("Locked audio changed")
                    manifest.setdefault("checks",{})[branch+"_audio_exact"]=True
                    if branch == "guided":
                        manifest["checks"]["guided_execution"] = verify_flow(json.loads(ref_metric.to_json()))
                    refined_branches[branch] = refined
                    del refined_model,cond
                # Record outputs after all matched branches have completed.
                # These small retained latents are included in whole-run RSS.
                for branch in branches:
                    refined = refined_branches[branch]
                    decode(branch,refined)
                    # Diagnostics are timed separately: not part of the normal
                    # low-res -> upscale -> refine -> final decode workflow.
                    decode(branch+"-upscaled",upscaled_branches[branch])
                decode("preview",sampled)
            record()
    except BaseException as error:
        status = "oom" if "out of memory" in str(error).lower() else "failed"
        manifest["error"] = type(error).__name__+": "+str(error)
        (directory/"error.txt").write_text(traceback.format_exc(),encoding="utf-8")
        print(traceback.format_exc(),flush=True)
    finally:
        record()
        telemetry.close(status)
    if status != "complete":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
