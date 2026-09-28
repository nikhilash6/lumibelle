import json
import os
import re
import uuid
from pathlib import Path

import torch
import folder_paths
from aiohttp import web
from server import PromptServer
from comfy.nested_tensor import NestedTensor
from comfy_extras.nodes_custom_sampler import Noise_RandomNoise

from .package import MAX_BYTES, load_package, save_package, validate_av
from .preparation import audio_lock, lock_noise, resize_conditioning

PROTOCOL = "lumibelle-h3-v1"


def protocol_input():
    return ([PROTOCOL],)


def path_for(token, output=False):
    if not re.fullmatch(r"[0-9a-f]{32}", token):
        raise ValueError("Invalid refinement transfer identity")
    root = folder_paths.get_output_directory() if output else folder_paths.get_input_directory()
    return Path(root) / "lumibelle" / "refinement" / (token + ".safetensors")


@PromptServer.instance.routes.get("/lumibelle/refinement/v1/{token}")
async def download(request):
    try:
        path = path_for(request.match_info["token"], True)
    except ValueError:
        raise web.HTTPBadRequest()
    if not path.is_file():
        raise web.HTTPNotFound()
    return web.FileResponse(path, headers={"Cache-Control": "no-store"})


@PromptServer.instance.routes.post("/lumibelle/refinement/v1/upload")
async def upload(request):
    # Stream to a random app-owned filename. Never accept a client filesystem path.
    reader = await request.multipart()
    part = await reader.next()
    if part is None or part.name != "package":
        raise web.HTTPBadRequest(text="A refinement package is required")
    token = uuid.uuid4().hex
    path = path_for(token)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(".upload")
    try:
        count = 0
        with temp.open("xb") as stream:
            while chunk := await part.read_chunk(1024 * 1024):
                count += len(chunk)
                if count > MAX_BYTES:
                    raise web.HTTPRequestEntityTooLarge(max_size=MAX_BYTES, actual_size=count)
                stream.write(chunk)
            stream.flush()
            os.fsync(stream.fileno())
        # Full tensor validation happens in the worker, not on the web event loop.
        os.replace(temp, path)
        return web.json_response({"token": token})
    finally:
        temp.unlink(missing_ok=True)


class LumibelleH3CaptureV1:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"protocol": protocol_input(), "latent": ("LATENT",),
                "conditioning": ("CONDITIONING",), "context": ("STRING", {"multiline": True}),
                "package_id": ("STRING",), "width": ("INT", {"min": 32, "max": 8192}),
                "height": ("INT", {"min": 32, "max": 8192}), "frames": ("INT", {"min": 22, "max": 362})},
                "optional": {"locked_audio_source": ("LATENT",)}}

    RETURN_TYPES = ("LATENT",)
    FUNCTION = "capture"
    OUTPUT_NODE = True
    CATEGORY = "Lumibelle/H3"

    def capture(self, protocol, latent, conditioning, context, package_id, width, height, frames, locked_audio_source=None):
        if protocol != PROTOCOL:
            raise ValueError("Unsupported capture protocol")
        members = latent["samples"].unbind()
        if len(members) != 2:
            raise ValueError("Expected joint H3 video/audio latents")
        video, audio = members
        if locked_audio_source is not None:
            audio = locked_audio_source["samples"].unbind()[1].clone()
        token = uuid.UUID(package_id).hex
        save_package(path_for(token, True), video, audio, conditioning, json.loads(context),
                     package_id, width, height, frames)
        clean = {"samples": NestedTensor([video, audio])}
        return {"ui": {"refinement": [{"token": token, "protocol": PROTOCOL}]}, "result": (clean,)}


class LumibelleH3LoadV1:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"protocol": protocol_input(), "token": ("STRING",), "package_id": ("STRING",)}}

    RETURN_TYPES = ("LATENT", "LATENT", "CONDITIONING")
    RETURN_NAMES = ("joint_latent", "video_latent", "conditioning")
    FUNCTION = "load"
    CATEGORY = "Lumibelle/H3"

    def load(self, protocol, token, package_id):
        if protocol != PROTOCOL:
            raise ValueError("Unsupported loading protocol")
        video, audio, conditioning, manifest = load_package(path_for(token))
        if uuid.UUID(manifest["id"]) != uuid.UUID(package_id):
            raise ValueError("The uploaded package belongs to a different take")
        return ({"samples": NestedTensor([video, audio])}, {"samples": video}, conditioning)


class LockedAudioNoise:
    def __init__(self, seed):
        self.seed = seed
        self.noise = Noise_RandomNoise(seed)

    def generate_noise(self, latent):
        return NestedTensor(lock_noise(self.noise.generate_noise(latent).unbind()))


class LumibelleH3PrepareV1:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"protocol": protocol_input(), "source": ("LATENT",), "video": ("LATENT",),
                "conditioning": ("CONDITIONING",), "width": ("INT", {"min": 32, "max": 8192}),
                "height": ("INT", {"min": 32, "max": 8192}), "frames": ("INT", {"min": 22, "max": 362}),
                "lock_audio": ("BOOLEAN", {"default": True}), "seed": ("INT", {"min": 0, "max": 0xffffffffffffffff})}}

    RETURN_TYPES = ("LATENT", "CONDITIONING", "NOISE")
    FUNCTION = "prepare"
    CATEGORY = "Lumibelle/H3"

    def prepare(self, protocol, source, video, conditioning, width, height, frames, lock_audio, seed):
        if protocol != PROTOCOL:
            raise ValueError("Unsupported preparation protocol")
        original, audio = source["samples"].unbind()
        upscaled = video["samples"]
        source_height, source_width = original.shape[-2] * 16, original.shape[-1] * 16
        validate_av(original, audio, source_width, source_height, frames)
        validate_av(upscaled, audio, width, height, frames)
        if width < source_width or height < source_height or width > source_width * 4 or height > source_height * 4:
            raise ValueError("Refinement supports 1x to 4x spatial scaling only")
        joint = {"samples": NestedTensor([upscaled, audio.clone()])}
        if lock_audio:
            # Zero both injected noise AND sampler mask. A zero-noise audio stream
            # alone would still be updated by H3's joint diffusion transformer.
            joint["noise_mask"] = NestedTensor(audio_lock(upscaled, audio))
        cond = resize_conditioning(conditioning, source_width, source_height, width, height)
        return (joint, cond, LockedAudioNoise(seed) if lock_audio else Noise_RandomNoise(seed))


NODE_CLASS_MAPPINGS = {cls.__name__: cls for cls in (LumibelleH3CaptureV1, LumibelleH3LoadV1, LumibelleH3PrepareV1)}
NODE_DISPLAY_NAME_MAPPINGS = {name: name.replace("LumibelleH3", "Lumibelle H3 ").replace("V1", " (v1)") for name in NODE_CLASS_MAPPINGS}
