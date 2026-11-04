#!/usr/bin/env python3
"""Offline tests for the deploy scripts: the bridge swap against a stand-in Docker API, and which data files data.py ships.

    python -I deploy/test_deploy.py

No server, no Docker, no private config: the stand-in keeps containers and image tags in memory and answers the calls bridge.py
makes. What it checks: a running bridge and a stopped one are swapped onto the IP and MAC they are configured with (a stopped one
has no runtime address: on 2026-11-04 a swap of a stopped bridge put the old one back), a failed swap puts it back as it was, and
data.py ships the hand-made files but never a list the Workshop sync writes on the server (the owner's 77 a).
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

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
