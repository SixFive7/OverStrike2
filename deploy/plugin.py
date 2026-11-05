#!/usr/bin/env python3
"""The game server's plugin (plugin/OverStrike2): build it, test it, ship the DLL to the server's plugin folder.

    python deploy/plugin.py build             dotnet build -c Release, then the plugin's own self-test (below normal priority)
    python deploy/plugin.py ship              show what would change in <server.plugins_dir>/OverStrike2/
    python deploy/plugin.py ship --apply      copy it there, verified byte for byte

The folder name must equal the DLL name. What ships: OverStrike2.dll, its .pdb (line numbers in stack traces) and the config
files the build copies beside it (blocked.txt, the game-changers block list that "mapreset_block reload" reads again without a
rebuild; a module's .json). CounterStrikeSharp.API comes with the server's install. The game server copies the plugin folder in at its next start (pre.sh): a restart of the game
container, which disconnects everybody - do it at 0 humans. Only committed sources ship (plugin/ and the bridge sources the
plugin compiles in).
"""
import argparse
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

NAME = "OverStrike2"
PROJECT = os.path.join(os2.REPO, "plugin", NAME)
SELFTEST = os.path.join(os2.REPO, "plugin", NAME + ".SelfTest")
OUT = os.path.join(PROJECT, "bin", "Release", "net10.0")


def declared_content(csproj_text):
    """The config files the project copies beside the DLL - its <None Include="..." [Link="..."] CopyToOutputDirectory="..."> items, by
    the name they get there (blocked.txt; catchup.json, linked from the CatchUp module). Only these ship: a file an older build left in
    bin/Release (blocked-deathmatch.txt and blocked-wingman.txt, once) is no part of the plugin any more."""
    names = set()
    for m in re.finditer(r"<None\s+([^>]*?CopyToOutputDirectory=\"(?:PreserveNewest|Always)\"[^>]*?)/?>", csproj_text):
        attrs = dict(re.findall(r"(\w+)=\"([^\"]*)\"", m.group(1)))
        if attrs.get("Include"):
            names.add(attrs.get("Link") or os.path.basename(attrs["Include"].replace("\\", "/")))
    return sorted(names)


def ship_list():
    """The DLL and its .pdb, and the config files the project declares (declared_content) as the build put them beside it. Not the
    .deps.json or the runtimeconfig - CounterStrikeSharp needs neither."""
    with open(os.path.join(PROJECT, NAME + ".csproj"), encoding="utf-8") as f:
        declared = declared_content(f.read())
    return [NAME + ".dll", NAME + ".pdb"] + [n for n in declared if os.path.exists(os.path.join(OUT, n))]
BELOW_NORMAL, NO_WINDOW = 0x00004000, 0x08000000


def dotnet(args, cwd):
    flags = BELOW_NORMAL | NO_WINDOW if os.name == "nt" else 0
    r = subprocess.run(["dotnet", *args, "-nodeReuse:false", "-p:UseSharedCompilation=false"], cwd=cwd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", creationflags=flags)
    return r.returncode, r.stdout + r.stderr


def build():
    commit = os2.clean_tree(["plugin", "bridge"])
    code, out = dotnet(["build", "-c", "Release"], PROJECT)
    warnings = [l for l in out.splitlines() if ": warning " in l]
    if code != 0:
        os2.fail("build:\n" + out[-3000:])
    os2.log(f"built {NAME}.dll at {commit} ({len(set(warnings))} warnings)")
    code, out = dotnet(["run", "-c", "Release"], SELFTEST)
    os2.log("self-test: " + (out.strip().splitlines() or ["(no output)"])[-1])
    if code != 0:
        os2.fail("the plugin's self-test failed:\n" + out[-2000:])
    return commit


def ship(cfg, apply):
    dest = os.path.join(cfg.get("server.plugins_dir", required=True), NAME)
    if not os.path.isdir(OUT):
        os2.fail("not built - run: python deploy/plugin.py build")
    ship = ship_list()
    missing = [f for f in ship if not os.path.exists(os.path.join(OUT, f))]
    if missing:
        os2.fail(f"not built: {', '.join(missing)} - run: python deploy/plugin.py build")
    address = cfg.get("server.address", "")
    if address:
        on = os2.humans(address)
        os2.log(f"humans on the server: {'unknown' if on is None else on} (copying changes nothing until the game container restarts)")
    note = os2.lock_note(cfg)
    if note:
        os2.log("server lock: " + note.replace("\n", " | ")[:300])
    for f in ship:
        os2.log(f"{f}: {os2.copy_verified(os.path.join(OUT, f), os.path.join(dest, f), dry=not apply)}")
    if not apply:
        os2.log("dry run - add --apply to copy")
    else:
        os2.log(f"shipped to {NAME}/ in the plugin folder; it loads at the game server's next start (restart it at 0 humans)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["build", "ship"])
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.command == "build":
        build()
    else:
        ship(os2.Config(args.config), args.apply)
    return 0


if __name__ == "__main__":
    sys.exit(main())
