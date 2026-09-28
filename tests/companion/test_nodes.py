"""Exercise our node wiring on CPU; ComfyUI host services are small test doubles."""
import importlib.util
import sys
import types
import uuid
from pathlib import Path

import pytest
import torch

ROOT = Path(__file__).resolve().parents[2] / "comfy_nodes" / "lumibelle_h3"


class Nested:
    def __init__(self, tensors):
        self.tensors = tensors

    def unbind(self):
        return self.tensors


class Noise:
    def __init__(self, seed):
        self.seed = seed

    def generate_noise(self, latent):
        return Nested([torch.ones_like(t) for t in latent["samples"].unbind()])


@pytest.fixture
def nodes(tmp_path, monkeypatch):
    def namespace(name, **values):
        module = types.ModuleType(name)
        for key, value in values.items():
            setattr(module, key, value)
        monkeypatch.setitem(sys.modules, name, module)
        return module

    class Routes:
        def get(self, path):
            return lambda fn: fn

        post = get

    namespace("folder_paths", get_output_directory=lambda: str(tmp_path / "out"), get_input_directory=lambda: str(tmp_path / "in"))
    namespace("aiohttp", web=types.SimpleNamespace())
    namespace("server", PromptServer=types.SimpleNamespace(instance=types.SimpleNamespace(routes=Routes())))
    namespace("comfy")
    namespace("comfy.nested_tensor", NestedTensor=Nested)
    namespace("comfy_extras")
    namespace("comfy_extras.nodes_custom_sampler", Noise_RandomNoise=Noise)
    namespace("lumibelle_test_nodes", __path__=[str(ROOT)])
    name = "lumibelle_test_nodes.nodes"
    spec = importlib.util.spec_from_file_location(name, ROOT / "nodes.py")
    module = importlib.util.module_from_spec(spec)
    monkeypatch.setitem(sys.modules, name, module)
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("locked", [True, False])
def test_node_roundtrip_rebuilds_joint_streams_and_preserves_temporal_length(nodes, locked):
    video = torch.randn(1, 24, 7, 4, 6)
    audio = torch.randn(1, 32, 2, 37)
    cond = [[torch.randn(1, 2, 3), {"minimax_refs": []}]]
    source = {"samples": Nested([video, audio])}
    captured_id = str(uuid.uuid4())
    captured = nodes.LumibelleH3CaptureV1().capture(nodes.PROTOCOL, source, cond, '{"prompt":"exact words"}', captured_id, 96, 64, 22)
    receipt = captured["ui"]["refinement"][0]
    # Transfer the complete archive as the HTTP client does; no tensors come from MP4.
    uploaded_id = uuid.uuid4().hex
    destination = nodes.path_for(uploaded_id)
    destination.parent.mkdir(parents=True)
    destination.write_bytes(nodes.path_for(receipt["token"], True).read_bytes())
    joint, loaded_video, loaded_cond = nodes.LumibelleH3LoadV1().load(nodes.PROTOCOL, uploaded_id, captured_id)
    assert torch.equal(loaded_video["samples"], video)
    upscaled = {"samples": torch.randn(1, 24, 7, 8, 12)}
    prepared, result_cond, noise = nodes.LumibelleH3PrepareV1().prepare(nodes.PROTOCOL, joint, upscaled, loaded_cond, 192, 128, 22, locked, 814)
    v, a = prepared["samples"].unbind()
    assert v.shape[2] == video.shape[2] and torch.equal(a, audio)
    assert result_cond is not loaded_cond and torch.equal(result_cond[0][0], loaded_cond[0][0])
    injected_video, injected_audio = noise.generate_noise(prepared).unbind()
    assert injected_video.any() and noise.seed == 814
    if locked:
        video_mask, audio_mask = prepared["noise_mask"].unbind()
        assert video_mask.all() and not audio_mask.any() and not injected_audio.any()
    else:
        assert "noise_mask" not in prepared and injected_audio.any()
    # Refine captures original audio even if a sampler returned changed audio.
    sampled = {"samples": Nested([v, audio + 10])}
    final = nodes.LumibelleH3CaptureV1().capture(nodes.PROTOCOL, sampled, result_cond, '{}', str(uuid.uuid4()), 192, 128, 22, joint if locked else None)
    assert torch.equal(final["result"][0]["samples"].unbind()[1], audio if locked else audio + 10)
    with pytest.raises(ValueError, match="different take"):
        nodes.LumibelleH3LoadV1().load(nodes.PROTOCOL, uploaded_id, str(uuid.uuid4()))


def test_transfer_paths_cannot_escape_package_storage(nodes):
    for token in ("../settings", "A" * 32, "", "c:/secret", uuid.uuid4().hex + "/../x"):
        with pytest.raises(ValueError):
            nodes.path_for(token)
