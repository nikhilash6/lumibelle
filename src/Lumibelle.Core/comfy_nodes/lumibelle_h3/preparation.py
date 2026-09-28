"""Pure tensor preparation, also exercised in CPU tests without ComfyUI."""
import copy
import torch
import torch.nn.functional as F


def resize_conditioning(conditioning, source_width, source_height, width, height):
    result = copy.deepcopy(conditioning)
    for entry in result:
        if not isinstance(entry, list) or len(entry) != 2 or not isinstance(entry[1], dict):
            raise ValueError("Invalid H3 conditioning entry")
        meta = entry[1]
        if meta.get("minimax_keyframes"):
            raise ValueError("Ref2VA refinement does not support first-frame/keyframe anchoring")
        for block in meta.get("minimax_refs", []):
            if block.get("kind") == "audio":
                continue
            if block.get("kind") != "image":
                raise ValueError("This refinement profile supports image and voice references only")
            z = block["latent"]
            if z.ndim not in (4, 5) or z.shape[1] != 24:
                raise ValueError("Invalid H3 image conditioning latent")
            # Preserve each reference's own aspect and temporal dimension; H3 RoPE
            # layout metadata must describe the resized tensor, not the old canvas.
            scale = ((width * height) / (source_width * source_height)) ** .5
            h = max(2, round(z.shape[-2] * scale / 2) * 2)
            w = max(2, round(z.shape[-1] * scale / 2) * 2)
            size = (z.shape[2], h, w) if z.ndim == 5 else (h, w)
            block["latent"] = F.interpolate(z.float(), size=size, mode="nearest-exact").to(z.dtype)
            block["latent_h"], block["latent_w"] = h, w
            if "latent_t" in block:
                block["latent_t"] = z.shape[2]
    return result


def audio_lock(video, audio):
    return (torch.ones((video.shape[0], 1, *video.shape[2:]), dtype=torch.float32, device=video.device),
            torch.zeros((audio.shape[0], 1, *audio.shape[2:]), dtype=torch.float32, device=audio.device))


def lock_noise(members):
    if len(members) != 2:
        raise ValueError("H3 needs a video and audio noise stream")
    return [members[0], torch.zeros_like(members[1])]
