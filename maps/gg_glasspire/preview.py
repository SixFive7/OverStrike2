#!/usr/bin/env python3
"""Renders the Workshop preview of gg_glasspire from layout.json (written by generate.py): a CPU z-buffer rasterizer in
numpy, opaque pieces Lambert-shaded by the map's sun with outlines, glass blended back to front in its two tints.

  python preview.py <layout.json> <out.jpg> [--size 1280x720] [--ss 2]
"""
import argparse
import json
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

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


def colour(mat):
    for k, v in COL.items():
        if k in mat:
            return v
    return (170, 170, 170)


def box_tris(lo, hi):
    v = np.array([[hi[0] if i & 1 else lo[0], hi[1] if i & 2 else lo[1], hi[2] if i & 4 else lo[2]] for i in range(8)], float)
    quads = [[4, 5, 7, 6], [0, 2, 3, 1], [1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4]]
    roles = ['top', 'bottom', 'side', 'side', 'side', 'side']
    return v, quads, roles


def wedge_tris(s):
    a, b, c0, c1, z0, z1 = s['a'], s['b'], s['c0'], s['c1'], s['z0'], s['z1']
    P = (lambda u, c, z: (u, c, z)) if s['axis'] == 'x' else (lambda u, c, z: (c, u, z))
    v = np.array([P(a, c0, z0), P(b, c0, z0), P(b, c1, z0), P(a, c1, z0), P(b, c0, z1), P(b, c1, z1)], float)
    polys = [[0, 1, 2, 3], [1, 2, 5, 4], [0, 4, 5, 3], [0, 1, 4], [3, 2, 5]]
    roles = ['bottom', 'side', 'top', 'side', 'side']
    return v, polys, roles


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('layout')
    ap.add_argument('out')
    ap.add_argument('--size', default='1280x720')
    ap.add_argument('--ss', type=int, default=2)
    args = ap.parse_args()
    W0, H0 = map(int, args.size.split('x'))
    W, H = W0 * args.ss, H0 * args.ss
    lay = json.load(open(args.layout))

    opaque, glass = [], []   # (tri 3x3, rgb or (rgb, alpha), id)
    fid = 0
    for s in lay['solids']:
        if any(k in s['mats'].get('top', '') for k in SKIP):
            continue
        if s['tag'] == 'city' and s['lo'][1] < -500:
            continue   # the blocks between this camera and the tower
        v, polys, roles = box_tris(s['lo'], s['hi']) if s['kind'] == 'box' else wedge_tris(s)
        c = v.mean(0)
        for poly, role in zip(polys, roles):
            fid += 1
            col = colour(s['mats'][role])
            for k in range(1, len(poly) - 1):
                opaque.append((v[[poly[0], poly[k], poly[k + 1]]], col, fid, c))
    for p in lay['panes']:
        v, polys, _ = box_tris(p['lo'], p['hi'])
        tint = GLASS['glass_floor' if 'floor' in p['mat'] else 'glass_window']
        for poly in polys:
            fid += 1
            for k in range(1, len(poly) - 1):
                glass.append((v[[poly[0], poly[k], poly[k + 1]]], tint, fid, v.mean(0)))

    # camera
    eye = np.array([1650.0, -3500.0, 1900.0])
    at = np.array([0.0, 0.0, 330.0])
    fwd = at - eye
    fwd /= np.linalg.norm(fwd)
    right = np.cross(fwd, [0, 0, 1.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, fwd)
    fov = math.radians(33)
    f = (W / 2) / math.tan(fov / 2)

    def project(P):
        d = P - eye
        x, y, z = d @ right, d @ up, d @ fwd
        return np.stack([W / 2 + f * x / z, H / 2 - f * y / z, z], -1)

    pitch, yaw = math.radians(52), math.radians(128)
    sun_dir = -np.array([math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), -math.sin(pitch)])

    zbuf = np.full((H, W), np.inf)
    img = np.zeros((H, W, 3))
    ids = np.zeros((H, W), np.int64)
    # sky
    t = np.linspace(0, 1, H)[:, None]
    img[:] = (np.array([96, 150, 214]) * (1 - t) + np.array([206, 226, 244]) * t)[:, None, :].reshape(H, 1, 3)

    def raster(tri, cb):
        q = project(tri)
        if (q[:, 2] <= 10).any():
            return
        x0, x1 = int(max(0, np.floor(q[:, 0].min()))), int(min(W - 1, np.ceil(q[:, 0].max())))
        y0, y1 = int(max(0, np.floor(q[:, 1].min()))), int(min(H - 1, np.ceil(q[:, 1].max())))
        if x1 < x0 or y1 < y0:
            return
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay, az), (bx, by, bz), (cx, cy, cz) = q
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            return
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        w2 = 1 - w0 - w1
        m = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not m.any():
            return
        iz = w0 / az + w1 / bz + w2 / cz
        z = 1 / np.where(m, iz, 1)
        cb(slice(y0, y1 + 1), slice(x0, x1 + 1), m, z)

    for tri, col, i, c in opaque:
        n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        ln = np.linalg.norm(n)
        if ln < 1e-9:
            continue
        n /= ln
        if np.dot(n, tri.mean(0) - c) < 0:
            n = -n
        if np.dot(n, eye - tri.mean(0)) <= 0:
            continue   # back face
        lam = max(0.0, float(np.dot(n, sun_dir)))
        amb = 0.50 + 0.12 * n[2]
        shade = np.array(col) * min(1.25, amb + 0.62 * lam)

        def put(sy, sx, m, z, shade=shade, i=i):
            zb = zbuf[sy, sx]
            w = m & (z < zb)
            zb[w] = z[w]
            img[sy, sx][w] = shade
            ids[sy, sx][w] = i
        raster(tri, put)

    # outlines where the face id changes
    edge = np.zeros((H, W), bool)
    edge[:, 1:] |= ids[:, 1:] != ids[:, :-1]
    edge[1:, :] |= ids[1:, :] != ids[:-1, :]
    img[edge & (ids > 0)] *= 0.55
    # distance haze
    zf = np.where(np.isinf(zbuf), 0, np.clip((zbuf - 1800) / 4000, 0, 0.55))[..., None]
    img = img * (1 - zf) + np.array([200, 220, 238]) * zf

    # glass, back to front, depth-tested against the opaque scene
    def depth(e):
        return float(np.linalg.norm(e[0].mean(0) - eye))
    glass.sort(key=depth, reverse=True)
    for tri, (col, alpha), i, c in glass:
        n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        ln = np.linalg.norm(n)
        if ln < 1e-9:
            continue
        n /= ln
        view = eye - tri.mean(0)
        facing = abs(float(np.dot(n, view / np.linalg.norm(view))))
        a = min(0.85, alpha + 0.25 * (1 - facing))   # a little fresnel: edge-on glass reads stronger
        lam = max(0.0, abs(float(np.dot(n, sun_dir))))
        tint = np.array(col) * (0.85 + 0.3 * lam)

        def blend(sy, sx, m, z, tint=tint, a=a):
            w = m & (z < zbuf[sy, sx])
            reg = img[sy, sx]
            reg[w] = reg[w] * (1 - a) + tint * a
            img[sy, sx] = reg
        raster(tri, blend)

    out = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).resize((W0, H0), Image.LANCZOS)
    out = out.filter(ImageFilter.UnsharpMask(radius=1.2, percent=60, threshold=2))
    d = ImageDraw.Draw(out)
    try:
        big = ImageFont.truetype('segoeuib.ttf', 66)
        small = ImageFont.truetype('segoeui.ttf', 26)
    except OSError:
        big = small = ImageFont.load_default()
    d.text((44, 30), 'GLASSPIRE', font=big, fill=(255, 255, 255), stroke_width=3, stroke_fill=(30, 60, 90))
    d.text((48, 110), 'gg_glasspire  -  four floors, a roof and 292 panes of breakable glass', font=small,
           fill=(255, 255, 255), stroke_width=2, stroke_fill=(30, 60, 90))
    out.save(args.out, quality=92)
    print('wrote', args.out, out.size)


if __name__ == '__main__':
    main()
