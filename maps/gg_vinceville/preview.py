#!/usr/bin/env python3
"""Renders the Workshop preview of gg_vinceville from layout.json (written by generate.py) with maps/previewkit.py. World
meshes are shaded by the map's sun in a colour per material, each face turned as its mesh winds it; the stock props are
stand-ins (trees as trunk and canopy, bushes as green blobs, vehicles and crates as boxes); ponds are blended in blue. The
near edge of the forest is cut away so the field can be seen.

  python preview.py <layout.json> <out.jpg> [--size 1280x720] [--ss 2]
"""
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from previewkit import Canvas, box, colour, lit, load, normal, parser, sun  # noqa: E402 (maps/previewkit.py: the renderer the previews share)

COL = {
    'wild_grass_1': (104, 142, 62), 'wild_grass_2': (88, 124, 58), 'ground_mud': (98, 84, 62), 'ground_dirt': (156, 126, 88),
    'ground_rock': (132, 128, 120), 'brick_red': (156, 74, 58), 'hrts2_brick': (176, 128, 84), 'stucco': (228, 222, 206),
    'paint_green': (124, 166, 116), 'paint_red': (176, 74, 62), 'planks_wood': (134, 104, 72), 'stone_wall_01': (156, 150, 138),
    'stone_wall_02': (122, 116, 106), 'cobblestone': (138, 134, 124), 'wood_floor': (150, 112, 76),
    'floor_boards': (142, 106, 72), 'tileroof': (176, 84, 54), 'roof_metal': (112, 118, 124), 'thatch': (196, 166, 96),
    'corrugated': (134, 138, 142), 'wood_planks_1': (124, 94, 62), 'wood_boards_worn': (132, 96, 66),
    'fabric': (126, 62, 60), 'straw': (218, 188, 112), 'concrete_floor': (162, 160, 154), 'wood_ceiling': (150, 116, 80),
    'plaster_01': (212, 206, 196), 'wallpaper': (182, 162, 140), 'plasterceiling': (232, 230, 226),
    'ceiling_wood': (140, 106, 72), 'carpet': (120, 44, 44),
}
SKIP = ('toolsnodraw', 'toolsskybox', 'toolsplayerclip', 'tools_postprocess')
TREE = {  # canopy shape: (bottom fraction of the height, colour)
    'poplar': (0.22, (70, 104, 48)), 'poplar_s': (0.25, (78, 112, 52)), 'maple1': (0.28, (84, 118, 50)),
    'maple2': (0.28, (90, 122, 52)), 'oak': (0.3, (76, 108, 46)), 'sumac': (0.3, (110, 120, 50)),
    'cypress': (0.12, (54, 84, 46)),
}


def icosphere():
    t = (1 + 5 ** 0.5) / 2
    v = np.array([(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
                  (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)], float)
    v /= np.linalg.norm(v, axis=1)[:, None]
    f = [[0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11], [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6],
         [7, 1, 8], [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9], [4, 9, 5], [2, 4, 11], [6, 2, 10],
         [8, 6, 7], [9, 8, 1]]
    # one subdivision for rounder blobs
    vs = [tuple(x) for x in v]
    cache = {}

    def mid(a, b):
        k = (min(a, b), max(a, b))
        if k not in cache:
            m = (np.array(vs[a]) + np.array(vs[b])) / 2
            m /= np.linalg.norm(m)
            cache[k] = len(vs)
            vs.append(tuple(m))
        return cache[k]
    nf = []
    for a, b, c in f:
        ab, bc, ca = mid(a, b), mid(b, c), mid(c, a)
        nf += [[a, ab, ca], [b, bc, ab], [c, ca, bc], [ab, bc, ca]]
    return np.array(vs), nf


SPHERE = icosphere()


def main():
    args = parser(__doc__).parse_args()
    lay = load(args.layout)
    half = lay['half']

    def cut(x, y):
        """the ground beyond the play area's south and east edges (towards the camera) is left out: whole cells, as the play
        area's edge is a cell edge, so the ground ends in a straight line."""
        return y < -half or x > half

    tris = []      # (triangle, colour, face id, a point behind the face: it is turned away from it)
    fid = 0
    plane_ids = {}  # a building is many small faces on one grid: one id per plane and material, so outlines show its edges only
    for si, s in enumerate(lay['solids']):
        vs = np.array(s['verts'], float)
        c = vs.mean(0)
        for f, m in zip(s['faces'], s['mats']):
            if any(k in m for k in SKIP):
                continue
            pts = vs[f]
            if s['tag'] == 'terrain':
                cx, cy = pts[:, 0].mean(), pts[:, 1].mean()
                if cut(cx, cy):
                    continue
            if s['tag'] == 'building':
                n = np.cross(pts[1] - pts[0], pts[2] - pts[0])
                n = n / np.linalg.norm(n)
                key = (si, m, tuple(np.round(n, 3)), round(float(np.dot(n, pts[0])), 2))
                if key not in plane_ids:
                    fid += 1
                    plane_ids[key] = fid
                face_id = plane_ids[key]
            else:
                fid += 1
                face_id = fid
            col = colour(COL, m)
            for k in range(1, len(f) - 1):
                tri = pts[[0, k, k + 1]]
                if s.get('wind') == 'given':      # wound counter-clockwise from outside: behind is against the winding's normal
                    behind = tri.mean(0) - np.cross(tri[1] - tri[0], tri[2] - tri[0])
                else:                             # a convex solid: behind is its middle
                    behind = c
                tris.append((tri, col, face_id, behind))
    # stand-ins for the stock props
    sv, sf = SPHERE
    rng = np.random.RandomState(7)
    for p in lay['props']:
        key, (x, y, z) = p['key'], p['origin']
        if cut(x, y) or p['tag'] in ('grass',):
            continue
        s = p['scale']
        (rx0, ry0, rz0), (rx1, ry1, rz1) = p['render']
        if p['tag'] in ('tree',) or key in TREE:
            h = (rz1 - rz0) * s
            frac, col = TREE.get(key, (0.3, (80, 112, 50)))
            rad = max(rx1 - rx0, ry1 - ry0) * s / 2 * (0.85 if 'poplar' in key or key == 'cypress' else 0.95)
            tr = max(5.0, rad * 0.07)
            v, q = box((x - tr, y - tr, z), (x + tr, y + tr, z + h * 0.6))
            fid += 1
            for qq in q:
                for k in (1, 2):
                    tris.append((v[[qq[0], qq[k], qq[k + 1]]], (92, 72, 52), fid, np.array([x, y, z + h * 0.3])))
            zc = z + h * (frac + (1 - frac) / 2)
            rz = h * (1 - frac) / 2
            cen = np.array([x, y, zc])
            jit = 1 + 0.08 * rng.randn(len(sv))
            vv = sv * jit[:, None] * np.array([rad, rad, rz]) + cen
            fid += 1
            shade = np.array(col) * rng.uniform(0.9, 1.1)
            for f in sf:
                tris.append((vv[f], tuple(shade), fid, cen))
        elif p['tag'] == 'card':
            h = (rz1 - rz0) * s
            w = (rx1 - rx0) * s / 2
            cen = np.array([x, y, z + h * 0.55])
            vv = sv * np.array([w, w, h * 0.45]) + cen
            fid += 1
            for f in sf:
                tris.append((vv[f], (64, 96, 50), fid, cen))
        elif p['tag'] == 'bush':
            rad = max(rx1 - rx0, ry1 - ry0) * s / 2
            h = (rz1 - rz0) * s
            cen = np.array([x, y, z + h * 0.45])
            vv = sv * np.array([rad, rad * 0.8, h * 0.55]) + cen
            fid += 1
            for f in sf:
                tris.append((vv[f], (72, 110, 52), fid, cen))
        else:
            # boxes in the model's frame, turned by its yaw
            yaw = math.radians(p['yaw'])
            cy_, sy_ = math.cos(yaw), math.sin(yaw)
            v, q = box((rx0 * s, ry0 * s, rz0 * s), (rx1 * s, ry1 * s, rz1 * s))
            v = np.stack([x + cy_ * v[:, 0] - sy_ * v[:, 1], y + sy_ * v[:, 0] + cy_ * v[:, 1], z + v[:, 2]], -1)
            col = {'hedge': (60, 96, 46), 'vehicle': (120, 50, 46)}.get(p['tag'], (130, 100, 70))
            if p['key'] == 'van':
                col = (196, 196, 186)
            elif p['key'] == 'hatch':
                col = (60, 96, 150)
            elif p['key'] == 'sedan2':
                col = (190, 170, 100)
            fid += 1
            cen = v.mean(0)
            for qq in q:
                for k in (1, 2):
                    tris.append((v[[qq[0], qq[k], qq[k + 1]]], col, fid, cen))
    # ponds: their water's surface
    water = []
    for (cx, cy, R, _) in lay['ponds']:
        z = next(w['z1'] for w in lay['waters'] if w['pond'][:2] == [cx, cy])
        n = 48
        ring = [np.array([cx + R * math.cos(2 * math.pi * k / n), cy + R * math.sin(2 * math.pi * k / n), z]) for k in range(n)]
        cen = np.array([cx, cy, z])
        for k in range(n):
            water.append(np.array([cen, ring[k], ring[(k + 1) % n]]))

    # camera: from the south-east, high, looking at the middle of the field
    cv = Canvas(args.size, args.ss, eye=(2050.0, -2350.0, 1650.0), at=(-60.0, 120.0, -40.0), fov=52)
    light = sun(50, 135)
    cv.sky((92, 146, 212), (214, 230, 242))
    for tri, col, i, c in tris:
        n = normal(tri, c)
        if n is not None and cv.faces_camera(n, tri):
            cv.fill(tri, lit(col, n, light, 0.46, 0.14, 0.66, 1.25), i)
    cv.outline(0.62)
    for tri in water:
        cv.blend(tri, np.array([84, 138, 164]), 0.65)
    cv.haze(2600, 4200, 0.45, (206, 224, 238))
    nh = sum(1 for b in lay['buildings'] if b['name'].startswith('H'))
    cv.save(args.out, 'VINCEVILLE', f'gg_vinceville  -  {nh} houses, a barn and two ponds in a field full of ambushes', (40, 70, 30))


if __name__ == '__main__':
    main()
