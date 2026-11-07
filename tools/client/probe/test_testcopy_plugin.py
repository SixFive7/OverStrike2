"""Offline test of testcopy.py's plugin of a commit of main against an in-memory Docker: only a commit of main is staged, the staged folder is
made beside production's plugins folder by a helper (production's own folder only ever read-only), byte for byte as built; run mounts it in
the place of production's plugins folder and checks the copy runs it; plugin-rm and restore take it away, restore only once production runs
its own plugin again. The build itself (dotnet) is replaced by made-up files.

    python -I tools/client/probe/test_testcopy_plugin.py
"""
import contextlib, copy, hashlib, io, json, re, shutil, sys, tarfile, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simconfig  # noqa: E402

tmp = simconfig.make("tc-plugin-sim-")
PLUGINS = "/srv/overstrike2/plugins"           # production's plugins folder on the host, as its inspect names it (made up)
STAGE = PLUGINS + "-candidate"
TREE = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/plugins"
BUILT = {"OverStrike2.dll": b"MZ the new plugin", "OverStrike2.pdb": b"its symbols", "blocked.txt": b"game_type\n", "catchup.json": b"{}\n"}
OTHER = {"./Other/Other.dll": b"MZ another plugin"}   # production's other plugin folder, which the staged folder keeps as it is
calls, created, containers = [], {}, {}
disk = {}                  # the staged folder's files as the helper leaves them: ./<folder>/<name> -> bytes
copy_tree = {}             # the game's tree in the test copy: what its start copied from the folder it mounts
sim = {"tree_differs": False, "corrupt": False}
execs = {}

import probe  # noqa: E402


def sha(b):
    return hashlib.sha256(b).hexdigest()


def answer(cid, cmd):
    """What an exec in a container prints."""
    text = cmd[-1] if cmd else ""
    if cmd[:1] == ["sha256sum"]:
        data = copy_tree.get(cmd[1])
        return f"{sha(data)}  {cmd[1]}\n" if data is not None else f"sha256sum: {cmd[1]}: No such file or directory\n"
    if "files $n differ $bad" in text:
        return "DIFF OverStrike2/OverStrike2.dll\nfiles 5 differ 1\n" if sim["tree_differs"] else "files 5 differ 0\n"
    if "find /dst -mindepth 1 -delete" in text:
        disk.clear()
        disk.update(OTHER)
        return "EXIT=0\n"
    if "sha256sum" in text and "chown -R --reference=/src /dst" in text:
        files = dict(disk)
        if sim["corrupt"]:
            files["./OverStrike2/OverStrike2.dll"] = b"something else"
        return "".join(f"{sha(b)}  {p}\n" for p, b in sorted(files.items())) + "EXIT=0\n"
    raise AssertionError(f"unexpected exec {cmd}")


def req(path, method="GET", body=None, ctype=None, timeout=None):
    calls.append((method, path, copy.deepcopy(body) if not isinstance(body, (bytes, bytearray)) else bytes(body)))
    parts = path.split("?")[0].split("/")
    if method == "GET" and parts[1] == "containers" and parts[-1] == "json":
        c = containers.get(parts[2])
        if c is None:
            raise probe.DockerError("HTTP Error 404: Not Found")
        return 200, json.dumps(c)
    if method == "POST" and path.startswith("/containers/create"):
        name = path.split("name=")[1]
        created[name] = body
        containers[name] = {"Id": name + "-id", "Name": "/" + name, "Image": body["Image"], "State": {"Status": "created"},
                            "Config": {"Labels": body.get("Labels") or {}, "Env": body.get("Env") or []}, "HostConfig": body.get("HostConfig") or {}}
        return 201, json.dumps({"Id": name + "-id"})
    if method == "POST" and parts[1] == "exec" and parts[-1] == "start":
        return 200, answer(*execs[parts[2]]).encode()
    name = next((n for n, c in containers.items() if c["Id"] == parts[2] or n == parts[2]), None)
    if method == "POST" and parts[-1] == "exec":
        execs[f"x{len(execs)}"] = (name, body["Cmd"])
        return 201, json.dumps({"Id": f"x{len(execs) - 1}"})
    if method == "PUT" and parts[-1] == "archive":
        assert path.endswith("?path=/dst"), path
        with tarfile.open(fileobj=io.BytesIO(body)) as tar:
            for m in tar.getmembers():
                if m.isfile():
                    disk["./" + m.name] = tar.extractfile(m).read()
        return 200, b""
    if method == "POST" and parts[-1] == "start":
        containers[name]["State"]["Status"] = "running"
        if name == "cs2" and containers[name]["Config"]["Labels"].get("cs2.test-config"):   # the copy's start copies its plugins into the game's tree
            src = next(b.split(":")[0] for b in containers[name]["HostConfig"]["Binds"] if b.split(":")[1] == "/home/steam/cs2-plugins")
            copy_tree.clear()
            if src == STAGE:
                copy_tree.update({f"{TREE}/{p[2:]}": b for p, b in disk.items()})
        return 204, b""
    if method == "POST" and parts[-1] == "wait":
        containers[name]["State"]["Status"] = "exited"
        return 200, json.dumps({"StatusCode": 0})
    if method == "GET" and parts[-1] == "logs":
        return 200, b"removed\n"
    if method == "POST" and parts[-1] in ("stop", "rename"):
        if parts[-1] == "rename":
            new = path.split("name=")[1]
            containers[new] = containers.pop(name)
        else:
            containers[name]["State"]["Status"] = "exited"
        return 204, b""
    if method == "DELETE":
        containers.pop(name, None)
        return 204, b""
    raise AssertionError(f"unexpected call {method} {path}")


probe.need_lock, probe.req = (lambda: None), req
probe.rcon = lambda *c: '[#0:LOADED]: "OverStrike2" (5.0.0)\n'
import testcopy as tc  # noqa: E402

time.sleep = lambda s: None
tc.a2s = lambda timeout=2.0: ("srv", "ar_shoots", 0, 1)
tc.wait_a2s = lambda want, limit: (("srv", "ar_shoots", 0, want), 1)
builds = []
tc.build_plugin = lambda s: builds.append(s) or dict(BUILT)
fails = 0


def check(name, ok):
    global fails
    print(("PASS " if ok else "FAIL ") + name)
    fails += 0 if ok else 1


def refused(fn, *a):
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            fn(*a)
        return None
    except SystemExit as e:
        return str(e)


def production(status="running", text='{ "schema": 1 }', mounts=False):
    env = ["CS2_RCONPW=pw"] + ([tc.CONTENT + "=" + text] if text else [])
    binds = [f"{PLUGINS}:/home/steam/cs2-plugins:ro", "/srv/overstrike2/bridge:/data:ro", "/srv/cs2:/home/steam/cs2-dedicated"]
    host = {"RestartPolicy": {"Name": "always"}}
    if mounts:
        host["Mounts"] = [{"Type": "bind", "Source": b.split(":")[0], "Target": b.split(":")[1], "ReadOnly": b.endswith(":ro")} for b in binds]
    else:
        host["Binds"] = binds
    return {"Id": "p" * 64, "Name": "/cs2", "Image": "sha256:gameimg", "RestartCount": 0, "State": {"Status": status},
            "Config": {"Labels": {}, "Env": env}, "HostConfig": host, "NetworkSettings": {"Networks": {}}}


ROOT = tc.git("rev-list", "--max-parents=0", "HEAD").stdout.split()[-1]   # the first commit: on main wherever main is
POSTS = lambda: [c for c in calls if c[0] in ("POST", "PUT", "DELETE")]

# 1. what is refused before anything is built or touched
containers.clear(); containers["cs2"] = production()
for args, what in (([], "no commit"), (["--all"], "an option"), (["0123456789abcdef0123"], "no such commit"), ([ROOT, "x"], "two arguments")):
    calls.clear(); builds.clear()
    r = refused(tc.cmd_plugin, args)
    check(f"plugin refuses {what}, nothing built, nothing sent", r is not None and not builds and not POSTS())
real_git = tc.git
tc.git = lambda *a, **k: (real_git(*a, **k) if a[0] == "rev-parse" else type("R", (), {"returncode": 1})())
calls.clear()
r = refused(tc.cmd_plugin, [ROOT])
check("plugin refuses a commit that is not on main, nothing built", r is not None and "not on main" in r and not builds and not POSTS())
tc.git = real_git
probe.need_lock = lambda: sys.exit("the server lock is not held by the harness")
r = refused(tc.cmd_plugin, [ROOT])
check("plugin refuses without the server lock", r is not None and "lock" in r and not builds)
probe.need_lock = lambda: None
check("commit_of_main: a commit of main gives its full id", tc.commit_of_main(ROOT) == ROOT)

# 2. staged while production still runs: only read, never touched
calls.clear(); created.clear()
out = io.StringIO()
with contextlib.redirect_stdout(out):
    tc.cmd_plugin([ROOT])
h = created["cs2-plugin-stage"]
check("plugin: built from the commit asked for", builds == [ROOT])
check("plugin: the helper - the game image as root, no network, production's plugins folder read-only, the staged folder beside it",
      h["Image"] == "sha256:gameimg" and h["User"] == "0:0" and h["HostConfig"]["NetworkMode"] == "none"
      and h["HostConfig"]["Binds"] == [f"{PLUGINS}:/src:ro", f"{STAGE}:/dst"])
check("plugin: the helper carries the test label and the config's extra labels, and is removed",
      h["Labels"][tc.TEST_LABEL].startswith("helper") and h["Labels"]["monitor.mute"] == "true" and "cs2-plugin-stage" not in containers)
puts = [c for c in calls if c[0] == "PUT"]
with tarfile.open(fileobj=io.BytesIO(puts[0][2])) as tar:
    names = sorted(m.name for m in tar.getmembers())
check("plugin: one upload, the plugin's folder and exactly what ships", len(puts) == 1 and names == ["OverStrike2"] + [f"OverStrike2/{n}" for n in sorted(BUILT)])
check("plugin: production's game server untouched - no stop, rename, start or exec in it",
      not [c for c in POSTS() if c[1].startswith("/containers/cs2/") or c[1].startswith("/containers/cs2?")])
rec = json.loads(tc.STAGED.read_text())
check("plugin: the record - the commit, the folder beside production's, the image, each file's hash, production's other plugin folders",
      rec["commit"] == ROOT and rec["dir"] == STAGE and rec["image"] == "sha256:gameimg" and rec["files"] == {n: sha(b) for n, b in BUILT.items()}
      and rec["others"] == ["Other"])
check("plugin: says it, and names no host folder", "staged in <plugins>-candidate" in out.getvalue() and PLUGINS not in out.getvalue())

# 3. a staged folder that is not the build byte for byte: refused, no record
tc.STAGED.unlink()
sim["corrupt"] = True
r = refused(tc.cmd_plugin, [ROOT])
check("plugin: a staged file that differs from the build - refused, no record", r is not None and "byte for byte" in r and not tc.STAGED.exists())
sim["corrupt"] = False
with contextlib.redirect_stdout(io.StringIO()):
    tc.cmd_plugin([ROOT])

# 4. prep, then run: the copy mounts the staged folder in production's place
containers["cs2"] = production()
with contextlib.redirect_stdout(io.StringIO()):
    tc.cmd_prep()
check("prep after a stage: production aside as ever", "cs2-prod-aside" in containers and "cs2" not in containers)
b = tc.body()
check("run: the copy's plugins folder is the staged one, read-only as production's, every other bind production's",
      b["HostConfig"]["Binds"] == [f"{STAGE}:/home/steam/cs2-plugins:ro", "/srv/overstrike2/bridge:/data:ro", "/srv/cs2:/home/steam/cs2-dedicated"])
calls.clear()
out = io.StringIO()
with contextlib.redirect_stdout(out):
    tc.cmd_run()
check("run: production's plugins folder in no mount of the copy", not any(x.startswith(PLUGINS + ":") for x in created["cs2"]["HostConfig"]["Binds"]))
check("run: the copy runs the staged build - its DLL in the game's tree, the plugin loaded",
      f"plugin on the copy: the build of {ROOT[:12]}" in out.getvalue() and "LOOK" not in out.getvalue())

# 5. the copy's game tree holds another DLL: run says so
copy_tree[f"{TREE}/OverStrike2/OverStrike2.dll"] = b"production's plugin"
ok, words = tc.plugin_on_copy(rec)
check("run's check: another DLL in the copy's game tree is NOT the build", not ok and "NOT the build" in words and "another DLL" in words)

# 6. plugin-rm while the copy mounts it: refused; after rm: the helper removes the folder by its name
r = refused(tc.cmd_plugin_rm)
check("plugin-rm refuses while the test copy mounts the staged folder", r is not None and "rm it first" in r and tc.STAGED.exists())
with contextlib.redirect_stdout(io.StringIO()):
    tc.cmd_rm()

# 7. restore: production back, its own plugins in the game's tree, then the staged folder goes
containers["cs2-prod-aside"]["State"]["Status"] = "exited"
tc.FLAG.write_text("x"); tc.FLAG_MINE.write_text("1")
created.pop("cs2-plugin-rm", None)
sim["tree_differs"] = True
with contextlib.redirect_stdout(io.StringIO()) as out:
    tc.cmd_restore()
check("restore: production's game tree not what its plugins folder holds - the flag left on, the staged folder kept",
      tc.FLAG.exists() and tc.STAGED.exists() and "cs2-plugin-rm" not in created and "NOT WHAT ITS PLUGINS FOLDER HOLDS" in out.getvalue())
containers["cs2-prod-aside"] = containers.pop("cs2")
containers["cs2-prod-aside"]["State"]["Status"] = "exited"
tc.PROD_SAVED.write_text(json.dumps(production()))
sim["tree_differs"] = False
with contextlib.redirect_stdout(io.StringIO()) as out:
    tc.cmd_restore()
rm = created.get("cs2-plugin-rm") or {}
check("restore: production runs its own plugins - the flag removed, then the staged folder by its name and the record",
      not tc.FLAG.exists() and not tc.STAGED.exists() and rm.get("HostConfig", {}).get("Binds") == ["/srv/overstrike2:/p"]
      and rm.get("Cmd") == ["rm -rf -- '/p/plugins-candidate' && echo removed"] and rm.get("Image") == "sha256:gameimg")

# 8. plugin-rm with nothing staged; a stale record whose folder is not beside production's; a production on Mounts; a settings text's paths
check("plugin-rm with nothing staged says so", refused(tc.cmd_plugin_rm) is None)
tc.STAGED.write_text(json.dumps(dict(rec, dir="/elsewhere/plugins-candidate")))
tc.PROD_SAVED.write_text(json.dumps(production()))
r = refused(tc.body)
check("run refuses a staged record that is not beside production's plugins folder: no copy that runs production's plugin", r is not None and "REFUSED" in r)
tc.STAGED.write_text(json.dumps(rec))
tc.PROD_SAVED.write_text(json.dumps(production(mounts=True)))
m = tc.body()["HostConfig"]["Mounts"]
check("run, a production on Mounts: the plugins mount's source is the staged folder, read-only kept, the rest as they were",
      [(x["Source"], x["Target"], x["ReadOnly"]) for x in m] == [(STAGE, "/home/steam/cs2-plugins", True), ("/srv/overstrike2/bridge", "/data", True),
                                                                  ("/srv/cs2", "/home/steam/cs2-dedicated", False)])
moved = production(text='{ "schema": 1, "paths": { "plugins": "/opt/plugins", "gameDir": "/opt/cs2" } }')
moved["HostConfig"]["Binds"][0] = f"{PLUGINS}:/opt/plugins:ro"
tc.PROD_SAVED.write_text(json.dumps(moved))
check("run, a settings text that moves paths.plugins: the bind at that path is swapped",
      tc.body()["HostConfig"]["Binds"][0] == f"{STAGE}:/opt/plugins:ro" and tc.plugins_at(moved) == "/opt/plugins"
      and tc.game_plugins_dir(moved) == "/opt/cs2/game/csgo/addons/counterstrikesharp/plugins")
check("paths without a settings text: the older variables, else the defaults",
      tc.plugins_at({"Config": {"Env": ["CS2_PLUGINS_SRC=/p2"]}}) == "/p2" and tc.plugins_at({"Config": {"Env": []}}) == tc.PLUGINS_AT
      and tc.game_plugins_dir({"Config": {"Env": ["STEAMAPPDIR=/g"]}}) == "/g/game/csgo/addons/counterstrikesharp/plugins")
tc.STAGED.unlink()
containers.clear()
containers["cs2"] = dict(production(), HostConfig={"Binds": ["/srv/overstrike2/bridge:/data:ro"]})
tc.PROD_SAVED.unlink(missing_ok=True)
r = refused(tc.cmd_plugin, [ROOT])
check("plugin refuses a production without a plugins bind", r is not None and "no bind at /home/steam/cs2-plugins" in r)
containers["cs2"] = dict(production(), Config={"Labels": {tc.TEST_LABEL: "x"}, "Env": []})
r = refused(tc.cmd_plugin, [ROOT])
check("plugin refuses when the game server's name is a test copy and production's inspect is not saved", r is not None and "neither aside" in r)
check("the tar: the folder, then its files, readable by the game's user", re.search(r"OverStrike2/OverStrike2\.dll", str(tarfile.open(fileobj=io.BytesIO(tc.plugin_tar(BUILT))).getnames()))
      and all(m.mode in (0o644, 0o755) for m in tarfile.open(fileobj=io.BytesIO(tc.plugin_tar(BUILT))).getmembers()))

shutil.rmtree(tmp)
print("failures:", fails)
sys.exit(1 if fails else 0)
