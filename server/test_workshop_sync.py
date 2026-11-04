"""Tests for workshop-sync.py beside this file: the owner's limits (the Web API ledger's caps, the 2-second gap, the stops
on 429 / 403 / 5xx / network failures), the configuration clamps, discovery's stop rule, the check's verdicts, the
download caps, the scan's advert rules and its LZ4 reader, the verdicts, the publish plan and the mail.

    python test_workshop_sync.py [path/to/workshop-sync.py]
Runs on Linux and Windows with Python 3.9 or newer, no network: every HTTP answer is a stub. Exit 0 when every check passes.
Not deployed: only workshop-sync.py is copied to the server.
"""
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import types
import urllib.error

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "workshop-sync.py")
sys.path.insert(0, os.path.dirname(os.path.abspath(SRC)))   # keepset.py beside the script, as the deployment's compose file mounts it (also under python -I)
ROOT = tempfile.mkdtemp(prefix="ws-")
for k in [k for k in os.environ if k.startswith("SYNC_")]:
    del os.environ[k]
os.environ.update({"TZ": "Europe/Paris", "SYNC_STATE_DIR": os.path.join(ROOT, "state"), "SYNC_WORK_DIR": os.path.join(ROOT, "work"),
                   "SYNC_BRIDGE_DATA": os.path.join(ROOT, "data")})
m = types.ModuleType("ws")
m.__dict__["__name__"] = "ws"
exec(compile(open(SRC, encoding="utf-8").read(), "workshop-sync", "exec"), m.__dict__)
m.log = lambda message: LOGS.append(message)
LOGS = []
FAILS = []
KEY = "0123456789ABCDEF0123456789ABCDEF"


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


class FakeClock(object):
    def __init__(self, t):
        self.t = t
        self.tz = m.tz_of("Europe/Paris")

    def now(self):
        return self.t

    local = m.Clock.local
    day = m.Clock.day
    at = m.Clock.at
    in_window = m.Clock.in_window


class FakeTime(object):
    """monotonic + sleep for SteamApi: sleeping moves the clock, so the gap is measurable."""

    def __init__(self, clock):
        self.clock = clock
        self.sleeps = []

    def monotonic(self):
        return self.clock.t

    def sleep(self, s):
        self.sleeps.append(s)
        self.clock.t += s


class Resp(object):
    def __init__(self, body, status=200):
        self.body, self.status = body, status

    def read(self):
        return self.body

    def close(self):
        pass


def opener_of(answers, sent):
    """Each call pops the next answer: an int is an HTTP error status, 'net' a network failure, else a JSON body."""
    def opener(request, timeout=30):
        sent.append(request)
        a = answers.pop(0) if answers else {"response": {}}
        if a == "net":
            raise urllib.error.URLError("timed out")
        if isinstance(a, int):
            raise urllib.error.HTTPError(request.full_url, a, "status %d" % a, {}, io.BytesIO(b"x"))
        return Resp(json.dumps(a).encode())
    return opener


def ledger(path, day_cap=10000, t=1793500000.0):
    clock = FakeClock(t)
    led = m.ApiLedger(path, day_cap, clock)
    led.load()
    return led, clock


def api_with(led, clock, answers, key=KEY, gap=2.0):
    sent = []
    ft = FakeTime(clock)
    api = m.SteamApi(led, clock.day(), gap, key=key, opener=opener_of(answers, sent), sleep=ft.sleep, monotonic=ft.monotonic)
    return api, sent, ft


# ---- configuration: the owner's limits can only be lowered ------------------------------------------------------
os.environ.update({"SYNC_API_DAY_CAP": "20000", "SYNC_API_RUN_CAP": "99999", "SYNC_API_GAP_SECONDS": "0.5",
                   "SYNC_DOWNLOAD_MAX": "8000", "SYNC_DOWNLOAD_MAX_MB": "999999", "SYNC_DOWNLOAD_PAUSE": "0.1"})   # the two caps: ignored
c = m.Config()
check("config: a day cap above 10,000 is clamped to 10,000", c.api_day_cap == 10000)
check("config: the run's cap never exceeds the day cap", c.api_run_cap == 10000)
check("config: the gap never drops under 2 s", c.api_gap == 2.0)
check("config: no daily download cap any more (the owner's 36 c): SYNC_DOWNLOAD_MAX and SYNC_DOWNLOAD_MAX_MB mean nothing",
      not hasattr(c, "download_max") and not hasattr(c, "download_bytes"))
check("config: the pause between downloads never drops under 2 s", c.download_pause == 2.0)
for k in ("SYNC_API_DAY_CAP", "SYNC_API_RUN_CAP", "SYNC_API_GAP_SECONDS", "SYNC_DOWNLOAD_MAX", "SYNC_DOWNLOAD_MAX_MB", "SYNC_DOWNLOAD_PAUSE"):
    del os.environ[k]
os.environ["SYNC_API_NIGHT_CAP"] = "50"
c = m.Config()
check("config: defaults are 500 calls a run, 10,000 in 24 h, 2 s - SYNC_API_NIGHT_CAP, a name of before, means nothing (82 b)",
      (c.api_run_cap, c.api_day_cap, c.api_gap) == (500, 10000, 2.0))
del os.environ["SYNC_API_NIGHT_CAP"]
check("config: no time windows - the daily restart at 07:00 to keep clear of (no prefetch any more: the owner's 71 a), runs of 3 h, "
      "the API every 4 h", not hasattr(c, "day_start") and not hasattr(c, "start") and not hasattr(c, "prefetch_at")
      and not hasattr(c, "watcher_state") and (c.daily_restart, c.run_hours, c.api_every) == ((7, 0), 3, 4 * 3600))
c.lower({"api_run_cap": 50, "batch": 5, "publish": False, "api": True})
check("config: run-now lowers a run's limits, and may ask for the API steps", (c.api_run_cap, c.batch, c.publish, c.force_api) == (50, 5, False, True))
c.lower({"api_run_cap": 5000, "batch": 1000})
check("config: run-now cannot raise them", (c.api_run_cap, c.batch) == (50, 5))
c.lower({"api": False, "only_ids": [11, "12", "x"]})
check("config: run-now may leave the API steps out, and limit the run to some maps", c.skip_api and c.only_ids == ["11", "12"])
check("ledger: the hard day cap cannot be raised by a caller", m.ApiLedger("x", 50000, FakeClock(0)).day_cap == 10000)

# ---- the ledger: rolling 24 h, the day, persisted before every call ----------------------------------------------
lp = os.path.join(ROOT, "ledger1.json")
led, clock = ledger(lp, day_cap=10000)
today = clock.day()
for i in range(600):
    led.reserve(today, "GetPublishedFileDetails")
check("ledger: no cap per day but the 24 h one: call 601 of a day goes", led.reserve(today, "QueryFiles") >= 0)
check("ledger: every reserved call is on disk, counted under its day", len(json.load(open(lp))["calls"]) == 601
      and json.load(open(lp))["days"] == {today: 601} and "nights" not in json.load(open(lp)))
led2, _ = ledger(lp, t=clock.t)
check("ledger: the day's count survives a restart (a new process reads the file)", led2.count(today) == 601)
old = os.path.join(ROOT, "ledger-old.json")
json.dump({"schema": 1, "calls": [], "nights": {today: 3}, "stops": {}}, open(old, "w"))
led3, _ = ledger(old, t=clock.t)
check("ledger: a file with the old key 'nights' and no 'days' is not read - no Web API call until the deploy's edit (82 b)",
      led3.data is None and "does not parse" in (led3.broken or ""))
capped, sent_c, _ft = api_with(ledger(os.path.join(ROOT, "ledger-run.json"))[0], FakeClock(1793500000.0), [{"response": {}}] * 5)
capped.run_cap = 3
for i in range(3):
    capped.call("GetPublishedFileDetails", m.DETAILS_URL, data=[("itemcount", 1)])
try:
    capped.call("GetPublishedFileDetails", m.DETAILS_URL, data=[("itemcount", 1)])
    check("api: the run's cap - its 4th call of 3 is refused, and nothing is sent", False)
except m.StopApi as e:
    check("api: the run's cap - its 4th call of 3 is refused, and nothing is sent", "this run's cap of 3" in str(e) and len(sent_c) == 3)
# the rolling day: 10,000 across days
lp = os.path.join(ROOT, "ledger2.json")
led, clock = ledger(lp, day_cap=10000)
led.data["calls"] = [[clock.t - 3600 - i * 0.5, "QueryFiles", 200] for i in range(9999)]
led.data["days"] = {"2026-10-31": 9999}
led.save()
led.reserve(clock.day(), "QueryFiles")
try:
    led.reserve(clock.day(), "QueryFiles")
    check("ledger: the 10,001st call within 24 h is refused, whatever day it is", False)
except m.StopApi as e:
    check("ledger: the 10,001st call within 24 h is refused, whatever day it is", "10000 Web API calls in 24 hours" in str(e))
clock.t += 86400
led.reserve(clock.day(), "QueryFiles")
check("ledger: 24 h later the old calls have rolled off (a call exactly 24 h old included)", led.last_24h() == 1)
# an unreadable ledger means no calls at all
lp = os.path.join(ROOT, "ledger3.json")
open(lp, "w").write("{not json")
led, clock = ledger(lp)
try:
    led.reserve(clock.day(), "QueryFiles")
    check("ledger: an unreadable ledger allows no call", False)
except m.StopApi as e:
    check("ledger: an unreadable ledger allows no call", "does not parse" in str(e))

# ---- the API door: the gap, the stops, the key ------------------------------------------------------------------
lp = os.path.join(ROOT, "ledger4.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [{"response": {"publishedfiledetails": []}}] * 5)
for i in range(5):
    api.details(["1", "2"])
gaps = [sent[i] for i in range(5)]
check("api: 5 calls made one by one", len(sent) == 5)
check("api: at least 2 s between calls (4 waits of 2 s on a frozen clock)", ft.sleeps == [2.0] * 4)
check("api: an honest User-Agent naming the tool", all("cs2-workshop-sync" in r.get_header("User-agent") for r in sent))
check("api: GetPublishedFileDetails is a POST without the key", all(r.data is not None and KEY not in r.full_url for r in sent))
for code in (429, 403, 401):
    lp = os.path.join(ROOT, "ledger-%d.json" % code)
    led, clock = ledger(lp)
    api, sent, ft = api_with(led, clock, [code, {"response": {}}])
    try:
        api.details(["1"])
        check("api: HTTP %d stops the day" % code, False)
    except m.StopApi as e:
        check("api: HTTP %d stops the day" % code, "HTTP %d" % code in str(e))
    check("api: HTTP %d is sent once, never retried" % code, len(sent) == 1)
    try:
        api.details(["1"])
        check("api: after HTTP %d no further call goes out" % code, False)
    except m.StopApi:
        check("api: after HTTP %d no further call goes out" % code, len(sent) == 1)
    led2, _ = ledger(lp, t=clock.t)
    try:
        led2.reserve(clock.day(), "QueryFiles")
        check("api: the HTTP %d stop is in the ledger, so a restart cannot resume it today" % code, False)
    except m.StopApi as e:
        check("api: the HTTP %d stop is in the ledger, so a restart cannot resume it today" % code, "stopped earlier today" in str(e))
# 5xx: three in a row stop (the retries count)
lp = os.path.join(ROOT, "ledger-5xx.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [502, 503, 500, {"response": {}}])
try:
    api.details(["1"])
    check("api: 3 server errors in a row stop the day", False)
except m.StopApi as e:
    check("api: 3 server errors in a row stop the day", "server errors" in str(e) and len(sent) == 3)
check("api: the 5xx retries wait 30 s and 90 s", ft.sleeps[:1] == [30] and 90 in ft.sleeps)
# 5xx: a single one is retried and the run goes on
lp = os.path.join(ROOT, "ledger-5xx1.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [503, {"response": {"publishedfiledetails": [{"publishedfileid": "7", "result": 1}]}}])
got = api.details(["7"])
check("api: one 503 is retried and the answer used", "7" in got and len(sent) == 2 and not led.stopped(clock.day()))
# 5 in a run stop, even not in a row
lp = os.path.join(ROOT, "ledger-5xx5.json")
led, clock = ledger(lp)
answers = []
for i in range(5):
    answers += [500, {"response": {}}]
api, sent, ft = api_with(led, clock, answers)
stopped = None
for i in range(6):
    try:
        api.details(["1"])
    except m.StopApi as e:
        stopped = str(e)
        break
check("api: 5 server errors in a run stop it", stopped is not None and "5 server errors" in stopped)
# network failures
lp = os.path.join(ROOT, "ledger-net.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, ["net"] * 6)
stopped = None
for i in range(3):
    try:
        api.details(["1"])
    except m.StopApi as e:
        stopped = str(e)
        break
    except m.ApiError:
        pass
check("api: 5 network failures stop the day", stopped is not None and "5 network failures" in stopped)
# other 4xx: given up at once, not retried, the run goes on
lp = os.path.join(ROOT, "ledger-400.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [400, {"response": {}}])
try:
    api.details(["1"])
    check("api: HTTP 400 is given up without a retry", False)
except m.StopApi:
    check("api: HTTP 400 is given up without a retry", False)
except m.ApiError:
    check("api: HTTP 400 is given up without a retry", len(sent) == 1 and not led.stopped(clock.day()))
# the key: only on QueryFiles, never in a message
lp = os.path.join(ROOT, "ledger-key.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [{"response": {"total": 0, "publishedfiledetails": []}}])
api.query_files(["Armsrace", "Deathmatch", "Wingman"], "*", 100)
q = sent[0].full_url
check("api: QueryFiles carries the key, app 730, the three tags, newest first, 100 a page, the cursor",
      ("key=" + KEY) in q and "appid=730" in q and "requiredtags%5B2%5D=Wingman" in q and "query_type=1" in q
      and "numperpage=100" in q and "cursor=%2A" in q and "match_all_tags=false" in q)
check("api: the redactor hides the key", KEY not in api.redact("url ...key=%s&x" % KEY))
api2, sent2, _ = api_with(led, clock, [], key=None)
try:
    api2.query_files(["Armsrace"], "*", 100)
    check("api: no key, no QueryFiles", False)
except m.StopApi as e:
    check("api: no key, no QueryFiles", "no Web API key" in str(e))
check("key: a malformed secret file reads as no key", m.read_key(os.path.join(ROOT, "nokey")) is None)
open(os.path.join(ROOT, "key.txt"), "w").write(KEY + "\n")
check("key: the secret file's key is read, whitespace stripped", m.read_key(os.path.join(ROOT, "key.txt")) == KEY)


# ---- discovery: newest first, stops at the first page with nothing new ---------------------------------------------
def page(ids, cursor):
    return {"response": {"total": 1000, "next_cursor": cursor, "publishedfiledetails": [
        {"publishedfileid": str(i), "title": "map %s" % i, "file_size": "1048576", "time_created": 1793400000,
         "time_updated": 1793400000, "tags": [{"tag": "Armsrace"}], "consumer_appid": 730} for i in ids]}}


lp = os.path.join(ROOT, "ledger-disc.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [page([10, 9, 8], "c2"), page([7, 6, 5], "c3"), page([4, 3, 2], "c4"), page([1], "")])
st = m.State(os.path.join(ROOT, "st1.json"))
cfg = m.Config()
d = m.discover(api, cfg, {"7": "listed", "6": "listed", "5": "listed"}, st, clock.day(), clock.t + 3600, now=lambda: clock.t)
check("discovery: page 1 new, page 2 all known -> stops after 2 pages", d["pages"] == 2 and d["new"] == ["10", "9", "8"])
check("discovery: says why it stopped", "page 2 held nothing new" in d["stop"])
check("discovery: the cursor of page 1 is sent with page 2", "cursor=c2" in sent[1].full_url)
check("discovery: new items are remembered as queued with their details",
      st.item("9")["status"] == "queued" and st.item("9")["size"] == 1048576 and st.item("9")["ws"] == "ok")
api, sent, ft = api_with(led, clock, [page([11, 10, 9], "c2"), page([8, 7], "c3")])
d = m.discover(api, cfg, {"7": "listed", "6": "listed", "5": "listed"}, st, clock.day(), clock.t + 3600, now=lambda: clock.t)
check("discovery: the next day, an item seen before is not new (stop after the page with only id 11 new)",
      d["new"] == ["11"] and d["pages"] == 2)
cfg.max_pages = 1
api, sent, ft = api_with(led, clock, [page([20, 21], "c2"), page([22], "")])
d = m.discover(api, cfg, {}, st, clock.day(), clock.t + 3600, now=lambda: clock.t)
check("discovery: SYNC_MAX_PAGES bounds it", d["pages"] == 1 and "SYNC_MAX_PAGES" in d["stop"])

# ---- the check: removed, changed, the 10 % guard -------------------------------------------------------------------
cfg = m.Config()
st = m.State(os.path.join(ROOT, "st2.json"))
listed = [str(i) for i in range(100, 160)]
det = {w: {"publishedfileid": w, "result": 1, "visibility": 0, "time_updated": 1700000000} for w in listed}
det["101"] = {"publishedfileid": "101", "result": 9}
det["102"].update(visibility=2)
det["103"].update(banned=True)
det["104"].update(time_updated=1793000000)       # updated after SYNC_CHANGED_SINCE (2026-10-25)
j = m.judge_listed(det, listed, st, cfg, "2026-11-01")
check("check: gone, hidden and banned are told apart", sorted(j["removed"]) == [("101", "removed"), ("102", "hidden"), ("103", "banned")])
check("check: a map updated since SYNC_CHANGED_SINCE is changed at the first check", j["changed"] == ["104"])
check("check: the others get a baseline", st.data["baseline"].get("100") == 1700000000 and "104" not in st.data["baseline"])
det["100"]["time_updated"] = 1700000500
j = m.judge_listed(det, listed, st, cfg, "2026-11-02")
check("check: later, a map updated past its baseline is changed", "100" in j["changed"])
check("check: 3 gone of 60 is believed", not j["suspicious"])
for w in listed[:10]:
    det[w] = {"publishedfileid": w, "result": 9}
j = m.judge_listed(det, listed, st, cfg, "2026-11-02")
check("check: over 10% of 50+ gone is not believed (the bridge's own rule)", j["suspicious"])

# ---- downloads: the day's items and volume, persisted ---------------------------------------------------------------
st = m.State(os.path.join(ROOT, "st3.json"))
b = m.DownloadBudget(st, "2026-11-01")
for i in range(3000):
    b.spend(1 << 30, str(10000 + i))
check("downloads: no daily cap (the owner's 36 c) - after 3,000 downloads and 3,000 GB in a day the next is allowed",
      b.allows("1")[0] and b.today["items"] == 3000 and b.today["bytes"] == 3000 << 30)
b.spend(10, "7")
b.spend(10, "7")
check("downloads: the bug brake - a map downloaded twice today is not downloaded a third time today, another one is",
      not b.allows("7")[0] and "the bug brake" in b.allows("7")[1] and b.allows("8")[0])
st.save()
st2 = m.State(os.path.join(ROOT, "st3.json")).load()
check("downloads: the book and the brake survive a restart", not m.DownloadBudget(st2, "2026-11-01").allows("7")[0])
check("downloads: the next day, the map may come again", m.DownloadBudget(st2, "2026-11-02").allows("7")[0])
b4 = m.DownloadBudget(st2, "z")
b4.refuse_for_today("Rate Limit Exceeded")
st2.save()
st5 = m.State(os.path.join(ROOT, "st3.json")).load()
check("downloads: a rate limit ends the day's downloads, also for a later run that day",
      not m.DownloadBudget(st5, "z").allows()[0] and "Rate Limit Exceeded" in m.DownloadBudget(st5, "z").allows("1")[1])
check("downloads: the next day starts clean", m.DownloadBudget(st5, "zz").allows()[0])
check("downloads: what stops them", m.STEAMCMD_STOP_RX.search("Rate Limit Exceeded") and m.STEAMCMD_STOP_RX.search("Too many requests")
      and not m.STEAMCMD_STOP_RX.search("Access Denied") and m.ACCESS_DENIED_RX.search("Access Denied"))

# ---- the scan: adverts by the rules of 2026-10-24 -------------------------------------------------------------------
cmds = m.commands_in(b'say "Hey guys! Want to open some free cases? Use code PICHET on Clash.gg for 3 FREE cases"\x00m_x')
check("scan: a Clash.gg promo is a gambling ad", m.advert_of(cmds, False)[0] == "gambling ad")
check("scan: 'say connect <ip>' is a chat advert", m.advert_of(m.commands_in(b"say connect 203.0.113.6:27015"), False)[0] == "chat advert")
check("scan: a bind to connect with a client-command entity is a client connect",
      m.advert_of(m.commands_in(b'bind w "connect 203.0.113.52:27215; bind w +forward"'), True)[0] == "client connect")
check("scan: a bind to connect run on the server only is not listed",
      m.advert_of(m.commands_in(b'bind w "connect 203.0.113.52:27215"'), False)[0] == "server connect")
check("scan: a bare connect with a client-command entity is a client connect",
      m.advert_of(m.commands_in(b"connect 203.0.113.4:27015"), True)[0] == "client connect")
check("scan: a bare server-side connect is not listed (CS2 refuses it)", m.advert_of(m.commands_in(b"connect 203.0.113.4:27015"), False)[0] == "server connect")
check("scan: an ordinary map has none", m.advert_of(m.commands_in(b'say "Leave a like if you enjoyed the map !"'), False) == (None, None))


def lz4_literals(data):
    """A valid LZ4 block of literals only, then one match: compressor stand-in for the test."""
    out = bytearray()
    lit = len(data)
    out.append((15 if lit >= 15 else lit) << 4 | 0)
    if lit >= 15:
        n = lit - 15
        while n >= 255:
            out.append(255)
            n -= 255
        out.append(n)
    out += data
    return bytes(out)


blob = b"say connect 203.0.113.4:27015\x00point_clientcommand\x00" * 3
check("scan: the LZ4 reader decodes a literal block", m.lz4_block(lz4_literals(blob), len(blob)) == blob)
match = bytes([0x41]) + b"abcd" + bytes([4, 0])           # 4 literals, then a match of 5 at offset 4
check("scan: the LZ4 reader copies an overlapping match", m.lz4_block(match, 64) == b"abcdabcda")

# a fake KV3 v5 entity lump inside a resource, inside a map VPK, inside the item's VPK
kv3 = bytearray(b"\x053VK" + b"\x00" * 16 + (1).to_bytes(4, "little") + b"\x00" * 96)
comp = lz4_literals(blob)
kv3[72:76] = len(blob).to_bytes(4, "little")
kv3[76:80] = len(comp).to_bytes(4, "little")
kv3 = bytes(kv3) + comp
res = bytearray(16)
res[8:12] = (8).to_bytes(4, "little")             # block offset, relative to byte 8
res[12:16] = (1).to_bytes(4, "little")             # one block
block = b"DATA" + (8).to_bytes(4, "little") + len(kv3).to_bytes(4, "little")   # offset from the block's offset field
resource = bytes(res) + block + kv3
check("scan: kv3_strings finds the commands in a v5 LZ4 entity lump", b"say connect 203.0.113.4" in m.kv3_strings(resource))


def vpk_of(files):
    """A VPK v1 holding `files` {path: bytes}, data inline (archive 0x7fff)."""
    tree, data = bytearray(), bytearray()
    by_ext = {}
    for path, body in files.items():
        folder, name = path.rsplit("/", 1)
        stem, ext = name.rsplit(".", 1)
        by_ext.setdefault(ext, {}).setdefault(folder, []).append((stem, body))
    for ext, folders in by_ext.items():
        tree += ext.encode() + b"\x00"
        for folder, items in folders.items():
            tree += folder.encode() + b"\x00"
            for stem, body in items:
                tree += stem.encode() + b"\x00"
                tree += (0).to_bytes(4, "little") + (0).to_bytes(2, "little") + (0x7FFF).to_bytes(2, "little")
                tree += len(data).to_bytes(4, "little") + len(body).to_bytes(4, "little") + (0xFFFF).to_bytes(2, "little")
                data += body
            tree += b"\x00"
        tree += b"\x00"
    tree += b"\x00"
    return (0x55AA1234).to_bytes(4, "little") + (1).to_bytes(4, "little") + len(tree).to_bytes(4, "little") + bytes(tree) + bytes(data)


def kv3_resource(text):
    body = bytearray(b"\x053VK" + b"\x00" * 16 + (1).to_bytes(4, "little") + b"\x00" * 96)
    packed = lz4_literals(text)
    body[72:76] = len(text).to_bytes(4, "little")
    body[76:80] = len(packed).to_bytes(4, "little")
    body = bytes(body) + packed
    head = bytearray(16)
    head[8:12] = (8).to_bytes(4, "little")
    head[12:16] = (1).to_bytes(4, "little")
    return bytes(head) + b"DATA" + (8).to_bytes(4, "little") + len(body).to_bytes(4, "little") + body


inner = vpk_of({"maps/adtest/entities/default_ents.vents_c": kv3_resource(b"connect 203.0.113.4:27015\x00point_clientcommand\x00")})
item_dir = os.path.join(ROOT, "item1")
os.makedirs(item_dir)
open(os.path.join(item_dir, "1_dir.vpk"), "wb").write(vpk_of({"maps/adtest.vpk": inner, "materials/x.vmat_c": b"m"}))
s = m.scan_item(item_dir)
check("scan: a map VPK is found and named", s["verdict"] == "map" and s["maps"] == ["adtest"])
check("scan: its entity lump's connect with a client-command entity makes it a client connect", s["advert"] == "client connect")
item_dir2 = os.path.join(ROOT, "item1b")
os.makedirs(item_dir2)
open(os.path.join(item_dir2, "1b_dir.vpk"), "wb").write(vpk_of({"maps/adtest.vpk": vpk_of(
    {"maps/adtest/entities/default_ents.vents_c": resource})}))
check("scan: a 'say connect' in the entity lump is a chat advert", m.scan_item(item_dir2)["advert"] == "chat advert")
pack_dir = os.path.join(ROOT, "item2")
os.makedirs(pack_dir)
open(os.path.join(pack_dir, "2_dir.vpk"), "wb").write(vpk_of({"models/a/b.vmdl_c": b"x", "models/a/c.vmdl_c": b"y"}))
check("scan: models and no map is a pack", m.scan_item(pack_dir)["verdict"] == "pack")
empty_dir = os.path.join(ROOT, "item3")
os.makedirs(empty_dir)
open(os.path.join(empty_dir, "3_dir.vpk"), "wb").write(vpk_of({"materials/a/b.vmat_c": b"x"}))
check("scan: no map and no models is empty", m.scan_item(empty_dir)["verdict"] == "empty")
legacy_dir = os.path.join(ROOT, "item4")
os.makedirs(legacy_dir)
open(os.path.join(legacy_dir, "4_legacy.bin"), "wb").write(b"x")
check("scan: only a _legacy.bin is a CS:GO-era upload", m.scan_item(legacy_dir)["verdict"] == "legacy")

# ---- verdicts ------------------------------------------------------------------------------------------------------
N = "2026-11-01"
ok_scan = {"verdict": "map", "maps": ["x"], "advert": None}
fv = m.final_verdict
check("verdict: a pass", fv({"scan": ok_scan, "loads": [{"verdict": "pass", "level": "x", "mode": "armsrace"}]}, N)[0] == "pass")
check("verdict: a pass after a crash is a (flaky) pass",
      fv({"scan": ok_scan, "loads": [{"verdict": "crash"}, {"verdict": "pass", "level": "x"}]}, N)[1]["flaky"])
check("verdict: one crash is not a verdict yet", fv({"scan": ok_scan, "loads": [{"verdict": "crash"}]}, N)[0] == "pending")
k, d = fv({"scan": ok_scan, "loads": [{"verdict": "crash"}] * 3}, N)
check("verdict: 3 of 3 crashes is a crasher with crashes 3", k == "crasher" and d["crashes"] == 3 and "all 3" in d["evidence"])
k, d = fv({"scan": ok_scan, "loads": [{"verdict": "freeze"}, {"verdict": "rejected"}, {"verdict": "rejected"}]}, N)
check("verdict: 1 freeze of 3 is a crasher with crashes 1", k == "crasher" and d["crashes"] == 1 and "1 of 3" in d["evidence"])
check("verdict: 3 error maps make a non-map", fv({"scan": ok_scan, "loads": [{"verdict": "errormap"}] * 3}, N)[0] == "nonmap")
check("verdict: a stock map instead of its own is a stub",
      fv({"scan": ok_scan, "loads": [{"verdict": "rejected", "level": "de_dust2"}] * 3}, N)[1]["kind"] == "stub")
check("verdict: an aborted load does not count", fv({"scan": ok_scan, "loads": [{"verdict": "abort"}] * 5}, N)[0] == "pending")
check("verdict: a pack never loads", fv({"scan": {"verdict": "pack", "models": 3}}, N) [0] == "nonmap")
k, d = fv({"scan": dict(ok_scan, advert="chat advert", advert_sample="say connect 203.0.113.4"), "loads": [{"verdict": "pass"}]}, N)
check("verdict: a loading map with a chat advert goes on broken.json", k == "advert" and d["reason"] == "advertises another server")
k, d = fv({"scan": dict(ok_scan, advert="server connect"), "loads": [{"verdict": "pass", "level": "x"}]}, N)
check("verdict: a server-side connect alone is still a pass", k == "pass")
k, d = fv({"scan": ok_scan, "loads": [{"verdict": "pass", "chat": ["connect 203.0.113.8:27015"]}]}, N)
check("verdict: an advert seen in chat on a test load counts", k == "advert")
check("verdict: kick", fv({"scan": ok_scan, "loads": [{"verdict": "kick"}]}, N)[0] == "kick")

# ---- the publish plan ----------------------------------------------------------------------------------------------
maps = [{"rank": 1, "id": 111, "name": "a", "sizeMb": 5}, {"rank": 2, "id": 222, "name": "b", "sizeMb": 7}]
broken = {"schema": "x", "maps": [], "packs": [], "removed": [], "blocked": []}
files = {"maps": maps, "broken": broken, "modes": {"deathmatch": [], "wingman": []}, "search": {"111": "a 111", "222": "b 222"}}
st = m.State(os.path.join(ROOT, "st4.json"))


def rec(wid, tags, subs, title=None):
    return {"title": title or "map %s" % wid, "tags": tags, "size": 41 << 20, "subs": subs, "lifetime": subs, "description": "nice map",
            "preview": "p"}


outcomes = [
    ("901", "pass", {"level": "x"}, rec("901", ["Armsrace", "Deathmatch"], 5)),
    ("902", "pass", {"level": "y"}, rec("902", ["Armsrace"], 50)),
    ("903", "pass", {"level": "z"}, rec("903", ["Wingman"], 1)),
    ("904", "nonmap", {"kind": "pack", "detail": "models only"}, rec("904", ["Armsrace"], 0)),
    ("905", "crasher", {"crashes": 3, "evidence": "crashes the server while loading - all 3"}, rec("905", ["Armsrace"], 0)),
    ("906", "advert", {"reason": "posts a gambling ad", "evidence": "posts a gambling ad in chat", "sample": "clash.gg"},
     rec("906", ["Armsrace"], 0)),
    ("222", "crasher", {"crashes": 3, "evidence": "crashes after its update"}, rec("222", ["Armsrace"], 0)),
]
details = {"111": {"publishedfileid": "111", "result": 9, "tags": [{"tag": "Wingman"}], "subscriptions": 9}}
texts, summary = m.plan_publish(files, st, N, outcomes, [("111", "removed")], True, details)
newmaps = json.loads(texts["maps.json"])
check("publish: Arms Race passes are appended, most subscribed first, ranks go on",
      [(e["rank"], e["id"]) for e in newmaps[2:]] == [(3, 902), (4, 901)] and newmaps[:2] == maps)
check("publish: maps.json is written in its own format (indent 1, a final newline)", texts["maps.json"] == json.dumps(newmaps, indent=1, ensure_ascii=False) + "\n")
check("publish: sizeMb in whole MB", newmaps[2]["sizeMb"] == 41)
check("publish: search.json gets the new maps", "902" in json.loads(texts["search.json"]) and "nice" in json.loads(texts["search.json"])["902"])
nb = json.loads(texts["broken.json"])
check("publish: a pack goes on packs with its kind", [p["id"] for p in nb["packs"]] == [904] and nb["packs"][0]["kind"] == "pack")
check("publish: a crasher and an advert go on maps", {e["id"] for e in nb["maps"]} == {905, 906, 222})
check("publish: the advert has its reason, crashes 0", [e for e in nb["maps"] if e["id"] == 906][0]["reason"] == "posts a gambling ad")
check("publish: a listed map that turned bad keeps its rank on broken.json", [e for e in nb["maps"] if e["id"] == 222][0]["rank"] == 2)
check("publish: a listed map gone from the Workshop goes to the bridge as a verdict, never into broken.json", nb.get("removed", []) == [])
check("publish: ...but it is in the summary, for the bridge's report and the mail", summary["removed"] == [("111", 1, "a", "removed")])
check("publish: broken.json keeps its format (indent 2)", texts["broken.json"] == json.dumps(nb, indent=2, ensure_ascii=False) + "\n")
dm = json.loads(texts["maps-deathmatch.json"])
wm = json.loads(texts["maps-wingman.json"])
check("publish: the Deathmatch list gets the pass tagged Deathmatch, in the maps.json shape",
      [e["id"] for e in dm] == [901] and set(dm[0]) == {"rank", "id", "name", "sizeMb"})
check("publish: the Wingman list gets the Wingman pass (rank by popularity)", [e["id"] for e in wm] == [903])
check("publish: the removed listed map is not backfilled into a mode list", 111 not in [e["id"] for e in wm])
files2 = {"maps": maps, "broken": broken, "modes": {"deathmatch": [{"rank": 1, "id": 7, "name": "q", "sizeMb": 1},
                                                                {"name": "de_dust2", "official": True}], "wingman": []},
          "search": None}
details2 = {"111": {"publishedfileid": "111", "result": 1, "tags": [{"tag": "Deathmatch"}], "subscriptions": 9}}
texts2, summary2 = m.plan_publish(files2, st, N, [], [], True, details2)
dm2 = json.loads(texts2["maps-deathmatch.json"])
check("publish: backfill adds a listed map that carries the tag, keeps the old entries and the official one",
      [e.get("id") for e in dm2 if e.get("id")] == [111, 7] and dm2[-1] == {"name": "de_dust2", "official": True})
check("publish: nothing to write when nothing changed", m.plan_publish(files2, st, N, [], [], False, {})[0] == {})
# blocked.json (77 a): the maps blocked for good, a file of their own - known, never a candidate, never published by a test
fb = dict(files, blocked=[{"id": 902, "name": "b", "map": "b", "reason": "kicks everybody"}, 905])
tb, sb = m.plan_publish(fb, st, N, outcomes, [], True, details)
check("blocked.json: a blocked map that passed is not appended, a blocked crasher not added to broken.json",
      902 not in [e["id"] for e in json.loads(tb["maps.json"])] and 905 not in [e["id"] for e in json.loads(tb["broken.json"])["maps"]])
check("blocked.json: its ids are known, as broken:blocked; without the file nothing is",
      m.known_ids(fb, m.Config()).get("902") == "broken:blocked" and m.known_ids(fb, m.Config()).get("905") == "broken:blocked"
      and "902" not in m.known_ids(files, m.Config()))
check("publish: search.json gets the mode lists' new maps too, whatever their mode", "903" in json.loads(texts["search.json"]) and "nice" in json.loads(texts["search.json"])["903"]
      and "wingman" in json.loads(texts["search.json"])["903"])
st.data["items"]["7"] = {"title": "q old", "tags": ["Deathmatch"], "description": "red walls"}
files3 = dict(files2, search={"111": "a 111", "222": "b 222"})
texts3, _ = m.plan_publish(files3, st, N, [], [], False, {})
check("publish: a map on a mode list before search.json covered it gets its text from the state, the other texts stay, an official entry gets none",
      json.loads(texts3["search.json"]) == {"111": "a 111", "222": "b 222", "7": "q 7 deathmatch red walls"} and list(texts3) == ["search.json"])
files3["search"] = json.loads(texts3["search.json"])
check("publish: ...and then there is nothing to write", m.plan_publish(files3, st, N, [], [], False, {})[0] == {})

# ---- the mail ------------------------------------------------------------------------------------------------------
r = {"day": N, "manual": False, "api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day",
     "discovery": {"skipped": "stopped"}, "check": {"asked": 10, "calls": 1}, "downloads": {"ok": 0}, "tests": {"rows": []},
     "published": {}, "limits": {"api_run": 3, "api_run_cap": 500, "api_24h": 3, "api_day_cap": 10000}}
subject, body = m.mail_text(r, m.Config())
check("mail: a stop is in the subject and the body", "STOPPED: HTTP 429" in subject and "STOPPED: Steam's Web API answered HTTP 429" in body)
check("mail: the limits are reported", "Web API calls this run 3 of 500, last 24 h 3 of 10000" in body)

# ---- the backlog: strictly most subscribed first, whatever the mode; its size and estimate in the mail -----------------
items = {"1": {"tags": ["Deathmatch"], "subs": 100, "size": 10 << 20}, "2": {"tags": ["Deathmatch"], "subs": 90, "size": 10 << 20},
         "3": {"tags": ["Wingman"], "subs": 5, "size": 10 << 20}, "4": {"tags": ["Armsrace", "Deathmatch"], "subs": 50, "size": 10 << 20},
         "5": {"tags": ["Wingman", "Deathmatch"], "subs": 95, "size": 10 << 20}, "6": {"tags": ["Armsrace"], "subs": 1, "size": 10 << 20}}
o = m.order_backlog(items, list(items))
check("backlog: strictly by subscribers across the modes", o == ["1", "5", "2", "4", "3", "6"])
c = m.Config()
bs = m.backlog_stats(items, list(items), c)
check("backlog: counted per mode", bs["items"] == 6 and bs["by_mode"] == {"armsrace": 2, "deathmatch": 4, "wingman": 2})
c = m.Config()
big = {str(i): {"tags": ["Wingman"], "subs": 1, "size": 100 << 20} for i in range(5255)}
both = m.backlog_stats(big, list(big), c)
check("backlog: no time windows, about 22 h a day at 31 s a map, 2,554 a day - no daily cap: 3 days for the backlog of 2026-11-01",
      both["hours"] == 22 and both["per_day"] == 2554 and both["days"] == 3)
check("backlog: a pace measured counts", m.backlog_stats(big, list(big), c, pace=60.0)["days"] == 4)
r2 = dict(r, api_stop=None, backlog=bs, backlog_until=" (until about 2026-11-04)")
subject2, body2 = m.mail_text(r2, m.Config())
check("mail: the backlog's size, per mode, and its estimate", "Backlog: 6 item(s) waiting" in body2 and "Deathmatch 4, Wingman 2, Arms Race 2" in body2
      and "until about 2026-11-04" in body2 and "strictly by subscribers" in body2)
rows = ["PASS     map%d (%d) - wingman, loads as x in 1s" % (i, i) for i in range(60)] + ["CRASHER  bad (1) - crashes the server"]
r3 = dict(r, api_stop=None, tests={"rows": rows}, published={"modes": {"wingman": [(str(i), "m") for i in range(60)]}, "files": ["maps-wingman.json"]})
subject3, body3 = m.mail_text(r3, m.Config())
check("mail: a long run gives counts, what did not pass, a taste of what did", "60 PASS, 1 CRASHER." in body3 and "CRASHER  bad (1)" in body3
      and "and 50 more passes" in body3 and "map30 " not in body3)
check("mail: a long mode list is counted", "maps-wingman.json  60 new maps" in body3)
check("mail: a run says when it started", "(the run of 03:30)" in m.mail_text(dict(r, started_hm="03:30"), m.Config())[0])
# the publish steps of a run add up
mp = m.merge_published({"listed": [("1", 1, "a", 5)], "modes": {"wingman": [("2", "b")]}, "files": ["maps.json"], "reload": "x"},
                       {"listed": [("3", 2, "c", 5)], "modes": {"wingman": [("4", "d")], "deathmatch": [("5", "e")]}, "files": ["maps.json", "search.json"], "reload": "y"})
check("publish: a run's batches add up", [x[0] for x in mp["listed"]] == ["1", "3"] and [x[0] for x in mp["modes"]["wingman"]] == ["2", "4"]
      and mp["files"] == ["maps.json", "search.json"] and mp["reload"] == "y" and list(mp["modes"]["deathmatch"]) == [("5", "e")])
check("service: a backlog waits while anything is queued or staged without a verdict",
      m.backlog_waits({"items": {"1": {"status": "queued"}}}) and m.backlog_waits({"items": {"1": {"status": "tested"}}})
      and not m.backlog_waits({"items": {"1": {"status": "tested", "verdict": "pass"}, "2": {"status": "published"}}}))
# nobody on: a map change shows 0 for a moment, so once somebody was seen it takes the quiet minutes
real_humans = m.humans_on
seen = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
m.humans_on = lambda cfg: seen.pop(0)
fc = FakeClock(1793500000.0)
waits = []
nq = m.Run(m.Config(), fc, manual=True, sleep=lambda n: (waits.append(n), setattr(fc, "t", fc.t + n)))
check("wait: somebody on now means waiting, and 0 humans alone is not enough for the next 10 minutes", nq.wait_for_nobody("x") and len(waits) == 10)
seen[:] = [0]
waits[:] = []
nq2 = m.Run(m.Config(), fc, manual=True, sleep=lambda n: (waits.append(n), setattr(fc, "t", fc.t + n)))
check("wait: nobody seen this run: at once", nq2.wait_for_nobody("x") and waits == [])
m.humans_on = real_humans

# ---- the merge check: a publish only ever adds to the live files ---------------------------------------------------
old = json.dumps([{"rank": 1, "id": 5, "name": "a", "sizeMb": 1}])
check("merge: an append passes", m.merge_problems("maps.json", old.encode(), json.dumps(json.loads(old) + [{"rank": 2, "id": 6, "name": "b", "sizeMb": 1}])) == [])
check("merge: a changed entry is refused", m.merge_problems("maps.json", old.encode(), json.dumps([{"rank": 1, "id": 5, "name": "X", "sizeMb": 1}])) != [])
check("merge: a rank given twice is refused", m.merge_problems("maps.json", old.encode(), json.dumps(json.loads(old) + [{"rank": 1, "id": 6, "name": "b", "sizeMb": 1}])) != [])
ob = json.dumps({"schema": "x", "maps": [{"id": 1}], "packs": []})
check("merge: broken.json with entries added passes", m.merge_problems("broken.json", ob.encode(), json.dumps({"schema": "x", "maps": [{"id": 1}, {"id": 2}], "packs": [{"id": 3}]})) == [])
check("merge: broken.json losing an entry is refused", m.merge_problems("broken.json", ob.encode(), json.dumps({"schema": "x", "maps": [], "packs": []})) != [])
check("merge: a mode list may re-rank but never lose a map",
      m.merge_problems("maps-wingman.json", json.dumps([{"rank": 1, "id": 7}]).encode(), json.dumps([{"rank": 1, "id": 8}, {"rank": 2, "id": 7}])) == []
      and m.merge_problems("maps-wingman.json", json.dumps([{"rank": 1, "id": 7}]).encode(), json.dumps([{"rank": 1, "id": 8}])) != [])

# ---- the bridge: the verdicts report and the reload, against a stand-in bridge on localhost -------------------------
import http.server
import threading

GOT = []


class FakeBridge(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode("utf-8")
        GOT.append((self.path, self.headers.get("Content-Type"), body))
        answer = b"3 checked, 1 not downloadable, 1 newly blocked" if self.path.startswith("/removed/") else b"reload: maps: 9 maps"
        self.send_response(200)
        self.send_header("Content-Length", str(len(answer)))
        self.end_headers()
        self.wfile.write(answer)

    def log_message(self, *a):
        pass


srv = http.server.HTTPServer(("127.0.0.1", 0), FakeBridge)
threading.Thread(target=srv.serve_forever, daemon=True).start()
os.environ.update({"SYNC_BRIDGE_URL": "http://127.0.0.1:%d" % srv.server_port, "SYNC_BRIDGE_SECRET": "s3cr3t"})
one = m.Run.__new__(m.Run)
one.cfg = m.Config()
ans = one.report_verdicts([("111", "ok"), ("222", "removed"), ("333", "hidden")])
check("bridge: the verdicts go to POST /removed/<secret> in the plugin's format (magic line, id TAB verdict)",
      GOT[0][0] == "/removed/s3cr3t" and GOT[0][2] == "gg-removed\t1\n111\tok\n222\tremoved\n333\thidden\n"
      and GOT[0][1].startswith("text/plain"))
check("bridge: its answer is kept for the mail", ans.startswith("3 checked"))
check("bridge: POST /reload/<secret> after a publish", one.reload() == "reload: maps: 9 maps" and GOT[1][0] == "/reload/s3cr3t")
# ---- a run's publish and finish, end to end, on a bridge folder of its own and the stand-in bridge -------------------
data = os.path.join(ROOT, "data")
os.makedirs(data, exist_ok=True)
live_maps = [{"rank": 1, "id": 111, "name": "a", "sizeMb": 5}, {"rank": 2, "id": 222, "name": "b", "sizeMb": 7}]
live_broken = {"schema": "x", "maps": [], "packs": [], "removed": [{"id": 3659287341}], "blocked": []}
open(os.path.join(data, "maps.json"), "w", encoding="utf-8").write(json.dumps(live_maps, indent=1) + "\n")
open(os.path.join(data, "broken.json"), "w", encoding="utf-8").write(json.dumps(live_broken, indent=2) + "\n")
open(os.path.join(data, "search.json"), "w", encoding="utf-8").write(json.dumps({"111": "a 111", "222": "b 222"}, indent=0) + "\n")
nt = m.Run(m.Config(), FakeClock(1793500000.0), manual=True)
nt.ledger.load()
nt.state.load()
nt.verdicts = [("111", "ok"), ("222", "removed")]
pass_rec = {"title": "new map", "tags": ["Armsrace", "Wingman"], "size": 20 << 20, "subs": 3, "lifetime": 3, "description": "d",
            "status": "tested", "verdict": "pass", "updated": 1793400000}
pack_rec = {"title": "a pack", "tags": ["Armsrace"], "size": 1 << 20, "status": "tested", "verdict": "nonmap", "preview": "", "description": ""}
nt.state.data["items"] = {"901": pass_rec, "902": pack_rec}
GOT[:] = []
files0 = m.bridge_files(nt.cfg)
check("blocked.json: none in the bridge's folder - none blocked, and no problem", files0["blocked"] == [])
open(os.path.join(data, "blocked.json"), "w", encoding="utf-8").write(json.dumps([{"id": 3076804331, "name": "x", "map": "x", "reason": "r"}]))
check("blocked.json: read when it is there", m.blocked_ids(m.bridge_files(nt.cfg)) == {"3076804331": "blocked"})
open(os.path.join(data, "blocked.json"), "w", encoding="utf-8").write('{"blocked": 5}')
try:
    m.bridge_files(nt.cfg)
    check("blocked.json: one that is no list stops the publish (ValueError)", False)
except ValueError as error:
    check("blocked.json: one that is no list stops the publish (ValueError)", "blocked.json" in str(error))
os.remove(os.path.join(data, "blocked.json"))
nt.publish(files0, [("901", "pass", {"level": "x"}, pass_rec), ("902", "nonmap", {"kind": "pack", "detail": "models"}, pack_rec)],
           [("222", "removed")], {})
after = json.load(open(os.path.join(data, "maps.json"), encoding="utf-8"))
check("run: the pass is appended to the live maps.json as #3", after[:2] == live_maps and after[2]["id"] == 901 and after[2]["rank"] == 3)
check("run: the pack joined broken.json's packs, the removed seed untouched",
      [p["id"] for p in json.load(open(os.path.join(data, "broken.json")))["packs"]] == [902]
      and json.load(open(os.path.join(data, "broken.json")))["removed"] == [{"id": 3659287341}])
check("run: the Wingman list is created with the pass", [e["id"] for e in json.load(open(os.path.join(data, "maps-wingman.json")))] == [901])
check("run: the verdicts went to /removed, then the files were reloaded",
      [g[0] for g in GOT] == ["/removed/s3cr3t", "/reload/s3cr3t"] and "222\tremoved" in GOT[0][2])
check("run: the old files are backed up", len(os.listdir(os.path.join(nt.cfg.state_dir, "backups"))) == 1)
check("run: search.json, written after the reload (the reload does not read it), holds the new map",
      "901" in json.load(open(os.path.join(data, "search.json"), encoding="utf-8")) and "search.json" in nt.report["published"]["files"])
check("run: both items are marked published", pass_rec["status"] == "published" and pack_rec["status"] == "published")
# a file that changed under the plan is planned again; a result that is not the live files plus additions is refused
nt2 = m.Run(m.Config(), FakeClock(1793500100.0), manual=True)
nt2.ledger.load()
nt2.state.load()
rec3 = {"title": "third", "tags": ["Armsrace"], "size": 1 << 20, "subs": 1, "status": "tested", "verdict": "pass", "updated": 1}
stale = m.bridge_files(nt2.cfg)
grown = after + [{"rank": 4, "id": 950, "name": "added by the release", "sizeMb": 1}]
open(os.path.join(data, "maps.json"), "w", encoding="utf-8").write(json.dumps(grown, indent=1, ensure_ascii=False) + "\n")
nt2.publish(stale, [("903", "pass", {"level": "y"}, rec3)], [], {})
now_maps = json.load(open(os.path.join(data, "maps.json"), encoding="utf-8"))
check("run: a maps.json that grew meanwhile is merged into, never written from the older copy",
      now_maps[:4] == grown and now_maps[4]["id"] == 903 and now_maps[4]["rank"] == 5)
nt.finish()
rep_txt = [f for f in os.listdir(os.path.join(nt.cfg.state_dir, "reports")) if f.endswith(".txt")]
check("run: the report and its text are written; a run without a problem mails nothing (the owner's 34)",
      rep_txt and nt.report["mail"].startswith("NOT sent: no problem") and nt.report["issues"] == [])
srv.shutdown()
del os.environ["SYNC_BRIDGE_URL"], os.environ["SYNC_BRIDGE_SECRET"]

# ---- a whole run in batches, on stand-ins for SteamCMD and the test engine -----------------------------------------------
class FakeSteam(object):
    logins = 0
    answers = {}
    asked = []

    def __init__(self, exe, install_dir, log_path, home=None):
        self.install_dir = install_dir

    def start(self):
        FakeSteam.logins += 1
        return True, "logged in anonymously"

    def download(self, wid):
        FakeSteam.asked.append(wid)
        if wid in FakeSteam.answers:
            return False, FakeSteam.answers[wid], 0
        folder = os.path.join(self.install_dir, "steamapps", "workshop", "content", "730", wid)
        os.makedirs(folder, exist_ok=True)
        open(os.path.join(folder, wid + "_dir.vpk"), "w").write("x")
        return True, "OK", 1000

    def stop(self):
        pass


class FakeEngine(object):
    def __init__(self, cfg, work_dir, home):
        self.starts, self.up, self.stops = 0, False, 0

    def alive(self):
        return self.up

    def start(self):
        self.starts += 1
        self.up = True
        return True, "test engine up"

    def stop(self):
        self.up = False
        self.stops += 1


# the test engine's stop, with real processes: a wrapper that dies at the SIGTERM over an engine that does not (the night of 2026-11-02)
if os.name == "posix":
    stop_root = tempfile.mkdtemp()
    te = m.TestEngine(m.Config(), stop_root, stop_root)
    te.QUIT_WAIT, te.STOP_WAIT = 0, 1
    stubborn = "import signal,time; signal.signal(signal.SIGTERM, signal.SIG_IGN); time.sleep(300)"
    te.proc = subprocess.Popen(["bash", "-c", '%s -c "%s" & wait' % (sys.executable, stubborn)], start_new_session=True)
    time.sleep(1.0)
    stop_pgid = te.proc.pid
    check("engine stop: the group is up before the stop", m.group_alive(stop_pgid))
    te.stop()
    check("engine stop: a frozen engine goes with its wrapper, by SIGKILL on the group", not m.group_alive(stop_pgid) and te.proc is None)
    farm_bin = os.path.join(stop_root, "farm", "game", "bin")
    os.makedirs(farm_bin)
    shutil.copy("/bin/sleep", os.path.join(farm_bin, "cs2"))
    stale_engine = subprocess.Popen([os.path.join(farm_bin, "cs2"), "300"])
    with open(os.path.join(stop_root, "farm", "game", "cs2.sh"), "w") as h:
        h.write("#!/bin/bash\nsleep 300\n")
    os.chmod(os.path.join(stop_root, "farm", "game", "cs2.sh"), 0o755)
    time.sleep(0.3)
    ok_start, why_start = te.start(timeout=2)
    check("engine start: nothing answering is a refusal (%s)" % why_start, not ok_start and "did not answer" in why_start)
    check("engine start: an engine still running from the test tree is killed first", stale_engine.wait(5) == -9)
    check("engine start: its own attempt is gone again", te.proc is None)
    shutil.rmtree(stop_root, ignore_errors=True)

real = {k: getattr(m, k) for k in ("SteamCmd", "TestEngine", "scan_item", "build_farm", "kill_stale_engines", "test_load", "humans_on", "read_key", "check", "patch_version")}
m.SteamCmd, m.TestEngine = FakeSteam, FakeEngine
m.scan_item = lambda folder: {"verdict": "map", "maps": ["lvl"], "models": 0, "files": 1, "names": []}
m.build_farm = lambda *a: "test tree built"
m.kill_stale_engines = lambda root: 0
m.patch_version = lambda game_dir: "1.41.9.4"
m.read_key = lambda path: None
m.check = lambda api, ids, deadline, now=None: ({}, [])
loads = []
humans_now = [0]
def fake_load(engine, wid, levels, tags, hold, stop_check):
    loads.append(wid)
    if stop_check():
        return {"id": wid, "verdict": "abort", "reason": "somebody came on the game server"}
    return {"id": wid, "verdict": "pass", "reason": "loads", "level": "lvl", "load_s": 1.0, "mode": "wingman", "bots": 0}
m.test_load = fake_load
m.humans_on = lambda cfg: humans_now[0]
run_root = os.path.join(ROOT, "run")
run_data = os.path.join(run_root, "data")
os.makedirs(run_data)
os.environ.update({"SYNC_STATE_DIR": os.path.join(run_root, "state"), "SYNC_WORK_DIR": os.path.join(run_root, "work"), "SYNC_BRIDGE_DATA": run_data})
os.makedirs(os.path.join(run_root, "state"))
open(os.path.join(run_data, "maps.json"), "w", encoding="utf-8").write(json.dumps([{"rank": 1, "id": 11, "name": "a", "sizeMb": 5}], indent=1) + "\n")
open(os.path.join(run_data, "broken.json"), "w", encoding="utf-8").write(json.dumps({"schema": "x", "maps": [], "packs": [], "removed": [], "blocked": []}, indent=2) + "\n")
open(os.path.join(run_data, "search.json"), "w", encoding="utf-8").write(json.dumps({"11": "a 11"}, indent=0) + "\n")
T0 = 1793540000.0   # an afternoon
run_items = {str(1000 + i): {"status": "queued", "ws": "ok", "app": 730, "tags": ["Wingman"], "subs": 10 * (i + 1), "lifetime": 1, "size": 1000, "created": T0 - 100 * m.DAY,
                             "title": "m%d" % i, "description": "", "first": "2026-11-01", "why": "new"} for i in range(7)}
run_items["2000"] = dict(run_items["1000"], subs=1, created=T0 - m.DAY, title="new upload")
json.dump({"schema": 1, "items": run_items, "baseline": {}, "downloads": {}}, open(os.path.join(run_root, "state", "state.json"), "w"))
cfg = m.Config()
cfg.batch, cfg.download_pause = 2, 0
fc = FakeClock(T0)
nr = m.Run(cfg, fc, manual=True)
FakeSteam.answers = {"1002": "Rate Limit Exceeded"}   # Steam pushes back at the sixth: no daily cap ends a run any more, that does
rep = nr.run()
FakeSteam.answers = {}
check("run: a new upload first, then strictly by subscribers", FakeSteam.asked[:5] == ["2000", "1006", "1005", "1004", "1003"])
check("run: every download was tested", sorted(loads) == sorted(FakeSteam.asked[:5]))
check("run: 5 downloads in batches of 2: 3 logins, one engine for each batch with work", FakeSteam.logins == 3 and rep["downloads"]["ok"] == 5)
check("run: Steam's push-back ends it, with the reason", "Rate Limit Exceeded" in rep["downloads"].get("stop", ""))
wing = json.load(open(os.path.join(run_data, "maps-wingman.json"), encoding="utf-8"))
check("run: every batch published (5 maps on the list, the report adds them up)",
      len(wing) == 5 and len(rep["published"]["modes"]["wingman"]) == 5 and "maps-wingman.json" in rep["published"]["files"])
check("run: staging is empty again", os.listdir(os.path.join(run_root, "work", "staging", "steamapps", "workshop", "content", "730")) == [])
state_after = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
check("run: the rest waits, the day's book says 5", state_after["downloads"]["2026-11-01"]["items"] == 5
      and sum(1 for r in state_after["items"].values() if r["status"] in ("queued", "retry")) == 3)
check("run: the mail's estimate counts what is left", rep["backlog"]["items"] == 3 and "Backlog: 3 item(s)" in open(rep["report_path"][:-5] + ".txt", encoding="utf-8").read())
check("run: the state remembers the run, and when the API steps last ran", state_after["last_run"]["ended"] > 0 and state_after.get("last_api") == T0)
check("run: the estimate counts on the day without the restarts' stretches", rep["backlog"]["hours"] == 22)
check("run: Steam's push-back is a problem the mail names (not sent here: no SMTP host)",
      any("SteamCMD pushed back" in i for i in rep["issues"]) and rep["mail"].startswith("NOT sent: no SMTP host"))

# the issues-only mail (the owner's 34): what counts, what does not, the words, once a day per problem
quiet = {"day": "2026-11-03", "started_hm": "12:00", "ended_hm": "15:00", "downloads": {"stop": "the run's deadline (time is kept for this batch's tests)"},
         "tests": {"stop": "somebody was on the server until the deadline"}, "serve": {"stop": "the run's deadline"}, "errors": []}
check("issues: a run that ended at its deadline, or waited for players, is no problem", m.issues_of(quiet) == [])
cases = [
    ({"api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day"}, "steam-webapi"),
    ({"downloads": {"stop": "SteamCMD answered 'Rate Limit Exceeded' - no more downloads today"}}, "steamcmd-pushback"),
    ({"serve": {"stop": "SteamCMD answered 'Access Denied' twice in a row - no more downloads today"}}, "steamcmd-pushback"),
    ({"downloads": {"stop": "3 downloads in a row failed - no more in this run"}}, "downloads-in-a-row"),
    ({"downloads": {"stop": "SteamCMD did not log in anonymously: no answer"}}, "steamcmd"),
    ({"tests": {"stop": "test engine did not answer in 90 s", "engine_failed": "test engine did not answer in 90 s"}}, "test-engine"),
    ({"tests": {"stop": "the test tree could not be built: disk full"}}, "tests-stopped"),
    ({"discovery": {"skipped": "failed: HTTP 500"}}, "discovery-failed"),
    ({"check": {"suspicious": True}}, "check-not-believed"),
    ({"serve": {"handover_failed": [("305", "its folder is empty")]}}, "handover-failed"),
    ({"serve": {"stop": "205.0 GB of hand-overs wait for the server already"}}, "handovers-waiting"),
    ({"disk": {"low": "80.0 GB free on the game's volume (the mark is 100.0 GB)"}}, "disk-low"),
    ({"watcher_keep": {"computed": "the next morning", "problems": ["ratings.json is 80 h old (at most 72 h)"]}}, "deletion-refused:"),
    ({"errors": ["the bridge's data cannot be read (x) - nothing done in this run"]}, "error:"),
]
for extra, key in cases:
    got = m.issues_of(dict(quiet, **extra))
    check("issues: %s counts (%s)" % (key, got[0][1][:70] if got else "nothing"), len(got) == 1 and got[0][0].startswith(key) and got[0][2])
sent_mails = []
real_send = m.send_mail
m.send_mail = lambda cfg, subject, body: sent_mails.append((subject, body))
icfg = m.Config()
icfg.state_dir = os.path.join(ROOT, "issues-state")
os.makedirs(icfg.state_dir)
open(os.path.join(icfg.state_dir, "dashboard-url"), "w").write("https://dash.example/\n")
os.environ.pop("SYNC_DASHBOARD_URL", None)
os.environ.pop("SYNC_DASHBOARD_URL", None)
os.environ["SYNC_STATE_DIR"], old_state = icfg.state_dir, os.environ.get("SYNC_STATE_DIR")
check("issues: the dashboard's address is SYNC_DASHBOARD_URL alone - a <state>/dashboard-url file means nothing (the owner's 85)",
      m.Config().dashboard_url == "")
os.environ["SYNC_DASHBOARD_URL"] = "https://dash.example/"
check("issues: ... and set there, it is the one the mails point to", m.Config().dashboard_url == "https://dash.example/")
del os.environ["SYNC_DASHBOARD_URL"]
os.environ["SYNC_STATE_DIR"] = old_state
icfg.dashboard_url = "https://dash.example/"
ic = FakeClock(T0)
probs = m.issues_of(dict(quiet, api_stop="Steam's Web API answered HTTP 429 to QueryFiles", report_path="/x/r.json"))
out1 = m.mail_issues(icfg, ic, probs, dict(quiet, report_path="/x/r.json"))
check("issues: a problem is mailed - what went wrong, what the sync did, the dashboard, the report",
      out1.startswith("sent") and len(sent_mails) == 1 and "What went wrong: Steam's Web API pushed back" in sent_mails[0][1]
      and "What the sync did: Every Web API step stopped" in sent_mails[0][1] and "The dashboard: https://dash.example/" in sent_mails[0][1]
      and "/x/r.txt" in sent_mails[0][1] and "pushed back" in sent_mails[0][0])
ic.t += 3600
out2 = m.mail_issues(icfg, ic, probs, quiet)
check("issues: the same problem an hour later is not mailed again", out2.startswith("NOT sent: every problem") and len(sent_mails) == 1)
probs2 = probs + m.issues_of(dict(quiet, disk={"low": "50 GB free"}))
out3 = m.mail_issues(icfg, ic, probs2, quiet)
check("issues: a new problem is mailed, the ongoing one named as still going on", out3.startswith("sent") and len(sent_mails) == 2
      and "disk is running low" in sent_mails[1][1] and "Still going on, mailed already in the last day: Steam's Web API" in sent_mails[1][1])
ic.t += 21 * 3600
m.mail_issues(icfg, ic, probs, quiet)
check("issues: a problem that persists is mailed again once a day", len(sent_mails) == 3)
check("issues: no problem, no mail", m.mail_issues(icfg, ic, [], quiet).startswith("NOT sent: no problem") and len(sent_mails) == 3)
crash = m.mail_crash(icfg, ic, ValueError("boom"), False)
check("issues: a run that crashed is mailed from the service", crash.startswith("sent") and "The run failed: ValueError('boom')" in sent_mails[-1][1])
check("issues: ...once a day too", m.mail_crash(icfg, ic, ValueError("boom 2"), False).startswith("NOT sent: every problem"))
m.send_mail = real_send


# the status file for the in-game dashboard (schema 1): written by the run into the bridge's data, atomically
def status_problems(doc):
    """What in `doc` breaks schema 1 (the dashboard's contract: fields may be added, never renamed or dropped)."""
    out = []
    num = (int, float)

    def need(obj, key, kinds, where):
        if not isinstance(obj, dict) or key not in obj:
            out.append("%s.%s missing" % (where, key))
            return None
        if not isinstance(obj[key], kinds):
            out.append("%s.%s is %s" % (where, key, type(obj[key]).__name__))
        return obj[key]
    need(doc, "schema", int, "")
    if doc.get("schema") != 1:
        out.append("schema is not 1")
    need(doc, "updated", int, "")
    now = need(doc, "now", dict, "") or {}
    if now.get("state") not in ("idle", "waiting", "discovery", "check", "ratings", "serve", "download", "test", "publish", "stopped"):
        out.append("now.state is %r" % now.get("state"))
    need(now, "why", str, "now")
    for key, fields in (("map", (("id", str), ("name", str), ("mode", str), ("sizeMb", int))), ("step", (("done", int), ("total", int))),
                        ("run", (("started", int), ("deadline", int), ("kind", str)))):
        v = need(now, key, (dict, type(None)), "now")
        for f, kind in fields if isinstance(v, dict) else ():
            need(v, f, kind, "now." + key)
    today = need(doc, "today", dict, "") or {}
    for f in ("downloads", "bytes", "pass", "rejected", "failedDownloads", "apiCalls"):
        need(today, f, int, "today")
    totals = need(doc, "totals", dict, "") or {}
    for f in ("tested", "pass", "broken", "nonmap"):
        need(totals, f, int, "totals")
    listed = need(totals, "listed", dict, "totals") or {}
    queue = need(doc, "queue", dict, "") or {}
    for f in ("total", "bytes"):
        need(queue, f, int, "queue")
    by_mode = need(queue, "byMode", dict, "queue") or {}
    for mode in ("armsrace", "deathmatch", "wingman"):
        need(listed, mode, int, "totals.listed")
        need(by_mode, mode, int, "queue.byMode")
    need(queue, "eta", (int, type(None)), "queue")
    nxt = need(queue, "next", list, "queue") or []
    if len(nxt) > 25:
        out.append("queue.next holds %d" % len(nxt))
    for e in nxt:
        for f, kind in (("id", str), ("name", str), ("mode", str), ("sizeMb", int), ("why", str)):
            need(e, f, kind, "queue.next[]")
    serve = need(queue, "serve", dict, "queue") or {}
    need(serve, "missing", int, "queue.serve")
    need(serve, "bytes", int, "queue.serve")
    keep = need(doc, "keep", dict, "") or {}
    for e in need(keep, "parts", list, "keep") or []:
        if e.get("part") not in ("up", "rated", "popular", "subscribed", "own", "hud", "queues"):
            out.append("keep.parts[].part is %r" % e.get("part"))
        for f, kind in (("mode", str), ("count", int), ("onDisk", int), ("gb", num)):
            need(e, f, kind, "keep.parts[]")
    need(keep, "totalGb", num, "keep")
    hist = need(doc, "history", dict, "") or {}
    runs, maps_ = need(hist, "runs", list, "history") or [], need(hist, "maps", list, "history") or []
    if len(runs) > 50 or len(maps_) > 200:
        out.append("history holds %d runs, %d maps" % (len(runs), len(maps_)))
    for e in runs:
        for f in ("started", "ended", "kind", "downloads", "bytes", "pass", "rejected", "listed", "removed", "stop"):
            if f not in e:
                out.append("history.runs[].%s missing" % f)
    for e in maps_:
        for f, kind in (("at", int), ("id", str), ("name", str), ("mode", str), ("event", str), ("why", str)):
            need(e, f, kind, "history.maps[]")
        if e.get("event") not in ("listed", "broken", "removed", "changed", "handed-over", "failed"):
            out.append("history.maps[].event is %r" % e.get("event"))
    return out


sp = os.path.join(run_data, "sync-status.json")
sdoc = json.load(open(sp, encoding="utf-8"))
check("status: the run wrote sync-status.json into the bridge's data, schema 1 (%s)" % status_problems(sdoc)[:4], status_problems(sdoc) == [])
check("status: after the run it says idle, no map, no step, no run", sdoc["now"]["state"] == "idle" and sdoc["now"]["map"] is None
      and sdoc["now"]["step"] is None and sdoc["now"]["run"] is None and "has ended" in sdoc["now"]["why"])
check("status: written atomically - no tmp file left beside it", not [n for n in os.listdir(run_data) if n.startswith(".sync-status")])
check("status: today's counts - 5 downloads, 5 passes, the day's API calls", (sdoc["today"]["downloads"], sdoc["today"]["pass"]) == (5, 5)
      and sdoc["today"]["apiCalls"] == state_after.get("api_calls_today", sdoc["today"]["apiCalls"]))
check("status: the totals and the lists - 5 tested, 5 passed, 5 on the Wingman list, 1 on Arms Race", (sdoc["totals"]["tested"],
      sdoc["totals"]["pass"], sdoc["totals"]["listed"]["wingman"], sdoc["totals"]["listed"]["armsrace"]) == (5, 5, 5, 1))
check("status: the queue - the 3 left, in the order the downloads take them, each with why", sdoc["queue"]["total"] == 3
      and [e["id"] for e in sdoc["queue"]["next"]] == [w for w in ("1002", "1001", "1000")] and all(e["why"] for e in sdoc["queue"]["next"])
      and sdoc["queue"]["byMode"]["wingman"] == 3 and sdoc["queue"]["eta"] > T0)
check("status: the run's history line - manual, 5 downloads, 5 passed, 5 listed, Steam's push-back as its stop",
      sdoc["history"]["runs"][0]["kind"] == "manual" and (sdoc["history"]["runs"][0]["downloads"], sdoc["history"]["runs"][0]["pass"],
      sdoc["history"]["runs"][0]["listed"]) == (5, 5, 5) and "Rate Limit" in (sdoc["history"]["runs"][0]["stop"] or ""))
ev = sdoc["history"]["maps"]
check("status: the map events, newest first - 5 listed, the failed download (%s)" % sorted(set(e["event"] for e in ev)),
      sum(1 for e in ev if e["event"] == "listed") == 5 and any(e["event"] == "failed" and e["id"] == "1002" for e in ev)
      and ev == sorted(ev, key=lambda e: -e["at"]))
check("status: the day's failed downloads are counted", sdoc["today"]["failedDownloads"] == 1)
st_obj = m.Status(cfg, fc)
st_obj.path = os.path.join(run_data, "tick-status.json")
real_tick = m.STATUS_TICK
m.STATUS_TICK = 0.05
st_obj.set(state="download", why="x")
st_obj.start_ticker()
m.time.sleep(0.4)
st_obj.stop_ticker()
m.STATUS_TICK = real_tick
tick_doc = json.load(open(st_obj.path, encoding="utf-8"))
check("status: the ticker writes the file while a run is active, and stops", tick_doc["now"]["state"] == "download" and st_obj.ticker is None)
# somebody joins during a test: the engine stops at once, nothing runs until the quiet time is over
loads[:] = []
FakeSteam.logins = 0
fc2 = FakeClock(T0 + m.DAY)
cfg2 = m.Config()
cfg2.batch, cfg2.download_pause, cfg2.quiet = 3, 0, 600
slept = []
def sleeper(n):
    slept.append(n)
    fc2.t += n
    if len(slept) == 3:
        humans_now[0] = 0
nj = m.Run(cfg2, fc2, manual=True, sleep=sleeper)
engines = []
real_engine = FakeEngine
class SpyEngine(FakeEngine):
    def __init__(self, *a):
        FakeEngine.__init__(self, *a)
        engines.append(self)
m.TestEngine = SpyEngine
def joiner(engine, wid, levels, tags, hold, stop_check):
    loads.append(wid)
    if len(loads) == 1:
        humans_now[0] = 2
    if stop_check():
        return {"id": wid, "verdict": "abort", "reason": "somebody came on the game server"}
    return {"id": wid, "verdict": "pass", "reason": "loads", "level": "lvl", "load_s": 1.0, "mode": "wingman", "bots": 0}
m.test_load = joiner
rep2 = nj.run()
check("join: the engine went down when somebody joined", engines and engines[0].stops >= 1 and engines[0].starts >= 2)
check("join: it waited until 10 minutes after they left, and then finished the batch", sum(slept) >= 600 and len(rep2["tests"]["rows"]) == 3)
# a rate limit: no more downloads that day, also for a later run; one access-denied item is that item
m.test_load = fake_load
fc3 = FakeClock(T0 + 2 * m.DAY)
for w, r in list(state_after["items"].items()):
    pass
state3 = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
state3["items"]["3001"] = dict(run_items["1000"], subs=5000, title="denied")
state3["items"]["3002"] = dict(run_items["1000"], subs=4000, title="fine")
state3["items"]["3003"] = dict(run_items["1000"], subs=3000, title="limited")
state3["items"]["3004"] = dict(run_items["1000"], subs=2000, title="never reached")
json.dump(state3, open(os.path.join(run_root, "state", "state.json"), "w"))
FakeSteam.answers = {"3001": "Access Denied", "3003": "Rate Limit Exceeded"}
cfg3 = m.Config()
cfg3.batch, cfg3.download_pause = 10, 0
rep3 = m.Run(cfg3, fc3, manual=True).run()
state3 = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
check("stop: one access-denied item is failed, the next one goes on", state3["items"]["3001"]["status"] == "failed" and state3["items"]["3002"]["status"] == "published")
check("stop: a rate limit ends the downloads, the next item is not asked for", "Rate Limit Exceeded" in rep3["downloads"]["stop"] and state3["items"]["3004"]["status"] == "queued")
rep4 = m.Run(cfg3, FakeClock(T0 + 2 * m.DAY + 3600), manual=True).run()
check("stop: a later run that day downloads nothing", rep4["downloads"]["ok"] == 0 and "Rate Limit Exceeded" in rep4["downloads"]["stop"])
FakeSteam.answers = {}
# a SteamCMD stall (the owner's 86 a): one is routine - a fresh session takes the next map and the downloads go on, nothing to mail; a
# second stall in a run ends its downloads, and that, or one map stalling twice in a day, is a problem for the mail
humans_now[0] = 0
state5 = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
for wid, subs in (("5001", 99000), ("5002", 98000), ("5003", 97000)):
    state5["items"][wid] = dict(run_items["1000"], subs=subs, title="stall %s" % wid)
json.dump(state5, open(os.path.join(run_root, "state", "state.json"), "w"))
FakeSteam.asked, FakeSteam.answers = [], {"5001": m.STALL + " 15m00s"}
logins5 = FakeSteam.logins
rep5 = m.Run(cfg3, FakeClock(T0 + 5 * m.DAY), manual=True).run()
check("stall: one stall is routine - a fresh SteamCMD session, the next map at once, the downloads go on (%s, %d logins)"
      % (FakeSteam.asked[:3], FakeSteam.logins - logins5), FakeSteam.asked[:3] == ["5001", "5002", "5003"]
      and "stalled" not in str(rep5["downloads"].get("stop") or "") and [tuple(x) for x in rep5.get("stalls") or []] == [("5001", 1)]
      and FakeSteam.logins - logins5 >= 2)
check("stall: ... and nothing about SteamCMD for the mail", not [k for k, _w, _d in m.issues_of(rep5) if k.startswith("steamcmd")])
state6 = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
for wid, subs in (("6001", 99500), ("6002", 99400), ("6003", 99300)):
    state6["items"][wid] = dict(run_items["1000"], subs=subs, title="stall %s" % wid)
json.dump(state6, open(os.path.join(run_root, "state", "state.json"), "w"))
FakeSteam.asked, FakeSteam.answers = [], {"6001": m.STALL + " 15m00s", "6002": m.STALL + " 15m00s"}
rep6 = m.Run(cfg3, FakeClock(T0 + 6 * m.DAY), manual=True).run()
check("stall: a second stall in a run ends its downloads (6003 not asked) and is a problem for the mail (%s)" % rep6["downloads"].get("stop"),
      FakeSteam.asked[:2] == ["6001", "6002"] and "6003" not in FakeSteam.asked and "stalled twice" in str(rep6["downloads"].get("stop"))
      and [k for k, _w, _d in m.issues_of(rep6)].count("steamcmd-stalls") == 1
      and not [k for k, _w, _d in m.issues_of(rep6) if k == "steamcmd"])
check("stall: one map stalling twice in a day is a problem too; once is not",
      [k for k, _w, _d in m.issues_of({"stalls": [["7001", 2]]})] == ["steamcmd-stalls"] and m.issues_of({"stalls": [["7001", 1]]}) == [])
FakeSteam.answers = {}
# a listed map gone from the Workshop (the seed filter gone, the owner's 77 a): the run's check has it, its event comes once - when the
# bridge's removed.json does not hold that verdict yet - and verdicts that do not reach the bridge are a problem (the only check, 74 a)
json.dump({"checked": "x", "verdicts": {"11": "ok"}}, open(os.path.join(run_data, "removed.json"), "w"))
m.check = lambda api, ids, deadline, now=None: ({"11": {"publishedfileid": "11", "result": 9}}, [])
cfg7 = m.Config()
cfg7.batch, cfg7.download_pause = 1, 0
cfg7.lower({"api": True})
r7 = m.Run(cfg7, FakeClock(T0 + 7 * m.DAY), manual=True)
r7.run()
gone_events = lambda run: sum(1 for e in run.state.data.get("history", {}).get("maps") or [] if e["id"] == "11" and e["event"] == "removed")  # noqa: E731
check("check: a listed map gone from the Workshop is in the run's check, and says so in the history (%d)" % gone_events(r7),
      [tuple(x) for x in r7.report["check"]["removed"]] == [("11", "removed")] and gone_events(r7) == 1)
json.dump({"checked": "x", "verdicts": {"11": "removed"}}, open(os.path.join(run_data, "removed.json"), "w"))
r8 = m.Run(cfg7, FakeClock(T0 + 7 * m.DAY + 3600), manual=True)
r8.run()
check("check: ... at the next check still in its check, but no second event: the bridge holds the verdict (%d)" % gone_events(r8),
      [tuple(x) for x in r8.report["check"]["removed"]] == [("11", "removed")] and gone_events(r8) == 1)
check("issues: verdicts that do not reach the bridge are a problem for the mail - nothing else tells it (%s)"
      % (r8.report.get("published") or {}).get("verdicts"), "verdicts-not-reported" in [k for k, _w, _d in m.issues_of(r8.report)])
m.check = lambda api, ids, deadline, now=None: ({}, [])
os.remove(os.path.join(run_data, "removed.json"))
# no time windows: a run lasts SYNC_RUN_HOURS, cut short before the restarts; the service starts one whenever nobody plays and work waits
fcd = FakeClock(T0 + 3 * m.DAY)
day0 = fcd.at((0, 0))
hm = lambda ts: fcd.local(ts).strftime("%H:%M")  # noqa: E731
fcd.t = day0 + 12 * 3600
check("run: at noon a run lasts 3 h", hm(m.Run(m.Config(), fcd).deadline) == "15:00")
fcd.t = day0 + 5 * 3600
check("run: at 05:00 it ends at 06:45, before the 07:00 restart", hm(m.Run(m.Config(), fcd).deadline) == "06:45")
fcd.t = day0 + 2 * 3600
check("run: at 02:00 it runs its 3 h - no prefetch to keep clear of (the owner's 71 a)", hm(m.Run(m.Config(), fcd).deadline) == "05:00")
cfgs = m.Config()
check("service: no start in the daily restart's stretch (06:45-07:10)", m.start_blocked(cfgs, fcd, day0 + 6.9 * 3600) is not None
      and m.start_blocked(cfgs, fcd, day0 + 7.2 * 3600) is None)
check("service: the night is the sync's - a run may start at 03:10 and at 03:30, nothing waits for a prefetch (the owner's 71 a)",
      m.start_blocked(cfgs, fcd, day0 + (3 * 60 + 10) * 60) is None and m.start_blocked(cfgs, fcd, day0 + 3.5 * 3600) is None
      and not hasattr(m, "prefetch_due") and not hasattr(m.Run, "wait_for_prefetch"))
check("service: no run with under 20 minutes before the daily restart's cut: at 06:30 it would end at 06:45 (%s) - at 06:20 one starts"
      % m.start_blocked(cfgs, fcd, day0 + 6.5 * 3600), "daily restart" in (m.start_blocked(cfgs, fcd, day0 + 6.5 * 3600) or "")
      and m.start_blocked(cfgs, fcd, day0 + (6 * 60 + 20) * 60) is None)
fcd.t = day0 + 12 * 3600
check("service: work waits while maps are queued, unless Steam stopped today's downloads; or when the API steps are due",
      m.work_waits(cfgs, fcd, {"items": {"1": {"status": "queued"}}, "last_api": fcd.t})
      and not m.work_waits(cfgs, fcd, {"items": {"1": {"status": "queued"}}, "last_api": fcd.t, "downloads": {fcd.day(): {"stop": "x"}}})
      and m.work_waits(cfgs, fcd, {"items": {}, "last_api": fcd.t - 5 * 3600}) and not m.work_waits(cfgs, fcd, {"items": {}, "last_api": fcd.t}))
for k, v in real.items():
    setattr(m, k, v)
os.environ.update({"SYNC_STATE_DIR": os.path.join(ROOT, "state"), "SYNC_WORK_DIR": os.path.join(ROOT, "work"), "SYNC_BRIDGE_DATA": os.path.join(ROOT, "data")})

# ---- acf editing ---------------------------------------------------------------------------------------------------
acf = os.path.join(ROOT, "appworkshop_730.acf")
open(acf, "w").write('"AppWorkshop"\n{\n\t"appid"\t\t"730"\n\t"WorkshopItemsInstalled"\n\t{\n\t\t"1"\n\t\t{\n\t\t\t"timeupdated"\t\t"5"\n\t\t}\n'
                     '\t\t"2"\n\t\t{\n\t\t\t"timeupdated"\t\t"6"\n\t\t}\n\t}\n\t"WorkshopItemDetails"\n\t{\n\t\t"1"\n\t\t{\n\t\t\t"manifest"\t\t"9"\n\t\t}\n\t}\n}\n')
check("acf: items read", m.acf_items(acf) == {"1": 5, "2": 6})
m.acf_forget(acf, ["1"])
check("acf: an item is forgotten in both sections, the rest kept", m.acf_items(acf) == {"2": 6} and '"1"' not in open(acf).read())

# ---- 2026-11-03: the keep set (keepset.py, shared with update-watch.py), the scores, the hand-over, the 223 --------------------------
UW = os.path.join(os.path.dirname(os.path.abspath(SRC)), "update-watch.py")
uw_text, ws_text = open(UW, encoding="utf-8").read(), open(SRC, encoding="utf-8").read()
check("keep set: one copy of the rules - keepset.py; neither script holds its own (the owner's 76 a)",
      m.keepset is not None and "KEEP_TOP = " not in uw_text and "KEEP_TOP = " not in ws_text and "def keep_set(inputs, now" not in ws_text)
sys.modules.setdefault("fcntl", types.ModuleType("fcntl"))
os.environ.setdefault("STEAMAPPDIR", os.path.join(ROOT, "uwroot"))
uw = types.ModuleType("uw")
uw.__dict__["__name__"] = "uw"
exec(compile(uw_text, "update-watch", "exec"), uw.__dict__)
check("keep set: both scripts import the same keepset.py", uw.keepset is m.keepset)
KT = 1793700000.0
kdata = {"maps.json": [{"rank": r, "id": 100 + r, "name": "a%d" % r, "sizeMb": 1} for r in range(1, 130)],
         "maps-deathmatch.json": [{"rank": r, "id": 300 + r, "name": "d%d" % r, "sizeMb": 1} for r in range(1, 30)],
         "maps-wingman.json": [], "broken.json": {"maps": [{"id": 101}]}, "removed.json": {"verdicts": {"102": "removed"}},
         "votes.json": {"110": {"[U:1:1]": 1, "[U:1:2]": -1}, "111": {"[U:1:1]": -2}}, "ours.json": {"maps": [{"id": 900}]},
         "ratings.json": {"version": 1, "fetched": KT - 60, "scores": dict((str(100 + r), [r / 200.0, 1, 0, 1000 - r]) for r in range(1, 130)),
                          "trend": {"armsrace": [str(100 + r) for r in range(60, 130)], "deathmatch": ["310", "311", "999"], "wingman": []}},
         "queues.json": {"version": 1, "random": {"armsrace-new": ["120"], "deathmatch": ["301"]}, "requested": ["150"]},
         "state.json": {"crashes": {"103": 1}}}
check("keep set: both scripts give the same set from the same data", m.safe_keep_set(kdata, KT, "500") == uw.keep_set(kdata, KT, "500")
      and m.safe_keep_set(kdata, KT, "")["problems"] == uw.keep_set(kdata, KT, "")["problems"])
kb = m.safe_keep_set(dict(kdata, **{"blocked.json": [{"id": 105}, 106]}), KT, "500")
k0 = m.safe_keep_set(kdata, KT, "500")
check("keep set: blocked.json's maps (77 a) are left out of the top lists; without the file they are in, and neither is a problem",
      "105" in k0["top"]["armsrace"] and "106" in k0["top"]["armsrace"] and not {"105", "106"} & set(kb["top"]["armsrace"])
      and kb["problems"] == k0["problems"])
kb2 = m.safe_keep_set(dict(kdata, **{"blocked.json": {"blocked": [105]}}), KT, "500")
check("keep set: blocked.json may also hold its list under \"blocked\"", "105" not in kb2["top"]["armsrace"] and "106" in kb2["top"]["armsrace"])

# the scores: IPublishedFileService/GetDetails with includevotes, with the key; a removed item is no score
led, clock = ledger(os.path.join(ROOT, "votes-ledger.json"))
api, sent, ft = api_with(led, clock, [{"response": {"publishedfiledetails": [
    {"publishedfileid": "11", "result": 1, "vote_data": {"score": 0.547, "votes_up": 14, "votes_down": 3}},
    {"publishedfileid": "12", "result": 15}, {"publishedfileid": "13", "result": 1}]}}])
got = api.votes(["11", "12", "13"])
check("votes: the score, the votes and the subscriptions of what the Workshop has; result 15 and no vote_data give none", got == {"11": [0.547, 14, 3, 0]})
check("votes: one keyed GET to IPublishedFileService/GetDetails with includevotes, counted in the ledger as GetDetails",
      len(sent) == 1 and sent[0].full_url.startswith(m.VOTES_URL) and "key=" + KEY in sent[0].full_url and "includevotes=true" in sent[0].full_url
      and "publishedfileids%5B2%5D=13" in sent[0].full_url and led.data["calls"][-1][1] == "GetDetails")

# a run's own steps, on a folder of their own
srv = os.path.join(ROOT, "srv")
sdata, sstate, swork, sgame = (os.path.join(srv, x) for x in ("data", "state", "work", "game"))
gws = os.path.join(sgame, "game", "bin", "linuxsteamrt64", "steamapps", "workshop")
for d in (sdata, sstate, swork, os.path.join(gws, "content", "730"), os.path.join(sgame, "maps", "armsrace")):
    os.makedirs(d, exist_ok=True)
for name, value in kdata.items():
    json.dump(value, open(os.path.join(sdata, name), "w"))
os.environ.update({"SYNC_STATE_DIR": sstate, "SYNC_WORK_DIR": swork, "SYNC_BRIDGE_DATA": sdata, "SYNC_GAME_DIR": sgame,
                   "SYNC_DEPOTCACHE": os.path.join(srv, "depotcache")})
os.makedirs(os.path.join(srv, "depotcache"))
items = {"140": {"status": "skipped", "evidence": "1.5 GB: more than a whole night's download cap (800 MB)", "size": 1 << 30, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
         "141": {"status": "skipped", "evidence": "160.0 GB: more than a whole day's download cap (150.0 GB)", "size": 160 << 30, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
         "142": {"status": "skipped", "evidence": "a CS:GO-era upload (its Workshop file is x): no CS2 map, not downloaded", "size": 1, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
         "143": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 999, "created": 1, "lifetime": 1},
         "144": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1, "lifetime": 1}}
json.dump({"schema": 1, "items": items, "baseline": {}, "downloads": {}}, open(os.path.join(sstate, "state.json"), "w"))
ncfg = m.Config()
nn = m.Run(ncfg, FakeClock(KT), manual=True)
nn.state.load()
nn.requeue_size_skips()
check("the 223: every map skipped as too big for an old daily cap is queued again, 160 GB too - there is no cap now; a CS:GO-era skip stays",
      nn.state.item("140")["status"] == "queued" and nn.state.item("141")["status"] == "queued" and nn.state.item("142")["status"] == "skipped"
      and nn.report["requeued"] == 2)


class VoteApi(object):
    def __init__(self, scores, stop_after=None, trends=None):
        self.scores, self.stop_after, self.by_api, self.asked, self.trends, self.tags = scores, stop_after, {}, [], trends or {}, []

    def votes(self, ids):
        self.by_api["GetDetails"] = self.by_api.get("GetDetails", 0) + 1
        if self.stop_after is not None and self.by_api["GetDetails"] > self.stop_after:
            raise m.StopApi("HTTP 429")
        self.asked += ids
        return dict((w, [self.scores.get(w, 0.0), 1, 0, 5]) for w in ids if w in self.scores)

    def trend(self, tag, cursor, days):
        self.tags.append((tag, days))
        return self.trends.get(tag, []), "", len(self.trends.get(tag, []))


vapi = VoteApi({"143": 0.99, "144": 0.01, "140": 0.5, "105": 0.7}, trends={"Armsrace": ["999", "105", "106"], "Deathmatch": ["301"], "Wingman": []})
nn.ratings(vapi, ["105", "106"])
rj = json.load(open(os.path.join(sdata, "ratings.json")))
check("ratings: the listed maps and every map waiting for its test, scored with their subscriptions and written to the bridge's data with the time",
      set(vapi.asked) == set(["105", "106", "140", "141", "143", "144"]) and rj["version"] == 1 and rj["fetched"] == KT and rj["scores"]["143"] == [0.99, 1, 0, 5]
      and nn.report["ratings"]["complete"])
check("ratings: each mode's Most Popular order over 30 days, by its tag", rj["trend"] == {"armsrace": ["999", "105", "106"], "deathmatch": ["301"], "wingman": []}
      and rj["trend_days"] == 30 and sorted(vapi.tags) == [("Armsrace", 30), ("Deathmatch", 30), ("Wingman", 30)])
nn.ratings(VoteApi({}), ["105"])
check("ratings: once a day - not again within RATINGS_EVERY", "fetched" in nn.report["ratings"]["skipped"])
nn.state.data["ratings"]["fetched"] = KT - 2 * m.DAY
nn.report.pop("ratings")
nn.ratings(VoteApi({"105": 0.1}, stop_after=0), ["105"])
check("ratings: cut short by a stop - the old scores kept, with their old time", nn.state.data["ratings"]["fetched"] == KT - 2 * m.DAY
      and not nn.report["ratings"]["complete"] and nn.state.data["ratings"]["scores"]["143"][0] == 0.99)
saved_ratings = json.loads(json.dumps(nn.state.data["ratings"]))
nn.state.data["ratings"]["fetched"] = KT
nn.state.data["ratings"].pop("trend")
nn.report.pop("ratings")
nn.ratings(VoteApi({"105": 0.7}, trends={"Armsrace": ["105"]}), ["105"])
check("ratings: at once when a list is missing (kept before the lists were three), however fresh",
      nn.report["ratings"].get("complete") and "trend" in nn.state.data["ratings"])
nn.state.data["ratings"] = saved_ratings
nn.state.data["ratings"]["fetched"] = KT
json.dump({"version": 1, "fetched": KT, "scores": dict(kdata["ratings.json"]["scores"], **{"143": [0.99, 1, 0], "144": [0.01, 1, 0]})},
          open(os.path.join(sdata, "ratings.json"), "w"))
nn._cut = None
order = nn.queue({}, {})
check("queue: a waiting map whose score puts it among its mode's best goes first (143), the rest by popularity: %s" % order[:4],
      order[0] == "143" and order.index("143") < order.index("144"))

# the hand-over: a staged download moved, its manifest copied, its marker last; the staging acf forgets it
stg = os.path.join(swork, "staging", "steamapps", "workshop")
os.makedirs(os.path.join(stg, "content", "730", "305"))
open(os.path.join(stg, "content", "730", "305", "305_dir.vpk"), "w").write("vpk!")
open(os.path.join(stg, "appworkshop_730.acf"), "w").write(
    '"AppWorkshop"\n{\n\t"appid"\t\t"730"\n\t"WorkshopItemsInstalled"\n\t{\n\t\t"305"\n\t\t{\n\t\t\t"size"\t\t"4"\n\t\t\t"timeupdated"\t\t"77"\n'
    '\t\t\t"manifest"\t\t"555"\n\t\t}\n\t}\n\t"WorkshopItemDetails"\n\t{\n\t\t"305"\n\t\t{\n\t\t\t"manifest"\t\t"555"\n\t\t\t"timeupdated"\t\t"77"\n'
    '\t\t\t"timetouched"\t\t"78"\n\t\t\t"latest_timeupdated"\t\t"77"\n\t\t\t"latest_manifest"\t\t"555"\n\t\t}\n\t}\n}\n')
open(os.path.join(srv, "depotcache", "730_555.manifest"), "wb").write(b"M")
hdir = os.path.join(swork, "handover")
ok, words = m.hand_over(os.path.join(swork, "staging"), hdir, "305", "d5", os.path.join(srv, "depotcache"))
mk = json.load(open(os.path.join(hdir, "305.json")))
check("hand-over: the folder moved, the manifest copied, the marker names its acf entries and what the folder holds (%s)" % words,
      ok and os.path.isfile(os.path.join(hdir, "305", "305_dir.vpk")) and open(os.path.join(hdir, "305.manifest"), "rb").read() == b"M"
      and mk["installed"] == {"size": "4", "timeupdated": "77", "manifest": "555"} and mk["details"]["latest_manifest"] == "555"
      and (mk["files"], mk["bytes"]) == (1, 4) and not os.path.exists(os.path.join(stg, "content", "730", "305"))
      and m.acf_items(os.path.join(stg, "appworkshop_730.acf")) == {})
check("hand-over: what waits in the folder", m.handovers(hdir) == {"305": 4})
check("hand-over: nothing staged, nothing handed over", m.hand_over(os.path.join(swork, "staging"), hdir, "306", "", "")[0] is False)
uw.HANDOVER_DIR, uw.DEPOTCACHE = hdir, os.path.join(srv, "server-depotcache")
os.makedirs(uw.DEPOTCACHE)
check("hand-over: update-watch.py reads the sync's marker as complete", uw.handover_read(os.path.join(hdir, "305.json"), "305")[0] is not None
      and uw.folder_stats(os.path.join(hdir, "305")) == (mk["files"], mk["bytes"]))

# serve: the keep set's maps the server does not store are fetched and handed over; what it stores, or waits, is not
for wid in ("110", "120", "150", "301", "900"):
    os.makedirs(os.path.join(sgame, "maps", "armsrace", wid))
    link = os.path.join(gws, "content", "730", wid)
    os.symlink(os.path.relpath(os.path.join(sgame, "maps", "armsrace", wid), os.path.dirname(link)), link) if os.name == "posix" else os.makedirs(link)
open(os.path.join(gws, "appworkshop_730.acf"), "w").write('"AppWorkshop"\n{\n\t"WorkshopItemsInstalled"\n\t{\n' + "".join(
    '\t\t"%s"\n\t\t{\n\t\t\t"timeupdated"\t\t"1"\n\t\t}\n' % w for w in ("110", "120", "150", "301", "900", "999")) + '\t}\n}\n')
check("server: what the game server stores - in its acf, with a folder behind its link", sorted(m.server_maps(sgame)) == ["110", "120", "150", "301", "900"])
real_steam = m.SteamCmd
FakeSteam.asked, FakeSteam.answers = [], {}


class AcfSteam(FakeSteam):
    def download(self, wid):
        ok = FakeSteam.download(self, wid)
        acf_path = os.path.join(self.install_dir, "steamapps", "workshop", "appworkshop_730.acf")
        body = '\t\t"%s"\n\t\t{\n\t\t\t"size"\t\t"1"\n\t\t\t"timeupdated"\t\t"9"\n\t\t\t"manifest"\t\t"4%s"\n\t\t}\n' % (wid, wid)
        open(acf_path, "w").write('"AppWorkshop"\n{\n\t"WorkshopItemsInstalled"\n\t{\n' + body + '\t}\n}\n')
        return ok


m.SteamCmd = AcfSteam
m.humans_on = lambda cfg: 0
json.dump(dict(kdata["ratings.json"], fetched=KT), open(os.path.join(sdata, "ratings.json"), "w"))
ncfg2 = m.Config()
ncfg2.download_pause = 0
ns = m.Run(ncfg2, FakeClock(KT), manual=True)
ns.state.load()
want = m.safe_keep_set(m.keep_inputs(sdata), KT)
ns.serve()
handed = sorted(m.handovers(hdir))
missing = sorted(set(want["up"] + want["top"]["armsrace"] + want["top"]["deathmatch"] + want["own"]) - set(["110", "120", "150", "301", "900", "305"]))
check("serve: every keep-set map the server lacks is fetched and handed over (%d), 160 GB too, never one it stores or one waiting already (%s)"
      % (len(missing), dict(ns.report["serve"], diff=sorted(set(missing + ["305"]) ^ set(handed)))), handed == sorted(missing + ["305"]) and not set(FakeSteam.asked) & set(["110", "301", "900", "305"])
      and ns.report["serve"]["ok"] == len(missing))
FakeSteam.asked = []
ns2 = m.Run(ncfg2, FakeClock(KT + 60), manual=True)
ns2.state.load()
ns2.serve()
check("serve: a second run fetches nothing more", FakeSteam.asked == [] and ns2.report["serve"]["missing"] == 0)
# the owner's 71 a: serve also fetches the stored keep-set maps the Workshop has a newer accepted version of - and only those
state_file = os.path.join(sstate, "state.json")
saved_state = open(state_file, encoding="utf-8").read()       # the runs below save it: the later tests get it back as it was
FakeSteam.asked = []
ns7 = m.Run(ncfg2, FakeClock(KT + 90), manual=True)
ns7.state.load()
ns7.state.data["baseline"].update({"110": 50, "120": 1, "150": 60, "301": 0})
ns7.state.data["items"]["150"] = dict(ns7.state.data["items"].get("150") or {}, status="queued", why="changed", updated=80)
ns7.state.data["items"]["900"] = dict(ns7.state.data["items"].get("900") or {}, status="skipped", why="changed", updated=70)
keep7 = m.safe_keep_set(m.keep_inputs(sdata), KT)
miss7, outd7 = m.serve_wants(keep7, m.server_maps(sgame), m.handovers(hdir), ns7.state.data, ncfg2)
check("serve: outdated - a stored map whose tested version is newer than the server's (110) and an own map the Workshop has newer "
      "(900, never tested here); not one as new (120) nor one whose new version waits for its test (150) (%s)" % outd7,
      "110" in keep7["all"] and "150" in keep7["all"] and "120" in keep7["all"] and sorted(outd7) == ["110", "900"] and not miss7)
guard = json.loads(json.dumps(ns7.state.data))
guard["baseline"]["120"] = 30
guard["items"]["120"] = {"verdict": "crasher", "status": "published", "why": "changed", "updated": 30}
check("serve: nor one whose last test failed (120: the Workshop's version now is the one that crashed)",
      "120" not in m.serve_wants(keep7, m.server_maps(sgame), m.handovers(hdir), guard, ncfg2)[1])
check("serve: the status file counts them", m.status_heavy(ns7)["queue"]["serve"]["outdated"] == 2)
ns7.serve()
check("serve: ...both are downloaded and handed over, nothing else (%s)" % FakeSteam.asked,
      sorted(FakeSteam.asked) == ["110", "900"] and set(["110", "900"]) <= set(m.handovers(hdir))
      and ns7.report["serve"]["outdated"] == 2 and ns7.report["serve"]["ok"] == 2)
check("serve: the history says it was the newer version", any(e["id"] == "110" and "newer version" in e["why"]
                                                                for e in ns7.state.data["history"]["maps"]))
FakeSteam.asked = []
ns8 = m.Run(ncfg2, FakeClock(KT + 100), manual=True)
ns8.state.load()
ns8.state.data["baseline"].update({"110": 50})
ns8.serve()
check("serve: a newer version waiting as a hand-over is not fetched again", "110" not in FakeSteam.asked)
open(state_file, "w", encoding="utf-8").write(saved_state)
for w in ("110", "900"):
    for pth in (os.path.join(hdir, w + ".json"), os.path.join(hdir, w + ".manifest")):
        if os.path.exists(pth):
            os.remove(pth)
    shutil.rmtree(os.path.join(hdir, w), ignore_errors=True)


# serve after a run that could not hand over (2026-11-03: 136 of 160 - it looked before SteamCMD had written its acf): what is
# staged with SteamCMD's entry goes without a download, an older staged version is downloaded again, a late acf is handed over
# once SteamCMD has quit, and the cleanup keeps a keep-set pass whose hand-over failed
def unhand(wid):
    for p in (os.path.join(hdir, wid + ".json"), os.path.join(hdir, wid + ".manifest")):
        if os.path.exists(p):
            os.remove(p)
    shutil.rmtree(os.path.join(hdir, wid), ignore_errors=True)


def write_acf(entries):
    open(os.path.join(stg, "appworkshop_730.acf"), "w").write('"AppWorkshop"\n{\n\t"WorkshopItemsInstalled"\n\t{\n' + "".join(
        '\t\t"%s"\n\t\t{\n\t\t\t"size"\t\t"6"\n\t\t\t"timeupdated"\t\t"%s"\n\t\t\t"manifest"\t\t"7%s"\n\t\t}\n' % (w, u, w)
        for w, u in entries.items()) + '\t}\n}\n')


def stage(wid):
    os.makedirs(os.path.join(stg, "content", "730", wid), exist_ok=True)
    open(os.path.join(stg, "content", "730", wid, wid + "_dir.vpk"), "w").write("staged")


class LateAcfSteam(FakeSteam):
    """SteamCMD as on 2026-11-03 at its worst: its acf names the downloads only once it quits."""
    got = []

    def download(self, wid):
        LateAcfSteam.got.append(wid)
        return FakeSteam.download(self, wid)

    def stop(self):
        if LateAcfSteam.got:
            write_acf(dict((w, "200") for w in LateAcfSteam.got))


sa, sb, sc, sd = missing[:4]
for w in (sa, sb, sc, sd):
    unhand(w)
stage(sa)
stage(sb)
write_acf({sa: "50", sb: "50"})
m.ACF_WAIT = 1
m.SteamCmd = LateAcfSteam
FakeSteam.asked, LateAcfSteam.got = [], []
ns3 = m.Run(ncfg2, FakeClock(KT + 120), manual=True)
ns3.state.load()
ns3.state.data["items"].setdefault(sb, {})["updated"] = 100      # the Workshop has a newer version than the staged one
for w in (sa, sc):
    (ns3.state.data["items"].get(w) or {}).pop("updated", None)
ns3.serve()
s3, waiting = ns3.report["serve"], m.handovers(hdir)
check("serve: a map staged already with SteamCMD's entry is handed over without a download; an older staged version and one not "
      "staged are downloaded (%s)" % dict(s3, asked=FakeSteam.asked), sa in waiting and sa not in FakeSteam.asked and s3.get("staged") == 1
      and sorted(FakeSteam.asked) == sorted([sb, sc, sd]))
check("serve: SteamCMD's acf late - every download is handed over once it has quit, none lost",
      all(w in waiting for w in (sb, sc, sd)) and s3["ok"] == 4 and not s3["failed"])
check("serve: the staging acf names none of the handed-over maps afterwards",
      not set(m.acf_items(os.path.join(stg, "appworkshop_730.acf"))) & set([sa, sb, sc, sd]))
check("serve: the marker of the re-downloaded map carries the new version, not the staged one",
      json.load(open(os.path.join(hdir, sb + ".json")))["installed"]["timeupdated"] == "200")
se, sf = "998", missing[4]           # sf: downloaded once today (sd twice: the brake holds it)
unhand(sf)
stage(sf)
stage(se)
write_acf({})
ncfg4 = m.Config()
ns4 = m.Run(ncfg4, FakeClock(KT + 180), manual=True)
ns4.state.load()
for w in (sf, se):
    ns4.state.data["items"][w] = dict(ns4.state.data["items"].get(w) or {}, verdict="pass", status="tested", title="t" + w)
ns4.cleanup_staging()
check("cleanup: a keep-set pass whose hand-over failed (no acf entry) stays staged for the next run; a pass the keep set does not "
      "want is removed", os.path.isdir(os.path.join(stg, "content", "730", sf)) and not os.path.isdir(os.path.join(stg, "content", "730", se)))
# the owner's 71 a: a tested pass of a map the server stores is handed over when it is newer than the server's copy (110: the server's
# acf says 1, the staged one 40), and removed like any decided download when it is not (120: 1 against 1)
saved_state = open(state_file, encoding="utf-8").read()
stage("110")
stage("120")
write_acf({"110": "40", "120": "1"})
ns9 = m.Run(ncfg4, FakeClock(KT + 200), manual=True)
ns9.state.load()
for w in ("110", "120"):
    ns9.state.data["items"][w] = dict(ns9.state.data["items"].get(w) or {}, verdict="pass", status="tested", title="t" + w)
ns9.cleanup_staging()
check("cleanup: a tested update newer than the server's copy is handed over (110), one as old as it is removed (120)",
      "110" in m.handovers(hdir) and json.load(open(os.path.join(hdir, "110.json")))["installed"]["timeupdated"] == "40"
      and not os.path.isdir(os.path.join(stg, "content", "730", "120")) and "120" not in m.handovers(hdir)
      and any(e["id"] == "110" and "replaces the older copy" in e["why"] for e in ns9.state.data["history"]["maps"]))
unhand("110")
write_acf({})
open(state_file, "w", encoding="utf-8").write(saved_state)
m.SteamCmd = AcfSteam
FakeSteam.asked = []
ns5 = m.Run(ncfg2, FakeClock(KT + 240), manual=True)
ns5.state.load()
ns5.serve()
check("serve: ...and the next run downloads it (SteamCMD's acf did not name it) and hands it over",
      FakeSteam.asked == [sf] and sf in m.handovers(hdir))
unhand(sd)
FakeSteam.asked = []
ns6 = m.Run(ncfg2, FakeClock(KT + 300), manual=True)
ns6.state.load()
ns6.serve()
check("serve: the brake - a map downloaded twice today is not downloaded a third time", FakeSteam.asked == [] and sd not in m.handovers(hdir))
m.ACF_WAIT = 20

# tidy_staging: SteamCMD would download again by itself what its acf names without a folder (2026-11-03: 68 maps, 11.9 GB)
tg = os.path.join(srv, "tidy")
tstg = os.path.join(tg, "steamapps", "workshop")
thand = os.path.join(srv, "tidy-handover")
for w in ("71", "73", "74"):
    os.makedirs(os.path.join(tstg, "content", "730", w))
os.makedirs(thand)
for w in ("73", "74"):
    json.dump({"version": 1, "id": w, "bytes": 1}, open(os.path.join(thand, w + ".json"), "w"))
open(os.path.join(tstg, "appworkshop_730.acf"), "w").write('"AppWorkshop"\n{\n\t"WorkshopItemsInstalled"\n\t{\n' + "".join(
    '\t\t"%s"\n\t\t{\n\t\t\t"size"\t\t"6"\n\t\t\t"timeupdated"\t\t"5"\n\t\t\t"manifest"\t\t"7"\n\t\t}\n' % w
    for w in ("71", "72", "73", "74")) + '\t}\n}\n')
res = m.tidy_staging(tg, thand, {"71": {"status": "downloaded"}, "74": {"status": "downloaded"}})
left = m.acf_items(os.path.join(tstg, "appworkshop_730.acf"))
check("tidy: an acf entry without a folder is forgotten (72), a staged copy of a waiting hand-over goes (73), a map waiting for its "
      "test stays (71, and 74 although a hand-over waits for it) (%s, %s)" % (res, sorted(left)),
      res == (1, 1) and sorted(left) == ["71", "74"] and not os.path.isdir(os.path.join(tstg, "content", "730", "73"))
      and os.path.isdir(os.path.join(tstg, "content", "730", "74")))
check("tidy: nothing to do, nothing written", m.tidy_staging(tg, thand, {"71": {"status": "downloaded"}, "74": {"status": "downloaded"}}) == (0, 0))
m.SteamCmd, m.humans_on = real_steam, real["humans_on"]
os.environ.update({"SYNC_STATE_DIR": os.path.join(ROOT, "state"), "SYNC_WORK_DIR": os.path.join(ROOT, "work"), "SYNC_BRIDGE_DATA": os.path.join(ROOT, "data")})
for k in ("SYNC_GAME_DIR", "SYNC_DEPOTCACHE"):
    os.environ.pop(k, None)

# the crash of 2026-11-04 (its first test load): a second module-level mode_of (the status file's) replaced test_load's, and every real test load
# failed at its first line - the tests' own test_load was a stand-in. One definition per name, and the real test_load's first steps
import ast as _ast
for script in ("workshop-sync.py", "update-watch.py"):
    names = [n.name for n in _ast.parse(open(os.path.join(HERE, script), encoding="utf-8").read()).body
             if isinstance(n, (_ast.FunctionDef, _ast.ClassDef))]
    twice = sorted(set(n for n in names if names.count(n) > 1))
    check("one definition per module-level name in %s (twice: %s)" % (script, twice), twice == [])
check("mode_of is test_load's: (mode, game_type, game_mode) from the tags", m.mode_of(["Wingman"]) == ("wingman", 0, 2)
      and m.mode_of([]) == ("armsrace", 1, 0) and m.list_mode({"tags": ["Deathmatch"]}) == "deathmatch")


class DownEngine(object):
    """An engine that is gone the moment it is asked: the real test_load runs its first steps and says crash."""

    class tail(object):
        @staticmethod
        def lines():
            return []
    sent = []

    def rcon(self, command):
        DownEngine.sent.append(command)
        return ""

    def alive(self):
        return False


got = m.test_load(DownEngine(), "4242", ["de_x"], ["Wingman"], 20, lambda: False)
check("test_load (the real one): the map asked for in its mode, a crash reported - no exception (%s)" % got.get("verdict"),
      got.get("verdict") == "crash" and DownEngine.sent == ["game_type 0; game_mode 2; host_workshop_map 4242"])
# a reshaped input (a list where a dict is read, a string, a dict of the wrong things) is logged and counted, never a crash
shp = os.path.join(ROOT, "reshaped")
os.makedirs(shp)
for name, value in (("queues.json", []), ("votes.json", ["x"]), ("ratings.json", "x"), ("ours.json", {"maps": "x"}),
                    ("maps.json", {"x": 1}), ("broken.json", []), ("removed.json", [1])):
    json.dump(value, open(os.path.join(shp, name), "w"))
ks = m.safe_keep_set(m.keep_inputs(shp), KT)
check("reshaped inputs: the keep set says why it is not to be trusted, no exception (%s)" % ks["problems"][:2], ks["problems"] and not ks["all"] - set())
real_keep = m.keepset.keep_set
m.keepset.keep_set = lambda inputs, now, hud="": (_ for _ in ()).throw(AttributeError("'list' object has no attribute 'get'"))
ks2 = m.safe_keep_set({}, KT)
check("reshaped inputs: an exception inside the keep set is caught and named", ks2["problems"][0].startswith("the keep set could not be computed")
      and ks2["all"] == set())
nsr = m.Run(ncfg2, FakeClock(KT + 400), manual=True)
nsr.state.load()
nsr.serve()
check("reshaped inputs: serve stops at such a keep set and names it as an error, the run goes on",
      "could not be computed" in nsr.report["serve"].get("skipped", "") and any("serve:" in e for e in nsr.report["errors"]))
m.keepset.keep_set = real_keep
# keepset.py not mounted (the deployment's compose file names it, and a missing source becomes a directory): the keep set says so, serve hands over nothing
saved_ks = sys.modules.get("keepset")
sys.modules["keepset"] = None                                   # what an import of a file that is not there raises: ImportError
try:
    bare = types.ModuleType("ws_bare")
    bare.__dict__["__name__"] = "ws_bare"
    exec(compile(ws_text, "workshop-sync", "exec"), bare.__dict__)
finally:
    sys.modules["keepset"] = saved_ks
bare.log = lambda message: LOGS.append(message)
ksb = bare.safe_keep_set(bare.keep_inputs(sdata), KT, "500")
check("no keepset.py: an empty keep set that says why (%s)" % ksb["problems"][:1],
      bare.keepset is None and ksb["all"] == set() and ksb["problems"][0].startswith("the keep set could not be computed: keepset.py is not beside"))
nb = bare.Run(ncfg2, FakeClock(KT + 500), manual=True)
nb.state.load()
nb.serve()
check("no keepset.py: serve hands over nothing and names it as an error", "could not be computed" in nb.report["serve"].get("skipped", "")
      and any("keepset.py" in e for e in nb.report["errors"]))

shutil.rmtree(ROOT, ignore_errors=True)
print("\n%d failed" % len(FAILS) if FAILS else "\nall passed")
sys.exit(1 if FAILS else 0)
