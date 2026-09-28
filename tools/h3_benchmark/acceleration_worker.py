"""Manual application-graph acceleration worker. Never loads production custom nodes."""
import argparse
import asyncio
import hashlib
import json
import logging
import os
from pathlib import Path
import sys
import time
import traceback
import uuid
from acceleration_graph import CONFIGURATIONS, NODE_IDS, accelerator_evidence, base_variant, logical_archive_frames, modify
from metrics import Measurements, atomic_json
from runner import GPU, hash_file, import_package, vendor


def boot(directory, cpu=False):
    os.environ['CUDA_VISIBLE_DEVICES'] = '' if cpu else GPU
    sys.path.insert(0, '/comfyui')
    sys.argv = ['h3-acceleration', '--disable-smart-memory', '--disable-metadata', '--cpu' if cpu else '--use-sage-attention']
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
    if not cpu and (torch.cuda.device_count() != 1 or 'RTX 4000 Ada' not in torch.cuda.get_device_name(0)):
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
    folder_paths.set_input_directory('/bench')
    folder_paths.set_output_directory(str(directory))
    # Packages that create their own model folder must do so in benchmark storage.
    folder_paths.models_dir = '/bench/models'
    for kind in ('loras', 'pdd_acc'):
        folder_paths.add_model_folder_path(kind, '/bench/models/'+kind, is_default=True)
    return torch, nodes, app, loop


def load_packages(nodes, names):
    for name in names:
        package = import_package('acceleration_'+name, vendor(name))
        overlap = nodes.NODE_CLASS_MAPPINGS.keys() & package.NODE_CLASS_MAPPINGS.keys()
        if overlap:
            raise ValueError('Unexpected node registry collision: '+str(overlap))
        nodes.NODE_CLASS_MAPPINGS.update(package.NODE_CLASS_MAPPINGS)


def request_for(opts, nodes):
    name = f'{opts.scene}-{base_variant(opts.configuration)}-{opts.frames}-{"native" if opts.native else "upscaled"}-{opts.seed}'
    source = Path('/bench/requests')/(name+'.json')
    request = json.loads(source.read_text('utf-8-sig'))
    schemas = {node: nodes.NODE_CLASS_MAPPINGS[node].INPUT_TYPES() for node in NODE_IDS.values() if node in nodes.NODE_CLASS_MAPPINGS}
    request = modify(request, opts.configuration, schemas)
    request['application_request_sha256'] = hash_file(source)
    return request


def validate(loop, request):
    import execution
    workflow = request['workflow']
    validation = loop.run_until_complete(execution.validate_prompt(workflow['client_id'], workflow['prompt'], None))
    if not validation[0] or validation[3] or set(validation[2]) != {'14', '15'}:
        raise ValueError('Native graph validation failed: '+str(validation))
    return validation


def media_receipt(directory, outputs, expected):
    import av
    from PIL import Image
    entry = (outputs['14'].get('images') or outputs['14'].get('videos'))[0]
    path = directory/entry.get('subfolder', '')/entry['filename']
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        actual = dict(width=stream.width, height=stream.height, frames=sum(1 for _ in container.decode(video=0)), fps=float(stream.average_rate))
    if actual != expected:
        raise ValueError('Unexpected video: '+str(actual))
    digest = hashlib.sha256(); samples = 0
    with av.open(str(path)) as container:
        for frame in container.decode(audio=0):
            digest.update(frame.to_ndarray().tobytes()); samples += frame.samples
    if not samples:
        raise ValueError('No decoded audio')
    archives = []
    for entry in outputs['15']['images']:
        archive = directory/entry.get('subfolder', '')/entry['filename']
        with Image.open(archive) as image:
            if image.size != (actual['width'], actual['height']):
                raise ValueError('Wrong archive dimensions')
            durations = []
            for index in range(image.n_frames):
                image.seek(index); image.load(); durations.append(image.info.get('duration', 1000/24))
            count = logical_archive_frames(durations)
        archives.append(dict(file=archive.relative_to(directory).as_posix(), frames=count, sha256=hash_file(archive)))
    if sum(item['frames'] for item in archives) != actual['frames']:
        raise ValueError('Incomplete frame archive')
    return dict(file=path.relative_to(directory).as_posix(), sha256=hash_file(path), validation=actual,
                audio_sha256=digest.hexdigest(), audio_samples=samples, archives=archives)


def run(opts):
    root = Path('/bench')
    directory = root/('contracts' if opts.validate_only else 'results')/opts.name
    directory.mkdir(parents=True, exist_ok=False)
    atomic_json(directory/'worker.json', dict(pid=os.getpid(), name=opts.name))
    manifest = dict(name=opts.name, configuration=opts.configuration, scene=opts.scene, seed=opts.seed, frames=opts.frames,
                    native=opts.native, status='initializing', runs=[], started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                    pins=json.loads((root/'pins.json').read_text()), weights=json.loads((root/'weights.json').read_text()),
                    preflight_sha256=hash_file(root/'preflight.json'), harness_sha256={p.name:hash_file(p) for p in Path('/harness').glob('*.py')})
    atomic_json(directory/'manifest.json', manifest)
    telemetry = None
    try:
        torch, nodes, app, loop = boot(directory, opts.validate_only)
        packages = ['plus'] if not opts.native else []
        if opts.configuration in NODE_IDS:
            packages.append(opts.configuration)
        load_packages(nodes, packages)
        request = request_for(opts, nodes)
        atomic_json(directory/'request.json', request)
        validation = validate(loop, request)
        atomic_json(directory/'validation.json', validation)
        if opts.validate_only:
            manifest['status'] = 'validated'; return
        import execution
        # No node-output cache on either pass. Comfy's model manager may reuse loaded weights.
        executor = execution.PromptExecutor(app, cache_type=execution.CacheType.NONE, cache_args={'ram':0, 'ram_inactive':0, 'lru':0})
        for index in range(2 if opts.warm else 1):
            label = 'cold' if index == 0 else 'warm'
            sub = directory/label; sub.mkdir()
            telemetry = Measurements(sub, torch, GPU, opts.seconds_remaining)
            graph = request['workflow']['prompt']
            events = []; scope = None
            def close_scope():
                nonlocal scope
                if scope is not None:
                    scope.__exit__(None, None, None); scope = None
            def send(event, data, sid=None):
                nonlocal scope
                if event in ('executing', 'execution_error', 'execution_success', 'execution_cached'):
                    events.append(dict(event=event, data=data, elapsed=time.monotonic()-telemetry.started))
                if event == 'executing':
                    close_scope()
                    node = data.get('node')
                    if node is not None:
                        scope = telemetry.stage(str(node)+'/'+graph[str(node)]['class_type']); scope.__enter__()
            app.send_sync = send
            result = dict(temperature=label, status='running')
            manifest['runs'].append(result)
            atomic_json(directory/'manifest.json', manifest)
            log_path = directory/'comfyui-detail.log'
            log_start = log_path.stat().st_size if log_path.exists() else 0
            try:
                client_id = str(uuid.uuid4())
                executor.execute(graph, client_id, {'client_id':client_id}, validation[2])
                close_scope()
                execution_seconds = time.monotonic()-telemetry.started
                atomic_json(sub/'history.json', executor.history_result)
                if not executor.success or any(e['event']=='execution_error' for e in events):
                    raise RuntimeError('Workflow execution failed; inspect history/log')
                if not any(e['event']=='executing' and str(e['data'].get('node'))=='10' for e in events):
                    raise RuntimeError('Sampling did not execute')
                result.update(media_receipt(directory, executor.history_result['outputs'], request['expected']))
                result.update(status='complete', execution_seconds=execution_seconds)
            finally:
                close_scope()
                if result['status'] == 'running': result['status'] = 'failed'
                telemetry.close('complete' if result['status']=='complete' else 'failed'); telemetry = None
                atomic_json(sub/'events.json', events)
                for handler in logging.getLogger().handlers:
                    handler.flush()
                if log_path.exists():
                    with log_path.open('rb') as log:
                        log.seek(log_start); text = log.read().decode('utf-8', 'replace')
                    (sub/'execution.log').write_text(text, 'utf-8')
                    result['accelerator'] = accelerator_evidence(opts.configuration, text)
                atomic_json(directory/'manifest.json', manifest)
        manifest['status'] = 'complete'
    except BaseException as error:
        manifest.update(status='failed', error=str(error), traceback=traceback.format_exc())
        raise
    finally:
        if telemetry:
            telemetry.close('failed')
        atomic_json(directory/'manifest.json', manifest)
        print('ACCELERATION_RESULT '+json.dumps(manifest), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--name', required=True)
    parser.add_argument('--configuration', choices=CONFIGURATIONS, default='standard')
    parser.add_argument('--scene', choices=('dialogue', 'motion'), default='dialogue')
    parser.add_argument('--frames', type=int, choices=(141, 243), default=141)
    parser.add_argument('--seed', type=int, choices=(20260911, 20260912), default=20260911)
    parser.add_argument('--seconds-remaining', type=float, default=7200)
    parser.add_argument('--native', action='store_true')
    parser.add_argument('--warm', action='store_true')
    parser.add_argument('--validate-only', action='store_true')
    run(parser.parse_args())
