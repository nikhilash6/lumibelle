"""CPU-only complete-frame contact sheets and basic decode checks for clip review.

Every decoded frame is represented, not just selected keyframes. The MP4 remains
the source for motion/audio review; these sheets do not establish either.
"""
import argparse
import json
from pathlib import Path


def review(path):
    import av
    import numpy as np
    from PIL import Image, ImageDraw
    directory = path.parent/(path.stem+"-review")
    directory.mkdir(exist_ok=True)
    cells = []
    differences = []
    previous = None
    count = 0

    def save_sheet():
        sheet = Image.new("RGB",(4*336,((len(cells)+3)//4)*216),(23,26,31))
        for offset,cell in enumerate(cells):
            sheet.paste(cell,((offset%4)*336,(offset//4)*216))
        sheet.save(directory/f"frames-{count-len(cells):03d}-{count-1:03d}.jpg",quality=94)
        cells.clear()

    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        fps = str(stream.average_rate)
        for frame in container.decode(video=0):
            rgb = frame.to_ndarray(format="rgb24")
            small = frame.to_image().resize((336,192),Image.Resampling.LANCZOS)
            cell = Image.new("RGB",(336,216),(23,26,31))
            cell.paste(small,(0,24))
            ImageDraw.Draw(cell).text((8,5),f"Frame {count:03d} / {count/24:.3f}s",fill="white")
            cells.append(cell)
            # Descriptive signal only: natural motion also raises frame difference.
            gray = np.asarray(small.convert("L"),dtype=np.float32)
            if previous is not None:
                differences.append(float(np.mean(np.abs(gray-previous))))
            previous = gray
            # H3's temporal ratio is 17 decoded frames per 5 latent frames,
            # after its initial 5-frame/2-latent block. LBH's boundary at
            # latent 32 is near decoded frame 107, not frame 128.
            if count in (0,36,72,96,100,104,108,112,116,124,128,132,140):
                Image.fromarray(rgb).save(directory/f"frame-{count:03d}.png")
            count += 1
            if len(cells)==24:
                save_sheet()
        if cells:
            save_sheet()
    evidence = dict(frames=count,fps=fps,adjacent_mean_luma_difference_8bit=differences,
                    note="Frame difference is descriptive and is not a flicker or quality score.")
    (directory/"decode-review.json").write_text(json.dumps(evidence,indent=2),encoding="utf-8")
    print(f"Reviewed decode of {count} frames: {directory}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("clips",type=Path,nargs="+")
    for clip in parser.parse_args().clips:
        review(clip)
