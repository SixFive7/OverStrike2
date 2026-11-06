"""The throwaway GgDocName (this folder) on the test copy: copied to /tmp/ggdocname in the game container and loaded, and the client named;
refuses unless the game server's container is the test copy and the harness holds the server lock. unload removes it. The pictures are public,
so the client plays under a test name: docshots.py --renamed then needs no mask for a name.

    python -I docname.py load [<account> <name>]     build, copy, load, and name that account (default: this PC's Steam login, Alice)
    python -I docname.py status                      what ggd_name says
    python -I docname.py unload                      unload and remove /tmp/ggdocname
"""
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "probe"))
import probe  # noqa: E402  (the private config, the server lock, the Docker API and RCON)
import testcopy  # noqa: E402  (what a test copy is)

PROJECT = HERE / "GgDocName"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "GgDocName.dll"
NAME = "Alice"          # docshots.py's ME


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    verb = argv[0] if argv else "status"
    if verb not in ("load", "status", "unload"):
        sys.exit(__doc__)
    probe.need_lock()
    if not testcopy.is_test(testcopy.inspect(testcopy.GAME)):
        sys.exit(f"REFUSED: {testcopy.GAME} is not the test copy")
    if verb == "load":
        account, name = (argv[1], argv[2]) if len(argv) > 2 else (str(probe.steam_account() or ""), NAME)
        if not account.isdigit() or not name.strip():
            sys.exit("load: no account - Steam names no logged-in account on this PC; give <account> <name>")
        subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd=PROJECT, check=True)
        data = DLL.read_bytes()
        print("copied", len(data), "bytes, HTTP", probe.upload({"GgDocName.dll": data}, folder="ggdocname"))
        print(probe.rcon("css_plugins load /tmp/ggdocname/GgDocName.dll").strip()[:300])
        time.sleep(1)
        print([l.strip() for l in probe.rcon("css_plugins list").splitlines() if "LOADED" in l])
        print(probe.rcon(f"ggd_name {account} {name}").strip()[:300])
    elif verb == "status":
        print(probe.rcon("ggd_name").strip()[:300])
    else:
        print(probe.rcon("css_plugins unload GgDocName").strip()[:300])
        print(probe.exec_in_game(["rm", "-rf", "/tmp/ggdocname"]).strip() or "removed /tmp/ggdocname")


if __name__ == "__main__":
    main(sys.argv[1:])
