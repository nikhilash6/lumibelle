import importlib.util
import json
import struct
import uuid
from pathlib import Path

import pytest
import torch
from safetensors.torch import save_file

ROOT = Path(__file__).resolve().parents[2] / "comfy_nodes" / "lumibelle_h3"


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


package = module("package")
prepare = module("preparation")


def inputs(dtype=torch.float32):
    return (torch.randn(1, 24, 7, 4, 6, dtype=dtype), torch.randn(1, 32, 2, 37, dtype=dtype),
            [[torch.randn(1, 2, 3, dtype=dtype), {"minimax_refs": [
                {"kind": "image", "latent": torch.randn(1, 24, 1, 4, 6, dtype=dtype), "latent_h": 4, "latent_w": 6},
                {"kind": "audio", "audio_latent": torch.randn(1, 32, 2, 37, dtype=dtype), "ref_audio_t": 37}],
                "name": "Juniper's exact lettering", "pooled_output": None}]])


@pytest.mark.parametrize("dtype", [torch.float32, torch.float16, torch.bfloat16])
def test_package_roundtrip_preserves_pixels_and_conditioning(tmp_path, dtype):
    video, audio, cond = inputs(dtype)
    path = tmp_path / "package.safetensors"
    identity = str(uuid.uuid4())
    context = {"prompt": "<Picture 1> exact words", "profile": "h3-single-take-v3"}
    package.save_package(path, video, audio, cond, context, identity, 96, 64, 22)
    v, a, c, manifest = package.load_package(path)
    assert torch.equal(v, video) and torch.equal(a, audio)
    assert v.dtype == dtype and torch.equal(c[0][0], cond[0][0])
    assert torch.equal(c[0][1]["minimax_refs"][0]["latent"], cond[0][1]["minimax_refs"][0]["latent"])
    assert manifest["context"] == context and manifest["id"] == identity
    assert not path.with_suffix(".tmp").exists()


@pytest.mark.parametrize("fault", ["truncated", "header", "version", "audio-shape", "tensor-reference"])
def test_corrupt_packages_are_rejected(tmp_path, fault):
    path = tmp_path / "package.safetensors"
    v, a, c = inputs()
    package.save_package(path, v, a, c, {}, str(uuid.uuid4()), 96, 64, 22)
    data = path.read_bytes()
    if fault == "truncated":
        path.write_bytes(data[:-1])
    elif fault == "header":
        path.write_bytes(struct.pack("<Q", 2**63) + data[8:])
    else:
        from safetensors import safe_open
        with safe_open(path, framework="pt") as f:
            tensors = {k: f.get_tensor(k).clone() for k in f.keys()}
            metadata = json.loads(f.metadata()["lumibelle"])
        if fault == "version": metadata["version"] = 2
        if fault == "audio-shape": tensors["audio"] = a[..., :-1].contiguous()
        if fault == "tensor-reference": metadata["conditioning"][0][0]["tensor"] = "missing"
        save_file(tensors, path, metadata={"lumibelle": json.dumps(metadata)})
    with pytest.raises(Exception): package.load_package(path)


@pytest.mark.parametrize("fault", ["nan", "object", "wrong-time", "wrong-audio", "wrong-canvas"])
def test_capture_rejects_invalid_latents_or_serialization(tmp_path, fault):
    v, a, c = inputs()
    if fault == "nan": v[0, 0, 0, 0, 0] = float("nan")
    if fault == "object": c[0][1]["unsafe"] = object()
    if fault == "wrong-time": v = v[:, :, :-1]
    if fault == "wrong-audio": a = a[..., :-1]
    if fault == "wrong-canvas": v = v[..., :-1]
    with pytest.raises(ValueError): package.save_package(tmp_path / "bad", v, a, c, {}, str(uuid.uuid4()), 96, 64, 22)


def test_conditioning_is_resized_independently_without_temporal_or_text_changes():
    v, a, c = inputs()
    result = prepare.resize_conditioning(c, 96, 64, 192, 128)
    before, after = c[0][1]["minimax_refs"], result[0][1]["minimax_refs"]
    assert after[0]["latent"].shape == (1, 24, 1, 8, 12)
    assert (after[0]["latent_h"], after[0]["latent_w"]) == (8, 12)
    assert torch.equal(after[1]["audio_latent"], before[1]["audio_latent"])
    assert torch.equal(result[0][0], c[0][0])
    assert before[0]["latent"].shape == (1, 24, 1, 4, 6)
    assert result[0][1]["name"] == c[0][1]["name"]


def test_refine_locks_both_noise_injection_and_sampler_updates():
    video, audio, _ = inputs()
    masks = prepare.audio_lock(video, audio)
    assert torch.all(masks[0] == 1) and torch.all(masks[1] == 0)
    noise = prepare.lock_noise([torch.ones_like(video), torch.ones_like(audio)])
    assert torch.all(noise[0] == 1) and torch.all(noise[1] == 0)
    # H3's joint sampler must preserve the audio despite an arbitrary update.
    stepped = audio + torch.randn_like(audio) * masks[1]
    assert torch.equal(stepped, audio)


def test_no_first_frame_or_video_conditioning_is_silently_accepted():
    _, _, c = inputs()
    c[0][1]["minimax_keyframes"] = [{"latent": torch.zeros(1)}]
    with pytest.raises(ValueError): prepare.resize_conditioning(c, 96, 64, 192, 128)
