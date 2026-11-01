#!/usr/bin/env python3
"""Renders the Workshop preview of gg_moonglass from layout.json (written by generate.py): a CPU z-buffer rasterizer in
numpy - opaque pieces shaded by the map's sun with outlines, the glass blended back to front in its five tints, the Earth
painted from the generated texture, stars in the black sky.

  python preview.py <layout.json> <out.jpg> [--earth <earth_color.png>] [--size 1280x720] [--ss 2]
"""
import argparse
import json
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

COL = {'regolith': (108, 108, 112), 'module_white': (196, 198, 202), 'hr_concrete_wall_001': (150, 150, 153),
       'hr_metal_trim_002': (78, 84, 94), 'hr_metal_panel_001': (108, 113, 122), 'hr_metal_floor_001': (98, 103, 110),
       'moss': (72, 112, 62)}
GLASS = {'glass_clear': ((172, 222, 255), 0.16), 'glass_tint': ((40, 50, 62), 0.45), 'glass_frost': ((232, 236, 242), 0.58),
         'glass_floor': ((130, 200, 184), 0.40), 'glass_green': ((130, 210, 140), 0.24),
         'glass_thick': ((122, 168, 194), 0.38)}
SKIP = ('toolsskybox', 'toolsplayerclip', 'tools_postprocess')


def colour(mat):
    for k, v in COL.items():
        if k in mat:
            return np.array(v, float)
    return np.array((170, 170, 170), float)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('layout')
    ap.add_argument('out')
    ap.add_argument('--earth', default=None)
    ap.add_argument('--size', default='1280x720')
    ap.add_argument('--ss', type=int, default=2)
    args = ap.parse_args()
    W0, H0 = map(int, args.size.split('x'))
    W, H = W0 * args.ss, H0 * args.ss
    lay = json.load(open(args.layout))
    earth = np.asarray(Image.open(args.earth).convert('RGB'), float) if args.earth else None

    opaque, glass = [], []
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
                col = colour(mat) * tint
                emissive = False
            for k in range(1, len(f) - 1):
                opaque.append((v[[f[0], f[k], f[k + 1]]], col, fid, c, emissive))
    for p in lay['panes']:
        v = np.array(p['verts'], float)
        key = next(k for k in GLASS if k in p['mat'])
        for f in p['faces']:
            fid += 1
            for k in range(1, len(f) - 1):
                glass.append((v[[f[0], f[k], f[k + 1]]], GLASS[key], fid))

    eye = np.array([2700.0, -3150.0, 1050.0])
    at = np.array([60.0, 260.0, 560.0])
    fwd = at - eye
    fwd /= np.linalg.norm(fwd)
    right = np.cross(fwd, [0, 0, 1.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, fwd)
    f = (W / 2) / math.tan(math.radians(40) / 2)

    def project(P):
        d = P - eye
        return np.stack([W / 2 + f * (d @ right) / (d @ fwd), H / 2 - f * (d @ up) / (d @ fwd), d @ fwd], -1)

    pitch, yaw = math.radians(38), math.radians(40)
    sun = -np.array([math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), -math.sin(pitch)])

    zbuf = np.full((H, W), np.inf)
    img = np.zeros((H, W, 3))
    ids = np.zeros((H, W), np.int64)
    rng = np.random.default_rng(3)
    for _ in range(1400):
        x, y = rng.integers(0, W), rng.integers(0, H)
        b = rng.uniform(60, 255)
        img[max(0, y - args.ss // 2):y + 1, max(0, x - args.ss // 2):x + 1] = b

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
        z = 1 / np.where(m, w0 / az + w1 / bz + w2 / cz, 1)
        cb(slice(y0, y1 + 1), slice(x0, x1 + 1), m, z)

    for tri, col, i, c, emissive in opaque:
        n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        ln = np.linalg.norm(n)
        if ln < 1e-9:
            continue
        n /= ln
        if np.dot(n, tri.mean(0) - c) < 0:
            n = -n
        if np.dot(n, eye - tri.mean(0)) <= 0:
            continue
        if emissive:
            shade = col
        else:
            lam = max(0.0, float(np.dot(n, sun)))
            shade = col * min(1.3, 0.30 + 0.10 * n[2] + 0.85 * lam)

        def put(sy, sx, m, z, shade=shade, i=i):
            zb = zbuf[sy, sx]
            w = m & (z < zb)
            zb[w] = z[w]
            img[sy, sx][w] = shade
            ids[sy, sx][w] = i
        raster(tri, put)
    edge = np.zeros((H, W), bool)
    edge[:, 1:] |= ids[:, 1:] != ids[:, :-1]
    edge[1:, :] |= ids[1:, :] != ids[:-1, :]
    img[edge & (ids > 1)] *= 0.6

    def dist(e):
        return float(np.linalg.norm(e[0].mean(0) - eye))
    glass.sort(key=dist, reverse=True)
    for tri, (col, alpha), i in glass:
        n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        ln = np.linalg.norm(n)
        if ln < 1e-9:
            continue
        n /= ln
        view = eye - tri.mean(0)
        facing = abs(float(np.dot(n, view / np.linalg.norm(view))))
        a = min(0.9, alpha + 0.2 * (1 - facing))
        lam = abs(float(np.dot(n, sun)))
        tint = np.array(col, float) * (0.75 + 0.45 * lam)

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
        big_f = ImageFont.truetype('segoeuib.ttf', 66)
        small = ImageFont.truetype('segoeui.ttf', 26)
    except OSError:
        big_f = small = ImageFont.load_default()
    d.text((44, 30), 'MOONGLASS', font=big_f, fill=(255, 255, 255), stroke_width=3, stroke_fill=(20, 30, 60))
    d.text((48, 110), f"gg_moonglass  -  a moon base under {len(lay['panes'])} panes of breakable glass", font=small,
           fill=(255, 255, 255), stroke_width=2, stroke_fill=(20, 30, 60))
    out.save(args.out, quality=92)
    print('wrote', args.out, out.size)


if __name__ == '__main__':
    main()
