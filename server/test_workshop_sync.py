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


def ledger(path, day_cap=10000, night_cap=500, t=1793500000.0):
    clock = FakeClock(t)
    led = m.ApiLedger(path, day_cap, night_cap, clock)
    led.load()
    return led, clock


def api_with(led, clock, answers, key=KEY, gap=2.0):
    sent = []
    ft = FakeTime(clock)
    api = m.SteamApi(led, clock.day(), gap, key=key, opener=opener_of(answers, sent), sleep=ft.sleep, monotonic=ft.monotonic)
    return api, sent, ft


# ---- configuration: the owner's limits can only be lowered ------------------------------------------------------
os.environ.update({"SYNC_API_DAY_CAP": "20000", "SYNC_API_NIGHT_CAP": "99999", "SYNC_API_GAP_SECONDS": "0.5",
                   "SYNC_DOWNLOAD_MAX": "8000", "SYNC_DOWNLOAD_MAX_MB": "999999", "SYNC_DOWNLOAD_PAUSE": "0.1"})
c = m.Config()
check("config: a day cap above 10,000 is clamped to 10,000", c.api_day_cap == 10000)
check("config: the night cap never exceeds the day cap", c.api_night_cap == 10000)
check("config: the gap never drops under 2 s", c.api_gap == 2.0)
check("config: downloads at most 1,500 a day", c.download_max == 1500)
check("config: downloads at most 150 GB a day", c.download_bytes == 150 << 30)
check("config: the pause between downloads never drops under 2 s", c.download_pause == 2.0)
for k in ("SYNC_API_DAY_CAP", "SYNC_API_NIGHT_CAP", "SYNC_API_GAP_SECONDS", "SYNC_DOWNLOAD_MAX", "SYNC_DOWNLOAD_MAX_MB", "SYNC_DOWNLOAD_PAUSE"):
    del os.environ[k]
c = m.Config()
check("config: defaults are 500 calls a night, 10,000 in 24 h, 2 s, 1,500 downloads, 150 GB",
      (c.api_night_cap, c.api_day_cap, c.api_gap, c.download_max, c.download_bytes) == (500, 10000, 2.0, 1500, 150 << 30))
check("config: no day window unless both ends are set", c.day_start is None and c.day_end is None)
os.environ["SYNC_DAY_AT"] = "07:15"
check("config: one end alone is no window", m.Config().day_start is None)
os.environ["SYNC_DAY_END"] = "18:00"
check("config: the day's window", (m.Config().day_start, m.Config().day_end) == ((7, 15), (18, 0)))
del os.environ["SYNC_DAY_AT"], os.environ["SYNC_DAY_END"]
c.lower({"api_night_cap": 50, "download_max": 5, "download_max_mb": 200, "publish": False})
check("config: run-now lowers a run's limits", (c.api_night_cap, c.download_max, c.download_bytes, c.publish) == (50, 5, 200 << 20, False))
c.lower({"api_night_cap": 5000, "download_max": 1000})
check("config: run-now cannot raise them", (c.api_night_cap, c.download_max) == (50, 5))
check("ledger: the hard day cap cannot be raised by a caller", m.ApiLedger("x", 50000, 10, FakeClock(0)).day_cap == 10000)

# ---- the ledger: rolling 24 h, the night, persisted before every call ----------------------------------------------
lp = os.path.join(ROOT, "ledger1.json")
led, clock = ledger(lp, day_cap=10000, night_cap=500)
night = clock.day()
for i in range(500):
    led.reserve(night, "GetPublishedFileDetails")
try:
    led.reserve(night, "QueryFiles")
    check("ledger: call 501 of a night is refused", False)
except m.StopNight as e:
    check("ledger: call 501 of a night is refused", "tonight's cap of 500" in str(e))
check("ledger: every reserved call is on disk", len(json.load(open(lp))["calls"]) == 500)
led2, _ = ledger(lp, night_cap=500, t=clock.t)
try:
    led2.reserve(night, "QueryFiles")
    check("ledger: the night cap survives a restart (a new process reads the file)", False)
except m.StopNight:
    check("ledger: the night cap survives a restart (a new process reads the file)", True)
# the rolling day: 10,000 across nights
lp = os.path.join(ROOT, "ledger2.json")
led, clock = ledger(lp, day_cap=10000, night_cap=10000)
led.data["calls"] = [[clock.t - 3600 - i * 0.5, "QueryFiles", 200] for i in range(9999)]
led.data["nights"] = {"2026-10-31": 9999}
led.save()
led.reserve(clock.day(), "QueryFiles")
try:
    led.reserve(clock.day(), "QueryFiles")
    check("ledger: the 10,001st call within 24 h is refused, whatever night it is", False)
except m.StopNight as e:
    check("ledger: the 10,001st call within 24 h is refused, whatever night it is", "10000 Web API calls in 24 hours" in str(e))
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
except m.StopNight as e:
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
        check("api: HTTP %d stops the night" % code, False)
    except m.StopNight as e:
        check("api: HTTP %d stops the night" % code, "HTTP %d" % code in str(e))
    check("api: HTTP %d is sent once, never retried" % code, len(sent) == 1)
    try:
        api.details(["1"])
        check("api: after HTTP %d no further call goes out" % code, False)
    except m.StopNight:
        check("api: after HTTP %d no further call goes out" % code, len(sent) == 1)
    led2, _ = ledger(lp, t=clock.t)
    try:
        led2.reserve(clock.day(), "QueryFiles")
        check("api: the HTTP %d stop is in the ledger, so a restart cannot resume tonight" % code, False)
    except m.StopNight as e:
        check("api: the HTTP %d stop is in the ledger, so a restart cannot resume tonight" % code, "stopped earlier tonight" in str(e))
# 5xx: three in a row stop (the retries count)
lp = os.path.join(ROOT, "ledger-5xx.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [502, 503, 500, {"response": {}}])
try:
    api.details(["1"])
    check("api: 3 server errors in a row stop the night", False)
except m.StopNight as e:
    check("api: 3 server errors in a row stop the night", "server errors" in str(e) and len(sent) == 3)
check("api: the 5xx retries wait 30 s and 90 s", ft.sleeps[:1] == [30] and 90 in ft.sleeps)
# 5xx: a single one is retried and the night goes on
lp = os.path.join(ROOT, "ledger-5xx1.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [503, {"response": {"publishedfiledetails": [{"publishedfileid": "7", "result": 1}]}}])
got = api.details(["7"])
check("api: one 503 is retried and the answer used", "7" in got and len(sent) == 2 and not led.stopped(clock.day()))
# 5 in a night stop, even not in a row
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
    except m.StopNight as e:
        stopped = str(e)
        break
check("api: 5 server errors in a night stop it", stopped is not None and "5 server errors" in stopped)
# network failures
lp = os.path.join(ROOT, "ledger-net.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, ["net"] * 6)
stopped = None
for i in range(3):
    try:
        api.details(["1"])
    except m.StopNight as e:
        stopped = str(e)
        break
    except m.ApiError:
        pass
check("api: 5 network failures stop the night", stopped is not None and "5 network failures" in stopped)
# other 4xx: given up at once, not retried, the night goes on
lp = os.path.join(ROOT, "ledger-400.json")
led, clock = ledger(lp)
api, sent, ft = api_with(led, clock, [400, {"response": {}}])
try:
    api.details(["1"])
    check("api: HTTP 400 is given up without a retry", False)
except m.StopNight:
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
except m.StopNight as e:
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
check("discovery: the next night, an item seen before is not new (stop after the page with only id 11 new)",
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
b = m.DownloadBudget(st, "2026-11-01", 50, 3 << 30)
ok_count = 0
while b.allows(10 << 20)[0]:
    b.spend(10 << 20)
    ok_count += 1
check("downloads: the 51st download is refused", ok_count == 50 and "50 downloads" in b.allows(1)[1])
st.save()
st2 = m.State(os.path.join(ROOT, "st3.json")).load()
b2 = m.DownloadBudget(st2, "2026-11-01", 50, 3 << 30)
check("downloads: the count survives a restart", not b2.allows(1)[0])
b3 = m.DownloadBudget(st2, "2026-11-02", 50, 3 << 30)
b3.spend(3 << 30)
check("downloads: 3 GB a night", not b3.allows(1)[0] and "3.0 GB" in b3.allows(1)[1])
check("downloads: a caller cannot raise the caps", m.DownloadBudget(st2, "x", 5000, 500 << 30).max_items == 1500
      and m.DownloadBudget(st2, "y", 5000, 500 << 30).max_bytes == 150 << 30)
b4 = m.DownloadBudget(st2, "z", 50, 3 << 30)
b4.refuse_for_today("Rate Limit Exceeded")
st2.save()
st5 = m.State(os.path.join(ROOT, "st3.json")).load()
check("downloads: a rate limit ends the day's downloads, also for a later run that day",
      not m.DownloadBudget(st5, "z", 50, 3 << 30).allows(0)[0] and "Rate Limit Exceeded" in m.DownloadBudget(st5, "z", 50, 3 << 30).allows(0)[1])
check("downloads: the next day starts clean", m.DownloadBudget(st5, "zz", 50, 3 << 30).allows(0)[0])
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
check("publish: a listed map gone from the Workshop is NOT added to the removed seed (the repo pins it)", nb["removed"] == [])
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
r = {"night": N, "manual": False, "api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for tonight",
     "discovery": {"skipped": "stopped"}, "check": {"asked": 10, "calls": 1}, "downloads": {"ok": 0}, "tests": {"rows": []},
     "published": {}, "limits": {"api_night": 3, "api_night_cap": 500, "api_24h": 3, "api_day_cap": 10000}}
subject, body = m.mail_text(r, m.Config())
check("mail: a stop is in the subject and the body", "STOPPED: HTTP 429" in subject and "STOPPED: Steam's Web API answered HTTP 429" in body)
check("mail: the limits are reported", "Web API calls tonight 3 of 500, last 24 h 3 of 10000" in body)

# ---- the backlog: strictly most subscribed first, whatever the mode; its size and estimate in the mail -----------------
items = {"1": {"tags": ["Deathmatch"], "subs": 100, "size": 10 << 20}, "2": {"tags": ["Deathmatch"], "subs": 90, "size": 10 << 20},
         "3": {"tags": ["Wingman"], "subs": 5, "size": 10 << 20}, "4": {"tags": ["Armsrace", "Deathmatch"], "subs": 50, "size": 10 << 20},
         "5": {"tags": ["Wingman", "Deathmatch"], "subs": 95, "size": 10 << 20}, "6": {"tags": ["Armsrace"], "subs": 1, "size": 10 << 20}}
o = m.order_backlog(items, list(items))
check("backlog: strictly by subscribers across the modes", o == ["1", "5", "2", "4", "3", "6"])
c = m.Config()
bs = m.backlog_stats(items, list(items), c)
check("backlog: counted per mode", bs["items"] == 6 and bs["by_mode"] == {"armsrace": 2, "deathmatch": 4, "wingman": 2})
c.download_max = 2
check("backlog: the estimate is set by the slowest bound (6 items at 2 a day: 3 days)", m.backlog_stats(items, list(items), c)["days"] == 3)
c = m.Config()
c.download_bytes = 20 << 20
check("backlog: real bytes count, nothing halved (60 MB at 20 MB a day: 3 days)", m.backlog_stats(items, list(items), c)["days"] == 3)
c = m.Config()
big = {str(i): {"tags": ["Wingman"], "subs": 1, "size": 100 << 20} for i in range(5255)}
night_only = m.backlog_stats(big, list(big), c)
check("backlog: the night's window alone, about 2.9 h at 31 s a map: 339 maps a day, 16 days for the backlog of 2026-11-01",
      night_only["hours"] == 2.9 and night_only["days"] == 16)
c.day_start, c.day_end = (7, 15), (18, 0)
both = m.backlog_stats(big, list(big), c)
check("backlog: with the day's window, 13.7 h at 31 s a map stops at the 1,500 cap: 4 days", both["hours"] == 13.7 and both["days"] == 4)
check("backlog: a pace measured counts", m.backlog_stats(big, list(big), c, pace=60.0)["days"] == 7)
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
check("mail: a day run says so", "(day run)" in m.mail_text(dict(r, day_run=True), m.Config())[0])
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
nq = m.Night(m.Config(), fc, manual=True, sleep=lambda n: (waits.append(n), setattr(fc, "t", fc.t + n)))
check("wait: somebody on now means waiting, and 0 humans alone is not enough for the next 10 minutes", nq.wait_for_nobody("x") and len(waits) == 10)
seen[:] = [0]
waits[:] = []
nq2 = m.Night(m.Config(), fc, manual=True, sleep=lambda n: (waits.append(n), setattr(fc, "t", fc.t + n)))
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
night = m.Night.__new__(m.Night)
night.cfg = m.Config()
ans = night.report_verdicts([("111", "ok"), ("222", "removed"), ("333", "hidden")])
check("bridge: the verdicts go to POST /removed/<secret> in the plugin's format (magic line, id TAB verdict)",
      GOT[0][0] == "/removed/s3cr3t" and GOT[0][2] == "gg-removed\t1\n111\tok\n222\tremoved\n333\thidden\n"
      and GOT[0][1].startswith("text/plain"))
check("bridge: its answer is kept for the mail", ans.startswith("3 checked"))
check("bridge: POST /reload/<secret> after a publish", night.reload() == "reload: maps: 9 maps" and GOT[1][0] == "/reload/s3cr3t")
# ---- a night's publish and finish, end to end, on a bridge folder of its own and the stand-in bridge -----------------
data = os.path.join(ROOT, "data")
os.makedirs(data, exist_ok=True)
live_maps = [{"rank": 1, "id": 111, "name": "a", "sizeMb": 5}, {"rank": 2, "id": 222, "name": "b", "sizeMb": 7}]
live_broken = {"schema": "x", "maps": [], "packs": [], "removed": [{"id": 3659287341}], "blocked": []}
open(os.path.join(data, "maps.json"), "w", encoding="utf-8").write(json.dumps(live_maps, indent=1) + "\n")
open(os.path.join(data, "broken.json"), "w", encoding="utf-8").write(json.dumps(live_broken, indent=2) + "\n")
open(os.path.join(data, "search.json"), "w", encoding="utf-8").write(json.dumps({"111": "a 111", "222": "b 222"}, indent=0) + "\n")
nt = m.Night(m.Config(), FakeClock(1793500000.0), manual=True)
nt.ledger.load()
nt.state.load()
nt.verdicts = [("111", "ok"), ("222", "removed")]
pass_rec = {"title": "new map", "tags": ["Armsrace", "Wingman"], "size": 20 << 20, "subs": 3, "lifetime": 3, "description": "d",
            "status": "tested", "verdict": "pass", "updated": 1793400000}
pack_rec = {"title": "a pack", "tags": ["Armsrace"], "size": 1 << 20, "status": "tested", "verdict": "nonmap", "preview": "", "description": ""}
nt.state.data["items"] = {"901": pass_rec, "902": pack_rec}
GOT[:] = []
files0 = m.bridge_files(nt.cfg)
nt.publish(files0, [("901", "pass", {"level": "x"}, pass_rec), ("902", "nonmap", {"kind": "pack", "detail": "models"}, pack_rec)],
           [("222", "removed")], {})
after = json.load(open(os.path.join(data, "maps.json"), encoding="utf-8"))
check("night: the pass is appended to the live maps.json as #3", after[:2] == live_maps and after[2]["id"] == 901 and after[2]["rank"] == 3)
check("night: the pack joined broken.json's packs, the removed seed untouched",
      [p["id"] for p in json.load(open(os.path.join(data, "broken.json")))["packs"]] == [902]
      and json.load(open(os.path.join(data, "broken.json")))["removed"] == [{"id": 3659287341}])
check("night: the Wingman list is created with the pass", [e["id"] for e in json.load(open(os.path.join(data, "maps-wingman.json")))] == [901])
check("night: the verdicts went to /removed, then the files were reloaded",
      [g[0] for g in GOT] == ["/removed/s3cr3t", "/reload/s3cr3t"] and "222\tremoved" in GOT[0][2])
check("night: the old files are backed up", len(os.listdir(os.path.join(nt.cfg.state_dir, "backups"))) == 1)
check("night: search.json, written after the reload (the reload does not read it), holds the new map",
      "901" in json.load(open(os.path.join(data, "search.json"), encoding="utf-8")) and "search.json" in nt.report["published"]["files"])
check("night: both items are marked published", pass_rec["status"] == "published" and pack_rec["status"] == "published")
# a file that changed under the plan is planned again; a result that is not the live files plus additions is refused
nt2 = m.Night(m.Config(), FakeClock(1793500100.0), manual=True)
nt2.ledger.load()
nt2.state.load()
rec3 = {"title": "third", "tags": ["Armsrace"], "size": 1 << 20, "subs": 1, "status": "tested", "verdict": "pass", "updated": 1}
stale = m.bridge_files(nt2.cfg)
grown = after + [{"rank": 4, "id": 950, "name": "added by the release", "sizeMb": 1}]
open(os.path.join(data, "maps.json"), "w", encoding="utf-8").write(json.dumps(grown, indent=1, ensure_ascii=False) + "\n")
nt2.publish(stale, [("903", "pass", {"level": "y"}, rec3)], [], {})
now_maps = json.load(open(os.path.join(data, "maps.json"), encoding="utf-8"))
check("night: a maps.json that grew meanwhile is merged into, never written from the older copy",
      now_maps[:4] == grown and now_maps[4]["id"] == 903 and now_maps[4]["rank"] == 5)
nt.finish()
rep_txt = [f for f in os.listdir(os.path.join(nt.cfg.state_dir, "reports")) if f.endswith(".txt")]
check("night: the report and the mail text are written, the mail reports why it was not sent",
      rep_txt and nt.report["mail"].startswith("NOT sent: no SMTP host"))
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
T0 = 1793540000.0   # an afternoon, outside the night's window
run_items = {str(1000 + i): {"status": "queued", "ws": "ok", "app": 730, "tags": ["Wingman"], "subs": 10 * (i + 1), "lifetime": 1, "size": 1000, "created": T0 - 100 * m.DAY,
                             "title": "m%d" % i, "description": "", "first": "2026-11-01", "why": "new"} for i in range(7)}
run_items["2000"] = dict(run_items["1000"], subs=1, created=T0 - m.DAY, title="new upload")
json.dump({"schema": 1, "items": run_items, "baseline": {}, "runs": [], "downloads": {}}, open(os.path.join(run_root, "state", "state.json"), "w"))
cfg = m.Config()
cfg.batch, cfg.download_max, cfg.download_pause = 2, 5, 0
fc = FakeClock(T0)
nr = m.Night(cfg, fc, manual=True)
rep = nr.run()
check("run: a new upload first, then strictly by subscribers", FakeSteam.asked[:5] == ["2000", "1006", "1005", "1004", "1003"])
check("run: every download was tested", sorted(loads) == sorted(FakeSteam.asked))
check("run: 5 downloads in batches of 2: 3 logins, one engine for each batch with work", FakeSteam.logins == 3 and rep["downloads"]["ok"] == 5)
check("run: the day's cap ends it, with the reason", "today's cap of 5 downloads" in rep["downloads"].get("stop", ""))
wing = json.load(open(os.path.join(run_data, "maps-wingman.json"), encoding="utf-8"))
check("run: every batch published (5 maps on the list, the report adds them up)",
      len(wing) == 5 and len(rep["published"]["modes"]["wingman"]) == 5 and "maps-wingman.json" in rep["published"]["files"])
check("run: staging is empty again", os.listdir(os.path.join(run_root, "work", "staging", "steamapps", "workshop", "content", "730")) == [])
state_after = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
check("run: the rest waits, the day's book says 5", state_after["downloads"]["2026-11-01"]["items"] == 5
      and sum(1 for r in state_after["items"].values() if r["status"] == "queued") == 3)
check("run: the mail's estimate counts what is left", rep["backlog"]["items"] == 3 and "Backlog: 3 item(s)" in open(rep["report_path"][:-5] + ".txt", encoding="utf-8").read())
check("run: no last_run_day from a manual run", state_after.get("last_run_day") in (None, ""))
check("run: the estimate takes the service's caps, not the manual run's lowered one", rep["backlog"]["per_day"][0] == m.Config().download_max != 5)
# somebody joins during a test: the engine stops at once, nothing runs until the quiet time is over
loads[:] = []
FakeSteam.logins = 0
fc2 = FakeClock(T0 + m.DAY)
cfg2 = m.Config()
cfg2.batch, cfg2.download_max, cfg2.download_pause, cfg2.quiet = 3, 3, 0, 600
slept = []
def sleeper(n):
    slept.append(n)
    fc2.t += n
    if len(slept) == 3:
        humans_now[0] = 0
nj = m.Night(cfg2, fc2, manual=True, sleep=sleeper)
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
rep3 = m.Night(cfg3, fc3, manual=True).run()
state3 = json.load(open(os.path.join(run_root, "state", "state.json"), encoding="utf-8"))
check("stop: one access-denied item is failed, the next one goes on", state3["items"]["3001"]["status"] == "failed" and state3["items"]["3002"]["status"] == "published")
check("stop: a rate limit ends the downloads, the next item is not asked for", "Rate Limit Exceeded" in rep3["downloads"]["stop"] and state3["items"]["3004"]["status"] == "queued")
rep4 = m.Night(cfg3, FakeClock(T0 + 2 * m.DAY + 3600), manual=True).run()
check("stop: a later run that day downloads nothing", rep4["downloads"]["ok"] == 0 and "Rate Limit Exceeded" in rep4["downloads"]["stop"])
FakeSteam.answers = {}
# the day's window: its own report, no mail when it did nothing
os.environ.update({"SYNC_DAY_AT": "07:15", "SYNC_DAY_END": "18:00"})
cfgd = m.Config()
nd = m.Night(cfgd, FakeClock(T0 + 3 * m.DAY), day_run=True)
check("day: the deadline is the day window's end", m.time.strftime("%H:%M", m.time.localtime(nd.deadline)) in ("18:00", "17:00"))
del os.environ["SYNC_DAY_AT"], os.environ["SYNC_DAY_END"]
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

shutil.rmtree(ROOT, ignore_errors=True)
print("\n%d failed" % len(FAILS) if FAILS else "\nall passed")
sys.exit(1 if FAILS else 0)
