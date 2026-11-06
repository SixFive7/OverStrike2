"""Offline test of testcopy.py's candidate-bridge verbs against an in-memory Docker: production's data is only ever mounted
read-only, the candidate runs the new image on the copy, and everything comes back.

    python -I tools/client/probe/test_testcopy_bridge.py
"""
import copy, json, shutil, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simconfig  # noqa: E402

tmp = simconfig.make("tc-bridge-sim-")
calls, created = [], {}
DATA = "/srv/overstrike2/bridge"           # the bridge's /data on the host, as its inspect names it (made up)
containers, images = {}, {"sha256:oldimg": {"Config": {"Env": ["PATH=/usr/bin", "MAPS_FILE=/app/maps.json"]}},
                          "localhost/overstrike2-bridge:candidate": {"Config": {"Env": ["PATH=/usr/bin"]}}}
helper_exit = {"code": 0}

import probe  # noqa: E402


def prod_bridge():
    return {"Id": "b" * 64, "Name": "/bridge", "Image": "sha256:oldimg", "RestartCount": 0, "State": {"Status": "running"},
            "Config": {"Image": "localhost/overstrike2-bridge", "User": "1000:1000", "Labels": {"com.docker.compose.service": "bridge",
                                                                                                "org.opencontainers.image.revision": "old"},
                       "Env": ["PATH=/usr/bin", "MAPS_FILE=/app/maps.json", "LOG_SECRET=s3cr3t", "RCON_PASSWORD=pw", "MAPS_FILE=/data/maps.json"]},
            "HostConfig": {"Binds": [f"{DATA}:/data:rw"], "RestartPolicy": {"Name": "always"}, "ReadonlyRootfs": True},
            "NetworkSettings": {"Networks": {"game": {"IPAMConfig": {"IPv4Address": "192.0.2.101"}, "MacAddress": "02:00:00:00:00:65",
                                                      "Aliases": ["bridge", "os2-bridge"], "IPAddress": "192.0.2.101"}}}}


def req(path, method="GET", body=None, ctype=None, timeout=None):
    calls.append((method, path, copy.deepcopy(body)))
    parts = path.split("?")[0].split("/")
    if method == "GET" and path.startswith("/images/"):
        ref = path[len("/images/"):-len("/json")]
        if ref not in images:
            raise probe.DockerError("HTTP Error 404: Not Found")
        return 200, json.dumps(images[ref])
    if method == "GET" and parts[1] == "containers" and parts[-1] == "json":
        c = containers.get(parts[2])
        if c is None:
            raise probe.DockerError("HTTP Error 404: Not Found")
        return 200, json.dumps(c)
    if method == "POST" and path.startswith("/containers/create"):
        name = path.split("name=")[1]
        cid = name + "-id"
        created[name] = body
        state = {"Status": "created"}
        net = {n: dict(e, IPAddress=(e.get("IPAMConfig") or {}).get("IPv4Address", "")) for n, e in ((body.get("NetworkingConfig") or {}).get("EndpointsConfig") or {}).items()}
        containers[name] = {"Id": cid, "Name": "/" + name, "Image": body["Image"], "RestartCount": 0, "State": state,
                            "Config": {"Labels": body.get("Labels") or {}, "Env": body.get("Env") or [], "User": body.get("User")},
                            "HostConfig": body.get("HostConfig") or {}, "NetworkSettings": {"Networks": net}}
        return 201, json.dumps({"Id": cid})
    name = next((n for n, c in containers.items() if c["Id"] == parts[2] or n == parts[2]), None)
    if method == "POST" and parts[-1] == "start":
        containers[name]["State"]["Status"] = "running"
        return 204, b""
    if method == "POST" and parts[-1] == "stop":
        containers[name]["State"]["Status"] = "exited"
        return 204, b""
    if method == "POST" and parts[-1] == "wait":
        containers[name]["State"]["Status"] = "exited"
        return 200, json.dumps({"StatusCode": helper_exit["code"]})
    if method == "GET" and parts[-1] == "logs":
        return 200, b"\x01\x00\x00\x00\x00\x00\x00\x14files: 7, 2.0M\nok\n"
    if method == "POST" and parts[-1] == "rename":
        new = path.split("name=")[1]
        containers[new] = containers.pop(name)
        containers[new]["Name"] = "/" + new
        return 204, b""
    if method == "DELETE":
        containers.pop(name, None)
        return 204, b""
    raise AssertionError(f"unexpected call {method} {path}")


probe.need_lock, probe.req, probe.rcon = (lambda: None), req, (lambda *c: "")
import testcopy as tc  # noqa: E402
time.sleep = lambda s: None
fails = 0


def check(name, ok):
    global fails
    print(("PASS " if ok else "FAIL ") + name)
    fails += 0 if ok else 1


def refused(fn, *a):
    try:
        fn(*a)
        return None
    except SystemExit as e:
        return str(e)


# production's game server not aside yet: no candidate bridge
containers["bridge"] = prod_bridge()
r = refused(tc.cmd_bridge_prep)
check("bridge-prep refuses while production's game server is not aside", r is not None and "run prep first" in r and "bridge-prod-aside" not in containers)

# prep was done: production's game server aside, its inspect saved
tc.STATE.mkdir(parents=True, exist_ok=True)
tc.PROD_SAVED.write_text(json.dumps({"Image": "sha256:gameimg"}))
containers["cs2-prod-aside"] = {"Id": "g" * 64, "State": {"Status": "exited"}, "Config": {"Labels": {}}}
tc.cmd_bridge_prep()
check("bridge-prep: production's bridge saved, stopped and renamed aside",
      tc.BRIDGE_SAVED.exists() and "bridge" not in containers and containers["bridge-prod-aside"]["State"]["Status"] == "exited")

# a tag the server does not have
r = refused(tc.cmd_bridge_run, ["nosuch"])
check("bridge-run refuses an image the server does not have, before any copy", r is not None and "no image" in r and "bridge-copy" not in created)

calls.clear()
tc.cmd_bridge_run(["candidate"])
h = created["bridge-copy"]
cand = created["bridge"]
check("bridge-run: the copy helper - game image as root, no network, production's data read-only, the copy beside it",
      h["Image"] == "sha256:gameimg" and h["User"] == "0:0" and h["HostConfig"]["NetworkMode"] == "none"
      and h["HostConfig"]["Binds"] == [f"{DATA}:/src:ro", f"{DATA}-candidate:/dst"])
check("bridge-run: the helper drops the maintenance flag and gives the copy to the bridge's user",
      "rm -f /dst/maintenance" in h["Cmd"][0] and "chown 1000:1000 /dst" in h["Cmd"][0] and "cp -a /src/. /dst/" in h["Cmd"][0])
check("bridge-run: the helper is removed afterwards", "bridge-copy" not in containers)
binds = cand["HostConfig"]["Binds"]
check("bridge-run: the candidate runs the new image with /data on the copy - production's data in no candidate mount",
      cand["Image"] == "localhost/overstrike2-bridge:candidate" and binds == [f"{DATA}-candidate:/data:rw"] and not any(b.startswith(DATA + ":") for b in binds))
check("bridge-run: restart policy no, the candidate's label, the config's extra labels, compose's config-hash poisoned, the old image's own labels gone",
      cand["HostConfig"]["RestartPolicy"]["Name"] == "no" and cand["Labels"][tc.TEST_LABEL] == tc.BRIDGE_TAG and cand["Labels"]["monitor.mute"] == "true"
      and cand["Labels"]["com.docker.compose.config-hash"] == "test-config-not-from-main" and "org.opencontainers.image.revision" not in cand["Labels"]
      and cand["Labels"]["com.docker.compose.service"] == "bridge")
ep = cand["NetworkingConfig"]["EndpointsConfig"]["game"]
check("bridge-run: production's IP, MAC and aliases, so the test copy reaches it by name",
      ep["IPAMConfig"]["IPv4Address"] == "192.0.2.101" and ep["MacAddress"] == "02:00:00:00:00:65" and ep["Aliases"] == ["bridge", "os2-bridge"])
check("bridge-run: the environment compose set (secrets kept, the old image's own variables left to the new image)",
      "LOG_SECRET=s3cr3t" in cand["Env"] and "MAPS_FILE=/data/maps.json" in cand["Env"] and "MAPS_FILE=/app/maps.json" not in cand["Env"]
      and "PATH=/usr/bin" not in cand["Env"] and cand["User"] == "1000:1000")
check("bridge-run: the candidate is running", containers["bridge"]["State"]["Status"] == "running")

# production's bridge on the settings text: it is in the candidate's environment as it is, and nothing else is called for it
containers.pop("bridge")
production = json.loads(tc.BRIDGE_SAVED.read_text())
env = production["Config"]["Env"] + [tc.CONTENT + '={ "schema": 1 }', "CS2_RCONPW=pw"]
tc.BRIDGE_SAVED.write_text(json.dumps(dict(production, Config=dict(production["Config"], Env=env))))
calls.clear()
tc.cmd_bridge_run(["candidate"])
check("bridge-run, settings text: the candidate's environment holds production's text and password, byte for byte, read-only root and all",
      tc.CONTENT + '={ "schema": 1 }' in created["bridge"]["Env"] and "CS2_RCONPW=pw" in created["bridge"]["Env"] and created["bridge"]["HostConfig"]["ReadonlyRootfs"] is True
      and not [c for c in calls if "/archive" in c[1]])
tc.BRIDGE_SAVED.write_text(json.dumps(production))

# --keep-flag
containers.pop("bridge")
tc.cmd_bridge_run(["candidate", "--keep-flag"])
check("bridge-run --keep-flag keeps the maintenance flag in the copy", "maintenance" not in created["bridge-copy"]["Cmd"][0])

# a failed copy starts no candidate
containers.pop("bridge")
created.pop("bridge")
helper_exit["code"] = 1
r = refused(tc.cmd_bridge_run, ["candidate"])
check("bridge-run: a failed copy - refused, no candidate", r is not None and "copy failed" in r and "bridge" not in created)
helper_exit["code"] = 0
tc.cmd_bridge_run(["candidate"])

# bridge-rm refuses production
saved_cand = containers.pop("bridge")
containers["bridge"] = prod_bridge()
r = refused(tc.cmd_bridge_rm)
check("bridge-rm refuses a bridge that is not the candidate", r is not None and "NOT a candidate" in r and "bridge" in containers)
containers["bridge"] = saved_cand

calls.clear()
tc.cmd_bridge_rm()
rm = created["bridge-copy-rm"]
check("bridge-rm: the candidate removed, then the copy - its folder alone, by name",
      "bridge" not in containers and rm["HostConfig"]["Binds"] == ["/srv/overstrike2:/p"]
      and rm["Cmd"][0] == "rm -rf -- '/p/bridge-candidate' && echo removed" and rm["HostConfig"]["NetworkMode"] == "none")

tc.cmd_bridge_restore()
d = containers.get("bridge")
check("bridge-restore: production's own container back under its name, running, the saved inspect gone",
      d is not None and d["Id"] == "b" * 64 and d["State"]["Status"] == "running" and "bridge-prod-aside" not in containers
      and not tc.BRIDGE_SAVED.exists())
check("never: a rw mount of production's data in any container this created",
      not any(b == f"{DATA}:/data:rw" or b == f"{DATA}:/dst" for body in created.values() for b in (body.get("HostConfig") or {}).get("Binds") or []))

# restore (production's game server) refuses while a candidate bridge or the aside is there
containers["cs2-prod-aside"] = {"Id": "g" * 64, "State": {"Status": "exited"}, "Config": {"Labels": {}}}
containers["bridge-prod-aside"] = prod_bridge()
r = refused(tc.cmd_restore)
check("restore refuses while production's bridge is aside", r is not None and "bridge-restore first" in r and "cs2" not in containers)
containers.pop("bridge-prod-aside")
containers["bridge"] = {"Id": "c-id", "State": {"Status": "running"}, "Config": {"Labels": {tc.TEST_LABEL: tc.BRIDGE_TAG}}}
r = refused(tc.cmd_restore)
check("restore refuses while the bridge is the candidate", r is not None and "bridge-restore first" in r and "cs2" not in containers)
for bad in ("", "relative/path", "/", "/mnt", "/srv/../etc"):
    check(f"copy_dir refuses {bad!r}", refused(tc.copy_dir, bad) is not None)
check("copy_dir: beside production's folder", tc.copy_dir(DATA) == DATA + "-candidate")

shutil.rmtree(tmp)
print("failures:", fails)
sys.exit(1 if fails else 0)
