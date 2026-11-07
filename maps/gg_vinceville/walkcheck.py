#!/usr/bin/env python3
"""Walk check for gg_vinceville, on layout.json (written by generate.py). Everything here is by walking: no jumping. A player is
a box 32 x 32, 72 tall standing and 54 crouched, that steps up at most 18, on ground no steeper than 45 degrees. To step up it
rises in place first, so it needs room over where it stands as well as over where it steps to; and a box that only touches a
floor's underside does not get through, so every height here has 2 to spare.

  1. Headroom: over every step of every staircase, at least 80 clear above the middle of its tread.
  2. Stairs: the box walks every staircase up and back down, standing and crouched, along lines across its whole width - from
     hard against the wall to overhanging the open side by 15 - from 48 before the first step to 48 past the last: never stuck.
  3. Doors: the box walks straight in through every door, along lines across its whole clear width, from 48 outside to 96 (three
     boxes) inside: never stuck.
  4. Reach: walking from the field, a player reaches every spot of every floor of every building that a player fits on,
     every step of every staircase, and every spawn.

Sections 2 and 3 move the box 2 units at a time over columns 2 units apart: in each, where it is inside a solid (from the
crossings of a vertical ray with the closed meshes). The box stands on the highest top under it within a step of its feet, and
is stuck when a solid is in it. Section 4 looks at columns every 8 units over the play area: a spot holds a player when no
solid is within 16 of it between 18 and 74 over its floor, and two neighbouring spots join when they are at most a step (18)
apart in height, with room over the lower one to rise to the higher.

  python walkcheck.py <layout.json>          exit status 0 when everything passes"""
import collections
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from raycast import World  # noqa: E402

G = 8
STEP, HULL, CROUCH, REACH, HEAD, SPARE = 18, 72, 54, 16, 80, 2
F = 2            # the fine columns' spacing (sections 2 and 3)

world = World(sys.argv[1])
lay = world.lay
HALF = lay['half']
TAG, N, S = world.TAG, world.N, world.S
SOLID = lambda ts: (TAG[ts] != 'water') & (TAG[ts] != 'skybox')  # noqa: E731 - what a player cannot walk through, the sky aside


def spans_of(hits, walkable=False):
    """Each column's stretches inside a solid, merged, from the ordered crossings of its ray: (z0, z1[, walkable top])."""
    out = []
    for h in hits:
        per = collections.defaultdict(list)
        for z, t in h:
            per[S[t]].append((z, t))
        spans = []
        for cr in per.values():
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
        out.append(merged if walkable else [(z0, z1) for z0, z1, _ in merged])
    return out


# ---------------------------------------------------------------- the box, walked over fine columns (sections 2 and 3)
class Region:
    """Columns F apart over a rectangle, at x = F*i + 1.37, y = F*j + 1.61; the box centred on (F*k, F*l) covers the 16 x 16
    columns from k - 8 to k + 7, from 14.6 behind its middle to 15.4 ahead."""

    def __init__(self, x0, y0, x1, y1):
        self.i0, self.j0 = math.floor(x0 / F) - 9, math.floor(y0 / F) - 9
        ni, nj = math.ceil(x1 / F) + 9 - self.i0, math.ceil(y1 / F) + 9 - self.j0
        xs = [F * (self.i0 + a) + 1.37 for a in range(ni) for _ in range(nj)]
        ys = [F * (self.j0 + b) + 1.61 for _ in range(ni) for b in range(nj)]
        cols = spans_of(world.hits(xs, ys, SOLID))
        depth = max(1, max(len(c) for c in cols))
        self.lo = np.full((ni, nj, depth), np.inf)
        self.hi = np.full((ni, nj, depth), -np.inf)
        for n, c in enumerate(cols):
            for s, (z0, z1) in enumerate(c):
                self.lo[n // nj, n % nj, s], self.hi[n // nj, n % nj, s] = z0, z1

    def box(self, x, y):
        a, b = round(x / F) - 8 - self.i0, round(y / F) - 8 - self.j0
        return self.lo[a:a + 16, b:b + 16].ravel(), self.hi[a:a + 16, b:b + 16].ravel()


def stand(region, x, y, ref, height):
    """Where the box's feet are at (x, y), on the highest top within a step of ref; None when something is in it there."""
    lo, hi = region.box(x, y)
    tops = hi[(hi <= ref + STEP + 0.01) & np.isfinite(hi)]
    if tops.size == 0:
        return None
    feet = float(tops.max())
    return None if ((lo < feet + height + SPARE) & (hi > feet + 0.01)).any() else feet


def walk(region, line, z_start, height):
    """Walks the box along the line (its middle's places, F apart), from feet at z_start. None when it gets through, else where
    and why it is stuck."""
    feet = prev = None
    for x, y in line:
        lo, hi = region.box(x, y)
        ref = z_start if feet is None else feet
        tops = hi[(hi <= ref + STEP + 0.01) & np.isfinite(hi)]
        if tops.size == 0:
            return x, y, ref, 'nothing to stand on'
        new = float(tops.max())
        over = (lo < new + height + SPARE) & (hi > new + 0.01)
        if over.any():
            z = float(lo[over].min())
            return x, y, new, (f'only {z - new:.0f} clear over the feet' if z > new + STEP else 'a wall in the way')
        if feet is not None and new > feet + 0.01:
            plo, phi = prev
            over = (plo < new + height + SPARE) & (phi > feet + 0.01)
            if over.any():
                return x, y, new, f'no room to rise {new - feet:.0f} onto the next step: {float(plo[over].min()) - feet:.0f} clear'
        feet, prev = new, (lo, hi)
    return None


def even(v, up):
    return F * (math.ceil(v / F) if up else math.floor(v / F))


bad = 0
lines = []
# 1. headroom over the middle of every step
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

# 2. every staircase, up and down, standing and crouched, across its whole width
stair_walks = stair_stuck = staircases = 0
for b in lay['buildings']:
    if not b['steps']:
        continue
    staircases += 1
    st = sorted(b['steps'], key=lambda s: s['hi'][2])
    first, last = st[0], st[-1]
    mid = lambda s: ((s['lo'][0] + s['hi'][0]) / 2, (s['lo'][1] + s['hi'][1]) / 2)  # noqa: E731
    ax = 0 if abs(mid(last)[0] - mid(first)[0]) > abs(mid(last)[1] - mid(first)[1]) else 1    # the axis it climbs along
    up = 1 if mid(last)[ax] > mid(first)[ax] else -1
    front = first['lo'][ax] if up > 0 else first['hi'][ax]
    back = last['hi'][ax] if up > 0 else last['lo'][ax]
    l0, l1 = min(s['lo'][1 - ax] for s in st), max(s['hi'][1 - ax] for s in st)
    z_low = max(f['z'] for f in b['floors'] if f['z'] <= first['lo'][2] + 2)
    z_high = last['hi'][2]
    a0, a1 = front - up * 48, back + up * 48
    box_lo = [min(a0, a1), l0 - 40]
    box_hi = [max(a0, a1), l1 + 40]
    if ax:
        box_lo.reverse()
        box_hi.reverse()
    region = Region(box_lo[0], box_lo[1], box_hi[0], box_hi[1])
    along = list(range(even(min(a0, a1), True), even(max(a0, a1), False) + 1, F))
    if up < 0:
        along.reverse()
    across = sorted({even(l0 - 15, True), even(l1 + 15, False)} | set(range(even(l0 - 15, True), even(l1 + 15, False) + 1, 4)))
    r_low = next(f['rect'] for f in b['floors'] if abs(f['z'] - z_low) < 0.5)
    r_high = next(f['rect'] for f in b['floors'] if abs(f['z'] - z_high) < 0.5)
    inside_of = lambda rc, p: rc[0] < p[0] < rc[2] and rc[1] < p[1] < rc[3]  # noqa: E731
    walked = 0
    for c in across:
        path = [(a, c) if ax == 0 else (c, a) for a in along]
        # from the first place at the foot, inside, where the box stands on the floor or within a step of it, to the last place
        # at the top, inside, where it stands on the floor up there; a line with neither runs into a wall: nobody walks there
        low = [k for k, p in enumerate(path) if inside_of(r_low, p) and (stand(region, *p, z_low, HULL) or -1) >= z_low - 0.5]
        high = [k for k, p in enumerate(path) if inside_of(r_high, p) and abs((stand(region, *p, z_high, HULL) or 0) - z_high) < 0.5]
        if not low or not high or low[0] >= high[-1]:
            continue
        path = path[low[0]:high[-1] + 1]
        walked += 1
        for name, height in (('standing', HULL), ('crouched', CROUCH)):
            for way, p, z0 in (('up', path, stand(region, *path[0], z_low, HULL)), ('down', path[::-1], z_high)):
                stair_walks += 1
                stuck = walk(region, p, z0, height)
                if stuck:
                    stair_stuck += 1
                    x, y, feet, why = stuck
                    if stair_stuck <= 12:
                        lines.append(f"  {b['name']}: going {way} {name} at {'xy'[1 - ax]} {c}, stuck at ({x}, {y}), feet {feet:.0f}: {why}")
    if walked == 0:
        stair_stuck += 1
        lines.append(f"  {b['name']}: no line across its stair to walk")
print(f'2. stairs: {staircases} staircases, {stair_walks} walks up and down, standing and crouched, across their width' +
      (f', {stair_stuck} stuck' if stair_stuck else ', none stuck'))

# 3. every door, walked straight in across its whole clear width
door_walks = door_stuck = doors = 0
for b in lay['buildings']:
    for d in b.get('doors', []):
        doors += 1
        r, (dx, dy) = d['rect'], d['inward']
        ax = 0 if dx else 1                       # the axis it is walked along
        sgn = dx or dy
        outer, inner = (r[ax], r[ax + 2]) if sgn > 0 else (r[ax + 2], r[ax])
        w0, w1 = r[1 - ax], r[3 - ax]
        a0, a1 = outer - sgn * 48, inner + sgn * 96
        box_lo = [min(a0, a1), w0 - 24]
        box_hi = [max(a0, a1), w1 + 24]
        if ax:
            box_lo.reverse()
            box_hi.reverse()
        region = Region(box_lo[0], box_lo[1], box_hi[0], box_hi[1])
        along = list(range(even(min(a0, a1), True), even(max(a0, a1), False) + 1, F))
        if sgn < 0:
            along.reverse()
        c0, c1 = even(w0 + 16, True), even(w1 - 16, False)
        across = sorted({c0, c1} | set(range(c0, c1 + 1, 4)))
        for c in across:
            door_walks += 1
            stuck = walk(region, [(a, c) if ax == 0 else (c, a) for a in along], b['level'], HULL)
            if stuck:
                door_stuck += 1
                x, y, feet, why = stuck
                if door_stuck <= 12:
                    lines.append(f"  {b['name']}: in through the {d['wall']} door at {'xy'[1 - ax]} {c}, stuck at ({x}, {y}), feet "
                                 f"{feet:.0f}: {why}")
if doors == 0:
    door_stuck += 1
    lines.append('  the layout records no doors')
print(f'3. doors: {doors} doors, {door_walks} walks straight in across their clear width' +
      (f', {door_stuck} stuck' if door_stuck else ', none stuck'))

# 4. reach: per column of the play area, where it is inside a solid, and the floors on top of those stretches
n = int((2 * HALF - 16) / G)
coords = [-HALF + 8 + G * i + 0.37 for i in range(n)]
xs = [coords[i] for i in range(n) for _ in range(n)]
ys = [coords[j] + 0.24 for _ in range(n) for j in range(n)]
inside = {divmod(idx, n): spans for idx, spans in enumerate(spans_of(world.hits(xs, ys, SOLID), walkable=True))}


def blocked(i, j, z0, z1):
    for a, b_, _ in inside.get((i, j), ()):
        if a < z1 and z0 < b_:
            return True
    return False


r = REACH // G
spots = {}       # (i, j) -> [z]: spots a player stands on and fits on
for (i, j), spans in inside.items():
    levels = []
    for k, (z0, z1, walkable) in enumerate(spans):
        if not walkable:
            continue
        above = spans[k + 1][0] if k + 1 < len(spans) else math.inf
        if above - z1 < HULL + SPARE:
            continue
        if any(blocked(i + di, j + dj, z1 + STEP, z1 + HULL + SPARE) for di in range(-r, r + 1) for dj in range(-r, r + 1)):
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


def rises(i, j, z, z2):
    """Room over the spot (i, j, z) for the box to rise to z2 before it steps over."""
    return not any(blocked(i + di, j + dj, z + HULL, z2 + HULL + SPARE) for di in range(-r, r + 1) for dj in range(-r, r + 1))


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
                if abs(lz - z) <= STEP and (i + di, j + dj, lz) not in seen and (lz <= z + 4 or rises(i, j, z, lz)):
                    seen.add((i + di, j + dj, lz))
                    todo.append((i + di, j + dj, lz))

bad2 = 0
floor_spots = unreached = 0
for b in lay['buildings']:
    missed = 0
    for fl in b['floors']:
        rc, z = fl['rect'], fl['z']
        i0, i1 = int(math.ceil((rc[0] - (-HALF + 8)) / G)), int((rc[2] - (-HALF + 8)) / G)
        j0, j1 = int(math.ceil((rc[1] - (-HALF + 8)) / G)), int((rc[3] - (-HALF + 8)) / G)
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
print(f"4. reach: {len(seen)} spots reached on foot of {sum(len(v) for v in spots.values())}; {floor_spots} spots on "
      f"{sum(len(b['floors']) for b in lay['buildings'])} floors of {len(lay['buildings'])} buildings and "
      f"{sum(len(b['steps']) for b in lay['buildings'])} steps"
      + (f', {unreached} not reached' if unreached else ', every one reached') +
      (f'; {spawn_missed} spawns not reached' if spawn_missed else f"; all {len(lay['spawns'])} spawns reached"))
for line in lines[:40]:
    print(line)
ok = bad == 0 and stair_stuck == 0 and door_stuck == 0 and bad2 == 0 and spawn_missed == 0
print('walkcheck: PASS' if ok else 'walkcheck: FAIL')
sys.exit(0 if ok else 1)
