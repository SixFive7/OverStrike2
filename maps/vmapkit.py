"""What the map generators share: ids that depend only on the map's name and the order of the calls, the keyvalues2 DMX writer
a Hammer map source is written in and the elements every map has (a mesh's data streams, an entity, where a node is), and
where CS2 is when --cs2 is not given (build_map.py's default too). The same inputs write the same bytes, whatever the Python
hash seed."""
import hashlib
import numbers
import os
import re
import uuid

_name = ''
_counter = [0]
_node = [1]
STEAM = r'C:\Program Files (x86)\Steam'   # Steam's usual place


def find_cs2(needs=r'game\bin\win64\cs2.exe'):
    """The first CS2 install Steam knows of that has needs, or '' for none: Steam's own folder from the registry
    (HKCU\\Software\\Valve\\Steam, SteamPath) and its usual place, then every library folder its libraryfolders.vdf lists, in
    that order - tools/workshop/FindCs2.ps1's lookup, which the PowerShell scripts use: keep the two alike."""
    steams = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam') as key:
            path = winreg.QueryValueEx(key, 'SteamPath')[0]
        if path:
            steams.append(path.replace('/', '\\'))
    except (ImportError, OSError):
        pass
    steams.append(STEAM)
    roots = []
    for steam in steams:
        roots.append(steam)
        try:
            with open(os.path.join(steam, 'steamapps', 'libraryfolders.vdf'), encoding='utf-8', errors='replace') as f:
                roots += [p.replace('\\\\', '\\') for p in re.findall(r'"path"\s+"(.+?)"', f.read())]
        except OSError:
            pass
    for root in roots:
        candidate = os.path.join(root, 'steamapps', 'common', 'Counter-Strike Global Offensive')
        if os.path.exists(os.path.join(candidate, needs)):
            return candidate
    return ''


def cs2_dir():
    """The 'Counter-Strike Global Offensive' folder when --cs2 is not given: CS2_DIR, else the install find_cs2 finds, else
    Steam's usual place (where a missing CS2 then shows)."""
    return os.environ.get('CS2_DIR') or find_cs2() or os.path.join(STEAM, 'steamapps', 'common', 'Counter-Strike Global Offensive')


def use_name(name):
    """The map whose ids these are: the generator says so once, before it makes its first element."""
    global _name
    _name = name


def new_id():
    _counter[0] += 1
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f'{_name}/{_counter[0]}'))


def node_ids():
    _node[0] += 1
    ref = int(hashlib.sha1(f'{_name}/node/{_node[0]}'.encode()).hexdigest()[:16], 16)
    return _node[0], f'0x{ref:x}'


class El:
    __slots__ = ('cls', 'attrs', 'id')

    def __init__(self, cls, attrs=None):
        self.cls = cls
        self.attrs = attrs or []
        self.id = new_id()


def fnum(v):
    if isinstance(v, bool):
        return '1' if v else '0'
    if isinstance(v, numbers.Integral):   # numpy's integers too
        return str(int(v))
    s = f'{float(v):.6f}'.rstrip('0').rstrip('.')
    return '0' if s in ('-0', '', '-') else s


def fval(typ, v):
    if typ in ('string', 'uint64', 'elementid'):
        return v
    if typ == 'bool':
        return '1' if v else '0'
    if typ in ('int', 'float'):
        return fnum(v)
    return ' '.join(fnum(x) for x in v)   # vector2/3/4, qangle, color


def write_el(out, el, ind, name=None):
    t = '\t' * ind
    t1 = t + '\t'
    out.append(f'{t}"{el.cls}"' if name is None else f'{t}"{name}" "{el.cls}"')
    out.append(t + '{')
    out.append(f'{t1}"id" "elementid" "{el.id}"')
    for n, typ, v in el.attrs:
        if typ == 'element':
            if v is None:
                out.append(f'{t1}"{n}" "element" ""')
            else:
                write_el(out, v, ind + 1, n)
        elif typ == 'element_array':
            out.append(f'{t1}"{n}" "element_array" ')
            out.append(t1 + '[')
            for k, child in enumerate(v):
                write_el(out, child, ind + 2)
                if k < len(v) - 1:
                    out[-1] += ','
            out.append(t1 + ']')
        elif typ.endswith('_array'):
            base = typ[:-6]
            out.append(f'{t1}"{n}" "{typ}" ')
            out.append(t1 + '[')
            if v:
                out.append(',\n'.join(f'{t1}\t"{fval(base, x)}"' for x in v))
            out.append(t1 + ']')
        else:
            out.append(f'{t1}"{n}" "{typ}" "{fval(typ, v)}"')
    out.append(t + '}')


def data_array(size, streams):
    return El('CDmePolygonMeshDataArray', [('size', 'int', size), ('streams', 'element_array', streams)])


def stream(sem, typ, data, flags, std=None, loc=0):
    """One stream of a mesh's data array: positions, texture coordinates, normals, face flags and the like."""
    return El('CDmePolygonMeshDataStream', [
        ('name', 'string', f'{sem}:0'), ('standardAttributeName', 'string', sem if std is None else std),
        ('semanticName', 'string', sem), ('semanticIndex', 'int', 0), ('vertexBufferLocation', 'int', loc),
        ('dataStateFlags', 'int', flags), ('subdivisionBinding', 'element', None), ('data', typ, data)])


def transform_tail(origin, angles=(0, 0, 0), scales=(1, 1, 1)):
    """The attributes every map node ends with: where it is, how it is turned and how it is scaled."""
    return [('origin', 'vector3', tuple(origin)), ('angles', 'qangle', angles), ('scales', 'vector3', scales),
            ('transformLocked', 'bool', False), ('force_hidden', 'bool', False), ('editorOnly', 'bool', False)]


def entity(classname, origin, props, children=(), angles=(0, 0, 0), scales=(1, 1, 1)):
    """An entity: its class, its keyvalues (each written as a string), its children (a brush entity's meshes) and where it is."""
    nid, ref = node_ids()
    ep = El('EditGameClassProps', [('classname', 'string', classname)] + [(k, 'string', str(v)) for k, v in props.items()])
    return El('CMapEntity', [
        ('nodeID', 'int', nid), ('referenceID', 'uint64', ref), ('children', 'element_array', list(children)),
        ('variableTargetKeys', 'string_array', []), ('variableNames', 'string_array', []),
        ('relayPlugData', 'element', El('DmePlugList', [('names', 'string_array', []), ('dataTypes', 'int_array', []),
                                                         ('plugTypes', 'int_array', []), ('descriptions', 'string_array', [])])),
        ('connectionsData', 'element_array', []), ('entity_properties', 'element', ep),
        ('hitNormal', 'vector3', (0, 0, 1)), ('isProceduralEntity', 'bool', False),
    ] + transform_tail(origin, angles, scales))
