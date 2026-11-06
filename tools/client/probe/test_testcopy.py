"""Offline test of testcopy.py's prep, rm and run against an in-memory Docker: no RCON quit, the rename only once production has exited,
never a credential as an override; the copy on production's own network endpoint; without a settings text in production run works on the
variables, with one the copy's environment holds production's text with the automatic actions off, or there is no copy.

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

# 11. restore: production back on the endpoint saved at prep, or the flag stays on
up = dict(ins("running"), NetworkSettings={"Networks": net})
for saved, want_ok, what in ((up, True, "the same container on the saved endpoint"),
                             (dict(up, NetworkSettings={"Networks": {"game": dict(net["game"], MacAddress="02:00:00:00:00:65")}}), False, "another MAC than at prep"),
                             (None, False, "no saved inspect")):
    state.clear(); calls.clear()
    state["cs2-prod-aside"] = [up]
    tc.FLAG.write_text("x"); tc.FLAG_MINE.write_text("1")
    if saved is None:
        tc.PROD_SAVED.unlink(missing_ok=True)
    else:
        tc.PROD_SAVED.write_text(json.dumps(saved))
    with contextlib.redirect_stdout(io.StringIO()):
        tc.cmd_restore()
    check(f"restore, {what}: the flag {'removed' if want_ok else 'left on'}", tc.FLAG.exists() != want_ok and tc.FLAG_MINE.exists() != want_ok)

shutil.rmtree(tmp)
print("failures:", fails)
sys.exit(1 if fails else 0)
