"""What every map's build.py runs: the map's generate.py, then the Workshop Tools' resource compiler - first on the addon's
own materials, when the map has any, then on the map. Each compile waits until no other resourcecompiler.exe is running
(another map may be compiling on this PC) and runs at below-normal priority, so a game running on the same PC keeps its
frame rate. The result is game/csgo_addons/<addon>/maps/<map>.vpk.

  python build.py [--cs2 <path>] [--log <file>] [--layout <file>] [--addon <folder>] [--nolight] [--vis | --novis]
                  [--lightmap 1024] [--quality 0] [--threads 4] [--nogen]

--cs2 is the 'Counter-Strike Global Offensive' folder: CS2_DIR, else Steam's default library.
--log is the compile log (default .work/<map>-build/build.log in this repository); the materials' log goes beside it, as
<log>.materials.txt. The lighting bake's working files (about 50 MB) are moved beside it too, out of the addon folder, so
they are never uploaded with it.
--addon builds into another addon folder (content and game), for example a staging copy while the published build is under
review; the map inside keeps its name.
--nolight skips the lighting bake: a quick check of the geometry, whose map renders unlit - do not ship it. The bake (vrad3)
runs on the GPU through Vulkan ray tracing; --lightmap and --quality keep it small.
--vis adds the visibility build. Without it the map draws whole, which is how every published build was compiled: on
gg_glasspire and gg_moonglass it ran for many minutes without finishing. --novis, the default, is still taken.
--nogen compiles the map source as it is."""
import argparse
import os
import shutil
import subprocess
import sys
import time

from vmapkit import cs2_dir

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
SHOWN = 80   # log lines mentioning a problem that are printed after the compile


def compilers_running():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq resourcecompiler.exe', '/FO', 'CSV', '/NH'],
                         capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l.lower().startswith('"resourcecompiler.exe"')]


def wait_turn(poll=10):
    """Waits until no other resourcecompiler.exe runs: another map may be compiling on this PC."""
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


def compile_to(log, cmd):
    """Runs the resource compiler with its output into log."""
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        return subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                              creationflags=BELOW_NORMAL_PRIORITY_CLASS, cwd=os.path.dirname(cmd[0]))


def main(name, *, materials, generator_options=()):
    """Builds the map name: materials says whether its addon has materials of its own to compile first, generator_options
    which other options of its generate.py build.py passes on."""
    here = os.path.join(REPO, 'maps', name)
    ap = argparse.ArgumentParser()
    ap.add_argument('--cs2', default=cs2_dir())
    ap.add_argument('--log', default=None)
    ap.add_argument('--layout', default=None)
    ap.add_argument('--addon', default=name, help='addon folder to build into (content and game); the map stays ' + name)
    ap.add_argument('--nolight', action='store_true')
    ap.add_argument('--vis', dest='vis', action='store_true', default=False, help='add the visibility build')
    ap.add_argument('--novis', dest='vis', action='store_false', help='no visibility build: the default (the map draws everything)')
    ap.add_argument('--lightmap', type=int, default=1024)
    ap.add_argument('--quality', type=int, default=0, choices=(0, 1, 2))
    ap.add_argument('--threads', type=int, default=4)
    ap.add_argument('--nogen', action='store_true', help='compile the vmap as it is')
    for opt in generator_options:
        ap.add_argument(opt, default=None, help="passed on to generate.py")
    args = ap.parse_args()

    if not args.nogen:
        gen = [sys.executable, '-I', os.path.join(here, 'generate.py'), '--cs2', args.cs2]
        if args.layout:
            gen += ['--layout', args.layout]
        gen += ['--addon', args.addon]
        for opt in generator_options:
            value = getattr(args, opt.lstrip('-').replace('-', '_'))
            if value is not None:
                gen += [opt, value]
        subprocess.run(gen, check=True)

    rc = os.path.join(args.cs2, 'game', 'bin', 'win64', 'resourcecompiler.exe')
    content = os.path.join(args.cs2, 'content', 'csgo_addons', args.addon)
    game = os.path.join(args.cs2, 'game', 'csgo_addons', args.addon)
    log = os.path.abspath(args.log or os.path.join(REPO, '.work', name + '-build', 'build.log'))
    os.makedirs(os.path.dirname(log), exist_ok=True)
    if materials:
        # a map compile does not compile a missing child material, it fails the mesh; and compiled constant-colour textures
        # carry the colour's hash in their name, so old outputs are cleared first, or stale ones would ride along in the upload
        shutil.rmtree(os.path.join(game, 'materials', name), ignore_errors=True)
        mats = os.path.join(content, 'materials', name, '*.vmat')
        wait_turn()
        m = compile_to(log + '.materials.txt', [rc, '-threads', '2', '-f', '-nop4', '-i', mats])
        with open(log + '.materials.txt', encoding='utf-8', errors='replace') as f:
            mlog = f.read()
        print('materials:', 'OK' if m.returncode == 0 and ' 0 failed' in mlog else 'FAILED - see ' + log + '.materials.txt')

    cmd = [rc, '-threads', str(args.threads), '-fshallow', '-unbufferedio', '-i', os.path.join(content, 'maps', name + '.vmap'),
           '-noassert', '-world', '-phys'] + (['-vis'] if args.vis else []) + ['-breakpad', '-nop4', '-retail']
    if not args.nolight:
        cmd += ['-bakelighting', '-lightmapMaxResolution', str(args.lightmap), '-lightmapVRadQuality', str(args.quality)]
    wait_turn()
    print('compile:', subprocess.list2cmdline(cmd), flush=True)
    t0 = time.time()
    p = compile_to(log, cmd)
    dt = time.time() - t0
    print(f'exit {p.returncode} after {dt:.0f} s; log {log}')
    work = os.path.join(game, '_vrad3')
    if os.path.isdir(work):
        dest = os.path.join(os.path.dirname(log), '_vrad3-' + time.strftime('%Y%m%d-%H%M%S'))
        shutil.move(work, dest)
        print('vrad3 working files moved to', dest)
    vpk = os.path.join(game, 'maps', name + '.vpk')
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
    for line in bad[:SHOWN]:
        print('  ' + line)
    return p.returncode
