#!/usr/bin/env python3
"""Renders the Workshop preview of gg_moonglass from layout.json (written by generate.py) with maps/previewkit.py: opaque
pieces shaded by the map's sun with outlines, the glass blended back to front in its tints, the Earth painted from the
generated texture, stars in the black sky.

  python preview.py <layout.json> <out.jpg> [--earth <earth_color.png>] [--size 1280x720] [--ss 2]
"""
import math
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from previewkit import Canvas, colour, lit, load, normal, parser, sun  # noqa: E402 (maps/previewkit.py: the renderer the previews share)

COL = {'regolith': (108, 108, 112), 'module_white': (196, 198, 202), 'hr_concrete_wall_001': (150, 150, 153),
       'hr_metal_trim_002': (78, 84, 94), 'hr_metal_panel_001': (108, 113, 122), 'hr_metal_floor_001': (98, 103, 110),
       'moss': (72, 112, 62)}
GLASS = {'glass_clear': ((172, 222, 255), 0.16), 'glass_tint': ((40, 50, 62), 0.45), 'glass_frost': ((232, 236, 242), 0.58),
         'glass_floor': ((130, 200, 184), 0.40), 'glass_green': ((130, 210, 140), 0.24),
         'glass_thick': ((122, 168, 194), 0.38)}
SKIP = ('toolsskybox', 'toolsplayerclip', 'tools_postprocess')


def main():
    ap = parser(__doc__)
    ap.add_argument('--earth', default=None, help="the Earth's texture, as generate.py writes it")
    args = ap.parse_args()
    lay = load(args.layout)
    earth = np.asarray(Image.open(args.earth).convert('RGB'), float) if args.earth else None

    opaque, glass = [], []   # (triangle, colour, face id, middle of its solid, emissive); (triangle, (tint, alpha))
    # a wide moon plain for the picture only (the map's own ground ends at its skybox)
    big = 9000
    plain = [np.array(p, float) for p in ((-big, -big, -9), (big, -big, -9), (big, big, -9), (-big, big, -9))]
    opaque.append((np.array([plain[0], plain[1], plain[2]]), np.array(COL['regolith'], float), 1, np.array((0, 0, -100.0)), False))
    opaque.append((np.array([plain[0], plain[2], plain[3]]), np.array(COL['regolith'], float), 1, np.array((0, 0, -100.0)), False))
    fid = 2
    for s in lay['solids']:
        if any(k in s['mats'][0] for k in SKIP) or s['tag'] in ('outside',):
            continue
        v = np.array(s['verts'], float)
        c = v.mean(0)
        tint = np.array(s.get('tint', (255, 255, 255)), float) / 255
        for f, mat in zip(s['faces'], s['mats']):
            fid += 1
            if s['tag'] == 'earth' and earth is not None:
                d = v[f].mean(0) - np.array((430, 830, 1190.0))
                d /= np.linalg.norm(d)
                u = (math.atan2(d[1], d[0]) / (2 * math.pi) + 0.5) % 1
                vv = 0.5 - math.asin(max(-1, min(1, d[2]))) / math.pi
                col = earth[min(earth.shape[0] - 1, int(vv * earth.shape[0])), min(earth.shape[1] - 1, int(u * earth.shape[1]))]
                emissive = True
            else:
                col = np.array(colour(COL, mat), float) * tint
                emissive = False
            for k in range(1, len(f) - 1):
                opaque.append((v[[f[0], f[k], f[k + 1]]], col, fid, c, emissive))
    for p in lay['panes']:
        v = np.array(p['verts'], float)
        key = next(k for k in GLASS if k in p['mat'])
        for f in p['faces']:
            for k in range(1, len(f) - 1):
                glass.append((v[[f[0], f[k], f[k + 1]]], GLASS[key]))

    cv = Canvas(args.size, args.ss, eye=(2700.0, -3150.0, 1050.0), at=(60.0, 260.0, 560.0), fov=40)
    light = sun(38, 40)
    rng = np.random.default_rng(3)
    for _ in range(1400):   # stars
        x, y = rng.integers(0, cv.W), rng.integers(0, cv.H)
        b = rng.uniform(60, 255)
        cv.img[max(0, y - args.ss // 2):y + 1, max(0, x - args.ss // 2):x + 1] = b
    for tri, col, i, c, emissive in opaque:
        n = normal(tri, c)
        if n is not None and cv.faces_camera(n, tri):
            cv.fill(tri, col if emissive else lit(col, n, light, 0.30, 0.10, 0.85, 1.3), i)
    cv.outline(0.6, above=1)   # not on the plain (face 1)
    cv.glass(glass, light, fresnel=0.2, cap=0.9, base=0.75, gain=0.45)
    cv.save(args.out, 'MOONGLASS', f"gg_moonglass  -  a moon base under {len(lay['panes'])} panes of breakable glass", (20, 30, 60))


if __name__ == '__main__':
    main()
