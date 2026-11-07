"""Vertical rays against gg_vinceville's world, as layout.json (written by generate.py) has it: what checks.py and
walkcheck.py look with. Every face of every mesh, turned outward as the map compile turns it, cut into triangles."""
import collections
import json
import math

import numpy as np


def newell(vs, f):
    n = [0.0, 0.0, 0.0]
    for k in range(len(f)):
        a, b = vs[f[k]], vs[f[(k + 1) % len(f)]]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    l = math.sqrt(sum(c * c for c in n))
    return [c / l for c in n] if l > 0 else [0.0, 0.0, 0.0]


class World:
    BIN = 64

    def __init__(self, path):
        self.lay = json.load(open(path, encoding='utf-8'))
        self.tools = set(self.lay['tools'])
        self.solids = self.lay['solids'] + [dict(w, tag='water', name='') for w in self.lay['waters']]
        # faces as given, or turned outward from the mesh's centre (the 'centroid' winding of convex boxes and prisms)
        self.faces = []    # (solid index, vertex positions, normal, drawn, vertex indices)
        for si, s in enumerate(self.solids):
            vs = s['verts']
            c = [sum(v[i] for v in vs) / len(vs) for i in range(3)]
            for f, m in zip(s['faces'], s['mats']):
                n = newell(vs, f)
                if s.get('wind') == 'centroid':
                    fc = [sum(vs[i][k] for i in f) / len(f) for k in range(3)]
                    if sum(n[k] * (fc[k] - c[k]) for k in range(3)) < 0:
                        f = list(reversed(f))
                        n = [-x for x in n]
                self.faces.append((si, [vs[i] for i in f], n, m not in self.tools, f))
        ta, tb, tc, tf = [], [], [], []
        for fi, (si, poly, n, dr, f) in enumerate(self.faces):
            if abs(n[2]) < 1e-6:
                continue                       # a vertical face: a vertical ray only grazes it
            for k in range(1, len(poly) - 1):
                ta.append(poly[0])
                tb.append(poly[k])
                tc.append(poly[k + 1])
                tf.append(fi)
        self.A, self.B, self.C = np.array(ta, float), np.array(tb, float), np.array(tc, float)
        self.N = np.array([self.faces[f][2][2] for f in tf])                  # the face's normal z: > 0 looks up
        self.S = np.array([self.faces[f][0] for f in tf])                     # the solid
        self.TAG = np.array([self.solids[self.faces[f][0]]['tag'] for f in tf])
        self.NAME = np.array([self.solids[self.faces[f][0]].get('name', '') for f in tf])
        self.DRAWN = np.array([self.faces[f][3] for f in tf])
        lo = np.minimum(np.minimum(self.A, self.B), self.C)
        hi = np.maximum(np.maximum(self.A, self.B), self.C)
        bins = collections.defaultdict(list)
        for t in range(len(self.A)):
            for bx in range(int(math.floor(lo[t, 0] / self.BIN)), int(math.floor(hi[t, 0] / self.BIN)) + 1):
                for by in range(int(math.floor(lo[t, 1] / self.BIN)), int(math.floor(hi[t, 1] / self.BIN)) + 1):
                    bins[(bx, by)].append(t)
        self.bins = {k: np.array(v) for k, v in bins.items()}

    def hits(self, xs, ys, which=None):
        """For each point, the sorted list of (z, triangle) where the vertical line through it meets a triangle (edges
        included); which(triangles) -> mask picks the triangles that count."""
        out = [[] for _ in xs]
        pts = collections.defaultdict(list)
        for i, (x, y) in enumerate(zip(xs, ys)):
            pts[(int(math.floor(x / self.BIN)), int(math.floor(y / self.BIN)))].append(i)
        A, B, C = self.A, self.B, self.C
        for key, idx in pts.items():
            ts = self.bins.get(key)
            if ts is None:
                continue
            if which is not None:
                ts = ts[which(ts)]
                if len(ts) == 0:
                    continue
            P = np.array([(xs[i], ys[i]) for i in idx])
            a, b, c = A[ts], B[ts], C[ts]
            den = (b[:, 1] - c[:, 1]) * (a[:, 0] - c[:, 0]) + (c[:, 0] - b[:, 0]) * (a[:, 1] - c[:, 1])
            ok = np.abs(den) > 1e-9
            den = np.where(ok, den, 1)
            px, py = P[:, 0:1], P[:, 1:2]
            w0 = ((b[:, 1] - c[:, 1]) * (px - c[:, 0]) + (c[:, 0] - b[:, 0]) * (py - c[:, 1])) / den
            w1 = ((c[:, 1] - a[:, 1]) * (px - c[:, 0]) + (a[:, 0] - c[:, 0]) * (py - c[:, 1])) / den
            w2 = 1 - w0 - w1
            inside = ok & (w0 >= -1e-9) & (w1 >= -1e-9) & (w2 >= -1e-9)
            z = w0 * a[:, 2] + w1 * b[:, 2] + w2 * c[:, 2]
            for r, i in enumerate(idx):
                sel = np.nonzero(inside[r])[0]
                out[i] = sorted((float(z[r, s]), int(ts[s])) for s in sel)
        return out
