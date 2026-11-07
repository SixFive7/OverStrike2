"""Offline test of testcopy.py's prep, rm and run against an in-memory Docker: no RCON quit, the rename only once production has exited,
never a credential as an override; the copy on production's own network endpoint; without a settings text in production run works on the
variables, with one the copy's environment holds production's text with the automatic actions off, or there is no copy; run --downloads
keeps the background downloads production's only while production's watcher has nothing of its own to judge; beside a candidate bridge
the copy reads the candidate's copy of the bridge's data.

    python -I tools/client/probe/test_testcopy.py
"""
import contextlib, io, json, re, shutil, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simconfig  # noqa: E402

tmp = simconfig.make("tc-sim-")
calls = []
state = {}

import probe  # noqa: E402


def rcon(*c):
    calls.append(("rcon",) + c)
    return ""


def req(path, method="GET", body=None, ctype=None, timeout=None):
    calls.append((method, path))
    if method == "POST" and path.startswith("/containers/create"):
        name = path.split("name=")[1]
        state[name] = [{"Id": "new-id", "State": {"Status": "created"}, "Config": {"Labels": (body.get("Labels") or {})}, "NetworkSettings": {"Networks": {"cs2": {}}}}]
        state["created_body"] = body
        return 201, json.dumps({"Id": "new-id"})
    if method == "GET" and path.startswith("/containers/") and path.endswith("/json"):
        name = path.split("/")[2]
        seq = state.get(name)
        if not seq:
            raise probe.DockerError("HTTP Error 404: Not Found")
        cur = seq.pop(0) if len(seq) > 1 else seq[0]
        return 200, json.dumps(cur)
    if method == "POST" and "/stop" in path:
        if state.get("stop_raises"):
            raise TimeoutError("timed out")
        return 204, b""
    if method == "POST" and "/rename" in path:
        old, new = path.split("/")[2], path.split("name=")[1]
        state[new] = [dict(state[old][-1], Name="/" + new)]
        del state[old]
        return 204, b""
    if method == "DELETE":
        state.pop("cs2", None)
        return 204, b""
    return 200, b"{}"


probe.need_lock, probe.rcon, probe.req = (lambda: None), rcon, req
import testcopy as tc  # noqa: E402

tc.a2s = lambda timeout=2.0: ("srv", "de_dust2", 0, 1)
time.sleep = lambda s: None
ID, IMAGE = "c0ffee" * 10 + "c0ff", "sha256:" + "be" * 32


def ins(status, rc=0, test=False):
    labels = {tc.TEST_LABEL: "x"} if test else {}
    return {"Id": ID, "Image": IMAGE, "RestartCount": rc, "State": {"Status": status}, "Config": {"Labels": labels}}


fails = 0


def check(name, ok):
    global fails
    print(("PASS " if ok else "FAIL ") + name)
    fails += 0 if ok else 1


check("the deployment's names come from the private config", (tc.GAME, tc.BRIDGE, tc.ASIDE, tc.BRIDGE_ASIDE) == ("cs2", "bridge", "cs2-prod-aside", "bridge-prod-aside")
      and (tc.HOST, tc.PORT) == ("203.0.113.10", 27015) and tc.FLAG == tmp / "bridge-data" / "maintenance" and tc.STATE == tmp / "state" / "testcopy")

# 1. prep, running -> exited after the stop
state.clear(); calls.clear(); state["cs2"] = [ins("running"), ins("running"), ins("exited")]
tc.cmd_prep()
check("prep: no RCON quit", not any(c[0] == "rcon" for c in calls))
check("prep: one stop, t=30", [c for c in calls if "/stop" in c[1]] == [("POST", "/containers/cs2/stop?t=30")])
check("prep: renamed aside", "cs2-prod-aside" in state and "cs2" not in state)
check("prep: rename after the stop", [i for i, c in enumerate(calls) if "/stop" in c[1]][0] < [i for i, c in enumerate(calls) if "/rename" in c[1]][0])
check("prep: the maintenance flag set in the bridge's data folder, and noted as this script's", tc.FLAG.exists() and tc.FLAG_MINE.exists())

# 2. prep, the stop call gives up first, the container exits later
tc.FLAG.unlink(missing_ok=True); tc.FLAG_MINE.unlink(missing_ok=True)
state.clear(); calls.clear(); state["cs2"] = [ins("running"), ins("running"), ins("running"), ins("exited")]; state["stop_raises"] = True
tc.cmd_prep()
check("prep after a timed-out stop call: waited, renamed", "cs2-prod-aside" in state)

# 3. prep, still running after the stop -> refused, not renamed
tc.FLAG.unlink(missing_ok=True); tc.FLAG_MINE.unlink(missing_ok=True)
state.clear(); calls.clear(); state["cs2"] = [ins("running")]
t_real = time.time
clock = [t_real()]
tc.time.time = lambda: clock.__setitem__(0, clock[0] + 5) or clock[0]
try:
    tc.cmd_prep(); refused = False
except SystemExit as e:
    refused = "REFUSED" in str(e); print("   ", e)
tc.time.time = t_real
check("prep: still running -> REFUSED, no rename", refused and not any("/rename" in c[1] for c in calls) and "cs2" in state)

# 4. prep, restarted underneath meanwhile -> warning, still renamed (it is exited now)
tc.FLAG.unlink(missing_ok=True); tc.FLAG_MINE.unlink(missing_ok=True)
state.clear(); calls.clear(); state["cs2"] = [ins("running", 0), ins("running", 0), ins("exited", 1)]
tc.cmd_prep()
check("prep: restart count changed -> renamed after the warning", "cs2-prod-aside" in state)

# 4b. prep refuses a production with somebody on, or one without VAC
for a2s, why in ((("srv", "m", 1, 1), "a human on"), (None, "no A2S answer"), (("srv", "m", 0, 0), "VAC off")):
    tc.a2s = lambda timeout=2.0, a=a2s: a
    state.clear(); calls.clear(); state["cs2"] = [ins("running")]
    try:
        tc.cmd_prep(); refused = False
    except SystemExit as e:
        refused = "REFUSED" in str(e)
    check(f"prep refuses with {why}, nothing stopped", refused and not any(c[0] == "POST" for c in calls))
tc.a2s = lambda timeout=2.0: ("srv", "de_dust2", 0, 1)

# 5. rm, running test copy
state.clear(); calls.clear(); state["cs2"] = [ins("running", test=True), ins("running", test=True), ins("exited", test=True)]
tc.cmd_rm()
check("rm: no RCON quit", not any(c[0] == "rcon" for c in calls))
check("rm: stop t=10 then delete", [c for c in calls if c[0] in ("POST", "DELETE")] == [("POST", "/containers/cs2/stop?t=10"), ("DELETE", "/containers/cs2?force=true")])

# 6. rm refuses production
state.clear(); calls.clear(); state["cs2"] = [ins("running")]
try:
    tc.cmd_rm(); refused = False
except SystemExit as e:
    refused = "NOT a test copy" in str(e)
check("rm: refuses production, no stop", refused and not any(c[0] in ("POST", "DELETE") for c in calls))


# 7. run --env: one more image variable for the copy alone, never a credential
tc.PROD_SAVED.parent.mkdir(parents=True, exist_ok=True)
tc.PROD_SAVED.write_text(json.dumps({"Image": "sha256:img", "Config": {"Env": ["CS2_MAPGROUP=mg_armsrace", "CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug"],
                                                                  "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}}))
b = tc.body(tc.env_overrides(["--env", "CS2_MAPGROUP=mg_ar_shoots"]))
check("run --env: the copy gets CS2_MAPGROUP=mg_ar_shoots instead of production's, and -insecure",
      "CS2_MAPGROUP=mg_ar_shoots" in b["Env"] and "CS2_MAPGROUP=mg_armsrace" not in b["Env"] and "CS2_ADDITIONAL_ARGS=-condebug -insecure" in b["Env"]
      and "CS2_RCONPW=pw" in b["Env"])
check("run: the copy's labels - the test label, the private config's extra labels, compose's config-hash poisoned; restart policy no",
      b["Labels"][tc.TEST_LABEL] == tc.TAG and b["Labels"]["monitor.mute"] == "true" and b["Labels"]["com.docker.compose.config-hash"] == "test-config-not-from-main"
      and b["HostConfig"]["RestartPolicy"]["Name"] == "no")
check("run --env=K=V works too", tc.env_overrides(["--env=CS2_MAPGROUP=mg_ar_shoots"]) == {"CS2_MAPGROUP": "mg_ar_shoots"})
for bad in (["--env", "CS2_RCONPW=x"], ["--env", "SRCDS_TOKEN=x"], ["--env", "CS2_PW=x"], ["--env", "CS2_ADDITIONAL_ARGS=x"], ["--env", "novalue"], ["--bogus"]):
    try:
        tc.env_overrides(bad); refused = False
    except SystemExit:
        refused = True
    check("run --env refuses %s" % " ".join(bad), refused)
check("run without --env: the copy as before", tc.body()["Env"].count("CS2_MAPGROUP=mg_armsrace") == 1)

# 7b. the copy on production's own network endpoint, from production's inspect: its networks, pinned IP, MAC and aliases
net = {"game": {"IPAMConfig": {"IPv4Address": "192.0.2.100"}, "MacAddress": "02:00:00:00:00:64", "Aliases": ["cs2", "game"], "IPAddress": "192.0.2.100"}}
tc.PROD_SAVED.write_text(json.dumps({"Image": "sha256:img", "Config": {"Env": [], "Labels": {}}, "HostConfig": {}, "NetworkSettings": {"Networks": net}}))
check("run: the copy's endpoint is production's - network, IP, MAC and aliases from its inspect",
      tc.body()["NetworkingConfig"]["EndpointsConfig"] == {"game": {"IPAMConfig": {"IPv4Address": "192.0.2.100"}, "MacAddress": "02:00:00:00:00:64", "Aliases": ["cs2", "game"]}})
check("pinned: (network, IP, MAC)", tc.pinned({"NetworkSettings": {"Networks": net}}) == [("game", "192.0.2.100", "02:00:00:00:00:64")])

# 8. run against a production without the settings text: the variables do the work, nothing else is touched
tc.wait_a2s = lambda want, limit: (("srv", "m", 0, want), 1)
PROD = {"Image": "sha256:img", "Config": {"Env": ["CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug"], "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}}


def run_copy(prod):
    state.clear(); calls.clear()
    tc.PROD_SAVED.write_text(json.dumps(prod))
    state["cs2-prod-aside"] = [ins("exited")]


def steps():
    return [c[1].split("?")[0] for c in calls if c[0] in ("POST", "PUT", "DELETE")]


run_copy(PROD)
tc.cmd_run()
check("run, no settings text in production: create and start as ever", steps() == ["/containers/create", "/containers/cs2/start"])
check("run, no settings text in production: the variables that do the work are in the copy's environment, and nothing else is added",
      all(f"{k}={v}" in state["created_body"]["Env"] for k, v in tc.OFF.items()) and "CS2_ADDITIONAL_ARGS=-condebug -insecure" in state["created_body"]["Env"]
      and not any(e.startswith(tc.CONTENT) for e in state["created_body"]["Env"]))

# 9. run against a production on the settings text: its text, with the automatic actions off, in the copy's environment
TEXT = '''{
  "schema": 1,
  "server": { "rconPassword": "env:CS2_RCONPW" },
  "bridge": { "secret": "theBridgeSecret", "url": "http://bridge:8080" },
  "hud": { "addon": "3823993566", "check": "warn" },
  "restarts": { "daily": "07:00" },
  "sync": { "mail": { "from": "A // B <a@example.com>" } }
}
'''
SWITCHED = {"Image": "sha256:img", "Config": {"Env": ["CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug", tc.CONTENT + "=" + TEXT], "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}}
run_copy(SWITCHED)
out = io.StringIO()
with contextlib.redirect_stdout(out):
    tc.cmd_run()
env = state["created_body"]["Env"]
texts = [e for e in env if e.startswith(tc.CONTENT + "=")]
check("run, settings text: no call beyond create and start, one text in the copy's environment", steps() == ["/containers/create", "/containers/cs2/start"] and len(texts) == 1)
copy_settings = json.loads(texts[0].split("=", 1)[1])
check("run, settings text: the watcher's automatic actions off, the deletion off, no visits to the HUD item's page",
      copy_settings["restarts"] == {"daily": "off", "updates": False, "newVersions": False, "freezeRecovery": False, "downloadRestart": False}
      and copy_settings["maps"] == {"delete": "off", "backgroundDownloads": False, "downloadLine": False} and copy_settings["hud"]["itemPage"] is False)
check("run, settings text: every other value production's, a // in a string kept, a reference still a reference", copy_settings["server"]["rconPassword"] == "env:CS2_RCONPW"
      and copy_settings["bridge"]["secret"] == "theBridgeSecret" and copy_settings["hud"]["addon"] == "3823993566" and copy_settings["hud"]["check"] == "warn"
      and copy_settings["sync"]["mail"]["from"] == "A // B <a@example.com>" and "CS2_RCONPW=pw" in env)
check("run, settings text: no secret printed", "theBridgeSecret" not in out.getvalue() and "settings text" in out.getvalue())
commented = '// a comment\n{ "schema": 1, "hud": { "addon": "1", }, }\n'
check("run, settings text with comments and trailing commas, as core takes it: read, and it leaves without them",
      json.loads(tc.settings_for_copy(tc.CONTENT + "=" + commented).split("=", 1)[1])["hud"] == {"addon": "1", "itemPage": False})
props = json.loads((simconfig.REPO / "docs" / "settings.schema.json").read_text(encoding="utf-8"))["properties"]
check("run, settings text: every overridden key is a real one (docs/settings.schema.json)",
      all(k in props[s]["properties"] for s, v in tc.SETTINGS_OFF.items() for k in v))
told = re.search(r'log\.Line\("(restarts\.updates,[^"]*)nothing to do', (simconfig.REPO / "server" / "OverStrike2.Server" / "Program.cs").read_text(encoding="utf-8"))
keys = re.findall(r"\b(restarts|maps)\.(\w+)", told.group(1)) if told else []
check("run, settings text: every switch that keeps the watcher running is off, so it exits at once and the copy has none (%d, from os2-server's own line)" % len(keys),
      keys and all(tc.SETTINGS_OFF[s].get(k) in (False, "off") for s, k in keys))

# 10. a copy that cannot get its overrides is never made
for bad_text, what in (("{ not json", "that does not parse"), ("[1, 2]", "that is no object")):
    run_copy({"Image": "sha256:img", "Config": {"Env": [tc.CONTENT + "=" + bad_text], "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}})
    try:
        tc.cmd_run(); refused = None
    except SystemExit as e:
        refused = str(e)
    check(f"run, a settings text {what}: refused before anything is created, quoting nothing of it", refused and "REFUSED" in refused and not steps() and "not json" not in refused)
try:
    tc.env_overrides(["--env", tc.CONTENT + "=x"]); refused = False
except SystemExit:
    refused = True
check("run --env refuses the settings text: it holds secrets", refused)

# 10b. run --downloads: maps.backgroundDownloads production's, so the watcher stays up for it alone; never while production's watcher has a
# restart of its own to judge (its state's pending record), never without the settings text
helped = []
watch_state = {"text": '{"schema": 1, "pending": null}\n', "code": 0}
real_helper = tc.helper


def fake_helper(name, binds, script, image=None):
    helped.append((name, binds, script))
    return watch_state["code"], watch_state["text"]


tc.helper = fake_helper
GAMEDIR = {"Binds": ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata:/data:ro"], "RestartPolicy": {"Name": "always"}}


def run_out(prod, argv):
    run_copy(prod)
    helped.clear()
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            tc.cmd_run(argv)
        return None, out.getvalue()
    except SystemExit as e:
        return str(e), out.getvalue()


DL = dict(SWITCHED, HostConfig=GAMEDIR)
refused, said = run_out(DL, ["--downloads"])
copy_settings = json.loads(next(e for e in state["created_body"]["Env"] if e.startswith(tc.CONTENT + "=")).split("=", 1)[1])
check("run --downloads: maps.backgroundDownloads left as production has it (absent: core's default, on), every other switch still off",
      refused is None and copy_settings["maps"] == {"delete": "off", "downloadLine": False}
      and copy_settings["restarts"] == {"daily": "off", "updates": False, "newVersions": False, "freezeRecovery": False, "downloadRestart": False}
      and copy_settings["hud"]["itemPage"] is False)
check("run --downloads: says so, and the overridden keys it names leave it out",
      "maps.backgroundDownloads at production's value (on)" in said and "maps.delete, maps.downloadLine, hud.itemPage overridden" in said and "theBridgeSecret" not in said)
check("run --downloads: production's watcher state read by a helper with the game's tree read-only, before anything is created",
      [(n, b) for n, b, _ in helped] == [("cs2-watch-read", ["/srv/game:/g:ro"])] and "update-watch/state.json" in helped[0][2])
OFF_IN_PROD = dict(DL, Config=dict(DL["Config"], Env=[e if not e.startswith(tc.CONTENT + "=") else tc.CONTENT + "=" + TEXT.replace('"schema": 1,', '"schema": 1, "maps": { "backgroundDownloads": false },') for e in DL["Config"]["Env"]]))
refused, said = run_out(OFF_IN_PROD, ["--downloads"])
copy_settings = json.loads(next(e for e in state["created_body"]["Env"] if e.startswith(tc.CONTENT + "=")).split("=", 1)[1])
check("run --downloads, production has it off: the copy too, and it says the watcher still exits",
      refused is None and copy_settings["maps"]["backgroundDownloads"] is False and "still has nothing to do" in said)
for text, code, why in (('{"schema": 1, "pending": {"kind": "version", "at": 1}}\n', 0, "a restart of its own to judge"), ("not json", 0, "cannot be read"),
                        ("", 1, "cannot be read")):
    watch_state.update(text=text, code=code)
    refused, said = run_out(DL, ["--downloads"])
    check(f"run --downloads refuses when production's watcher state has {why}, nothing created", refused and "REFUSED" in refused and why in refused and not steps())
watch_state.update(text="{}\n", code=0)
refused, said = run_out(DL, ["--downloads"])
check("run --downloads with no watcher state yet: taken", refused is None and steps() == ["/containers/create", "/containers/cs2/start"])
refused, said = run_out(dict(PROD, HostConfig=GAMEDIR), ["--downloads"])
check("run --downloads refuses a production without the settings text, nothing created", refused and "has none" in refused and not steps())
refused, said = run_out(DL, ["--downloads", "--env", "CS2_MAPGROUP=mg_ar_shoots"])
check("run --downloads with --env: both taken", refused is None and "CS2_MAPGROUP=mg_ar_shoots" in state["created_body"]["Env"])
refused, said = run_out(DL, [])
copy_settings = json.loads(next(e for e in state["created_body"]["Env"] if e.startswith(tc.CONTENT + "=")).split("=", 1)[1])
check("run without --downloads: no helper, the downloads off as ever", not helped and copy_settings["maps"]["backgroundDownloads"] is False)

# 10c. beside a candidate bridge the copy reads the candidate's copy of the bridge's data, read-only as production reads production's
CAND = {"Id": "cand", "Config": {"Labels": {tc.TEST_LABEL: tc.BRIDGE_TAG}}, "HostConfig": {"Binds": ["/srv/bdata-candidate:/data"]}, "State": {"Status": "running"}}


def binds_beside(cand, prod=DL):
    run_copy(prod)
    if cand:
        state["bridge"] = [cand]
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            tc.cmd_run([])
        return None, state["created_body"]["HostConfig"].get("Binds"), out.getvalue()
    except SystemExit as e:
        return str(e), None, out.getvalue()


refused, binds, said = binds_beside(CAND)
check("run beside a candidate: the bridge data at /data is the candidate's copy, its read-only kept; the game's tree as it is",
      refused is None and binds == ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata-candidate:/data:ro"] and "<data>-candidate" in said and "/srv/" not in said)
refused, binds, said = binds_beside(None)
check("run without a candidate: production's bridge data", refused is None and binds == GAMEDIR["Binds"] and "candidate" not in said)
refused, binds, said = binds_beside(dict(CAND, Config={"Labels": {}}))
check("run beside production's own bridge: production's bridge data", refused is None and binds == GAMEDIR["Binds"])
refused, binds, said = binds_beside(dict(CAND, HostConfig={"Binds": ["/elsewhere:/data"]}))
check("run beside a candidate whose data is not the copy beside production's: refused, nothing created", refused and "REFUSED" in refused and not steps())
refused, binds, said = binds_beside(dict(CAND, HostConfig={"Binds": []}))
check("run beside a candidate without a /data bind: refused, nothing created", refused and "REFUSED" in refused and not steps())
MOVED = dict(DL, HostConfig={"Binds": ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata:/bridge:ro"]},
             Config=dict(DL["Config"], Env=[e if not e.startswith(tc.CONTENT + "=") else tc.CONTENT + "=" + TEXT.replace('"schema": 1,', '"schema": 1, "paths": { "bridgeData": "/bridge" },') for e in DL["Config"]["Env"]]))
refused, binds, said = binds_beside(CAND, MOVED)
check("run beside a candidate, paths.bridgeData elsewhere: the bind there is the one swapped", refused is None and binds == ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata-candidate:/bridge:ro"])
refused, binds, said = binds_beside(CAND, dict(DL, HostConfig={"Binds": ["/srv/game:/home/steam/cs2-dedicated"]}))
check("run beside a candidate, production reads no bridge data: nothing to swap, and it says so", refused is None and binds == ["/srv/game:/home/steam/cs2-dedicated"] and "reads no bridge data" in said)
state.pop("bridge", None)
tc.helper = real_helper

# 11. restore: production back on the endpoint saved at prep, running what its plugins folder holds, or the flag stays on
up = dict(ins("running"), NetworkSettings={"Networks": net})
installed = {"answer": "files 4 differ 0"}
probe.exec_in = lambda container, cmd, env=None, timeout=120: calls.append(("exec", container, cmd[-1][:40])) or installed["answer"]
for saved, plugins, want_ok, what in ((up, "files 4 differ 0", True, "the same container on the saved endpoint, its own plugins in the game's tree"),
                                      (dict(up, NetworkSettings={"Networks": {"game": dict(net["game"], MacAddress="02:00:00:00:00:65")}}), "files 4 differ 0", False, "another MAC than at prep"),
                                      (None, "files 4 differ 0", False, "no saved inspect"),
                                      (up, "DIFF OverStrike2/OverStrike2.dll\nfiles 4 differ 1", False, "a plugin in the game's tree that is not its plugins folder's")):
    state.clear(); calls.clear()
    state["cs2-prod-aside"] = [up]
    installed["answer"] = plugins
    tc.FLAG.write_text("x"); tc.FLAG_MINE.write_text("1")
    if saved is None:
        tc.PROD_SAVED.unlink(missing_ok=True)
    else:
        tc.PROD_SAVED.write_text(json.dumps(saved))
    with contextlib.redirect_stdout(io.StringIO()):
        tc.cmd_restore()
    check(f"restore, {what}: the flag {'removed' if want_ok else 'left on'}", tc.FLAG.exists() != want_ok and tc.FLAG_MINE.exists() != want_ok)
check("restore: the plugins compared in production itself, its plugins folder against the game's tree",
      any(c[0] == "exec" and c[1] == "cs2" and c[2].startswith("cd /home/steam/cs2-plugins") for c in calls))

# 12. probe.py unload on a test copy keeps the server lock: production is still aside; on production it releases it
probe.exec_in_game = lambda cmd: "ls: cannot access '/tmp/ggprobe': No such file or directory"
for test, kept, what in ((True, True, "a test copy: the lock kept"), (False, False, "production: the lock released")):
    state.clear()
    state["cs2"] = [ins("running", test=test)]
    probe.lock_path().unlink(missing_ok=True)
    with contextlib.redirect_stdout(io.StringIO()):
        probe.cmd_lock("unload test")
        probe.cmd_unload()
    check(f"probe.py unload on {what}", probe.lock_path().exists() == kept)
probe.lock_path().unlink(missing_ok=True)

shutil.rmtree(tmp)
print("failures:", fails)
sys.exit(1 if fails else 0)
