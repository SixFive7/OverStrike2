#!/usr/bin/env python3
"""The bridge's data files and the game server's own files: copy the committed ones to the server, verified.

    python deploy/data.py bridge [--apply]    maps.json, broken.json, sizes.json, search.json, ours.json -> <bridge.data_dir>
    python deploy/data.py server [--apply]    server/pre.sh, the mode cfgs, update-watch.py, workshop-sync.py -> <server.config_dir>

Without --apply it only shows what would change. The bridge reads its data files again by itself within about 10 seconds
(nothing restarts); the server files take effect at the game container's next start. Only committed bytes ship.
The nightly Workshop sync, when it runs, writes maps.json, broken.json and search.json on the server itself (and the
mode lists), so a server copy can be newer than the repository: a file whose server copy is not one this repository ever
committed is DRIFT, and nothing ships until its changes are taken into the repository (or --force, after you looked).
"""
import argparse
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

SETS = {
    "bridge": ("bridge.data_dir", "bridge", ["maps.json", "broken.json", "sizes.json", "search.json", "ours.json"]),
    "server": ("server.config_dir", "server", ["pre.sh", "gamemode_armsrace_server.cfg", "gamemode_deathmatch_server.cfg",
                                               "gamemode_competitive2v2_server.cfg", "update-watch.py", "workshop-sync.py"]),
}


def committed_before(rel, data, depth=50):
    """The commit (of the last `depth` that touched rel) whose copy of rel is exactly these bytes, or None."""
    for c in os2.git("log", "--format=%H", "-n", str(depth), "--", rel).split():
        r = subprocess.run(["git", "-C", os2.REPO, "show", f"{c}:{rel}"], capture_output=True)
        if r.returncode == 0 and r.stdout == data:
            return c
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("set", choices=sorted(SETS))
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--force", action="store_true", help="ship over a file that was changed on the server (after you looked)")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    cfg = os2.Config(args.config)
    key, folder, files = SETS[args.set]
    dest = cfg.get(key, "")
    if not dest:
        os2.log(f"{key} is empty in the private config: these files are not deployed from this repository")
        return 0
    commit = os2.clean_tree([f"{folder}/{f}" for f in files])
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
        os2.fail("DRIFT: changed on the server, and no commit holds that copy (the nightly Workshop sync writes the bridge's "
                 f"lists): {', '.join(drift)} - take the server's changes into the repository first, or --force after you looked")
    for f in files:
        src = os.path.join(os2.REPO, folder, f)
        os2.log(f"{f}: {os2.copy_verified(src, os.path.join(dest, f), dry=not args.apply)}" + (" (DRIFT overridden)" if f in drift else ""))
    os2.log(f"{'shipped' if args.apply else 'dry run - add --apply to copy'} ({folder}/ at {commit})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
