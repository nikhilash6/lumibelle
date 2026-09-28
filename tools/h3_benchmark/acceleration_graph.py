"""Pure, reviewable modifications to application-built H3 control graphs."""
import copy
import re

CONFIGURATIONS = ('standard', 'turbo4', 'turbo8', 'sol', 'spectrum', 'cache', 'pdd', 'larry')
NODE_IDS = {'spectrum': 'SpectrumApplyMiniMaxH3', 'cache': 'ApplyMiniMaxH3FirstBlockCache',
            'pdd': 'MiniMaxH3PDDAccApply', 'larry': 'MiniMaxH3TurboLoRA'}


def base_variant(configuration):
    if configuration not in CONFIGURATIONS:
        raise ValueError('Unknown acceleration recipe')
    return configuration if configuration in CONFIGURATIONS[:4] else 'standard'


def defaults(schema):
    result = {}
    for fields in (schema.get('required', {}), schema.get('optional', {})):
        for name, contract in fields.items():
            if len(contract) > 1 and 'default' in contract[1]:
                result[name] = contract[1]['default']
    return result


def modify(request, configuration, schemas):
    request = copy.deepcopy(request)
    graph = request['workflow']['prompt']
    if sum(node['class_type'] == 'SamplerCustomAdvanced' for node in graph.values()) != 1:
        raise ValueError('Expected exactly one sampling pass')
    if any('LumibelleH3' in node['class_type'] or 'FlowAligned' in node['class_type'] for node in graph.values()):
        raise ValueError('Refinement/capture is outside this experiment')
    if request['configuration'] != base_variant(configuration):
        raise ValueError('Wrong application control graph')
    request['configuration'] = configuration
    request['benchmark_modifications'] = []
    if configuration not in NODE_IDS:
        return request
    if any(id in graph for id in ('900', '901')):
        raise ValueError('Benchmark node IDs already in use')
    node_type = NODE_IDS[configuration]
    inputs = defaults(schemas[node_type])
    inputs['model'] = ['1', 0]
    if configuration == 'spectrum':
        inputs.update(debug=True, history_storage='system_ram', offline_archive_storage='system_ram', offline_smoothing_replay=True)
    elif configuration == 'cache':
        modes = schemas[node_type]['required']['mode'][0]
        inputs['mode'] = next(mode for mode in modes if mode.startswith('H3 Safe'))
    elif configuration == 'pdd':
        graph['900'] = dict(class_type='MiniMaxH3SigmaShift', inputs=dict(model=['1', 0], shift_video=12.0, shift_audio=3.0))
        inputs.update(model=['900', 0], pdd_file='MiniMax-H3-Ref2VA-Acc-8Step.safetensors', nfe='8',
                      lora_strength=1.0, head_strength=1.0, on_off_grid='error', partition_check='error')
        graph['8']['inputs']['sampler_name'] = 'euler'
        graph['10']['inputs']['sigmas'] = ['901', 1]
        del graph['9']
    elif configuration == 'larry':
        inputs.update(lora_name='minimax_h3_turbo_v4_step600_ema.safetensors', strength=1.0, low_vram=False)
        graph['8'] = dict(class_type='MiniMaxH3TurboSampler', inputs={})
        graph['9']['inputs']['steps'] = 6
    graph['901'] = dict(class_type=node_type, inputs=inputs)
    graph['6']['inputs']['model'] = ['901', 0]
    # Cache wrappers must also supply the scheduler, as required by their contract.
    if configuration in ('spectrum', 'cache', 'larry'):
        graph['9']['inputs']['model'] = ['901', 0]
    request['benchmark_modifications'] = [dict(node=id, value=node) for id, node in graph.items()
                                            if id in ('6', '8', '9', '10', '900', '901')]
    return request


def accelerator_evidence(configuration, log):
    lines = [line for line in log.splitlines() if any(word in line.lower() for word in
             ('spectrum', 'fbcache', 'sol-attn', 'sparse', 'pdd', 'h3turbo', 'fallback', 'out of memory'))]
    result = dict(diagnostics=lines, effective=None)
    if configuration == 'cache':
        hits = re.findall(r'FBCache: cached (\d+)/(\d+) steps', log)
        if hits:
            result.update(cached_steps=int(hits[-1][0]), model_steps=int(hits[-1][1]), effective=int(hits[-1][0]) > 0)
    elif configuration == 'spectrum':
        phases = []
        for line in log.splitlines():
            if 'Spectrum H3 run summary ' not in line:
                continue
            fields = dict(re.findall(r'\b(phase|actual_transformer_calls|forecast_calls|fallbacks|offline_replay_calls)=(\S+)', line))
            if 'actual_transformer_calls' in fields and 'forecast_calls' in fields:
                phases.append({k:int(v) if k != 'phase' else v for k,v in fields.items()})
        if phases:
            actual=sum(p['actual_transformer_calls'] for p in phases)
            forecasts=sum(p['forecast_calls'] for p in phases)
            result.update(actual=actual, forecast=forecasts, phases=phases,
                          fallbacks=sum(p.get('fallbacks',0) for p in phases), effective=forecasts > 0)
    return result


def logical_archive_frames(durations_ms, fps=24):
    """WebP can coalesce identical frames; validate the logical timeline instead."""
    frames = 0
    for duration in durations_ms:
        count = max(1, round(duration * fps / 1000))
        if abs(duration - count * 1000 / fps) > 2:
            raise ValueError('Unexpected archive frame duration')
        frames += count
    return frames
