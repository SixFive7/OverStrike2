#!/usr/bin/env python3
"""Builds gg_moonglass: runs generate.py, then the Workshop Tools' resource compiler on the map, at below-normal priority with
4 threads so a game running on the same PC keeps its frame rate. The compile log goes to --log; the result is
game/csgo_addons/gg_moonglass/maps/gg_moonglass.vpk.

  python build.py --novis                      # what shipped (v1 2026-10-31, v2 2026-11-01)
  python build.py [--cs2 <path>] [--log <file>] [--novis] [--nolight] [--lightmap 1024] [--quality 0]

--novis skips the visibility build: on this map it ran 12 minutes without finishing (and ignored -threads, using about
17 cores), so build 1 shipped without it; the map then draws whole.
--nolight skips the lighting bake (a quick check of the geometry; such a map renders unlit - do not ship it).
The lighting bake (vrad3) runs on the GPU through Vulkan ray tracing; --lightmap and --quality keep it small.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..'))
NAME = 'gg_moonglass'
BELOW_NORMAL_PRIORITY_CLASS = 0x00004000


def wait_idle():
    """Waits until no other resourcecompiler.exe runs (another map may be building on this PC)."""
    while True:
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq resourcecompiler.exe'], capture_output=True, text=True).stdout
        if 'resourcecompiler.exe' not in out.lower():
            return
        print('waiting for another resourcecompiler.exe to finish...', flush=True)
        time.sleep(10)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cs2', default=os.environ.get('CS2_DIR') or r'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive')
    ap.add_argument('--log', default=None)
    ap.add_argument('--layout', default=None)
    ap.add_argument('--nolight', action='store_true')
    ap.add_argument('--novis', action='store_true', help='skip the visibility build (slow on this open map; the map then draws everything)')
    ap.add_argument('--lightmap', type=int, default=1024)
    ap.add_argument('--quality', type=int, default=0, choices=(0, 1, 2))
    ap.add_argument('--threads', type=int, default=4)
    ap.add_argument('--nogen', action='store_true', help='compile the vmap as it is')
    args = ap.parse_args()

    if not args.nogen:
        gen = [sys.executable, os.path.join(HERE, 'generate.py'), '--cs2', args.cs2]
        if args.layout:
            gen += ['--layout', args.layout]
        subprocess.run(gen, check=True)

    vmap = os.path.join(args.cs2, 'content', 'csgo_addons', NAME, 'maps', NAME + '.vmap')
    rc = os.path.join(args.cs2, 'game', 'bin', 'win64', 'resourcecompiler.exe')
    log = args.log or os.path.join(REPO, '.work', NAME + '-build', 'build.log')
    os.makedirs(os.path.dirname(os.path.abspath(log)), exist_ok=True)
    # the addon's own materials first: a map compile does not compile a missing child material, it fails the mesh
    mats = os.path.join(args.cs2, 'content', 'csgo_addons', NAME, 'materials', NAME, '*.vmat')
    # compiled constant-colour textures carry the colour's hash in their name: clear old outputs so none linger in the upload
    shutil.rmtree(os.path.join(args.cs2, 'game', 'csgo_addons', NAME, 'materials', NAME), ignore_errors=True)
    wait_idle()
    with open(log + '.materials.txt', 'w', encoding='utf-8', errors='replace') as f:
        m = subprocess.run([rc, '-threads', '2', '-f', '-nop4', '-i', mats], stdout=f, stderr=subprocess.STDOUT,
                           stdin=subprocess.DEVNULL, creationflags=BELOW_NORMAL_PRIORITY_CLASS, cwd=os.path.dirname(rc))
    with open(log + '.materials.txt', encoding='utf-8', errors='replace') as f:
        mlog = f.read()
    print('materials:', 'OK' if m.returncode == 0 and ' 0 failed' in mlog else 'FAILED - see ' + log + '.materials.txt')
    cmd = [rc, '-threads', str(args.threads), '-fshallow', '-unbufferedio', '-i', vmap, '-noassert',
           '-world', '-phys'] + ([] if args.novis else ['-vis']) + ['-breakpad', '-nop4', '-retail']
    if not args.nolight:
        cmd += ['-bakelighting', '-lightmapMaxResolution', str(args.lightmap), '-lightmapVRadQuality', str(args.quality)]
    print('compile:', subprocess.list2cmdline(cmd), flush=True)
    wait_idle()
    t0 = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                           creationflags=BELOW_NORMAL_PRIORITY_CLASS, cwd=os.path.dirname(rc))
    dt = time.time() - t0
    vpk = os.path.join(args.cs2, 'game', 'csgo_addons', NAME, 'maps', NAME + '.vpk')
    print(f'exit {p.returncode} after {dt:.0f} s; log {log}')
    # vrad3 works in <addon>\_vrad3 (about 50 MB of EXRs): move it beside the log so it is never uploaded with the addon
    work = os.path.join(args.cs2, 'game', 'csgo_addons', NAME, '_vrad3')
    if os.path.isdir(work):
        dest = os.path.join(os.path.dirname(os.path.abspath(log)), '_vrad3-' + time.strftime('%Y%m%d-%H%M%S'))
        shutil.move(work, dest)
        print('vrad3 working files moved to', dest)
    if os.path.exists(vpk):
        print(f'vpk {vpk} {os.path.getsize(vpk) / 1e6:.1f} MB, written {time.ctime(os.path.getmtime(vpk))}')
    else:
        print('no vpk written')
    bad = []
    with open(log, encoding='utf-8', errors='replace') as f:
        for line in f:
            l = line.lower()
            if 'error' in l or 'leak' in l or 'missing' in l or 'failed' in l or 'warning' in l:
                bad.append(line.rstrip())
    print(f'{len(bad)} log lines mention error/warning/missing/failed/leak')
    for line in bad[:60]:
        print('  ' + line)
    return p.returncode


if __name__ == '__main__':
    sys.exit(main())
