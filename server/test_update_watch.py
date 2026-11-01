"""Unit and scenario tests for update-watch.py beside this file: the version trap, Valve's replies, the console lines,
the daily window, both freeze paths (error-map signature; A2S + RCON silence), the loop protection and the planned
restarts - the scenarios drive Watch.loop_once() itself on a fake clock with a stubbed engine.

Run them under Linux with the image's Python (os.open on a directory, which save_state() fsyncs, fails on Windows):
    docker run --rm -v "$PWD:/repo:ro" -w /repo/server python:3.9-slim python test_update_watch.py
Exit status 0 when every check passes. Not deployed: only update-watch.py is copied to the server.
"""
import json, os, sys, tempfile, types
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

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
