#!/usr/bin/env python3
"""Checks that our maps come out as committed: runs each map's generate.py into a scratch folder and compares every file it
writes with the hashes in selfcheck.json, then renders each preview.py from the layout it wrote and compares the picture with
the committed workshop_preview.jpg, byte for byte. Needs no CS2: gg_vinceville's generator reads Valve's underwater look out
of ar_pool_day's map package, and here reads it out of a stand-in package written for the run.

  python -I maps/selfcheck.py                  exit 0 when everything is as committed, 1 when something differs
  python -I maps/selfcheck.py --record         after an intended change to a map: write selfcheck.json anew
  python -I maps/selfcheck.py --keep <folder>  keep what the run wrote there, to look at what differs

The hashes hold for the Python, numpy and Pillow they were recorded with (selfcheck.json says which): another version can
write other bytes with no change to a map. A changed map also needs its preview rendered anew and committed, with its line in
tools/scan/allow.txt; this check does not write pictures."""
import argparse
import hashlib
import json
import os
import platform
import struct
import subprocess
import sys
import tempfile

import numpy
import PIL

HERE = os.path.dirname(os.path.abspath(__file__))
MAPS = ('gg_glasspire', 'gg_moonglass', 'gg_vinceville')
RECORD = os.path.join(HERE, 'selfcheck.json')
STAND_IN = b'not the game\'s file: a stand-in written by maps/selfcheck.py\n'


def stand_in_vpk(path, name):
    """A one-file VPK (version 1) holding name, for generate.py to read as if it were the game's map package."""
    folder, file = name.rsplit('/', 1)
    base, ext = file.rsplit('.', 1)
    entry = struct.pack('<IHHIIH', 0, 0, 0x7FFF, 0, len(STAND_IN), 0xFFFF)
    tree = ext.encode() + b'\0' + folder.encode() + b'\0' + base.encode() + b'\0' + entry + b'\0\0\0'
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 0x55AA1234, 1, len(tree)) + tree + STAND_IN)


def hashes(root, skip):
    out = {}
    for dp, dns, fns in os.walk(root):
        dns.sort()
        for fn in sorted(fns):
            p = os.path.join(dp, fn)
            if os.path.normcase(p) == os.path.normcase(skip) or fn.endswith('.jpg'):
                continue
            with open(p, 'rb') as f:
                out[os.path.relpath(p, root).replace(os.sep, '/')] = hashlib.sha256(f.read()).hexdigest()
    return out


def run_all(cmds):
    """Runs the commands side by side; returns each one's exit code and the end of its output."""
    procs = {k: subprocess.Popen(c, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, text=True)
             for k, c in cmds.items()}
    out = {k: p.communicate()[0] for k, p in procs.items()}
    return {k: (p.returncode, out[k][-1500:]) for k, p in procs.items()}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--record', action='store_true', help='write selfcheck.json anew from this run')
    ap.add_argument('--keep', default=None, help='a folder to keep what the run writes in')
    args = ap.parse_args()
    versions = {'python': platform.python_version(), 'numpy': numpy.__version__, 'pillow': PIL.__version__}
    with tempfile.TemporaryDirectory(prefix='maps-selfcheck-') as tmp:
        work = os.path.abspath(args.keep) if args.keep else tmp
        roots = {m: os.path.join(work, m) for m in MAPS}
        package = os.path.join(roots['gg_vinceville'], 'cs2', 'game', 'csgo', 'maps', 'ar_pool_day.vpk')
        for m, root in roots.items():
            if os.path.exists(root):
                sys.exit(f'{root} is there already: --keep takes a new folder')
        stand_in_vpk(package, 'postprocess/postprocess_filmic_underwater.vpost_c')
        done = run_all({m: [sys.executable, '-I', os.path.join(HERE, m, 'generate.py'), '--cs2', os.path.join(root, 'cs2'),
                            '--layout', os.path.join(root, 'layout.json')] for m, root in roots.items()})
        failed = [m for m, (code, _) in done.items() if code]
        for m in failed:
            print(f'{m}: generate.py failed (exit {done[m][0]}):\n{done[m][1]}')
        if failed:
            return 2
        got = {m: hashes(root, package) for m, root in roots.items()}
        earth = os.path.join(roots['gg_moonglass'], 'cs2', 'content', 'csgo_addons', 'gg_moonglass', 'materials', 'gg_moonglass',
                             'earth_color.png')
        done = run_all({m: [sys.executable, '-I', os.path.join(HERE, m, 'preview.py'), os.path.join(root, 'layout.json'),
                            os.path.join(root, 'preview.jpg')] + (['--earth', earth] if m == 'gg_moonglass' else [])
                        for m, root in roots.items()})
        failed = [m for m, (code, _) in done.items() if code]
        for m in failed:
            print(f'{m}: preview.py failed (exit {done[m][0]}):\n{done[m][1]}')
        if failed:
            return 2
        if args.record:
            with open(RECORD, 'w', encoding='utf-8', newline='\n') as f:
                json.dump({'about': 'What each map\'s generate.py writes, by sha256, as maps/selfcheck.py checks it; written by '
                                    'selfcheck.py --record with the Python, numpy and Pillow named here.',
                           'recorded_with': versions, 'maps': got}, f, indent=2)
                f.write('\n')
            print(f'recorded {sum(len(v) for v in got.values())} files of {len(got)} maps in selfcheck.json, with {versions}')
        with open(RECORD, encoding='utf-8') as f:
            record = json.load(f)
        bad = 0
        for m, root in roots.items():
            want = record['maps'].get(m, {})
            changed = sorted(k for k in set(want) | set(got[m]) if want.get(k) != got[m].get(k))
            with open(os.path.join(root, 'preview.jpg'), 'rb') as a, open(os.path.join(HERE, m, 'workshop_preview.jpg'), 'rb') as b:
                same_picture = a.read() == b.read()
            files = (f'{len(got[m])} files as recorded' if not changed else
                     f'{len(changed)} of {len(set(want) | set(got[m]))} files not as recorded: ' + ', '.join(changed))
            print(f'{m}: {files}; ' + ('the preview as committed' if same_picture else 'the preview NOT as committed'))
            bad += bool(changed) + (not same_picture)
        if bad and record.get('recorded_with') != versions:
            print(f'recorded with {record.get("recorded_with")}, run with {versions}: other versions can write other bytes')
        if bad and args.keep:
            print(f'what the run wrote is in {work}')
        print('maps: all as committed' if not bad else 'maps: NOT as committed')
        return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
