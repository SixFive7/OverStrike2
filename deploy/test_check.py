#!/usr/bin/env python3
"""Offline tests for check.py: the guard's decision and what it shows, the settings and the secrets, the roll-call, the audits, and
verify from end to end - against stand-ins: a Docker API kept in memory, A2S, the audits' commands, git. Nothing reaches a server.

    python -I deploy/test_check.py
"""
import contextlib
import io
import os
import shutil
import sys
import tempfile
import types

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import check as C  # noqa: E402

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


class Cfg:
    """The private config as a flat {dotted key: value}; a required key that is missing ends the run, as os2.Config does (exit 2)."""

    def __init__(self, values, path="the-private-config.toml"):
        self.values, self.path = values, path

    def get(self, dotted, default=None, required=False):
        if dotted in self.values and self.values[dotted] not in ("", None):
            return self.values[dotted]
        if required:
            raise SystemExit(2)
        return self.values.get(dotted, default)


GAME, BRIDGE, SYNC = "cs2", "bridge", "sync"
N = {"game": GAME, "bridge": BRIDGE, "sync": SYNC}
BASE = {"bridge.game_container": GAME, "bridge.container": BRIDGE, "bridge.sync_container": SYNC, "server.address": "192.0.2.1:27015",
        "bridge.image": "localhost/overstrike2-bridge"}
BS = "BridgeValueOfThisTest0123"   # the bridge's secret in these tests: letters and digits, as bridge.secret holds
RP = "RconValueOfThisTest4567"     # the RCON password in these tests
HOST_DIR = "/srv/overstrike2/"     # the host's folders a container mounts


def out_of(fn):
    """fn's result (or the exit code it ended with) and every word it printed."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        try:
            r = fn()
        except SystemExit as e:
            r = ("exit", e.code)
    return r, out.getvalue()


# ---- the guard's decision

check("guard: nobody on - GO", C.decide(0, False) == (0, "GO: nobody is on"))
check("guard: somebody on - NO, exit 3", C.decide(2, False, GAME)[0] == 3 and C.decide(2, False, GAME)[1].startswith("NO: a redeploy would restart cs2 with 2"))
check("guard: no A2S answer is not a go", C.decide(None, False)[0] == 3 and "an unknown number of" in C.decide(None, False)[1])
check("guard: --allow-kick goes with players on, and says so",
      C.decide(3, True, GAME) == (0, "GO (--allow-kick): cs2 restarts with 3 player(s) on - the owner said so"))


# ---- the guard from end to end

class Stand:
    """The calls check.py makes on OverStrike2's Docker: inspect, the container list, an exec in the game server. Any other call fails."""

    def __init__(self, containers, health="os2-health 1\nlog pushes 3\n", images=None):
        self.containers, self.health, self.execs = containers, health, []
        self.images = images if images is not None else {"localhost/overstrike2-bridge:latest": {"Id": "sha256:bridge-image"},
                                                         "docker.io/joedwards32/cs2": {"Id": "sha256:game-image"}}

    def inspect(self, kind, name):
        return self.images.get(name) if kind == "images" else self.containers.get(name)

    def call(self, method, path, **kw):
        if method != "GET" or not path.startswith("/containers/json"):
            raise AssertionError("an unexpected call: " + method + " " + path)
        return 200, [{"Names": ["/" + n], "State": (c.get("State") or {}).get("Status"), "Status": "Up" if (c.get("State") or {}).get("Running") else "Exited (0)",
                      "Labels": (c.get("Config") or {}).get("Labels") or {}} for n, c in self.containers.items()]

    def exec_in(self, name, argv, timeout=60):
        self.execs.append((name, argv))
        return self.health


def box(env=(), mounts=(), labels=None, image="sha256:game-image", cimage="docker.io/joedwards32/cs2", running=True):
    return {"Image": image, "RestartCount": 0, "State": {"Running": running, "Status": "running" if running else "exited", "StartedAt": "start-1"},
            "Config": {"Env": list(env), "Image": cimage, "Labels": dict(labels or {})}, "Mounts": [{"Source": s, "Destination": t} for s, t in mounts]}


def guard_run(on, values=None, d=None, allow_kick=False, git=None, fetch=None):
    chk = C.Check(Cfg({**BASE, **(values or {})}), d=d, humans=lambda address: on, fetch=fetch)
    if git:
        chk.git = git
    return out_of(lambda: chk.guard(allow_kick))


saved = dict(os.environ)
try:
    os.environ.pop("OS2_TEST_KEY", None)
    nokey = {"docker.api": "https://docker.example.com:2376", "docker.headers": {"X-API-Key": "env:OS2_TEST_KEY"}}
    r, out = guard_run(0, nokey)
    check("guard without the Docker API key: still decides by A2S - GO with nobody on, the drift not shown, the variable named",
          r == 0 and "GO: nobody is on" in out and "docker.headers.X-API-Key reads OS2_TEST_KEY, which is not set: the drift is not shown" in out)
    r, out = guard_run(2, nokey)
    check("guard without the key and players on: NO, exit 3", r == 3 and "NO: a redeploy would restart cs2 with 2 player(s) on" in out)
    r, out = guard_run(None, nokey)
    check("guard: no A2S answer - NO, exit 3, said as unknown", r == 3 and "humans on the server: unknown (no A2S answer)" in out)
    r, out = guard_run(0, {**nokey, "server.address": ""})
    check("guard: no server.address in the config - exit 2, never a go", r == ("exit", 2) and "GO" not in out)
finally:
    os.environ.clear()
    os.environ.update(saved)

COMPOSE_OLD = "x-settings: &settings |\n  {}\nservices:\n  cs2:\n    image: game\n    environment:\n      OVERSTRIKE2_SETTINGS_CONTENT: *settings\n  bridge:\n    image: b1\n"
COMPOSE_BRIDGE = COMPOSE_OLD.replace("image: b1", "image: b2")                     # only the bridge's service changed
COMPOSE_ANCHOR = COMPOSE_OLD.replace("  {}\n", '  { "schema": 1 }\n')               # the settings anchor, which the game server takes
calls = []


def fake_git(versions):
    def git(repo, *args):
        calls.append(args)
        if args[0] == "show":
            return versions.get(args[1].split(":", 1)[0])
        return ""
    return git


game_labels = {C.PROJECT: "os2", C.SERVICE: "cs2"}
stack = {"stack.repo": "the-repo", "stack.file": "stack/compose.yml", "stack.ref": "origin/main",
         "stack.deployed_url": "https://manager.example.com/stacks/7", "stack.deployed_field": "Git.Commit"}
d = Stand({GAME: box(labels=game_labels)})
r, out = guard_run(0, stack, d=d, git=fake_git({"1111111aaaa": COMPOSE_OLD, "origin/main": COMPOSE_OLD}),
                   fetch=lambda url: {"Git": {"Commit": "1111111aaaa"}})
check("guard, the drift: the image, and the game server's service unchanged since the commit deployed - after a fetch of the ref's remote",
      r == 0 and "cs2 image: container game-image, tag docker.io/joedwards32/cs2 -> game-image" in out
      and "the cs2 service in stack/compose.yml: deployed 1111111 vs origin/main: unchanged" in out and ("fetch", "-q", "origin") in calls)
calls.clear()
r, out = guard_run(0, stack, d=d, git=fake_git({"1111111aaaa": COMPOSE_OLD, "origin/main": COMPOSE_ANCHOR}),
                   fetch=lambda url: {"Git": {"Commit": "1111111aaaa"}})
check("guard, the drift: a change in the settings anchor the game server takes is a change of its service", "vs origin/main: changed" in out)
r, out = guard_run(0, stack, d=Stand({GAME: box(labels=game_labels, image="sha256:older-image")}),
                   git=fake_git({"1111111aaaa": COMPOSE_OLD, "origin/main": COMPOSE_OLD}), fetch=lambda url: {"Git": {"Commit": "1111111aaaa"}})
check("guard, the drift: a container behind its tag - the redeploy also moves it to the newer image", "(the redeploy also moves it to the newer image)" in out)
r, out = guard_run(0, {k: v for k, v in stack.items() if not k.startswith("stack.deployed")}, d=d)
check("guard, the drift: without stack.deployed_url it says the commit deployed is not known", r == 0 and "the commit last deployed is not known" in out)


def failing(url):
    raise OSError("the manager did not answer")


r, out = guard_run(1, stack, d=d, fetch=failing)
check("guard: the drift's failure is only information - the image line stays, the decision is made (NO with 1 on)",
      r == 3 and "cs2 image: container" in out and "could not read the drift: OSError" in out)
check("service_changed: another service's change is not the game server's", C.service_changed(COMPOSE_OLD, COMPOSE_BRIDGE, "cs2") == (False, True))
check("service_changed: a version that could not be read counts as changed", C.service_changed(None, COMPOSE_OLD, "cs2") == (True, False))
had = sys.modules.get("yaml", "absent")
sys.modules["yaml"] = None   # as without PyYAML
try:
    check("service_changed without PyYAML: by the whole file", C.service_changed(COMPOSE_OLD, COMPOSE_BRIDGE, "cs2") == (True, False)
          and C.service_changed(COMPOSE_OLD, COMPOSE_OLD, "cs2") == (False, False))
finally:
    if had == "absent":
        del sys.modules["yaml"]
    else:
        sys.modules["yaml"] = had

# ---- the settings text and the secrets

check("jsonc: comments and trailing commas go, a // or a comma inside a string stays",
      C.jsonc('// top\n{ "a": "http://x,}", /* b */ "c": [1, 2,], "d": {"e": 1,}, } // end\n') == {"a": "http://x,}", "c": [1, 2], "d": {"e": 1}})
try:
    C.jsonc('{ "x": "abc" oops }')
    words = ""
except ValueError as e:
    words = str(e)
check("jsonc: a syntax error's words quote nothing of the text", words and "abc" not in words and "oops" not in words)


def ebox(*env):
    return {"Config": {"Env": list(env)}}


S1, S2 = "a" * 32, "b" * 32
places = C.secret_places({"bridge": ebox("LOG_SECRET=" + S1, "RCON_PASSWORD=x"), "game": ebox("CS2_LOG_HTTP_URL=http://bridge:8080/log/" + S1),
                          "sync": ebox("SYNC_BRIDGE_SECRET=" + S1)}, N)
ok, line = C.secrets_agree(places, BRIDGE)
check("secret: the same in the bridge, the game server's log URL and the sync - one secret, three places named, no value printed",
      ok and "3 places" in line and S1 not in line)
places = C.secret_places({"bridge": ebox("LOG_SECRET=" + S2), "game": ebox("CS2_LOG_HTTP_URL=http:\\/\\/bridge:8080\\/log\\/" + S2, "OVERSTRIKE2_BRIDGE_SECRET=" + S2),
                          "sync": ebox("OVERSTRIKE2_BRIDGE_SECRET=" + S1)}, N)
ok, line = C.secrets_agree(places, BRIDGE)
check("secret: a rotation that missed the sync names that place alone, and no value",
      not ok and "sync OVERSTRIKE2_BRIDGE_SECRET" in line and "cs2 CS2_LOG_HTTP_URL" not in line and S1 not in line and S2 not in line)
ok, line = C.secrets_agree(C.secret_places({"bridge": ebox("LOG_SECRET=" + S1), "game": ebox(), "sync": None}, N), BRIDGE)
check("secret: no CS2_LOG_HTTP_URL in the game server is not agreement", not ok and "cs2 CS2_LOG_HTTP_URL" in line)

ok, line = C.mapgroup_ok(ebox("CS2_MAPGROUP=mg_ar_shoots"), name=GAME)
check("mapgroup: mg_ar_shoots, the one-map group - ok", ok and "mg_ar_shoots" in line)
ok, line = C.mapgroup_ok(ebox("CS2_MAPGROUP=mg_armsrace"), name=GAME)
check("mapgroup: mg_armsrace (four maps, CS2's VOTING tab) - look, and it says why", not ok and "'mg_armsrace'" in line and "VOTING" in line)
check("mapgroup: none set (the image's default) - look", not C.mapgroup_ok(ebox())[0])
check("mapgroup: another group the config asks for", C.mapgroup_ok(ebox("CS2_MAPGROUP=mg_custom"), "mg_custom")[0])

TEXT = '{\n  "schema": 1,\n  "server": { "rconPassword": "env:CS2_RCONPW" },\n  "bridge": { "secret": "%s" }\n}\n' % BS
FOLDER = (HOST_DIR + "programs", C.PROGRAM_FOLDER)


def after(text=TEXT, game_env=(), bridge_env=(), sync_env=(), skip=()):
    """The stack on the C# programs: the settings text in each container's environment, but those in skip; the password in the game server's and the bridge's."""
    content = lambda role: [] if role in skip else [C.CONTENT + "=" + text]
    return {"game": box(content("game") + ["CS2_LOG_HTTP_URL=http://bridge:8080/log/" + BS, "CS2_RCONPW=" + RP, *game_env], [FOLDER], labels=game_labels),
            "bridge": box(content("bridge") + ["CS2_RCONPW=" + RP, *bridge_env], labels={C.PROJECT: "os2", C.SERVICE: "bridge"},
                          image="sha256:bridge-image", cimage="localhost/overstrike2-bridge"),
            "sync": box(content("sync") + list(sync_env), [FOLDER], labels={C.PROJECT: "os2", C.SERVICE: "sync"})}


def older():
    """The stack on the older Python programs: no settings text, the secret and the password in their variables."""
    return {"game": box(["CS2_LOG_HTTP_URL=http://bridge:8080/log/" + BS, "CS2_RCONPW=" + RP],
                        [(HOST_DIR + "update-watch.py", "/home/steam/cs2-update-watch.py"), (HOST_DIR + "keepset.py", "/home/steam/keepset.py")], labels=game_labels),
            "bridge": box(["LOG_SECRET=" + BS, "RCON_PASSWORD=" + RP], labels={C.PROJECT: "os2", C.SERVICE: "bridge"}, image="sha256:bridge-image"),
            "sync": box(["SYNC_BRIDGE_SECRET=" + BS], [(HOST_DIR + "workshop-sync.py", "/home/steam/cs2-workshop-sync.py"), (HOST_DIR + "workshop-sync", "/home/steam/sync-state")],
                        labels={C.PROJECT: "os2", C.SERVICE: "sync"})}


def verdict(c, n=N):
    """(what went wrong, all the words) of settings_lines: the LOOK lines, and every line, which must never hold a value."""
    result = C.settings_lines(c, n)
    return [t for ok, t in result if ok is False], " | ".join(t for _ok, t in result)


bad, words = verdict(older())
check("the older Python programs: it says they run, naming what is mounted; the variables agree - nothing to look at",
      not bad and "the older Python programs run: no container holds the settings text" in words and "cs2 mounts keepset.py, update-watch.py" in words
      and "sync mounts workshop-sync.py" in words and "one bridge secret in all 3 places" in words and BS not in words)
c = older()
c["sync"]["Config"]["Env"] = ["SYNC_BRIDGE_SECRET=other"]
bad, words = verdict(c)
check("the older programs: a variable with another secret - look, the place named, no value", len(bad) == 1 and "sync SYNC_BRIDGE_SECRET" in bad[0] and "other" not in words)
c = older()
c["sync"]["Mounts"] = []
bad, words = verdict(c)
check("no settings text and no older programs mounted: it does not claim they run", any("neither way of running" in b and "sync" in b for b in bad) and " run:" not in words)

bad, words = verdict(after())
check("the C# programs: one text in all three, one secret, the password compared, no older program, the program folder - nothing to look at, no value printed",
      not bad and "identical in cs2, bridge, sync" in words and "one bridge secret in all 4 places" in words and "server.rconPassword as cs2 and bridge read it is cs2's CS2_RCONPW" in words
      and "no container mounts one of the older Python programs" in words and "cs2 and sync mount the program folder" in words and BS not in words and RP not in words)
c = after()
c["sync"]["Config"]["Env"][0] = C.CONTENT + "=" + TEXT.replace('"schema": 1', '"schema": 1, "sync": {}')
bad, words = verdict(c)
check("a text that differs in one container - look", any("differs between" in b for b in bad))
bad, words = verdict(after(TEXT.replace(BS, "AnotherValueAnotherValue00")))
check("a rotation that missed CS2_LOG_HTTP_URL - look, naming that place, printing neither value",
      any("cs2 CS2_LOG_HTTP_URL" in b for b in bad) and BS not in words and "AnotherValue" not in words)
bad, words = verdict(after(bridge_env=["LOG_SECRET=stale"]))
check("a variable left behind with another secret - look, naming it", any("bridge LOG_SECRET" in b for b in bad) and "stale" not in words)
bad, words = verdict(after(bridge_env=["LOG_SECRET=" + BS]))
check("a variable left behind with the same secret counts as one more place, and agrees", not bad and "5 places" in words)
c = after()
c["game"]["Config"]["Env"][2] = "CS2_RCONPW=changed"
bad, words = verdict(c)
check("the password the bridge reads is not the game server's CS2_RCONPW - look, naming the bridge, no value",
      any("not cs2's CS2_RCONPW in: bridge" in b for b in bad) and "changed" not in words and RP not in words)
c = after()
c["bridge"]["Config"]["Env"] = [c["bridge"]["Config"]["Env"][0]]
bad, words = verdict(c)
check("a bridge with no CS2_RCONPW reads no password - look", any("not cs2's CS2_RCONPW in: bridge" in b for b in bad))
bad, words = verdict(after(TEXT.replace("env:CS2_RCONPW", RP)))
check("a literal password that is the game server's would do", not bad and RP not in words)
c = after()
c["game"]["Mounts"].append({"Source": HOST_DIR + "update-watch.py", "Destination": "/home/steam/cs2-update-watch.py"})
bad, words = verdict(c)
check("an older program still mounted - look", any("still mounted: cs2: update-watch.py" in b for b in bad))
c = after()
c["sync"]["Mounts"] = []
bad, words = verdict(c)
check("no program folder in the sync - look", any("no program folder" in b and "sync" in b for b in bad))
REF = TEXT.replace(BS, "env:OVERSTRIKE2_BRIDGE_SECRET")
every = ["OVERSTRIKE2_BRIDGE_SECRET=" + BS]
bad, words = verdict(after(REF, game_env=every, bridge_env=every, sync_env=every))
check("references: env: is read in each container itself", not bad and BS not in words)
bad, words = verdict(after(REF, game_env=every, sync_env=every))
check("references: a container without the variable reads it as empty - look, naming that container", any("bridge settings" in b for b in bad))
bad, words = verdict(after(TEXT.replace(BS, "file:/run/secrets/bridge")))
check("references: file: is inside the container, where this does not look - it says so by naming the places with no value",
      any("cs2 settings" in b and "sync settings" in b for b in bad) and BS not in words)
bad, words = verdict(after(skip=("game", "sync")))
check("mixed: only the bridge holds the text - look, naming who has none, and the secret places it took with it",
      any("only bridge hold" in b and "cs2, sync do not" in b for b in bad) and any("cs2 settings" in b for b in bad) and " run:" not in words and BS not in words)
c = after()
c["bridge"]["Config"]["Env"][0] = C.CONTENT + "={ not json"
bad, words = verdict(c)
check("a text that does not parse - look, naming it", any("bridge's settings text does not parse" in b for b in bad))
c = after()
bad, words = verdict({"game": c["game"], "bridge": c["bridge"], "sync": None}, {**N, "sync": ""})
check("a stack without the sync: two places hold the text, and only the game server needs the program folder",
      not bad and "identical in cs2, bridge" in words and "cs2 mount the program folder" in words)


# ---- verify from end to end

SCRATCH = tempfile.mkdtemp(prefix="test-check-")
os.makedirs(os.path.join(SCRATCH, "stack"))
COMPOSE = os.path.join(SCRATCH, "stack", "compose.yml")
open(COMPOSE, "w", encoding="utf-8").write("services:\n  cs2:\n    container_name: cs2\n  bridge:\n    container_name: bridge\n  sync:\n    container_name: sync\n"
                                           "#  old:\n#    container_name: old-one\n")
FULL = {"check.test_label": "os2.test-copy", "check.aside": ["cs2-aside", "bridge-aside"], "stack.repo": SCRATCH, "stack.file": "stack/compose.yml",
        "check.audit": [{"name": "the audit", "argv": ["python", "-I", "audit.py"], "env": {"AUDIT_KEY": "env:OS2_TEST_AUDIT"},
                         "done": r"^audit: (OK|\d+ drift)", "look": r"DRIFT (cs2|bridge|sync):"}]}
AUDIT_OK = types.SimpleNamespace(stdout="audit: OK -- 3 pins\n", stderr="", returncode=0)
AUDIT_ELSEWHERE = types.SimpleNamespace(stdout="", stderr="audit: 2 drift item(s):\n  DRIFT other: live MAC\n  DRIFT cs2-other: live IP\n", returncode=1)


def grouped(c):
    c["game"]["Config"]["Env"].append("CS2_MAPGROUP=" + C.MAPGROUP)
    return c


def run_verify(c, values=None, health="os2-health 1\nlog pushes 3\n", audit=AUDIT_OK, a2s=(0, 1), extra=None, audit_env=True):
    """verify's exit code, every word it printed, the stand-in Docker, and the audit's runs."""
    containers = {GAME: c["game"], BRIDGE: c["bridge"]}
    if c.get("sync") is not None:
        containers[SYNC] = c["sync"]
    containers.update(extra or {})
    stand, runs = Stand(containers, health), []

    def run(argv, env=None):
        runs.append((argv, env))
        return audit
    chk = C.Check(Cfg({**BASE, **(values if values is not None else FULL)}), d=stand, a2s_info=lambda address: a2s, run=run)
    if audit_env:
        os.environ["OS2_TEST_AUDIT"] = "the-audit-value"
    try:
        code, out = out_of(chk.verify)
    finally:
        os.environ.pop("OS2_TEST_AUDIT", None)
    return code, out, stand, runs


code, out, stand, runs = run_verify(grouped(after()))
check("verify, the C# programs: ALL GOOD - the image, the project, production, the map group, VAC, the health, the settings, the roll-call, the audit",
      code == 0 and "RESULT: ALL GOOD" in out and "LOOK" not in out and "bridge belongs to compose project 'os2', service 'bridge'" in out
      and "production: no os2.test-copy label on cs2, bridge, sync" in out and "nothing set aside: no cs2-aside, bridge-aside" in out
      and "roll-call, stack/compose.yml: cs2, bridge, sync declared and running" in out and "old-one" not in out and "ok    the audit: no line matches" in out)
check("verify: the bridge's health with the secret and port of the game server's CS2_LOG_HTTP_URL, printed nowhere",
      stand.execs[0][0] == GAME and stand.execs[0][1][-1] == f"http://bridge:8080/health/{BS}" and BS not in out and RP not in out)
check("verify: the audit runs with this Python and the variable it needs, whose value is printed nowhere",
      runs[0][0][0] == sys.executable and runs[0][0][1:] == ["-I", "audit.py"] and runs[0][1]["AUDIT_KEY"] == "the-audit-value" and "the-audit-value" not in out)
code, out, *_ = run_verify(grouped(older()))
check("verify, the older Python programs: ALL GOOD, a note that they run", code == 0 and "note  the older Python programs run" in out)
code, out, *_ = run_verify(after())
check("verify: another map group is a look", code == 1 and "LOOK  cs2's CS2_MAPGROUP" in out)
code, out, *_ = run_verify(grouped(after(skip=("game", "sync"))))
check("verify: two containers without the text - exit 1, and it says so", code == 1 and "only bridge hold" in out and "RESULT: ALL GOOD" not in out)
c = grouped(after())
c["game"]["Config"]["Labels"]["os2.test-copy"] = "a test copy"
code, out, *_ = run_verify(c, a2s=(0, 0))
check("verify: a test copy - its label and the insecure server are both a look", code == 1 and "a TEST COPY, not production: cs2 (a test copy)" in out
      and "A2S says VAC-secured: NO" in out)
code, out, *_ = run_verify(grouped(after()), extra={"cs2-aside": box()})
check("verify: production set aside for a test - look", code == 1 and "set aside for a test: cs2-aside (running)" in out)
c = grouped(after())
c["bridge"]["Image"] = "sha256:older-bridge"
code, out, *_ = run_verify(c)
check("verify: a bridge not on <image>:latest - look", code == 1 and "LOOK  bridge runs older-bridge" in out)
c = grouped(after())
c["bridge"]["Config"]["Labels"] = {}
code, out, *_ = run_verify(c)
check("verify: a bridge outside the game server's compose project - look", code == 1 and "LOOK  bridge belongs to compose project None" in out)
code, out, *_ = run_verify(grouped(after()), health="os2-health 1\nlog pushes 0\n")
check("verify: no log push reaching the bridge - look", code == 1 and "LOOK  log pushes reaching the bridge: 0" in out)
code, out, *_ = run_verify(grouped(after()), health="Traceback: refused at /health/" + BS)
check("verify: a failed health request - look, and the secret in its words is not printed", code == 1 and "LOOK  bridge /health" in out and BS not in out)
code, out, *_ = run_verify(grouped(after()), a2s=None)
check("verify: no A2S answer - look", code == 1 and "the game server answers A2S: no answer" in out)
c = grouped(after())
c["sync"]["State"] = {"Running": False, "Status": "exited", "StartedAt": "start-1"}
code, out, *_ = run_verify(c)
check("verify: the sync stopped - look, by its state and in the roll-call", code == 1 and "LOOK  sync state exited" in out and "not running: sync (Exited (0))" in out)
code, out, *_ = run_verify(grouped(after()), extra={"strayed": box(labels={C.PROJECT: "os2"})})
check("verify: a container of the project that the compose file does not declare - a note", code == 0 and "note  in compose project 'os2' but declared nowhere in stack/compose.yml: strayed" in out)
code, out, *_ = run_verify(grouped(after()), audit=AUDIT_ELSEWHERE)
check("verify: the audit's drift elsewhere is a note, not a look", code == 0 and "note  the audit: exit 1; 3 other line(s); audit: 2 drift item(s):" in out)
code, out, *_ = run_verify(grouped(after()), audit=types.SimpleNamespace(stdout="", stderr="audit: 1 drift item(s):\n  DRIFT bridge: live MAC x\n", returncode=1))
check("verify: the audit names this stack - look, with its line", code == 1 and "LOOK  the audit: DRIFT bridge: live MAC x" in out)
code, out, *_ = run_verify(grouped(after()), audit=types.SimpleNamespace(stdout="", stderr="Traceback (most recent call last):\nKeyError: 'x'\n", returncode=1))
check("verify: an audit that did not finish is a look, not a quiet ok", code == 1 and "LOOK  the audit: did not finish (exit 1): KeyError: 'x'" in out)
plain = {**FULL, "check.audit": [{"name": "plain", "argv": ["audit"]}]}
code, out, *_ = run_verify(grouped(after()), plain, audit=types.SimpleNamespace(stdout="", stderr="nope\n", returncode=2))
check("verify: an audit without look - its exit code decides", code == 1 and "LOOK  plain: exit 2; nope" in out)
code, out, _stand, runs = run_verify(grouped(after()), audit_env=False)
check("verify: an audit whose variable is not set is not run - look, naming the variable",
      code == 1 and "LOOK  the audit: not run - AUDIT_KEY (OS2_TEST_AUDIT is not set)" in out and not runs)
code, out, *_ = run_verify(grouped(after()), {"stack.repo": SCRATCH, "stack.file": "stack/missing.yml"})
check("verify: a compose file that cannot be read - look", code == 1 and "LOOK  roll-call: stack/missing.yml cannot be read" in out)
code, out, *_ = run_verify(grouped(after()), {"bridge.container": "renamed", "stack.repo": SCRATCH, "stack.file": "stack/compose.yml"})
check("verify: a container the config names that the compose file does not declare - look", code == 1
      and "not declared there, though the private config names it: renamed" in out)
code, out, *_ = run_verify(grouped(after()), {})
check("verify with only the required keys: the optional checks are named as not done, and nothing else is a look",
      code == 0 and "no check.test_label or check.aside" in out and "no stack.repo and stack.file" in out and "no [[check.audit]]" in out)
saved = dict(os.environ)
try:
    os.environ.pop("OS2_TEST_KEY", None)
    chk = C.Check(Cfg({**BASE, "docker.api": "https://docker.example.com:2376", "docker.headers": {"X-API-Key": "env:OS2_TEST_KEY"}}))
    code, out = out_of(chk.verify)
    check("verify without the Docker API key: exit 1, nothing checked, the variable named", code == 1 and "LOOK  docker.headers.X-API-Key reads OS2_TEST_KEY" in out
          and "nothing checked" in out)
finally:
    os.environ.clear()
    os.environ.update(saved)
shutil.rmtree(SCRATCH, ignore_errors=True)

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
