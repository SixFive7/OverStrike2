#!/usr/bin/env python3
"""Builds gg_vinceville: runs generate.py, waits until no other resourcecompiler.exe is running (another map may be
compiling on the same PC), then compiles the map with the Workshop Tools' resource compiler at below-normal priority with
4 threads, so a game running on the same PC keeps its frame rate. The result is
game/csgo_addons/gg_vinceville/maps/gg_vinceville.vpk; the map uses stock materials only, so there is nothing else to
compile and no materials folder to ship.

  python build.py --log <file> [--layout <file>] [--cs2 <path>] [--nolight] [--vis] [--lightmap 1024] [--quality 0]
                  [--addon <folder>] [--spawns-from <layout.json>]

--addon builds into another addon folder (content and game), for example a staging copy while the published build
is under review; the map inside stays gg_vinceville. --spawns-from keeps an earlier build's spawn points.

--nolight skips the lighting bake (a quick geometry check; such a map renders unlit - do not ship it).
--vis adds the visibility build; without it the map draws whole, which is what shipped.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
NAME = 'gg_vinceville'
BELOW_NORMAL_PRIORITY_CLASS = 0x00004000


def compilers_running():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq resourcecompiler.exe', '/FO', 'CSV', '/NH'],
                         capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l.lower().startswith('"resourcecompiler.exe"')]


def wait_turn(poll=10):
    t0 = time.time()
    while True:
        running = compilers_running()
        if not running:
            if time.time() - t0 > 1:
                print(f'other compile finished; waited {time.time() - t0:.0f} s', flush=True)
            return
        if time.time() - t0 < 1:
            print(f'waiting: {len(running)} resourcecompiler.exe already running', flush=True)
        time.sleep(poll)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cs2', default=os.environ.get('CS2_DIR') or r'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive')
    ap.add_argument('--log', required=True)
    ap.add_argument('--layout', default=None)
    ap.add_argument('--nolight', action='store_true')
    ap.add_argument('--vis', action='store_true')
    ap.add_argument('--lightmap', type=int, default=1024)
    ap.add_argument('--quality', type=int, default=0, choices=(0, 1, 2))
    ap.add_argument('--threads', type=int, default=4)
    ap.add_argument('--nogen', action='store_true', help='compile the vmap as it is')
    ap.add_argument('--addon', default=NAME)
    ap.add_argument('--spawns-from', default=None)
    args = ap.parse_args()

    if not args.nogen:
        gen = [sys.executable, '-I', os.path.join(HERE, 'generate.py'), '--cs2', args.cs2]
        if args.layout:
            gen += ['--layout', args.layout]
        gen += ['--addon', args.addon]
        if args.spawns_from:
            gen += ['--spawns-from', args.spawns_from]
        subprocess.run(gen, check=True)

    vmap = os.path.join(args.cs2, 'content', 'csgo_addons', args.addon, 'maps', NAME + '.vmap')
    rc = os.path.join(args.cs2, 'game', 'bin', 'win64', 'resourcecompiler.exe')
    log = os.path.abspath(args.log)
    os.makedirs(os.path.dirname(log), exist_ok=True)
    cmd = [rc, '-threads', str(args.threads), '-fshallow', '-unbufferedio', '-i', vmap, '-noassert',
           '-world', '-phys'] + (['-vis'] if args.vis else []) + ['-breakpad', '-nop4', '-retail']
    if not args.nolight:
        cmd += ['-bakelighting', '-lightmapMaxResolution', str(args.lightmap), '-lightmapVRadQuality', str(args.quality)]
    wait_turn()
    print('compile:', subprocess.list2cmdline(cmd), flush=True)
    t0 = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                           creationflags=BELOW_NORMAL_PRIORITY_CLASS, cwd=os.path.dirname(rc))
    dt = time.time() - t0
    vpk = os.path.join(args.cs2, 'game', 'csgo_addons', args.addon, 'maps', NAME + '.vpk')
    print(f'exit {p.returncode} after {dt:.0f} s; log {log}')
    # vrad3 works in <addon>\_vrad3: move it beside the log so it is never uploaded with the addon
    work = os.path.join(args.cs2, 'game', 'csgo_addons', args.addon, '_vrad3')
    if os.path.isdir(work):
        dest = os.path.join(os.path.dirname(log), '_vrad3-' + time.strftime('%Y%m%d-%H%M%S'))
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
    for line in bad[:80]:
        print('  ' + line)
    return p.returncode


if __name__ == '__main__':
    sys.exit(main())
