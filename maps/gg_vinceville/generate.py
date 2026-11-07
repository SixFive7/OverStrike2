#!/usr/bin/env python3
"""gg_vinceville - generates a CS2 gun game map: small houses scattered over a big grassy field, with clusters of trees,
shallow ponds and plenty of corners to ambush (and taser) people in.
Build 2 (2026-11-01): every staircase walkable without crouching or jumping (see walkcheck.py).
Build 3 (2026-11-01): wide doors and little furniture, a cellar under every house, a sewer under the field
joining every building (stairs down in the barn and the shed, four manholes in the field), ponds deep enough to
swim in (Valve's ar_pool_day setup: a deep func_water and an underwater post-processing volume), two to three
times the vegetation, and no tree, bush, hedge or grass with any collision.

Writes the Hammer map source into the CS2 Workshop Tools' content folder:

  content/csgo_addons/gg_vinceville/maps/gg_vinceville.vmap    keyvalues2 DMX, the same structure Hammer saves
  game/csgo_addons/gg_vinceville/addoninfo.txt

and layout.json beside --layout (every mesh, prop, pond and spawn, for the preview render and the checks).

  python generate.py [--cs2 <path to 'Counter-Strike Global Offensive'>] [--layout <file>] [--addon <folder>]
                     [--spawns-from <an earlier layout.json>]

Only stock CS2 materials and models are used, so the addon needs no materials folder of its own. World geometry is closed
CMapMesh solids (boxes, prisms, a height-field terrain in chunks); the trees, bushes, hedges, cars and some furniture are
prop_static entities of stock models; each pond is a func_water (rendered as world, physics as entity) with the stock
shallow water material. Units are Hammer units (1 unit = 1 inch).
"""
import argparse
import json
import math
import os
import sys
import random

NAME = 'gg_vinceville'
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from vmapkit import El, cs2_dir, data_array, entity, fnum, node_ids, stream, transform_tail, use_name, write_el  # noqa: E402 (maps/vmapkit.py: what the generators share)

use_name(NAME)

# ---------------------------------------------------------------- dimensions
GRID = 48            # terrain grid
HALF = 1344          # half-size of the play area: the player clip stands here (2688 x 2688)
OUTER = 1920         # half-size of the terrain and of the inside of the skybox shell
T_BOTTOM = -320      # underside of the terrain
SKY_TOP = 1280       # inside top of the skybox shell
T = 8                # wall thickness
SH = 144             # storey height, floor top to floor top
SLAB = 12            # floor slab thickness
PITCH = 1.07         # roof rise per unit of run: 47 degrees, too steep to stand on
DOOR_W, DOOR_H = 72, 104
WIN_SILL, WIN_HEAD = 34, 94   # 60 tall: a crouching player (54) can jump through
SHELL = 24           # thickness of the terrain shell: the sewer runs under it
HEAD = 84            # headroom kept over every step you walk: a 72-unit player and 12 to spare
TUN_W, TUN_H, CHAN, JUN = 176, 112, 48, 224   # sewer: tunnel width and height inside, water channel, junction
UNDERWATER_VPOST = 'postprocess/postprocess_filmic_underwater.vpost'   # ar_pool_day's underwater look

# ---------------------------------------------------------------- materials (all stock CS2)
M = dict(
    GRASS='materials/de_overpass/nature/wild_grass_1.vmat',
    GRASS2='materials/de_overpass/nature/wild_grass_2.vmat',
    MUD='materials/de_ancient/hr_ancient_ground_mud_01_base.vmat',
    DIRT='materials/de_ancient/hr_ancient_ground_dirt_01_base.vmat',
    ROCK='materials/de_ancient/hr_ancient_ground_rock_01_base.vmat',
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
    WOODWALL='materials/de_inferno/wood/inferno_wood_ceiling_01.vmat',
    FLOOR='materials/de_inferno/wood/inferno_wood_floor_01.vmat',
    FLOOR2='materials/de_train/hr_train_s2/wood/hrts2_wood_floor_boards_01.vmat',
    CEIL='materials/plaster/plasterceiling_int_01.vmat',
    CEILWOOD='materials/de_ancient/hr_ancient_ceiling_wood_01_base.vmat',
    TILE='materials/cs_italy/tileroof01.vmat',
    METALROOF='materials/de_train/hr_train_s2/roof/hrts2_roof_metal_01.vmat',
    THATCH='materials/de_ancient/hr_ancient_roof_thatch_01_base.vmat',
    CORR='materials/metal/hr_metal/hr_metal_corrugated_001_grey.vmat',
    TRIM='materials/hr_massive/wood_planks_1.vmat',
    FURN='materials/cs_italy/wood/wood_boards_worn_1.vmat',
    FABRIC='materials/fabric/fabric_opaque.vmat',
    CARPET='materials/carpet/carpet03.vmat',
    STRAW='materials/de_inferno/ground/inferno_straw_filling_01.vmat',
    CONCRETE='materials/concrete/hr_c/hr_concrete_floor_05.vmat',
    WATER='materials/de_aztec/hr_aztec/hr_aztec_water_shallow.vmat',
    POOLWATER='materials/liquids/overpasswater.vmat',
    SEWERWALL='materials/de_cache/brick/ch2_brick_01_base.vmat',
    NODRAW='materials/tools/toolsnodraw.vmat',
    SKYBOX='materials/tools/toolsskybox.vmat',
    CLIP='materials/tools/toolsplayerclip.vmat',
    LMRES='materials/tools/toolslightmapres.vmat',
    POST='materials/tools_postprocess_volume.vmat',
)
SKY = 'materials/skybox/sky_de_overpass_01.vmat'
# world units per texture repeat: Hammer's default 0.25 units per texel times the colour texture's size
TILE = {M['GRASS']: 512, M['GRASS2']: 512, M['MUD']: 512, M['DIRT']: 512, M['ROCK']: 256, M['BRICK']: 512,
        M['BRICK2']: 512, M['STUCCO']: 512, M['GREEN']: 512, M['RED']: 512, M['PLANKS']: 512, M['STONE']: 512,
        M['STONE2']: 512, M['COBBLE']: 384, M['WALLPAPER1']: 512, M['WALLPAPER2']: 512, M['PLASTER']: 512,
        M['WOODWALL']: 512, M['FLOOR']: 512, M['FLOOR2']: 512, M['CEIL']: 128, M['CEILWOOD']: 512, M['TILE']: 128,
        M['METALROOF']: 512, M['THATCH']: 512, M['CORR']: 256, M['TRIM']: 256, M['FURN']: 256, M['FABRIC']: 128,
        M['CARPET']: 64, M['STRAW']: 128, M['CONCRETE']: 256, M['WATER']: 512, M['POOLWATER']: 512,
        M['SEWERWALL']: 384}
NO_BAKE = {M['SKYBOX'], M['CLIP'], M['NODRAW'], M['POST'], M['LMRES']}

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
    'viburnum': ('models/de_inferno/imported/imported_viburnum_davidii_bush_01.vmdl', ((-64, -67, 0), (40, 46, 32)), None),
    'huckle': ('models/de_overpass/foliage/eg_huckleberry/eg_huckleberry_bush_01.vmdl', ((-19, -19, 0), (20, 19, 60)), None),
    'hedge': ('models/de_overpass/foliage/hedge/hedge_128a.vmdl', ((-78, -28, 0), (79, 27, 74)), ((-65, -18, 0), (63, 19, 66))),
    'tallgrass': ('models/de_overpass/foliage/weed/grass_detail_tall_001.vmdl', ((-16, -17, 0), (15, 12, 42)), None),
    'flower': ('models/foliage/flowers/buttercup/flower_buttercup.vmdl', ((-10, -10, 0), (11, 12, 34)), None),
    'sedan1': ('models/props/de_nuke/hr_nuke/nuke_cars/nuke_sedan01.vmdl', ((-117, -51, 0), (118, 43, 68)), ((-112, -44, 0), (116, 36, 67))),
    'sedan2': ('models/props/de_nuke/hr_nuke/nuke_cars/nuke_sedan02.vmdl', ((-117, -49, 0), (111, 49, 71)), ((-116, -43, 0), (110, 43, 70))),
    'hatch': ('models/props_vehicles/cara_82hatchback.vmdl', ((-84, -39, 0), (87, 39, 60)), ((-82, -39, 0), (84, 39, 60))),
    'van': ('models/props_vehicles/van.vmdl', ((-49, -116, 0), (49, 116, 90)), ((-44, -117, 0), (44, 116, 88))),
    'bookshelf': ('models/de_inferno/generic/bookshelf_wood_01.vmdl', ((0, -24, 0), (19, 24, 107)), ((0, -24, 0), (19, 24, 107))),
    'dresser': ('models/de_inferno/generic/wood_dresser_01.vmdl', ((-33, -14, 0), (33, 17, 44)), ((-33, -14, 0), (33, 17, 44))),
    'fridge': ('models/cs_italy/italy_fridge/italy_fridge.vmdl', ((-26, -30, 0), (0, 0, 88)), ((-26, -30, 0), (0, 0, 88))),
    'chest': ('models/cs_italy/interior/office/cabinet_chest_1.vmdl', ((-22, -43, 0), (22, 43, 55)), ((-22, -43, 0), (22, 43, 55))),
    'armchair': ('models/cs_italy/interior/living/couch_chair_base_1.vmdl', ((-20, -24, 0), (14, 23, 34)), ((-20, -24, 0), (13, 24, 34))),
    'chair': ('models/cs_italy/seating/chair/wood_chair_1.vmdl', ((-7, -9, 0), (10, 9, 37)), ((-7, -9, 0), (10, 9, 37))),
    'sidetable': ('models/de_inferno/generic/side_table_square_01.vmdl', ((-12, -12, 0), (12, 12, 30)), ((-12, -12, 0), (12, 12, 30))),
    'barrel': ('models/cs_italy/props/barrel/italy_barrel_wood_1.vmdl', ((-17, -17, 0), (17, 17, 48)), ((-17, -17, 0), (17, 17, 48))),
    'barrel2': ('models/generic/barrel_kit_01/barrel_01_a.vmdl', ((-17, -17, 0), (17, 17, 48)), ((-17, -17, 0), (17, 17, 48))),
    'crate': ('models/cs_italy/crate/italy_wood_crate_1.vmdl', ((-20, -20, -20), (20, 20, 20)), ((-20, -20, -20), (20, 20, 20))),
    'crate_big': ('models/de_inferno/wood_crate_01/inferno_crate_wood_01.vmdl', ((-41, -41, 0), (41, 43, 61)), ((-41, -41, 0), (41, 43, 61))),
    'pallet': ('models/de_overpass/construction/wood_pallet/wood_pallet.vmdl', ((-26, -31, 0), (28, 31, 7)), ((-26, -31, 0), (28, 31, 7))),
    'wheelbarrow': ('models/de_overpass/junk/wheelbarrow01a/wheelbarrow01a.vmdl', ((-33, -15, 0), (35, 15, 28)), ((-34, -16, 0), (35, 16, 29))),
}
# z offset that puts a model's base on the floor (the crate's origin is its centre)
BASE_Z = {'crate': 20}
VEG = ('tree', 'bush', 'hedge', 'grass', 'card')        # prop tags that get no collision at all (solid 0)
FADE = {'grass': (1400, 2000), 'bush': (2600, 3400)}    # small foliage fades out with distance


# ---------------------------------------------------------------- keyvalues2 DMX writer (as gg_glasspire's)


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
    ax, ay, az = abs(n[0]), abs(n[1]), abs(n[2])
    if az >= ax and az >= ay:
        return (1, 0, 0), (0, -1, 0)
    if ax >= ay:
        return (0, 1, 0), (0, 0, -1)
    return (1, 0, 0), (0, 0, -1)


def smooth(t):
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------- polygon mesh (half-edge CDmePolygonMesh)
def polygon_mesh(verts, faces, face_mats, origin, lm_bias=0, wind='centroid', vn=None, smooth_faces=None):
    """verts: local positions; faces: vertex index loops; face_mats: material per face.
    wind 'centroid': faces are turned to point away from the mesh centre (convex solids); 'given': loops are already
    counter-clockwise seen from outside. vn/smooth_faces: per-vertex normals used on the faces flagged smooth."""
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
    bake = s.get('bake', True) and not all(m in NO_BAKE for m in s['mats'])
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
        ('physicsType', 'string', s.get('phys', 'default')), ('physicsGroup', 'string', ''), ('physicsInteractsAs', 'string', ''),
        ('physicsInteractsWith', 'string', ''), ('physicsInteractsExclude', 'string', ''),
        ('meshData', 'element', polygon_mesh(local, s['faces'], s['mats'], o, s.get('lm', 0), s.get('wind', 'centroid'),
                                              s.get('vn'), s.get('smooth'))),
        ('physicsSimplificationOverride', 'bool', False), ('physicsSimplificationError', 'float', 0.0),
    ] + transform_tail(o))


# ---------------------------------------------------------------- geometry primitives
FK = ['+z', '-z', '+x', '-x', '+y', '-y']                 # face order of BOX_FACES
BOX_FACES = [[4, 5, 7, 6], [0, 2, 3, 1], [1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4]]
ROT_KEY = {'+x': '+y', '+y': '-x', '-x': '-y', '-y': '+x', '+z': '+z', '-z': '-z'}   # one quarter turn CCW


def mats6(m):
    if isinstance(m, str):
        return {k: m for k in FK}
    if 'side' in m or 'top' in m or 'bottom' in m:
        side = m.get('side', m.get('top'))
        d = {'+z': m.get('top', side), '-z': m.get('bottom', side)}
        for k in ('+x', '-x', '+y', '-y'):
            d[k] = m.get(k, side)
        return d
    return {k: m.get(k, m.get('*')) for k in FK}


def box_verts(lo, hi):
    return [(hi[0] if i & 1 else lo[0], hi[1] if i & 2 else lo[1], hi[2] if i & 4 else lo[2]) for i in range(8)]


def rot_pt(x, y, k):
    for _ in range(k % 4):
        x, y = -y, x
    return x, y


def clip_poly(poly, axis, val, keep_le):
    """Sutherland-Hodgman clip of a 2D polygon [(a, b)] against coordinate[axis] <= val (keep_le) or >= val."""
    out = []
    n = len(poly)
    for i in range(n):
        p, q = poly[i], poly[(i + 1) % n]
        pin = p[axis] <= val + 1e-9 if keep_le else p[axis] >= val - 1e-9
        qin = q[axis] <= val + 1e-9 if keep_le else q[axis] >= val - 1e-9
        if pin:
            out.append(p)
        if pin != qin:
            t = (val - p[axis]) / (q[axis] - p[axis])
            out.append((p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])))
    # drop duplicates
    res = []
    for p in out:
        if not res or abs(p[0] - res[-1][0]) > 1e-6 or abs(p[1] - res[-1][1]) > 1e-6:
            res.append(p)
    if len(res) > 1 and abs(res[0][0] - res[-1][0]) < 1e-6 and abs(res[0][1] - res[-1][1]) < 1e-6:
        res.pop()
    return res


def poly_area(poly):
    return 0.5 * sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))


def prism(poly, axis, c0, c1):
    """Convex prism: 2D polygon poly in the plane across axis ('x': poly is (y, z); 'y': poly is (x, z); 'z': (x, y)),
    extruded from c0 to c1 along axis. Returns verts, faces (cap, cap, sides)."""
    n = len(poly)
    if poly_area(poly) < 0:
        poly = list(reversed(poly))

    def P(a, b, c):
        if axis == 'x':
            return (c, a, b)
        if axis == 'y':
            return (a, c, b)
        return (a, b, c)
    vs = [P(a, b, c0) for a, b in poly] + [P(a, b, c1) for a, b in poly]
    faces = [list(range(n)), list(range(n, 2 * n))]
    for i in range(n):
        j = (i + 1) % n
        faces.append([i, j, n + j, n + i])
    return vs, faces


# ---------------------------------------------------------------- layout
class Layout:
    def __init__(self):
        self.solids = []      # {'verts','faces','mats','tag','lm','bake','phys','wind','vn','smooth','lo','hi','block'}
        self.props = []       # {'key','model','origin','yaw','scale','tag'}
        self.waters = []      # {'center','r','n','z0','z1'}
        self.lights = []      # {'origin','lumens','range','color'}
        self.probes = []      # {'lo','hi','voxel','priority'}
        self.floors = []      # indoor spawn regions {'rect','z','tag'}
        self.houses = []      # summary per building
        self.airs = []        # walking spaces cut out of whatever crosses them: (lo, hi, why)
        self.exits = []       # sewer doors and hatch feet: {'bld','x','y','z','dir'}
        self.anchors = []     # doorways and stair ends, for the lane check: {'bld','z','x','y','label'}
        self.sewer_paths = []
        self.tunnel_segs = []
        self.channel_waters = []

    def add_solid(self, verts, faces, mats, tag, lm=0, bake=True, block=True, **kw):
        lo = [min(v[i] for v in verts) for i in range(3)]
        hi = [max(v[i] for v in verts) for i in range(3)]
        s = {'verts': verts, 'faces': faces, 'mats': mats, 'tag': tag, 'lm': lm, 'bake': bake, 'lo': lo, 'hi': hi,
             'block': block}
        s.update(kw)
        self.solids.append(s)
        return s

    def box(self, lo, hi, mats, tag, lm=0, block=True):
        assert all(hi[i] > lo[i] for i in range(3)), (tag, lo, hi)
        m = mats6(mats)
        return self.add_solid(box_verts(lo, hi), BOX_FACES, [m[k] for k in FK], tag, lm, block=block, box=True)

    def prop(self, key, origin, yaw=0.0, scale=1.0, tag='prop'):
        o = list(origin)
        o[2] += BASE_Z.get(key, 0) * scale          # origin given at the base: lift models whose origin is their centre
        self.props.append({'key': key, 'model': MODELS[key][0], 'origin': o, 'yaw': yaw, 'scale': scale, 'tag': tag})


def prop_aabb(p, which='phys', pad=0.0):
    key = p['key']
    b = MODELS[key][2] if which == 'phys' else MODELS[key][1]
    if b is None:
        return None
    (x0, y0, z0), (x1, y1, z1) = b
    s = p['scale']
    yaw = math.radians(p['yaw'])
    c, sn = math.cos(yaw), math.sin(yaw)
    xs, ys = [], []
    for x in (x0, x1):
        for y in (y0, y1):
            xs.append(p['origin'][0] + s * (c * x - sn * y))
            ys.append(p['origin'][1] + s * (sn * x + c * y))
    oz = p['origin'][2]
    return [min(xs) - pad, min(ys) - pad, oz + s * z0 - pad], [max(xs) + pad, max(ys) + pad, oz + s * z1 + pad]


# ---------------------------------------------------------------- local building parts (placed with a quarter-turn rotation)
class Part:
    def __init__(self):
        self.items = []

    def box(self, lo, hi, mats, tag, lm=0):
        if not all(hi[i] - lo[i] > 0.01 for i in range(3)):
            return
        self.items.append(('box', tuple(lo), tuple(hi), mats6(mats), tag, lm))

    def poly(self, verts, faces, mats, tag):
        self.items.append(('poly', verts, faces, mats, tag))

    def prop(self, key, pos, yaw=0.0, scale=1.0):
        self.items.append(('prop', key, pos, yaw, scale))

    def light(self, pos, lumens=900, rng=360):
        self.items.append(('light', pos, lumens, rng))

    def floor(self, rect, z, tag):
        self.items.append(('floor', rect, z, tag))

    def probe(self, lo, hi):
        self.items.append(('probe', lo, hi))

    def air(self, lo, hi, why):
        self.items.append(('air', tuple(lo), tuple(hi), why))

    def exit_pt(self, x, y, z, d, why):
        self.items.append(('exit', x, y, z, d, why))

    def anchor(self, z, x, y, label):
        self.items.append(('anchor', z, x, y, label))

    def place(self, L, cx, cy, cz, k, tag):
        def tp(x, y, z):
            x, y = rot_pt(x, y, k)
            return (x + cx, y + cy, z + cz)
        for it in self.items:
            if it[0] == 'box':
                _, lo, hi, m, t, lm = it
                a = tp(lo[0], lo[1], lo[2])
                b = tp(hi[0], hi[1], hi[2])
                nlo = (min(a[0], b[0]), min(a[1], b[1]), lo[2] + cz)
                nhi = (max(a[0], b[0]), max(a[1], b[1]), hi[2] + cz)
                mm = dict(m)
                for _ in range(k % 4):
                    mm = {ROT_KEY[key]: v for key, v in mm.items()}
                L.box(nlo, nhi, mm, f'{tag}/{t}', lm)
            elif it[0] == 'poly':
                _, vs, fs, ms, t = it
                L.add_solid([tp(*v) for v in vs], fs, ms, f'{tag}/{t}')
            elif it[0] == 'prop':
                _, key, pos, yaw, scale = it
                L.prop(key, tp(*pos), (yaw + 90 * k) % 360, scale, tag)
            elif it[0] == 'light':
                _, pos, lm, rng = it
                L.lights.append({'origin': tp(*pos), 'lumens': lm, 'range': rng})
            elif it[0] == 'floor':
                _, r, z, t = it
                a, b = tp(r[0], r[1], z), tp(r[2], r[3], z)
                L.floors.append({'rect': (min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])), 'z': z + cz,
                                 'tag': f'{tag}/{t}'})
            elif it[0] == 'probe':
                _, lo, hi = it
                a, b = tp(lo[0], lo[1], lo[2]), tp(hi[0], hi[1], hi[2])
                L.probes.append({'lo': (min(a[0], b[0]), min(a[1], b[1]), lo[2] + cz),
                                 'hi': (max(a[0], b[0]), max(a[1], b[1]), hi[2] + cz), 'voxel': 24, 'priority': 2})
            elif it[0] == 'air':
                _, lo, hi, why = it
                a, b = tp(lo[0], lo[1], lo[2]), tp(hi[0], hi[1], hi[2])
                L.airs.append(((min(a[0], b[0]), min(a[1], b[1]), lo[2] + cz),
                               (max(a[0], b[0]), max(a[1], b[1]), hi[2] + cz), f'{tag}/{why}'))
            elif it[0] == 'exit':
                _, x, y, z, d, why = it
                p = tp(x, y, z)
                L.exits.append({'bld': tag, 'x': p[0], 'y': p[1], 'z': p[2], 'dir': rot_pt(d[0], d[1], k), 'why': why})
            elif it[0] == 'anchor':
                _, z, x, y, label = it
                p = tp(x, y, z)
                L.anchors.append({'bld': tag, 'z': p[2], 'x': p[0], 'y': p[1], 'label': label})


def rect_minus(outer, holes):
    """outer minus holes, as merged axis-aligned rectangles (x0, y0, x1, y1)."""
    xs = sorted({outer[0], outer[2]} | {min(max(h[i], outer[0]), outer[2]) for h in holes for i in (0, 2)})
    ys = sorted({outer[1], outer[3]} | {min(max(h[i], outer[1]), outer[3]) for h in holes for i in (1, 3)})
    rows = []
    for j in range(len(ys) - 1):
        strips, cur = [], None
        for i in range(len(xs) - 1):
            cx, cy = (xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2
            inside = not any(h[0] < cx < h[2] and h[1] < cy < h[3] for h in holes)
            if inside:
                cur = [xs[i], xs[i + 1]] if cur is None else [cur[0], xs[i + 1]]
            elif cur is not None:
                strips.append(tuple(cur))
                cur = None
        if cur is not None:
            strips.append(tuple(cur))
        rows.append((ys[j], ys[j + 1], strips))
    out, open_ = [], {}
    for y0, y1, strips in rows:
        nxt = {}
        for s in strips:
            nxt[s] = (open_[s][0], y1) if s in open_ else (y0, y1)
        for s, (a, b) in open_.items():
            if s not in nxt:
                out.append((s[0], a, s[1], b))
        open_ = nxt
    for s, (a, b) in open_.items():
        out.append((s[0], a, s[1], b))
    return out


# ---------------------------------------------------------------- house builder (local frame: footprint centred on 0,
# front wall on -y, ground floor top at z 0, outside ground at z -12)
class House:
    def __init__(self, W, D, storeys, ext, ints, floor, roof, ceil=M['CEIL'], trim=M['TRIM'], found=M['COBBLE'],
                 plinth=None, cellar=False, chimney=None, overhang=20):
        self.P = Part()
        self.W, self.D, self.n = W, D, storeys
        self.ext, self.ints, self.fl, self.roofm, self.ceil, self.trim = ext, ints, floor, roof, ceil, trim
        self.ze = SH * storeys                       # top of the walls (eave line)
        self.zr = self.ze + (D / 2) * PITCH          # roof ridge (underside)
        self.oh = overhang
        P = self.P
        W2, D2 = W / 2, D / 2
        self.ix0, self.iy0, self.ix1, self.iy1 = -W2 + T, -D2 + T, W2 - T, D2 - T   # inner faces of the outer walls
        pr = plinth or (-W2 - 8, -D2 - 8, W2 + 8, D2 + 8)
        self.plinth = pr
        if cellar:
            # the plinth is a ring round the cellar; its inner faces are the cellar's walls
            inner = (self.ix0, self.iy0, self.ix1, self.iy1)
            for r in rect_minus(pr, [inner]):
                P.box((r[0], r[1], -SH - SLAB), (r[2], r[3], -2), {'top': found, 'side': M['STONE2'], '-z': M['NODRAW']}, 'plinth')
            P.box((inner[0], inner[1], -SH - SLAB), (inner[2], inner[3], -SH), {'top': M['CONCRETE'], 'side': M['STONE2'],
                                                                                 '-z': M['NODRAW']}, 'cellarfloor')
            self.cellar_inner = inner
        else:
            P.box((pr[0], pr[1], -72), (pr[2], pr[3], -2), {'top': found, 'side': found, '-z': M['NODRAW']}, 'plinth')
            self.cellar_inner = None
        self.slab_holes = {0: [], 1: [], 2: []}

    # walls ----------------------------------------------------------------
    def outer_walls(self, s, openings):
        """storey s; openings: {'S'|'N'|'W'|'E': [(centre, width, 'door'|'window')]} along x (S/N) or y (W/E)."""
        W2, D2 = self.W / 2, self.D / 2
        z0 = s * SH - (2 if s == 0 else 0)
        z1 = (s + 1) * SH if s < self.n - 1 else self.ze + 9
        zf = s * SH
        ext, inn, trim = self.ext, self.ints[min(s, len(self.ints) - 1)], self.trim
        for side in ('S', 'N', 'W', 'E'):
            ops = []
            for (c, w, kind) in openings.get(side, []):
                if kind == 'door':
                    ops.append((c - w / 2, c + w / 2, z0, zf + DOOR_H))
                else:
                    ops.append((c - w / 2, c + w / 2, zf + WIN_SILL, zf + WIN_HEAD))
            if side in ('S', 'N'):
                a, b = -W2, W2
                y0, y1 = (-D2, -D2 + T) if side == 'S' else (D2 - T, D2)
                m_out, m_in = ('-y', '+y') if side == 'S' else ('+y', '-y')
            else:
                a, b = -D2 + T, D2 - T
                x0, x1 = (-W2, -W2 + T) if side == 'W' else (W2 - T, W2)
                m_out, m_in = ('-x', '+x') if side == 'W' else ('+x', '-x')
            cur = a
            pieces = []
            for (o0, o1, zb, zt) in sorted(ops):
                if o0 > cur:
                    pieces.append((cur, o0, z0, z1))
                if zb > z0:
                    pieces.append((o0, o1, z0, zb))
                if zt < z1:
                    pieces.append((o0, o1, zt, z1))
                cur = o1
            if cur < b:
                pieces.append((cur, b, z0, z1))
            for (p0, p1, q0, q1) in pieces:
                m = {k: trim for k in FK}
                m[m_out] = ext
                m[m_in] = inn
                if side in ('S', 'N'):
                    if abs(p0 + W2) < 0.1:
                        m['-x'] = ext
                    if abs(p1 - W2) < 0.1:
                        m['+x'] = ext
                    self.P.box((p0, y0, q0), (p1, y1, q1), m, 'wall')
                else:
                    self.P.box((x0, p0, q0), (x1, p1, q1), m, 'wall')

    def wall_y(self, x, y0, y1, s, doors=(), mat=None, z1=None, tag='partition'):
        """interior wall along y centred on x, storey s; doors: [(centre, width)]."""
        zf = s * SH
        top = z1 if z1 is not None else zf + SH - SLAB
        m = mats6({'side': mat or self.ints[min(s, len(self.ints) - 1)], 'top': self.trim, 'bottom': self.trim})
        m['+y'] = m['-y'] = self.trim
        cur = y0
        for (c, w) in sorted(doors):
            a, b = c - w / 2, c + w / 2
            self.P.box((x - T / 2, cur, zf), (x + T / 2, a, top), m, tag)
            if zf + DOOR_H < top:
                self.P.box((x - T / 2, a, zf + DOOR_H), (x + T / 2, b, top), m, tag)
            cur = b
        self.P.box((x - T / 2, cur, zf), (x + T / 2, y1, top), m, tag)

    def wall_x(self, y, x0, x1, s, doors=(), mat=None, z1=None, tag='partition'):
        zf = s * SH
        top = z1 if z1 is not None else zf + SH - SLAB
        m = mats6({'side': mat or self.ints[min(s, len(self.ints) - 1)], 'top': self.trim, 'bottom': self.trim})
        m['+x'] = m['-x'] = self.trim
        cur = x0
        for (c, w) in sorted(doors):
            a, b = c - w / 2, c + w / 2
            self.P.box((cur, y - T / 2, zf), (a, y + T / 2, top), m, tag)
            if zf + DOOR_H < top:
                self.P.box((a, y - T / 2, zf + DOOR_H), (b, y + T / 2, top), m, tag)
            cur = b
        self.P.box((cur, y - T / 2, zf), (x1, y + T / 2, top), m, tag)

    # floors ----------------------------------------------------------------
    def slabs(self):
        """ground floor, upper floors and the ceiling under the roof, minus the stair holes."""
        inner = (self.ix0, self.iy0, self.ix1, self.iy1)
        for s in range(self.n + 1):
            z = s * SH
            for r in rect_minus(inner, self.slab_holes.get(s, [])):
                top = self.fl if s < self.n else M['FLOOR2']
                bottom = self.ceil if s > 0 else (M['CEILWOOD'] if self.cellar_inner else M['NODRAW'])
                self.P.box((r[0], r[1], z - SLAB), (r[2], r[3], z), {'top': top, 'bottom': bottom, 'side': self.trim}, 'slab')
                if s < self.n:
                    self.P.floor(r, z, f'floor{s}')     # spawn candidates: only where there is floor

    def _steps(self, rect, axis, up, z_lo, z_hi, n, first, mat):
        """step boards over rect climbing along axis (up = +1 when height grows with the coordinate): each a 10-unit
        board (open risers), the lowest two solid to the floor. first: depth of the lowest step, so a stair whose foot
        stands against a wall can be stepped onto from the side (a player is 32 deep, so next to a 13-unit first step
        he also stands over the 24-unit second one, too high to step onto). Returns [(s0, s1, top)] per step."""
        x0, y0, x1, y1 = rect
        a0, a1 = (x0, x1) if axis == 'x' else (y0, y1)
        rise = (z_hi - z_lo) / n
        if first:
            cuts = [0.0, first] + [first + (a1 - a0 - first) * i / (n - 1) for i in range(1, n)]
        else:
            cuts = [(a1 - a0) * i / n for i in range(n + 1)]
        out = []
        for k in range(1, n + 1):
            top = z_lo + rise * k
            bot = z_lo if k <= 2 else top - 10
            if up > 0:
                s0, s1 = a0 + cuts[k - 1], a0 + cuts[k]
            else:
                s0, s1 = a1 - cuts[k], a1 - cuts[k - 1]
            m = {'top': mat, 'bottom': mat, 'side': mat}
            if axis == 'x':
                self.P.box((s0, y0, bot), (s1, y1, top), m, 'step')
            else:
                self.P.box((x0, s0, bot), (x1, s1, top), m, 'step')
            out.append((s0, s1, top))
        return out

    @staticmethod
    def _hole(steps, rect, axis, ceiling):
        """the opening in the slab above: wherever a standing player (72 tall, 2 to spare) on a step would reach the
        slab's underside, plus a player's depth (32) and 4 to spare round that step, so the head never clips the edge.
        (Build 1 opened only 8 past the step: there you had to crouch to get past the fifth step.)"""
        x0, y0, x1, y1 = rect
        a0, a1 = (x0, x1) if axis == 'x' else (y0, y1)
        high = [(s0, s1) for (s0, s1, top) in steps if top + HEAD > ceiling]
        h0 = max(a0, min(s0 for s0, _ in high) - 36)
        h1 = min(a1, max(s1 for _, s1 in high) + 36)
        return (h0, y0, h1, y1) if axis == 'x' else (x0, h0, x1, h1)

    def stairs(self, rect, axis, up, z_lo, z_hi, n=12, mat=None, hole_level=None, first=None):
        """a staircase over rect; hole_level: the level of the slab it climbs through (that slab gets the opening)."""
        steps = self._steps(rect, axis, up, z_lo, z_hi, n, first, mat or M['FURN'])
        if hole_level is not None:
            hole = self._hole(steps, rect, axis, hole_level - SLAB)
            self.slab_holes.setdefault(round(hole_level / SH), []).append(hole)
            return hole
        return None

    def cellar_stairs(self, rect, axis, up, first=None):
        """stairs from the cellar floor (z -SH) up to the ground floor (z 0), through an opening in the ground floor."""
        steps = self._steps(rect, axis, up, -SH, 0, 12, first, M['FURN'])
        hole = self._hole(steps, rect, axis, -SLAB)
        self.slab_holes.setdefault(0, []).append(hole)
        return hole

    def light(self, pos, lumens=900, rng=360):
        self.P.light(pos, lumens, rng)

    def anchor(self, z, x, y, label):
        self.P.anchor(z, x, y, label)

    def cellar_exit(self, side, c):
        """the sewer door: a corridor out of the cellar through its wall, centred on c along that side."""
        x, y, d = {'E': (self.ix1, c, (1, 0)), 'W': (self.ix0, c, (-1, 0)), 'S': (c, self.iy0, (0, -1)),
                   'N': (c, self.iy1, (0, 1))}[side]
        self.P.exit_pt(x, y, -SH, d, 'cellar')

    def rail(self, x0, y0, x1, y1, z, h=36, mat=None):
        self.P.box((x0, y0, z), (x1, y1, z + h), {'side': mat or self.trim, 'top': self.trim}, 'rail')

    # roof ----------------------------------------------------------------
    def roof(self, gable_windows=(), chimney=None):
        """gable roof, ridge along x; gable walls at x = +-W/2 (window openings: [('W'|'E', centre_y, width, z0, z1)])."""
        W2, D2 = self.W / 2, self.D / 2
        ze, oh, th = self.ze, self.oh, 12
        zr = ze + D2 * PITCH
        xa, xb = -W2 - 16, W2 + 16
        ye = D2 + oh
        z_e = ze - oh * PITCH
        for sgn in (-1, 1):
            # underside from the eave (y = sgn*ye, z_e) up to the ridge (y=0, zr); the top surface th above it.
            # The walls stop 9 into the slab, so no light leaks between wall and roof.
            p = [(sgn * ye, z_e), (0, zr), (0, zr + th), (sgn * ye, z_e + th)]
            vs, fs = prism(p, 'x', xa, xb)
            self.P.poly(vs, fs, self._roof_mats(vs, fs, sgn), 'roof')
        # gable walls, cut round their windows
        for side in ('W', 'E'):
            x0, x1 = (-W2, -W2 + T) if side == 'W' else (W2 - T, W2)
            outline = [(-D2, ze), (D2, ze), (D2, ze + 9), (0, zr + 9), (-D2, ze + 9)]
            wins = [w for w in gable_windows if w[0] == side]
            pieces = [outline]
            for (_, c, w, wz0, wz1) in wins:
                nxt = []
                for pc in pieces:
                    below = clip_poly(pc, 1, wz0, True)
                    above = clip_poly(pc, 1, wz1, False)
                    mid = clip_poly(clip_poly(pc, 1, wz0, False), 1, wz1, True)
                    left = clip_poly(mid, 0, c - w / 2, True)
                    right = clip_poly(mid, 0, c + w / 2, False)
                    for q in (below, above, left, right):
                        if len(q) >= 3 and abs(poly_area(q)) > 1:
                            nxt.append(q)
                pieces = nxt
            for pc in pieces:
                vs, fs = prism(pc, 'x', x0, x1)
                ms = []
                for f in fs:
                    n = norm(newell(vs, f))
                    fx = sum(vs[i][0] for i in f) / len(f)
                    if abs(n[0]) > 0.9:
                        # the cap at the outer face of the house is the outside of the gable
                        outer = abs(fx - (-W2 if side == 'W' else W2)) < 0.01
                        ms.append(self.ext if outer else self.ints[-1])
                    else:
                        ms.append(self.trim)
                self.P.poly(vs, fs, ms, 'gable')
        if chimney:
            cx, cy = chimney
            self.P.box((cx - 20, cy - 20, ze - 4), (cx + 20, cy + 20, zr + 48), {'side': M['BRICK'], 'top': M['CONCRETE']},
                       'chimney')

    def _roof_mats(self, vs, fs, sgn):
        out = []
        for f in fs:
            n = norm(newell(vs, f))
            c = (sum(vs[i][0] for i in f) / len(f), sum(vs[i][1] for i in f) / len(f), sum(vs[i][2] for i in f) / len(f))
            # newell's normal depends on the loop's direction; orient it away from the slab's centre
            cc = (sum(v[0] for v in vs) / len(vs), sum(v[1] for v in vs) / len(vs), sum(v[2] for v in vs) / len(vs))
            if dot(n, sub(c, cc)) < 0:
                n = (-n[0], -n[1], -n[2])
            if n[2] > 0.3:
                out.append(self.roofm)
            elif n[2] < -0.3:
                out.append(M['CEILWOOD'])
            else:
                out.append(self.trim)
        return out

    # furniture ----------------------------------------------------------------
    def table(self, x, y, w, d, z):
        f = M['FURN']
        self.P.box((x - w / 2, y - d / 2, z + 28), (x + w / 2, y + d / 2, z + 32), f, 'table')
        for sx in (-1, 1):
            for sy in (-1, 1):
                lx, ly = x + sx * (w / 2 - 4), y + sy * (d / 2 - 4)
                self.P.box((lx - 2, ly - 2, z), (lx + 2, ly + 2, z + 28), f, 'table')

    def counter(self, x0, y0, x1, y1, z, h=36):
        self.P.box((x0, y0, z), (x1, y1, z + h - 2), {'side': M['FURN'], 'top': M['FURN']}, 'counter')
        self.P.box((x0 - 1, y0 - 1, z + h - 2), (x1 + 1, y1 + 1, z + h), M['CONCRETE'], 'counter')

    def wardrobe(self, x0, y0, x1, y1, z, h=84):
        self.P.box((x0, y0, z), (x1, y1, z + h), M['FURN'], 'wardrobe')

    def bed(self, x0, y0, x1, y1, z, head):
        self.P.box((x0, y0, z), (x1, y1, z + 14), M['FURN'], 'bed')
        self.P.box((x0 + 2, y0 + 2, z + 14), (x1 - 2, y1 - 2, z + 22), M['FABRIC'], 'bed')
        if head == '-x':
            self.P.box((x0, y0, z), (x0 + 4, y1, z + 40), M['FURN'], 'bed')
        elif head == '+x':
            self.P.box((x1 - 4, y0, z), (x1, y1, z + 40), M['FURN'], 'bed')
        elif head == '-y':
            self.P.box((x0, y0, z), (x1, y0 + 4, z + 40), M['FURN'], 'bed')
        else:
            self.P.box((x0, y1 - 4, z), (x1, y1, z + 40), M['FURN'], 'bed')

    def sofa(self, x0, y0, x1, y1, z, back):
        self.P.box((x0, y0, z), (x1, y1, z + 18), M['FABRIC'], 'sofa')
        if back == '-y':
            self.P.box((x0, y0, z + 18), (x1, y0 + 10, z + 34), M['FABRIC'], 'sofa')
        elif back == '+y':
            self.P.box((x0, y1 - 10, z + 18), (x1, y1, z + 34), M['FABRIC'], 'sofa')
        elif back == '-x':
            self.P.box((x0, y0, z + 18), (x0 + 10, y1, z + 34), M['FABRIC'], 'sofa')
        else:
            self.P.box((x1 - 10, y0, z + 18), (x1, y1, z + 34), M['FABRIC'], 'sofa')

    def rug(self, x0, y0, x1, y1, z):
        self.P.box((x0, y0, z), (x1, y1, z + 1), M['CARPET'], 'rug')

    def stove(self, x0, y0, x1, y1, z):
        self.P.box((x0, y0, z), (x1, y1, z + 34), {'side': M['CORR'], 'top': M['CONCRETE']}, 'stove')
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        self.P.box((cx - 5, cy - 5, z + 34), (cx + 5, cy + 5, z + SH - SLAB), M['CORR'], 'flue')

    def porch(self, x0, x1, depth, roof_mat=None, rails=True, door_c=0.0):
        """front porch on the -y side between x0 and x1."""
        D2 = self.D / 2
        ya, yb = -D2 - depth, -D2
        self.P.box((x0, ya, -40), (x1, yb, 0), {'top': M['FLOOR2'], 'side': M['PLANKS'], '-z': M['NODRAW']}, 'porch')
        for px in (x0 + 4, x1 - 4):
            self.P.box((px - 4, ya + 2, 0), (px + 4, ya + 10, 116), M['TRIM'], 'porchpost')
        # a lean-to roof sloping away from the wall
        p = [(yb, 128), (ya - 12, 112), (ya - 12, 120), (yb, 136)]
        vs, fs = prism(p, 'x', x0 - 8, x1 + 8)
        self.P.poly(vs, fs, self._roof_mats(vs, fs, -1), 'porchroof')
        if rails:
            for (a, b) in [(x0 + 8, door_c - 40), (door_c + 40, x1 - 8)]:
                if b - a > 16:
                    self.P.box((a, ya + 3, 30), (b, ya + 9, 34), M['TRIM'], 'porchrail')
                    for xx in range(int(a) + 12, int(b) - 4, 24):
                        self.P.box((xx - 1.5, ya + 4.5, 0), (xx + 1.5, ya + 7.5, 30), M['TRIM'], 'baluster')
        self.P.floor((x0 + 8, ya + 12, x1 - 8, yb), 0, 'porch')


# ---------------------------------------------------------------- the buildings (build 3: wide doors, few small pieces of
# furniture against the walls, 64-wide stairs with clear ends, a cellar under every house with a door to the sewer)
def farmhouse(plinth):
    """H1: two storeys of red brick, an attic and a cellar; 432 x 336."""
    h = House(432, 336, 2, M['BRICK'], [M['WALLPAPER1'], M['WALLPAPER2']], M['FLOOR'], M['TILE'], plinth=plinth,
              cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(-20, DOOR_W, 'door'), (-150, 48, 'window'), (110, 48, 'window')],
                      'N': [(110, DOOR_W, 'door')],
                      'W': [(-92, DOOR_W, 'door'), (40, 48, 'window')],
                      'E': [(120, 48, 'window')]})
    h.wall_y(44, h.iy0, h.iy1, 0, doors=[(0, DOOR_W)])
    # stairs up along the north wall of the living room, rising west; you step off them south at the top
    hole = h.stairs((-208, 96, -40, 160), 'x', -1, 0, SH, hole_level=SH)
    h.rail(-136, 88, hole[2], 96, SH)
    h.rail(hole[2], 96, hole[2] + 8, 160, SH)
    h.anchor(0, 0, 128, 'stairs up: foot')
    h.anchor(SH, -172, 52, 'stairs up: top')
    P.prop('barrel', (-130, 140, 0), 0)                   # under the high end of the stairs
    # cellar stairs along the east wall of the kitchen, down northwards from a landing at the south wall
    hole = h.cellar_stairs((144, -88, 208, 80), 'y', -1)
    h.rail(136, -40, 144, hole[3], 0)
    h.rail(136, hole[3], 208, hole[3] + 8, 0)
    h.anchor(0, 176, -124, 'cellar stairs: top')
    h.anchor(-SH, 176, 124, 'cellar stairs: foot')
    h.anchor(0, 0, -100, 'front door')
    h.anchor(0, 110, 120, 'back door')
    h.anchor(0, -170, -92, 'side door')
    h.anchor(0, 90, 0, 'kitchen door')
    h.anchor(0, -10, 0, 'living-room door')
    # living room and kitchen: a sofa, a table, a counter and a stove, all against the walls or in the open
    h.sofa(-190, -160, -110, -128, 0, '-y')
    h.table(-90, -10, 56, 40, 0)
    h.rug(-170, -80, -30, 40, 0)
    h.counter(88, -160, 136, -136, 0)
    h.stove(52, -160, 88, -124, 0)
    P.prop('barrel', (190, 140, 0), 0)
    h.light((-110, 0, SH - SLAB - 10))
    h.light((110, 20, SH - SLAB - 10))
    # first floor: two bedrooms
    h.outer_walls(1, {'S': [(-150, 48, 'window'), (-40, 48, 'window'), (110, 48, 'window')],
                      'N': [(-140, 48, 'window'), (90, 48, 'window')],
                      'W': [(-70, 48, 'window'), (60, 48, 'window')],
                      'E': [(128, 40, 'window')]})
    h.wall_y(44, h.iy0, h.iy1, 1, doors=[(-60, DOOR_W)])
    # stairs to the attic along the east wall, rising north towards the ridge, a deep first step at the wall
    hole = h.stairs((144, -160, 208, 8), 'y', +1, SH, 2 * SH, n=12, hole_level=2 * SH, first=34)
    h.rail(136, hole[1], 144, hole[3], 2 * SH)
    h.anchor(SH, 104, -126, 'attic stairs: foot')
    h.anchor(2 * SH, 176, 48, 'attic stairs: top')
    h.anchor(SH, 90, -60, 'bedroom door east')
    h.anchor(SH, -10, -60, 'bedroom door west')
    h.bed(-200, -150, -140, -70, SH, '-x')
    P.prop('dresser', (-110, -142, SH), 0)
    h.bed(52, 92, 112, 156, SH, '+y')
    h.light((-100, -20, 2 * SH - SLAB - 10))
    h.light((100, -40, 2 * SH - SLAB - 10))
    # attic: two crates under the ridge
    P.prop('crate', (-140, -30, 2 * SH), 15)
    P.prop('crate', (-100, -10, 2 * SH), 40)
    h.light((-40, 0, 2 * SH + 120), 600, 300)
    # cellar: three pieces along the walls; the sewer door in the east wall
    P.prop('crate_big', (-170, 120, -SH), 0)
    P.prop('barrel', (-180, -130, -SH), 0)
    P.prop('barrel2', (20, 136, -SH), 0)
    h.cellar_exit('E', -112)
    h.anchor(-SH, 160, -112, 'cellar: sewer door')
    h.light((-100, 0, -SLAB - 12), 700, 320)
    h.light((100, -40, -SLAB - 12), 700, 320)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof(gable_windows=[('W', 0, 48, 2 * SH + 40, 2 * SH + 100), ('E', -10, 40, 2 * SH + 44, 2 * SH + 100)],
           chimney=(-120, 60))
    P.probe((-224, -176, -SH - 8), (224, 176, h.zr))
    return h


def cottage(plinth):
    """H2: a white cottage under a thatched roof: one room, an attic and a cellar; 336 x 264."""
    h = House(336, 264, 1, M['STUCCO'], [M['PLASTER']], M['FLOOR2'], M['THATCH'], ceil=M['CEILWOOD'], plinth=plinth,
              cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(-30, DOOR_W, 'door'), (70, 48, 'window'), (128, 40, 'window'), (-120, 40, 'window')],
                      'N': [(-90, 48, 'window'), (70, 48, 'window')],
                      'W': [(80, 48, 'window')],
                      'E': [(-80, DOOR_W, 'door'), (80, 48, 'window')]})
    # stairs to the attic along the west wall, rising north, a deep first step at the wall
    hole = h.stairs((-160, -124, -96, 40), 'y', +1, 0, SH, n=12, hole_level=SH, first=34)
    h.rail(-96, hole[1], -88, 0, SH)
    h.anchor(0, -56, -90, 'attic stairs: foot')
    h.anchor(SH, -56, 22, 'attic stairs: top')
    # cellar stairs along the north wall, down eastwards
    hole = h.cellar_stairs((-72, 60, 96, 124), 'x', -1)
    h.rail(-40, 52, hole[2], 60, 0)
    h.rail(hole[2], 60, hole[2] + 8, 124, 0)
    h.anchor(0, -120, 92, 'cellar stairs: top')
    h.anchor(-SH, 126, 92, 'cellar stairs: foot')
    h.anchor(0, -30, -84, 'front door')
    h.anchor(0, 120, -80, 'side door')
    h.table(40, -30, 56, 40, 0)
    h.light((0, -40, SH - SLAB - 10))
    h.light((-40, 90, SH - SLAB - 10), 700, 300)
    P.prop('crate', (40, -20, SH), 25)
    P.prop('barrel', (100, -40, SH), 0)
    h.light((20, 0, SH + 110), 600, 300)
    P.prop('crate', (-120, -90, -SH), 10)
    P.prop('barrel', (120, -96, -SH), 0)
    h.cellar_exit('S', 0)
    h.anchor(-SH, 0, -84, 'cellar: sewer door')
    h.light((0, 0, -SLAB - 12), 700, 320)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof(gable_windows=[('W', 0, 48, SH + 40, SH + 100), ('E', 0, 48, SH + 40, SH + 100)])
    P.probe((-176, -140, -SH - 8), (176, 140, h.zr))
    return h


def cabin(plinth):
    """H3: a plank cabin with a metal roof, a porch across the front and a cellar; 304 x 232."""
    h = House(304, 232, 1, M['PLANKS'], [M['WOODWALL']], M['FLOOR2'], M['METALROOF'], ceil=M['CEILWOOD'],
              plinth=plinth, cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(-80, DOOR_W, 'door'), (20, 48, 'window'), (100, 48, 'window')],
                      'N': [(-90, DOOR_W, 'door'), (70, 48, 'window')],
                      'W': [(10, 48, 'window')],
                      'E': [(-40, 48, 'window'), (60, 40, 'window')]})
    # cellar stairs along the east wall, down northwards; you step on from the west at the south end
    hole = h.cellar_stairs((80, -108, 144, 60), 'y', -1)
    h.rail(72, -60, 80, hole[3], 0)
    h.rail(72, hole[3], 144, hole[3] + 8, 0)
    h.anchor(0, 40, -84, 'cellar stairs: top')
    h.anchor(-SH, 40, 40, 'cellar stairs: foot')
    h.anchor(0, -80, -76, 'front door')
    h.anchor(0, -90, 76, 'back door')
    h.table(-10, 40, 56, 40, 0)
    P.prop('barrel', (120, 90, 0), 0)
    h.light((-20, -20, SH - SLAB - 10))
    h.light((100, 70, SH - SLAB - 10), 600, 260)
    h.porch(-152, 152, 96, door_c=-80)
    P.prop('barrel2', (-120, 80, -SH), 0)
    h.cellar_exit('W', 0)
    h.anchor(-SH, -112, 0, 'cellar: sewer door')
    h.light((0, 0, -SLAB - 12), 700, 320)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof()
    P.probe((-168, -220, -SH - 8), (168, 132, h.zr))
    return h


def green_house(plinth):
    """H4: a green two-storey house with a cellar; 368 x 288. The cellar stairs run under the stairs up."""
    h = House(368, 288, 2, M['GREEN'], [M['WALLPAPER2'], M['PLASTER']], M['FLOOR'], M['METALROOF'], plinth=plinth,
              cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(40, DOOR_W, 'door'), (-120, 48, 'window'), (140, 48, 'window')],
                      'N': [(-120, 48, 'window'), (30, 48, 'window')],
                      'W': [(28, DOOR_W, 'door'), (-80, 48, 'window')],
                      'E': []})
    h.wall_y(-40, h.iy0, h.iy1, 0, doors=[(88, DOOR_W)])
    # stairs up along the east wall rising north (deep first step, you step on from the west); the cellar stairs run
    # under them, down southwards from the north end
    hole = h.stairs((112, -136, 176, 80), 'y', +1, 0, SH, hole_level=SH, first=34)
    h.rail(104, hole[1], 112, 40, SH)
    h.rail(104, hole[1] - 8, 176, hole[1], SH)
    hole2 = h.cellar_stairs((112, -88, 176, 80), 'y', +1)
    h.anchor(0, 76, -110, 'stairs up: foot')
    h.anchor(SH, 76, 92, 'stairs up: top')
    h.anchor(0, 76, 62, 'cellar stairs: top')
    h.anchor(-SH, 76, -108, 'cellar stairs: foot')
    h.anchor(0, 40, -100, 'front door')
    h.anchor(0, -140, 28, 'side door')
    h.anchor(0, -76, 88, 'partition door west')
    h.anchor(0, 0, 88, 'partition door east')
    h.sofa(-170, -132, -60, -104, 0, '-y')
    h.table(-100, -40, 64, 48, 0)
    h.counter(52, 112, 112, 136, 0)
    h.light((-100, 0, SH - SLAB - 10))
    h.light((50, 0, SH - SLAB - 10))
    h.outer_walls(1, {'S': [(-120, 48, 'window'), (-10, 48, 'window'), (110, 48, 'window')],
                      'N': [(-140, 48, 'window'), (40, 48, 'window'), (150, 32, 'window')],
                      'W': [(-60, 48, 'window'), (80, 48, 'window')],
                      'E': [(100, 40, 'window')]})
    h.wall_y(-40, h.iy0, h.iy1, 1, doors=[(-20, DOOR_W)])
    h.wall_x(72, h.ix0, -40, 1, doors=[(-110, DOOR_W)])        # a walk-in closet in the north-west corner
    h.anchor(SH, -76, -20, 'bedroom door west')
    h.anchor(SH, 0, -20, 'bedroom door east')
    h.anchor(SH, -110, 40, 'closet door')
    h.bed(-176, -132, -96, -60, SH, '-x')
    h.wardrobe(-30, -132, 0, -76, SH)
    h.bed(-36, 60, 28, 128, SH, '+y')
    P.prop('dresser', (40, -118, SH), 0)
    h.light((-100, -40, 2 * SH - SLAB - 10))
    h.light((60, 0, 2 * SH - SLAB - 10))
    P.prop('crate', (-120, 80, -SH), 0)
    h.cellar_exit('W', -100)
    h.anchor(-SH, -144, -100, 'cellar: sewer door')
    h.light((-60, 0, -SLAB - 12), 700, 320)
    h.light((80, 0, -SLAB - 12), 600, 300)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof(chimney=(-110, -50))
    P.probe((-192, -152, -SH - 8), (192, 152, h.zr))
    return h


def stone_bungalow(plinth):
    """H5: a stone bungalow with a tiled roof and a cellar; 336 x 288."""
    h = House(336, 288, 1, M['STONE'], [M['PLASTER']], M['FLOOR'], M['TILE'], plinth=plinth, cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(68, DOOR_W, 'door'), (-100, 48, 'window'), (140, 32, 'window')],
                      'N': [(-68, DOOR_W, 'door'), (60, 48, 'window'), (120, 40, 'window')],
                      'W': [(70, 48, 'window')],
                      'E': [(-80, 48, 'window'), (80, 48, 'window')]})
    h.wall_x(0, h.ix0, h.ix1, 0, doors=[(-12, DOOR_W)])
    # cellar stairs along the west wall of the front room, down northwards; the partition closes their north end
    hole = h.cellar_stairs((-160, -136, -96, 8), 'y', -1)
    h.rail(-96, -96, -88, hole[3], 0)
    h.anchor(0, -52, -104, 'cellar stairs: top')
    h.anchor(-SH, -128, 44, 'cellar stairs: foot')
    h.anchor(0, 68, -100, 'front door')
    h.anchor(0, -68, 100, 'back door')
    h.anchor(0, -12, -36, 'partition door south')
    h.anchor(0, -12, 36, 'partition door north')
    h.table(90, -40, 64, 48, 0)
    h.sofa(108, -132, 156, -104, 0, '-y')
    P.prop('bookshelf', (152, -30, 0), 180)
    h.bed(-156, 56, -108, 132, 0, '-x')
    h.wardrobe(128, 8, 156, 50, 0)
    P.prop('dresser', (20, 118, 0), 180)
    h.light((20, -70, SH - SLAB - 10))
    h.light((20, 70, SH - SLAB - 10))
    P.prop('crate_big', (60, 88, -SH), 0)
    P.prop('barrel', (100, -118, -SH), 0)
    h.cellar_exit('E', -60)
    h.anchor(-SH, 120, -60, 'cellar: sewer door')
    h.light((0, -60, -SLAB - 12), 700, 320)
    h.light((60, 90, -SLAB - 12), 700, 320)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof(chimney=(110, 60))
    P.probe((-176, -152, -SH - 8), (176, 152, h.zr))
    return h


def red_house(plinth):
    """H6: a narrow red two-storey house with a cellar; 264 x 232."""
    h = House(264, 232, 2, M['RED'], [M['WALLPAPER1'], M['PLASTER']], M['FLOOR2'], M['TILE'], plinth=plinth,
              cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(-10, DOOR_W, 'door'), (-80, 40, 'window'), (90, 40, 'window')],
                      'N': [(60, 48, 'window'), (-30, DOOR_W, 'door')],
                      'W': [],
                      'E': [(20, 48, 'window'), (-70, 40, 'window')]})
    # stairs up along the west wall rising north (deep first step, you step on from the east); at the top you step
    # off east
    hole = h.stairs((-124, -108, -60, 88), 'y', +1, 0, SH, hole_level=SH, first=34)
    h.rail(-60, hole[1], -52, 40, SH)
    h.rail(-124, hole[1] - 8, -52, hole[1], SH)
    h.anchor(0, -24, -76, 'stairs up: foot')
    h.anchor(SH, -20, 72, 'stairs up: top')
    # cellar stairs along the east wall, down northwards; you step on from the west at the south end
    hole = h.cellar_stairs((60, -108, 124, 60), 'y', -1)
    h.rail(52, -60, 60, hole[3], 0)
    h.rail(52, hole[3], 124, hole[3] + 8, 0)
    h.anchor(0, 20, -84, 'cellar stairs: top')
    h.anchor(-SH, 24, 40, 'cellar stairs: foot')
    h.anchor(0, -10, -76, 'front door')
    h.anchor(0, -30, 76, 'back door')
    h.counter(40, 76, 124, 100, 0)
    h.light((20, 0, SH - SLAB - 10))
    h.outer_walls(1, {'S': [(-60, 48, 'window'), (60, 48, 'window')],
                      'N': [(0, 48, 'window'), (90, 32, 'window')],
                      'W': [(-60, 48, 'window')],
                      'E': [(-40, 48, 'window'), (60, 48, 'window')]})
    h.wall_x(20, -52, h.ix1, 1, doors=[(20, DOOR_W)])
    h.anchor(SH, 20, -12, 'bedroom door south')
    h.anchor(SH, 20, 52, 'bedroom door north')
    h.bed(60, 40, 124, 104, SH, '+x')
    h.wardrobe(96, -108, 124, -80, SH)
    P.prop('dresser', (60, -90, SH), 0)
    h.light((20, -40, 2 * SH - SLAB - 10))
    h.light((60, 60, 2 * SH - SLAB - 10), 600, 260)
    P.prop('crate', (-90, 60, -SH), 0)
    P.prop('barrel', (-90, -80, -SH), 0)
    h.cellar_exit('S', 0)
    h.anchor(-SH, 0, -76, 'cellar: sewer door')
    h.light((0, 0, -SLAB - 12), 700, 320)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof(chimney=(80, -30))
    P.probe((-136, -120, -SH - 8), (136, 120, h.zr))
    return h


def tiny_house(plinth):
    """H7: a small yellow-brick house: one room and a cellar; 256 x 208."""
    h = House(256, 208, 1, M['BRICK2'], [M['WALLPAPER2']], M['FLOOR'], M['TILE'], plinth=plinth, cellar=True)
    P = h.P
    h.outer_walls(0, {'S': [(-40, DOOR_W, 'door'), (60, 48, 'window')],
                      'N': [(-10, DOOR_W, 'door'), (-80, 40, 'window')],
                      'W': [(0, 48, 'window')],
                      'E': [(-30, 48, 'window')]})
    # cellar stairs along the east wall, down northwards; you step on from the west at the south end
    hole = h.cellar_stairs((56, -96, 120, 72), 'y', -1)
    h.rail(48, -48, 56, hole[3], 0)
    h.rail(48, hole[3], 120, hole[3] + 8, 0)
    h.anchor(0, 20, -72, 'cellar stairs: top')
    h.anchor(-SH, 20, 56, 'cellar stairs: foot')
    h.anchor(0, -40, -64, 'front door')
    h.anchor(0, -10, 64, 'back door')
    h.bed(-116, 24, -52, 92, 0, '-x')
    P.prop('barrel', (-100, -76, 0), 0)
    h.light((-20, -10, SH - SLAB - 10))
    P.prop('barrel', (-90, 60, -SH), 0)
    h.cellar_exit('S', -10)
    h.anchor(-SH, -10, -64, 'cellar: sewer door')
    h.light((0, 0, -SLAB - 12), 600, 300)
    P.floor(h.cellar_inner, -SH, 'cellar')
    h.slabs()
    h.roof()
    P.probe((-128, -104, -SH - 8), (128, 104, h.zr))
    return h


def lean_wall(P, axis, c0, c1, a0, a1, zb, roof_z, mat, tag):
    """a wall whose top follows a lean-to roof: axis 'x' = wall in the y-z plane between x c0..c1 running y a0..a1;
    roof_z(a) gives the roof's underside along the run (the wall goes 6 into the roof slab)."""
    poly = [(a0, zb), (a1, zb), (a1, roof_z(a1) + 6), (a0, roof_z(a0) + 6)]
    vs, fs = prism(poly, axis, c0, c1)
    P.poly(vs, fs, [mat] * len(fs), tag)


def lean_roof(P, W2, y_back, z_back, y_front, z_front, th, mat, tag, over=16):
    p = [(y_back, z_back), (y_front, z_front), (y_front, z_front + th), (y_back, z_back + th)]
    vs, fs = prism(p, 'x', -W2 - over, W2 + over)
    cc = tuple(sum(v[i] for v in vs) / len(vs) for i in range(3))
    mats = []
    for f in fs:
        n = norm(newell(vs, f))
        c = tuple(sum(vs[i][k] for i in f) / len(f) for k in range(3))
        if dot(n, sub(c, cc)) < 0:
            n = (-n[0], -n[1], -n[2])
        mats.append(mat if abs(n[2]) > 0.3 else M['TRIM'])
    P.poly(vs, fs, mats, tag)


def barn_shed(z_sewer):
    """S1: an open-fronted barn: plank walls on three sides under a lean-to of corrugated metal, and a hatch with
    stairs down to the sewer along its west side; 320 x 224. z_sewer: the sewer's walkway in this part's frame."""
    P = Part()
    W2, D2 = 160, 112
    yb, zb_, yf, zf_ = D2 + 16, 168, -D2 - 24, 142          # roof underside: back and front edges

    def rz(y):
        return zb_ + (y - yb) * (zf_ - zb_) / (yf - yb)
    P.box((-W2 - 8, -D2 - 8, -60), (W2 + 8, D2 + 8, -4), {'top': M['DIRT'], 'side': M['COBBLE'], '-z': M['NODRAW']}, 'barnfloor')
    P.box((-W2, D2 - T, -4), (W2, D2, rz(D2 - T) + 6), M['PLANKS'], 'barnwall')
    for sx in (-1, 1):
        x0, x1 = (-W2, -W2 + T) if sx < 0 else (W2 - T, W2)
        lean_wall(P, 'x', x0, x1, -D2 + 40, -20, -4, rz, M['PLANKS'], 'barnwall')
        P.box((x0, -20, -4), (x1, 28, 40), M['PLANKS'], 'barnwall')
        lean_wall(P, 'x', x0, x1, -20, 28, 96, rz, M['PLANKS'], 'barnwall')
        lean_wall(P, 'x', x0, x1, 28, D2 - T, -4, rz, M['PLANKS'], 'barnwall')
    for px in (-W2 + 4, -40, 40, W2 - 4):
        P.box((px - 5, -D2 - 4, -4), (px + 5, -D2 + 6, rz(-D2 + 1) + 4), M['TRIM'], 'barnpost')
    lean_roof(P, W2, yb, zb_, yf, zf_, 8, M['CORR'], 'barnroof')
    # the hatch: stairs down southwards along the west wall, from the back to under the open front
    info = hatch(P, -120, D2 - T, 'y', -1, -4, z_sewer, tail=40)
    P.anchor(-4, -56, 76, 'hatch: top')
    P.anchor(-4, 20, -70, 'barn front')
    for (x, y, z, rot) in [(100, 76, 0, 0), (100, 76, 32, 0), (110, -40, 0, 1)]:
        hay_bale(P, x, y, z - 4, rot)
    P.prop('wheelbarrow', (40, 20, -4), 30)
    P.floor((-W2 + 12, -D2 + 12, W2 - 12, D2 - 12), -4, 'barn')
    P.light((0, 0, 130), 700, 320)
    P.probe((-W2 - 16, -D2 - 32, z_sewer - 32), (W2 + 16, D2 + 16, 180))
    return P


def tool_shed(z_sewer):
    """S2: a plank shed with a doorway, a window, and a hatch with stairs down to the sewer; 256 x 160."""
    P = Part()
    W2, D2 = 128, 80
    yb, zb_, yf, zf_ = D2 + 12, 140, -D2 - 16, 118

    def rz(y):
        return zb_ + (y - yb) * (zf_ - zb_) / (yf - yb)
    P.box((-W2 - 6, -D2 - 6, -60), (W2 + 6, D2 + 6, -2), {'top': M['FLOOR2'], 'side': M['COBBLE'], '-z': M['NODRAW']}, 'shedfloor')
    m = M['PLANKS']
    top_f = rz(-D2) + 6
    dx0, dx1 = -116, -116 + DOOR_W
    P.box((-W2, -D2, -2), (dx0, -D2 + T, top_f), m, 'shedwall')
    P.box((dx1, -D2, -2), (W2, -D2 + T, top_f), m, 'shedwall')
    P.box((dx0, -D2, DOOR_H), (dx1, -D2 + T, top_f), m, 'shedwall')
    P.box((-W2, D2 - T, -2), (W2, D2, rz(D2 - T) + 6), m, 'shedwall')
    lean_wall(P, 'x', -W2, -W2 + T, -D2 + T, D2 - T, -2, rz, m, 'shedwall')
    lean_wall(P, 'x', W2 - T, W2, -D2 + T, -20, -2, rz, m, 'shedwall')
    P.box((W2 - T, -20, -2), (W2, 28, WIN_SILL), m, 'shedwall')
    lean_wall(P, 'x', W2 - T, W2, -20, 28, WIN_HEAD, rz, m, 'shedwall')
    lean_wall(P, 'x', W2 - T, W2, 28, D2 - T, -2, rz, m, 'shedwall')
    lean_roof(P, W2, yb, zb_, yf, zf_, 8, M['CORR'], 'shedroof', over=12)
    # the hatch: stairs down westwards along the north wall, from the east end
    info = hatch(P, 104, 40, 'x', -1, -2, z_sewer, tail=10)
    P.anchor(-2, -80, -40, 'shed door')
    P.anchor(-2, 80, -24, 'hatch: top')
    P.prop('crate', (-90, 44, -2), 20)
    P.prop('barrel2', (-50, 50, -2), 0)
    P.light((0, 0, 100), 500, 240)
    P.probe((-W2 - 8, -D2 - 24, z_sewer - 32), (W2 + 8, D2 + 16, 150))
    return P


def hay_bale(P, x, y, z, rot=0):
    """a 64 x 32 x 32 straw bale centred on (x, y), lying from z; rot 1 turns it 90 degrees."""
    a, b = (32, 16) if rot == 0 else (16, 32)
    P.box((x - a, y - b, z), (x + a, y + b, z + 32), M['STRAW'], 'hay')


# ---------------------------------------------------------------- the site plan
# buildings: (key, builder, centre x, y, quarter turns CCW (front faces: 0 south, 1 east, 2 north, 3 west), footprint W, D,
# extra front depth (porch), cellar)
SITES = [
    ('H1 farmhouse', 'farmhouse', -800, 760, 0, 432, 336, 0, True),
    ('H2 cottage', 'cottage', 120, 980, 0, 336, 264, 0, True),
    ('H3 cabin', 'cabin', 900, 880, 0, 304, 232, 96, True),
    ('H4 green house', 'green_house', 860, -560, 3, 368, 288, 0, True),
    ('H5 stone bungalow', 'stone_bungalow', -880, -560, 1, 336, 288, 0, True),
    ('H6 red house', 'red_house', -160, -1000, 2, 264, 232, 0, True),
    ('H7 tiny house', 'tiny_house', 1080, 120, 3, 256, 208, 0, True),
    ('S1 barn', 'barn_shed', -1080, 330, 1, 320, 224, 24, True),
    ('S2 tool shed', 'tool_shed', 560, -1080, 0, 256, 160, 0, True),
]
# ponds: (x, y, radius, depth of the bowl under the pond's ground level; the water stands 10 under that level, or
# lower where a flat reaches the pond: see build_layout). The bank's steepest slope is 1.5 x 0.55 = 0.83 (40 degrees): a
# player walks out anywhere.
PONDS = [(60, 200, 310, 170), (300, -700, 240, 140), (-620, -1030, 230, 126)]
MOUNDS = [(-260, -400, 220, 34), (620, -180, 210, 28), (-600, 280, 190, 24), (-220, 620, 200, 22)]
PATHS = [
    [(-820, 560), (-780, 400), (-460, 330), (-140, 500), (40, 760), (90, 840)],
    [(-140, 500), (220, 560), (420, 760), (720, 700), (880, 650)],
    [(-130, -880), (-220, -640), (-480, -520), (-700, -540)],
    [(700, -560), (420, -440), (60, -340), (-230, -150), (-460, 330)],
    [(950, 120), (760, 140), (520, 260)],
]


def site_rect(site):
    """the world rectangle a building's plinth covers (with its porch), and the terrain hole for cellars."""
    _, _, cx, cy, k, W, D, front, cellar = site
    lx0, ly0, lx1, ly1 = -W / 2 - 8, -D / 2 - 8 - front, W / 2 + 8, D / 2 + 8
    pts = [rot_pt(x, y, k) for x in (lx0, lx1) for y in (ly0, ly1)]
    r = (min(p[0] for p in pts) + cx, min(p[1] for p in pts) + cy, max(p[0] for p in pts) + cx, max(p[1] for p in pts) + cy)
    if cellar:
        r = (math.floor(r[0] / GRID) * GRID, math.floor(r[1] / GRID) * GRID, math.ceil(r[2] / GRID) * GRID,
             math.ceil(r[3] / GRID) * GRID)
    return r


def world_to_local_rect(r, cx, cy, k):
    pts = [rot_pt(x - cx, y - cy, -k % 4) for x in (r[0], r[2]) for y in (r[1], r[3])]
    return (min(p[0] for p in pts), min(p[1] for p in pts), max(p[0] for p in pts), max(p[1] for p in pts))


# ---------------------------------------------------------------- terrain
def raw_height(x, y):
    h = 9 * math.sin(x / 290 + 0.6) * math.cos(y / 350 - 0.3) + 6 * math.sin((x + 0.6 * y) / 470 + 1.1) \
        + 4 * math.cos((x - 1.7 * y) / 380 - 0.5)
    for cx, cy, r, a in MOUNDS:
        h += a * math.exp(-((x - cx) ** 2 + (y - cy) ** 2) / (r * r))
    e = max(abs(x), abs(y)) - (HALF - 256)
    if e > 0:
        h += 24 * smooth(e / 256)
    dx, dy = max(0.0, abs(x) - HALF), max(0.0, abs(y) - HALF)
    d = math.hypot(dx, dy)
    if d > 0:
        h += 290 * smooth(d / 560) + 36 * math.sin(x / 170 + y / 230) * smooth(d / 300)
    return h


FLATS = []        # (rect, ground level)
POND_LVL = []     # ground level per pond


def rect_dist(x, y, r):
    dx = max(r[0] - x, 0, x - r[2])
    dy = max(r[1] - y, 0, y - r[3])
    return math.hypot(dx, dy)


def height(x, y):
    h = raw_height(x, y)
    for (cx, cy, R, D), lvl in zip(PONDS, POND_LVL):
        r = math.hypot(x - cx, y - cy)
        if r < 1.6 * R:
            w = 1 - smooth((r - 0.9 * R) / (0.7 * R))
            h = h * (1 - w) + lvl * w
            if r < R:
                h -= D * smooth((R - r) / (D / 0.55))
    for rect, g in FLATS:
        d = rect_dist(x, y, rect)
        if d < 200:
            w = 1 - smooth((d - 32) / 168)
            h = h * (1 - w) + g * w
    return h


class Terrain:
    def __init__(self, holes):
        self.n = int(2 * OUTER / GRID)
        self.h = [[height(-OUTER + i * GRID, -OUTER + j * GRID) for j in range(self.n + 1)] for i in range(self.n + 1)]
        self.holes = holes
        self.cell_on = [[not any(r[0] <= -OUTER + i * GRID and -OUTER + (i + 1) * GRID <= r[2] and
                                 r[1] <= -OUTER + j * GRID and -OUTER + (j + 1) * GRID <= r[3] for r in holes)
                         for j in range(self.n)] for i in range(self.n)]

    def diag(self, i, j):
        return (i + j) % 2 == 0      # True: split a-c, else b-d

    def z(self, x, y):
        """exact height of the triangulated surface at (x, y)."""
        fx, fy = (x + OUTER) / GRID, (y + OUTER) / GRID
        i, j = min(self.n - 1, max(0, int(fx))), min(self.n - 1, max(0, int(fy)))
        u, v = fx - i, fy - j
        a, b, c, d = self.h[i][j], self.h[i + 1][j], self.h[i + 1][j + 1], self.h[i][j + 1]
        if self.diag(i, j):        # triangles (a,b,c) and (a,c,d)
            return a + u * (b - a) + v * (c - b) if u >= v else a + u * (c - d) + v * (d - a)
        # triangles (a,b,d) and (b,c,d)
        return a + u * (b - a) + v * (d - a) if u + v <= 1 else c + (1 - u) * (d - c) + (1 - v) * (b - c)

    def zmax(self, x, y, r=16):
        return max(self.z(x + dx, y + dy) for dx in (-r, 0, r) for dy in (-r, 0, r))

    def zmin(self, x, y, r=16):
        return min(self.z(x + dx, y + dy) for dx in (-r, 0, r) for dy in (-r, 0, r))

    def material(self, x, y):
        for (cx, cy, R, D) in PONDS:
            if math.hypot(x - cx, y - cy) < 0.92 * R:
                return M['MUD']
        if max(abs(x), abs(y)) > HALF + 40:
            return M['GRASS2']
        for path in PATHS:
            for (a, b) in zip(path, path[1:]):
                if seg_dist(x, y, a, b) < 30:
                    return M['DIRT']
        return M['GRASS']

    def build(self, L, chunk=16):
        n = self.n
        vnorm = {}
        for i in range(n + 1):
            for j in range(n + 1):
                hx = (self.h[min(n, i + 1)][j] - self.h[max(0, i - 1)][j]) / (GRID * (min(n, i + 1) - max(0, i - 1)))
                hy = (self.h[i][min(n, j + 1)] - self.h[i][max(0, j - 1)]) / (GRID * (min(n, j + 1) - max(0, j - 1)))
                vnorm[(i, j)] = norm((-hx, -hy, 1.0))
        for ci in range(0, n, chunk):
            for cj in range(0, n, chunk):
                cells = [(i, j) for i in range(ci, min(n, ci + chunk)) for j in range(cj, min(n, cj + chunk))
                         if self.cell_on[i][j]]
                if not cells:
                    continue
                cs = set(cells)
                vid = {}
                verts, vn = [], []

                def V(i, j, top):
                    key = (i, j, top)
                    if key not in vid:
                        vid[key] = len(verts)
                        x, y = -OUTER + i * GRID, -OUTER + j * GRID
                        verts.append((x, y, self.h[i][j] if top else self.h[i][j] - SHELL))
                        vn.append(vnorm[(i, j)] if top else (0, 0, -1))
                    return vid[key]
                faces, mats, sm, lms = [], [], [], []
                outside = all(max(abs(-OUTER + (i + 0.5) * GRID), abs(-OUTER + (j + 0.5) * GRID)) > HALF + 64 for i, j in cells)
                for (i, j) in cells:
                    a, b, c, d = V(i, j, 1), V(i + 1, j, 1), V(i + 1, j + 1, 1), V(i, j + 1, 1)
                    tris = [(a, b, c), (a, c, d)] if self.diag(i, j) else [(a, b, d), (b, c, d)]
                    for t in tris:
                        cx = sum(verts[q][0] for q in t) / 3
                        cy = sum(verts[q][1] for q in t) / 3
                        faces.append(list(t))
                        mats.append(self.material(cx, cy))
                        sm.append(True)
                        lms.append(-1 if outside else 0)
                    # the shell's underside follows the surface SHELL lower: the same triangles, turned over
                    ab, bb, cb, db = V(i, j, 0), V(i + 1, j, 0), V(i + 1, j + 1, 0), V(i, j + 1, 0)
                    btris = [(ab, cb, bb), (ab, db, cb)] if self.diag(i, j) else [(ab, db, bb), (bb, db, cb)]
                    for t in btris:
                        faces.append(list(t))
                        mats.append(M['NODRAW'])
                        sm.append(False)
                        lms.append(-2)
                    # sides where the neighbour cell is missing
                    for (di, dj, e0, e1) in [(0, -1, (i, j), (i + 1, j)), (1, 0, (i + 1, j), (i + 1, j + 1)),
                                             (0, 1, (i + 1, j + 1), (i, j + 1)), (-1, 0, (i, j + 1), (i, j))]:
                        if (i + di, j + dj) not in cs:
                            t0, t1 = V(e0[0], e0[1], 1), V(e1[0], e1[1], 1)
                            b0, b1 = V(e0[0], e0[1], 0), V(e1[0], e1[1], 0)
                            faces.append([b0, b1, t1, t0])
                            mats.append(M['NODRAW'])
                            sm.append(False)
                            lms.append(-2)
                L.add_solid(verts, faces, mats, 'terrain', lm=lms, wind='given', vn=vn, smooth=sm, block=False)


def seg_dist(x, y, a, b):
    ax, ay = a
    bx, by = b
    vx, vy = bx - ax, by - ay
    l2 = vx * vx + vy * vy
    t = 0 if l2 == 0 else max(0, min(1, ((x - ax) * vx + (y - ay) * vy) / l2))
    return math.hypot(x - (ax + t * vx), y - (ay + t * vy))


# ---------------------------------------------------------------- outdoor pieces
def rock(L, x, y, z, r, rng, tag='rock'):
    """a squashed, jittered icosahedron half buried at (x, y, z)."""
    t = (1 + 5 ** 0.5) / 2
    base = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
            (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    faces = [[0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11], [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6],
             [7, 1, 8], [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9], [4, 9, 5], [2, 4, 11], [6, 2, 10],
             [8, 6, 7], [9, 8, 1]]
    yaw = rng.uniform(0, math.pi)
    sx, sy, sz = r * rng.uniform(0.8, 1.2), r * rng.uniform(0.7, 1.0), r * rng.uniform(0.45, 0.7)
    vs = []
    for (a, b, c) in base:
        l = math.sqrt(a * a + b * b + c * c)
        j = rng.uniform(0.85, 1.12)
        a, b, c = a / l * j, b / l * j, c / l * j
        a, b = a * math.cos(yaw) - b * math.sin(yaw), a * math.sin(yaw) + b * math.cos(yaw)
        vs.append((x + a * sx, y + b * sy, z + c * sz))
    L.add_solid(vs, faces, [M['ROCK']] * len(faces), tag)


def haystack(L, x, y, z, r=56, h=64):
    """a round haystack: an octagonal straw stack with a pointed top."""
    n = 8
    ring = [(x + r * math.cos(2 * math.pi * k / n), y + r * math.sin(2 * math.pi * k / n)) for k in range(n)]
    ring2 = [(x + (r - 6) * math.cos(2 * math.pi * k / n), y + (r - 6) * math.sin(2 * math.pi * k / n)) for k in range(n)]
    vs = [(p[0], p[1], z - 8) for p in ring] + [(p[0], p[1], z + h) for p in ring2] + [(x, y, z + h + 0.75 * r)]
    faces = [list(range(n - 1, -1, -1))]
    for k in range(n):
        k2 = (k + 1) % n
        faces.append([k, k2, n + k2, n + k])
        faces.append([n + k, n + k2, 2 * n])
    L.add_solid(vs, faces, [M['STRAW']] * len(faces), 'haystack')


def fence_line(L, pts, ter, h=42, post_every=112, tag='fence'):
    """posts and two rails along a polyline at ground level."""
    for a, b in zip(pts, pts[1:]):
        dx, dy = b[0] - a[0], b[1] - a[1]
        length = math.hypot(dx, dy)
        n = max(1, round(length / post_every))
        posts = [(a[0] + dx * k / n, a[1] + dy * k / n) for k in range(n + 1)]
        for (px, py) in posts:
            gz = ter.zmin(px, py, 4)
            L.box((px - 3, py - 3, gz - 12), (px + 3, py + 3, gz + h), M['TRIM'], tag)
        horiz = abs(dx) >= abs(dy)
        for (p, q) in zip(posts, posts[1:]):
            g0, g1 = ter.z(*p), ter.z(*q)
            for rz in (16, 34):
                zz = (g0 + g1) / 2 + rz
                if horiz:
                    L.box((min(p[0], q[0]), p[1] - 1.5, zz - 2.5), (max(p[0], q[0]), p[1] + 1.5, zz + 2.5), M['TRIM'], tag)
                else:
                    L.box((p[0] - 1.5, min(p[1], q[1]), zz - 2.5), (p[0] + 1.5, max(p[1], q[1]), zz + 2.5), M['TRIM'], tag)


def stone_wall(L, a, b, ter, h=38, w=20):
    dx, dy = b[0] - a[0], b[1] - a[1]
    n = max(1, round(math.hypot(dx, dy) / 64))
    for k in range(n):
        p = (a[0] + dx * k / n, a[1] + dy * k / n)
        q = (a[0] + dx * (k + 1) / n, a[1] + dy * (k + 1) / n)
        g = min(ter.zmin(*p, 8), ter.zmin(*q, 8))
        hh = h + (k * 7 % 5) - 2
        if abs(dx) >= abs(dy):
            L.box((min(p[0], q[0]), p[1] - w / 2, g - 16), (max(p[0], q[0]), p[1] + w / 2, g + hh), M['STONE'], 'stonewall')
        else:
            L.box((p[0] - w / 2, min(p[1], q[1]), g - 16), (p[0] + w / 2, max(p[1], q[1]), g + hh), M['STONE'], 'stonewall')


def trailer(L, x, y, ter, yaw_k=0):
    """a farm trailer: plank bed on two wheels with low sides and a drawbar (axis-aligned, along x or y)."""
    P = Part()
    P.box((-90, -40, 22), (90, 40, 28), {'side': M['PLANKS'], 'top': M['PLANKS']}, 'trailer')
    P.box((-90, -40, 28), (90, -34, 52), M['PLANKS'], 'trailer')
    P.box((-90, 34, 28), (90, 40, 52), M['PLANKS'], 'trailer')
    P.box((84, -34, 28), (90, 34, 52), M['PLANKS'], 'trailer')
    for sy in (-1, 1):
        P.box((-16, sy * 44 - 4, 0), (16, sy * 44 + 4, 30), M['CORR'], 'wheel')
    P.box((90, -3, 18), (150, 3, 24), M['CORR'], 'drawbar')
    P.box((-90, -40, 0), (-84, -34, 22), M['TRIM'], 'trailer')
    P.box((-90, 34, 0), (-84, 40, 22), M['TRIM'], 'trailer')
    hay_bale(P, -40, 0, 28, 1)
    hay_bale(P, 30, -2, 28, 1)
    gz = ter.zmin(x, y, 60)
    P.place(L, x, y, gz, yaw_k, 'trailer')


def woodpile(L, x, y, ter, along='y', length=128):
    g = ter.zmin(x, y, 40)
    if along == 'y':
        L.box((x - 16, y - length / 2, g - 4), (x + 16, y + length / 2, g + 44), {'side': M['FURN'], 'top': M['FURN']}, 'woodpile')
        L.box((x - 20, y - length / 2 - 4, g + 44), (x + 20, y + length / 2 + 4, g + 50), M['CORR'], 'woodpile')
    else:
        L.box((x - length / 2, y - 16, g - 4), (x + length / 2, y + 16, g + 44), {'side': M['FURN'], 'top': M['FURN']}, 'woodpile')
        L.box((x - length / 2 - 4, y - 20, g + 44), (x + length / 2 + 4, y + 20, g + 50), M['CORR'], 'woodpile')


def well(L, x, y, ter):
    g = ter.zmin(x, y, 40)
    n = 8
    for k in range(n):
        a0, a1 = 2 * math.pi * k / n, 2 * math.pi * (k + 1) / n
        ro, ri = 34, 26
        p = [(x + ri * math.cos(a0), y + ri * math.sin(a0)), (x + ro * math.cos(a0), y + ro * math.sin(a0)),
             (x + ro * math.cos(a1), y + ro * math.sin(a1)), (x + ri * math.cos(a1), y + ri * math.sin(a1))]
        vs, fs = prism(p, 'z', g - 24, g + 36)
        L.add_solid(vs, fs, [M['COBBLE']] * len(fs), 'well')
    L.box((x - 30, y - 3, g + 36), (x - 26, y + 3, g + 100), M['TRIM'], 'well')
    L.box((x + 26, y - 3, g + 36), (x + 30, y + 3, g + 100), M['TRIM'], 'well')
    p = [(-40, g + 96), (0, g + 122), (40, g + 96), (40, g + 102), (0, g + 128), (-40, g + 102)]
    p = [(-40, g + 96), (40, g + 96), (0, g + 122)]
    vs, fs = prism([(-44, g + 98), (44, g + 98), (44, g + 104), (0, g + 128), (-44, g + 104)], 'y', y - 30, y + 30)
    vs = [(v[0] + x, v[1], v[2]) for v in vs]
    L.add_solid(vs, fs, [M['PLANKS']] * len(fs), 'well')
    L.box((x - 34, y - 34, g - 24), (x + 34, y + 34, g - 2), M['MUD'], 'well')


# ---------------------------------------------------------------- stairs down (hatches) and the sewer (build 3)
def hatch(P, x0, y0, axis, sign, z_top, z_bot, width=64, run=14, tail=0, mat=None, wall=None):
    """Straight stairs down from a floor (z_top) to the sewer's walkway (z_bot), built in a Part (local frame).
    The top edge's centre is (x0, y0); the stairs descend along axis ('x'|'y') in direction sign. Solid steps, side walls
    and a ceiling that steps down with them; the air above each step (80 tall: a 72-unit player and 8 to spare, reaching
    36 ahead over the lower steps) is registered for carving, which opens the floor above the top steps. tail: a level
    corridor after the last step. Returns the steps' extent and the end of the tail."""
    mat = mat or M['CONCRETE']
    wall = wall or M['SEWERWALL']
    n = max(1, math.ceil((z_top - z_bot) / 12 - 1e-9))
    rise = (z_top - z_bot) / n
    w2 = width / 2
    start = y0 if axis == 'y' else x0
    c = x0 if axis == 'y' else y0

    def rect(a, b, c0, c1):
        lo, hi = min(a, b), max(a, b)
        return (c0, lo, c1, hi) if axis == 'y' else (lo, c0, hi, c1)
    steps = []
    for k in range(1, n + 1):
        a, b = start + sign * run * (k - 1), start + sign * run * k
        top = z_top - rise * k
        r = rect(a, b, c - w2, c + w2)
        P.box((r[0], r[1], z_bot - 16), (r[2], r[3], top), {'top': mat, 'side': mat, '-z': M['NODRAW']}, 'step')
        steps.append((a, b, top))
        # side walls (8 thick) and, below the floor structure, a ceiling 128 over the step (clear of the air reaching
        # 36 ahead from the steps behind)
        wt = min(top + 144, z_top)
        for (c0, c1) in ((c - w2 - 8, c - w2), (c + w2, c + w2 + 8)):
            rr = rect(a, b, c0, c1)
            P.box((rr[0], rr[1], z_bot - 16), (rr[2], rr[3], wt), {'side': wall, 'top': M['TRIM'], '-z': M['NODRAW']}, 'hatchwall')
        if top + 144 <= z_top - 12:
            rr = rect(a, b, c - w2 - 8, c + w2 + 8)
            P.box((rr[0], rr[1], top + 128), (rr[2], rr[3], top + 144), {'side': wall, 'bottom': M['CONCRETE'], 'top': M['NODRAW']}, 'hatchceil')
        # air: over the step, and 36 ahead over the lower steps; it starts 1 under the tread, so whatever it cuts
        # (a floor, a pad) ends inside the step instead of leaving a lip on it
        fwd = b + sign * 36
        ra = rect(a, fwd, c - w2, c + w2)
        P.air((ra[0], ra[1], top - 1), (ra[2], ra[3], top + HEAD), 'hatch')
    end = start + sign * run * n
    if tail > 0:
        a, b = end, end + sign * tail
        r = rect(a, b, c - w2, c + w2)
        P.box((r[0], r[1], z_bot - 16), (r[2], r[3], z_bot), {'top': mat, 'side': mat, '-z': M['NODRAW']}, 'step')
        for (c0, c1) in ((c - w2 - 8, c - w2), (c + w2, c + w2 + 8)):
            rr = rect(a, b, c0, c1)
            P.box((rr[0], rr[1], z_bot - 16), (rr[2], rr[3], z_bot + TUN_H + 16), {'side': wall, 'top': M['NODRAW']}, 'hatchwall')
        rr = rect(a, b, c - w2 - 8, c + w2 + 8)
        P.box((rr[0], rr[1], z_bot + TUN_H), (rr[2], rr[3], z_bot + TUN_H + 16), {'side': wall, 'bottom': M['CONCRETE'], 'top': M['NODRAW']}, 'hatchceil')
        P.air((r[0], r[1], z_bot + 1), (r[2], r[3], z_bot + TUN_H), 'hatch')
        end = b
    # the floor opening: steps whose air reaches the floor surface
    open_n = sum(1 for (_, _, top) in steps if top + HEAD > z_top)
    tend = (c, end) if axis == 'y' else (end, c)
    P.exit_pt(tend[0], tend[1], z_bot, (0, sign) if axis == 'y' else (sign, 0), 'hatch')
    return {'n': n, 'rise': rise, 'start': start, 'end': end, 'open_len': open_n * run + 36, 'c': c}


def box_minus(lo, hi, clo, chi):
    """the parts of box [lo, hi] outside box [clo, chi]."""
    if not all(lo[i] < chi[i] and clo[i] < hi[i] for i in range(3)):
        return [(list(lo), list(hi))]
    out = []
    lo, hi = list(lo), list(hi)
    for ax in range(3):
        if clo[ax] > lo[ax]:
            b_lo, b_hi = lo[:], hi[:]
            b_hi[ax] = clo[ax]
            out.append((b_lo, b_hi))
            lo[ax] = clo[ax]
        if chi[ax] < hi[ax]:
            b_lo, b_hi = lo[:], hi[:]
            b_lo[ax] = chi[ax]
            out.append((b_lo, b_hi))
            hi[ax] = chi[ax]
    return [(a, b) for (a, b) in out if all(b[i] - a[i] > 0.5 for i in range(3))]


NO_CARVE = ('terrain', 'skybox', 'clip')


def carve(L):
    """Cut every walking space (L.airs) out of the box solids it crosses: floor openings over stairs, doors through the
    cellar walls, tunnel mouths in junction walls. Returns what was cut, per tag."""
    cut = {}
    for (clo, chi, why) in L.airs:
        new = []
        for s in L.solids:
            if not s.get('box') or s['tag'] in NO_CARVE or s['tag'].endswith('/step') or s['tag'] == 'step':
                new.append(s)
                continue
            lo, hi = s['lo'], s['hi']
            if not all(lo[i] < chi[i] - 0.01 and clo[i] < hi[i] - 0.01 for i in range(3)):
                new.append(s)
                continue
            mats = {k: s['mats'][i] for i, k in enumerate(FK)}
            cut[s['tag']] = cut.get(s['tag'], 0) + 1
            for (a, b) in box_minus(lo, hi, clo, chi):
                m = mats6(mats)
                new.append({'verts': box_verts(a, b), 'faces': BOX_FACES, 'mats': [m[k] for k in FK], 'tag': s['tag'],
                            'lm': s['lm'], 'bake': s['bake'], 'lo': a, 'hi': b, 'block': s['block'], 'box': True})
        L.solids = new
    return cut


def seg_boxes(a, b, half, z0, z1):
    """axis-aligned box along a segment a->b (2D), half-width half, from z0 to z1, ends extended by half."""
    if abs(a[0] - b[0]) >= abs(a[1] - b[1]):
        x0, x1 = min(a[0], b[0]) - half, max(a[0], b[0]) + half
        return (x0, a[1] - half, z0), (x1, a[1] + half, z1)
    y0, y1 = min(a[1], b[1]) - half, max(a[1], b[1]) + half
    return (a[0] - half, y0, z0), (a[0] + half, y1, z1)


def corridor(L, pts, z_from, W, tag, width=80, h=96, lights=True):
    """A walkable corridor along an axis-aligned polyline at the sewer level W: brick walls, concrete floor and ceiling.
    It starts at z_from (a cellar floor or a hatch's foot) and ramps to W over its first 96 units; its last segment
    reaches 16 into the wall it meets, so carving opens that wall."""
    half = width / 2
    wall = M['SEWERWALL']
    segs = []
    a, b = pts[0], pts[1]
    d = math.hypot(b[0] - a[0], b[1] - a[1])
    if abs(z_from - W) > 1:
        f = min(1.0, 96 / d)
        m = (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)
        segs.append((a, m, True))
        if f < 1:
            segs.append((m, b, False))
    else:
        segs.append((a, b, False))
    for i in range(1, len(pts) - 1):
        segs.append((pts[i], pts[i + 1], False))
    segs = [sg for sg in segs if math.hypot(sg[1][0] - sg[0][0], sg[1][1] - sg[0][1]) > 0.5]
    top_r = max(z_from, W) + h
    zlow = min(z_from, W)

    def dirn(sg):
        dx, dy = sg[1][0] - sg[0][0], sg[1][1] - sg[0][1]
        return (1 if dx > 0 else -1, 0) if abs(dx) >= abs(dy) else (0, 1 if dy > 0 else -1)
    for i, (a, b, is_ramp) in enumerate(segs):
        dv = dirn((a, b))
        horiz = dv[1] == 0
        prev_same = i > 0 and dirn(segs[i - 1][:2]) == dv
        next_same = i < len(segs) - 1 and dirn(segs[i + 1][:2]) == dv
        e0 = 0 if (i == 0 or prev_same) else half
        e1 = 16 if i == len(segs) - 1 else (0 if next_same else half)
        u0 = (a[0] if horiz else a[1]) - (e0 if (dv[0] + dv[1]) > 0 else -e0)
        u1 = (b[0] if horiz else b[1]) + (e1 if (dv[0] + dv[1]) > 0 else -e1)
        lo_u, hi_u = min(u0, u1), max(u0, u1)
        c = a[1] if horiz else a[0]
        ceil = top_r if is_ramp else W + h
        floor_lo = zlow if is_ramp else W

        def B(ul, uh, vl, vh, z0, z1):
            return ((ul, c + vl, z0), (uh, c + vh, z1)) if horiz else ((c + vl, ul, z0), (c + vh, uh, z1))
        if is_ramp:
            ua, ub = (a[0], b[0]) if horiz else (a[1], b[1])
            poly = [(ua, zlow - 16), (ub, zlow - 16), (ub, W), (ua, z_from)]
            vs, fs = prism(poly, 'y' if horiz else 'x', c - half - 16, c + half + 16)
            L.add_solid(vs, fs, [M['CONCRETE']] * len(fs), tag + '/ramp')
        else:
            L.box(*B(lo_u, hi_u, -half - 16, half + 16, W - 16, W), {'top': M['CONCRETE'], 'side': wall, '-z': M['NODRAW']}, tag + '/floor')
        L.box(*B(lo_u, hi_u, -half - 16, -half, floor_lo - 16, ceil + 16), {'side': wall, 'top': M['NODRAW']}, tag + '/wall')
        L.box(*B(lo_u, hi_u, half, half + 16, floor_lo - 16, ceil + 16), {'side': wall, 'top': M['NODRAW']}, tag + '/wall')
        L.box(*B(lo_u, hi_u, -half - 16, half + 16, ceil, ceil + 16), {'side': wall, 'bottom': M['CONCRETE'], 'top': M['NODRAW']}, tag + '/ceil')
        L.airs.append((*B(lo_u, hi_u, -half, half, floor_lo + 1, ceil), tag))
        if is_ramp and top_r > W + h + 0.5 and i < len(segs) - 1:
            # where the ramp's higher ceiling meets the level corridor's: close the step with a header
            ub = b[0] if horiz else b[1]
            hu0, hu1 = (ub, ub + 16) if (dv[0] + dv[1]) > 0 else (ub - 16, ub)
            L.box(*B(hu0, hu1, -half - 16, half + 16, W + h, top_r + 16), {'side': wall, 'top': M['NODRAW']}, tag + '/header')
        if lights and not is_ramp and hi_u - lo_u > 96:
            # a light at least every 200 along the corridor, so no stretch of it is out of reach of one
            m = max(1, math.ceil((hi_u - lo_u) / 200))
            for k in range(m):
                u = lo_u + (hi_u - lo_u) * (k + 0.5) / m
                p = (u, c) if horiz else (c, u)
                L.lights.append({'origin': (p[0], p[1], W + h - 12), 'lumens': 260, 'range': 260})
    L.sewer_paths.append({'tag': tag, 'pts': [list(p) for p in pts], 'z_from': z_from, 'width': width})


def junction(L, c, W, tag):
    """a square chamber where tunnels meet: 224 x 224 inside, 128 tall, a light in the middle."""
    half, h, wall = JUN / 2, TUN_H + 16, M['SEWERWALL']
    x0, y0, x1, y1 = c[0] - half, c[1] - half, c[0] + half, c[1] + half
    L.box((x0 - 16, y0 - 16, W - 32), (x1 + 16, y1 + 16, W), {'top': M['CONCRETE'], 'side': wall, '-z': M['NODRAW']}, tag + '/floor')
    for (lo, hi) in [((x0 - 16, y0 - 16, W), (x1 + 16, y0, W + h + 16)), ((x0 - 16, y1, W), (x1 + 16, y1 + 16, W + h + 16)),
                     ((x0 - 16, y0, W), (x0, y1, W + h + 16)), ((x1, y0, W), (x1 + 16, y1, W + h + 16))]:
        L.box(lo, hi, {'side': wall, 'top': M['NODRAW']}, tag + '/wall')
    L.box((x0 - 16, y0 - 16, W + h), (x1 + 16, y1 + 16, W + h + 16), {'side': wall, 'bottom': M['CONCRETE'], 'top': M['NODRAW']}, tag + '/ceil')
    # a pillar in the middle: cover to circle round
    L.box((c[0] - 20, c[1] - 20, W), (c[0] + 20, c[1] + 20, W + h), {'side': M['BRICK'], 'top': M['NODRAW'], 'bottom': M['NODRAW']}, tag + '/pillar')
    L.airs.append(((x0, y0, W + 1), (x1, y1, W + h), tag))
    L.lights.append({'origin': (c[0] + 60, c[1] + 60, W + h - 14), 'lumens': 450, 'range': 340})
    L.lights.append({'origin': (c[0] - 60, c[1] - 60, W + h - 14), 'lumens': 450, 'range': 340})
    L.floors.append({'rect': (x0, y0, x1, y1), 'z': W, 'tag': tag + '/sewer'})


def tunnel(L, a, b, W, tag):
    """a main tunnel between two junction centres: 176 wide inside (walkways of 64 either side of a 48-wide channel,
    16 deep, with shallow water), 112 tall over the walkways."""
    half, h, wall = TUN_W / 2, TUN_H, M['SEWERWALL']
    jh = JUN / 2
    horiz = abs(b[0] - a[0]) >= abs(b[1] - a[1])
    if horiz:
        s0, s1 = min(a[0], b[0]) + jh, max(a[0], b[0]) - jh
        c = a[1]
        R = lambda u0, u1, v0, v1, z0, z1: ((u0, c + v0, z0), (u1, c + v1, z1))
    else:
        s0, s1 = min(a[1], b[1]) + jh, max(a[1], b[1]) - jh
        c = a[0]
        R = lambda u0, u1, v0, v1, z0, z1: ((c + v0, u0, z0), (c + v1, u1, z1))
    cw = CHAN / 2
    for (v0, v1) in ((-half, -cw), (cw, half)):
        L.box(*R(s0, s1, v0, v1, W - 32, W), {'top': M['CONCRETE'], 'side': M['CONCRETE'], '-z': M['NODRAW']}, tag + '/walkway')
    L.box(*R(s0, s1, -cw, cw, W - 32, W - 16), {'top': M['COBBLE'], 'side': M['COBBLE'], '-z': M['NODRAW']}, tag + '/channel')
    L.box(*R(s0, s1, -half - 16, -half, W - 32, W + h + 16), {'side': wall, 'top': M['NODRAW']}, tag + '/wall')
    L.box(*R(s0, s1, half, half + 16, W - 32, W + h + 16), {'side': wall, 'top': M['NODRAW']}, tag + '/wall')
    L.box(*R(s0, s1, -half - 16, half + 16, W + h, W + h + 16), {'side': wall, 'bottom': M['CONCRETE'], 'top': M['NODRAW']}, tag + '/ceil')
    lo, hi = R(s0 - 17, s1 + 17, -half, half, W + 1, W + h)
    L.airs.append((lo, hi, tag))
    lo, hi = R(s0 - 17, s1 + 17, -cw, cw, W - 15, W + 1)
    L.airs.append((lo, hi, tag + '/channel'))
    # shallow water in the channel, in pieces of at most 256 (a long thin water hull fails to build)
    n = max(1, math.ceil((s1 - s0) / 256))
    for k in range(n):
        u0, u1 = s0 + (s1 - s0) * k / n, s0 + (s1 - s0) * (k + 1) / n
        lo, hi = R(u0, u1, -cw, cw, W - 64, W - 6)
        L.channel_waters.append((lo, hi))
    # lights along the ceiling, at least every 256
    m = max(1, math.ceil((s1 - s0) / 256))
    for k in range(m):
        u = s0 + (s1 - s0) * (k + 0.5) / m
        p = (u, c) if horiz else (c, u)
        L.lights.append({'origin': (p[0], p[1], W + h - 12), 'lumens': 320, 'range': 300})
    L.tunnel_segs.append({'tag': tag, 'a': list(a), 'b': list(b), 'horiz': horiz})


def manhole(L, ter, x, y, axis, sign, W, tag):
    """stairs down from the field into the sewer: a hole in the terrain with a stone curb round it, the stairs going
    down along axis, and their tail. The terrain round it is flattened to the curb (FLATS, set up beforehand)."""
    g = MANHOLE_LEVEL[tag]
    P = Part()
    info = hatch(P, 0, 0, axis, sign, 0, W - g, tail=24, wall=M['SEWERWALL'])
    # curb and pad: cover the rest of the terrain hole, a low wall round the opening except its top end
    hr = MANHOLE_HOLE[tag]
    lo_open = info['start'], info['start'] + sign * info['open_len']
    if axis == 'x':
        op = (min(lo_open) + x, y - 40, max(lo_open) + x, y + 40)
    else:
        op = (x - 40, min(lo_open) + y, x + 40, max(lo_open) + y)
    for r in rect_minus(hr, [op]):
        L.box((r[0], r[1], g - SHELL - 16), (r[2], r[3], g), {'top': M['COBBLE'], 'side': M['COBBLE'], '-z': M['NODRAW']}, tag + '/pad')
    # curb: 24 tall round the opening, open at the top end (where you step in)
    o0, o1 = op[:2], op[2:]
    if axis == 'x':
        top_end = op[0] if sign > 0 else op[2]
        L.box((op[0], op[1] - 8, g), (op[2], op[1], g + 24), M['COBBLE'], tag + '/curb')
        L.box((op[0], op[3], g), (op[2], op[3] + 8, g + 24), M['COBBLE'], tag + '/curb')
        far = op[2] if sign > 0 else op[0] - 8
        L.box((far, op[1] - 8, g), (far + 8, op[3] + 8, g + 24), M['COBBLE'], tag + '/curb')
    else:
        L.box((op[0] - 8, op[1], g), (op[0], op[3], g + 24), M['COBBLE'], tag + '/curb')
        L.box((op[2], op[1], g), (op[2] + 8, op[3], g + 24), M['COBBLE'], tag + '/curb')
        far = op[3] if sign > 0 else op[1] - 8
        L.box((op[0] - 8, far, g), (op[2] + 8, far + 8, g + 24), M['COBBLE'], tag + '/curb')
    P.place(L, x, y, g, 0, tag)
    return info


# ---------------------------------------------------------------- the sewer's plan (world coordinates)
JUNCTIONS = {'NW': (-400, 680), 'N1': (120, 680), 'NE': (540, 680), 'E1': (540, 120), 'SE': (540, -260),
             'S1': (-160, -260), 'SW': (-400, -260), 'W1': (-400, 210)}
TUNNELS = [('NW', 'N1'), ('N1', 'NE'), ('NE', 'E1'), ('E1', 'SE'), ('SE', 'S1'), ('S1', 'SW'), ('SW', 'W1'), ('W1', 'NW')]
# manholes on the field: (tag, top x, top y, axis, direction, spoke waypoints after the tail)
# (a hatch's tail always runs on 56 in its own direction before these; MH1 and MH2 reach their tunnel that way; MH3
# runs on 14 more, to the outer face of the red house's corridor wall, so its end opens that wall)
MANHOLES = [('MH1', -760, 400, 'x', +1, []),
            ('MH2', 860, 300, 'x', -1, []),
            ('MH3', -520, -470, 'x', +1, [(-216, None)]),
            ('MH4', 420, 540, 'x', -1, [(None, 576)])]
# how each building's sewer door reaches a junction: waypoints after the door (or the hatch's tail)
SPOKES = {'H1': [(-528, None)], 'H2': [(None, 808)], 'H3': [(600, None), (600, 808)], 'H4': [(None, -260), (668, -260)],
          'H5': [(None, -260), (-528, -260)], 'H6': [(None, -388)], 'H7': [(668, None)], 'S1': [(-528, None)],
          'S2': [(60, None), (None, -780), (-104, None)]}
MANHOLE_LEVEL = {}
MANHOLE_HOLE = {}


def route(start, way):
    """waypoints with None meaning 'keep that coordinate of the previous point'."""
    pts = [tuple(start)]
    for (x, y) in way:
        px, py = pts[-1]
        pts.append((px if x is None else x, py if y is None else y))
    return pts


# ---------------------------------------------------------------- the map
def sewer_level():
    """the sewer's walkway: low enough that every tunnel, junction and corridor stays under the terrain shell (whose
    underside is 24 under the surface), sampled along the whole network."""
    worst = []
    for (a, b) in TUNNELS:
        pa, pb = JUNCTIONS[a], JUNCTIONS[b]
        for k in range(33):
            x, y = pa[0] + (pb[0] - pa[0]) * k / 32, pa[1] + (pb[1] - pa[1]) * k / 32
            for dx in (-104, 0, 104):
                for dy in (-104, 0, 104):
                    worst.append(height(x + dx, y + dy) - SHELL - TUN_H - 8)
    for c in JUNCTIONS.values():
        for dx in (-128, 0, 128):
            for dy in (-128, 0, 128):
                worst.append(height(c[0] + dx, c[1] + dy) - SHELL - TUN_H - 16 - 8)
    return int(math.floor(min(worst) / 4) * 4)


def build_layout():
    L = Layout()
    rng = random.Random(20261031)
    # ground levels of the buildings and the ponds; every building stands in a hole in the terrain shell (its cellar or
    # its hatch goes through the shell)
    for s in SITES:
        r = site_rect(s)
        g = round(raw_height(s[2], s[3]))
        FLATS.append((r, g))
    for (cx, cy, R, D) in PONDS:
        POND_LVL.append(round(raw_height(cx, cy)))
    # manholes: their level, the hole for the opening, a flat round them
    for (tag, x, y, axis, sign, way) in MANHOLES:
        g = round(raw_height(x, y))
        MANHOLE_LEVEL[tag] = g
    W = sewer_level()
    L.sewer_W = W
    holes = [site_rect(s) for s in SITES]
    for (tag, x, y, axis, sign, way) in MANHOLES:
        g = MANHOLE_LEVEL[tag]
        n = max(1, math.ceil((g - W) / 12 - 1e-9))
        open_n = sum(1 for k in range(1, n + 1) if g - (g - W) / n * k + HEAD > g)
        length = open_n * 14 + 36
        if axis == 'x':
            r = (min(x, x + sign * length), y - 40, max(x, x + sign * length), y + 40)
        else:
            r = (x - 40, min(y, y + sign * length), x + 40, max(y, y + sign * length))
        hr = (math.floor((r[0] - 24) / GRID) * GRID, math.floor((r[1] - 24) / GRID) * GRID,
              math.ceil((r[2] + 24) / GRID) * GRID, math.ceil((r[3] + 24) / GRID) * GRID)
        MANHOLE_HOLE[tag] = hr
        holes.append(hr)
        FLATS.append((hr, g))
    ter = Terrain(holes)
    ter.build(L)

    # buildings
    builders = {'farmhouse': farmhouse, 'cottage': cottage, 'cabin': cabin, 'green_house': green_house,
                'stone_bungalow': stone_bungalow, 'red_house': red_house, 'tiny_house': tiny_house,
                'barn_shed': barn_shed, 'tool_shed': tool_shed}
    for s, (rect, g) in zip(SITES, FLATS):
        name, key, cx, cy, k, Wd, Dd, front, cellar = s
        tag = name.split()[0]
        if key in ('barn_shed', 'tool_shed'):
            zoff = g + 4
            obj = builders[key](W - zoff)
        else:
            zoff = g + 12
            obj = builders[key](world_to_local_rect(rect, cx, cy, k))
        part = obj.P if isinstance(obj, House) else obj
        part.place(L, cx, cy, zoff, k, tag)
        L.houses.append({'name': name, 'center': (cx, cy), 'rot': k, 'size': (Wd, Dd), 'ground': g, 'floor_z': zoff,
                         'storeys': obj.n if isinstance(obj, House) else 1, 'cellar': isinstance(obj, House),
                         'rect': rect})

    # ponds: deep enough to swim in the middle, banks you can walk out on (see PONDS). The water is two bodies: a layer
    # 24 deep over the whole pond (the shallows, out to the banks), and under it a core down to the bowl's bottom, only
    # as wide as the bowl is deeper than that layer (plus 12), so no water hangs under the banks' thin shell, where a
    # sewer corridor may run
    # The water stands 10 under the pond's level, and at least 6 under every flat (a building's or a manhole's) whose
    # blend reaches the pond, so no flattened shore lies level with the water (MH4's flat reaches pond 1's bank).
    for (cx, cy, R, D), lvl in zip(PONDS, POND_LVL):
        z1 = lvl - 10
        for rect, g in FLATS:
            if rect_dist(cx, cy, rect) < R + 200:
                z1 = min(z1, g - 6)
        z_mid = z1 - 24
        r = R
        while r > 0 and D * smooth((R - r) / (D / 0.55)) < lvl - z_mid - 4:
            r -= 2
        L.waters.append({'center': (cx, cy), 'r': R, 'n': 24, 'z0': lvl - D - 30, 'z_mid': z_mid, 'z1': z1,
                         'depth': D, 'r_core': min(R, r + 12)})

    # ------------------------------------------------ the sewer
    for name, c in JUNCTIONS.items():
        junction(L, c, W, 'sewer/' + name)
    for (a, b) in TUNNELS:
        tunnel(L, JUNCTIONS[a], JUNCTIONS[b], W, f'sewer/{a}-{b}')
    def spoke(ex, way):
        start = (ex['x'], ex['y'])
        if ex['why'] == 'hatch':
            # run on straight out of the hatch's tail first, so a turn is a proper corner
            start2 = (start[0] + ex['dir'][0] * 56, start[1] + ex['dir'][1] * 56)
            pts = route(start, [start2] + list(way))
        else:
            pts = route(start, way)
        corridor(L, pts, ex['z'], W, f"sewer/{ex['bld']}", width=80, h=96)
    for ex in list(L.exits):
        if ex['bld'] in SPOKES:
            spoke(ex, SPOKES[ex['bld']])
    for (tag, x, y, axis, sign, way) in MANHOLES:
        manhole(L, ter, x, y, axis, sign, W, tag)
    for (tag, x, y, axis, sign, way) in MANHOLES:
        spoke([e for e in L.exits if e['bld'] == tag][0], way)
    L.carved = carve(L)
    # no walking space may reach into a pond's water
    for w in L.waters:
        cx, cy = w['center']
        for (lo, hi, why) in L.airs:
            d = rect_dist(cx, cy, (lo[0], lo[1], hi[0], hi[1]))
            if (d < w['r_core'] and lo[2] < w['z_mid'] and hi[2] > w['z0']) or (d < w['r'] and lo[2] < w['z1'] and hi[2] > w['z_mid']):
                raise SystemExit(f"walking space {why} {lo}-{hi} reaches into the pond at {w['center']}")

    # ------------------------------------------------ nature: tree clusters in the field (no tree, bush, hedge or
    # grass has any collision: you walk through all of them)
    def free(x, y, pad=40, pond_pad=24):
        if max(abs(x), abs(y)) > HALF - 60:
            return False
        if any(rect_dist(x, y, f[0]) < pad for f in FLATS):
            return False
        if any(math.hypot(x - p[0], y - p[1]) < p[2] + pond_pad for p in PONDS):
            return False
        return True

    def tree(key, x, y, scale=1.0, yaw=None):
        gz = ter.zmin(x, y, 24) - 4
        L.prop(key, (x, y, gz), rng.uniform(0, 360) if yaw is None else yaw, scale, 'tree')

    def bush(key, x, y, scale=1.0):
        gz = ter.zmin(x, y, 20) - 3
        L.prop(key, (x, y, gz), rng.uniform(0, 360), scale, 'bush')

    clusters = [
        ((-400, 760), [('poplar', -40, 60, 0.9), ('poplar', 70, -40, 0.8), ('maple1', -120, -120, 0.55), ('poplar_s', 120, 90, 1.2)], 4),
        ((620, 60), [('maple2', 0, 0, 0.6), ('oak', 140, 160, 0.8), ('poplar_s', -40, 200, 1.1)], 3),
        ((-1060, -1060), [('poplar', 0, 0, 0.9), ('poplar', 150, 60, 0.75), ('oak', 60, 190, 0.7)], 3),
        ((1060, -1050), [('maple1', 0, 0, 0.55), ('poplar', -150, 90, 0.8)], 3),
        ((-430, -170), [('oak', 0, 0, 0.85), ('oak', -40, 230, 0.7), ('maple2', 160, -120, 0.5)], 5),
        ((580, 1130), [('poplar', 0, 0, 0.85), ('poplar', 140, -40, 0.7), ('sumac', -110, -20, 1.0)], 3),
        ((-300, 440), [('maple1', 0, 0, 0.5)], 2),
        ((-260, -820), [('oak', 0, 0, 0.75), ('sumac', 90, 80, 0.9)], 2),
        ((1150, 560), [('poplar', 0, 0, 0.8), ('poplar_s', -60, -120, 1.0)], 2),
        ((40, -1150), [('poplar_s', 0, 0, 1.2), ('sumac', 120, 40, 1.0)], 3),
        # build 3: more clusters
        ((-700, 120), [('poplar', 0, 0, 0.85), ('poplar_s', 80, 70, 1.1), ('sumac', -70, -60, 0.9)], 4),
        ((-150, 1180), [('maple2', 0, 0, 0.55), ('poplar_s', 140, 30, 1.0)], 3),
        ((760, 1180), [('poplar', 0, 0, 0.8), ('oak', -120, -20, 0.7)], 2),
        ((1180, 330), [('poplar', 0, 0, 0.75), ('sumac', -40, 110, 1.0)], 3),
        ((1180, -260), [('maple1', 0, 0, 0.5), ('poplar_s', -90, 60, 1.1)], 3),
        ((700, -1180), [('poplar', 0, 0, 0.85), ('poplar', 130, 60, 0.7), ('sumac', -110, 40, 0.9)], 3),
        ((-560, -1150), [('oak', 0, 0, 0.8), ('poplar_s', 130, 30, 1.1)], 3),
        ((-1200, -700), [('poplar', 0, 0, 0.8), ('sumac', 30, 130, 1.0)], 2),
        ((-1180, 640), [('maple2', 0, 0, 0.5), ('poplar_s', 60, -120, 1.0)], 3),
        ((-640, 1180), [('poplar', 0, 0, 0.8), ('poplar', 150, -20, 0.75)], 2),
        ((360, 900), [('sumac', 0, 0, 1.1), ('poplar_s', 90, 70, 1.0)], 3),
        ((900, 300), [('oak', 0, 0, 0.7), ('poplar_s', 80, -100, 1.1)], 3),
        ((-760, -260), [('poplar_s', 0, 0, 1.2), ('sumac', 70, -70, 1.0)], 3),
        ((-40, -620), [('maple1', 0, 0, 0.5), ('poplar_s', -90, 80, 1.0)], 3),
        ((620, -620), [('poplar', 0, 0, 0.8)], 3),
    ]
    trees_field = 0
    bush_pts = []
    for (cx, cy), trees, nb in clusters:
        for (key, dx, dy, sc) in trees:
            x, y = cx + dx, cy + dy
            if not free(x, y, pad=60, pond_pad=40):
                continue
            tree(key, x, y, sc)
            trees_field += 1
        for b in range(nb):
            a = rng.uniform(0, 2 * math.pi)
            r = rng.uniform(90, 220)
            bush_pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    # single trees scattered over the field
    for k in range(60):
        if trees_field >= 62:
            break
        x, y = rng.uniform(-HALF + 120, HALF - 120), rng.uniform(-HALF + 120, HALF - 120)
        if not free(x, y, pad=90, pond_pad=60):
            continue
        tree(rng.choice(['poplar_s', 'poplar_s', 'sumac', 'poplar']), x, y, rng.uniform(0.75, 1.15))
        trees_field += 1
    # bushes along house walls and in corners, plus many more scattered: places to hide in
    bush_pts += [(-580, 640), (-1040, 960), (-560, 940), (300, 880), (-70, 860), (720, 1000), (1080, 760),
                 (700, -720), (1020, -380), (-700, -420), (-1060, -760), (-20, -1080), (-320, -900), (1180, 10),
                 (960, 280), (620, -1150), (-1150, 160), (-900, 420), (360, 360), (-60, -420), (240, -560)]
    for k in range(70):
        bush_pts.append((rng.uniform(-HALF + 90, HALF - 90), rng.uniform(-HALF + 90, HALF - 90)))
    n_bush = 0
    for (x, y) in bush_pts:
        if not free(x, y, pad=30, pond_pad=16):
            continue
        bush(rng.choice(['dogwood', 'dogwood', 'dogwood2', 'dogwood2', 'viburnum', 'huckle']), x, y, rng.uniform(0.9, 1.35))
        n_bush += 1
    # reeds and tall grass at the ponds' edges, tall grass and flowers in the meadow
    n_grass = 0
    for (cx, cy, R, D) in PONDS:
        for k in range(26):
            a = 2 * math.pi * k / 26 + rng.uniform(-0.12, 0.12)
            r = R * rng.uniform(0.96, 1.12)
            x, y = cx + r * math.cos(a), cy + r * math.sin(a)
            if any(rect_dist(x, y, f[0]) < 20 for f in FLATS):
                continue
            L.prop('tallgrass', (x, y, ter.zmin(x, y, 10) - 2), rng.uniform(0, 360), rng.uniform(1.1, 1.7), 'grass')
            n_grass += 1
    for k in range(320):
        x, y = rng.uniform(-HALF + 80, HALF - 80), rng.uniform(-HALF + 80, HALF - 80)
        if not free(x, y, pad=20, pond_pad=8):
            continue
        # tall grass in clumps of three
        for j in range(3 if rng.random() < 0.5 else 1):
            xx, yy = x + rng.uniform(-40, 40), y + rng.uniform(-40, 40)
            L.prop(rng.choice(['tallgrass', 'tallgrass', 'tallgrass', 'flower']), (xx, yy, ter.zmin(xx, yy, 8) - 2),
                   rng.uniform(0, 360), rng.uniform(1.0, 1.7), 'grass')
            n_grass += 1

    # rocks at the ponds and in the clusters
    for (cx, cy, R, D) in PONDS:
        for k in range(4):
            a = rng.uniform(0, 2 * math.pi)
            r = R * rng.uniform(1.04, 1.18)
            x, y = cx + r * math.cos(a), cy + r * math.sin(a)
            if any(rect_dist(x, y, f[0]) < 60 for f in FLATS):
                continue
            rock(L, x, y, ter.zmin(x, y, 20), rng.uniform(18, 34), rng)
    for (x, y, r) in [(-290, -140, 40), (-600, -230, 30), (720, 40, 36), (-1180, -980, 44), (1180, -940, 40),
                      (380, 1200, 34), (-200, 1180, 30), (200, -1180, 38), (-660, 500, 26), (840, 200, 30)]:
        if free(x, y, pad=40, pond_pad=30):
            rock(L, x, y, ter.zmin(x, y, r), r, rng)

    # ------------------------------------------------ farm things
    for (x, y, r, h) in [(-640, 520, 56, 64), (-520, 300, 48, 56), (-760, 200, 60, 70), (160, -470, 52, 60),
                         (-180, 600, 50, 58), (900, -60, 54, 62)]:
        if free(x, y, pad=60, pond_pad=40):
            haystack(L, x, y, ter.zmin(x, y, r), r, h)
    bales = Part()
    for (x, y, z, k) in [(0, 0, 0, 0), (64, 0, 0, 0), (128, 0, 0, 0), (32, 0, 32, 0), (96, 0, 32, 0)]:
        hay_bale(bales, x, y, z, k)
    for (x, y, k) in [(-1100, 0, 0), (-60, -470, 1), (-420, -40, 0), (380, -200, 0)]:
        bales.place(L, x, y, ter.zmin(x + 64, y, 80) - 2, k, 'bales')
    trailer(L, -880, 90, ter, 0)
    woodpile(L, 680, 900, ter, 'y', 128)
    woodpile(L, -960, -330, ter, 'x', 112)
    well(L, 200, 690, ter)
    for (key, x, y, yaw) in [('van', -430, 480, 100), ('sedan1', 590, -300, 92), ('hatch', -100, -780, 160),
                             ('sedan2', 900, 500, 200), ('sedan1', -1160, -260, 10)]:
        L.prop(key, (x, y, ter.zmin(x, y, 80) - 1), yaw, 1.0, 'vehicle')

    # hedges: garden lines, L-corners and more rows in the field (passable: they hide you, they do not stop you)
    n_hedge = [0]

    def hedge_run(a, b, gaps=()):
        dx, dy = b[0] - a[0], b[1] - a[1]
        n = max(1, round(math.hypot(dx, dy) / 128))
        yaw = math.degrees(math.atan2(dy, dx))
        for k in range(n):
            if k in gaps:
                continue
            x, y = a[0] + dx * (k + 0.5) / n, a[1] + dy * (k + 0.5) / n
            if not free(x, y, pad=24, pond_pad=8):
                continue
            L.prop('hedge', (x, y, ter.zmin(x, y, 40) - 3), yaw, 1.0, 'hedge')
            n_hedge[0] += 1
    hedge_run((-60, 760), (330, 760), gaps=(0,))
    hedge_run((-80, 640), (-80, 840))
    hedge_run((-300, 680), (-300, 520))
    hedge_run((-300, 680), (-140, 680))
    hedge_run((440, -380), (440, -560))
    hedge_run((440, -380), (600, -380))
    hedge_run((-700, -300), (-460, -300), gaps=(1,))
    hedge_run((-640, 120), (-640, -60))
    hedge_run((720, 400), (900, 400))
    hedge_run((40, -1150), (40, -880))
    # build 3: more rows and corners
    hedge_run((-1000, -160), (-760, -160), gaps=(1,))
    hedge_run((-1240, 560), (-1240, 820))
    hedge_run((260, 1240), (560, 1240), gaps=(1,))
    hedge_run((1240, 460), (1240, 700))
    hedge_run((1000, -900), (1240, -900))
    hedge_run((-360, -1240), (-60, -1240), gaps=(1,))
    hedge_run((820, -200), (820, -40))
    hedge_run((820, -40), (980, -40))
    hedge_run((-860, 1220), (-560, 1220))
    hedge_run((-1100, -880), (-940, -880))
    hedge_run((-940, -880), (-940, -1040))

    # fences: the farmyard paddock, the cabin's yard, the tiny house's garden
    fence_line(L, [(-1240, 40), (-1240, -120), (-560, -120), (-560, 0)], ter)
    fence_line(L, [(-640, 160), (-640, 300)], ter)
    fence_line(L, [(740, 1180), (1200, 1180), (1200, 700)], ter)
    fence_line(L, [(940, 380), (1220, 380)], ter)
    fence_line(L, [(940, -140), (1220, -140)], ter)
    stone_wall(L, (300, 1260), (-40, 1260), ter)
    stone_wall(L, (1260, -700), (1260, -1000), ter)

    # ------------------------------------------------ the edge: a fence on the boundary, the clip, hills and a forest
    e = HALF - 16
    fence_line(L, [(-e, -e), (e, -e), (e, e), (-e, e), (-e, -e)], ter, post_every=128, tag='boundary')
    for lo, hi in [((-HALF - 32, HALF, T_BOTTOM), (HALF + 32, HALF + 32, SKY_TOP)),
                   ((-HALF - 32, -HALF - 32, T_BOTTOM), (HALF + 32, -HALF, SKY_TOP)),
                   ((HALF, -HALF, T_BOTTOM), (HALF + 32, HALF, SKY_TOP)),
                   ((-HALF - 32, -HALF, T_BOTTOM), (-HALF, HALF, SKY_TOP))]:
        L.box(lo, hi, M['CLIP'], 'clip', block=False)
    forest = 0
    placed = []
    for k in range(600):
        if forest >= 70:
            break
        side = rng.randrange(4)
        u = rng.uniform(-OUTER + 80, OUTER - 80)
        d = rng.uniform(HALF + 110, OUTER - 70)
        x, y = [(u, -d), (d, u), (u, d), (-d, u)][side]
        if any(math.hypot(x - p[0], y - p[1]) < 170 for p in placed):
            continue
        placed.append((x, y))
        key = rng.choice(['poplar', 'poplar', 'poplar', 'maple1', 'maple2', 'oak', 'cypress', 'poplar_s'])
        sc = {'maple1': 0.6, 'maple2': 0.6, 'cypress': 0.75}.get(key, 1.0) * rng.uniform(0.8, 1.05)
        tree(key, x, y, sc)
        forest += 1
    cards = 0
    for k in range(400):
        if cards >= 56:
            break
        side = rng.randrange(4)
        u = rng.uniform(-OUTER + 60, OUTER - 60)
        d = rng.uniform(OUTER - 260, OUTER - 40)
        x, y = [(u, -d), (d, u), (u, d), (-d, u)][side]
        if any(math.hypot(x - p[0], y - p[1]) < 110 for p in placed):
            continue
        placed.append((x, y))
        yaw = math.degrees(math.atan2(-y, -x)) - 90
        L.prop(rng.choice(['card1', 'card2', 'card3']), (x, y, ter.zmin(x, y, 30) - 6), yaw, rng.uniform(1.0, 1.4), 'card')
        cards += 1

    # sealed skybox shell round the terrain and everything under it
    S, t = OUTER, 32
    for lo, hi in [((-S - t, -S - t, T_BOTTOM - 64), (S + t, S + t, T_BOTTOM - 32)),
                   ((-S - t, -S - t, SKY_TOP), (S + t, S + t, SKY_TOP + t)),
                   ((-S - t, S, T_BOTTOM - 32), (S + t, S + t, SKY_TOP)), ((-S - t, -S - t, T_BOTTOM - 32), (S + t, -S, SKY_TOP)),
                   ((S, -S, T_BOTTOM - 32), (S + t, S, SKY_TOP)), ((-S - t, -S, T_BOTTOM - 32), (-S, S, SKY_TOP))]:
        L.box(lo, hi, M['SKYBOX'], 'skybox', block=False)

    L.stats = {'trees_field': trees_field, 'trees_forest': forest, 'tree_cards': cards, 'bushes': n_bush,
               'grass': n_grass, 'hedges': n_hedge[0]}
    L.ter = ter
    return L


# ---------------------------------------------------------------- spawns
def overlaps3(lo, hi, a, b):
    return all(lo[i] < b[i] and a[i] < hi[i] for i in range(3))


def pick_spawns(L, fixed=None):
    ter = L.ter
    blockers = [(s['lo'], s['hi']) for s in L.solids if s.get('block', True)]
    for p in L.props:
        bb = prop_aabb(p, 'phys', 8)
        if bb is None and p['tag'] in ('bush', 'tree'):
            bb = prop_aabb(p, 'render', 0)
            if bb and p['tag'] == 'tree':      # a tree without collision: keep clear of its trunk only
                c = p['origin']
                bb = ([c[0] - 30, c[1] - 30, c[2]], [c[0] + 30, c[1] + 30, c[2] + 200])
        if bb:
            blockers.append(bb)
    # a coarse grid of blockers for speed
    cell = 256
    grid = {}
    for i, (lo, hi) in enumerate(blockers):
        for gx in range(int(math.floor(lo[0] / cell)), int(math.floor(hi[0] / cell)) + 1):
            for gy in range(int(math.floor(lo[1] / cell)), int(math.floor(hi[1] / cell)) + 1):
                grid.setdefault((gx, gy), []).append(i)

    def clear(x, y, z, pad=8):
        lo = (x - 16 - pad, y - 16 - pad, z + 2)
        hi = (x + 16 + pad, y + 16 + pad, z + 74)
        for i in grid.get((int(math.floor(x / cell)), int(math.floor(y / cell))), []) + \
                grid.get((int(math.floor((x - 40) / cell)), int(math.floor((y - 40) / cell))), []) + \
                grid.get((int(math.floor((x + 40) / cell)), int(math.floor((y + 40) / cell))), []) + \
                grid.get((int(math.floor((x - 40) / cell)), int(math.floor((y + 40) / cell))), []) + \
                grid.get((int(math.floor((x + 40) / cell)), int(math.floor((y - 40) / cell))), []):
            if overlaps3(lo, hi, blockers[i][0], blockers[i][1]):
                return False
        return True

    outdoor, indoor = [], []
    for x in range(-HALF + 112, HALF - 111, 48):
        for y in range(-HALF + 112, HALF - 111, 48):
            if any(math.hypot(x - p[0], y - p[1]) < p[2] + 24 for p in PONDS):
                continue
            if any(rect_dist(x, y, f[0]) < 48 for f in FLATS):
                continue
            z0, z1 = ter.zmin(x, y), ter.zmax(x, y)
            if z1 - z0 > 10:
                continue
            if clear(x, y, z1):
                outdoor.append((x, y, z1))
    for f in L.floors:
        r, z = f['rect'], f['z']
        if f['tag'].endswith('porch'):
            continue
        for x in range(int(r[0]) + 24, int(r[2]) - 23, 16):
            for y in range(int(r[1]) + 24, int(r[3]) - 23, 16):
                if clear(x, y, z, pad=10):
                    indoor.append((x, y, z, f['tag'].split('/')[0]))

    if fixed:
        # keep an earlier build's spawn points (generate.py --spawns-from): each must still be clear here, and an indoor
        # one must still stand on floor; any that is not is replaced by the farthest valid point of its kind
        def on_floor(x, y, z):
            return any(abs(f['z'] - z) < 0.5 and f['rect'][0] + 16 <= x <= f['rect'][2] - 16 and
                       f['rect'][1] + 16 <= y <= f['rect'][3] - 16 for f in L.floors)
        def ground(x, y):
            # an outdoor point: out of the ponds and off the building flats, on level ground; its height, or None
            if any(math.hypot(x - p[0], y - p[1]) < p[2] + 24 for p in PONDS):
                return None
            if any(rect_dist(x, y, f[0]) < 48 for f in FLATS):
                return None
            z0, z1 = ter.zmin(x, y), ter.zmax(x, y)
            return z1 if z1 - z0 <= 10 else None
        keep, lost = [], []
        for sp in fixed:
            cls, x, y, z, where = sp
            if where == 'in':
                ok = clear(x, y, z, pad=10) and on_floor(x, y, z)
            else:
                gz = ground(x, y)
                ok = gz is not None and clear(x, y, gz)
                if ok:
                    z = gz
            (keep if ok else lost).append((cls, x, y, z, where))
        L.spawns_replaced = []
        for (cls, x, y, z, where) in lost:
            pool = [p for p in (indoor if where == 'in' else outdoor) if where != 'in' or p[3] != 'sewer']
            pool = [p for p in pool
                    if cls == 'info_deathmatch_spawn' or (p[0] < -150 if cls == 'info_player_terrorist' else p[0] > 150)]
            best, bd = None, -1
            for p in pool:
                d = min(math.hypot(p[0] - q[1], p[1] - q[2]) + 0.5 * abs(p[2] - q[3]) for q in keep)
                if d > bd:
                    best, bd = p, d
            new = (cls, best[0], best[1], best[2], where)
            L.spawns_replaced.append(((cls, x, y, z, where), new))
            keep.append(new)
        order = {c: i for i, c in enumerate(['info_deathmatch_spawn', 'info_player_terrorist', 'info_player_counterterrorist'])}
        keep.sort(key=lambda t: (order[t[0]], t[4] != 'in'))
        # three deathmatch spawns down in the sewer, in junctions far apart
        sew = [p for p in indoor if p[3] == 'sewer']
        got = []
        for _ in range(3):
            best, bd = None, -1
            for p in sew:
                d = min([math.hypot(p[0] - q[1], p[1] - q[2]) for q in keep if q[4] == 'sewer'] +
                        [math.hypot(p[0] - q[0], p[1] - q[1]) for q in got] or [1e9])
                if d > bd:
                    best, bd = p, d
            if best:
                got.append(best)
        keep += [('info_deathmatch_spawn', p[0], p[1], p[2], 'sewer') for p in got]
        return keep, len(outdoor), len(indoor)

    taken = []

    def farthest(pts, k, mind=200, key=None, cap=None):
        out = []
        per = {}
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
    dm_out = farthest(outdoor, 16, 200)
    taken += dm_out
    west_in = [p for p in indoor if p[0] < -150]
    east_in = [p for p in indoor if p[0] > 150]
    west_out = [p for p in outdoor if p[0] < -150]
    east_out = [p for p in outdoor if p[0] > 150]
    t_in = farthest(west_in, 3, 96, cap=1)
    taken += t_in
    t_out = farthest(west_out, 7, 150)
    taken += t_out
    ct_in = farthest(east_in, 3, 96, cap=1)
    taken += ct_in
    ct_out = farthest(east_out, 7, 150)
    taken += ct_out
    spawns = [('info_deathmatch_spawn', p[0], p[1], p[2], 'in') for p in dm_in] + \
             [('info_deathmatch_spawn', p[0], p[1], p[2], 'out') for p in dm_out] + \
             [('info_player_terrorist', p[0], p[1], p[2], 'in') for p in t_in] + \
             [('info_player_terrorist', p[0], p[1], p[2], 'out') for p in t_out] + \
             [('info_player_counterterrorist', p[0], p[1], p[2], 'in') for p in ct_in] + \
             [('info_player_counterterrorist', p[0], p[1], p[2], 'out') for p in ct_out]
    return spawns, len(outdoor), len(indoor)


# ---------------------------------------------------------------- audit: things that overlap where they should not
def audit(L):
    """Returns warnings: furniture through walls, outdoor pieces on a plinth or in a pond, outdoor pieces through each other."""
    warn = []
    bld = [h['name'].split()[0] for h in L.houses]

    def inter(a, b, shrink=2.0):
        return all(a[0][i] + shrink < b[1][i] and b[0][i] + shrink < a[1][i] for i in range(3))

    def hits(bb, s, shrink=3.0):
        """box against a solid: exact for boxes, by sample points against the face planes for convex prisms."""
        if not inter(bb, (s['lo'], s['hi']), shrink):
            return False
        if s.get('box'):
            return True
        vs = s['verts']
        cc = tuple(sum(v[i] for v in vs) / len(vs) for i in range(3))
        planes = []
        for f in s['faces']:
            n = norm(newell(vs, f))
            fc = tuple(sum(vs[i][k] for i in f) / len(f) for k in range(3))
            if dot(n, sub(fc, cc)) < 0:
                n = (-n[0], -n[1], -n[2])
            planes.append((n, dot(n, vs[f[0]])))
        lo = [bb[0][i] + shrink for i in range(3)]
        hi = [bb[1][i] - shrink for i in range(3)]
        pts = [(x, y, z) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]
        pts.append(tuple((lo[i] + hi[i]) / 2 for i in range(3)))
        return any(all(dot(n, p) - d < 0 for n, d in planes) for p in pts)
    # 1. props inside a building against that building's solids
    for p in L.props:
        if p['tag'] not in bld:
            continue
        bb = prop_aabb(p, 'render')
        for s in L.solids:
            if not s['tag'].startswith(p['tag'] + '/'):
                continue
            if hits(bb, s):
                warn.append(f"{p['tag']}: {p['key']} at {[round(v) for v in p['origin']]} hits {s['tag']} "
                            f"{[round(v) for v in s['lo']]}..{[round(v) for v in s['hi']]}")
                break
    # 2./3. outdoor pieces on a building plot or in a pond
    outdoor = []
    for s in L.solids:
        if s['tag'].split('/')[0] in bld or s['tag'] in ('terrain', 'clip', 'skybox', 'boundary') or \
                s['tag'].startswith('sewer') or s['tag'].startswith('MH'):
            continue
        outdoor.append((s['tag'], (s['lo'], s['hi'])))
    for p in L.props:
        if p['tag'] in bld or p['tag'] in VEG:
            continue
        bb = prop_aabb(p, 'phys') or (prop_aabb(p, 'render') if p['tag'] == 'bush' else None)
        if p['tag'] == 'tree' and bb is None:
            c = p['origin']
            bb = ([c[0] - 24, c[1] - 24, c[2]], [c[0] + 24, c[1] + 24, c[2] + 200])
        if bb:
            outdoor.append((f"{p['tag']}:{p['key']}", bb))
    for tag, (lo, hi) in outdoor:
        if tag.startswith('bush'):
            continue
        for h in L.houses:
            r = h['rect']
            if lo[0] < r[2] and r[0] < hi[0] and lo[1] < r[3] and r[1] < hi[1]:
                warn.append(f'{tag} {[round(v) for v in lo]}..{[round(v) for v in hi]} on the plot of {h["name"]}')
        for (cx, cy, R, D) in PONDS:
            dx = max(lo[0] - cx, 0, cx - hi[0])
            dy = max(lo[1] - cy, 0, cy - hi[1])
            if math.hypot(dx, dy) < 0.75 * R and not tag.startswith('rock'):
                warn.append(f'{tag} {[round(v) for v in lo]}..{[round(v) for v in hi]} in the pond at {(cx, cy)}')
    # 4. outdoor pieces through each other (fences and rails of one line touch by design: same tag skipped)
    for i in range(len(outdoor)):
        for j in range(i + 1, len(outdoor)):
            ti, tj = outdoor[i][0], outdoor[j][0]
            if ti == tj or ti.startswith('bush') or tj.startswith('bush'):
                continue
            if inter(outdoor[i][1], outdoor[j][1], 1.0):
                warn.append(f'{ti} {[round(v) for v in outdoor[i][1][0]]} meets {tj} {[round(v) for v in outdoor[j][1][0]]}')
    return warn


# ---------------------------------------------------------------- vmap
def build_vmap(L, path):
    world_children = []
    for s in L.solids:
        world_children.append(map_mesh(s))
    # lightmap resolution volume round the play space: the texels go there, not to the hills outside
    lv = box_verts((-HALF - 64, -HALF - 64, -SH - 48), (HALF + 64, HALF + 64, 720))
    world_children.append(map_mesh({'verts': lv, 'faces': BOX_FACES, 'mats': [M['LMRES']] * 6, 'bake': False}))
    # ponds: func_water (rendered as world, physics as entity). The water body is cut into four convex quarters: one
    # hull the size of the big pond failed to build ("ExtrudeReplaceHull failed"). Top face water, the rest nodraw.
    for w in L.waters:
        cx, cy = w['center']
        n, R = w['n'], w['r']
        for q in range(4):
            arc = [(cx + R * math.cos(2 * math.pi * k / n), cy + R * math.sin(2 * math.pi * k / n))
                   for k in range(q * n // 4, (q + 1) * n // 4 + 1)]
            poly = [(cx, cy)] + arc
            vs, fs = prism(poly, 'z', w['z_mid'], w['z1'])
            mats = []
            for f in fs:
                c = sum(vs[i][2] for i in f) / len(f)
                mats.append(M['POOLWATER'] if abs(c - w['z1']) < 0.01 and len(f) == len(poly) else M['NODRAW'])
            mesh = map_mesh({'verts': vs, 'faces': fs, 'mats': mats})
            o = (sum(p[0] for p in poly) / len(poly), sum(p[1] for p in poly) / len(poly), (w['z_mid'] + w['z1']) / 2)
            world_children.append(entity('func_water', o, {'targetname': ''}, [mesh]))
        # the deep core under the shallows' layer (see build_layout): no drawn face; its top meets the layer's bottom
        Rc = w['r_core']
        for q in range(4):
            arc = [(cx + Rc * math.cos(2 * math.pi * k / n), cy + Rc * math.sin(2 * math.pi * k / n))
                   for k in range(q * n // 4, (q + 1) * n // 4 + 1)]
            poly = [(cx, cy)] + arc
            vs, fs = prism(poly, 'z', w['z0'], w['z_mid'])
            o = (sum(p[0] for p in poly) / len(poly), sum(p[1] for p in poly) / len(poly), (w['z0'] + w['z_mid']) / 2)
            world_children.append(entity('func_water', o, {'targetname': ''},
                                         [map_mesh({'verts': vs, 'faces': fs, 'mats': [M['NODRAW']] * len(fs)})]))
        # under the surface: ar_pool_day's underwater post-processing (its blue blur and grade), as Valve's pool does it,
        # over the deep core (in the shallows round it a player's eyes stay above the water)
        n2 = 24
        poly = [(cx + Rc * math.cos(2 * math.pi * k / n2), cy + Rc * math.sin(2 * math.pi * k / n2)) for k in range(n2)]
        vs, fs = prism(poly, 'z', w['z0'], w['z1'] - 2)
        world_children.append(entity('post_processing_volume', (cx, cy, (w['z0'] + w['z1'] - 2) / 2), {
            'StartDisabled': 0, 'spawnflags': 4097, 'postprocessing': UNDERWATER_VPOST, 'master': 0, 'minexposure': '1.0',
            'maxexposure': '1.0', 'enableexposure': 1, 'fadetime': '0.1', 'exposurespeedup': '1', 'exposurespeeddown': '1',
        }, [map_mesh({'verts': vs, 'faces': fs, 'mats': [M['POST']] * len(fs), 'bake': False})]))
    # the sewer's channels: shallow water in short pieces
    for (lo, hi) in L.channel_waters:
        vs = box_verts(lo, hi)
        mesh = map_mesh({'verts': vs, 'faces': BOX_FACES, 'mats': [M['POOLWATER']] + [M['NODRAW']] * 5})
        world_children.append(entity('func_water', tuple((lo[i] + hi[i]) / 2 for i in range(3)), {'targetname': ''}, [mesh]))
    # props: light probed, not lightmapped (the foliage alone made 815,577 lightmap charts, more than a 1024 atlas holds)
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
    probes = [{'lo': (-HALF - 32, -HALF - 32, -SH - 40), 'hi': (HALF + 32, HALF + 32, 700), 'voxel': 48, 'priority': 0},
              {'lo': (-HALF, -HALF, L.sewer_W - 40), 'hi': (HALF, HALF, L.sewer_W + TUN_H + 24), 'voxel': 24, 'priority': 1}]
    probes += L.probes
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
    }, [map_mesh({'verts': pv, 'faces': BOX_FACES, 'mats': [M['POST']] * 6, 'bake': False})]))
    # spawns
    spawns, n_out, n_in = pick_spawns(L, getattr(L, 'fixed_spawns', None))
    for (cls, x, y, z, where) in spawns:
        yaw = math.degrees(math.atan2(-y, -x)) + random.Random(int(x * 31 + y)).uniform(-40, 40)
        props = {'targetname': '', 'enabled': 1} if cls == 'info_deathmatch_spawn' else {'targetname': '', 'priority': 0, 'enabled': 1}
        world_children.append(entity(cls, (x, y, z + 8), props, angles=(0, yaw, 0)))
    L.spawns = spawns
    L.spawn_candidates = (n_out, n_in)

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


def vpk_read(path, name):
    """one file out of a single-file Valve VPK (a map VPK)."""
    import struct

    def cstr(f):
        b = bytearray()
        while True:
            c = f.read(1)
            if not c or c == b'\0':
                return b.decode('utf-8', 'replace')
            b += c
    with open(path, 'rb') as f:
        sig, ver, tree = struct.unpack('<III', f.read(12))
        base = 12 + tree
        if ver == 2:
            f.read(16)
            base = 28 + tree
        while True:
            ext = cstr(f)
            if not ext:
                break
            while True:
                d = cstr(f)
                if not d:
                    break
                while True:
                    n = cstr(f)
                    if not n:
                        break
                    crc, pre, arch, off, ln, term = struct.unpack('<IHHIIH', f.read(18))
                    pre_data = f.read(pre)
                    full = (f"{d}/{n}.{ext}" if d != ' ' else f"{n}.{ext}")
                    if full == name:
                        here = f.tell()
                        f.seek(base + off)
                        data = pre_data + f.read(ln)
                        f.seek(here)
                        return data
    raise FileNotFoundError(name + ' not in ' + path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cs2', default=cs2_dir())
    ap.add_argument('--layout', default=None, help='where to write layout.json (default: next to the vmap)')
    ap.add_argument('--addon', default=NAME, help='addon folder to write into (content and game); the map stays ' + NAME)
    ap.add_argument('--spawns-from', default=None, help="keep the spawn points of an earlier build's layout.json")
    args = ap.parse_args()
    content = os.path.join(args.cs2, 'content', 'csgo_addons', args.addon)
    game = os.path.join(args.cs2, 'game', 'csgo_addons', args.addon)
    os.makedirs(os.path.join(content, 'maps'), exist_ok=True)
    os.makedirs(game, exist_ok=True)
    with open(os.path.join(game, 'addoninfo.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('"AddonInfo"\n{\n\t"maps"\t"%s"\n}\n' % NAME)
    # the underwater post-processing: Valve's own, from ar_pool_day's map VPK (it is in no shared VPK), shipped in the
    # addon like gg_glasspire's materials
    data = vpk_read(os.path.join(args.cs2, 'game', 'csgo', 'maps', 'ar_pool_day.vpk'), UNDERWATER_VPOST + '_c')
    os.makedirs(os.path.join(game, 'postprocess'), exist_ok=True)
    with open(os.path.join(game, UNDERWATER_VPOST + '_c'), 'wb') as f:
        f.write(data)
    L = build_layout()
    if args.spawns_from:
        L.fixed_spawns = [tuple(s) for s in json.load(open(args.spawns_from, encoding='utf-8'))['spawns']]
    warns = audit(L)
    vmap = os.path.join(content, 'maps', NAME + '.vmap')
    build_vmap(L, vmap)
    lp = args.layout or os.path.join(content, 'maps', NAME + '_layout.json')
    layout = {
        'solids': [{'verts': s['verts'], 'faces': s['faces'], 'mats': s['mats'], 'tag': s['tag'], 'lo': s['lo'], 'hi': s['hi'],
                    'block': s.get('block', True)} for s in L.solids],
        'props': [dict(p, render=MODELS[p['key']][1], phys=MODELS[p['key']][2]) for p in L.props],
        'waters': L.waters, 'spawns': L.spawns, 'lights': L.lights, 'houses': L.houses,
        'ponds': PONDS, 'half': HALF, 'outer': OUTER, 'grid': GRID,
        'anchors': L.anchors, 'exits': L.exits, 'sewer_W': L.sewer_W, 'junctions': JUNCTIONS,
        'sewer_paths': L.sewer_paths, 'tunnel_segs': L.tunnel_segs, 'carved': L.carved, 'stats': L.stats, 'shell': SHELL,
        'manholes': [{'tag': t, 'x': x - (sg * 40 if ax == 'x' else 0), 'y': y - (sg * 40 if ax == 'y' else 0),
                      'z': MANHOLE_LEVEL[t]} for (t, x, y, ax, sg, way) in MANHOLES],
    }
    with open(lp, 'w', encoding='utf-8') as f:
        json.dump(layout, f)
    sp = {}
    for s in L.spawns:
        sp[s[0] + '/' + s[4]] = sp.get(s[0] + '/' + s[4], 0) + 1
    tags = {}
    for p in L.props:
        tags[p['tag']] = tags.get(p['tag'], 0) + 1
    print(f'vmap: {vmap} ({os.path.getsize(vmap) / 1e6:.1f} MB)')
    print(f'meshes: {len(L.solids)}, props: {len(L.props)} {tags}, ponds: {len(L.waters)}, lights: {len(L.lights)}, '
          f'probe volumes: {L.probe_count}')
    print(f'buildings: {[h["name"] for h in L.houses]}')
    print(f'nature: {L.stats}')
    print(f'sewer walkway at z {L.sewer_W}; carved {L.carved}')
    print(f'spawns: {sp} (candidates outdoor {L.spawn_candidates[0]}, indoor {L.spawn_candidates[1]})')
    print(f'layout: {lp}')
    for old, new in getattr(L, 'spawns_replaced', []):
        print(f'spawn moved: {old} -> {new}')
    if getattr(L, 'fixed_spawns', None):
        print(f"spawns kept from {args.spawns_from}: {len(L.fixed_spawns) - len(getattr(L, 'spawns_replaced', []))} "
              f"of its {len(L.fixed_spawns)}; {len(L.spawns)} in all")
    print(f'audit: {len(warns)} warnings')
    for w in warns:
        print('  ' + w)


if __name__ == '__main__':
    main()
