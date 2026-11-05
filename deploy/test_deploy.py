#!/usr/bin/env python3
"""Offline tests for the deploy scripts: the bridge swap against a stand-in Docker API, and which data files data.py ships.

    python -I deploy/test_deploy.py

No server, no Docker, no private config: the stand-in keeps containers and image tags in memory and answers the calls bridge.py
makes. What it checks: a running bridge and a stopped one are swapped onto the IP and MAC they are configured with (a stopped one
has no runtime address, so a swap that checked the runtime address put the old one back), a failed swap puts it back as it was,
and data.py ships the hand-made files but never a list the Workshop sync writes on the server (one writer per file).
"""
import copy
import io
import os
import sys
import tarfile
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bridge as B  # noqa: E402
import data as D  # noqa: E402
import os2  # noqa: E402

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


IP, MAC, NET = "192.0.2.10", "mac-of-the-bridge", "game_net"


class FakeDocker:
    """The Docker Engine API calls bridge.py makes, on containers and tags kept in memory. old_engine: a stopped container keeps
    its MAC only in Config.MacAddress (engines before API 1.44), not on its network endpoint."""

    def __init__(self, bridge_running=True, old_engine=False, pinned=True, health="gg-health 1\nlog pushes 3\n", settings=None, bare=False):
        self.old_engine, self.health_text, self.calls, self.n = old_engine, health, [], 0
        self.files, self.tar_names, self.execs, self.put_refused = {"c-old": settings} if settings is not None else {}, [], [], False
        self.refs = {"localhost/overstrike2-bridge:latest": "sha256:image-old-00000", "localhost/overstrike2-bridge:candidate": "sha256:image-new-00000"}
        self.images = {i: {"Id": i, "Config": {"Env": ["PATH=/usr/bin"], "Labels": {"org.opencontainers.image.title": "x"}}}
                       for i in set(self.refs.values())}
        env = ["PATH=/usr/bin"] if bare else ["PATH=/usr/bin", "HTTP_PORT=8080", "LOG_SECRET=s", "RCON_PASSWORD=p"]   # bare: all it has is the settings file
        endpoint = {"IPAMConfig": {"IPv4Address": IP} if pinned else None, "Aliases": ["bridge"], "MacAddress": MAC, "IPAddress": IP}
        self.containers = {
            "c-old": {"Id": "c-old", "Name": "/bridge", "Image": "sha256:image-old-00000", "RestartCount": 0,
                      "State": {"Running": True, "Status": "running", "StartedAt": "start-0"},
                      "Config": {"Image": "localhost/overstrike2-bridge", "Env": env, "Labels": {"com.docker.compose.service": "bridge"}},
                      "HostConfig": {"RestartPolicy": {"Name": "unless-stopped"}, "NetworkMode": NET},
                      "NetworkSettings": {"Networks": {NET: endpoint}}},
            "c-game": {"Id": "c-game", "Name": "/cs2", "Image": "sha256:game", "RestartCount": 0,
                       "State": {"Running": True, "Status": "running", "StartedAt": "start-game"},
                       "Config": {"Image": "game", "Env": ["RCON_PORT=27015"] + (["CS2_LOG_HTTP_URL=http://bridge:8090/log/fromthegame", "CS2_RCONPW=gamepw"] if bare else [])},
                       "HostConfig": {}, "NetworkSettings": {"Networks": {}}},
        }
        if not bridge_running:
            self.stop(self.containers["c-old"])

    def find(self, name):
        return next((c for c in self.containers.values() if c["Id"] == name or c["Name"] == "/" + name), None)

    def stop(self, c):
        c["State"].update(Running=False, Status="exited")
        for e in c["NetworkSettings"]["Networks"].values():
            e["IPAddress"] = ""
            if self.old_engine:
                c["Config"]["MacAddress"] = c["Config"].get("MacAddress") or e["MacAddress"]
                e["MacAddress"] = ""

    def inspect(self, kind, name):
        if kind == "containers":
            c = self.find(name)
        else:
            c = self.images.get(self.refs.get(name, name))
        return copy.deepcopy(c) if c else None

    def call(self, method, path, body=None, raw=None, ctype=None, timeout=60):
        route, _, query = path.partition("?")
        q = dict(p.split("=", 1) for p in query.split("&") if "=" in p)
        parts = route.strip("/").split("/")
        self.calls.append((method, route, q.get("name") or q.get("tag") or ""))
        if parts[-1] == "archive":
            c = self.find(parts[1])
            if method == "PUT":
                if self.put_refused:
                    return 400, "container rootfs is marked read-only"
                with tarfile.open(fileobj=io.BytesIO(raw)) as tar:
                    self.tar_names = tar.getnames()
                    self.files[c["Id"]] = tar.extractfile(tar.getmembers()[0]).read()
                return 200, b""
            if c["Id"] not in self.files:
                return 404, "Could not find the file"
            out = io.BytesIO()
            with tarfile.open(fileobj=out, mode="w") as tar:
                info = tarfile.TarInfo(os2.SETTINGS_FILE.rsplit("/", 1)[1])
                info.size = len(self.files[c["Id"]])
                tar.addfile(info, io.BytesIO(self.files[c["Id"]]))
            return 200, out.getvalue()
        if parts[0] == "images" and parts[-1] == "tag":
            self.refs[f"{q['repo']}:{q['tag']}"] = parts[1]
            return 201, b""
        if route == "/containers/create":
            if self.find(q["name"]):
                return 409, "name in use"
            self.n += 1
            cid = f"c-new-{self.n}"
            nets = {n: {"IPAMConfig": e.get("IPAMConfig"), "Aliases": e.get("Aliases"), "MacAddress": e.get("MacAddress") or "", "IPAddress": ""}
                    for n, e in body["NetworkingConfig"]["EndpointsConfig"].items()}
            self.containers[cid] = {"Id": cid, "Name": "/" + q["name"], "Image": self.refs[body["Image"] + ":latest"], "RestartCount": 0,
                                    "State": {"Running": False, "Status": "created", "StartedAt": ""}, "body": body,
                                    "Config": {"Image": body["Image"], "Env": body["Env"], "Labels": body["Labels"]},
                                    "HostConfig": body["HostConfig"], "NetworkSettings": {"Networks": nets}}
            return 201, {"Id": cid}
        c = self.find(parts[1]) if parts[0] == "containers" else None
        if not c:
            return 404, "no such container"
        if method == "DELETE":
            del self.containers[c["Id"]]
        elif parts[-1] == "stop":
            self.stop(c)
        elif parts[-1] == "start":
            self.n += 1
            c["State"].update(Running=True, Status="running", StartedAt=f"start-{self.n}")
            for e in c["NetworkSettings"]["Networks"].values():
                e["IPAddress"] = (e.get("IPAMConfig") or {}).get("IPv4Address") or "198.51.100.99"   # unpinned: whatever is free
                e["MacAddress"] = e["MacAddress"] or c["Config"].get("MacAddress") or "a-new-random-mac"
        elif parts[-1] == "rename":
            if self.find(q["name"]):
                return 409, "name in use"
            c["Name"] = "/" + q["name"]
        return 204, b""

    def must(self, method, path, what, ok=(200, 201, 204, 304), **kw):
        status, out = self.call(method, path, **kw)
        if status not in ok:
            os2.fail(f"{what}: HTTP {status} {out}")
        return out

    def exec_in(self, container, argv, timeout=60):
        self.execs.append(argv)
        if argv[2] == B.INNER_RCON:   # something said on the server: its log push reaches the bridge
            self.health_text = "gg-health 1\nlog pushes 1\n"
        return self.health_text

    def writes(self):
        return [c for c in self.calls if c[0] in ("POST", "DELETE")]


def bridge_on(fake):
    b = B.Bridge.__new__(B.Bridge)
    b.cfg, b.d, b.repo, b.name, b.game = None, fake, "localhost/overstrike2-bridge", "bridge", "cs2"
    return b


def swap(fake, source="candidate"):
    try:
        return bridge_on(fake).swap(source, "previous")
    except SystemExit as e:
        return f"exit {e.code}"


real_sleep = time.sleep
B.time.sleep = lambda s: None
try:
    # a running bridge: stopped, renamed aside, recreated on the same address, verified, the old one removed
    f = FakeDocker()
    r = swap(f)
    new = f.find("bridge")
    check("swap, running: done, the new bridge runs the candidate on the IP and MAC it had",
          r == 0 and new["Id"] != "c-old" and new["State"]["Running"] and new["Image"] == "sha256:image-new-00000"
          and B.Bridge.addresses(new) == [(NET, IP, MAC)] and "c-old" not in f.containers)
    check("swap, running: :previous is the old image, :latest the new one",
          f.refs["localhost/overstrike2-bridge:previous"] == "sha256:image-old-00000" and f.refs["localhost/overstrike2-bridge:latest"] == "sha256:image-new-00000")
    check("swap, running: the old bridge was stopped first", ("POST", "/containers/c-old/stop", "") in f.calls)
    check("swap, running: the game container is untouched", f.find("cs2")["State"]["StartedAt"] == "start-game")

    # a stopped bridge: no runtime address, swapped all the same onto the pinned one, started on the new image
    f = FakeDocker(bridge_running=False)
    r = swap(f)
    new = f.find("bridge")
    check("swap, stopped: done - checked against its configured IP and MAC, not the runtime address it lacks",
          r == 0 and new["State"]["Running"] and new["Image"] == "sha256:image-new-00000" and B.Bridge.addresses(new) == [(NET, IP, MAC)])
    check("swap, stopped: no stop call for a bridge that is not running", not any(c[1].endswith("/stop") for c in f.calls))
    check("swap, stopped: the new bridge is created on the pinned address",
          new["body"]["NetworkingConfig"]["EndpointsConfig"][NET].get("IPAMConfig") == {"IPv4Address": IP}
          and new["body"]["NetworkingConfig"]["EndpointsConfig"][NET].get("MacAddress") == MAC)

    # an older engine keeps a stopped container's MAC in Config only: the new bridge still gets it
    f = FakeDocker(bridge_running=False, old_engine=True)
    r = swap(f)
    new = f.find("bridge")
    check("swap, stopped on an older engine: the MAC comes from Config.MacAddress, and it is kept",
          r == 0 and new["body"]["NetworkingConfig"]["EndpointsConfig"][NET].get("MacAddress") == MAC and B.Bridge.addresses(new) == [(NET, IP, MAC)])

    # a stopped bridge whose new image fails its check: the new one goes, the old one is back under its name, stopped as it was
    f = FakeDocker(bridge_running=False, health="(exec start 500)")
    r = swap(f)
    back = f.find("bridge")
    check("swap, stopped, check fails: the old bridge is back, stopped, on the old image; the new one is gone",
          r == 1 and back["Id"] == "c-old" and not back["State"]["Running"] and len(f.containers) == 2
          and f.refs["localhost/overstrike2-bridge:latest"] == "sha256:image-old-00000")
    check("swap, stopped, check fails: the old bridge is not started", ("POST", "/containers/c-old/start", "") not in f.calls)

    # a running bridge whose new image fails: the old one is back up
    f = FakeDocker(health="(exec start 500)")
    r = swap(f)
    back = f.find("bridge")
    check("swap, running, check fails: the old bridge is back up", r == 1 and back["Id"] == "c-old" and back["State"]["Running"]
          and len(f.containers) == 2)

    # a stopped bridge already on the target image: started and checked, nothing recreated
    f = FakeDocker(bridge_running=False)
    f.refs["localhost/overstrike2-bridge:candidate"] = "sha256:image-old-00000"
    r = swap(f)
    check("swap, stopped on the target already: started and checked, not recreated",
          r == 0 and f.find("bridge")["Id"] == "c-old" and f.find("bridge")["State"]["Running"]
          and not any(c[1] == "/containers/create" for c in f.calls))

    # a running bridge on the target: nothing at all
    f = FakeDocker()
    f.refs["localhost/overstrike2-bridge:candidate"] = "sha256:image-old-00000"
    check("swap, running on the target already: nothing to do, nothing written", swap(f) == 0 and f.writes() == [])

    # the settings file lives in the container's own layer: a new container made from the old one's inspect gets it from the swap
    f = FakeDocker(settings=b"{ settings of the old bridge }")
    r = swap(f)
    new = f.find("bridge")
    check("swap, with a settings file: the new bridge holds the same bytes, put there before it starts, at the path every part reads",
          r == 0 and f.files.get(new["Id"]) == b"{ settings of the old bridge }" and f.tar_names == ["etc/overstrike2/overstrike2.jsonc"]
          and f.calls.index(("PUT", f"/containers/{new['Id']}/archive", "")) < f.calls.index(("POST", f"/containers/{new['Id']}/start", "")))
    f = FakeDocker()
    r = swap(f)
    check("swap, a bridge on its environment: no settings file is made up", r == 0 and not f.files and not any(c[0] == "PUT" for c in f.calls))
    f = FakeDocker(settings=b"{ }")
    f.put_refused = True
    r = swap(f)
    back = f.find("bridge")
    check("swap, the settings file refused (a read-only root): never started without it - the old bridge is back up",
          r == 1 and back["Id"] == "c-old" and back["State"]["Running"] and len(f.containers) == 2
          and not any(c[0] == "POST" and c[1].endswith("/start") and "new" in c[1] for c in f.calls))

    # a bridge that reads the settings file has no secret in its environment: the game container's are used
    f = FakeDocker(settings=b"{ }", bare=True, health="gg-health 1\nlog pushes 0\n")
    r = swap(f)
    gets = [a[3] for a in f.execs if a[2] == B.INNER_GET]
    says = [a for a in f.execs if a[2] == B.INNER_RCON]
    check("swap, no LOG_SECRET and no RCON_PASSWORD in the bridge: /health with the secret and port of the game's CS2_LOG_HTTP_URL, RCON with its CS2_RCONPW",
          r == 0 and gets and all(g == "http://bridge:8090/health/fromthegame" for g in gets) and says and says[0][3] == "gamepw")
    f = FakeDocker(health="gg-health 1\nlog pushes 0\n")
    r = swap(f)
    gets = [a[3] for a in f.execs if a[2] == B.INNER_GET]
    says = [a for a in f.execs if a[2] == B.INNER_RCON]
    check("swap, a game container without them: the bridge's own LOG_SECRET, HTTP_PORT and RCON_PASSWORD, as before",
          r == 0 and gets and all(g == "http://bridge:8080/health/s" for g in gets) and says and says[0][3] == "p")

    # a stopped bridge with no pinned IP: nothing to recreate it on - refused before any change
    f = FakeDocker(bridge_running=False, pinned=False)
    r = swap(f)
    check("swap, stopped and unpinned: refused, nothing written (%s)" % r, r == "exit 1" and f.writes() == [])
finally:
    B.time.sleep = real_sleep

# ---- os2: the secrets of a bridge that has none in its environment
def box(*env):
    return {"Config": {"Env": list(env)}}


check("os2: the bridge's secret and port from the log push address, its templated form too",
      os2.bridge_secret(box("CS2_LOG_HTTP_URL=http:\\/\\/bridge:8090\\/log\\/abc123")) == "abc123"
      and os2.bridge_port(box("CS2_LOG_HTTP_URL=http://bridge:8090/log/abc123")) == "8090")
check("os2: the log push address first, then the bridge's own variables; a URL with no port is the default port",
      os2.bridge_secret(box("CS2_LOG_HTTP_URL=http://b:1/log/a"), box("LOG_SECRET=b")) == "a" and os2.bridge_secret(box(), box("LOG_SECRET=b")) == "b"
      and os2.bridge_secret(None, None) == "" and os2.bridge_port(box("CS2_LOG_HTTP_URL=http://b/log/a"), box("HTTP_PORT=9")) == "9"
      and os2.bridge_port(box()) == "8080")
check("os2: the RCON password from the game container first, then the bridge's",
      os2.rcon_password(box("CS2_RCONPW=a"), box("RCON_PASSWORD=b")) == "a" and os2.rcon_password(box(), box("RCON_PASSWORD=b")) == "b"
      and os2.rcon_password(None, None) == "")
f = FakeDocker()
check("os2: a container with no settings file reads as None", os2.read_settings(f, "c-old") is None)
f.call = lambda *a, **kw: (500, "the daemon's trouble")
try:
    os2.read_settings(f, "c-old")
    check("os2: an archive call that fails is no 'no file'", False)
except SystemExit:
    check("os2: an archive call that fails is no 'no file'", True)

# ---- data.py: what ships
with tempfile.TemporaryDirectory() as dest:
    hand = [f for f in ("ours.json", "sizes.json", "blocked.json") if os.path.isfile(os.path.join(os2.REPO, "bridge", f))]
    first = [f for f in ("maps.json", "broken.json", "search.json") if os.path.isfile(os.path.join(os2.REPO, "bridge", f))]
    files = D.SETS["bridge"][2]
    check("data: a server with no lists yet gets the hand-made files and the first lists (%s)" % ", ".join(D.plan("bridge", files, dest)),
          D.plan("bridge", files, dest) == [f for f in files if f in hand + first])
    for f in ("maps.json", "broken.json", "search.json"):
        open(os.path.join(dest, f), "w").write("{}")
    check("data: once the server has its lists, only the hand-made files ship - the sync writes the rest there (77 a)",
          D.plan("bridge", files, dest) == [f for f in files if f in hand] and "ours.json" in hand and "sizes.json" in hand)
    check("data: blocked.json ships when the checkout has it, and is skipped (said) when not",
          ("blocked.json" in D.plan("bridge", files, dest)) == os.path.isfile(os.path.join(os2.REPO, "bridge", "blocked.json")))
    check("data: the bridge's files are its only set - the server side ships with server.py", list(D.SETS) == ["bridge"])

import plugin as P  # noqa: E402
csproj = ('<None Include="..\\CatchUp\\catchup.json" Link="catchup.json" CopyToOutputDirectory="PreserveNewest" />\n'
          '<EmbeddedResource Include="convars.txt" LogicalName="convars.txt" />\n'
          '<None Include="blocked.txt" CopyToOutputDirectory="PreserveNewest" />\n<None Remove="hud\\**" />\n')
check("plugin: only the config files the project declares ship - blocked.txt and the linked catchup.json, never a stale file of an older build",
      P.declared_content(csproj) == ["blocked.txt", "catchup.json"])
with open(os.path.join(P.PROJECT, P.NAME + ".csproj"), encoding="utf-8") as f:
    real = P.declared_content(f.read())
check("plugin: the real project declares blocked.txt and catchup.json (%s)" % ", ".join(real), real == ["blocked.txt", "catchup.json"])

# ---- server.py: what ships where, only what the build made, renamed into place
import json  # noqa: E402
import server as S  # noqa: E402


class FakeConfig:
    def __init__(self, **values):
        self.values = values

    def get(self, dotted, default=None, required=False):
        return self.values.get(dotted, default)


def exits(fn, *a, **kw):
    try:
        fn(*a, **kw)
        return None
    except SystemExit as e:
        return e.code


check("server: the launcher pre.sh and the three mode cfgs it ships are in the checkout",
      all(os.path.isfile(os.path.join(os2.REPO, p)) for p in [S.LAUNCHER] + S.CFGS) and len(S.CFGS) == 3)
saved = (S.OUT, S.RECORD, os2.git, os2.clean_tree, D.committed_before)
with tempfile.TemporaryDirectory() as tmp:
    try:
        S.OUT, S.RECORD = os.path.join(tmp, "ship"), os.path.join(tmp, "ship", "build.json")
        record = {"commit": "c0ffee", "programs": {}}
        for name in S.PROGRAMS:
            os.makedirs(os.path.join(S.OUT, name))
            with open(os.path.join(S.OUT, name, name), "wb") as f:
                f.write(b"ELF " + name.encode())
            record["programs"][name] = {"sha256": os2.sha256(os.path.join(S.OUT, name, name))}
        with open(S.RECORD, "w") as f:
            json.dump(record, f)
        diff = {"out": ""}
        os2.git = lambda *a: diff["out"] if a[0] == "diff" else "c0ffee"
        os2.clean_tree = lambda paths: "c0ffee"
        D.committed_before = lambda rel, data, depth=50: None
        prog, conf = os.path.join(tmp, "program"), os.path.join(tmp, "config")
        cfg = FakeConfig(**{"server.program_dir": prog, "server.config_dir": conf})
        targets = sorted(os.path.relpath(dst, tmp).replace(os.sep, "/") for _, dst in S.plan(cfg))
        check("server: os2-server, os2-sync and pre.sh go to the program folder, the mode cfgs to the config folder (%s)" % ", ".join(targets),
              targets == sorted(["program/os2-server", "program/os2-sync", "program/pre.sh"] + ["config/" + os.path.basename(c) for c in S.CFGS]))
        check("server: an empty program folder in the config ships the cfgs alone",
              [os.path.basename(d) for _, d in S.plan(FakeConfig(**{"server.config_dir": conf}))] == [os.path.basename(c) for c in S.CFGS])
        S.ship(cfg, apply=False)
        check("server: ship without --apply copies nothing", not os.path.exists(prog) and not os.path.exists(conf))
        S.ship(cfg, apply=True)
        landed = all(os.path.exists(dst) and open(dst, "rb").read() == open(src, "rb").read() for src, dst in S.plan(cfg))
        check("server: ship --apply puts every file in place, byte for byte, and leaves no .new behind",
              landed and not [f for d in (prog, conf) for f in os.listdir(d) if f.endswith(".new")])
        with open(os.path.join(S.OUT, "os2-sync", "os2-sync"), "ab") as f:
            f.write(b" changed")
        check("server: a program that is not the file the build recorded is refused", exits(S.ship, cfg, True) == 1)
        with open(os.path.join(S.OUT, "os2-sync", "os2-sync"), "wb") as f:
            f.write(b"ELF os2-sync")
        diff["out"] = "server/OverStrike2.Server/Watch.cs"
        check("server: sources changed since the build - refused until it is built again", exits(S.ship, cfg, True) == 1)
        diff["out"] = ""
        with open(os.path.join(conf, os.path.basename(S.CFGS[0])), "w") as f:
            f.write("sv_cheats 1 // made on the server\n")
        check("server: a cfg changed on the server that no commit holds is DRIFT - refused", exits(S.ship, cfg, True) == 1)
        S.ship(cfg, apply=True, force=True)
        check("server: --force ships over it",
              open(os.path.join(conf, os.path.basename(S.CFGS[0])), "rb").read() == open(os.path.join(os2.REPO, S.CFGS[0]), "rb").read())
        one = os.path.join(tmp, "single")
        os.makedirs(one)
        for f in ("os2-server", "OverStrike2.Core.pdb"):
            open(os.path.join(one, f), "w").close()
        check("server: a published program with only debug symbols beside it is a single file", exits(S.single_file, one, "os2-server") is None)
        open(os.path.join(one, "libSystem.Native.so"), "w").close()
        check("server: a published program with a library beside it is refused - it would need that file too",
              exits(S.single_file, one, "os2-server") == 1)
        a, b = os.path.join(tmp, "a.bin"), os.path.join(tmp, "b.bin")
        open(a, "wb").write(b"new")
        open(b, "wb").write(b"old")
        check("os2: an atomic copy replaces the file and leaves no .new",
              os2.copy_verified(a, b, atomic=True) == "copied" and open(b, "rb").read() == b"new" and not os.path.exists(b + ".new"))
    finally:
        S.OUT, S.RECORD, os2.git, os2.clean_tree, D.committed_before = saved

# ---- the bridge's build context: bridge/ and core/ as the repository lays them out, the Dockerfile at bridge/Dockerfile
import io  # noqa: E402
import re  # noqa: E402
import tarfile  # noqa: E402

saved_clean = os2.clean_tree
try:
    os2.clean_tree = lambda paths: "test"      # the context is HEAD's either way; a dirty tree is refused in real use
    _commit, ctx = B.Bridge.context(None)
finally:
    os2.clean_tree = saved_clean
names = set(tarfile.open(fileobj=io.BytesIO(ctx)).getnames())
check("bridge context: bridge/ and core/ at their own paths, the Dockerfile at bridge/Dockerfile, no bin/obj and no core self-test",
      {"bridge/Dockerfile", "bridge/GunGameBridge.csproj", "bridge/maps.json", "core/OverStrike2.Core.csproj", "core/Settings.cs"} <= names
      and not [n for n in names if n.startswith("core/SelfTest") or "/bin/" in n or "/obj/" in n] and B.DOCKERFILE == "bridge/Dockerfile")
bridge_df = open(os.path.join(os2.REPO, "bridge", "Dockerfile"), encoding="utf-8").read()
sources = [s.rstrip("/") for line in re.findall(r"^COPY (?!--from)(.+)$", bridge_df, re.M) for s in line.split()[:-1]]
check("bridge context: every source the Dockerfile copies is in it (%s)" % ", ".join(sources),
      sources and all(s in names or any(n.startswith(s + "/") for n in names) for s in sources))

# ---- compose.yml and its installer: everything they name is in the checkout, and they ship what the deploy scripts ship

DEPLOY = os.path.join(os2.REPO, "deploy")
compose = open(os.path.join(DEPLOY, "compose.yml"), encoding="utf-8").read()
dockerfile = open(os.path.join(DEPLOY, "installer", "Dockerfile"), encoding="utf-8").read()
install = open(os.path.join(DEPLOY, "installer", "install.sh"), encoding="utf-8").read()
named = sorted(set(re.findall(r"(\.\./[\w./-]+?)(?=:|\s|$)", compose)))
dockerfiles = re.findall(r"^\s+dockerfile: (\S+)", compose, re.M)   # relative to the context, the repository root (context: ..)
check("compose: every file of the checkout it mounts or builds from is there (%d, and the Dockerfiles %s)" % (len(named), ", ".join(dockerfiles)),
      named and all(os.path.exists(os.path.normpath(os.path.join(DEPLOY, p))) for p in named)
      and sorted(dockerfiles) == ["bridge/Dockerfile", "deploy/installer/Dockerfile"] and compose.count("context: ..") == 2
      and all(os.path.isfile(os.path.join(os2.REPO, d)) for d in dockerfiles))
copied = [s for line in re.findall(r"^COPY (?!--from)(.+)$", dockerfile, re.M) for s in line.split()[:-1]]
check("installer: every source its Dockerfile copies from the checkout is there (%d)" % len(copied),
      copied and all(os.path.exists(os.path.join(os2.REPO, s)) for s in copied))
plugin_files = re.search(r"COPY --from=build (/out/plugin/\S+(?: /out/plugin/\S+)*) /payload/plugins/OverStrike2/", dockerfile)
check("installer: the plugin's files are the ones deploy/plugin.py ships (%s)" % (plugin_files.group(1) if plugin_files else "none"),
      plugin_files and sorted(p.rsplit("/", 1)[1] for p in plugin_files.group(1).split()) == sorted(P.ship_list()))
hand, first = re.search(r"for f in ([\w. ]+); do\s+put \"/payload/data", install), re.search(r"for f in ([\w. ]+); do\s+if \[ -e", install)
check("installer: the bridge's hand-made files always, the sync's lists only to a server without them - as data.py ships them",
      hand and first and set(hand.group(1).split()) | set(first.group(1).split()) == set(D.SETS["bridge"][2])
      and set(first.group(1).split()) == D.FIRST_ONLY)
check("installer: the programs and the launcher go to the program volume, pre.sh also to the top of the game's",
      all(f"/overstrike2/{n}" in install for n in list(S.PROGRAMS) + ["pre.sh"]) and "/game/pre.sh" in install)
mounts = {"/etc/overstrike2/overstrike2.jsonc": "the settings file", "program:/home/steam/overstrike2:ro": "pre.sh's OS2_SERVER",
          "plugins:/home/steam/cs2-plugins:ro": "paths.plugins", "data:/data": "paths.bridgeData",
          "target: /home/steam/workshop-sync": "paths.syncWork", "sync-state:/home/steam/sync-state": "paths.syncState"}
check("compose: the settings file, the program volume and the data paths are where the defaults look (%s)"
      % ", ".join(v for k, v in mounts.items() if k not in compose), all(k in compose for k in mounts)
      and "/home/steam/overstrike2/os2-server" in open(os.path.join(os2.REPO, S.LAUNCHER), encoding="utf-8").read())


# ---- compose.example.yml, the shape of a host the scripts ship to: nothing builds or runs it here, so this holds it to what the parts read

def service(text, name):
    """One service's block of a compose file: from its two-space key to the next one."""
    found = re.search(r"^  %s:.*?(?=^  \w|^\S|\Z)" % name, text, re.M | re.S)
    return found.group(0) if found else ""


def settings_of(jsonc):
    """A settings file's values: JSON once the comments (only ever after white space here) and the trailing commas are gone."""
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", re.sub(r"(?m)(^|\s)//.*$", "", jsonc)))


example = open(os.path.join(DEPLOY, "compose.example.yml"), encoding="utf-8").read()
mounts = {"<program dir>:/home/steam/overstrike2:ro": "the programs", "<program dir>/pre.sh:/home/steam/cs2-dedicated/pre.sh:ro": "the launcher",
          "<plugins dir>:/home/steam/cs2-plugins:ro": "paths.plugins", "<data dir>:/data:ro": "the game server's paths.bridgeData",
          "<data dir>:/data #": "the bridge's and the sync's paths.bridgeData", ":/home/steam/workshop-sync #": "paths.syncWork",
          ":/home/steam/sync-state #": "paths.syncState", ":/home/steam/sync-cfg/": "paths.syncCfg", "CS2_MAPGROUP: mg_ar_shoots": "the map group"}
check("compose.example: the program folder, the launcher and the data paths are where the defaults look (%s)"
      % ", ".join(v for k, v in mounts.items() if k not in example), all(k in example for k in mounts))
check("compose.example: every service has the settings file, and the sync runs a copy of os2-sync it can execute",
      all("target: /etc/overstrike2/overstrike2.jsonc" in service(example, s) for s in ("cs2", "bridge", "sync"))
      and "cp /home/steam/overstrike2/os2-sync /tmp/os2-sync && chmod 755 /tmp/os2-sync && exec /tmp/os2-sync serve" in service(example, "sync"))
inline = re.search(r"^    content: \|\n(.*?)^\S", example, re.M | re.S)
inline = settings_of(inline.group(1)) if inline else {}
sample = settings_of(open(os.path.join(DEPLOY, "overstrike2.example.jsonc"), encoding="utf-8").read())
inline.get("server", {}).pop("name", None), sample["server"].pop("name")
check("compose.example: the inline settings are overstrike2.example.jsonc's, the server's name aside (core's self-test reads that file)", inline == sample)
for name, text in (("compose.yml", compose), ("compose.example.yml", example)):
    check("%s: no inline settings file on a read-only service - Compose refuses it" % name,
          not (re.search(r"^    content: \|", text, re.M) and re.search(r"^    read_only: true", text, re.M)))
    check("%s: each service has the variables the settings file's env: values read (else the bridge exits at its start)" % name,
          all(re.search(r"^      %s: " % v, service(text, s), re.M) for s, names in (("cs2", ["CS2_RCONPW", "OVERSTRIKE2_BRIDGE_SECRET"]),
                                                                               ("bridge", ["CS2_RCONPW", "OVERSTRIKE2_BRIDGE_SECRET"]),
                                                                               ("sync", ["OVERSTRIKE2_BRIDGE_SECRET"])) for v in names))

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
