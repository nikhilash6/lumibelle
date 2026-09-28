"""Execute an unmodified application-exported API graph in isolated native ComfyUI."""
import asyncio
import hashlib
import json
import os
from pathlib import Path
import sys
import traceback
from metrics import Measurements, atomic_json
from runner import GPU, import_package, vendor, hash_file


def manifest_for(request, request_sha256):
    return dict(case='preview-app', name=request['name'], seed=request['seed'], frames=request['expected']['frames'],
                request_sha256=request_sha256, events=[], outputs=[], audio_sha256=None)


def output_record(file, sha256, actual):
    return dict(label='application', file=file, sha256=sha256, validation=dict(actual, has_audio=True))


def run(opts):
    request_file = Path('/bench/app-preview') / (opts.name + '.json')
    request = json.loads(request_file.read_text(encoding='utf-8-sig'))
    directory = Path('/bench/results') / opts.name
    directory.mkdir(exist_ok=False)
    atomic_json(directory / 'worker.json', dict(pid=os.getpid(), name=opts.name))
    atomic_json(directory / 'request.json', request)
    os.environ['CUDA_VISIBLE_DEVICES'] = GPU
    sys.path.insert(0, '/comfyui')
    sys.argv = ['h3-app-preview', '--disable-smart-memory', '--disable-metadata', '--use-sage-attention']
    import comfy.options
    comfy.options.enable_args_parsing()
    import cuda_malloc
    from comfy.cli_args import args, enables_dynamic_vram
    from app.logger import setup_logger
    setup_logger(log_level='INFO', file_outputs=[('DETAIL', str(directory/'comfyui-detail.log'))])
    import comfy_aimdo.control
    if enables_dynamic_vram():
        comfy_aimdo.control.init(simple_vram_headroom=None if args.reserve_vram is None else int(args.reserve_vram*1024**3), nvml_pressure=not args.disable_nvml_pressure)
    import torch
    if torch.cuda.device_count() != 1 or 'RTX 4000 Ada' not in torch.cuda.get_device_name(0):
        raise RuntimeError('Unexpected benchmark GPU')
    import nodes
    import folder_paths
    import server
    import comfy.model_management as mm
    import comfy.memory_management
    import comfy.model_patcher
    import hook_breaker_ac10a0
    if enables_dynamic_vram():
        if not comfy_aimdo.control.init_devices((d.index, int(args.vram_headroom*1024**3)) for d in mm.get_all_torch_devices()):
            raise RuntimeError('DynamicVRAM initialization failed')
        comfy_aimdo.control.set_log_info()
        comfy.model_patcher.CoreModelPatcher = comfy.model_patcher.ModelPatcherDynamic
        comfy.memory_management.aimdo_enabled = True
    loop = asyncio.new_event_loop(); asyncio.set_event_loop(loop)
    app = server.PromptServer(loop)
    loop.run_until_complete(nodes.init_extra_nodes(init_custom_nodes=False, init_api_nodes=False))
    folder_paths.set_input_directory('/bench'); folder_paths.set_output_directory(str(directory))
    plus = import_package('preview_plus', vendor('plus'))
    nodes.NODE_CLASS_MAPPINGS.update(plus.NODE_CLASS_MAPPINGS)
    import execution
    graph = request['workflow']['prompt']
    prompt_id = request['workflow']['client_id']
    validation = loop.run_until_complete(execution.validate_prompt(prompt_id, graph, None))
    atomic_json(directory/'validation.json', validation)
    if not validation[0] or validation[3] or set(validation[2]) != {'14','15'}:
        raise RuntimeError('Application graph failed native validation: '+str(validation))
    telemetry = Measurements(directory, torch, GPU, opts.seconds_remaining)
    scope = None
    manifest = manifest_for(request, hash_file(request_file))
    events = manifest['events']
    def close_scope():
        nonlocal scope
        if scope is not None:
            scope.__exit__(None, None, None); scope = None
    def send(event, data, sid=None):
        nonlocal scope
        if event in ('executing','execution_error','execution_success','execution_cached'):
            events.append(dict(event=event, data=data))
        if event == 'executing':
            close_scope()
            node_id = data.get('node')
            if node_id is not None:
                scope = telemetry.stage(str(node_id)+'/'+graph[str(node_id)]['class_type']); scope.__enter__()
    app.send_sync = send
    executor = execution.PromptExecutor(app, cache_type=execution.CacheType.CLASSIC, cache_args={'ram':0,'ram_inactive':0,'lru':0})
    status = 'failed'
    try:
        executor.execute(graph, prompt_id, {'client_id':prompt_id}, validation[2])
        close_scope()
        atomic_json(directory/'history.json', executor.history_result)
        if not executor.success or any(e['event']=='execution_error' for e in events):
            raise RuntimeError('Application workflow execution failed')
        # Validate the real saved MP4 and archives, and retain audio hashes for matched pairs.
        import av
        from PIL import Image
        outputs = executor.history_result['outputs']
        video_entry = (outputs['14'].get('images') or outputs['14'].get('videos'))[0]
        video_path = directory/video_entry.get('subfolder','')/video_entry['filename']
        with av.open(str(video_path)) as container:
            stream = container.streams.video[0]
            frames = sum(1 for _ in container.decode(video=0))
            actual = dict(width=stream.width,height=stream.height,frames=frames,fps=int(stream.average_rate))
        if actual != request['expected']:
            raise RuntimeError('Unexpected output dimensions/frame rate: '+str(actual))
        digest = hashlib.sha256(); audio_frames = 0
        with av.open(str(video_path)) as container:
            for frame in container.decode(audio=0):
                digest.update(frame.to_ndarray().tobytes()); audio_frames += frame.samples
        if audio_frames == 0: raise RuntimeError('Output has no decoded audio')
        manifest['audio_sha256'] = digest.hexdigest()
        archive_count = 0
        for entry in outputs['15']['images']:
            path = directory/entry.get('subfolder','')/entry['filename']
            with Image.open(path) as image:
                if image.size != (actual['width'], actual['height']): raise RuntimeError('Wrong archive dimensions')
                archive_count += image.n_frames
        if archive_count != frames: raise RuntimeError('Wrong archive frame count')
        manifest['outputs'] = [output_record(video_path.relative_to(directory).as_posix(), hash_file(video_path), actual)]
        manifest['archive_frames'] = archive_count
        manifest['status'] = status = 'complete'
    except BaseException as error:
        manifest['error'] = str(error); manifest['traceback'] = traceback.format_exc()
        raise
    finally:
        close_scope(); telemetry.close(status); atomic_json(directory/'manifest.json', manifest)
        print('APP_PREVIEW_RESULT '+json.dumps(manifest), flush=True)
