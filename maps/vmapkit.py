"""What the map generators share: ids that depend only on the map's name and the order of the calls, and the keyvalues2 DMX writer
a Hammer map source is written in. The same inputs write the same bytes, whatever the Python hash seed."""
import hashlib
import numbers
import uuid

_name = ''
_counter = [0]
_node = [1]


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
