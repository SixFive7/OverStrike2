#!/usr/bin/env python3
"""update-watch.py - restart the CS2 server when it needs it, between matches, and never in a loop.

Started in the background by pre.sh (which the image's entry.sh SOURCES as the steam user right before it launches
the game) and lives as long as the container. Deployed to <config dir>/update-watch.py,
bind-mounted read-only at /home/steam/cs2-update-watch.py (source of truth: server/ in the Over Strike 2
repo). Python 3.9 - the version in the joedwards32/cs2:4.0.1 image.

NINE JOBS, each behind its own switch in deploy/compose.example.yml (4, 5 and 9 never restart anything)
  1. UPDATES (CS2_AUTOUPDATE). entry.sh runs SteamCMD only when the container starts, so a Valve patch left the
     server rejecting every client for ~44 hours ("Bad host version"). Every CHECK_EVERY this asks
     Valve's ISteamApps/UpToDateCheck whether the installed build is current and restarts it when it is not.
  2. DAILY RESTART (CS2_RESTART_AT, 07:00 local time). Once a day,
     players or not, but only between matches and announced in chat.
  3. FREEZE RECOVERY (CS2_FREEZE_RECOVERY). A frozen engine never exits, so Docker never restarts it; this kills it
     after CS2_FREEZE_GRACE_SECONDS (10 s) of silence, on either of two paths:
     a. The error-map signature. Some workshop items drive the engine into its `error` map:
            SV:  Level loading started for 'error'
            LoopInit:  in "error" map - disconnecting...
            ../../game/shared/loopmodegame.cpp (652) : Assertion Failed in function LoopInit():
        Measured in the container log: 27 of 27 freezes printed that signature and then NOT ONE further
        line until something outside restarted the container (110-154 s later with the sweep's nanny, 65 and 118 min
        without it); none ever recovered. Kill once the signature is seen AND A2S has been silent for the grace AND
        no other map has started loading since.
     b. Silence alone, for the freezes that print nothing: "Mirage Silent" hung a sweep for 131 and
        125 s without a line, and the jb_ maps stall in their level load until the ENGINE'S own watchdog kills the
        process about a minute later. Kill when A2S AND RCON have both failed on every poll for the grace - at least
        SILENT_POLLS polls each, so one lost packet is one failed poll and never a streak - but only for an engine
        process that has answered A2S since it started (armed per pid: a boot, a relaunch or a new process is never
        killed for being slow to come up), never once the watcher itself has sent `quit` or killed it, and never
        after a `quit`/`_restart` from anyone else shows in console.log (the bridge's !restart quits too).
     Why 10 s and no map-load exemption: in the same sweep 368 healthy map loads (327 fresh downloads, up to 2.9 GB)
     kept A2S quiet for 2 s at most - the old map keeps answering while a workshop map downloads, and the load itself
     is the only silent stretch. 10 s is 5x the longest healthy silence, so a load needs no exemption, and an
     exemption would blind both paths in exactly the place freezes happen: during a load. The honest cost: a real
     host stall of more than 10 s (the whole box frozen, not the game) would restart a healthy server once, and the
     players on it would reconnect after ~15-20 s.

  4. WARM-UP (CS2_WARMUP, CS2_WARMUP_ITEM). The first workshop download in an engine process makes the Steam client
     re-request the depot manifest of every installed item first (~250 a minute: a large collection takes many
     minutes) - but only those it has no verified manifest for in ~/Steam/depotcache; with that folder on a volume
     the warm-up takes seconds (THE STUCK STATE below). So
     CS2_WARMUP_DELAY_SECONDS after a new engine first answers A2S this sends `mm_download_addon <CS2_WARMUP_ITEM>`
     (MultiAddonManager: a download without a map change) and logs how long the job took and how many manifests the
     depotcache holds. pre.sh runs `update-watch.py --warmup-reset` before every engine start - nothing holds the acf
     then - so that item is never installed at boot and the job is a real one: a test of the download path.
  5. NIGHTLY PREFETCH (CS2_PREFETCH_AT, CS2_PREFETCH_MINUTES). Once a night inside that window, and only while A2S
     shows nobody (it pauses for players and carries on when they leave): asks the Workshop for time_updated of the maps
     it looks after, then `mm_download_addon`s each one that is missing and each one whose Workshop copy is newer than
     the installed one, one at a time. Since 2026-11-03 those are THE KEEP SET and the bridge's queues (below) - a few
     hundred maps, of which a handful need anything on a night - not every listed or installed map any more. Packs,
     blocked and removed items (broken.json, removed.json) are never fetched.

  6. DOWNLOAD RESTART (CS2_WORKSHOP_RESTART). Sometimes an engine process's Steam client
     gets stuck: every workshop download ends "No Connection" ("No target sources connected") while SteamCMD fetches
     the same item at once, and only a restart clears it. Why
     (THE STUCK STATE below): the client has dropped every download source it had, and a process never
     gets them back. So a warm-up or prefetch download that ends "No Connection" marks the process broken at once
     (a retry in the same process cannot work); any other failure is retried once. A "workshop" restart then becomes
     due: only while A2S and RCON both show 0 humans (with players on it waits - never a kick, never a match-end
     restart), through the same clean RCON `quit` as the daily restart (the bridge reads its echo as a clean shutdown,
     not a crash), announced in the server log first. At most one per 2 h and 3 per rolling 24 h; past that it only
     warns. A download that succeeds later in the same process, or a new process, clears it. The moment a process is
     marked broken - before any restart - the Steam client's logs are copied to update-watch/steamlogs/<time>-<why>/
     on the game volume, with a summary.txt (the newest 10 are kept): the evidence dies with the container otherwise.
  7. FRESH PROCESS FOR THE PREFETCH (CS2_PREFETCH_RESTART). A clean restart
     PREFETCH_RESTART_LEAD (5 min) before CS2_PREFETCH_AT - 03:25 - so the nightly prefetch always runs in a fresh
     engine process with all its download sources, warmed: only at 0 humans (A2S and RCON), the same clean quit,
     never with players on (it waits for an empty server until the prefetch window closes). An engine younger than
     PREFETCH_FRESH (20 min) is fresh already and is not restarted. The prefetch itself starts only once that process's
     warm-up is done - not on the clock - and never in a process marked broken.
  8. THE DOWNLOAD LINE (CS2_DOWNLOAD_LINE; since 2026-11-02). While the server downloads a map somebody is
     waiting for - a vote's result, a request, a Deathmatch or Wingman Workshop map, an id typed on the keyboard - the HUD's news
     line says so, with the progress, once it has taken LINE_AFTER seconds: nothing else on screen tells them why nothing
     happens. The engine's own download (host_workshop_map) prints only "[Workshop] Updating <id>: state <n>"; MultiAddonManager's
     (mm_download_addon) prints its bytes every second; both are read from console.log, and the engine's bytes from the cache's
     staging folders. Never for the watcher's own warm-up, prefetch and job 9, never with nobody on, never in chat. A player's
     download that ends "No Connection" marks the process broken like the watcher's own (job 6): the restart waits for an
     empty server, as ever.
  9. THE QUEUED MAPS IN THE BACKGROUND (CS2_READY; since 2026-11-03: random maps never wait for a download). The bridge rolls its
     dice ahead into four random queues of 15 (Arms Race's new and original
     maps, Deathmatch, Wingman) and writes them, with the maps players have queued, to its /data/queues.json. Every READY_EVERY
     seconds this looks which of them are not on disk and downloads the next one - the requests first, then the queues' first
     entries, their second ones, and so on - with `mm_download_addon`, one at a time, also while people play, and silently: no
     news line, since nobody waits for it. Never while a download somebody waits for runs (job 8's: it always goes first), nor
     before the warm-up is done, nor beside the prefetch, nor in a process whose downloads are broken; a stuck result marks the
     process broken exactly like job 6's own. A map a player picks while it is being fetched is theirs from then on: the line
     shows it. After every look - and at least every READY_REPORT_EVERY - the bridge hears which queued maps are on disk (POST
     /ready/<secret>, the log push's own address and secret), and its random picks take those first. Right before a download starts
     the report goes once more, and an answer of '; hold: <why>' - a map change counting down, a map loading, a vote open - holds it:
     Steam's client runs one Workshop download at a time and one already running cannot be stopped (measured on a test copy on
     2026-11-03: a map change's 449 MB download started only when the 350 MB one in the background had ended, 8 s later).

THE MAPS FOLDER (`update-watch.py --organize`, run by pre.sh before every engine start, so at the latest by the 03:25 restart; since
2026-11-02). The maps live in real folders per type - maps/armsrace, deathmatch, wingman, own, hud, other, each
map <id>-<name> - on the game volume, and Steam's steamapps/workshop/content/730/<id> is a relative link to its folder. Every step
is one rename or one link on one file system, with no engine running. What Steam downloaded since the last run is a real folder
in content/730 (a new map; an update may replace a link by a real folder too): it moves into its type's folder and gets its link;
an older copy of it there goes. Moved between types when the lists say so, renamed when a title changes. The warm-up item stays where
Steam puts it: it is forgotten at every boot. CS2_MAPS_LAYOUT=off leaves everything alone. Then, in the same run: THE HAND-OVER and
THE DELETION.

THE KEEP SET (the owner's rules of 2026-11-03, keep_set; workshop-sync.py has the same function, word for word): the maps the server
stores. Every map with at least one player's up vote in votes.json (an up vote is kept however many down votes the map has; too-big
and ads votes are down votes); per mode, three lists of its 100 playable maps (on that mode's list, not on broken.json, not gone
from the Workshop, no crash on record), together up to 300 - the best Workshop score (Top Rated: vote_data.score), the most
current subscriptions (Most Subscribed) and Steam's trend order over the last 30 days (Most Popular), all from ratings.json, which
the sync fetches daily; the own maps (ours.json) and the HUD item; the bridge's random queues and the maps players queued
(queues.json). Written with its counts and sizes per part and per list to update-watch/keep.json at every engine start.

THE HAND-OVER: the sync (workshop-sync.py) downloads the keep set's maps the server lacks, and keeps the ones it tested that are in
the keep set, and leaves each in workshop-sync/handover/: <id>/ (the content), <id>.manifest (the Steam client's verified manifest)
and, written last, <id>.json (its acf entries as SteamCMD wrote them, and how many files and bytes the folder holds). --organize
takes in every complete one - the folder holds exactly what its marker says - while no engine runs: the folder into its type's
folder (a rename, the same dataset), its link in content/730, its two entries in appworkshop_730.acf (the acf backed up beside
itself first, SizeOnDisk summed again), the manifest into ~/Steam/depotcache. A map the server has as new already is dropped. Steam's
client then holds it exactly as if it had downloaded it itself: it loads without a download, and an author's later update is seen
like any other (the prefetch's time_updated against the acf's, the client's manifest against the latest).

THE DELETION (CS2_MAPS_DELETE, off | dryrun | on; MAPS_DELETE_DEFAULT): every stored map outside the keep set - its acf entries
(backed up, one atomic write), its link, its folder - in armsrace, deathmatch, wingman and other; never own, never hud, never the
warm-up item, a queued or a requested map, whatever the keep set says. Refused, with the reason in the log, when any input is
missing or does not parse, the ratings are over 3 days old or have the scores or subscriptions of under 90% of a list, a mode has no
Most Popular order, any of a mode's three lists is under 50 maps, a queue holds under 10, or there is no own map or HUD item. Every run writes its list to update-watch/deleted-<time>-<done | dryrun |
refused>.tsv and logs the count, the size and the volume's free space before and after. dryrun writes the list and deletes nothing.
It runs at every engine start - crash restarts and container recreates included - so all of this is checked every time.

THE STUCK STATE
  The Steam client in the engine downloads from a ranked list of content servers. A request that fails - an HTTP 503
  included - drops that server for the life of the process, and once none is left every download ends "No target sources
  connected" without a single connection attempt; only a new process recovers. Valve's caches answer manifest requests
  with short 503 bursts, so a job that must fetch the manifest of every installed item meets several of them. That full
  re-check happens when ~/Steam/depotcache, where the client keeps every manifest it has verified, is empty - in a new
  container, and again after a failed re-check, which writes nothing. With the manifests on disk a new process re-checks
  nothing, so keep that folder on a volume; this watcher logs at start how many manifests it holds. Servers the client
  ranks first but cannot reach lead to the same state after an idle spell, so every content server the client is given
  must stay reachable. A process loses a server on every 503 over its life, which is what the fresh process before the
  prefetch is for; one whose downloads are stuck is restarted (CS2_WORKSHOP_RESTART).

HOW A RESTART HAPPENS
  Planned (update, daily): RCON `quit` - the engine exits, game/cs2.sh returns (its relaunch loop only repeats
  on exit code 42), entry.sh reaches its end, PID 1 exits and Docker's `restart: always` re-enters entry.sh, the
  only thing that re-runs SteamCMD. RCON `_restart` would NOT update: cs2.sh relaunches the binary in place.
  The quit is echoed into the log stream as `rcon from ...: command "quit"`, which the bridge reads as a clean
  shutdown, so it is not recorded as a crash. Freeze: SIGTERM, then SIGKILL, to the engine; the bridge records
  that one as a crash of the map that caused it, which is the truth.
  When nobody is on, a planned restart happens at once. With players on it is announced, then waits for the next
  `Game Over:` (one per match; mp_match_end_restart replays the map straight away) and quits 3 s after it.

WHERE IT LOOKS
  The watcher cannot read the container's stdout, so the engine runs with -condebug (console.log) and
  sv_logecho 1 (game-event lines such as `Game Over:` echoed into the console) - both set in the deployment's compose file. At start
  it proves the file is live: an RCON `echo` of a random token must appear in a console.log within 10 s. If no
  file proves live, freeze recovery is off and planned restarts wait for an empty server; the log says so.

THE VERSION TRAP
  Valve's UpToDateCheck does a numeric >= against required_version and reports "up to date" for almost any
  number. Measured: version=14186 -> up_to_date:false, required_version:14188; version=14188 -> true;
  version=2000919 (steam.inf's ServerVersion, for a KNOWN-STALE build) -> true. The only correct input is
  steam.inf's dotted PatchVersion with the dots removed (1.41.8.8 -> 14188); patch_version() reads that field
  and nothing else, and version_int() rejects anything without a dot.

LOOP PROTECTION, persisted in update-watch/state.json on the volume (the container restarting is the very thing
being counted, so memory would be worthless)
  planned restarts: >= 10 min apart, <= 4 in any rolling 24 h; a build restart that left PatchVersion unchanged
  is a miss, retried after 30 min and then 2 h (Valve's CDN can lag its own API), and the 3rd miss latches the
  update breaker - at most three update restarts, ever, for a build SteamCMD cannot fetch. freeze kills (both paths
  share these counters): >= 3 min apart, <= 10 per rolling 24 h;
  a kill after which the server never answers A2S within 10 min is a miss and 2 misses latch the freeze breaker.
  A latched breaker logs every 30 min and only deleting state.json re-arms it. A state file that will not parse,
  a state directory that is not writable, no RCON password, Valve unreachable or unparseable, steam.inf missing,
  A2S silence, or A2S and RCON disagreeing about players: each means "no action", never "act".
"""

import fcntl
import json
import math
import os
import re
import secrets
import shutil
import signal
import socket
import struct
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timedelta

try:
    from zoneinfo import ZoneInfo
except ImportError:  # pragma: no cover - Python 3.9 has it; the C library's local time is the fallback
    ZoneInfo = None

BOOT_WARNINGS = []


def int_env(name, default, low, high):
    """An integer from the environment, range-checked; a typo must never silently widen a safety gate."""
    raw = os.environ.get(name)
    if raw is None or str(raw).strip() == "":
        return default
    try:
        value = int(str(raw).strip())
    except ValueError:
        BOOT_WARNINGS.append("%s=%r is not a number, using %d" % (name, raw, default))
        return default
    if not low <= value <= high:
        BOOT_WARNINGS.append("%s=%d is outside %d..%d, using %d" % (name, value, low, high, default))
        return default
    return value


def flag_env(name, default):
    raw = (os.environ.get(name) or "").strip()
    if raw == "":
        return default
    if raw not in ("0", "1"):
        BOOT_WARNINGS.append("%s=%r is neither 0 nor 1, using %d" % (name, raw, default))
        return default
    return raw == "1"


def parse_hhmm(raw, default):
    """'07:00' -> (7, 0); 'off' -> None; anything else -> the default, loudly."""
    if raw is None or raw.strip() == "":
        return default
    raw = raw.strip().lower()
    if raw in ("off", "none", "-1"):
        return None
    found = re.match(r"^([01]?\d|2[0-3]):([0-5]\d)$", raw)
    if not found:
        BOOT_WARNINGS.append("CS2_RESTART_AT=%r is not HH:MM or off, using %02d:%02d" % ((raw,) + default))
        return default
    return int(found.group(1)), int(found.group(2))


AUTOUPDATE = flag_env("CS2_AUTOUPDATE", True)
FREEZE_RECOVERY = flag_env("CS2_FREEZE_RECOVERY", True)
RESTART_AT = parse_hhmm(os.environ.get("CS2_RESTART_AT"), (7, 0))
FREEZE_GRACE = int_env("CS2_FREEZE_GRACE_SECONDS", 10, 5, 3600)  # seconds of silence before a kill, both paths
CHECK_EVERY = int_env("CS2_AUTOUPDATE_CHECK_MINUTES", 30, 10, 1440) * 60

DAILY_WINDOW = 7200         # the daily restart may happen up to 2 h after its slot, then today is skipped
PLANNED_MIN_GAP = 600       # floor between two planned restarts (10 minutes)
PLANNED_MAX_PER_DAY = 4     # cap on planned restarts in any rolling 24 h
UPDATE_MAX_MISSES = 3       # build restarts that changed nothing before the update breaker latches
UPDATE_RETRY = (1800, 7200) # after a miss, wait 30 min, then 2 h, before trying again (CDN propagation lag)
FREEZE_MIN_GAP = 180        # floor between two freeze kills (3 minutes)
FREEZE_MAX_PER_DAY = 10     # cap on freeze kills in any rolling 24 h
FREEZE_MAX_MISSES = 2       # freeze kills after which the server never came back, before that breaker latches
RECOVER_WITHIN = 600        # a restart must bring A2S back within 10 minutes to count as recovered
QUIT_LEAD = 3               # seconds between the "restarting now" line and the quit
QUIT_TIMEOUT = 30           # an engine still alive this long after `quit` is SIGTERMed, then SIGKILLed
DEFAULT_BACK_IN = 30        # announced "back in" until restarts were measured: container start -> A2S was 6-12 s (n=19)
WINDOW = 86400
TICK = 1.0
A2S_EVERY = 2               # seconds between local A2S probes: a 10 s grace needs several polls inside it
PROBE_RETRY = 300           # console.log liveness probe: retry interval, up to PROBE_TRIES times
PROBE_TRIES = 3
PROBE_RCON_RETRY = 10       # RCON not accepting yet: it listens a moment after A2S at boot, so come back soon...
PROBE_RCON_MISSES = 30      # ...but only this many times (5 min), then a dead RCON counts as tries again
ALERT_REPEAT = 1800
OK_REPEAT = 21600
A2S_TIMEOUT = 1             # a healthy engine answers on localhost in milliseconds; a load is the silent stretch
RCON_PROBE_TIMEOUT = 1.5    # the RCON liveness probe (auth only, no command), asked only while A2S is failing
SILENT_POLLS = 3            # consecutive failed polls, A2S and RCON each, before silence alone may kill
RCON_TIMEOUT = 5
HTTP_TIMEOUT = 10

STEAMAPPDIR = os.environ.get("STEAMAPPDIR") or "/home/steam/cs2-dedicated"
STATE_DIR = os.path.join(STEAMAPPDIR, "update-watch")
STATE_FILE = os.path.join(STATE_DIR, "state.json")
LOCK_FILE = os.path.join(STATE_DIR, "lock")
STEAM_INF = os.path.join(STEAMAPPDIR, "game", "csgo", "steam.inf")
# -condebug writes to the FIRST writable game search path. With the plugin layer on (CS2_PLUGINS=1), pre.sh puts
# csgo/addons/metamod at the head of gameinfo.gi, so the log lands there - measured live. Vanilla
# (CS2_PLUGINS=0) falls through to csgo/console.log.
CONSOLE_CANDIDATES = [os.path.join(STEAMAPPDIR, "game", "csgo", "addons", "metamod", "console.log"),
                      os.path.join(STEAMAPPDIR, "game", "csgo", "console.log"),
                      os.path.join(STEAMAPPDIR, "game", "console.log"),
                      os.path.join(STEAMAPPDIR, "game", "bin", "linuxsteamrt64", "console.log")]

GAME_PORT = int_env("CS2_PORT", 27015, 1, 65535)
RCON_PASSWORD = os.environ.get("CS2_RCONPW") or ""
VALVE_URL = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/?appid=730&version=%d"
USER_AGENT = "cs2-update-watch/2 (Over Strike 2; server/update-watch.py)"
TZNAME = os.environ.get("TZ") or "UTC"

STOPPING = []


def local_tz():
    if ZoneInfo is None:
        return None
    try:
        return ZoneInfo(TZNAME)
    except Exception:
        return None


TZ = local_tz()


# ------------------------------------------------------------------------------------------------
# Logging
# ------------------------------------------------------------------------------------------------

def log(message):
    """One line to the container log (stdout is inherited from entry.sh)."""
    sys.stdout.write("update-watch: %s %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), message))
    sys.stdout.flush()


LAST_SAID = {}


def log_once(key, message, repeat=ALERT_REPEAT):
    """Log when the wording changes, else again after `repeat` s: a 1 s loop must not flood the log."""
    now = time.time()
    previous = LAST_SAID.get(key)
    if previous and previous[0] == message and now - previous[1] < repeat:
        return
    LAST_SAID[key] = (message, now)
    log(message)


def hms(seconds):
    seconds = max(0, int(seconds))
    return "%dh%02dm" % (seconds // 3600, (seconds % 3600) // 60)


def at_clock(t):
    """A moment as local wall-clock time, HH:MM:SS. Why a gate is shut names WHEN the restart or kill it waits on
    happened, not how long ago: an age changes every second, and log_once then took every line for a change -
    "NOT restarting: a planned restart was N s ago. Only warning." 44 times in a minute.
    A moment stays the same, so the line comes once per change, as the other warnings do."""
    return "%02d:%02d:%02d" % tuple(time.localtime(t)[3:6])


# ------------------------------------------------------------------------------------------------
# Versions - pure, unit-tested
# ------------------------------------------------------------------------------------------------

VERSION_RX = re.compile(r"^[0-9]+(?:\.[0-9]+)+$")  # at least one dot: structurally rejects ServerVersion


def version_int(dotted):
    """1.41.8.8 -> 14188, the only input UpToDateCheck interprets correctly. None for anything not dotted
    (a bare ServerVersion such as 2000924 is exactly the trap) or outside a plausible range."""
    if not isinstance(dotted, str) or not VERSION_RX.match(dotted):
        return None
    value = int(dotted.replace(".", ""))
    return value if 1000 <= value <= 99999999 else None


def patch_version(steam_inf_text):
    """The PatchVersion field of steam.inf, and only that field (never ServerVersion or ClientVersion)."""
    for line in steam_inf_text.splitlines():
        key, _, value = line.partition("=")
        if key.strip() == "PatchVersion":
            return value.strip() or None
    return None


def read_patch_version():
    try:
        with open(STEAM_INF, "r", encoding="utf-8", errors="replace") as handle:
            return patch_version(handle.read(65536))
    except OSError:
        return None


def valve_verdict(raw, vint):
    """Classify an UpToDateCheck reply for build `vint`: ("current", None), ("stale", required) or
    ("unknown", reason). Every field is type-checked; required_version must be strictly newer than ours."""
    try:
        document = json.loads(raw)
    except ValueError as error:
        return "unknown", "response is not JSON (%s)" % error
    body = document.get("response") if isinstance(document, dict) else None
    if not isinstance(body, dict):
        return "unknown", "no response object"
    if body.get("success") is not True:
        return "unknown", "success is not true (%r)" % body.get("success")
    up_to_date = body.get("up_to_date")
    if up_to_date is True:
        return "current", None
    if up_to_date is not False:
        return "unknown", "up_to_date is not a boolean (%r)" % (up_to_date,)
    required = body.get("required_version")
    if isinstance(required, bool) or not isinstance(required, int):
        return "unknown", "required_version is absent or not an integer (%r)" % (required,)
    if required <= vint:
        return "unknown", "required_version %d is not newer than the local %d" % (required, vint)
    return "stale", required


def valve_check(vint):
    try:
        request = urllib.request.Request(VALVE_URL % vint, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT) as response:
            if response.status != 200:
                return "unknown", "HTTP %s" % response.status
            raw = response.read(65536).decode("utf-8", "replace")
    except Exception as error:  # urllib raises a wide family; none of them may stop the loop
        return "unknown", "%s: %s" % (type(error).__name__, error)
    return valve_verdict(raw, vint)


# ------------------------------------------------------------------------------------------------
# Console lines - pure, unit-tested. Anchored so a player's chat (echoed as `"Name<..>" say "..."`) can never
# imitate them: the patterns only match before the first double quote of a line.
# ------------------------------------------------------------------------------------------------

LOG_PREFIX = r'^[^"]*?(?:L )?\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d(?:\.\d+)?(?: - |: )'
GAME_OVER_RX = re.compile(LOG_PREFIX + r"Game Over: ")
FREEZE_RXS = [re.compile(r"^[^\"]*Level loading started for 'error'"),
              re.compile(r'^[^"]*LoopInit:\s+in "error" map'),
              re.compile(r"^[^\"]*Assertion Failed in function LoopInit\(\)")]
LEVEL_RX = re.compile(r"^[^\"]*Level loading started for '([^']*)'")
# The echo of an RCON quit or restart, from anyone (the bridge's !restart quits): the engine is going down on purpose.
QUIT_ECHO_RX = re.compile(LOG_PREFIX + r'rcon from "[^"]*": command "(?:quit|_restart|restart)(?: [^"]*)?"\s*$')


def is_game_over(line):
    return bool(GAME_OVER_RX.match(line))


def is_freeze_signature(line):
    return any(rx.match(line) for rx in FREEZE_RXS)


def level_started(line):
    found = LEVEL_RX.match(line)
    return found.group(1) if found else None


def is_quit_echo(line):
    return bool(QUIT_ECHO_RX.match(line))


# ------------------------------------------------------------------------------------------------
# Decisions - pure, unit-tested
# ------------------------------------------------------------------------------------------------

def next_slot(after, hhmm, tz):
    """Epoch of the first local hh:mm at or after `after`, or None when switched off."""
    if hhmm is None:
        return None
    base = datetime.fromtimestamp(after, tz) if tz else datetime.fromtimestamp(after)
    for add in range(-1, 3):
        day = (base + timedelta(days=add)).date()
        if tz:
            candidate = datetime(day.year, day.month, day.day, hhmm[0], hhmm[1], tzinfo=tz)
        else:
            candidate = datetime(day.year, day.month, day.day, hhmm[0], hhmm[1])
        if candidate.timestamp() >= after:
            return candidate.timestamp()
    return None


def local_day(epoch, tz):
    return (datetime.fromtimestamp(epoch, tz) if tz else datetime.fromtimestamp(epoch)).strftime("%Y-%m-%d")


def daily_due(now, hhmm, tz, done_day):
    """Is the daily restart due now? From today's slot until DAILY_WINDOW after it, unless done today."""
    if hhmm is None:
        return False
    slot = next_slot(now - DAILY_WINDOW, hhmm, tz)
    return slot is not None and slot <= now < slot + DAILY_WINDOW and done_day != local_day(slot, tz)


def freeze_confirmed(now, signature_at, silent_since, engine_alive, other_level_since, grace):
    """Kill only on all of: the error-map signature, continuous A2S silence that began no later than shortly after
    it and has lasted `grace`, a live engine process, and no other map load started since the signature."""
    if signature_at is None or silent_since is None or not engine_alive or other_level_since:
        return False
    if silent_since > signature_at + 30:   # silence must cover the freeze, not start long after it
        return False
    return now - signature_at >= grace and now - silent_since >= grace


def silence_confirmed(now, a2s_since, a2s_fails, rcon_since, rcon_fails, armed, engine_alive, going_down, grace,
                      polls=SILENT_POLLS):
    """Kill on silence alone only on all of: an armed engine (this process answered A2S since it started, and no quit
    or restart has been seen since), still running, not being taken down by the watcher itself, and A2S AND RCON
    both failing on every poll for `grace` - at least `polls` polls each, so a lost packet is never a streak."""
    if not armed or not engine_alive or going_down or a2s_since is None or rcon_since is None:
        return False
    if a2s_fails < polls or rcon_fails < polls:
        return False
    return now - a2s_since >= grace and now - rcon_since >= grace


PLANNED_KINDS = ("update", "daily", "prefetch")


def planned_block(state, now):
    """None, or (alert, why) for a planned (update, daily or prefetch) restart. The cap counts update and daily
    restarts only - the prefetch restart is once a night by construction (prefetch_restart_day) and must never cost an
    update its slot - but every planned kind keeps the 10-minute floor."""
    recent = [e for e in state["restarts"] if 0 <= now - e[0] < WINDOW and e[1] in ("update", "daily")]
    if len(recent) >= PLANNED_MAX_PER_DAY:
        return True, "%d planned restarts in the last 24 h, the cap is %d" % (len(recent), PLANNED_MAX_PER_DAY)
    last = max([e[0] for e in state["restarts"] if e[1] in PLANNED_KINDS] or [0])
    if last > now:
        return True, "the last restart is in the future (the clock moved backwards)"
    if last and now - last < PLANNED_MIN_GAP:
        return False, "the last planned restart was at %s, the floor is %d s" % (at_clock(last), PLANNED_MIN_GAP)
    return None


def workshop_block(state, now):
    """None, or why a download restart may not happen now: at most one per WORKSHOP_RESTART_GAP and
    WORKSHOP_RESTART_MAX per rolling 24 h (a persistent Steam problem must not become a restart loop and a mail storm),
    and never inside PLANNED_MIN_GAP of a planned restart."""
    mine = [e[0] for e in state["restarts"] if e[1] == "workshop" and 0 <= now - e[0] < WINDOW]
    if len(mine) >= WORKSHOP_RESTART_MAX:
        return "%d download restart(s) in the last 24 h, the cap is %d" % (len(mine), WORKSHOP_RESTART_MAX)
    if mine and now - max(mine) < WORKSHOP_RESTART_GAP:
        return "the last download restart was at %s, the floor is %s" % (at_clock(max(mine)), hms(WORKSHOP_RESTART_GAP))
    last = max([e[0] for e in state["restarts"] if e[1] in PLANNED_KINDS] or [0])
    if last and 0 <= now - last < PLANNED_MIN_GAP:
        return "a planned restart was at %s, the floor is %d s" % (at_clock(last), PLANNED_MIN_GAP)
    return None


def freeze_block(state, now):
    if state["freeze_tripped"]:
        return True, "the freeze breaker is latched (%d kills did not bring the server back)" % state["freeze_misses"]
    kills = [e[0] for e in state["restarts"] if e[1] == "freeze"]
    recent = [k for k in kills if 0 <= now - k < WINDOW]
    if len(recent) >= FREEZE_MAX_PER_DAY:
        return True, "%d freeze kills in the last 24 h, the cap is %d" % (len(recent), FREEZE_MAX_PER_DAY)
    if kills and max(kills) > now:
        return True, "the last kill is in the future (the clock moved backwards)"
    if kills and now - max(kills) < FREEZE_MIN_GAP:
        return False, "the last freeze kill was at %s, the floor is %d s" % (at_clock(max(kills)), FREEZE_MIN_GAP)
    return None


def back_in(durations):
    """Seconds to announce: slowest of the last five measured restarts + 5 s, in 5 s steps, 20..300."""
    if not durations:
        return DEFAULT_BACK_IN
    return int(min(300, max(20, math.ceil((max(durations[-5:]) + 5) / 5.0) * 5)))


def say_back_in(seconds):
    return "about %d seconds" % seconds if seconds <= 90 else "about %d minutes" % int(math.ceil(seconds / 60.0))


def chat_safe(text, limit=120):
    """What may be handed to RCON `say`: one line, no quotes or semicolons, at most `limit` characters."""
    text = "".join("'" if ch == '"' else "," if ch == ";" else " " if ch < " " else ch for ch in text).strip()
    return text if len(text) <= limit else text[:limit - 3] + "..."


def texts(kind, back):
    """(warning when it becomes due, line at the match end) for a planned restart."""
    if kind == "workshop":
        return ("", "Workshop downloads are broken in this server process - restarting it while nobody is on (update-watch). "
                    "Back in %s." % say_back_in(back))
    if kind == "prefetch":
        return ("", "Nightly restart before the Workshop prefetch, while nobody is on (update-watch). Back in %s."
                    % say_back_in(back))
    if kind == "update":
        return ("A CS2 update is out: the server restarts at the end of this match to install it.",
                "Restarting to install the CS2 update - back in a minute or two. Restart CS2 to update, then reconnect.")
    return ("Daily server restart at the end of this match - back %s later." % say_back_in(back),
            "Daily server restart now - back in %s. Reconnect then (console: retry)." % say_back_in(back))


# ------------------------------------------------------------------------------------------------
# Persisted state
# ------------------------------------------------------------------------------------------------

RESTART_KINDS = ("update", "daily", "freeze", "workshop", "prefetch")


def default_state():
    return {"schema": 2, "restarts": [], "done_day": "", "durations": [], "pending": None, "update_misses": 0,
            "update_retry_after": 0, "update_tripped": 0, "update_tripped_for": 0, "freeze_misses": 0,
            "freeze_tripped": 0, "prefetch_day": "", "prefetch_restart_day": ""}


def is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def validate_state(loaded):
    """(state, None) or (defaults, reason). A file that exists but does not type-check is a HARD FAULT, never a
    reason to start counting from zero - that would hand a corrupting bug a fresh budget on every boot."""
    state = default_state()
    if not isinstance(loaded, dict):
        return state, "not an object"
    if loaded.get("schema") not in (None, 2):
        return state, "unknown schema %r (delete the file to start over)" % loaded.get("schema")
    for key in ("update_misses", "update_retry_after", "update_tripped", "update_tripped_for", "freeze_misses",
                "freeze_tripped"):
        value = loaded.get(key, 0)
        if not is_int(value) or value < 0:
            return default_state(), "bad %s (%r)" % (key, value)
        state[key] = value
    day = loaded.get("done_day", "")
    if not isinstance(day, str) or len(day) > 10:
        return default_state(), "bad done_day (%r)" % (day,)
    state["done_day"] = day
    for key in ("prefetch_day", "prefetch_restart_day"):
        value = loaded.get(key, "")
        if not isinstance(value, str) or len(value) > 10:
            return default_state(), "bad %s (%r)" % (key, value)
        state[key] = value
    restarts = loaded.get("restarts", [])
    if not isinstance(restarts, list) or len(restarts) > 1000:
        return default_state(), "bad restarts list"
    for entry in restarts:
        if not (isinstance(entry, list) and len(entry) == 2 and is_int(entry[0]) and entry[0] >= 0
                and entry[1] in RESTART_KINDS):
            return default_state(), "bad restarts entry (%r)" % (entry,)
    state["restarts"] = [[e[0], e[1]] for e in restarts]
    durations = loaded.get("durations", [])
    if not isinstance(durations, list) or not all(is_int(d) and 0 < d < 3600 for d in durations):
        return default_state(), "bad durations (%r)" % (durations,)
    state["durations"] = durations[-5:]
    pending = loaded.get("pending")
    if pending is not None:
        if not (isinstance(pending, dict) and pending.get("kind") in RESTART_KINDS
                and is_int(pending.get("at")) and pending["at"] >= 0
                and (pending.get("from") is None or (isinstance(pending.get("from"), str) and len(pending["from"]) <= 32))
                and (pending.get("required") is None or (is_int(pending["required"]) and pending["required"] > 0))):
            return default_state(), "bad pending record (%r)" % (pending,)
        state["pending"] = {"kind": pending["kind"], "at": pending["at"], "from": pending.get("from"),
                            "required": pending.get("required")}
    return state, None


def load_state():
    if not os.path.exists(STATE_FILE):
        return default_state(), None
    try:
        with open(STATE_FILE, "r") as handle:
            loaded = json.load(handle)
    except (OSError, ValueError) as error:
        return default_state(), "%s is unreadable (%s)" % (STATE_FILE, error)
    state, fault = validate_state(loaded)
    return state, ("%s: %s" % (STATE_FILE, fault) if fault else None)


def save_state(state):
    """Atomic and durable: the counters must be on disk before a quit or a kill takes the container down."""
    temporary = STATE_FILE + ".tmp"
    with open(temporary, "w") as handle:
        json.dump(state, handle, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, STATE_FILE)
    directory = os.open(STATE_DIR, os.O_RDONLY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def persistence_selftest():
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        probe = os.path.join(STATE_DIR, ".writable")
        with open(probe, "w") as handle:
            handle.write("ok")
            handle.flush()
            os.fsync(handle.fileno())
        with open(probe, "r") as handle:
            readback = handle.read()
        os.unlink(probe)
        return None if readback == "ok" else "wrote 'ok', read back %r" % readback
    except OSError as error:
        return "%s: %s" % (type(error).__name__, error)


# ------------------------------------------------------------------------------------------------
# Local probes: A2S, RCON, the engine process
# ------------------------------------------------------------------------------------------------

def a2s_parse(data):
    """A2S_INFO reply -> {"version", "humans", "bots", "map"}, or None if it is not one."""
    try:
        if data[4:5] != b"I":
            return None
        offset = 6
        fields = []
        for _ in range(4):  # name, map, folder, game
            end = data.index(b"\x00", offset)
            fields.append(data[offset:end].decode("utf-8", "replace"))
            offset = end + 1
        offset += 2  # appid
        players, bots = data[offset], data[offset + 2]
        offset += 3 + 4  # player counts, then server type / environment / visibility / vac
        end = data.index(b"\x00", offset)
        version = data[offset:end].decode("utf-8", "replace")
    except (IndexError, ValueError):
        return None
    return {"version": version, "humans": max(0, players - bots), "bots": bots, "map": fields[1]}


def a2s_probe():
    """One A2S_INFO to 127.0.0.1. None means 'no answer' - which is never read as 'empty'."""
    query = b"\xff\xff\xff\xffTSource Engine Query\x00"
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.settimeout(A2S_TIMEOUT)
        sock.sendto(query, ("127.0.0.1", GAME_PORT))
        data = sock.recv(4096)
        if data[4:5] == b"A":  # S2C_CHALLENGE: repeat with the challenge appended
            sock.sendto(query + data[5:9], ("127.0.0.1", GAME_PORT))
            data = sock.recv(4096)
        return a2s_parse(data)
    except OSError:
        return None
    finally:
        sock.close()


def rcon_send(sock, request_id, packet_type, body):
    payload = body.encode("utf-8") + b"\x00\x00"
    sock.sendall(struct.pack("<iii", 8 + len(payload), request_id, packet_type) + payload)


def rcon_recv(sock):
    header = b""
    while len(header) < 4:
        chunk = sock.recv(4 - len(header))
        if not chunk:
            raise OSError("RCON closed while reading the length")
        header += chunk
    size = struct.unpack("<i", header)[0]
    if not 10 <= size <= 65536:
        raise OSError("implausible RCON packet size %d" % size)
    buffer = b""
    while len(buffer) < size:
        chunk = sock.recv(size - len(buffer))
        if not chunk:
            raise OSError("RCON closed mid-packet")
        buffer += chunk
    request_id, packet_type = struct.unpack("<ii", buffer[:8])
    return request_id, packet_type, buffer[8:-2].decode("utf-8", "replace")


def rcon(commands, expect_reply=True):
    """Run commands over one authenticated RCON session to 127.0.0.1. Returns the list of replies (None for a
    command whose reply did not arrive), or None if the session could not be opened."""
    if not RCON_PASSWORD:
        return None
    try:
        sock = socket.create_connection(("127.0.0.1", GAME_PORT), timeout=RCON_TIMEOUT)
    except OSError as error:
        log_once("rcon", "WARNING: RCON connect failed (%s)" % error)
        return None
    replies = []
    try:
        sock.settimeout(RCON_TIMEOUT)
        rcon_send(sock, 1, 3, RCON_PASSWORD)  # SERVERDATA_AUTH
        while True:
            request_id, packet_type, _ = rcon_recv(sock)
            if packet_type == 2:  # SERVERDATA_AUTH_RESPONSE
                break
        if request_id == -1:
            log_once("rcon", "WARNING: RCON authentication was refused")
            return None
        for index, command in enumerate(commands):
            rcon_send(sock, 10 + index, 2, command)  # SERVERDATA_EXECCOMMAND
            if not expect_reply:
                replies.append(None)
                continue
            try:
                replies.append(rcon_recv(sock)[2])
            except OSError:
                replies.append(None)
        return replies
    except OSError as error:
        log_once("rcon", "WARNING: RCON failed (%s)" % error)
        return replies or None
    finally:
        try:
            sock.close()
        except OSError:
            pass


PLAYERS_RX = re.compile(r"^\s*players\s*:\s*([0-9]+)\s+humans,\s*([0-9]+)\s+bots", re.M)  # CS2 1.41 status


def rcon_humans():
    replies = rcon(["status"])
    if not replies or replies[0] is None:
        return None
    found = PLAYERS_RX.search(replies[0])
    return int(found.group(1)) if found else None


def rcon_alive():
    """Does the engine answer RCON at all? True on any auth reply - accepted or refused, both come from a running engine
    loop. False on no listener, no reply within RCON_PROBE_TIMEOUT, or a broken stream. It authenticates and sends no
    command, so nothing is echoed into console.log or the log stream the bridge reads."""
    try:
        sock = socket.create_connection(("127.0.0.1", GAME_PORT), timeout=RCON_PROBE_TIMEOUT)
    except OSError:
        return False
    try:
        sock.settimeout(RCON_PROBE_TIMEOUT)
        rcon_send(sock, 1, 3, RCON_PASSWORD)  # SERVERDATA_AUTH
        rcon_recv(sock)                       # any packet back (the auth response, or the empty one before it) is a live loop
        return True
    except OSError:
        return False
    finally:
        try:
            sock.close()
        except OSError:
            pass


def say(text):
    line = chat_safe(text)
    ok = rcon(['say "%s"' % line]) is not None
    log(("chat: %s" % line) if ok else "WARNING: could not say: %s" % line)
    return ok


def notice(text, seconds):
    """A line for everybody. The in-game menu is the server's whole interface, so it goes on the HUD's news line
    (GunGameWelcome's server-only `gg_notice <seconds> <text>`, which answers "gg_notice: shown for N s: ..."); chat gets it only
    where there is no HUD to show it - an empty GUNGAME_HUD_ADDON ("gg_notice: no HUD"), a plugin that does not answer, or no
    RCON reply at all."""
    line = chat_safe(text, 240)
    if not line:
        return True
    replies = rcon(['gg_notice %d "%s"' % (seconds, line)])
    reply = ((replies or [None])[0] or "").strip()
    if reply.startswith("gg_notice: shown"):
        log("hud: %s (%d s)" % (line, seconds))
        return True
    return say(text)


def engine_pid():
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        try:
            with open("/proc/%s/cmdline" % name, "rb") as handle:
                argv0 = handle.read().split(b"\0")[0]
        except OSError:
            continue
        if argv0.endswith(b"linuxsteamrt64/cs2"):
            return int(name)
    return None


def kill_engine(pid, why):
    log("killing the engine (pid %d): %s" % (pid, why))
    for sig, wait in ((signal.SIGTERM, 10), (signal.SIGKILL, 5)):
        try:
            os.kill(pid, sig)
        except OSError:
            return
        for _ in range(wait * 2):
            if engine_pid() != pid:
                return
            time.sleep(0.5)


# ------------------------------------------------------------------------------------------------
# console.log
# ------------------------------------------------------------------------------------------------

class Tail:
    def __init__(self, path):
        self.path = path
        self.buffer = b""
        try:
            self.position = os.path.getsize(path)
        except OSError:
            self.position = 0

    def lines(self, limit=1 << 20):
        try:
            size = os.path.getsize(self.path)
        except OSError:
            return []
        if size < self.position:
            self.position, self.buffer = 0, b""
        if size == self.position:
            return []
        with open(self.path, "rb") as handle:
            handle.seek(self.position)
            data = handle.read(min(size - self.position, limit))
        self.position += len(data)
        parts = (self.buffer + data).split(b"\n")
        self.buffer = parts.pop()
        return [p.decode("utf-8", "replace").rstrip("\r") for p in parts]


# ------------------------------------------------------------------------------------------------
# Workshop: the warm-up after every engine start and the nightly prefetch
# ------------------------------------------------------------------------------------------------
# Why: the first workshop download in an engine process makes the Steam client re-request the depot manifest of EVERY
# installed item it has no verified copy of in ~/Steam/depotcache before it fetches the requested one (~250 manifests a
# minute: many minutes for a large collection with an empty depotcache, seconds with it
# full). After that one job, later downloads in the same process are fast. So the watcher spends that job
# right after boot on a throwaway item (the warm-up), and once a night it brings every listed or installed map up to
# date while nobody plays (the prefetch) - both through MultiAddonManager's `mm_download_addon`, which downloads into
# the server's own cache without changing the map. Neither restarts anything; the restarts are jobs 6 and 7.

WARMUP = flag_env("CS2_WARMUP", True)
WARMUP_ITEM = (os.environ.get("CS2_WARMUP_ITEM") or "").strip()
if WARMUP_ITEM and not WARMUP_ITEM.isdigit():
    BOOT_WARNINGS.append("CS2_WARMUP_ITEM=%r is not a workshop id, warm-up off" % WARMUP_ITEM)
    WARMUP_ITEM = ""
WARMUP_DELAY = int_env("CS2_WARMUP_DELAY_SECONDS", 60, 0, 3600)  # after the engine first answers A2S (start roll first)
WARMUP_TIMEOUT = 3600
WARMUP_RETRY = 600            # a warm-up whose item did not arrive is tried once more after this
WORKSHOP_RESTART = flag_env("CS2_WORKSHOP_RESTART", True)  # restart a process whose downloads are broken (only at 0 humans)
WORKSHOP_RESTART_GAP = 7200    # at most one such restart per 2 h...
WORKSHOP_RESTART_MAX = 3       # ...and 3 per rolling 24 h; past that, warn only
PREFETCH_FAIL_STREAK = 3      # this many failed downloads in a row stop the night's prefetch (the download path is broken)
NOOP_AFTER = 120                # no "Download item <id> requested" this long after the command: nothing to download
PREFETCH_RESTART = flag_env("CS2_PREFETCH_RESTART", True)  # a fresh engine process for the nightly prefetch
PREFETCH_RESTART_LEAD = 300     # ...this long before CS2_PREFETCH_AT (03:25 for 03:30), only while nobody is on
PREFETCH_FRESH = 1200           # an engine process younger than this is fresh already: no restart for it
SNAPSHOT_KEEP = 10              # Steam log snapshots kept in update-watch/steamlogs/ (one per broken process)
SNAPSHOT_MAX_FILE = 32 << 20    # a log bigger than this is copied from its tail


STUCK_RX = re.compile(r"no connection|no download source", re.I)


def stuck_result(*texts):
    """Does a failed download say the process has no download source left - for good? Its result
    ('No Connection') or the job's 'Update canceled:' line in workshop_log: 'Failed downloading 306 manifests (No
    connection)' (the source pool ran out during the manifest re-check), '... while downloading chunk ... "No Download
    Source"' (it ran out during the download), '... while starting download (No connection to content servers)' (it
    was empty already)."""
    return any(STUCK_RX.search(text or "") for text in texts)


def parse_hhmm_or_none(raw, name):
    if raw is None or raw.strip() == "" or raw.strip().lower() in ("off", "none", "-1"):
        return None
    found = re.match(r"^([01]?\d|2[0-3]):([0-5]\d)$", raw.strip())
    if not found:
        BOOT_WARNINGS.append("%s=%r is not HH:MM or off, prefetch off" % (name, raw))
        return None
    return int(found.group(1)), int(found.group(2))


PREFETCH_AT = parse_hhmm_or_none(os.environ.get("CS2_PREFETCH_AT"), "CS2_PREFETCH_AT")  # e.g. "03:30"; unset = off
PREFETCH_SPAN = int_env("CS2_PREFETCH_MINUTES", 195, 10, 1200) * 60          # 03:30 + 195 min = 06:45, before 07:00
PREFETCH_ITEM_TIMEOUT = 900
PREFETCH_DETAIL_ERRORS = 40     # failed details requests (the API answers 502 in streaks) before planning with what came back
PREFETCH_DETAIL_RETRY = 30      # seconds before a failed batch is asked again
WORKSHOP_LOG = os.environ.get("CS2_WORKSHOP_LOG") or "/home/steam/Steam/logs/workshop_log.txt"
STEAM_LOGS = os.path.dirname(WORKSHOP_LOG)     # the Steam client's logs: content_log.txt names every server it tried
DEPOTCACHE = os.environ.get("CS2_DEPOTCACHE") or "/home/steam/Steam/depotcache"   # the manifests it has verified
SNAPSHOT_DIR = os.path.join(STATE_DIR, "steamlogs")
WORKSHOP_DIR = os.path.join(STEAMAPPDIR, "game", "bin", "linuxsteamrt64", "steamapps", "workshop")
WORKSHOP_ACF = os.path.join(WORKSHOP_DIR, "appworkshop_730.acf")
BRIDGE_DATA = os.environ.get("CS2_BRIDGE_DATA") or "/home/steam/bridge-data"   # the bridge's /data, read-only
DETAILS_URL = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
DETAILS_BATCH = 100

KV_TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|([{}])')
DOWNLOAD_RX = re.compile(r"Download item (\d+) (?:(requested) by app|result : (.*?))\s*$")
CANCELED_RX = re.compile(r"\] Update canceled: (.*?)\s*$")


def kv_parse(text):
    """Valve KeyValues text (an .acf) -> nested [(key, value | list)], order kept. Raises ValueError if malformed."""
    tokens = [(m.group(1), m.group(2)) for m in KV_TOKEN.finditer(text)]
    pos = [0]

    def block():
        out = []
        while pos[0] < len(tokens):
            text_, brace = tokens[pos[0]]
            if brace == "}":
                pos[0] += 1
                return out
            if brace == "{" or pos[0] + 1 >= len(tokens):
                raise ValueError("malformed KeyValues at token %d" % pos[0])
            text2, brace2 = tokens[pos[0] + 1]
            pos[0] += 2
            if brace2 == "{":
                out.append((text_, block()))
            elif brace2 == "}":
                raise ValueError("key %r without a value" % text_)
            else:
                out.append((text_, text2))
        return out
    return block()


def kv_dump(kv, depth=0):
    tab, lines = "\t" * depth, []
    for key, value in kv:
        if isinstance(value, list):
            lines += ['%s"%s"' % (tab, key), "%s{" % tab]
            body = kv_dump(value, depth + 1)
            if body:
                lines.append(body)
            lines.append("%s}" % tab)
        else:
            lines.append('%s"%s"\t\t"%s"' % (tab, key, value))
    return "\n".join(lines)


def kv_get(kv, key):
    for k, v in kv or []:
        if k == key:
            return v
    return None


def acf_installed(text):
    """{workshop id: installed timeupdated} from an appworkshop_730.acf."""
    out = {}
    for wid, body in kv_get(kv_get(kv_parse(text), "AppWorkshop"), "WorkshopItemsInstalled") or []:
        if isinstance(body, list):
            stamp = kv_get(body, "timeupdated")
            out[wid] = int(stamp) if isinstance(stamp, str) and stamp.isdigit() else 0
    return out


def acf_forget(text, wid):
    """The acf text without item `wid` (its WorkshopItemsInstalled and WorkshopItemDetails entries), the text itself if
    it holds neither, or None when the file would not come back byte-identical through parse/dump - never rewrite a
    file this parser does not fully understand."""
    kv = kv_parse(text)
    if kv_dump(kv) + "\n" != text:
        return None
    workshop = kv_get(kv, "AppWorkshop")
    if not isinstance(workshop, list):
        return None
    changed = False
    for section in ("WorkshopItemsInstalled", "WorkshopItemDetails"):
        entries = kv_get(workshop, section)
        if isinstance(entries, list):
            kept = [(k, v) for k, v in entries if k != wid]
            changed = changed or len(kept) != len(entries)
            entries[:] = kept
    return kv_dump(kv) + "\n" if changed else text


ACF_BACKUPS_KEEP = 5            # appworkshop_730.acf.bak-<local time>-<why> beside it: the newest this many of each reason


def acf_edit(text, add=None, drop=()):
    """The acf text with items added or replaced (add: {id: (installed, details)}, each a [(key, value)] list, [] for none) and items
    dropped (both sections), SizeOnDisk the sum of the installed sizes again, every section's entries in id order. None when the text
    does not come back byte-identical through parse/dump or has no AppWorkshop block: never rewrite a file this parser does not fully
    understand (the rule acf_forget follows)."""
    kv = kv_parse(text)
    if kv_dump(kv) + "\n" != text:
        return None
    workshop = kv_get(kv, "AppWorkshop")
    if not isinstance(workshop, list):
        return None
    add = add or {}
    gone = set(drop) | set(add)
    for section, part in (("WorkshopItemsInstalled", 0), ("WorkshopItemDetails", 1)):
        entries = kv_get(workshop, section)
        if not isinstance(entries, list):
            entries = []
            workshop.append((section, entries))
        kept = [(k, v) for k, v in entries if k not in gone] + [(wid, pair[part]) for wid, pair in add.items() if pair[part]]
        entries[:] = sorted(kept, key=lambda e: (0, int(e[0])) if e[0].isdigit() else (1, 0))
    total = 0
    for _wid, body in kv_get(workshop, "WorkshopItemsInstalled"):
        size = kv_get(body, "size") if isinstance(body, list) else None
        total += int(size) if isinstance(size, str) and size.isdigit() else 0
    for index, (key, value) in enumerate(workshop):
        if key == "SizeOnDisk" and not isinstance(value, list):
            workshop[index] = (key, str(total))
    return kv_dump(kv) + "\n"


def acf_write(text, why):
    """Replace the workshop acf with `text`: first a copy of the old one beside it (appworkshop_730.acf.bak-<local time>-<why>; the newest
    ACF_BACKUPS_KEEP of each reason are kept), then a temporary file with the old file's mode and an atomic rename. Returns the backup's
    path; raises OSError, and then the old file is still in place."""
    folder = os.path.dirname(WORKSHOP_ACF)
    stamp = datetime.now(TZ).strftime("%Y%m%d-%H%M%S") if TZ else time.strftime("%Y%m%d-%H%M%S")
    backup = "%s.bak-%s-%s" % (WORKSHOP_ACF, stamp, why)
    shutil.copy2(WORKSHOP_ACF, backup)
    mode = os.stat(WORKSHOP_ACF).st_mode & 0o7777
    temporary = "%s.%s-tmp" % (WORKSHOP_ACF, why)
    with open(temporary, "w", encoding="utf-8") as handle:
        handle.write(text)
        handle.flush()
        os.fsync(handle.fileno())
    os.chmod(temporary, mode)
    os.replace(temporary, WORKSHOP_ACF)
    prefix = os.path.basename(WORKSHOP_ACF) + ".bak-"
    for old in sorted(n for n in os.listdir(folder) if n.startswith(prefix) and n.endswith("-" + why))[:-ACF_BACKUPS_KEEP]:
        try:
            os.remove(os.path.join(folder, old))
        except OSError:
            pass
    return backup


def acf_sizes(text):
    """{id: installed size in bytes} from an acf; {} when it does not parse."""
    out = {}
    try:
        for wid, body in kv_get(kv_get(kv_parse(text), "AppWorkshop"), "WorkshopItemsInstalled") or []:
            size = kv_get(body, "size") if isinstance(body, list) else None
            if isinstance(size, str) and size.isdigit():
                out[wid] = int(size)
    except (ValueError, TypeError):
        pass
    return out


def download_events(lines):
    """[(id, "requested" | "result", text)] from new workshop_log.txt lines, plus ("", "canceled", why) for a job's
    'Update canceled:' line (it comes right before the result of the item that job was for)."""
    out = []
    for line in lines:
        found = DOWNLOAD_RX.search(line)
        if found:
            out.append((found.group(1), "requested" if found.group(2) else "result", found.group(3) or ""))
            continue
        found = CANCELED_RX.search(line)
        if found:
            out.append(("", "canceled", found.group(1)))
    return out


def details_parse(raw):
    """{id: (result, time_updated)} from a GetPublishedFileDetails reply (result 1 = available)."""
    out = {}
    for item in json.loads(raw).get("response", {}).get("publishedfiledetails", []) or []:
        wid = str(item.get("publishedfileid", ""))
        if wid.isdigit():
            out[wid] = (int(item.get("result") or 0), int(item.get("time_updated") or 0))
    return out


def prefetch_plan(listed, installed, folders, details, skip):
    """(missing, outdated). missing: listed ids not installed, or installed without a folder. outdated: listed or
    installed ids whose Workshop time_updated is newer than the installed copy. Listed ids first in list order, then
    the rest of the installed ones by id. Only ids the Workshop returns as available (result 1) are queued; `skip`
    (packs, blocked, removed, the warm-up item) never is."""
    listed_set = set(listed)
    order = list(dict.fromkeys(list(listed) + sorted(set(installed) - listed_set)))
    missing, outdated = [], []
    for wid in order:
        if wid in skip:
            continue
        result, updated = details.get(wid, (0, 0))
        if result != 1:
            continue
        if wid in listed_set and (wid not in installed or wid not in folders):
            missing.append(wid)
        elif wid in installed and wid in folders and updated > installed[wid] > 0:
            outdated.append(wid)
    return missing, outdated


def read_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return default


def prefetch_lists():
    """(the ids the prefetch looks after, ids never to fetch, what was found) from the bridge's data volume (read-only). Since
    2026-11-03 the keep set and the queues (THE KEEP SET): what players queued, the random queues' entries in the order they are
    played, the upvoted maps, each mode's top-rated, the own maps and the HUD - not every listed map any more."""
    keep = keep_set(keep_inputs(BRIDGE_DATA), time.time(), (os.environ.get("GUNGAME_HUD_ADDON") or "").strip())
    listed = list(keep["requested"])
    rounds = max([len(ids) for ids in keep["queues"].values()] or [0])
    for i in range(rounds):
        listed += [ids[i] for ids in keep["queues"].values() if i < len(ids)]
    listed += keep["up"]
    for mode, _name in KEEP_MODES:
        listed += keep["top"].get(mode) or []
    listed = list(dict.fromkeys(listed + keep["own"] + keep["hud"]))
    broken = read_json(os.path.join(BRIDGE_DATA, "broken.json"), {})
    skip = set()
    for section in ("packs", "blocked"):
        for entry in (broken.get(section) if isinstance(broken, dict) else None) or []:
            if isinstance(entry, dict) and str(entry.get("id", "")).isdigit():
                skip.add(str(entry["id"]))
    removed = (read_json(os.path.join(BRIDGE_DATA, "removed.json"), {}) or {}).get("verdicts") or {}
    skip |= set(wid for wid, verdict in removed.items() if verdict != "ok")
    if WARMUP_ITEM:
        skip.add(WARMUP_ITEM)
    return listed, skip, "the keep set and the queues: %d maps, %d never fetched (packs, blocked, removed)%s" % (
        len(listed), len(skip), ("; the keep set's problems: " + "; ".join(keep["problems"])) if keep["problems"] else "")


def fetch_details(ids):
    form = [("itemcount", str(len(ids)))] + [("publishedfileids[%d]" % i, w) for i, w in enumerate(ids)]
    body = urllib.parse.urlencode(form)
    request = urllib.request.Request(DETAILS_URL, data=body.encode(), headers={
        "User-Agent": USER_AGENT, "Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT) as response:
        return details_parse(response.read())


def warmup_reset():
    """`update-watch.py --warmup-reset`, run by pre.sh BEFORE the engine starts (nothing holds the acf then): forget
    the warm-up item - its folder and its two acf entries - so the watcher's first download in the new process is a
    real one. Exit 0 also when there is nothing to do; any doubt leaves the files alone."""
    if not (WARMUP and WARMUP_ITEM):
        return 0
    folder = os.path.join(WORKSHOP_DIR, "content", "730", WARMUP_ITEM)
    try:
        with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
            text = handle.read()
        new = acf_forget(text, WARMUP_ITEM)
    except (OSError, ValueError) as error:
        log("warm-up reset: %s not changed (%s)" % (WORKSHOP_ACF, error))
        return 0
    if new is None:
        log("warm-up reset: %s does not round-trip through the parser - left alone" % WORKSHOP_ACF)
        return 0
    if new != text:
        try:
            acf_write(new, "warmup")
        except OSError as error:
            log("warm-up reset: %s not changed (%s)" % (WORKSHOP_ACF, error))
            return 0
    if os.path.islink(folder):
        os.unlink(folder)
    elif os.path.isdir(folder):
        shutil.rmtree(folder, ignore_errors=True)
    log("warm-up reset: workshop item %s forgotten (%s), so the first download after this boot is a real one"
        % (WARMUP_ITEM, "acf entries and folder" if new != text else "it was not installed"))
    return 0


MAPS_DIR = os.path.join(STEAMAPPDIR, "maps")
MAPS_LAYOUT = (os.environ.get("CS2_MAPS_LAYOUT") or "folders").strip().lower()
MAP_TYPES = ("armsrace", "deathmatch", "wingman", "own", "hud", "other")
SLUG_RX = re.compile(r"[^a-z0-9_.+-]+")


def map_slug(name):
    """What a folder may say of a title: lowercase ASCII, at most 40 characters; empty if nothing is left."""
    return SLUG_RX.sub("_", (name or "").lower()).strip("_.-+")[:40].rstrip("_.-+")


def map_types():
    """{id: (type, title)} from the bridge's lists: the HUD item, then our own maps, then Arms Race, Deathmatch, Wingman. Not listed: other."""
    out = {}
    hud = (os.environ.get("GUNGAME_HUD_ADDON") or "").strip()
    if hud:
        out[hud] = ("hud", "hud")
    for entry in (read_json(os.path.join(BRIDGE_DATA, "ours.json"), {}) or {}).get("maps") or []:
        if isinstance(entry, dict) and entry.get("id"):
            out.setdefault(str(entry["id"]), ("own", str(entry.get("name") or "")))
    for kind, fname in (("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json")):
        for entry in read_json(os.path.join(BRIDGE_DATA, fname), []) or []:
            if isinstance(entry, dict) and entry.get("id"):
                out.setdefault(str(entry["id"]), (kind, str(entry.get("name") or "")))
    return out


def organize(content=None, maps_dir=None):
    """--organize: every map of content/730 in its type's folder under maps/, content/730/<id> a relative link to it (see THE MAPS
    FOLDER above). Only ever renames and links, with no engine running; a real folder only goes when a newer copy of the same id has
    taken its place. Returns a line for the log. Never raises: the engine's start must not depend on it."""
    content = content or os.path.join(WORKSHOP_DIR, "content", "730")
    maps_dir = maps_dir or MAPS_DIR
    if MAPS_LAYOUT != "folders":
        return "maps folder: off (CS2_MAPS_LAYOUT=%s)" % MAPS_LAYOUT
    counts = {"moved": 0, "renamed": 0, "linked": 0, "replaced": 0, "broken": 0, "errors": 0}
    try:
        types = map_types()
        if not any(kind == "armsrace" for kind, _ in types.values()):
            return "maps folder: the bridge's lists are not readable - left as it is"
        for kind in MAP_TYPES:
            os.makedirs(os.path.join(maps_dir, kind), exist_ok=True)
        where = {}      # id -> the folders under maps/ that hold it
        for kind in MAP_TYPES:
            for name in os.listdir(os.path.join(maps_dir, kind)):
                wid = name.split("-", 1)[0]
                if wid.isdigit():
                    where.setdefault(wid, []).append(os.path.join(maps_dir, kind, name))
        rel = os.path.relpath(maps_dir, content)
        installed = set(os.listdir(content))
        for wid in sorted(installed | set(where)):
            if not wid.isdigit() or wid == WARMUP_ITEM:
                continue
            try:
                path = os.path.join(content, wid)
                kind, title = types.get(wid, ("other", ""))
                slug = map_slug(title)
                folder = "%s-%s" % (wid, slug) if slug else wid
                target = os.path.join(maps_dir, kind, folder)
                link = os.path.join(rel, kind, folder)
                if os.path.islink(path):
                    current = os.path.normpath(os.path.join(content, os.readlink(path)))
                    if not os.path.isdir(current):
                        os.unlink(path)     # its folder is gone: Steam or the prefetch fetches it again when it is wanted
                        counts["broken"] += 1
                        installed.discard(wid)
                        if not where.get(wid):
                            continue
                    elif current == target and os.readlink(path) == link:
                        for extra in where.get(wid, []):
                            if extra != target:
                                shutil.rmtree(extra, ignore_errors=True)
                        continue
                    else:
                        os.rename(current, target)
                        os.unlink(path)
                        os.symlink(link, path)
                        counts["renamed"] += 1
                        continue
                if os.path.isdir(path) and not os.path.islink(path):
                    for old in where.get(wid, []):      # what Steam just wrote is the newer copy
                        shutil.rmtree(old, ignore_errors=True)
                        counts["replaced"] += 1
                    os.rename(path, target)
                    os.symlink(link, path)
                    counts["moved"] += 1
                elif not os.path.lexists(path) and where.get(wid):
                    keep = target if target in where[wid] else where[wid][0]   # a folder that lost its link
                    if keep != target:
                        os.rename(keep, target)
                    os.symlink(link, path)
                    counts["linked"] += 1
            except OSError as error:
                counts["errors"] += 1
                log("maps folder: %s: %s" % (wid, error))
        where = {}      # again, now that everything is in its place
        for kind in MAP_TYPES:
            for name in os.listdir(os.path.join(maps_dir, kind)):
                wid = name.split("-", 1)[0]
                if wid.isdigit() and os.path.isdir(os.path.join(maps_dir, kind, name)):
                    where.setdefault(wid, []).append(os.path.join(maps_dir, kind, name))
        extra = keep_and_delete(content, maps_dir, types, where, rel)
        write_maps_index(content, maps_dir, types)
    except Exception as error:  # noqa: BLE001 - never in the way of the engine's start
        return "maps folder: stopped (%s: %s) - the engine starts anyway" % (type(error).__name__, error)
    done = ["%d %s" % (v, k) for k, v in counts.items() if v] + extra
    return "maps folder: %s" % ", ".join(done) if done else "maps folder: in order"


def write_maps_index(content, maps_dir, types):
    """maps/INDEX.tsv: type, id, title, megabytes, for looking around. Sizes from Steam's acf."""
    sizes = {}
    try:
        with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
            for wid, body in kv_get(kv_get(kv_parse(handle.read()), "AppWorkshop"), "WorkshopItemsInstalled") or []:
                if isinstance(body, list) and str(kv_get(body, "size") or "").isdigit():
                    sizes[wid] = int(kv_get(body, "size"))
    except (OSError, ValueError, TypeError):
        pass
    rows = ["type\tid\ttitle\tMB"]
    for wid in sorted(x for x in os.listdir(content) if x.isdigit()):
        kind, title = types.get(wid, ("other", ""))
        rows.append("%s\t%s\t%s\t%d" % (kind, wid, title.replace("\t", " "), sizes.get(wid, 0) // 1000000))
    temporary = os.path.join(maps_dir, ".INDEX.tsv.tmp")
    with open(temporary, "w", encoding="utf-8") as handle:
        handle.write("\n".join(rows) + "\n")
    os.replace(temporary, os.path.join(maps_dir, "INDEX.tsv"))


# ------------------------------------------------------------------------------------------------
# THE KEEP SET (the owner's rules of 2026-11-03). keep_inputs and keep_set are in workshop-sync.py too, word for word:
# test_workshop_sync.py runs both on the same data and fails when they differ.
# ------------------------------------------------------------------------------------------------
KEEP_TOP = 100                    # per mode: the best-rated playable maps that stay
KEEP_RATINGS_MAX_AGE = 3 * 86400  # ratings older than this (the sync fetches them daily): nothing is deleted
KEEP_RATED_SHARE = 0.9            # the ratings must score this share of each mode's playable maps
KEEP_MIN_TOP = 50                 # a mode's top list shorter than this, with more playable maps listed: nothing is deleted
KEEP_MIN_QUEUE = 10               # each random queue must hold this many entries (the bridge keeps 15)
KEEP_MODES = (("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json"))
KEEP_QUEUES = ("armsrace-new", "armsrace-original", "deathmatch", "wingman")
KEEP_FILES = ("maps.json", "maps-deathmatch.json", "maps-wingman.json", "broken.json", "removed.json", "votes.json", "ours.json",
              "ratings.json", "queues.json", "state.json")


def keep_inputs(data_dir):
    """The keep set's inputs: the bridge's data files by name, each None when it is missing or does not parse."""
    out = {}
    for name in KEEP_FILES:
        try:
            with open(os.path.join(data_dir, name), "r", encoding="utf-8") as handle:
                out[name] = json.load(handle)
        except (OSError, ValueError):
            out[name] = None
    return out


def keep_set(inputs, now, hud=""):
    """THE KEEP SET: the Workshop maps the server stores (official maps are part of the game). Its parts:
      up       every map with at least one player's up vote in votes.json - one vote per player and map, up or down, the opposite
               replaces it; too-big and ads votes are down votes - however many down votes it has besides
      top      per mode, the union of three lists of its KEEP_TOP playable maps (on that mode's list; not broken: broken.json's lists,
               removed.json's verdicts, the bridge's crash counts), all from ratings.json, which the sync fetches daily: "rated", the
               best Workshop score (vote_data.score, the Workshop's Top Rated order); "subscribed", the most current subscriptions
               (Most Subscribed); "popular", Steam's trend order over the last 30 days (Most Popular). Ties by list order. Up to 300
               maps a mode (the owner's 35, 2026-11-03)
      own, hud ours.json's maps and the HUD item
      queues   the bridge's random queues and the maps players have queued (queues.json)
    Returns {"up", "top": {mode: [ids]}, "lists": {mode: {"rated", "subscribed", "popular": [ids]}}, "cutoff": {mode: {"score",
    "subscriptions": the 100th's, or None}}, "own", "hud", "queues": {name: [ids]}, "requested", "all": set, "scored", "problems"}.
    problems: why the set is not to be trusted with a deletion - an input missing or unreadable, ratings older than
    KEEP_RATINGS_MAX_AGE, scores or subscriptions for under KEEP_RATED_SHARE of a list, no Most Popular order for a mode, any of a
    mode's three lists under KEEP_MIN_TOP, a queue under KEEP_MIN_QUEUE, no own map, no HUD item. Pure: the same inputs give the
    same set."""
    problems = []

    def need(name, kind):
        value = inputs.get(name)
        if not isinstance(value, kind):
            problems.append("%s is missing or unreadable" % name)
            return kind()
        return value

    def digits(value):
        return str(value) if str(value or "").isdigit() else None

    lists = {}
    for mode, name in KEEP_MODES:
        lists[mode] = list(dict.fromkeys(digits(e.get("id")) for e in need(name, list) if isinstance(e, dict) and digits(e.get("id"))))
    if not lists["armsrace"] and "maps.json is missing or unreadable" not in problems:
        problems.append("maps.json lists no map")
    bad = set()
    broken = need("broken.json", dict)
    for section in ("maps", "packs", "removed", "blocked"):
        for entry in broken.get(section) or []:
            wid = digits(entry.get("id") if isinstance(entry, dict) else entry)
            if wid:
                bad.add(wid)
    bad |= set(str(w) for w, v in (need("removed.json", dict).get("verdicts") or {}).items() if v != "ok")
    bridge = need("state.json", dict)
    bad |= set(str(k) for k, v in (bridge.get("crashes") or {}).items() if isinstance(v, int) and not isinstance(v, bool) and v > 0)
    bad |= set(str(k) for k in (bridge.get("playerCrashes") or {}))
    up = set(str(k) for k, v in need("votes.json", dict).items() if digits(k) and isinstance(v, dict)
             and any(isinstance(x, int) and not isinstance(x, bool) and x > 0 for x in v.values()))
    ratings = need("ratings.json", dict)
    scores, subs = {}, {}
    for wid, value in (ratings.get("scores") or {}).items() if isinstance(ratings.get("scores"), dict) else []:
        score = value[0] if isinstance(value, list) and value else value
        if digits(wid) and isinstance(score, (int, float)) and not isinstance(score, bool):
            scores[str(wid)] = float(score)
        count = value[3] if isinstance(value, list) and len(value) > 3 else None
        if digits(wid) and isinstance(count, int) and not isinstance(count, bool):
            subs[str(wid)] = count
    trend = ratings.get("trend") if isinstance(ratings.get("trend"), dict) else {}
    if ratings:
        fetched = ratings.get("fetched")
        age = now - fetched if isinstance(fetched, (int, float)) and not isinstance(fetched, bool) else None
        if age is None or age > KEEP_RATINGS_MAX_AGE:
            problems.append("ratings.json is %s old (at most %d h)" % ("%d h" % (age // 3600) if age is not None else "of no known age",
                                                                       KEEP_RATINGS_MAX_AGE // 3600))
    top, cutoff, three = {}, {}, {}
    for mode, _name in KEEP_MODES:
        playable = [w for w in lists[mode] if w not in bad]
        place = dict((w, i) for i, w in enumerate(playable))
        rated = [w for w in playable if w in scores]
        counted = [w for w in playable if w in subs]
        for what, have in (("scores", rated), ("subscriptions", counted)):
            if playable and len(have) < KEEP_RATED_SHARE * len(playable):
                problems.append("ratings.json has the %s of %d of the %d playable %s maps (%d%% needed)" % (
                    what, len(have), len(playable), mode, round(KEEP_RATED_SHARE * 100)))
        order = trend.get(mode) if isinstance(trend.get(mode), list) else None
        if order is None:
            problems.append("ratings.json has no Most Popular order for %s" % mode)
        best = {"rated": sorted(rated, key=lambda w: (-scores[w], place[w]))[:KEEP_TOP],
                "subscribed": sorted(counted, key=lambda w: (-subs[w], place[w]))[:KEEP_TOP],
                "popular": [w for w in dict.fromkeys(str(x) for x in order or []) if w in place][:KEEP_TOP]}
        three[mode] = best
        top[mode] = list(dict.fromkeys(best["rated"] + best["subscribed"] + best["popular"]))
        cutoff[mode] = {"score": scores[best["rated"][-1]] if len(best["rated"]) >= KEEP_TOP else None,
                        "subscriptions": subs[best["subscribed"][-1]] if len(best["subscribed"]) >= KEEP_TOP else None}
        for what, ids in best.items():
            if len(ids) < min(KEEP_MIN_TOP, len(playable)):
                problems.append("the %s %s list holds %d maps (%d playable listed)" % (mode, what, len(ids), len(playable)))
    own = set(digits(e.get("id")) for e in need("ours.json", dict).get("maps") or [] if isinstance(e, dict) and digits(e.get("id")))
    if not own:
        problems.append("ours.json names no own map")
    hud_ids = set([str(hud)]) if digits(hud) else set()
    if not hud_ids:
        problems.append("no HUD item is set")
    queues = need("queues.json", dict)
    if queues and queues.get("version") != 1:
        problems.append("queues.json is not version 1")
    random = queues.get("random") if isinstance(queues.get("random"), dict) else {}
    qs = {}
    for name in KEEP_QUEUES:
        entries = [str(x) for x in random.get(name) or []] if isinstance(random.get(name), list) else []
        if len(entries) < KEEP_MIN_QUEUE:
            problems.append("the %s queue holds %d entries (at least %d)" % (name, len(entries), KEEP_MIN_QUEUE))
        qs[name] = [x for x in entries if x.isdigit()]
    requested = [str(x) for x in queues.get("requested") or [] if digits(x)] if isinstance(queues.get("requested"), list) else []
    every = up | own | hud_ids | set(requested)
    for ids in list(top.values()) + list(qs.values()):
        every |= set(ids)
    return {"up": sorted(up), "top": top, "lists": three, "cutoff": cutoff, "own": sorted(own), "hud": sorted(hud_ids), "queues": qs,
            "requested": requested, "all": every, "scored": len(scores), "problems": problems}


def keep_parts(keep):
    """[(part name, ids)] in the order the log and keep.json give them: per mode its three lists, then their union (what stays)."""
    parts = [("up", keep["up"])]
    for mode, _ in KEEP_MODES:
        parts += [("%s %s" % (what, mode), (keep.get("lists") or {}).get(mode, {}).get(what) or []) for what in ("rated", "subscribed", "popular")]
        parts.append(("top " + mode, keep["top"].get(mode) or []))
    parts += [("own", keep["own"]), ("hud", keep["hud"])] + [("queue " + name, keep["queues"].get(name) or []) for name in KEEP_QUEUES]
    return parts + [("requested", keep["requested"]), ("all", sorted(keep["all"]))]


def keep_summary(keep, on_disk, listed_mb):
    """{part: {"maps", "on_disk", "gb_on_disk", "gb"}}: how many, how many the server stores, and their size (the acf's for what is on
    disk, the lists' sizeMb for the rest), in GiB."""
    out = {}
    for part, ids in keep_parts(keep):
        stored = [w for w in ids if w in on_disk]
        disk = sum(on_disk[w] for w in stored)
        rest = sum(listed_mb.get(w, 0) << 20 for w in ids if w not in on_disk)
        out[part] = {"maps": len(ids), "on_disk": len(stored), "gb_on_disk": round(disk / float(1 << 30), 1),
                     "gb": round((disk + rest) / float(1 << 30), 1)}
    return out


def listed_sizes():
    """{id: sizeMb} from the bridge's three lists."""
    out = {}
    for _mode, name in KEEP_MODES:
        for entry in read_json(os.path.join(BRIDGE_DATA, name), []) or []:
            if isinstance(entry, dict) and str(entry.get("id") or "").isdigit():
                out.setdefault(str(entry["id"]), int(entry.get("sizeMb") or 0))
    return out


# ------------------------------------------------------------------------------------------------
# THE HAND-OVER (the sync's maps for the server) and THE DELETION (what is not in the keep set), both in --organize
# ------------------------------------------------------------------------------------------------
HANDOVER_DIR = os.environ.get("CS2_HANDOVER_DIR") or os.path.join(STEAMAPPDIR, "workshop-sync", "handover")
MAPS_DELETE_DEFAULT = "on"       # "dryrun" for the first production run (2026-11-03: 3,560 maps, 412.9 GB, checked clean), then on
MAPS_DELETE = (os.environ.get("CS2_MAPS_DELETE") or MAPS_DELETE_DEFAULT).strip().lower()   # off | dryrun | on
if MAPS_DELETE not in ("off", "dryrun", "on"):
    BOOT_WARNINGS.append("CS2_MAPS_DELETE=%r is not off, dryrun or on - dryrun" % MAPS_DELETE)
    MAPS_DELETE = "dryrun"
DELETE_TYPES = ("armsrace", "deathmatch", "wingman", "other")   # never own, never hud
KEEP_FILE = os.path.join(STATE_DIR, "keep.json")
DELETE_LISTS_KEEP = 20          # update-watch/deleted-<time>-<dryrun|done>.tsv: the newest this many


def folder_stats(path):
    """(files, bytes) of everything under a folder."""
    files = total = 0
    for root, _dirs, names in os.walk(path):
        for name in names:
            try:
                total += os.path.getsize(os.path.join(root, name))
                files += 1
            except OSError:
                pass
    return files, total


def handover_read(marker, wid):
    """A hand-over's marker, handover/<id>.json (the sync writes it last, so it means complete): (record, None) or (None, why)."""
    try:
        with open(marker, "r", encoding="utf-8") as handle:
            rec = json.load(handle)
    except (OSError, ValueError) as error:
        return None, "its marker does not parse (%s)" % error
    if not isinstance(rec, dict) or rec.get("version") != 1 or str(rec.get("id")) != wid:
        return None, "its marker is not a version-1 hand-over of %s" % wid
    installed, details = rec.get("installed"), rec.get("details")
    if not (isinstance(installed, dict) and all(str(installed.get(k) or "").isdigit() for k in ("size", "timeupdated", "manifest"))):
        return None, "its marker has no complete WorkshopItemsInstalled entry"
    if not (isinstance(details, dict) and all(str(details.get(k) or "").isdigit() for k in ("manifest", "timeupdated"))):
        return None, "its marker has no complete WorkshopItemDetails entry"
    if not (isinstance(rec.get("files"), int) and isinstance(rec.get("bytes"), int) and rec["files"] > 0):
        return None, "its marker does not say what the folder holds"
    return rec, None


def take_in(content, maps_dir, types, where, rel, installed_now):
    """Every complete hand-over in HANDOVER_DIR moved into its type's folder and linked from content/730, like a download of Steam's
    own. Returns ({id: (installed, details)} for the acf, [(id, marker, manifest file)] to finish once the acf is written, counts).
    A hand-over is complete when its marker is there and the folder holds exactly the files and bytes the marker names; one the
    server has an equal or newer copy of is dropped. A marker whose folder was moved already (a take-in cut short) still counts."""
    add, finish = {}, []
    counts = {"taken": 0, "redundant": 0, "incomplete": 0}
    try:
        names = sorted(os.listdir(HANDOVER_DIR))
    except OSError:
        return add, finish, counts
    for name in names:
        if not name.endswith(".json") or not name[:-5].isdigit():
            continue
        wid = name[:-5]
        marker, folder = os.path.join(HANDOVER_DIR, name), os.path.join(HANDOVER_DIR, wid)
        manifest = os.path.join(HANDOVER_DIR, wid + ".manifest")
        rec, why = handover_read(marker, wid)
        if rec is None:
            counts["incomplete"] += 1
            log("hand-over %s: left alone - %s" % (wid, why))
            continue
        kind, title = types.get(wid, ("other", rec.get("title") or ""))
        slug = map_slug(title)
        target = os.path.join(maps_dir, kind, "%s-%s" % (wid, slug) if slug else wid)
        stamp = int(rec["installed"]["timeupdated"])
        try:
            if not os.path.isdir(folder) or os.path.islink(folder):
                moved = [p for p in where.get(wid, []) if folder_stats(p) == (rec["files"], rec["bytes"])]
                if not moved:
                    counts["incomplete"] += 1
                    log("hand-over %s: its marker is there, its folder is not - left alone" % wid)
                    continue
                target = moved[0]                                      # taken in before, cut short before the acf: finish it
            else:
                if folder_stats(folder) != (rec["files"], rec["bytes"]):
                    counts["incomplete"] += 1
                    log("hand-over %s: its folder does not hold the %d files and %d bytes its marker names - left alone"
                        % (wid, rec["files"], rec["bytes"]))
                    continue
                if installed_now.get(wid, 0) >= stamp and where.get(wid):
                    shutil.rmtree(folder, ignore_errors=True)            # the server's own copy is as new: nothing to take in
                    for path in (marker, manifest):
                        if os.path.exists(path):
                            os.remove(path)
                    counts["redundant"] += 1
                    continue
                old = [p for p in where.get(wid, [])]
                aside = []
                for path in old:                                       # an older copy of the same map: aside, then gone
                    os.rename(path, path + ".old-%d" % os.getpid())
                    aside.append(path + ".old-%d" % os.getpid())
                os.rename(folder, target)
                for path in aside:
                    shutil.rmtree(path, ignore_errors=True)
            link = os.path.join(content, wid)
            if os.path.islink(link):
                os.unlink(link)
            elif os.path.isdir(link):
                shutil.rmtree(link, ignore_errors=True)
            os.symlink(os.path.relpath(target, content), link)
            where[wid] = [target]
            installed = [(k, str(rec["installed"][k])) for k in ("size", "timeupdated", "manifest")]
            details = [(k, str(rec["details"][k])) for k in ("manifest", "timeupdated", "timetouched", "latest_timeupdated",
                                                                "latest_manifest") if str(rec["details"].get(k) or "").isdigit()]
            add[wid] = (installed, details)
            finish.append((wid, marker, manifest if os.path.isfile(manifest) else None, str(rec["installed"]["manifest"])))
            counts["taken"] += 1
        except OSError as error:
            counts["incomplete"] += 1
            log("hand-over %s: not taken in (%s)" % (wid, error))
    return add, finish, counts


def unkept(maps_dir, keep, protected):
    """[(type, id, folder)] of every stored map outside the keep set, in DELETE_TYPES' folders only; never a protected id."""
    out = []
    for kind in DELETE_TYPES:
        try:
            names = sorted(os.listdir(os.path.join(maps_dir, kind)))
        except OSError:
            continue
        for name in names:
            wid = name.split("-", 1)[0]
            path = os.path.join(maps_dir, kind, name)
            if wid.isdigit() and wid not in keep and wid not in protected and os.path.isdir(path) and not os.path.islink(path):
                out.append((kind, wid, path))
    return out


def disk_free(path):
    try:
        st = os.statvfs(path)
        return st.f_bavail * st.f_frsize
    except (OSError, AttributeError):
        return None


def gib(nbytes):
    return "%.1f GB" % (nbytes / float(1 << 30))


def write_list(rows, what):
    """update-watch/deleted-<local time>-<what>.tsv: type, id, title, MB - the record of a deletion or a dry run."""
    stamp = datetime.now(TZ).strftime("%Y%m%d-%H%M%S") if TZ else time.strftime("%Y%m%d-%H%M%S")
    path = os.path.join(STATE_DIR, "deleted-%s-%s.tsv" % (stamp, what))
    try:
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("type\tid\ttitle\tMB\n" + "".join("%s\t%s\t%s\t%d\n" % r for r in rows))
        for old in sorted(n for n in os.listdir(STATE_DIR) if n.startswith("deleted-") and n.endswith(".tsv"))[:-DELETE_LISTS_KEEP]:
            os.remove(os.path.join(STATE_DIR, old))
    except OSError as error:
        log("maps folder: the list %s was not written (%s)" % (path, error))
        return None
    return path


def keep_and_delete(content, maps_dir, types, where, rel):
    """THE HAND-OVER and THE DELETION, after the maps are in their folders: the hand-overs taken in, the keep set computed (and written
    to update-watch/keep.json), then - CS2_MAPS_DELETE on, and nothing wrong with the keep set - every stored map outside it deleted:
    its acf entries first (backed up, one atomic write together with the hand-overs' entries), then its link and its folder. dryrun:
    the same list, logged and written, nothing deleted. Never the own maps, the HUD, the warm-up item or a queued map, whatever the
    keep set says. Returns words for the summary line."""
    now = time.time()
    try:
        with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
            text = handle.read()
    except OSError as error:
        log("maps folder: %s cannot be read (%s) - no hand-over taken in, nothing deleted" % (WORKSHOP_ACF, error))
        return []
    try:
        installed_now = acf_installed(text)
    except ValueError as error:
        log("maps folder: %s does not parse (%s) - no hand-over taken in, nothing deleted" % (WORKSHOP_ACF, error))
        return []
    sizes = acf_sizes(text)
    add, finish, counts = take_in(content, maps_dir, types, where, rel, installed_now)
    hud = (os.environ.get("GUNGAME_HUD_ADDON") or "").strip()
    keep = keep_set(keep_inputs(BRIDGE_DATA), now, hud)
    protected = set(keep["own"]) | set(keep["hud"]) | set(keep["requested"]) | set([WARMUP_ITEM] if WARMUP_ITEM else [])
    for ids in keep["queues"].values():
        protected |= set(ids)
    protected |= set(add)                                              # just handed over: the keep set asked for them
    on_disk = dict((w, sizes.get(w, 0)) for w in where if where.get(w))
    for wid, (inst, _det) in add.items():
        on_disk[wid] = int(dict(inst).get("size") or 0)
    summary = keep_summary(keep, on_disk, listed_sizes())
    gone = unkept(maps_dir, keep["all"], protected)
    words = []
    parts = ", ".join("%s %d (%d on disk, %.1f GB)" % (p, s["maps"], s["on_disk"], s["gb_on_disk"]) for p, s in summary.items())
    log("keep set: %s; %d scored; problems: %s" % (parts, keep["scored"], "; ".join(keep["problems"]) or "none"))
    try:
        record = {"computed": time.strftime("%Y-%m-%dT%H:%M:%S"), "delete": MAPS_DELETE, "problems": keep["problems"],
                  "summary": summary, "cutoff": keep["cutoff"], "parts": dict((p, ids) for p, ids in keep_parts(keep) if p != "all")}
        temporary = KEEP_FILE + ".tmp"
        with open(temporary, "w", encoding="utf-8") as handle:
            json.dump(record, handle, indent=1)
        os.replace(temporary, KEEP_FILE)
    except OSError as error:
        log("keep set: %s not written (%s)" % (KEEP_FILE, error))
    rows = [(kind, wid, types.get(wid, ("other", ""))[1].replace("\t", " ") or os.path.basename(path).split("-", 1)[-1],
             sizes.get(wid, 0) // 1000000) for kind, wid, path in gone]
    by_type = ", ".join("%s %d" % (k, sum(1 for r in rows if r[0] == k)) for k in DELETE_TYPES if any(r[0] == k for r in rows))
    volume = sum(sizes.get(wid, 0) for _k, wid, _p in gone)
    delete = MAPS_DELETE == "on" and not keep["problems"] and gone
    if gone and not delete:
        listed = write_list(rows, "refused" if MAPS_DELETE == "on" else MAPS_DELETE)
        why = ("CS2_MAPS_DELETE is %s" % MAPS_DELETE) if MAPS_DELETE != "on" else "REFUSED: " + "; ".join(keep["problems"])
        log("maps folder: %d map(s) outside the keep set (%s, %s) - not deleted, %s; the list: %s"
            % (len(gone), gib(volume), by_type, why, listed))
        words.append("%d outside the keep set, not deleted (%s)" % (len(gone), MAPS_DELETE if MAPS_DELETE != "on" else "refused"))
    drop = set(wid for _k, wid, _p in gone) if delete else set()
    if add or drop:
        new = acf_edit(text, add, drop)
        if new is None:
            log("maps folder: %s does not round-trip through the parser - no hand-over registered, nothing deleted" % WORKSHOP_ACF)
            return words + ["acf left alone"]
        try:
            backup = acf_write(new, "organize")
        except OSError as error:
            log("maps folder: %s not written (%s) - no hand-over registered, nothing deleted" % (WORKSHOP_ACF, error))
            return words + ["acf not written"]
        log("maps folder: %s written - %d hand-over(s) registered, %d map(s) dropped; the old one is %s"
            % (WORKSHOP_ACF, len(add), len(drop), backup))
    for wid, marker, manifest, gid in finish:
        if manifest:
            target = os.path.join(DEPOTCACHE, "730_%s.manifest" % gid)
            try:
                if not os.path.exists(target):
                    shutil.copy2(manifest, target)        # the Steam client's verified manifest: no re-check request for it
            except OSError as error:
                log("hand-over %s: its manifest was not copied into %s (%s) - the client asks for it once" % (wid, DEPOTCACHE, error))
        for path in (marker, manifest):
            try:
                if path and os.path.exists(path):
                    os.remove(path)
            except OSError:
                pass
    if add:
        log("hand-over: %d map(s) taken in (%s)" % (len(add), ", ".join(sorted(add)[:20]) + (" ..." if len(add) > 20 else "")))
        words.append("%d handed over" % len(add))
    if counts["redundant"] or counts["incomplete"]:
        words.append("%d hand-over(s) not needed, %d left alone" % (counts["redundant"], counts["incomplete"]))
    if delete:
        before = disk_free(maps_dir)
        listed = write_list(rows, "done")
        failed = 0
        for kind, wid, path in gone:
            link = os.path.join(content, wid)
            try:
                if os.path.islink(link):
                    os.unlink(link)
                shutil.rmtree(path)
            except OSError as error:
                failed += 1
                log("maps folder: %s not deleted (%s)" % (path, error))
        after = disk_free(maps_dir)
        log("maps folder: DELETED %d map(s) outside the keep set, %s (%s)%s; free on the volume %s -> %s (ZFS frees in the background); "
            "the list: %s" % (len(gone) - failed, gib(volume), by_type, (", %d failed" % failed) if failed else "",
                              gib(before) if before is not None else "?", gib(after) if after is not None else "?", listed))
        words.append("%d deleted (%s)" % (len(gone) - failed, gib(volume)))
    return words


def engine_age(pid):
    """Seconds since engine process `pid` started (its /proc entry), or None."""
    try:
        return max(0.0, time.time() - os.stat("/proc/%d" % pid).st_ctime)
    except (OSError, TypeError):
        return None


def depotcache_state():
    """(manifests in ~/Steam/depotcache, is it a mounted volume, writable) - None for the count if unreadable."""
    try:
        count = sum(1 for name in os.listdir(DEPOTCACHE) if name.endswith(".manifest"))
    except OSError:
        count = None
    return count, os.path.ismount(DEPOTCACHE), os.access(DEPOTCACHE, os.W_OK)


def content_log_summary(text):
    """What content_log.txt says about the download sources: the lines that tell the stuck state apart."""
    keys = (("directory lists", "download sources and"), ("manifests OK", "manifest request received 200"),
            ("HTTP 503", "received 503"), ("send failed", "failed to send"), ("dropped (No Connection)", ": No Connection"),
            ("servers created", "Created download interface"), ("No target sources connected", "No target sources connected"))
    lines = text.splitlines()
    out = ["%s: %d" % (label, sum(1 for line in lines if needle in line)) for label, needle in keys]
    last = [line for line in lines if "download sources and" in line or "Moving to source priority class" in line][-3:]
    return out + ["last: " + line for line in last]


def snapshot_steam_logs(why, pid):
    """Copy the Steam client's logs (they live in the container's own layer, and a recreate loses them) to
    update-watch/steamlogs/<local time>-<why>/ with a summary.txt; keep the newest SNAPSHOT_KEEP. Returns the
    directory or None. Never raises: the restart it precedes must not depend on it."""
    try:
        slug = re.sub(r"[^a-z0-9]+", "-", why.lower()).strip("-")[:48] or "broken"
        target = os.path.join(SNAPSHOT_DIR, "%s-%s" % (datetime.now(TZ).strftime("%Y-%m-%d_%H%M%S") if TZ
                                                       else time.strftime("%Y-%m-%d_%H%M%S"), slug))
        os.makedirs(target, exist_ok=True)
        copied, content = [], ""
        for name in sorted(os.listdir(STEAM_LOGS)):
            source = os.path.join(STEAM_LOGS, name)
            if not (name.endswith(".txt") and os.path.isfile(source)):
                continue
            size = os.path.getsize(source)
            with open(source, "rb") as handle:
                if size > SNAPSHOT_MAX_FILE:
                    handle.seek(size - SNAPSHOT_MAX_FILE)
                data = handle.read()
            with open(os.path.join(target, name), "wb") as handle:
                handle.write(data)
            copied.append("%s (%d bytes%s)" % (name, len(data), ", tail" if size > SNAPSHOT_MAX_FILE else ""))
            if name == "content_log.txt":
                content = data.decode("utf-8", "replace")
        count, mounted, writable = depotcache_state()
        age = engine_age(pid) if pid else None
        summary = ["why: %s" % why, "engine pid: %s, running for %s" % (pid, hms(age) if age is not None else "?"),
                   "depotcache %s: %s manifests, %s, %s" % (DEPOTCACHE, count, "a mounted volume" if mounted
                                                            else "NOT a mounted volume", "writable" if writable
                                                            else "NOT writable"),
                   "copied from %s: %s" % (STEAM_LOGS, ", ".join(copied) or "nothing"), "", "content_log.txt:"]
        summary += ["  " + line for line in content_log_summary(content)]
        with open(os.path.join(target, "summary.txt"), "w", encoding="utf-8") as handle:
            handle.write("\n".join(summary) + "\n")
        for old in sorted(os.listdir(SNAPSHOT_DIR))[:-SNAPSHOT_KEEP]:
            shutil.rmtree(os.path.join(SNAPSHOT_DIR, old), ignore_errors=True)
        log("Steam logs saved for the record: %s (%d files)" % (target, len(copied)))
        return target
    except Exception as error:  # noqa: BLE001 - evidence is a nice-to-have, the restart is not
        log("WARNING: could not save the Steam logs (%s: %s)" % (type(error).__name__, error))
        return None


def prefetch_slot(now):
    """(slot, local day) when `now` is inside [slot - PREFETCH_RESTART_LEAD, slot + PREFETCH_SPAN) of a prefetch slot,
    else (None, None): the stretch in which the restart before the prefetch may happen."""
    if PREFETCH_AT is None:
        return None, None
    slot = next_slot(now - PREFETCH_SPAN, PREFETCH_AT, TZ)
    if slot is not None and slot - PREFETCH_RESTART_LEAD <= now < slot + PREFETCH_SPAN:
        return slot, local_day(slot, TZ)
    return None, None


DOWNLOAD_LINE = flag_env("CS2_DOWNLOAD_LINE", True)
LINE_AFTER = 3          # a download this young says nothing: a cached map's "download" ends within a second
LINE_EVERY = 2          # seconds between two updates of the line
LINE_HOLD = 4           # seconds one update stays up, so the line lasts until the next and goes by itself after the last
LINE_FORGET = 1800      # a download with no sign of life for this long is over, whatever it never said
ENGINE_STATE_RX = re.compile(r"\[Workshop\] Updating (\d+): state (\d+)")
ENGINE_LOADING_RX = re.compile(r'\[Workshop\] Loading custom game "(\d+)"')
MAM_STARTED_RX = re.compile(r"\[MultiAddonManager\] Addon download started for (\d+)")
MAM_PROGRESS_RX = re.compile(r"\[MultiAddonManager\] Downloading addon (\d+): ([\d.]+)/([\d.]+) MB")
MAM_DONE_RX = re.compile(r"\[MultiAddonManager\] Addon (\d+) downloaded successfully")
MAM_FAILED_RX = re.compile(r'\[MultiAddonManager\] Addon (\d+) download failed with reason "([^"]*)"')


def size_words(nbytes):
    """As the HUD shows a map's size, the lists' sizeMb: binary units, labelled MB and GB (a client showed 486 MB of 464 MB in decimal ones)."""
    return "%.1f GB" % (nbytes / float(1 << 30)) if nbytes >= (1 << 30) else "%d MB" % max(1, round(nbytes / float(1 << 20)))


class DownloadLine:
    """Job 8: which downloads are running, and the news line about the newest one. Fed from console.log (console_line) and
    the Steam client's workshop log (started, finished, failed); step() sends the line. A failed RCON call only skips an update."""

    def __init__(self):
        self.active = {}          # id -> {"since", "seen", "done", "total"} (done, total in bytes; from MultiAddonManager when it says)
        self.failure = None       # (id, why): one line about it goes out at the next step
        self.sent = 0
        self.told = set()         # ids whose line was logged once
        self.names = {}
        self.names_at = 0

    def reset(self):
        self.active.clear()
        self.failure = None
        self.told.clear()

    def started(self, wid, now):
        entry = self.active.setdefault(wid, {"since": now, "seen": now, "done": None, "total": None})
        entry["seen"] = now
        return entry

    def finished(self, wid):
        self.active.pop(wid, None)
        self.told.discard(wid)

    def failed(self, wid, why, now):
        if wid in self.active or wid in self.told:
            self.failure = (wid, why)
        self.finished(wid)

    def console_line(self, line, now):
        """One console.log line. Cheap: most lines match nothing."""
        if "[Workshop]" not in line and "[MultiAddonManager]" not in line:
            return
        found = ENGINE_LOADING_RX.search(line)
        if found:
            self.finished(found.group(1))
            return
        found = ENGINE_STATE_RX.search(line)
        if found:
            if found.group(2) == "5":
                self.finished(found.group(1))
            else:
                self.started(found.group(1), now)["engine"] = True   # the engine itself waits for it: a map change does
            return
        found = MAM_STARTED_RX.search(line)
        if found:
            self.started(found.group(1), now)
            return
        found = MAM_PROGRESS_RX.search(line)
        if found:
            entry = self.started(found.group(1), now)
            entry["done"], entry["total"] = float(found.group(2)) * (1 << 20), float(found.group(3)) * (1 << 20)
            return
        found = MAM_DONE_RX.search(line)
        if found:
            self.finished(found.group(1))
            return
        found = MAM_FAILED_RX.search(line)
        if found:
            self.failed(found.group(1), found.group(2), now)

    def lookup(self, now):
        """{id: (name, bytes)} from the bridge's lists, read again once a minute while a line is due."""
        if now - self.names_at >= 60:
            self.names_at = now
            names = {}
            for fname in ("maps-wingman.json", "maps-deathmatch.json", "maps.json"):
                for entry in read_json(os.path.join(BRIDGE_DATA, fname), []) or []:
                    if isinstance(entry, dict) and entry.get("id"):
                        names[str(entry["id"])] = (str(entry.get("name") or ""), int(entry.get("sizeMb") or 0) << 20)
            self.names = names
        return self.names

    @staticmethod
    def staged(wid):
        """Bytes of this item in the cache's staging folders."""
        total = 0
        for stage in ("downloads", "temp"):
            for root, _dirs, files in os.walk(os.path.join(WORKSHOP_DIR, stage)):
                if wid in root.split(os.sep):
                    for name in files:
                        try:
                            total += os.path.getsize(os.path.join(root, name))
                        except OSError:
                            pass
        return total

    def title(self, wid, now):
        return self.lookup(now).get(wid, ("", 0))[0].replace("%", "")[:48].strip() or "Workshop item %s" % wid

    def words(self, wid, now):
        name, size = self.title(wid, now), self.lookup(now).get(wid, ("", 0))[1]
        entry = self.active[wid]
        done, total = entry["done"], entry["total"] or size
        if done is None:
            done = self.staged(wid)
        if done and total:
            return "Server is downloading %s: %d percent (%s of %s) - the map changes when it is ready" % (   # no % sign on a console line
                name, min(99, int(done * 100 / total)), size_words(done), size_words(total))
        if done:
            return "Server is downloading %s: %s so far - the map changes when it is ready" % (name, size_words(done))
        if total:
            return "Server is downloading %s (%s) - the map changes when it is ready" % (name, size_words(total))
        return "Server is downloading %s - the map changes when it is ready" % name

    def step(self, now, humans, own=()):
        if not DOWNLOAD_LINE:
            return
        for wid in [w for w, e in self.active.items() if now - e["seen"] > LINE_FORGET]:
            self.finished(wid)
        if not humans:
            self.failure = None
            return
        if self.failure:
            wid, why = self.failure
            self.failure = None
            line = chat_safe("The server could not download %s: %s - pick another map or press Next" % (
                self.title(wid, now), why.replace("%", "").strip() or "no reason given"), 240)
            log("hud: %s" % line)
            rcon(['gg_notice 10 "%s"' % line])
            self.sent = now
            return
        if now - self.sent < LINE_EVERY:
            return
        waiting = [w for w, e in self.active.items() if w not in own and now - e["since"] >= LINE_AFTER]
        if not waiting:
            return
        wid = max(waiting, key=lambda w: self.active[w]["since"])
        line = chat_safe(self.words(wid, now), 240)
        if wid not in self.told:
            self.told.add(wid)
            log("hud: %s" % line)
        self.sent = now
        rcon(['gg_notice %d "%s"' % (LINE_HOLD, line)])


READY = flag_env("CS2_READY", True)   # job 9's switch
READY_EVERY = 20                # seconds between two looks at the bridge's queues.json and the cache
READY_REPORT_EVERY = 120        # the report to the bridge goes again at least this often (the bridge trusts one for 5 minutes)
READY_RETRY = 1800              # a queued map whose background download failed is tried again after this...
READY_TRIES = 3                 # ...up to this many times in one engine process


def bridge_endpoint():
    """(base URL, secret) of the bridge: CS2_BRIDGE_URL and CS2_BRIDGE_SECRET, else the log push's own URL (CS2_LOG_HTTP_URL,
    http://<bridge>:<port>/log/<secret>, which the image's templating may write with \\/). None when neither says."""
    base, secret = (os.environ.get("CS2_BRIDGE_URL") or "").strip().rstrip("/"), (os.environ.get("CS2_BRIDGE_SECRET") or "").strip()
    if base and secret:
        return base, secret
    found = re.match(r"^(https?://[^/\s]+)/log/([A-Za-z0-9]+)$", (os.environ.get("CS2_LOG_HTTP_URL") or "").strip().replace("\\/", "/"))
    return (found.group(1), found.group(2)) if found else None


class ReadyMaps:
    """Job 9: the maps the bridge's random queues and the players' requests play next (queues.json), on disk before anybody asks -
    downloaded in the background, one at a time, also while people play, and silently (job 8's line is for downloads somebody waits
    for). The same engine path as the prefetch (mm_download_addon); never while a download somebody waits for runs, nor before the
    warm-up, nor beside the prefetch, nor in a process whose downloads are broken (job 6, unchanged: a stuck result marks it). Every
    look, and at least every READY_REPORT_EVERY, the bridge hears which of them are on disk (POST /ready), and takes those first."""

    def __init__(self, ws):
        self.ws = ws
        self.current = None          # (id, sent): the background download in flight
        self.failed = {}             # id -> (tries, not before) in this engine process
        self.look_at = 0.0
        self.reported = None         # (the ids reported, when)
        self.queues = (None, [])     # (queues.json's stamp, the ids it wants, in play order)
        self.acf = (None, {})        # (the acf's stamp, its installed items)
        self.endpoint = bridge_endpoint()
        self.downloads = 0

    def reset(self):
        self.current, self.failed, self.look_at = None, {}, 0.0

    def wanted(self):
        """What queues.json names, in the order it is needed: the requests (the next vote shows them), then the queues' first
        entries, their second ones, and so on."""
        path = os.path.join(BRIDGE_DATA, "queues.json")
        try:
            st = os.stat(path)
        except OSError:
            return []
        if (st.st_mtime, st.st_size) != self.queues[0]:
            data = read_json(path, {})
            data = data if isinstance(data, dict) else {}
            random = data.get("random") if isinstance(data.get("random"), dict) else {}
            ids = [str(x) for x in data.get("requested") or [] if str(x).isdigit()] if isinstance(data.get("requested"), list) else []
            lanes = [[str(x) for x in random.get(n) or [] if str(x).isdigit()] for n in KEEP_QUEUES]
            for i in range(max([len(lane) for lane in lanes] or [0])):
                ids += [lane[i] for lane in lanes if i < len(lane)]
            self.queues = ((st.st_mtime, st.st_size), list(dict.fromkeys(ids)))
        return self.queues[1]

    def installed(self):
        try:
            st = os.stat(WORKSHOP_ACF)
        except OSError:
            return {}
        if (st.st_mtime, st.st_size) != self.acf[0]:
            try:
                with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
                    self.acf = ((st.st_mtime, st.st_size), acf_installed(handle.read()))
            except (OSError, ValueError):
                pass
        return self.acf[1]

    def gate(self):
        """Why no background download may start now, or None."""
        ws, w = self.ws, self.ws.watch
        if w.a2s is None or w.armed_pid is None:
            return "the engine is not answering A2S"
        if ws.warm is not None and ws.warm["state"] in ("waiting", "running"):
            return "the warm-up comes first"
        if ws.pf is not None:
            return "the nightly prefetch is running (it fetches the queued maps too)"
        if ws.broken is not None:
            return "workshop downloads are broken in this engine process (%s)" % ws.broken[1]
        own = ws.own()
        if any(x not in own for x in ws.line.active):
            return "a download somebody waits for goes first"
        return None

    def report(self, now, ready, total, force=False):
        """POST /ready to the bridge when what is on disk changed, every READY_REPORT_EVERY, or when forced (right before a background
        download starts). Returns the bridge's answer, or None. An answer ending in '; hold: <why>' means a map a player waits for is
        on its way: Steam runs one Workshop download at a time and one already running cannot be stopped (measured on a test copy on
        2026-11-03: a map change's download started only when the background one had ended), so none may start now."""
        if self.endpoint is None:
            log_once("ready-endpoint", "ready: no bridge address (CS2_LOG_HTTP_URL) - the bridge counts every queued map as on disk",
                     repeat=21600)
            return None
        key = frozenset(ready)
        if not force and self.reported is not None and self.reported[0] == key and now - self.reported[1] < READY_REPORT_EVERY:
            return None
        base, secret = self.endpoint
        body = ("gg-ready\t1\n" + "".join("%s\n" % x for x in sorted(ready))).encode("utf-8")
        try:
            request = urllib.request.Request("%s/ready/%s" % (base, secret), data=body, method="POST",
                                             headers={"User-Agent": USER_AGENT, "Content-Type": "text/plain; charset=utf-8"})
            with urllib.request.urlopen(request, timeout=3) as response:
                reply = response.read().decode("utf-8", "replace").strip()
        except Exception as error:  # noqa: BLE001 - a bridge that is down or older: it then counts every queued map as on disk
            log_once("ready-report", "WARNING: the ready report to the bridge failed (%s: %s) - it counts every queued map as on disk"
                     % (type(error).__name__, str(error).replace(secret, "<secret>")), repeat=3600)
            return None
        news = self.reported is None or self.reported[0] != key
        self.reported = (key, now)
        if news:
            log("ready: %d of %d queued map(s) on disk, reported to the bridge (%s)" % (len(ready), total, reply[:240]))
        return reply

    def step(self, now, events):
        if not READY:
            return
        ws = self.ws
        if self.current is not None:
            wid, sent = self.current
            for ewid, kind, text, cancel in events:
                if ewid != wid or kind != "result":
                    continue
                self.current = None
                ws.line.finished(wid)                          # whatever MultiAddonManager printed of it is over too
                if text.strip().upper() == "OK":
                    self.downloads += 1
                    self.failed[wid] = (0, now + READY_RETRY)  # should the acf not show it, not again at once: no download loop
                    self.look_at = 0.0                         # look again at once: the bridge hears of it now
                    log("ready: %s downloaded in the background in %s (%d this engine process)" % (wid, hms(now - sent), self.downloads))
                else:
                    tries = self.failed.get(wid, (0, 0))[0] + 1
                    self.failed[wid] = (tries, now + READY_RETRY)
                    log("ready: the background download of %s came back '%s' (try %d of %d in this engine process)"
                        % (wid, text, tries, READY_TRIES))
                    if stuck_result(text, cancel):
                        ws.mark_broken(now, "a background download (%s) came back '%s'" % (wid, text))
                break
            if self.current is not None and now - sent > PREFETCH_ITEM_TIMEOUT:
                self.failed[wid] = (self.failed.get(wid, (0, 0))[0] + 1, now + READY_RETRY)
                log("ready: no result for %s after %s - tried again later" % (wid, hms(PREFETCH_ITEM_TIMEOUT)))
                self.current = None
        if now < self.look_at:
            return
        self.look_at = now + READY_EVERY
        wanted = self.wanted()
        if not wanted:
            return
        installed = self.installed()
        content = os.path.join(WORKSHOP_DIR, "content", "730")
        ready = set(x for x in wanted if x in installed and os.path.isdir(os.path.join(content, x)))
        missing = [x for x in wanted if x not in ready and x != WARMUP_ITEM]
        todo = [x for x in missing if self.failed.get(x, (0, 0))[0] < READY_TRIES and now >= self.failed.get(x, (0, 0))[1]]
        why = self.gate() if self.current is None and todo else None
        reply = self.report(now, ready, len(wanted), force=self.current is None and bool(todo) and why is None)
        if self.current is not None or not todo:
            return
        if why is None and reply and "; hold:" in reply:
            why = "the bridge says hold (%s)" % reply.split("; hold:", 1)[1].strip()
        if why:
            log_once("ready-wait", "ready: %d of %d queued map(s) not on disk - waiting: %s" % (len(missing), len(wanted), why),
                     repeat=1800)
            return
        wid = todo[0]
        replies = rcon(["mm_download_addon %s" % wid])
        if not replies or replies[0] is None:
            return
        if "unknown command" in replies[0].lower():
            log_once("ready-mam", "ready: mm_download_addon is not loaded (MultiAddonManager runs only while the HUD is on) - no "
                                  "background downloads", repeat=21600)
            return
        self.current = (wid, now)
        log("ready: %s is not on disk - downloading it in the background, silently (%d of %d queued map(s) missing)"
            % (wid, len(missing), len(wanted)))


class Workshop:
    """The warm-up, the nightly prefetch and job 9, a state machine stepped once per watcher tick - never blocking it for
    longer than one RCON command, one Workshop details request or one report to the bridge."""

    def __init__(self, watch):
        self.watch = watch
        self.line = DownloadLine()
        self.tail = Tail(WORKSHOP_LOG)
        self.warm = None                 # {"pid", "state": waiting|running|done|noop|skipped|failed, "at", "sent", "seen"}
        self.pf = None
        self.broken = None               # (since, why): downloads are failing in this engine process -> a restart is due
        self.pid_seen = None
        self.cancel = None               # the last 'Update canceled:' reason in workshop_log, until its result line
        self.ready = ReadyMaps(self)     # job 9

    def own(self):
        """The downloads nobody waits for: the warm-up's, the prefetch's and job 9's - unless the engine itself waits for job 9's (a
        pick of the very map it was fetching: then the line shows it)."""
        own = set([WARMUP_ITEM]) if WARMUP_ITEM else set()
        if self.pf is not None and self.pf.get("current"):
            own.add(self.pf["current"][0])
        if self.ready.current is not None and not self.line.active.get(self.ready.current[0], {}).get("engine"):
            own.add(self.ready.current[0])
        return own

    def mark_broken(self, now, why):
        if self.broken is None:
            self.broken = (now, why)
            log("WARNING: workshop downloads are broken in this engine process (%s) - a restart is due as soon as "
                "nobody is on (at most one per %s, %d per 24 h)" % (why, hms(WORKSHOP_RESTART_GAP), WORKSHOP_RESTART_MAX))
            snapshot_steam_logs(why, self.watch.armed_pid)   # before any restart: a recreate would lose them

    def step(self, now):
        events = []
        for wid, kind, text in download_events(self.tail.lines()):
            if kind == "canceled":                  # the job's reason, attached to the result that follows it
                self.cancel = text
            elif kind == "result":
                events.append((wid, kind, text, self.cancel))
                self.cancel = None
            else:
                events.append((wid, kind, text, None))
        pid = self.watch.armed_pid
        if pid is not None and pid != self.pid_seen:
            self.pid_seen = pid
            if self.broken is not None:
                log("workshop downloads: engine pid %d is a new process - the broken flag is cleared" % pid)
            self.broken = None
            self.line.reset()
            self.ready.reset()
        if self.broken is not None:
            for wid, kind, text, _ in events:
                if kind == "result" and text.strip().upper() == "OK":
                    log("workshop downloads work again (item %s OK) - the download restart is no longer due" % wid)
                    self.broken = None
                    break
        self.step_players(now, events)
        self.step_warmup(now, events)
        self.step_prefetch(now, events)
        self.ready.step(now, events)

    # -- what players wait for ------------------------------------------------------------------------------------
    def step_players(self, now, events):
        """Every download that is not the warm-up's, the prefetch's or job 9's: job 8's line, and job 6's broken flag for it (the stuck
        state of 2026-10-31: a player's pick failed in a process nothing had marked, and nothing noticed)."""
        own = self.own()
        for wid, kind, text, cancel in events:
            if not wid or wid in own:
                continue
            if kind == "requested":
                self.line.started(wid, now)
            elif text.strip().upper() == "OK":
                self.line.finished(wid)
            else:
                self.line.failed(wid, text, now)
                if stuck_result(text, cancel):
                    self.mark_broken(now, "a download for players (%s) came back '%s'" % (wid, text))
        self.line.step(now, self.watch.a2s["humans"] if self.watch.a2s else 0, own)

    # -- warm-up --------------------------------------------------------------------------------------------------
    def step_warmup(self, now, events):
        w = self.watch
        if not (WARMUP and WARMUP_ITEM) or w.a2s is None or w.armed_pid is None:
            return
        if self.warm is None or self.warm["pid"] != w.armed_pid:
            self.warm = {"pid": w.armed_pid, "state": "waiting", "at": now, "sent": None, "seen": False, "tries": 0}
            try:
                age = time.time() - os.stat("/proc/%d" % w.armed_pid).st_ctime
            except OSError:
                age = 0
            if age > 600 and now - w.started < 120:
                self.warm["state"] = "skipped"
                log("warm-up: engine pid %d has been running for %s - the watcher restarted, not the engine: no warm-up"
                    % (w.armed_pid, hms(age)))
            return
        warm = self.warm
        if warm["state"] == "waiting" and now - warm["at"] >= WARMUP_DELAY:
            replies = rcon(["mm_download_addon %s" % WARMUP_ITEM])
            if not replies or replies[0] is None:
                warm["at"] = now                       # RCON not up yet: try again after another delay
                return
            if "unknown command" in replies[0].lower():
                warm["state"] = "skipped"
                log("warm-up skipped: mm_download_addon is not loaded (MultiAddonManager runs only while the HUD is "
                    "on) - the first player download in this process pays the per-item re-check")
                return
            warm["state"], warm["sent"], warm["seen"] = "running", now, False
            warm["tries"] += 1
            log("warm-up: downloading workshop item %s for engine pid %d - seconds with the depotcache in place, ~15 min "
                "if the Steam client must re-check every installed item first" % (WARMUP_ITEM, warm["pid"]))
            return
        if warm["state"] != "running":
            return
        for wid, kind, text, cancel in events:
            if wid != WARMUP_ITEM:
                continue
            if kind == "requested":
                warm["seen"] = True
            elif text.strip().upper() == "OK":
                warm["state"] = "done"
                count = depotcache_state()[0]
                took = now - warm["sent"]
                log("warm-up done in %s: this engine process is warm (%s manifests in the depotcache)"
                    % ("%d s" % took if took < 120 else hms(took), count if count is not None else "?"))
                return
            elif stuck_result(text, cancel):
                warm["state"] = "failed"
                log("WARNING: warm-up: '%s' after %s (%s) - the Steam client has no download source left in this "
                    "engine process, and a process never gets them back: no retry; players' uncached maps "
                    "will not arrive" % (text, hms(now - warm["sent"]), cancel or "no cancel line"))
                self.mark_broken(now, "the warm-up item came back '%s'" % text)
                return
            elif warm["tries"] < 2:
                warm["state"], warm["at"] = "waiting", now + WARMUP_RETRY - WARMUP_DELAY
                log("warm-up: the job ended after %s with '%s' - the item did not arrive; trying once more in %s"
                    % (hms(now - warm["sent"]), text, hms(WARMUP_RETRY)))
                return
            else:
                warm["state"] = "failed"
                log("WARNING: warm-up: '%s' again - workshop downloads are failing in this engine process (only a restart "
                    "clears it); players' uncached maps will not arrive"
                    % text)
                self.mark_broken(now, "the warm-up item came back '%s' twice" % text)
                return
        if not warm["seen"] and now - warm["sent"] > NOOP_AFTER:
            warm["state"] = "noop"
            log("WARNING: warm-up: no download started for %s within %d s - it was installed already (did pre.sh's "
                "--warmup-reset run?), so this process is still cold" % (WARMUP_ITEM, NOOP_AFTER))
        elif now - warm["sent"] > WARMUP_TIMEOUT:
            warm["state"] = "failed"
            log("WARNING: warm-up: no result for %s after %s" % (WARMUP_ITEM, hms(now - warm["sent"])))

    # -- nightly prefetch -----------------------------------------------------------------------------------------
    def window(self, now):
        """(slot start, local day) of the prefetch window `now` falls in, or (None, None)."""
        if PREFETCH_AT is None:
            return None, None
        slot = next_slot(now - PREFETCH_SPAN, PREFETCH_AT, TZ)
        if slot is not None and slot <= now < slot + PREFETCH_SPAN:
            return slot, local_day(slot, TZ)
        return None, None

    def finish(self, why, stamp=True):
        """End tonight's prefetch run. stamp=False (the process lost its download sources): the night is not
        stamped, so the prefetch runs again in the next engine process while its window is still open."""
        pf, w = self.pf, self.watch
        log("prefetch %s: %d of %d done (%d missing, %d outdated at the start), %d failed%s, %d left, in %s%s"
            % (why, pf["done"], pf["total"], pf["missing"], pf["outdated"], len(pf["failed"]),
               (" (" + ", ".join(pf["failed"][:8]) + ")") if pf["failed"] else "", len(pf["queue"]),
               hms(time.time() - pf["started"]), "" if stamp else " - it goes on in the next engine process"))
        if stamp:
            w.state["prefetch_day"] = pf["day"]
            w.save()
        self.pf = None

    def step_prefetch(self, now, events):
        w = self.watch
        slot, day = self.window(now)
        if self.pf is None:
            if slot is None or w.state.get("prefetch_day") == day:
                return
            if self.warm is not None and self.warm["state"] in ("waiting", "running"):
                return                                   # the warm-up's job comes first
            if w.due is not None and w.due[0] == "prefetch":
                return                                   # the fresh process comes first: its restart is due
            if self.broken is not None:
                log_once("prefetch-broken", "prefetch: not starting - workshop downloads are broken in this engine "
                                            "process (%s); it waits for a new one" % self.broken[1], repeat=3600)
                return
            if w.a2s is None or w.a2s["humans"] > 0:
                log_once("prefetch-wait", "prefetch: waiting for an empty server", repeat=3600)
                return
            listed, skip, what = prefetch_lists()
            try:
                with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
                    installed = acf_installed(handle.read())
                folders = set(os.listdir(os.path.join(WORKSHOP_DIR, "content", "730")))
            except (OSError, ValueError) as error:
                log("prefetch: cannot read the workshop cache (%s) - not tonight" % error)
                w.state["prefetch_day"] = day
                w.save()
                return
            ids = [x for x in listed if x not in skip]   # the keep set and the queues only, since 2026-11-03 (prefetch_lists)
            wanted = set(ids)
            installed = dict((w, t) for w, t in installed.items() if w in wanted)
            self.pf = {"day": day, "end": slot + PREFETCH_SPAN, "phase": "plan", "started": now, "listed": listed,
                       "skip": skip, "installed": installed, "folders": folders, "details": {}, "errors": 0,
                       "batches": [(ids[i:i + DETAILS_BATCH], 0) for i in range(0, len(ids), DETAILS_BATCH)],
                       "queue": [], "current": None, "done": 0, "failed": [], "total": 0, "missing": 0, "outdated": 0}
            log("prefetch: checking %d items against the Workshop (%s)" % (len(ids), what))
            return
        pf = self.pf
        if now >= pf["end"] and pf["current"] is None:
            self.finish("stopped at the end of its window")
            return
        if pf["phase"] == "plan":
            if pf["batches"] and pf["errors"] < PREFETCH_DETAIL_ERRORS:
                batch, not_before = pf["batches"][0]
                if now < not_before:
                    return
                pf["batches"].pop(0)
                try:
                    pf["details"].update(fetch_details(batch))
                except Exception as error:   # urllib, socket, JSON: count it and ask again later
                    pf["errors"] += 1
                    pf["last_error"] = "%s: %s" % (type(error).__name__, error)
                    pf["batches"].append((batch, now + PREFETCH_DETAIL_RETRY))
                return
            if pf["batches"]:
                log("prefetch: the Workshop details API failed %d times (last: %s) - planning without %d item(s)"
                    % (pf["errors"], pf.get("last_error"), sum(len(b) for b, _ in pf["batches"])))
                pf["batches"] = []
            missing, outdated = prefetch_plan(pf["listed"], pf["installed"], pf["folders"], pf["details"], pf["skip"])
            pf.update(queue=missing + outdated, total=len(missing) + len(outdated), missing=len(missing),
                      outdated=len(outdated), phase="run")
            gone = sum(1 for r in pf["details"].values() if r[0] != 1)
            log("prefetch: %d missing, %d outdated, %d not available on the Workshop" % (len(missing), len(outdated), gone))
            return
        if pf["current"] is not None:
            wid, sent = pf["current"]
            for ewid, kind, text, cancel in events:
                if ewid == wid and kind == "result":
                    pf["current"] = None
                    if text.strip().upper() == "OK":
                        pf["done"] += 1
                        pf["streak"] = 0
                    else:
                        pf["failed"].append("%s: %s" % (wid, text))
                        pf["streak"] = pf.get("streak", 0) + 1
                        if stuck_result(text, cancel):
                            self.finish("stopped: %s came back '%s' (%s) - no download source is left in this engine "
                                        "process" % (wid, text, cancel or "no cancel line"), stamp=False)
                            self.mark_broken(now, "a prefetch download came back '%s'" % text)
                        elif pf["streak"] >= PREFETCH_FAIL_STREAK:
                            streak = pf["streak"]
                            self.finish("stopped: %d downloads in a row failed ('%s') - the download path is broken in "
                                        "this engine process" % (streak, text))
                            self.mark_broken(now, "%d prefetch downloads in a row came back '%s'" % (streak, text))
                    return
            if now - sent > PREFETCH_ITEM_TIMEOUT:
                pf["failed"].append("%s: no result in %s" % (wid, hms(PREFETCH_ITEM_TIMEOUT)))
                pf["current"] = None
            return
        if not pf["queue"]:
            self.finish("done")
            return
        if w.a2s is None or w.a2s["humans"] > 0:
            log_once("prefetch-yield", "prefetch: paused while %s human(s) are on" % (w.a2s["humans"] if w.a2s else "?"),
                     repeat=1800)
            return
        wid = pf["queue"].pop(0)
        replies = rcon(["mm_download_addon %s" % wid])
        if not replies or replies[0] is None:
            pf["queue"].insert(0, wid)
            return
        if "unknown command" in replies[0].lower():
            pf["queue"].insert(0, wid)
            self.finish("stopped: mm_download_addon is not loaded")
            return
        pf["current"] = (wid, now)


# ------------------------------------------------------------------------------------------------
# The watcher
# ------------------------------------------------------------------------------------------------

class Watch:
    def __init__(self):
        self.state, self.fault = load_state()
        self.dir_fault = persistence_selftest()
        self.tails = [Tail(path) for path in CONSOLE_CANDIDATES]   # positioned before the engine starts writing
        self.console = None              # the Tail proven live by the echo probe
        self.probe_tries = 0
        self.probe_rcon_misses = 0       # probes that could not even open RCON (not counted as tries, up to a cap)
        self.probe_next = 0
        self.probe_token = None
        self.probe_sent = 0
        self.a2s = None
        self.a2s_at = 0
        self.silent_since = None         # A2S: when the current run of failed polls began (None: it answered last)
        self.a2s_fails = 0               # ...and how many polls in a row have failed
        self.rcon_since = None           # the same two for the RCON liveness probe, asked only while A2S fails
        self.rcon_fails = 0
        self.armed_pid = None            # the engine pid the silence path may kill: it answered A2S since it started
        self.going_down = None           # when the watcher itself quit or killed the engine (no silence kill after)
        self.first_answer = None
        self.signature_at = None
        self.other_level = False
        self.match_ends = []
        self.verdict = ("unchecked", None)
        self.verdict_build = None
        self.verdict_at = 0
        self.check_backoff = 0
        self.due = None                  # (kind, since) of a planned restart waiting for its moment
        self.warned = False
        self.started = time.time()
        self.workshop = Workshop(self)

    def blocked(self):
        """Faults that disable every restart: the gates are only as good as the file they live in."""
        if self.dir_fault:
            return "the state directory is unusable (%s)" % self.dir_fault
        if self.fault:
            return "the state file is damaged (%s)" % self.fault
        if not RCON_PASSWORD:
            return "CS2_RCONPW is empty, so neither `quit` nor a chat line can be sent"
        return None

    def save(self):
        if self.dir_fault or self.fault:
            return False
        try:
            save_state(self.state)
            return True
        except OSError as error:
            self.dir_fault = "cannot write %s (%s)" % (STATE_FILE, error)
            log("ALERT: %s - every restart is disabled" % self.dir_fault)
            return False

    # -- the previous restart, judged on this start --------------------------------------------------
    def verify_pending(self, now):
        """Judge the restart a PREVIOUS run of this watcher triggered (it is recorded just before the container goes
        down, so the verdict belongs to the next start). A record from this very run is left for the next one."""
        pending = self.state["pending"]
        if pending is None or pending["at"] >= self.started:
            return
        if self.first_answer is None:
            if now - pending["at"] > RECOVER_WITHIN and now - self.started > RECOVER_WITHIN:
                self.state["pending"] = None
                if pending["kind"] == "freeze":
                    self.state["freeze_misses"] += 1
                    if self.state["freeze_misses"] >= FREEZE_MAX_MISSES:
                        self.state["freeze_tripped"] = int(now)
                    log("ALERT: the server has not answered A2S %s after the freeze kill (miss %d of %d%s)"
                        % (hms(now - pending["at"]), self.state["freeze_misses"], FREEZE_MAX_MISSES,
                           ", FREEZE BREAKER LATCHED - delete %s to re-arm" % STATE_FILE
                           if self.state["freeze_tripped"] else ""))
                else:
                    log("WARNING: the %s restart was not followed by an A2S answer within %s"
                        % (pending["kind"], hms(RECOVER_WITHIN)))
                self.save()
            return
        self.state["pending"] = None
        took = int(self.first_answer - pending["at"])
        if pending["kind"] == "freeze":
            self.state["freeze_misses"] = 0
            log("freeze recovery worked: A2S answered %d s after the kill" % took)
        else:
            if 0 < took < 3600 and pending["kind"] == "daily":
                self.state["durations"] = (self.state["durations"] + [took])[-5:]
            log("measured: the %s restart took %d s from quit to A2S answering (last %s)"
                % (pending["kind"], took, self.state["durations"]))
        if pending["kind"] == "update":
            now_version = read_patch_version()
            if now_version and now_version != pending["from"]:
                self.state["update_misses"] = 0
                log("update installed: PatchVersion %s -> %s" % (pending["from"], now_version))
            else:
                self.state["update_misses"] += 1
                misses = self.state["update_misses"]
                if misses >= UPDATE_MAX_MISSES:
                    self.state["update_tripped"] = int(now)
                    self.state["update_tripped_for"] = pending.get("required") or 0
                else:
                    self.state["update_retry_after"] = int(now) + UPDATE_RETRY[min(misses, len(UPDATE_RETRY)) - 1]
                log("%s: the update restart left PatchVersion on %s (miss %d of %d)%s"
                    % ("ALERT" if self.state["update_tripped"] else "WARNING", now_version,
                       self.state["update_misses"], UPDATE_MAX_MISSES,
                       " - UPDATE BREAKER LATCHED for this build (a newer required build, or deleting %s, re-arms "
                       "it)" % STATE_FILE if self.state["update_tripped"] else ""))
        self.save()

    # -- console.log ------------------------------------------------------------------------------------
    def read_console(self, now):
        tails = [self.console] if self.console else self.tails
        for tail in tails:
            for line in tail.lines():
                if self.probe_token and self.probe_token in line and not self.console:
                    self.console = tail
                    log("console.log is live: %s (echo seen %.1f s after sending)" % (tail.path, now - self.probe_sent))
                    self.probe_token = None
                if tail is not self.console:
                    continue
                try:
                    self.workshop.line.console_line(line, now)
                except Exception as error:   # a bug in the line must never cost the freeze recovery below
                    log_once("download-line", "WARNING: the download line (%s: %s) - no line" % (type(error).__name__, error))
                if is_quit_echo(line):
                    if self.armed_pid is not None:
                        log("a quit/restart was sent over RCON - the silence watch stands down until the engine answers "
                            "again")
                    self.armed_pid = None
                    continue
                if is_freeze_signature(line):
                    if self.signature_at is None:
                        log("freeze signature seen: %s" % line.strip()[:120])
                    self.signature_at = self.signature_at or now
                    self.other_level = False
                    continue
                level = level_started(line)
                if level is not None and level != "error":
                    if self.signature_at is not None:
                        log("a new map is loading after the freeze signature (%s) - not a freeze after all" % level)
                    self.signature_at, self.other_level = None, True
                if is_game_over(line):
                    self.match_ends.append(now)
                    self.match_ends = self.match_ends[-10:]

    def probe_console(self, now):
        """Prove a console.log is live: RCON `echo <token>` is logged (sv_logecho) and must show up in it."""
        if self.console or self.probe_tries >= PROBE_TRIES or now < self.probe_next or self.a2s is None:
            if self.probe_token and now - self.probe_sent > 10:
                self.probe_token = None
                log("console.log probe %d of %d: the echo token did not appear within 10 s" % (self.probe_tries,
                                                                                               PROBE_TRIES))
                if self.probe_tries >= PROBE_TRIES:
                    log("ALERT: no console.log showed the echo token after %d tries - freeze recovery is OFF and "
                        "planned restarts wait for an empty server. Check CS2_ADDITIONAL_ARGS (-condebug) and "
                        "CS2_LOG_ECHO (1) in the deployment's compose file." % PROBE_TRIES)
            return
        self.probe_tries += 1
        self.probe_next = now + PROBE_RETRY
        self.probe_token = "update-watch-probe-" + secrets.token_hex(6)
        self.probe_sent = now
        if rcon(["echo " + self.probe_token]) is None:
            self.probe_token = None
            # The session never opened, so nothing was echoed: that says nothing about console.log. At boot the
            # first probe lands about a second after A2S answers, before RCON listens (measured), and
            # counting it would leave freeze recovery off for the 5-minute retry after every container restart.
            if self.probe_rcon_misses < PROBE_RCON_MISSES:
                self.probe_rcon_misses += 1
                self.probe_tries -= 1
                self.probe_next = now + PROBE_RCON_RETRY

    # -- the decisions ------------------------------------------------------------------------------------
    def check_valve(self, now):
        if not AUTOUPDATE:
            return
        dotted = read_patch_version()
        vint = version_int(dotted)
        if vint is None:
            log_once("valve", "WARNING: steam.inf PatchVersion is %r - cannot ask Valve, taking no action" % (dotted,))
            return
        if self.a2s and self.a2s["version"] != dotted and VERSION_RX.match(self.a2s["version"] or ""):
            log_once("valve", "WARNING: A2S reports %s but steam.inf says %s - not deciding on a disagreement"
                     % (self.a2s["version"], dotted))
            return
        if vint == self.verdict_build and now - self.verdict_at < CHECK_EVERY + self.check_backoff:
            return
        answer, detail = valve_check(vint)
        self.verdict, self.verdict_build, self.verdict_at = (answer, detail), vint, now
        if answer == "unknown":
            self.check_backoff = min(21600, max(CHECK_EVERY, self.check_backoff * 2))
            log_once("valve", "WARNING: cannot establish whether %s (%d) is current - %s. No action; next try in %s"
                     % (dotted, vint, detail, hms(CHECK_EVERY + self.check_backoff)))
        else:
            self.check_backoff = 0
            if answer == "stale":
                log_once("valve", "Valve reports %s (%d) as OUT OF DATE, required %d" % (dotted, vint, detail))
            else:
                log_once("valve", "build %s (%d) is current" % (dotted, vint), OK_REPEAT)

    def planned_kind(self, now):
        if AUTOUPDATE and self.verdict[0] == "stale" and self.verdict_build == version_int(read_patch_version()):
            required = self.verdict[1]
            if self.state["update_tripped"] and required > self.state["update_tripped_for"]:
                log("Valve now requires %d, newer than the %d the update breaker latched on - re-arming it"
                    % (required, self.state["update_tripped_for"]))
                self.state.update(update_tripped=0, update_tripped_for=0, update_misses=0, update_retry_after=0)
                self.save()
            if self.state["update_tripped"]:
                log_once("breaker", "ALERT: the build is stale (Valve requires %d) but the update breaker is latched "
                                    "for it - not restarting. Delete %s to re-arm." % (required, STATE_FILE))
            elif now >= self.state["update_retry_after"]:
                return "update"
        if daily_due(now, RESTART_AT, TZ, self.state["done_day"]):
            return "daily"
        if self.prefetch_restart_due(now):
            return "prefetch"
        broken = self.workshop.broken
        if WORKSHOP_RESTART and broken is not None:
            why = workshop_block(self.state, now)
            if why:
                log_once("workshop-limit", "WARNING: workshop downloads are broken in this engine process (%s) - NOT "
                                           "restarting: %s. Only warning." % (broken[1], why), 3600)
                return None
            return "workshop"
        return None

    def prefetch_restart_due(self, now):
        """The restart before tonight's prefetch (03:25 for 03:30): due from PREFETCH_RESTART_LEAD before the prefetch
        window until it closes, unless tonight's prefetch or this restart already happened, or the engine process is
        younger than PREFETCH_FRESH - then it is fresh already, and that is recorded as tonight's restart."""
        if not (PREFETCH_RESTART and PREFETCH_AT):
            return False
        slot, day = prefetch_slot(now)
        if slot is None or self.state.get("prefetch_day") == day or self.state.get("prefetch_restart_day") == day:
            return False
        if self.armed_pid is None or self.a2s is None:
            return False                                     # nothing to judge yet: the engine has not answered
        age = engine_age(self.armed_pid)
        if age is not None and age < PREFETCH_FRESH:
            self.state["prefetch_restart_day"] = day
            self.save()
            log("prefetch restart: not needed tonight - engine pid %d started %s ago, it is fresh already"
                % (self.armed_pid, hms(age)))
            return False
        return True

    def try_planned(self, now):
        kind = self.planned_kind(now)
        if kind is None:
            if self.due:
                log("the %s restart is no longer due (window passed or build current) - standing down" % self.due[0])
            self.due, self.warned = None, False
            return
        if self.due is None or self.due[0] != kind:
            self.due, self.warned = (kind, now), False
            log("%s restart due" % kind)
        why = self.blocked()
        block = planned_block(self.state, now) if why is None else (True, why)
        if block:
            log_once("planned", "%s%s restart due but %s: %s" % ("ALERT: " if block[0] else "", kind,
                                                                 "blocked" if block[0] else "deferred", block[1]))
            return
        if self.a2s is None:
            log_once("planned", "%s restart due, but the engine is not answering A2S - waiting" % kind)
            return
        back = back_in(self.state["durations"])
        warning, final = texts(kind, back)
        if kind in ("workshop", "prefetch") and self.a2s["humans"] > 0:
            log_once("planned", "%s restart due (%s) but %d human(s) on - waiting for an empty server, never a kick"
                     % (kind, "downloads broken: %s" % self.workshop.broken[1] if kind == "workshop"
                        else "a fresh engine process for the nightly prefetch", self.a2s["humans"]), 900)
            return
        if self.a2s["humans"] == 0:
            humans = rcon_humans()
            if humans == 0:
                self.quit(kind, now, final if kind in ("workshop", "prefetch") else None)
            else:
                log_once("planned", "A2S says the server is empty but RCON status says %r - not restarting on a "
                                    "disagreement" % (humans,))
            return
        if not self.console:
            log_once("planned", "%s restart due with %d player(s) on, but no live console.log to see the match end - "
                                "waiting until the server is empty" % (kind, self.a2s["humans"]))
            return
        if not self.warned:
            notice(warning, 600)  # once per due restart, delivered or not: never a retry loop
            self.warned = True
        if any(t >= self.due[1] for t in self.match_ends) and self.match_ends[-1] >= now - 20:
            self.quit(kind, now, final)
        else:
            log_once("planned", "%s restart due, waiting for the end of the current match (%d player(s))"
                     % (kind, self.a2s["humans"]), 900)

    def commit(self, now, kind, required=None):
        """Count a restart on disk BEFORE it happens - the container goes down seconds later. The in-memory state
        only changes if the write succeeded, so a failed write can never leave a half-counted restart behind."""
        new = json.loads(json.dumps(self.state))
        new["restarts"] = [e for e in new["restarts"] if 0 <= now - e[0] < WINDOW] + [[int(now), kind]]
        new["pending"] = {"kind": kind, "at": int(now), "from": read_patch_version() if kind != "freeze" else None,
                          "required": required}
        if kind == "daily":
            new["done_day"] = local_day(now, TZ)
        if kind == "prefetch":
            new["prefetch_restart_day"] = prefetch_slot(now)[1] or local_day(now, TZ)
        previous, self.state = self.state, new
        if self.save():
            return True
        self.state = previous
        log("ALERT: %s restart abandoned - the counters could not be written" % kind)
        return False

    def quit(self, kind, now, final_line):
        """The only place a planned restart happens."""
        if not self.commit(now, kind, self.verdict[1] if kind == "update" else None):
            return
        if final_line:
            notice(final_line, 30)
            time.sleep(QUIT_LEAD)
        log("RESTARTING NOW (%s): RCON quit, so entry.sh ends and Docker re-enters it (SteamCMD runs again). "
            "%d planned restart(s) in 24 h." % (kind, len([e for e in self.state["restarts"]
                                                         if e[1] != "freeze" and now - e[0] < WINDOW])))
        pid = engine_pid()
        self.going_down = now            # from here on the silence is ours: the silence path must not "rescue" it
        if rcon(["quit"], expect_reply=False) is None:
            # Never force an undelivered quit: without its echo the bridge would book a crash on an innocent map.
            self.going_down = None
            log("WARNING: RCON quit could not be delivered - not forcing it; the %s restart stays due and is retried "
                "after the %d min floor" % (kind, PLANNED_MIN_GAP // 60))
            return
        self.due, self.warned = None, False   # done: no "no longer due" line while the container goes down
        for _ in range(QUIT_TIMEOUT * 2):
            if engine_pid() is None:
                return
            time.sleep(0.5)
        if pid and engine_pid() == pid:
            kill_engine(pid, "it was still alive %d s after `quit`" % QUIT_TIMEOUT)

    def try_freeze(self, now):
        """Path a: the error-map signature, confirmed by A2S silence."""
        if not FREEZE_RECOVERY or self.signature_at is None:
            return
        pid = engine_pid()
        if not freeze_confirmed(now, self.signature_at, self.silent_since, pid is not None, self.other_level,
                                FREEZE_GRACE):
            if self.silent_since is None and now - self.signature_at > 60:
                log("the freeze signature was followed by A2S answering - not a freeze; standing down")
                self.signature_at = None
            return
        self.kill(now, pid, "error-map freeze: signature %d s ago, A2S silent for %d s, no other map loading since"
                  % (now - self.signature_at, now - self.silent_since),
                  "frozen engine (signature %d s ago, A2S silent %d s)" % (now - self.signature_at,
                                                                           now - self.silent_since))

    def try_silence(self, now):
        """Path b: silence alone - A2S and RCON both dead for the grace - for the freezes that print nothing."""
        if not FREEZE_RECOVERY or self.silent_since is None or now - self.silent_since < FREEZE_GRACE:
            return
        pid = engine_pid()
        armed = self.console is not None and pid is not None and pid == self.armed_pid
        if not silence_confirmed(now, self.silent_since, self.a2s_fails, self.rcon_since, self.rcon_fails, armed,
                                 pid is not None, self.going_down is not None, FREEZE_GRACE):
            why = ("the watcher is taking it down itself" if self.going_down is not None
                   else "no engine process" if pid is None
                   else "no live console.log, so a quit or restart could not be told apart" if self.console is None
                   else "this engine process has not answered A2S since it started, or a quit/restart was sent"
                   if pid != self.armed_pid
                   else "RCON still answers" if self.rcon_since is None
                   else "%d A2S and %d RCON polls failed so far, %d needed" % (self.a2s_fails, self.rcon_fails,
                                                                                SILENT_POLLS)
                   if min(self.a2s_fails, self.rcon_fails) < SILENT_POLLS else "RCON silent since %s only"
                   % at_clock(self.rcon_since))
            log_once("silent", "A2S silent since %s - not killing: %s" % (at_clock(self.silent_since), why), 300)
            return
        self.kill(now, pid, "silent freeze: A2S silent for %d s (%d polls) and RCON for %d s (%d polls), no error-map "
                  "signature" % (now - self.silent_since, self.a2s_fails, now - self.rcon_since, self.rcon_fails),
                  "silent engine (A2S %d s, RCON %d s)" % (now - self.silent_since, now - self.rcon_since))

    def kill(self, now, pid, why, what):
        """The one place a freeze kill happens, for both paths: the same gates, the same counters, the same record."""
        blocked = self.blocked()
        block = freeze_block(self.state, now) if blocked is None else (True, blocked)
        if block:
            log_once("freeze", "%s%s but %s: %s" % ("ALERT: " if block[0] else "", what,
                                                     "not killing it" if block[0] else "waiting", block[1]))
            return
        if not self.commit(now, "freeze"):
            return
        self.going_down = now
        kill_engine(pid, why)
        self.signature_at = None

    # -- the loop -----------------------------------------------------------------------------------------
    def poll(self, now):
        """A2S every A2S_EVERY s. While it fails, ask RCON too (auth only), so path b sees both. Each keeps a streak:
        since when it has failed, and on how many polls in a row."""
        if now - self.a2s_at < A2S_EVERY:
            return
        self.a2s_at = now
        self.a2s = a2s_probe()
        if self.a2s is not None:
            self.silent_since, self.a2s_fails, self.rcon_since, self.rcon_fails = None, 0, None, 0
            pid = engine_pid()
            if pid is not None and pid != self.armed_pid and self.going_down is None:
                self.armed_pid = pid
                log("silence watch armed for engine pid %d" % pid)
            if self.first_answer is None:
                self.first_answer = now
                log("engine answering A2S: %s on %s, %d human(s)" % (self.a2s["version"], self.a2s["map"],
                                                                    self.a2s["humans"]))
            return
        self.silent_since = self.silent_since or now
        self.a2s_fails += 1
        if not FREEZE_RECOVERY or self.armed_pid is None:
            return
        if rcon_alive():
            self.rcon_since, self.rcon_fails = None, 0
        else:
            self.rcon_since = self.rcon_since or now   # the poll's start: A2S and RCON failed on the same polls
            self.rcon_fails += 1

    def tick(self, now):
        """One pass of the loop, everything but the sleep."""
        self.read_console(now)
        suspect = self.signature_at is not None
        self.poll(now)
        now = time.time()                # a poll of a dead engine takes up to 2.5 s; judge on the clock after it
        self.verify_pending(now)
        self.probe_console(now)
        try:
            self.check_valve(now)
        except Exception as error:
            log_once("valve", "WARNING: the Valve check crashed (%s: %s) - no action" % (type(error).__name__, error))
        self.try_freeze(now)
        self.try_silence(now)
        if not suspect:
            self.try_planned(now)
        try:
            self.workshop.step(time.time())
        except Exception as error:   # a bug here must never cost the restarts and the freeze recovery above
            log_once("workshop", "WARNING: the warm-up/prefetch step crashed (%s: %s) - no action"
                     % (type(error).__name__, error))

    def log_depotcache(self):
        """How many verified manifests the Steam client starts with - the one number that says whether this process's
        first download re-checks every installed item (many minutes for a large collection, and a way into the stuck state)."""
        count, mounted, writable = depotcache_state()
        try:
            with open(WORKSHOP_ACF, "r", encoding="utf-8") as handle:
                installed = len(acf_installed(handle.read()))
        except (OSError, ValueError):
            installed = None
        log("workshop: %s manifests in %s for %s installed items (%s, %s)"
            % (count if count is not None else "no", DEPOTCACHE, installed if installed is not None else "?",
               "a mounted volume" if mounted else "NOT a mounted volume", "writable" if writable else "NOT writable"))
        if not mounted or not writable:
            log("WARNING: the depotcache %s - a new container starts without the manifests, so its first download "
                "re-checks every installed item, a way into the stuck state (mount it as a volume, as deploy/compose.example.yml does)"
                % ("is not a mounted volume" if not mounted else "is not writable for this user"))
        elif count is not None and installed and count < installed // 2:
            log("WARNING: the depotcache holds %d manifests for %d installed items - the first download in this process "
                "re-checks the rest (up to ~15 min)" % (count, installed))

    def run(self):
        for warning in BOOT_WARNINGS:
            log("WARNING: " + warning)
        log("started: updates %s (Valve check every %s), daily restart %s, freeze recovery %s (after %d s of silence: "
            "A2S after the error-map signature, or A2S and RCON on %d+ polls each without it); planned restarts >= %d "
            "min apart and <= %d per 24 h, freeze kills >= %d min apart and <= %d per 24 h"
            % ("on" if AUTOUPDATE else "OFF", hms(CHECK_EVERY), "%02d:%02d" % RESTART_AT if RESTART_AT else "off",
               "on" if FREEZE_RECOVERY else "OFF", FREEZE_GRACE, SILENT_POLLS, PLANNED_MIN_GAP // 60,
               PLANNED_MAX_PER_DAY, FREEZE_MIN_GAP // 60, FREEZE_MAX_PER_DAY))
        log("workshop: warm-up %s, nightly prefetch %s" % (
            ("item %s, %d s after the engine answers" % (WARMUP_ITEM, WARMUP_DELAY)) if WARMUP and WARMUP_ITEM else "off",
            ("%02d:%02d for %s, only while nobody is on" % (PREFETCH_AT + (hms(PREFETCH_SPAN),))) if PREFETCH_AT else "off")
            + ", download restart %s" % ("on (only at 0 humans, <= 1 per %s and %d per 24 h)" % (hms(WORKSHOP_RESTART_GAP),
                                                                                      WORKSHOP_RESTART_MAX)
                                         if WORKSHOP_RESTART else "off")
            + ", fresh process for the prefetch %s" % (
                "on (%s, only at 0 humans)" % datetime.fromtimestamp(
                    next_slot(time.time(), PREFETCH_AT, TZ) - PREFETCH_RESTART_LEAD, TZ).strftime("%H:%M")
                if PREFETCH_RESTART and PREFETCH_AT else "off")
            + ", the queued maps in the background %s" % (("on, reported to the bridge at %s" % self.workshop.ready.endpoint[0])
                                                          if READY and self.workshop.ready.endpoint else "on, no bridge address"
                                                          if READY else "off")
            + ", maps outside the keep set at engine start: %s" % {"on": "deleted", "dryrun": "listed only (dry run)",
                                                                  "off": "kept"}[MAPS_DELETE])
        if WARMUP or PREFETCH_AT:
            self.log_depotcache()
        if self.blocked():
            log("ALERT: RESTARTS ARE DISABLED - %s. Observing only." % self.blocked())
        for key, what in (("update_tripped", "update"), ("freeze_tripped", "freeze")):
            if self.state[key]:
                log("ALERT: the %s breaker is latched since %s - delete %s to re-arm"
                    % (what, time.strftime("%Y-%m-%d %H:%M", time.localtime(self.state[key])), STATE_FILE))
        last_status = 0
        while not STOPPING:
            now = self.loop_once()
            if now - last_status >= OK_REPEAT:
                last_status = now
                log("status: A2S %s, console.log %s, silence watch %s, verdict %s, %d restart(s) in 24 h"
                    % ("answering (%d human(s))" % self.a2s["humans"] if self.a2s else "silent",
                       "live" if self.console else "not proven",
                       "armed (pid %d)" % self.armed_pid if self.armed_pid else "not armed", self.verdict[0],
                       len([e for e in self.state["restarts"] if now - e[0] < WINDOW])))
        log("stopping")

    def loop_once(self):
        """One tick, paced to TICK. A tick that polled a dead engine already took longer than that, so the next poll
        follows at once: while silent the probes' own timeouts set the pace (a poll every 2.5 s), never a spin.
        The scenario tests drive this very method."""
        now = time.time()
        self.tick(now)
        time.sleep(max(0.0, TICK - (time.time() - now)))
        return now


def main():
    if sys.argv[1:] == ["--warmup-reset"]:
        return warmup_reset()
    if sys.argv[1:] == ["--organize"]:
        log(organize())
        return 0
    if not (AUTOUPDATE or FREEZE_RECOVERY or RESTART_AT or (WARMUP and WARMUP_ITEM) or PREFETCH_AT):
        log("CS2_AUTOUPDATE=0, CS2_FREEZE_RECOVERY=0, CS2_RESTART_AT=off and no warm-up or prefetch: nothing to do, exiting")
        return 0
    signal.signal(signal.SIGTERM, lambda *_: STOPPING.append(True))
    lock = None
    if persistence_selftest() is None:
        try:
            lock = open(LOCK_FILE, "w")
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            log("another update-watch already holds %s, exiting" % LOCK_FILE)
            return 0
    Watch().run()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # never die silently: the 44-hour outage happened because nobody was told
        log("ALERT: the watcher crashed - no update checks, daily restarts or freeze recovery until the container "
            "restarts (%s: %s)" % (type(error).__name__, error))
        raise
