#!/usr/bin/env python3
"""gg_vinceville - generates an Arms Race map for CS2: a village of small houses scattered over a grassy field, with a barn,
trees, two shallow ponds, haystacks and low stone walls to fight around, and two houses with an upper floor.

Writes the Hammer map source into the CS2 Workshop Tools' content folder:

  content/csgo_addons/gg_vinceville/maps/gg_vinceville.vmap    keyvalues2 DMX, the same structure Hammer saves
  game/csgo_addons/gg_vinceville/addoninfo.txt

and layout.json beside --layout: every mesh of the world (vertices, faces, materials), every prop, the ponds, the buildings'
floors with their holes, stairs and doors, and the spawns with the way they face - for checks.py, walkcheck.py and preview.py.

  python generate.py [--cs2 <path to 'Counter-Strike Global Offensive'>] [--layout <file>] [--addon <folder>]

Solid by construction, and checks.py proves it on what is written:
- The ground is one height field, cut into chunks that share every edge vertex. Nothing is ever cut out of it: no holes.
- Each building is one closed mesh: the union of axis-aligned boxes on the building's own grid, its outer surface only, so
  walls, floors, stairs and openings meet with no seam, no inner face and no T-junction.
- Whatever stands on the ground reaches into it, and a roof reaches into its walls: no two drawn faces lie in one plane over
  each other.
- Textures are world-aligned: a face's texture axes follow its main axis, at the material's own scale, so neighbouring faces
  line up.
Only stock CS2 materials and models are used. Units are Hammer units (1 unit = 1 inch).
"""
import argparse
import bisect
import json
import math
import os
import random
import sys

NAME = 'gg_vinceville'
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from vmapkit import El, cs2_dir, data_array, entity, fnum, node_ids, stream, transform_tail, use_name, write_el  # noqa: E402 (maps/vmapkit.py: what the generators share)

use_name(NAME)

# ---------------------------------------------------------------- dimensions
GRID = 64            # terrain grid
CHUNK = 8            # terrain cells per chunk side
HALF = 1280          # half-size of the play area: the player clip stands here (2560 x 2560)
OUTER = 1792         # half-size of the terrain, and the inside of the skybox shell
T_BOTTOM = -384      # underside of the terrain
SKY_TOP = 1152       # inside top of the skybox shell
T = 8                # wall thickness
PL = 4               # how far a building's base stands out from its walls
FZ = 8               # a ground floor's top above its pad; the base reaches FOUND below the pad
FOUND = 24
SH = 144             # storey height, floor top to floor top: 12 steps of RISE
SLAB = 12            # upper floor thickness
RISE, RUN = 12, 16   # a step: 12 up (a player steps up 18), 16 deep
PITCH = 1.07         # roof rise per unit of run: 47 degrees, too steep to stand on
EAVE, GABLE = 24, 12 # roof overhang at the eaves and at the gable ends
DOOR_W, DOOR_H = 72, 104
WIN_SILL, WIN_HEAD = 34, 94   # 60 tall: a crouching player (54) gets through
PAD = 128            # flat ground round a building's base, at its level: two grid cells, so no terrain triangle reaches under it
BLEND = 160          # and the distance over which the field rises or falls to it
HULL_W, HULL_H = 32, 72   # a standing player's box: 32 x 32, 72 tall
HEADROOM = HULL_H + 8     # what a standing player needs over a tread, with 8 to spare

# ---------------------------------------------------------------- materials (all stock CS2)
M = dict(
    GRASS='materials/de_overpass/nature/wild_grass_1.vmat',
    GRASS2='materials/de_overpass/nature/wild_grass_2.vmat',
    MUD='materials/de_ancient/hr_ancient_ground_mud_01_base.vmat',
    DIRT='materials/de_ancient/hr_ancient_ground_dirt_01_base.vmat',
    BRICK='materials/shared_base/brick/brick_red_worn_002.vmat',
    BRICK2='materials/de_train/hr_train_s2/brick/hrts2_brick_01.vmat',
    STUCCO='materials/de_train/hr_train_s2/plaster/hrts2_stucco_01.vmat',
    GREEN='materials/de_train/hr_train_s2/plaster/hrts2_plaster_02_paint_green.vmat',
    RED='materials/de_train/hr_train_s2/plaster/hrts2_plaster_02_paint_red.vmat',
    PLANKS='materials/de_cache/wood/planks_wood_abandoned_ref_01a.vmat',
    STONE='materials/de_inferno/stone/inferno_stone_wall_01.vmat',
    STONE2='materials/de_inferno/stone/inferno_stone_wall_02.vmat',
    COBBLE='materials/de_overpass/brick/cobblestone_wall_1.vmat',
    WALLPAPER1='materials/de_inferno/wallpaper/inferno_wallpaper_01.vmat',
    WALLPAPER2='materials/de_inferno/wallpaper/inferno_wallpaper_02.vmat',
    PLASTER='materials/de_train/hr_train_s2/plaster/hrts2_plaster_01.vmat',
    FLOOR='materials/de_inferno/wood/inferno_wood_floor_01.vmat',
    FLOOR2='materials/de_train/hr_train_s2/wood/hrts2_wood_floor_boards_01.vmat',
    CEILWOOD='materials/de_ancient/hr_ancient_ceiling_wood_01_base.vmat',
    TILE='materials/cs_italy/tileroof01.vmat',
    METALROOF='materials/de_train/hr_train_s2/roof/hrts2_roof_metal_01.vmat',
    THATCH='materials/de_ancient/hr_ancient_roof_thatch_01_base.vmat',
    CORR='materials/metal/hr_metal/hr_metal_corrugated_001_grey.vmat',
    TRIM='materials/hr_massive/wood_planks_1.vmat',
    STRAW='materials/de_inferno/ground/inferno_straw_filling_01.vmat',
    POOLWATER='materials/liquids/overpasswater.vmat',
    NODRAW='materials/tools/toolsnodraw.vmat',
    SKYBOX='materials/tools/toolsskybox.vmat',
    CLIP='materials/tools/toolsplayerclip.vmat',
    LMRES='materials/tools/toolslightmapres.vmat',
    POST='materials/tools_postprocess_volume.vmat',
)
SKY = 'materials/skybox/sky_de_overpass_01.vmat'
# world units per texture repeat: Hammer's default 0.25 units per texel times the colour texture's size
TILE = {M['GRASS']: 512, M['GRASS2']: 512, M['MUD']: 512, M['DIRT']: 512, M['BRICK']: 512, M['BRICK2']: 512,
        M['STUCCO']: 512, M['GREEN']: 512, M['RED']: 512, M['PLANKS']: 512, M['STONE']: 512, M['STONE2']: 512,
        M['COBBLE']: 384, M['WALLPAPER1']: 512, M['WALLPAPER2']: 512, M['PLASTER']: 512, M['FLOOR']: 512, M['FLOOR2']: 512,
        M['CEILWOOD']: 512, M['TILE']: 128, M['METALROOF']: 512, M['THATCH']: 512, M['CORR']: 256,
        M['TRIM']: 256, M['STRAW']: 128, M['POOLWATER']: 512}
TOOLS = {M['NODRAW'], M['SKYBOX'], M['CLIP'], M['LMRES'], M['POST']}

# ---------------------------------------------------------------- stock models: render bounds and physics bounds
# (read from the game's pak01 with Source2Viewer-CLI 20.1 on 2026-10-31; None = the model has no collision)
MODELS = {
    'poplar': ('models/foliage/trees/poplar/tree_poplar_mature_01.vmdl', ((-124, -134, 0), (117, 113, 748)), ((-17, -13, 0), (17, 21, 384))),
    'poplar_s': ('models/foliage/trees/poplar/tree_poplar_adult_01.vmdl', ((-44, -56, 0), (40, 51, 359)), ((-4, -4, 0), (4, 4, 189))),
    'maple1': ('models/foliage/trees/maple/maple_01/maple_tree_01.vmdl', ((-326, -306, 0), (325, 284, 467)), ((-28, -28, 0), (28, 28, 240))),
    'maple2': ('models/foliage/trees/maple/maple_01/maple_tree_02.vmdl', ((-370, -286, 0), (304, 255, 493)), ((-28, -28, 0), (28, 28, 240))),
    'oak': ('models/cs_italy/foliage/tree/generic_tree_large_1.vmdl', ((-172, -197, 0), (168, 185, 487)), ((-79, -54, 0), (40, 39, 272))),
    'sumac': ('models/foliage/trees/sumac_poison_tree/sumac_poison_tree_01.vmdl', ((-69, -105, 0), (85, 79, 288)), None),
    'cypress': ('models/foliage/trees/cypress/foliage_tree_cypress_01.vmdl', ((-167, -204, 0), (177, 155, 861)), None),
    'card1': ('models/foliage/background/background_foliage_tree_01.vmdl', ((-61, -2, 0), (61, 2, 388)), None),
    'card2': ('models/foliage/background/background_foliage_tree_02.vmdl', ((-96, -2, 0), (91, 2, 528)), None),
    'card3': ('models/foliage/background/background_foliage_tree_03.vmdl', ((-88, -2, 0), (83, 2, 528)), None),
    'dogwood': ('models/foliage/bushes/dogwood/dogwood_adult_01.vmdl', ((-81, -55, 0), (92, 62, 100)), None),
    'dogwood2': ('models/foliage/bushes/dogwood/shrub_dogwood_mature_01.vmdl', ((-101, -63, 0), (113, 70, 121)), None),
    'huckle': ('models/de_overpass/foliage/eg_huckleberry/eg_huckleberry_bush_01.vmdl', ((-19, -19, 0), (20, 19, 60)), None),
    'hedge': ('models/de_overpass/foliage/hedge/hedge_128a.vmdl', ((-78, -28, 0), (79, 27, 74)), ((-65, -18, 0), (63, 19, 66))),
    'tallgrass': ('models/de_overpass/foliage/weed/grass_detail_tall_001.vmdl', ((-16, -17, 0), (15, 12, 42)), None),
    'flower': ('models/foliage/flowers/buttercup/flower_buttercup.vmdl', ((-10, -10, 0), (11, 12, 34)), None),
    'sedan1': ('models/props/de_nuke/hr_nuke/nuke_cars/nuke_sedan01.vmdl', ((-117, -51, 0), (118, 43, 68)), ((-112, -44, 0), (116, 36, 67))),
    'sedan2': ('models/props/de_nuke/hr_nuke/nuke_cars/nuke_sedan02.vmdl', ((-117, -49, 0), (111, 49, 71)), ((-116, -43, 0), (110, 43, 70))),
    'hatch': ('models/props_vehicles/cara_82hatchback.vmdl', ((-84, -39, 0), (87, 39, 60)), ((-82, -39, 0), (84, 39, 60))),
    'van': ('models/props_vehicles/van.vmdl', ((-49, -116, 0), (49, 116, 90)), ((-44, -117, 0), (44, 116, 88))),
    'barrel': ('models/cs_italy/props/barrel/italy_barrel_wood_1.vmdl', ((-17, -17, 0), (17, 17, 48)), ((-17, -17, 0), (17, 17, 48))),
    'crate_big': ('models/de_inferno/wood_crate_01/inferno_crate_wood_01.vmdl', ((-41, -41, 0), (41, 43, 61)), ((-41, -41, 0), (41, 43, 61))),
    'wheelbarrow': ('models/de_overpass/junk/wheelbarrow01a/wheelbarrow01a.vmdl', ((-33, -15, 0), (35, 15, 28)), ((-34, -16, 0), (35, 16, 29))),
}
VEG = ('tree', 'bush', 'hedge', 'grass', 'card')        # prop tags with no collision at all (solid 0): nobody gets stuck in foliage
FADE = {'grass': (1400, 2000), 'bush': (2600, 3400)}    # small foliage fades out with distance


# ---------------------------------------------------------------- vector helpers
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    l = math.sqrt(dot(a, a))
    return (a[0] / l, a[1] / l, a[2] / l) if l > 0 else (0.0, 0.0, 1.0)


def newell(vs, f):
    n = [0.0, 0.0, 0.0]
    for k in range(len(f)):
        a, b = vs[f[k]], vs[f[(k + 1) % len(f)]]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    return tuple(n)


def uv_axes(n):
    """World-aligned texture axes for a face of normal n: its main axis decides."""
    ax, ay, az = abs(n[0]), abs(n[1]), abs(n[2])
    if az >= ax and az >= ay:
        return (1, 0, 0), (0, -1, 0)
    if ax >= ay:
        return (0, 1, 0), (0, 0, -1)
    return (1, 0, 0), (0, 0, -1)


def smooth(t):
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


def rect_dist(x, y, r):
    """distance from (x, y) to the rectangle r = (x0, y0, x1, y1); 0 inside."""
    dx = max(r[0] - x, 0, x - r[2])
    dy = max(r[1] - y, 0, y - r[3])
    return math.hypot(dx, dy)


# ---------------------------------------------------------------- polygon mesh (half-edge CDmePolygonMesh)
def polygon_mesh(verts, faces, face_mats, origin, lm_bias=0, wind='given', vn=None, smooth_faces=None):
    """verts: local positions; faces: vertex index loops, counter-clockwise seen from outside ('given'), or turned to point
    away from the mesh centre ('centroid', for convex solids); face_mats: material per face. vn/smooth_faces: per-vertex
    normals for the faces flagged smooth. Every edge must be used once each way: a closed, manifold mesh."""
    c = tuple(sum(v[i] for v in verts) / len(verts) for i in range(3))
    of = []
    for f in faces:
        n = newell(verts, f)
        if wind == 'centroid':
            fc = tuple(sum(verts[i][k] for i in f) / len(f) for k in range(3))
            if dot(n, sub(fc, c)) < 0:
                f = list(reversed(f))
                n = (-n[0], -n[1], -n[2])
        of.append((list(f), norm(n)))
    materials = []
    for m in face_mats:
        if m not in materials:
            materials.append(m)
    dest, face, pair, face_hes = [], [], {}, []
    for fi, (f, _) in enumerate(of):
        hes = []
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            h = pair.get((a, b))
            if h is None:
                h = len(dest)
                dest += [b, a]
                face += [-1, -1]
                pair[(a, b)] = h
                pair[(b, a)] = h + 1
            assert face[h] == -1, 'non-manifold face loop'
            face[h] = fi
            hes.append(h)
        face_hes.append(hes)
    nh = len(dest)
    assert all(x >= 0 for x in face), 'open mesh'
    nxt = [0] * nh
    for hes in face_hes:
        for k, h in enumerate(hes):
            nxt[h] = hes[(k + 1) % len(hes)]
    vedge = [-1] * len(verts)
    for (a, b), h in pair.items():
        if vedge[a] < 0:
            vedge[a] = h
    tex, nrm, tan = [None] * nh, [None] * nh, [None] * nh
    fscale, fu, fv, fmat = [], [], [], []
    for fi, (f, n) in enumerate(of):
        u, v = uv_axes(n)
        tile = TILE.get(face_mats[fi], 128)
        w = 1 if dot(cross(n, u), v) > 0 else -1
        sm = vn is not None and smooth_faces is not None and smooth_faces[fi]
        for h in face_hes[fi]:
            p = verts[dest[h]]
            wp = (p[0] + origin[0], p[1] + origin[1], p[2] + origin[2])
            tex[h] = (dot(wp, u) / tile, dot(wp, v) / tile)
            nrm[h] = vn[dest[h]] if sm else n
            tan[h] = (u[0], u[1], u[2], w)
        fscale.append((0.25, 0.25))
        fu.append((u[0], u[1], u[2], 0))
        fv.append((v[0], v[1], v[2], 0))
        fmat.append(materials.index(face_mats[fi]))
    nf = len(of)
    lmb = lm_bias if isinstance(lm_bias, list) else [lm_bias] * nf
    return El('CDmePolygonMesh', [
        ('name', 'string', 'meshData'),
        ('vertexEdgeIndices', 'int_array', vedge),
        ('vertexDataIndices', 'int_array', list(range(len(verts)))),
        ('edgeVertexIndices', 'int_array', dest),
        ('edgeOppositeIndices', 'int_array', [h ^ 1 for h in range(nh)]),
        ('edgeNextIndices', 'int_array', nxt),
        ('edgeFaceIndices', 'int_array', face),
        ('edgeDataIndices', 'int_array', [h // 2 for h in range(nh)]),
        ('edgeVertexDataIndices', 'int_array', list(range(nh))),
        ('faceEdgeIndices', 'int_array', [hes[0] for hes in face_hes]),
        ('faceDataIndices', 'int_array', list(range(nf))),
        ('materials', 'string_array', materials),
        ('vertexData', 'element', data_array(len(verts), [stream('position', 'vector3_array', verts, 3)])),
        ('faceVertexData', 'element', data_array(nh, [stream('texcoord', 'vector2_array', tex, 1),
                                                      stream('normal', 'vector3_array', nrm, 1),
                                                      stream('tangent', 'vector4_array', tan, 1)])),
        ('edgeData', 'element', data_array(nh // 2, [stream('flags', 'int_array', [0] * (nh // 2), 3)])),
        ('faceData', 'element', data_array(nf, [stream('textureScale', 'vector2_array', fscale, 0),
                                                stream('textureAxisU', 'vector4_array', fu, 0),
                                                stream('textureAxisV', 'vector4_array', fv, 0),
                                                stream('materialindex', 'int_array', fmat, 8),
                                                stream('flags', 'int_array', [0] * nf, 3),
                                                stream('lightmapScaleBias', 'int_array', lmb, 1)])),
        ('subdivisionData', 'element', El('CDmePolygonMeshSubdivisionData', [
            ('subdivisionLevels', 'int_array', [0] * nh), ('streams', 'element_array', [])])),
    ])


def map_mesh(s):
    """A CMapMesh from a layout solid {'verts' (world), 'faces', 'mats', ...}."""
    vw = s['verts']
    lo = [min(v[i] for v in vw) for i in range(3)]
    hi = [max(v[i] for v in vw) for i in range(3)]
    o = tuple((lo[i] + hi[i]) / 2 for i in range(3))
    local = [(v[0] - o[0], v[1] - o[1], v[2] - o[2]) for v in vw]
    bake = s.get('bake', True) and not all(m in TOOLS for m in s['mats'])
    nid, ref = node_ids()
    return El('CMapMesh', [
        ('nodeID', 'int', nid), ('referenceID', 'uint64', ref), ('children', 'element_array', []),
        ('variableTargetKeys', 'string_array', []), ('variableNames', 'string_array', []),
        ('cubeMapName', 'string', ''), ('lightGroup', 'string', ''), ('visexclude', 'bool', False),
        ('disablemerging', 'bool', False), ('renderwithdynamic', 'bool', False), ('disableHeightDisplacement', 'bool', False),
        ('fademindist', 'float', -1.0), ('fademaxdist', 'float', 0.0), ('bakelighting', 'bool', bake),
        ('precomputelightprobes', 'bool', True), ('renderToCubemaps', 'bool', True), ('emissiveLightingEnabled', 'bool', True),
        ('emissiveLightingBoost', 'float', 1.0), ('disableShadows', 'int', 0), ('lightingDummy', 'bool', False),
        ('smoothingAngle', 'float', 40.0), ('tintColor', 'color', (255, 255, 255, 255)), ('renderAmt', 'int', 255),
        ('physicsType', 'string', 'default'), ('physicsGroup', 'string', ''), ('physicsInteractsAs', 'string', ''),
        ('physicsInteractsWith', 'string', ''), ('physicsInteractsExclude', 'string', ''),
        ('meshData', 'element', polygon_mesh(local, s['faces'], s['mats'], o, s.get('lm', 0), s.get('wind', 'given'),
                                              s.get('vn'), s.get('smooth'))),
        ('physicsSimplificationOverride', 'bool', False), ('physicsSimplificationError', 'float', 0.0),
    ] + transform_tail(o))


# ---------------------------------------------------------------- simple convex solids
BOX_FACES = [[4, 5, 7, 6], [0, 2, 3, 1], [1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4]]   # +z -z +x -x +y -y


def box_verts(lo, hi):
    return [(hi[0] if i & 1 else lo[0], hi[1] if i & 2 else lo[1], hi[2] if i & 4 else lo[2]) for i in range(8)]


def prism(poly, z0, z1):
    """A vertical prism over a convex polygon (counter-clockwise from above): verts, faces (top, bottom, sides)."""
    n = len(poly)
    verts = [(x, y, z0) for x, y in poly] + [(x, y, z1) for x, y in poly]
    faces = [list(range(n, 2 * n)), list(range(n - 1, -1, -1))]
    for k in range(n):
        a, b = k, (k + 1) % n
        faces.append([a, b, n + b, n + a])
    return verts, faces


# ---------------------------------------------------------------- the ground
PONDS = [(380, 270, 200, 30), (-180, -820, 180, 28)]     # (x, y, radius, depth): shallow bowls with water in them
PATHS = [[(-1240, -150), (-560, -170), (0, -120), (600, -260), (1240, -200)],
         [(-300, 1240), (-300, 400), (-200, -140), (-150, -1240)],
         [(720, 1240), (720, 1040)], [(560, -1240), (560, -980)]]


def field(x, y):
    """The ground before the pads and the ponds: a gently rolling field, rising into hills outside the play area."""
    h = 9.0 * math.sin(x / 317.0 + 0.6) * math.cos(y / 283.0 - 0.3) + 5.0 * math.sin((x - y) / 199.0 + 1.1)
    e = max(abs(x), abs(y)) - (HALF - 96)
    if e > 0:
        h += 150.0 * (e / (OUTER - HALF + 96)) ** 2
    return h


PADS = []     # (rect, level): flat ground at a building's level


def ground(x, y):
    h = field(x, y)
    for (r, level) in PADS:
        if rect_dist(x, y, r) <= 0:
            return float(level)
    for (r, level) in PADS:
        d = rect_dist(x, y, r)
        if d < BLEND:
            h += (level - h) * smooth(1 - d / BLEND)
    for (cx, cy, R, depth) in PONDS:
        r = math.hypot(x - cx, y - cy)
        if r < R:
            h -= depth * smooth(1 - r / R)
    return h


def seg_dist(x, y, a, b):
    ax, ay = b[0] - a[0], b[1] - a[1]
    t = max(0.0, min(1.0, ((x - a[0]) * ax + (y - a[1]) * ay) / (ax * ax + ay * ay)))
    return math.hypot(x - a[0] - t * ax, y - a[1] - t * ay)


SUB = 4          # round a pond a cell is SUB x SUB smaller ones: 16 units
MUD = 0.92       # the mud reaches this much of a pond's radius
CUT_TOL = 0.75   # a vertex within this of the mud's circle counts as on it: the cut passes through it, no sliver beside it


def rect_minus(p, r):
    """The rectangle of cells p = (i0, j0, i1, j1) less r, as rectangles."""
    if r[0] >= p[2] or p[0] >= r[2] or r[1] >= p[3] or p[1] >= r[3]:
        return [p]
    out = []
    if p[0] < r[0]:
        out.append((p[0], p[1], r[0], p[3]))
    if r[2] < p[2]:
        out.append((r[2], p[1], p[2], p[3]))
    x0, x1 = max(p[0], r[0]), min(p[2], r[2])
    if p[1] < r[1]:
        out.append((x0, p[1], x1, r[1]))
    if r[3] < p[3]:
        out.append((x0, r[3], x1, p[3]))
    return out


class Terrain:
    """The ground: a height field over the whole terrain, its triangles, and the closed meshes it is built of.

    It is a grid of GRID cells, each two triangles, and finer round the ponds, so that a pond's bowl, its waterline and the
    edge of its mud come out round: there every cell within a pond's radius and 16 is cut into SUB x SUB smaller ones, the
    ring of cells round those fans its triangles out to the finer vertices on its inner edge, and the triangles the mud's
    circle crosses are cut along it. Vertices are named on the fine lattice, (I, J) = SUB times a grid corner's, and a cut's
    by the edge it cuts, so two triangles that share an edge share its vertices and every mesh is closed. Each pond's
    rectangle of cells, ring and all, is one mesh; the rest of the ground is CHUNK x CHUNK squares of cells less those."""

    def __init__(self):
        self.n = int(2 * OUTER / GRID)
        self.h = [[ground(-OUTER + i * GRID, -OUTER + j * GRID) for j in range(self.n + 1)] for i in range(self.n + 1)]
        self.hf = {}                 # (I, J) -> the height of a fine lattice point
        self.cutv = {}               # ('c', a, b) -> where the mud's circle cuts the edge a-b: (x, y, z, normal)
        self.tris = {}               # (i, j) -> the cell's triangles
        self.fine = set()            # the cells cut SUB x SUB
        self.pond_rects = []         # each pond's rectangle of cells (i0, j0, i1, j1): its fine cells and the ring round them
        for (cx, cy, R, D) in PONDS:
            i0, i1 = self.cell(cx - R - 16), self.cell(cx + R + 16) + 1
            j0, j1 = self.cell(cy - R - 16), self.cell(cy + R + 16) + 1
            self.fine |= {(i, j) for i in range(i0, i1) for j in range(j0, j1)}
            self.pond_rects.append((i0 - 1, j0 - 1, i1 + 1, j1 + 1))

    def cell(self, x):
        return min(self.n - 1, max(0, int((x + OUTER) // GRID)))

    def H(self, I, J):
        """The height of the fine lattice point (I, J)."""
        if I % SUB == 0 and J % SUB == 0:
            return self.h[I // SUB][J // SUB]
        if (I, J) not in self.hf:
            self.hf[(I, J)] = ground(-OUTER + I * GRID / SUB, -OUTER + J * GRID / SUB)
        return self.hf[(I, J)]

    def point(self, k):
        """A vertex's (x, y, z)."""
        if k[0] == 'c':
            return self.cutv[k][:3]
        return (-OUTER + k[0] * GRID / SUB, -OUTER + k[1] * GRID / SUB, self.H(*k))

    def normal(self, k):
        """A top vertex's normal, for smooth shading: from the slope across the grid round a grid corner, across the fine
        lattice round a finer vertex, and between its edge's ends for a cut."""
        if k[0] == 'c':
            return self.cutv[k][3]
        I, J = k
        n = self.n
        if I % SUB == 0 and J % SUB == 0:
            i, j = I // SUB, J // SUB
            hx = (self.h[min(n, i + 1)][j] - self.h[max(0, i - 1)][j]) / (GRID * (min(n, i + 1) - max(0, i - 1)))
            hy = (self.h[i][min(n, j + 1)] - self.h[i][max(0, j - 1)]) / (GRID * (min(n, j + 1) - max(0, j - 1)))
        else:
            hx = (self.H(I + 1, J) - self.H(I - 1, J)) / (2 * GRID / SUB)
            hy = (self.H(I, J + 1) - self.H(I, J - 1)) / (2 * GRID / SUB)
        return norm((-hx, -hy, 1.0))

    def cell_tris(self, i, j):
        """The cell's triangles, as fine lattice points, counter-clockwise from above: two of them, SUB x SUB times two in a
        fine cell, or a fan from a corner out to the finer vertices on an edge it shares with a fine cell."""
        if (i, j) in self.tris:
            return self.tris[(i, j)]
        S = SUB
        I0, J0, I1, J1 = i * S, j * S, (i + 1) * S, (j + 1) * S
        if (i, j) in self.fine:
            out = []
            for I in range(I0, I1):
                for J in range(J0, J1):
                    a, b, c, d = (I, J), (I + 1, J), (I + 1, J + 1), (I, J + 1)
                    out += [(a, b, c), (a, c, d)] if (I + J) % 2 == 0 else [(a, b, d), (b, c, d)]
        else:
            south = [(I, J0) for I in range(I0 + 1, I1)] if (i, j - 1) in self.fine else []
            east = [(I1, J) for J in range(J0 + 1, J1)] if (i + 1, j) in self.fine else []
            north = [(I, J1) for I in range(I1 - 1, I0, -1)] if (i, j + 1) in self.fine else []
            west = [(I0, J) for J in range(J1 - 1, J0, -1)] if (i - 1, j) in self.fine else []
            a, b, c, d = (I0, J0), (I1, J0), (I1, J1), (I0, J1)
            if not (south or east or north or west):
                out = [(a, b, c), (a, c, d)] if (i + j) % 2 == 0 else [(a, b, d), (b, c, d)]
            else:
                poly = [a] + south + [b] + east + [c] + north + [d] + west
                free = {a: not (south or west), b: not (south or east), c: not (north or east), d: not (north or west)}
                k = poly.index(next(q for q in (a, b, c, d) if free[q]))
                poly = poly[k:] + poly[:k]
                out = [(poly[0], poly[m], poly[m + 1]) for m in range(1, len(poly) - 1)]
        self.tris[(i, j)] = out
        return out

    def z(self, x, y):
        """The height of the triangulated surface at (x, y)."""
        fx, fy = (x + OUTER) / GRID, (y + OUTER) / GRID
        i, j = min(self.n - 1, max(0, int(fx))), min(self.n - 1, max(0, int(fy)))
        if (i, j) in self.fine:
            gx, gy = fx * SUB, fy * SUB
            I, J = min((i + 1) * SUB - 1, max(i * SUB, int(gx))), min((j + 1) * SUB - 1, max(j * SUB, int(gy)))
            u, v = gx - I, gy - J
            a, b, c, d = self.H(I, J), self.H(I + 1, J), self.H(I + 1, J + 1), self.H(I, J + 1)
            split_ac = (I + J) % 2 == 0
        elif any(q in self.fine for q in ((i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1))):
            best = None
            for t in self.cell_tris(i, j):
                (x0, y0, z0), (x1, y1, z1), (x2, y2, z2) = (self.point(k) for k in t)
                den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
                w0 = ((y1 - y2) * (x - x2) + (x2 - x1) * (y - y2)) / den
                w1 = ((y2 - y0) * (x - x2) + (x0 - x2) * (y - y2)) / den
                w2 = 1 - w0 - w1
                if best is None or min(w0, w1, w2) > best[0]:
                    best = (min(w0, w1, w2), w0 * z0 + w1 * z1 + w2 * z2)
            return best[1]
        else:
            u, v = fx - i, fy - j
            a, b, c, d = self.h[i][j], self.h[i + 1][j], self.h[i + 1][j + 1], self.h[i][j + 1]
            split_ac = (i + j) % 2 == 0
        if split_ac:               # triangles (a,b,c) and (a,c,d)
            return a + u * (b - a) + v * (c - b) if u >= v else a + u * (c - d) + v * (d - a)
        return a + u * (b - a) + v * (d - a) if u + v <= 1 else c + (1 - u) * (d - c) + (1 - v) * (b - c)

    def zrange(self, x, y, r=16):
        zs = [self.z(x + dx, y + dy) for dx in (-r, 0, r) for dy in (-r, 0, r)]
        return min(zs), max(zs)

    def material(self, x, y, mud=True):
        if mud:
            for (cx, cy, R, D) in PONDS:
                if math.hypot(x - cx, y - cy) < MUD * R:
                    return M['MUD']
        if max(abs(x), abs(y)) > HALF + 48:
            return M['GRASS2']
        for path in PATHS:
            for a, b in zip(path, path[1:]):
                if seg_dist(x, y, a, b) < 34:
                    return M['DIRT']
        return M['GRASS']

    def cut(self, a, b, pond):
        """The vertex where the mud's circle round pond crosses the edge a-b."""
        key = ('c',) + tuple(sorted((a, b)))
        if key not in self.cutv:
            (cx, cy, R, D) = pond
            pa, pb = self.point(a), self.point(b)
            dx, dy, ex, ey = pa[0] - cx, pa[1] - cy, pb[0] - pa[0], pb[1] - pa[1]
            A, B, C = ex * ex + ey * ey, 2 * (dx * ex + dy * ey), dx * dx + dy * dy - (MUD * R) ** 2
            root = math.sqrt(B * B - 4 * A * C)
            t = next(t for t in ((-B - root) / (2 * A), (-B + root) / (2 * A)) if 0 < t < 1)
            na, nb = self.normal(a), self.normal(b)
            nm = norm(tuple(na[k] + t * (nb[k] - na[k]) for k in range(3)))
            self.cutv[key] = (pa[0] + t * ex, pa[1] + t * ey, pa[2] + t * (pb[2] - pa[2]), nm)
        return key

    def pieces(self, tri):
        """A triangle's faces and their materials: in two along the mud's circle where that crosses it, the mud inside."""
        pts = [self.point(k) for k in tri]
        for pond in PONDS:
            (cx, cy, R, D) = pond
            sides = []
            for p in pts:
                d = math.hypot(p[0] - cx, p[1] - cy) - MUD * R
                sides.append(0 if abs(d) <= CUT_TOL else (1 if d > 0 else -1))
            if -1 in sides and 1 in sides:
                walk = []
                for e in range(3):
                    walk.append((tri[e], sides[e]))
                    if sides[e] * sides[(e + 1) % 3] < 0:
                        walk.append((self.cut(tri[e], tri[(e + 1) % 3], pond), 0))
                a, b = [k for k, (_, s) in enumerate(walk) if s == 0]
                out = []
                for part in (walk[a:b + 1], walk[b:] + walk[:a + 1]):
                    poly = [k for k, _ in part]
                    if any(s < 0 for _, s in part):
                        out.append((poly, M['MUD']))
                    else:
                        q = [self.point(k) for k in poly]
                        out.append((poly, self.material(sum(p[0] for p in q) / len(q), sum(p[1] for p in q) / len(q), mud=False)))
                return out
            if -1 in sides:
                return [(list(tri), M['MUD'])]
            if 1 not in sides and math.hypot(sum(p[0] for p in pts) / 3 - cx, sum(p[1] for p in pts) / 3 - cy) < MUD * R:
                return [(list(tri), M['MUD'])]
        x, y = sum(p[0] for p in pts) / 3, sum(p[1] for p in pts) / 3
        return [(list(tri), self.material(x, y, mud=False))]

    def tiles(self):
        """The ground's meshes, as rectangles of cells: each pond's, then the CHUNK x CHUNK squares less those."""
        out = list(self.pond_rects)
        for i0 in range(0, self.n, CHUNK):
            for j0 in range(0, self.n, CHUNK):
                parts = [(i0, j0, min(self.n, i0 + CHUNK), min(self.n, j0 + CHUNK))]
                for r in self.pond_rects:
                    parts = [q for p in parts for q in rect_minus(p, r)]
                out += parts
        return out

    def build(self, L):
        for (i0, j0, i1, j1) in self.tiles():
            vid, verts, vn = {}, [], []

            def V(k, top):
                if (k, top) not in vid:
                    vid[(k, top)] = len(verts)
                    x, y, z = self.point(k)
                    verts.append((x, y, z if top else T_BOTTOM))
                    vn.append(self.normal(k) if top else (0, 0, -1))
                return vid[(k, top)]
            faces, mats, sm, lms = [], [], [], []
            outside = max(abs(-OUTER + (i0 + i1) / 2 * GRID), abs(-OUTER + (j0 + j1) / 2 * GRID)) > HALF + 256
            for i in range(i0, i1):
                for j in range(j0, j1):
                    for t in self.cell_tris(i, j):
                        for poly, mat in self.pieces(t):
                            faces.append([V(k, 1) for k in poly])
                            mats.append(mat)
                            sm.append(True)
                            lms.append(-1 if outside else 0)
            # the sides, then the flat underside: a closed solid
            ring = [(i, j0) for i in range(i0, i1)] + [(i1, j) for j in range(j0, j1)] + \
                   [(i, j1) for i in range(i1, i0, -1)] + [(i0, j) for j in range(j1, j0, -1)]
            ring = [(i * SUB, j * SUB) for i, j in ring]
            for k, p in enumerate(ring):
                q = ring[(k + 1) % len(ring)]
                faces.append([V(p, 0), V(q, 0), V(q, 1), V(p, 1)])
                mats.append(M['NODRAW'])
                sm.append(False)
                lms.append(-2)
            faces.append([V(p, 0) for p in reversed(ring)])
            mats.append(M['NODRAW'])
            sm.append(False)
            lms.append(-2)
            L.add(verts, faces, mats, 'terrain', lm=lms, vn=vn, smooth=sm)


# ---------------------------------------------------------------- buildings: the union of boxes on the building's own grid
def cuts(values):
    return sorted(set(values))


def stair_hole(x0, y0, y1, n, under):
    """The hole in an upper floor, of underside `under`, over a stair of n steps rising along +x from x0 between y0 (the room
    side) and y1 (a wall). A player is a box, not a point: from the first tread with less than HEADROOM under the floor, the
    floor is gone over that tread and over the box's width behind it, where the box still stands when it steps up onto that
    tread, with 8 to spare; and it is gone a box's width past the room side, where a box still standing on the stair's edge
    overhangs it by almost that much, with 8 to spare. Reaching past the stair's side also keeps a tread and the floor's
    underside from touching along one edge only, which no closed mesh can hold."""
    first = next(i for i in range(n) if FZ + (i + 1) * RISE + HEADROOM > under)
    return (x0 + first * RUN - HULL_W - 8, y0 - HULL_W - 8, x0 + n * RUN, y1)


class Building:
    """A building in its own frame: x along W, y along D, z up from its pad; rot quarter turns about its centre (cx, cy).
    Boxes: pos (kind) adds, neg cuts out, late adds again over the cuts (stairs through a stair hole)."""

    def __init__(self, name, cx, cy, W, D, rot, style, storeys=1, wall_top=None):
        self.name, self.cx, self.cy, self.W, self.D, self.rot, self.style = name, cx, cy, W, D, rot, style
        self.storeys = storeys
        self.wall_top = wall_top or (FZ + 128 if storeys == 1 else FZ + SH + 120)
        self.pos, self.neg, self.late = [], [], []
        self.floors = []          # (z top, (x0, y0, x1, y1)) in the building's frame
        self.holes = {}           # z top of a floor -> the holes in it, (x0, y0, x1, y1)
        self.steps = []           # (lo, hi) of every step
        self.doors = []           # (wall, centre along it, width, height, sill)
        # the base: a slab reaching FOUND into the ground and standing out PL from the walls
        self.pos.append(((-PL, -PL, -FOUND), (W + PL, D + PL, FZ), 'slab'))
        # the four walls, corner to corner
        top = self.wall_top
        self.pos.append(((0, 0, FZ), (W, T, top), 'wall'))
        self.pos.append(((0, D - T, FZ), (W, D, top), 'wall'))
        self.pos.append(((0, 0, FZ), (T, D, top), 'wall'))
        self.pos.append(((W - T, 0, FZ), (W, D, top), 'wall'))
        self.floors.append((FZ, (T, T, W - T, D - T)))

    def door(self, wall, c, w=DOOR_W, h=DOOR_H, z=FZ):
        self.opening(wall, c, w, z, z + h)
        self.doors.append((wall, c, w, h, z))

    def window(self, wall, c, w=56, storey=0):
        z = FZ + storey * SH
        self.opening(wall, c, w, z + WIN_SILL, z + WIN_HEAD)

    def opening(self, wall, c, w, z0, z1):
        W, D = self.W, self.D
        if wall == 'S':
            self.neg.append(((c - w / 2, 0, z0), (c + w / 2, T, z1)))
        elif wall == 'N':
            self.neg.append(((c - w / 2, D - T, z0), (c + w / 2, D, z1)))
        elif wall == 'W':
            self.neg.append(((0, c - w / 2, z0), (T, c + w / 2, z1)))
        else:
            self.neg.append(((W - T, c - w / 2, z0), (W, c + w / 2, z1)))

    def upper(self, z_top, rect, stair):
        """An upper floor of top z_top over rect, with a hole for its stair; stair = (x0, y0, y1, n): n steps of RISE and RUN
        rising along +x from x0, between y0 (the room side) and y1 (a wall), from the ground floor up to z_top."""
        self.pos.append(((rect[0], rect[1], z_top - SLAB), (rect[2], rect[3], z_top), 'upper'))
        x0, y0, y1, n = stair
        hole = stair_hole(x0, y0, y1, n, z_top - SLAB)
        self.neg.append(((hole[0], hole[1], z_top - SLAB), (hole[2], hole[3], z_top)))
        self.holes.setdefault(z_top, []).append(hole)
        assert n * RISE == z_top - FZ, 'the stairs must end at the floor they lead to'
        for i in range(n):
            lo, hi = (x0 + i * RUN, y0, FZ - 1), (x0 + (i + 1) * RUN, y1, FZ + (i + 1) * RISE)
            self.late.append((lo, hi, 'step'))
            self.steps.append((lo, hi))
        self.floors.append((z_top, (max(rect[0], T), max(rect[1], T), min(rect[2], self.W - T), min(rect[3], self.D - T))))

    def to_world(self, p):
        x, y, z = p[0] - self.W / 2, p[1] - self.D / 2, p[2]
        for _ in range(self.rot % 4):
            x, y = -y, x
        return (self.cx + x, self.cy + y, self.level + z)

    def world_rect(self, r):
        a = self.to_world((r[0], r[1], 0))
        b = self.to_world((r[2], r[3], 0))
        return (min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1]))

    def footprint(self):
        return self.world_rect((-PL, -PL, self.W + PL, self.D + PL))

    def grid(self):
        boxes = [b[:2] for b in self.pos] + list(self.neg) + [b[:2] for b in self.late]
        xs = cuts([v for lo, hi in boxes for v in (lo[0], hi[0])])
        ys = cuts([v for lo, hi in boxes for v in (lo[1], hi[1])])
        zs = cuts([v for lo, hi in boxes for v in (lo[2], hi[2])])
        nx, ny, nz = len(xs) - 1, len(ys) - 1, len(zs) - 1
        solid = [[[None] * nz for _ in range(ny)] for _ in range(nx)]

        def rng(lo, hi):
            return (range(bisect.bisect_left(xs, lo[0]), bisect.bisect_left(xs, hi[0])),
                    range(bisect.bisect_left(ys, lo[1]), bisect.bisect_left(ys, hi[1])),
                    range(bisect.bisect_left(zs, lo[2]), bisect.bisect_left(zs, hi[2])))
        for lo, hi, kind in reversed(self.pos):          # the first box listed owns a cell
            ri, rj, rk = rng(lo, hi)
            for i in ri:
                for j in rj:
                    for k in rk:
                        solid[i][j][k] = kind
        for lo, hi in self.neg:
            ri, rj, rk = rng(lo, hi)
            for i in ri:
                for j in rj:
                    for k in rk:
                        solid[i][j][k] = None
        for lo, hi, kind in self.late:
            ri, rj, rk = rng(lo, hi)
            for i in ri:
                for j in rj:
                    for k in rk:
                        solid[i][j][k] = kind
        self.xs, self.ys, self.zs, self.solid = xs, ys, zs, solid
        return xs, ys, zs, solid

    def solid_at(self, p):
        """True when the world point p is inside the building's mesh (the frame's cells), or inside its roof."""
        x, y, z = p[0] - self.cx, p[1] - self.cy, p[2] - self.level
        for _ in range((4 - self.rot % 4) % 4):
            x, y = -y, x
        x, y = x + self.W / 2, y + self.D / 2
        if self.roof_contains(x, y, z):
            return True
        i, j, k = bisect.bisect_right(self.xs, x) - 1, bisect.bisect_right(self.ys, y) - 1, bisect.bisect_right(self.zs, z) - 1
        if not (0 <= i < len(self.xs) - 1 and 0 <= j < len(self.ys) - 1 and 0 <= k < len(self.zs) - 1):
            return False
        return self.solid[i][j][k] is not None

    def roof_contains(self, x, y, z):
        base, ridge, along_x, half = self.roof_shape()
        if along_x:
            if not (-GABLE <= x <= self.W + GABLE):
                return False
            off = abs(y - self.D / 2)
        else:
            if not (-GABLE <= y <= self.D + GABLE):
                return False
            off = abs(x - self.W / 2)
        if off > half or z < base:
            return False
        return z <= ridge - PITCH * off

    def roof_shape(self):
        along_x = self.W >= self.D
        span = (self.D if along_x else self.W) / 2 + EAVE
        base = self.wall_top - 2
        return base, base + PITCH * span, along_x, span

    def mats(self, kind, n, plane, centre):
        st = self.style
        W, D = self.W, self.D
        if kind == 'wall':
            if n[2] > 0:
                return M['NODRAW'] if plane >= self.wall_top else M['TRIM']
            if n[2] < 0:
                return M['TRIM']
            if (n[0] < 0 and plane == 0) or (n[0] > 0 and plane == W) or (n[1] < 0 and plane == 0) or (n[1] > 0 and plane == D):
                return st['ext']
            if (n[0] > 0 and plane == T) or (n[0] < 0 and plane == W - T) or (n[1] > 0 and plane == T) or (n[1] < 0 and plane == D - T):
                return st['int']
            return M['TRIM']
        if kind == 'slab':
            if n[2] > 0:
                inside = T <= centre[0] <= W - T and T <= centre[1] <= D - T
                return st['floor'] if inside else st['base']
            if n[2] < 0:
                return M['NODRAW']
            return st['base']
        if kind == 'upper':
            if n[2] > 0:
                return st['floor2']
            if n[2] < 0:
                return M['CEILWOOD']      # dark wood, as under every roof: a white ceiling near a light glares
            return M['TRIM']
        if kind == 'step':
            return st['floor'] if n[2] > 0 else M['TRIM']
        raise ValueError(kind)

    def mesh(self):
        """The building's mesh: every face between a solid cell and an empty one, on the shared grid vertices."""
        xs, ys, zs, solid = self.grid()
        nx, ny, nz = len(xs) - 1, len(ys) - 1, len(zs) - 1

        def at(i, j, k):
            return solid[i][j][k] if 0 <= i < nx and 0 <= j < ny and 0 <= k < nz else None
        vid, verts, faces, mats = {}, [], [], []

        def V(i, j, k):
            if (i, j, k) not in vid:
                vid[(i, j, k)] = len(verts)
                verts.append(self.to_world((xs[i], ys[j], zs[k])))
            return vid[(i, j, k)]
        for i in range(nx):
            for j in range(ny):
                for k in range(nz):
                    kind = solid[i][j][k]
                    if kind is None:
                        continue
                    centre = ((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2, (zs[k] + zs[k + 1]) / 2)
                    if at(i + 1, j, k) is None:
                        faces.append([V(i + 1, j, k), V(i + 1, j + 1, k), V(i + 1, j + 1, k + 1), V(i + 1, j, k + 1)])
                        mats.append(self.mats(kind, (1, 0, 0), xs[i + 1], centre))
                    if at(i - 1, j, k) is None:
                        faces.append([V(i, j + 1, k), V(i, j, k), V(i, j, k + 1), V(i, j + 1, k + 1)])
                        mats.append(self.mats(kind, (-1, 0, 0), xs[i], centre))
                    if at(i, j + 1, k) is None:
                        faces.append([V(i, j + 1, k), V(i, j + 1, k + 1), V(i + 1, j + 1, k + 1), V(i + 1, j + 1, k)])
                        mats.append(self.mats(kind, (0, 1, 0), ys[j + 1], centre))
                    if at(i, j - 1, k) is None:
                        faces.append([V(i, j, k), V(i + 1, j, k), V(i + 1, j, k + 1), V(i, j, k + 1)])
                        mats.append(self.mats(kind, (0, -1, 0), ys[j], centre))
                    if at(i, j, k + 1) is None:
                        faces.append([V(i, j, k + 1), V(i + 1, j, k + 1), V(i + 1, j + 1, k + 1), V(i, j + 1, k + 1)])
                        mats.append(self.mats(kind, (0, 0, 1), zs[k + 1], centre))
                    if at(i, j, k - 1) is None:
                        faces.append([V(i, j, k), V(i, j + 1, k), V(i + 1, j + 1, k), V(i + 1, j, k)])
                        mats.append(self.mats(kind, (0, 0, -1), zs[k], centre))
        if self.rot % 2:
            pass   # a quarter turn keeps every loop counter-clockwise from outside: rotations do not mirror
        return verts, faces, mats

    def roof(self):
        """The roof: a solid triangular prism over the building and its overhang, its underside 2 below the wall tops."""
        base, ridge, along_x, span = self.roof_shape()
        W, D = self.W, self.D
        if along_x:
            a0, a1 = -GABLE, W + GABLE
            pts = [(a0, D / 2 - span, base), (a1, D / 2 - span, base), (a1, D / 2 + span, base), (a0, D / 2 + span, base),
                   (a0, D / 2, ridge), (a1, D / 2, ridge)]
            faces = [[0, 1, 2, 3], [0, 4, 5, 1], [3, 2, 5, 4], [0, 3, 4], [1, 5, 2]]
        else:
            a0, a1 = -GABLE, D + GABLE
            pts = [(W / 2 - span, a0, base), (W / 2 + span, a0, base), (W / 2 + span, a1, base), (W / 2 - span, a1, base),
                   (W / 2, a0, ridge), (W / 2, a1, ridge)]
            faces = [[0, 1, 2, 3], [1, 4, 5, 2], [3, 5, 4, 0], [0, 4, 1], [2, 5, 3]]
        verts = [self.to_world(p) for p in pts]
        mats = []
        for f in faces:
            nz = norm(newell(verts, f))[2]
            mats.append(M['CEILWOOD'] if abs(nz) > 0.99 else self.style['roof'] if abs(nz) > 0.2 else self.style['gable'])
        return verts, faces, mats


# ---------------------------------------------------------------- the layout
class Layout:
    def __init__(self):
        self.solids = []
        self.props = []
        self.waters = []
        self.lights = []
        self.probes = []
        self.buildings = []
        self.spawns = []

    def add(self, verts, faces, mats, tag, wind='given', **kw):
        s = {'verts': [tuple(float(c) for c in v) for v in verts], 'faces': [list(f) for f in faces], 'mats': list(mats),
             'tag': tag, 'wind': wind}
        s.update(kw)
        self.solids.append(s)
        return s

    def box(self, lo, hi, mat, tag, bottom=None):
        mats = [mat] * 6
        if bottom:
            mats[1] = bottom
        return self.add(box_verts(lo, hi), BOX_FACES, mats, tag, wind='centroid')

    def prop(self, key, origin, yaw=0, scale=1.0, tag='prop'):
        model, render, phys = MODELS[key]
        self.props.append({'key': key, 'model': model, 'origin': tuple(origin), 'yaw': yaw, 'scale': scale, 'tag': tag,
                           'render': render, 'phys': phys})


STYLES = {
    'brick': dict(ext=M['BRICK'], int=M['WALLPAPER1'], floor=M['FLOOR'], floor2=M['FLOOR2'], base=M['STONE'],
                  roof=M['TILE'], gable=M['BRICK']),
    'stucco': dict(ext=M['STUCCO'], int=M['PLASTER'], floor=M['FLOOR2'], floor2=M['FLOOR'], base=M['STONE2'],
                   roof=M['THATCH'], gable=M['TRIM']),
    'green': dict(ext=M['GREEN'], int=M['WALLPAPER2'], floor=M['FLOOR'], floor2=M['FLOOR2'], base=M['COBBLE'],
                  roof=M['METALROOF'], gable=M['TRIM']),
    'stone': dict(ext=M['STONE'], int=M['PLASTER'], floor=M['FLOOR2'], floor2=M['FLOOR'], base=M['STONE2'],
                  roof=M['TILE'], gable=M['STONE']),
    'red': dict(ext=M['RED'], int=M['WALLPAPER1'], floor=M['FLOOR'], floor2=M['FLOOR2'], base=M['BRICK2'],
                roof=M['TILE'], gable=M['TRIM']),
    'barn': dict(ext=M['PLANKS'], int=M['PLANKS'], floor=M['FLOOR2'], floor2=M['FLOOR2'], base=M['COBBLE'],
                 roof=M['CORR'], gable=M['PLANKS']),
    'shed': dict(ext=M['PLANKS'], int=M['PLANKS'], floor=M['FLOOR'], floor2=M['FLOOR'], base=M['STONE2'],
                 roof=M['CORR'], gable=M['PLANKS']),
}


def buildings():
    """The village: where each building stands, its doors and windows, and the two houses' and the barn's upper floors."""
    out = []
    # H1: the farmhouse, two storeys of brick; the stairs run along the north wall
    b = Building('H1 farmhouse', -720, 680, 448, 320, 0, STYLES['brick'], storeys=2)
    b.door('S', 136)
    b.door('E', 160)
    for c in (300, 380):
        b.window('S', c)
        b.window('S', c, storey=1)
    for c in (96, 224, 352):
        b.window('N', c, storey=1)
    b.window('N', 352)
    b.window('W', 112)
    b.window('W', 220, storey=1)
    b.window('E', 80, storey=1)
    b.upper(FZ + SH, (0, 0, 448, 320), (T + 24, 320 - T - 64, 320 - T, 12))
    out.append(b)
    # H2: a cottage of stucco under thatch
    b = Building('H2 cottage', -40, 900, 320, 256, 1, STYLES['stucco'])
    b.door('S', 160)
    b.door('N', 96)
    b.window('S', 64)
    b.window('N', 224)
    b.window('W', 128)
    b.window('E', 88)
    b.window('E', 176)
    out.append(b)
    # H3: the green house, a long single storey
    b = Building('H3 green house', 720, 760, 384, 256, 0, STYLES['green'])
    b.door('S', 96)
    b.door('W', 160)
    for c in (208, 304):
        b.window('S', c)
    for c in (96, 288):
        b.window('N', c)
    b.window('E', 128)
    out.append(b)
    # H4: the stone house
    b = Building('H4 stone house', 940, 40, 320, 288, 1, STYLES['stone'])
    b.door('S', 96)
    b.door('E', 192)
    b.window('S', 224)
    b.window('N', 96)
    b.window('N', 224)
    b.window('W', 144)
    out.append(b)
    # H5: the red house, two storeys; the stairs run along the north wall
    b = Building('H5 red house', 560, -700, 384, 288, 0, STYLES['red'], storeys=2)
    b.door('S', 112)
    b.door('W', 96)
    b.window('S', 272)
    b.window('S', 272, storey=1)
    b.window('S', 112, storey=1)
    b.window('E', 96)
    b.window('E', 192, storey=1)
    b.window('N', 320, storey=1)
    b.upper(FZ + SH, (0, 0, 384, 288), (T + 24, 288 - T - 64, 288 - T, 12))
    out.append(b)
    # the barn: one tall storey with a hay loft over its west end, up a stair against the north wall; wide doors both sides,
    # the north one east of the stair's foot (x 352), so the stair stands in none of it
    b = Building('B1 barn', -780, -620, 512, 320, 0, STYLES['barn'], wall_top=FZ + 232)
    b.door('S', 320, w=160, h=168)
    b.door('N', 432, w=128, h=168)
    b.door('E', 160)
    b.window('W', 160)
    loft, x_loft = FZ + 11 * RISE, T + 168
    b.pos.append(((0, 0, loft - SLAB), (x_loft, 320, loft), 'upper'))
    b.floors.append((loft, (T, T, x_loft, 320 - T)))
    for i in range(11):                      # the top step meets the loft's edge
        lo, hi = (x_loft + (10 - i) * RUN, 320 - T - 64, FZ - 1), (x_loft + (11 - i) * RUN, 320 - T, FZ + (i + 1) * RISE)
        b.late.append((lo, hi, 'step'))
        b.steps.append((lo, hi))
    out.append(b)
    # H6: a small house of brick on the west side
    b = Building('H6 small house', -860, 40, 256, 224, 0, STYLES['brick'])
    b.door('S', 80)
    b.door('E', 112)
    b.window('S', 192)
    b.window('W', 112)
    b.window('N', 128)
    out.append(b)
    # a shed of planks in the middle of the field
    b = Building('S1 shed', -120, 120, 224, 160, 0, STYLES['shed'], wall_top=FZ + 120)
    b.door('W', 80)
    b.door('E', 80)
    b.window('N', 112)
    out.append(b)
    return out


# ---------------------------------------------------------------- outdoor pieces
def stone_wall(L, ter, a, b, h=40, w=20):
    """A low dry-stone wall from a to b (axis-aligned): one box, its foot below the lowest ground under it."""
    (x0, y0), (x1, y1) = a, b
    lo = (min(x0, x1) - (w / 2 if x0 == x1 else 0), min(y0, y1) - (w / 2 if y0 == y1 else 0))
    hi = (max(x0, x1) + (w / 2 if x0 == x1 else 0), max(y0, y1) + (w / 2 if y0 == y1 else 0))
    zs = [ter.z(lo[0] + (hi[0] - lo[0]) * s / 8, lo[1] + (hi[1] - lo[1]) * t / 2) for s in range(9) for t in range(3)]
    zb, zt = math.floor(min(zs)) - 20, math.ceil(max(zs)) + h
    L.box((lo[0], lo[1], zb), (hi[0], hi[1], zt), M['COBBLE'], 'stonewall', bottom=M['NODRAW'])


def hay_bale(L, ter, x, y, along_x=True, stack=False):
    lx, ly = (48, 32) if along_x else (32, 48)
    z0, z1 = ter.zrange(x, y, 24)
    zb, zt = math.floor(z0) - 12, math.floor(z1) + 30
    L.box((x - lx / 2, y - ly / 2, zb), (x + lx / 2, y + ly / 2, zt), M['STRAW'], 'bale', bottom=M['NODRAW'])
    if stack:
        L.box((x - lx / 2 + 6, y - ly / 2 + 6, zt - 6), (x + lx / 2 - 6, y + ly / 2 - 6, zt + 30), M['STRAW'], 'bale',
              bottom=M['NODRAW'])


# ---------------------------------------------------------------- build the layout
def build_layout():
    L = Layout()
    L.ter = None
    bs = buildings()
    PADS.clear()
    for b in bs:
        # the pad: the building's level is the field's mean height under it, rounded
        b.level = 0
        r = b.footprint()
        samples = [field(r[0] + (r[2] - r[0]) * s / 4, r[1] + (r[3] - r[1]) * t / 4) for s in range(5) for t in range(5)]
        b.level = int(round(sum(samples) / len(samples)))
        PADS.append(((r[0] - PAD, r[1] - PAD, r[2] + PAD, r[3] + PAD), b.level))
    for a in range(len(bs)):
        ra = PADS[a][0]
        assert -HALF + 64 <= ra[0] and ra[2] <= HALF - 64 and -HALF + 64 <= ra[1] and ra[3] <= HALF - 64, f'{bs[a].name} too near the edge'
        for c in range(a + 1, len(bs)):
            rc = PADS[c][0]
            assert ra[2] + 32 <= rc[0] or rc[2] + 32 <= ra[0] or ra[3] + 32 <= rc[1] or rc[3] + 32 <= ra[1], \
                f'{bs[a].name} and {bs[c].name}: their pads are less than 32 apart'
        for (cx, cy, R, D) in PONDS:
            assert rect_dist(cx, cy, ra) > R + 16, f'{bs[a].name}: a pond on its pad'
    ter = Terrain()
    L.ter = ter
    ter.build(L)
    # the buildings, their roofs, a light per storey and a probe volume each
    for b in bs:
        verts, faces, mats = b.mesh()
        L.add(verts, faces, mats, 'building', name=b.name)
        rv, rf, rm = b.roof()
        L.add(rv, rf, rm, 'roof', wind='centroid', name=b.name)
        fp = b.footprint()
        tops = sorted(z for z, _ in b.floors)
        for (z, r) in b.floors:
            ceiling = min([t - SLAB for t in tops if t > z] + [b.wall_top - 2])
            mx, my = (r[0] + r[2]) / 2, (r[1] + r[3]) / 2
            # under an upper floor the light hangs 40 under its underside: at 20 it burns a white circle into it
            under = any(t > z and u[0] <= mx <= u[2] and u[1] <= my <= u[3] for t, u in b.floors)
            cr = b.world_rect(r)
            L.lights.append({'origin': ((cr[0] + cr[2]) / 2, (cr[1] + cr[3]) / 2, b.level + ceiling - (40 if under else 20)),
                             'lumens': 1600, 'range': 420})
        L.probes.append({'lo': (fp[0] - 8, fp[1] - 8, b.level - 8), 'hi': (fp[2] + 8, fp[3] + 8, b.level + b.wall_top + 8),
                         'voxel': 24, 'priority': 1})
        L.buildings.append(b)
    # the ponds: shallow water in convex quarters (one big hull fails to build), level below the lowest ground round it
    for (cx, cy, R, depth) in PONDS:
        rim = min(ter.z(cx + R * math.cos(2 * math.pi * k / 48), cy + R * math.sin(2 * math.pi * k / 48)) for k in range(48))
        w = math.floor(rim) - 6 + 0.375
        n = 24
        for q in range(4):
            arc = [(cx + R * math.cos(2 * math.pi * k / n), cy + R * math.sin(2 * math.pi * k / n))
                   for k in range(q * n // 4, (q + 1) * n // 4 + 1)]
            poly = [(cx, cy)] + arc
            vs, fs = prism(poly, w - depth - 24, w)
            mats = [M['POOLWATER']] + [M['NODRAW']] * (len(fs) - 1)
            L.waters.append({'verts': [tuple(float(c) for c in v) for v in vs], 'faces': fs, 'mats': mats, 'pond': (cx, cy, R),
                             'z1': w})
    # low stone walls and haystacks: cover in the open; the wall at y -360 leaves a gap for the north-south path
    for a, b in [((-300, -360), (-238, -360)), ((-142, -360), (-60, -360)), ((300, 600), (300, 800)), ((920, -420), (1120, -420)),
                 ((-500, -1100), (-260, -1100)), ((420, 1100), (640, 1100)), ((-1140, 300), (-1140, 360)), ((60, -560), (60, -380))]:
        stone_wall(L, ter, a, b)
    for (x, y, ax, st) in [(-200, 480, True, True), (-130, 540, False, False), (640, 80, True, False), (1000, -600, False, True),
                           (-480, -980, True, False), (-1080, 520, False, True), (120, 1160, True, False), (1100, 1100, False, False),
                           (-1060, -1100, True, True), (820, -1100, False, False)]:
        hay_bale(L, ter, x, y, ax, st)
    # props: cars and crates (solid), clear of the paths; foliage (none: nobody gets stuck in it)
    rnd = random.Random(7)
    for (key, x, y, yaw) in [('sedan1', -370, 300, 100), ('hatch', 120, -68, 167), ('van', -1080, -260, 90), ('sedan2', 640, -340, 180),
                             ('crate_big', -960, 340, 0), ('crate_big', 1150, -320, 30), ('barrel', -560, 900, 0),
                             ('barrel', 470, -380, 0), ('wheelbarrow', -560, -280, 70)]:
        L.prop(key, (x, y, ter.zrange(x, y, 24)[0] - 2), yaw, 1.0, 'cover')

    def clear_of_buildings(x, y, r):
        return all(rect_dist(x, y, b.footprint()) > r for b in bs)

    def clear_of_ponds(x, y, r):
        return all(math.hypot(x - cx, y - cy) > R + r for (cx, cy, R, _) in PONDS)

    def clear_of_paths(x, y, r):
        return all(seg_dist(x, y, a, b) > r for p in PATHS for a, b in zip(p, p[1:]))
    trees = []
    for (cx, cy, n) in [(-1080, 1080, 5), (-480, 1100, 3), (360, 1080, 3), (1100, 620, 3), (1100, -760, 3), (180, -1060, 4),
                        (-40, -420, 3), (-1100, -980, 3), (-460, 300, 2), (300, 760, 2), (-560, 160, 2)]:
        for k in range(n):
            for _ in range(20):
                x, y = cx + rnd.uniform(-170, 170), cy + rnd.uniform(-170, 170)
                if abs(x) < HALF - 80 and abs(y) < HALF - 80 and clear_of_buildings(x, y, 140) and clear_of_ponds(x, y, 60) and \
                        clear_of_paths(x, y, 60) and all(math.hypot(x - tx, y - ty) > 150 for tx, ty in trees):
                    trees.append((x, y))
                    key = rnd.choice(['poplar', 'poplar_s', 'oak', 'sumac'])      # no maples in the field: 650 wide, they hide it
                    L.prop(key, (x, y, ter.zrange(x, y, 16)[0] - 4), rnd.uniform(0, 360), rnd.uniform(0.8, 1.05), 'tree')
                    break
    for k in range(70):
        for _ in range(20):
            x, y = rnd.uniform(-HALF + 60, HALF - 60), rnd.uniform(-HALF + 60, HALF - 60)
            if clear_of_buildings(x, y, 60) and clear_of_ponds(x, y, 20) and clear_of_paths(x, y, 40):
                L.prop(rnd.choice(['dogwood', 'dogwood2', 'huckle']), (x, y, ter.zrange(x, y, 16)[0] - 2), rnd.uniform(0, 360),
                       rnd.uniform(0.7, 1.0), 'bush')
                break
    for k in range(160):
        x, y = rnd.uniform(-HALF + 40, HALF - 40), rnd.uniform(-HALF + 40, HALF - 40)
        if clear_of_buildings(x, y, 24) and clear_of_ponds(x, y, 0):
            L.prop(rnd.choice(['tallgrass', 'tallgrass', 'flower']), (x, y, ter.zrange(x, y, 8)[0] - 1), rnd.uniform(0, 360),
                   rnd.uniform(0.8, 1.2), 'grass')
    # the forest round the field, outside the player clip
    for k in range(260):
        side = rnd.random()
        t = rnd.uniform(-OUTER + 80, OUTER - 80)
        d = rnd.uniform(HALF + 120, OUTER - 120)
        x, y = [(t, d), (t, -d), (d, t), (-d, t)][int(side * 4)]
        key = rnd.choice(['poplar', 'maple2', 'oak', 'cypress', 'card1', 'card2', 'card3'])
        L.prop(key, (x, y, ter.zrange(x, y, 16)[0] - 6), rnd.uniform(0, 360), rnd.uniform(0.9, 1.2),
               'card' if key.startswith('card') else 'tree')
    # tall grass in clumps along the edge of each pond's mud, every 64 or so round it
    tufts = random.Random(11)
    for (cx, cy, R, D) in PONDS:
        n = round(2 * math.pi * MUD * R / 64)
        for k in range(n):
            a = 2 * math.pi * (k + tufts.uniform(-0.3, 0.3)) / n
            for _ in range(2):
                r = MUD * R + tufts.uniform(-10, 14)
                px, py = cx + r * math.cos(a) + tufts.uniform(-8, 8), cy + r * math.sin(a) + tufts.uniform(-8, 8)
                L.prop('tallgrass', (px, py, ter.zrange(px, py, 8)[0] - 1), tufts.uniform(0, 360), tufts.uniform(1.0, 1.4), 'grass')
    # the player clip at the edge of the field, and the skybox shell round everything
    c = HALF
    for lo, hi in [((-c - 16, -c - 16), (c + 16, -c)), ((-c - 16, c), (c + 16, c + 16)), ((-c - 16, -c), (-c, c)), ((c, -c), (c + 16, c))]:
        L.box((lo[0], lo[1], T_BOTTOM + 8), (hi[0], hi[1], SKY_TOP - 8), M['CLIP'], 'clip')
    o, top = OUTER, SKY_TOP
    for lo, hi in [((-o - 16, -o - 16, T_BOTTOM - 16), (o + 16, o + 16, T_BOTTOM)), ((-o - 16, -o - 16, top), (o + 16, o + 16, top + 16)),
                   ((-o - 16, -o - 16, T_BOTTOM), (-o, o + 16, top)), ((o, -o - 16, T_BOTTOM), (o + 16, o + 16, top)),
                   ((-o, -o - 16, T_BOTTOM), (o, -o, top)), ((-o, o, T_BOTTOM), (o, o + 16, top))]:
        L.box(lo, hi, M['SKYBOX'], 'skybox')
    pick_spawns(L)
    return L


# ---------------------------------------------------------------- spawns
def blocked(L, x, y, z, pad=4):
    """True when a standing player's box at (x, y) on the floor at z hits a building, a roof, a solid box, a solid prop or the
    ground: the box is sampled every 8 units, 2 above the floor up to 74."""
    for dx in range(-16 - pad, 17 + pad, 8):
        for dy in range(-16 - pad, 17 + pad, 8):
            px, py = x + dx, y + dy
            if L.ter.z(px, py) > z + 2:
                return True
            for dz in range(2, 75, 8):
                p = (px, py, z + dz)
                for b in L.buildings:
                    if rect_dist(px, py, b.footprint()) < 48 and b.solid_at(p):
                        return True
    for s in L.solids:
        if s['tag'] in ('stonewall', 'bale'):
            lo = [min(v[i] for v in s['verts']) for i in range(3)]
            hi = [max(v[i] for v in s['verts']) for i in range(3)]
            if lo[0] < x + 16 + pad and x - 16 - pad < hi[0] and lo[1] < y + 16 + pad and y - 16 - pad < hi[1] and lo[2] < z + 74 and z < hi[2]:
                return True
    for p in L.props:
        if p['tag'] == 'cover' or p['tag'] == 'tree':
            b = p['phys'] or ((-24, -24, 0), (24, 24, 1))
            r = max(abs(b[0][0]), abs(b[0][1]), abs(b[1][0]), abs(b[1][1])) * p['scale'] + 24 + pad
            if math.hypot(x - p['origin'][0], y - p['origin'][1]) < r:
                return True
    return False


def pick_spawns(L):
    """24 deathmatch spawns (Arms Race spawns at random, mp_randomspawn 1), and 10 T in the west and 10 CT in the east for a
    mode with team spawns: all on level ground or a floor, clear of everything, far apart."""
    ter = L.ter
    outdoor, indoor = [], []
    for x in range(-HALF + 112, HALF - 111, 48):
        for y in range(-HALF + 112, HALF - 111, 48):
            if any(math.hypot(x - cx, y - cy) < R + 48 for (cx, cy, R, _) in PONDS):
                continue
            if any(rect_dist(x, y, b.footprint()) < 40 for b in L.buildings):
                continue
            z0, z1 = ter.zrange(x, y)
            if z1 - z0 > 10:
                continue
            if not blocked(L, x, y, z1):
                outdoor.append((x, y, z1, 'out'))
    for b in L.buildings:
        for (fz, r) in b.floors:
            for lx in range(int(r[0]) + 32, int(r[2]) - 31, 32):
                for ly in range(int(r[1]) + 32, int(r[3]) - 31, 32):
                    if any(lo[0] - 24 < lx < hi[0] + 24 and lo[1] - 24 < ly < hi[1] + 24 for lo, hi in b.steps):
                        continue
                    w = b.to_world((lx, ly, fz))
                    if not blocked(L, w[0], w[1], w[2]):
                        indoor.append((w[0], w[1], w[2], b.name.split()[0]))
    taken = []

    def farthest(pts, k, mind, cap=None):
        out, per = [], {}
        for _ in range(k):
            best, bd = None, -1
            for p in pts:
                if cap and per.get(p[3], 0) >= cap:
                    continue
                d = min([math.hypot(p[0] - q[0], p[1] - q[1]) + 0.5 * abs(p[2] - q[2]) for q in taken + out] or [1e9])
                if d > bd:
                    best, bd = p, d
            if best is None or bd < mind:
                break
            out.append(best)
            if cap:
                per[best[3]] = per.get(best[3], 0) + 1
        return out
    dm_in = farthest(indoor, 8, 120, cap=2)
    taken += dm_in
    dm_out = farthest(outdoor, 16, 220)
    taken += dm_out
    t_in = farthest([p for p in indoor if p[0] < -150], 3, 96, cap=1)
    taken += t_in
    t_out = farthest([p for p in outdoor if p[0] < -150], 7, 160)
    taken += t_out
    ct_in = farthest([p for p in indoor if p[0] > 150], 3, 96, cap=1)
    taken += ct_in
    ct_out = farthest([p for p in outdoor if p[0] > 150], 7, 160)
    picked = [('info_deathmatch_spawn', p) for p in dm_in + dm_out] + [('info_player_terrorist', p) for p in t_in + t_out] + \
             [('info_player_counterterrorist', p) for p in ct_in + ct_out]
    L.spawns = [(cls, p, spawn_yaw(L, *p)) for cls, p in picked]
    L.spawn_candidates = (len(outdoor), len(indoor))


def solid_point(L, x, y, z):
    """True when the point is in the ground, a building, a roof, a solid box or a solid prop."""
    if L.ter.z(x, y) > z:
        return True
    for b in L.buildings:
        if rect_dist(x, y, b.footprint()) < 48 and b.solid_at((x, y, z)):
            return True
    for s in L.solids:
        if s['tag'] in ('stonewall', 'bale'):
            if all(min(v[i] for v in s['verts']) < (x, y, z)[i] < max(v[i] for v in s['verts']) for i in range(3)):
                return True
    for p in L.props:
        if p['tag'] == 'cover':
            (x0, y0, z0), (x1, y1, z1) = p['phys']
            a = math.radians(p['yaw'])
            dx, dy = x - p['origin'][0], y - p['origin'][1]
            u, v = dx * math.cos(a) + dy * math.sin(a), -dx * math.sin(a) + dy * math.cos(a)
            if x0 <= u <= x1 and y0 <= v <= y1 and p['origin'][2] + z0 <= z <= p['origin'][2] + z1:
                return True
    return False


def eye_clear(L, x, y, z, yaw, reach=128):
    """How far a player standing on the floor at (x, y, z) sees straight ahead at eye height (64) before anything solid,
    looked at every 8 units up to reach."""
    a = math.radians(yaw)
    for d in range(16, reach + 1, 8):
        if solid_point(L, x + d * math.cos(a), y + d * math.sin(a), z + 64):
            return d
    return reach


def spawn_yaw(L, x, y, z, where):
    """Which way a spawn faces: outside, the middle of the field give or take 40 degrees; inside, the middle of its room give
    or take 15, or its first door when it stands in the middle. When something solid stands closer than 128 that way, it
    turns to the most open way within 90 degrees of it."""
    rnd = random.Random(int(x * 31 + y))
    if where == 'out':
        want = math.degrees(math.atan2(-y, -x)) + rnd.uniform(-40, 40)
    else:
        b = next(b for b in L.buildings if b.name.split()[0] == where)
        r = b.world_rect(min(b.floors, key=lambda f: abs(b.level + f[0] - z))[1])
        cx, cy = (r[0] + r[2]) / 2, (r[1] + r[3]) / 2
        if math.hypot(cx - x, cy - y) < 48:
            wall, c, w, h, sill = b.doors[0]
            cx, cy = {'S': b.to_world((c, 0, 0)), 'N': b.to_world((c, b.D, 0)), 'W': b.to_world((0, c, 0)),
                      'E': b.to_world((b.W, c, 0))}[wall][:2]
        want = math.degrees(math.atan2(cy - y, cx - x)) + rnd.uniform(-15, 15)
    if eye_clear(L, x, y, z, want) >= 128:
        return round(want, 2)
    turn = max(range(-90, 91, 10), key=lambda d: (eye_clear(L, x, y, z, want + d), -abs(d)))
    return round(want + turn, 2)


# ---------------------------------------------------------------- the map source
def build_vmap(L, path):
    world_children = [map_mesh(s) for s in L.solids]
    # lightmap resolution volume round the play space: the texels go there, not to the hills outside
    lv = box_verts((-HALF - 64, -HALF - 64, -64), (HALF + 64, HALF + 64, 640))
    world_children.append(map_mesh({'verts': lv, 'faces': BOX_FACES, 'mats': [M['LMRES']] * 6, 'bake': False, 'wind': 'centroid'}))
    # ponds: func_water (rendered as world, physics as entity), each quarter its own convex hull
    for w in L.waters:
        o = tuple(sum(v[i] for v in w['verts']) / len(w['verts']) for i in range(3))
        world_children.append(entity('func_water', o, {'targetname': ''}, [map_mesh({'verts': w['verts'], 'faces': w['faces'],
                                                                                       'mats': w['mats'], 'wind': 'given'})]))
    # props: light probed, not lightmapped
    for p in L.props:
        veg = p['tag'] in VEG
        fmin, fmax = FADE.get(p['tag'], (-1, 0))
        world_children.append(entity('prop_static', tuple(p['origin']), {
            'model': p['model'], 'skin': 0, 'solid': 0 if veg else 6, 'disableshadows': 0, 'lodlevel': -1,
            'fademindist': fmin, 'fademaxdist': fmax, 'baketoworld': 0, 'disablemerging': 0, 'renderamt': 255, 'rendercolor': '255 255 255 255',
            'rendertocubemaps': 1, 'precomputelightprobes': 1, 'bakelightdoublesided': 0, 'emissive': 1,
            'emissive_lighting_boost': 1, 'materialoverride': '', 'disableinlowquality': 0, 'lightmapscalebias': 0,
            'bakelighting': 0, 'renderwithdynamic': 0, 'detailgeometry': 0, 'terrainlighting': 0, 'snaptoterrain': 0,
            'visoccluder': 0, 'lightingorigin': '', 'lightgroup': '',
        }, angles=(0, p['yaw'], 0), scales=(p['scale'],) * 3))
    # sky and sun: a summer afternoon
    world_children.append(entity('env_sky', (0, 0, 900), {
        'targetname': 'sky', 'StartDisabled': 0, 'skyname': SKY, 'tint_color': '255 255 255', 'brightnessscale': '1.0'},
        angles=(0, 125, 0)))
    world_children.append(entity('light_environment', (0, 0, 1000), {
        'targetname': '', 'enabled': 1, 'color': '255 232 196', 'brightness': '2.6', 'range': 512, 'castshadows': 1,
        'shadowpriority': -1, 'style': 0, 'rendertocubemaps': 1, 'priority': 0, 'lightgroup': '', 'bouncescale': '1.5',
        'renderdiffuse': 1, 'renderspecular': 1, 'rendertransmissive': 1, 'directlight': 3, 'indirectlight': 1,
        'angulardiameter': '0.8', 'skycolor': '120 160 205', 'skyintensity': '1.2', 'skytexture': 'sky',
        'skytexturescale': '1.4', 'skyambientbounce': '0 0 0', 'skydirectlight': 1, 'skybouncescale': '2.0',
        'brightnessscale': '1.0', 'baked_light_indexing': 1, 'minroughness': 0, 'clientSideEntity': 1,
    }, angles=(50, 135, 0)))
    for lt in L.lights:
        world_children.append(entity('light_omni2', tuple(lt['origin']), {
            'clientSideEntity': 1, 'enabled': 1, 'colormode': 0, 'color': '255 222 186', 'colortemperature': 4500,
            'brightness_lumens': lt['lumens'], 'directlight': 1, 'brightness_units': 1, 'shape': 0, 'skirt': '0.1',
            'size_params': '8 16 0.15', 'range': lt['range'], 'rendertocubemaps': 1, 'bakespeculartocubemaps': 2,
            'brightnessscale': '1.0', 'castshadows': 2, 'shadowmapsize': -1, 'shadowpriority': -1, 'bouncelight': 1,
            'bouncescale': '1.0', 'outer_angle': '180.0', 'inner_angle': '180.0', 'fog': 0, 'fogstrength': 0,
        }, angles=(90, 0, 0)))
    # light probes and cubemaps: one big outdoor volume, one finer volume per building that wins inside it
    probes = [{'lo': (-HALF - 32, -HALF - 32, -96), 'hi': (HALF + 32, HALF + 32, 640), 'voxel': 48, 'priority': 0}] + L.probes
    for pv in probes:
        lo, hi = pv['lo'], pv['hi']
        o = tuple((lo[i] + hi[i]) / 2 for i in range(3))
        world_children.append(entity('env_combined_light_probe_volume', o, {
            'targetname': '', 'StartDisabled': 0, 'cubemaptexture': '', 'bakenearz': '2.0', 'bakefarz': '4096.0',
            'lightgroup': '', 'moveable': 0, 'lightprobetexture': '',
            'box_mins': ' '.join(fnum(lo[i] - o[i]) for i in range(3)),
            'box_maxs': ' '.join(fnum(hi[i] - o[i]) for i in range(3)),
            'voxel_size': f"{pv['voxel']:.1f}", 'flood_fill': 1, 'voxelize': 1, 'light_probe_volume_from_cubemap': 0,
            'storage': -1, 'indoor_outdoor_level': pv['priority'], 'edge_fade_dists': '0 0 0', 'clientSideEntity': 1,
        }))
    L.probe_count = len(probes)
    # exposure: one master volume over everything
    pv = box_verts((-OUTER, -OUTER, T_BOTTOM), (OUTER, OUTER, SKY_TOP))
    world_children.append(entity('post_processing_volume', (0, 0, (T_BOTTOM + SKY_TOP) / 2), {
        'StartDisabled': 0, 'spawnflags': 4097, 'postprocessing': '', 'master': 1, 'minexposure': '0.8',
        'maxexposure': '2.0', 'enableexposure': 1, 'fadetime': '1.0', 'exposurespeedup': '1', 'exposurespeeddown': '1',
    }, [map_mesh({'verts': pv, 'faces': BOX_FACES, 'mats': [M['POST']] * 6, 'bake': False, 'wind': 'centroid'})]))
    # spawns: 8 above the floor, facing the way spawn_yaw picked
    for cls, (x, y, z, where), yaw in L.spawns:
        props = {'targetname': '', 'enabled': 1} if cls == 'info_deathmatch_spawn' else {'targetname': '', 'priority': 0, 'enabled': 1}
        world_children.append(entity(cls, (x, y, z + 8), props, angles=(0, yaw, 0)))

    world_props = El('EditGameClassProps', [(k, 'string', v) for k, v in [
        ('classname', 'worldspawn'), ('targetname', ''), ('skyname', 'sky_day01_01'), ('startdark', '0'),
        ('startcolor', '0 0 0'), ('pvstype', '10'), ('newunit', '0'), ('maxpropscreenwidth', '-1'),
        ('minpropscreenwidth', '0'), ('vrchaperone', '0'), ('vrmovement', '0'), ('baked_light_index_min', '0'),
        ('baked_light_index_max', '256'), ('max_lightmap_resolution', '0'), ('lightmap_queries', '1'),
        ('prefab_has_runtime_entity_by_default', '0')]])
    world = El('CMapWorld', [
        ('nodeID', 'int', 1), ('referenceID', 'uint64', '0x0'), ('children', 'element_array', world_children),
        ('variableTargetKeys', 'string_array', []), ('variableNames', 'string_array', []),
        ('relayPlugData', 'element', El('DmePlugList', [('names', 'string_array', []), ('dataTypes', 'int_array', []),
                                                         ('plugTypes', 'int_array', []), ('descriptions', 'string_array', [])])),
        ('connectionsData', 'element_array', []), ('entity_properties', 'element', world_props),
        ('nextDecalID', 'int', 0), ('fixupEntityNames', 'bool', True), ('mapUsageType', 'string', 'standard'),
    ] + transform_tail((0, 0, 0)))
    root = El('CMapRootElement', [
        ('isprefab', 'bool', False), ('editorbuild', 'int', 9806), ('editorversion', 'int', 400), ('itemFile', 'string', ''),
        ('defaultcamera', 'element', El('CStoredCamera', [('position', 'vector3', (1200, -1500, 700)),
                                                          ('lookat', 'vector3', (0, 0, 0))])),
        ('3dcameras', 'element', El('CStoredCameras', [('activecamera', 'int', -1), ('cameras', 'element_array', [])])),
        ('world', 'element', world),
        ('visbility', 'element', El('CVisibilityMgr', [
            ('nodeID', 'int', 0), ('referenceID', 'uint64', '0x0'), ('children', 'element_array', []),
            ('variableTargetKeys', 'string_array', []), ('variableNames', 'string_array', []),
            ('nodes', 'element_array', []), ('hiddenFlags', 'int_array', [])] + transform_tail((0, 0, 0)))),
        ('mapVariables', 'element', El('CMapVariableSet', [
            ('variableNames', 'string_array', []), ('variableValues', 'string_array', []),
            ('variableTypeNames', 'string_array', []), ('variableTypeParameters', 'string_array', []),
            ('m_ChoiceGroups', 'element_array', [])])),
        ('rootSelectionSet', 'element', El('CMapSelectionSet', [
            ('children', 'element_array', []), ('selectionSetName', 'string', ''), ('selectionSetData', 'element', None)])),
        ('m_ReferencedMeshSnapshots', 'element_array', []), ('m_bIsCordoning', 'bool', False),
        ('m_bCordonsVisible', 'bool', False), ('nodeInstanceData', 'element_array', []),
    ])
    used = sorted({m for s in L.solids for m in s['mats']} | {M['POOLWATER'], M['NODRAW'], M['POST'], M['LMRES']})
    models = sorted({p['model'] for p in L.props})
    prefix = El('$prefix_element$', [('map_asset_references', 'string_array', used + models + [SKY])])
    out = ['<!-- dmx encoding keyvalues2 4 format vmap 40 -->']
    write_el(out, prefix, 0)
    write_el(out, root, 0)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(out) + '\n')


def layout_json(L):
    """What checks.py, walkcheck.py and preview.py read: every world mesh, the ponds' water, the props, the buildings' floors
    with their holes, steps and doors (world coordinates), the spawns with the way they face."""
    def solid(s):
        return {'verts': [[round(c, 4) for c in v] for v in s['verts']], 'faces': s['faces'], 'mats': s['mats'], 'tag': s['tag'],
                'wind': s.get('wind', 'given'), 'name': s.get('name', '')}
    def door(b, wall, c, w, h, sill):
        """A door in world terms: the opening through the wall, its sill and head, and the way into the building."""
        W, D = b.W, b.D
        rect, inward = {'S': ((c - w / 2, 0, c + w / 2, T), (0, 1)), 'N': ((c - w / 2, D - T, c + w / 2, D), (0, -1)),
                        'W': ((0, c - w / 2, T, c + w / 2), (1, 0)), 'E': ((W - T, c - w / 2, W, c + w / 2), (-1, 0))}[wall]
        for _ in range(b.rot % 4):
            inward = (-inward[1], inward[0])
        return {'wall': wall, 'rect': b.world_rect(rect), 'z0': b.level + sill, 'z1': b.level + sill + h, 'inward': inward}
    bl = []
    for b in L.buildings:
        bl.append({'name': b.name, 'level': b.level, 'footprint': b.footprint(), 'wall_top': b.level + b.wall_top,
                   'floors': [{'z': b.level + z, 'rect': b.world_rect(r), 'holes': [b.world_rect(h) for h in b.holes.get(z, [])]}
                              for z, r in b.floors],
                   'steps': [{'lo': b.world_rect((lo[0], lo[1], hi[0], hi[1]))[:2] + (b.level + lo[2],),
                              'hi': b.world_rect((lo[0], lo[1], hi[0], hi[1]))[2:] + (b.level + hi[2],)} for lo, hi in b.steps],
                   'doors': [door(b, *d) for d in b.doors]})
    return {
        'half': HALF, 'outer': OUTER, 'grid': GRID, 'tools': sorted(TOOLS),
        'solids': [solid(s) for s in L.solids],
        'waters': [{'verts': w['verts'], 'faces': w['faces'], 'mats': w['mats'], 'pond': w['pond'], 'z1': w['z1']} for w in L.waters],
        'ponds': PONDS, 'paths': PATHS,
        'props': L.props,
        'buildings': bl,
        'spawns': [{'class': cls, 'x': p[0], 'y': p[1], 'z': p[2], 'where': p[3], 'yaw': yaw} for cls, p, yaw in L.spawns],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cs2', default=cs2_dir())
    ap.add_argument('--layout', default=None, help='where to write layout.json (default: next to the vmap)')
    ap.add_argument('--addon', default=NAME, help='addon folder to write into (content and game); the map stays ' + NAME)
    args = ap.parse_args()
    content = os.path.join(args.cs2, 'content', 'csgo_addons', args.addon)
    game = os.path.join(args.cs2, 'game', 'csgo_addons', args.addon)
    os.makedirs(os.path.join(content, 'maps'), exist_ok=True)
    os.makedirs(game, exist_ok=True)
    with open(os.path.join(game, 'addoninfo.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('"AddonInfo"\n{\n\t"maps"\t"%s"\n}\n' % NAME)
    L = build_layout()
    vmap = os.path.join(content, 'maps', NAME + '.vmap')
    build_vmap(L, vmap)
    lp = args.layout or os.path.join(content, 'maps', NAME + '_layout.json')
    with open(lp, 'w', encoding='utf-8') as f:
        json.dump(layout_json(L), f)
    sp = {}
    for cls, p, _ in L.spawns:
        sp[cls + '/' + p[3]] = sp.get(cls + '/' + p[3], 0) + 1
    tags = {}
    for p in L.props:
        tags[p['tag']] = tags.get(p['tag'], 0) + 1
    nfaces = sum(len(s['faces']) for s in L.solids)
    print(f'vmap: {vmap} ({os.path.getsize(vmap) / 1e6:.1f} MB)')
    print(f'meshes: {len(L.solids)} ({nfaces} faces), props: {len(L.props)} {tags}, water pieces: {len(L.waters)}, '
          f'lights: {len(L.lights)}, probe volumes: {L.probe_count}')
    print(f'buildings: {[b.name for b in L.buildings]}')
    print(f'spawns: {len(L.spawns)} {sp} (candidates outdoor {L.spawn_candidates[0]}, indoor {L.spawn_candidates[1]})')
    print(f'layout: {lp}')


if __name__ == '__main__':
    main()
