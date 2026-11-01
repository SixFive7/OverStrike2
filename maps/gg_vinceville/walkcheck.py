#!/usr/bin/env python3
"""Walk check for gg_vinceville, on layout.json (written by generate.py). Everything here is by walking: no jumping, no
crouching. A player is a box 32 x 32 and 80 tall (72 plus 8 to spare) that moves in small steps and climbs at most 18.

  1. Headroom: over every step of every staircase, at least 80 clear above the tread.
  2. Floors: in every house, from every floor (cellar, ground, upstairs, attic) a player reaches every doorway and stair
     end (the anchors generate.py writes) on every other floor.
  3. Lanes: on each floor a 64-wide box (a lane of 64) reaches every doorway and stair end of that floor.
  4. Sewer: from one junction a player reaches every junction, every building's sewer door and the foot of every stair
     up (the barn's, the shed's, each manhole's); and each of those stairs, on a finer grid of its own, leads from its
     foot to its top.

  python walkcheck.py <layout.json>          exit status 0 when everything passes
"""
import json
import math
import sys
from collections import deque

import numpy as np

HEAD = 80
VEG = ('tree', 'bush', 'hedge', 'grass', 'card')      # props with no collision (generate.py gives them solid 0)


def newell(vs, f):
    n = [0.0, 0.0, 0.0]
    for k in range(len(f)):
        a, b = vs[f[k]], vs[f[(k + 1) % len(f)]]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    return n


def planes(vs, faces):
    cc = [sum(v[i] for v in vs) / len(vs) for i in range(3)]
    out = []
    for f in faces:
        n = newell(vs, f)
        l = math.sqrt(sum(x * x for x in n)) or 1.0
        n = [x / l for x in n]
        fc = [sum(vs[i][k] for i in f) / len(f) for k in range(3)]
        if sum(n[k] * (fc[k] - cc[k]) for k in range(3)) < 0:
            n = [-x for x in n]
        out.append((n, sum(n[k] * vs[f[0]][k] for k in range(3))))
    return out


def prop_box(p):
    (x0, y0, z0), (x1, y1, z1) = p['render']
    s, yaw = p['scale'], math.radians(p['yaw'])
    c, sn = math.cos(yaw), math.sin(yaw)
    xs, ys = [], []
    for x in (x0, x1):
        for y in (y0, y1):
            xs.append(p['origin'][0] + s * (c * x - sn * y))
            ys.append(p['origin'][1] + s * (sn * x + c * y))
    oz = p['origin'][2]
    return [min(xs), min(ys), oz + s * z0], [max(xs), max(ys), oz + s * z1]


def is_box(s):
    lo, hi = s['lo'], s['hi']
    return len(s['verts']) == 8 and all(abs(v[i] - lo[i]) < 1e-6 or abs(v[i] - hi[i]) < 1e-6 for v in s['verts'] for i in range(3))


class Grid:
    def __init__(self, lo, hi, dxy, dz, half=16, height=HEAD, step=18):
        self.dxy, self.dz = dxy, dz
        self.X0, self.Y0, self.Z0 = lo
        self.nx = int(math.ceil((hi[0] - lo[0]) / dxy))
        self.ny = int(math.ceil((hi[1] - lo[1]) / dxy))
        self.nz = int(math.ceil((hi[2] - lo[2]) / dz))
        self.occ = np.zeros((self.nx, self.ny, self.nz), bool)
        self.set_hull(half, height, step)

    def set_hull(self, half, height=HEAD, step=18):
        self.hw = int(round(half / self.dxy))
        self.hh = int(math.ceil(height / self.dz))
        self.step = int(step // self.dz)

    def crange(self, a, b, o, d, n):
        return max(0, int(math.floor((a + 0.5 - o) / d))), min(n, int(math.ceil((b - 0.5 - o) / d)))

    def add_box(self, lo, hi):
        i0, i1 = self.crange(lo[0], hi[0], self.X0, self.dxy, self.nx)
        j0, j1 = self.crange(lo[1], hi[1], self.Y0, self.dxy, self.ny)
        k0 = max(0, int(math.floor((lo[2] + 0.25 - self.Z0) / self.dz)))
        k1 = min(self.nz, int(math.ceil((hi[2] - 0.25 - self.Z0) / self.dz)))
        if i1 > i0 and j1 > j0 and k1 > k0:
            self.occ[i0:i1, j0:j1, k0:k1] = True

    def add_convex(self, vs, faces, lo, hi):
        pl = planes(vs, faces)
        i0, i1 = self.crange(lo[0], hi[0], self.X0, self.dxy, self.nx)
        j0, j1 = self.crange(lo[1], hi[1], self.Y0, self.dxy, self.ny)
        for i in range(i0, i1):
            x = self.X0 + self.dxy * (i + 0.5)
            for j in range(j0, j1):
                y = self.Y0 + self.dxy * (j + 0.5)
                zl, zh = -1e9, 1e9
                for n, d in pl:
                    r = d - n[0] * x - n[1] * y
                    if abs(n[2]) < 1e-9:
                        if r < -1e-6:
                            zl, zh = 1, 0
                            break
                    elif n[2] > 0:
                        zh = min(zh, r / n[2])
                    else:
                        zl = max(zl, r / n[2])
                if zh - zl > 0.5:
                    k0 = max(0, int(math.floor((zl + 0.25 - self.Z0) / self.dz)))
                    k1 = min(self.nz, int(math.ceil((zh - 0.25 - self.Z0) / self.dz)))
                    if k1 > k0:
                        self.occ[i, j, k0:k1] = True

    def add_solid(self, s):
        if is_box(s):
            self.add_box(s['lo'], s['hi'])
        else:
            self.add_convex(s['verts'], s['faces'], s['lo'], s['hi'])

    def add_terrain(self, s, shell):
        """the terrain's shell (not convex): under every upward face, a layer shell thick."""
        vs = s['verts']
        for f in s['faces']:
            n = newell(vs, f)
            if n[2] <= 0.3 * math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2):
                continue
            for t in range(1, len(f) - 1):
                a, b, c = (np.array(vs[f[0]], float), np.array(vs[f[t]], float), np.array(vs[f[t + 1]], float))
                i0 = max(0, int(math.ceil((min(a[0], b[0], c[0]) - self.X0) / self.dxy - 0.5)))
                i1 = min(self.nx - 1, int(math.floor((max(a[0], b[0], c[0]) - self.X0) / self.dxy - 0.5)))
                j0 = max(0, int(math.ceil((min(a[1], b[1], c[1]) - self.Y0) / self.dxy - 0.5)))
                j1 = min(self.ny - 1, int(math.floor((max(a[1], b[1], c[1]) - self.Y0) / self.dxy - 0.5)))
                if i1 < i0 or j1 < j0:
                    continue
                I, J = np.meshgrid(np.arange(i0, i1 + 1), np.arange(j0, j1 + 1), indexing='ij')
                px, py = self.X0 + self.dxy * (I + 0.5), self.Y0 + self.dxy * (J + 0.5)
                det = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
                if abs(det) < 1e-9:
                    continue
                l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / det
                l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / det
                l3 = 1 - l1 - l2
                inside = (l1 >= -1e-6) & (l2 >= -1e-6) & (l3 >= -1e-6)
                z = l1 * a[2] + l2 * b[2] + l3 * c[2]
                for ii, jj, zz in zip(I[inside], J[inside], z[inside]):
                    k0 = max(0, int(math.floor((zz - shell + 0.25 - self.Z0) / self.dz)))
                    k1 = min(self.nz, int(math.ceil((zz - 0.25 - self.Z0) / self.dz)))
                    if k1 > k0:
                        self.occ[ii, jj, k0:k1] = True

    def finish(self):
        s = np.zeros((self.nx + 1, self.ny + 1, self.nz + 1), np.int32)
        s[1:, 1:, 1:] = self.occ.astype(np.int32).cumsum(0).cumsum(1).cumsum(2)
        self.S = s

    def boxsum(self, a0, a1, b0, b1, c0, c1):
        a0, a1 = max(0, a0), min(self.nx, a1)
        b0, b1 = max(0, b0), min(self.ny, b1)
        c0, c1 = max(0, c0), min(self.nz, c1)
        if a1 <= a0 or b1 <= b0 or c1 <= c0:
            return 0
        S = self.S
        return int(S[a1, b1, c1] - S[a0, b1, c1] - S[a1, b0, c1] - S[a1, b1, c0]
                   + S[a0, b0, c1] + S[a0, b1, c0] + S[a1, b0, c0] - S[a0, b0, c0])

    def clear(self, i, j, k):
        hw = self.hw
        if i - hw < 0 or j - hw < 0 or i + hw > self.nx or j + hw > self.ny or k < 1 or k + self.hh > self.nz:
            return False
        return self.boxsum(i - hw, i + hw, j - hw, j + hw, k, k + self.hh) == 0

    def support(self, i, j, k):
        hw = self.hw
        return self.boxsum(i - hw, i + hw, j - hw, j + hw, k - 1, k) > 0

    def land(self, i, j, k):
        for kk in range(k, max(0, k - 400), -1):
            if self.support(i, j, kk):
                return kk
            if not self.clear(i, j, kk - 1):
                return None
        return None

    def flood(self, seeds, kmin=None, kmax=None):
        seen = set(seeds)
        q = deque(seeds)
        while q:
            i, j, k = q.popleft()
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ni, nj = i + di, j + dj
                if self.clear(ni, nj, k):
                    nk = self.land(ni, nj, k)
                else:
                    nk = None
                    for up in range(1, self.step + 1):
                        if self.clear(ni, nj, k + up) and self.clear(i, j, k + up):
                            nk = self.land(ni, nj, k + up)
                            break
                if nk is None or (kmin is not None and (nk < kmin or nk > kmax)):
                    continue
                if (ni, nj, nk) not in seen:
                    seen.add((ni, nj, nk))
                    q.append((ni, nj, nk))
        return seen

    def cell(self, x, y, z):
        return (int(round((x - self.X0) / self.dxy)), int(round((y - self.Y0) / self.dxy)), int(round((z - self.Z0) / self.dz)))

    def state_near(self, x, y, z, rad=16, dzr=8):
        """a valid standing state close to (x, y, z), if any."""
        i0, j0, k0 = self.cell(x, y, z)
        r, rz = int(math.ceil(rad / self.dxy)), int(math.ceil(dzr / self.dz))
        best = None
        for i in range(i0 - r, i0 + r + 1):
            for j in range(j0 - r, j0 + r + 1):
                for k in range(k0 - rz, k0 + rz + 1):
                    if self.support(i, j, k) and self.clear(i, j, k):
                        d = (i - i0) ** 2 + (j - j0) ** 2 + (k - k0) ** 2
                        if best is None or d < best[0]:
                            best = (d, (i, j, k))
        return best[1] if best else None

    def reached_near(self, reached, x, y, z, rad=16, dzr=8):
        i0, j0, k0 = self.cell(x, y, z)
        r, rz = int(math.ceil(rad / self.dxy)), int(math.ceil(dzr / self.dz))
        return any((i, j, k) in reached for i in range(i0 - r, i0 + r + 1) for j in range(j0 - r, j0 + r + 1)
                   for k in range(k0 - rz, k0 + rz + 1))

    def headroom(self, x, y, z):
        """clear height above (x, y) from z up to the first solid."""
        i = int(math.floor((x - self.X0) / self.dxy))
        j = int(math.floor((y - self.Y0) / self.dxy))
        k = int(math.ceil((z - 0.25 - self.Z0) / self.dz))      # the first layer above the tread's own top layer
        if not (0 <= i < self.nx and 0 <= j < self.ny):
            return 1e9
        col = self.occ[i, j, max(0, k):]
        hit = np.nonzero(col)[0]
        return (hit[0] * self.dz + (self.Z0 + max(0, k) * self.dz) - z) if len(hit) else 1e9


def building_grid(lay, h, solids, props):
    r, fz = h['rect'], h['floor_z']
    top = max(s['hi'][2] for s in solids) + 8
    g = Grid((r[0] - 64, r[1] - 64, fz - 260), (r[2] + 64, r[3] + 64, top), 4.0, 2.0)
    for s in solids:
        g.add_solid(s)
    for p in props:
        lo, hi = prop_box(p)
        g.add_box(lo, hi)
    g.finish()
    return g


def main():
    lay = json.load(open(sys.argv[1]))
    fails = []
    W = lay['sewer_W']
    by_bld = {}
    for s in lay['solids']:
        by_bld.setdefault(s['tag'].split('/')[0], []).append(s)
    for h in lay['houses']:
        tag = h['name'].split()[0]
        solids = by_bld.get(tag, [])
        props = [p for p in lay['props'] if p['tag'] == tag]
        anchors = [a for a in lay['anchors'] if a['bld'] == tag]
        g = building_grid(lay, h, solids, props)
        line = []
        # 1. headroom over every step
        worst = 1e9
        nsteps = 0
        for s in solids:
            if not s['tag'].endswith('/step'):
                continue
            nsteps += 1
            lo, hi = s['lo'], s['hi']
            # sample only grid cells wholly inside the tread, so a neighbouring step sharing a cell is not counted
            i0 = int(math.ceil((lo[0] - g.X0) / g.dxy)); i1 = int(math.floor((hi[0] - g.X0) / g.dxy))
            j0 = int(math.ceil((lo[1] - g.Y0) / g.dxy)); j1 = int(math.floor((hi[1] - g.Y0) / g.dxy))
            for i in range(i0, i1):
                for j in range(j0, j1):
                    worst = min(worst, g.headroom(g.X0 + g.dxy * (i + 0.5), g.Y0 + g.dxy * (j + 0.5), hi[2]))
        if nsteps:
            ok = worst >= HEAD - 0.5
            line.append(f'headroom over {nsteps} steps: least {worst:.0f} {"OK" if ok else "TOO LOW"}')
            if not ok:
                fails.append(f'{tag}: headroom {worst:.0f} over a step')
        # 2. floors: from each anchor, every other anchor
        states = {}
        for a in anchors:
            st = g.state_near(a['x'], a['y'], a['z'])
            if st is None:
                fails.append(f"{tag}: nowhere to stand at {a['label']}")
            states[a['label']] = (a, st)
        groups = []
        done = set()
        for lab, (a, st) in states.items():
            if st is None or lab in done:
                continue
            reached = g.flood([st])
            got = {l2 for l2, (a2, st2) in states.items() if st2 is not None and g.reached_near(reached, a2['x'], a2['y'], a2['z'])}
            done |= got
            groups.append(got)
        if len(groups) == 1 and len(groups[0]) == len(anchors):
            line.append(f'floors: all {len(anchors)} doorways and stair ends reach each other on foot')
        else:
            line.append(f'floors: SPLIT into {len(groups)} groups: ' + ' | '.join(', '.join(sorted(gr)) for gr in groups))
            fails.append(f'{tag}: floors not all connected')
        # 3. lanes of 64 on each floor
        g.set_hull(32)
        levels = sorted({round(a['z']) for a in anchors})
        bad = []
        for zl in levels:
            here = [a for a in anchors if abs(a['z'] - zl) < 1]
            seeds = []
            for a in here:
                st = g.state_near(a['x'], a['y'], a['z'], rad=8, dzr=4)
                if st is None:
                    bad.append(f"{a['label']} (no room for a 64 lane)")
                else:
                    seeds.append((a, st))
            if not seeds:
                continue
            k0 = seeds[0][1][2]
            reached = g.flood([seeds[0][1]], kmin=k0 - 3, kmax=k0 + 3)
            for (a, st) in seeds[1:]:
                if st not in reached:
                    bad.append(f"{a['label']} (not reached by a 64 lane from {seeds[0][0]['label']})")
        g.set_hull(16)
        if bad:
            line.append('lanes of 64: ' + '; '.join(bad))
            fails.append(f'{tag}: lanes: ' + '; '.join(bad))
        else:
            line.append(f'lanes of 64: every doorway and stair end on all {len(levels)} floors')
        print(f"{h['name']}:")
        for l in line:
            print('    ' + l)

    # 4a. the sewer network, as one walk from the first junction, kept below the ground floors: every junction, every
    # cellar-side sewer door, and the foot of every stair up (the barn's, the shed's and each manhole's). The terrain's
    # shell is in the grid too, so a shell that dipped into a corridor would show as a blocked walk.
    shell = lay.get('shell', 24)
    terrain = [s for s in lay['solids'] if s['tag'] == 'terrain']
    houses = {h['name'].split()[0] for h in lay['houses']}
    sew = [s for s in lay['solids'] if s['tag'].startswith('sewer') or s['tag'].startswith('MH')
           or (s['tag'].split('/')[0] in houses and s['lo'][2] < 60 and s['hi'][2] > W - 60)]
    sprops = [p for p in lay['props'] if p['tag'] in houses and p['origin'][2] < 40]
    half = lay['half']
    g = Grid((-half - 64, -half - 64, W - 48), (half + 64, half + 64, 140), 8.0, 4.0)
    for s in sew:
        g.add_solid(s)
    for s in terrain:
        g.add_terrain(s, shell)
    for p in sprops:
        lo, hi = prop_box(p)
        g.add_box(lo, hi)
    g.finish()
    j0 = list(lay['junctions'].values())[0]
    seed = g.state_near(j0[0] + 60, j0[1] - 60, W, rad=24)
    ceiling = g.cell(0, 0, -60)[2]
    reached = g.flood([seed], kmin=0, kmax=ceiling) if seed else set()
    targets = [(f'junction {n}', c[0] + 60, c[1] - 60, W) for n, c in lay['junctions'].items()]
    for e in lay['exits']:
        if e['why'] == 'cellar':
            targets.append((f"{e['bld']} sewer door (cellar side)", e['x'] - e['dir'][0] * 40, e['y'] - e['dir'][1] * 40, e['z']))
        else:
            targets.append((f"{e['bld']} stairs' foot", e['x'] - e['dir'][0] * 24, e['y'] - e['dir'][1] * 24, e['z']))
    miss = [t[0] for t in targets if not g.reached_near(reached, t[1], t[2], t[3], rad=24, dzr=12)]
    print(f'sewer: {len(targets) - len(miss)} of {len(targets)} places reached on foot from junction '
          f"{list(lay['junctions'])[0]}" + (f"; NOT reached: {', '.join(miss)}" if miss else ''))
    if miss:
        fails.append('sewer: ' + ', '.join(miss))
    # 4b. each stair up out of the sewer, on a fine grid of its own: from its foot to its top on foot
    tops = {a['bld']: (a['x'], a['y'], a['z']) for a in lay['anchors'] if a['label'] == 'hatch: top'}
    for m in lay.get('manholes', []):
        tops[m['tag']] = (m['x'], m['y'], m['z'])
    for e in lay['exits']:
        if e['why'] != 'hatch':
            continue
        b = e['bld']
        own = [s for s in lay['solids'] if s['tag'].split('/')[0] == b]
        top = tops.get(b)
        if not own or top is None:
            fails.append(f'{b}: stairs up not found')
            continue
        lo = [min(s['lo'][i] for s in own) - 48 for i in range(2)] + [W - 48]
        hi = [max(s['hi'][i] for s in own) + 48 for i in range(2)] + [max(top[2] + 120, max(s['hi'][2] for s in own) + 8)]
        gl = Grid(lo, hi, 4.0, 2.0)
        for s in lay['solids']:
            if s['tag'] == 'skybox' or not all(s['lo'][i] < hi[i] and s['hi'][i] > lo[i] for i in range(3)):
                continue
            if s['tag'] == 'terrain':
                gl.add_terrain(s, shell)
            else:
                gl.add_solid(s)
        for p in lay['props']:
            if p['tag'] in VEG:
                continue
            plo, phi = prop_box(p)
            if all(plo[i] < hi[i] and phi[i] > lo[i] for i in range(3)):
                gl.add_box(plo, phi)
        gl.finish()
        st = gl.state_near(e['x'] - e['dir'][0] * 24, e['y'] - e['dir'][1] * 24, e['z'], rad=16, dzr=6)
        ok = st is not None and gl.reached_near(gl.flood([st]), top[0], top[1], top[2], rad=16, dzr=6)
        print(f"{b}: stairs up from the sewer {'reach' if ok else 'do NOT reach'} the top on foot")
        if not ok:
            fails.append(f'{b}: stairs up do not reach the top')
    print('PASS' if not fails else 'FAIL: ' + ' | '.join(fails))
    return 0 if not fails else 1


if __name__ == '__main__':
    sys.exit(main())
