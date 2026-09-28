"""Version 1: JSON tree + SafeTensors only. Never deserialize Python objects."""
import json
import math
import os
import uuid
from pathlib import Path

import torch
from safetensors import safe_open
from safetensors.torch import save_file

VERSION = 1
MAX_HEADER = 16 * 1024 * 1024
MAX_BYTES = 8 * 1024**3
DTYPES = {torch.float16, torch.bfloat16, torch.float32, torch.float64,
          torch.int64, torch.int32, torch.int16, torch.int8, torch.uint8, torch.bool}


def validate_av(video, audio, width, height, frames):
    if not (32 <= width <= 8192 and 32 <= height <= 8192 and width % 32 == height % 32 == 0
            and 22 <= frames <= 362 and frames % 17 == 5):
        raise ValueError("Invalid H3 canvas or temporal grid")
    expected_t = ((frames - 5) // 17) * 5 + 2
    if tuple(video.shape) != (1, 24, expected_t, height // 16, width // 16):
        raise ValueError("Video latent shape does not match the captured canvas and duration")
    if tuple(audio.shape) != (1, 32, 2, round(frames / 24 * 40)):
        raise ValueError("Audio latent shape does not match the captured duration")
    for tensor in (video, audio):
        if not tensor.is_floating_point() or not torch.isfinite(tensor).all():
            raise ValueError("Invalid or non-finite joint latent")


def _encode(value, tensors, depth=0):
    if depth > 32:
        raise ValueError("Conditioning tree is too deep")
    if isinstance(value, torch.Tensor):
        if value.dtype not in DTYPES or value.ndim > 8 or value.numel() == 0:
            raise ValueError("Unsupported conditioning tensor")
        if value.is_floating_point() and not torch.isfinite(value).all():
            raise ValueError("Non-finite conditioning tensor")
        key = f"cond{len(tensors):05d}"
        tensors[key] = value.detach().cpu().contiguous().clone()
        return {"tensor": key}
    if value is None or isinstance(value, (bool, str, int)):
        return value
    if isinstance(value, float) and math.isfinite(value):
        return value
    if isinstance(value, (list, tuple)):
        return [_encode(item, tensors, depth + 1) for item in value]
    if isinstance(value, dict) and all(isinstance(k, str) for k in value):
        # Dictionaries are tagged separately so metadata cannot impersonate tensors.
        return {"dict": {k: _encode(v, tensors, depth + 1) for k, v in value.items()}}
    raise ValueError(f"Unsupported conditioning value: {type(value).__name__}")


def _decode(value, tensors, used, depth=0):
    if depth > 32:
        raise ValueError("Conditioning tree is too deep")
    if isinstance(value, dict):
        if set(value) == {"tensor"}:
            key = value["tensor"]
            if key in ("video", "audio") or key not in tensors:
                raise ValueError("Unknown conditioning tensor")
            used.add(key)
            return tensors[key]
        if set(value) == {"dict"} and isinstance(value["dict"], dict):
            return {k: _decode(v, tensors, used, depth + 1) for k, v in value["dict"].items()}
        raise ValueError("Unsupported JSON tree tag")
    if isinstance(value, list):
        return [_decode(item, tensors, used, depth + 1) for item in value]
    if value is None or isinstance(value, (str, bool, int)) or isinstance(value, float) and math.isfinite(value):
        return value
    raise ValueError("Unsupported JSON tree value")


def save_package(path, video, audio, conditioning, context, package_id, width, height, frames):
    uuid.UUID(package_id)
    validate_av(video, audio, width, height, frames)
    if not isinstance(conditioning, list) or not conditioning:
        raise ValueError("Missing H3 conditioning")
    tensors = {"video": video.detach().cpu().contiguous().clone(), "audio": audio.detach().cpu().contiguous().clone()}
    manifest = {"version": VERSION, "id": package_id, "width": width, "height": height,
                "frameCount": frames, "fps": 24, "context": context, "conditioning": _encode(conditioning, tensors)}
    metadata = json.dumps(manifest, allow_nan=False, separators=(",", ":"))
    if len(metadata.encode()) > MAX_HEADER // 2 or sum(t.numel() * t.element_size() for t in tensors.values()) > MAX_BYTES:
        raise ValueError("Refinement package exceeds supported limits")
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(".tmp")
    save_file(tensors, str(temp), metadata={"lumibelle": metadata})
    with temp.open("r+b") as stream:
        os.fsync(stream.fileno())
    os.replace(temp, path)


def load_package(path):
    path = Path(path)
    if path.stat().st_size > MAX_BYTES:
        raise ValueError("Refinement package is too large")
    with path.open("rb") as stream:
        length = int.from_bytes(stream.read(8), "little")
        if not 2 <= length <= MAX_HEADER:
            raise ValueError("Invalid SafeTensors header length")
    with safe_open(str(path), framework="pt", device="cpu") as saved:
        manifest = json.loads((saved.metadata() or {})["lumibelle"])
        if manifest.get("version") != VERSION or manifest.get("fps") != 24:
            raise ValueError("Unsupported refinement package version")
        uuid.UUID(manifest["id"])
        tensors = {key: saved.get_tensor(key) for key in saved.keys()}
    if any(t.dtype not in DTYPES or t.ndim > 8 or t.numel() == 0 or
           (t.is_floating_point() and not torch.isfinite(t).all()) for t in tensors.values()):
        raise ValueError("Invalid saved tensor")
    validate_av(tensors["video"], tensors["audio"], manifest["width"], manifest["height"], manifest["frameCount"])
    used = {"video", "audio"}
    conditioning = _decode(manifest["conditioning"], tensors, used)
    if used != set(tensors) or not isinstance(conditioning, list) or not conditioning:
        raise ValueError("Invalid conditioning tensor membership")
    return tensors["video"], tensors["audio"], conditioning, manifest
