"""Offline check of the mask boxes on real frames: draws the named boxes of docshots.py on a frame, writes a crop or a smaller copy to look at.

    python -I maskview.py <frame.png> <out.png|jpg> <x0,y0,x1,y1 crop or 'small'> NAME [NAME...]     NAME: a Mask or Box constant of docshots.py
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import docshots as d  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

frame, out, region, names = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
im = Image.open(frame).convert("RGB")
w, h = im.size
dr = ImageDraw.Draw(im)
colours = [(0, 255, 0), (255, 0, 255), (0, 200, 255), (255, 255, 0), (255, 120, 0), (255, 60, 60), (160, 120, 255)]
for i, name in enumerate(names):
    obj = getattr(d, name)
    box = obj.box if isinstance(obj, d.Mask) else obj
    dr.rectangle(d.mask_box(obj, w, h) if isinstance(obj, d.Mask) else d.place(box, w, h), outline=colours[i % len(colours)], width=2)
    print(name, colours[i % len(colours)], d.mask_box(obj, w, h) if isinstance(obj, d.Mask) else d.place(box, w, h))
if region == "small":
    im = im.resize((w // 2, h // 2))
else:
    im = im.crop(tuple(int(v) for v in region.split(",")))
im.save(out, quality=85) if out.endswith(".jpg") else im.save(out)
