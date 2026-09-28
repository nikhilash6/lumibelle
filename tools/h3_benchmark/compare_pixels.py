"""Compare archived RGB pixels, avoiding MP4 encoder/container differences.

Frame differences locate changes for visual review; they are not quality scores.
"""
import argparse
import json
from itertools import zip_longest
from pathlib import Path

from PIL import Image, ImageChops, ImageStat

from metrics import atomic_json


def difference(left, right):
    if left.size != right.size:
        raise ValueError("Cannot compare different output dimensions")
    delta = ImageChops.difference(left.convert("RGB"), right.convert("RGB"))
    maximum = max(high for low, high in delta.getextrema())
    return dict(exact=maximum == 0, maximum_channel_difference=maximum,
                mean_absolute_channel_difference=sum(ImageStat.Stat(delta).mean) / 3)


def frames(directory, label):
    for path in sorted(directory.glob(label+"-frames-*.webp")):
        with Image.open(path) as archive:
            for index in range(archive.n_frames):
                archive.seek(index)
                yield archive.convert("RGB")


def compare(directory, left, right):
    manifest = json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    outputs = {o["label"]:o for o in manifest["outputs"]}
    rows = []
    for index, (a, b) in enumerate(zip_longest(frames(directory,left), frames(directory,right))):
        if a is None or b is None:
            raise ValueError("Archived frame counts differ")
        rows.append(dict(frame=index, **difference(a,b)))
        a.close()
        b.close()
    if len(rows) != manifest["frames"]:
        raise ValueError("Incomplete archived frame sequence")
    result = dict(left=left, right=right, frames=len(rows), all_pixels_exact=all(r["exact"] for r in rows),
        video_latents_exact=outputs[left]["latent_sha256"][0] == outputs[right]["latent_sha256"][0],
        audio_latents_exact=outputs[left]["latent_sha256"][1] == outputs[right]["latent_sha256"][1],
        decoded_audio_exact=outputs[left]["decoded_audio_sha256"] == outputs[right]["decoded_audio_sha256"],
        differences=rows, note="Pixel differences describe changes, not perceptual quality or flicker.")
    destination=directory/(left+"-vs-"+right+"-pixels.json")
    atomic_json(destination,result)
    print(f"{destination}: {len(rows)} frames; all pixels exact: {result['all_pixels_exact']}")


if __name__ == "__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("directory",type=Path)
    parser.add_argument("left")
    parser.add_argument("right")
    opts=parser.parse_args()
    compare(opts.directory,opts.left,opts.right)
