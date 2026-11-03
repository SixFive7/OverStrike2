"""Unit and scenario tests for update-watch.py beside this file: the version trap, Valve's replies, the console lines,
the daily window, both freeze paths (error-map signature; A2S + RCON silence), the loop protection and the planned
restarts - the scenarios drive Watch.loop_once() itself on a fake clock with a stubbed engine.

Run them under Linux with the image's Python (os.open on a directory, which save_state() fsyncs, fails on Windows):
    docker run --rm -v "$PWD:/repo:ro" -w /repo/server python:3.9-slim python test_update_watch.py
Exit status 0 when every check passes. Not deployed: only update-watch.py is copied to the server.
"""
import json, os, shutil, sys, tempfile, types
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "update-watch.py")
ROOT = tempfile.mkdtemp(prefix="uw-")
os.makedirs(os.path.join(ROOT, "game", "csgo"))
os.environ.update({"STEAMAPPDIR": ROOT, "CS2_RCONPW": "secret", "TZ": "Europe/Paris"})
sys.modules.setdefault("fcntl", types.ModuleType("fcntl"))
m = types.ModuleType("uw")
m.__dict__["__name__"] = "uw"
exec(compile(open(SRC, encoding="utf-8").read(), "update-watch", "exec"), m.__dict__)

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


# The real steam.inf of the running server (captured 2026-10-03), ServerVersion FIRST on purpose.
STEAM_INF = """ClientVersion=2000924
ServerVersion=2000924
PatchVersion=1.41.8.8
ProductName=cs2
appID=730
ServerAppID=2347773
SourceRevision=11076591
VersionDate=Oct 02 2026
VersionTime=14:45:21
"""

# ---- the version trap ------------------------------------------------------------------------------------
check("trap: PatchVersion is the field read", m.patch_version(STEAM_INF) == "1.41.8.8")
check("trap: dots removed -> 14188", m.version_int(m.patch_version(STEAM_INF)) == 14188)
check("trap: the Valve URL carries 14188, never ServerVersion 2000924",
      "version=14188" in m.VALVE_URL % m.version_int(m.patch_version(STEAM_INF))
      and "2000924" not in m.VALVE_URL % m.version_int(m.patch_version(STEAM_INF)))
check("trap: ServerVersion itself is rejected (no dots)", m.version_int("2000924") is None)
check("trap: a dotless 14188 is rejected too", m.version_int("14188") is None)
check("trap: steam.inf without PatchVersion -> None", m.patch_version("ServerVersion=2000924\n") is None)
check("trap: 'PatchVersionX=' is not PatchVersion", m.patch_version("PatchVersionX=9.9\n") is None)
open(os.path.join(ROOT, "game", "csgo", "steam.inf"), "w").write(STEAM_INF)
check("trap: read_patch_version() reads the volume's steam.inf", m.read_patch_version() == "1.41.8.8")

# ---- Valve replies (shapes as measured) --------------------------------------------------------
STALE = '{"response":{"success":true,"up_to_date":false,"version_is_listable":false,"required_version":14188,"message":"Your server is out of date, please upgrade"}}'
CURRENT = '{"response":{"success":true,"up_to_date":true,"version_is_listable":true}}'
check("valve: 14186 -> stale, required 14188", m.valve_verdict(STALE, 14186) == ("stale", 14188))
check("valve: current", m.valve_verdict(CURRENT, 14188) == ("current", None))
check("valve: required not newer -> unknown", m.valve_verdict(STALE, 14188)[0] == "unknown")
check("valve: success false -> unknown", m.valve_verdict('{"response":{"success":false}}', 1)[0] == "unknown")
check("valve: not JSON -> unknown", m.valve_verdict("<html>502</html>", 1)[0] == "unknown")
check("valve: required as string -> unknown", m.valve_verdict(
    '{"response":{"success":true,"up_to_date":false,"required_version":"14188"}}', 14186)[0] == "unknown")
check("valve: up_to_date as string -> unknown", m.valve_verdict(
    '{"response":{"success":true,"up_to_date":"false","required_version":14188}}', 14186)[0] == "unknown")
check("valve: required true (bool) -> unknown", m.valve_verdict(
    '{"response":{"success":true,"up_to_date":false,"required_version":true}}', 14186)[0] == "unknown")

# ---- console lines (as the container log has them) -----------------------------------------
SIG = ["SV:  Level loading started for 'error'", 'LoopInit:  in "error" map - disconnecting...',
       "../../game/shared/loopmodegame.cpp (652) : Assertion Failed in function LoopInit():"]
for line in SIG:
    check("freeze signature: %s" % line[:40], m.is_freeze_signature(line))
check("freeze: a normal level load is not the signature", not m.is_freeze_signature("SV:  Level loading started for 'ar_shoots'"))
check("freeze: chat cannot fake it", not m.is_freeze_signature(
    'L 01/03/2001 - 12:00:01: "Evil<3><[U:1:999]><CT>" say "SV:  Level loading started for \'error\'"'))
check("freeze: chat cannot fake the LoopInit line", not m.is_freeze_signature(
    'L 01/03/2001 - 12:00:01: "Evil<3><[U:1:999]><CT>" say "LoopInit:  in "error" map"'))
check("level: ar_shoots", m.level_started("SV:  Level loading started for 'ar_shoots'") == "ar_shoots")
check("level: error", m.level_started("SV:  Level loading started for 'error'") == "error")
OVER = ["L 01/05/2001 - 12:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min",
        "01/05/2001 - 12:00:00.123 - Game Over: gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min"]
for line in OVER:
    check("game over: %s" % line[:30], m.is_game_over(line))
check("game over: chat cannot fake it", not m.is_game_over(
    'L 01/05/2001 - 12:00:00: "Remy<2><[U:1:1]><CT>" say "Game Over: nice try"'))
check("game over: chat with a fake prefix inside cannot fake it", not m.is_game_over(
    'L 01/05/2001 - 12:00:00: "Remy<2><[U:1:1]><CT>" say "L 01/05/2001 - 12:00:00: Game Over: x"'))

# ---- daily window ----------------------------------------------------------------------------------------
CEST = timezone(timedelta(hours=2))
def at(s):
    return datetime.strptime(s, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CEST).timestamp()
check("daily: 07:00:00 due", m.daily_due(at("2026-10-04 07:00:00"), (7, 0), CEST, "2026-10-03"))
check("daily: 08:59 still due", m.daily_due(at("2026-10-04 08:59:00"), (7, 0), CEST, "2026-10-03"))
check("daily: 09:00 window closed", not m.daily_due(at("2026-10-04 09:00:00"), (7, 0), CEST, "2026-10-03"))
check("daily: 06:59 not yet", not m.daily_due(at("2026-10-04 06:59:59"), (7, 0), CEST, "2026-10-03"))
check("daily: done today", not m.daily_due(at("2026-10-04 07:30:00"), (7, 0), CEST, "2026-10-04"))
check("daily: off", not m.daily_due(at("2026-10-04 07:30:00"), None, CEST, ""))
try:
    from zoneinfo import ZoneInfo
    ZONE = ZoneInfo("Europe/Paris")
    t = datetime(2026, 10, 25, 7, 30, tzinfo=ZONE).timestamp()     # the morning DST ends
    check("daily: DST-end morning 07:30 CET is due", m.daily_due(t, (7, 0), ZONE, "2026-10-24"))
except Exception:
    print("SKIP DST test (no zoneinfo data here)")

# ---- freeze decision -------------------------------------------------------------------------------------
check("freeze: all conditions -> kill", m.freeze_confirmed(1090, 1000, 999, True, False, 90))
check("freeze: grace not over", not m.freeze_confirmed(1089, 1000, 999, True, False, 90))
check("freeze: A2S answering", not m.freeze_confirmed(1200, 1000, None, True, False, 90))
check("freeze: engine already gone", not m.freeze_confirmed(1200, 1000, 999, False, False, 90))
check("freeze: another map started loading", not m.freeze_confirmed(1200, 1000, 999, True, True, 90))
check("freeze: silence began long after the signature", not m.freeze_confirmed(1500, 1000, 1100, True, False, 90))
check("freeze (path a): no signature -> no path-a kill", not m.freeze_confirmed(99999, None, 0, True, False, 90))
check("freeze: the default grace is 10 s", m.FREEZE_GRACE == 10)
check("freeze (path a) at 10 s: 10 s of silence after the signature -> kill", m.freeze_confirmed(1010, 1000, 1000, True, False, 10))
check("freeze (path a) at 10 s: 9 s -> no kill", not m.freeze_confirmed(1009, 1000, 1000, True, False, 10))
check("polling: A2S every 2 s with a 1 s timeout, so the 10 s grace spans several polls",
      m.A2S_EVERY == 2 and m.A2S_TIMEOUT == 1 and m.SILENT_POLLS >= 3 and m.A2S_EVERY * m.SILENT_POLLS < m.FREEZE_GRACE)

# ---- silence alone (path b) ------------------------------------------------------------------------------
S = m.silence_confirmed
check("silence: armed, A2S and RCON dead 10 s, 5 polls each -> kill", S(1010, 1000, 5, 1000, 5, True, True, False, 10))
check("silence: 9 s -> no kill", not S(1009, 1000, 5, 1000, 5, True, True, False, 10))
check("silence: RCON silent only 9 s -> no kill", not S(1010, 1000, 5, 1001.5, 5, True, True, False, 10))
check("silence: RCON answering -> no kill, however long A2S is silent", not S(99999, 1000, 999, None, 0, True, True, False, 10))
check("silence: two failed polls are not a streak, whatever the clock says", not S(1100, 1000, 2, 1000, 2, True, True, False, 10))
check("silence: not armed (never answered since it started) -> no kill", not S(1100, 1000, 9, 1000, 9, False, True, False, 10))
check("silence: no engine process -> no kill", not S(1100, 1000, 9, 1000, 9, True, False, False, 10))
check("silence: the watcher itself is taking it down -> no kill", not S(1100, 1000, 9, 1000, 9, True, True, True, 10))
QUITS = ['01/01/2001 - 12:05:34.547 - rcon from "192.0.2.10:64265": command "quit"',
         'L 01/03/2001 - 12:01:02: rcon from "192.0.2.101:40112": command "quit"',
         'L 01/03/2001 - 12:01:02: rcon from "127.0.0.1:5555": command "_restart"']
for line in QUITS:
    check("quit echo: %s" % line[-40:], m.is_quit_echo(line))
check("quit echo: other rcon commands are not one", not m.is_quit_echo('L 01/03/2001 - 12:01:02: rcon from "127.0.0.1:5555": command "say quit"'))
check("quit echo: chat cannot fake it", not m.is_quit_echo(
    'L 01/03/2001 - 12:01:02: "Evil<3><[U:1:999]><CT>" say "rcon from "x": command "quit""'))

# ---- gates -----------------------------------------------------------------------------------------------
s = m.default_state()
now = 10 ** 6
s["restarts"] = [[now - 700 - i * 1000, "daily"] for i in range(4)]
check("gate: 4 planned in 24 h -> blocked", m.planned_block(s, now)[0] is True)
s["restarts"] = [[now - 300, "update"]]
check("gate: planned min gap -> deferred", m.planned_block(s, now)[0] is False)
s["restarts"] = [[now - 30, "freeze"]] * 1
check("gate: a freeze kill does not count as planned", m.planned_block(s, now) is None)
check("gate: freeze min gap", m.freeze_block(s, now)[0] is False)
s["restarts"] = [[now - 200 - i * 300, "freeze"] for i in range(10)]
check("gate: 10 freeze kills in 24 h -> blocked", m.freeze_block(s, now)[0] is True)
s = m.default_state(); s["freeze_tripped"] = 5
check("gate: freeze breaker latched", m.freeze_block(s, now)[0] is True)
s = m.default_state(); s["restarts"] = [[now + 100, "daily"]]
check("gate: clock moved backwards", m.planned_block(s, now)[0] is True)

# ---- state -----------------------------------------------------------------------------------------------
check("state: defaults validate", m.validate_state(json.loads(json.dumps(m.default_state())))[1] is None)
check("state: unknown restart kind is a fault", m.validate_state({"restarts": [[1, "other"]]})[1] is not None)
check("state: bool counter is a fault", m.validate_state({"update_misses": True})[1] is not None)
check("state: old schema 1 file is a fault, not a reset", m.validate_state({"schema": 1})[1] is not None)
check("state: pending round-trips", m.validate_state({"pending": {"kind": "update", "at": 5, "from": "1.41.8.8",
                                                                  "required": 14189}})[0]["pending"]["required"] == 14189)

# ---- texts -----------------------------------------------------------------------------------------------
check("back_in: default 30 before measurements", m.back_in([]) == 30)
check("back_in: measured 14 s -> 20", m.back_in([14]) == 20)
check("chat_safe: quotes and semicolons", m.chat_safe('a "b"; quit') == "a 'b', quit")
for kind in ("daily", "update"):
    w, f = m.texts(kind, 30)
    check("texts %s fit 120 chars (%d/%d)" % (kind, len(w), len(f)), len(w) <= 120 and len(f) <= 120)
print("   daily:", m.texts("daily", 30))
print("   update:", m.texts("update", 30))


# ---- scenarios: the real Watch loop with a fake clock and a stubbed engine --------------------------------
class Clock:
    def __init__(self, t):
        self.t = t
    def time(self):
        return self.t
    def sleep(self, s):
        self.t += s
    def strftime(self, fmt, *a):
        return "T+%d" % self.t
    def localtime(self, *a):
        import time as real
        return real.localtime(*a)


class Engine:
    def __init__(self):
        self.pid, self.a2s, self.humans, self.chat, self.cmds, self.killed = 4242, True, 0, [], [], []
        self.rcon_up, self.alive_probes = True, 0
        self.hud, self.notices = False, []  # hud: GunGameWelcome answers gg_notice (a published HUD is on)
        self.console = os.path.join(ROOT, "game", "csgo", "console.log")
        open(self.console, "w").close()
    def write(self, line):
        with open(self.console, "a") as h:
            h.write(line + "\n")
    def probe(self):
        return {"version": "1.41.8.8", "humans": self.humans, "bots": 0, "map": "ar_shoots"} if self.a2s else None
    def rcon(self, commands, expect_reply=True):
        out = []
        for c in commands:
            self.cmds.append(c)
            if c.startswith("echo "):
                self.write('L 10/04/2026 - 07:00:00: rcon from "127.0.0.1:5555": command "%s"' % c)
            if c.startswith("say "):
                self.chat.append(c[5:-1])
            if c.startswith("gg_notice ") and self.hud:
                seconds, text = c[len("gg_notice "):].split(" ", 1)
                self.notices.append((int(seconds), text.strip('"')))
                out.append("gg_notice: shown for %s s: %s" % (seconds, text.strip('"')))
                continue
            if c == "quit":
                self.pid = None
            out.append("players  : %d humans, 0 bots (10 max) (not hibernating)" % self.humans if c == "status" else "")
        return out
    def kill(self, pid, why):
        self.killed.append(why)
        self.pid = None
    def alive(self):
        self.alive_probes += 1
        return self.rcon_up and self.pid is not None
    def freeze(self):
        """A frozen engine: the process is there, nothing answers."""
        self.a2s, self.rcon_up = False, False
    def thaw(self):
        self.a2s, self.rcon_up = True, True


def scenario(start, verdict=("current", None)):
    import shutil
    shutil.rmtree(os.path.join(ROOT, "update-watch"), ignore_errors=True)
    clock, eng = Clock(start), Engine()
    m.time = clock
    m.a2s_probe, m.rcon, m.kill_engine, m.rcon_alive = eng.probe, eng.rcon, eng.kill, eng.alive
    m.engine_pid = lambda: eng.pid
    m.valve_check = lambda vint: verdict
    m.LAST_SAID.clear()
    w = m.Watch()
    return w, clock, eng


def step(w, clock, seconds, each=None):
    """Run Watch.run()'s own loop body, Watch.loop_once() - one tick, paced - for `seconds` of fake time."""
    end = clock.t + seconds
    while clock.t < end:
        if each:
            each(clock.t)
        w.loop_once()


# A. path a: signature, A2S silent -> kill after the 10 s grace, not before - RCON may even still answer
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, 20)
check("A: console.log proven live by the echo probe", w.console is not None)
eng.a2s = False
eng.write("SV:  Level loading started for 'error'")
eng.write('LoopInit:  in "error" map - disconnecting...')
step(w, clock, 8)
check("A: no kill inside the grace (8 s)", not eng.killed)
step(w, clock, 6)
check("A: killed after the 10 s grace (%s)" % (eng.killed[:1],), len(eng.killed) == 1 and "error-map" in eng.killed[0])
check("A: the kill is persisted as pending freeze", w.state["pending"]["kind"] == "freeze")


# B. path b: silence alone
def frozen_after(seconds, setup=None, warm=20):
    """A scenario that answers for `warm` s, then freezes (A2S and RCON dead, process up), for `seconds` more."""
    w, clock, eng = scenario(at("2026-10-04 15:00:00"))
    step(w, clock, warm)
    if setup:
        setup(w, eng)
    eng.freeze()
    step(w, clock, seconds)
    return w, clock, eng


w, clock, eng = frozen_after(8)
check("B1: A2S and RCON silent for 8 s -> no kill yet", not eng.killed)
step(w, clock, 6)
check("B1: ... killed once both have been silent 10 s on several polls (%s)" % (eng.killed[:1],),
      len(eng.killed) == 1 and "silent freeze" in eng.killed[0] and w.state["pending"]["kind"] == "freeze")
check("B1: one kill, counted as a freeze, the engine marked going down",
      [e[1] for e in w.state["restarts"]] == ["freeze"] and w.going_down is not None)

w, clock, eng = frozen_after(0)
eng.rcon_up = True
step(w, clock, 3600)
check("B2: an hour of A2S silence while RCON still answers -> no kill", not eng.killed)

w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, 20)
for _ in range(30):                      # a lost packet now and then: one poll fails, the next answers
    eng.freeze(); step(w, clock, 2); eng.thaw(); step(w, clock, 4)
check("B3: single lost polls, over and over -> no kill", not eng.killed)

w, clock, eng = scenario(at("2026-10-04 15:00:00"))
eng.freeze()
step(w, clock, 600)
check("B4: an engine that has not answered once since the watcher started (boot) is never killed for silence",
      not eng.killed and w.armed_pid is None)

w, clock, eng = frozen_after(300, setup=lambda w, eng: setattr(eng, "pid", 4343))
check("B5: a new engine process (relaunch) is not armed until it answers -> no kill", not eng.killed)
eng.thaw(); step(w, clock, 4)
check("B5: ... it answers, so it is armed", w.armed_pid == 4343)
eng.freeze(); step(w, clock, 14)
check("B5: ... and a freeze after that is killed", len(eng.killed) == 1)

w, clock, eng = frozen_after(120, setup=lambda w, eng: eng.write(
    'L 10/04/2026 - 15:00:20: rcon from "192.0.2.101:40112": command "quit"'))
check("B6: silence after someone's RCON quit (the bridge's !restart) -> no kill", not eng.killed and w.armed_pid is None)

w, clock, eng = scenario(at("2026-10-04 06:59:50"))
step(w, clock, 5)
orig = eng.rcon
def stubborn(c, expect_reply=True):      # the quit is delivered, and the engine goes silent but does not exit
    out = orig(c, expect_reply)
    if c == ["quit"]:
        eng.pid = 4242
        eng.freeze()
    return out
m.rcon = stubborn
step(w, clock, 120)
check("B7: during the watcher's own restart the silence path stays out: one kill, the planned one (%s)" % eng.killed,
      len(eng.killed) == 1 and "after `quit`" in eng.killed[0]
      and [e[1] for e in w.state["restarts"]] == ["daily"])

w, clock, eng = frozen_after(14)
check("B8: first silent freeze killed", len(eng.killed) == 1)
st = json.loads(json.dumps(w.state))
w, clock, eng = scenario(clock.t + 30)   # the container came back 30 s later
json.dump(st, open(m.STATE_FILE, "w"))
w.state, w.fault = m.load_state()
step(w, clock, 10)
eng.freeze()
step(w, clock, 30)
check("B8: a second freeze inside the 3 min floor waits (loop protection)", not eng.killed)
step(w, clock, 180)
check("B8: ... and is killed once the floor has passed", len(eng.killed) == 1)
check("B8: the previous kill was judged recovered on this start", w.state["freeze_misses"] == 0)
w, clock, eng = frozen_after(60, setup=lambda w, eng: w.state.update(freeze_tripped=1))
check("B8: a latched freeze breaker blocks silence kills too", not eng.killed)
w, clock, eng = frozen_after(60, setup=lambda w, eng: w.state.update(
    restarts=[[int(clock.t) - 4000 - i * 600, "freeze"] for i in range(10)]))
check("B8: the daily cap of 10 freeze kills blocks silence kills too", not eng.killed)

w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, 20)
for _ in range(20):                      # a slow map load: both silent 6 s, then it answers again
    eng.freeze(); step(w, clock, 6); eng.thaw(); step(w, clock, 30)
check("B9: loads silent for 6 s (3x the longest measured) never trip it", not eng.killed)

w, clock, eng = frozen_after(0)
check("B10: RCON is only probed while A2S fails", eng.alive_probes <= 1)
eng.thaw(); step(w, clock, 600)
check("B10: ... no RCON probes at all while A2S answers", eng.alive_probes <= 1)

# B11. the same freeze with the probes taking their real time on a dead engine (A2S 1 s, RCON 1.5 s timeouts)
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, 20)
fast_probe, fast_alive, kill_at = eng.probe, eng.alive, []
def slow_probe():
    answer = fast_probe()
    if answer is None:
        clock.sleep(m.A2S_TIMEOUT)
    return answer
def slow_alive():
    answer = fast_alive()
    if not answer:
        clock.sleep(m.RCON_PROBE_TIMEOUT)
    return answer
def timed_kill(pid, why):
    kill_at.append(clock.t)
    eng.kill(pid, why)
m.a2s_probe, m.rcon_alive, m.kill_engine = slow_probe, slow_alive, timed_kill
frozen_at = clock.t
eng.freeze()
step(w, clock, 60)
check("B11: with real probe timeouts the kill lands 10-16 s after the freeze (%s s, %s)"
      % (["%.1f" % (t - frozen_at) for t in kill_at], eng.killed[:1]),
      len(kill_at) == 1 and 10 <= kill_at[0] - frozen_at <= 16)

# C. a map that starts loading inside the grace cancels the freeze (path a); RCON answering keeps path b out too
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, 20)
eng.write("SV:  Level loading started for 'error'")
eng.a2s = False
step(w, clock, 6)
eng.write("SV:  Level loading started for 'ar_baggage'")
step(w, clock, 200)
check("C: recovered by itself -> no kill", not eng.killed)

# D. daily restart, empty server: quits at 07:00, once
w, clock, eng = scenario(at("2026-10-04 06:59:30"))
step(w, clock, 40)
check("D: quit at 07:00 when empty", "quit" in eng.cmds and not eng.chat)
check("D: done_day recorded", w.state["done_day"] == "2026-10-04")
eng.pid = 4242
n = eng.cmds.count("quit")
step(w, clock, 1200)
check("D: no second daily restart that day", eng.cmds.count("quit") == n)

# E. daily restart with players: warns, waits for Game Over, says the line, quits 3 s later
w, clock, eng = scenario(at("2026-10-04 06:59:00"))
eng.humans = 3
step(w, clock, 300)
check("E: warned once, no quit mid-match", len(eng.chat) == 1 and "end of this match" in eng.chat[0] and "quit" not in eng.cmds)
eng.write("L 10/04/2026 - 07:05:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min")
step(w, clock, 10)
check("E: restart line then quit after Game Over", "quit" in eng.cmds and "now" in eng.chat[-1])
check("E: no HUD answers gg_notice, so both lines went to chat after asking it", sum(c.startswith("gg_notice ") for c in eng.cmds) == 2)

# E2. the same with the HUD on: both lines on its news line (the warning for 10 minutes, the last line for 30 s), none in chat
w, clock, eng = scenario(at("2026-10-04 06:59:00"))
eng.humans, eng.hud = 3, True
step(w, clock, 300)
check("E2: the warning is on the HUD for 600 s, not in chat", eng.notices[:1] and eng.notices[0][0] == 600
      and "end of this match" in eng.notices[0][1] and not eng.chat and "quit" not in eng.cmds)
eng.write("L 10/04/2026 - 07:05:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min")
step(w, clock, 10)
check("E2: the restart line on the HUD for 30 s, then quit - chat stays empty", "quit" in eng.cmds and len(eng.notices) == 2
      and eng.notices[1][0] == 30 and "now" in eng.notices[1][1] and not eng.chat)

# F. A2S says empty, RCON says 1 human -> no restart on a disagreement
w, clock, eng = scenario(at("2026-10-04 06:59:50"))
eng.probe = lambda: {"version": "1.41.8.8", "humans": 0, "bots": 0, "map": "ar_shoots"}
m.a2s_probe = eng.probe
eng.humans = 1
step(w, clock, 60)
check("F: disagreement -> no quit", "quit" not in eng.cmds)

# G. stale build, empty server -> update restart; three misses latch the breaker, a newer build re-arms it
w, clock, eng = scenario(at("2026-10-04 15:00:00"), verdict=("stale", 14189))
step(w, clock, 30)
check("G: stale + empty -> quit", "quit" in eng.cmds and w.state["pending"]["kind"] == "update")
quits, history = 1, []
for i in range(8):                       # each restart is judged on the NEXT start, so keep going until it latches
    st = dict(w.state)
    w, clock, eng = scenario(clock.t + 3 * 3600, verdict=("stale", 14189))
    json.dump(st, open(m.STATE_FILE, "w"))
    w.state, w.fault = m.load_state()
    step(w, clock, 60)
    quits += eng.cmds.count("quit")
    history.append((w.state["update_misses"], bool(w.state["update_tripped"]), eng.cmds.count("quit")))
    if w.state["update_tripped"]:
        break
check("G: latched after the 3rd unchanged build, 3 update restarts in total (%s)" % history,
      w.state["update_tripped"] and w.state["update_misses"] == 3 and quits == 3)
st = dict(w.state)
w, clock, eng = scenario(clock.t + 12 * 3600, verdict=("stale", 14189))
json.dump(st, open(m.STATE_FILE, "w"))
w.state, w.fault = m.load_state()
step(w, clock, 600)
check("G: still stale for the same build -> no restart while latched", "quit" not in eng.cmds)
m.valve_check = lambda vint: ("stale", 14190)
w.verdict_at = 0
step(w, clock, 30)
check("G: a newer required build re-arms the breaker", w.state["update_tripped"] == 0)

# H. console.log never proven -> players on -> the daily restart waits for an empty server
w, clock, eng = scenario(at("2026-10-04 06:59:00"))
eng.rcon_orig = eng.rcon
m.rcon = lambda c, expect_reply=True: eng.rcon_orig([x for x in c if not x.startswith("echo ")], expect_reply)
eng.humans = 2
step(w, clock, 900)
check("H: no live console, players on -> no quit, no chat", "quit" not in eng.cmds and w.console is None)
eng.humans = 0
step(w, clock, 30)
check("H: ... and quits once they leave", "quit" in eng.cmds)
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
m.rcon = lambda c, expect_reply=True: eng.rcon([x for x in c if not x.startswith("echo ")], expect_reply)
step(w, clock, 20)
eng.freeze()
step(w, clock, 600)
check("H: no live console.log -> no silence kill either (a quit could not be told apart)", not eng.killed and w.console is None)

# I. an undelivered quit is never forced (no quit echo would make the bridge book a false crash)
w, clock, eng = scenario(at("2026-10-04 06:59:50"))
base = eng.rcon
m.rcon = lambda c, expect_reply=True: None if c == ["quit"] else base(c, expect_reply)
step(w, clock, 120)
check("I: quit not delivered -> engine not killed, restart counted once", not eng.killed and eng.pid == 4242
      and len([e for e in w.state["restarts"] if e[1] == "daily"]) == 1)

# J. RCON not listening yet at boot (measured live: refused about a second after A2S first answered).
#    A session that never opens says nothing about console.log, so it is retried in seconds and never burns a try.
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
refused = {"n": 0}
live_rcon = eng.rcon
def boot_rcon(commands, expect_reply=True):
    if refused["n"] < 3:
        refused["n"] += 1
        return None
    return live_rcon(commands, expect_reply)
m.rcon = boot_rcon
step(w, clock, 60)
check("J: RCON refused at boot is retried within seconds; console.log proven inside a minute (%d refusals, %d try)"
      % (refused["n"], w.probe_tries), w.console is not None and w.probe_tries <= 1)
# J2. RCON never comes up: the fast retries stop at the cap, then the ordinary tries run out into the ALERT
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
m.rcon = lambda c, expect_reply=True: None
step(w, clock, m.PROBE_RCON_MISSES * m.PROBE_RCON_RETRY + m.PROBE_TRIES * m.PROBE_RETRY + 60)
check("J2: a dead RCON is capped at %d fast retries, then the %d ordinary tries run out"
      % (m.PROBE_RCON_MISSES, m.PROBE_TRIES),
      w.console is None and w.probe_rcon_misses == m.PROBE_RCON_MISSES and w.probe_tries >= m.PROBE_TRIES)


# ---- K-P: the warm-up and the nightly prefetch (2026-10-20) ----
ACF = ('"AppWorkshop"\n{\n\t"appid"\t\t"730"\n\t"SizeOnDisk"\t\t"30"\n\t"WorkshopItemsInstalled"\n\t{\n'
       '\t\t"111"\n\t\t{\n\t\t\t"size"\t\t"10"\n\t\t\t"timeupdated"\t\t"1000"\n\t\t\t"manifest"\t\t"7"\n\t\t}\n'
       '\t\t"222"\n\t\t{\n\t\t\t"size"\t\t"20"\n\t\t\t"timeupdated"\t\t"2000"\n\t\t\t"manifest"\t\t"8"\n\t\t}\n\t}\n'
       '\t"WorkshopItemDetails"\n\t{\n\t\t"111"\n\t\t{\n\t\t\t"manifest"\t\t"7"\n\t\t}\n'
       '\t\t"222"\n\t\t{\n\t\t\t"manifest"\t\t"8"\n\t\t}\n\t}\n}\n')
check("K: an acf round-trips byte-identical through kv_parse/kv_dump", m.kv_dump(m.kv_parse(ACF)) + "\n" == ACF)
check("K: acf_installed reads ids and timeupdated", m.acf_installed(ACF) == {"111": 1000, "222": 2000})
gone = m.acf_forget(ACF, "111")
check("K: acf_forget drops both entries of the item and nothing else",
      gone is not None and '"111"' not in gone and m.acf_installed(gone) == {"222": 2000} and gone.count('"222"') == 2)
check("K: acf_forget leaves a file without the item untouched", m.acf_forget(ACF, "999") == ACF)
check("K: acf_forget refuses a file the parser cannot reproduce",
      m.acf_forget(ACF.replace('\t\t"730"', ' "730"'), "111") is None)
ev = m.download_events(["[2001-01-01 12:00:00] [AppID 730] Download item 333 requested by app",
                        "[2001-01-01 12:14:53] [AppID 730] Download item 333 result : OK",
                        "[2001-01-01 12:14:54] [AppID 730] Download item 444 result : No Connection", "noise"])
check("L: workshop_log download lines parse",
      ev == [("333", "requested", ""), ("333", "result", "OK"), ("444", "result", "No Connection")])
det = m.details_parse(json.dumps({"response": {"publishedfiledetails": [
    {"publishedfileid": "111", "result": 1, "time_updated": 1500}, {"publishedfileid": "222", "result": 1, "time_updated": 2000},
    {"publishedfileid": "333", "result": 1, "time_updated": 10}, {"publishedfileid": "444", "result": 9},
    {"publishedfileid": "555", "result": 1, "time_updated": 10}]}}))
check("M: details_parse", det["111"] == (1, 1500) and det["444"] == (9, 0))
missing, outdated = m.prefetch_plan(["333", "444", "555", "111"], {"111": 1000, "222": 2000}, {"111", "222"}, det, {"555"})
check("M: plan = missing listed maps (not gone, not skipped) + newer Workshop copies (%s, %s)" % (missing, outdated),
      missing == ["333"] and outdated == ["111"])
# N. --warmup-reset: before the engine starts, the item's folder and acf entries go; a second run changes nothing
wsdir = os.path.join(ROOT, "game", "bin", "linuxsteamrt64", "steamapps", "workshop")
os.makedirs(os.path.join(wsdir, "content", "730", "111"), exist_ok=True)
with open(os.path.join(wsdir, "appworkshop_730.acf"), "w") as h:
    h.write(ACF)
m.WARMUP, m.WARMUP_ITEM, m.WORKSHOP_DIR, m.WORKSHOP_ACF = True, "111", wsdir, os.path.join(wsdir, "appworkshop_730.acf")
m.warmup_reset()
after = open(m.WORKSHOP_ACF).read()
check("N: --warmup-reset forgets the item (acf + folder)",
      '"111"' not in after and not os.path.isdir(os.path.join(wsdir, "content", "730", "111")))
m.warmup_reset()
check("N: a second --warmup-reset changes nothing", open(m.WORKSHOP_ACF).read() == after)
# N2. the download line: what is said, when, and to nobody when nobody is on
sent = []
real_rcon = m.rcon
m.rcon = lambda c, expect_reply=True: sent.extend(c) or ["gg_notice: shown"]
bridge0 = os.path.join(ROOT, "bridge0")
os.makedirs(bridge0, exist_ok=True)
with open(os.path.join(bridge0, "maps-deathmatch.json"), "w") as h:
    json.dump([{"rank": 1, "id": 900, "name": "Bastion", "sizeMb": 155}], h)
m.BRIDGE_DATA = bridge0
ln = m.DownloadLine()
ln.console_line('11/01 08:45:57 [Workshop] Updating 900: state 3', 100.0)
ln.step(101.0, 2)
check("line: nothing in its first seconds", sent == [])
ln.step(103.5, 0)
check("line: nothing with nobody on", sent == [])
ln.step(103.5, 2)
check("line: the name and its size once it has taken 3 s (%s)" % sent, len(sent) == 1 and sent[0].startswith("gg_notice 4 ") and "Bastion" in sent[0] and "155 MB" in sent[0])
ln.step(104.5, 2)
check("line: not more often than every 2 s", len(sent) == 1)
stage = os.path.join(wsdir, "downloads", "730", "900")
os.makedirs(stage, exist_ok=True)
with open(os.path.join(stage, "x.vpk"), "wb") as h:
    h.write(b"0" * 1000)
m.DownloadLine.staged = staticmethod(lambda wid: (155 << 20) // 2 if wid == "900" else 0)
ln.step(106.0, 2)
check("line: the progress from the staging folders (%s)" % sent[-1], "50 percent (78 MB of 155 MB)" in sent[-1] or "50 percent (77 MB of 155 MB)" in sent[-1])
m.DownloadLine.staged = staticmethod(lambda wid: 155 << 20 if wid == "900" else 0)
ln.step(108.5, 2)
check("line: the bytes in the size the list gives, never more than the whole (%s)" % sent[-1], "99 percent (155 MB of 155 MB)" in sent[-1])
check("line: a size in the units the HUD shows", m.size_words(464 << 20) == "464 MB" and m.size_words(3 << 29) == "1.5 GB")
ln.console_line('11/01 08:46:30 [Workshop] Updating 900: state 5', 107.0)
n = len(sent)
ln.step(110.0, 2)
check("line: state 5 ends it", len(sent) == n and not ln.active)
ln.console_line('11/01 [MultiAddonManager] Addon download started for 901', 200.0)
ln.console_line('11/01 [MultiAddonManager] Downloading addon 901: 3.00/6.00 MB (50.00%)', 203.0)
ln.step(204.0, 1)
check("line: MultiAddonManager's own bytes, an unlisted id by its number (%s)" % sent[-1], "Workshop item 901" in sent[-1] and "50 percent" in sent[-1])
ln.console_line('11/01 [MultiAddonManager] Addon 901 downloaded successfully', 205.0)
check("line: its success ends it", not ln.active)
ln.step(300.0, 1, own={"902"})
ln.started("902", 290.0)
n = len(sent)
ln.step(300.0, 1, own={"902"})
check("line: never about the watcher's own downloads", len(sent) == n)
ln.finished("902")
ln.started("900", 400.0)
ln.failed("900", "No Connection", 401.0)
ln.step(402.0, 3)
check("line: a failure says so once, and what to do (%s)" % sent[-1], sent[-1].startswith("gg_notice 10 ") and "could not download Bastion: No Connection" in sent[-1])
n = len(sent)
ln.step(405.0, 3)
check("line: then nothing more", len(sent) == n)
ln.console_line('nothing about downloads here', 500.0)
ln.console_line('[Workshop] Updating 903: state 1', 500.0)
ln.console_line('[Workshop] Updating 903: state 1', 500.0)
ln.console_line('[Workshop] Loading custom game "903" with map "x"', 500.0)
check("line: a cached map's load leaves nothing behind", not ln.active)
m.rcon = real_rcon
m.DownloadLine.staged = staticmethod(lambda wid: 0)
# N3. the maps folder: real folders per type, links in content/730, new downloads moved in, nothing lost
tree = os.path.join(ROOT, "orgtree")
content_o = os.path.join(tree, "game", "bin", "linuxsteamrt64", "steamapps", "workshop", "content", "730")
os.makedirs(content_o)
maps_o = os.path.join(tree, "maps")
def item(wid, text="x"):
    os.makedirs(os.path.join(content_o, wid), exist_ok=True)
    with open(os.path.join(content_o, wid, wid + "_dir.vpk"), "w") as h:
        h.write(text)
for wid in ("100", "200", "300", "400", "500", "600"):
    item(wid)
orgdata = os.path.join(ROOT, "orgdata")
os.makedirs(orgdata)
json.dump([{"rank": 1, "id": 100, "name": "De Dust 2 (remake)!", "sizeMb": 5}, {"rank": 2, "id": 400, "name": "My Map", "sizeMb": 1}], open(os.path.join(orgdata, "maps.json"), "w"))
json.dump([{"rank": 1, "id": 200, "name": "Bastion", "sizeMb": 5}, {"rank": 2, "id": 100, "name": "De Dust 2 (remake)!", "sizeMb": 5}], open(os.path.join(orgdata, "maps-deathmatch.json"), "w"))
json.dump([{"rank": 1, "id": 300, "name": "\u0420\u0443\u0441\u0441\u043a\u0438\u0439", "sizeMb": 5}], open(os.path.join(orgdata, "maps-wingman.json"), "w"))
json.dump({"maps": [{"id": 400, "name": "My Map"}]}, open(os.path.join(orgdata, "ours.json"), "w"))
saved = (m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, os.environ.get("GUNGAME_HUD_ADDON"))
m.BRIDGE_DATA, m.WARMUP_ITEM = orgdata, "600"
os.environ["GUNGAME_HUD_ADDON"] = "500"
m.WORKSHOP_ACF = os.path.join(ROOT, "no-such.acf")
line = m.organize(content_o, maps_o)
look = lambda wid: os.path.normpath(os.path.join(content_o, os.readlink(os.path.join(content_o, wid))))  # noqa: E731
check("organize: an Arms Race map, also on Deathmatch, is Arms Race's (%s)" % line, look("100") == os.path.join(maps_o, "armsrace", "100-de_dust_2_remake"))
check("organize: Deathmatch, Wingman with no ASCII in its title, own before the list, the HUD, not listed",
      look("200") == os.path.join(maps_o, "deathmatch", "200-bastion") and look("300") == os.path.join(maps_o, "wingman", "300")
      and look("400") == os.path.join(maps_o, "own", "400-my_map") and look("500") == os.path.join(maps_o, "hud", "500-hud"))
check("organize: the link is relative, the file readable through it",
      not os.path.isabs(os.readlink(os.path.join(content_o, "100"))) and open(os.path.join(content_o, "100", "100_dir.vpk")).read() == "x")
check("organize: the warm-up item stays where it is", os.path.isdir(os.path.join(content_o, "600")) and not os.path.islink(os.path.join(content_o, "600")))
check("organize: the index lists them", "armsrace\t100\tDe Dust 2 (remake)!" in open(os.path.join(maps_o, "INDEX.tsv"), encoding="utf-8").read())
check("organize: a second run changes nothing", m.organize(content_o, maps_o) == "maps folder: in order")
os.unlink(os.path.join(content_o, "100"))
item("100", "new")
line = m.organize(content_o, maps_o)
check("organize: Steam's real folder (an update replaced the link) replaces the old copy (%s)" % line,
      open(os.path.join(maps_o, "armsrace", "100-de_dust_2_remake", "100_dir.vpk")).read() == "new" and os.path.islink(os.path.join(content_o, "100")))
item("700")
line = m.organize(content_o, maps_o)
check("organize: a new download goes to other until the lists name it", look("700") == os.path.join(maps_o, "other", "700"))
json.dump([{"rank": 1, "id": 700, "name": "Now Listed", "sizeMb": 1}], open(os.path.join(orgdata, "maps-wingman.json"), "w"))
line = m.organize(content_o, maps_o)
check("organize: moved when the lists name it (%s)" % line, look("700") == os.path.join(maps_o, "wingman", "700-now_listed") and not os.path.exists(os.path.join(maps_o, "other", "700")))
os.unlink(os.path.join(content_o, "200"))
line = m.organize(content_o, maps_o)
check("organize: a folder that lost its link gets it back (%s)" % line, look("200") == os.path.join(maps_o, "deathmatch", "200-bastion"))
shutil.rmtree(os.path.join(maps_o, "deathmatch", "200-bastion"))
line = m.organize(content_o, maps_o)
check("organize: a link to nothing goes, the prefetch fetches it again (%s)" % line, not os.path.lexists(os.path.join(content_o, "200")))
m.BRIDGE_DATA = os.path.join(ROOT, "nothing-here")
check("organize: no lists, nothing touched", "not readable" in m.organize(content_o, maps_o))
m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF = saved[:3]
os.environ.pop("GUNGAME_HUD_ADDON", None)
# O. warm-up: WARMUP_DELAY after the engine answers, one mm_download_addon; the result line ends it
wslog = os.path.join(ROOT, "workshop_log.txt")
open(wslog, "w").close()
m.WORKSHOP_LOG = wslog


def wsw(line):
    with open(wslog, "a") as h:
        h.write(line + "\n")


w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, m.WARMUP_DELAY - 5)
check("O: no warm-up inside the delay", not any(c.startswith("mm_download_addon") for c in eng.cmds))
step(w, clock, 10)
check("O: warm-up command sent after the delay",
      eng.cmds.count("mm_download_addon 111") == 1 and w.workshop.warm["state"] == "running")
wsw("[x] [AppID 730] Download item 111 requested by app")
step(w, clock, 30)
wsw("[x] [AppID 730] Download item 111 result : OK")
step(w, clock, 3)
check("O: the result line ends the warm-up, once",
      w.workshop.warm["state"] == "done" and eng.cmds.count("mm_download_addon 111") == 1)
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, m.WARMUP_DELAY + 5 + m.NOOP_AFTER + 5)
check("O: no download started within NOOP_AFTER -> noop (the item was installed)", w.workshop.warm["state"] == "noop")
# P. prefetch: in its window, nobody on -> missing then outdated, one at a time; a human pauses it; a day stamp ends it
m.PREFETCH_RESTART = False           # the restart before the prefetch (03:25) has its own tests, T below
bridge = os.path.join(ROOT, "bridge")
os.makedirs(bridge, exist_ok=True)
with open(os.path.join(bridge, "maps.json"), "w") as h:
    json.dump([{"rank": 1, "id": 333}, {"rank": 2, "id": 222}, {"rank": 3, "id": 666}], h)
upvote = lambda *ids: json.dump(dict((i, {"[U:1:1]": 1}) for i in ids), open(os.path.join(bridge, "votes.json"), "w"))  # noqa: E731
upvote("333", "111")   # since 2026-11-03 the prefetch looks after the keep set and the queues only: upvoted maps are in it
with open(os.path.join(bridge, "broken.json"), "w") as h:
    json.dump({"packs": [{"id": 666}], "blocked": []}, h)
with open(os.path.join(bridge, "removed.json"), "w") as h:
    json.dump({"verdicts": {"222": "ok"}}, h)
with open(m.WORKSHOP_ACF, "w") as h:
    h.write(ACF)
os.makedirs(os.path.join(wsdir, "content", "730", "111"), exist_ok=True)
os.makedirs(os.path.join(wsdir, "content", "730", "222"), exist_ok=True)
m.BRIDGE_DATA, m.PREFETCH_AT, m.WARMUP_ITEM = bridge, (3, 30), ""
m.fetch_details = lambda ids: {i: {"333": (1, 50), "222": (1, 2000), "111": (1, 1500), "666": (1, 9)}.get(i, (9, 0))
                               for i in ids}
w, clock, eng = scenario(at("2026-10-05 03:29:00"))
step(w, clock, 50)
check("P: nothing before the window", not any(c.startswith("mm_download_addon") for c in eng.cmds))
step(w, clock, 30)
dl = lambda: [c for c in eng.cmds if c.startswith("mm_download_addon")]  # noqa: E731
check("P: first the missing listed map (333), never the pack (666): %s" % dl(), dl() == ["mm_download_addon 333"])
eng.humans = 1
wsw("[x] [AppID 730] Download item 333 result : OK")
step(w, clock, 20)
check("P: a human on -> no next download", dl() == ["mm_download_addon 333"])
eng.humans = 0
step(w, clock, 10)
check("P: empty again -> the outdated one (111): %s" % dl(), dl()[-1:] == ["mm_download_addon 111"])
wsw("[x] [AppID 730] Download item 111 result : OK")
step(w, clock, 10)
check("P: queue empty -> done and stamped for the night",
      w.workshop.pf is None and w.state["prefetch_day"] == "2026-10-05")
step(w, clock, 600)
check("P: not again the same night", dl().count("mm_download_addon 333") == 1)
m.PREFETCH_AT, m.WARMUP_ITEM = None, ""


# P2. the details API failing (it answers 502 in streaks): the batch is asked again after PREFETCH_DETAIL_RETRY,
#     and past PREFETCH_DETAIL_ERRORS the plan goes ahead with what came back
calls = {"n": 0}


def flaky(ids):
    calls["n"] += 1
    if calls["n"] <= 2:
        raise OSError("HTTP Error 502: Bad Gateway")
    return {i: {"333": (1, 50), "222": (1, 2000), "111": (1, 1500)}.get(i, (9, 0)) for i in ids}


m.fetch_details, m.PREFETCH_AT, m.WARMUP_ITEM = flaky, (3, 30), ""
with open(m.WORKSHOP_ACF, "w") as h:
    h.write(ACF)
w, clock, eng = scenario(at("2026-10-06 03:30:05"))
step(w, clock, 2 * m.PREFETCH_DETAIL_RETRY + 20)
check("P2: two failed details requests, asked again, then the plan runs (333 first): %s" % dl(),
      calls["n"] == 3 and dl()[:1] == ["mm_download_addon 333"])
calls["n"], m.PREFETCH_DETAIL_ERRORS = -100, 3
m.fetch_details = lambda ids: (_ for _ in ()).throw(OSError("HTTP Error 502: Bad Gateway"))
w, clock, eng = scenario(at("2026-10-07 03:30:05"))
step(w, clock, 4 * m.PREFETCH_DETAIL_RETRY + 30)
check("P2: the API down for good -> planned without details: nothing fetched blind, the night stamped",
      not any(c.startswith("mm_download_addon") for c in eng.cmds) and w.state["prefetch_day"] == "2026-10-07")
m.PREFETCH_AT, m.PREFETCH_DETAIL_ERRORS = None, 40


# O3. a warm-up whose item does not arrive for another reason than the stuck state ('Busy') is tried
#     once more, and a second failure ends in a WARNING, not in "warm" ('No Connection' has no retry: O4 below)
m.WARMUP_ITEM, m.PREFETCH_AT = "111", None
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
step(w, clock, m.WARMUP_DELAY + 5)
wsw("[x] [AppID 730] Download item 111 requested by app")
wsw("[x] [AppID 730] Download item 111 result : Busy")
step(w, clock, 5)
check("O3: a 'Busy' result is not 'done' - it waits to try again", w.workshop.warm["state"] == "waiting")
step(w, clock, m.WARMUP_RETRY + 5)
check("O3: the second try is sent after WARMUP_RETRY", eng.cmds.count("mm_download_addon 111") == 2)
wsw("[x] [AppID 730] Download item 111 requested by app")
wsw("[x] [AppID 730] Download item 111 result : Busy")
step(w, clock, 5)
check("O3: a second failure ends the warm-up as failed, with no third try",
      w.workshop.warm["state"] == "failed" and eng.cmds.count("mm_download_addon 111") == 2)
# P3. three failed downloads in a row ('Busy': not the stuck state, which stops it at once - U below) stop the night
m.WARMUP_ITEM, m.PREFETCH_AT = "", (3, 30)
with open(os.path.join(bridge, "maps.json"), "w") as h:
    json.dump([{"rank": 1, "id": 333}, {"rank": 2, "id": 444}, {"rank": 3, "id": 555}, {"rank": 4, "id": 777}], h)
upvote("333", "444", "555", "777")
with open(os.path.join(bridge, "broken.json"), "w") as h:
    json.dump({"packs": [], "blocked": []}, h)
m.fetch_details = lambda ids: {i: (1, 50) for i in ids}
with open(m.WORKSHOP_ACF, "w") as h:
    h.write(ACF)
w, clock, eng = scenario(at("2026-10-08 03:30:05"))
for wid in ("333", "444", "555"):
    step(w, clock, 15)
    wsw("[x] [AppID 730] Download item %s result : Busy" % wid)
step(w, clock, 15)
check("P3: 3 failures in a row stop the night (777 never asked): %s" % dl(),
      dl() == ["mm_download_addon 333", "mm_download_addon 444", "mm_download_addon 555"]
      and w.workshop.pf is None and w.state["prefetch_day"] == "2026-10-08")
m.PREFETCH_AT = None


# R. downloads broken in the engine process -> a "workshop" restart: only at 0 humans, the clean quit, limits
def broken_warmup(w, clock):
    """Drive a warm-up into 'No Connection': the process is marked broken at once (2026-10-22: no retry)."""
    step(w, clock, m.WARMUP_DELAY + 5)
    wsw("[x] [AppID 730] Download item 111 result : No Connection")
    step(w, clock, 3)


m.WARMUP_ITEM, m.PREFETCH_AT, m.RESTART_AT, m.AUTOUPDATE = "111", None, None, False
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
eng.humans = 1
broken_warmup(w, clock)
check("R: a 'No Connection' warm-up marks the process broken", w.workshop.broken is not None)
step(w, clock, 120)
check("R: with a human on, no restart and no chat warning", "quit" not in eng.cmds and not eng.chat)
eng.humans = 0
step(w, clock, 10)
check("R: the first moment nobody is on: a line for the log, then the clean quit",
      "quit" in eng.cmds and any("Workshop downloads are broken" in c for c in eng.chat))
check("R: counted as a workshop restart", [e[1] for e in w.state["restarts"]] == ["workshop"]
      and w.state["pending"]["kind"] == "workshop")
# R2. the limits: one 1 h ago -> warn only; three in 24 h -> warn only
for history, label in (([[int(clock.t) - 3600, "workshop"]], "one 1 h ago"),
                       ([[int(clock.t) - h * 3 * 3600, "workshop"] for h in (3, 4, 5)], "three in 24 h")):
    w, clock2, eng = scenario(at("2026-10-04 15:00:00"))
    w.state["restarts"] = [[e[0] - int(clock.t) + int(clock2.t), e[1]] for e in history]
    eng.humans = 0
    broken_warmup(w, clock2)
    step(w, clock2, 60)
    check("R2: %s -> no restart, only a warning" % label, "quit" not in eng.cmds and w.workshop.broken is not None)
# R2b. the gate's warning comes once per change, not once a second - it named the age of the restart it
# waits on ("a planned restart was N s ago"), which changed every second, so log_once took every line for a new one
said, real_log = [], m.log
m.log = said.append
try:
    w, clock2, eng = scenario(at("2026-10-04 15:00:00"))
    w.state["restarts"] = [[int(clock2.t) - 60, "daily"]]
    eng.humans = 0
    broken_warmup(w, clock2)
    step(w, clock2, 180)
finally:
    m.log = real_log
gate_lines = [s for s in said if "NOT restarting" in s]
check("R2b: a planned restart 1 min before the breakage -> the warning once in 3 min, naming when it was (%d line(s): %r)"
      % (len(gate_lines), gate_lines[:2]), len(gate_lines) == 1 and "planned restart was at " in gate_lines[0]
      and "quit" not in eng.cmds)
check("R2b: every gate names a moment, never an age",
      " ago" not in m.planned_block({"restarts": [[1000 - 100, "daily"]]}, 1000)[1]
      and " ago" not in m.freeze_block({"restarts": [[1000 - 100, "freeze"]], "freeze_tripped": 0, "freeze_misses": 0}, 1000)[1]
      and " ago" not in m.workshop_block({"restarts": [[1000 - 100, "daily"]]}, 1000)
      and " ago" not in m.workshop_block({"restarts": [[1000 - 3600, "workshop"]]}, 1000))
# R3. a download that works later clears the flag; a new engine pid clears it too
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
eng.humans = 1
broken_warmup(w, clock)
wsw("[x] [AppID 730] Download item 999 result : OK")
step(w, clock, 3)
check("R3: an OK download clears the broken flag", w.workshop.broken is None)
m.RESTART_AT, m.AUTOUPDATE = (7, 0), True


# ---- S-U: the stuck state: no futile retry, the evidence saved, a fresh process for the prefetch ----
CANCEL_MANIFESTS = "[x] [AppID 730] Update canceled: Failed downloading 306 manifests (No connection)"
CANCEL_CHUNK = ('[x] [AppID 730] Update canceled: Failed updating depot 730 while downloading chunk "a5e8" (Failure) '
                '(Content unavailable) "No Download Source"')
CANCEL_EMPTY = "[x] [AppID 730] Update canceled: Failed updating depot 730 while starting download (No connection to content servers)"
check("S: the cancel line parses as its own event", m.download_events([CANCEL_MANIFESTS]) ==
      [("", "canceled", "Failed downloading 306 manifests (No connection)")])
check("S: the three stuck signatures (result or cancel line)",
      m.stuck_result("No Connection") and m.stuck_result("Failure", CANCEL_CHUNK)
      and m.stuck_result("Failure", CANCEL_EMPTY) and m.stuck_result("Failure", CANCEL_MANIFESTS))
check("S: other failures are not the stuck state",
      not m.stuck_result("Failure", None) and not m.stuck_result("Busy", "Failed updating depot 730 (Disk write failure)"))
state, fault = m.validate_state({"schema": 2, "restarts": [[100, "prefetch"]], "prefetch_restart_day": "2026-10-22",
                                 "pending": {"kind": "prefetch", "at": 100}})
check("S: state with a prefetch restart validates", fault is None and state["prefetch_restart_day"] == "2026-10-22")
check("S: a prefetch restart is planned (floor) but outside the cap",
      m.planned_block({"restarts": [[1000 - 100, "prefetch"]]}, 1000) is not None
      and m.planned_block({"restarts": [[1000 - 3600 * h, "prefetch"] for h in range(1, 6)]}, 1000 + 3600) is None)

logs = os.path.join(ROOT, "steamlogs-src")
os.makedirs(logs, exist_ok=True)
depot = os.path.join(ROOT, "depotcache")
os.makedirs(depot, exist_ok=True)
for name in ("730_1.manifest", "730_2.manifest"):
    open(os.path.join(depot, name), "w").close()
with open(os.path.join(logs, "content_log.txt"), "w") as h:
    h.write("[x] Got 31 download sources and 0 caching proxies via ContentServerDirectoryService\n"
            "[x] HTTPS (SteamCache,1) - a: a/depot/730/manifest/1/5/2 - manifest request received 503 (Service Unavailable) HTTP response\n"
            "[x] No target sources connected\n")
wslog2 = os.path.join(logs, "workshop_log.txt")
open(wslog2, "w").close()
m.WORKSHOP_LOG, m.STEAM_LOGS, m.DEPOTCACHE = wslog2, logs, depot


def wsw2(*lines):
    with open(wslog2, "a") as h:
        h.write("\n".join(lines) + "\n")


# O4. a warm-up that ends 'No Connection' marks the process broken AT ONCE (no 10-minute retry), and the logs are kept
m.WARMUP_ITEM, m.PREFETCH_AT, m.RESTART_AT, m.AUTOUPDATE = "111", None, None, False
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
eng.humans = 1
step(w, clock, m.WARMUP_DELAY + 5)
wsw2("[x] [AppID 730] Download item 111 requested by app", CANCEL_MANIFESTS,
     "[x] [AppID 730] Download item 111 result : No Connection")
step(w, clock, 3)
snaps = sorted(os.listdir(m.SNAPSHOT_DIR)) if os.path.isdir(m.SNAPSHOT_DIR) else []
check("O4: 'No Connection' -> failed and broken at once, no second try",
      w.workshop.warm["state"] == "failed" and w.workshop.broken is not None and eng.cmds.count("mm_download_addon 111") == 1)
summary = open(os.path.join(m.SNAPSHOT_DIR, snaps[0], "summary.txt")).read() if snaps else ""
check("O4: one snapshot of the Steam logs, with a summary (%s)" % snaps,
      len(snaps) == 1 and os.path.exists(os.path.join(m.SNAPSHOT_DIR, snaps[0], "content_log.txt"))
      and "No target sources connected: 1" in summary and "HTTP 503: 1" in summary and "2 manifests" in summary)
step(w, clock, m.WARMUP_RETRY + 60)
check("O4: still one try and one snapshot after the old retry time",
      eng.cmds.count("mm_download_addon 111") == 1 and len(os.listdir(m.SNAPSHOT_DIR)) == 1)
eng.humans = 0
step(w, clock, 10)
check("O4: ... and the download restart follows at 0 humans", "quit" in eng.cmds
      and [e[1] for e in w.state["restarts"]] == ["workshop"])
# O5. 'Failure' with a 'No Download Source' cancel line is the stuck state too; a disk error is retried as before
for cancel, stuck, label in ((CANCEL_CHUNK, True, "chunk 'No Download Source'"),
                             ("[x] [AppID 730] Update canceled: Failed updating depot 730 (Disk write failure)", False,
                              "a disk write failure")):
    w, clock, eng = scenario(at("2026-10-04 15:00:00"))
    eng.humans = 1
    step(w, clock, m.WARMUP_DELAY + 5)
    wsw2("[x] [AppID 730] Download item 111 requested by app", cancel, "[x] [AppID 730] Download item 111 result : Failure")
    step(w, clock, 3)
    check("O5: %s -> %s" % (label, "broken at once" if stuck else "one retry, not broken"),
          (w.workshop.broken is not None and w.workshop.warm["state"] == "failed") if stuck
          else (w.workshop.broken is None and w.workshop.warm["state"] == "waiting"))
# U. the prefetch: never in a broken process, and only after the warm-up is done
with open(m.WORKSHOP_ACF, "w") as h:
    h.write(ACF)
with open(os.path.join(bridge, "maps.json"), "w") as h:
    json.dump([{"rank": 1, "id": 333}], h)
upvote("333")
m.fetch_details = lambda ids: {i: (1, 50) for i in ids}
m.PREFETCH_AT, m.PREFETCH_RESTART = (3, 30), False
w, clock, eng = scenario(at("2026-10-12 03:30:05"))
step(w, clock, 20)
check("U: inside the window, but the warm-up is waiting -> no prefetch yet",
      not [c for c in eng.cmds if c.startswith("mm_download_addon")] and w.workshop.pf is None)
step(w, clock, m.WARMUP_DELAY)
wsw2("[x] [AppID 730] Download item 111 requested by app", "[x] [AppID 730] Download item 111 result : OK")
step(w, clock, 30)
check("U: the warm-up done -> the prefetch starts (333): %s" % [c for c in eng.cmds if c.startswith("mm_download_addon")],
      [c for c in eng.cmds if c.startswith("mm_download_addon")] == ["mm_download_addon 111", "mm_download_addon 333"])
wsw2("[x] [AppID 730] Download item 333 requested by app", CANCEL_EMPTY, "[x] [AppID 730] Download item 333 result : Failure")
step(w, clock, 5)
check("U: a stuck prefetch download stops the run unstamped (it goes on in the next process) and marks broken",
      w.workshop.pf is None and w.state["prefetch_day"] == "" and w.workshop.broken is not None)
step(w, clock, 120)
check("U: ... and no new run starts in the broken process",
      w.workshop.pf is None and [c for c in eng.cmds if c.startswith("mm_download_addon")].count("mm_download_addon 333") == 1)
# T. a fresh process before the prefetch: 03:25, only at 0 humans, the clean quit, once a night
m.WARMUP_ITEM, m.PREFETCH_RESTART = "", True
age = {"s": 7200}
m.engine_age = lambda pid: age["s"]
w, clock, eng = scenario(at("2026-10-13 03:24:00"))
step(w, clock, 50)
check("T: before 03:25 -> no restart", "quit" not in eng.cmds)
step(w, clock, 20)
check("T: 03:25 with nobody on -> the clean quit, counted as a prefetch restart for that night",
      "quit" in eng.cmds and [e[1] for e in w.state["restarts"]] == ["prefetch"]
      and w.state["prefetch_restart_day"] == "2026-10-13" and w.state["pending"]["kind"] == "prefetch")
eng.pid, age["s"] = 4343, 30
step(w, clock, 600)
check("T: the new process is not restarted again that night", eng.cmds.count("quit") == 1)
age["s"] = 7200
w, clock, eng = scenario(at("2026-10-14 03:24:30"))
eng.humans = 2
step(w, clock, 900)
check("T2: players on at 03:25 -> no restart, no chat warning, it waits", "quit" not in eng.cmds and not eng.chat)
eng.humans = 0
step(w, clock, 10)
check("T2: ... and restarts the moment the server is empty", eng.cmds.count("quit") == 1
      and [e[1] for e in w.state["restarts"]] == ["prefetch"])
age["s"] = 300
w, clock, eng = scenario(at("2026-10-15 03:25:30"))
step(w, clock, 60)
check("T3: an engine 5 min old is fresh already -> no restart, tonight stamped",
      "quit" not in eng.cmds and w.state["prefetch_restart_day"] == "2026-10-15")
age["s"] = 7200
w, clock, eng = scenario(at("2026-10-16 03:20:00"))
w.state["prefetch_day"] = "2026-10-16"
step(w, clock, 600)
check("T4: tonight's prefetch already ran -> no restart", "quit" not in eng.cmds)
m.PREFETCH_AT, m.RESTART_AT, m.AUTOUPDATE = None, (7, 0), True

# ---- 2026-11-03: the keep set, the hand-over, the deletion, job 9 ----------------------------------------------------------------
# K2. acf_edit: hand-overs added, maps dropped, SizeOnDisk the sum again, every section in id order; a file it cannot reproduce: None
inst = [("size", "5"), ("timeupdated", "3000"), ("manifest", "9")]
det = [("manifest", "9"), ("timeupdated", "3000"), ("timetouched", "3001"), ("latest_timeupdated", "3000"), ("latest_manifest", "9")]
new = m.acf_edit(ACF, {"150": (inst, det)}, {"222"})
check("K2: acf_edit adds a hand-over's two entries in id order, drops the other map's, SizeOnDisk 10 + 5",
      new is not None and m.acf_installed(new) == {"111": 1000, "150": 3000} and '"SizeOnDisk"\t\t"15"' in new
      and new.index('"111"') < new.index('"150"') and '"latest_manifest"\t\t"9"' in new and '"222"' not in new
      and m.kv_dump(m.kv_parse(new)) + "\n" == new)
check("K2: acf_edit refuses a file it cannot reproduce byte for byte", m.acf_edit(ACF.replace('\t\t"730"', ' "730"'), {}, {"111"}) is None)

# KS. the keep set: its parts, its order, and every reason it refuses to be trusted with a deletion
NOW = 1793700000.0


def kin(**over):
    scores = dict((str(1000 + r), [1.0 - r / 1000.0, r, 0]) for r in range(1, 151))
    scores.update(dict((str(2000 + r), [r / 1000.0, r, 0]) for r in range(1, 121)))
    scores.update(dict((str(3000 + r), [0.5, r, 0]) for r in range(1, 61)))       # Wingman: all equal - list order breaks the ties
    base = {
        "maps.json": [{"rank": r, "id": 1000 + r, "name": "ar%d" % r, "sizeMb": 10} for r in range(1, 151)],
        "maps-deathmatch.json": [{"rank": r, "id": 2000 + r, "name": "dm%d" % r, "sizeMb": 20} for r in range(1, 121)],
        "maps-wingman.json": [{"rank": r, "id": 3000 + r, "name": "wm%d" % r, "sizeMb": 30} for r in range(1, 61)],
        "broken.json": {"maps": [{"id": 1001}], "packs": [{"id": 1002}], "removed": [], "blocked": [{"id": 1003}]},
        "removed.json": {"verdicts": {"1004": "removed", "1005": "ok"}},
        "votes.json": {"1149": {"[U:1:1]": 1, "[U:1:2]": -1, "[U:1:3]": -1, "[U:1:4]": -1}, "1148": {"[U:1:1]": -2},
                       "1147": {"[U:1:1]": -3}, "1146": {"[U:1:1]": -1}, "ar_baggage": {"[U:1:1]": 1}},
        "ours.json": {"maps": [{"id": 4001, "name": "own"}]},
        "ratings.json": {"version": 1, "fetched": NOW - 3600, "scores": scores},
        "queues.json": {"version": 1, "random": {"armsrace-new": [str(1100 + i) for i in range(15)],
                                                 "armsrace-original": [str(1010 + i) for i in range(15)],
                                                 "deathmatch": [str(2001 + i) for i in range(14)] + ["de_dust2"],
                                                 "wingman": [str(3001 + i) for i in range(15)]}, "requested": ["5001"]},
        "state.json": {"crashes": {"1006": 2, "1007": 0}, "playerCrashes": {"1008": "crashed 3 players' games"}},
    }
    base.update(over)
    return base


ks = m.keep_set(kin(), NOW, "500")
check("KS: a map with 1 up vote and 3 down votes stays; a too-big (-2), an ads (-3) or a plain down vote alone does not; an official name never counts",
      ks["up"] == ["1149"])
check("KS: Arms Race's top 100 by score, leaving out the broken, the non-map, the blocked, the removed, the crashed and the player-crash maps (%s ...)"
      % ks["top"]["armsrace"][:6], ks["top"]["armsrace"][:4] == ["1005", "1007", "1009", "1010"] and len(ks["top"]["armsrace"]) == 100
      and not set(["1001", "1002", "1003", "1004", "1006", "1008"]) & set(ks["top"]["armsrace"]) and ks["cutoff"]["armsrace"] == 1.0 - 106 / 1000.0)
check("KS: Deathmatch's best 100 of 120; Wingman's 60 all stay (fewer than 100), ties in list order, no cutoff",
      ks["top"]["deathmatch"][0] == "2120" and len(ks["top"]["deathmatch"]) == 100 and ks["top"]["wingman"] == [str(3000 + r) for r in range(1, 61)]
      and ks["cutoff"]["wingman"] is None)
check("KS: own maps, the HUD, the queues (official names left out) and the requests are in it; nothing is wrong",
      ks["own"] == ["4001"] and ks["hud"] == ["500"] and "de_dust2" not in ks["queues"]["deathmatch"] and len(ks["queues"]["deathmatch"]) == 14
      and set(["4001", "500", "5001", "1100", "1010", "3015"]) <= ks["all"] and ks["problems"] == [])
check("KS: the same inputs, the same set", m.keep_set(kin(), NOW, "500") == ks)
for label, inputs, hud, want in (
        ("ratings older than 3 days", kin(**{"ratings.json": {"version": 1, "fetched": NOW - 4 * 86400, "scores": kin()["ratings.json"]["scores"]}}), "500", "ratings.json is 96 h old"),
        ("ratings of no known age", kin(**{"ratings.json": {"version": 1, "scores": kin()["ratings.json"]["scores"]}}), "500", "of no known age"),
        ("ratings scoring half a list", kin(**{"ratings.json": {"version": 1, "fetched": NOW, "scores": dict((k, v) for k, v in kin()["ratings.json"]["scores"].items() if not ("2000" < k < "2061"))}}), "500", "scores 60 of the 120 playable deathmatch maps"),
        ("no votes.json", kin(**{"votes.json": None}), "500", "votes.json is missing or unreadable"),
        ("no maps.json", kin(**{"maps.json": None}), "500", "maps.json is missing or unreadable"),
        ("an empty maps.json", kin(**{"maps.json": []}), "500", "maps.json lists no map"),
        ("a short queue", kin(**{"queues.json": {"version": 1, "random": {"armsrace-new": ["1100"], "armsrace-original": [str(1010 + i) for i in range(15)], "deathmatch": [str(2001 + i) for i in range(15)], "wingman": [str(3001 + i) for i in range(15)]}}}), "500", "the armsrace-new queue holds 1 entries"),
        ("no queues.json", kin(**{"queues.json": None}), "500", "queues.json is missing or unreadable"),
        ("no own map", kin(**{"ours.json": {"maps": []}}), "500", "ours.json names no own map"),
        ("no HUD item", kin(), "", "no HUD item is set"),
        ("a tiny top list", kin(**{"ratings.json": {"version": 1, "fetched": NOW, "scores": {"1005": 1.0}}}), "500", "the armsrace top list holds 1 maps")):
    got = m.keep_set(inputs, NOW, hud)["problems"]
    check("KS: refused for %s (%s)" % (label, "; ".join(got)[:150]), any(want in p for p in got))

# N4. --organize with the keep set: a hand-over taken in; the deletion as a dry run, then for real, never the protected maps; refused when
#     the keep set is not to be trusted
t4 = os.path.join(ROOT, "tree4")
ws4 = os.path.join(t4, "game", "bin", "linuxsteamrt64", "steamapps", "workshop")
content4, maps4, hand4, depot4, data4 = os.path.join(ws4, "content", "730"), os.path.join(t4, "maps"), os.path.join(t4, "handover"), \
    os.path.join(t4, "depotcache"), os.path.join(t4, "data")
for d in (content4, maps4, hand4, depot4, data4, m.STATE_DIR):
    os.makedirs(d, exist_ok=True)


def put(folder, wid, text="x"):
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, wid + "_dir.vpk"), "w") as h:
        h.write(text)


ITEMS = ("100", "101", "102", "103", "150", "400", "500", "600")
for wid in ITEMS[:4] + ITEMS[5:]:
    put(os.path.join(content4, wid), wid)
acf4 = ('"AppWorkshop"\n{\n\t"appid"\t\t"730"\n\t"SizeOnDisk"\t\t"0"\n\t"WorkshopItemsInstalled"\n\t{\n'
        + "".join('\t\t"%s"\n\t\t{\n\t\t\t"size"\t\t"%d"\n\t\t\t"timeupdated"\t\t"1000"\n\t\t\t"manifest"\t\t"7%s"\n\t\t}\n' % (w, 1 << 30, w)
                  for w in ITEMS if w != "150")
        + '\t}\n\t"WorkshopItemDetails"\n\t{\n'
        + "".join('\t\t"%s"\n\t\t{\n\t\t\t"manifest"\t\t"7%s"\n\t\t}\n' % (w, w) for w in ITEMS if w != "150") + '\t}\n}\n')
data_files = {"maps.json": [{"rank": 1, "id": 100, "name": "Up Voted"}, {"rank": 2, "id": 101, "name": "Not Kept"}, {"rank": 3, "id": 102, "name": "Queued"}],
              "maps-deathmatch.json": [{"rank": 1, "id": 150, "name": "Top Rated"}], "maps-wingman.json": [], "broken.json": {},
              "removed.json": {"verdicts": {}}, "votes.json": {"100": {"[U:1:1]": 1}, "101": {"[U:1:1]": -1}},
              "ours.json": {"maps": [{"id": 400, "name": "Own"}]}, "state.json": {"crashes": {}},
              "ratings.json": {"version": 1, "fetched": None, "scores": {"100": 0.1, "101": 0.05, "102": 0.2, "150": 0.9}},
              "queues.json": {"version": 1, "random": {"armsrace-new": ["102"], "armsrace-original": ["102"], "deathmatch": ["150"],
                                                       "wingman": ["de_debris"]}, "requested": []}}
saved4 = (m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, m.WORKSHOP_DIR, m.HANDOVER_DIR, m.DEPOTCACHE, m.MAPS_DELETE, m.KEEP_TOP,
          m.KEEP_MIN_TOP, m.KEEP_MIN_QUEUE, m.time)
import time as real_time  # noqa: E402
m.time = real_time
data_files["ratings.json"]["fetched"] = real_time.time() - 600


def write_data(**over):
    for name, value in dict(data_files, **over).items():
        with open(os.path.join(data4, name), "w") as h:
            json.dump(value, h)


write_data()
with open(os.path.join(ws4, "appworkshop_730.acf"), "w") as h:
    h.write(acf4)
m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, m.WORKSHOP_DIR = data4, "600", os.path.join(ws4, "appworkshop_730.acf"), ws4
m.HANDOVER_DIR, m.DEPOTCACHE, m.MAPS_DELETE, m.KEEP_TOP, m.KEEP_MIN_TOP, m.KEEP_MIN_QUEUE = hand4, depot4, "dryrun", 1, 1, 1
os.environ["GUNGAME_HUD_ADDON"] = "500"
line = m.organize(content4, maps4)
stored = lambda: sorted(n.split("-", 1)[0] for k in m.MAP_TYPES for n in os.listdir(os.path.join(maps4, k)))  # noqa: E731
lists = sorted(n for n in os.listdir(m.STATE_DIR) if n.startswith("deleted-"))
check("N4: a dry run lists the maps outside the keep set and deletes nothing (%s)" % line,
      stored() == sorted(set(ITEMS) - set(["150", "600"])) and lists and lists[-1].endswith("-dryrun.tsv")
      and open(os.path.join(m.STATE_DIR, lists[-1])).read().splitlines()[1:] == ["armsrace\t101\tNot Kept\t1073", "other\t103\t103\t1073"])
keep4 = json.load(open(m.KEEP_FILE))
check("N4: keep.json: the parts, their sizes, the delete mode, no problems",
      keep4["delete"] == "dryrun" and keep4["problems"] == [] and keep4["parts"]["up"] == ["100"] and keep4["parts"]["top deathmatch"] == ["150"]
      and keep4["summary"]["up"] == {"maps": 1, "on_disk": 1, "gb_on_disk": 1.0, "gb": 1.0} and keep4["summary"]["top deathmatch"]["on_disk"] == 0)
# a hand-over of 150, complete, with its manifest; one incomplete (no marker); one whose folder does not match its marker
put(os.path.join(hand4, "150"), "150", "handed")
open(os.path.join(hand4, "150.manifest"), "wb").write(b"MANIFEST")
marker = {"version": 1, "id": "150", "title": "Top Rated", "files": 1, "bytes": 6,
          "installed": {"size": "6", "timeupdated": "3000", "manifest": "9150"},
          "details": {"manifest": "9150", "timeupdated": "3000", "timetouched": "3001", "latest_timeupdated": "3000", "latest_manifest": "9150"}}
json.dump(marker, open(os.path.join(hand4, "150.json"), "w"))
put(os.path.join(hand4, "160"), "160")
put(os.path.join(hand4, "170"), "170", "short")
json.dump(dict(marker, id="170", bytes=999), open(os.path.join(hand4, "170.json"), "w"))
line = m.organize(content4, maps4)
acf_now = open(m.WORKSHOP_ACF).read()
check("N4: a complete hand-over is taken in: its type's folder, its link, its acf entries, its manifest, its marker gone (%s)" % line,
      os.path.realpath(os.path.join(content4, "150")) == os.path.realpath(os.path.join(maps4, "deathmatch", "150-top_rated"))
      and open(os.path.join(content4, "150", "150_dir.vpk")).read() == "handed" and m.acf_installed(acf_now).get("150") == 3000
      and '"latest_manifest"\t\t"9150"' in acf_now and open(os.path.join(depot4, "730_9150.manifest"), "rb").read() == b"MANIFEST"
      and not os.path.exists(os.path.join(hand4, "150.json")) and not os.path.exists(os.path.join(hand4, "150")))
check("N4: a hand-over without its marker, and one whose folder does not match it, are left alone",
      os.path.isdir(os.path.join(hand4, "160")) and os.path.isdir(os.path.join(hand4, "170")) and os.path.exists(os.path.join(hand4, "170.json")))
check("N4: the acf was backed up before the edit", any(n.endswith("-organize") for n in os.listdir(ws4)))
put(os.path.join(hand4, "150"), "150", "handed")
json.dump(marker, open(os.path.join(hand4, "150.json"), "w"))
line = m.organize(content4, maps4)
check("N4: a hand-over of a map the server has as new already is dropped (%s)" % line,
      not os.path.exists(os.path.join(hand4, "150")) and not os.path.exists(os.path.join(hand4, "150.json")) and "1 hand-over(s) not needed" in line)
m.MAPS_DELETE = "on"
write_data(**{"ratings.json": dict(data_files["ratings.json"], fetched=real_time.time() - 5 * 86400)})
line = m.organize(content4, maps4)
check("N4: deletion on, but the ratings are 5 days old: refused, nothing deleted (%s)" % line,
      "101" in stored() and "103" in stored() and "refused" in line)
write_data()
line = m.organize(content4, maps4)
acf_now = open(m.WORKSHOP_ACF).read()
check("N4: deletion on: the maps outside the keep set go - folder, link, acf entries (%s)" % line,
      stored() == ["100", "102", "150", "400", "500"] and not os.path.lexists(os.path.join(content4, "101"))
      and not os.path.lexists(os.path.join(content4, "103")) and "101" not in m.acf_installed(acf_now) and '"103"' not in acf_now
      and any(n.startswith("deleted-") and n.endswith("-done.tsv") for n in os.listdir(m.STATE_DIR)))
check("N4: never the own map, the HUD, the queued map, the upvoted one or the warm-up item",
      set(["100", "102", "400", "500"]) <= set(stored()) and os.path.isdir(os.path.join(content4, "600")) and "600" in m.acf_installed(acf_now))
write_data(**{"votes.json": {}, "queues.json": dict(data_files["queues.json"], random={"armsrace-new": [], "armsrace-original": ["102"],
                                                                                     "deathmatch": ["150"], "wingman": ["de_debris"]})})
m.KEEP_MIN_QUEUE = 1
line = m.organize(content4, maps4)
check("N4: an empty queue is a reason to refuse: 100 and 102 stay (%s)" % line, "100" in stored() and "102" in stored() and "refused" in line)
put(os.path.join(content4, "180"), "180")
line = m.organize(content4, maps4)
check("N4: CS2_MAPS_DELETE=off-style refusals keep a new download too", "180" in stored())
(m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, m.WORKSHOP_DIR, m.HANDOVER_DIR, m.DEPOTCACHE, m.MAPS_DELETE, m.KEEP_TOP,
 m.KEEP_MIN_TOP, m.KEEP_MIN_QUEUE, m.time) = saved4
os.environ.pop("GUNGAME_HUD_ADDON", None)

# PL. the prefetch's list: what players queued, the queues' entries in play order, the upvoted maps, the top lists, own maps, the HUD
saved_pl = (m.BRIDGE_DATA, m.WARMUP_ITEM)
plist = os.path.join(ROOT, "pldata")
os.makedirs(plist, exist_ok=True)
for name, value in kin().items():
    json.dump(value, open(os.path.join(plist, name), "w"))
m.BRIDGE_DATA, m.WARMUP_ITEM = plist, ""
os.environ["GUNGAME_HUD_ADDON"] = "500"
real_t = m.time
m.time = types.SimpleNamespace(time=lambda: NOW, strftime=real_t.strftime, localtime=real_t.localtime)
listed, skip, what = m.prefetch_lists()
check("PL: the prefetch looks after the keep set and the queues only: %d maps, not the %d listed (%s)" % (len(listed), 150 + 120 + 60, what[:80]),
      listed[:6] == ["5001", "1100", "1010", "2001", "3001", "1101"] and "1149" in listed and "4001" in listed and "500" in listed
      and len(listed) == len(set(listed)) and len(listed) < 330 and "1002" in skip and "1003" in skip)
m.time = real_t
m.BRIDGE_DATA, m.WARMUP_ITEM = saved_pl
os.environ.pop("GUNGAME_HUD_ADDON", None)

# J9. job 9: the queued maps downloaded in the background - the requests first, then the queues' entries in play order; one at a
#     time; silent; never beside a download somebody waits for; the bridge told what is on disk
j9 = os.path.join(ROOT, "j9data")
os.makedirs(j9, exist_ok=True)
json.dump({"version": 1, "random": {"armsrace-new": ["901", "902"], "armsrace-original": ["903"], "deathmatch": ["904", "de_dust2"],
                                    "wingman": ["905"]}, "requested": ["900"]}, open(os.path.join(j9, "queues.json"), "w"))
saved9 = (m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, m.PREFETCH_AT, m.urllib.request.urlopen, os.environ.get("CS2_LOG_HTTP_URL"))
with open(m.WORKSHOP_ACF, "w") as h:
    h.write(ACF.replace('"111"', '"901"'))                     # 901 installed, with its folder: on disk
os.makedirs(os.path.join(wsdir, "content", "730", "901"), exist_ok=True)
reports = []


class Reply:
    def __init__(self, body):
        self.body = body
    def read(self):
        return self.body
    def __enter__(self):
        return self
    def __exit__(self, *a):
        return False


HOLD = []


def fake_urlopen(request, timeout=None):
    reports.append((request.full_url, request.data.decode()))
    return Reply(b"ok: 1 on disk" + (b"; hold: the vote is open" if HOLD else b""))


m.urllib.request.urlopen = fake_urlopen
os.environ["CS2_LOG_HTTP_URL"] = "http://bridge.test:8080/log/abc123"
m.BRIDGE_DATA, m.WARMUP_ITEM, m.PREFETCH_AT = j9, "", None
w, clock, eng = scenario(at("2026-10-04 15:00:00"))
eng.hud, eng.humans = True, 2
step(w, clock, 30)
dl = lambda: [c for c in eng.cmds if c.startswith("mm_download_addon")]  # noqa: E731
check("J9: the request first: %s" % dl(), dl() == ["mm_download_addon 900"])
check("J9: the bridge hears which queued maps are on disk, at its /ready/<secret>",
      reports and reports[0][0] == "http://bridge.test:8080/ready/abc123" and reports[0][1] == "gg-ready\t1\n901\n")
eng.write("[MultiAddonManager] Addon download started for 900")
eng.write("[MultiAddonManager] Downloading addon 900: 10.00/100.00 MB (10.00%)")
step(w, clock, 10)
check("J9: silent - no news line about it, with players on", not eng.notices)


def landed(wid):    # what the engine's Steam client does after a download: the acf names it, its folder is there
    with open(m.WORKSHOP_ACF) as h:
        text = h.read()
    entry = '\t\t"%s"\n\t\t{\n\t\t\t"size"\t\t"1"\n\t\t\t"timeupdated"\t\t"1"\n\t\t\t"manifest"\t\t"1"\n\t\t}\n' % wid
    head = '\t"WorkshopItemsInstalled"\n\t{\n'
    with open(m.WORKSHOP_ACF, "w") as h:
        h.write(text.replace(head, head + entry, 1))
    os.makedirs(os.path.join(wsdir, "content", "730", wid), exist_ok=True)


wsw2("[x] [AppID 730] Download item 900 requested by app")
wsw2("[x] [AppID 730] Download item 900 result : OK")
landed("900")
step(w, clock, 25)
check("J9: one at a time - after its result the next missing one, in play order (903, not 902): %s" % dl(),
      dl() == ["mm_download_addon 900", "mm_download_addon 903"])
eng.write("[Workshop] Updating 903: state 1")
step(w, clock, 10)
check("J9: a player's pick of the map it is fetching: the line shows it (%s)" % eng.notices[-1:], any("903" in n[1] for n in eng.notices))
wsw2("[x] [AppID 730] Download item 903 result : OK")
landed("903")
eng.write('[Workshop] Loading custom game "903" with map "x"')
eng.write("[Workshop] Updating 999: state 1")
step(w, clock, 30)
check("J9: while a download somebody waits for runs, nothing new starts: %s" % dl(), dl() == ["mm_download_addon 900", "mm_download_addon 903"])
eng.write("[Workshop] Updating 999: state 5")
step(w, clock, 25)
check("J9: ... and once it is done, the next: %s" % dl(), dl()[-1] == "mm_download_addon 904")
HOLD.append(1)
wsw2("[x] [AppID 730] Download item 904 result : OK")
landed("904")
step(w, clock, 45)
check("J9: the bridge says hold (a vote, a change counting down, a map loading): nothing new starts: %s" % dl(), dl()[-1] == "mm_download_addon 904")
HOLD.pop()
step(w, clock, 25)
check("J9: ... and once it does not, the next one goes (905): %s" % dl(), dl()[-1] == "mm_download_addon 905")
wsw2("[x] [AppID 730] Download item 905 result : OK")
landed("905")
step(w, clock, 25)
check("J9: ... then the one after it in play order (902, the second round): %s" % dl(), dl()[-1] == "mm_download_addon 902")
wsw2("[x] [AppID 730] Download item 902 result : Busy")
step(w, clock, 25)
check("J9: a failure is tried again later, and nothing is left to fetch now: %s" % dl(), dl()[-1] == "mm_download_addon 902" and w.workshop.ready.failed["902"][0] == 1)
w.workshop.ready.failed["902"] = (1, 0)
step(w, clock, 25)
check("J9: its retry: %s" % dl(), dl()[-1] == "mm_download_addon 902")
wsw2("[x] [AppID 730] Download item 902 result : No Connection")
step(w, clock, 5)
check("J9: a stuck result marks the process broken (job 6), and nothing more starts in it", w.workshop.broken is not None)
step(w, clock, 60)
check("J9: ... nothing more", dl()[-1] == "mm_download_addon 902" and dl().count("mm_download_addon 902") == 2)
(m.BRIDGE_DATA, m.WARMUP_ITEM, m.WORKSHOP_ACF, m.PREFETCH_AT, m.urllib.request.urlopen, url9) = saved9
if url9 is None:
    os.environ.pop("CS2_LOG_HTTP_URL", None)
os.environ["CS2_LOG_HTTP_URL"] = "http:\\/\\/bridge:8080\\/log\\/d4ab"
check("J9: bridge_endpoint reads CS2_LOG_HTTP_URL as the image templates it", m.bridge_endpoint() == ("http://bridge:8080", "d4ab"))
os.environ.pop("CS2_LOG_HTTP_URL", None)

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
