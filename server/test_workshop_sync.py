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
import sys
import tempfile
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
                   "SYNC_DOWNLOAD_MAX": "80", "SYNC_DOWNLOAD_MAX_MB": "10000"})
c = m.Config()
check("config: a day cap above 10,000 is clamped to 10,000", c.api_day_cap == 10000)
check("config: the night cap never exceeds the day cap", c.api_night_cap == 10000)
check("config: the gap never drops under 2 s", c.api_gap == 2.0)
check("config: downloads at most 50 a night", c.download_max == 50)
check("config: downloads at most 3 GB a night", c.download_bytes == 3 << 30)
for k in ("SYNC_API_DAY_CAP", "SYNC_API_NIGHT_CAP", "SYNC_API_GAP_SECONDS", "SYNC_DOWNLOAD_MAX", "SYNC_DOWNLOAD_MAX_MB"):
    del os.environ[k]
c = m.Config()
check("config: defaults are 500 calls a night, 10,000 in 24 h, 2 s, 50 downloads, 3 GB",
      (c.api_night_cap, c.api_day_cap, c.api_gap, c.download_max, c.download_bytes) == (500, 10000, 2.0, 50, 3 << 30))
c.lower({"api_night_cap": 50, "download_max": 5, "download_max_mb": 200, "publish": False})
check("config: run-now lowers a run's limits", (c.api_night_cap, c.download_max, c.download_bytes, c.publish) == (50, 5, 200 << 20, False))
c.lower({"api_night_cap": 5000, "download_max": 500})
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

# ---- downloads: 50 items and 3 GB a night, persisted ---------------------------------------------------------------
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
check("downloads: a caller cannot raise the caps", m.DownloadBudget(st2, "x", 500, 10 << 30).max_items == 50
      and m.DownloadBudget(st2, "y", 500, 10 << 30).max_bytes == 3 << 30)

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

# ---- the mail ------------------------------------------------------------------------------------------------------
r = {"night": N, "manual": False, "api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for tonight",
     "discovery": {"skipped": "stopped"}, "check": {"asked": 10, "calls": 1}, "downloads": {"ok": 0}, "tests": {"rows": []},
     "published": {}, "limits": {"api_night": 3, "api_night_cap": 500, "api_24h": 3, "api_day_cap": 10000}}
subject, body = m.mail_text(r, m.Config())
check("mail: a stop is in the subject and the body", "STOPPED: HTTP 429" in subject and "STOPPED: Steam's Web API answered HTTP 429" in body)
check("mail: the limits are reported", "Web API calls tonight 3 of 500, last 24 h 3 of 10000" in body)

# ---- the backlog: most subscribed first per mode, the modes in turn; its size and ETA in the mail ------------------
items = {"1": {"tags": ["Deathmatch"], "subs": 100, "size": 10 << 20}, "2": {"tags": ["Deathmatch"], "subs": 90, "size": 10 << 20},
         "3": {"tags": ["Wingman"], "subs": 5, "size": 10 << 20}, "4": {"tags": ["Armsrace", "Deathmatch"], "subs": 50, "size": 10 << 20},
         "5": {"tags": ["Wingman", "Deathmatch"], "subs": 95, "size": 10 << 20}, "6": {"tags": ["Armsrace"], "subs": 1, "size": 10 << 20}}
o = m.order_backlog(items, list(items))
check("backlog: Deathmatch's best, then Wingman's best, then Arms Race's best, in turn", o[:3] == ["1", "5", "4"])
check("backlog: an item tagged twice is taken once", sorted(o) == sorted(items) and len(o) == 6)
check("backlog: Wingman's next best comes in its own turn, before Deathmatch's third", o.index("3") < o.index("2") or o[3] == "2")
c = m.Config()
bs = m.backlog_stats(items, list(items), c)
check("backlog: counted per mode", bs["items"] == 6 and bs["by_mode"] == {"armsrace": 2, "deathmatch": 4, "wingman": 2})
c.download_max = 2
check("backlog: the ETA is set by the slower cap (6 items at 2 a night: 3 nights)", m.backlog_stats(items, list(items), c)["nights"] == 3)
r2 = dict(r, api_stop=None, backlog=bs, backlog_until=" (until about 2026-11-04)")
subject2, body2 = m.mail_text(r2, m.Config())
check("mail: the backlog's size, per mode, and its ETA", "Backlog: 6 item(s) waiting" in body2 and "Deathmatch 4, Wingman 2, Arms Race 2" in body2
      and "until about 2026-11-04" in body2)

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
