"""CPU-only check of native SaveAnimatedWEBP settings. Requires Pillow==12.3.0.

No ComfyUI request, GPU work, or source-file modification. Encodings stay in memory.
Optionally re-encode every segment of an existing take with --archive-directory
and its captured --frame-count. Static segments expand using the captured count.
"""
import argparse
import io
import json
import time
from pathlib import Path

from PIL import Image, ImageDraw, features, __version__

SEGMENT = 24
DELAY = 1000 // 24


def decode(data, count):
    frames = []
    with Image.open(io.BytesIO(data)) as image:
        for index in range(image.n_frames):
            image.seek(index)
            frame = image.convert("RGB")  # Materialize/composite before reading delay.
            delay = image.info.get("duration", 0)
            copies = count if image.n_frames == 1 and delay == 0 else delay // DELAY
            assert copies > 0 and (delay == 0 or delay % DELAY == 0), delay
            frames.extend([frame] * copies)
    assert len(frames) == count, (len(frames), count)
    return frames


def check(name, segments):
    total = {0: {"seconds": 0, "bytes": 0}, 4: {"seconds": 0, "bytes": 0}}
    frame_count = 0
    segment_count = 0
    for frames in segments:
        frame_count += len(frames)
        segment_count += 1
        for method in (4, 0):
            output = io.BytesIO()
            started = time.perf_counter()
            frames[0].save(output, format="WEBP", save_all=True, append_images=frames[1:],
                           lossless=True, quality=80, method=method, duration=DELAY)
            total[method]["seconds"] += time.perf_counter() - started
            encoded = output.getvalue()
            total[method]["bytes"] += len(encoded)
            for expected, actual in zip(frames, decode(encoded, len(frames)), strict=True):
                assert expected.size == actual.size
                assert expected.tobytes() == actual.tobytes(), (name, segment_count, method)
    print(json.dumps({"fixture": name, "frames": frame_count, "segments": segment_count,
                      "pixels_and_timing": "identical", "compact": total[4], "fast": total[0]}), flush=True)


def synthetic(width, height, count, static=False):
    base = Image.new("RGB", (width, height))
    base.putdata([((x * 5) % 256, (y * 7) % 256, ((x + y) * 3) % 256)
                  for y in range(height) for x in range(width)])
    for start in range(0, count, SEGMENT):
        frames = []
        for index in range(start, min(count, start + SEGMENT)):
            frame = base.copy()
            # Duplicate frames and sparse moving rectangles exercise coalescing and
            # transparent delta-frame composition, plus whole static segments.
            if not static and start != SEGMENT:
                x = (index // 3 * 5) % (width - 10)
                ImageDraw.Draw(frame).rectangle((x, 4, x + 8, 12), fill=(255, 0, 200))
            frames.append(frame)
        yield frames


def archived(directory, count):
    paths = sorted(directory.glob("archive-*.webp"))
    assert len(paths) == (count + SEGMENT - 1) // SEGMENT
    for index, path in enumerate(paths):
        assert path.name == f"archive-{index:04}.webp"
        yield decode(path.read_bytes(), min(SEGMENT, count - index * SEGMENT))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive-directory", type=Path)
    parser.add_argument("--frame-count", type=int)
    args = parser.parse_args()
    print(json.dumps({"pillow": __version__, "libwebp": features.version("webp")}), flush=True)
    check("full-length-landscape", synthetic(128, 96, 362))
    check("full-length-portrait", synthetic(96, 128, 362))
    check("static", synthetic(128, 96, 362, static=True))
    check("single-frame", synthetic(96, 128, 1))
    if args.archive_directory:
        if not args.frame_count or not 1 <= args.frame_count <= 362:
            parser.error("--frame-count must specify the captured count (1-362)")
        check("existing-take", archived(args.archive_directory, args.frame_count))
