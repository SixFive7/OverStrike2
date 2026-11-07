"""The server half of the CS2 client test harness (../README.md): the server lock, the Docker API and RCON that the harness's verbs and
testcopy.py use, and the test-only OsClientProbe plugin, loaded into the game container for one test and taken out again.

Nothing here knows a deployment. Everything comes from the private config through deploy/os2.py's Config ($OVERSTRIKE2_CONFIG, else
.private/config.toml): the server's query address (server.address), the lock file (server.lock_file), the game server's and the
bridge's containers (bridge.game_container, bridge.container), the Docker Engine API (docker.*, its key an env: value). The RCON
password is the game container's own (CS2_RCONPW in its inspect), handed to one exec in its environment; it is never printed.

Production actions need the server lock (server.lock_file, taken by exclusive create): `load` needs it, `unload` releases it. Nothing
is ever written to the plugins folder, and a restart drops the probe anyway: it lives in /tmp of the game container, loaded by
`css_plugins load <absolute path>`.

  python -I probe.py lock "<purpose>"                 take the server lock (refuses if anyone holds it)
  python -I probe.py purpose <minutes> "<purpose>"    the lock's since, expected end and purpose anew, its owner line kept (the harness's lock only)
  python -I probe.py unlock                           release it, if it is the harness's
  python -I probe.py load                             build if needed, copy the DLL to /tmp/osprobe in the game container, css_plugins load it
  python -I probe.py players                          who is on (slot, name, account id)
  python -I probe.py html <account|*> <seconds> <html or @file>   centre-screen HTML to that player only, re-sent every tick
  python -I probe.py htmlfile <account|*> <seconds> <file>         the same from a file of any length (a console line holds ~500 chars)
  python -I probe.py stop                             clear the probe's messages
  python -I probe.py clientcmd <account> <command>    ExecuteClientCommand (only FCVAR_CLIENT_CAN_EXECUTE commands run)
  python -I probe.py rcon "<command>" ["<command>" ...]   server console commands, each one's answer printed
  python -I probe.py unload                           css_plugins unload, delete /tmp/osprobe, verify both, release the lock - kept while
                                                      the game server is a test copy (testcopy.py restore first)
  python -I probe.py status                           the probe's state on the server and the lock
  python -I probe.py config                           what the harness reads from the private config, as JSON (no credential in it)
  python -I probe.py server                           the running containers that publish the server's port, and the game container, as JSON
"""
import base64
import io
import json
import os
import subprocess
import sys
import tarfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]                       # tools/client/probe -> the repository
sys.path.insert(0, str(REPO / "deploy"))
import os2  # noqa: E402  (the private config and the Docker Engine API, as the deploy scripts use them)
from check import jsonc  # noqa: E402,F401  (deploy/check.py's: the settings text's syntax, for testcopy.py and bridgepost.py)

OWNER = "cs2 client test harness (tools/client)"   # the lock's first line is "owner: " and this
TEST_LABEL = "cs2.test-config"               # the label of every container testcopy.py makes: a test copy, never production
PROJECT = HERE / "OsClientProbe"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "OsClientProbe.dll"
REMOTE_DIR = "/tmp/osprobe"
REMOTE_DLL = REMOTE_DIR + "/OsClientProbe.dll"
GAME_UID = 1000                              # the game image's user: the files it is handed are its own

# RCON from inside the game container, the way the game server's own tools reach it: no RCON port needs to reach this PC. The password
# and the port come from the exec's environment, the commands from its arguments; an answer that comes in several packets is read whole.
INNER_RCON = r"""
import os,socket,struct,sys
s=socket.create_connection(("127.0.0.1",int(os.environ.get("OS2_RCON_PORT") or 27015)),5)
def pkt(i,t,b): b=b.encode()+b"\0\0"; return struct.pack("<iii",len(b)+8,i,t)+b
def take(n):
    d=b""
    while len(d)<n:
        c=s.recv(n-len(d))
        if not c: raise EOFError
        d+=c
    return d
def read():
    n=struct.unpack("<i",take(4))[0]; d=take(n)
    return struct.unpack("<ii",d[:8])+(d[8:-2].decode("utf-8","replace"),)
s.sendall(pkt(1,3,os.environ["OS2_RCON_PW"])); r=read()
if r[1]==0: r=read()
if r[0]==-1: sys.exit("rcon: the password was refused")
for k,c in enumerate(sys.argv[1:]):
    s.settimeout(30); s.sendall(pkt(10+k,2,c)); out=[read()[2]]
    s.settimeout(0.3)
    try:
        while True: out.append(read()[2])
    except (socket.timeout,EOFError): pass
    print("".join(out),flush=True)
"""


# ---------------------------------------------------------------------------------------------------------- the deployment

_cfg = _docker = None


def has_config():
    """Whether there is a private config to read (os2.Config's own lookup): the offline modes do without one."""
    return os.path.exists(os.environ.get("OVERSTRIKE2_CONFIG") or os.path.join(os2.REPO, ".private", "config.toml"))


def cfg():
    """The private config (deploy/os2.py's Config), read once, on first use: the offline tests never need one."""
    global _cfg
    if _cfg is None:
        _cfg = os2.Config()
    return _cfg


def docker():
    global _docker
    if _docker is None:
        _docker = os2.Docker(cfg())
    return _docker


def game():
    """The game server's container: production's, or the test copy under the same name."""
    return cfg().get("bridge.game_container", required=True)


def bridge():
    return cfg().get("bridge.container", required=True)


def address():
    """The game server's query address, host:port (server.address)."""
    return cfg().get("server.address", required=True)


def host_port():
    host, _, port = address().rpartition(":")
    return host, int(port)


def lock_path():
    p = cfg().get("server.lock_file", "")
    if not p:
        sys.exit("REFUSED: no server.lock_file in the private config - the harness acts on the server only under a lock file; name one")
    return Path(p)


def test_labels():
    """client.test_labels: extra labels every test container gets (a monitor's mute, say); {} when the config has none."""
    labels = cfg().get("client.test_labels", {}) or {}
    return {str(k): str(v) for k, v in labels.items()}


def main_checkout(start=REPO):
    """The repository's main working tree: a worktree's .git file names <main>/.git/worktrees/<name>, so every worktree shares one state."""
    dotgit = Path(start) / ".git"
    if dotgit.is_file():
        line = dotgit.read_text(encoding="utf-8", errors="replace").splitlines()[0] if dotgit.stat().st_size else ""
        if line.startswith("gitdir:"):
            gitdir = Path(line[7:].strip())
            gitdir = gitdir if gitdir.is_absolute() else (Path(start) / gitdir)
            common = gitdir.resolve().parent.parent
            if common.name == ".git":
                return common.parent
    return Path(start)


def state_dir():
    """Where the harness keeps its state: $CS2CLIENT_STATE, else .work/cs2-client in the main checkout (Cs2Client.psm1 resolves it alike)."""
    env = os.environ.get("CS2CLIENT_STATE")
    return Path(env) if env else main_checkout() / ".work" / "cs2-client"


# ------------------------------------------------------------------------------------------------------------- Steam

def steam_account():
    """The account id (the n of [U:1:n]) Steam is logged in with on this PC, which the harness's client plays under; 0 when unknown."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam\ActiveProcess") as k:
            return int(winreg.QueryValueEx(k, "ActiveUser")[0])
    except (ImportError, OSError, ValueError):
        return 0


def steam_names(account=None):
    """The persona name of that account, from Steam's loginusers.vdf: what the game shows of the client. [] when unknown."""
    account = steam_account() if account is None else account
    if not account:
        return []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = Path(winreg.QueryValueEx(k, "SteamPath")[0])
        text = (steam / "config" / "loginusers.vdf").read_text(encoding="utf-8", errors="replace")
    except (ImportError, OSError):
        return []
    import re
    block = re.search(r'"%d"\s*\{([^}]*)\}' % (76561197960265728 + account), text)
    name = re.search(r'"PersonaName"\s+"([^"]*)"', block.group(1)) if block else None
    return [name.group(1)] if name and name.group(1).strip() else []


def private_names():
    """Names that must never reach a picture, a command or a log: the client's own Steam name, and client.private_names from the config."""
    names = list(steam_names())
    if has_config():
        names += [str(n) for n in (cfg().get("client.private_names", []) or [])]
    return [n for n in dict.fromkeys(names) if n.strip()]


# ------------------------------------------------------------------------------------------------------- the Docker API

class DockerError(RuntimeError):
    pass


def req(path, method="GET", body=None, ctype="application/json", timeout=120):
    """One Docker Engine API call through os2's client: (status, the answer as text, or bytes when it is not JSON). An error status
    raises DockerError, its code in the words ("HTTP Error 404: ...")."""
    raw = bytes(body) if isinstance(body, (bytes, bytearray)) else None
    status, out = docker().call(method, path, body=None if raw is not None else body, raw=raw, ctype=ctype, timeout=timeout)
    if status >= 400:
        raise DockerError(f"HTTP Error {status}: {str(out)[:300]}")
    return status, (json.dumps(out) if isinstance(out, (dict, list)) else out)


def inspect(name):
    """A container's inspect, or None when there is no such container."""
    try:
        return json.loads(req(f"/containers/{name}/json")[1])
    except DockerError as e:
        if "404" in str(e):
            return None
        raise


def demux(raw):
    """A container's output without a TTY: frames of an 8-byte header (stream, 0, 0, 0, length) and their text; plain text as it is."""
    if isinstance(raw, str):
        raw = raw.encode("utf-8", "replace")
    out, i = [], 0
    while i + 8 <= len(raw) and raw[i] in (1, 2) and raw[i + 1:i + 4] == b"\0\0\0":
        n = int.from_bytes(raw[i + 4:i + 8], "big")
        out.append(raw[i + 8:i + 8 + n])
        i += 8 + n
    return (b"".join(out) if out else raw).decode("utf-8", "replace")


def exec_in(container, cmd, env=None, timeout=120):
    """One exec in a container, its output (stdout and stderr) as text. env: variables for this exec alone - how a secret travels."""
    body = {"AttachStdout": True, "AttachStderr": True, "Cmd": cmd}
    if env:
        body["Env"] = [f"{k}={v}" for k, v in env.items()]
    st, b = req(f"/containers/{container}/exec", "POST", body)
    st, raw = req(f"/exec/{json.loads(b)['Id']}/start", "POST", {"Detach": False, "Tty": False}, timeout=timeout)
    return demux(raw)


def exec_in_game(cmd):
    return exec_in(game(), cmd)


def rcon(*commands):
    """Server console commands by RCON from inside the game container, each one's answer in order. The password is the container's own
    (its inspect), handed to the exec in its environment and scrubbed from the answer, should it ever be in there."""
    g = inspect(game())
    if g is None:
        sys.exit(f"no container {game()}")
    pw = os2.rcon_password(g) or os2.rcon_password(None, inspect(bridge()))
    if not pw:
        sys.exit(f"no RCON password in {game()}'s inspect (CS2_RCONPW)")
    port = os2.env_of(g, "RCON_PORT") or "27015"
    out = exec_in(game(), ["python3", "-c", INNER_RCON, *commands], env={"OS2_RCON_PW": pw, "OS2_RCON_PORT": port})
    return out.replace(pw, "<hidden>").replace("\0", "")


# ------------------------------------------------------------------------------------------------------------- the lock

def lock_text():
    p = lock_path()
    return p.read_text(encoding="utf-8", errors="replace") if p.exists() else None


def holds_lock():
    t = lock_text()
    return t is not None and t.startswith(f"owner: {OWNER}")


def need_lock():
    if not holds_lock():
        sys.exit(f"the server lock is not held by the harness - run `probe.py lock \"<purpose>\"` first\ncurrent lock: {lock_text()!r}")


def lock_words(purpose, minutes=30):
    now = time.time()
    return (f"owner: {OWNER}\nsince: {time.strftime('%Y-%m-%dT%H:%M:%S', time.localtime(now))}\n"
            f"expected end: {time.strftime('%Y-%m-%dT%H:%M:%S', time.localtime(now + 60 * minutes))}\npurpose: {purpose}\n")


def cmd_lock(purpose):
    try:
        fd = os.open(lock_path(), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        sys.exit(f"REFUSED: the server lock is held:\n{lock_text()}")
    os.write(fd, lock_words(purpose).encode())
    os.close(fd)
    print(f"server lock taken: {purpose}")


def cmd_purpose(minutes, purpose):
    """After a hand-over: the harness's lock keeps its owner line and gets this session's since, expected end and purpose."""
    if not holds_lock():
        t = lock_text()
        sys.exit(f"REFUSED: the lock is not the harness's: {t.splitlines()[0][:100] if t else '(none)'}")
    lock_path().write_text(lock_words(purpose, int(minutes)), encoding="utf-8")
    print(lock_text())


def cmd_unlock():
    if holds_lock():
        lock_path().unlink()
        print("server lock released")
    else:
        print(f"not ours, left alone: {lock_text()!r}")


# ------------------------------------------------------------------------------------------------------- the probe plugin

def upload(files, folder="osprobe"):
    """files: {name: bytes} -> one tar into the game container's /tmp/<folder> (world-readable, owned by the game's user)."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tar:
        d = tarfile.TarInfo(folder); d.type = tarfile.DIRTYPE; d.mode = 0o755; d.uid = d.gid = GAME_UID; d.mtime = int(time.time())
        tar.addfile(d)
        for name, data in files.items():
            f = tarfile.TarInfo(f"{folder}/{name}"); f.size = len(data); f.mode = 0o644; f.uid = f.gid = GAME_UID; f.mtime = int(time.time())
            tar.addfile(f, io.BytesIO(data))
    st, _ = req(f"/containers/{game()}/archive?path=/tmp", "PUT", buf.getvalue(), "application/x-tar")
    return st


def cmd_load():
    need_lock()
    subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd=PROJECT, check=True)
    loaded = [l for l in rcon("css_plugins list").splitlines() if "OsClientProbe" in l]
    if loaded:  # a newer build replaces the loaded one
        print(rcon("css_plugins unload OsClientProbe").strip())
    data = DLL.read_bytes()
    st = upload({"OsClientProbe.dll": data})
    print(f"copied {len(data)} bytes to {game()}:{REMOTE_DLL} (HTTP {st})")
    print(rcon(f"css_plugins load {REMOTE_DLL}").strip())
    time.sleep(1)
    out = rcon("css_plugins list")
    line = [l for l in out.splitlines() if "OsClientProbe" in l]
    print("css_plugins list:", line or "OsClientProbe NOT LISTED")
    if not line:
        sys.exit(1)


def cmd_unload():
    out = rcon("css_plugins unload OsClientProbe")
    print(out.strip())
    print(exec_in_game(["rm", "-rf", REMOTE_DIR]).strip() or f"removed {REMOTE_DIR}")
    left = rcon("css_plugins list")
    # CounterStrikeSharp keeps an unloaded plugin in its list as [#n:UNLOADED] until the server restarts: inert, its DLL gone.
    still = [l for l in left.splitlines() if "OsClientProbe" in l and "UNLOADED" not in l]
    files = exec_in_game(["sh", "-c", f"ls -la {REMOTE_DIR} 2>&1 || true"]).strip()
    print("css_plugins list:", still or [l.strip() for l in left.splitlines() if "OsClientProbe" in l] or "OsClientProbe gone")
    print(f"{game()}:{REMOTE_DIR}:", files)
    if still or "No such file" not in files:
        sys.exit("the probe is NOT fully gone - check by hand before releasing the lock")
    g = inspect(game())
    if (((g or {}).get("Config") or {}).get("Labels") or {}).get(TEST_LABEL):
        print("server lock kept: the game server is a test copy, and production is back only after testcopy.py rm and restore (then probe.py unlock)")
        return
    cmd_unlock()


def cmd_html(who, seconds, html):
    if html.startswith("@"):
        html = Path(html[1:]).read_text(encoding="utf-8").strip()
    b64 = base64.urlsafe_b64encode(html.encode("utf-8")).decode().rstrip("=")
    print(rcon(f"osprobe_html64 {who} {int(seconds)} {b64}").strip())


def cmd_htmlfile(who, seconds, path):
    """A message of any length: uploaded as /tmp/osprobe/msg.html, then osprobe_htmlfile."""
    upload({"msg.html": Path(path).read_bytes()})
    print(rcon(f"osprobe_htmlfile {who} {int(seconds)} {REMOTE_DIR}/msg.html").strip())


def cmd_status():
    print("lock:", lock_text() or "free")
    out = rcon("css_plugins list")
    print("css_plugins list:", [l.strip() for l in out.splitlines() if "OsClientProbe" in l] or "OsClientProbe not loaded")
    print(f"{game()}:{REMOTE_DIR}:", exec_in_game(["sh", "-c", f"ls -la {REMOTE_DIR} 2>&1 || true"]).strip())


# --------------------------------------------------------------------------------------------- what the PowerShell side asks

def cmd_config():
    """The harness's view of the private config, as JSON: no credential is in it."""
    print(json.dumps({"address": address(), "lock_file": cfg().get("server.lock_file", ""), "game_container": game(),
                      "bridge_container": bridge(), "owner": OWNER}))


def cmd_server():
    """The running containers that publish the server's port, and the game container: what answers on the address right now (a test copy
    answers there under production's name)."""
    port = host_port()[1]
    rows = []
    for c in json.loads(req("/containers/json")[1]):
        ports = sorted({p.get("PublicPort") for p in c.get("Ports", []) if p.get("PublicPort")})
        if port in ports or c["Names"][0] == "/" + game():
            labels = c.get("Labels") or {}
            rows.append({"name": c["Names"][0].lstrip("/"), "state": c["State"], "status": c["Status"], "image": c["Image"][:71], "ports": ports,
                         "test": "cs2.test-config" in labels})
    print(json.dumps(rows))


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if not argv:
        print(__doc__)
        return
    verb, rest = argv[0], argv[1:]
    if verb == "lock":
        cmd_lock(" ".join(rest) or "client test")
    elif verb == "purpose":
        if len(rest) != 2 or not rest[0].isdigit():
            sys.exit('usage: probe.py purpose <minutes> "<purpose>"')
        cmd_purpose(rest[0], rest[1])
    elif verb == "unlock":
        cmd_unlock()
    elif verb == "load":
        cmd_load()
    elif verb == "unload":
        cmd_unload()
    elif verb == "players":
        print(rcon("osprobe_players").strip())
    elif verb == "html":
        need_lock()
        cmd_html(rest[0], rest[1], " ".join(rest[2:]))
    elif verb == "htmlfile":
        need_lock()
        cmd_htmlfile(rest[0], rest[1], rest[2])
    elif verb == "stop":
        print(rcon("osprobe_stop").strip())
    elif verb == "clientcmd":
        need_lock()
        print(rcon(f"osprobe_clientcmd {rest[0]} {' '.join(rest[1:])}").strip())
    elif verb == "rcon":
        if not rest:
            sys.exit('usage: probe.py rcon "<command>" ["<command>" ...]')
        print(rcon(*rest).strip())
    elif verb == "status":
        cmd_status()
    elif verb == "config":
        cmd_config()
    elif verb == "server":
        cmd_server()
    else:
        sys.exit(f"unknown verb {verb}\n{__doc__}")


if __name__ == "__main__":
    main(sys.argv[1:])
