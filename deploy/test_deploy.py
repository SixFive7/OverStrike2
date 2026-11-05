#!/usr/bin/env python3
"""Offline tests for the deploy scripts: the bridge swap against a stand-in Docker API, and which data files data.py ships.

    python -I deploy/test_deploy.py

No server, no Docker, no private config: the stand-in keeps containers and image tags in memory and answers the calls bridge.py
makes. What it checks: a running bridge and a stopped one are swapped onto the IP and MAC they are configured with (a stopped one
has no runtime address, so a swap that checked the runtime address put the old one back), a failed swap puts it back as it was,
and data.py ships the hand-made files but never a list the Workshop sync writes on the server (one writer per file).
"""
import copy
import os
import sys
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

    def __init__(self, bridge_running=True, old_engine=False, pinned=True, health="gg-health 1\nlog pushes 3\n"):
        self.old_engine, self.health_text, self.calls, self.n = old_engine, health, [], 0
        self.refs = {"localhost/overstrike2-bridge:latest": "sha256:image-old-00000", "localhost/overstrike2-bridge:candidate": "sha256:image-new-00000"}
        self.images = {i: {"Id": i, "Config": {"Env": ["PATH=/usr/bin"], "Labels": {"org.opencontainers.image.title": "x"}}}
                       for i in set(self.refs.values())}
        env = ["PATH=/usr/bin", "HTTP_PORT=8080", "LOG_SECRET=s", "RCON_PASSWORD=p"]
        endpoint = {"IPAMConfig": {"IPv4Address": IP} if pinned else None, "Aliases": ["bridge"], "MacAddress": MAC, "IPAddress": IP}
        self.containers = {
            "c-old": {"Id": "c-old", "Name": "/bridge", "Image": "sha256:image-old-00000", "RestartCount": 0,
                      "State": {"Running": True, "Status": "running", "StartedAt": "start-0"},
                      "Config": {"Image": "localhost/overstrike2-bridge", "Env": env, "Labels": {"com.docker.compose.service": "bridge"}},
                      "HostConfig": {"RestartPolicy": {"Name": "unless-stopped"}, "NetworkMode": NET},
                      "NetworkSettings": {"Networks": {NET: endpoint}}},
            "c-game": {"Id": "c-game", "Name": "/cs2", "Image": "sha256:game", "RestartCount": 0,
                       "State": {"Running": True, "Status": "running", "StartedAt": "start-game"},
                       "Config": {"Image": "game", "Env": ["RCON_PORT=27015"]}, "HostConfig": {}, "NetworkSettings": {"Networks": {}}},
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

    def call(self, method, path, body=None, timeout=60, **kw):
        route, _, query = path.partition("?")
        q = dict(p.split("=", 1) for p in query.split("&") if "=" in p)
        parts = route.strip("/").split("/")
        self.calls.append((method, route, q.get("name") or q.get("tag") or ""))
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

    # a stopped bridge with no pinned IP: nothing to recreate it on - refused before any change
    f = FakeDocker(bridge_running=False, pinned=False)
    r = swap(f)
    check("swap, stopped and unpinned: refused, nothing written (%s)" % r, r == "exit 1" and f.writes() == [])
finally:
    B.time.sleep = real_sleep

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
    server = D.plan("server", D.SETS["server"][2], dest)
    check("data: the server set is server/'s own files, keepset.py with them when it is there (%s)" % ", ".join(server),
          all(os.path.isfile(os.path.join(os2.REPO, "server", f)) for f in server) and "update-watch.py" in server and "workshop-sync.py" in server
          and ("keepset.py" in server) == os.path.isfile(os.path.join(os2.REPO, "server", "keepset.py")))

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

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
