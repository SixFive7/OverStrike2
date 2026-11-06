"""Offline test of bridgepost.py against an in-memory Docker: it sends only with a test copy, the candidate on its copy of the data and production aside; the secret
never reaches the output; the bodies are the bridge's own format; and the two programs it runs inside the game container do what they say, against a local server.

    python -I tools/client/probe/test_bridgepost.py
"""
import contextlib
import copy
import http.server
import io
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import simconfig  # noqa: E402

tmp = simconfig.make("bp-sim-")
SECRET = "s3cr3t-log-word"
TEST_LABEL = "cs2.test-config"
BRIDGE_TAG = "candidate bridge (tools/client/probe/testcopy.py): a new image on a copy of the bridge's data"
CLIENT = "42424242"                           # a made-up account for the real client, in what the bridge is told about it
execs, lock = [], {"held": True}
state = {"mv": "mv\t1793900000\topen\t55\t2\t0\tmid\t\t1", "answer": "ok: taken at {url}"}


def world():
    def c(status="running", labels=None, env=(), binds=()):
        return {"Id": "x" * 64, "State": {"Status": status}, "Config": {"Labels": labels or {}, "Env": list(env)}, "HostConfig": {"Binds": list(binds)}, "Mounts": []}
    return {"cs2": c(labels={TEST_LABEL: "copy"}),
            "bridge": c(labels={TEST_LABEL: BRIDGE_TAG}, env=[f"LOG_SECRET={SECRET}", "HTTP_PORT=8081"], binds=["/data/bridge-candidate:/data:rw"]),
            "cs2-prod-aside": c("exited"), "bridge-prod-aside": c("exited")}


containers = world()
import probe  # noqa: E402


def req(path, method="GET", body=None, ctype=None, timeout=None):
    if method == "GET" and path.startswith("/containers/"):
        d = containers.get(path.split("/")[2])
        if d is None:
            raise probe.DockerError("HTTP Error 404: Not Found")
        return 200, json.dumps(d)
    if method == "POST" and path == "/containers/cs2/exec":
        execs.append(copy.deepcopy(body))
        return 201, json.dumps({"Id": "e1"})
    if method == "POST" and path.startswith("/exec/"):
        url = next(e for e in execs[-1]["Env"] if e.startswith("OS2_BRIDGE_URL=")).split("=", 1)[1]
        if "/hud/" in url:
            return 200, (state["mv"] + "\n").encode()    # GET_VOTE passes on the vote's line alone (held below against a real server)
        if "/log/" in url:
            return 200, b"200 \n"
        if "/act/" in url:
            return 200, b"200 gg-hud\tHUD holds Secret Name\n"
        return 200, ("200 " + state["answer"].format(url=url)).encode()    # a bridge that echoes its URL: the scrub must catch it
    raise AssertionError(f"unexpected call {method} {path}")


def need_lock():
    if not lock["held"]:
        sys.exit("the server lock is not held")


probe.need_lock, probe.req, probe.rcon = need_lock, req, (lambda *c: "")
import bridgepost as bp  # noqa: E402

bp.time = types.SimpleNamespace(sleep=lambda s: None, strftime=__import__("time").strftime)
failures, checks = 0, 0


def check(ok, what):
    global failures, checks
    checks += 1
    if not ok:
        failures += 1
        print("FAIL ", what)


def run(*argv):
    """(exit code or None, what it printed)"""
    out = io.StringIO()
    code = None
    with contextlib.redirect_stdout(out):
        try:
            bp.main(list(argv))
        except SystemExit as e:
            code = e.code
    return code, out.getvalue()


def refused(why, *argv):
    before = len(execs)
    code, _ = run(*(argv or ("restart", "our-update", "match-end", "0")))
    check(isinstance(code, str) and len(execs) == before, f"{why}: refused and nothing sent (exit {code!r})")


# every send, the OK path first
code, out = run("check")
check(code is None and "all checks hold" in out and not execs, "check: all hold, nothing sent")

code, out = run("restart", "our-update", "match-end", "0")
e = execs[-1]
check(code is None, "restart: sent")
check(e["Cmd"][0] == "python3" and e["Cmd"][3] == "gg-restart\t1\tour-update\tmatch-end\t0", f"restart: the bridge's own body: {e['Cmd'][3:]!r}")
check(e["Env"] == [f"OS2_BRIDGE_URL=http://bridge:8081/restart-notice/{SECRET}"], "restart: the candidate's name, port, route and secret, in the exec's environment")
check(SECRET not in " ".join(e["Cmd"]), "the secret is not on the exec's command line")
check(SECRET not in out and "<secret>" in out, "the secret never reaches the output, even when the answer holds it")
for answer in ("refused: the kind is daily", "busy: the bridge did not get to it within 5 s", "error: boom"):
    state["answer"] = answer
    code, out = run("restart", "our-update", "match-end", "0")
    check(code == 1 and answer in out, f"an answer that is not ok fails the call and shows: {answer}")
state["answer"] = "ok: taken at {url}"

run("restart-cancel", "daily")
check(execs[-1]["Cmd"][3] == "gg-restart\t1\tcancel\tdaily", "restart-cancel: its body")
run("updated", "game-update", "CS2", "1.41.9.6")
check(execs[-1]["Cmd"][3] == "gg-updated\t1\tgame-update\tCS2 1.41.9.6" and "/updated/" in execs[-1]["Env"][0], "updated: its body and route")

code, out = run("fake-on", "2")
lines = execs[-1]["Cmd"][3].split("\n")
player = re.compile(r'^\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d\.000 - "(.+?)<(\d+)><(\[U:1:\d+\])><([^>]*)>" (.+)$')    # bridge/Program.cs's PlayerRx behind the stamp
check(code is None and "/log/" in execs[-1]["Env"][0] and len(lines) == 4 and all(player.match(l) for l in lines), f"fake-on 2: four log lines the bridge's PlayerRx reads: {lines[:1]}")
check(player.match(lines[0]).group(5).startswith("connected, address") and player.match(lines[-1]).group(5) == "entered the game" and "Test A" in lines[0], "fake-on: connected, then entered, under test names")
code, out = run("fake-off")
gone = execs[-1]["Cmd"][3].split("\n")
check(code is None and len(gone) == 5 and all("disconnected (reason" in l and "SHUTDOWN" not in l for l in gone) and "HUDtest" in gone[-1], "fake-off: every fake leaves for real, and HUDtest (a reason with SHUTDOWN in it would only be a reload)")

code, out = run("act", "0", "next")
check(code is None and execs[-1]["Cmd"][3] == "[U:1:42424251]\tTest A\t!next" and "/act/" in execs[-1]["Env"][0], "act: the fake's Start a vote")
check("Secret Name" not in out, "an /act answer, the live state with its names, is not printed")
run("act", "3", "time10")
check(execs[-1]["Cmd"][3] == "[U:1:42424254]\tTest D\t!time 10", "act: a word becomes the bridge's own command")
run("fake-crash")
crash = execs[-1]["Cmd"][3].split("\n")
check(len(crash) == 3 and all("NETWORK_DISCONNECT_TIMEDOUT" in l for l in crash), "fake-crash: three players time out (BrokenWhy.cs: the third marks the map)")
run("quit-notice")
check(execs[-1]["Cmd"][3] == "gg-quit\t1\tdaily" and "/quitting/" in execs[-1]["Env"][0], "quit-notice: its body and route")
run("client-off", CLIENT)
off = execs[-1]["Cmd"][3]
check(player.match(off) and player.match(off).group(1) == "Alice" and player.match(off).group(3) == f"[U:1:{CLIENT}]" and "disconnected (reason" in off and "SHUTDOWN" not in off, f"client-off: a real departure, under the test name: {off}")
run("client-on", CLIENT)
on = execs[-1]["Cmd"][3]
check(player.match(on) and player.match(on).group(1) == "Alice" and player.match(on).group(5) == 'say "back"', f"client-on: a chat line, under the test name: {on}")

before = len(execs)
code, out = run("vote", "1", "4")
check(code is None and len(execs) == before + 2 and "/hud/" in execs[before]["Env"][0] and "?" not in execs[before]["Env"][0] and execs[before + 1]["Cmd"][3] == "[U:1:42424252]\tTest B\t!mv 1793900000 4", f"vote: the round from the bridge's state, the click in /act's format: {execs[-1]['Cmd'][3:]!r}")
state["mv"] = "mv\t1793900000\tdone\t0\t2\t2\tmid\t\t1"
before = len(execs)
code, _ = run("vote", "1", "4")
check(isinstance(code, str) and "no vote is open" in code and len(execs) == before + bp.ROUND_TRIES and all("/hud/" in x["Env"][0] for x in execs[before:]), "vote: refused when no vote comes up: only the state was read")
state["mv"] = ""
code, _ = run("vote", "1", "4")
check(isinstance(code, str) and "no vote is open" in code, "vote: refused when the state has no vote line at all")
state["mv"] = "mv\t1793900000\topen\t55\t2\t0\tmid\t\t1"

# a candidate with a settings text instead of the environment
containers["bridge"]["Config"]["Env"] = ['OVERSTRIKE2_SETTINGS_CONTENT={"schema":1,"bridge":{"secret":"env:THE_WORD","port":9000}}', f"THE_WORD={SECRET}"]
run("restart-cancel", "daily")
check(execs[-1]["Env"] == [f"OS2_BRIDGE_URL=http://bridge:9000/restart-notice/{SECRET}"], "the settings text's port and secret, by its env: reference")
containers["bridge"]["Config"]["Env"] = ['OVERSTRIKE2_SETTINGS_CONTENT={"schema":1,"bridge":{"secret":"file:/run/x"}}']
refused("a secret the inspect cannot give")

# every way it must refuse
containers.clear()
containers.update(world())
lock["held"] = False
before = len(execs)
code, _ = run("restart", "our-update", "match-end", "0")
check(code is not None and len(execs) == before, "no server lock: nothing sent")
lock["held"] = True

for why, change in (("cs2 is production", lambda w: w["cs2"]["Config"].update(Labels={})),
                    ("no cs2 at all", lambda w: w.pop("cs2")),
                    ("the copy is stopped", lambda w: w["cs2"]["State"].update(Status="exited")),
                    ("the bridge is production's", lambda w: w["bridge"]["Config"].update(Labels={})),
                    ("the bridge is some other test thing", lambda w: w["bridge"]["Config"].update(Labels={TEST_LABEL: "something else"})),
                    ("no bridge at all", lambda w: w.pop("bridge")),
                    ("the candidate is stopped", lambda w: w["bridge"]["State"].update(Status="exited")),
                    ("the candidate's /data is production's folder", lambda w: w["bridge"]["HostConfig"].update(Binds=["/data/bridge:/data:rw"])),
                    ("production's game server is not aside", lambda w: w.pop("cs2-prod-aside")),
                    ("production's bridge is not aside", lambda w: w.pop("bridge-prod-aside")),
                    ("production's bridge is aside but running", lambda w: w["bridge-prod-aside"]["State"].update(Status="running")),
                    ("production's game server is aside but running", lambda w: w["cs2-prod-aside"]["State"].update(Status="running"))):
    containers.clear()
    containers.update(world())
    change(containers)
    refused(why)
containers.clear()
containers.update(world())

# bad arguments: no free route, no free text
for args in (("restart", "nuke", "now", "5"), ("restart", "daily", "soon", "5"), ("restart", "daily", "now", "x"), ("updated", "daily", "a;b"), ("fake-on", "9"), ("fake-on", "x"), ("act", "0"), ("act", "7", "next"), ("act", "0", "quit"), ("act", "0", "!rcon x"), ("client-off",), ("client-on", "x"),
             ("vote", "7", "1"), ("vote", "x", "1"), ("vote", "1", "x"), ("raw", "quitting", "x")):
    before = len(execs)
    code, _ = run(*args)
    check(code is not None and len(execs) == before, f"bad arguments {args}: nothing sent")
check(sum("/quitting/" in e["Env"][0] for e in execs) == 1, "/quitting is reached by quit-notice alone")


# the two programs that run inside the game container, for real, against a local server standing in for the bridge
class Bridge(http.server.BaseHTTPRequestHandler):
    got = []

    def answer(self, text):
        data = text.encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        Bridge.got.append((self.path, self.rfile.read(int(self.headers["Content-Length"])).decode()))
        self.answer("ok: heard" if "notice" in self.path else "")

    def do_GET(self):
        self.answer("gg-hud\tname\nmvo\t1\tHUDtest\nmv\t77\topen\t50\n" + "x" * 200000 + "\n")

    def log_message(self, *a):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Bridge)
threading.Thread(target=server.serve_forever, daemon=True).start()
base = f"http://127.0.0.1:{server.server_address[1]}"


def inner(path, *cmd):
    r = subprocess.run([sys.executable, "-I", "-c", *cmd], env={**os.environ, "OS2_BRIDGE_URL": base + path}, capture_output=True, text=True, timeout=30, stdin=subprocess.DEVNULL)
    return r.returncode, r.stdout, r.stderr


code, out, err = inner("/restart-notice/x", bp.POST, "gg-restart\t1\tcancel\tdaily")
check(code == 0 and out.strip() == "200 ok: heard" and Bridge.got[-1] == ("/restart-notice/x", "gg-restart\t1\tcancel\tdaily"), f"POST runs and passes the answer on: {out!r} {err[-200:]!r}")
code, out, err = inner("/log/x", bp.POST, "line one\nline two")
check(code == 0 and out.strip() == "200" and Bridge.got[-1][1] == "line one\nline two", f"POST with an empty answer, and a body of two lines: {out!r} {err[-200:]!r}")
code, out, err = inner("/hud/x", bp.GET_VOTE)
check(code == 0 and out.strip() == "mv\t77\topen\t50", f"GET_VOTE gives the vote's line alone, however long the state: {out[:80]!r} {err[-200:]!r}")
server.shutdown()

shutil.rmtree(tmp)
print(f"bridgepost: {checks - failures} of {checks} checks passed" if failures else f"bridgepost: {checks} checks passed")
sys.exit(1 if failures else 0)
