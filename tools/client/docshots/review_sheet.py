"""Contact sheets of a docshots run's masked pictures, for review by eye: six frames a sheet (3 x 2, each 640 px wide, its file name on it), in the manifest's order.

    python -I review_sheet.py <out>/<res> [--only a,b]     writes <out>/<res>/_review/sheet-NN.jpg and prints which files each holds
"""
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

res_dir = Path(sys.argv[1])
only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
man = json.loads((res_dir / "manifest.json").read_text(encoding="utf-8"))
files = [f["file"] for rid, g in man["rows"].items() if only is None or rid in only for f in g.get("frames", [])]
out = res_dir / "_review"
out.mkdir(exist_ok=True)
for old in out.glob("sheet-*.jpg"):
    old.unlink()
try:
    font = ImageFont.truetype("arial.ttf", 18)      # Windows finds it in its fonts folder
except OSError:
    font = ImageFont.load_default()
W, H = 640, 360
for n in range(0, len(files), 6):
    sheet = Image.new("RGB", (3 * W, 2 * (H + 24)), (20, 20, 20))
    d = ImageDraw.Draw(sheet)
    names = files[n:n + 6]
    for i, name in enumerate(names):
        with Image.open(res_dir / name) as im:
            small = im.convert("RGB").resize((W, round(im.size[1] * W / im.size[0])), Image.LANCZOS)
        x, y = (i % 3) * W, (i // 3) * (H + 24)
        sheet.paste(small.crop((0, 0, W, min(H, small.size[1]))), (x, y + 24))
        d.text((x + 4, y + 2), name, fill=(255, 212, 126), font=font)
    path = out / f"sheet-{n // 6 + 1:02d}.jpg"
    sheet.save(path, quality=85)
    print(path.name, ": ", ", ".join(names))
