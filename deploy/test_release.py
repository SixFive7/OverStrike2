#!/usr/bin/env python3
"""Offline tests for release.py: both commands against stand-ins - a Docker API whose RCON, exec and container starts are canned,
A2S, the deploy scripts, the lock and the maintenance flag in a scratch folder. Nothing reaches a server, Steam or a share.

    python -I deploy/test_release.py
"""
import contextlib
import io
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import release as R  # noqa: E402

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


HUD = "3823993566"
FILES = {"OverStrike2.dll": "a" * 64, "OverStrike2.pdb": "b" * 64, "blocked.txt": "c" * 64}


class Cfg:
    def __init__(self, values, path="the-private-config.toml"):
        self.values, self.path = values, path

    def get(self, dotted, default=None, required=False):
        return self.values.get(dotted, default)


class FakeDocker:
    """The calls release.py makes: inspect, and exec in the game container - RCON (by bridge.py's INNER_RCON), the files' hashes,
    the plugin's log. A quit restarts the game container; os_hud page writes its probe line to the log."""

    def __init__(self, test):
        self.t, self.rcon_sent, self.started, self.n = test, [], "start-1", 1
        self.loaded = '[#2:LOADED]: "OverStrike2" (4.7.1) by the authors'
        self.hashes, self.probe, self.log = dict(FILES), "page HTTP 500, title 'Steam Workshop' -> NOT shown; Web API ok", []
        self.old_plugin = False   # a plugin from before release 191, which knows os_hud by its old word only
        self.unknown_says = None   # what RCON answers to an unknown command: None the engine's words, "" nothing at all
        self.lists = {"mm_extra_addons": "", "mm_client_extra_addons": HUD}
        self.env = ["CS2_RCONPW=pw-secret", "RCON_PORT=27015"]
        self.bridge_env, self.bridge_image = [], "sha256:image-old-0000"   # a bridge on the settings file: no secret in its environment

    def inspect(self, kind, name):
        if kind == "images":
            return {"Id": "sha256:image-new-0000", "Config": {"Labels": {"org.opencontainers.image.revision": "abc123"}}} if name.endswith(":rel") else None
        if name == "game":
            return {"Id": "c-game", "State": {"Running": True, "StartedAt": self.started}, "Config": {"Env": list(self.env)}}
        if name == "bridge":
            return {"Id": "c-bridge", "Image": self.bridge_image, "State": {"Running": True}, "Config": {"Env": list(self.bridge_env)}}
        return None

    def exec_in(self, container, argv, timeout=60):
        if argv[2] == R.bridge_mod.INNER_RCON:
            pw, command = argv[3], argv[4]
            self.rcon_sent.append((command, pw, os.path.exists(self.t.flag)))
            if command == "status":
                return "  players  : %d humans, 2 bots (16 max)\n" % self.t.rcon_humans
            if command == "quit":
                self.n += 1
                self.started = "start-%d" % self.n
                return ""
            if command == "css_plugins list":
                return self.loaded + "\n[#1:LOADED]: \"Other\" (1.0)\n"
            if command in self.lists:
                return '%s = %s\n' % (command, self.lists[command])
            if command.startswith(("css_gghud page" if self.old_plugin else "os_hud page")):
                self.log.append("OverStrike2 HUD item probe: %s: %s" % (R.NO_SUCH_ITEM, self.probe))
                return ("gghud: " if self.old_plugin else "os_hud: ") + "probing ... the answer goes to the server log"
            if command.startswith(("os_hud " if self.old_plugin else "css_gghud ")):
                return "Unknown command '%s'!" % command.split()[0] if self.unknown_says is None else self.unknown_says
            return ""
        if argv[2] == R.INNER_SHA:
            return json.dumps({f: self.hashes.get(f) for f in argv[4:] if f in self.hashes}) + "\n"
        if argv[2] == R.INNER_GREP:
            return "\n".join(l for l in self.log if argv[4] in l)
        return "(exec start 500)"


class Test:
    def __init__(self, humans=(0, 0, 0, 0), rcon_humans=0, lock_by=None, verify="ok"):
        self.dir = tempfile.mkdtemp(prefix="release-test-", dir=SCRATCH)
        self.flag = os.path.join(self.dir, "maintenance")
        self.lock = os.path.join(self.dir, "server.lock")
        if lock_by:
            open(self.lock, "w").write("owner: %s\nsince: x\n" % lock_by)
        self.humans, self.rcon_humans, self.runs, self.sleeps, self.swaps = list(humans), rcon_humans, [], [], []
        self.verify = {"ok": VERIFY_OK, "bad": VERIFY_BAD, "missing": os.path.join(SCRATCH, "no-such-check.py"), "none": None}[verify]
        self.d = FakeDocker(self)
        self.api = []

    def run(self, argv):
        self.runs.append(argv)
        return 1 if argv[-1] == VERIFY_BAD else 0

    def release(self, dry=False, fetch=None):
        cfg = Cfg({"bridge.game_container": "game", "bridge.container": "bridge", "bridge.image": "localhost/overstrike2-bridge",
                   "server.lock_file": self.lock, "bridge.data_dir": self.dir, "server.address": "192.0.2.1:27015",
                   "release.verify": self.verify})
        return R.Release(cfg, d=self.d, humans=lambda: self.humans.pop(0) if len(self.humans) > 1 else self.humans[0],
                         run=self.run, sleep=self.sleeps.append, fetch=fetch, dry=dry)


def fake_swap(self, tag, keep):
    T.swaps.append((tag, keep))
    T.d.bridge_image = "sha256:image-new-0000"
    return 0


def go(t, fn):
    """fn's exit code (or the Stop's text) and what it printed."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        try:
            r = fn()
        except R.Stop as stop:
            r = "STOP " + str(stop)
    return r, out.getvalue()


SCRATCH = tempfile.mkdtemp(prefix="test-release-")     # removed at the end
VERIFY_OK, VERIFY_BAD = os.path.join(SCRATCH, "check-ok.py"), os.path.join(SCRATCH, "check-bad.py")
for path in (VERIFY_OK, VERIFY_BAD):
    open(path, "w").write("raise SystemExit(0)\n")
R.plugin_files = lambda: dict(FILES)
R.plugin_version = lambda: "4.7.1"
R.bridge_mod.Bridge.swap = fake_swap

try:
    # ---- live, the whole way
    T = Test()
    rel = T.release()
    r, out = go(T, lambda: rel.live("rel", True, False))
    sent = [c for c, _pw, _f in T.d.rcon_sent]
    check("live: exit 0, the bridge swapped to that exact tag, keeping :previous", r == 0 and T.swaps == [("rel", "previous")])
    check("live: the plugin shipped with deploy/plugin.py ship --apply", any(a[-2:] == ["ship", "--apply"] and a[-3].endswith("plugin.py") for a in T.runs))
    check("live: the flag was on at the quit, and is off now", [f for c, _pw, f in T.d.rcon_sent if c == "quit"] == [True] and not os.path.exists(T.flag))
    check("live: 0 humans checked before the release and again right before the quit", sent.count("status") == 2 and sent.index("quit") > sent.index("status"))
    check("live: the lock taken and released", not os.path.exists(T.lock) and "server lock taken" in out and "server lock released" in out)
    check("live: RCON with the game container's own password, never printed", all(pw == "pw-secret" for _c, pw, _f in T.d.rcon_sent) and "pw-secret" not in out)
    check("live: the record's lines - the image, the plugin LOADED, the addon lists, the hashes, the deployment's check",
          "RECORD - live" in out and "sha256:image-new-0000" in out and '"OverStrike2" (4.7.1)' in out and "`mm_client_extra_addons = " + HUD + "`" in out
          and "hashes those of the build" in out and "the deployment's check: ALL GOOD" in out and T.runs[-1][-1] == VERIFY_OK)

    # ---- the HUD mounted by the server (mm_extra_addons); the item is hud/workshop_id.txt, whatever the container's variables say
    check("the HUD item is the repository's hud/workshop_id.txt", R.workshop_item() == HUD)
    T = Test()
    T.d.lists = {"mm_extra_addons": HUD, "mm_client_extra_addons": ""}
    T.d.env = ["CS2_RCONPW=pw-secret", "OS2_HUD_ADDON=1234", "GUNGAME_HUD_ADDON=5678"]
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: the HUD item mounted by the server passes; OS2_HUD_ADDON and GUNGAME_HUD_ADDON mean nothing any more",
          r == 0 and "`mm_extra_addons = " + HUD + "`" in out)
    T = Test()
    T.d.lists = {"mm_extra_addons": "1234", "mm_client_extra_addons": "5678"}
    T.d.env = ["CS2_RCONPW=pw-secret", "OS2_HUD_ADDON=1234", "GUNGAME_HUD_ADDON=5678"]
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: the items the variables name, handed out in place of the one HUD item - stopped",
          str(r).startswith("STOP the server hands out no HUD item " + HUD))
    saved_dir = R.OS2_DIR
    try:
        R.OS2_DIR = os.path.join(SCRATCH, "no-id")
        os.makedirs(os.path.join(R.OS2_DIR, "hud"))
        open(os.path.join(R.OS2_DIR, "hud", "workshop_id.txt"), "w").write("\n")
        T = Test()
        r, out = go(T, lambda: T.release().live("rel", True, False))
        check("live: a workshop_id.txt that names no item - refused before the lock, the swap or the copy",
              str(r).startswith("STOP REFUSED: hud/workshop_id.txt names no item") and not T.swaps and not T.runs and not os.path.exists(T.lock))
    finally:
        R.OS2_DIR = saved_dir

    # ---- the bridge's environment and the RCON password
    T = Test()
    T.d.bridge_env = ["RCON_PASSWORD=from-the-bridge", "LOG_SECRET=from-the-bridge"]
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: the game container's CS2_RCONPW before the bridge's RCON_PASSWORD", r == 0 and all(pw == "pw-secret" for _c, pw, _f in T.d.rcon_sent))
    T = Test()
    T.d.env, T.d.bridge_env = ["RCON_PORT=27015"], ["RCON_PASSWORD=from-the-bridge"]
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: a game container with no CS2_RCONPW - the bridge's RCON_PASSWORD, never printed",
          r == 0 and all(pw == "from-the-bridge" for _c, pw, _f in T.d.rcon_sent) and "from-the-bridge" not in out)
    T = Test()
    T.d.lists = {"mm_extra_addons": "", "mm_client_extra_addons": ""}
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: the configured HUD item in neither list - stopped, the flag off, the lock kept",
          str(r).startswith("STOP the server hands out no HUD item " + HUD) and not os.path.exists(T.flag) and os.path.exists(T.lock))

    # ---- live refuses: somebody on, by A2S or by RCON
    for label, humans, rh in (("A2S shows 1", (1, 0), 0), ("A2S 0 at first, 1 ten seconds later", (0, 1), 0), ("RCON's status shows 1", (0, 0), 1)):
        T = Test(humans=humans, rcon_humans=rh)
        r, out = go(T, lambda: T.release().live("rel", True, False))
        check("live refused - %s: nothing done (no lock, swap, copy, flag or quit)" % label,
              str(r).startswith("STOP REFUSED: somebody is on") and not T.swaps and not T.runs and not os.path.exists(T.lock)
              and not os.path.exists(T.flag) and "quit" not in [c for c, _p, _f in T.d.rcon_sent])

    # ---- the lock: held by somebody else; --under-lock uses it and leaves it
    T = Test(lock_by="somebody else's test")
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live refused - the lock is held: nothing done", str(r).startswith("STOP REFUSED: the server lock is held") and not T.swaps and not T.runs)
    r, out = go(T, lambda: T.release().live("rel", True, True))
    check("live --under-lock: done, and the lock is left as it was", r == 0 and T.swaps and open(T.lock).read().startswith("owner: somebody else's"))
    T = Test()
    r, out = go(T, lambda: T.release().live("rel", True, True))
    check("live --under-lock with a free lock: refused", str(r).startswith("STOP REFUSED: --under-lock, but the server lock is free") and not T.swaps)

    # ---- failures after the quit: the flag off, the lock kept
    for label, spoil, want in (("the plugin not LOADED", lambda d: setattr(d, "loaded", '[#2:FAILED]: "OverStrike2" (4.7.1)'), "is not LOADED"),
                               ("an old plugin version", lambda d: setattr(d, "loaded", '[#2:LOADED]: "OverStrike2" (4.7.0)'), "is not LOADED"),
                               ("a file not the build's", lambda d: d.hashes.update({"blocked.txt": "d" * 64}), "not the build's: blocked.txt"),
                               ("another HUD addon", lambda d: d.lists.update({"mm_client_extra_addons": "1234"}), "hands out no HUD item")):
        T = Test()
        spoil(T.d)
        rel = T.release()
        r, out = go(T, lambda: rel.live("rel", True, False))
        check("live, %s: stopped (%s), the flag off, the lock kept" % (label, str(r)[5:70]),
              str(r).startswith("STOP ") and want in str(r) and not os.path.exists(T.flag) and os.path.exists(T.lock) and rel.took_lock)

    # ---- somebody joins between the ship and the quit: no quit
    T = Test(humans=(0, 0, 0, 1))
    rel = T.release()
    r, out = go(T, lambda: rel.live("rel", True, False))
    check("live: a join right before the quit - no quit, the flag off, the lock kept",
          str(r).startswith("STOP REFUSED: somebody is on") and "quit" not in [c for c, _p, _f in T.d.rcon_sent]
          and not os.path.exists(T.flag) and os.path.exists(T.lock))

    # ---- the deployment's own check
    T = Test(verify="bad")
    rel = T.release()
    r, out = go(T, lambda: rel.live("rel", True, False))
    check("live: the deployment's check says look - exit 1, the lock kept", r == 1 and "the deployment's check: LOOK" in out and os.path.exists(T.lock))
    T = Test(verify="missing")
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: a check named in the config but missing - exit 1 (NOT RUN), the lock kept", r == 1 and "the deployment's check: NOT RUN" in out
          and os.path.exists(T.lock))
    T = Test(verify="none")
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: no release.verify - deploy/check.py verify runs, on the config this release runs with; ALL GOOD, the lock released",
          r == 0 and T.runs[-1] == [sys.executable, "-I", os.path.join(HERE, "check.py"), "verify", "--config", os.path.abspath("the-private-config.toml")]
          and os.path.isfile(T.runs[-1][2]) and "the deployment's check: ALL GOOD" in out and not os.path.exists(T.lock))
    env = R.child_env(Cfg({}, path="the-private-config.toml"))
    check("the deploy scripts a release runs read the release's own config: OVERSTRIKE2_CONFIG, made absolute, the rest of the environment kept",
          env["OVERSTRIKE2_CONFIG"] == os.path.abspath("the-private-config.toml") and env.get("PATH") == os.environ.get("PATH")
          and R.child_env(Cfg({}, path=None)) is None)
    T = Test(verify="none")
    T.run = lambda argv: (T.runs.append(argv), 1 if argv[2] == os.path.join(HERE, "check.py") else 0)[1]
    r, out = go(T, lambda: T.release().live("rel", True, False))
    check("live: deploy/check.py verify says look - exit 1, the lock kept", r == 1 and "the deployment's check: LOOK" in out and os.path.exists(T.lock))

    # ---- --no-plugin: the swap alone
    T = Test()
    r, out = go(T, lambda: T.release().live("rel", False, False))
    check("live --no-plugin: the swap and the check only - no copy, no flag, no quit",
          r == 0 and T.swaps == [("rel", "previous")] and [a for a in T.runs if "plugin.py" in " ".join(a)] == []
          and "quit" not in [c for c, _p, _f in T.d.rcon_sent] and "maintenance flag" not in out and "the deployment's check: ALL GOOD" in out)

    # ---- --dry-run: reads only
    T = Test()
    r, out = go(T, lambda: T.release(dry=True).live("rel", True, False))
    check("live --dry-run: the checks, and nothing written", r == 0 and not T.swaps and not T.runs and not os.path.exists(T.lock)
          and not os.path.exists(T.flag) and [c for c, _p, _f in T.d.rcon_sent] == ["status"] and "dry run: would swap" in out)
    T = Test()
    r, out = go(T, lambda: T.release().live("nosuch", True, False))
    check("live: an image tag the server does not have is refused first", str(r).startswith("STOP REFUSED: no image") and not T.d.rcon_sent)

    # ---- publish
    run_dir = os.path.join(SCRATCH, "release-test-run")
    os.makedirs(os.path.join(run_dir, "shots"), exist_ok=True)

    def fetches(times):
        seq = list(times)

        def fetch(item):
            T.api.append((item, len(T.sleeps)))
            t = seq.pop(0) if len(seq) > 1 else seq[0]
            return {"result": 1, "visibility": 0, "file_size": 1, "time_updated": t}
        return fetch

    T = Test()
    r, out = go(T, lambda: T.release(fetch=fetches([100, 100, 200, 200, 200, 300])).publish("v9: what changed", run_dir, False, 120))
    check("publish: the held-item check read NOT shown, then the upload with the note and the update's flag - asked as os_hud alone, which answered",
          r == 0 and any(a[-4:] == ["hud-publish", "--note", "v9: what changed", "--user-approved-update"] for a in T.runs)
          and [c for c, _p, _f in T.d.rcon_sent if " page " in c] == ["os_hud page " + R.NO_SUCH_ITEM])
    check("publish: time_updated polled one call at a time, a minute apart, until it moved twice",
          len(T.api) == 6 and T.sleeps.count(60) == 5 and "the upload: time_updated" in out and "Valve's approval, most likely" in out)
    check("publish: the record's line, and the lock released", "RECORD - publish" in out and "release-test-run" in out and not os.path.exists(T.lock))
    T = Test()
    T.d.old_plugin = True
    r, out = go(T, lambda: T.release(fetch=fetches([100, 100, 200, 200, 200, 300])).publish("v9: what changed", run_dir, False, 120))
    sent = [c for c, _p, _f in T.d.rcon_sent]
    check("publish: a live plugin from before release 191 answers os_hud as unknown - the held-item check asks it by its old word, and reads NOT shown",
          r == 0 and "os_hud page " + R.NO_SUCH_ITEM in sent and "css_gghud page " + R.NO_SUCH_ITEM in sent and sent.index("os_hud page " + R.NO_SUCH_ITEM) < sent.index("css_gghud page " + R.NO_SUCH_ITEM)
          and "held-item check: css_gghud page" in out)
    T = Test()
    T.d.old_plugin, T.d.unknown_says = True, ""
    r, out = go(T, lambda: T.release(fetch=fetches([100, 100, 200, 200, 200, 300])).publish("v9: what changed", run_dir, False, 120))
    sent = [c for c, _p, _f in T.d.rcon_sent if " page " in c]
    check("publish: a plugin from before release 191 behind an RCON that answers an unknown command with nothing - no os_hud: answer, so the "
          "held-item check asks css_gghud too, and reads NOT shown in the log",
          r == 0 and sent == ["os_hud page " + R.NO_SUCH_ITEM, "css_gghud page " + R.NO_SUCH_ITEM] and "(no answer) - not the plugin's os_hud: answer" in out
          and "held-item check: css_gghud page " + R.NO_SUCH_ITEM + " -> gghud: probing" in out and "-> NOT shown" in out)
    T = Test()
    T.d.probe = "page HTTP 200, title 'Steam Workshop :: x' -> shown to everybody"
    r, out = go(T, lambda: T.release(fetch=fetches([100])).publish("note", run_dir, False, 120))
    check("publish refused - the held-item check does not read NOT shown: no upload, the lock released",
          str(r).startswith("STOP REFUSED: the held-item check") and not T.runs and not os.path.exists(T.lock))
    T = Test()
    r, out = go(T, lambda: T.release(fetch=fetches([100])).publish("note", os.path.join(SCRATCH, "no-such-run"), False, 120))
    check("publish refused - no client test run with shots", str(r).startswith("STOP REFUSED: --client-test") and not T.runs and not T.d.rcon_sent)
    T = Test()
    r, out = go(T, lambda: T.release(fetch=fetches([100, 100, 200])).publish("note", run_dir, False, 3))
    check("publish: no approval within --wait-minutes - says so, the lock released", str(r).startswith("STOP time_updated moved 1 time(s)")
          and not os.path.exists(T.lock))
    T = Test()
    r, out = go(T, lambda: T.release(dry=True, fetch=fetches([100])).publish("note", run_dir, False, 120))
    check("publish --dry-run: the held-item check, no upload, no lock", r == 0 and not T.runs and not os.path.exists(T.lock) and "dry run: would publish" in out)
finally:
    shutil.rmtree(SCRATCH, ignore_errors=True)

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
