#!/usr/bin/env python3
"""Renders the Workshop preview of gg_glasspire from layout.json (written by generate.py) with maps/previewkit.py: opaque
pieces Lambert-shaded by the map's sun with outlines, glass blended back to front in its two tints.

  python preview.py <layout.json> <out.jpg> [--size 1280x720] [--ss 2]
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from previewkit import Canvas, box, colour, lit, load, normal, parser, sun  # noqa: E402 (maps/previewkit.py: the renderer the previews share)

COL = {
    'hr_concrete_polished_001': (176, 172, 164), 'hr_concrete_ceiling_painted_001': (206, 204, 198),
    'hr_concrete_trim_001': (152, 149, 142), 'hr_concrete_column_002': (188, 182, 172),
    'grey_warm': (196, 186, 170), 'blue_light': (140, 178, 214), 'green_light': (150, 198, 158),
    'yellow_light': (226, 204, 132), 'orange': (222, 138, 78),
    'hr_metal_floor_001': (112, 118, 124), 'hr_metal_panel_001': (98, 104, 112),
    'hr_concrete_floor_05': (118, 116, 110), 'hr_concrete_wall_004': (150, 154, 162),
}
GLASS = {'glass_window': ((150, 222, 255), 0.30), 'glass_floor': ((140, 236, 206), 0.42)}
SKIP = ('toolsskybox', 'toolsplayerclip', 'tools_postprocess')
BOX_ROLES = ['top', 'bottom', 'side', 'side', 'side', 'side']


def wedge(s):
    a, b, c0, c1, z0, z1 = s['a'], s['b'], s['c0'], s['c1'], s['z0'], s['z1']
    P = (lambda u, c, z: (u, c, z)) if s['axis'] == 'x' else (lambda u, c, z: (c, u, z))
    v = np.array([P(a, c0, z0), P(b, c0, z0), P(b, c1, z0), P(a, c1, z0), P(b, c0, z1), P(b, c1, z1)], float)
    polys = [[0, 1, 2, 3], [1, 2, 5, 4], [0, 4, 5, 3], [0, 1, 4], [3, 2, 5]]
    roles = ['bottom', 'side', 'top', 'side', 'side']
    return v, polys, roles


def main():
    args = parser(__doc__).parse_args()
    lay = load(args.layout)

    opaque, glass = [], []   # (triangle, colour, face id, middle of its solid); (triangle, (tint, alpha))
    fid = 0
    for s in lay['solids']:
        if any(k in s['mats'].get('top', '') for k in SKIP):
            continue
        if s['tag'] == 'city' and s['lo'][1] < -500:
            continue   # the blocks between this camera and the tower
        if s['kind'] == 'box':
            v, polys = box(s['lo'], s['hi'])
            roles = BOX_ROLES
        else:
            v, polys, roles = wedge(s)
        c = v.mean(0)
        for poly, role in zip(polys, roles):
            fid += 1
            col = colour(COL, s['mats'][role])
            for k in range(1, len(poly) - 1):
                opaque.append((v[[poly[0], poly[k], poly[k + 1]]], col, fid, c))
    for p in lay['panes']:
        v, polys = box(p['lo'], p['hi'])
        tint = GLASS['glass_floor' if 'floor' in p['mat'] else 'glass_window']
        for poly in polys:
            for k in range(1, len(poly) - 1):
                glass.append((v[[poly[0], poly[k], poly[k + 1]]], tint))

    cv = Canvas(args.size, args.ss, eye=(1650.0, -3500.0, 1900.0), at=(0.0, 0.0, 330.0), fov=33)
    light = sun(52, 128)
    cv.sky((96, 150, 214), (206, 226, 244))
    for tri, col, i, c in opaque:
        n = normal(tri, c)
        if n is not None and cv.faces_camera(n, tri):
            cv.fill(tri, lit(col, n, light, 0.50, 0.12, 0.62, 1.25), i)
    cv.outline(0.55)
    cv.haze(1800, 4000, 0.55, (200, 220, 238))
    cv.glass(glass, light, fresnel=0.25, cap=0.85, base=0.85, gain=0.3)
    cv.save(args.out, 'GLASSPIRE', 'gg_glasspire  -  four floors, a roof and 292 panes of breakable glass', (30, 60, 90))


if __name__ == '__main__':
    main()
