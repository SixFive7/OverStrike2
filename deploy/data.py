#!/usr/bin/env python3
"""The bridge's hand-made data files and the game server's own files: copy the committed ones to the server, verified.

    python deploy/data.py bridge [--apply]    ours.json, sizes.json, blocked.json -> <bridge.data_dir> (and the first lists, once)
    python deploy/data.py server [--apply]    server/pre.sh, the mode cfgs, update-watch.py, keepset.py, workshop-sync.py -> <server.config_dir>

Without --apply it only shows what would change. The bridge reads its data files again by itself within about 10 seconds
(nothing restarts); the server files take effect at the game container's (or the sync's) next start. Only committed bytes ship.

One writer per file. The Workshop sync writes what it generates on the server itself - maps.json, the mode lists, broken.json,
search.json, ratings.json, sync-status.json - so this script never ships over them: the repository's maps.json, broken.json and
search.json go only to a server that has none yet (a first install), and from then on the server's copies are the sync's. They are
snapshots here (the bridge's image and its self-test read them), refreshed from the server with the sync's --export when wanted.
What ships is what people make by hand: our maps (ours.json), the measured sizes (sizes.json) and the maps blocked for good
(blocked.json). A file this checkout does not have is skipped, and said. A server copy of one of those that no commit of this
repository ever held was changed there: that is DRIFT, and nothing ships until the change is in the repository (or --force).
"""
import argparse
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

SETS = {
    "bridge": ("bridge.data_dir", "bridge", ["ours.json", "sizes.json", "blocked.json", "maps.json", "broken.json", "search.json"]),
    "server": ("server.config_dir", "server", ["pre.sh", "gamemode_armsrace_server.cfg", "gamemode_deathmatch_server.cfg",
                                               "gamemode_competitive2v2_server.cfg", "update-watch.py", "keepset.py",
                                               "workshop-sync.py"]),
}
FIRST_ONLY = {"maps.json", "broken.json", "search.json"}   # the sync's on the server: shipped only where there is none yet


def committed_before(rel, data, depth=50):
    """The commit (of the last `depth` that touched rel) whose copy of rel is exactly these bytes, or None."""
    for c in os2.git("log", "--format=%H", "-n", str(depth), "--", rel).split():
        r = subprocess.run(["git", "-C", os2.REPO, "show", f"{c}:{rel}"], capture_output=True)
        if r.returncode == 0 and r.stdout == data:
            return c
    return None


def plan(folder, files, dest):
    """The files of a set that ship: the checkout must have them (else said and skipped), and a file the sync writes ships only to a
    server without one."""
    out = []
    for f in files:
        if not os.path.isfile(os.path.join(os2.REPO, folder, f)):
            os2.log(f"{folder}/{f}: not in this checkout - skipped")
        elif f in FIRST_ONLY and os.path.exists(os.path.join(dest, f)):
            os2.log(f"{f}: the server has its own - the Workshop sync writes it there, left alone")
        else:
            out.append(f)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("set", choices=sorted(SETS))
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--force", action="store_true", help="ship over a hand-made file that was changed on the server (after you looked)")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    cfg = os2.Config(args.config)
    key, folder, files = SETS[args.set]
    dest = cfg.get(key, "")
    if not dest:
        os2.log(f"{key} is empty in the private config: these files are not deployed from this repository")
        return 0
    files = plan(folder, files, dest)
    commit = os2.clean_tree([f"{folder}/{f}" for f in files]) if files else os2.git("rev-parse", "--short=12", "HEAD")
    note = os2.lock_note(cfg)
    if note:
        os2.log("server lock: " + note.replace("\n", " | ")[:300])
    drift = []
    for f in files:  # drift: a server copy that is neither this commit's nor an earlier commit's was changed there
        target = os.path.join(dest, f)
        if os.path.exists(target):
            live = open(target, "rb").read()
            if live != open(os.path.join(os2.REPO, folder, f), "rb").read() and not committed_before(f"{folder}/{f}", live):
                drift.append(f)
    if drift and not args.force:
        os2.fail(f"DRIFT: changed on the server, and no commit holds that copy: {', '.join(drift)} - take the server's changes into the "
                 "repository first, or --force after you looked")
    for f in files:
        src = os.path.join(os2.REPO, folder, f)
        os2.log(f"{f}: {os2.copy_verified(src, os.path.join(dest, f), dry=not args.apply)}" + (" (DRIFT overridden)" if f in drift else ""))
    os2.log(f"{'shipped' if args.apply else 'dry run - add --apply to copy'} ({folder}/ at {commit})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
