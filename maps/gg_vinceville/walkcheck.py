#!/usr/bin/env python3
"""Walk check for gg_vinceville, on layout.json (written by generate.py). Everything here is by walking: no jumping, no
crouching. A player is a box 32 x 32 and 72 tall that steps up at most 18, on ground no steeper than 45 degrees.

  1. Headroom: over every step of every staircase, at least 80 clear above the tread (72, and 8 to spare).
  2. Reach: walking from the field, a player reaches every spot of every floor of every building that a player fits on,
     every step of every staircase, and every spawn.

The world is looked at in columns every 8 units over the play area: in each, where it is inside a solid (from the crossings
of a vertical ray with the closed meshes), and so where a player can stand - on top of a solid, under 72 free. A spot holds
a player when no solid is within 16 of it between 18 and 72 over its floor; two neighbouring spots join when they are at
most a step (18) apart in height.

  python walkcheck.py <layout.json>          exit status 0 when everything passes"""
import collections
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from raycast import World  # noqa: E402

G = 8
STEP, HULL, REACH, HEAD = 18, 72, 16, 80

world = World(sys.argv[1])
lay = world.lay
HALF = lay['half']
TAG, N, S = world.TAG, world.N, world.S
n = int((2 * HALF - 16) / G)
coords = [-HALF + 8 + G * i + 0.37 for i in range(n)]
xs = [coords[i] for i in range(n) for _ in range(n)]
ys = [coords[j] + 0.24 for _ in range(n) for j in range(n)]
SOLID = lambda ts: (TAG[ts] != 'water') & (TAG[ts] != 'skybox')  # noqa: E731 - what a player cannot walk through, the sky aside
H = world.hits(xs, ys, SOLID)

# per column: where it is inside a solid, and the floors on top of those stretches
inside = {}     # (i, j) -> [(z0, z1, walkable top)]
for idx, h in enumerate(H):
    i, j = divmod(idx, n)
    per = collections.defaultdict(list)
    for z, t in h:
        per[S[t]].append((z, t))
    spans = []
    for s, cr in per.items():
        for k in range(0, len(cr) - 1, 2):
            spans.append((cr[k][0], cr[k + 1][0], N[cr[k + 1][1]] >= 0.7071))
    spans.sort()
    merged = []
    for z0, z1, walk in spans:
        if merged and z0 <= merged[-1][1] + 0.01:
            if z1 > merged[-1][1]:
                merged[-1] = (merged[-1][0], z1, walk)
        else:
            merged.append((z0, z1, walk))
    inside[(i, j)] = merged


def blocked(i, j, z0, z1):
    for a, b, _ in inside.get((i, j), ()):
        if a < z1 and z0 < b:
            return True
    return False


# spots a player stands on and fits on
spots = {}       # (i, j) -> [z]
for (i, j), spans in inside.items():
    levels = []
    for k, (z0, z1, walk) in enumerate(spans):
        if not walk:
            continue
        above = spans[k + 1][0] if k + 1 < len(spans) else math.inf
        if above - z1 < HULL:
            continue
        r = REACH // G
        if any(blocked(i + di, j + dj, z1 + STEP, z1 + HULL) for di in range(-r, r + 1) for dj in range(-r, r + 1)):
            continue
        levels.append(z1)
    if levels:
        spots[(i, j)] = levels


def nearest(x, y, z, tol=STEP):
    i, j = int(round((x - 0.37 - (-HALF + 8)) / G)), int(round((y - 0.61 - (-HALF + 8)) / G))
    best = None
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for lz in spots.get((i + di, j + dj), ()):
                if abs(lz - z) <= tol and (best is None or abs(lz - z) < abs(best[2] - z)):
                    best = (i + di, j + dj, lz)
    return best


# walk from the field: the spot nearest the first outdoor spawn
seed = next(nearest(s['x'], s['y'], s['z']) for s in lay['spawns'] if s['where'] == 'out' and nearest(s['x'], s['y'], s['z']))
seen = {seed}
todo = collections.deque([seed])
while todo:
    i, j, z = todo.popleft()
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            if di == dj == 0:
                continue
            for lz in spots.get((i + di, j + dj), ()):
                if abs(lz - z) <= STEP and (i + di, j + dj, lz) not in seen:
                    seen.add((i + di, j + dj, lz))
                    todo.append((i + di, j + dj, lz))

bad = 0
lines = []
# 1. headroom over every step
steps = 0
for b in lay['buildings']:
    for st in b['steps']:
        lo, hi = st['lo'], st['hi']
        cx, cy = (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2
        h = world.hits([cx + 0.37], [cy + 0.61], SOLID)[0]
        over = [z for z, t in h if z > hi[2] + 0.01]
        clear = (min(over) - hi[2]) if over else math.inf
        steps += 1
        if clear < HEAD:
            bad += 1
            lines.append(f"  {b['name']}: only {clear:.0f} clear over the step at {hi[2]}")
print(f'1. headroom: {steps} steps' + ('' if bad == 0 else f', {bad} with less than {HEAD} over the tread') +
      (f', every one {HEAD} or more clear' if bad == 0 else ''))

# 2. reach: every floor spot, every step, every spawn
bad2 = 0
floor_spots = unreached = 0
for b in lay['buildings']:
    missed = 0
    for fl in b['floors']:
        r, z = fl['rect'], fl['z']
        i0, i1 = int(math.ceil((r[0] - (-HALF + 8)) / G)), int((r[2] - (-HALF + 8)) / G)
        j0, j1 = int(math.ceil((r[1] - (-HALF + 8)) / G)), int((r[3] - (-HALF + 8)) / G)
        for i in range(i0, i1 + 1):
            for j in range(j0, j1 + 1):
                for lz in spots.get((i, j), ()):
                    if abs(lz - z) < 0.5:
                        floor_spots += 1
                        if (i, j, lz) not in seen:
                            missed += 1
    for st in b['steps']:
        lo, hi = st['lo'], st['hi']
        p = nearest((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, hi[2], tol=1)
        if p is None or p not in seen:
            missed += 1
            lines.append(f"  {b['name']}: the step at {hi[2]} is not reached on foot")
    if missed:
        bad2 += 1
        unreached += missed
        lines.append(f"  {b['name']}: {missed} spots of its floors or stairs not reached on foot")
spawn_missed = 0
for s in lay['spawns']:
    p = nearest(s['x'], s['y'], s['z'])
    if p is None or p not in seen:
        spawn_missed += 1
        lines.append(f"  {s['class']} at ({s['x']}, {s['y']}, {s['z']:.1f}) is not reached on foot")
print(f"2. reach: {len(seen)} spots reached on foot of {sum(len(v) for v in spots.values())}; {floor_spots} spots on "
      f"{sum(len(b['floors']) for b in lay['buildings'])} floors of {len(lay['buildings'])} buildings and "
      f"{sum(len(b['steps']) for b in lay['buildings'])} steps"
      + (f', {unreached} not reached' if unreached else ', every one reached') +
      (f'; {spawn_missed} spawns not reached' if spawn_missed else f"; all {len(lay['spawns'])} spawns reached"))
for line in lines[:30]:
    print(line)
ok = bad == 0 and bad2 == 0 and spawn_missed == 0
print('walkcheck: PASS' if ok else 'walkcheck: FAIL')
sys.exit(0 if ok else 1)
