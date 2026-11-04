#!/usr/bin/env python3
"""workshop-sync.py - the Workshop sync of the CS2 server: new Arms Race, Deathmatch and Wingman maps, the Workshop's scores, and
the keep set's maps for the game server.

The main process of the `sync` service in deploy/compose.example.yml (its own container, the game server's own image), bind-
mounted read-only at /home/steam/cs2-workshop-sync.py (source of truth: server/ in this repo, like update-watch.py).
Python 3.9 compatible. Every path, address, id and limit below comes from the environment (see CONFIG); the code holds
no infrastructure detail, so it can be published as it is.

NO TIME WINDOWS (the owner's decision of 2026-11-03 - the sync works at any hour while nobody plays): the service starts a run
whenever the game server has had nobody on for SYNC_QUIET_MINUTES and there is work - maps waiting for a download or a test, or the
API steps due - and a run lasts SYNC_RUN_HOURS (3), cut short before the game server's restarts. Steps 1 to 2b run at most every
SYNC_API_EVERY_HOURS (4), and when nothing waits. Steps 3 to 5 repeat in batches of SYNC_BATCH maps until the backlog or the run
ends, or Steam pushes back, so a long run publishes as it goes and staging stays small. When somebody joins, the run stops
downloading and testing within seconds and waits; after a run that did nothing, the service looks again after IDLE_WAIT:
  0. KEEP CLEAR OF THE PREFETCH AND THE RESTARTS. The restart watcher's nightly prefetch (update-watch.py) runs from
     SYNC_PREFETCH_AT (03:30) in the game server's own engine: from 10 minutes before it until the watcher stamps tonight's
     prefetch done (its state.json, prefetch_day; SYNC_PREFETCH_MINUTES at most) no run starts, and a running one downloads and
     tests nothing - the two never fetch from Steam at once. No run starts from 15 minutes before the daily restart
     (SYNC_DAILY_RESTART, 07:00) to 10 after, and every run ends 15 minutes before it and 10 before the prefetch. Nor does
     one start with under MIN_RUN (20 min) left before such a cut: a run of minutes only writes a report (the owner's 85).
  1. FIND NEW MAPS. IPublishedFileService/QueryFiles (api.steampowered.com, the owner's Web API key - the only call that
     needs one: without it the API answers 403, checked 2026-11-01): app 730, any of the tags SYNC_TAGS (Armsrace,
     Deathmatch, Wingman), newest first, 100 a page, the cursor from page to page, and it stops at the first page that
     holds nothing it has never seen. No key file: one log line, and the step is skipped; everything else runs.
  2. CHECK THE MAPS WE LIST. ISteamRemoteStorage/GetPublishedFileDetails (no key), 100 ids a call: every map on
     maps.json and on the mode lists, and every candidate before its download. The verdicts (ok, or gone: result not 1,
     hidden, banned) are handed to the bridge (POST /removed), so a map that is gone is blocked at once - the only Workshop
     check the bridge hears since the owner's 74 a (2026-11-04), the plugin's daily one gone; a map that goes says so once in
     the history. A map updated since we last saw it is downloaded and tested again, after the new ones.
  2b. THE RATINGS (since 2026-11-03). Once every RATINGS_EVERY, and at once when the ratings kept lack a list: the score
     (vote_data.score, the Workshop's "Top Rated") and the current subscriptions ("Most Subscribed") of every listed map and every
     map waiting for its test - IPublishedFileService/GetDetails with includevotes (the key), 100 ids a call - and each mode's
     "Most Popular" order, Steam's trend over the last TREND_DAYS (30) days (QueryFiles RankedByTrend by the mode's tag; the API
     takes 30, its order differs from 7's and 90's). Kept in the state and written to the bridge's data as ratings.json, from which
     THE KEEP SET (keepset.py, shared with update-watch.py) takes each mode's three lists of 100 best playable maps
     (the owner's 35). A run cut short keeps the old time.
  2c. THE HAND-OVER. The keep set's maps the game server does not store (its acf and folders, read on the read-only mount of its
     tree) - the upvoted maps, each mode's best-rated, the own maps; never the random queues, which the server fetches itself - are
     downloaded with SteamCMD (step 3's session rules and stops) and handed over at once: hand_over moves each into
     <work>/handover/<id>/ with its manifest and, last, its marker <id>.json, and the game server's --organize takes it in at its
     next engine start. A map tested in step 4 that the keep set holds (its score puts it among its mode's best once it is listed)
     is handed over the same way instead of being deleted with the rest of the staging folder.
  3. DOWNLOAD with SteamCMD's anonymous login - never the owner's account, which is logged in nowhere - into this
     container's own staging folder: one SteamCMD session a batch, one item at a time, SYNC_DOWNLOAD_PAUSE between. First every
     waiting map that would make one of its mode's three lists (the keep set's cut-offs; best score first), then new uploads
     (created in the last 14 days, newest first), then listed maps that changed, then everything else strictly by subscribers,
     whatever the mode. Nothing is skipped for its size: there is no daily download cap (the owner's 36 c, 2026-11-03), and the
     maps the old caps parked as too big are queued again (the owner's 33: 223 popular maps). The bug brake instead: a map
     downloaded DOWNLOADS_PER_MAP_DAY (2) times in a day is not downloaded again that day. A stall - SteamCMD silent about an
     item for 15 minutes - is routine once: a fresh session takes the next map, and the map that stalled waits for the next
     run; a second stall in a run ends that run's downloads (the owner's 86 a, 2026-11-04).
  4. SCAN AND LOAD-TEST. The scan reads each download's VPKs: a map, a model pack, an empty upload or a CS:GO-era one,
     and the commands its entity lump and cfg files carry (connect <ip>, `say connect ...`, gambling promo codes). The
     maps then load, one by one, in a TEST COPY OF THE SERVER: a second CS2 engine in this container, started from a
     read-only view of the game server's own install (a disposable tree of links, the engine's few files copied, no
     plugins), with its own port, RCON password, join password and Workshop folder (the staging folder), its own
     anonymous game-server logon, no log push and no access to anything the game server uses. Each map in the mode its
     tags name (Arms Race first, then Deathmatch, then Wingman). PASS: a level of its own loads, A2S reports it and the
     engine holds it SYNC_HOLD_SECONDS with two bots. FAIL: the error map, a crash, a freeze (A2S silent 45 s), a stock
     map instead of its own (a stub), no level of its own in time, or the bots thrown off. Up to 3 loads a map.
  5. PUBLISH into the bridge's /data (the bridge reads its files again by itself about 10 s after they change, and this
     run asks it to at once with POST /reload): the Arms Race passes appended to maps.json (and search.json), the
     Deathmatch and Wingman passes in maps-deathmatch.json and maps-wingman.json (the maps.json entry shape, rank 1..N by popularity; their texts in search.json too), the crashers, the maps with ads and the non-maps
     on broken.json with their reasons. Previous copies are kept under
     <state>/backups/. THE MAIL is for problems only (the owner's 34, 2026-11-03, issues_of): a run stopped early or a step that errored, Steam pushing back, the test engine failing
     to start, a safety check refusing, the disk running low (SYNC_DISK_LOW_GB), a failed hand-over, the check's verdicts not
     reaching the bridge, SteamCMD stalling twice in a run or twice on one map in a day (one stall is routine), any other
     error, a run that crashed. Each mail says what went wrong and what the sync did about it, and points to the dashboard; a problem that
     persists is mailed again once a day at most (MAIL_EVERY). A run without one mails nothing: its report stays in
     <state>/reports/ and on the dashboard.

SAFETY (the owner's rules of 2026-11-01, enforced here, not by convention)
  * The owner's Steam account is never logged in anywhere: SteamCMD and the test engine log on anonymously.
  * No steamcommunity.com request at all: only api.steampowered.com (the Web API) and Steam's own clients.
  * One request at a time: every Web API call goes through one function, in one thread, at least SYNC_API_GAP_SECONDS
    (2 s, never less) after the previous one, with an honest User-Agent naming this tool.
  * HTTP 429 or 403 (401 too) stops every API step for the rest of the day at once; so do 3 server errors (5xx) in a
    row, 5 in a run, or 5 network failures in a run. The stop is written to the ledger, so a restart cannot resume it, and the
    mail says so. An HTTP 429, 403 or 401 ends the day's downloads
    too. SteamCMD: a rate-limit answer, two access-denied answers in a row (one is about that item: 1 of 2,129 on 2026-10-05),
    3 failed downloads in a row, or a second stall in a run, end the downloads.
  * HARD CAPS on the Web API, persistent (<state>/api-ledger.json, written and flushed BEFORE each call, so a crash never
    loses one): at most 10,000 Web API calls in any rolling 24 hours (10 % of Valve's 100,000 a day per key; the
    configuration can only lower it) and SYNC_API_RUN_CAP (500) a run. An unreadable ledger means no calls at all.
  * NO DAILY DOWNLOAD CAP (the owner's 36 c, 2026-11-03; 1,500 items and 150 GB a day until then): only Steam's push-back
    stops the downloads - an HTTP 429, 403 or 401, a SteamCMD rate-limit answer or two Access Denied in a row end the day's
    downloads (booked in state.json, so every later run of that day keeps to it), and 3 failed downloads in a row end the
    run's. Still one anonymous SteamCMD download at a time, SYNC_DOWNLOAD_PAUSE apart. The bug brake: a map downloaded
    twice in a day is not downloaded a third time that day.
  * Only while nobody plays: A2S of the game server must show 0 humans (an unanswered query counts as somebody), before
    every download, every test load and the publish. When somebody joins, the test engine is stopped at once (a second or
    two later) and no new download starts; the run goes on only after SYNC_QUIET_MINUTES with nobody on, because a map
    change shows 0 humans for a moment.
  * The test engine never touches the game server: read-only install, its own folders, no plugins, `log off`, no log
    address (checked over RCON before the first load), and it stops with its run, before the daily restart and the prefetch. A
    game update during a test (steam.inf changes) ends the run's tests.

FILES (<state> = SYNC_STATE_DIR, kept and backed up; <work> = SYNC_WORK_DIR, disposable)
  <state>/state.json        every item seen (its verdict, attempts, the details we know), the update baseline of the
                            listed maps, the last run and when the API steps last ran, the day's downloads. Readable.
  <state>/api-ledger.json   the Web API calls of the last 24 h, the count per day, the day's stop.
  <state>/reports/<day>-<HHMM>.json and .txt   a run's full result and its summary text (-manual: a run-now), mail or no mail.
  <state>/mail-sent.json    when each problem was last mailed (its key: issues_of), so a persisting one goes once a day.
  <state>/backups/<stamp>/  the bridge's files as they were before each publish.
  <state>/run-now           create it (any content, or JSON with lower limits; {"api": true} runs the API steps whatever their
                            cadence) to start a run at once.
  <work>/staging/           SteamCMD's install dir: steamapps/workshop is the test engine's Workshop folder.
  <work>/handover/          maps for the game server (step 2c): <id>/, <id>.manifest, <id>.json last; its --organize takes them in.
  <work>/farm/              the test engine's disposable game tree, rebuilt before every test session.
  <bridge>/sync-status.json THE STATUS FILE for the in-game dashboard (the owner's 34), schema 1 (class Status): what the sync does
                            now and why, today's counts, the totals, the queue and its next maps, the keep set's parts, the
                            history of runs and map events. Written atomically at every phase change, every STATUS_TICK (15) s
                            during a run, and every minute while idle, waiting or stopped. The bridge does not watch it.
  <bridge>/ratings.json     step 2b's lists, version 1: {"fetched": unix time, "scores": {id: [score, votes up, votes down,
                            subscriptions]}, "trend": {mode: [ids, Most Popular first]}, "trend_days": 30}.

CONFIG (environment of the sync service; defaults in brackets)
  SYNC_ENABLED [1], SYNC_PUBLISH [1], SYNC_RUN_HOURS [3], SYNC_API_EVERY_HOURS [4], SYNC_PREFETCH_AT [03:30],
  SYNC_PREFETCH_MINUTES [195], SYNC_DAILY_RESTART [07:00], SYNC_TAGS [Armsrace,Deathmatch,Wingman], SYNC_EXCLUDE_IDS (never candidates: the
  warm-up item, the HUD items), SYNC_EXCLUDE_CREATORS (Workshop authors whose items are listed by hand), SYNC_CHANGED_SINCE [2026-10-25] (a listed map updated after it counts as changed at the
  first check), SYNC_MODE_BACKFILL [0] (1: listed Arms Race maps that carry a mode's tag join that mode's list too,
  which this sync never loaded; off, a mode list holds only maps this sync load-tested itself),
  SYNC_API_RUN_CAP [500], SYNC_API_DAY_CAP [10000], SYNC_API_GAP_SECONDS [2], SYNC_MAX_PAGES [200],
  SYNC_DOWNLOAD_PAUSE [2], SYNC_HOLD_SECONDS [20], SYNC_BATCH [100], SYNC_QUIET_MINUTES [10],
  SYNC_TEST_PORT [27215], SYNC_ENGINE_STARTS_MAX [200], SYNC_STATE_DIR, SYNC_WORK_DIR, SYNC_GAME_DIR,
  SYNC_BRIDGE_DATA, SYNC_BRIDGE_URL, SYNC_BRIDGE_SECRET, SYNC_GAME_HOST, SYNC_GAME_PORT, SYNC_WATCHER_STATE,
  SYNC_CFG_DIR, STEAM_WEBAPI_KEY_FILE, SYNC_SMTP_HOST, SYNC_SMTP_PORT [25], SYNC_MAIL_FROM, SYNC_MAIL_TO, SYNC_DASHBOARD_URL, SYNC_DISK_LOW_GB [100], TZ,
  SYNC_RATINGS [1] (step 2b), SYNC_HANDOVER [1] (step 2c and the tested hand-overs), SYNC_HANDOVER_DIR [<work>/handover],
  SYNC_DEPOTCACHE [~/Steam/depotcache: where SteamCMD keeps the manifests a hand-over carries].

    python3 workshop-sync.py              the service: a run whenever nobody plays and there is work (or at once: run-now)
    python3 workshop-sync.py --once       one run now, then exit (the same rules)
    python3 workshop-sync.py --status     print the ledger's counters and the state's totals
    python3 workshop-sync.py --export     what it has published, as entries a republish from the repo can merge
"""

import calendar
import email.utils
import html
import json
import os
import re
import secrets
import select
import shutil
import signal
import smtplib
import socket
import struct
import subprocess
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime

try:
    from zoneinfo import ZoneInfo
except ImportError:  # pragma: no cover - Python < 3.9
    ZoneInfo = None

VERSION = "1"
USER_AGENT = "cs2-workshop-sync/%s (Workshop sync of a private CS2 server; server/workshop-sync.py)" % VERSION

# ------------------------------------------------------------------------------------------------
# The owner's hard limits on the Web API (2026-11-01). The environment may lower them, never raise them. The daily download caps went
# with the owner's 36 c (2026-11-03); the bug brake stands in for them.
# ------------------------------------------------------------------------------------------------
HARD_API_DAY_CAP = 10000          # 10 % of Valve's 100,000 Web API calls a day per key, over any rolling 24 hours
HARD_API_GAP = 2.0                # seconds between two Web API calls, at least
DOWNLOADS_PER_MAP_DAY = 2         # the bug brake: a map downloaded this often today is not downloaded again today (no daily caps since the owner's 36 c)
QUEUE_CHECK = 3000                # the check (step 2) asks the Workshop about this many of the download queue's head
HOURS_A_DAY = 22                  # what the estimate counts on: the day without the prefetch's and the restarts' stretches; play comes off it
MAIL_EVERY = 20 * 3600            # the mail is for problems only, and one that persists is mailed again once this long has passed
IDLE_WAIT = 1800                  # after a run that did nothing, the service waits this long before it looks again
MIN_RUN = 1200                    # no automatic run starts with less than this before its cut: a run of minutes does nothing (the owner's 85)
API_5XX_ROW = 3                   # server errors in a row that stop the day's API steps
API_5XX_RUN = 5                   # server errors in a run that stop them
API_NETFAIL_RUN = 5               # network failures (timeouts, resets) in a run that stop them
API_RETRY_WAIT = (30, 90)         # a failed call is tried again after these pauses, then given up
DAY = 86400

QUERY_URL = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/"
DETAILS_URL = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
VOTES_URL = "https://api.steampowered.com/IPublishedFileService/GetDetails/v1/"   # with includevotes: vote_data.score (the key needed)
DETAILS_BATCH = 100
KEYED_APIS = ("QueryFiles", "GetDetails")   # the calls that carry the Web API key; GetPublishedFileDetails needs none
RATINGS_EVERY = 20 * 3600         # the lists are fetched again once this old: once a day, by whichever run comes then
TREND_DAYS = 30                   # Most Popular's window: the trend over the last 30 days (the API takes it, 2026-11-03)
TREND_PAGES = 20                  # pages of 100 a mode's Most Popular walk reads at most...
TREND_WANT = 150                  # ...stopping once this many of the mode's listed maps were in it (100 stay, the rest is slack)
ACF_WAIT = 20                 # seconds serve waits for SteamCMD's acf entry of a download (it came 1.0-1.4 s after Success, 2026-11-03)
HANDOVER_MAX_BYTES = 200 << 30    # hand-overs the server has not taken in yet: past this much, no more until it does

MODES = (  # (id, Workshop tag, game_type, game_mode, the list file beside maps.json)
    ("armsrace", "Armsrace", 1, 0, "maps.json"),
    ("deathmatch", "Deathmatch", 1, 2, "maps-deathmatch.json"),
    ("wingman", "Wingman", 0, 2, "maps-wingman.json"),
)
MODE_LISTS = tuple((mode, fname) for mode, _tag, _gt, _gm, fname in MODES)   # (mode, its list file beside maps.json)


# ------------------------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------------------------

def env_int(name, default, low, high):
    raw = os.environ.get(name, "")
    try:
        value = int(raw) if raw.strip() else default
    except ValueError:
        value = default
    return max(low, min(high, value))


def env_float(name, default, low, high):
    raw = os.environ.get(name, "")
    try:
        value = float(raw) if raw.strip() else default
    except ValueError:
        value = default
    return max(low, min(high, value))


def env_flag(name, default):
    raw = (os.environ.get(name) or "").strip().lower()
    if raw in ("1", "true", "yes", "on"):
        return True
    if raw in ("0", "false", "no", "off"):
        return False
    return default


def env_hhmm(name, default):
    """HH:MM from the environment, as (hour, minute); anything else (empty, off) gives the default."""
    raw = (os.environ.get(name) or "").strip()
    m = re.match(r"^(\d{1,2}):(\d{2})$", raw)
    if m and int(m.group(1)) < 24 and int(m.group(2)) < 60:
        return int(m.group(1)), int(m.group(2))
    return default


class Config(object):
    def __init__(self, env=None):
        if env is not None:
            os.environ.update(env)
        self.enabled = env_flag("SYNC_ENABLED", True)
        self.publish = env_flag("SYNC_PUBLISH", True)
        self.prefetch_at = env_hhmm("SYNC_PREFETCH_AT", (3, 30))            # the watcher's CS2_PREFETCH_AT: no run from 10 min before
        self.prefetch_span = env_int("SYNC_PREFETCH_MINUTES", 195, 10, 1200) * 60   # ...until it stamps tonight's done, at most this long
        self.daily_restart = env_hhmm("SYNC_DAILY_RESTART", (7, 0))         # the watcher's CS2_RESTART_AT: no run 15 min before to 10 after
        self.run_hours = env_int("SYNC_RUN_HOURS", 3, 1, 12)               # a run's length; the service starts the next while nobody plays
        self.api_every = env_int("SYNC_API_EVERY_HOURS", 4, 1, 48) * 3600   # discovery, the check and the ratings at most this often
        self.tags = [t.strip() for t in (os.environ.get("SYNC_TAGS") or "Armsrace,Deathmatch,Wingman").split(",") if t.strip()]
        self.exclude = set(x for x in re.split(r"[,\s]+", os.environ.get("SYNC_EXCLUDE_IDS") or "") if x.isdigit())
        self.exclude_creators = set(x for x in re.split(r"[,\s]+", os.environ.get("SYNC_EXCLUDE_CREATORS") or "") if x.isdigit())
        self.changed_since = os.environ.get("SYNC_CHANGED_SINCE") or "2026-10-25"
        self.mode_backfill = env_flag("SYNC_MODE_BACKFILL", False)
        self.api_day_cap = env_int("SYNC_API_DAY_CAP", HARD_API_DAY_CAP, 0, HARD_API_DAY_CAP)
        self.api_run_cap = env_int("SYNC_API_RUN_CAP", 500, 0, self.api_day_cap)
        self.api_gap = env_float("SYNC_API_GAP_SECONDS", HARD_API_GAP, HARD_API_GAP, 600.0)
        self.max_pages = env_int("SYNC_MAX_PAGES", 200, 1, 2000)
        self.download_pause = env_float("SYNC_DOWNLOAD_PAUSE", 2.0, 2.0, 600.0)
        self.batch = env_int("SYNC_BATCH", 100, 1, 1500)
        self.quiet = env_int("SYNC_QUIET_MINUTES", 10, 0, 240) * 60
        self.hold = env_int("SYNC_HOLD_SECONDS", 20, 5, 300)
        self.test_port = env_int("SYNC_TEST_PORT", 27215, 1024, 65535)
        self.engine_starts_max = env_int("SYNC_ENGINE_STARTS_MAX", 200, 1, 400)  # a run's; each is one anonymous game-server logon
        self.state_dir = os.environ.get("SYNC_STATE_DIR") or "/home/steam/sync-state"
        self.work_dir = os.environ.get("SYNC_WORK_DIR") or "/home/steam/workshop-sync"
        self.game_dir = os.environ.get("SYNC_GAME_DIR") or "/home/steam/cs2-dedicated"
        self.bridge_data = os.environ.get("SYNC_BRIDGE_DATA") or "/home/steam/bridge-data"
        self.bridge_url = (os.environ.get("SYNC_BRIDGE_URL") or "").rstrip("/")
        self.bridge_secret = os.environ.get("SYNC_BRIDGE_SECRET") or ""
        self.game_host = os.environ.get("SYNC_GAME_HOST") or ""
        self.game_port = env_int("SYNC_GAME_PORT", 27015, 1, 65535)
        self.watcher_state = os.environ.get("SYNC_WATCHER_STATE") or os.path.join(self.game_dir, "update-watch", "state.json")
        self.cfg_dir = os.environ.get("SYNC_CFG_DIR") or ""
        self.key_file = os.environ.get("STEAM_WEBAPI_KEY_FILE") or "/run/secrets/steam-webapi-key.txt"
        self.smtp_host = os.environ.get("SYNC_SMTP_HOST") or ""
        self.smtp_port = env_int("SYNC_SMTP_PORT", 25, 1, 65535)
        self.mail_from = os.environ.get("SYNC_MAIL_FROM") or ""
        self.mail_to = [a.strip() for a in (os.environ.get("SYNC_MAIL_TO") or "").split(",") if a.strip()]
        self.tzname = os.environ.get("TZ") or "UTC"
        self.steamcmd = os.environ.get("SYNC_STEAMCMD") or "/home/steam/steamcmd/steamcmd.sh"
        self.ratings = env_flag("SYNC_RATINGS", True)      # step 2b: the Workshop scores, once a day
        self.force_api = False
        self.skip_api = False
        self.disk_low = env_int("SYNC_DISK_LOW_GB", 100, 1, 100000) << 30   # free on the game's volume under this: a problem mailed
        self.dashboard_url = (os.environ.get("SYNC_DASHBOARD_URL") or "").strip()   # the one place it is set (the owner's 85)
        self.handover = env_flag("SYNC_HANDOVER", True)    # the keep set's maps handed over to the game server
        self.handover_dir = os.environ.get("SYNC_HANDOVER_DIR") or os.path.join(self.work_dir, "handover")
        self.depotcache = os.environ.get("SYNC_DEPOTCACHE") or os.path.join(os.environ.get("HOME") or "/home/steam", "Steam", "depotcache")

    def lower(self, overrides):
        """run-now may only lower a limit for its run, never raise one."""
        for key, attr in (("api_run_cap", "api_run_cap"), ("max_pages", "max_pages"), ("batch", "batch")):
            if isinstance(overrides.get(key), int) and overrides[key] >= 0:
                setattr(self, attr, min(getattr(self, attr), overrides[key]))
        if overrides.get("publish") is False:
            self.publish = False
        if overrides.get("discover") is False:
            self.key_file = ""
        if overrides.get("api") is True:
            self.force_api = True                       # discovery, the check and the ratings this run, whatever the cadence
        if overrides.get("api") is False:
            self.skip_api = True                        # ...or not at all in this run
        if isinstance(overrides.get("only_ids"), list):
            self.only_ids = [str(x) for x in overrides["only_ids"] if str(x).isdigit()]


# ------------------------------------------------------------------------------------------------
# Small helpers: time, logging, JSON files
# ------------------------------------------------------------------------------------------------

def tz_of(name):
    if ZoneInfo is not None:
        try:
            return ZoneInfo(name)
        except Exception:
            return None
    return None


class Clock(object):
    """Local time for the restarts' times and the day's name; injectable for the tests."""

    def __init__(self, tzname, now=None):
        self.tz = tz_of(tzname)
        self._now = now or time.time

    def now(self):
        return self._now()

    def local(self, ts=None):
        ts = self.now() if ts is None else ts
        return datetime.fromtimestamp(ts, self.tz) if self.tz else datetime.fromtimestamp(ts)

    def day(self, ts=None):
        return self.local(ts).strftime("%Y-%m-%d")

    def at(self, hhmm, ts=None):
        """Today's hh:mm (local) as a Unix time."""
        d = self.local(ts).replace(hour=hhmm[0], minute=hhmm[1], second=0, microsecond=0)
        return d.timestamp()

    def in_window(self, start, end, ts=None):
        ts = self.now() if ts is None else ts
        return self.at(start, ts) <= ts < self.at(end, ts)


LOG_FILE = [None]


def log(message):
    line = "%s workshop-sync: %s" % (time.strftime("%Y-%m-%d %H:%M:%S"), message)
    print(line, flush=True)
    path = LOG_FILE[0]
    if path:
        try:
            if os.path.exists(path) and os.path.getsize(path) > (10 << 20):
                os.replace(path, path + ".1")
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass


def read_json(path, default=None):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return default


def write_atomic(path, text, mode=0o666):
    """Write beside the target, flush to disk, then rename over it: a reader sees the old file or the new one."""
    folder = os.path.dirname(os.path.abspath(path))
    tmp = os.path.join(folder, ".%s.tmp-%d" % (os.path.basename(path), os.getpid()))
    data = text.encode("utf-8") if isinstance(text, str) else text
    with open(tmp, "wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    try:
        os.chmod(tmp, mode)
    except OSError:
        pass
    os.replace(tmp, path)


def dumps(obj, indent=1):
    return json.dumps(obj, indent=indent, ensure_ascii=False) + "\n"


def hms(seconds):
    seconds = int(max(0, seconds))
    return "%dh%02dm" % (seconds // 3600, seconds % 3600 // 60) if seconds >= 3600 else (
        "%dm%02ds" % (seconds // 60, seconds % 60) if seconds >= 60 else "%ds" % seconds)


def mb(nbytes):
    return "%.1f GB" % (nbytes / 1073741824.0) if nbytes >= 1073741824 else "%.0f MB" % (nbytes / 1048576.0)


# Text as a player may read it: no control characters, no bidi overrides.
CTRL_RX = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]")
BIDI_RX = re.compile("[‪-‮⁦-⁩]")
WS_RX = re.compile(r"\s+")
BBCODE_RX = re.compile(r"\[/?[a-zA-Z0-9\*]+(?:=[^\]]*)?\]")
IMG_RX = re.compile(r"\[img\].*?\[/img\]", re.I | re.S)
TAG_RX = re.compile(r"<[^>]+>")


def clean(value, limit=None):
    text = CTRL_RX.sub(" ", str(value if value is not None else ""))
    text = BIDI_RX.sub("", text)
    text = WS_RX.sub(" ", text).strip()
    if limit and len(text) > limit:
        cut = text[:limit].rstrip()
        space = cut.rfind(" ")
        if space > limit * 0.6:
            cut = cut[:space].rstrip()
        text = cut.rstrip(",;:.-") + "…"
    return text


def clean_description(raw):
    text = IMG_RX.sub(" ", raw or "")
    text = BBCODE_RX.sub(" ", text)
    text = TAG_RX.sub(" ", text)
    return clean(html.unescape(text))


def size_mb(nbytes):
    """maps.json's sizeMb: whole MB, rounded half up, never 0 (the bridge would announce '(0 MB)')."""
    return max(1, int(int(nbytes or 0) / 1048576.0 + 0.5))


def item_name(item):
    return clean(html.unescape(item.get("title") or "")) or "Workshop item %s" % item.get("publishedfileid")


def item_tags(item):
    return [str(t.get("tag")) for t in (item.get("tags") or []) if isinstance(t, dict) and t.get("tag")]


def workshop_verdict(item):
    """ok | removed | hidden | banned, as the bridge's daily check (bridge/Workshop.cs) reads the same reply."""
    if not item or int(item.get("result") or 0) != 1:
        return "removed"
    if item.get("banned"):
        return "banned"
    if int(item.get("visibility") or 0) != 0:
        return "hidden"
    return "ok"


# ------------------------------------------------------------------------------------------------
# The Web API: one door, the ledger, the stop rules
# ------------------------------------------------------------------------------------------------

class StopApi(Exception):
    """A limit or a refusal: no more Web API calls this run (a cap) or today (Steam's push-back)."""


class ApiError(Exception):
    """One call failed and was given up; the run goes on."""


class ApiRefused(ApiError):
    """A 4xx other than 401/403/429: asking again would get the same answer, so it is not retried."""


class ApiLedger(object):
    """Every Web API call this tool makes, persisted BEFORE it is sent. The rolling 24 h cap, the count per day, the day's stop (the
    per-run cap is SteamApi's)."""

    def __init__(self, path, day_cap, clock):
        self.path, self.day_cap, self.clock = path, min(day_cap, HARD_API_DAY_CAP), clock
        self.data = None
        self.broken = None

    def load(self):
        if not os.path.exists(self.path):
            self.data = {"schema": 1, "calls": [], "days": {}, "stops": {}}
            return
        data = read_json(self.path, None)
        if not isinstance(data, dict) or not isinstance(data.get("calls"), list) or not isinstance(data.get("days"), dict):
            self.broken = "%s does not parse - no Web API call until it is fixed or deleted" % self.path
            self.data = None
            return
        data.setdefault("stops", {})
        self.data = data

    def save(self):
        write_atomic(self.path, json.dumps(self.data, separators=(",", ":")) + "\n", mode=0o644)

    def prune(self, now):
        self.data["calls"] = [c for c in self.data["calls"] if isinstance(c, list) and c and now - float(c[0]) < DAY]
        for old in sorted(self.data["days"])[:-14]:
            del self.data["days"][old]
        for old in sorted(self.data["stops"])[:-14]:
            del self.data["stops"][old]

    def last_24h(self, now=None):
        now = self.clock.now() if now is None else now
        return sum(1 for c in (self.data or {}).get("calls", []) if now - float(c[0]) < DAY)

    def count(self, day):
        return int((self.data or {}).get("days", {}).get(day, 0))

    def stopped(self, day):
        return (self.data or {}).get("stops", {}).get(day)

    def stop(self, day, why):
        if self.data is None:
            return
        if not self.data["stops"].get(day):
            self.data["stops"][day] = why
            self.save()

    def reserve(self, day, api):
        """Count a call before it is made. Raises StopApi when a cap or the day's stop says no."""
        if self.data is None:
            raise StopApi(self.broken or "the API ledger is not loaded")
        now = self.clock.now()
        self.prune(now)
        if self.stopped(day):
            raise StopApi("stopped earlier today: %s" % self.stopped(day))
        if self.last_24h(now) >= self.day_cap:
            raise StopApi("the cap of %d Web API calls in 24 hours is reached" % self.day_cap)
        self.data["calls"].append([round(now, 3), api, None])
        self.data["days"][day] = self.count(day) + 1
        self.save()
        return len(self.data["calls"]) - 1

    def settle(self, index, status):
        try:
            self.data["calls"][index][2] = status
            self.save()
        except (IndexError, TypeError, OSError):
            pass


class SteamApi(object):
    """The only way this tool talks to api.steampowered.com: one call at a time, at least `gap` seconds apart."""

    def __init__(self, ledger, day, gap, key=None, opener=None, sleep=time.sleep, monotonic=time.monotonic, run_cap=None):
        self.ledger, self.day, self.gap, self.key = ledger, day, max(HARD_API_GAP, gap), key
        self.run_cap = run_cap                       # this run's calls at most (SYNC_API_RUN_CAP); the ledger keeps the 24 h cap
        self.opener = opener or urllib.request.urlopen
        self.sleep, self.monotonic = sleep, monotonic
        self.last = None
        self.errors_5xx_row = 0
        self.errors_5xx = 0
        self.netfails = 0
        self.calls = 0
        self.by_api = {}
        self.stop_reason = None

    def redact(self, text):
        text = str(text)
        return text.replace(self.key, "<key>") if self.key else text

    def _stop(self, why):
        self.stop_reason = why
        self.ledger.stop(self.day, why)
        raise StopApi(why)

    def _wait_gap(self):
        if self.last is not None:
            wait = self.gap - (self.monotonic() - self.last)
            if wait > 0:
                self.sleep(wait)

    def call(self, api, url, data=None, params=None):
        """One logical request: up to len(API_RETRY_WAIT) + 1 sends, every one through the ledger."""
        attempts = 0
        while True:
            attempts += 1
            try:
                return self._send(api, url, data, params)
            except ApiRefused:
                raise
            except ApiError as error:
                if attempts > len(API_RETRY_WAIT):
                    raise
                log("Web API %s failed (%s) - trying again in %d s" % (api, self.redact(error), API_RETRY_WAIT[attempts - 1]))
                self.sleep(API_RETRY_WAIT[attempts - 1])

    def _send(self, api, url, data, params):
        if self.stop_reason:
            raise StopApi(self.stop_reason)
        if self.run_cap is not None and self.calls >= self.run_cap:
            raise StopApi("this run's cap of %d Web API calls is reached" % self.run_cap)
        self._wait_gap()
        index = self.ledger.reserve(self.day, api)
        query = dict(params or {})
        if api in KEYED_APIS:
            if not self.key:
                raise StopApi("no Web API key")
            query["key"] = self.key
        full = url + ("?" + urllib.parse.urlencode(query, doseq=True) if query else "")
        body = urllib.parse.urlencode(data, doseq=True).encode() if data is not None else None
        request = urllib.request.Request(full, data=body, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
        if body is not None:
            request.add_header("Content-Type", "application/x-www-form-urlencoded")
        self.last = self.monotonic()
        self.calls += 1
        self.by_api[api] = self.by_api.get(api, 0) + 1
        status = None
        try:
            try:
                response = self.opener(request, timeout=30)
                try:
                    status = getattr(response, "status", 200)
                    raw = response.read()
                finally:
                    try:
                        response.close()
                    except Exception:
                        pass
            finally:
                self.last = self.monotonic()      # the gap runs from the END of this call: never two requests closer
        except urllib.error.HTTPError as error:
            status = error.code
            self.ledger.settle(index, status)
            if status in (401, 403, 429):
                self._stop("Steam's Web API answered HTTP %d to %s - every API step stops for the rest of the day" % (status, api))
            if 500 <= status < 600:
                self.errors_5xx_row += 1
                self.errors_5xx += 1
                if self.errors_5xx_row >= API_5XX_ROW or self.errors_5xx >= API_5XX_RUN:
                    self._stop("Steam's Web API answered %d server errors (%d in a row, the last HTTP %d to %s) - every API "
                               "step stops for the rest of the day" % (self.errors_5xx, self.errors_5xx_row, status, api))
                raise ApiError("HTTP %d" % status)
            raise ApiRefused("HTTP %d" % status)
        except (urllib.error.URLError, socket.timeout, ConnectionError, OSError) as error:
            self.ledger.settle(index, "net")
            self.netfails += 1
            if self.netfails >= API_NETFAIL_RUN:
                self._stop("%d network failures talking to the Web API (the last: %s) - every API step stops for the rest of the day"
                           % (self.netfails, self.redact(error)))
            raise ApiError("network: %s" % self.redact(error))
        self.ledger.settle(index, status)
        if status == 429 or status == 403 or status == 401:
            self._stop("Steam's Web API answered HTTP %d to %s - every API step stops for the rest of the day" % (status, api))
        self.errors_5xx_row = 0
        try:
            return json.loads(raw.decode("utf-8", "replace"))
        except ValueError:
            raise ApiError("the answer to %s is not JSON" % api)

    def query_files(self, tags, cursor, per_page):
        params = [("query_type", 1), ("cursor", cursor), ("numperpage", per_page), ("appid", 730),
                  ("filetype", 0), ("match_all_tags", "false"), ("return_tags", "true"), ("return_metadata", "true"),
                  ("return_short_description", "false"), ("strip_description_bbcode", "true")]
        params += [("requiredtags[%d]" % i, t) for i, t in enumerate(tags)]
        reply = self.call("QueryFiles", QUERY_URL, params=params)
        response = (reply or {}).get("response") or {}
        items = [i for i in (response.get("publishedfiledetails") or []) if isinstance(i, dict)]
        return items, response.get("next_cursor") or "", int(response.get("total") or 0)

    def votes(self, ids):
        """{id: [score, votes up, votes down]} of the items the Workshop has (result 1): IPublishedFileService/GetDetails with
        includevotes, 100 ids a call, the key needed (probed 2026-11-03: vote_data {score, votes_up, votes_down}; a removed item
        answers result 15 and nothing else)."""
        params = [("includevotes", "true"), ("short_description", "true"), ("includetags", "false"), ("appid", 730)]
        params += [("publishedfileids[%d]" % i, w) for i, w in enumerate(ids)]
        reply = self.call("GetDetails", VOTES_URL, params=params)
        out = {}
        for item in ((reply or {}).get("response") or {}).get("publishedfiledetails") or []:
            wid, vote = str(item.get("publishedfileid") or ""), item.get("vote_data")
            if wid.isdigit() and int(item.get("result") or 0) == 1 and isinstance(vote, dict) and isinstance(vote.get("score"), (int, float)):
                out[wid] = [round(float(vote["score"]), 6), int(vote.get("votes_up") or 0), int(vote.get("votes_down") or 0),
                            int(item.get("subscriptions") or 0)]
        return out

    def trend(self, tag, cursor, days, per_page=100):
        """(ids, next cursor, total): one page of the Workshop's Most Popular for a tag - IPublishedFileService/QueryFiles with
        query_type 3 (RankedByTrend) over `days` days, ids only, the key needed (probed 2026-11-03: 30 days is accepted, its order
        differs from 7's and 90's)."""
        params = [("query_type", 3), ("days", days), ("cursor", cursor), ("numperpage", per_page), ("appid", 730), ("filetype", 0),
                  ("requiredtags[0]", tag), ("ids_only", "true")]
        reply = self.call("QueryFiles", QUERY_URL, params=params)
        response = (reply or {}).get("response") or {}
        ids = [str(i.get("publishedfileid")) for i in response.get("publishedfiledetails") or [] if isinstance(i, dict)
               and str(i.get("publishedfileid") or "").isdigit()]
        return ids, response.get("next_cursor") or "", int(response.get("total") or 0)

    def details(self, ids):
        form = [("itemcount", len(ids))] + [("publishedfileids[%d]" % i, w) for i, w in enumerate(ids)]
        reply = self.call("GetPublishedFileDetails", DETAILS_URL, data=form)
        out = {}
        for item in ((reply or {}).get("response") or {}).get("publishedfiledetails") or []:
            wid = str(item.get("publishedfileid") or "")
            if wid.isdigit():
                out[wid] = item
        return out


def read_key(path):
    """The Web API key from its secret file, or None. Never logged: only whether there is one."""
    if not path:
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            key = handle.read().strip()
    except OSError:
        return None
    return key if re.match(r"^[0-9A-Fa-f]{32}$", key) else None


# ------------------------------------------------------------------------------------------------
# What we know: the bridge's files and this tool's own state
# ------------------------------------------------------------------------------------------------

def bridge_files(cfg):
    """The bridge's data as it is now: {"maps": [...], "broken": {...}, "blocked": [...], "modes": {mode: [...]}, "search": {...},
    "raw": {}}. Raises ValueError when maps.json, broken.json or a blocked.json that is there does not parse: then nothing is
    published and nothing is assumed. blocked.json - the maps blocked for good, written by hand and shipped by deploy/data.py, never
    by this tool (the owner's 77 a: one writer per file) - may be missing; broken.json's own "blocked" counts as well."""
    out = {"raw": {}}
    path = os.path.join(cfg.bridge_data, "maps.json")
    with open(path, "rb") as handle:
        raw = handle.read()
    out["raw"]["maps.json"] = raw
    maps = json.loads(raw.decode("utf-8"))
    if not isinstance(maps, list) or not maps:
        raise ValueError("%s holds no map list" % path)
    out["maps"] = maps
    path = os.path.join(cfg.bridge_data, "broken.json")
    with open(path, "rb") as handle:
        raw = handle.read()
    out["raw"]["broken.json"] = raw
    broken = json.loads(raw.decode("utf-8"))
    if not isinstance(broken, dict):
        raise ValueError("%s is not the object form" % path)
    out["broken"] = broken
    out["blocked"] = []
    path = os.path.join(cfg.bridge_data, "blocked.json")
    if os.path.exists(path):
        with open(path, "rb") as handle:
            blocked = json.loads(handle.read().decode("utf-8"))
        entries = blocked.get("blocked") if isinstance(blocked, dict) else blocked
        if not isinstance(entries, list):
            raise ValueError("%s is not a list of blocked maps" % path)
        out["blocked"] = entries
    out["modes"] = {}
    for mode, _tag, _gt, _gm, name in MODES[1:]:
        path = os.path.join(cfg.bridge_data, name)
        if os.path.exists(path):
            with open(path, "rb") as handle:
                raw = handle.read()
            out["raw"][name] = raw
            data = json.loads(raw.decode("utf-8"))
            if not isinstance(data, list):
                raise ValueError("%s is not a list" % path)
            out["modes"][mode] = data
        else:
            out["modes"][mode] = []
    path = os.path.join(cfg.bridge_data, "search.json")
    search = read_json(path, None)
    out["search"] = search if isinstance(search, dict) else None
    if out["search"] is not None:
        with open(path, "rb") as handle:
            out["raw"]["search.json"] = handle.read()
    return out


def broken_ids(broken):
    out = {}
    for section in ("maps", "packs", "removed", "blocked"):
        for entry in broken.get(section) or []:
            wid = str(entry.get("id") if isinstance(entry, dict) else entry)
            if wid.isdigit():
                out.setdefault(wid, section)
    return out


def blocked_ids(files):
    """The ids on blocked.json (bridge_files' "blocked"): the maps blocked for good, by hand."""
    out = {}
    for entry in files.get("blocked") or []:
        wid = str(entry.get("id") if isinstance(entry, dict) else entry)
        if wid.isdigit():
            out[wid] = "blocked"
    return out


def known_ids(files, cfg):
    """Every id with a verdict of ours already: listed (any mode), on broken.json or blocked.json, or excluded by configuration."""
    known = {}
    for m in files["maps"]:
        if isinstance(m, dict) and str(m.get("id", "")).isdigit():
            known[str(m["id"])] = "listed"
    for mode, entries in files["modes"].items():
        for m in entries:
            if isinstance(m, dict) and str(m.get("id", "")).isdigit():
                known.setdefault(str(m["id"]), "listed:" + mode)
    for wid, section in broken_ids(files["broken"]).items():
        known.setdefault(wid, "broken:" + section)
    for wid in blocked_ids(files):
        known.setdefault(wid, "broken:blocked")
    for wid in cfg.exclude:
        known.setdefault(wid, "excluded")
    return known


class State(object):
    """<state>/state.json: the items this tool has seen and what it decided, the update baseline, the last run."""

    def __init__(self, path):
        self.path = path
        self.data = {"schema": 1, "items": {}, "baseline": {}}

    def load(self):
        data = read_json(self.path, None)
        if data is None and os.path.exists(self.path):
            raise ValueError("%s does not parse - fix or delete it" % self.path)
        if isinstance(data, dict):
            data.setdefault("items", {})
            data.setdefault("baseline", {})
            self.data = data
        return self

    def save(self):
        write_atomic(self.path, json.dumps(self.data, ensure_ascii=False, separators=(",", ":")) + "\n", mode=0o644)

    def item(self, wid):
        return self.data["items"].get(str(wid))

    def remember(self, item, day, why):
        """Record a Workshop item we were shown (discovery, or a changed listed map), keeping what we decided before."""
        wid = str(item.get("publishedfileid"))
        rec = self.data["items"].setdefault(wid, {"first": day, "status": "queued", "why": why, "attempts": 0, "tests": 0})
        rec.update({"title": item_name(item), "tags": item_tags(item), "size": int(item.get("file_size") or 0),
                    "created": int(item.get("time_created") or 0), "updated": int(item.get("time_updated") or 0),
                    "subs": int(item.get("subscriptions") or 0), "lifetime": int(item.get("lifetime_subscriptions") or 0),
                    "filename": item.get("filename") or "", "app": int(item.get("consumer_appid") or 0),
                    "creator": str(item.get("creator") or ""),
                    "ws": workshop_verdict(dict(item, result=item.get("result", 1))), "preview": item.get("preview_url") or "",
                    "description": clean_description(item.get("file_description") or "")[:300]})
        if why == "changed" and rec.get("status") in ("published", "pass"):
            rec["status"] = "queued"
            rec["why"] = "changed"
        return rec


# ------------------------------------------------------------------------------------------------
# 1. Discovery and 2. the check
# ------------------------------------------------------------------------------------------------

def discover(api, cfg, known, state, day, deadline, now=time.time):
    """QueryFiles newest first; stops at the first page with nothing this tool has never seen (or a cap, or the clock).
    Returns {"pages", "seen", "new": [ids], "total", "stop"}."""
    out = {"pages": 0, "seen": 0, "new": [], "total": None, "stop": None}
    cursor = "*"
    while out["pages"] < cfg.max_pages:
        if now() >= deadline:
            out["stop"] = "the run's deadline"
            break
        items, next_cursor, total = api.query_files(cfg.tags, cursor, DETAILS_BATCH)
        out["pages"] += 1
        out["total"] = total
        out["seen"] += len(items)
        fresh = []
        for item in items:
            wid = str(item.get("publishedfileid") or "")
            if not wid.isdigit() or wid in known or state.item(wid) is not None:
                continue
            if str(item.get("creator") or "") in cfg.exclude_creators:
                continue                       # listed by hand (SYNC_EXCLUDE_CREATORS): never a candidate, never "new"
            state.remember(item, day, "new")
            fresh.append(wid)
        out["new"] += fresh
        if not items:
            out["stop"] = "an empty page"
            break
        if not fresh:
            out["stop"] = "page %d held nothing new" % out["pages"]
            break
        if not next_cursor or next_cursor == cursor:
            out["stop"] = "the last page"
            break
        cursor = next_cursor
    else:
        out["stop"] = "SYNC_MAX_PAGES (%d)" % cfg.max_pages
    return out


def check(api, ids, deadline, now=time.time):
    """GetPublishedFileDetails for `ids`, 100 a call. Returns ({id: item}, ids not asked because of the clock)."""
    got, left = {}, []
    for start in range(0, len(ids), DETAILS_BATCH):
        batch = ids[start:start + DETAILS_BATCH]
        if now() >= deadline:
            left += ids[start:]
            break
        try:
            got.update(api.details(batch))
        except ApiError as error:
            log("details of %d ids failed for good (%s) - they are left out of this check" % (len(batch), error))
            left += batch
    return got, left


def changed_since_epoch(day):
    try:
        return calendar.timegm(time.strptime(day, "%Y-%m-%d"))
    except ValueError:
        return 0


def judge_listed(details, listed, state, cfg, day):
    """The step 2 verdicts for the listed maps: {"removed": [(id, verdict)], "changed": [ids], "back": [ids], "missing": n,
    "suspicious": bool}. Updates the baseline for every map that is unchanged."""
    removed, changed, missing = [], [], 0
    epoch = changed_since_epoch(cfg.changed_since)
    base = state.data["baseline"]
    for wid in listed:
        item = details.get(wid)
        if item is None:
            missing += 1
            continue
        verdict = workshop_verdict(item)
        if verdict != "ok":
            removed.append((wid, verdict))
            continue
        updated = int(item.get("time_updated") or 0)
        known = base.get(wid)
        if known is None:
            if updated > epoch:
                changed.append(wid)
            else:
                base[wid] = updated
        elif updated > int(known):
            changed.append(wid)
    asked = len(listed) - missing
    suspicious = asked >= 50 and len(removed) > asked // 10
    return {"removed": removed, "changed": changed, "missing": missing, "suspicious": suspicious}


# ------------------------------------------------------------------------------------------------
# THE KEEP SET (the owner's rules of 2026-11-03) lives in keepset.py, which the deployment's compose file mounts beside this script and update-watch.py: one
# copy of the rules for both (the owner's 76 a, 2026-11-04). The game server deletes what is outside it; this sync hands over what is in
# it. The import is guarded: without the file the keep set is empty and says why, so serve and the clean-up stop on it as an error and
# nothing is handed over or lost - the rest of a run goes on.
# ------------------------------------------------------------------------------------------------
try:
    import keepset
    KEEPSET_MISSING = None
except ImportError as error:
    keepset = None
    KEEPSET_MISSING = "keepset.py is not beside workshop-sync.py (%s)" % error


def keep_inputs(data_dir):
    """The bridge's data files the keep set reads (keepset.keep_inputs); nothing without the module."""
    return keepset.keep_inputs(data_dir) if keepset else {}


def safe_keep_set(inputs, now, hud=""):
    """THE KEEP SET (keepset.keep_set), and never an exception: an input of a shape it does not read, or no module at all, gives an
    empty set whose problems say why - nothing is handed over or deleted on it."""
    if keepset is None:
        return {"up": [], "top": {}, "lists": {}, "cutoff": {}, "own": [], "hud": [], "queues": {}, "requested": [], "all": set(),
                "scored": 0, "problems": ["the keep set could not be computed: " + KEEPSET_MISSING]}
    return keepset.safe_keep_set(inputs, now, hud, log)


# ------------------------------------------------------------------------------------------------
# 3. Downloads: one anonymous SteamCMD session, one item at a time
# ------------------------------------------------------------------------------------------------

KV_TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|([{}])')


def kv_parse(text):
    """Valve KeyValues text (an .acf) -> nested lists of [key, value-or-list]; raises ValueError on anything odd."""
    stack, cur, key = [], [], None
    for m in KV_TOKEN.finditer(text):
        if m.group(2) == "{":
            if key is None:
                raise ValueError("block without a key")
            child = []
            cur.append([key, child])
            stack.append(cur)
            cur, key = child, None
        elif m.group(2) == "}":
            if not stack or key is not None:
                raise ValueError("unbalanced block")
            cur = stack.pop()
        elif key is None:
            key = m.group(1)
        else:
            cur.append([key, m.group(1)])
            key = None
    if stack or key is not None:
        raise ValueError("truncated")
    return cur


def kv_dump(kv, depth=0):
    out = []
    for key, value in kv:
        if isinstance(value, list):
            out.append('%s"%s"\n%s{\n%s%s}\n' % ("\t" * depth, key, "\t" * depth, kv_dump(value, depth + 1), "\t" * depth))
        else:
            out.append('%s"%s"\t\t"%s"\n' % ("\t" * depth, key, value))
    return "".join(out)


def kv_get(kv, key):
    for k, v in kv or []:
        if k == key:
            return v
    return None


def acf_items(path):
    """{id: timeupdated} of a workshop acf; {} when there is none."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            kv = kv_parse(handle.read())
    except (OSError, ValueError):
        return {}
    installed = kv_get(kv_get(kv, "AppWorkshop") or [], "WorkshopItemsInstalled") or []
    out = {}
    for wid, body in installed:
        if isinstance(body, list):
            try:
                out[wid] = int(kv_get(body, "timeupdated") or 0)
            except ValueError:
                out[wid] = 0
    return out


def acf_forget(path, ids):
    """Drop `ids` from a workshop acf (both sections), parsed back and checked before an atomic replace."""
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()
    kv = kv_parse(text)
    aw = kv_get(kv, "AppWorkshop")
    if aw is None:
        raise ValueError("no AppWorkshop block")
    drop = set(str(i) for i in ids)
    for section in ("WorkshopItemsInstalled", "WorkshopItemDetails"):
        body = kv_get(aw, section)
        if isinstance(body, list):
            body[:] = [e for e in body if e[0] not in drop]
    new = kv_dump(kv)
    if kv_parse(new) != kv:
        raise ValueError("the edited acf does not parse back")
    write_atomic(path, new, mode=0o644)


def folder_bytes(path):
    total = 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total


def folder_stats(path):
    """(files, bytes) of everything under a folder - what a hand-over's marker names, and update-watch.py checks before it takes it in."""
    files = total = 0
    for root, _dirs, names in os.walk(path):
        for name in names:
            try:
                total += os.path.getsize(os.path.join(root, name))
                files += 1
            except OSError:
                pass
    return files, total


def acf_entries(path, wid):
    """({key: value} of WorkshopItemsInstalled, of WorkshopItemDetails) of one item in a workshop acf; None when it has no entry."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            kv = kv_parse(handle.read())
    except (OSError, ValueError):
        return None
    workshop = kv_get(kv, "AppWorkshop") or []
    installed = kv_get(kv_get(workshop, "WorkshopItemsInstalled") or [], wid)
    details = kv_get(kv_get(workshop, "WorkshopItemDetails") or [], wid)
    if not isinstance(installed, list):
        return None
    return (dict((k, v) for k, v in installed if not isinstance(v, list)),
            dict((k, v) for k, v in (details if isinstance(details, list) else []) if not isinstance(v, list)))


def server_maps(game_dir):
    """{id: timeupdated} of the Workshop maps the game server stores: in its acf, with a folder behind content/730/<id> (its relative
    links resolve on this read-only mount of the same tree)."""
    ws = os.path.join(game_dir, "game", "bin", "linuxsteamrt64", "steamapps", "workshop")
    return dict((w, t) for w, t in acf_items(os.path.join(ws, "appworkshop_730.acf")).items()
                if os.path.isdir(os.path.join(ws, "content", "730", w)))


def handovers(folder):
    """{id: bytes} of the complete hand-overs the game server has not taken in yet (their markers)."""
    out = {}
    try:
        names = os.listdir(folder)
    except OSError:
        return out
    for name in names:
        if name.endswith(".json") and name[:-5].isdigit():
            rec = read_json(os.path.join(folder, name), {})
            out[name[:-5]] = int(rec.get("bytes") or 0) if isinstance(rec, dict) else 0
    return out


def hand_over(staging, folder, wid, title, depotcache):
    """THE HAND-OVER: a staged download moved to the game server's hand-over folder instead of being deleted. <folder>/<id>/ is its
    content (one rename: the staging folder and the hand-over folder are on the game volume's dataset, the server's maps too),
    <id>.manifest the Steam client's verified manifest of it (SteamCMD keeps it in ~/Steam/depotcache, byte-identical to the one the
    game server's own client keeps - compared on 2026-11-03), and last <id>.json, the marker: its acf entries as SteamCMD wrote them
    and what the folder holds. update-watch.py --organize takes it in at the server's next engine start. Returns (ok, words)."""
    root = os.path.join(staging, "steamapps", "workshop")
    content = os.path.join(root, "content", "730", wid)
    acf = os.path.join(root, "appworkshop_730.acf")
    entries = acf_entries(acf, wid)
    if entries is None or not all(str(entries[0].get(k) or "").isdigit() for k in ("size", "timeupdated", "manifest")):
        return False, "SteamCMD's acf has no complete entry for it"
    if not os.path.isdir(content) or os.path.islink(content):
        return False, "its folder is not in the staging folder"
    files, nbytes = folder_stats(content)
    if not files:
        return False, "its folder is empty"
    os.makedirs(folder, exist_ok=True)
    for stale in (os.path.join(folder, wid + ".json"), os.path.join(folder, wid), os.path.join(folder, wid + ".manifest")):
        if os.path.isdir(stale) and not os.path.islink(stale):
            shutil.rmtree(stale)                                   # an older hand-over the server never took in
        elif os.path.lexists(stale):
            os.remove(stale)
    os.rename(content, os.path.join(folder, wid))
    gid = entries[0]["manifest"]
    manifest = os.path.join(depotcache, "730_%s.manifest" % gid)
    if os.path.isfile(manifest):
        shutil.copy2(manifest, os.path.join(folder, wid + ".manifest"))
    marker = {"version": 1, "id": wid, "title": title or "", "files": files, "bytes": nbytes,
              "installed": {"size": entries[0]["size"], "timeupdated": entries[0]["timeupdated"], "manifest": gid},
              "details": entries[1] or {"manifest": gid, "timeupdated": entries[0]["timeupdated"]}, "created": int(time.time())}
    write_atomic(os.path.join(folder, wid + ".json"), json.dumps(marker, ensure_ascii=False) + "\n", mode=0o644)
    try:
        acf_forget(acf, [wid])
    except (OSError, ValueError) as error:
        log("hand-over %s: SteamCMD's acf still names it (%s)" % (wid, error))
    return True, "handed over (%s in %d files%s)" % (mb(nbytes), files, "" if os.path.isfile(manifest) else ", no manifest")


def tidy_staging(staging, handover_dir, items):
    """Before every SteamCMD session. SteamCMD writes its acf from memory, so it names again a map already handed over; at its next
    login it finds that map's folder gone and downloads it again by itself - 68 maps, 11.9 GB on 2026-11-03, after three restarts of
    the sync ended sessions before the serve step's clean-up. So: every acf entry without a staged folder is forgotten, and a staged
    copy of a map that waits as a hand-over goes (folder and entry) unless it waits for its test. Returns (forgotten, dropped)."""
    root = os.path.join(staging, "steamapps", "workshop")
    acf = os.path.join(root, "appworkshop_730.acf")
    content = os.path.join(root, "content", "730")
    if not os.path.exists(acf):
        return 0, 0
    named = acf_items(acf)
    pending = handovers(handover_dir)
    ghosts = [w for w in named if not os.path.isdir(os.path.join(content, w))]
    copies = [w for w in (os.listdir(content) if os.path.isdir(content) else [])
              if w in pending and (items.get(w) or {}).get("status") != "downloaded" and not os.path.islink(os.path.join(content, w))]
    if not ghosts and not copies:
        return 0, 0
    acf_forget(acf, ghosts + [w for w in copies if w in named])
    for wid in copies:
        shutil.rmtree(os.path.join(content, wid), ignore_errors=True)
    log("staging: %d acf entr%s without a folder forgotten, %d staged cop%s of waiting hand-overs removed - SteamCMD would fetch them "
        "again by itself" % (len(ghosts), "y" if len(ghosts) == 1 else "ies", len(copies), "y" if len(copies) == 1 else "ies"))
    return len(ghosts), len(copies)


SUCCESS_RX = re.compile(r'Success\. Downloaded item (\d+) to "[^"]*" \((\d+) bytes\)')
FAILURE_RX = re.compile(r"ERROR! Download item (\d+) failed \(([^)]*)\)")
LOGIN_FAIL_RX = re.compile(r"FAILED (?:to log ?in|login)|Login Failure|Rate Limit Exceeded|Invalid Password|No Connection", re.I)
STEAMCMD_STOP_RX = re.compile(r"rate limit|limit exceeded|too many", re.I)
STALL = "no result within"     # SteamCMD.download's answer when SteamCMD said nothing about the item in time: a stall
ACCESS_DENIED_RX = re.compile(r"access denied", re.I)   # about one item (1 of 2,129 on 2026-10-05) - two in a row is about us: that stops the day
ANSI_RX = re.compile(r"\x1b\[[0-9;]*m")


class SteamCmd(object):
    """One interactive SteamCMD process, logged in anonymously, fed one command at a time through its stdin."""

    def __init__(self, exe, install_dir, log_path, home=None):
        self.exe, self.install_dir, self.log_path, self.home = exe, install_dir, log_path, home
        self.proc = None
        self.buffer = b""

    def _log(self, data):
        try:
            if os.path.exists(self.log_path) and os.path.getsize(self.log_path) > (20 << 20):
                os.replace(self.log_path, self.log_path + ".1")
            with open(self.log_path, "ab") as handle:
                handle.write(data)
        except OSError:
            pass

    def _read_until(self, rx, timeout):
        """Bytes up to and including the first match of `rx`, or None (timeout / the process ended)."""
        end = time.time() + timeout
        fd = self.proc.stdout.fileno()
        while True:
            m = rx.search(ANSI_RX.sub("", self.buffer.decode("utf-8", "replace")))
            if m:
                text = ANSI_RX.sub("", self.buffer.decode("utf-8", "replace"))
                self.buffer = b""
                return text
            left = end - time.time()
            if left <= 0:
                return None
            ready, _, _ = select.select([fd], [], [], min(1.0, left))
            if ready:
                chunk = os.read(fd, 65536)
                if not chunk:
                    return None
                self._log(chunk)
                self.buffer += chunk

    def start(self, timeout=240):
        os.makedirs(self.install_dir, exist_ok=True)
        env = dict(os.environ)
        if self.home:
            env["HOME"] = self.home
        self._log(("\n==== %s SteamCMD session (anonymous)\n" % time.strftime("%Y-%m-%d %H:%M:%S")).encode())
        self.proc = subprocess.Popen([self.exe, "+force_install_dir", self.install_dir, "+login", "anonymous"],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd="/tmp",
                                     env=env, bufsize=0, start_new_session=True)
        text = self._read_until(re.compile(r"Steam>|" + LOGIN_FAIL_RX.pattern, re.I), timeout)
        if text is None:
            self.stop()
            return False, "no Steam> prompt within %d s" % timeout
        bad = LOGIN_FAIL_RX.search(text)
        if bad or "Connecting anonymously to Steam Public...OK" not in text.replace("\n", ""):
            reason = bad.group(0) if bad else "the anonymous login did not answer OK"
            self.stop()
            return False, reason
        return True, "logged in anonymously"

    def download(self, wid, timeout=900):
        """(ok, reason, bytes) for one `workshop_download_item 730 <wid>`."""
        if self.proc is None or self.proc.poll() is not None:
            return False, "SteamCMD is not running", 0
        try:
            self.proc.stdin.write(("workshop_download_item 730 %s\n" % wid).encode())
            self.proc.stdin.flush()
        except OSError as error:
            return False, "SteamCMD's stdin: %s" % error, 0
        rx = re.compile(r"Success\. Downloaded item %s to \"[^\"]*\" \(\d+ bytes\)|ERROR! Download item %s failed \([^)]*\)"
                        % (wid, wid))
        text = self._read_until(rx, timeout)
        if text is None:
            return False, "no result within %s" % hms(timeout), 0
        ok = SUCCESS_RX.search(text)
        if ok and ok.group(1) == str(wid):
            return True, "OK", int(ok.group(2))
        bad = FAILURE_RX.search(text)
        return False, (bad.group(2) if bad else "unknown answer"), 0

    def stop(self):
        if self.proc is None:
            return
        try:
            if self.proc.poll() is None:
                self.proc.stdin.write(b"quit\n")
                self.proc.stdin.flush()
                self.proc.wait(30)
        except Exception:
            pass
        self.proc.poll()
        if group_alive(self.proc.pid):      # steamcmd.sh may be gone with its binary still running under it
            try:
                os.killpg(self.proc.pid, signal.SIGKILL)
            except OSError:
                pass
            try:
                self.proc.wait(10)
            except Exception:
                pass
        self.proc = None


class DownloadBudget(object):
    """The day's downloads, booked in state.json's "downloads" (so a restart of the service cannot reset them, and every run of
    the day shares them). No daily cap since the owner's 36 c (2026-11-03): only Steam's push-back stops them - a
    SteamCMD rate-limit answer or two Access Denied in a row end the day's downloads (refuse_for_today, kept here), and a run ends
    its own at 3 failures in a row. The bug brake instead of a volume cap: a map downloaded DOWNLOADS_PER_MAP_DAY times today is not
    downloaded again today."""

    def __init__(self, state, day):
        self.book = state.data.setdefault("downloads", {})
        for old in sorted(self.book)[:-14]:
            del self.book[old]
        self.today = self.book.setdefault(day, {"items": 0, "bytes": 0})

    def allows(self, wid=None):
        """(ok, why): no when Steam pushed back today; for a map, no when it was downloaded DOWNLOADS_PER_MAP_DAY times today already."""
        if self.today.get("stop"):
            return False, "no more downloads today: %s" % self.today["stop"]
        if wid is not None and int((self.today.get("maps") or {}).get(str(wid), 0)) >= DOWNLOADS_PER_MAP_DAY:
            return False, "downloaded %d times today already - not again today (the bug brake)" % DOWNLOADS_PER_MAP_DAY
        return True, ""

    def refuse_for_today(self, why):
        self.today["stop"] = why[:200]

    def spend(self, nbytes, wid=None):
        self.today["items"] += 1
        self.today["bytes"] += max(0, int(nbytes))
        if wid is not None:
            maps = self.today.setdefault("maps", {})
            maps[str(wid)] = int(maps.get(str(wid), 0)) + 1

    def fail(self):
        self.today["failed"] = int(self.today.get("failed") or 0) + 1

    def stall(self, wid):
        """A SteamCMD stall on this map: booked for the day, and how many it has had today."""
        stalls = self.today.setdefault("stalls", {})
        stalls[str(wid)] = int(stalls.get(str(wid), 0)) + 1
        return stalls[str(wid)]

    def spend_bytes(self, nbytes):
        """The test engine's own copy of an item SteamCMD already fetched: volume, not another item."""
        self.today["bytes"] += max(0, int(nbytes))
        self.today["engine"] = self.today.get("engine", 0) + 1


# ------------------------------------------------------------------------------------------------
# 4a. The scan: what a download holds, and the commands its map carries
# ------------------------------------------------------------------------------------------------

VPK_SIG = 0x55AA1234


def _cstr(buf, pos):
    end = buf.index(b"\x00", pos)
    return buf[pos:end].decode("utf-8", "replace"), end + 1


def vpk_tree(buf):
    """[(path, archive_index, offset, length, preload, data_start)] of a VPK v1/v2 directory held in `buf` (its head)."""
    if len(buf) < 12:
        return []
    sig, ver, tree = struct.unpack("<III", buf[:12])
    if sig != VPK_SIG or ver not in (1, 2):
        return []
    pos = 28 if ver == 2 else 12
    data_start = pos + tree
    out = []
    try:
        while True:
            ext, pos = _cstr(buf, pos)
            if not ext:
                break
            while True:
                folder, pos = _cstr(buf, pos)
                if not folder:
                    break
                while True:
                    name, pos = _cstr(buf, pos)
                    if not name:
                        break
                    _crc, pre, arch, off, length, _term = struct.unpack("<IHHIIH", buf[pos:pos + 18])
                    pos += 18
                    preload = buf[pos:pos + pre]
                    pos += pre
                    path = ("%s/%s.%s" % (folder, name, ext)) if folder.strip() else "%s.%s" % (name, ext)
                    out.append((path, arch, off, length, preload, data_start))
    except (ValueError, struct.error, IndexError):
        pass
    return out


class Vpk(object):
    """A VPK on disk (a _dir.vpk with its _NNN.vpk archives, or a single .vpk), or one held in memory."""

    def __init__(self, path=None, data=None):
        self.path, self.data = path, data
        if data is None:
            with open(path, "rb") as handle:
                head = handle.read(12)
                if len(head) < 12:
                    self.entries = []
                    return
                sig, ver, tree = struct.unpack("<III", head)
                if sig != VPK_SIG or ver not in (1, 2) or tree > (256 << 20):
                    self.entries = []
                    return
                handle.seek(0)
                self.entries = vpk_tree(handle.read((28 if ver == 2 else 12) + tree))
        else:
            self.entries = vpk_tree(data)

    def read(self, entry, limit=64 << 20):
        _path, arch, off, length, preload, data_start = entry
        if length > limit:
            return None
        if self.data is not None:
            return preload + self.data[data_start + off:data_start + off + length] if arch == 0x7FFF else None
        if arch == 0x7FFF:
            target, at = self.path, data_start + off
        else:
            target, at = re.sub(r"_dir\.vpk$", "_%03d.vpk" % arch, self.path), off
        try:
            with open(target, "rb") as handle:
                handle.seek(at)
                return preload + handle.read(length)
        except OSError:
            return None


def item_vpks(folder):
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return [], []
    trees = [n for n in names if n.endswith("_dir.vpk")] or [
        n for n in names if n.endswith(".vpk") and not re.search(r"_\d{3}\.vpk$", n)]
    return names, trees


def lz4_block(src, limit):
    """Decompress one raw LZ4 block (no frame header) into at most `limit` bytes."""
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        if off == 0 or off > len(dst):
            raise ValueError("bad LZ4 offset")
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        if ml <= off:
            dst += dst[start:start + ml]
        else:
            for k in range(ml):
                dst.append(dst[start + k])
        if len(dst) > limit:
            raise ValueError("LZ4 output over its stated size")
    return bytes(dst)


def kv3_strings(resource):
    """The text a compiled Source 2 resource (an entity lump, .vents_c) carries: the string table of its KV3 DATA block,
    decompressed when it is KV3 v5 with LZ4 (the format CS2's compiler writes, checked 2026-11-01), else its raw bytes."""
    try:
        block_offset, block_count = struct.unpack("<II", resource[8:16])
        pos = 8 + block_offset
        data = None
        for _ in range(min(block_count, 32)):
            kind = resource[pos:pos + 4]
            offset, size = struct.unpack("<II", resource[pos + 4:pos + 12])
            if kind == b"DATA":
                data = resource[pos + 4 + offset:pos + 4 + offset + size]
            pos += 12
        if data is None or len(data) < 120:
            return resource
        if data[:4] == b"\x053VK" and struct.unpack("<I", data[20:24])[0] == 1:
            u1, c1 = struct.unpack("<II", data[72:80])
            return lz4_block(data[120:120 + c1], u1 + 16)
        return data
    except (struct.error, ValueError, IndexError):
        return resource


IP_RX = r"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d+)?"
CONNECT_RX = re.compile(r'^\s*"?connect\s+' + IP_RX, re.I)
SAY_CONNECT_RX = re.compile(r'^\s*"?(say|say_team)\b.*\bconnect\b\s*"?\s*' + IP_RX, re.I)
AD_RX = re.compile(r"clash\.gg|\bpromo ?code\b|use code\b|deposit bonus|free cases", re.I)
CLIENT_RX = re.compile(r"\b(bind|alias)\b[^;]*\bconnect\s+" + IP_RX, re.I)
TEXT_RX = re.compile(rb"[\x20-\x7e\x80-\xff\t]{4,}")


def commands_in(text_bytes):
    out = []
    for m in TEXT_RX.finditer(text_bytes):
        s = m.group(0).decode("utf-8", "replace")
        for part in re.split(r"[;\n]", s):
            part = part.strip()
            if part:
                out.append(part)
    return out


def advert_of(commands, has_clientcommand):
    """(category, sample) by the 2026-10-24 rules (ads_scan.py / ads_final.py), or (None, None):
    'gambling ad'      a gambling or case-site promotion
    'chat advert'      `say ... connect <ip>`: everybody reads it in chat
    'client connect'   a `connect <ip>` the players' own game is made to run (point_clientcommand, a bind or an alias)
    'server connect'   only a server-side `connect <ip>`, which CS2 refuses for Workshop maps: nobody sees it; not listed."""
    found = {}
    for c in commands:
        if AD_RX.search(c):
            found.setdefault("gambling ad", c)
        elif SAY_CONNECT_RX.search(c):
            found.setdefault("chat advert", c)
        elif CONNECT_RX.search(c) or CLIENT_RX.search(c):
            # a bind or an alias to connect, or a bare connect, reaches a player only through a client-command entity;
            # run on the server it does nothing (validated 2026-11-01 against the 2026-10-24 scan: 82 of 82 adverts alike)
            found.setdefault("client connect" if has_clientcommand else "server connect", c)
    for category in ("gambling ad", "chat advert", "client connect", "server connect"):
        if category in found:
            return category, found[category][:160]
    return None, None


def scan_item(folder):
    """What one Workshop download holds. verdict: map | pack | empty | legacy | unknown, maps (the level names in it),
    models, files, bytes, advert (category, sample) and the cfg/entity commands that made it, or none."""
    res = {"verdict": "unknown", "maps": [], "models": 0, "files": 0, "bytes": folder_bytes(folder), "names": [],
           "advert": None, "advert_sample": None}
    names, trees = item_vpks(folder)
    res["names"] = names[:30]
    if not trees:
        if names and all(n.endswith("_legacy.bin") or n in ("publish_data.txt",) for n in names) and any(
                n.endswith("_legacy.bin") for n in names):
            res["verdict"] = "legacy"
        return res
    commands, clientcommand, seen = [], False, False
    for tree in trees:
        try:
            vpk = Vpk(os.path.join(folder, tree))
        except OSError:
            continue
        for entry in vpk.entries:
            seen = True
            res["files"] += 1
            low = entry[0].lower()
            if low.startswith("maps/") and low.endswith(".vpk") and low.count("/") == 1:
                res["maps"].append(entry[0][5:-4])
                inner_bytes = vpk.read(entry, limit=1 << 30)
                if not inner_bytes:
                    continue
                inner = Vpk(data=inner_bytes)
                for ie in inner.entries:
                    il = ie[0].lower()
                    if il.endswith(".vents_c") or il.endswith(".cfg"):
                        raw = inner.read(ie, limit=32 << 20)
                        if raw:
                            text = kv3_strings(raw) if il.endswith(".vents_c") else raw
                            clientcommand = clientcommand or b"point_clientcommand" in text or b"point_broadcastclientcommand" in text
                            commands += commands_in(text)
            elif low.endswith(".vmdl_c"):
                res["models"] += 1
            elif low.endswith(".cfg"):
                raw = vpk.read(entry, limit=4 << 20)
                if raw:
                    commands += commands_in(raw)
    if seen:
        res["verdict"] = "map" if res["maps"] else ("pack" if res["models"] else "empty")
    res["advert"], res["advert_sample"] = advert_of(commands, clientcommand)
    return res


# ------------------------------------------------------------------------------------------------
# A2S and RCON
# ------------------------------------------------------------------------------------------------

def a2s(host, port, timeout=2.0):
    """A2S_INFO -> {"map", "players", "bots", "humans"} or None (no answer, which is never read as 'empty')."""
    query = b"\xff\xff\xff\xffTSource Engine Query\x00"
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.settimeout(timeout)
        sock.sendto(query, (host, port))
        data = sock.recv(4096)
        if data[4:5] == b"A":
            sock.sendto(query + data[5:9], (host, port))
            data = sock.recv(4096)
        if data[4:5] != b"I":
            return None
        pos, fields = 6, []
        for _ in range(4):
            end = data.index(b"\x00", pos)
            fields.append(data[pos:end].decode("utf-8", "replace"))
            pos = end + 1
        players, bots = data[pos + 2], data[pos + 4]
        return {"map": fields[1], "players": players, "bots": bots, "humans": max(0, players - bots)}
    except (OSError, ValueError, IndexError):
        return None
    finally:
        sock.close()


def rcon(host, port, password, command, timeout=6.0, collect=0.0):
    """One command on a fresh connection: its reply text, or None when RCON cannot be reached or refuses the password."""
    sock = None
    try:
        sock = socket.create_connection((host, port), timeout)
        sock.settimeout(timeout)

        def send(rid, kind, body):
            raw = body.encode("utf-8")
            sock.sendall(struct.pack("<iii", 10 + len(raw), rid, kind) + raw + b"\x00\x00")

        def recv():
            head = b""
            while len(head) < 4:
                chunk = sock.recv(4 - len(head))
                if not chunk:
                    return None
                head += chunk
            size = struct.unpack("<i", head)[0]
            if size < 10 or size > (1 << 20):
                return None
            buf = b""
            while len(buf) < size:
                chunk = sock.recv(size - len(buf))
                if not chunk:
                    return None
                buf += chunk
            return struct.unpack("<i", buf[:4])[0], struct.unpack("<i", buf[4:8])[0], buf[8:-2].decode("utf-8", "replace")

        send(1, 3, password)
        while True:
            pkt = recv()
            if pkt is None:
                return None
            if pkt[1] == 2:
                if pkt[0] == -1:
                    return None
                break
        send(7, 2, command)
        pkt = recv()
        out = "" if pkt is None else pkt[2]
        if collect > 0:
            end = time.time() + collect
            sock.settimeout(0.5)
            while time.time() < end:
                try:
                    pkt = recv()
                except OSError:
                    break
                if pkt is None:
                    break
                out += pkt[2]
        return out
    except OSError:
        return None
    finally:
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass


def humans_on(cfg):
    """Humans on the game server by A2S, asked twice 2 s apart when the first answer is not 0. None = no answer."""
    if not cfg.game_host:
        return 0
    first = a2s(cfg.game_host, cfg.game_port)
    if first is not None and first["humans"] == 0:
        return 0
    time.sleep(2)
    second = a2s(cfg.game_host, cfg.game_port)
    if second is None:
        return None if first is None else first["humans"]
    return second["humans"]


class Tail(object):
    def __init__(self, path):
        self.path, self.buffer = path, b""
        try:
            self.position = os.path.getsize(path)
        except OSError:
            self.position = 0

    def lines(self):
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
            data = handle.read(min(size - self.position, 8 << 20))
        self.position += len(data)
        parts = (self.buffer + data).split(b"\n")
        self.buffer = parts.pop()
        return [p.decode("utf-8", "replace").rstrip("\r") for p in parts]


# ------------------------------------------------------------------------------------------------
# 4b. The test copy of the server: a second engine from a read-only view of the game server's install
# ------------------------------------------------------------------------------------------------

def patch_version(game_dir):
    try:
        with open(os.path.join(game_dir, "game", "csgo", "steam.inf"), "r", encoding="utf-8", errors="replace") as handle:
            m = re.search(r"^PatchVersion=(\S+)", handle.read(), re.M)
            return m.group(1) if m else None
    except OSError:
        return None


TEST_SERVER_CFG = """// Written by workshop-sync.py for its test engine on every start. Not the game server's server.cfg: no log address.
hostname "workshop sync test"
sv_cheats 0
sv_hibernate_when_empty 0
sv_lan 0
log off
"""


def build_farm(game_dir, work_dir, cfg_dir=None):
    """<work>/farm/game: links to every file and folder of <game_dir>/game, except what the test engine must own -
    the engine's own folder bin/linuxsteamrt64 (its files copied: the engine finds its root and its Workshop folder
    from where its executable is), csgo/cfg (copied, server.cfg replaced, every log address removed), csgo/gameinfo.gi
    (copied without the Metamod search path: no plugins), the engine's log files. Returns a line for the log."""
    t0 = time.time()
    src = os.path.join(game_dir, "game")
    farm_root = os.path.join(work_dir, "farm")
    dst = os.path.join(farm_root, "game")
    stage = os.path.join(work_dir, "staging")
    if os.path.lexists(farm_root):
        shutil.rmtree(farm_root)
    os.makedirs(os.path.join(dst, "bin", "linuxsteamrt64"))
    os.makedirs(os.path.join(stage, "steamapps", "workshop"), exist_ok=True)
    for name in os.listdir(src):
        if name not in ("bin", "csgo"):
            if name == "cs2.sh":
                shutil.copy2(os.path.join(src, name), os.path.join(dst, name))
            else:
                os.symlink(os.path.join(src, name), os.path.join(dst, name))
    for name in os.listdir(os.path.join(src, "bin")):
        if name != "linuxsteamrt64":
            os.symlink(os.path.join(src, "bin", name), os.path.join(dst, "bin", name))
    copied = 0
    engine_src = os.path.join(src, "bin", "linuxsteamrt64")
    for name in os.listdir(engine_src):
        path = os.path.join(engine_src, name)
        if name in ("steamapps", "steam_appid.txt") or name.endswith(".log") or os.path.islink(path) or not os.path.isfile(path):
            continue
        shutil.copy2(path, os.path.join(dst, "bin", "linuxsteamrt64", name))
        copied += 1
    os.symlink(os.path.join(stage, "steamapps"), os.path.join(dst, "bin", "linuxsteamrt64", "steamapps"))
    csgo_src, csgo_dst = os.path.join(src, "csgo"), os.path.join(dst, "csgo")
    os.makedirs(csgo_dst)
    own = {"addons", "cfg", "gameinfo.gi", "console.log", "console.log.1", "backup_round00.txt", "rpt", "save", "logs"}
    for name in os.listdir(csgo_src):
        if name not in own and not name.startswith("backup_round"):
            os.symlink(os.path.join(csgo_src, name), os.path.join(csgo_dst, name))
    shutil.copytree(os.path.join(csgo_src, "cfg"), os.path.join(csgo_dst, "cfg"), symlinks=False)
    if cfg_dir and os.path.isdir(cfg_dir):
        for name in os.listdir(cfg_dir):
            if name.endswith(".cfg") and os.path.isfile(os.path.join(cfg_dir, name)):
                shutil.copy2(os.path.join(cfg_dir, name), os.path.join(csgo_dst, "cfg", name))
    scrubbed = 0
    for root, _dirs, files in os.walk(os.path.join(csgo_dst, "cfg")):
        for name in files:
            path = os.path.join(root, name)
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as handle:
                    text = handle.read()
            except OSError:
                continue
            if "logaddress" in text.lower():
                keep = "\n".join(l for l in text.split("\n") if "logaddress" not in l.lower())
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(keep)
                scrubbed += 1
    with open(os.path.join(csgo_dst, "cfg", "server.cfg"), "w", encoding="utf-8") as handle:
        handle.write(TEST_SERVER_CFG)
    with open(os.path.join(csgo_src, "gameinfo.gi"), "r", encoding="utf-8", errors="replace") as handle:
        gameinfo = handle.read()
    vanilla = "\n".join(l for l in gameinfo.split("\n") if "csgo/addons/metamod" not in l)
    with open(os.path.join(csgo_dst, "gameinfo.gi"), "w", encoding="utf-8") as handle:
        handle.write(vanilla)
    return ("test tree built in %.1f s: %d engine files copied, the plugins' search path %s, log addresses removed from %d "
            "cfg file(s)" % (time.time() - t0, copied, "removed" if vanilla != gameinfo else "absent", scrubbed))


def kill_stale_engines(farm_root):
    """Kill any engine still running from the test tree (a run that died without stopping it): it would hold the port,
    and the tree is about to be rebuilt under it. Matched on its executable's path, read from /proc. Returns the count."""
    killed = 0
    prefix = os.path.join(farm_root, "")
    for pid in os.listdir("/proc") if os.path.isdir("/proc") else []:
        if not pid.isdigit() or int(pid) == os.getpid():
            continue
        try:
            exe = os.readlink("/proc/%s/exe" % pid)
        except OSError:
            continue
        if exe.startswith(prefix):
            try:
                os.kill(int(pid), signal.SIGKILL)
                killed += 1
            except OSError:
                pass
    if killed:
        time.sleep(2)
    return killed


DOWNLOADING_RX = re.compile(r"Downloading (\d+) \([^/]*/\s*([\d.]+)\s*(KB|MB|GB)\)")


def group_alive(pgid):
    """Is anything left of this process group? Its leader's exit is not the group's: the engine's wrapper (cs2.sh) dies at a SIGTERM, a
    frozen engine under it does not, and it keeps the port. Seen on the night of 2026-11-02: one map froze, the stop believed the wrapper,
    and the next engine never answered, which ended the night's run with 1h37m of its window left."""
    try:
        os.killpg(pgid, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


class TestEngine(object):
    """The test copy: one engine process at a time, started from the farm on its own port with its own passwords."""

    MODE_ARGS = {"armsrace": ("1", "0", "mg_armsrace", "ar_shoots")}
    QUIT_WAIT = 15      # seconds a clean quit gets
    STOP_WAIT = 10      # seconds each signal gets

    def __init__(self, cfg, work_dir, home):
        self.cfg, self.work_dir, self.home = cfg, work_dir, home
        self.farm = os.path.join(work_dir, "farm", "game")
        self.console = os.path.join(self.farm, "csgo", "console.log")
        self.port = cfg.test_port
        self.password = secrets.token_hex(16)
        self.proc = None
        self.starts = 0
        self.tail = None

    def rcon(self, command, collect=0.0):
        return rcon("127.0.0.1", self.port, self.password, command, collect=collect)

    def a2s(self):
        return a2s("127.0.0.1", self.port)

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def start(self, timeout=180):
        """Start a fresh engine and wait until A2S answers on its start map and RCON works. (ok, why)."""
        self.stop()
        stale = kill_stale_engines(os.path.dirname(self.farm))      # whatever still holds the port: a new engine would never answer
        if stale:
            log("killed %d test engine(s) still running from the test tree" % stale)
        if self.starts >= self.cfg.engine_starts_max:
            return False, "this run's cap of %d test-engine starts is reached" % self.cfg.engine_starts_max
        self.starts += 1
        sdk = os.path.join(self.home, ".steam", "sdk64")
        os.makedirs(sdk, exist_ok=True)
        link = os.path.join(sdk, "steamclient.so")
        client = os.path.join(os.path.dirname(self.cfg.steamcmd), "linux64", "steamclient.so")
        if not os.path.lexists(link) and os.path.exists(client):
            os.symlink(client, link)
        try:
            os.remove(self.console)
        except OSError:
            pass
        gt, gm, group, start_map = self.MODE_ARGS["armsrace"]
        args = ["./cs2.sh", "-dedicated", "-ip", "0.0.0.0", "-port", str(self.port), "-console", "-usercon", "-condebug",
                "-maxplayers", "10", "+game_type", gt, "+game_mode", gm, "+mapgroup", group, "+map", start_map,
                "+rcon_password", self.password, "+sv_password", secrets.token_hex(12), "+sv_lan", "0", "+tv_enable", "0"]
        env = {k: v for k, v in os.environ.items() if not k.startswith(("CS2_", "SYNC_", "STEAM_WEBAPI"))}
        env["HOME"] = self.home
        stdout_log = os.path.join(self.work_dir, "engine-stdout.log")
        try:
            if os.path.getsize(stdout_log) > (10 << 20):
                os.replace(stdout_log, stdout_log + ".1")
        except OSError:
            pass
        out = open(stdout_log, "ab")
        try:
            self.proc = subprocess.Popen(["bash", "-c", 'ulimit -c 0; exec "$@"', "engine"] + args, cwd=self.farm,
                                         stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env,
                                         start_new_session=True)
        finally:
            out.close()
        end = time.time() + timeout
        while time.time() < end:
            time.sleep(2)
            if not self.alive():
                return False, "the test engine exited during its start (code %s)" % self.proc.returncode
            info = self.a2s()
            if info is not None and info["map"] == start_map and self.rcon("echo ok") is not None:
                self.tail = Tail(self.console)
                listing = self.rcon("logaddress_list_http") or ""
                if re.search(r"https?://", listing):
                    self.stop()
                    return False, "the test engine has a log address (%s) - it must never push anything" % listing.strip()[:80]
                return True, "test engine up in %.0f s (start %d of this run)" % (timeout - (end - time.time()), self.starts)
        self.stop()
        return False, "the test engine did not answer within %d s" % timeout

    def stop(self):
        if self.proc is None:
            return
        pgid = self.proc.pid
        if self.alive():
            self.rcon("quit")
            for _ in range(self.QUIT_WAIT):
                if not self.alive():
                    break
                time.sleep(1)
        for sig in (signal.SIGTERM, signal.SIGKILL):
            self.proc.poll()            # reaps the leader: a zombie still counts as a member of the group
            if not group_alive(pgid):
                break
            try:
                os.killpg(pgid, sig)
            except OSError:
                pass
            for _ in range(self.STOP_WAIT):
                self.proc.poll()
                if not group_alive(pgid):
                    break
                time.sleep(1)
        try:
            self.proc.wait(5)
        except Exception:
            pass
        self.proc = None


LEVEL_RX = re.compile(r"Level loading started for '([^']*)'")
KICK_RX = re.compile(r"kick|Dropped .* from server|was banned", re.I)
NOISE_RX = re.compile(r"rcon from|bot_kick|bot_quota|Unknown command|ReplicateConVar|sv_kick_ban_duration|sv_vote_issue_kick|"
                      r"sv_vote_kick_ban|autokick", re.I)
CHAT_RX = re.compile(r"\[All Chat\]\[[^\]]*\]:\s*(.*)$|DISALLOWED WORKSHOP COMMANDS:\s*(.*)$")


def a2s_is(a2s_map, level):
    """A2S cuts map names at 31 characters: equal, or A2S's name is a 31+-character prefix of the level."""
    a, l = (a2s_map or "").lower(), (level or "").lower()
    return a == l or (len(a) >= 31 and l.startswith(a))


def mode_of(tags):
    for mode, tag, gt, gm, _name in MODES:
        if tag in tags:
            return mode, gt, gm
    return MODES[0][0], MODES[0][2], MODES[0][3]


def test_load(engine, wid, levels, tags, hold, stop_check):
    """One test load of an item that is in the staging folder. Returns a record with verdict pass | errormap | crash |
    freeze | rejected | kick | abort (somebody came on the game server: not counted)."""
    mode, gt, gm = mode_of(tags)
    rec = {"id": wid, "mode": mode, "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    levels = [l.lower() for l in levels]
    engine.tail.lines()
    t0 = time.time()
    engine.rcon("game_type %d; game_mode %d; host_workshop_map %s" % (gt, gm, wid))
    level, seen, mounted, silent, foreign_since, lines = None, [], None, None, None, []
    chat = []
    limit = 150
    while time.time() - t0 < limit:
        time.sleep(1)
        new = engine.tail.lines()
        lines += new
        for line in new:
            d = DOWNLOADING_RX.search(line)
            if d and d.group(1) == str(wid):
                # the engine's own Steam client fetches a newer copy than SteamCMD's: counted in the day's downloads
                size = float(d.group(2)) * {"KB": 1 << 10, "MB": 1 << 20, "GB": 1 << 30}[d.group(3)]
                rec["engine_download"] = max(int(size), rec.get("engine_download", 0))
                limit = 300
            if mounted is None and ("Mounting addon '%s'" % wid) in line:
                mounted = len(seen)
            m = LEVEL_RX.search(line)
            if m:
                seen.append(m.group(1))
                if level is None and (m.group(1).lower() in levels or m.group(1) == "error"):
                    level = m.group(1)
            c = CHAT_RX.search(line)
            if c:
                chat.append((c.group(1) or c.group(2) or "").strip()[:200])
        rec["levels_seen"] = seen[:6]
        if level == "error" or any("Error map!" in l for l in new):
            rec.update(verdict="errormap", reason="the engine loaded its error map: nothing in the item it can load")
            return rec
        if not engine.alive():
            rec.update(verdict="crash", reason="the test engine went down while loading it")
            return rec
        info = engine.a2s()
        if info is None:
            silent = silent or time.time()
            if time.time() - silent > 45:
                rec.update(verdict="freeze", reason="A2S silent for 45 s while loading it")
                return rec
            continue
        silent = None
        if stop_check():
            rec.update(verdict="abort", reason="somebody came on the game server")
            return rec
        if level and a2s_is(info["map"], level):
            rec["load_s"] = round(time.time() - t0, 1)
            rec["level"] = level
            break
        other = [x for x in seen[mounted:] if x.lower() not in levels and x != "error"] if mounted is not None else []
        if other and level is None and a2s_is(info["map"], other[-1]):
            foreign_since = foreign_since or time.time()
            if time.time() - foreign_since >= 15:
                rec.update(verdict="rejected", level=other[-1],
                           reason="the addon mounts, but the engine loads %s, not a map of its own (%s)" % (
                               other[-1], ", ".join(levels[:3]) or "none in its VPK"))
                return rec
        else:
            foreign_since = None
    else:
        rec.update(verdict="rejected", reason="no level of its own (%s) within %d s; levels seen %s" % (
            ", ".join(levels[:3]) or "none in its VPK", limit, ", ".join(seen[:4]) or "none"))
        return rec
    engine.rcon("bot_join_after_player 0; bot_quota_mode normal; bot_quota 2")
    bots, end = [], time.time() + hold
    while time.time() < end:
        time.sleep(2)
        new = engine.tail.lines()
        lines += new
        for line in new:
            c = CHAT_RX.search(line)
            if c:
                chat.append((c.group(1) or c.group(2) or "").strip()[:200])
        if not engine.alive():
            rec.update(verdict="crash", reason="the test engine went down %d s into the map" % (hold - (end - time.time())))
            return rec
        info = engine.a2s()
        if info is None:
            silent = silent or time.time()
            if time.time() - silent > 45:
                rec.update(verdict="freeze", reason="A2S silent for 45 s on the map")
                return rec
            continue
        silent = None
        bots.append(info["bots"])
        if stop_check():
            rec.update(verdict="abort", reason="somebody came on the game server")
            return rec
    engine.rcon("bot_quota 0")
    kicks = [l.strip()[:160] for l in lines if KICK_RX.search(l) and not NOISE_RX.search(l)]
    rec["bots"] = bots[-1] if bots else 0
    rec["chat"] = chat[:8]
    if kicks and bots and max(bots) > 0 and bots[-1] == 0:
        rec.update(verdict="kick", reason="the bots were thrown off the server (%s)" % kicks[0][:100])
        return rec
    rec.update(verdict="pass", reason="loads as %s in %.0f s and held %d s (bots %d)" % (rec["level"], rec["load_s"], hold,
                                                                                      rec["bots"]))
    return rec


# ------------------------------------------------------------------------------------------------
# Verdicts: from the scan and the test loads to what is published
# ------------------------------------------------------------------------------------------------

ADVERT_REASON = {"gambling ad": "posts a gambling ad", "chat advert": "advertises another server",
                 "client connect": "sends players to another server"}
ADVERT_EVIDENCE = {
    "gambling ad": "posts a gambling ad - a case-site promo code in the map (scan of the map, %s)",
    "chat advert": "advertises another server - the map tells everyone in chat to connect to it (scan of the map, %s)",
    "client connect": "sends players to another server - the map makes their game run a connect command (scan of the map, %s)",
}


def final_verdict(rec, day):
    """The item's outcome once its scan and its loads are in: (kind, detail) where kind is one of
    pass | advert | crasher | kick | nonmap | pending. detail: a dict with what the publish step writes."""
    scan = rec.get("scan") or {}
    loads = [l for l in rec.get("loads") or [] if l.get("verdict") != "abort"]
    sv = scan.get("verdict")
    if sv in ("pack", "empty", "legacy", "unknown"):
        kind = {"pack": "pack", "empty": "empty", "legacy": "legacy", "unknown": "stub"}[sv]
        detail = {
            "pack": "no maps/<name>.vpk in its VPK, only %s compiled models - an asset pack (workshop sync %s)" % (scan.get("models"), day),
            "empty": "no map and no models in its VPK (%s files) - nothing the engine can load (workshop sync %s)" % (scan.get("files"), day),
            "legacy": "a CS:GO-era upload: it holds only %s and no CS2 VPK - CS2 cannot load it (workshop sync %s)" % (
                ", ".join(scan.get("names") or [])[:120], day),
            "stub": "no readable VPK index (%s) - nothing the engine can load (workshop sync %s)" % (
                ", ".join(scan.get("names") or [])[:120], day),
        }[kind]
        return "nonmap", {"kind": kind, "detail": detail}
    if not loads:
        return "pending", {}
    verdicts = [l["verdict"] for l in loads]
    if "pass" in verdicts:
        first = loads[verdicts.index("pass")]
        if scan.get("advert") in ADVERT_REASON:
            return "advert", {"reason": ADVERT_REASON[scan["advert"]], "sample": scan.get("advert_sample"),
                              "evidence": ADVERT_EVIDENCE[scan["advert"]] % day}
        chat = [c for l in loads for c in l.get("chat") or [] if AD_RX.search(c) or SAY_CONNECT_RX.search("say " + c)]
        if chat:
            return "advert", {"reason": "advertises another server" if not AD_RX.search(chat[0]) else "posts a gambling ad",
                              "sample": chat[0], "evidence": "showed an advert in chat on a test load: %s (workshop sync %s)"
                              % (chat[0][:80], day)}
        return "pass", {"level": first.get("level"), "mode": first.get("mode"), "load_s": first.get("load_s"),
                        "flaky": verdicts.index("pass") > 0, "before": verdicts[:verdicts.index("pass")]}
    if "kick" in verdicts:
        return "kick", {"evidence": "threw the bots off the server on a test load (workshop sync %s)" % day}
    if len(loads) < 3:
        return "pending", {}
    down = verdicts.count("crash") + verdicts.count("freeze")
    if down:
        how = "all %d" % down if down == len(verdicts) else "%d of %d" % (down, len(verdicts))
        if verdicts.count("crash") and not verdicts.count("freeze"):
            text = "crashes the server while loading - it went down on %s test loads (%s)" % (how, day)
        elif verdicts.count("freeze") and not verdicts.count("crash"):
            text = "freezes the server while loading - it stopped answering for 45 s on %s test loads (%s)" % (how, day)
        else:
            text = "crashes or freezes the server while loading - on %s test loads (%s)" % (how, day)
        return "crasher", {"crashes": down, "evidence": text}
    if all(v == "errormap" for v in verdicts):
        return "nonmap", {"kind": "stub" if scan.get("maps") else "empty",
                          "detail": "the engine finds no map in it - it loaded the error map on all 3 test loads (workshop sync %s)" % day}
    level = next((l.get("level") for l in loads if l.get("level")), "")
    return "nonmap", {"kind": "stub", "detail": "never loads a level of its own%s - %s on its 3 test loads (workshop sync %s)" % (
        (" (the engine loads %s)" % level) if level else "", "/".join(verdicts), day)}


def search_blob(name, wid, tags, description):
    """The search text of a map: lowercased words."""
    parts = clean(name).lower().split()
    parts.append(str(wid))
    for tag in tags:
        parts.extend(clean(tag).lower().split())
    parts.extend(clean(description).lower()[:800].split())
    return " ".join(p for p in parts if p)


def popularity_key(rec):
    return (-int(rec.get("subs") or 0), -int(rec.get("lifetime") or 0), int(rec.get("id") or 0))


def order_backlog(items, wids):
    """The backlog, strictly most subscribed first, whatever the mode (until 2026-11-02 the modes took
    turns). An item tagged for several modes is one item."""
    return sorted(wids, key=lambda w: popularity_key(dict(items[w], id=w)))


PACE_DEFAULT = 31.0   # seconds a map takes, download and test, until a run has measured it: 7 s + 24 s (the run of 2026-11-01)


def backlog_stats(items, wids, cfg, pace=None):
    """How much is waiting and how long it takes: {"items", "bytes", "by_mode", "days", "pace", "hours", "per_day"}. With no daily
    caps (the owner's 36 c) and no time windows one thing bounds a day: HOURS_A_DAY at the pace measured (or PACE_DEFAULT) - every
    hour somebody plays comes on top."""
    by_mode = {}
    for mode, tag, _gt, _gm, _f in MODES:
        by_mode[mode] = sum(1 for w in wids if tag in (items[w].get("tags") or []))
    total = sum(int(items[w].get("size") or 0) for w in wids)
    pace = pace if pace and pace > 0 else PACE_DEFAULT
    hours = float(HOURS_A_DAY)
    per_day = max(1, int(hours * 3600 / pace))
    days = -(-len(wids) // per_day) if wids else 0
    return {"items": len(wids), "bytes": total, "by_mode": by_mode, "days": days, "pace": round(pace, 1), "hours": round(hours, 1),
            "per_day": per_day}


def merge_problems(name, old_raw, new_text):
    """[] when the new text of a bridge file is the live one plus additions only: the publish never rewrites what is there
    (maps.json and broken.json's lists keep every old entry, in place; search.json every old text; a mode list every id)."""
    if old_raw is None:
        return []
    try:
        old = json.loads(old_raw.decode("utf-8") if isinstance(old_raw, bytes) else old_raw)
        new = json.loads(new_text)
    except ValueError as error:
        return ["%s: does not parse (%s)" % (name, error)]
    problems = []
    if name == "maps.json":
        if new[:len(old)] != old:
            problems.append("maps.json: an existing entry changed or moved")
        ids = [str(e.get("id")) for e in new if isinstance(e, dict)]
        ranks = [e.get("rank") for e in new if isinstance(e, dict)]
        if len(set(ids)) != len(ids) or len(set(ranks)) != len(ranks):
            problems.append("maps.json: an id or a rank is there twice")
    elif name == "broken.json":
        for k, v in old.items():
            if isinstance(v, list):
                if (new.get(k) or [])[:len(v)] != v:
                    problems.append("broken.json: the %s list lost or changed an entry" % k)
            elif new.get(k) != v:
                problems.append("broken.json: %s changed" % k)
    elif name == "search.json":
        if any(new.get(k) != v for k, v in old.items()):
            problems.append("search.json: an existing text changed")
    else:
        def keys(lst):
            return set(str(e.get("id") or "official:%s" % e.get("name")) for e in lst if isinstance(e, dict))
        if not keys(old) <= keys(new):
            problems.append("%s: a map left the list" % name)
    return problems


def plan_publish(files, state, day, outcomes, removed_now, mode_backfill, listed_details):
    """What a publish writes. Pure: returns ({file name: new text}, summary dict); nothing is written here.
    outcomes: [(id, kind, detail, record)] of the items this run decided. removed_now: [(id, verdict)] of listed maps."""
    maps = list(files["maps"])
    broken = json.loads(json.dumps(files["broken"]))
    listed = {str(m["id"]): m for m in maps if isinstance(m, dict) and str(m.get("id", "")).isdigit()}
    on = broken_ids(broken)
    for wid in blocked_ids(files):
        on.setdefault(wid, "blocked")
    summary = {"listed": [], "modes": {}, "broken": [], "packs": [], "removed": [], "advert": [], "back": []}
    out = {}
    # -- broken.json: crashers, kicks and adverts under "maps"; non-maps under "packs"; removed listed maps under "removed"
    for wid, kind, detail, rec in outcomes:
        name = clean(rec.get("title") or "") or "Workshop item %s" % wid
        rank = listed[wid]["rank"] if wid in listed else None
        if kind in ("crasher", "kick", "advert") and on.get(wid) not in ("maps", "packs", "blocked"):
            entry = {"id": int(wid), "rank": rank, "name": name, "crashes": int(detail.get("crashes") or 0),
                     "evidence": detail["evidence"]}
            if kind == "advert":
                entry["reason"] = detail["reason"]
                summary["advert"].append((wid, name, detail["reason"], detail.get("sample")))
            elif kind == "kick":
                entry["reason"] = "throws players off the server"
            broken.setdefault("maps", []).append(entry)
            on[wid] = "maps"
            summary["broken"].append((wid, name, kind, detail["evidence"]))
        elif kind == "nonmap" and on.get(wid) not in ("maps", "packs", "blocked") and wid not in listed:
            broken.setdefault("packs", []).append({
                "id": int(wid), "name": name, "kind": detail["kind"], "evidence": "confirmed by test", "phrase": None,
                "detail": detail["detail"], "list": "sync", "formerRank": None, "spareRank": None,
                "preview": rec.get("preview") or "", "description": clean(rec.get("description") or "", 200)})
            on[wid] = "packs"
            summary["packs"].append((wid, name, detail["kind"], detail["detail"]))
        elif kind == "nonmap" and wid in listed and on.get(wid) not in ("maps", "packs", "blocked"):
            # a listed map whose update turned it into a non-map: gate it like a crasher (a pick asks to confirm)
            broken.setdefault("maps", []).append({"id": int(wid), "rank": rank, "name": listed[wid].get("name") or name,
                                                  "crashes": 0, "evidence": detail["detail"], "reason": "is no longer a playable map"})
            on[wid] = "maps"
            summary["broken"].append((wid, name, "nonmap", detail["detail"]))
    # removed listed maps go to the bridge as this run's verdicts (POST /removed, Run.report_verdicts), which blocks them at once;
    # broken.json holds no list of them
    for wid, verdict in removed_now:
        if wid in listed:
            summary["removed"].append((wid, listed[wid]["rank"], listed[wid].get("name") or "", verdict))
    if broken != files["broken"]:
        out["broken.json"] = dumps(broken, indent=2)
    # -- maps.json: the Arms Race passes that are new, appended in popularity order (a number, once given, keeps its map)
    new_ar = [(wid, rec) for wid, kind, detail, rec in outcomes
              if kind == "pass" and wid not in listed and on.get(wid) is None and "Armsrace" in (rec.get("tags") or [])]
    new_ar.sort(key=lambda p: popularity_key(dict(p[1], id=p[0])))
    rank = max([int(m.get("rank") or 0) for m in maps] or [0])
    search = dict(files["search"]) if files.get("search") is not None else None
    for wid, rec in new_ar:
        rank += 1
        entry = {"rank": rank, "id": int(wid), "name": clean(rec.get("title") or "") or "Workshop item %s" % wid,
                 "sizeMb": size_mb(rec.get("size"))}
        maps.append(entry)
        listed[wid] = entry
        summary["listed"].append((wid, rank, entry["name"], entry["sizeMb"]))
        if search is not None:
            search[wid] = search_blob(entry["name"], wid, rec.get("tags") or [], rec.get("description") or "")
    if new_ar:
        out["maps.json"] = dumps(maps, indent=1)
        if search is not None:
            out["search.json"] = dumps(search, indent=0)
    # -- the mode lists: the maps.json entry shape, rank 1..N by popularity, never shrinking
    for mode, tag, _gt, _gm, fname in MODES[1:]:
        current = files["modes"].get(mode) or []
        entries = {}
        for e in current:
            if isinstance(e, dict) and str(e.get("id", "")).isdigit():
                entries[str(e["id"])] = dict(e)
            elif isinstance(e, dict) and e.get("official"):
                entries["official:" + str(e.get("name"))] = dict(e)
        pop = {}
        for wid, kind, detail, rec in outcomes:
            if kind == "pass" and tag in (rec.get("tags") or []) and on.get(wid) is None and wid not in entries:
                entries[wid] = {"id": int(wid), "name": clean(rec.get("title") or "") or "Workshop item %s" % wid,
                                "sizeMb": size_mb(rec.get("size"))}
                pop[wid] = rec
                summary["modes"].setdefault(mode, []).append((wid, entries[wid]["name"]))
        if mode_backfill:
            for wid, m in listed.items():
                item = listed_details.get(wid)
                if item is None or wid in entries or on.get(wid) is not None or workshop_verdict(item) != "ok":
                    continue
                if tag in item_tags(item):
                    entries[wid] = {"id": int(wid), "name": m.get("name") or item_name(item), "sizeMb": int(m.get("sizeMb") or 0)}
                    pop[wid] = {"subs": item.get("subscriptions"), "lifetime": item.get("lifetime_subscriptions")}
                    summary.setdefault("backfill", {}).setdefault(mode, 0)
                    summary["backfill"][mode] += 1
        for wid in list(entries):
            if wid.isdigit() and wid not in pop:
                item = listed_details.get(wid) or {}
                old = state.item(wid) or {}
                pop[wid] = {"subs": item.get("subscriptions", old.get("subs")), "lifetime": item.get("lifetime_subscriptions",
                                                                                                   old.get("lifetime"))}
        ids = [w for w in entries if w.isdigit()]
        ids.sort(key=lambda w: (-int(pop[w].get("subs") or 0), -int(pop[w].get("lifetime") or 0), int(w)))
        new_list = [{"rank": k + 1, "id": int(w), "name": entries[w]["name"], "sizeMb": int(entries[w].get("sizeMb") or 0)}
                    for k, w in enumerate(ids)]
        new_list += [entries[k] for k in entries if not k.isdigit()]
        if current and len(new_list) < len(current):
            new_list = current            # never shrink (the bridge refuses a list that drops below half anyway)
        if new_list != current and new_list:
            out[fname] = dumps(new_list, indent=1)
            summary["modes"].setdefault(mode, summary["modes"].get(mode, []))
            summary.setdefault("mode_totals", {})[mode] = len(new_list)
    # -- search.json: the mode lists' maps have their texts too, since the HUD's Maps page browses every mode: a map with none gets it now, from
    #    this run's outcome or the state, so this also brings the maps listed before it (old texts stay, merge_problems)
    if search is not None:
        recs = {wid: rec for wid, _kind, _detail, rec in outcomes}
        missing = 0
        for mode, _tag, _gt, _gm, fname in MODES[1:]:
            for e in json.loads(out[fname]) if fname in out else files["modes"].get(mode) or []:
                wid = str(e.get("id", "")) if isinstance(e, dict) else ""
                if wid.isdigit() and wid not in search:
                    rec = recs.get(wid) or state.item(wid) or {}
                    search[wid] = search_blob(e.get("name") or rec.get("title") or "", wid, rec.get("tags") or [], rec.get("description") or "")
                    missing += 1
        if missing:
            out["search.json"] = dumps(search, indent=0)
    return out, summary


# ------------------------------------------------------------------------------------------------
# 5b. The problem mail
# ------------------------------------------------------------------------------------------------

def send_mail(cfg, subject, body):
    """Plain-text mail through the SMTP relay the server's other mailers use (STARTTLS when it is offered).
    Returns None when sent, else the reason."""
    if not (cfg.smtp_host and cfg.mail_from and cfg.mail_to):
        return "no SMTP host, sender or recipient configured"
    msg = ("From: %s\r\nTo: %s\r\nSubject: %s\r\nDate: %s\r\nMessage-ID: %s\r\nMIME-Version: 1.0\r\n"
           "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\nX-Mailer: %s\r\n\r\n%s\r\n") % (
        cfg.mail_from, ", ".join(cfg.mail_to), subject, email.utils.formatdate(localtime=True),
        email.utils.make_msgid("workshop-sync"), USER_AGENT, body.replace("\n", "\r\n"))
    sender = email.utils.parseaddr(cfg.mail_from)[1] or cfg.mail_from
    last = None
    for tls in (True, False):
        try:
            with smtplib.SMTP(cfg.smtp_host, cfg.smtp_port, timeout=60) as smtp:
                smtp.ehlo()
                if tls and smtp.has_extn("starttls"):
                    smtp.starttls()
                    smtp.ehlo()
                elif tls:
                    tls = False
                smtp.sendmail(sender, cfg.mail_to, msg.encode("utf-8"))
            return None
        except (smtplib.SMTPException, OSError) as error:
            last = "%s: %s" % (type(error).__name__, error)
            if not tls:
                break
    return last


MAIL_ROWS = 40        # past this many lines of a kind the mail gives counts; the report keeps every line


def merge_published(before, now):
    """A run's publish steps as one: what each batch's publish reported, added up, for the report and the mail."""
    if not before:
        return now
    out = dict(before)
    for key, value in (now or {}).items():
        if isinstance(value, list):
            have = list(out.get(key) or [])
            out[key] = have + [x for x in value if x not in have] if key == "files" else have + value
        elif key == "modes" and isinstance(value, dict):
            modes = dict(out.get("modes") or {})
            for mode, rows in value.items():
                modes[mode] = list(modes.get(mode) or []) + list(rows)
            out["modes"] = modes
        elif key == "backfill" and isinstance(value, dict):
            fill = dict(out.get("backfill") or {})
            for mode, n in value.items():
                fill[mode] = fill.get(mode, 0) + n
            out["backfill"] = fill
        elif value not in (None, "", {}):
            out[key] = value
    return out


def mail_text(r, cfg):
    """A run's summary (its report's text, and the body of a problem mail), from its report `r`."""
    L = []
    w = L.append
    stop = r.get("api_stop")
    head = []
    pub = r.get("published") or {}
    if pub.get("listed"):
        head.append("%d new Arms Race map%s listed" % (len(pub["listed"]), "" if len(pub["listed"]) == 1 else "s"))
    for mode in ("deathmatch", "wingman"):
        n = len((pub.get("modes") or {}).get(mode) or [])
        if n:
            head.append("%d new %s map%s" % (n, mode.capitalize(), "" if n == 1 else "s"))
    nb = len(pub.get("broken") or []) + len(pub.get("packs") or [])
    if nb:
        head.append("%d on broken.json" % nb)
    if pub.get("removed"):
        head.append("%d gone from the Workshop" % len(pub["removed"]))
    subject = "%s Workshop sync %s%s: %s%s" % (cfg_name(cfg), r["day"], " (manual run)" if r.get("manual") else " (the run of %s)" % r.get("started_hm", "?"),
                                             ", ".join(head) or "nothing new", " - STOPPED: %s" % short_stop(stop) if stop else "")
    w("%s - the Workshop sync, a run of %s (%s to %s). It runs whenever nobody plays; one mail a day, the reports of every run in" % (
        cfg_name(cfg), r["day"], r.get("started_hm", "?"), r.get("ended_hm", "?")))
    w("its reports folder.")
    w("")
    if stop:
        w("STOPPED: %s" % stop)
        w("Every Web API step stopped for the rest of the day at that moment (the owner's rule: any 429, 403 or burst of")
        w("server errors). The next day starts again from the ledger's counts.")
        w("")
    for line in r.get("notes") or []:
        w("NOTE: %s" % line)
    if r.get("notes"):
        w("")
    d = r.get("discovery") or {}
    w("1. Find new maps (IPublishedFileService/QueryFiles, the Web API key):")
    if d.get("skipped"):
        w("   skipped: %s" % d["skipped"])
    else:
        w("   %d page(s), %d item(s) seen, %d new (%s); the Workshop holds %s items with these tags." % (
            d.get("pages", 0), d.get("seen", 0), len(d.get("new") or []), d.get("stop") or "-", d.get("total") or "?"))
    c = r.get("check") or {}
    w("2. Check the listed maps (ISteamRemoteStorage/GetPublishedFileDetails, no key):")
    if c.get("skipped"):
        w("   skipped: %s" % c["skipped"])
    else:
        w("   %d id(s) asked in %d call(s): %d gone from the Workshop, %d changed since last seen%s." % (
            c.get("asked", 0), c.get("calls", 0), len(c.get("removed") or []), len(c.get("changed") or []),
            ", %d not answered" % c["left"] if c.get("left") else ""))
        if c.get("suspicious"):
            w("   NOT BELIEVED: more than 10%% of the maps came back gone - nothing was marked removed in this run.")
    rt = r.get("ratings") or {}
    w("2b. Ratings (IPublishedFileService/GetDetails with vote_data, the Web API key; the keep set's top lists):")
    if rt.get("skipped"):
        w("   %s." % rt["skipped"])
    elif rt:
        w("   %d of %d scored in %d call(s): %d listed, %d waiting for their test%s." % (
            rt.get("scored", 0), rt.get("asked", 0), rt.get("calls", 0), rt.get("listed", 0), rt.get("waiting", 0),
            "" if rt.get("complete") else " - INCOMPLETE (%s), the old scores' time kept" % (rt.get("stop") or "%d failed" % rt.get("failed", 0))))
    sv = r.get("serve") or {}
    w("2c. For the game server (the keep set's maps it does not store, downloaded and handed over; it takes them in at its next start):")
    if sv.get("skipped"):
        w("   skipped: %s" % sv["skipped"])
    elif sv:
        w("   %d missing of the keep set, %d handed over (%s), %d tested in this run and handed over, %d failed, %d still missing%s." % (
            sv.get("missing", 0), sv.get("ok", 0), mb(sv.get("bytes", 0)), sv.get("tested", 0), len(sv.get("failed") or []),
            sv.get("left", 0), "; stopped: %s" % sv["stop"] if sv.get("stop") else ""))
    if r.get("requeued"):
        w("   %d map(s) skipped as too big for an older download cap are queued again (they fit today's)." % r["requeued"])
    dl = r.get("downloads") or {}
    w("3. Downloads (SteamCMD, anonymous login):")
    if dl.get("skipped"):
        w("   skipped: %s" % dl["skipped"])
    else:
        w("   %d downloaded (%s), %d failed%s; %d still queued for later runs." % (
            dl.get("ok", 0), mb(dl.get("bytes", 0)), len(dl.get("failed") or []),
            " (%s)" % "; ".join("%s: %s" % f for f in dl["failed"][:5]) if dl.get("failed") else "", dl.get("queued_left", 0)))
        if dl.get("stop"):
            w("   stopped: %s" % dl["stop"])
    t = r.get("tests") or {}
    w("4. Scan and load test (a test copy of the server, no plugins, its own anonymous logon):")
    if t.get("skipped"):
        w("   skipped: %s" % t["skipped"])
    rows = t.get("rows") or []
    passes = [row for row in rows if row.startswith("PASS")]
    if len(rows) > MAIL_ROWS:                 # a long run: the counts, what did not pass, a taste of what did; the report has them all
        counts = {}
        for row in rows:
            word = row.split()[0]
            counts[word] = counts.get(word, 0) + 1
        w("   %s." % ", ".join("%d %s" % (n, word) for word, n in sorted(counts.items(), key=lambda kv: -kv[1])))
        for row in [row for row in rows if not row.startswith("PASS")][:MAIL_ROWS]:
            w("   %s" % row)
        for row in passes[:10]:
            w("   %s" % row)
        if len(passes) > 10:
            w("   ... and %d more passes (all in the report)." % (len(passes) - 10))
    else:
        for row in rows:
            w("   %s" % row)
    if t.get("stop"):
        w("   stopped: %s" % t["stop"])
    w("5. Published into the bridge's data:")
    if pub.get("skipped"):
        w("   skipped: %s" % pub["skipped"])
    elif not pub.get("files") and not pub.get("verdicts"):
        w("   nothing to publish.")
    else:
        for wid, rank, name, size in (pub.get("listed") or [])[:MAIL_ROWS]:
            w("   maps.json  #%d %s (%s, %d MB)" % (rank, name, wid, size))
        if len(pub.get("listed") or []) > MAIL_ROWS:
            w("   maps.json  ... and %d more" % (len(pub["listed"]) - MAIL_ROWS))
        for mode, rows in sorted((pub.get("modes") or {}).items()):
            fname = {"deathmatch": "maps-deathmatch.json", "wingman": "maps-wingman.json"}[mode]
            if len(rows) > MAIL_ROWS // 2:
                w("   %s  %d new maps (all in the report)" % (fname, len(rows)))
                continue
            for wid, name in rows:
                w("   %s  %s (%s)" % (fname, name, wid))
        for mode, n in sorted((pub.get("backfill") or {}).items()):
            w("   %s: %d listed Arms Race map(s) that carry the %s tag" % (
                {"deathmatch": "maps-deathmatch.json", "wingman": "maps-wingman.json"}[mode], n, mode.capitalize()))
        for wid, name, kind, evidence in pub.get("broken") or []:
            w("   broken.json maps   %s (%s): %s" % (name, wid, evidence))
        for wid, name, kind, detail in (pub.get("packs") or [])[:MAIL_ROWS]:
            w("   broken.json packs  %s (%s): %s" % (name, wid, kind))
        if len(pub.get("packs") or []) > MAIL_ROWS:
            w("   broken.json packs  ... and %d more" % (len(pub["packs"]) - MAIL_ROWS))
        for wid, rank, name, verdict in pub.get("removed") or []:
            w("   gone from the Workshop  #%s %s (%s): %s" % (rank, name, wid, verdict))
        if pub.get("verdicts"):
            w("   the check's verdicts, handed to the bridge (POST /removed): %s" % pub["verdicts"])
        w("   files: %s; bridge: %s" % (", ".join(pub.get("files") or []), pub.get("reload") or "not asked"))
        if pub.get("listed"):
            w("   The new maps have no size class yet (sizes.json is measured on the PC) and no picture until the next HUD")
            w("   content update.")
    lim = r.get("limits") or {}
    w("")
    w("Limits: Web API calls this run %s of %s, last 24 h %s of %s (10%% of Valve's 100,000), at least %.0f s apart;"
      % (lim.get("api_run"), lim.get("api_run_cap"), lim.get("api_24h"), lim.get("api_day_cap"), lim.get("api_gap", 2)))
    w("downloads today %s, %s%s - no daily cap (the owner's 36 c): only Steam's push-back stops them; test-engine starts %s of %s." % (
        lim.get("dl_items"), mb(lim.get("dl_bytes") or 0),
        " (with %d copies the test engine fetched itself)" % lim["dl_engine"] if lim.get("dl_engine") else "",
        lim.get("engine_starts"), lim.get("engine_starts_max")))
    bl = r.get("backlog") or {}
    if bl.get("items"):
        eta = r.get("backlog_until") or ""
        w("Backlog: %d item(s) waiting, %s (Deathmatch %d, Wingman %d, Arms Race %d - one item can carry several tags), taken"
          % (bl["items"], mb(bl.get("bytes") or 0), bl["by_mode"].get("deathmatch", 0), bl["by_mode"].get("wingman", 0),
             bl["by_mode"].get("armsrace", 0)))
        w("the ones the keep set wants first, new uploads next, then strictly by subscribers. About %d more day(s)%s: %.0f s a map"
          % (bl.get("days", 0), eta, bl.get("pace", PACE_DEFAULT)))
        w("(download and test), about %.0f h a day while nobody plays (no time windows), so about %d a day; every hour somebody"
          % (bl.get("hours", 0), bl["per_day"] if isinstance(bl.get("per_day"), int) else 0))
        w("plays comes off it. No daily download cap (the owner's 36 c): only time and the test speed set it.")
    if r.get("waited"):
        w("Waited: %s." % "; ".join(r["waited"]))
    if r.get("errors"):
        w("")
        for e in r["errors"]:
            w("ERROR: %s" % e)
    w("")
    w("The full report: %s" % r.get("report_path", "?"))
    return subject, "\n".join(L)


def issues_of(r):
    """THE MAIL'S PROBLEMS (the owner's 34, 2026-11-03: the mail only for issues and errors): [(key, what went wrong, what the
    sync did about it)] of a run's report. What counts: a run stopped early or a step that errored, Steam pushing back (HTTP 429,
    403 or 401; a SteamCMD rate-limit answer or two Access Denied in a row; 3 failed downloads in a row), the test engine failing
    to start, a safety check refusing (the game server's deletion, the check's 10% rule, the publish's), the disk running low, a
    hand-over that failed, and any other error. The key groups a problem that persists, so it is mailed once a day at most."""
    out = []

    def add(key, what, did):
        out.append((key, what, did))
    api = r.get("api_stop")
    if api:
        add("steam-webapi", "Steam's Web API pushed back: %s." % clean(api, 220),
            "Every Web API step stopped for the rest of the day%s; the next day's runs try again by themselves." % (
                ", and the downloads with them" if re.search(r"HTTP (?:429|403|401)", str(api)) else ""))
    dl, sv, t = r.get("downloads") or {}, r.get("serve") or {}, r.get("tests") or {}
    for name, part in (("the test downloads", dl), ("the hand-over downloads", sv)):
        stop = str(part.get("stop") or "")
        if "SteamCMD answered" in stop or "no more downloads today" in stop:
            add("steamcmd-pushback", "SteamCMD pushed back during %s: %s." % (name, clean(stop, 220)),
                "No more downloads today; they resume tomorrow by themselves.")
        elif "in a row failed" in stop:
            add("downloads-in-a-row", "3 downloads in a row failed during %s." % name,
                "This run downloaded nothing more; the next run tries again, and a map is tried 3 times at most.")
        elif "did not log in" in stop or "stopped answering" in stop:
            add("steamcmd", "SteamCMD failed during %s: %s." % (name, clean(stop, 220)),
                "This run's downloads stopped; the next run starts SteamCMD again.")
        elif "hand-overs wait" in stop:
            add("handovers-waiting", "The game server has not taken in its hand-overs: %s." % clean(stop, 160),
                "No more hand-overs until it does; its next engine start takes them in.")
    stalls = [tuple(s) for s in r.get("stalls") or [] if isinstance(s, (list, tuple)) and len(s) == 2]
    again = [w for w, n in stalls if n >= 2]
    if len(stalls) >= 2 or again:      # one stall is routine (the owner's 86 a): a fresh session took the next map
        add("steamcmd-stalls", "SteamCMD stalled %s: %s." % (
            "twice in this run" if len(stalls) >= 2 else "on the same map twice today",
            ", ".join("%s (%s today)" % (w, n) for w, n in stalls[:6])),
            "This run's downloads stopped at the second stall; a map is tried 3 times at most, and the next run starts SteamCMD afresh."
            if len(stalls) >= 2 else "The next map got a fresh session; that map is tried again later, 3 times at most.")
    if t.get("engine_failed"):
        add("test-engine", "The test engine did not start: %s." % clean(t["engine_failed"], 220),
            "This run's tests stopped; the downloaded maps wait in the staging folder for the next run.")
    elif t.get("stop") and not re.search(r"deadline|somebody was on|game was updated", str(t["stop"])):
        add("tests-stopped", "The tests stopped early: %s." % clean(t["stop"], 220), "The maps without a verdict wait for the next run.")
    for name in ("discovery", "ratings"):
        skipped = str((r.get(name) or {}).get("skipped") or "")
        if skipped.startswith("failed"):
            add("%s-failed" % name, "The %s step failed: %s." % (name, clean(skipped, 200)), "The run went on without it; the next one tries again.")
    verdicts = str((r.get("published") or {}).get("verdicts") or "")
    if verdicts.startswith(("the report failed", "not reported")):
        add("verdicts-not-reported", "The Workshop check's verdicts did not reach the bridge: %s." % clean(verdicts, 220),
            "Maps gone from the Workshop are not blocked until a check reaches it; the next check (every SYNC_API_EVERY_HOURS) reports again.")
    if (r.get("check") or {}).get("suspicious"):
        add("check-not-believed", "More than 10% of the listed maps came back gone from the Workshop - not believed.",
            "Nothing was marked removed; the bridge's own rule refuses it too.")
    failed = sv.get("handover_failed") or []
    if failed:
        add("handover-failed", "%d hand-over(s) to the game server failed: %s." % (len(failed), clean("; ".join("%s %s" % (w, why) for w, why in failed[:5]), 300)),
            "Each map stays staged; the next run hands it over, or downloads it again.")
    disk = r.get("disk") or {}
    if disk.get("low"):
        add("disk-low", "The disk is running low: %s." % disk["low"], "Nothing yet - the sync keeps working; make room, or raise the dataset's quota.")
    wk = r.get("watcher_keep") or {}
    if wk.get("problems"):
        add("deletion-refused:%s" % re.sub(r"\d+", "#", str(wk["problems"][0]))[:60],
            "The game server's deletion at its engine start of %s refused: %s." % (wk.get("computed", "?"), clean("; ".join(map(str, wk["problems"][:3])), 300)),
            "It deleted nothing and kept every map; it tries again at its next engine start.")
    for e in r.get("errors") or []:
        add("error:%s" % re.sub(r"\d+", "#", str(e))[:80], "An error: %s." % clean(e, 300), "The run went on with its other steps.")
    return out


def issue_mail(issues, ongoing, r, cfg):
    """(subject, body) of a problem mail: what went wrong, what the sync did, the dashboard, the run's report."""
    when = "%s %s-%s" % (r.get("day", "?"), r.get("started_hm", "?"), r.get("ended_hm", "?"))
    subject = "%s Workshop sync: %s%s" % (cfg_name(cfg), issues[0][1][:90].rstrip("."), " (and %d more)" % (len(issues) - 1) if len(issues) > 1 else "")
    lines = ["The Workshop sync of %s ran into %s in its %srun of %s." % (
        cfg_name(cfg), "a problem" if len(issues) == 1 else "%d problems" % len(issues), "manual " if r.get("manual") else "", when), ""]
    for _key, what, did in issues:
        lines += ["What went wrong: %s" % what, "What the sync did: %s" % did, ""]
    if ongoing:
        lines += ["Still going on, mailed already in the last day: %s" % " | ".join(what for _k, what, _d in ongoing), ""]
    lines.append("The dashboard: %s" % (cfg.dashboard_url or "the HUD's Sync tab"))
    lines.append("This run's full report: %s" % (r.get("report_path", "?")[:-5] + ".txt" if r.get("report_path") else "?"))
    lines.append("Runs without a problem send no mail (the owner's 34); their reports stay in the reports folder and on the dashboard.")
    return subject, "\n".join(lines)


def mail_issues(cfg, clock, issues, r):
    """Mails what is new among `issues`, once a day at most per problem (<state>/mail-sent.json keeps when each was last sent).
    Returns the outcome for the report: "sent", or why not."""
    if not issues:
        return "NOT sent: no problem in this run (the mail is for problems only)"
    path = os.path.join(cfg.state_dir, "mail-sent.json")
    sent = read_json(path, {}) or {}
    now = clock.now()
    new = [i for i in issues if now - float(sent.get(i[0]) or 0) >= MAIL_EVERY]
    if not new:
        return "NOT sent: every problem of this run was mailed in the last %s" % hms(MAIL_EVERY)
    ongoing = [i for i in issues if i not in new]
    subject, body = issue_mail(new, ongoing, r, cfg)
    why = send_mail(cfg, subject, body)
    if why is not None:
        return "NOT sent: %s" % why
    for key, _w, _d in new:
        sent[key] = now
    for key in [k for k, at in sent.items() if now - float(at or 0) > 14 * DAY]:
        del sent[key]
    try:
        write_atomic(path, dumps(sent), mode=0o644)
    except OSError as error:
        log("mail: %s not written (%s)" % (path, error))
    return "sent: %s" % subject


def mail_crash(cfg, clock, error, manual):
    """A run that ended in an exception has no report: its problem is mailed from here, once a day at most."""
    r = {"day": clock.day(), "manual": manual, "started_hm": "?", "ended_hm": clock.local().strftime("%H:%M")}
    return mail_issues(cfg, clock, [("run-failed:%s" % type(error).__name__, "The run failed: %s." % clean(repr(error), 300),
                                     "The service went on; it starts the next run after %s." % hms(IDLE_WAIT))], r)


def short_stop(text):
    m = re.search(r"HTTP \d+|server errors|network failures|cap of [\d,]+", text or "")
    return m.group(0) if m else (text or "")[:60]


def cfg_name(cfg):
    return os.environ.get("SYNC_SERVER_NAME") or "CS2 server"


# ------------------------------------------------------------------------------------------------
# THE STATUS FILE for the in-game dashboard (the owner's 34, brought forward on 2026-11-03)
# ------------------------------------------------------------------------------------------------
STATUS_FILE = "sync-status.json"  # in the bridge's data folder; the bridge does not watch it (Reload.cs watches its own files only)
STATUS_TICK = 15                  # seconds: written at least this often while a run is active
STATUS_HEAVY = 15                 # seconds: the counts, the queue and the keep set are rebuilt at most this often during a run
STATUS_IDLE = 60                  # seconds: written again this often while the service is idle, waiting or stopped...
STATUS_IDLE_HEAVY = 300           # ...with the counts rebuilt at most this often
STATUS_RUNS, STATUS_MAPS, STATUS_NEXT = 50, 200, 25
PHASE_STATE = {"start": "waiting", "discovery": "discovery", "check": "check", "ratings": "ratings", "serve": "serve",
               "downloads": "download", "tests": "test", "publish": "publish"}
PHASE_WHY = {"start": "A run is starting.",
             "discovery": "Looking for new maps on the Workshop.",
             "check": "Asking the Workshop about every listed map: gone, hidden or updated.",
             "ratings": "Fetching the Workshop's scores, subscriptions and trends for the lists of maps the server keeps.",
             "serve": "Downloading the maps the server keeps but does not have yet, and handing them over to it.",
             "downloads": "Downloading queued maps for their test.",
             "tests": "Load-testing downloaded maps on a test copy of the server.",
             "publish": "Putting the tested maps on the map lists."}
QUEUE_MODE = {"armsrace-new": "armsrace", "armsrace-original": "armsrace", "deathmatch": "deathmatch", "wingman": "wingman"}
BROKEN_KINDS = ("advert", "crasher", "kick")


def list_mode(rec, listed=None, wid=None):
    """armsrace | deathmatch | wingman: the list a map is on, else the first of its Workshop tags; armsrace when neither says. (Not
    mode_of: that name is test_load's - a second definition replaced it on 2026-11-03 and crashed every test load.)"""
    if listed and wid in listed:
        return listed[wid]
    tags = (rec or {}).get("tags") or []
    for mode, tag, _gt, _gm, _f in MODES:
        if tag in tags:
            return mode
    return "armsrace"


def unix_of(stamp):
    try:
        return int(time.mktime(time.strptime(str(stamp), "%Y-%m-%dT%H:%M:%S")))
    except (TypeError, ValueError, OverflowError):
        return None


def run_entry(r):
    """A run's line in the status file's history, from its report (the ones written before the status file existed too)."""
    dl, sv, t, pub = r.get("downloads") or {}, r.get("serve") or {}, r.get("tests") or {}, r.get("published") or {}
    rows = t.get("rows") or []
    stop = next((str(s) for s in (dl.get("stop"), t.get("stop"), sv.get("stop"), r.get("api_stop")) if s), None)
    return {"started": unix_of(r.get("started")), "ended": unix_of(r.get("ended")), "kind": "manual" if r.get("manual") else "auto",
            "downloads": int(dl.get("ok") or 0) + int(sv.get("downloads", sv.get("ok")) or 0),
            "bytes": int(dl.get("bytes") or 0) + int(sv.get("dl_bytes", sv.get("bytes")) or 0),
            "pass": sum(1 for x in rows if str(x).startswith("PASS")), "rejected": sum(1 for x in rows if not str(x).startswith("PASS")),
            "listed": len(pub.get("listed") or []) + sum(len(v or []) for v in (pub.get("modes") or {}).values()),
            "removed": len((r.get("check") or {}).get("removed") or []), "stop": stop[:200] if stop else None}


def seed_runs(state_dir):
    """The history's runs from the reports kept so far, newest first: what the status file starts with."""
    folder = os.path.join(state_dir, "reports")
    try:
        names = sorted((n for n in os.listdir(folder) if n.endswith(".json")), reverse=True)
    except OSError:
        return []
    runs = []
    for name in names:
        r = read_json(os.path.join(folder, name), None)
        if isinstance(r, dict) and r.get("started") and r.get("ended"):
            runs.append(run_entry(r))
        if len(runs) >= STATUS_RUNS:
            break
    runs.sort(key=lambda e: -(e.get("started") or 0))
    return runs


class Status(object):
    """THE STATUS FILE: <bridge>/sync-status.json, schema 1, for the in-game dashboard - what the sync does now and why, today's
    counts, the totals, the download queue (its next maps in the order they are taken), the keep set's parts and the history
    of runs and map events. Written atomically (a tmp file, then a rename): at every phase change, at least every STATUS_TICK
    seconds while a run is active (a ticker thread writes the last picture with a fresh time; the main thread alone rebuilds
    it), and when the service goes idle, waits or stops (again every STATUS_IDLE seconds, so its time says the sync is alive).
    A failure to build or write it is logged once and never stops the sync."""

    def __init__(self, cfg, clock):
        self.cfg, self.clock = cfg, clock
        self.path = os.path.join(cfg.bridge_data, STATUS_FILE)
        self.lock = threading.Lock()
        self.now = {"state": "idle", "why": "The sync is starting.", "map": None, "step": None, "run": None}
        self.heavy = {}
        self.heavy_at = 0.0
        self.failed = None
        self.ticker, self.stop_event = None, threading.Event()

    def set(self, **fields):
        self.now = dict(self.now, **fields)            # a new dict each time: the ticker never sees one half-changed

    def doc(self):
        out = {"schema": 1, "updated": int(self.clock.now()), "now": self.now}
        for key, empty in (("today", {}), ("totals", {}), ("queue", {}), ("keep", {"parts": [], "totalGb": 0.0}),
                           ("history", {"runs": [], "maps": []})):
            out[key] = self.heavy.get(key, empty)
        return out

    def write(self):
        with self.lock:
            try:
                write_atomic(self.path, json.dumps(self.doc(), ensure_ascii=False, separators=(",", ":")) + "\n", mode=0o644)
                if self.failed:
                    log("status: %s written again" % self.path)
                self.failed = None
            except Exception as error:   # the dashboard must never stop the sync
                if self.failed != str(error):
                    log("status: %s not written (%s)" % (self.path, error))
                self.failed = str(error)

    def rebuild(self, run, force=False, every=STATUS_HEAVY):
        """The counts, the queue, the keep set and the history again (the main thread only), at most every `every` seconds."""
        if not force and self.clock.now() - self.heavy_at < every:
            return
        self.heavy_at = self.clock.now()
        try:
            self.heavy = status_heavy(run)
        except Exception as error:
            if self.failed != "rebuild: %r" % error:
                log("status: not rebuilt (%r) - the last picture stays" % error)
            self.failed = "rebuild: %r" % error

    def start_ticker(self):
        self.stop_ticker()
        event = threading.Event()

        def tick():
            while not event.wait(STATUS_TICK):
                self.write()
        self.stop_event = event
        self.ticker = threading.Thread(target=tick, name="sync-status", daemon=True)
        self.ticker.start()

    def stop_ticker(self):
        if self.ticker is not None:
            self.stop_event.set()
            self.ticker.join(5)
            self.ticker = None


def status_heavy(n):
    """The status file's parts but "now", from a Run `n` whose state (and ledger) are loaded."""
    cfg, clock = n.cfg, n.clock
    items = n.state.data.get("items") or {}
    day = clock.day()
    book = ((n.state.data.get("downloads") or {}).get(day) or {})
    decided = [r for r in items.values() if r.get("decided") == day and r.get("verdict")]
    today = {"downloads": int(book.get("items") or 0), "bytes": int(book.get("bytes") or 0),
             "pass": sum(1 for r in decided if r["verdict"] == "pass"), "rejected": sum(1 for r in decided if r["verdict"] != "pass"),
             "failedDownloads": int(book.get("failed") or 0),
             "apiCalls": n.ledger.count(day) if n.ledger.data is not None else 0}
    if book.get("stop"):
        today["stop"] = str(book["stop"])[:200]
    verdicts = [r.get("verdict") for r in items.values() if r.get("verdict")]
    try:
        files = bridge_files(cfg)
    except (OSError, ValueError):
        files = None
    listed, sizes, names = {}, {}, {}
    if files:
        for mode, entries in [("armsrace", files["maps"])] + [(m, files["modes"].get(m) or []) for m in ("deathmatch", "wingman")]:
            for entry in entries:
                if isinstance(entry, dict) and str(entry.get("id") or "").isdigit():
                    listed.setdefault(str(entry["id"]), mode)
                    sizes.setdefault(str(entry["id"]), int(entry.get("sizeMb") or 0) << 20)
                    names.setdefault(str(entry["id"]), clean(entry.get("name") or "", 80))
    n.listed_mode, n.listed_size, n.listed_name = listed, sizes, names
    totals = {"tested": len(verdicts), "pass": sum(1 for v in verdicts if v == "pass"),
              "broken": sum(1 for v in verdicts if v in BROKEN_KINDS), "nonmap": sum(1 for v in verdicts if v == "nonmap"),
              "listed": {"armsrace": len(files["maps"]) if files else 0,
                         "deathmatch": len(files["modes"].get("deathmatch") or []) if files else 0,
                         "wingman": len(files["modes"].get("wingman") or []) if files else 0}}

    def size_of(wid):
        return sizes.get(wid) or int((items.get(wid) or {}).get("size") or 0)
    # the queue, in the order the downloads take it
    order = n.queue(known_ids(files, cfg) if files else {}, {})
    top, fresh, changed = getattr(n, "q_top", set()), getattr(n, "q_fresh", set()), getattr(n, "q_changed", set())

    def why(wid):
        rec = items.get(wid) or {}
        if wid in top:
            return "it would make one of its mode's lists of maps the server keeps"
        if wid in changed:
            return "it changed on the Workshop since its test"
        if wid in fresh:
            return "a new upload"
        return "by popularity: %d subscribers" % int(rec.get("subs") or 0)
    pace = n.pace() or PACE_DEFAULT
    queue = {"total": len(order), "bytes": sum(int((items.get(w) or {}).get("size") or 0) for w in order),
             "byMode": dict((mode, sum(1 for w in order if tag in ((items.get(w) or {}).get("tags") or []))) for mode, tag, _g, _m, _f in MODES),
             "eta": int(clock.now() + len(order) * pace * DAY / (HOURS_A_DAY * 3600.0)) if order else None,
             "next": [{"id": w, "name": clean((items.get(w) or {}).get("title") or "", 80), "mode": list_mode(items.get(w), listed, w),
                       "sizeMb": int((items.get(w) or {}).get("size") or 0) >> 20, "why": why(w)} for w in order[:STATUS_NEXT]]}
    # the keep set: its parts as the game server stores them, and what the sync still has to hand over
    # the HUD item: the sync is not told which it is - the restart watcher's keep.json of its last engine start names it
    watcher_keep = read_json(os.path.join(os.path.dirname(cfg.watcher_state), "keep.json"), {}) or {}
    hud = [str(w) for w in ((watcher_keep.get("parts") or {}).get("hud") or []) if str(w).isdigit()] if isinstance(watcher_keep, dict) else []
    keep = safe_keep_set(keep_inputs(cfg.bridge_data), clock.now(), hud=hud[0] if hud else "")
    has, pending = server_maps(cfg.game_dir), handovers(cfg.handover_dir)
    want = [w for w in dict.fromkeys(keep["up"] + [w for mode, _f in MODE_LISTS for w in keep["top"].get(mode) or []] + keep["own"])
            if w not in has and w not in pending and w not in cfg.exclude]
    queue["serve"] = {"missing": len(want), "bytes": sum(size_of(w) for w in want), "handedOver": len(pending),
                      "handedOverBytes": sum(pending.values())}
    parts = []

    def part(name, mode, ids, **extra):
        ids = list(dict.fromkeys(str(w) for w in ids))
        parts.append(dict({"part": name, "mode": mode, "count": len(ids), "onDisk": sum(1 for w in ids if w in has),
                           "gb": round(sum(size_of(w) for w in ids) / float(1 << 30), 1)}, **extra))
    part("up", "", keep["up"])
    for mode, _f in MODE_LISTS:
        lists = keep["lists"].get(mode) or {}
        for name in ("rated", "popular", "subscribed"):
            part(name, mode, lists.get(name) or [])
    part("own", "", keep["own"])
    part("hud", "", keep["hud"])
    for name, ids in (keep["queues"] or {}).items():
        part("queues", QUEUE_MODE.get(name, ""), ids, queue=name)
    if keep["requested"]:
        part("queues", "", keep["requested"], queue="requested")
    keep_out = {"parts": parts, "totalGb": round(sum(size_of(w) for w in keep["all"]) / float(1 << 30), 1), "count": len(keep["all"]),
                "onDisk": sum(1 for w in keep["all"] if w in has),
                "problems": [p for p in keep["problems"] if not (p.startswith("no HUD item") and not hud)][:5]}
    hist = n.state.data.setdefault("history", {})
    if not hist.get("runs"):
        hist["runs"] = seed_runs(cfg.state_dir)
    history = {"runs": list(hist.get("runs") or [])[:STATUS_RUNS], "maps": list(reversed(hist.get("maps") or []))[:STATUS_MAPS]}
    return {"today": today, "totals": totals, "queue": queue, "keep": keep_out, "history": history}


# ------------------------------------------------------------------------------------------------
# One run
# ------------------------------------------------------------------------------------------------

class Run(object):
    def __init__(self, cfg, clock, manual=False, sleep=time.sleep, status=None):
        self.cfg, self.clock, self.manual, self.sleep = cfg, clock, manual, sleep
        self.status = status if status is not None else Status(cfg, clock)
        self.phase_state = ("waiting", PHASE_WHY["start"])
        self.listed_mode, self.listed_size, self.listed_name = {}, {}, {}
        self.day = clock.day()
        self.t0 = clock.now()
        self.human_at = None        # when this run last saw somebody on the game server (or could not tell)
        self.engine_starts = 0      # the test engine's starts this run, over all its batches
        self.stalls = []            # (id, its stalls today) of every SteamCMD stall in this run: one is routine, two stop the downloads
        self.worked = 0.0           # seconds spent on downloads and tests, for the pace in the mail
        self.decided = 0            # items decided in that time
        os.makedirs(cfg.state_dir, exist_ok=True)
        os.makedirs(os.path.join(cfg.state_dir, "reports"), exist_ok=True)
        os.makedirs(cfg.work_dir, exist_ok=True)
        LOG_FILE[0] = os.path.join(cfg.state_dir, "sync.log")
        self.deadline = self._deadline()
        self.report = {"day": self.day, "manual": manual, "started": time.strftime("%Y-%m-%dT%H:%M:%S"),
                       "started_hm": clock.local().strftime("%H:%M"), "notes": [], "errors": [], "waited": []}
        self.ledger = ApiLedger(os.path.join(cfg.state_dir, "api-ledger.json"), cfg.api_day_cap, clock)
        self.state = State(os.path.join(cfg.state_dir, "state.json"))
        self.engine = None
        self.verdicts = []          # this run's (id, ok|removed|hidden|banned) of every listed map the check answered
        self.backlog = None

    def _deadline(self):
        return run_deadline(self.cfg, self.clock)

    def phase(self, name):
        self.report["phase"] = name
        log("phase: %s" % name)
        self.phase_state = (PHASE_STATE.get(name, "waiting"), PHASE_WHY.get(name, "Working."))
        self.status.set(state=self.phase_state[0], why=self.phase_state[1], map=None, step=None)
        self.status.rebuild(self, force=True)
        self.status.write()

    def left(self):
        return self.deadline - self.clock.now()

    def on_map(self, wid, done, total):
        """The status file: the map this step is on now, and how far the step is."""
        rec, wid = self.state.item(wid) or {}, str(wid)
        self.status.set(map={"id": wid, "name": clean(rec.get("title") or "", 80) or self.listed_name.get(wid, ""),
                             "mode": list_mode(rec, self.listed_mode, wid),
                             "sizeMb": (int(rec.get("size") or 0) or self.listed_size.get(wid, 0)) >> 20}, step={"done": int(done), "total": int(total)})
        self.status.rebuild(self)
        self.status.write()

    def event(self, wid, event, why):
        """A map's event for the status file's history (listed, broken, removed, changed, handed-over, failed), kept in the state."""
        try:
            hist = self.state.data.setdefault("history", {})
            rec = self.state.item(wid) or {}
            hist.setdefault("maps", []).append({"at": int(self.clock.now()), "id": str(wid),
                                                "name": clean(rec.get("title") or "", 80) or self.listed_name.get(str(wid), ""),
                                                "mode": list_mode(rec, self.listed_mode, str(wid)), "event": event, "why": clean(why, 200)})
            del hist["maps"][:-STATUS_MAPS]
        except Exception as error:
            log("status: an event of %s not kept (%r)" % (wid, error))

    def humans(self):
        """Humans on the game server by A2S (None: it did not answer, which counts as somebody); remembers when it last saw any."""
        h = humans_on(self.cfg)
        if h is None or h > 0:
            self.human_at = self.clock.now()
        return h

    def wait_for_nobody(self, what, margin=0):
        """True once the game server shows 0 humans (by A2S) - and has for SYNC_QUIET_MINUTES, if this run saw anybody: a map change
        shows 0 for a moment. False if the deadline comes first."""
        t = self.clock.now()
        said = False
        while True:
            h = self.humans()
            flag = os.path.exists(os.path.join(self.cfg.bridge_data, "maintenance"))
            quiet = self.human_at is None or self.clock.now() - self.human_at >= self.cfg.quiet
            if h == 0 and not flag and quiet:
                if said:
                    self.report["waited"].append("%s for %s (somebody on the server, or the maintenance flag)" % (
                        what, hms(self.clock.now() - t)))
                    self.status.set(state=self.phase_state[0], why=self.phase_state[1])
                    self.status.write()
                return True
            if not said:
                log("%s: waiting - %s" % (what, "the game server does not answer A2S" if h is None else (
                    "%d human(s) on the game server" % h if h else ("the bridge's maintenance flag is set" if flag else
                                                                    "somebody was on the server %s ago" % hms(self.clock.now() - self.human_at)))))
                said = True
                self.status.set(state="waiting", why=(
                    "The game server does not answer, so the sync waits." if h is None else
                    "Somebody is playing: the sync waits until the server has been empty for %d minutes." % (self.cfg.quiet // 60) if h
                    else "The server is in maintenance, so the sync waits." if flag else
                    "Somebody played a moment ago: the sync waits until the server has been empty for %d minutes." % (self.cfg.quiet // 60)))
                self.status.write()
            if self.left() <= margin + 60:
                self.report["waited"].append("%s: gave up at the deadline (%s)" % (what, "nobody answered" if h is None else
                                                                                    "%s human(s)" % h))
                return False
            self.sleep(60)

    def wait_for_prefetch(self):
        """True once no nightly prefetch of the restart watcher is due or running: from 10 minutes before SYNC_PREFETCH_AT until the
        watcher stamps tonight's as done (its state.json, prefetch_day) or SYNC_PREFETCH_MINUTES are over, this run downloads and
        tests nothing - the two never fetch from Steam at once. False when the run's deadline comes first."""
        t = self.clock.now()
        said = False
        while True:
            why = prefetch_due(self.cfg, self.clock)
            if why is None:
                if said:
                    self.report["waited"].append("the watcher's prefetch for %s" % hms(self.clock.now() - t))
                return True
            if self.left() < 600:
                self.report["notes"].append("%s until the run's deadline - its downloads and tests wait for the next run" % why)
                return False
            if not said:
                log("waiting: %s" % why)
                said = True
                self.status.set(state="waiting", why="The server's nightly prefetch runs first: the sync waits until it is done.")
                self.status.write()
            self.sleep(60)

    # -- the steps ----------------------------------------------------------------------------------------------
    def run(self):
        """One run, with the status file kept up to date: a ticker writes it every STATUS_TICK seconds until the run ends."""
        self.status.set(run={"started": int(self.t0), "deadline": int(self.deadline), "kind": "manual" if self.manual else "auto"},
                        map=None, step=None)
        self.status.start_ticker()
        try:
            return self._run()
        finally:
            self.status.stop_ticker()
            self.status.set(state="idle", why="The run that started at %s has ended." % self.clock.local(self.t0).strftime("%H:%M"),
                            map=None, step=None, run=None)
            self.status.rebuild(self, force=True)
            self.status.write()

    def _run(self):
        self.phase("start")
        try:
            self.ledger.load()
            self.state.load()
        except (ValueError, OSError) as error:
            self.report["errors"].append(str(error))
            self.finish()
            return self.report
        if self.ledger.broken:
            self.report["errors"].append(self.ledger.broken)
        try:
            files = bridge_files(self.cfg)
        except (OSError, ValueError) as error:
            self.report["errors"].append("the bridge's data cannot be read (%s) - nothing done in this run" % error)
            self.finish()
            return self.report
        self.requeue_size_skips()
        known = known_ids(files, self.cfg)
        listed = [str(m["id"]) for m in files["maps"] if isinstance(m, dict) and str(m.get("id", "")).isdigit()]
        for mode in files["modes"]:
            listed += [str(m["id"]) for m in files["modes"][mode] if isinstance(m, dict) and str(m.get("id", "")).isdigit()
                       and str(m["id"]) not in listed]
        log("start: %d listed map(s), %d id(s) known, %d item(s) in the state, deadline %s%s" % (
            len(listed), len(known), len(self.state.data["items"]), time.strftime("%H:%M", time.localtime(self.deadline)),
            " (manual run)" if self.manual else ""))
        prefetch_done = self.wait_for_prefetch()
        key = read_key(self.cfg.key_file)
        api = SteamApi(self.ledger, self.day, self.cfg.api_gap, key=key, run_cap=self.cfg.api_run_cap)
        self.api = api
        last_api = float(self.state.data.get("last_api") or 0)
        api_due = (self.cfg.force_api or self.clock.now() - last_api >= self.cfg.api_every or not backlog_waits(self.state.data)) \
            and not self.cfg.skip_api
        details, removed_now = {}, []
        if not api_due:
            why = "the last ran at %s; again after %s, or once nothing waits" % (self.clock.local(last_api).strftime("%H:%M"), hms(self.cfg.api_every))
            for step in ("discovery", "check", "ratings"):
                self.report[step] = {"skipped": why}
            log("discovery, the check and the ratings: not this run - %s" % why)
        else:
            # 1. discovery
            self.phase("discovery")
            if key is None:
                log("discovery skipped: no Steam Web API key in %s - the other steps run" % (self.cfg.key_file or "(not configured)"))
                self.report["discovery"] = {"skipped": "no Web API key in the secret file"}
            else:
                try:
                    d = discover(api, self.cfg, known, self.state, self.day, self.deadline - 1800, now=self.clock.now)
                    self.report["discovery"] = d
                    log("discovery: %d page(s), %d seen, %d new (%s)" % (d["pages"], d["seen"], len(d["new"]), d["stop"]))
                except StopApi as stop:
                    self.report["discovery"] = {"skipped": str(stop)}
                    log("discovery stopped: %s" % stop)
                except ApiError as error:
                    self.report["discovery"] = {"skipped": "failed: %s" % error}
                self.state.save()
            # 2. the check: the listed maps, then the head of the download queue
            self.phase("check")
            queue_head = self.queue(known, {})[:QUEUE_CHECK]
            ids = list(dict.fromkeys(listed + queue_head))
            details, left, chk = {}, [], {"asked": len(ids)}
            try:
                details, left = check(api, ids, self.deadline - 1500, now=self.clock.now)
                chk["calls"] = api.by_api.get("GetPublishedFileDetails", 0)
                chk["left"] = len(left)
            except StopApi as stop:
                chk["skipped"] = str(stop)
                log("check stopped: %s" % stop)
            judged = judge_listed(details, listed, self.state, self.cfg, self.day) if details else {
                "removed": [], "changed": [], "missing": len(listed), "suspicious": False}
            removed_now = [] if judged["suspicious"] else list(judged["removed"])
            known = read_json(os.path.join(self.cfg.bridge_data, "removed.json"), {})       # the bridge's verdicts so far: this sync's
            known = (known.get("verdicts") if isinstance(known, dict) else None) or {}
            newly = [(w, v) for w, v in removed_now if known.get(w) != v]
            chk.update(removed=removed_now, changed=judged["changed"], suspicious=judged["suspicious"])
            if not judged["suspicious"]:
                self.verdicts = [(w, workshop_verdict(details[w])) for w in listed if w in details]
            self.report["check"] = chk
            for wid in judged["changed"]:
                if wid in details:
                    self.state.remember(details[wid], self.day, "changed")
                    self.event(wid, "changed", "It changed on the Workshop: it is tested again.")
            for wid, verdict in newly:         # one event when a map goes, not one at every check
                self.event(wid, "removed", "The Workshop says it is %s: it is blocked." % ("gone" if verdict == "removed" else verdict))
            for wid in queue_head:
                if wid in details:
                    self.state.remember(details[wid], self.day, (self.state.item(wid) or {}).get("why") or "new")
            self.state.save()
            log("check: %d asked, %d gone (%d newly), %d changed%s" % (
                len(ids), len(judged["removed"]), len(newly), len(judged["changed"]),
                " - NOT BELIEVED (over 10%)" if judged["suspicious"] else ""))
            if api.stop_reason:
                self.report["api_stop"] = api.stop_reason
            # 2b. the ratings: the Workshop's score of every listed map and of every map waiting for its test, once a day
            self.phase("ratings")
            self.ratings(api, listed)
            if api.stop_reason:
                self.report["api_stop"] = api.stop_reason
            self.state.data["last_api"] = self.clock.now()
            self.state.save()
        stopped = api.stop_reason or (self.ledger.stopped(self.day) if self.ledger.data else None)
        if stopped and re.search(r"HTTP (?:429|403|401)", stopped):   # Steam said stop: no downloads either, today
            DownloadBudget(self.state, self.day).refuse_for_today(
                "the Web API answered %s" % short_stop(stopped))
        # 2c. THE HAND-OVER: the keep set's maps the game server does not store, downloaded and handed over, before any test
        if prefetch_done:
            self.phase("serve")
            self.serve()
        # 3.-5. downloads, tests and the publish, in batches: a long run publishes as it goes, and staging stays small
        if not prefetch_done:
            self.report["downloads"] = {"skipped": "the watcher's prefetch was still running"}
            self.report["tests"] = {"skipped": "the watcher's prefetch was still running"}
        first = True
        while True:
            t_batch = self.clock.now()
            outcomes, got = [], 0
            if prefetch_done:
                self.phase("downloads")
                got = self.download(known, details)
                self.phase("tests")
                outcomes = self.scan_and_test()
                self.worked += self.clock.now() - t_batch
                self.decided += len(outcomes)
            # this batch's verdicts and any earlier one that could not be published then
            self.phase("publish")
            decided = set(w for w, _k, _d, _r in outcomes)
            for wid, rec in self.state.data["items"].items():
                if wid not in decided and rec.get("verdict") and rec.get("status") == "tested":
                    outcomes.append((wid, rec["verdict"], rec.get("detail") or {}, rec))
            before = self.report.get("published")
            self.publish(files, outcomes, removed_now if first else [], details)
            self.report["published"] = merge_published(before, self.report.get("published"))
            self.verdicts, first = [], False   # the check's verdicts go to the bridge once a run
            self.cleanup_staging()
            dl, t = self.report.get("downloads") or {}, self.report.get("tests") or {}
            if not (prefetch_done and got) or dl.get("stop") or t.get("stop") or self.left() < 300:
                break
            try:
                known = known_ids(bridge_files(self.cfg), self.cfg)
            except (OSError, ValueError):
                pass                           # the publish step says so; the known ids of the last batch still hold
        self.finish()
        return self.report

    def queue(self, known, details):
        """The download queue, best first: items created in the last 14 days (newest first), then listed maps changed
        since we last saw them, then everything else we have seen (most subscribed first)."""
        items = self.state.data["items"]
        only = getattr(self.cfg, "only_ids", None)
        now = self.clock.now()
        fresh, changed, backlog = [], [], []
        for wid, rec in items.items():
            if rec.get("status") not in ("queued", "retry"):
                continue
            if only is not None and wid not in only:
                continue
            if wid in known and rec.get("why") != "changed":
                rec["status"] = "known"
                continue
            if rec.get("ws") != "ok" or rec.get("app") not in (0, 730):
                continue
            if rec.get("creator") and rec["creator"] in self.cfg.exclude_creators:
                rec["status"] = "skipped"
                rec["evidence"] = "listed by hand (its author is in SYNC_EXCLUDE_CREATORS)"
                continue
            if rec.get("filename"):
                rec["status"] = "skipped"
                rec["evidence"] = "a CS:GO-era upload (its Workshop file is %s): no CS2 map, not downloaded" % rec["filename"][:60]
                continue
            if not set(rec.get("tags") or []) & set(self.cfg.tags):
                continue
            if rec.get("why") == "changed":
                changed.append(wid)
            elif now - int(rec.get("created") or 0) < 14 * DAY:
                fresh.append(wid)
            else:
                backlog.append(wid)
        fresh.sort(key=lambda w: -int(items[w].get("created") or 0))
        changed.sort(key=lambda w: popularity_key(dict(items[w], id=w)))
        ordered = fresh + changed + order_backlog(items, backlog)
        # the owner's rule of 2026-11-03: a waiting map whose Workshop score would put it among its mode's best (the keep set's top
        # lists) goes first, best score first, so the maps that will stay on the server are tested before the rest
        scores = (self.state.data.get("ratings") or {}).get("scores") or {}
        cut = self.top_cutoffs()
        def best(wid):
            v = scores.get(wid)
            s = v[0] if isinstance(v, list) and v else v
            n = v[3] if isinstance(v, list) and len(v) > 3 else None
            if not isinstance(s, (int, float)):
                return None
            tags = items[wid].get("tags") or []
            for tag, c in cut.items():
                if tag in tags and ((c["score"] is None or s >= c["score"]) or (isinstance(n, int) and (c["subscriptions"] is None
                                                                                                     or n >= c["subscriptions"])) or wid in c["popular"]):
                    return s
            return None
        self.q_fresh, self.q_changed = set(fresh), set(changed)
        place = dict((w, i) for i, w in enumerate(ordered))
        top = sorted((w for w in ordered if best(w) is not None), key=lambda w: (-best(w), place[w]))
        self.q_top = set(top)
        if top:
            first = set(top)
            ordered = top + [w for w in ordered if w not in first]
            self.top_first = len(top)
        # the service's caps: a manual run's lowered one would make the mail promise 877 days (a test run of 6 maps did)
        self.backlog = backlog_stats(items, ordered, Config() if self.manual else self.cfg, self.pace())
        return ordered

    def top_cutoffs(self):
        """{Workshop tag: {"score", "subscriptions": what a map needs to be among its mode's KEEP_TOP by that list (None: the list is
        shorter, anything does), "popular": the ids of Most Popular's order up to its mode's KEEP_TOP-th listed map}}, from the keep
        set of the bridge's files as they are; {} without ratings. A waiting map that would make any of the three goes first."""
        if getattr(self, "_cut", None) is None or self.clock.now() - self._cut[0] > 600:
            inputs = keep_inputs(self.cfg.bridge_data)
            keep = safe_keep_set(inputs, self.clock.now())
            trend = (inputs.get("ratings.json") or {}).get("trend") if isinstance(inputs.get("ratings.json"), dict) else {}
            cut = {}
            if keepset and keep["scored"]:
                for mode, tag, _gt, _gm, _f in MODES:
                    order = [str(x) for x in (trend or {}).get(mode) or []]
                    last = (keep["lists"].get(mode) or {}).get("popular") or []
                    upto = order.index(last[-1]) + 1 if len(last) >= keepset.KEEP_TOP and last[-1] in order else len(order)
                    cut[tag] = dict(keep["cutoff"].get(mode) or {"score": None, "subscriptions": None}, popular=set(order[:upto]))
            self._cut = (self.clock.now(), cut)
        return self._cut[1]

    def ratings(self, api, listed):
        """2b. Once every RATINGS_EVERY - and at once when the ratings kept lack a list the keep set needs - the three lists the keep
        set takes each mode's best from: the Workshop's score (vote_data.score) and the current subscriptions of every listed map,
        every mode, and of every map still waiting for its test (IPublishedFileService/GetDetails with includevotes, 100 a call); and
        each mode's Most Popular order, Steam's trend over the last TREND_DAYS days (QueryFiles RankedByTrend by the mode's tag, pages of
        100 until TREND_WANT of the mode's listed maps were in it, at most TREND_PAGES). Kept in the state and written to the bridge's
        data as ratings.json. A run cut short merges what it got into what was there and keeps the old time: the keep set refuses
        deletions on ratings over 3 days old, so their age must stay honest."""
        st = self.state.data.setdefault("ratings", {"fetched": 0, "scores": {}})
        rep = self.report.setdefault("ratings", {})
        if not self.cfg.ratings:
            rep["skipped"] = "SYNC_RATINGS is off"
            return
        age = self.clock.now() - float(st.get("fetched") or 0)
        sample = list((st.get("scores") or {}).values())[:50]
        whole = bool(sample) and all(isinstance(v, list) and len(v) > 3 for v in sample) and all(
            isinstance((st.get("trend") or {}).get(mode), list) for mode, _t, _g, _m, _f in MODES)
        if whole and age < RATINGS_EVERY:
            rep["skipped"] = "fetched %s ago, again after %s" % (hms(age), hms(RATINGS_EVERY))
            if not os.path.exists(os.path.join(self.cfg.bridge_data, "ratings.json")):
                self.write_ratings(st)
            return
        waiting = [w for w, rec in self.state.data["items"].items() if rec.get("status") in ("queued", "retry") and rec.get("ws") == "ok"
                   and set(rec.get("tags") or []) & set(self.cfg.tags)]
        ids = list(dict.fromkeys(listed + waiting))
        inputs = keep_inputs(self.cfg.bridge_data)
        members = {}
        for mode, name in MODE_LISTS:
            members[mode] = set(str(e.get("id")) for e in inputs.get(name) or [] if isinstance(e, dict) and str(e.get("id") or "").isdigit())
        got, trend, failed, walked = {}, {}, 0, {}
        try:
            for start in range(0, len(ids), DETAILS_BATCH):
                if self.clock.now() >= self.deadline - 1200:
                    rep["stop"] = "the run's deadline"
                    break
                try:
                    got.update(api.votes(ids[start:start + DETAILS_BATCH]))
                except ApiError as error:
                    failed += 1
                    log("scores of %d ids failed for good (%s)" % (len(ids[start:start + DETAILS_BATCH]), error))
            for mode, tag, _gt, _gm, _f in MODES:
                if rep.get("stop"):
                    break
                order, cursor, hits = [], "*", 0
                for page in range(TREND_PAGES):
                    try:
                        page_ids, cursor_next, _total = api.trend(tag, cursor, TREND_DAYS)
                    except ApiError as error:
                        failed += 1
                        log("Most Popular of %s, page %d, failed for good (%s)" % (mode, page + 1, error))
                        break
                    order += [w for w in page_ids if w not in order]
                    hits = sum(1 for w in order if w in members[mode])
                    if hits >= TREND_WANT or not page_ids or not cursor_next or cursor_next == cursor:
                        break
                    cursor = cursor_next
                trend[mode] = order
                walked[mode] = (len(order), hits)
        except StopApi as stop:
            rep["stop"] = str(stop)
            log("ratings stopped: %s" % stop)
        complete = not rep.get("stop") and not failed and len(trend) == len(MODES)
        if complete:
            st["scores"], st["trend"], st["fetched"], st["trend_days"] = got, trend, self.clock.now(), TREND_DAYS
        else:
            if got:
                st["scores"] = dict(st.get("scores") or {}, **got)
            if trend:
                st["trend"] = dict(st.get("trend") or {}, **trend)
        rep.update(asked=len(ids), listed=len(listed), waiting=len(waiting), scored=len(got), failed=failed, complete=complete,
                   calls=api.by_api.get("GetDetails", 0), trend=dict((m, list(v)) for m, v in walked.items()), trend_days=TREND_DAYS)
        log("ratings: %d of %d scored with their subscriptions (%d listed, %d waiting) in %d call(s); Most Popular over %d days: %s%s" % (
            len(got), len(ids), len(listed), len(waiting), rep["calls"], TREND_DAYS,
            ", ".join("%s %d read, %d listed" % (m, v[0], v[1]) for m, v in walked.items()) or "none",
            "" if complete else " - INCOMPLETE, the old time kept"))
        self.state.save()
        self._cut = None
        if got or trend:
            self.write_ratings(st)

    def write_ratings(self, st):
        """/data/ratings.json, version 1: {"fetched": unix time, "scores": {id: [score, votes up, votes down, subscriptions]}, "trend":
        {mode: [ids in Most Popular order]}, "trend_days": days}."""
        if not self.cfg.publish:
            self.report.setdefault("ratings", {})["file"] = "not written (SYNC_PUBLISH is off)"
            return
        try:
            write_atomic(os.path.join(self.cfg.bridge_data, "ratings.json"),
                         json.dumps({"version": 1, "fetched": st.get("fetched") or 0, "source": "IPublishedFileService/GetDetails vote_data "
                                     "and subscriptions; QueryFiles RankedByTrend", "trend_days": st.get("trend_days") or TREND_DAYS,
                                     "scores": st.get("scores") or {}, "trend": st.get("trend") or {}}, separators=(",", ":")) + "\n")
            self.report.setdefault("ratings", {})["file"] = "ratings.json written"
        except OSError as error:
            self.report["errors"].append("ratings.json not written: %s" % error)

    def requeue_size_skips(self):
        """The owner's 33 and 36 c (2026-11-03): a map skipped as bigger than a whole day's download cap is queued again - the 223
        popular maps the old caps parked (800 MB, then 3 GB a night) - and nothing is skipped for its size any more: there is no
        daily cap. They are tested like the rest, by popularity, the ones the keep set wants first."""
        back = []
        for wid, rec in self.state.data["items"].items():
            if rec.get("status") != "skipped" or "download cap" not in (rec.get("evidence") or ""):
                continue
            rec["status"] = "queued"
            rec["requeued"] = "%s: no daily download cap any more (%s)" % (self.day, rec.get("evidence") or "")
            rec.pop("skip_cap", None)
            back.append(wid)
        if back:
            self.state.save()
            log("queued again: %d map(s) skipped as too big for an old daily download cap - there is none any more" % len(back))
        self.report["requeued"] = len(back)

    def serve(self):
        """THE HAND-OVER, for what is missing: the keep set's maps the game server does not store - the upvoted maps, each mode's
        best-rated, the own maps - downloaded with SteamCMD (anonymous, with every push-back stop and the brake of the downloads) and
        handed over at once; never what waits in the hand-over folder, nor the random queues and the requests (the server fetches
        those itself, update-watch.py job 9), nor an excluded id. They were tested when they were listed: no test again."""
        cfg = self.cfg
        rep = self.report.setdefault("serve", {"ok": 0, "bytes": 0, "failed": []})
        if not cfg.handover:
            rep["skipped"] = "SYNC_HANDOVER is off"
            return
        keep = safe_keep_set(keep_inputs(cfg.bridge_data), self.clock.now())
        broken_keep = [p for p in keep["problems"] if p.startswith("the keep set could not be computed")]
        if broken_keep:
            rep["skipped"] = broken_keep[0]
            self.report["errors"].append("serve: %s - nothing handed over" % broken_keep[0])
            return
        has, pending = server_maps(cfg.game_dir), handovers(cfg.handover_dir)
        want = keep["up"] + [w for mode, _f in MODE_LISTS for w in keep["top"].get(mode) or []] + keep["own"]
        want = [w for w in dict.fromkeys(want) if w not in has and w not in pending and w not in cfg.exclude
                and (getattr(cfg, "only_ids", None) is None or w in cfg.only_ids)]
        rep.update(missing=len(want), stored=len(has), pending=len(pending))
        if not has:
            rep["skipped"] = "the game server's acf is not readable here - nothing fetched"
            return
        if not want:
            log("serve: the game server stores every map of the keep set (%d pending hand-over(s))" % len(pending))
            return
        if sum(pending.values()) >= HANDOVER_MAX_BYTES:
            rep["stop"] = "%s of hand-overs wait for the server already" % mb(sum(pending.values()))
            return
        sizes = {}
        for e in list(self.state.data["items"].items()):
            sizes[e[0]] = int(e[1].get("size") or 0)
        for _mode, name in MODE_LISTS:
            for entry in read_json(os.path.join(cfg.bridge_data, name), []) or []:
                if isinstance(entry, dict) and str(entry.get("id") or "").isdigit():
                    sizes.setdefault(str(entry["id"]), int(entry.get("sizeMb") or 0) << 20)
        staging = os.path.join(cfg.work_dir, "staging")
        acf = os.path.join(staging, "steamapps", "workshop", "appworkshop_730.acf")
        try:
            rep["tidied"] = tidy_staging(staging, cfg.handover_dir, self.state.data["items"])
        except (OSError, ValueError) as error:
            log("staging: not tidied (%s)" % error)
        handed, later = [], []
        # what is staged already - an earlier run's download it could not hand over (killed, or it looked before SteamCMD had
        # written its acf: 136 of 160 on 2026-11-03) - is handed over as it is, without a download, unless the Workshop has a newer one
        for wid in [w for w in want if self.staged(w)]:
            rec = self.state.item(wid) or {}
            entry = acf_entries(acf, wid)
            if not entry or not str(entry[0].get("timeupdated") or "").isdigit() or int(entry[0]["timeupdated"]) < int(rec.get("updated") or 0):
                continue                          # no entry in SteamCMD's acf, or an older version: downloaded again below
            nbytes = folder_bytes(os.path.join(staging, "steamapps", "workshop", "content", "730", wid))
            try:
                done, words = hand_over(staging, cfg.handover_dir, wid, rec.get("title") or "", cfg.depotcache)
            except OSError as error:
                done, words = False, "the hand-over failed (%s)" % error
            log("serve: %s %s was staged already, %s" % (wid, (rec.get("title") or "")[:40], words))
            if done:
                rep["ok"] += 1
                rep["bytes"] += nbytes
                rep["staged"] = rep.get("staged", 0) + 1
                handed.append(wid)
                self.event(wid, "handed-over", "Handed over to the game server from an earlier download (%s)." % mb(nbytes))
        want = [w for w in want if w not in handed and w not in set(x for x, _n in self.stalls)]   # a map that stalled waits for the next run
        log("serve: %d map(s) of the keep set the game server does not store - downloading them for it%s" % (
            len(want), " (%d handed over from the staging folder first)" % len(handed) if handed else ""))
        budget = DownloadBudget(self.state, self.day)
        steam = SteamCmd(cfg.steamcmd, staging, os.path.join(cfg.work_dir, "steamcmd.log"), home=os.environ.get("HOME"))
        started, row, denied = False, 0, 0
        try:
            for step, wid in enumerate(want):
                ok, why = budget.allows(wid)
                if not ok:
                    if "no more downloads" in why:
                        rep["stop"] = why
                        break
                    log("serve: %s: %s" % (wid, why))
                    continue
                self.on_map(wid, step, len(want))
                if self.left() < 900:
                    rep["stop"] = "the run's deadline"
                    break
                t_wait = self.clock.now()
                if not self.wait_for_nobody("serve", margin=900):
                    rep["stop"] = "somebody was on the server until the deadline"
                    break
                if started and self.clock.now() - t_wait > 300:
                    steam.stop()
                    started = False
                if not started:
                    started, why = steam.start()
                    if not started:
                        rep["stop"] = "SteamCMD did not log in anonymously: %s" % why
                        if STEAMCMD_STOP_RX.search(why):
                            budget.refuse_for_today("SteamCMD answered '%s'" % why)
                        break
                t = time.time()
                good, reason, nbytes = steam.download(wid)
                if good and self.staged(wid):
                    on_disk = folder_bytes(os.path.join(cfg.work_dir, "staging", "steamapps", "workshop", "content", "730", wid))
                    budget.spend(max(nbytes, on_disk), wid)
                    rep["downloads"] = rep.get("downloads", 0) + 1
                    rep["dl_bytes"] = rep.get("dl_bytes", 0) + max(nbytes, on_disk)
                    row, denied = 0, 0
                    rec = self.state.item(wid) or {}
                    # SteamCMD names the item in its acf 1.0-1.4 s after it says Success (measured 2026-11-03; the deployed code looked
                    # at once and missed 136 of 160): wait for its entry of this version, else hand it over once SteamCMD has quit
                    fresh = False
                    for _ in range(ACF_WAIT):
                        entry = acf_entries(acf, wid)
                        if entry and all(str(entry[0].get(k) or "").isdigit() for k in ("size", "timeupdated", "manifest")) \
                                and int(entry[0]["timeupdated"]) >= int(rec.get("updated") or 0):
                            fresh = True
                            break
                        time.sleep(1)
                    if not fresh:
                        done, words = False, "SteamCMD's acf does not name this version yet - handed over once it has quit"
                    else:
                        try:
                            done, words = hand_over(staging, cfg.handover_dir, wid, rec.get("title") or "", cfg.depotcache)
                        except OSError as error:
                            done, words = False, "the hand-over failed (%s)" % error
                    log("serve: %s %s downloaded in %.0f s, %s" % (wid, (rec.get("title") or "")[:40], time.time() - t, words))
                    if done:
                        rep["ok"] += 1
                        rep["bytes"] += max(nbytes, on_disk)
                        handed.append(wid)
                        self.event(wid, "handed-over", "Downloaded and handed over to the game server (%s)." % mb(max(nbytes, on_disk)))
                    else:
                        later.append((wid, rec, max(nbytes, on_disk)))   # once SteamCMD has quit, its acf is complete
                else:
                    rep["failed"].append((wid, reason))
                    row += 1
                    budget.fail()
                    self.event(wid, "failed", "Its download failed (%s)." % reason)
                    log("serve: the download of %s failed: %s" % (wid, reason))
                    if ACCESS_DENIED_RX.search(reason):
                        denied += 1
                    if STEAMCMD_STOP_RX.search(reason) or denied >= 2:
                        rep["stop"] = "SteamCMD answered '%s'%s - no more downloads today" % (reason, " twice in a row" if denied >= 2 else "")
                        budget.refuse_for_today("SteamCMD answered '%s'" % reason)
                        break
                    if row >= 3:
                        rep["stop"] = "3 downloads in a row failed"
                        break
                    if reason.startswith(STALL):
                        if self.stalled(wid, reason, budget):
                            rep["stop"] = "SteamCMD stalled twice in this run (%s) - no more downloads in it" % reason
                            break
                        steam.stop()             # a fresh session for the next map: a stall alone is routine (the owner's 86 a)
                        started = False
                    elif "not running" in reason or "stdin" in reason:
                        rep["stop"] = "SteamCMD stopped answering (%s)" % reason
                        break
                self.state.save()
                time.sleep(cfg.download_pause)
        finally:
            steam.stop()
            self.state.save()
        for wid, rec, nbytes in later:            # SteamCMD has quit: its acf names them now
            try:
                done, words = hand_over(staging, cfg.handover_dir, wid, rec.get("title") or "", cfg.depotcache)
            except OSError as error:
                done, words = False, "the hand-over failed (%s)" % error
            log("serve: %s after SteamCMD quit: %s%s" % (wid, words, "" if done else " - it stays staged for the next run"))
            if not done:
                rep.setdefault("handover_failed", []).append((wid, words))
            if done:
                rep["ok"] += 1
                rep["bytes"] += nbytes
                handed.append(wid)
                self.event(wid, "handed-over", "Downloaded and handed over to the game server (%s)." % mb(nbytes))
            else:
                rep["failed"].append((wid, words))
        try:                                      # SteamCMD writes its acf from memory: it named the early ones again
            acf_forget(os.path.join(staging, "steamapps", "workshop", "appworkshop_730.acf"), handed)
        except (OSError, ValueError):
            pass
        waiting = handovers(cfg.handover_dir)
        rep["left"] = len([w for w in want if w not in waiting])
        self.limits(budget)
        log("serve: %d handed over (%s), %d failed, %d still missing%s" % (rep["ok"], mb(rep["bytes"]), len(rep["failed"]), rep["left"],
                                                                            " - stopped: %s" % rep["stop"] if rep.get("stop") else ""))

    def stalled(self, wid, reason, budget):
        """A SteamCMD stall on `wid`: booked (the run's list, the day's count of the map), logged. True when it is the run's second -
        the downloads stop then; one alone is routine (the owner's 86 a), and the mail speaks of stalls only from two in a run, or
        two of one map in a day (issues_of)."""
        self.stalls.append((str(wid), budget.stall(wid)))
        self.report["stalls"] = list(self.stalls)
        log("SteamCMD stalled on %s (%s), the run's stall %d%s" % (wid, reason, len(self.stalls),
                                                                  " - no more downloads in this run" if len(self.stalls) >= 2
                                                                  else " - a fresh session for the next map"))
        return len(self.stalls) >= 2

    def pace(self):
        """Seconds a map took this run, download and test together; None until enough were decided to say."""
        return self.worked / self.decided if self.decided >= 20 else None

    def staged(self, wid):
        return os.path.isdir(os.path.join(self.cfg.work_dir, "staging", "steamapps", "workshop", "content", "730", wid))

    def download(self, known, details):
        """One batch: at most SYNC_BATCH downloads on one anonymous SteamCMD login. Returns how many it downloaded."""
        cfg = self.cfg
        budget = DownloadBudget(self.state, self.day)
        dl = self.report.setdefault("downloads", {"ok": 0, "bytes": 0, "failed": [], "braked": []})
        try:
            tidy_staging(os.path.join(cfg.work_dir, "staging"), cfg.handover_dir, self.state.data["items"])
        except (OSError, ValueError) as error:
            log("staging: not tidied (%s)" % error)
        stalled = set(x for x, _n in self.stalls)                  # a map that stalled in this run waits for the next one
        todo = [w for w in self.queue(known, details) if not self.staged(w) and w not in stalled]
        if not todo:
            dl["queued_left"] = 0
            self.limits(budget)
            return 0
        ok, why = budget.allows()
        if not ok:
            dl["stop"] = why
            dl["queued_left"] = len(todo)
            self.limits(budget)
            return 0
        steam = SteamCmd(cfg.steamcmd, os.path.join(cfg.work_dir, "staging"), os.path.join(cfg.work_dir, "steamcmd.log"),
                         home=os.environ.get("HOME"))
        started = False
        row, denied, got, done = 0, 0, 0, set()
        try:
            for wid in todo:
                if got >= cfg.batch:
                    break
                self.on_map(wid, got, min(len(todo), cfg.batch))
                rec = self.state.item(wid)
                ok, why = budget.allows(wid)
                if not ok:
                    if "no more downloads" in why:
                        dl["stop"] = why
                        break
                    dl["braked"].append(wid)       # downloaded twice today already: a bug somewhere, not again today
                    continue
                if self.left() < 600 + 25 * (got + 1):
                    dl["stop"] = "the run's deadline (time is kept for this batch's tests)"
                    break
                t_wait = self.clock.now()
                if not self.wait_for_nobody("downloads", margin=600):
                    dl["stop"] = "somebody was on the server until the deadline"
                    break
                if started and self.clock.now() - t_wait > 300:
                    steam.stop()                   # a long wait: a fresh login rather than a session gone stale
                    started = False
                if not started:
                    started, why = steam.start()
                    if not started:
                        dl["stop"] = "SteamCMD did not log in anonymously: %s" % why
                        self.report["errors"].append(dl["stop"])
                        if STEAMCMD_STOP_RX.search(why):
                            budget.refuse_for_today("SteamCMD answered '%s'" % why)
                        break
                    log("SteamCMD: %s" % why)
                t = time.time()
                good, reason, nbytes = steam.download(wid)
                rec["attempts"] = int(rec.get("attempts") or 0) + 1
                if good and self.staged(wid):
                    on_disk = folder_bytes(os.path.join(cfg.work_dir, "staging", "steamapps", "workshop", "content", "730", wid))
                    budget.spend(max(nbytes, on_disk), wid)
                    dl["ok"] += 1
                    got += 1
                    dl["bytes"] += max(nbytes, on_disk)
                    rec["status"] = "downloaded"
                    rec["downloaded"] = self.day
                    rec["loads"] = [] if rec.get("why") == "changed" else rec.get("loads") or []
                    for stale in ("scan", "verdict", "decided", "detail"):
                        rec.pop(stale, None)
                    row, denied = 0, 0
                    log("downloaded %s %s (%s) in %.0f s" % (wid, rec.get("title", "")[:40], mb(max(nbytes, on_disk)), time.time() - t))
                else:
                    dl["failed"].append((wid, reason))
                    row += 1
                    budget.fail()
                    self.event(wid, "failed", "Its download failed (%s), %d of 3 tries." % (reason, rec["attempts"]))
                    rec["status"] = "retry" if rec["attempts"] < 3 else "failed"
                    rec["evidence"] = "SteamCMD could not download it (%s), %d of 3 tries" % (reason, rec["attempts"])
                    log("download of %s failed: %s" % (wid, reason))
                    if ACCESS_DENIED_RX.search(reason):
                        rec["status"] = "failed"   # this item, for now; Steam will not hand it out
                        denied += 1
                    if STEAMCMD_STOP_RX.search(reason) or denied >= 2:
                        dl["stop"] = "SteamCMD answered '%s'%s - no more downloads today" % (reason, " twice in a row" if denied >= 2 else "")
                        budget.refuse_for_today("SteamCMD answered '%s'" % reason)
                        break
                    if row >= 3:
                        dl["stop"] = "3 downloads in a row failed - no more in this run"
                        break
                    if reason.startswith(STALL):
                        if self.stalled(wid, reason, budget):
                            dl["stop"] = "SteamCMD stalled twice in this run (%s) - no more downloads in it" % reason
                            break
                        steam.stop()             # a fresh session for the next map: a stall alone is routine (the owner's 86 a)
                        started = False
                    elif "not running" in reason or "stdin" in reason:
                        dl["stop"] = "SteamCMD stopped answering (%s)" % reason
                        break
                done.add(wid)
                self.state.save()
                time.sleep(cfg.download_pause)
        finally:
            steam.stop()
            self.state.save()
        dl["queued_left"] = len([w for w in todo if w not in done])
        self.limits(budget)
        return got

    def limits(self, budget=None):
        budget = budget or DownloadBudget(self.state, self.day)
        self.report["limits"] = {
            "api_run": self.api.calls if getattr(self, "api", None) is not None else 0, "api_run_cap": self.cfg.api_run_cap,
            "api_24h": self.ledger.last_24h() if self.ledger.data else None, "api_day_cap": self.ledger.day_cap,
            "api_gap": self.cfg.api_gap, "dl_items": budget.today["items"],
            "dl_bytes": budget.today["bytes"], "dl_engine": budget.today.get("engine", 0),
            "engine_starts": self.engine.starts if self.engine else 0, "engine_starts_max": self.cfg.engine_starts_max}

    def scan_and_test(self):
        """Scan every staged item that has no verdict, then load the maps among them on the test engine.
        Returns [(id, kind, detail, record)] of the items this batch decided."""
        cfg = self.cfg
        t = self.report.setdefault("tests", {"rows": []})
        content = os.path.join(cfg.work_dir, "staging", "steamapps", "workshop", "content", "730")
        staged = sorted(os.listdir(content)) if os.path.isdir(content) else []
        only = getattr(cfg, "only_ids", None)          # a manual run limited to some maps tests only those
        work = []
        for wid in staged:
            if only is not None and wid not in only:
                continue
            rec = self.state.item(wid)
            if rec is None or rec.get("status") not in ("downloaded", "tested"):
                continue
            if "scan" not in rec:
                try:
                    rec["scan"] = scan_item(os.path.join(content, wid))
                except Exception as error:  # a malformed upload must not end the run
                    rec["scan"] = {"verdict": "unknown", "names": [], "error": repr(error)[:200]}
            work.append(wid)
        self.state.save()
        outcomes = []
        to_load = []
        for wid in work:
            rec = self.state.item(wid)
            kind, detail = final_verdict(rec, self.day)
            if kind == "nonmap":
                outcomes.append((wid, kind, detail, rec))
            elif kind == "pending":
                to_load.append(wid)
            else:
                outcomes.append((wid, kind, detail, rec))
        if to_load:
            version = patch_version(cfg.game_dir)
            if not self.wait_for_nobody("tests", margin=600):
                t["stop"] = "somebody was on the server until the deadline"
            else:
                stale = kill_stale_engines(os.path.join(cfg.work_dir, "farm"))
                if stale:
                    log("killed %d test engine(s) left running from an earlier run" % stale)
                try:
                    log(build_farm(cfg.game_dir, cfg.work_dir, cfg.cfg_dir))
                except OSError as error:
                    t["stop"] = "the test tree could not be built: %s" % error
                    self.report["errors"].append(t["stop"])
                    to_load = []
                self.engine = TestEngine(cfg, cfg.work_dir, os.environ.get("HOME") or "/home/steam")
                self.engine.starts = self.engine_starts
                budget = DownloadBudget(self.state, self.day)
                need_start = True
                try:
                    for step, wid in enumerate(to_load):
                        rec = self.state.item(wid)
                        self.on_map(wid, step, len(to_load))
                        while True:
                            kind, detail = final_verdict(rec, self.day)
                            if kind != "pending":
                                break
                            if self.left() < 300:
                                t["stop"] = "the run's deadline"
                                break
                            if patch_version(cfg.game_dir) != version:
                                t["stop"] = "the game was updated during the tests (steam.inf %s -> %s)" % (
                                    version, patch_version(cfg.game_dir))
                                break
                            if not self.wait_for_nobody("tests", margin=300):
                                t["stop"] = "somebody was on the server until the deadline"
                                break
                            if need_start or not self.engine.alive():
                                ok, why = self.engine.start()
                                log(why)
                                if not ok:
                                    t["stop"] = t["engine_failed"] = why
                                    break
                                need_start = False
                            levels = (rec.get("scan") or {}).get("maps") or []
                            load = test_load(self.engine, wid, levels, rec.get("tags") or [], cfg.hold,
                                             lambda: (self.humans() or 0) > 0)
                            rec.setdefault("loads", []).append(load)
                            rec["tests"] = int(rec.get("tests") or 0) + 1
                            rec["status"] = "tested"
                            if load.get("engine_download"):
                                budget.spend_bytes(load["engine_download"])
                                try:                      # what the engine loaded is its own copy: scan that one
                                    rec["scan"] = scan_item(os.path.join(content, wid))
                                except Exception as error:
                                    rec["scan"]["rescan_error"] = repr(error)[:200]
                                log("%s: the test engine fetched its own copy (%s) - scanned again" % (
                                    wid, mb(load["engine_download"])))
                            self.state.save()
                            log("%s load %d: %s - %s" % (wid, len(rec["loads"]), load["verdict"], load["reason"]))
                            if load["verdict"] in ("errormap", "crash", "freeze"):
                                need_start = True          # a fresh engine: the old one is down or stuck on the error map
                            if load["verdict"] == "abort":
                                self.engine.stop()         # somebody joined: nothing of ours runs beside them
                                need_start = True
                                break
                        if t.get("stop"):
                            break
                        kind, detail = final_verdict(rec, self.day)
                        if kind != "pending":
                            outcomes.append((wid, kind, detail, rec))
                finally:
                    self.engine.stop()
                    self.engine_starts = self.engine.starts
        for wid, kind, detail, rec in outcomes:
            rec["verdict"] = kind
            rec["decided"] = self.day
            rec["detail"] = {k: v for k, v in detail.items() if k in ("kind", "detail", "evidence", "reason", "level", "mode",
                                                                      "crashes", "sample")}
            title = (rec.get("title") or "")[:48]
            if kind == "pass":
                t["rows"].append("PASS     %s (%s) - %s, loads as %s in %ss" % (title, wid, detail.get("mode"), detail.get("level"),
                                                                              detail.get("load_s")))
            elif kind == "nonmap":
                t["rows"].append("NON-MAP  %s (%s) - %s" % (title, wid, detail.get("detail", "")[:110]))
            elif kind == "advert":
                t["rows"].append("ADVERT   %s (%s) - %s: %s" % (title, wid, detail.get("reason"), (detail.get("sample") or "")[:80]))
            else:
                t["rows"].append("%-8s %s (%s) - %s" % (kind.upper(), title, wid, detail.get("evidence", "")[:110]))
        pending = [w for w in work if (self.state.item(w) or {}).get("verdict") is None]
        if pending:
            log("waiting for the next batch or run: %d staged item(s) without a verdict" % len(pending))
            t["waiting_verdict"] = len(pending)
        self.limits()
        self.state.save()
        return outcomes

    def publish(self, files, outcomes, removed_now, details):
        cfg = self.cfg
        pub = {}
        self.report["published"] = pub
        try:
            files = bridge_files(cfg)           # read again: anything may have changed during the run
        except (OSError, ValueError) as error:
            pub["skipped"] = "the bridge's data cannot be read (%s)" % error
            return
        texts, summary = plan_publish(files, self.state, self.day, outcomes, removed_now, cfg.mode_backfill, details)
        pub.update(summary)
        verdicts = self.verdicts
        if not texts and not verdicts:
            self.mark_published(outcomes, files)
            return
        if not cfg.publish:
            pub["skipped"] = "SYNC_PUBLISH is off (would write %s%s)" % (", ".join(sorted(texts)) or "no file",
                                                                       ", and report %d verdicts" % len(verdicts) if verdicts else "")
            return
        if not self.wait_for_nobody("publish"):
            pub["skipped"] = "somebody was on the server until the deadline - the results wait for the next run"
            return
        if verdicts:
            pub["verdicts"] = self.report_verdicts(verdicts)
            log("Workshop verdicts of %d listed map(s) to the bridge: %s" % (len(verdicts), pub["verdicts"]))
        if not texts:
            self.mark_published(outcomes, files)
            return
        # the live files once more, right before writing: what is planned must be them plus additions, nothing else
        try:
            live = bridge_files(cfg)
        except (OSError, ValueError) as error:
            pub["skipped"] = "the bridge's data cannot be read (%s)" % error
            return
        if any(live["raw"].get(n) != files["raw"].get(n) for n in set(live["raw"]) | set(files["raw"])):
            texts, summary = plan_publish(live, self.state, self.day, outcomes, removed_now, cfg.mode_backfill, details)
            pub.update(summary)
            files = live
            log("publish: the bridge's data changed while it was planned - planned again on the live files")
        problems = [p for n in texts for p in merge_problems(n, files["raw"].get(n), texts[n])]
        if problems:
            pub["skipped"] = "NOT written - the result would not be the live files plus additions: %s" % "; ".join(problems)
            self.report["errors"].append(pub["skipped"])
            return
        stamp = time.strftime("%Y%m%d-%H%M%S")
        backup = os.path.join(cfg.state_dir, "backups", stamp)
        os.makedirs(backup, exist_ok=True)
        for name, raw in files["raw"].items():
            with open(os.path.join(backup, name), "wb") as handle:
                handle.write(raw)
        written = []

        def write_one(name):
            path = os.path.join(cfg.bridge_data, name)
            now_raw = None
            if os.path.exists(path):
                with open(path, "rb") as handle:
                    now_raw = handle.read()
            if now_raw != files["raw"].get(name):
                self.report["errors"].append("%s changed on the share a moment before it was written - left alone" % name)
                return
            write_atomic(path, texts[name])
            with open(path, "rb") as handle:
                back = handle.read()
            if back != texts[name].encode("utf-8"):
                self.report["errors"].append("%s does not read back as written" % name)
            elif merge_problems(name, files["raw"].get(name), back.decode("utf-8")):
                self.report["errors"].append("%s: the written file is not the old one plus additions" % name)
            written.append(name)
        for name in ("broken.json", "maps.json", "maps-deathmatch.json", "maps-wingman.json"):
            if name in texts:
                write_one(name)
        pub["reload"] = self.reload()
        # search.json AFTER the reload: POST /reload does not read search.json yet marks it as read (Reload.cs, ReloadAll,
        # 2026-11-01), so written before it the new texts would wait for the next change; written now, the bridge's own
        # watch reads it about 10 s later
        if "search.json" in texts:
            write_one("search.json")
        pub["files"] = written
        pub["backup"] = backup
        log("published %s; bridge: %s" % (", ".join(written), pub["reload"]))
        self.mark_published(outcomes, bridge_files(cfg))

    def report_verdicts(self, verdicts):
        """This run's check of the listed maps, handed to the bridge (POST /removed/<secret>, Workshop.cs) - the only Workshop check
        it hears (the owner's 74 a): the bridge keeps them in /data/removed.json, blocks a map that is gone at once (and unblocks
        one that came back), and applies its own rule - more than 10% of 50+ gone is not believed. A report that does not arrive
        is a problem for the mail: nothing else tells the bridge; the next check reports again."""
        if not (self.cfg.bridge_url and self.cfg.bridge_secret):
            return "not reported (no SYNC_BRIDGE_URL / SYNC_BRIDGE_SECRET) - nothing else tells the bridge"
        body = "gg-removed\t1\n" + "".join("%s\t%s\n" % (wid, v) for wid, v in verdicts)
        try:
            request = urllib.request.Request("%s/removed/%s" % (self.cfg.bridge_url, self.cfg.bridge_secret),
                                             data=body.encode("utf-8"), method="POST",
                                             headers={"User-Agent": USER_AGENT, "Content-Type": "text/plain; charset=utf-8"})
            with urllib.request.urlopen(request, timeout=20) as response:
                return response.read().decode("utf-8", "replace").strip()[:300] or "HTTP %d" % response.status
        except (urllib.error.URLError, OSError) as error:
            return "the report failed (%s) - the next check reports again" % error

    def mark_published(self, outcomes, files):
        listed = {str(m.get("id")) for m in files["maps"] if isinstance(m, dict)}
        for mode in files["modes"]:
            listed |= {str(m.get("id")) for m in files["modes"][mode] if isinstance(m, dict)}
        on = broken_ids(files["broken"])
        for wid, kind, detail, rec in outcomes:
            if (wid in listed or wid in on) and rec.get("status") != "published":
                if kind == "pass" and wid in listed:
                    self.event(wid, "listed", "It passed its test: it loads as %s." % (detail.get("level") or "its own map"))
                elif wid in on:
                    self.event(wid, "broken", detail.get("evidence") or detail.get("detail") or detail.get("reason") or kind)
            if wid in listed or wid in on:
                rec["status"] = "published"
            elif kind == "pass" and rec.get("why") == "changed":
                rec["status"] = "published"
            if kind == "pass":
                self.state.data["baseline"][wid] = int(rec.get("updated") or 0)
        self.state.save()

    def reload(self):
        if not (self.cfg.bridge_url and self.cfg.bridge_secret):
            return "not asked (no SYNC_BRIDGE_URL / SYNC_BRIDGE_SECRET): it reads the files again within about 10 s"
        try:
            request = urllib.request.Request("%s/reload/%s" % (self.cfg.bridge_url, self.cfg.bridge_secret), data=b"",
                                             method="POST", headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=20) as response:
                text = response.read().decode("utf-8", "replace")
            return " | ".join(l.strip() for l in text.splitlines() if l.strip())[:600] or "HTTP %d" % response.status
        except (urllib.error.URLError, OSError) as error:
            return "the reload request failed (%s): the bridge reads the files again by itself within about 10 s" % error

    def cleanup_staging(self):
        """Forget the staged downloads that have a verdict (their folders and their acf entries); keep the rest. A pass the keep set
        holds - its score puts it among its mode's best now that it is listed, say - and the game server does not store is handed
        over instead (THE HAND-OVER), so the server never downloads it again; when that fails it stays staged, and the next run's
        serve step hands it over without a download (or downloads it again when SteamCMD's acf does not name it)."""
        root = os.path.join(self.cfg.work_dir, "staging", "steamapps", "workshop")
        content = os.path.join(root, "content", "730")
        if not os.path.isdir(content):
            return
        done = [w for w in os.listdir(content) if (self.state.item(w) or {}).get("verdict")]
        if not done:
            return
        if self.cfg.handover and self.cfg.publish:
            keep = safe_keep_set(keep_inputs(self.cfg.bridge_data), self.clock.now())     # the lists as just published
            if any(p.startswith("the keep set could not be computed") for p in keep["problems"]):
                log("staging: the keep set could not be computed - nothing removed, so no map the keep set may want is lost")
                return
            has, pending = server_maps(self.cfg.game_dir), handovers(self.cfg.handover_dir)
            rep = self.report.setdefault("serve", {"ok": 0, "bytes": 0, "failed": []})
            for wid in list(done):
                rec = self.state.item(wid) or {}
                if rec.get("verdict") != "pass" or wid not in keep["all"] or wid in has or wid in pending or wid in self.cfg.exclude:
                    continue
                try:
                    ok, why = hand_over(os.path.join(self.cfg.work_dir, "staging"), self.cfg.handover_dir, wid, rec.get("title"),
                                        self.cfg.depotcache)
                except OSError as error:
                    ok, why = False, "the hand-over failed (%s)" % error
                log("hand-over %s %s (tested, in the keep set): %s%s" % (wid, (rec.get("title") or "")[:40], why,
                                                                        "" if ok else " - it stays staged: the next run's serve hands it over"))
                if not ok:
                    rep.setdefault("handover_failed", []).append((wid, why))
                done.remove(wid)                  # handed over, or kept for the next run - never deleted while the keep set wants it
                if ok:
                    rep["tested"] = rep.get("tested", 0) + 1
                    self.event(wid, "handed-over", "Tested and handed over to the game server.")
        if not done:
            return
        acf = os.path.join(root, "appworkshop_730.acf")
        try:
            if os.path.exists(acf):
                acf_forget(acf, done)
        except (OSError, ValueError) as error:
            log("staging: the acf was not edited (%s) - the folders stay" % error)
            return
        for wid in done:
            shutil.rmtree(os.path.join(content, wid), ignore_errors=True)
        log("staging: %d decided item(s) removed" % len(done))

    def finish(self):
        try:
            self.queue({}, {})          # the backlog as it is at the end of the run, for the mail
            self.report["backlog"] = self.backlog
            if self.backlog and self.backlog.get("days"):
                until = self.clock.local(self.clock.now() + self.backlog["days"] * DAY).strftime("%Y-%m-%d")
                self.report["backlog_until"] = " (until about %s)" % until
        except Exception as error:
            log("backlog not counted: %r" % error)
        self.report["ended"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        self.report["ended_hm"] = self.clock.local().strftime("%H:%M")
        if "limits" not in self.report:
            try:
                self.limits()
            except Exception:
                pass
        if self.ledger.data is not None and self.ledger.stopped(self.day):
            self.report["api_stop"] = self.ledger.stopped(self.day)
        path = os.path.join(self.cfg.state_dir, "reports", "%s-%s%s.json" % (self.day, self.clock.local(self.t0).strftime("%H%M"),
                                                                           "-manual" if self.manual else ""))
        self.report["report_path"] = path
        subject, body = mail_text(self.report, self.cfg)
        self.report["mail_subject"] = subject
        try:
            write_atomic(path, dumps(self.report, indent=1), mode=0o644)
            write_atomic(path[:-5] + ".txt", subject + "\n\n" + body + "\n", mode=0o644)
        except OSError as error:
            log("report not written: %s" % error)
        idle = not ((self.report.get("downloads") or {}).get("ok") or (self.report.get("tests") or {}).get("rows")
                    or (self.report.get("published") or {}).get("files") or self.report.get("errors") or (self.report.get("serve") or {}).get("ok"))
        self.report["idle"] = idle
        try:                                    # the disk, and the game server's last deletion, for the problems below
            st = os.statvfs(self.cfg.game_dir)
            free = st.f_bavail * st.f_frsize
            self.report["disk"] = {"free": free}
            if free < self.cfg.disk_low:
                self.report["disk"]["low"] = "%s free on the game's volume (the mark is %s)" % (mb(free), mb(self.cfg.disk_low))
        except (OSError, AttributeError):       # no statvfs on Windows (the tests)
            pass
        wk = read_json(os.path.join(os.path.dirname(self.cfg.watcher_state), "keep.json"), {}) or {}
        if isinstance(wk, dict) and wk.get("problems") and str(wk.get("delete") or "on") != "off":
            self.report["watcher_keep"] = {"computed": str(wk.get("computed") or "?"), "problems": list(wk["problems"])[:5]}
        issues = issues_of(self.report)
        self.report["issues"] = [what for _k, what, _d in issues]
        self.report["mail"] = mail_issues(self.cfg, self.clock, issues, self.report)
        log("mail %s%s" % (self.report["mail"], "" if issues else " - this run's report: %s" % (path[:-5] + ".txt")))
        try:                                    # again, now with the mail's outcome in it
            write_atomic(path, dumps(self.report, indent=1), mode=0o644)
        except OSError:
            pass
        self.state.data["last_run"] = {"ended": self.clock.now(), "did": 0 if idle else 1}
        try:
            hist = self.state.data.setdefault("history", {})
            if not hist.get("runs"):
                hist["runs"] = seed_runs(self.cfg.state_dir)
            hist["runs"] = [e for e in hist["runs"] if e.get("started") != unix_of(self.report.get("started"))]
            hist["runs"].insert(0, run_entry(self.report))
            del hist["runs"][STATUS_RUNS:]
        except Exception as error:
            log("status: the run's history line not kept (%r)" % error)
        try:
            self.state.save()
        except OSError as error:
            log("state not saved: %s" % error)


# ------------------------------------------------------------------------------------------------
# The service
# ------------------------------------------------------------------------------------------------

STOPPING = []


def on_signal(signum, _frame):
    STOPPING.append(signum)
    raise SystemExit(0)


def backlog_waits(state):
    """Anything for a day's run to do: an item still to download, or one staged without a verdict."""
    for rec in (state.get("items") or {}).values():
        if rec.get("status") in ("queued", "retry", "downloaded") or (rec.get("status") == "tested" and not rec.get("verdict")):
            return True
    return False


def status(cfg):
    clock = Clock(cfg.tzname)
    ledger = ApiLedger(os.path.join(cfg.state_dir, "api-ledger.json"), cfg.api_day_cap, clock)
    ledger.load()
    st = State(os.path.join(cfg.state_dir, "state.json")).load()
    counts = {}
    for rec in st.data["items"].values():
        counts[rec.get("status")] = counts.get(rec.get("status"), 0) + 1
    print(json.dumps({"api_last_24h": ledger.last_24h() if ledger.data else None, "api_today": ledger.count(clock.day()),
                      "api_stop_today": ledger.stopped(clock.day()) if ledger.data else None,
                      "items": counts, "last_run": st.data.get("last_run"), "last_api": st.data.get("last_api"),
                      "key": "present" if read_key(cfg.key_file) else "absent"}, indent=1))


def export(cfg):
    """--export: what this sync has published, as entries a republish from the repo can merge (the entries' shape:
    id, rank, name, subscriptions, lifetime, sizeMb, updated, tags, description, preview), from the bridge's files and
    state.json. The server's data is ahead of the repo after a run that published; this is the way back."""
    st = State(os.path.join(cfg.state_dir, "state.json")).load()
    files = bridge_files(cfg)
    out = {"generated": time.strftime("%Y-%m-%dT%H:%M:%S"), "maps.json": [], "modes": {}, "broken.json": []}
    mine = {w for w, r in st.data["items"].items() if r.get("status") == "published"}

    def entry(m, rec):
        return {"id": int(m["id"]), "rank": m.get("rank"), "name": m.get("name"), "sizeMb": m.get("sizeMb"),
                "subscriptions": rec.get("subs"), "lifetime": rec.get("lifetime"),
                "updated": time.strftime("%Y-%m-%d", time.gmtime(int(rec.get("updated") or 0))),
                "tags": rec.get("tags") or [], "description": rec.get("description") or "", "preview": rec.get("preview") or ""}
    for m in files["maps"]:
        if isinstance(m, dict) and str(m.get("id")) in mine:
            out["maps.json"].append(entry(m, st.item(m["id"])))
    for mode, entries in files["modes"].items():
        out["modes"][mode] = [entry(m, st.item(m["id"]) or {}) for m in entries if isinstance(m, dict) and str(m.get("id")) in mine]
    for section in ("maps", "packs"):
        for e in files["broken"].get(section) or []:
            if isinstance(e, dict) and str(e.get("id")) in mine:
                out["broken.json"].append(dict(e, section=section))
    print(json.dumps(out, ensure_ascii=False, indent=1))


def main(argv):
    cfg = Config()
    if "--status" in argv:
        status(cfg)
        return 0
    if "--export" in argv:
        export(cfg)
        return 0
    signal.signal(signal.SIGTERM, on_signal)
    clock = Clock(cfg.tzname)
    os.makedirs(cfg.state_dir, exist_ok=True)
    LOG_FILE[0] = os.path.join(cfg.state_dir, "sync.log")
    if "--once" in argv:
        rest = argv[argv.index("--once") + 1:]
        if rest:
            try:
                cfg.lower(json.loads(rest[0]))
            except ValueError:
                print("--once takes an optional JSON object of lower limits, e.g. '{\"batch\": 3}'")
                return 2
        Run(cfg, clock, manual=True).run()
        return 0
    log("service start (version %s): no time windows - a run whenever nobody plays and there is work, at most %d h each, never "
        "around the %02d:%02d prefetch or the %02d:%02d restart; %s, Web API key %s, caps: %d calls a run, %d in 24 h, discovery and "
        "the check every %s; downloads: no daily cap, Steam's push-back stops them, a map at most %d times a day" % (
            (VERSION, cfg.run_hours) + cfg.prefetch_at + cfg.daily_restart + (
                "enabled" if cfg.enabled else "DISABLED (SYNC_ENABLED=0)", "present" if read_key(cfg.key_file) else "absent",
                cfg.api_run_cap, cfg.api_day_cap, hms(cfg.api_every), DOWNLOADS_PER_MAP_DAY)))
    seen, idle_until, said = None, 0.0, None   # when the server last showed somebody (or did not answer); the wait after an idle run
    status, shown = Status(cfg, clock), [None, 0.0]

    def show(state, why):
        """The status file while no run is going: on a change at once, else every STATUS_IDLE seconds (its counts every
        STATUS_IDLE_HEAVY), so its time says the sync is alive."""
        changed = shown[0] != (state, why)
        if not changed and clock.now() - shown[1] < STATUS_IDLE:
            return
        try:
            n = Run(cfg, clock, status=status)
            n.state.load()
            n.ledger.load()
            status.rebuild(n, force=changed, every=STATUS_IDLE_HEAVY)
        except Exception as error:
            log("status: the idle picture not rebuilt (%r)" % error)
        status.set(state=state, why=why, map=None, step=None, run=None)
        status.write()
        shown[0], shown[1] = (state, why), clock.now()
    while True:
        trigger = os.path.join(cfg.state_dir, "run-now")
        if os.path.exists(trigger):
            overrides = read_json(trigger, {}) or {}
            try:
                os.remove(trigger)
            except OSError:
                pass
            run_cfg = Config()
            run_cfg.lower(overrides if isinstance(overrides, dict) else {})
            log("run-now: a manual run (%s)" % (json.dumps(overrides) if overrides else "no overrides"))
            try:
                Run(run_cfg, clock, manual=True, status=status).run()
            except Exception as error:  # the service must outlive any one run
                log("the manual run failed: %r\n%s" % (error, traceback.format_exc().rstrip()))
                log("mail %s" % mail_crash(cfg, clock, error, True))
            shown[0] = None
        elif not cfg.enabled:
            show("stopped", "The sync is switched off.")
        elif clock.now() < idle_until:
            show("idle", "The last run found nothing to do: the sync looks again at %s." % clock.local(idle_until).strftime("%H:%M"))
        else:
            why = start_blocked(cfg, clock)
            state, words = "waiting", None
            if why is not None:
                words = ("The server's daily restart is near: the sync waits until it is over." if "restart" in why else
                         "The server's nightly prefetch runs first: the sync waits until it is done.")
            h = humans_on(cfg)
            flag = os.path.exists(os.path.join(cfg.bridge_data, "maintenance"))
            if h is None or h > 0 or flag:
                seen = clock.now()
            if why is None and h is None:
                why, words = "the game server does not answer A2S", "The game server does not answer, so the sync waits."
            elif why is None and h:
                why = "%d human(s) on the game server" % h
                words = "Somebody is playing: the sync waits until the server has been empty for %d minutes." % (cfg.quiet // 60)
            elif why is None and seen is not None and clock.now() - seen < cfg.quiet:
                why = "somebody was on the server %s ago (or the maintenance flag)" % hms(clock.now() - seen)
                words = ("The server is in maintenance, so the sync waits." if flag else
                         "Somebody played a moment ago: the sync waits until the server has been empty for %d minutes." % (cfg.quiet // 60))
            st = read_json(os.path.join(cfg.state_dir, "state.json"), {}) or {}
            if why is None and not work_waits(cfg, clock, st):
                why = "nothing to do: no backlog, discovery and the check not due"
                book = ((st.get("downloads") or {}).get(clock.day()) or {})
                due = clock.local(float(st.get("last_api") or 0) + cfg.api_every).strftime("%H:%M")
                if backlog_waits(st) and book.get("stop"):
                    state, words = "stopped", "Steam asked for a pause today (%s): the downloads go on tomorrow; the next Workshop check is at %s." % (
                        clean(book["stop"], 120), due)
                else:
                    state, words = "idle", "Nothing to do: no maps are waiting, and the next Workshop check is at %s." % due
            if why is not None:
                if why != said:
                    log("not starting a run: %s" % why)
                said = why
                show(state, words or why)
            else:
                said = None
                try:
                    report = Run(Config(), clock, status=status).run()
                    if report.get("idle"):
                        idle_until = clock.now() + IDLE_WAIT
                except Exception as error:
                    log("the run failed: %r\n%s" % (error, traceback.format_exc().rstrip()))
                    log("mail %s" % mail_crash(cfg, clock, error, False))
                    idle_until = clock.now() + IDLE_WAIT
                shown[0] = None
        time.sleep(30)


def prefetch_due(cfg, clock, now=None):
    """Why the watcher's nightly prefetch holds the sync now, or None: from 10 minutes before SYNC_PREFETCH_AT (the 03:25 restart is
    the watcher's) until the watcher stamps tonight's prefetch done or SYNC_PREFETCH_MINUTES are over."""
    now = clock.now() if now is None else now
    slot = clock.at(cfg.prefetch_at, now)
    if not slot - 600 <= now < slot + cfg.prefetch_span:
        return None
    st = read_json(cfg.watcher_state, {}) or {}
    if st.get("prefetch_day") == clock.day(now):
        return None
    return "the restart watcher's nightly prefetch (prefetch_day %s)" % (st.get("prefetch_day") or "none")


def run_deadline(cfg, clock, now=None):
    """A run's end, were it to start now: SYNC_RUN_HOURS on, cut short before the game server's restarts - 15 minutes before the daily
    one, 10 before the prefetch's - so the test engine never runs through one. The service starts the next run once nobody plays."""
    now = clock.now() if now is None else now
    end = now + cfg.run_hours * 3600
    for day in (0, 1):
        for hhmm, lead in ((cfg.daily_restart, 900), (cfg.prefetch_at, 600)):
            at = clock.at(hhmm, now + day * DAY) - lead
            if now < at < end:
                end = at
    return end


def start_blocked(cfg, clock, now=None):
    """Why no run may start now by the clock, or None: the daily restart's stretch (15 minutes before it to 10 after), the prefetch,
    and a run that would have under MIN_RUN before such a cut - a run of a few minutes only writes a report (the owner's 85)."""
    now = clock.now() if now is None else now
    daily = clock.at(cfg.daily_restart, now)
    if daily - 900 <= now < daily + 600:
        return "the game server's daily restart"
    why = prefetch_due(cfg, clock, now)
    if why:
        return why
    end = run_deadline(cfg, clock, now)
    if end - now < MIN_RUN:
        cut = "daily restart" if any(abs(clock.at(cfg.daily_restart, now + d * DAY) - 900 - end) < 1 for d in (0, 1)) else "nightly prefetch"
        return "a run now would have %s before the game server's %s cuts it short" % (hms(end - now), cut)
    return None


def work_waits(cfg, clock, st):
    """A run has work: maps to download or test (and Steam has not stopped today's downloads), or discovery and the check are due."""
    book = ((st.get("downloads") or {}).get(clock.day()) or {})
    if backlog_waits(st) and not book.get("stop"):
        return True
    return clock.now() - float(st.get("last_api") or 0) >= cfg.api_every


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
