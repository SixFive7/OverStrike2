#!/usr/bin/env python3
"""The Steam Workshop items: the HUD addon and our own maps, through the Steam client logged in on this PC.

    python deploy/workshop.py hud-thumbs --src DIR     the menu's map pictures (hud/thumbs.py), from a local folder of <id>.webp
    python deploy/workshop.py hud-build [--no-thumbs]  compile the addon with the CS2 Workshop Tools (hud/build.ps1)
    python deploy/workshop.py hud-query | hud-public   read-only: the item as Steam has it / whether everybody can see it
    python deploy/workshop.py hud-publish --note "..." --user-approved-update
                                                       a content update of the ONE HUD item (hud/PUBLISH.md, AGENTS.md)
    python deploy/workshop.py map <args...>            maps/publisher/publish.ps1 with these arguments (-Map gg_x -Query, ...)

Read hud/PUBLISH.md before any upload: every new version waits in Valve's moderation queue, and a new HUD item is never
created. The private config gives the CS2 folder (workshop.cs2_dir; empty: found through Steam's library folders).
"""
import argparse
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

HUD = os.path.join(os2.REPO, "hud")
MAPS = os.path.join(os2.REPO, "maps", "publisher")


def ps(script, args, cfg):
    cs2 = cfg.get("workshop.cs2_dir", "")
    argv = ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, *args] + (["-Cs2", cs2] if cs2 else [])
    return subprocess.run(argv).returncode


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["hud-thumbs", "hud-build", "hud-query", "hud-public", "hud-publish", "map"])
    ap.add_argument("rest", nargs=argparse.REMAINDER)
    ap.add_argument("--src")
    ap.add_argument("--no-thumbs", action="store_true")
    ap.add_argument("--note")
    ap.add_argument("--user-approved-update", action="store_true")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    cfg = os2.Config(args.config)
    if args.command == "hud-thumbs":
        return subprocess.run([sys.executable, os.path.join(HUD, "thumbs.py")] + (["--src", args.src] if args.src else [])).returncode
    if args.command == "hud-build":
        return ps(os.path.join(HUD, "build.ps1"), ["-NoThumbs"] if args.no_thumbs else [], cfg)
    if args.command == "hud-query":
        return ps(os.path.join(HUD, "publish.ps1"), ["-Query"], cfg)
    if args.command == "hud-public":
        return ps(os.path.join(HUD, "publish.ps1"), ["-Public"], cfg)
    if args.command == "hud-publish":
        if not args.note:
            os2.fail("--note: the change note Steam shows with the update", 2)
        extra = ["-UserApprovedUpdate"] if args.user_approved_update else []
        return ps(os.path.join(HUD, "publish.ps1"), ["-Note", args.note, *extra], cfg)
    if args.command == "map":
        return ps(os.path.join(MAPS, "publish.ps1"), args.rest, cfg)
    return 0


if __name__ == "__main__":
    sys.exit(main())
