#!/usr/bin/env python
"""docshots.py - the documentation screenshots of Over Strike 2's HUD, from a real client on a test copy (AGENTS.md, "The documentation shows
the whole HUD"). One table of rows (ROWS): every page, tab and state of the HUD, and every message of the message list (messages.txt beside this file,
or --picks <file> in its format). Standard library plus Pillow, runnable with python -I. It drives cs2client.ps1's verbs rcon, cmd, shot and log and, for
what only the restart watcher or other players can send the bridge, ../probe/bridgepost.py, which refuses anything but a candidate bridge on a test copy: it
starts no client and no server, and it never reads a secret.

    python -I docshots.py --list                                   every row, how it is reached, and the counts per status
    python -I docshots.py --dry-run [--res WxH|all] [--only a,b]   exactly what a run would execute, touching nothing
    python -I docshots.py --selfcheck                              the offline checks, every resolution; touches no server, no client, no lock
    python -I docshots.py --res 1920x1080 --out <folder> [--only a,b] [--bridge-copy] [--renamed] [--keep-raw]     the real run, one resolution
    python -I docshots.py --res 1920x1080 --out <folder> --remask      the masks again on a --keep-raw run's raw frames, after correcting a box: no game, no server
    python -I docshots.py --mask-file <in.png> <out.png> --row <id>    a row's masks on any picture, to check them offline

THE DEPLOYMENT comes from the private config (../probe/probe.py, deploy/os2.py): the server's address (server.address) and the server lock
(server.lock_file). The client's account and its Steam name are this PC's Steam login (probe.steam_account, probe.steam_names), and client.private_names
adds names that must never reach a picture, a command or the log. Without a private config the offline modes use a made-up deployment; a real run refuses.

BEFORE A REAL RUN (not done here; ../README.md, "The documentation's screenshots"): the server lock (probe.py lock), the insecure test copy (testcopy.py prep,
then for --bridge-copy bridge-prep and bridge-run <tag> --keep-flag, then run), the addon lists emptied with a map load (../README.md, step 4), the HUD built from the commit under test, and a client
per resolution, which the run refuses without:

    pwsh -NoProfile -File tools/client/cs2client.ps1 start -Connect default -HudLocal -IncludeThumbs -AllowServerLock -Console -Resolution <WxH> -MaxMinutes 120 -Purpose "docshots <WxH>"

The run refuses to start unless the server lock exists and starts with the harness's owner line and A2S on the server says VAC off, nobody but the test
client is on, and the map is the start map. Rows that write the bridge's data (a vote, a like) or need its word run only with --bridge-copy, which says that a
candidate bridge on a copy of its data is up (testcopy.py bridge-run): the run holds that to the facts with bridgepost.py check before its first verb.

TIMING: the harness's rcon verb runs its command about 1.4 s after its launch and returns at about 1.7 s; a shot takes 1-3 s, its frame about
1.7 s after its launch. A waited RCON call (rc, show, press, click) is followed at once by the next step; a message of a few seconds goes out in the
background (rb, press(bg=True), note) with its shots behind it, and no second RCON call starts before the first has returned: nobody has measured two at
once. A picture that depends on how long ago something happened, not on the call just before it, waits for that moment itself (until): the HUD check's
warning from the join, the vote's clocks and words from its opening or its close, a card or a line gone from a spawn or from the rows before.

NAMES: the pictures are public, so test names only. Best of all the client plays under the test name: OsDocName (this folder), a throwaway plugin on the test copy
only, makes the server call it Alice, and --renamed says so (the run checks it with osd_name): then no mask is needed for a name, only the avatar is blurred (and CS2's
scoreboard, for its avatars), and the rows whose own words name the client - a fun change typed in chat - can run. The bridge is another matter: it knows
the client by the name of the game log's connect line, the Steam name, until the client's next chat line or click under the test name. So the ballots' waiting
names keep their paint under --renamed (Mask.keep), HUDtest's vote tells the bridge the test name first (bridgepost.py client-on), and every row that opens a
vote runs bridgepost.py names-check before its picture, which refuses when a private name is among the vote's names. Without it: every frame gets the CS2 team counter's
avatar and the chat zone blurred (the player's own chat lines carry the name) before it is written; a row whose subject is chat blurs only what is above its lines.
Where a row would show the client's own name, the row says so in its names field and paints a test name over it, by a box at 1080p scaled by the height (words
drift up to 5 % in width between resolutions, so a mask takes a whole label, or blurs where a label's words change). The boxes marked UNSEEN come from the
committed stylesheet: no shot of this content exists yet, so the FIRST run of a content takes --keep-raw, its pictures are checked, a wrong box is corrected here
and --remask redoes them with no game. Everything else is sent under a test name: os_notice's words, os_hud centre's, os_fun's who, os_hud click for an
account not on the server (HUDtest). Raw frames are deleted once masked, unless --keep-raw. Review every picture before it goes into the public docs:
tools/scan/allow.txt takes its hash, nothing here reads text in a picture.

THE SESSION: nobody playing, the release's final commit; --bridge-copy --renamed --keep-raw --built <commit>. The full table at 1920x1080 for the docs (publish.py:
docs/hud/*, README.md, tools/scan/allow.txt); --review-set at the other six resolutions, 3840x2160 at full size, for the owner's review only, never committed.

STATUS is against the COMMITTED code: git show HEAD:..., never the working tree, where the message system may be half-built.
    ready                 today's committed code can trigger it (a row on today's surface says so in "today", beside the surface the message list picked: retake
                          it when the message system lands)
    needs-message-system  it waits for the message system the message list describes
    not-triggerable       it cannot be reached without leaving state on production, or without input the hidden client cannot give; the row says why
"""
from __future__ import annotations

import argparse
import html
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
OS2 = HERE.parents[2]                                   # the repository this file is in: its HEAD is what the rows are held to
HARNESS = OS2 / "tools" / "client" / "cs2client.ps1"
BRIDGEPOST = OS2 / "tools" / "client" / "probe" / "bridgepost.py"
sys.path.insert(0, str(OS2 / "tools" / "client" / "probe"))
import probe  # noqa: E402  (the private config, the harness's lock owner, this PC's Steam login)

OWNER_LINE = "owner: " + probe.OWNER    # the harness's lock: its first line
PICKS = HERE / "messages.txt"           # the message list: every on-screen message the documentation shows (--picks: another file in its format)
LAYOUT = "hud/layout/gungame_hud_v3_0_0.xml"
WORK = probe.main_checkout(OS2) / ".work" / "docshots"   # scratch: the dry run's default folder, the self-check's
MADE_UP_ACCOUNT = "42424242"            # the offline modes' client when Steam names none: no such player


def deployment():
    """(host, port, lock file or None, the client's account, its private names) from the private config and this PC's Steam login. Without
    a config: a made-up server and no lock, which the offline modes can work with and a real run refuses."""
    if probe.has_config():
        host, port = probe.host_port()
        lock = probe.cfg().get("server.lock_file", "") or None
    else:
        host, port, lock = "play.example.com", 27015, None
    account = probe.steam_account()
    return host, port, (Path(lock) if lock else None), (str(account) if account else ""), tuple(n.lower() for n in probe.private_names())


HOST, PORT, LOCK, STEAM_ACCOUNT, REAL_NAMES = deployment()   # REAL_NAMES: never in a command, a mask or a row's words; scrubbed from the run's log
START_MAP = "ar_shoots"
ACCOUNT = STEAM_ACCOUNT or MADE_UP_ACCOUNT   # the client's account
FAKE_ACCOUNT = "42424243"       # no such player on the server: os_hud click acts as HUDtest for it (HudScreen.SimulateClick)
ME = "Alice"                    # the test name painted over the client's own name, and the who of what the run itself changes
BUILT = ""                      # --built: the commit the build under test came from, when HEAD is later by documentation only
RENAMED = False                 # --renamed: the client goes by ME on the server (OsDocName), so no mask is needed for a name
RESOLUTIONS = ("1920x1080", "1280x720", "1680x1050", "1600x1200", "2560x1080", "2560x1440", "3840x2160")  # the docs' 1920x1080, then the review set's six
ALL_VERBS = ("rcon", "cmd", "shot", "log")

READY, NEEDS, NOT = "ready", "needs-message-system", "not-triggerable"
STATUSES = (READY, NEEDS, NOT)
INGAME = "CSGO_GAME_UI_STATE_LOADINGSCREEN -> CSGO_GAME_UI_STATE_INGAME"

# what the estimate counts (seconds), as the harness's verbs take them: rcon (RCON inside the game container) 1.7 s, its command about T_CMD after
# the launch (measured 2026-11-06); a shot 2.5 s, its frame T_FRAME after the launch
T_RCON, T_SHOT, T_CON, T_LOG, T_WAIT_LOG = 1.7, 2.5, 1.7, 2.0, 40.0
T_CMD, T_FRAME = 1.4, 1.7
T_POST = 2.0       # a bridgepost.py call: not timed yet, a guess; a vote is two execs
VOTE_GAP = 125     # MapVote.WaitSeconds, 120, and a little
NEWS_GONE = 9      # Notices.NewsSeconds, 8, and a little: the line that says a notice was taken back
CHAT_GONE = 20     # CS2's chat box fades 16-19 s after its last line, older lines with it (measured 2026-11-07): a chat row waits out the rows before it


# ---------------------------------------------------------------- geometry: boxes at 1080p, placed by the height ----------------------------------------

# ---------------------------------------------------------------- the committed code: git show HEAD:... ----------------------------------------------------

_HEAD_CACHE: dict = {}


def head_file(path):
    """A file as committed in this repository (None when it is not there): someone may be editing the working tree."""
    if path not in _HEAD_CACHE:
        r = subprocess.run(["git", "-C", str(OS2), "show", f"HEAD:{path}"], capture_output=True, stdin=subprocess.DEVNULL, timeout=60)
        if r.returncode != 0 and path.startswith("bridge/") and path.count("/") == 1:   # the bridge's shared files moved to bridge/Shared/ (cb69070)
            r = subprocess.run(["git", "-C", str(OS2), "show", f"HEAD:bridge/Shared/{path[7:]}"], capture_output=True, stdin=subprocess.DEVNULL, timeout=60)
        _HEAD_CACHE[path] = r.stdout.decode("utf-8", "replace") if r.returncode == 0 else None
    return _HEAD_CACHE[path]


def words_at_head(path, words):
    """Whether these words are still in the committed code: in that file, or anywhere under its top folder (a file split or moved keeps them)."""
    source = head_file(path)
    if source is not None and words.casefold() in source.casefold():
        return True
    r = subprocess.run(["git", "-C", str(OS2), "grep", "-q", "-i", "-F", words, "HEAD", "--", path.split("/")[0]], capture_output=True, stdin=subprocess.DEVNULL, timeout=60)
    return r.returncode == 0


def head_commit():
    r = subprocess.run(["git", "-C", str(OS2), "rev-parse", "--short", "HEAD"], capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=30)
    return r.stdout.strip() if r.returncode == 0 else "?"


def head_version(path, pattern):
    m = re.search(pattern, head_file(path) or "")
    return m.group(1) if m else "?"


_CONTENTS = re.findall(r'\("(v[\d.]+)", (\d+)\),', head_file("bridge/HudVersion.cs") or "")   # HudVersion.Contents: its last line is the committed content; the rows fake the versions around it
CONTENT, LEVEL = (_CONTENTS[-1][0], int(_CONTENTS[-1][1])) if _CONTENTS else ("?", 0)      # 46 is v4.6.0: os_hud level and os_hud content take this number


@dataclass(frozen=True)
class Box:
    """A box at 1920x1080. CS2 scales its HUD by the height (measured on real shots: at 1280x720, 1680x1050, 1600x1200, 2560x1080, 2560x1440, 3840x2160):
    what is centred keeps its distance from the centre, times the scale; anchor l and r keep it from that edge."""
    x0: float
    y0: float
    x1: float
    y1: float
    anchor: str = "c"


def place(b, w, h):
    """Anchors c, l, r as above; v: the big vote window, centred both ways, its ui-scale 92 % at 16:10 and 80 % at 4:3 (.AspectRatio16x10 .os-screen.voting .os-win)."""
    s = h / 1080
    fy = lambda y: y * s
    if b.anchor == "c":
        fx = lambda x: w / 2 + (x - 960) * s
    elif b.anchor == "l":
        fx = lambda x: x * s
    elif b.anchor == "r":
        fx = lambda x: w - (1920 - x) * s
    else:
        k = 0.92 if abs(w / h - 1.6) < 0.01 else 0.8 if abs(w / h - 4 / 3) < 0.01 else 1.0
        fx = lambda x: w / 2 + (x - 960) * s * k
        fy = lambda y: h / 2 + (y - 540) * s * k
    x0, y0, x1, y1 = fx(b.x0), fy(b.y0), fx(b.x1), fy(b.y1)
    return (max(0, round(x0)), max(0, round(y0)), min(w, round(x1)), min(h, round(y1)))


def parse_res(res):
    m = re.fullmatch(r"(\d+)x(\d+)", res)
    if not m:
        raise SystemExit(f"--res takes WIDTHxHEIGHT, not {res!r}")
    return int(m.group(1)), int(m.group(2))


@dataclass(frozen=True)
class Mask:
    kind: str                # blur, paint (a flat fill and a test name in the HUD's look), chat (the chat zone down to y1)
    box: Box | None = None
    text: str = ""
    color: tuple = (245, 245, 245)
    size: float = 13
    spacing: float = 0
    align: str = "l"
    y1: float = 975
    bold: bool = True
    drift: bool = False      # the box's left edge follows WORD_DRIFT: words before it on the line are narrower at the small resolutions
    keep: bool = False       # kept under --renamed too: a name the bridge has from the game's log (a connect line), which OsDocName does not change


def paint(x0, y0, x1, y1, text, color=(245, 245, 245), size=13, spacing=0, align="l", anchor="c", bold=True, drift=False, keep=False):
    return Mask("paint", Box(x0, y0, x1, y1, anchor), text, color, size, spacing, align, bold=bold, drift=drift, keep=keep)


WORD_DRIFT = {(1280, 720): -10, (1680, 1050): -4, (1600, 1200): -5, (2560, 1080): -3}   # at about 130 px of words from the line's start (measured on the ballot's label); the rest: 0


def mask_box(m, w, h):
    """Where a mask goes on this frame. A drifted left edge only ever widens the box to the left of the 1080p place, so the name stays inside."""
    x0, y0, x1, y1 = place(m.box, w, h)
    if m.drift:
        x0 = max(0, x0 + round(WORD_DRIFT.get((w, h), 0) * h / 1080))
    return x0, y0, x1, y1


def blur(x0, y0, x1, y1, anchor="c"):
    return Mask("blur", Box(x0, y0, x1, y1, anchor))


def chat_above(lines):
    """The chat zone but for its last so many lines: what stays is the row's subject, what goes the player's own typed line and whatever was there before.
    Chat's last line sits at y 941-962 at 1080p, 21.14 px a line (measured on a real frame of !help's answer)."""
    return Mask("chat", y1=962 - 21.14 * lines)


GOLD, GOLD_HI, GREY = (233, 180, 76), (255, 212, 126), (170, 176, 184)
BLUR_AVATAR = blur(928, 36, 992, 102)                        # CS2's team counter shows the player's Steam avatar, on every frame
BUILD_INFO = Box(0, 1046, 720, 1080, "l")                    # CS2's build line at the bottom left of an insecure session ("Nov 04 16:01:42 - S 10-00-..."): not ours to show, on every frame
AVATAR_OVER_NEWS = Box(928, 36, 992, 84)                     # ...down to the top line above the open window, drawn over it, whose words stay sharp
BLUR_SCOREBOARD = blur(505, 495, 1435, 735)                  # CS2's own scoreboard: the name and the avatar
BLUR_NEWS_WIN = blur(480, 80, 1440, 128)                     # the top line above the open window, where a line carries the own name: its words drift too far to paint one in
BLUR_SETBY = blur(558, 356, 720, 373)                        # "Matches last 10 minutes - set by <name>", should the state carry one
M_PLAYER1 = paint(372, 364, 432, 384, ME, size=13)                                     # SERVER, Players: the first row (measured on real shots at all seven resolutions; that page's geometry is unchanged)
M_SETBY = paint(388, 356, 730, 373, f"Matches last 10 minutes - Set by {ME}", GREY, 12, bold=False)  # SERVER, Game: the whole label, from x 390 (measured at 1080p, 2026-11-05, content v4.6.0: a long name reaches 700)
# From here the committed stylesheet's numbers, checked where a real frame of content v4.6.0 had them (1080p): M_GREET_BACK, M_VWAIT1,
# BLUR_VSTART, M_VRESET, BLUR_TILE1_NAMES, M_SETBY. The rest: check them on the first run (UNSEEN, --keep-raw, --remask)
# The normal window: x 340-1580, y 130-950; its body starts at 297 (130 + the rule 3, the head 76, the tabs 48, the sub-tabs 40).
M_GREET = paint(482, 333, 900, 355, "WELCOME, ALICE", GOLD, 13, 3)                     # START: x 340 + 34 + the 90 px logo + 18; y 297 + 26, centred in the 90 px hero
M_GREET_BACK = paint(482, 333, 900, 355, "WELCOME BACK, ALICE", GOLD, 13, 3)
# The ballot's big window: x 80-1840, y 80-1000, scaled at 16:10 and 4:3 (anchor v); its body starts at 247, its header row by row: the title and osvstart y 259-295, Random, the strip
# and osvwait y 299-339, the limit's clock y 261-305 and the inactivity's y 305-349, each x 1046-1806 with its words from 1586. The first tile: x 114-444 from y 357, its picture y 361-546.
M_PLAYER1_VOTE = paint(112, 314, 190, 336, ME, size=13, anchor="v")                    # Players in the big window: 80 + 34, 247 + 14 + the summary and the head
M_VWAIT1 = paint(296, 304, 1040, 334, f"Waiting for: {ME}", GOLD_HI, 15, anchor="v", keep=True)   # the whole label: no strip yet, so it starts after Random's button, at x 299 (seen at 1080p, 2026-11-05)
M_VWAIT2 = paint(296, 304, 1040, 334, f"Waiting for: {ME}, HUDtest", GOLD_HI, 15, anchor="v", keep=True)   # the bridge's waiting names: sorted, as it sorts them
BLUR_VWAIT_TAIL = blur(905, 304, 1048, 334, "v")                                       # the strip is on: Everyone has voted  ·  Inactivity timer 4 s - reset by <own>; a number in the words, so no whole label to paint
BLUR_VSTART = blur(400, 262, 1040, 292, "v")                                           # osvstart: empty on main, "Started by <own>" once branch wt/votedata lands
M_VSTART = paint(348, 264, 1040, 290, f"Started by {ME}", (245, 245, 245), 13, anchor="v")   # osvstart, the whole label, from x 350 (seen at 1080p, 2026-11-05): a vote the client started
M_VRESET = paint(1583, 314, 1806, 340, f"Reset by {ME}", (255, 255, 255), 14, anchor="v")  # the inactivity clock's words, the whole label, from x 1587 (seen at 1080p, 2026-11-05)
BLUR_TILE1_NAMES = blur(118, 492, 334, 544, "v")                                       # the first tile: who voted for it, at the foot of its picture (.os-mnames; 30 px higher on a broken or crashed card)
BLUR_TILE1_WHO = blur(120, 568, 444, 592, "v")                                         # the first tile: picked by <own>

C_TOP = Box(470, 140, 1450, 240)
C_TOP_WIN = Box(470, 76, 1450, 130)
C_BAN = Box(520, 140, 1400, 250)
C_PLAIN = Box(780, 858, 1140, 930)
C_ALERT = Box(780, 792, 1140, 880)
C_CARD = Box(720, 600, 1200, 735)
C_LINE = Box(340, 860, 1580, 950)
C_CHAT = Box(0, 740, 620, 975, "l")
C_BOARD = Box(1450, 180, 1880, 900)
C_WHY = Box(730, 250, 1190, 830)
C_X = Box(1240, 135, 1580, 205)
C_VHEAD = Box(100, 250, 1820, 352, "v")
C_CLOCKS = Box(1040, 252, 1820, 350, "v")
C_SETROW = Box(360, 320, 900, 430)

# A retake against the committed pictures (publish.py): a picture counts as changed only where it shows something else, not where it
# differs on every shot. What a frame is about: the window, the big vote window, or the row's crop (WINDOW, VOTE_WINDOW, compare_region);
# what differs on every shot - clocks, timers, counters, names that change by themselves - is left out (volatile_boxes): these boxes
# for every window of a kind, and a row's own (Row.volatile). Anything else that differs is a real change, so a box stays as tight as its
# clock: a word, a style or a layout changed anywhere else shows in the comparison. Measured on the committed pictures at 1080p.
WINDOW = Box(340, 130, 1580, 950)                       # the normal window
VOTE_WINDOW = Box(80, 80, 1840, 1000, "v")             # the big vote window
V_MAPCOUNT = Box(548, 179, 770, 199)                    # the window's header: "3751 WORKSHOP MAPS", a count the sync moves
V_IDLE = Box(1248, 914, 1558, 942)                      # the window's foot: "CLOSES WHEN IDLE" and its draining bar
V_VOTE_MAPCOUNT = Box(296, 130, 470, 147, "v")          # the big window's header count
V_VOTE_SUBTAB = Box(112, 214, 322, 242, "v")            # "VOTE - 51 S", the vote's seconds on its sub-tab, and BROWSE beside it, which moves as the seconds' words narrow
V_VOTE_TILES = Box(110, 352, 1810, 918, "v")            # the ballot's tiles: rolled at random for every vote - their maps, pictures, names, votes
V_ONFOR = Box(1470, 362, 1558, 382)                     # SERVER, Players: the first row's ON FOR, a timer
V_HUD_COLUMN = Box(748, 362, 1110, 382)                 # SERVER, Players: the first row's HUD column, where the check counts its seconds
V_SYNC_BODY = Box(362, 298, 1558, 908)                  # SERVER, Sync: the sync's own live status, queue and history - every word a value of the moment
V_BOARD_MAPCOUNT = Box(1476, 654, 1560, 671)            # the board's foot: "3731 maps", the count the sync moves (V_MAPCOUNT's on the board)
V_BOARD_WAIT = (Box(1596, 488, 1646, 504), Box(1603, 551, 1652, 567))   # board-wait: the seconds of "The next one can start in 59 s" and of the vote's line
V_LINE_WAIT = Box(590, 876, 625, 898)                   # vote-cannot-call: the seconds of "You can call a vote again in 1 min 35 s"
V_BROWSER_HEAD = Box(1176, 352, 1560, 390)              # the map browser's Previous: <the map before> and its "8129 maps": the history and the count the sync moves
V_START_WAIT = Box(1312, 506, 1500, 528)                # START's live column in the wait after a vote: "The next one can start in 1 min 16 s"
V_STRIP_NAMES = Box(296, 302, 1040, 336, "v")           # the ballot's strip: a segment per card with votes, named after the card's map - rolled at random
V_RESTART_SECONDS = Box(400, 494, 600, 512)             # SERVER, Game: the restart armed, "PRESS AGAIN WITHIN 25 S" - its seconds, and its words pulse
V_TOP_EDGE = Box(470, 126, 1450, 140)                   # the top line above the window reaches into its frame: its words follow the match's age ("This match too", "ends in 60 s")
V_VOTE_PAGER_NEXT = Box(1024, 914, 1096, 956, "v")      # the ballot's pager: Next pulses while more pages follow (.os-pgb-glow, 1.6 s)
V_WHY_BAR = Box(740, 632, 1185, 642)                     # the reasons dialog's bar, which drains for its 15 s
V_BANNER_MAP = Box(800, 184, 1203, 206)                  # the map change banner's map: rolled at random (its picture beside it is left of the banner's panel)


# ---------------------------------------------------------------- the steps: what a row does, in order ---------------------------------------------------

@dataclass(frozen=True)
class Step:
    kind: str                # rcon, rcon_bg, con, post, pause, shoot, mark, wait_log, vote_gap, vote_closed, guard
    text: str = ""           # the command
    seconds: float = 0
    expect: str = ""         # a regex the reply must match, or the row has failed
    button: str = ""         # the HUD button the command clicks
    n: int = 1
    gap: float = 0
    parallel: bool = False
    wait_ms: int = 300


def rc(cmd, expect=""):
    """An RCON command, waited for (about 1.7 s); the next step follows at once."""
    return Step("rcon", cmd, expect=expect)


def rb(cmd, expect=""):
    """An RCON command in the background: the shots behind it see what it does."""
    return Step("rcon_bg", cmd, expect=expect)


def show(page, bg=False):
    return Step("rcon_bg" if bg else "rcon", f"os_hud show {ACCOUNT} {page}", expect=r"window on ")


def press(button, bg=False):
    """A click through the real click path (HudScreen.Press)."""
    return Step("rcon_bg" if bg else "rcon", f"os_hud press {ACCOUNT} {button}", expect=r"pressed ", button=button)


def click(button, who=ACCOUNT, bg=False):
    """What a button sends the bridge, for that account (HudScreen.SimulateClick): under the player's own name, or HUDtest for an account not on the server."""
    return Step("rcon_bg" if bg else "rcon", f"os_hud click {who} {button}", expect=r"sent !", button=button)


def note(seconds, text, bg=True):
    """os_notice: the news line, for everybody: the plugin's own line wins over the bridge's while it lasts (HudScreen.Frame's ownNews)."""
    return Step("rcon_bg" if bg else "rcon", f'os_notice {seconds} "{text}"', expect=r"os_notice: shown")


def con(cmd, wait_ms=300):
    """The client's console (an insecure session started with -Console)."""
    return Step("con", cmd, wait_ms=wait_ms)


def pause(seconds):
    return Step("pause", seconds=seconds)


def shoot(n=1, gap=0.0, parallel=False):
    return Step("shoot", n=n, gap=gap, parallel=parallel)


def mark():
    return Step("mark")


def wait_ingame(timeout=150):
    return Step("wait_log", INGAME, seconds=timeout)


def vote_gap():
    return Step("vote_gap")


def vote_closed():
    return Step("vote_closed")


def vote_opened():
    """Right after the step that opens a vote: from here the vote's clocks count (until("vote", ...))."""
    return Step("vote_opened")


ANCHORS = {"join": "the client's last jointeam returned", "vote": "the vote opened (vote_opened)", "closed": "the last vote closed (vote_closed)",
           "press": "the last marked press (pressed)"}


def pressed():
    """Right after a press whose effect comes later or lasts a while (a card's vote and its inactivity timer, a dislike and its 15 s dialog)."""
    return Step("pressed")


def until(anchor, seconds):
    """Sleep until `seconds` after the anchor (ANCHORS), at once if that has passed: for a picture that depends on how long ago it happened, not
    on how fast the calls before it were."""
    assert anchor in ANCHORS, anchor
    return Step("until", anchor, seconds=seconds)


def guard(max_age):
    return Step("guard", seconds=max_age)


def post(*args):
    """bridgepost.py: what only the restart watcher or other players can send the bridge. It refuses anything but a candidate bridge on a test copy, so a row with one needs --bridge-copy."""
    return Step("post", " ".join(args))


RELEASE = rc("os_hud release", expect=r"released:")
RESET_FUN = rc(f'os_fun reset "{ME}"', expect=r"fun: reset")


def content(level):
    return rc(f"os_hud content {ACCOUNT} {level}", expect=r"account \d+: HUD")


# ---------------------------------------------------------------- the table ----------------------------------------------------------------------------

NO_NAME = "no player name on screen"
TEST_NAME = "test name only"


def real(how):
    return "real name would show: " + how


GREETING = real("START's greeting: a test name is painted over it")
UNSEEN = " (its box from the stylesheet, never yet seen on a shot: check it on the first run)"
PLAYERS = real("the Players page's first row: a test name is painted over it")
TOP_NAMED = real("the top line starts with who did it, typed by the client: only OsDocName's test name may reach this row, no mask covers it") + UNSEEN
NEWS_VOTE = real("the top line above the window names who started the vote: blurred (vote-started-line shows it under a test name)")

# what a row writes in the bridge's data: such a row runs only with --bridge-copy
W_VOTE = "state.json and queues.json: the ballot's cards as shown (S.Shown, S.Met), the stock rolled anew"
W_LIKE = "votes.json: the mark, taken back afterwards"
W_TIME = "state.json: the match length"
W_MAP = "state.json: the map and its history"
W_GUARD = "nothing by the code on a first click; but a card that is not risky after all would open a vote"
W_NOTICE = "nothing it keeps: a restart announced is in its memory; but no bridge but a candidate may hear it (bridgepost.py refuses)"
W_FAKES = "state.json: the fake players, as seen (S.Seen)"

S_BOARD = "The board (TAB)"
S_WINDOW = "The window: START, FUN, SERVER, HELP"
S_MAPS = "The window: MAPS, the map browser"
S_VOTE = "The vote and its ballot states"
S_MODES = "The window in the other game modes"
SECTIONS = (S_BOARD, S_WINDOW, S_MAPS, S_VOTE, S_MODES)  # then "Messages: ...", one per heading of the picks file


@dataclass
class Row:
    id: str
    shows: str
    status: str = READY
    steps: list = field(default_factory=list)   # how to reach it: the exact commands, the shots among them
    when: str = ""                              # when to shoot, in words
    crop: Box | None = None
    masks: tuple = ()
    reason: str = ""                            # needs-message-system, not-triggerable: why
    section: str = ""
    pick: str = ""                              # the message's name in the picks file
    names: str = NO_NAME
    after: str = ""                             # the row whose state this one needs
    cleanup: list = field(default_factory=list)
    bridge: str = ""                            # what it writes in the bridge's data
    chat: bool = False                          # chat is its subject: its own chat mask instead of the whole zone
    win: object = True                          # the window is open in its frames, True, or the big vote window, "vote": the chat zone stops at its edge
    avatar: Box = BLUR_AVATAR.box               # where the team counter's avatar shows in its frames (never blurred through words drawn over it)
    today: str = ""                             # the surface today's committed code uses
    stand_in: bool = False                      # the message's real words on its real panel, sent with os_notice
    words_from: tuple = ()                      # (path at HEAD, words that must still be there)
    renamed: bool = False                       # its own words name the client, where no mask can go: only with --renamed
    volatile: tuple = ()                        # boxes that differ on every shot beyond those of its window's kind (volatile_boxes)


ROWS: list[Row] = []


def R(id, shows="", **kw):
    r = Row(id, shows, **kw)
    ROWS.append(r)
    return r


# ---- a fresh, real join first: any click ends the HUD check -------------------------------------------------------------------------------------------

R("check-click-to-play", "The welcome window at a real join, while the HUD check waits: the X is the version button, Close this to play beside it", pick="HUD check: Click to play",
  today="the window's X (os_hudver_v4.8.0, class vcheck) and its words beside it, as the picks' 7a; no chat copy",
  steps=[con("disconnect"), pause(3), mark(), con(f"connect {HOST}:{PORT}"), wait_ingame(), pause(4), con("jointeam 2 1", 1500), pause(5), shoot()],
  when="5 s after the team is joined: the window opens 1.5 s after the first spawn. The run's first row: any click ends the check, and only a real join brings it back",
  crop=C_X, masks=(M_GREET_BACK,), names=GREETING + UNSEEN)
R("players-check", "SERVER > Players while the check waits: HUD check: N s left in the HUD column", section=S_WINDOW,
  after="check-click-to-play", steps=[show("players"), pause(1), shoot()], when="1 s after the show returns; show is no click", masks=(M_PLAYER1,), names=PLAYERS,
  volatile=(V_ONFOR, V_HUD_COLUMN))
R("check-warning", "The warning before the disconnect, in CS2's alert box; under HUD_CHECK warn its words say to close the window", pick="HUD check: the disconnect warning",
  today="CS2's alert text 10b, 15 s, and its chat lines (the words of enforce take HUD_CHECK=enforce, which no verb sets)",
  after="players-check", steps=[RELEASE, until("join", 78), shoot(n=2)],
  when="from about 80 s after the team is joined, the first spawn a second after it: the warning comes 15 s before the check's 90 s (HudCheck.GraceSeconds, WarnSeconds), so 4-7 s into it; the window closed by os_hud release, which is no click, since it hides the box",
  crop=C_ALERT, win=False, words_from=("bridge/HudCheck.cs", "HUD check: No click yet"))
R("joined-hud-version", "CS2's plain centre text: who joined with which HUD", pick='"Erik joined with HUD v4_6_0"', today="CS2's plain centre text 9a, 8 s, held by sending it again, with its chat and board copies",
  stand_in=True, words_from=("bridge/Program.cs", "joined with"), after="check-warning", steps=[rb(f'os_hud centre 3 8 "Erik joined with HUD {CONTENT}"', expect=r"sent again"), pause(3.5), shoot(n=2)], cleanup=[content(LEVEL)],
  when="from about 5 s after the send. os_hud centre sends the real text verb's words under a test name; the cleanup says which HUD this game holds without a click, whose news names the client",
  crop=C_PLAIN, win=False, names=TEST_NAME + ": the real one names the client")

# ---- the top line, window closed: os_notice is the real sender of the download lines; the other lines' real words on their real panel --------------------------


def top(id, pick, text, words_from, today="the top line 4d, as picked", **kw):
    R(id, f"The top line: {text}", pick=pick, stand_in=True, today=today, words_from=words_from, steps=[note(10, text), pause(2.5), shoot()], when="about 4 s after the send goes out in the background",
      crop=C_TOP, win=False, names=TEST_NAME if re.search(r"\b(Bob|Erik|Cara)\b", text) else NO_NAME, **kw)


R("top-plain", "The top line: a plain news item, the match length set by a fake player", pick="A plain news item (Previous, the match length, new maps, a mode's reload)", today="the top line 4d, as picked; chat and the board too", bridge=W_TIME,
  steps=[post("act", "0", "time10"), pause(2), shoot()], cleanup=[post("act", "0", "time60"), pause(NEWS_GONE), post("fake-off")], when="about 4 s after the click, a fake player's, so it names a test name. The cleanup waits out the line that puts it back",
  crop=C_TOP, win=False, names=TEST_NAME, words_from=("bridge/Program.cs", "set the match length to"))
top("top-server-down", "The server went down on a map", "The server crashed on #1809 bigjoe_water and has restarted - That map no longer comes up at random, and voting for it takes a second click",
    ("bridge/CrashWatch.cs", "The server crashed on"), today="the top line 4d, 30 s; a real one records a crash mark")
top("top-downloading", "The server downloading a map", "Server is downloading Assembly: 61 percent (438 MB of 711 MB) - The map changes when it is ready",
    ("server/OverStrike2.Server/Workshop.cs", "the map changes when it is ready"), today="the top line 4d by os_notice, the real sender's own verb; a real one leaves a map on the server")
top("top-background-first", "Finishing a background download first", "Server is finishing the background download of Lake Aim first (63%) - Then Assembly, the map changes when it is ready",
    ("server/OverStrike2.Server/Workshop.cs", "Server is finishing the background download"), today="the top line 4d by os_notice, the real sender's own verb")
top("top-download-failed", "A download failed", "The server could not download Assembly: No connection - Pick another map", ("server/OverStrike2.Server/Workshop.cs", "The server could not download"),
    today="the top line 4d by os_notice, the real sender's own verb, 10 s")
R("game-changer-undone", "The top line: a map's game-changer undone, the real thing", pick="A map's game-changer undone", today="the top line 4d, 10 s, as picked",
  steps=[rb("sv_allow_votes 1"), pause(3), shoot(n=2)], cleanup=[rc("sv_allow_votes")], when="from about 4.5 s after the send: the plugin puts the convar back within a second and says so, once per setting and map, so a setting not blocked yet on this map; the cleanup reads it back into the log",
  crop=C_TOP, win=False, words_from=("bridge/BlockList.cs", "Undone: the server keeps its own rules"))

# ---- the version banners: this content's own words --------------------------------------------------------------------------------------------------------

R("hud-outdated", "The status banner: your HUD is out of date, the server's version faked", pick="Your HUD is out of date: the banner that stays", today="the status banner 4e, top centre, as picked; os_hud level fakes the server's version",
  steps=[rc(f"os_hud level {LEVEL + 1} {ACCOUNT}", expect=r"join banner"), pause(1), shoot()], cleanup=[rc("os_hud level 0", expect=r"server level real")], when="1 s after the call returns: it stays", crop=C_BAN, win=False)
R("hud-newer", "The status banner: your HUD is newer than the server's", pick="Your HUD is newer than the server's", today="the status banner 4e, red as the owner's note asks, 15 s from the join (vjoin)",
  steps=[rc(f"os_hud level {LEVEL - 1} {ACCOUNT}", expect=r"join banner"), pause(1), shoot()], cleanup=[rc("os_hud level 0", expect=r"server level real")], when="1 s after the call returns, inside the 15 s that os_hud level's join starts", crop=C_BAN, win=False)

# ---- the welcome window, in both forms --------------------------------------------------------------------------------------------------------------------

R("join-tagline", "The welcome window at a return: the tagline's line at the foot of its page, for 3 s", pick="Join tagline", today="a line on the window's page, as picked, and a chat copy; the same words are the page's large text",
  steps=[RELEASE, rb(f"css_guide {ACCOUNT} back", expect=r"guide: showing"), pause(4.5), shoot()],
  when="about 6 s after the send: the window opens about 2.5 s after the command (HudSlots.SpawnDelaySeconds 1.5, then the HUD's next frame; 4-4.5 s after the verb's launch, measured 2026-11-07)",
  crop=C_LINE, masks=(M_GREET_BACK,), names=GREETING + UNSEEN)
R("welcome-back", "The welcome window at a return: Welcome back, the map that is on, its likes, the hint", pick="Welcome-back card", today="the window's page, the wback panel, as picked",
  after="join-tagline", steps=[pause(0.5), shoot()], cleanup=[RELEASE], when="the next frame, about 7 s after the send: it stands until the X", masks=(M_GREET_BACK,), names=GREETING + UNSEEN)
R("welcome-first", "The welcome window at a first login: until its X", pick="Welcome window, at every login", today="7a, as picked: every login, until its X",
  steps=[rc(f"css_guide {ACCOUNT} first", expect=r"guide: showing"), pause(3.5), shoot()], cleanup=[RELEASE],
  when="3.5 s after the call returns: the window opens about 2.5 s after the command (join-tagline)", masks=(M_GREET,), names=GREETING + UNSEEN)

# ---- chat -----------------------------------------------------------------------------------------------------------------------------------------------

_CHAT = real("the player's own typed line above the reply (its team colour): blurred")
R("chat-unknown", "Chat: an unknown command", pick="Command replies: unknown command, an alias taught, !help's list", today="a private chat line, as picked",
  steps=[pause(CHAT_GONE), con("say !foo"), pause(1), shoot()], when="about 1 s after the line", crop=C_CHAT, chat=True, win=False, masks=(chat_above(1),), names=_CHAT, words_from=("bridge/ChatCommands.cs", "Unknown command"))
R("chat-alias-help", "Chat: an alias taught, !help's list, and the Help page it opens", pick="Command replies: unknown command, an alias taught, !help's list",
  today="private chat lines beside the window, which covers chat from x 340", steps=[con("say !commands"), pause(1.5), shoot()], cleanup=[RELEASE],
  when="about 1.5 s after the line: the teach line and five lines of the list", crop=C_CHAT, chat=True, masks=(chat_above(6),), names=_CHAT, words_from=("bridge/ChatCommands.cs", "You typed"))
R("chat-refused", "Chat: a refusal with its reason", pick="The answer to your own click: refused, click again, already set", today="a private chat line, as picked",
  steps=[rc('os_fun gravity moon "Bob"', expect=r"fun: "), pause(CHAT_GONE), con("say !jump high"), pause(1), shoot()], cleanup=[RESET_FUN],
  when="about 1 s after the line, the lines of !commands gone first", crop=C_CHAT, chat=True, win=False,
  masks=(chat_above(1),), names=_CHAT, words_from=("bridge/Fun.cs", "together they throw players out of most maps"))

# ---- the window's pages, each by its tab through the real click path: a press on a closed window does nothing and still says pressed, so each opens it first ----

R("page-start", "START: the logo and the large text, the steps, the live column, the gold Start a vote", section=S_WINDOW, steps=[show("start"), press("os_tab_start"), pause(1), shoot()], when="1 s after the click returns", masks=(M_GREET,), names=GREETING + UNSEEN)
R("top-over-window", "The top line above the open window: one line, 84 px from the top", section=S_WINDOW, after="page-start", stand_in=True,
  steps=[note(10, "Bob set the match length to 10 minutes - From the next match"), pause(2.5), shoot()], cleanup=[pause(6)],
  when="about 4 s after the send; the cleanup waits out the note's 10 s, which the next page's frame would show above its window", crop=C_TOP_WIN, masks=(M_GREET,), names=GREETING + UNSEEN, avatar=AVATAR_OVER_NEWS,
  words_from=("bridge/Program.cs", "set the match length to"))
R("page-fun", "FUN: every setting at its default, the note, Random and Default", section=S_WINDOW, steps=[show("start"), press("os_tab_fun"), pause(1), shoot()], when="1 s after the click returns")
R("fun-set-by", "FUN: Gravity Moon, its row marked: Moon - set by Bob", pick='"set by <name>" on settings that are not default', today="a line on the Fun page's row, as picked; no chat or board copy yet",
  after="page-fun", steps=[rc('os_fun gravity moon "Bob"', expect=r"fun: "), show("fun"), pause(1), shoot()], when="1 s after the show returns", names=TEST_NAME + ": os_fun's who")
R("fun-refused", "FUN: Super jump High clicked just as Moon gravity greys it: the bubble at the button says why", pick="The answer to your own click: refused, click again, already set",
  today="the bubble at the button (FunClicks), as the owner's note asks; only a click within 2 s of the grey gets one, later ones nothing: the grey has said it", after="fun-set-by",
  steps=[pause(4), rc('os_fun gravity normal "Bob"', expect=r"fun: "), rb(f'os_fun gravity moon "Bob"; os_hud press {ACCOUNT} os_f2_2', expect=r"fun: "), pause(3.5), shoot()],
  when="about 5 s after the send, one line for both so the click comes within the 2 s: the bubble stands 8 s. The 4 s first: the chat box has faded since top-over-window's line, so fun-already's chat holds this row's lines alone",
  names=TEST_NAME + ": os_fun's who", words_from=("bridge/Fun.cs", "together they throw players out of most maps"))
R("fun-refused-tip", "FUN: a greyed choice, and a refused click answered at its button", section=S_WINDOW, today="the layout's os-ftip with the page's bubble words, .off and .refused", after="fun-refused", steps=[shoot()], when="the next frame, inside the bubble's 8 s", names=TEST_NAME + ": os_fun's who")
R("fun-already", "Chat: a setting typed that is on already: the reply says so", pick="The answer to your own click: refused, click again, already set", today="a private chat line; a click on a lit choice says nothing on this content: its look says it",
  after="fun-refused-tip", steps=[con("say !gravity moon"), pause(1), shoot()], when="about 1 s after the line", crop=C_CHAT, chat=True, masks=(chat_above(1),), names=_CHAT, words_from=("bridge/ChatCommands.cs", "already"))
R("fun-idle", "FUN: friendly fire off: Instakill and Grenades x10 dimmed, Does nothing while friendly fire is off", section=S_WINDOW, after="fun-already",
  steps=[rc('os_fun ff off "Bob"', expect=r"fun: "), show("fun"), pause(1), shoot()], when="1 s after the show returns", names=TEST_NAME + ": os_fun's who")
R("fun-many", "FUN: several rows off their default, free-for-all idling friendly fire's three", section=S_WINDOW, after="fun-idle",
  steps=[rc('os_fun bhop turbo "Bob"; os_fun ice on "Bob"; os_fun vampire on "Bob"; os_fun ffa on "Bob"', expect=r"fun: "), show("fun"), pause(1), shoot()], cleanup=[RESET_FUN, RELEASE], when="1 s after the show returns",
  names=TEST_NAME + ": os_fun's who")
R("fun-one-setting", "The top line: one fun setting changed, and who", pick="Fun: one setting changed", today="the top line 4d, as picked, 10 s; chat and the board too", steps=[con("say !gravity moon"), pause(1.5), shoot()], cleanup=[RESET_FUN],
  when="about 3 s after the typed line: the change is made a frame later and said 0.5 s after that", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True, words_from=("bridge/Fun.cs", "ChangedLine"))
R("fun-random", "The top line: Fun's Random, who, and what it rolled", pick="Fun: Random", today="the top line 4d, as picked, 10 s; chat and the board too", steps=[con("say !fun random"), pause(1.5), shoot()], cleanup=[RESET_FUN],
  when="about 3 s after the typed line", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True, words_from=("bridge/Fun.cs", "RandomLine"))
R("fun-default", "The top line: Fun's Default, and who", pick="Fun: Default", today="the top line 4d, as picked, 10 s; chat and the board too", steps=[rc('os_fun gravity moon "Bob"', expect=r"fun: "), con("say !fun default"), pause(1.5), shoot()],
  when="about 3 s after the typed line", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True, words_from=("bridge/Fun.cs", "DefaultLine"))
R("fun-catchup-off", "The top line: catch-up switched, a fun setting like any other", pick="Catch-up or grenade drops switched", today="the top line 4d, the catalogue's FunChanged, 10 s; chat and the board too", steps=[con("say !catchup off"), pause(1.5), shoot()], cleanup=[RESET_FUN],
  when="about 3 s after the typed line", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True)
# ---- FUN, Bots (content v4.8.0): the page, a row set by somebody, a click Arms Race refuses, and the two lines everybody gets; Wingman's greying is with the modes.
# Bots join for real on the copy: each row that puts some in takes them out again in its cleanup (os_fun reset undoes the Bots page's rows too).
R("page-bots", "FUN > Bots: every row at its default, the note, Kick all bots greyed, Default and the six presets", section=S_WINDOW,
  steps=[pause(9), show("start"), press("os_tab_fun"), press("os_tab_bots"), pause(1), shoot()],
  when="1 s after the click returns; the 9 s first let the top line of the row before (a fun change, 10 s) go",
  words_from=("bridge/Shared/Bots.cs", "Bots for everybody, set by anybody, until the map changes"))
R("bots-set-by", "FUN > Bots: Bots 2 set by Bob, its row marked: 2 - Set by Bob, Kick all bots no longer greyed", section=S_WINDOW, after="page-bots",
  steps=[rc('os_fun bots 2 "Bob"', expect=r"fun: "), show("bots"), pause(2), shoot()], cleanup=[RESET_FUN], when="2 s after the show returns: the two bots have joined",
  names=TEST_NAME + ": os_fun's who")
R("bots-refused", "FUN > Bots in Arms Race: Weapons greyed with the ladder's reason, and a click on Knives answered on its row", section=S_WINDOW, after="bots-set-by",
  steps=[show("bots"), pause(1), press("os_b5_2"), pause(1), shoot()], when="1 s after the click returns: the row says why the choice cannot be made here",
  words_from=("bridge/Shared/Bots.cs", "Arms Race's ladder hands out the weapons, to the bots too"))
R("bots-preset", "The top line: a Bots preset started, who, and what it set", pick="Bots: a preset started, and what it set",
  today="the top line 4d, the catalogue's BotsPreset, 10 s; chat and the board too",
  steps=[show("bots"), pause(1), press("os_bp2"), pause(0.5), RELEASE, pause(1), shoot()], cleanup=[RESET_FUN],
  when="about 1.5 s after the click: Sniper duel, one bot; the window closed first so the line stands alone", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True,
  words_from=("bridge/Shared/Bots.cs", "PresetLine"))
R("bots-kicked", "The top line: Kick all bots, and who", pick="Bots: Kick all bots, or Default", today="the top line 4d, the catalogue's BotsOff, 10 s; chat and the board too",
  steps=[rc('os_fun bots 1 "Bob"', expect=r"fun: "), pause(2), show("bots"), pause(1), press("os_bots_kick"), pause(0.5), RELEASE, pause(1), shoot()], cleanup=[RESET_FUN],
  when="about 1.5 s after the click: the window closed first so the line stands alone", crop=C_TOP, win=False, names=TOP_NAMED, renamed=True,
  words_from=("bridge/Shared/Bots.cs", "kicked every bot"))
R("catchup-boost", "Catch-up's boost: the card's green rows under the crosshair", pick="Catch-up's boost", today="the card 3d, as picked, 5 s; that player's chat and board", steps=[rb(f"os_catchup show {ACCOUNT} 5", expect=r"catchup: showed"), pause(2.4), shoot()],
  when="about 1.6 s after the command has run in the background: os_catchup show sends what a real spawn's boost sends", crop=C_CARD, win=False)
R("catchup-bounty", "The leader's bounty: the card's red rows under the crosshair", pick="The leader's bounty", today="the card 3d, as picked, 5 s; that player's chat", steps=[rb(f"os_catchup show {ACCOUNT} lead 4", expect=r"catchup: showed"), pause(2.4), shoot()],
  when="as catchup-boost", crop=C_CARD, win=False)
R("page-players", "SERVER > Players: the player, the team, the HUD of the check's click, how long on", section=S_WINDOW, steps=[show("start"), press("os_tab_server"), pause(1), shoot()],
  when="1 s after the click returns", masks=(M_PLAYER1,), names=PLAYERS, volatile=(V_ONFOR,))
R("players-older", "SERVER > Players: an older HUD: older - restart CS2 (os_hud content 1)", section=S_WINDOW, steps=[content(1), show("players"), pause(1), shoot()], cleanup=[content(LEVEL)],
  when="1 s after the show returns", masks=(M_PLAYER1,), names=PLAYERS, volatile=(V_ONFOR,))
R("page-game", "SERVER > Game: the match length, the two restarts", section=S_WINDOW, steps=[show("players"), press("os_tab_game"), pause(1), shoot()], when="1 s after the click returns",
  crop=C_SETROW, masks=(BLUR_SETBY,), names=real("a bridge state that carries a set-by: that place is blurred"))
R("game-restart-armed", "SERVER > Game: the restart after its first click, Press again; the line says click again", pick="The answer to your own click: refused, click again, already set",
  today="a line on the window's page, as picked, and the button's own words; NEVER a second click: it quits the server", steps=[pause(31), show("game"), press("os_set_restart", bg=True), pressed(), pause(3.5), shoot()],
  when="about 5 s after the one click. The 31 s first: a click within 30 s of an earlier one would be the second", crop=C_LINE, words_from=("bridge/Restarts.cs", "A restart kicks everyone"), volatile=(V_RESTART_SECONDS,))
R("game-time-10", "SERVER > Game: Matches last 10 minutes - Set by, the lit button", section=S_WINDOW, bridge=W_TIME,
  steps=[show("game"), post("act", "0", "time10"), pause(2), shoot()], cleanup=[post("act", "0", "time60"), pause(NEWS_GONE), post("fake-off"), RELEASE, until("press", 33)],
  when="about 4 s after a fake player's click, so the page and the top line name a test name; the cleanup waits out the line that puts it back, and the restart's armed line (30 s), which the next page's foot would show", crop=C_SETROW, names=TEST_NAME,
  avatar=AVATAR_OVER_NEWS, words_from=("bridge/HudBrowser.cs", "Matches last"), volatile=(V_RESTART_SECONDS, V_TOP_EDGE))
R("page-sync", "SERVER > Sync: what the map sync does, its queue, its history", section=S_WINDOW, steps=[show("sync"), pause(1), shoot()], when="1 s after the show returns. Live numbers and Workshop map names; no player name in the block (bridge/SyncDash.cs)",
  volatile=(V_SYNC_BODY,))
for _id, _b, _w in (("sync-queue-next", "os_osdq_next", "the queue's next page"), ("sync-history-next", "os_osdr_next", "the history's next page"), ("sync-maps-next", "os_osdm_next", "the map events' next page"),
                    ("sync-maps-last", "os_osdm_last", "the map events' last page")):
    R(_id, f"SERVER > Sync: {_w}", section=S_WINDOW, after="page-sync", steps=[press(_b), pause(1), shoot()], when="1 s after the click returns", volatile=(V_SYNC_BODY,))
R("page-help", "HELP > Help: where everything is", section=S_WINDOW, after="page-sync", steps=[press("os_tab_help"), pause(1), shoot()], when="1 s after the click returns")
R("page-how", "HELP > How it works: every rule and chance of a map pick", section=S_WINDOW, after="page-help", steps=[press("os_tab_how"), pause(1), shoot()], cleanup=[press("os_sub_help")], when="1 s after the click returns")

# ---- the map browser ------------------------------------------------------------------------------------------------------------------------------------

R("page-maps", "MAPS > Browse: eight cards by rank, sort, mode, size, search, Previous, pager", section=S_MAPS, steps=[show("maps"), press("os_tab_maps"), pause(1), shoot()], when="1 s after the click returns")


def browser(id, button, shows, cleanup=(), after="page-maps"):
    R(id, f"MAPS > Browse: {shows}", section=S_MAPS, after=after, steps=[press(button), pause(1), shoot()], cleanup=list(cleanup), when="1 s after the click returns")


browser("maps-sort-votes", "os_sort_votes", "sorted by likes")
browser("maps-sort-name", "os_sort_name", "sorted by name")
browser("maps-sort-size", "os_sort_size", "sorted by size")
browser("maps-sort-size-desc", "os_sort_size", "sorted by size, the other way (a second click)", cleanup=[press("os_sort_rank")], after="maps-sort-size")
browser("maps-mode-armsrace", "os_fm_ar", "the mode filter on Arms Race")
browser("maps-mode-deathmatch", "os_fm_dm", "the mode filter on Deathmatch")
browser("maps-mode-wingman", "os_fm_wm", "the mode filter on Wingman", cleanup=[press("os_fm_all")])
browser("maps-size-tiny", "os_fl_tiny", "the size chip under 25 MB")
browser("maps-size-small", "os_fl_small", "the size chip 25-120")
browser("maps-size-mid", "os_fl_mid", "the size chip 120-400")
browser("maps-size-large", "os_fl_large", "the size chip 400 MB and up", cleanup=[press("os_fl_all")])
browser("maps-liked", "os_fl_liked", "Liked by me", cleanup=[press("os_fl_liked")])
browser("maps-official", "os_fl_official", "Official", cleanup=[press("os_fl_official")])
browser("maps-ours", "os_fl_ours", "Our maps", cleanup=[press("os_fl_ours")])
browser("maps-page-2", "os_pg_next", "the second page")
browser("maps-page-last", "os_pg_last", "the last page", cleanup=[press("os_pg_first")])
browser("maps-keyboard", "os_q_kbd", "the on-screen keyboard, four cards left showing", cleanup=[press("os_kb_done")])
R("maps-search", "MAPS > Browse: a search: its best matches first", section=S_MAPS, after="page-maps", steps=[con("say !find lake"), pause(2), shoot()], when="2 s after the line")
browser("maps-search-keyboard", "os_q_kbd", "the keyboard over a search that is on", cleanup=[press("os_kb_done"), press("os_q_clear")], after="maps-search")
R("maps-search-empty", "MAPS > Browse: a search with no match, the empty page's words", section=S_MAPS, after="page-maps", steps=[con("say !find zzqqzz"), pause(2), shoot()], cleanup=[press("os_q_clear")], when="2 s after the line")
R("maps-workshop-refused", "MAPS > Browse: a pasted Workshop id the server refuses: the card says why (a non-map of the committed broken.json)", section=S_MAPS, after="page-maps",
  steps=[con("say 3369214545"), pause(3), shoot()], cleanup=[press("os_q_clear")], when="3 s after the line: the server's own no needs no Workshop question", words_from=("bridge/broken.json", "3369214545"))
# A public CS2 map that stays off the list: Aim Botz - Aim Training (CS2), tagged Cs2, Map and Custom - no Armsrace, Deathmatch or Wingman tag, which
# is all the sync's discovery searches (sync.tags), so it is never taken in, and with millions of subscribers it stays public. Not the HUD's own item:
# the server refuses that one as no map (WorkshopCard.ServerRefusal, 49901da).
UNLISTED_MAP = "3070244462"
R("maps-workshop-ready", "MAPS > Browse: a pasted Workshop id not on the list: Asking the Workshop, then Download and play. NEVER clicked: it downloads to the server", section=S_MAPS, after="page-maps",
  steps=[con(f"say {UNLISTED_MAP}"), pause(0.3), shoot(n=3)], cleanup=[press("os_q_clear")],
  when="from about 2 s after the line, serially: the Web API answers within a few seconds (Aim Botz, a public CS2 map on none of the lists)")
R("maps-liked-card", "MAPS > Browse: a card I like: its arrow lit, the line", section=S_MAPS, after="page-maps", bridge=W_LIKE, steps=[press("os_m2_up", bg=True), pause(3.5), shoot()], cleanup=[press("os_m2_down")],
  when="about 5 s after the click: the line lasts 8 s")
R("why-card", "Why the dislike?: the small dialog at the centre", pick="Why the dislike? (the reasons)", today="one small dialog at the centre, as the owner's note asks, 15 s; it shows only while the player has a cursor", after="page-maps",
  bridge=W_LIKE, steps=[press("os_m3_down", bg=True), pause(3.5), shoot()], cleanup=[press("os_m3_up"), pause(NEWS_GONE)],
  when="about 5 s after the click: it stands 15 s. The cleanup waits out the line its click leaves on the window's foot (8 s)", crop=C_WHY, volatile=(V_WHY_BAR,))
R("maps-risky-card", "MAPS > Browse: a risky map's card: the red strip with its record, Vote anyway?", section=S_MAPS, after="page-maps", bridge=W_GUARD, steps=[con("say !find jb_moti2"), pause(2), shoot()],
  when="2 s after the line (a map on the committed broken.json, 3 of 3 test loads)", words_from=("bridge/broken.json", '"jb_moti2"'))
R("play-asks-first", "Play on a risky map, the first click: Click again, the padlock, and the line says why. NEVER a second click: it would put the map to the vote", pick="Play asks first (a broken or crashed map)",
  today="the card's button and a line on the window's page, 30 s, as picked", after="maps-risky-card", bridge=W_GUARD, steps=[press("os_m1_play", bg=True), pause(3.5), shoot()], cleanup=[press("os_q_clear"), RELEASE],
  when="about 5 s after the one click", crop=C_LINE, words_from=("bridge/Program.cs", "to vote anyway"))

# ---- the board: +showscores from the client's console, which holds TAB down for the hidden client. Three lines first: Lately keeps real names otherwise ----

_BOARD = real("CS2's own scoreboard beside it, the name and the avatar: its rows are blurred; Lately is filled with test names first, because real lines stay on it")
_LATELY = [note(10, "Bob set the match length to 10 minutes - From the next match", bg=False), note(10, "Cara set the match length to 60 minutes - From the next match", bg=False), note(10, "Erik set the match length to 10 minutes - From the next match", bg=False)]
R("board-default", "The board beside the scoreboard: your like, Start a vote, the vote line, the full menu, Lately", section=S_BOARD, steps=[RELEASE, *_LATELY, con("+showscores", 1500), pause(2), shoot()], cleanup=[con("-showscores", 1500)],
  when="2 s after +showscores", crop=C_BOARD, win=False, masks=(BLUR_SCOREBOARD,), names=_BOARD, volatile=(V_BOARD_MAPCOUNT,))
R("board-liked", "The board with your like on the map that is on", section=S_BOARD, bridge=W_LIKE, steps=[press("os_b_vup"), *_LATELY, con("+showscores", 1500), pause(2), shoot()], cleanup=[con("-showscores", 1500), press("os_b_vdown")],
  when="2 s after +showscores", crop=C_BOARD, win=False, masks=(BLUR_SCOREBOARD,), names=_BOARD, volatile=(V_BOARD_MAPCOUNT,))
R("board-why", "Why the dislike? under the TAB cursor", pick="Why the dislike? (the reasons)", today="the same small dialog, under the board's cursor", bridge=W_LIKE,
  steps=[press("os_b_vdown", bg=True), con("+showscores", 1000), pause(1), shoot()], cleanup=[con("-showscores", 1500), press("os_b_vup")],
  when="about 4.5 s after the click: it stands 15 s", crop=C_WHY, win=False, masks=(BLUR_SCOREBOARD,), names=_BOARD, volatile=(V_WHY_BAR,))

for _id, _b, _w in (("board-big", "os_why_2", "You marked this map too big (the dislike's reason)"), ("board-ads", "os_why_3", "You marked this map: ads (the dislike's reason)")):
    R(_id, f"The board with my mark: {_w}", section=S_BOARD, bridge=W_LIKE, steps=[press(_b), *_LATELY, con("+showscores", 1500), pause(2), shoot()], cleanup=[con("-showscores", 1500), press("os_b_vup")],
      when="2 s after +showscores; the opposite vote takes the mark back", crop=C_BOARD, win=False, masks=(BLUR_SCOREBOARD,), names=_BOARD, volatile=(V_BOARD_MAPCOUNT,))

# ---- the vote. Solo first (nobody but the client present, so everybody has voted once it does), the fake's last: HUDtest stays present in the bridge's memory --

_V1_MASKS = (BLUR_NEWS_WIN, M_VSTART, M_VWAIT1)
_V1_VOTED = (BLUR_NEWS_WIN, M_VSTART, M_VRESET, BLUR_TILE1_NAMES)   # the strip on, the label says only Everybody has voted: no name in it (seen 2026-11-05)
_V1_NAMES = real("the waiting line: a test name is painted over it; the top line, which names who started the vote: blurred (vote-started-line shows it under a test name)") + UNSEEN
_V1_NAMES_VOTED = real("reset by: a test name on the clock, the header's blurred; the top line and the voter's name on the first tile: blurred") + UNSEEN
R("vote-open", "The ballot: everybody's window big, open and frozen, ten tiles, Random, the clocks, Waiting for", pick="The vote: everybody's window opens and freezes", today="the big vote window, as picked; its feeds still move",
  bridge=W_VOTE, steps=[vote_gap(), rc("mp_restartgame 1"), pause(5), click("os_b_next"), vote_opened(), show("vote", bg=True), post("names-check"), until("vote", 7.5), shoot()],
  when="about 9 s into the vote, its clocks at 51 s, the top line that says who started it (8 s) gone; the show's reply says whether a vote opened. A fresh match first, restarted 4 s before the click: the run's second half must not meet the 60-minute match end",
  masks=_V1_MASKS, names=_V1_NAMES, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-limit", "The ballot's limit clock: its seconds left, how many have voted", pick="The 60 s limit", today="a line on the window's page, as picked", after="vote-open", bridge=W_VOTE, steps=[shoot()], when="the next frame",
  crop=C_CLOCKS, masks=_V1_MASKS, names=_V1_NAMES, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-my-vote", "The ballot: my vote lit, LEADING, the strip, Everyone has voted", section=S_VOTE, after="vote-limit", bridge=W_VOTE, steps=[press("os_m1_play", bg=True), pressed(), pause(2.5), shoot()],
  when="about 4 s after the click, Keep (tile 1): the inactivity timer runs 5 s from the vote", crop=C_VHEAD, masks=_V1_VOTED, names=_V1_NAMES_VOTED, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-inactivity", "The inactivity timer, and who reset it", pick="Inactivity timer, and who reset it", today="a line on the window's page, as picked", after="vote-my-vote", bridge=W_VOTE, steps=[shoot()], when="the next frame, about 6.5 s after the click",
  crop=C_VHEAD, masks=_V1_VOTED, names=_V1_NAMES_VOTED, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-kept", "The ballot's header: the vote kept the map, Closes in, everybody still frozen", pick="The vote kept the map", today="a line on the window's page, as picked, 8 s; chat and the board too", after="vote-inactivity", bridge=W_VOTE, steps=[until("press", 8), shoot(n=2), until("press", 10), vote_closed()],
  when="from 8 s after the click: decided about 7 s after it (the command, then the inactivity timer's 5 s), the window stays 8 s", crop=C_VHEAD, masks=(BLUR_NEWS_WIN, M_VSTART, BLUR_TILE1_NAMES), names=real("who started it, the voter's name on the first tile: blurred") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS, words_from=("bridge/HudVote.cs", "The vote kept"))
R("vote-cannot-call", "The answer to a Start a vote in the wait after a vote: when the next one can start", pick="You cannot call a vote yet", today="a line on the window's page, as picked, 8 s; chat and the board name who tried",
  after="vote-kept", bridge=W_VOTE, steps=[pause(8), show("start"), guard(100), until("closed", 23), click("os_b_next", bg=True), pause(3.5), shoot()], cleanup=[RELEASE],
  when="about 5 s after the click, which goes out 25 s after the vote closed, so the line says 1 min 35 s; inside the 2 minutes (the guard refuses the click otherwise: it would open a vote)",
  crop=C_LINE, masks=(M_GREET,), names=GREETING + UNSEEN, words_from=("bridge/VoteRound.cs", "You can call a vote again in"), volatile=(V_LINE_WAIT, V_START_WAIT))
R("board-wait", "The board in the wait after a vote: Start a vote off, its words say how long", section=S_BOARD, after="vote-cannot-call", bridge=W_VOTE,
  steps=[*_LATELY, guard(100), until("closed", 55), con("+showscores", 1500), pause(2), shoot()], cleanup=[con("-showscores", 1500)],
  when="2 s after +showscores, about 61 s after the vote closed, so its words say 59 s; inside the 2 minutes; the three lines first, because the vote's own lines on Lately name the client",
  crop=C_BOARD, win=False, masks=(BLUR_SCOREBOARD,), names=_BOARD, volatile=(V_BOARD_MAPCOUNT, *V_BOARD_WAIT))
R("vote-browse", "The vote's Browse sub-tab: the map browser that adds to the vote: Add to the vote, On the vote", section=S_VOTE, bridge=W_VOTE, steps=[mark(), vote_gap(), click("os_b_next"), vote_opened(), press("os_v_browse", bg=True), post("names-check"), until("vote", 9), shoot()],
  when="about 11 s into the vote: the sub-tab says 49 s, the top line that says who started it (8 s) gone", masks=(BLUR_NEWS_WIN,), names=NEWS_VOTE + UNSEEN, avatar=AVATAR_OVER_NEWS)
R("vote-picked-card", "The ballot with a map a player put on it: PICKED, picked by, their vote on it", section=S_VOTE, after="vote-browse", bridge=W_VOTE, steps=[press("os_v_back", bg=True), pause(3), con("say !play ar_baggage"), pause(0.3), shoot()],
  when="about 2 s after the line, inside the 5 s of the inactivity timer. An official map, so the client loads it without a dialog", masks=(BLUR_NEWS_WIN, M_VSTART, BLUR_TILE1_NAMES, BLUR_TILE1_WHO, M_VRESET),
  names=real("the first tile's voter and picked by, the header's reset by and the top line: blurred; the clock's reset by: a test name is painted over it") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-decided", "The vote decided: NEXT MAP, The vote chose ... changing in N s, the top line, everybody still frozen", pick="The vote decided, and the change counting down", today="the ballot's words and the top line 4d; the countdown banner stays hidden under the window",
  after="vote-picked-card", bridge=W_VOTE + "; " + W_MAP, steps=[pause(2.5), shoot(n=3), vote_closed()], when="from about 7 s after the line: decided at 5 s, the change goes out at 13 s; the vote counts as closed after the frames", crop=C_VHEAD, masks=(M_VSTART, BLUR_TILE1_NAMES, BLUR_TILE1_WHO),
  names=real("the first tile's voter and picked by: blurred") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS, words_from=("bridge/VoteRound.cs", "Everybody has voted:"))
R("now-playing", "The card under the crosshair: Now playing, the map's own picture, at the new map's first spawn", pick="Now-playing card, at each map's first spawn",
  today="the card 3c, as picked, 8 s; a map the operator changed to, so the card names no picker (a Change's by is only ever the vote or the dice)",
  after="vote-decided", bridge=W_VOTE + "; " + W_MAP,
  steps=[wait_ingame(), pause(3), con("jointeam 2 1", 1000), show("start"), press("os_tab_start"), RELEASE, pause(1),
         mark(), rc("changelevel ar_pool_day"), wait_ingame(), con("jointeam 2 1", 1000), until("join", 0.5), shoot(n=2)], cleanup=[content(LEVEL)],
  when="two frames from about 3.5 s after the spawn at the team join on a map the operator changed to: at a player's first spawn on a new map the card fades in from 1.5 s and is fully shown from 1.8 s to 9.5 s (each up to a plugin frame, 0.25 s, late) - for a player still connected across the change, past the HUD check and with the window shut. So the vote's change is waited out first, a press passes the check (a real join's welcome or the check's window takes the card's place, and opening the window takes it away), the window is shut, and only then the map changes. Never a plugin reload to get there: it forgets the check", crop=C_CARD, win=False)
R("vote-started-line", "The ballot's header: Started by HUDtest, the real line under a test name", pick="The vote started: the line for everybody", today="NOT SHOWN on a surface of its own: the header of the window that opens, as the owner's note asks; chat and the board keep the line",
  bridge=W_VOTE, steps=[vote_gap(), post("client-on", ACCOUNT), click("os_b_next", who=FAKE_ACCOUNT, bg=True), vote_opened(), until("vote", 2.5), post("names-check"), shoot()],
  when="about 6 s after the click goes out. The bridge is told the client's test name first: the map change before reconnected it under its Steam name, and the waiting names are the bridge's",
  crop=C_VHEAD, masks=(M_VWAIT2,),
  names=real("the waiting line after HUDtest: a test name is painted over it") + UNSEEN, words_from=("bridge/VoteRound.cs", "started a vote for the next map"), win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-random-lit", "The ballot: my vote on Random, its button lit", section=S_VOTE, after="vote-started-line", bridge=W_VOTE, steps=[press("os_v_random", bg=True), pause(3.5), shoot()], cleanup=[press("os_m1_play", bg=True)],
  when="about 5 s after the click. The cleanup moves the vote to Keep at once, also after a failure: Random winning would roll a Workshop map, and the client would hang in CS2's download dialog", names=TEST_NAME + ": Waiting for: HUDtest", win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-waiting", "The ballot: Waiting for: HUDtest, my vote on Keep, 1 of 2 voted", pick="Waiting for: the players who have not voted", today="a line on the window's page, as picked; no chat or board copy yet", after="vote-random-lit", bridge=W_VOTE, steps=[pause(3.5), shoot()],
  when="about 5 s after the cleanup's click", crop=C_VHEAD, masks=(BLUR_TILE1_NAMES,), names=real("the voter's name on the first tile: blurred; Waiting for: HUDtest stays") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-pager", "The ballot's pager: Page 1 of 2, the hint for the other page (os_hud content fakes an older game's pages of eight)", section=S_VOTE, after="vote-waiting", bridge=W_VOTE, steps=[rb(f"os_hud content {ACCOUNT} {LEVEL - 1}", expect=r"account \d+: HUD"), pause(3.5), shoot()],
  when="about 5 s after the send", masks=(BLUR_TILE1_NAMES,), names=real("the voter's name on the first tile: blurred") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS,
  volatile=(V_VOTE_PAGER_NEXT,))
R("vote-players", "SERVER > Players during a vote: who has voted", section=S_WINDOW, after="vote-pager", bridge=W_VOTE, steps=[show("players", bg=True), pause(3.5), shoot()], when="about 5 s after the send, about 45 s into the vote",
  masks=(M_PLAYER1_VOTE,), names=PLAYERS + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS)
R("vote-limit-low", "The limit clock red and pulsing in its last 10 seconds", section=S_VOTE, today="the stylesheet's .os-screen.vlow, HudVote.LowSeconds", after="vote-players", bridge=W_VOTE, steps=[show("vote", bg=True), until("vote", 50.5), shoot()], cleanup=[content(LEVEL), until("vote", 72), vote_closed()],
  when="about 51 s into the vote, 9 s left; the cleanup waits for its 60 s to end, and for its window, which stays 8 s after", crop=C_CLOCKS, masks=(BLUR_TILE1_NAMES,), names=real("the voter's name on the first tile: blurred") + UNSEEN, win="vote", avatar=AVATAR_OVER_NEWS)

# ---- what only the bridge's own callers can say: bridgepost.py, to a candidate bridge on a test copy and nothing else. The real senders' notices, fake players' clicks ------------

R("restart-after-match", "The status banner: the server restarts after this match", pick="The server restarts after this match", today="the status banner 4e, from the bridge's RestartDue, as picked; chat and the board too", bridge=W_NOTICE,
  steps=[RELEASE, post("restart", "our-update", "match-end", "0"), pause(2), shoot()], cleanup=[post("restart-cancel", "our-update"), pause(NEWS_GONE)], when="about 4 s after the notice, the watcher's own; the cleanup takes it back and waits out the line that says so",
  crop=C_BAN, win=False, words_from=("bridge/Restarts.cs", "The server restarts after this match"))
R("restart-counting", "The countdown banner with the logo: the restart", pick="The restart counting down (10 s)", today="the countdown 6b with the logo, from the bridge's RestartDue, as the owner's note asks; chat and the board too", bridge=W_NOTICE,
  steps=[post("restart", "daily", "now", "12"), pause(2), shoot(n=2)], cleanup=[post("restart-cancel", "daily"), pause(NEWS_GONE)],
  when="about 4 s and 6.5 s into 12 s: the real one counts 10 (Watch.RestartCountdown), a little more here so that a slow call still shows digits; only the restart watcher would quit, and a test copy has none", crop=C_BAN, win=False, words_from=("bridge/Restarts.cs", "the server restarts in"))
R("window-restart-strip", "The window's foot: a restart coming, its words and its countdown", section=S_WINDOW, today="os-rstrip, from HudState.RestartKind", bridge=W_NOTICE,
  steps=[post("restart", "daily", "now", "30"), show("start", bg=True), pause(3.5), shoot()], cleanup=[post("restart-cancel", "daily"), RELEASE], when="about 5 s after the show goes out, of 30 s; the release outlasts the line that takes it back", crop=C_LINE, masks=(M_GREET,), names=GREETING + UNSEEN,
  volatile=(V_START_WAIT,))
R("top-cs2-update", "The top line: a CS2 update: the server restarts at this match's end, and restart CS2 afterwards", pick="A CS2 update: update your game", today="the top line 4d, as picked, until the restart: the watcher's own notice; chat and the board too", bridge=W_NOTICE,
  steps=[post("restart", "game-update", "match-end", "0"), pause(2), shoot()], cleanup=[post("restart-cancel", "game-update"), pause(NEWS_GONE)], when="about 4 s after the notice; the cleanup takes it back and waits out the line that says so", crop=C_TOP, win=False, words_from=("bridge/Restarts.cs", "A CS2 update"))
R("top-updated", "The top line: Updated to CS2 1.41.9.6 - Welcome back", pick='"Updated to vX" after the reconnect', today="the top line 4d, as picked, 60 s: the watcher's own notice; chat and the board too", bridge=W_NOTICE,
  steps=[post("updated", "game-update", "CS2", "1.41.9.6"), pause(2), shoot()], when="about 4 s after the notice. It stands 60 s: updated-joiner's reconnect outlasts most of that", crop=C_TOP, win=False, words_from=("bridge/Restarts.cs", "Welcome back"))
R("updated-joiner", "The line for a player who joins within 10 minutes of an update (the catalogue's UpdatedJoiner; no row of its own in the picks)", section=S_WINDOW, today="a line on the window's page, 15 s; that player's chat and board", after="top-updated", bridge=W_NOTICE,
  steps=[con("disconnect"), pause(3), mark(), con(f"connect {HOST}:{PORT}"), wait_ingame(), con("jointeam 2 1", 1000), pause(2.5), shoot(n=3)], cleanup=[content(LEVEL), RELEASE],
  when="three frames from about 7 s after the client is in game: the line stands 15 s from entered the game, the window opens 1.5 s after the first spawn. A real join asks the HUD check again: the cleanup says which HUD this game holds",
  crop=C_LINE, masks=(M_GREET_BACK,), names=GREETING + UNSEEN, words_from=("bridge/Restarts.cs", "Messages.UpdatedJoiner"))
R("vote-tie-lead", "The ballot with several voters: TIE on three cards, then LEADING with its voters' names and the strip in parts", section=S_VOTE, today="the tiles' vtie, vlead, the strip's segments", bridge=W_VOTE + "; " + W_FAKES,
  steps=[vote_gap(), post("fake-off"), post("fake-on", "4"), post("client-off", ACCOUNT), post("act", "0", "next"), post("vote", "0", "1"), post("vote", "1", "2"), post("vote", "2", "3"), pause(1), shoot(), post("vote", "2", "1"), pause(1), shoot()],
  cleanup=[post("vote", "0", "1"), post("vote", "1", "1"), post("vote", "2", "1"), post("vote", "3", "1")],
  when="a second after the third vote, and a second after one more moves to Keep (card 1). The fourth fake does not vote, so the inactivity timer does not run; the bridge takes the client for gone, so only test names are in its lines. The cleanup moves every vote to Keep: a tie is drawn by the dice, and a rolled Workshop map would hang the client in CS2's download dialog",
  crop=C_VHEAD, names=TEST_NAME + ": the client is out of the bridge's lists", win="vote", avatar=AVATAR_OVER_NEWS, volatile=(V_STRIP_NAMES,))
R("chat-vote-clicked", "A vote clicked on a card, in chat: who voted, and who still has to", pick="Lines for everybody: a vote clicked on a card", today="NOT SHOWN on a surface of its own: chat and the board", after="vote-tie-lead", bridge=W_VOTE + "; " + W_NOTICE,
  steps=[post("restart", "daily", "now", "30"), pause(1), shoot()], cleanup=[post("restart-cancel", "daily"), pause(NEWS_GONE), vote_closed(), post("client-on", ACCOUNT), post("fake-off")],
  when="a second after a restart's notice has ended the vote: the big window covers chat, which comes back with the lines still there. The cleanup takes the notice back and has the client present again", crop=C_CHAT, chat=True, win=False, masks=(chat_above(6),), names=TEST_NAME + ": the client is out of the bridge's lists", words_from=("bridge/VoteRound.cs", "voted to keep"))
R("rematch-countdown", "Restart match: the countdown banner, naming who", section=S_WINDOW, today="the countdown 6b, the catalogue's MatchRestart; chat and the board too", bridge=W_NOTICE,
  steps=[post("act", "0", "rematch"), post("act", "0", "rematch"), pause(1), shoot()], cleanup=[pause(10), post("fake-off")], when="about 3.5 s after a fake player's second click, of 10 s; the cleanup waits for the match to restart", crop=C_BAN, win=False, words_from=("bridge/Restarts.cs", "is restarting the match in"))

# ---- the other game modes: the header's mode, friendly fire's rows and the match length gone. The last rows: back on Arms Race, ready for the next resolution ----


def to_mode(mode, game_type, game_mode, map_name):
    """On a map of that mode, the team joined, and the new map's card under the crosshair gone (it comes 1.5 s after the spawn, for 8 s)."""
    return [RELEASE, rc(f"os_mode {mode}", expect=r"os_mode: "), rc(f"game_type {game_type}"), rc(f"game_mode {game_mode}"), mark(), rc(f"changelevel {map_name}"), wait_ingame(), pause(6),
            con("jointeam 2 1", 1500), content(LEVEL), until("join", 12)]


R("mode-deathmatch-fun", "FUN in Deathmatch: friendly fire's rows, the grenade drops and catch-up gone", section=S_MODES, bridge=W_MAP, steps=[*to_mode("deathmatch", 1, 2, "de_dust2"), show("fun"), pause(1), shoot()], when="1 s after the show returns")
R("mode-deathmatch-game", "SERVER > Game in Deathmatch: no match length", section=S_MODES, after="mode-deathmatch-fun", bridge=W_MAP, steps=[press("os_tab_game"), pause(1), shoot()], when="1 s after the click returns", masks=(BLUR_SETBY,), names=real("a set-by in the bridge's state: blurred"))
R("mode-wingman-fun", "FUN in Wingman", section=S_MODES, after="mode-deathmatch-game", bridge=W_MAP, steps=[*to_mode("wingman", 0, 2, "de_inferno"), show("fun"), pause(1), shoot()], when="1 s after the show returns")
R("mode-wingman-bots", "FUN > Bots in Wingman: the counts past its room for 4 players greyed, and every preset but Sniper duel", section=S_MODES, after="mode-wingman-fun",
  bridge=W_MAP, steps=[show("bots"), pause(1), shoot()], when="1 s after the show returns", words_from=("bridge/Shared/Bots.cs", "has room for"))
R("mode-wingman-game", "SERVER > Game in Wingman", section=S_MODES, after="mode-wingman-bots", bridge=W_MAP, steps=[press("os_tab_game"), pause(1), shoot()], when="1 s after the click returns", masks=(BLUR_SETBY,), names=real("a set-by in the bridge's state: blurred"))
R("mode-armsrace-back", "START, back on Arms Race: the run's last frame", section=S_MODES, after="mode-wingman-game", bridge=W_MAP, steps=[*to_mode("armsrace", 1, 0, START_MAP), show("start"), pause(1), shoot()], cleanup=[RELEASE], when="1 s after the show returns",
  masks=(M_GREET,), names=GREETING + UNSEEN)

# ---- at the very end, each leaves the bridge believing the server goes down, so each sets it right after itself: what must be called off in time ---------------

R("top-player-restart", "The countdown banner: a player's restart, naming who", pick="A player's !restart", today="the countdown 6b with the logo, as the owner's note asks, 10 s; a real second click quits the server, so the bridge is told the server is going down before it would", bridge=W_NOTICE,
  steps=[post("act", "0", "restart"), post("act", "0", "restart"), pause(1), shoot()], cleanup=[post("quit-notice"), post("fake-off"), *to_mode("armsrace", 1, 0, START_MAP)],
  when="about 3.5 s after a fake player's second click, of 10 s: the cleanup's quit notice must reach the bridge before the 10 s are up", crop=C_BAN, win=False, words_from=("bridge/Restarts.cs", "is restarting the server in"))
R("map-change-banner", "The countdown banner with the map's picture", pick="Map change counting down", today="the countdown 6b, as picked; a vote's change keeps everybody's window over it, so this is the dice's own: three players timing out on a map mark it and roll, called off in time. Marks the map in the candidate's data: once per copy and map (ar_pool_day, then ar_shoots_night on 2026-11-05)", bridge=W_MAP + "; state.json: the map's player-crash mark; " + W_FAKES,
  steps=[*to_mode("armsrace", 1, 0, "ar_shoots_night"), post("fake-off"), post("fake-on", "3"), rb("os_hud release", expect=r"released:"), pause(3), post("fake-crash"), pause(1.5), shoot()], cleanup=[post("quit-notice"), post("fake-off"), *to_mode("armsrace", 1, 0, START_MAP)],
  when="about 3.5 s after the third time-out, of 8 s: the cleanup's quit notice must reach the bridge before the 8 s are up: the change would hang the client in CS2's download dialog", crop=C_BAN, win=False, names=TEST_NAME,
  volatile=(V_BANNER_MAP,))

# ---- what cannot be shot yet ------------------------------------------------------------------------------------------------------------------------------

R("hud-hold", "The status banner: Steam is not showing the HUD, and back", pick="The HUD not shown by Steam, and back", status=NOT, today="the status banner 4e, set by the plugin's own PostBanner only",
  reason="Its trigger is Steam hiding the item, and no verb sets the banner: os_notice goes to the top line now. A test verb like os_hud centre would make it a row.")
R("hud-join-warning", "The join's version banner", pick="Your HUD is out of date: the warning at the join", status=NOT, today="none: the catalogue has no kind for it, a row that went",
  reason="The committed content has no panel for it (only the contents before v4_6_0 read svge/svlt); a game holding one is a published item, not committed code. The picks drop it: NOT SHOWN.")
R("vote-line-closed-window", "The vote's line for a closed window", pick="The vote line for a closed window", status=NOT, today="none: the panel's feed is gone (9685f17), a row that went",
  reason="Removed from the plugin: the window is held open while the vote freezes. The picks drop it: NOT SHOWN; the owner's note says it should not be possible.")
R("picture-on-request", "A map's picture, on request", pick="A map's picture, on request", status=NOT, today="none: the picture buttons and HudPreview's plugin half are gone (9685f17), a row that went",
  reason="Removed, as the owner's note asks: the picture belongs in the window and the browser, and the cards' pictures are not clickable. The picks: NOT SHOWN.")
R("workshop-card-downloading", "A Workshop card's progress", pick="A Workshop card downloading to the server", status=NOT, today="the os_ws card's bar, with chat and board copies",
  reason="Its progress needs os_ws_play, which downloads the map into the server's Workshop cache: a map left on production's volume. No verb fakes the step. maps-workshop-ready shows the card up to the click.")
R("crash-note", "The joiner's crash note", pick="The joiner's crash note", status=NOT, today="the welcome page's crash line, for 10 minutes after a crash",
  reason="Needs a real crash of the server the client is on, which records a crash mark on the map (CrashWatch.RecordCrash); no verb sends its words. top-server-down shows the crash's other line.")
R("no-hud-alert", "This server uses the menu", pick='"This server uses the menu: press TAB" (only without our HUD)', status=NOT, today="none: the words are gone, a row that went",
  reason="Its condition no longer exists: hud.addon is required and a server without the HUD cannot start (commits 78c6698 and 9685f17), and the catalogue has no kind for it. The owner's note on it asked the same: is this an error state?")
R("vote-match-end", "The vote at a match's end: Play this map again, nobody voting is Random", section=S_VOTE, status=NOT, today="the same ballot, MatchEnded",
  reason="Under the maintenance flag a match end opens nothing (Program.OnMatchEnd), and no cs2client verb takes the flag off; without the flag the dice roll a Workshop map at a start, and the client hangs in CS2's download dialog (HARNESS.md).")
R("vote-second-page", "A real second page of the ballot", section=S_VOTE, status=NOT, today="more than ten tiles",
  reason="One player's picks replace the rolled cards and leave when they lose their vote, so a solo run never holds more than ten; several players' picks would. vote-pager shows the pager's look under pages of eight.")
R("hold-no-menu", "The chat line for a player without the HUD during a hold (the catalogue's HoldNoMenu; no row of its own in the picks)", section=S_WINDOW, status=NOT, today="a chat line, from the plugin's own hold",
  reason="As hud-hold: only a real hold by Steam sets it, and the client, which holds the HUD by its loose files, is not who it is for.")
R("hover-only", "What only a mouse hover shows: who voted on a tile (os-mpop), a greyed Fun choice's tip (os-ftip), the buttons' hover looks", section=S_VOTE, status=NOT, today="the stylesheet's :hover rules",
  reason="os_hud press clicks through the server; nothing moves the hidden client's mouse, whose CS2 never has focus (HARNESS.md). vote-my-vote shows the names at the foot of a tile, which need no hover.")
R("sync-looks", "SERVER > Sync in its other looks: running with a progress bar, waiting, stopped, old or no word from the sync, a background download", section=S_WINDOW, status=NOT, today="the real sync's own status (SyncDash): page-sync shows whichever it is in at that moment",
  reason="Only the sync's status file sets them. No verb fakes one, and writing that file is state on production's volume.")
R("loading-dot", "The Live dot gold while a new map's announcement is pending", section=S_BOARD, status=NOT, today="the stylesheet's .os-global.loading .os-live-dot, off LoadPending",
  reason="It stands from a change until the map is announced, nearly all of it while the client is on its loading screen; no verb holds it.")


# The rows that show the map browser: its Previous and its count differ on every shot (V_BROWSER_HEAD)
BROWSER = {r.id for r in ROWS if r.section == S_MAPS} | {"why-card", "play-asks-first", "vote-browse"}

# The other six resolutions, for the owner's review only (never committed: the docs take 1920x1080): every page and one of each surface.
REVIEW_SET = ("page-start", "page-fun", "page-players", "page-game", "page-sync", "page-help", "page-how", "page-maps", "maps-keyboard", "maps-search", "board-default", "welcome-first",
              "join-tagline", "top-plain", "top-over-window", "hud-outdated", "restart-counting", "catchup-boost", "check-warning", "joined-hud-version", "chat-unknown", "why-card",
              "fun-refused", "vote-open", "vote-my-vote", "vote-kept", "vote-tie-lead", "window-restart-strip", "game-time-10")


# ---------------------------------------------------------------- the picks file ------------------------------------------------------------------------

@dataclass
class Pick:
    name: str
    bracket: str
    surfaces: str
    heading: str
    words: str = ""
    notes: bool = False


def parse_picks(text):
    m = re.search(r"^MESSAGES \((\d+)\)\s*$", text, re.M)
    end = text.find("WHAT CHANGES FROM TODAY")
    end = len(text) if end < 0 else end
    if not m or end < m.end():
        raise SystemExit(f"{PICKS}: no MESSAGES (n) section")
    declared, picks, heading = int(m.group(1)), [], ""
    for line in text[m.end():end].splitlines():
        hit = re.match(r"^- (.+?) \[(.+?)\]: (.*)$", line)
        if hit:
            picks.append(Pick(hit.group(1), hit.group(2), hit.group(3), heading))
        elif line.startswith("- "):
            raise SystemExit(f"{PICKS}: a message line this script cannot read: {line[:80]}")
        elif re.fullmatch(r"[A-Z][A-Z ,]+", line.strip()):
            heading = line.strip()
        elif line.startswith("  Words") and picks:
            picks[-1].words = line.strip()
        elif line.startswith("  OWNER'S NOTE") and picks:
            picks[-1].notes = True
    if len(picks) != declared:
        raise SystemExit(f"{PICKS}: says {declared} messages, holds {len(picks)}")
    return picks


def section_of(row, by_name):
    if row.pick:
        heading = by_name[row.pick].heading
        return "Messages: " + heading[0] + heading[1:].lower()
    return row.section


# ---------------------------------------------------------------- the checks every invocation runs -----------------------------------------------------

POST_VERBS = ("restart", "restart-cancel", "updated", "quit-notice", "fake-on", "fake-off", "fake-crash", "client-off", "client-on", "act", "vote", "names-check", "check")   # bridgepost.py's
RCON_ALLOWED = ("osd_name", "mp_restartgame 1", "sv_allow_votes", "os_hud show ", "os_hud press ", "os_hud click ", "os_hud level ", "os_hud content ", "os_hud centre ", "os_hud release", "css_guide ", "os_notice ", "os_fun ", "os_catchup ",
                "sv_disable_radar", "os_mode ", "game_type ", "game_mode ", "changelevel ")
CHANGELEVEL_ALLOWED = ("ar_shoots", "ar_pool_day", "ar_shoots_night", "de_dust2", "de_inferno")   # official maps: the client loads them without CS2's download dialog
CON_ALLOWED = re.compile(r"^(say !?[\w !]+|say \d{7,12}|jointeam [123] 1|[+-]showscores|disconnect|connect \S+|status)$")
BRIDGE_BUTTONS = re.compile(r"^(os_b_next|os_b_vup|os_b_vdown|os_btn_up|os_btn_down|os_m\d+_(play|up|down)|os_why_\d+|os_set_time_\d+|os_v_random|os_v_browse|os_v_back)$")
BRIDGE_SAYS = re.compile(r"^say !(play|up|down|next|map)\b")
NEVER_BUTTONS = ("os_ws_play",)  # downloads to the server


def real_name_in(text):
    low = text.lower()
    return any(n in low for n in REAL_NAMES)


def all_steps(row):
    return [*row.steps, *row.cleanup]


def validate():
    """Everything that can be held true offline: the 48 picks, the button ids against the COMMITTED layout, the words the stand-ins use against the committed code,
    what the table may and may not send. A picks file that cannot be read stops here; everything else comes back in info["problems"]."""
    errors = []
    text = PICKS.read_text(encoding="utf-8")
    picks = parse_picks(text)
    by_name = {p.name: p for p in picks}
    if len(by_name) != len(picks):
        errors.append("the picks file names a message twice")
    ids = [r.id for r in ROWS]
    for i in {x for x in ids if ids.count(x) > 1}:
        errors.append(f"row id twice: {i}")
    by_id = {r.id: r for r in ROWS}
    seen_before = set()
    for r in ROWS:
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", r.id):
            errors.append(f"{r.id}: not a file-safe id")
        if r.status not in STATUSES:
            errors.append(f"{r.id}: status {r.status}")
        if r.pick and r.pick not in by_name:
            errors.append(f"{r.id}: pick {r.pick!r} is not in the picks file")
        if not r.pick and r.section not in SECTIONS:
            errors.append(f"{r.id}: no pick and no known section")
        if r.after and r.after not in seen_before:
            errors.append(f"{r.id}: after {r.after}, which does not come before it")
        seen_before.add(r.id)
        if r.status == READY:
            if not any(s.kind == "shoot" for s in r.steps):
                errors.append(f"{r.id}: ready, and takes no shot")
            if not r.when:
                errors.append(f"{r.id}: ready, and no word on when it is shot")
            if r.names.startswith("real name") and not (r.masks or r.chat or r.renamed):
                errors.append(f"{r.id}: would show a real name, and has no mask")
            if r.after and by_id[r.after].status != READY:
                errors.append(f"{r.id}: after {r.after}, which is not ready")
            if r.after and by_id[r.after].bridge and not r.bridge:
                errors.append(f"{r.id}: after {r.after}, which needs --bridge-copy, and does not say so itself")
        else:
            if not r.reason:
                errors.append(f"{r.id}: {r.status} without a reason")
            if r.steps:
                errors.append(f"{r.id}: {r.status} with steps")
    missing = [p.name for p in picks if not any(r.pick == p.name for r in ROWS)]
    for name in missing:
        errors.append(f"a pick without a row: {name}")

    layout = head_file(LAYOUT)
    if layout is None:
        raise SystemExit(f"git show HEAD:{LAYOUT} failed: the button ids cannot be checked against the committed layout")
    buttons = set(re.findall(r"<Button\b[^>]*?\bid=\"([^\"]+)\"", layout))
    named = set()
    for r in ROWS:
        restarts = 0
        for s in all_steps(r):
            if s.button:
                named.add(s.button)
                if s.button not in buttons:
                    errors.append(f"{r.id}: button {s.button} is not in the committed layout")
                if s.button in NEVER_BUTTONS:
                    errors.append(f"{r.id}: {s.button} leaves state on the server")
                if BRIDGE_BUTTONS.match(s.button) and not r.bridge:
                    errors.append(f"{r.id}: {s.button} writes the bridge's data or opens a vote, and the row does not say so")
                if s.button == "os_set_restart":
                    restarts += 1
            if s.kind in ("rcon", "rcon_bg"):
                if not s.text.startswith(RCON_ALLOWED):
                    errors.append(f"{r.id}: an RCON command outside the allowed set: {s.text[:60]}")
                if s.text.startswith("changelevel ") and s.text.split()[1] not in CHANGELEVEL_ALLOWED:
                    errors.append(f"{r.id}: changelevel to {s.text.split()[1]}")
                if len(s.text) > 500:
                    errors.append(f"{r.id}: an RCON line of {len(s.text)} characters: one line carries about 512 (HARNESS.md)")
                hit = re.match(r'os_notice \d+ "(.*)"$', s.text)
                if s.text.startswith("os_notice") and (not hit or '"' in hit.group(1) or len(hit.group(1)) > 240):
                    errors.append(f"{r.id}: a os_notice line the plugin would cut or misread")
            if s.kind == "con":
                if not CON_ALLOWED.match(s.text):
                    errors.append(f"{r.id}: a console command outside the allowed set: {s.text[:60]}")
                if BRIDGE_SAYS.match(s.text) and not r.bridge:
                    errors.append(f"{r.id}: {s.text} writes the bridge's data, and the row does not say so")
            if s.kind == "post":
                if s.text.split()[0] not in POST_VERBS:
                    errors.append(f"{r.id}: bridgepost.py has no verb {s.text.split()[0]}")
                if not r.bridge:
                    errors.append(f"{r.id}: a post, which only a candidate bridge may hear, and the row does not say so")
            if real_name_in(s.text):
                errors.append(f"{r.id}: a real name in a command")
        for m in r.masks:
            if real_name_in(m.text):
                errors.append(f"{r.id}: a real name in a mask")
        if real_name_in(r.shows + r.when + r.reason + r.today + r.names):
            errors.append(f"{r.id}: a real name in the row's words")
        later = [x.text.split() for x in r.cleanup if x.kind == "post"]
        for said in (x.text.split() for x in r.steps if x.kind == "post"):
            if said[0] == "restart" and ["restart-cancel", said[1]] not in later:
                errors.append(f"{r.id}: a restart announced, and no restart-cancel {said[1]} in its cleanup: no vote can start after it")
            if said[0] == "fake-on" and ["fake-off"] not in later and not any(x.after == r.id for x in ROWS):
                errors.append(f"{r.id}: fake players on, and no fake-off in its cleanup or a row after it")
            if said[:1] == ["act"] and said[2] == "restart" and ["quit-notice"] not in later:
                errors.append(f"{r.id}: a player's restart, and no quit-notice in its cleanup: the second click quits the server")
        if restarts > 1:
            errors.append(f"{r.id}: os_set_restart twice: the second click quits the server")
        if restarts and not any(s.kind == "pause" and s.seconds >= 31 for s in r.steps[:1]):
            errors.append(f"{r.id}: os_set_restart without the 31 s first")
        if r.words_from and not words_at_head(*r.words_from):
            errors.append(f"{r.id}: the words {r.words_from[1]!r} are no longer in the committed code under {r.words_from[0].split('/')[0]}/: the stand-in or the row is out of date")
    if real_name_in(ME):
        errors.append("the test name is a real name")
    content_level = CONTENT
    if CONTENT == "?" or LEVEL <= 0:
        errors.append("bridge/Shared/HudVersion.cs's Contents cannot be read: the rows fake the versions around its last line")
    if not any(b.startswith("os_hudver_") for b in buttons):
        errors.append("no version button (os_hudver_...) in the committed layout")
    info = {"picks": picks, "by_name": by_name, "buttons": buttons, "named": named, "commit": head_commit(), "content": content_level,
            "plugin": head_version("plugin/OverStrike2/OverStrike2.cs", r'Version = "([\d.]+)"'), "problems": errors}
    return info


def require_valid(info):
    if info["problems"]:
        raise SystemExit("VALIDATION FAILED at HEAD " + info["commit"] + ":\n  " + "\n  ".join(info["problems"]))


def warn_problems(info):
    if info["problems"]:
        print(f"PROBLEMS at HEAD {info['commit']}: the committed code has moved past this table; --selfcheck and the real run refuse until the rows follow:")
        for e in info["problems"]:
            print("  " + e)


# ---------------------------------------------------------------- the plan ------------------------------------------------------------------------------

@dataclass
class Entry:
    row: Row
    mode: str                # shoot, or setup: its steps run for its state, no pictures
    jump: bool = False       # the row before it in the plan is not its predecessor in the table: the window's state is not known


def build_plan(only, bridge_copy):
    by_id = {r.id: r for r in ROWS}
    notes = []
    wanted = None
    if only:
        wanted = set()
        for rid in only:
            if rid not in by_id:
                raise SystemExit(f"--only: no row {rid}")
            if by_id[rid].status != READY:
                notes.append(f"NOT RUN {rid} ({by_id[rid].status}): {by_id[rid].reason}")
            else:
                wanted.add(rid)
    needed = set()
    if wanted is not None:
        for rid in wanted:
            a = by_id[rid].after
            while a:
                needed.add(a)
                a = by_id[a].after
    plan, gone = [], set()
    previous_index = None
    for index, r in enumerate(ROWS):
        selected = wanted is None or r.id in wanted
        if not (selected or r.id in needed):
            continue
        if r.status != READY:
            continue
        if r.bridge and not bridge_copy:
            notes.append(f"SKIPPED {r.id}: it writes the bridge's data ({r.bridge}); only with --bridge-copy")
            gone.add(r.id)
            continue
        if r.renamed and not RENAMED:
            notes.append(f"SKIPPED {r.id}: its own words name the client, where no mask can go; only with --renamed")
            gone.add(r.id)
            continue
        if r.after and r.after in gone:
            notes.append(f"SKIPPED {r.id}: after {r.after}, skipped")
            gone.add(r.id)
            continue
        jump = previous_index is not None and previous_index != index - 1 or previous_index is None
        plan.append(Entry(r, "shoot" if selected else "setup", jump and not r.after))
        previous_index = index
    return plan, notes


def frame_names(row):
    """The frames a row writes, in order."""
    total = sum(s.n for s in row.steps if s.kind == "shoot")
    return [row.id] if total == 1 else [f"{row.id}-{i + 1:02d}" for i in range(total)]


def harness_argv(*verb_args):
    if verb_args[0] not in ALL_VERBS:
        raise AssertionError(f"verb {verb_args[0]}: this script drives only {ALL_VERBS}")
    return ["pwsh", "-NoProfile", "-File", str(HARNESS), *verb_args]


def shot_argv(name, raw):
    """Full size, always: -MaxWidth 0. The harness's default of 1920 stored the last run's 2560 and 3840 shots 1920 wide, and the owner reviews on a 4K screen."""
    return harness_argv("shot", "-Name", name, "-MaxWidth", "0", "-OutDir", str(raw))


def rcon_argv(cmd):
    return harness_argv("rcon", "-Rcon", cmd)


def con_argv(cmd, wait_ms):
    return harness_argv("cmd", "-Cmd", cmd, "-WaitMs", str(wait_ms))


def post_argv(text):
    return [sys.executable, "-I", str(BRIDGEPOST), *text.split()]


START_COMMAND = "pwsh -NoProfile -File {h} start -Connect default -HudLocal -IncludeThumbs -AllowServerLock -Console -Resolution {res} -MaxMinutes 120 -Purpose \"docshots {res}\""


# ---------------------------------------------------------------- pictures -------------------------------------------------------------------------------

_FONTS: dict = {}


def font_for(size, bold=True):
    from PIL import ImageFont
    size = max(6, round(size))
    key = (size, bold)
    if key not in _FONTS:
        try:
            f = ImageFont.truetype(r"C:\Windows\Fonts\bahnschrift.ttf", size)   # Windows' Bahnschrift: the nearest stand-in for the HUD's Stratum2
            try:
                f.set_variation_by_name("Bold" if bold else "Regular")
            except (OSError, ValueError):
                pass
        except OSError:
            f = ImageFont.load_default()
        _FONTS[key] = f
    return _FONTS[key]


def chat_zone(w, h, window_open, y1=975):
    """Where chat is: the left of the screen, up to the window's edge while the window is open, which covers the rest of it."""
    s = h / 1080
    right = 620 * s
    if window_open == "vote":
        right = min(right, place(Box(80, 0, 81, 1, "v"), w, h)[0])
    elif window_open:
        right = min(right, w / 2 - 620 * s)
    return (0, round(740 * s), max(1, round(right)), round(y1 * s))


def smear(im, box, s):
    from PIL import Image, ImageFilter
    x0, y0, x1, y1 = box
    if x1 <= x0 or y1 <= y0:
        return
    part = im.crop(box)
    w, h = part.size
    small = part.resize((max(1, w // 8), max(1, h // 8)), Image.BOX)
    im.paste(small.resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(max(2, 3 * s))), box)


def fill_box(im, box):
    """What was under the words, as near as can be: each column from the pixel above the box to the pixel below it, so the line's other words never smear in."""
    x0, y0, x1, y1 = box
    px = im.load()
    h = y1 - y0
    top, bottom = max(0, y0 - 1), min(im.size[1] - 1, y1)
    for x in range(x0, x1):
        a, b = px[x, top], px[x, bottom]
        for y in range(y0, y1):
            f = (y - y0 + 1) / (h + 1)
            px[x, y] = (round(a[0] + (b[0] - a[0]) * f), round(a[1] + (b[1] - a[1]) * f), round(a[2] + (b[2] - a[2]) * f))


def paint_name(im, box, mask, s):
    from PIL import ImageDraw
    x0, y0, x1, y1 = box
    if x1 <= x0 or y1 <= y0:
        return
    fill_box(im, box)
    d = ImageDraw.Draw(im)
    font = font_for(mask.size * s, mask.bold)
    spacing = mask.spacing * s
    width = sum(font.getlength(c) + spacing for c in mask.text)
    x = x0 + max(1, 3 * s) if mask.align == "l" else x0 + (x1 - x0 - width) / 2
    y = y0 + (y1 - y0) / 2
    for c in mask.text:
        d.text((x, y), c, fill=mask.color, font=font, anchor="lm")
        x += font.getlength(c) + spacing


def compare_region(row, w, h, crop=False):
    """The part of a picture a retake is compared on, in its pixels: a crop file whole; a frame by its window, its big vote window, its crop, or
    whole when it has none of these."""
    if crop:
        b = place(row.crop, w, h) if row.crop else (0, 0, w, h)
        return (0, 0, b[2] - b[0], b[3] - b[1])
    if row.win == "vote":
        return place(VOTE_WINDOW, w, h)
    if row.win:
        return place(WINDOW, w, h)
    return place(row.crop, w, h) if row.crop else (0, 0, w, h)


def volatile_boxes(row, w, h, crop=False):
    """What differs on every shot in a picture of this row, in its pixels: its window kind's clocks and counters, and the row's own boxes. In a crop
    file the boxes are moved to the crop's corner and cut to it."""
    boxes = list(row.volatile) + ([V_BROWSER_HEAD] if row.id in BROWSER else [])
    if row.win == "vote":
        boxes += [V_VOTE_MAPCOUNT, V_VOTE_SUBTAB, C_CLOCKS, V_VOTE_TILES]
    elif row.win:
        boxes += [V_MAPCOUNT, V_IDLE]
    out = [place(b, w, h) for b in boxes]
    if crop and row.crop:
        cx0, cy0, cx1, cy1 = place(row.crop, w, h)
        out = [(max(x0, cx0) - cx0, max(y0, cy0) - cy0, min(x1, cx1) - cx0, min(y1, cy1) - cy0) for x0, y0, x1, y1 in out]
    return [b for b in out if b[2] > b[0] and b[3] > b[1]]


SAME_LEVEL, SAME_BLOCK = 48, 8    # a pixel differs by more than 48 of 255 in a channel; an 8x8 block counts when 8 of its 64 pixels do

# A crop's panel, for a retake's comparison: a crop over the game takes in a margin of the game around its panel, and the game differs in every run,
# so a retake compares the panel alone (same_picture's panel). PANEL_CENTRED: a box centred on the screen's middle - the top line's pill, the banners
# - found in each picture from a point of its flat background: its half-width where that colour ends on both sides (the nearer end, so a dark game
# beyond one side does not widen it), its top and bottom where the colour ends just inside both its ends; an end is PANEL_GAP pixels off the colour
# in a row, so a committed JPEG's noise does not end it early. A box of another width in the two pictures is a change; the rows compared are those
# both pictures' boxes cover (a banner's draining bar, a translucent edge). Where none is found at that point, the whole region decides.
# PANEL_FIXED: the panel's own rectangle where it never moves (the board, the reasons dialog). Only rows over the game: a window's crop is inside
# the window, where no game shows.
PANEL_CENTRED = ((C_TOP, Box(960, 156, 961, 157)), (C_BAN, Box(960, 156, 961, 157)))
PANEL_FIXED = ((C_BOARD, Box(1462, 241, 1851, 832)), (C_WHY, Box(742, 429, 1178, 648)))   # the board's last 16 px left out: its foot's height moved
PANEL_TOL, PANEL_INSET, PANEL_COLOUR, PANEL_MIN, PANEL_GAP = 4, 4, 36, 12, 4   # px at 1080: width difference allowed, the edge left out; a colour's spread;
                                                                                 # the least half-width; the pixels off the colour that end it


def panel_of(row, w, h, crop=False):
    """What a picture of this row is compared on inside its region: ("centred", (x, y), tol, inset) or ("fixed", box, tol, inset) in the picture's
    pixels (a crop file's from the crop's corner), or None: the whole region."""
    if row.crop is None or row.win:
        return None
    ox, oy = place(row.crop, w, h)[:2] if crop else (0, 0)
    s = h / 1080
    tol, inset = max(1, round(PANEL_TOL * s)), max(1, round(PANEL_INSET * s))
    for c, probe in PANEL_CENTRED:
        if row.crop == c:
            x, y = place(probe, w, h)[:2]
            return ("centred", (x - ox, y - oy), tol, inset)
    for c, box in PANEL_FIXED:
        if row.crop == c:
            b = place(box, w, h)
            return ("fixed", (b[0] - ox, b[1] - oy, b[2] - ox, b[3] - oy), tol, inset)
    return None


def find_panel(im, panel):
    """The panel's rectangle in a picture, or None when its point is not on a flat box: see PANEL_CENTRED."""
    kind, geo, tol, inset = panel
    if kind == "fixed":
        return geo
    pix = im.convert("RGB").load()
    w, h = im.size
    px, py = geo
    if not (0 <= px < w and 0 <= py < h):
        return None
    c = pix[px, py]
    same = lambda v: max(abs(v[0] - c[0]), abs(v[1] - c[1]), abs(v[2] - c[2])) <= PANEL_COLOUR

    def reach(x, y, dx, dy):
        """The last pixel of the colour from (x, y) on, stepping (dx, dy), before PANEL_GAP pixels in a row are off it."""
        last, off = (x, y), 0
        while 0 <= x + dx < w and 0 <= y + dy < h and off < PANEL_GAP:
            x, y = x + dx, y + dy
            if same(pix[x, y]):
                last, off = (x, y), 0
            else:
                off += 1
        return last
    left, right = reach(px, py, -1, 0)[0], reach(px, py, 1, 0)[0]
    half = min(px - left, right - px)
    if half < PANEL_MIN:
        return None
    x0, x1 = px - half, px + half + 1
    tops, bottoms = [], []
    for xv in (x0 + inset, x1 - 1 - inset):
        tops.append(reach(xv, py, 0, -1)[1])
        bottoms.append(reach(xv, py, 0, 1)[1])
    return (x0, max(tops), x1, min(bottoms) + 1)


def same_picture(new, old, region, volatile=(), panel=None):
    """Whether two pictures show the same within region, all but the volatile boxes: (True, words) or (False, where they differ). JPEG's noise and a
    translucent panel over a game world that differs behind it stay under SAME_LEVEL; a changed word, style or layout moves whole blocks. The game a
    crop shows around its panel, at full strength, does not: with a panel (panel_of) only the panel is compared, when both pictures have it at one
    place and size, else the whole region."""
    from PIL import Image, ImageChops, ImageDraw
    if new.size != old.size:
        return False, f"size {new.size[0]}x{new.size[1]}, committed {old.size[0]}x{old.size[1]}"
    inside = ""
    if panel:
        ra, rb = find_panel(new, panel), find_panel(old, panel)
        if ra and rb:
            if abs(ra[0] - rb[0]) > panel[2] or abs(ra[2] - rb[2]) > panel[2]:
                return False, f"the panel's width: {ra[0]} - {ra[2]}, committed {rb[0]} - {rb[2]}"
            ins = panel[3]
            r = (max(region[0], ra[0] + ins, rb[0] + ins), max(region[1], ra[1] + ins, rb[1] + ins),
                 min(region[2], ra[2] - ins, rb[2] - ins), min(region[3], ra[3] - ins, rb[3] - ins))
            if r[2] > r[0] and r[3] > r[1]:
                region, inside = r, " inside its panel"
    diff = ImageChops.difference(new.convert("RGB"), old.convert("RGB")).crop(region)
    level = diff.split()
    mask = ImageChops.lighter(ImageChops.lighter(level[0], level[1]), level[2]).point(lambda v: 255 if v > SAME_LEVEL else 0)
    if volatile:
        draw = ImageDraw.Draw(mask)
        for x0, y0, x1, y1 in volatile:
            draw.rectangle((x0 - region[0], y0 - region[1], x1 - region[0] - 1, y1 - region[1] - 1), fill=0)
    w, h = mask.size
    blocks = mask.resize((max(1, w // SAME_BLOCK), max(1, h // SAME_BLOCK)), Image.BOX)   # each block's share of differing pixels, 0-255
    hot = blocks.point(lambda v: 255 if v * 64 >= SAME_BLOCK * 255 else 0).getbbox()
    if hot is None:
        return True, f"the same{inside}, all but what differs on every shot"
    return False, f"differs{inside} at {region[0] + hot[0] * SAME_BLOCK},{region[1] + hot[1] * SAME_BLOCK} - {region[0] + hot[2] * SAME_BLOCK},{region[1] + hot[3] * SAME_BLOCK}"


def kept_masks(row):
    """The row's masks for this run: under --renamed only those that hide more than a name (CS2's scoreboard: its avatars) and those over a name the
    bridge has from the game's log (Mask.keep): OsDocName renames the client on the server, but the bridge learned its name at the connect."""
    return [m for m in row.masks if not RENAMED or m == BLUR_SCOREBOARD or m.keep]


def apply_masks(im, row):
    """The frame as it may be published: the avatar, chat (or what the row's chat mask says), the row's own; under --renamed the avatar and the scoreboard alone."""
    w, h = im.size
    s = h / 1080
    smear(im, place(row.avatar, w, h), s)
    smear(im, place(BUILD_INFO, w, h), s)
    if not row.chat and not RENAMED:
        smear(im, chat_zone(w, h, row.win), s)
    for m in kept_masks(row):
        if m.kind == "chat":
            smear(im, chat_zone(w, h, row.win, m.y1), s)
        elif m.kind == "blur":
            smear(im, mask_box(m, w, h), s)
        else:
            paint_name(im, mask_box(m, w, h), m, s)
    return im


def describe_post(row, w, h):
    parts = [f"blur avatar {place(row.avatar, w, h)}", f"blur build line {place(BUILD_INFO, w, h)}"]
    if not row.chat and not RENAMED:
        parts.append(f"blur chat {chat_zone(w, h, row.win)}")
    for m in kept_masks(row):
        if m.kind == "chat":
            parts.append(f"blur chat above its lines {chat_zone(w, h, row.win, m.y1)}")
        elif m.kind == "blur":
            parts.append(f"blur {mask_box(m, w, h)}")
        else:
            parts.append(f"paint {m.text!r} {mask_box(m, w, h)}")
    if row.crop:
        parts.append(f"crop {place(row.crop, w, h)}")
    return "; ".join(parts)


# The Steam overlay over the game (Shift+Tab, or opened by Steam itself: once it came up with the client's start and stayed in every frame of a
# session). It shows the account's own name and its store and community pages, so a frame with it is never kept. Steam's overlay of 2026 puts
# its Back to Game button at the screen's top right: a flat square of its own grey with a light X, where neither CS2's HUD nor ours draws anything
# like it. Tried on the 26 frames of a walk the overlay spoiled (all caught) and 624 clean frames of release 191's and 192's sessions (none taken).
OVERLAY_BUTTON = (35, 38, 46)   # the button's grey, #23262E
OVERLAY_TOL = 10                # per channel


def steam_overlay(im):
    """Where the Steam overlay's Back to Game button is in a frame, (x0, y0, x1, y1), or None. In the top right corner (the right 14 %, the top
    12 %): a square of the button's grey, 2 % to 8 % of the frame's height a side and at least half of it that grey, with light pixels in it of
    which most lie along its two diagonals - the X."""
    w, h = im.size
    x0, y1 = int(w * 0.86), int(h * 0.12)
    region = im.convert("RGB").crop((x0, 0, w, y1))
    rw, rh = region.size
    buf = region.tobytes()
    r, g, b = OVERLAY_BUTTON
    mask = [abs(buf[i] - r) <= OVERLAY_TOL and abs(buf[i + 1] - g) <= OVERLAY_TOL and abs(buf[i + 2] - b) <= OVERLAY_TOL for i in range(0, len(buf), 3)]
    seen = [False] * len(mask)
    lo, hi = 0.02 * h, 0.08 * h
    for start in range(len(mask)):
        if not mask[start] or seen[start]:
            continue
        stack, n = [start], 0
        seen[start] = True
        bx0 = bx1 = start % rw
        by0 = by1 = start // rw
        while stack:           # the grey's 4-connected piece
            i = stack.pop()
            n += 1
            x, y = i % rw, i // rw
            bx0, bx1, by0, by1 = min(bx0, x), max(bx1, x), min(by0, y), max(by1, y)
            for j, inside in ((i + 1, x + 1 < rw), (i - 1, x > 0), (i + rw, y + 1 < rh), (i - rw, y > 0)):
                if inside and mask[j] and not seen[j]:
                    seen[j] = True
                    stack.append(j)
        bw, bh = bx1 - bx0 + 1, by1 - by0 + 1
        if not (lo <= bw <= hi and lo <= bh <= hi and 0.8 <= bw / bh <= 1.25 and n >= 0.5 * bw * bh):
            continue
        light = [(x, y) for y in range(by0, by1 + 1) for x in range(bx0, bx1 + 1) if min(buf[(y * rw + x) * 3:(y * rw + x) * 3 + 3]) >= 170]
        if len(light) < 0.03 * bw * bh:
            continue
        cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
        if sum(1 for x, y in light if abs(abs(x - cx) - abs(y - cy)) <= 0.12 * bw) >= 0.7 * len(light):
            return (x0 + bx0, by0, x0 + bx1 + 1, by1 + 1)
    return None


def fake_overlay(base, side, margin, colour=OVERLAY_BUTTON):
    """A frame with the overlay's button drawn on it, as steam_overlay looks for it: the self-check's (both ports draw it pixel for pixel alike)."""
    im = base.copy().convert("RGB")
    w, h = im.size
    px = im.load()
    left, top = w - margin - side, margin
    k, t = side // 3, max(1, side // 16)
    for y in range(side):
        for x in range(side):
            dx, dy = x - (side - 1) / 2, y - (side - 1) / 2
            on_x = k <= x < side - k and k <= y < side - k and abs(abs(dx) - abs(dy)) <= t
            px[left + x, top + y] = (230, 233, 236) if on_x else colour
    return im


def post_frame(raw, row, out_dir):
    """Masks a raw frame, writes it and its crop at full size. Returns the file names."""
    from PIL import Image
    with Image.open(raw) as opened:
        im = opened.convert("RGB")
    apply_masks(im, row)
    out_dir.mkdir(parents=True, exist_ok=True)
    name = raw.stem + ".png"
    im.save(out_dir / name, optimize=False)
    crop = ""
    if row.crop:
        crop = raw.stem + "-crop.png"
        im.crop(place(row.crop, *im.size)).save(out_dir / crop)
    return name, crop


# ---------------------------------------------------------------- the index ------------------------------------------------------------------------------

STYLE = ("<style>body{font:14px system-ui;background:#111;color:#ddd;margin:24px}h2{color:#E9B44C;margin-top:32px}figure{display:inline-block;margin:8px;vertical-align:top;width:620px}"
         "img{width:620px;border:1px solid #333}figcaption{margin-top:4px}table{border-collapse:collapse}th,td{text-align:left;vertical-align:top;padding:3px 12px 3px 0}th{color:#E9B44C;white-space:nowrap}"
         "small{color:#99a}</style>")


def caption(row, by_name, res):
    words = f"<b>{html.escape(row.id)}</b> at {html.escape(res)}: {html.escape(row.shows)}"
    if row.pick:
        words += f"<br><small>Message: {html.escape(row.pick)}. Today: {html.escape(row.today)}. Picked: {html.escape(by_name[row.pick].surfaces)}</small>"
    if row.stand_in:
        words += "<br><small>STAND-IN: the message's real words on its panel, sent with os_notice; its real trigger would leave something behind.</small>"
    if row.names != NO_NAME:
        words += f"<br><small>Names: {html.escape(row.names)}</small>"
    return words


def write_index(out, info):
    """index.html over every resolution in the folder: relative links, nothing from outside."""
    manifests = {}
    for m in sorted(out.glob("*/manifest.json")):
        manifests[m.parent.name] = json.loads(m.read_text(encoding="utf-8"))
    by_name = info["by_name"]
    head = "; ".join(f"{res}: commit {man.get('commit')}, content {man.get('content')}, plugin {man.get('plugin')}, {man.get('taken')}" for res, man in manifests.items()) or "no run yet"
    parts = ['<!doctype html><meta charset=utf-8><title>Over Strike 2 - the whole HUD, documentation screenshots</title>', STYLE,
             f"<h1>Over Strike 2 - the whole HUD: real-client screenshots, insecure test copy, loose HUD files ({html.escape(head)})</h1>",
             "<p>Taken by docshots.py. Test names only: the avatar and chat are blurred on every frame, and the client's own name is painted over where a row says so. "
             "Review every picture before it goes into the public docs.</p>"]
    order = [*SECTIONS]
    for p in info["picks"]:
        sec = "Messages: " + p.heading[0] + p.heading[1:].lower()
        if sec not in order:
            order.append(sec)
    shot_rows = {}
    for r in ROWS:
        shot_rows.setdefault(section_of(r, by_name), []).append(r)
    failed = []
    for sec in order:
        figures = []
        for r in shot_rows.get(sec, []):
            for res, man in manifests.items():
                got = man.get("rows", {}).get(r.id)
                if not got:
                    continue
                if got.get("failed"):
                    failed.append((r.id, res, got["failed"]))
                for f in got.get("frames", []):
                    for name in filter(None, (f.get("file"), f.get("crop"))):
                        rel = f"{res}/{name}"
                        figures.append(f'<figure><a href="{html.escape(rel, quote=True)}"><img src="{html.escape(rel, quote=True)}"></a><figcaption>{caption(r, by_name, res)}</figcaption></figure>')
        if figures:
            parts.append(f"<h2>{html.escape(sec)}</h2>")
            parts.extend(figures)
    rows = [r for r in ROWS if r.status != READY]
    parts.append("<h2>What is not here, and why</h2><table>")
    for r in rows:
        parts.append(f"<tr><th>{html.escape(r.id)}</th><td>{html.escape(r.status)}{(' - ' + html.escape(r.pick)) if r.pick else ''}: {html.escape(r.reason)}</td></tr>")
    for rid, res, why in failed:
        parts.append(f"<tr><th>{html.escape(rid)}</th><td>FAILED at {html.escape(res)}: {html.escape(why)}</td></tr>")
    parts.append("</table>")
    (out / "index.html").write_text("\n".join(parts), encoding="utf-8")


# ---------------------------------------------------------------- the two runners: printing and doing ------------------------------------------------------

class RowFailed(Exception):
    pass


class OverlayOpen(Exception):
    """The Steam overlay in a frame (steam_overlay): the run stops, because it stays until the client starts again and every frame would show it."""


class Printer:
    """What a run would execute, line for line, touching nothing: the same walk as Doer's (walk below)."""

    def __init__(self, res, out, stream=None):
        self.res = res
        self.w, self.h = parse_res(res)
        self.raw = out / res / "_raw"
        self.stream = stream or sys.stdout
        self.clock = 0.0
        self.busy = 0.0
        self.closed_at = 0.0
        self.at = {"join": None, "vote": None, "closed": None, "press": None}
        self.frames = 0

    def say(self, text=""):
        print(text, file=self.stream)

    def cmdline(self, argv):
        return subprocess.list2cmdline(argv)

    def preflight(self):
        self.say(f"GATE (the run refuses otherwise): {LOCK} exists and starts with '{OWNER_LINE}'; A2S on {HOST}:{PORT} says VAC off, at most 1 human, map {START_MAP}")
        if RENAMED:
            self.say(f"  $ {self.cmdline(rcon_argv('osd_name'))}    --renamed: must say the client goes by {ME}, and is")
            self.clock += T_RCON
        self.say(f"PREFLIGHT: the client started by hand: {START_COMMAND.format(h=HARNESS, res=self.res)}")
        self.say(f"  $ {self.cmdline(con_argv('status', 2000))}    must name {HOST}:{PORT} and insecure")
        self.say(f"  $ {self.cmdline(shot_argv('_probe', self.raw))}    the frame must be {self.res}, not scaled")
        self.say(f"  $ {self.cmdline(rcon_argv('os_hud'))}    must have an entity, the bridge ok, no vote on")
        self.say(f"  $ {self.cmdline(con_argv('jointeam 2 1', 1500))}")
        self.say(f"  $ {self.cmdline(rcon_argv(f'os_hud content {ACCOUNT} {LEVEL}'))}    which HUD this game holds, so the window's X is the plain close (a real join forgets it: the first row)")
        self.say(f"  $ {self.cmdline(rcon_argv('mp_restartgame 1'))}    a fresh match: an Arms Race match ends after 60 minutes, and its end reloads the map under the run")
        self.clock += T_CON + T_SHOT + T_RCON + T_CON + T_RCON + T_RCON

    def begin(self, entry):
        r = entry.row
        self.say(f"== {r.id}{' (setup only, no picture)' if entry.mode == 'setup' else ''}: {r.shows}")

    def rcon(self, s):
        self.clock = max(self.clock, self.busy) + T_RCON
        self.say(f"  $ {self.cmdline(rcon_argv(s.text))}" + (f"    reply must match /{s.expect}/" if s.expect else ""))

    def rcon_bg(self, s):
        self.clock = max(self.clock, self.busy)
        self.busy = self.clock + T_RCON
        self.say(f"  (background, waited for before the next RCON call) $ {self.cmdline(rcon_argv(s.text))}" + (f"    reply must match /{s.expect}/" if s.expect else ""))

    def con(self, s):
        self.clock += T_CON + s.wait_ms / 1000
        if s.text.startswith("jointeam"):
            self.at["join"] = self.clock
        self.say(f"  $ {self.cmdline(con_argv(s.text, s.wait_ms))}")

    def post(self, s):
        self.clock += T_POST
        self.say(f"  $ {self.cmdline(post_argv(s.text))}    a candidate bridge on a test copy or it refuses; a refusal fails the row")

    def until(self, s):
        at = self.at[s.text]
        if at is not None:
            self.clock = max(self.clock, at + s.seconds)
        self.say(f"  sleep until {s.seconds:g} s after {ANCHORS[s.text]}" + ("" if at is not None else " (not yet in this plan: the row fails)"))

    def vote_opened(self):
        self.at["vote"] = self.clock
        self.say("  (the vote has opened: its clocks count from here)")

    def pressed(self):
        self.at["press"] = self.clock
        self.say("  (the press is marked: what it starts counts from here)")

    def pause(self, seconds):
        self.clock += seconds
        self.say(f"  sleep {seconds:g}")

    def mark(self):
        self.clock += T_LOG
        self.say(f"  $ {self.cmdline(harness_argv('log', '-Tail', '1'))}    its 'lines: N' is the mark")

    def wait_log(self, s):
        self.clock += T_WAIT_LOG
        self.say(f"  every 3 s, up to {s.seconds:g} s: $ {self.cmdline(harness_argv('log', '-Tail', '1', '-Pattern', s.text, '-FromLine', '<mark>'))}")

    def vote_gap(self):
        wait = max(0.0, self.closed_at + VOTE_GAP - self.clock)
        self.clock += wait
        self.say(f"  sleep until {VOTE_GAP} s after the last vote closed (MapVote.WaitSeconds, about {wait:.0f} s here)")

    def vote_closed(self):
        self.closed_at = self.at["closed"] = self.clock
        self.say("  (the vote has closed: the wait starts)")

    def guard(self, s):
        self.say(f"  refuse the row unless the last vote closed less than {s.seconds:g} s ago")

    def shoot(self, s, names, row):
        for i, name in enumerate(names):
            self.say(f"  {'(side by side) ' if s.parallel else ''}$ {self.cmdline(shot_argv(name, self.raw))}")
            self.say(f"    then: {describe_post(row, self.w, self.h)}")
            self.frames += 1
            if s.parallel:
                self.clock += s.gap
            else:
                self.clock += T_SHOT
                if s.gap and i < len(names) - 1:
                    self.clock += s.gap
        if s.parallel:
            self.clock += T_SHOT

    def setup_shoot(self, s):
        self.pause(s.n * T_SHOT if not s.parallel else s.n * s.gap + T_SHOT)

    def fail(self, entry, why):
        pass

    def finish_row(self, entry):
        pass

    def final(self):
        self.say("== at the end, whatever happened: back to neutral")
        for cmd in FINAL_COMMANDS:
            self.say(f"  $ {self.cmdline(rcon_argv(cmd))}")
            self.clock += T_RCON
        self.say(f"  $ {self.cmdline(con_argv('-showscores', 500))}")


FINAL_COMMANDS = ("os_hud release", "os_hud level 0", f"os_hud content {ACCOUNT} {LEVEL}", f'os_fun reset "{ME}"')


SECRETISH = (re.compile(r"(/log/|/hud/|/act/|/health/|/dash/|/search/|/catalog/|/restart-notice/|/updated/)[0-9A-Za-z_-]{8,}"),
             re.compile(r"(?i)\b(rcon_password|netconpassword|password|pass)\b\s*[=: ]\s*\S+"),
             re.compile(r"\b[0-9a-fA-F]{24,}\b"))


def scrub(text):
    """No secret, name or address leaves through what this script prints or logs."""
    for rx in SECRETISH:
        text = rx.sub(lambda m: (m.group(1) if m.lastindex else "") + "<hidden>", text)
    for n in REAL_NAMES:
        text = re.sub(re.escape(n), "<player>", text, flags=re.I)
    return text.replace(ACCOUNT, "<account>").replace(HOST, "<server>")


def local_paths_out(text):
    """The text without this PC's own folder names (the repository, the lock file's, the Python install, the home folder), which may hold a user name."""
    paths = {str(OS2), str(probe.main_checkout(OS2)), sys.executable, str(Path(sys.executable).parent), str(Path.home())} | ({str(LOCK), str(LOCK.parent)} if LOCK else set())
    for path in sorted(paths, key=len, reverse=True):
        text = text.replace(path, "")
    return text


class Doer:
    """The real thing: each verb a process of its own, its output in a file, not a pipe (a child that inherits a pipe can keep the call open past its timeout)."""

    def __init__(self, res, out, keep_raw):
        self.res = res
        self.w, self.h = parse_res(res)
        self.out = out
        self.res_dir = out / res
        self.raw = self.res_dir / "_raw"
        self.tmp = self.res_dir / "_tmp"
        self.keep_raw = keep_raw
        self.raw.mkdir(parents=True, exist_ok=True)
        self.tmp.mkdir(parents=True, exist_ok=True)
        self.logf = open(self.res_dir / "run.log", "a", encoding="utf-8")
        self.count = 0
        self.bg = None
        self.mark_line = 0
        self.closed_at = time.time()      # a run may follow another's vote: the first vote_gap waits it out
        self.at = {"join": None, "vote": None, "closed": None, "press": None}   # the anchors of until (ANCHORS), as this run met them
        self.vote_seen = False
        self.frames: dict = {}
        self.failed: dict = {}
        self.current = None

    def say(self, text=""):
        print(scrub(text), flush=True)
        self.log(text)

    def log(self, text):
        self.logf.write(f"{time.strftime('%H:%M:%S')} {scrub(text)}\n")
        self.logf.flush()

    def launch(self, argv):
        self.count += 1
        path = self.tmp / f"{self.count:05d}.txt"
        f = open(path, "wb")
        p = subprocess.Popen(argv, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        f.close()
        return p, path

    def collect(self, p, path, timeout, label):
        try:
            code = p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True, stdin=subprocess.DEVNULL)
            raise RowFailed(f"{label}: no answer in {timeout} s")
        text = path.read_bytes().decode("utf-8", "replace").replace("\0", "")
        path.unlink(missing_ok=True)
        self.log(f"{label} -> exit {code}: {text.strip()[:600]}")
        if code != 0 or text.lstrip().startswith("ERROR:"):
            raise RowFailed(f"{label}: exit {code}: {scrub(text.strip())[:300]}")
        return text

    def run_verb(self, argv, timeout, label):
        p, path = self.launch(argv)
        return self.collect(p, path, timeout, label)

    def join_bg(self):
        if self.bg:
            p, path, label, expect = self.bg
            self.bg = None
            text = self.collect(p, path, 60, label)
            if expect and not re.search(expect, text):
                raise RowFailed(f"{label}: the reply does not say /{expect}/: {scrub(text.strip())[:300]}")

    def rcon(self, s):
        self.join_bg()
        text = self.run_verb(rcon_argv(s.text), 60, f"rcon {s.text}")
        if s.expect and not re.search(s.expect, text):
            raise RowFailed(f"rcon {s.text}: the reply does not say /{s.expect}/: {scrub(text.strip())[:300]}")
        return text

    def rcon_bg(self, s):
        self.join_bg()
        p, path = self.launch(rcon_argv(s.text))
        self.bg = (p, path, f"rcon {s.text}", s.expect)

    def con(self, s):
        text = self.run_verb(con_argv(s.text, s.wait_ms), 30, f"cmd {s.text}")
        if s.text.startswith("jointeam"):
            self.at["join"] = time.time()
        return text

    def post(self, s):
        return self.run_verb(post_argv(s.text), 60, f"post {s.text}")

    def until(self, s):
        at = self.at[s.text]
        if at is None:
            raise RowFailed(f"until: {ANCHORS[s.text]} has not happened in this run")
        wait = at + s.seconds - time.time()
        if wait > 0:
            time.sleep(wait)

    def vote_opened(self):
        self.at["vote"] = time.time()

    def pressed(self):
        self.at["press"] = time.time()

    def pause(self, seconds):
        time.sleep(seconds)

    def mark(self):
        text = self.run_verb(harness_argv("log", "-Tail", "1"), 30, "log")
        m = re.search(r"lines: (\d+)", text)
        self.mark_line = int(m.group(1)) if m else 0

    def wait_log(self, s):
        end = time.time() + s.seconds
        while time.time() < end:
            text = self.run_verb(harness_argv("log", "-Tail", "1", "-Pattern", s.text, "-FromLine", str(self.mark_line)), 30, "log")
            if re.search(r"^\s*\d+: ", text, re.M):
                return
            time.sleep(3)
        raise RowFailed(f"no /{s.text}/ in the client's log in {s.seconds:g} s")

    def vote_gap(self):
        self.join_bg()
        wait = self.closed_at + VOTE_GAP - time.time()
        if wait > 0:
            self.say(f"  waiting {wait:.0f} s: the wait after a vote")
            time.sleep(wait)

    def vote_closed(self):
        self.closed_at = self.at["closed"] = time.time()
        self.vote_seen = True

    def guard(self, s):
        if not self.vote_seen or time.time() - self.closed_at >= s.seconds:
            raise RowFailed(f"the guard: no vote closed in the last {s.seconds:g} s, so this click could open one")

    def check_frame(self, text, name):
        m = re.search(r"ok (\d+)x(\d+) \(from (\d+)x(\d+)\)", text)
        if not m:
            raise RowFailed(f"shot {name}: no size in its answer: {scrub(text.strip())[:200]}")
        size = f"{m.group(1)}x{m.group(2)}"
        if size != self.res:
            raise RowFailed(f"shot {name}: stored at {size}, the run is at {self.res}: nothing may be scaled")
        self.no_overlay(name)
        return "WARNING" in text

    def no_overlay(self, name):
        """A frame the Steam overlay covers is deleted at once - it shows the account's name - and the run stops (OverlayOpen)."""
        from PIL import Image
        path = self.raw / f"{name}.png"
        if not path.exists():
            return
        with Image.open(path) as im:
            box = steam_overlay(im)
        if box:
            path.unlink(missing_ok=True)
            self.log(f"shot {name}: the Steam overlay's Back to Game button at {box} - the frame is deleted")
            raise OverlayOpen(f"the Steam overlay is over the game in the frame {name} (its Back to Game button at {box}): that frame is deleted, it shows "
                              f"the account. Stop the client, start it again (client.sh start {self.res}) and run the rows not done (--only)")

    def shoot(self, s, names, row):
        got = self.frames.setdefault(row.id, [])
        if s.parallel:
            procs = []
            for name in names:
                procs.append((name, *self.launch(shot_argv(name, self.raw))))
                time.sleep(s.gap)
            lost, overlay = 0, None
            for name, p, path in procs:
                try:
                    self.check_frame(self.collect(p, path, 60, f"shot {name}"), name)
                    got.append(name)
                except OverlayOpen as e:      # the others still collected, and each of their frames looked at, before the run stops
                    overlay = overlay or e
                except RowFailed as e:
                    lost += 1
                    self.log(f"a frame lost: {e}")
            if overlay:
                raise overlay
            if lost == len(names):
                raise RowFailed("every side-by-side frame was lost")
            return
        for i, name in enumerate(names):
            text = self.run_verb(shot_argv(name, self.raw), 60, f"shot {name}")
            if self.check_frame(text, name):         # a black frame: a map loading
                time.sleep(3)
                self.check_frame(self.run_verb(shot_argv(name, self.raw), 60, f"shot {name} again"), name)
            got.append(name)
            if s.gap and i < len(names) - 1:
                time.sleep(s.gap)

    def setup_shoot(self, s):
        time.sleep(s.n * T_SHOT if not s.parallel else s.n * s.gap + T_SHOT)

    def begin(self, entry):
        self.current = entry.row.id
        self.say(f"== {entry.row.id}{' (setup only)' if entry.mode == 'setup' else ''}")

    def fail(self, entry, why):
        self.failed[entry.row.id] = str(why)
        self.say(f"   FAILED {entry.row.id}: {why}")

    def finish_row(self, entry):
        pass

    def preflight(self):
        text = self.run_verb(con_argv("status", 2000), 30, "cmd status")
        if f"{HOST}:{PORT}" not in text or "insecure" not in text:
            raise SystemExit("REFUSED: the client is not an insecure session in game on the test copy (cmd status). Start it by hand:\n  " + START_COMMAND.format(h=HARNESS, res=self.res))
        text = self.run_verb(shot_argv("_probe", self.raw), 60, "shot _probe")
        m = re.search(r"ok (\d+)x(\d+) \(from (\d+)x(\d+)\)", text)
        if not m or f"{m.group(1)}x{m.group(2)}" != self.res or f"{m.group(3)}x{m.group(4)}" != self.res:
            raise SystemExit(f"REFUSED: the client's frame is not {self.res}: {scrub(text.strip())[:200]}\nStart it at that size:\n  " + START_COMMAND.format(h=HARNESS, res=self.res))
        self.no_overlay("_probe")
        if RENAMED:
            text = self.run_verb(rcon_argv("osd_name"), 60, "rcon osd_name")
            if f"{ACCOUNT} goes by {ME}" not in text or f"{ACCOUNT} is {ME}" not in text:
                raise SystemExit(f"REFUSED: --renamed, and the server does not call the client {ME} (OsDocName on the test copy, osd_name {ACCOUNT} {ME}): {scrub(text.strip())[:200]}")
        text = self.run_verb(rcon_argv("os_hud"), 60, "rcon os_hud")
        if not re.search(r"entity \d+", text) or "bridge ok" not in text:
            raise SystemExit(f"REFUSED: the HUD is not up on the server: {scrub(text.strip())[:400]}")
        if re.search(r"vote round \d+ open", text):
            raise SystemExit("REFUSED: a vote is open")
        self.run_verb(con_argv("jointeam 2 1", 1500), 30, "cmd jointeam")
        self.run_verb(rcon_argv(f"os_hud content {ACCOUNT} {LEVEL}"), 60, "rcon os_hud content")
        self.run_verb(rcon_argv("mp_restartgame 1"), 60, "rcon mp_restartgame 1")

    def final(self):
        self.say("== back to neutral")
        try:
            self.join_bg()
        except RowFailed as e:
            self.log(f"final: {e}")
        for cmd in FINAL_COMMANDS:
            try:
                self.run_verb(rcon_argv(cmd), 60, f"rcon {cmd}")
            except RowFailed as e:
                self.say(f"   the end's {cmd}: {e}")
        try:
            self.run_verb(con_argv("-showscores", 500), 30, "cmd -showscores")
        except RowFailed as e:
            self.log(f"final: {e}")

    def write_pictures(self, plan, info):
        rows = {}
        by_id = {e.row.id: e.row for e in plan}
        for rid, names in self.frames.items():
            row = by_id[rid]
            frames = []
            for name in names:
                raw = self.raw / f"{name}.png"
                if not raw.exists():
                    continue
                file, crop = post_frame(raw, row, self.res_dir)
                frames.append({"file": file, "crop": crop})
            rows[rid] = {"frames": frames}
        for rid, why in self.failed.items():
            rows.setdefault(rid, {"frames": []})["failed"] = why
        (self.res_dir / "manifest.json").write_text(json.dumps({"res": self.res, "commit": info["commit"], "content": info["content"], "plugin": info["plugin"],
                                                                "taken": time.strftime("%Y-%m-%d %H:%M"), "renamed": RENAMED, "built": BUILT, "rows": rows}, indent=1), encoding="utf-8")
        write_index(self.out, info)
        if not self.keep_raw:
            shutil.rmtree(self.raw, ignore_errors=True)
        shutil.rmtree(self.tmp, ignore_errors=True)
        self.logf.close()


def run_step(runner, s, row, mode, names):
    if s.kind == "rcon":
        runner.rcon(s)
    elif s.kind == "rcon_bg":
        runner.rcon_bg(s)
    elif s.kind == "con":
        runner.con(s)
    elif s.kind == "post":
        runner.post(s)
    elif s.kind == "pause":
        runner.pause(s.seconds)
    elif s.kind == "mark":
        runner.mark()
    elif s.kind == "wait_log":
        runner.wait_log(s)
    elif s.kind == "vote_gap":
        runner.vote_gap()
    elif s.kind == "vote_closed":
        runner.vote_closed()
    elif s.kind == "vote_opened":
        runner.vote_opened()
    elif s.kind == "pressed":
        runner.pressed()
    elif s.kind == "until":
        runner.until(s)
    elif s.kind == "guard":
        runner.guard(s)
    elif s.kind == "shoot":
        if mode == "setup":
            runner.setup_shoot(s)
        else:
            runner.shoot(s, [names.pop(0) for _ in range(s.n)], row)
    else:
        raise AssertionError(s.kind)


def walk(runner, plan):
    """The one walk through a plan: the printer and the real run go through the same code, so the dry run prints what the run does."""
    runner.preflight()
    failed = set()
    try:
        for e in plan:
            r = e.row
            if r.after and r.after in failed:
                runner.say(f"== {r.id}: skipped: {r.after} failed")
                failed.add(r.id)
                continue
            runner.begin(e)
            names = frame_names(r)
            try:
                if e.jump and not r.win:
                    run_step(runner, RELEASE, r, e.mode, names)
                for s in r.steps:
                    run_step(runner, s, r, e.mode, names)
            except RowFailed as why:
                runner.fail(e, why)
                failed.add(r.id)
            for s in r.cleanup:
                try:
                    run_step(runner, s, r, e.mode, names)
                except RowFailed as why:
                    runner.say(f"   {r.id}: its cleanup: {why}")
            runner.finish_row(e)
    finally:
        runner.final()
    return failed


# ---------------------------------------------------------------- the gate ------------------------------------------------------------------------------

def a2s(host=HOST, port=PORT, timeout=3.0):
    """A2S_INFO: (name, map, humans, vac), or None when it does not answer (testcopy.py's)."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(timeout)
    q = b"\xff\xff\xff\xffTSource Engine Query\x00"
    try:
        s.sendto(q, (host, port))
        d = s.recv(1400)
        if d[4] == 0x41:
            s.sendto(q + d[5:9], (host, port))
            d = s.recv(1400)
        i, f = 6, []
        for _ in range(4):
            j = d.index(b"\0", i)
            f.append(d[i:j].decode(errors="replace"))
            i = j + 1
        i += 2
        return f[0], f[1], d[i] - d[i + 2], d[i + 6]
    except Exception:
        return None
    finally:
        s.close()


def gate(read_lock, ask_a2s):
    """(True, what it found) or (False, why not). The lock first: no packet goes out without it."""
    if not STEAM_ACCOUNT:
        return False, "REFUSED: Steam names no logged-in account on this PC: the rows would address a made-up client"
    text = read_lock()
    if text is None:
        return False, "REFUSED: the server lock does not exist: it is not this harness's test (probe.py lock)"
    if not text.startswith(OWNER_LINE):
        return False, f"REFUSED: the server lock is not the harness's: its first line is: {text.splitlines()[0][:120] if text else '(empty)'}"
    info = ask_a2s()
    if info is None:
        return False, "REFUSED: A2S does not answer: the test copy cannot be proven to be VAC off"
    name, map_name, humans, vac = info
    if vac != 0:
        return False, f"REFUSED: A2S says VAC on (server {name!r}): that is production, not the test copy"
    if humans > 1:
        return False, f"REFUSED: {humans} humans on the test copy: a real player's name could reach a picture"
    if map_name != START_MAP:
        return False, f"REFUSED: the map is {map_name}, the rows expect {START_MAP} (official, so no download dialog): rcon changelevel {START_MAP} first"
    return True, f"A2S: {name!r}, {map_name}, {humans} human(s), VAC off"


def read_lock_file(path=None):
    path = path or LOCK
    if path is None:
        return None     # no private config, or no server.lock_file in it: no lock can be this harness's
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None


# ---------------------------------------------------------------- the commands -------------------------------------------------------------------------

def cmd_list(info):
    print(f"HEAD {info['commit']}: content {info['content']}, plugin {info['plugin']}. The message list: {PICKS.name}")
    print(f"Layout checked at HEAD: {LAYOUT}, {len(info['buttons'])} buttons; the table names {len(info['named'])}, all there.")
    by_name = info["by_name"]
    for r in ROWS:
        print(f"{r.id}  [{r.status}]{'  [needs --bridge-copy]' if r.bridge else ''}{'  [stand-in]' if r.stand_in else ''}{'  after ' + r.after if r.after else ''}")
        print(f"    shows: {r.shows}")
        if r.pick:
            print(f"    pick: {r.pick} [{by_name[r.pick].bracket}]")
            print(f"    today: {r.today}")
            print(f"    picked: {by_name[r.pick].surfaces}")
        elif r.today:
            print(f"    today: {r.today}")
        if r.status == READY:
            print(f"    names: {r.names}")
            print(f"    when: {r.when}")
            print("    how: " + "; ".join(step_words(s) for s in r.steps))
            if r.cleanup:
                print("    cleanup: " + "; ".join(step_words(s) for s in r.cleanup))
            if r.crop:
                print(f"    crop at 1080p: {r.crop.x0:g},{r.crop.y0:g} - {r.crop.x1:g},{r.crop.y1:g} ({r.crop.anchor})")
            if r.bridge:
                print(f"    writes in the bridge's data: {r.bridge}")
        else:
            print(f"    why: {r.reason}")
    print()
    print_counts(info)


def step_words(s):
    return {"rcon": lambda: f"rcon {s.text}", "rcon_bg": lambda: f"rcon in the background {s.text}", "con": lambda: f"cmd {s.text}", "post": lambda: f"bridgepost.py {s.text}", "pause": lambda: f"sleep {s.seconds:g}",
            "shoot": lambda: f"shot x{s.n}" + (" side by side" if s.parallel else ""), "mark": lambda: "mark the client's log", "wait_log": lambda: "wait for in game in the log",
            "vote_gap": lambda: f"wait out the {VOTE_GAP} s after a vote", "vote_closed": lambda: "(the vote has closed)", "guard": lambda: f"refuse unless a vote closed under {s.seconds:g} s ago",
            "vote_opened": lambda: "(the vote has opened)", "pressed": lambda: "(the press is marked)", "until": lambda: f"sleep until {s.seconds:g} s after {ANCHORS[s.text]}"}[s.kind]()


def print_counts(info):
    counts = {st: sum(1 for r in ROWS if r.status == st) for st in STATUSES}
    print("Rows: " + ", ".join(f"{counts[st]} {st}" for st in STATUSES) + f"; {len(ROWS)} in all.")
    picks = info["picks"]
    pick_status = {}
    for p in picks:
        states = {r.status for r in ROWS if r.pick == p.name}
        pick_status[p.name] = READY if READY in states else sorted(states)[0]
    print(f"The {len(picks)} picks: " + ", ".join(f"{sum(1 for v in pick_status.values() if v == st)} {st}" for st in STATUSES) + "; every pick has a row.")
    ready = [r for r in ROWS if r.status == READY]
    print(f"Of the {counts[READY]} ready rows: {sum(1 for r in ready if r.bridge)} need --bridge-copy, {sum(1 for r in ready if r.stand_in)} are stand-ins by os_notice, "
          f"{sum(1 for r in ready if r.names.startswith('real name'))} would show a real name and mask it, {sum(1 for r in ready if r.pick)} show a pick on today's surface: retake those when the message system lands.")
    print(f"Every ready frame also gets the avatar and, but for {sum(1 for r in ready if r.chat)} chat rows, the chat zone blurred.")


def cmd_dry_run(res_list, only, bridge_copy, out, stream=None):
    stream = stream or sys.stdout
    for res in res_list:
        if res not in RESOLUTIONS:
            raise SystemExit(f"--res {res}: not one of the owner's: {', '.join(RESOLUTIONS)}")
        plan, notes = build_plan(only, bridge_copy)
        print(f"######## {res}: what a run would execute, nothing is touched (out: {out})", file=stream)
        if bridge_copy:
            print(f"GATE with --bridge-copy (the run refuses otherwise): {subprocess.list2cmdline(post_argv('check'))}", file=stream)
        for n in notes:
            print(n, file=stream)
        if not plan:
            print("Nothing to run.", file=stream)
            continue
        p = Printer(res, out, stream)
        walk(p, plan)
        print(f"-------- {res}: {len([e for e in plan if e.mode == 'shoot'])} rows, {p.frames} frames at full size, about {p.clock / 60:.0f} min by the verbs' timings (T_RCON, T_SHOT, ...) "
              f"(-MaxMinutes in the start command: 120), {len(notes)} not run.", file=stream)


def candidate_up():
    """--bridge-copy's word held to the facts: bridgepost.py's checks, nothing sent. (True, words) or (False, why). Its output in a file, not a pipe."""
    with tempfile.TemporaryFile() as f:
        r = subprocess.run(post_argv("check"), stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, timeout=120)
        f.seek(0)
        return r.returncode == 0, f.read().decode("utf-8", "replace").strip()


def cmd_run(args, info):
    ok, words = gate(read_lock_file, a2s)
    print(scrub(words), flush=True)
    if not ok:
        return 2
    if args.bridge_copy:
        ok, words = candidate_up()
        print(scrub(words), flush=True)
        if not ok:
            return 2
    if args.res not in RESOLUTIONS:
        raise SystemExit(f"--res {args.res}: not one of the owner's: {', '.join(RESOLUTIONS)}")
    plan, notes = build_plan(args.only, args.bridge_copy)
    for n in notes:
        print(n, flush=True)
    if not plan:
        print("Nothing to run.")
        return 1
    out = Path(args.out)
    doer = Doer(args.res, out, args.keep_raw)
    print(f"HEAD {info['commit']}: {len(plan)} rows at {args.res}, out {out}")
    try:
        failed = walk(doer, plan)
    except KeyboardInterrupt:
        print("Interrupted: writing what there is.")
        failed = {"interrupted"}
    except OverlayOpen as e:
        print(f"STOPPED: {scrub(str(e))}\nWriting what there is.")
        failed = {"the Steam overlay"}
    doer.write_pictures(plan, info)
    print(f"Done: {sum(len(v) for v in doer.frames.values())} frames; {len(failed)} rows failed or skipped{': ' + ', '.join(sorted(failed)) if failed else ''}. Index: {out / 'index.html'}")
    print("Nothing here reads a picture: review each one before it goes into the public docs.")
    return 0 if not failed else 3


def cmd_remask(args, info):
    out = Path(args.out)
    res_dir = out / args.res
    manifest = json.loads((res_dir / "manifest.json").read_text(encoding="utf-8"))
    global RENAMED
    RENAMED = bool(manifest.get("renamed"))   # the masks of the run that took the frames
    rows = {r.id: r for r in ROWS}
    done = 0
    for rid, got in manifest["rows"].items():
        for f in got.get("frames", []):
            raw = res_dir / "_raw" / f["file"]
            if not raw.exists():
                raise SystemExit(f"{raw} is not there: --remask needs a run with --keep-raw")
            file, crop = post_frame(raw, rows[rid], res_dir)
            f["file"], f["crop"] = file, crop
            done += 1
    (res_dir / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    write_index(out, info)
    print(f"{done} frames masked again at {args.res}; index {out / 'index.html'}")
    return 0


def cmd_mask_file(args):
    from PIL import Image
    rows = {r.id: r for r in ROWS}
    if args.row not in rows:
        raise SystemExit(f"no row {args.row}")
    with Image.open(args.mask_file[0]) as opened:
        im = opened.convert("RGB")
    apply_masks(im, rows[args.row])
    im.save(args.mask_file[1])
    print(f"{args.mask_file[1]}: {im.size[0]}x{im.size[1]}: {describe_post(rows[args.row], *im.size)}")


# ---------------------------------------------------------------- the offline checks --------------------------------------------------------------------

FAKE_TOKEN = "ab" * 4 + "cd" * 4 + "12" * 4 + "34" * 4   # a made-up secret in the fake harness's answers, which the scrubber must hide

FAKE_HARNESS = r"""
# A stand-in for cs2client.ps1 in --selfcheck's deep check: answers each verb in the harness's words, the client's real name and the server's address in them
# as the real replies have them, and logs each call.
import json, os, re, shutil, sys, uuid
a = sys.argv[1:]
with open(os.path.join(os.environ["FAKE_LOG"], uuid.uuid4().hex), "w", encoding="utf-8") as f:   # a file per call: a shot and a background RCON run at once, and Windows' append is a seek and a write
    f.write(json.dumps(a))
fail = os.environ.get("FAKE_FAIL", "")
name = os.environ["FAKE_NAME"]     # the client's real name, as the real plugin's replies carry it
verb = a[0]
def arg(n):
    return a[a.index(n) + 1]
if verb == "shot":
    res = os.environ["FAKE_RES"]
    dst = os.path.join(arg("-OutDir"), arg("-Name") + ".png")
    rows_png = os.environ.get("FAKE_PNG_ROWS", "")
    shutil.copyfile(rows_png if rows_png and arg("-Name") != "_probe" else os.environ["FAKE_PNG"], dst)
    print(f"{dst}  ok {res} (from {res}) mean=72,7")
elif verb == "post":
    if a[1:2] == ["names-check"] and fail == "names-check":
        print("REFUSED: 1 private name(s) among the vote's names in the bridge's state - nothing may be shot")
        sys.exit(1)
    print(f"12:00:00 /x: {' '.join(a[1:])} -> 200 ok: heard, /log/{os.environ['FAKE_TOKEN']}")
elif verb == "log":
    if "-Pattern" in a:
        print("   501: 11/05 05:10:02 ChangeGameUIState: CSGO_GAME_UI_STATE_LOADINGSCREEN -> CSGO_GAME_UI_STATE_INGAME")
    print("lines: 500")
elif verb == "cmd":
    c = arg("-Cmd")
    print(f"Client: Connected [{os.environ['FAKE_ADDRESS']}]\n1.41.9.6/14196 10955 insecure public" if c == "status" else f"{c}: done")
elif verb == "rcon":
    c = arg("-Rcon")
    print(f"> {c}")
    if c == "osd_name":
        print(f"osd_name: {os.environ['FAKE_ACCOUNT']} goes by Alice; on the server: {os.environ['FAKE_ACCOUNT']} is Alice")
    elif fail and fail in c:
        print("os_hud: no vote is on: vote and browse need one")
    elif c == "os_hud":
        print(f"os_hud: addon 3823993566, nobody frozen, entity 123, world ready, state 1s old: ar_shoots by , vote none, bridge ok, window open for [{name}]")
    elif c.startswith("os_hud show"):
        print(f"os_hud: window on {c.split()[-1]} for {name}")
    elif c.startswith("os_hud press"):
        print(f"os_hud: pressed {c.split()[-1]} for {name}")
    elif c.startswith("os_hud click"):
        print(f"os_hud: sent !next for account {c.split()[2]}; the bridge's answer arrives with the next frame")
    elif c.startswith("os_hud centre"):
        print('os_hud: "words" for 8 s, sent again every 3 s')
    elif c.startswith("os_hud release"):
        print("os_hud: released: 1 window(s), 0 TAB cursor(s); capture off for everybody next frame")
    elif c == "os_hud level 0":
        print("os_hud: server level real")
    elif c.startswith("os_hud level"):
        print(f"os_hud: join banner for {name} for 15 s (server level 47)")
    elif c.startswith("os_hud content"):
        print(f"os_hud: account {c.split()[2]}: HUD v4.6.0")
    elif c.startswith("css_guide"):
        print(f"guide: showing to {name}")
    elif c.startswith("os_notice"):
        print("os_notice: shown for 10 s: words")
    elif c.startswith("os_fun reset"):
        print("fun: reset - done")
    elif c.startswith("os_fun"):
        print("fun: gravity moon by Bob: sv_gravity 200")
    elif c.startswith("os_catchup"):
        print(f"catchup: showed {name}: words")
    elif c.startswith("os_mode"):
        print("os_mode: mode deathmatch: kept")
    else:
        print(f"ok, /log/{os.environ['FAKE_TOKEN']} rcon_password hunter2")
"""


class RecordingPrinter(Printer):
    """The dry run's commands as lists, to hold them against what the real run's code launched."""

    def __init__(self, res, out):
        super().__init__(res, out, io.StringIO())
        self.calls = []

    def cmdline(self, argv):
        self.calls.append(["post", *argv[argv.index(str(BRIDGEPOST)) + 1:]] if str(BRIDGEPOST) in argv else argv[argv.index(str(HARNESS)) + 1:])
        return super().cmdline(argv)


def deep_check(info, check):
    """The real run's own code, Doer and all, with cs2client.ps1 swapped for FAKE_HARNESS and the sleeps skipped: what it launches must be what the dry run printed, its pictures, manifest and
    index must come out, its log must hold no secret and no name, and a vote that does not open must stop its chain before a click that would count."""
    from PIL import Image
    global harness_argv, post_argv
    work = WORK / f"_selfcheck_deep-{os.getpid()}"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    fake = work / "fake.py"
    fake.write_text(FAKE_HARNESS, encoding="utf-8")
    res = "1280x720"
    png = work / "frame.png"
    Image.new("RGB", (1280, 720), (60, 70, 80)).save(png)
    real_argv, real_post, real_sleep, real_stdout = harness_argv, post_argv, time.sleep, sys.stdout
    calls = work / "calls"
    calls.mkdir()
    os.environ.update(FAKE_LOG=str(calls), FAKE_PNG=str(png), FAKE_RES=res, FAKE_TOKEN=FAKE_TOKEN, FAKE_NAME=REAL_NAMES[0], FAKE_ADDRESS=f"{HOST}:{PORT}", FAKE_ACCOUNT=ACCOUNT)
    os.environ.pop("FAKE_FAIL", None)

    def norm(call):
        return ["<mark>" if i and call[i - 1] == "-FromLine" else ("<raw>" if i and call[i - 1] == "-OutDir" else x) for i, x in enumerate(call)]

    try:
        plan, _ = build_plan(None, True)
        printer = RecordingPrinter(res, work / "out")
        walk(printer, plan)
        expected = sorted(json.dumps(norm(c)) for c in printer.calls)
        harness_argv = lambda *verb_args: [sys.executable, "-I", str(fake), *verb_args]
        post_argv = lambda text: [sys.executable, "-I", str(fake), "post", *text.split()]
        time.sleep = lambda seconds: None
        sys.stdout = io.StringIO()
        doer = Doer(res, work / "out", False)
        failed = walk(doer, plan)
        doer.write_pictures(plan, info)
        sys.stdout = real_stdout
        check(not failed, f"deep check: rows failed against the fake: {sorted(failed)[:6]} {list(doer.failed.items())[:2]}")
        launched = [json.loads(f.read_text(encoding="utf-8")) for f in calls.iterdir()]
        got = sorted(json.dumps(norm(c)) for c in launched)
        check(got == expected, f"deep check: the real run launched {len(got)} commands, the dry run printed {len(expected)}; first difference: {next((a + ' / ' + b for a, b in zip(got, expected) if a != b), 'in length')[:300]}")
        check(all(c[0] in (*ALL_VERBS, "post") for c in launched), "deep check: a verb outside the four and bridgepost.py")
        check(all(c[1] in POST_VERBS for c in launched if c[0] == "post"), "deep check: a bridgepost.py verb that is not one")
        check(all("-MaxWidth" in c and c[c.index("-MaxWidth") + 1] == "0" for c in launched if c[0] == "shot"), "deep check: a shot not at full size")
        check(sum(1 for c in launched if c[0] == "rcon" and "os_set_restart" in c[2]) == 1, "deep check: the restart's button is not clicked exactly once")
        check(not any(c[0] == "rcon" and "os_hudver_" in c[2] for c in launched), "deep check: the version button was clicked: its real news names the client")
        check(not any(c[0] == "rcon" and re.search(r"\b(quit|exit|os_ws_play|mm_extra_addons|mm_client_extra_addons|host_workshop_map)\b", c[2]) for c in launched), "deep check: a forbidden command went out")
        man = json.loads((work / "out" / res / "manifest.json").read_text(encoding="utf-8"))
        shoot_rows = [e.row for e in plan if e.mode == "shoot"]
        for r in shoot_rows:
            frames = man["rows"].get(r.id, {}).get("frames", [])
            check(len(frames) == len(frame_names(r)), f"deep check: {r.id}: {len(frames)} frames in the manifest, {len(frame_names(r))} expected")
            for f in frames:
                check((work / "out" / res / f["file"]).exists(), f"deep check: {f['file']} is not there")
        check(not (work / "out" / res / "_raw").exists(), "deep check: the raw frames, with the real name in them, are still there")
        log = (work / "out" / res / "run.log").read_text(encoding="utf-8")
        check(not real_name_in(local_paths_out(log)) and ACCOUNT not in log and HOST not in log and "hunter2" not in log and FAKE_TOKEN not in log, "deep check: the run's log holds a secret, a name or the address")
        check((work / "out" / "index.html").exists(), "deep check: no index")

        # a ballot whose bridge names the client by a private name: the row stops before its picture
        shutil.rmtree(work / "out", ignore_errors=True)
        shutil.rmtree(calls)
        calls.mkdir()
        os.environ["FAKE_FAIL"] = "names-check"
        plan, _ = build_plan(["vote-started-line"], True)
        sys.stdout = io.StringIO()
        doer = Doer(res, work / "out", False)
        failed = walk(doer, plan)
        doer.write_pictures(plan, info)
        sys.stdout = real_stdout
        launched = [json.loads(f.read_text(encoding="utf-8")) for f in calls.iterdir()]
        check("vote-started-line" in failed, f"deep check: a refused names-check did not fail its row: {sorted(failed)}")
        check(not any(c[0] == "shot" and "vote-started-line" in c[c.index("-Name") + 1] for c in launched), "deep check: a ballot was shot after its names-check refused")
        os.environ.pop("FAKE_FAIL", None)

        # a vote that does not open: the chain stops before its clicks count
        shutil.rmtree(work / "out", ignore_errors=True)
        shutil.rmtree(calls)
        calls.mkdir()
        os.environ["FAKE_FAIL"] = f"os_hud show {ACCOUNT} vote"
        plan, _ = build_plan(["vote-kept"], True)
        sys.stdout = io.StringIO()
        doer = Doer(res, work / "out", False)
        failed = walk(doer, plan)
        doer.write_pictures(plan, info)
        sys.stdout = real_stdout
        launched = [json.loads(f.read_text(encoding="utf-8")) for f in calls.iterdir()]
        check("vote-kept" in failed, f"deep check: a vote that did not open did not stop its chain: {sorted(failed)}")
        check(not any(c[0] == "rcon" and "os_m1_play" in c[2] for c in launched), "deep check: a card was clicked in a vote that did not open")

        # a run that keeps its raw frames, and --remask on them: no verb, the same files, and without the raw frames it refuses
        os.environ.pop("FAKE_FAIL", None)
        shutil.rmtree(work / "out", ignore_errors=True)
        plan, _ = build_plan(["chat-unknown", "welcome-back"], True)
        sys.stdout = io.StringIO()
        doer = Doer(res, work / "out", True)
        failed = walk(doer, plan)
        doer.write_pictures(plan, info)
        before = len(list(calls.iterdir()))
        shot = work / "out" / res / "chat-unknown.png"
        shot.unlink()
        args = argparse.Namespace(res=res, out=str(work / "out"))
        code = cmd_remask(args, info)
        sys.stdout = real_stdout
        check(not failed and code == 0 and shot.exists() and len(list(calls.iterdir())) == before, f"deep check: --remask: failed {sorted(failed)}, exit {code}, the frame back: {shot.exists()}")
        check((work / "out" / res / "_raw" / "chat-unknown.png").exists(), "deep check: --keep-raw kept no raw frame")
        shutil.rmtree(work / "out" / res / "_raw")
        sys.stdout = io.StringIO()
        try:
            cmd_remask(args, info)
            check(False, "deep check: --remask did not refuse without raw frames")
        except SystemExit:
            pass
        sys.stdout = real_stdout

        # the Steam overlay in the probe's frame: the run stops before anything is clicked, and the frame is gone
        ow, oh = parse_res(res)
        overlay_png = work / "overlay.png"
        fake_overlay(Image.new("RGB", (ow, oh), (60, 70, 80)), round(oh * 0.044), round(oh * 0.02)).save(overlay_png)
        for case, env in (("the probe", {"FAKE_PNG": str(overlay_png)}), ("a row's shot", {"FAKE_PNG_ROWS": str(overlay_png)})):
            shutil.rmtree(work / "out", ignore_errors=True)
            shutil.rmtree(calls)
            calls.mkdir()
            os.environ.update(env)
            plan, _ = build_plan(["chat-unknown", "welcome-back"], True)
            sys.stdout = io.StringIO()
            doer = Doer(res, work / "out", True)
            stopped = None
            try:
                walk(doer, plan)
            except OverlayOpen as e:
                stopped = str(e)
            sys.stdout = real_stdout
            os.environ.update(FAKE_PNG=str(png))
            os.environ.pop("FAKE_PNG_ROWS", None)
            launched = [json.loads(f.read_text(encoding="utf-8")) for f in calls.iterdir()]
            shots = [c[c.index("-Name") + 1] for c in launched if c[0] == "shot"]
            raw_left = sorted(x.name for x in (work / "out" / res / "_raw").glob("*.png"))
            sys.stdout = io.StringIO()
            doer.write_pictures(plan, info)            # as the run does after it stops: what there is, and its log closed
            sys.stdout = real_stdout
            check(stopped is not None and "Steam overlay" in stopped, f"deep check: the overlay in {case} did not stop the run")
            if case == "the probe":
                check(shots == ["_probe"] and not raw_left and not any(c[0] == "rcon" and "os_hud press" in c[2] for c in launched),
                      f"deep check: the overlay in the probe: shots {shots}, raw frames left {raw_left}")
            else:
                first = next(frame_names(e.row)[0] for e in plan if e.mode == "shoot")   # the first row that shoots: the run stops at its frame, none after it
                check(sorted(shots) == sorted(["_probe", first]) and raw_left == ["_probe.png"], f"deep check: the overlay at a row's shot: shots {shots}, raw frames left {raw_left}")
    finally:
        sys.stdout = real_stdout
        time.sleep = real_sleep
        harness_argv, post_argv = real_argv, real_post
        os.environ.pop("FAKE_FAIL", None)
        shutil.rmtree(work, ignore_errors=True)
    check(not work.exists(), f"{work} is still there")

def cmd_selfcheck(info):
    """The offline checks, with or without Steam on this PC: a made-up account and name stand in for the missing ones while they run."""
    global REAL_NAMES, STEAM_ACCOUNT
    real = REAL_NAMES, STEAM_ACCOUNT
    REAL_NAMES = REAL_NAMES or ("zz-real-player",)   # so the name checks below still check something
    STEAM_ACCOUNT = STEAM_ACCOUNT or ACCOUNT          # so the gate's other checks are reached
    try:
        return _selfcheck(info)
    finally:
        REAL_NAMES, STEAM_ACCOUNT = real


def _selfcheck(info):
    from PIL import Image, ImageChops
    global STEAM_ACCOUNT
    fails = []

    def check(ok, words):
        if not ok:
            fails.append(words)

    # no real name from the bridge: every ballot a row opens is checked (names-check) before its first picture, and the paints over the bridge's names
    # stay under --renamed
    for r in ROWS:
        kinds = [(s.kind, s.text) for s in r.steps]
        if ("vote_opened", "") in kinds:
            first_shot = next((i for i, k in enumerate(kinds) if k[0] == "shoot"), len(kinds))
            check(any(k == ("post", "names-check") for k in kinds[kinds.index(("vote_opened", "")):first_shot]), f"{r.id} opens a vote and shoots it before a names-check")
    global RENAMED
    was = RENAMED
    RENAMED = True
    try:
        for r in ROWS:
            check(all(m in kept_masks(r) for m in r.masks if m.keep), f"{r.id}: a paint over a name the bridge has is dropped under --renamed")
        check(M_VWAIT1.keep and M_VWAIT2.keep, "the waiting lines' paints are not marked keep")
    finally:
        RENAMED = was

    # the messages, by their own words in the file: validate() has held the rows to them; said again here so the check names them
    picks = info["picks"]
    for p in picks:
        check(any(r.pick == p.name for r in ROWS), f"no row for the pick {p.name}")

    # every resolution, with and without the bridge copy: what would run
    ready = [r for r in ROWS if r.status == READY]
    out = WORK / "out"
    estimates = {}
    for res in RESOLUTIONS:
        for copy in (True, False):
            buf = io.StringIO()
            cmd_dry_run([res], None, copy, out, buf)
            text = buf.getvalue()
            for line in text.splitlines():
                m = re.search(r"cs2client\.ps1\"? (\w+)", line)
                if m and "start -Connect" not in line:
                    check(m.group(1) in ALL_VERBS, f"{res}: a verb outside {ALL_VERBS}: {line[:120]}")
                if " shot " in line:
                    check("-MaxWidth 0" in line, f"{res}: a shot not at full size: {line[:120]}")
                m = re.search(r"bridgepost\.py\"? ([\w-]+)", line)
                if m and line.lstrip().startswith(("$", "GATE")):
                    check(copy and m.group(1) in POST_VERBS, f"{res}: a bridgepost.py line {'with no --bridge-copy' if not copy else 'with no such verb'}: {line[:120]}")
            check(not real_name_in(local_paths_out(text)), f"{res}: a real name in the dry run, outside this PC's own folder names")
            check("MaxWidth 1920" not in text, f"{res}: -MaxWidth 1920 in the dry run")
            for r in ready:
                if (copy or not r.bridge) and (RENAMED or not r.renamed):
                    runs_after = r.after
                    chain_ok = True
                    while runs_after:
                        parent = next(x for x in ROWS if x.id == runs_after)
                        chain_ok = chain_ok and (copy or not parent.bridge)
                        runs_after = parent.after
                    for name in frame_names(r) if chain_ok else []:
                        check(f"-Name {name} " in text, f"{res}: no shot of {name}")
            if copy:
                m = re.search(r"about (\d+) min", text)
                estimates[res] = int(m.group(1)) if m else 0

    # every box lands on the frame at every resolution, the right size for it
    for res in RESOLUTIONS:
        w, h = parse_res(res)
        for r in ROWS:
            for x0, y0, x1, y1 in [*(mask_box(m, w, h) for m in r.masks if m.box), *([place(r.crop, w, h)] if r.crop else []), compare_region(r, w, h),
                                   *volatile_boxes(r, w, h)]:
                check(x1 > x0 and y1 > y0 and x0 >= 0 and y0 >= 0 and x1 <= w and y1 <= h, f"{res}, {r.id}: box {x0, y0, x1, y1} off the frame")

    # a retake against the committed picture (publish.py): the same within noise and the volatile boxes, a real change anywhere else
    rows = {r.id: r for r in ROWS}
    w, h = 1920, 1080
    shot = Image.effect_noise((w, h), 30).convert("RGB")
    jpeg_again = io.BytesIO()
    shot.save(jpeg_again, "JPEG", quality=85, subsampling=0)
    again = Image.open(io.BytesIO(jpeg_again.getvalue())).convert("RGB")
    help_row, players_row, vote_row = rows["page-help"], rows["page-players"], rows["vote-open"]
    region = compare_region(help_row, w, h)
    check(same_picture(again, shot, region, volatile_boxes(help_row, w, h))[0], "same_picture: the same frame through JPEG at quality 85 counts as changed")

    def painted(base, box, colour=(250, 250, 250)):
        from PIL import ImageDraw
        im = base.copy()
        ImageDraw.Draw(im).rectangle((box[0], box[1], box[2] - 1, box[3] - 1), fill=colour)   # a box is half-open, as place() gives it
        return im

    word = (900, 500, 960, 512)                              # a word's size, inside the window, outside every volatile box
    check(not same_picture(painted(shot, word), shot, region, volatile_boxes(help_row, w, h))[0], "same_picture: a changed word in the window counts as the same")
    clock = place(V_IDLE, w, h)
    check(same_picture(painted(shot, (clock[0] + 4, clock[1] + 4, clock[2] - 4, clock[3] - 4)), shot, region, volatile_boxes(help_row, w, h))[0],
          "same_picture: the idle bar's change counts as changed")
    onfor = place(V_ONFOR, w, h)
    check(same_picture(painted(shot, onfor), shot, compare_region(players_row, w, h), volatile_boxes(players_row, w, h))[0]
          and not same_picture(painted(shot, onfor), shot, compare_region(help_row, w, h), volatile_boxes(help_row, w, h))[0],
          "same_picture: a row's own volatile box is not its alone")
    outside = (10, 10, 200, 200)                               # the radar, outside the window: a frame is compared by its window
    check(same_picture(painted(shot, outside), shot, region, volatile_boxes(help_row, w, h))[0], "same_picture: the world outside the window counts")
    tiles = place(V_VOTE_TILES, w, h)
    check(same_picture(painted(shot, (tiles[0] + 50, tiles[1] + 50, tiles[0] + 400, tiles[1] + 200)), shot, compare_region(vote_row, w, h), volatile_boxes(vote_row, w, h))[0],
          "same_picture: the ballot's random tiles count")
    check(not same_picture(shot.resize((1280, 720)), shot, region)[0], "same_picture: another size counts as the same")
    crop_row = rows["chat-unknown"]
    cb = place(crop_row.crop, w, h)
    look, look_again = shot.crop(cb), again.crop(cb)
    check(same_picture(look_again, look, compare_region(crop_row, w, h, crop=True), volatile_boxes(crop_row, w, h, crop=True))[0]
          and not same_picture(painted(look_again, (20, 20, 80, 32)), look, compare_region(crop_row, w, h, crop=True), volatile_boxes(crop_row, w, h, crop=True))[0],
          "same_picture: a crop compared whole")

    # a crop over the game: the top line's pill on two different games - its panel alone compared, its width a change, no pill the whole region
    from PIL import ImageDraw
    top_row = rows["top-plain"]
    other_game = Image.effect_noise((w, h), 30).convert("RGB")

    def pill(base, half=230, word=None):
        im = base.copy()
        dr = ImageDraw.Draw(im)
        dr.rectangle((960 - half, 150, 960 + half, 186), fill=(37, 43, 45))
        dr.rectangle((960 - half + 8, 158, 960 - half + 70, 178), fill=(233, 182, 79))   # its tag
        dr.rectangle((960 - half + 90, 165, 960 + half - 40, 172), fill=(235, 235, 235))  # its words
        if word:
            dr.rectangle(word, fill=(37, 43, 45))
        return im
    tp, tr, tv = panel_of(top_row, w, h), compare_region(top_row, w, h), volatile_boxes(top_row, w, h)
    check(not same_picture(pill(other_game), pill(shot), tr, tv)[0], "same_picture: the game around a pill, without its panel, counts as a change (the old rule)")
    check(same_picture(pill(other_game), pill(shot), tr, tv, tp)[0], "same_picture: the same pill over another game counts as changed")
    check(not same_picture(pill(other_game, word=(1000, 165, 1040, 172)), pill(shot), tr, tv, tp)[0], "same_picture: a changed word in the pill counts as the same")
    check(not same_picture(pill(other_game, half=260), pill(shot), tr, tv, tp)[0], "same_picture: a wider pill counts as the same")
    check(not same_picture(other_game, pill(shot), tr, tv, tp)[0], "same_picture: a pill gone counts as the same")
    cb = place(top_row.crop, w, h)
    check(same_picture(pill(other_game).crop(cb), pill(shot).crop(cb), compare_region(top_row, w, h, crop=True), volatile_boxes(top_row, w, h, crop=True),
                       panel_of(top_row, w, h, crop=True))[0], "same_picture: a crop file's pill over another game counts as changed")
    check(panel_of(rows["page-help"], w, h) is None and panel_of(rows["chat-unknown"], w, h) is None and panel_of(rows["why-card"], w, h) is None,
          "panel_of: a window's crop, a chat crop or a frame without a crop gets a panel")

    # the Steam overlay: its button caught at every resolution over a dark or a busy game, nothing like it elsewhere taken for it
    for ores in RESOLUTIONS:
        ow, oh = parse_res(ores)
        side, margin = round(oh * 0.044), round(oh * 0.02)
        dark = Image.new("RGB", (ow, oh), (5, 5, 4))
        busy = Image.effect_noise((ow, oh), 40).convert("RGB")
        found = steam_overlay(fake_overlay(dark, side, margin))
        check(found == (ow - margin - side, margin, ow - margin, margin + side), f"steam_overlay at {ores}: the button over a dark game found at {found}")
        check(steam_overlay(fake_overlay(busy, side, margin)) is not None, f"steam_overlay at {ores}: the button over a busy game missed")
        check(steam_overlay(busy) is None and steam_overlay(dark) is None, f"steam_overlay at {ores}: a frame without the overlay taken for it")
    ow, oh = 1920, 1080
    plain = Image.new("RGB", (ow, oh), (5, 5, 4))
    no_x = plain.copy()
    no_x.paste(OVERLAY_BUTTON, (ow - 22 - 48, 22, ow - 22, 70))
    check(steam_overlay(no_x) is None, "steam_overlay: the button's grey without its X taken for the overlay")
    check(steam_overlay(fake_overlay(plain, 48, 22, colour=(70, 75, 82))) is None, "steam_overlay: a square of another grey with an X taken for the overlay")
    window_x = plain.copy()
    window_x.paste(fake_overlay(Image.new("RGB", (120, 120), (5, 5, 4)), 48, 22), (1440, 120))   # our window's X, at its top right corner
    check(steam_overlay(window_x) is None, "steam_overlay: a square with an X inside the HUD's window taken for the overlay")

    # the --only chain: a row's ancestors run first, as setup, and take no picture
    plan, _ = build_plan(["vote-kept"], True)
    check([e.row.id for e in plan] == ["vote-open", "vote-limit", "vote-my-vote", "vote-inactivity", "vote-kept"], f"--only vote-kept plans {[e.row.id for e in plan]}")
    check([e.mode for e in plan][-1] == "shoot" and all(e.mode == "setup" for e in plan[:-1]), "--only: ancestors are not setup only")
    check(all(r in {x.id: x for x in ROWS} and next(x for x in ROWS if x.id == r).status == READY for r in REVIEW_SET), "REVIEW_SET names a row that is not there or not ready")
    plan, notes = build_plan(["hud-hold"], True)
    check(not plan and notes, "--only of a row that is not ready must run nothing and say why")
    plan, notes = build_plan(None, False)
    check(not any(e.row.bridge for e in plan) and any("bridge-copy" in n for n in notes), "without --bridge-copy no row that writes the bridge's data may run")

    # the gate: it never goes to the network here
    no_a2s = lambda: (_ for _ in ()).throw(AssertionError("A2S asked without a good lock"))
    good = OWNER_LINE + "\nsince: x\n"
    cases = [
        ("no lock", lambda: None, no_a2s, False, "does not exist"),
        ("someone else's lock", lambda: "owner: release agent\n", no_a2s, False, "not the harness's"),
        ("an empty lock", lambda: "", no_a2s, False, "not the harness's"),
        ("no A2S", lambda: good, lambda: None, False, "does not answer"),
        ("VAC on", lambda: good, lambda: ("Over Strike 2", START_MAP, 0, 1), False, "VAC on"),
        ("another human", lambda: good, lambda: ("Over Strike 2", START_MAP, 2, 0), False, "humans"),
        ("a Workshop map", lambda: good, lambda: ("Over Strike 2", "workshop/1/aim", 1, 0), False, "the map is"),
        ("all good", lambda: good, lambda: ("Over Strike 2", START_MAP, 1, 0), True, "VAC off"),
    ]
    for name, lock, ask, want, says in cases:
        ok, words = gate(lock, ask)
        check(ok == want and says in words, f"gate, {name}: {ok}, {words}")
    account, STEAM_ACCOUNT = STEAM_ACCOUNT, ""
    ok, words = gate(lambda: good, no_a2s)
    STEAM_ACCOUNT = account
    check(not ok and "Steam" in words, f"gate, no Steam login on this PC: {ok}, {words}")

    # the real run's own code, against a fake of the harness: no pwsh, no client, no server
    deep_check(info, check)

    # the real run's door itself: no lock, or a foreign lock, or VAC on: it refuses before any verb, and no folder appears
    global read_lock_file, a2s, candidate_up
    real_lock, real_a2s, real_candidate = read_lock_file, a2s, candidate_up
    door = WORK / f"_selfcheck_door-{os.getpid()}"
    stdout = sys.stdout
    try:
        args = argparse.Namespace(res="1920x1080", out=str(door), only=None, bridge_copy=True, keep_raw=False)
        for name, lock, ask in (("no lock", lambda: None, None), ("a foreign lock", lambda: "owner: another agent\n", None), ("VAC on", lambda: OWNER_LINE + "\n", lambda: ("Over Strike 2", START_MAP, 1, 1))):
            read_lock_file, a2s = lock, ask or (lambda: (_ for _ in ()).throw(AssertionError("A2S asked without a good lock")))
            sys.stdout = io.StringIO()
            code = cmd_run(args, info)
            sys.stdout = stdout
            check(code == 2 and not door.exists(), f"the run's door, {name}: exit {code}, folder there: {door.exists()}")
        read_lock_file, a2s = lambda: OWNER_LINE + "\n", lambda: ("Over Strike 2", START_MAP, 1, 0)
        candidate_up = lambda: (False, "REFUSED: the bridge is not the candidate - nothing sent")
        sys.stdout = io.StringIO()
        code = cmd_run(args, info)
        sys.stdout = stdout
        check(code == 2 and not door.exists(), f"the run's door, a bridge that is not the candidate: exit {code}, folder there: {door.exists()}")
    finally:
        sys.stdout = stdout
        read_lock_file, a2s, candidate_up = real_lock, real_a2s, real_candidate

    # the scrubber, and the pictures at every resolution, on made-up frames in a folder of this script's
    check("<hidden>" in scrub("http://x/log/abcDEF123456") and not any(real_name_in(scrub(f"for {n.upper()}")) for n in REAL_NAMES) and ACCOUNT not in scrub(f"account {ACCOUNT}")
          and HOST not in scrub(HOST), "the scrubber lets something through")
    work = WORK / f"_selfcheck-{os.getpid()}"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    try:
        by_id = {r.id: r for r in ROWS}
        for res in RESOLUTIONS:
            w, h = parse_res(res)
            base = Image.effect_noise((w, h), 40).convert("RGB")
            raw = work / "raw"
            raw.mkdir(exist_ok=True)
            for rid in ("vote-my-vote", "chat-unknown", "welcome-back", "board-default", "vote-pager"):
                row = by_id[rid]
                path = raw / f"{rid}.png"
                base.save(path)
                file, crop = post_frame(path, row, work / res)
                with Image.open(work / res / file) as opened:
                    got = opened.convert("RGB")
                check(got.size == (w, h), f"{res}, {rid}: written at {got.size}: nothing may be scaled")
                if crop:
                    with Image.open(work / res / crop) as c:
                        b = place(row.crop, w, h)
                        check(c.size == (b[2] - b[0], b[3] - b[1]), f"{res}, {rid}: its crop is {c.size}")
                av = place(row.avatar, w, h)
                check(ImageChops.difference(got.crop(av), base.crop(av)).getbbox() is not None, f"{res}, {rid}: the avatar was not touched")
                for m in row.masks:
                    if m.box:
                        mb = mask_box(m, w, h)
                        check(ImageChops.difference(got.crop(mb), base.crop(mb)).getbbox() is not None, f"{res}, {rid}: a mask did nothing")
            man = {"res": res, "commit": info["commit"], "content": info["content"], "plugin": info["plugin"], "taken": "selfcheck",
                   "rows": {"vote-my-vote": {"frames": [{"file": "vote-my-vote.png", "crop": "vote-my-vote-crop.png"}]}, "chat-unknown": {"frames": [{"file": "chat-unknown.png", "crop": "chat-unknown-crop.png"}], "failed": "made up"}}}
            (work / res / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
        shutil.rmtree(work / "raw")
        write_index(work, info)
        page = (work / "index.html").read_text(encoding="utf-8")
        links = re.findall(r'(?:href|src)="([^"]+)"', page)
        check(len(links) >= 14, f"the index has {len(links)} links")
        for link in links:
            check(":" not in link and not link.startswith(("/", "\\")), f"not a relative link: {link}")
            check((work / html.unescape(link)).exists(), f"the index links a file that is not there: {link}")
        check("What is not here, and why" in page and "FAILED at" in page, "the index does not say what is missing")
        check(not real_name_in(page), "a real name in the index")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    check(not work.exists(), f"{work} is still there")

    if fails:
        print("SELFCHECK FAILED:")
        for f in fails:
            print("  " + f)
        return 1
    counts = {st: sum(1 for r in ROWS if r.status == st) for st in STATUSES}
    print(f"SELFCHECK OK at HEAD {info['commit']} (content {info['content']}, plugin {info['plugin']}):")
    print(f"  all {len(picks)} messages of the message list have a row; {counts[READY]} ready, {counts[NEEDS]} {NEEDS}, {counts[NOT]} {NOT}")
    print(f"  all {len(info['named'])} buttons the rows name are Buttons in the committed {LAYOUT} ({len(info['buttons'])} there)")
    print(f"  --dry-run at {len(RESOLUTIONS)} resolutions, with and without --bridge-copy: only the verbs {ALL_VERBS} and, with the copy, bridgepost.py's, every shot -MaxWidth 0, no real name; estimated minutes with the copy: "
          + ", ".join(f"{k} {v}" for k, v in estimates.items()))
    print("  the gate refuses in all eight bad cases without a packet, and the run's own door with it, no folder made; pictures keep their full size at every resolution; the index holds relative links only")
    print("  a retake's picture against the committed one: the same through JPEG's noise, a clock, a counter, the world dimmed behind a panel or the game around a crop's panel; changed by a word anywhere else")
    print("  the real run's own code against a fake harness: it launches exactly what the dry run prints, its log holds no secret or name, a vote that does not open stops its chain, --remask redoes the pictures with no verb")
    print("  the Steam overlay: its button found at every resolution over a dark or a busy game, nothing else taken for it; a frame with it is deleted and the run stops, at the probe before any click or at a row's shot")
    print("  NOT checked, and not checkable offline: anything against a real client or server")
    return 0


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="The documentation screenshots of Over Strike 2's HUD: see this file's docstring.")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--selfcheck", action="store_true")
    ap.add_argument("--res", help="WIDTHxHEIGHT, one of the owner's; with --dry-run also all")
    ap.add_argument("--only", type=lambda s: [x.strip() for x in s.split(",") if x.strip()], help="row ids, comma-separated; the rows they need run first, without pictures")
    ap.add_argument("--review-set", action="store_true", help="--only REVIEW_SET: the rows the other resolutions take, for the owner's review only")
    ap.add_argument("--out", help="the output folder of a real run")
    ap.add_argument("--bridge-copy", action="store_true", help="a candidate bridge on a copy of the bridge's data is up: rows that write it may run")
    ap.add_argument("--built", default="", help="the commit the plugin, the bridge and the HUD under test were built from, when not HEAD (only documentation after it)")
    ap.add_argument("--picks", help="the message list to hold the rows to, in messages.txt's format (default: messages.txt beside this file)")
    ap.add_argument("--renamed", action="store_true", help="the client goes by the test name on the server (OsDocName on the test copy, checked with osd_name): no name masks, and the rows whose words name it run")
    ap.add_argument("--keep-raw", action="store_true", help="keep the frames from before the masks: they hold the real name; the first run of a new content needs them, for --remask")
    ap.add_argument("--remask", action="store_true", help="with --res and --out: the masks and crops again, from a --keep-raw run's raw frames, after the boxes were corrected: no game, no server")
    ap.add_argument("--mask-file", nargs=2, metavar=("IN", "OUT"))
    ap.add_argument("--row")
    args = ap.parse_args()
    global RENAMED, BUILT, PICKS
    RENAMED, BUILT = args.renamed, args.built
    if args.picks:
        PICKS = Path(args.picks)
    if args.review_set:
        args.only = [*(args.only or []), *REVIEW_SET]
    info = validate()
    if args.mask_file:
        cmd_mask_file(args)
        return 0
    if args.list:
        warn_problems(info)
        cmd_list(info)
        return 0
    if args.selfcheck:
        require_valid(info)
        return cmd_selfcheck(info)
    if args.dry_run:
        warn_problems(info)
        res_list = RESOLUTIONS if args.res == "all" else [args.res or RESOLUTIONS[0]]
        cmd_dry_run(res_list, args.only, args.bridge_copy, Path(args.out) if args.out else WORK / "out")
        return 0
    if not args.res or not args.out:
        ap.error("the real run takes --res and --out; or --list, --dry-run, --selfcheck")
    if args.remask:
        return cmd_remask(args, info)
    require_valid(info)
    return cmd_run(args, info)


if __name__ == "__main__":
    sys.exit(main())
