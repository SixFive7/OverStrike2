#!/usr/bin/env python3
"""The server side: the server program os2-server, the sync program os2-sync, the launcher pre.sh and the mode cfgs - build and
test them here, ship them to the server's folders.

    python deploy/server.py build [--image]   both programs, one self-contained linux-x64 file each (dotnet publish), then both
                                              self-tests, at below normal priority, from committed sources only. The checks that
                                              need Linux skip here; --image runs both self-tests in the game server's image as well,
                                              with the local build engine (build.engine), where every check runs.
    python deploy/server.py ship              show what would change: os2-server, os2-sync and pre.sh in <server.program_dir>, the
                                              three mode cfgs in <server.config_dir>
    python deploy/server.py ship --apply      copy them there, verified byte for byte. The programs are written beside their targets and
                                              renamed over them: a program running from there keeps the copy it started with. pre.sh and
                                              the cfgs are written in place: a container mounts each as a single file, and such a mount
                                              stays on the old file when another is renamed over it, so a running container would never
                                              see the new one. --apply with no server.program_dir in the config fails: nothing would ship.

What the server does with them (docs/server.md): the game server's container mounts the program folder read-only at
/home/steam/overstrike2, with its pre.sh at <game>/pre.sh, which the image sources before every engine start: pre.sh copies
os2-server out, runs `install` and starts the restart watcher. The watcher notices a new os2-server, pre.sh or mode cfg by itself and
restarts the engine for it - at the end of the match with players on, at once with nobody on (restarts.newVersions), announced on
the HUD first. The game also runs a mode cfg again at every map load. os2-sync takes effect at the sync container's next start.

What ships is what `build` built - refused once the programs' sources changed since - and the committed pre.sh and cfgs. A pre.sh or
cfg on the server that no commit of this repository ever held was changed there: that is DRIFT, and nothing ships until the change is
in the repository (or --force).
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import data as D  # noqa: E402  (committed_before: is a server copy one this repository ever held?)
import os2  # noqa: E402

SOURCES = ["core", "server/OverStrike2.Server", "server/OverStrike2.Sync"]    # what the programs and their self-tests are built from
PROGRAMS = {"os2-server": "server/OverStrike2.Server", "os2-sync": "server/OverStrike2.Sync"}
SELFTESTS = {"server/OverStrike2.Server/SelfTest": "OverStrike2.Server.SelfTest", "server/OverStrike2.Sync/SelfTest": "OverStrike2.Sync.SelfTest"}
LAUNCHER = "server/OverStrike2.Server/pre.sh"
CFGS = ["server/gamemode_armsrace_server.cfg", "server/gamemode_deathmatch_server.cfg", "server/gamemode_competitive2v2_server.cfg"]
OUT = os.path.join(os2.REPO, "server", "bin", "ship")                 # one folder per program, and build.json (bin/ is ignored)
TEST_OUT = os.path.join(os2.REPO, "server", "bin", "ship-selftest")
RECORD = os.path.join(OUT, "build.json")
GAME_IMAGE = "docker.io/joedwards32/cs2"
BELOW_NORMAL, NO_WINDOW = 0x00004000, 0x08000000


def dotnet(args):
    flags = BELOW_NORMAL | NO_WINDOW if os.name == "nt" else 0
    r = subprocess.run(["dotnet", *args, "-nodeReuse:false", "-p:UseSharedCompilation=false"], cwd=os2.REPO, capture_output=True,
                       text=True, encoding="utf-8", errors="replace", creationflags=flags)
    return r.returncode, r.stdout + r.stderr


def last_line(text):
    return (text.strip().splitlines() or ["(no output)"])[-1].strip()


def single_file(folder, name):
    """A published program must be one file: anything beside it but debug symbols would have to ship too."""
    extra = sorted(f for f in os.listdir(folder) if f != name and not f.endswith(".pdb"))
    if extra:
        os2.fail(f"{name} is not a single file - its publish folder also holds {', '.join(extra)}")
    return os.path.join(folder, name)


def publish(project, folder):
    code, out = dotnet(["publish", project, "-c", "Release", "-r", "linux-x64", "-o", folder])
    if code != 0:
        os2.fail(f"publish {project}:\n" + out[-3000:])


def in_image(cfg, name):
    """One published self-test in the game image, as the image's own user; the repository read-only at /repo (some checks read it).
    The test is not the script's last command, so bash stays the container's first process and reaps the orphans of the checks that
    kill process groups - a test that bash hands that slot to never sees them go."""
    engine = cfg.get("build.engine", "wslc") if cfg else "wslc"
    image = cfg.get("build.game_image", GAME_IMAGE) if cfg else GAME_IMAGE
    script = f"cp /t/{name} /tmp/{name} && chmod 755 /tmp/{name} && cd /repo && /tmp/{name}; rc=$?; echo \"exit $rc\"; exit $rc"
    flags = BELOW_NORMAL | NO_WINDOW if os.name == "nt" else 0
    r = subprocess.run([engine, "run", "--rm", "--entrypoint", "/bin/bash", "-v", f"{TEST_OUT}:/t:ro", "-v", f"{os2.REPO}:/repo:ro",
                        image, "-c", script], capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=flags)
    return r.returncode, "\n".join(l for l in (r.stdout + r.stderr).splitlines() if not re.fullmatch(r"exit \d+", l.strip()))


def build(image, cfg):
    commit = os2.clean_tree(SOURCES)
    record = {"commit": commit, "built": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "programs": {}, "selftests": {}}
    for name, project in PROGRAMS.items():
        folder = os.path.join(OUT, name)
        publish(project, folder)
        path = single_file(folder, name)
        record["programs"][name] = {"sha256": os2.sha256(path), "bytes": os.path.getsize(path)}
        os2.log(f"published {name} at {commit}: {os.path.getsize(path) / 1e6:.1f} MB, sha256 {record['programs'][name]['sha256'][:16]}")
    for project, name in SELFTESTS.items():
        code, out = dotnet(["run", "-c", "Release", "--project", project])
        record["selftests"][name + " here"] = last_line(out)
        os2.log(f"{name} here: {last_line(out)}")
        if code != 0:
            os2.fail(f"{name} failed here:\n" + out[-2000:])
    if image:
        for project, name in SELFTESTS.items():
            publish(project, TEST_OUT)
            code, out = in_image(cfg, name)
            record["selftests"][name + " in the game image"] = last_line(out)
            os2.log(f"{name} in the game image: {last_line(out)}")
            if code != 0:
                os2.fail(f"{name} failed in the game image:\n" + out[-2000:])
    with open(RECORD, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=1)
    os2.log(f"built at {commit}: ship with  python deploy/server.py ship")
    return record


def built():
    """The last build's record, checked: its files are the ones it recorded, and the programs' sources are unchanged since."""
    if not os.path.exists(RECORD):
        os2.fail("not built - run: python deploy/server.py build")
    with open(RECORD, encoding="utf-8") as f:
        record = json.load(f)
    changed = os2.git("diff", "--name-only", record["commit"], "--", *SOURCES)
    if changed:
        os2.fail(f"the programs' sources changed since the build at {record['commit']} ({', '.join(changed.split()[:5])}) - build again")
    for name, info in record["programs"].items():
        path = os.path.join(OUT, name, name)
        if not os.path.exists(path) or os2.sha256(path) != info["sha256"]:
            os2.fail(f"{name} is not the file the build at {record['commit']} made - build again")
    return record


def plan(cfg):
    """(source, target) for every file that ships; a folder the private config leaves empty is not deployed from here."""
    program_dir, config_dir = cfg.get("server.program_dir", ""), cfg.get("server.config_dir", "")
    out = []
    if program_dir:
        out += [(os.path.join(OUT, n, n), os.path.join(program_dir, n)) for n in PROGRAMS]
        out.append((os.path.join(os2.REPO, LAUNCHER), os.path.join(program_dir, "pre.sh")))
    else:
        os2.log("server.program_dir is empty in the private config: the programs and pre.sh are not deployed from here")
    if config_dir:
        out += [(os.path.join(os2.REPO, c), os.path.join(config_dir, os.path.basename(c))) for c in CFGS]
    else:
        os2.log("server.config_dir is empty in the private config: the mode cfgs are not deployed from here")
    return out


def drift(files):
    """The targets of the committed files (pre.sh, the cfgs) whose server copy is neither this checkout's nor any earlier commit's.
    Never the programs: a server copy that differs from a new build is the version before it."""
    committed = {os.path.join(os2.REPO, rel): rel for rel in [LAUNCHER] + CFGS}
    out = []
    for src, dst in files:
        if src not in committed or not os.path.exists(dst):
            continue
        live = open(dst, "rb").read()
        if live != open(src, "rb").read() and not D.committed_before(committed[src], live):
            out.append(dst)
    return out


def ship(cfg, apply, force=False):
    if apply and not cfg.get("server.program_dir", ""):
        os2.fail(f"server.program_dir is empty in {getattr(cfg, 'path', 'the private config')}: --apply has nowhere to ship the programs, so nothing is "
                 "shipped. Is this the deployment's config? OVERSTRIKE2_CONFIG or --config names it", 2)
    record = built()
    head = os2.clean_tree(SOURCES + [LAUNCHER] + CFGS)
    files = plan(cfg)
    if not files:
        return 0
    address = cfg.get("server.address", "")
    if address:
        on = os2.humans(address)
        os2.log(f"humans on the server: {'unknown' if on is None else on} (a new os2-server, pre.sh or cfg restarts the engine at "
                "the end of the match, or at once with nobody on)")
    note = os2.lock_note(cfg)
    if note:
        os2.log("server lock: " + note.replace("\n", " | ")[:300])
    changed = drift(files)
    if changed and not force:
        os2.fail("DRIFT: changed on the server, and no commit holds that copy: " + ", ".join(changed) + " - take the server's "
                 "changes into the repository first, or --force after you looked")
    copied = 0
    programs = {os.path.join(OUT, n, n) for n in PROGRAMS}
    for src, dst in files:
        try:
            result = os2.copy_verified(src, dst, dry=not apply, atomic=src in programs)
        except OSError as error:
            os2.fail(f"{dst} could not be written ({error.__class__.__name__}): " + ("" if src in programs else "it goes in place, so the file itself must be writable, not only its folder; ")
                     + "look at its owner and mode. What was copied before it stays; ship again when it is fixed")
        copied += result == "copied"
        os2.log(f"{dst}: {result}" + (" (DRIFT overridden)" if dst in changed else ""))
    if not apply:
        os2.log(f"dry run (programs built at {record['commit']}, pre.sh and cfgs at {head}) - add --apply to copy")
    elif copied:
        os2.log(f"shipped {copied} file(s): the restart watcher takes a new os2-server, pre.sh or cfg in by itself; os2-sync at the "
                "sync container's next start")
    else:
        os2.log("nothing to copy: the server has these files already")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["build", "ship"])
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--force", action="store_true", help="ship over a pre.sh or cfg that was changed on the server (after you looked)")
    ap.add_argument("--image", action="store_true", help="build: run both self-tests in the game image as well")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.command == "build":
        cfg = os2.Config(args.config) if args.image and (args.config or os.environ.get("OVERSTRIKE2_CONFIG")
                                                           or os.path.exists(os.path.join(os2.REPO, ".private", "config.toml"))) else None
        build(args.image, cfg)
        return 0
    return ship(os2.Config(args.config), args.apply, args.force)


if __name__ == "__main__":
    sys.exit(main())
