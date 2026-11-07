"""What the maps' preview renderers share: a CPU z-buffer rasterizer in numpy - a camera, triangles shaded by the map's sun
with outlines where one face meets another, see-through triangles blended over them, distance haze - and the finished
picture, downsampled from the supersampled render, sharpened and titled, saved as the Workshop preview's JPEG. Each map's
preview.py builds its scene from layout.json (written by its generate.py) and picks the camera, the light and the colours."""
import argparse
import json
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont


def parser(doc):
    """preview.py's command line: <layout.json> <out.jpg> [--size 1280x720] [--ss 2]."""
    ap = argparse.ArgumentParser(description=doc, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('layout')
    ap.add_argument('out')
    ap.add_argument('--size', default='1280x720')
    ap.add_argument('--ss', type=int, default=2, help='supersampling: the render is this many times the size each way')
    return ap


def load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def colour(table, mat, default=(170, 170, 170)):
    """The colour of the first key of table that is part of the material's path."""
    for k, v in table.items():
        if k in mat:
            return v
    return default


def box(lo, hi):
    """A box's eight corners and its six faces as quads: top, bottom, then the four sides."""
    v = np.array([[hi[0] if i & 1 else lo[0], hi[1] if i & 2 else lo[1], hi[2] if i & 4 else lo[2]] for i in range(8)], float)
    quads = [[4, 5, 7, 6], [0, 2, 3, 1], [1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4]]
    return v, quads


def sun(pitch, yaw):
    """The direction towards a sun at pitch and yaw degrees (as the map's light_environment has them)."""
    pitch, yaw = math.radians(pitch), math.radians(yaw)
    return -np.array([math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), -math.sin(pitch)])


def normal(tri, c):
    """A triangle's unit normal, turned away from c (the middle of its solid) or, for c None, upwards; None when the
    triangle has no area."""
    n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
    ln = np.linalg.norm(n)
    if ln < 1e-9:
        return None
    n /= ln
    if c is None:
        if n[2] < 0:
            n = -n
    elif np.dot(n, tri.mean(0) - c) < 0:
        n = -n
    return n


def lit(col, n, light, ambient, ambient_up, diffuse, cap):
    """A colour lit by the sun: an ambient part that grows as the face turns up, and the sun's Lambert part."""
    lam = max(0.0, float(np.dot(n, light)))
    return np.array(col) * min(cap, ambient + ambient_up * n[2] + diffuse * lam)


class Canvas:
    """The render: size times ss pixels each way, seen from eye towards at with a horizontal field of view of fov degrees.
    img holds the colours, zbuf the depth and ids the face each pixel shows (0 for none)."""

    def __init__(self, size, ss, eye, at, fov):
        self.W0, self.H0 = map(int, size.split('x'))
        self.W, self.H = W, H = self.W0 * ss, self.H0 * ss
        self.eye = np.array(eye)
        fwd = np.array(at) - self.eye
        fwd /= np.linalg.norm(fwd)
        right = np.cross(fwd, [0, 0, 1.0])
        right /= np.linalg.norm(right)
        self.fwd, self.right, self.up = fwd, right, np.cross(right, fwd)
        self.f = (W / 2) / math.tan(math.radians(fov) / 2)
        self.zbuf = np.full((H, W), np.inf)
        self.img = np.zeros((H, W, 3))
        self.ids = np.zeros((H, W), np.int64)

    def sky(self, top, bottom):
        """A sky from the top colour to the bottom one."""
        t = np.linspace(0, 1, self.H)[:, None]
        self.img[:] = (np.array(top) * (1 - t) + np.array(bottom) * t)[:, None, :].reshape(self.H, 1, 3)

    def project(self, P):
        d = P - self.eye
        x, y, z = d @ self.right, d @ self.up, d @ self.fwd
        return np.stack([self.W / 2 + self.f * x / z, self.H / 2 - self.f * y / z, z], -1)

    def faces_camera(self, n, tri):
        return np.dot(n, self.eye - tri.mean(0)) > 0

    def raster(self, tri, cb):
        """Calls cb(rows, columns, mask, depth) with the pixels the triangle covers inside its bounding box."""
        q = self.project(tri)
        if (q[:, 2] <= 10).any():
            return
        W, H = self.W, self.H
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

    def fill(self, tri, shade, i):
        """Paints an opaque triangle of face i where it is nearer than what is there."""
        def put(sy, sx, m, z):
            zb = self.zbuf[sy, sx]
            w = m & (z < zb)
            zb[w] = z[w]
            self.img[sy, sx][w] = shade
            self.ids[sy, sx][w] = i
        self.raster(tri, put)

    def blend(self, tri, tint, a):
        """Blends a see-through triangle over what is there where it is nearer, without hiding what lies behind it."""
        def put(sy, sx, m, z):
            w = m & (z < self.zbuf[sy, sx])
            reg = self.img[sy, sx]
            reg[w] = reg[w] * (1 - a) + tint * a
            self.img[sy, sx] = reg
        self.raster(tri, put)

    def outline(self, factor, above=0):
        """Darkens the pixels where the face changes, for faces with an id above `above`."""
        ids = self.ids
        edge = np.zeros((self.H, self.W), bool)
        edge[:, 1:] |= ids[:, 1:] != ids[:, :-1]
        edge[1:, :] |= ids[1:, :] != ids[:-1, :]
        self.img[edge & (ids > above)] *= factor

    def haze(self, start, depth, cap, colour):
        """Fades what is further than start towards colour, fully over depth more, up to cap."""
        zf = np.where(np.isinf(self.zbuf), 0, np.clip((self.zbuf - start) / depth, 0, cap))[..., None]
        self.img = self.img * (1 - zf) + np.array(colour) * zf

    def glass(self, panes, light, fresnel, cap, base, gain):
        """Blends glass triangles back to front: panes holds (triangle, (colour, alpha)); edge-on glass reads stronger
        (fresnel, up to cap), and the colour brightens with the sun on it (base + gain x light)."""
        panes = sorted(panes, key=lambda e: float(np.linalg.norm(e[0].mean(0) - self.eye)), reverse=True)
        for tri, (col, alpha) in panes:
            n = np.cross(tri[1] - tri[0], tri[2] - tri[0])
            ln = np.linalg.norm(n)
            if ln < 1e-9:
                continue
            n /= ln
            view = self.eye - tri.mean(0)
            facing = abs(float(np.dot(n, view / np.linalg.norm(view))))
            a = min(cap, alpha + fresnel * (1 - facing))
            lam = abs(float(np.dot(n, light)))
            self.blend(tri, np.array(col, float) * (base + gain * lam), a)

    def save(self, path, title, subtitle, stroke):
        """Downsamples, sharpens, writes the title and the line under it, and saves the JPEG."""
        out = Image.fromarray(np.clip(self.img, 0, 255).astype(np.uint8)).resize((self.W0, self.H0), Image.LANCZOS)
        out = out.filter(ImageFilter.UnsharpMask(radius=1.2, percent=60, threshold=2))
        d = ImageDraw.Draw(out)
        try:
            big = ImageFont.truetype('segoeuib.ttf', 66)
            small = ImageFont.truetype('segoeui.ttf', 26)
        except OSError:
            big = small = ImageFont.load_default()
        d.text((44, 30), title, font=big, fill=(255, 255, 255), stroke_width=3, stroke_fill=stroke)
        d.text((48, 110), subtitle, font=small, fill=(255, 255, 255), stroke_width=2, stroke_fill=stroke)
        out.save(path, quality=92)
        print('wrote', path, out.size)
