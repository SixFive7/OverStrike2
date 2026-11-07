#!/usr/bin/env python3
"""Checks gg_vinceville's world on layout.json (written by generate.py), on the meshes themselves: no holes, no overlapping
faces, spawns that work. Exit status 0 when everything passes.

  1. Closed: every world mesh and every piece of water is a closed manifold - each edge used exactly once each way.
  2. Overlaps: no two drawn faces (any material but the tools) lie in one plane, within half a unit, over each other by more
     than one square unit - facing the same way (z-fighting) or each other (two drawn faces back to back).
  3. Ground: a vertical ray every 16 units over the whole terrain meets its top exactly once: no hole, nothing doubled.
  4. From above: over the play area a ray every 16 units from the sky first meets a face that looks up - never the inside of a
     solid through a gap - and always meets something.
  5. Floors: inside every building, on every floor, a ray every 8 units from under the next ceiling first meets that
     building's own floor, looking up: no hole in a floor, no ground showing through one; and every step's tread is there.
  6. Spawns: each stands on a floor or the ground (a face looking up, at most a step of 18 under its feet; the entity is 8
     higher and the game drops the player), in a player's box (32 x 32 x 72) that is inside no solid and no solid prop, out
     of the water, at least 96 from every other spawn; and there are enough for every mode (Arms Race and Deathmatch spawn
     anywhere: 16 deathmatch spawns for 16 players; a team mode: 8 a side).

  python -I checks.py <layout.json>"""
import collections
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from raycast import World  # noqa: E402

world = World(sys.argv[1])
lay, solids, faces = world.lay, world.solids, world.faces
TN, TS, TTAG, TDRAWN, NAME = world.N, world.S, world.TAG, world.DRAWN, world.NAME
HALF, OUTER = lay['half'], lay['outer']
problems = collections.Counter()
report = []


def fail(kind, text):
    problems[kind] += 1
    if problems[kind] <= 8:
        report.append(f'  {kind}: {text}')


# ---------------------------------------------------------------- 1. closed
per_solid = collections.defaultdict(collections.Counter)
for (si, poly, n, drawn, f) in faces:
    for k in range(len(f)):
        per_solid[si][(f[k], f[(k + 1) % len(f)])] += 1
for si, count in per_solid.items():
    s = solids[si]
    for e, c in count.items():
        if c > 1:
            fail('closed', f"{s['tag']} {s.get('name', '')}: edge {s['verts'][e[0]]}-{s['verts'][e[1]]} used {c} times one way")
        elif (e[1], e[0]) not in count:
            fail('closed', f"{s['tag']} {s.get('name', '')}: edge {s['verts'][e[0]]}-{s['verts'][e[1]]} has no face on its other side")


# ---------------------------------------------------------------- 2. overlaps
def clip(poly, a, b):
    """Sutherland-Hodgman: the part of convex poly on the left of the directed line a->b (2D)."""
    out = []
    for i in range(len(poly)):
        p, q = poly[i], poly[(i + 1) % len(poly)]
        sp = (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
        sq = (b[0] - a[0]) * (q[1] - a[1]) - (b[1] - a[1]) * (q[0] - a[0])
        if sp >= 0:
            out.append(p)
        if (sp >= 0) != (sq >= 0):
            t = sp / (sp - sq)
            out.append((p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])))
    return out


def area(poly):
    return 0.5 * sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))


def to2d(poly, n):
    ax = max(range(3), key=lambda i: abs(n[i]))
    u, v = [i for i in range(3) if i != ax]
    pts = [(p[u], p[v]) for p in poly]
    return pts if area(pts) > 0 else pts[::-1]


drawn = [x for x in faces if x[3] and len(x[1]) >= 3]
groups = collections.defaultdict(list)
for idx, (si, poly, n, _, _) in enumerate(drawn):
    k = max(range(3), key=lambda i: abs(n[i]))
    sgn = 1 if n[k] > 0 else -1
    key = tuple(round(sgn * c, 3) for c in n)
    groups[key].append((sgn * sum(n[i] * poly[0][i] for i in range(3)), idx))
overlap_pairs = 0
for key, items in groups.items():
    items.sort()
    for a in range(len(items)):
        da, ia = items[a]
        si_a, pa, na, _, _ = drawn[ia]
        pa2 = to2d(pa, na)
        xa0, xa1 = min(p[0] for p in pa2), max(p[0] for p in pa2)
        ya0, ya1 = min(p[1] for p in pa2), max(p[1] for p in pa2)
        b = a + 1
        while b < len(items) and items[b][0] - da < 0.5:
            si_b, pb, nb, _, _ = drawn[items[b][1]]
            pb2 = to2d(pb, nb)
            if min(p[0] for p in pb2) < xa1 - 0.01 and xa0 + 0.01 < max(p[0] for p in pb2) and \
                    min(p[1] for p in pb2) < ya1 - 0.01 and ya0 + 0.01 < max(p[1] for p in pb2):
                inter = pa2
                for i in range(len(pb2)):
                    inter = clip(inter, pb2[i], pb2[(i + 1) % len(pb2)])
                    if not inter:
                        break
                if inter and abs(area(inter)) > 1.0:
                    overlap_pairs += 1
                    same = sum(na[i] * nb[i] for i in range(3)) > 0
                    fail('overlap', f"{'same way' if same else 'back to back'}: {solids[si_a]['tag']} {solids[si_a].get('name', '')} and "
                                    f"{solids[si_b]['tag']} {solids[si_b].get('name', '')}, {abs(area(inter)):.0f} units^2 at {pa[0]}")
            b += 1

# ---------------------------------------------------------------- 3. the ground: one top over every point
step = 16
xs = [x + 5 for x in range(-OUTER + 8, OUTER - 8, step) for _ in range(-OUTER + 8, OUTER - 8, step)]
ys = [y + 7 for _ in range(-OUTER + 8, OUTER - 8, step) for y in range(-OUTER + 8, OUTER - 8, step)]
for x, y, h in zip(xs, ys, world.hits(xs, ys, lambda ts: (TTAG[ts] == 'terrain') & (TN[ts] > 0))):
    if len(h) != 1:
        fail('ground', f'({x}, {y}): the terrain top met {len(h)} times')
ground_rays = len(xs)

# ---------------------------------------------------------------- 4. from above: the first face met looks up
xs = [x + 5 for x in range(-HALF, HALF, step) for _ in range(-HALF, HALF, step)]
ys = [y + 7 for _ in range(-HALF, HALF, step) for y in range(-HALF, HALF, step)]
real = lambda ts: TDRAWN[ts] | (TTAG[ts] == 'water')  # noqa: E731
for x, y, h in zip(xs, ys, world.hits(xs, ys, real)):
    if not h:
        fail('from above', f'({x}, {y}): nothing under the sky')
    elif TN[h[-1][1]] <= 0:
        t = h[-1][1]
        fail('from above', f"({x}, {y}): the first face met at z {h[-1][0]:.1f} looks down ({TTAG[t]} {NAME[t]})")
above_rays = len(xs)

# ---------------------------------------------------------------- 5. floors, and every step's tread
floor_rays = 0
for bld in lay['buildings']:
    tops = sorted(f['z'] for f in bld['floors'])
    steps = [(s['lo'], s['hi']) for s in bld['steps']]
    for fl in bld['floors']:
        z, r = fl['z'], fl['rect']
        ceiling = min([t - 12 for t in tops if t > z] + [bld['wall_top'] - 2])
        xs, ys = [], []
        for x in np.arange(r[0] + 3, r[2] - 2, 8):
            for y in np.arange(r[1] + 5, r[3] - 2, 8):
                if not any(lo[0] - 1 < x < hi[0] + 1 and lo[1] - 1 < y < hi[1] + 1 for lo, hi in steps):   # stairs: below
                    xs.append(float(x))
                    ys.append(float(y))
        for x, y, h in zip(xs, ys, world.hits(xs, ys)):
            below = [(zz, t) for zz, t in h if zz < ceiling - 1 and (TDRAWN[t] or TTAG[t] == 'water')]
            if not below:
                fail('floors', f"{bld['name']} floor {z}: nothing under ({x}, {y})")
                continue
            zz, t = below[-1]
            if TN[t] <= 0 or TTAG[t] != 'building' or NAME[t] != bld['name'] or zz < bld['level'] + 7.5:
                fail('floors', f"{bld['name']} floor {z}: at ({x}, {y}) the first face is {TTAG[t]} at {zz:.1f}, looking "
                               f"{'up' if TN[t] > 0 else 'down'}")
        floor_rays += len(xs)
    for lo, hi in steps:
        xs, ys = [], []
        for x in np.arange(lo[0] + 3, hi[0] - 2, 8):
            for y in np.arange(lo[1] + 5, hi[1] - 2, 8):
                xs.append(float(x))
                ys.append(float(y))
        for x, y, h in zip(xs, ys, world.hits(xs, ys)):
            below = [(zz, t) for zz, t in h if zz < hi[2] + 1 and TDRAWN[t]]
            if not below or abs(below[-1][0] - hi[2]) > 0.01 or TN[below[-1][1]] <= 0 or NAME[below[-1][1]] != bld['name']:
                fail('floors', f"{bld['name']}: the step at {hi[2]} has no tread under ({x}, {y})")
        floor_rays += len(xs)


# ---------------------------------------------------------------- 6. spawns
def inside_solid(p):
    """True when p lies inside a closed solid other than water: an odd number of crossings straight up."""
    h = world.hits([p[0]], [p[1]], lambda ts: TTAG[ts] != 'water')[0]
    per = collections.Counter(TS[t] for z, t in h if z > p[2])
    return any(c % 2 for c in per.values())


spawns = lay['spawns']
for s in spawns:
    x, y, z = s['x'], s['y'], s['z']
    h = world.hits([x + 0.37], [y + 0.61], real)[0]
    under = [(zz, t) for zz, t in h if zz <= z + 8.01]
    if not under or not (z - 18 <= under[-1][0] <= z + 1.0) or TN[under[-1][1]] <= 0 or TTAG[under[-1][1]] == 'water':
        fail('spawns', f"{s['class']} at ({x}, {y}, {z}): no floor under it within a step (18) of its feet")
    for dx in (-15.5, -8, 0, 8, 15.5):
        for dy in (-15.5, -8, 0, 8, 15.5):
            for dz in (2, 12, 24, 36, 48, 60, 72):
                if inside_solid((x + dx + 0.13, y + dy + 0.29, z + dz)):
                    fail('spawns', f"{s['class']} at ({x}, {y}, {z}): its box is in a solid at {(x + dx, y + dy, z + dz)}")
                    break
    for p in lay['props']:
        if p['phys'] is None or p['tag'] in ('tree', 'bush', 'hedge', 'grass', 'card'):
            continue
        b = p['phys']
        r = max(abs(b[0][0]), abs(b[0][1]), abs(b[1][0]), abs(b[1][1])) * p['scale']
        if math.hypot(x - p['origin'][0], y - p['origin'][1]) < r + 16:
            fail('spawns', f"{s['class']} at ({x}, {y}): within reach of the prop {p['key']}")
    for (cx, cy, R, _) in lay['ponds']:
        if math.hypot(x - cx, y - cy) < R:
            fail('spawns', f"{s['class']} at ({x}, {y}): in a pond")
for i, a in enumerate(spawns):
    for b in spawns[i + 1:]:
        if math.hypot(a['x'] - b['x'], a['y'] - b['y']) < 96 and abs(a['z'] - b['z']) < 72:
            fail('spawns', f"{a['class']} and {b['class']} at ({a['x']}, {a['y']}) and ({b['x']}, {b['y']}): closer than 96")
by = collections.Counter(s['class'] for s in spawns)
if by['info_deathmatch_spawn'] < 16:
    fail('spawns', f"{by['info_deathmatch_spawn']} deathmatch spawns: Arms Race and Deathmatch take 16 players")
if by['info_player_terrorist'] < 8 or by['info_player_counterterrorist'] < 8:
    fail('spawns', f"{by['info_player_terrorist']} T and {by['info_player_counterterrorist']} CT spawns: a team mode takes 8 a side")

print(f'1. closed: {len(solids)} meshes and water pieces' + (f', {problems["closed"]} bad edges' if problems['closed'] else ', every edge once each way'))
print(f'2. overlaps: {len(drawn)} drawn faces in {len(groups)} planes' + (f', {overlap_pairs} overlapping pairs' if overlap_pairs else ', none over another'))
print(f'3. ground: {ground_rays} rays' + (f', {problems["ground"]} wrong' if problems['ground'] else ', the terrain top met once by each'))
print(f'4. from above: {above_rays} rays' + (f', {problems["from above"]} wrong' if problems['from above'] else ', each first meets a face looking up'))
print(f'5. floors: {floor_rays} rays in {len(lay["buildings"])} buildings' + (f', {problems["floors"]} wrong' if problems['floors'] else ', each meets its own floor or tread'))
print(f'6. spawns: {len(spawns)} ({by["info_deathmatch_spawn"]} deathmatch, {by["info_player_terrorist"]} T, '
      f'{by["info_player_counterterrorist"]} CT)' + (f', {problems["spawns"]} wrong' if problems['spawns'] else ', each on a floor, clear and apart'))
for line in report:
    print(line)
print('checks: PASS' if not problems else f'checks: FAIL {dict(problems)}')
sys.exit(1 if problems else 0)
