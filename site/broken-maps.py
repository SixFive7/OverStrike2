"""Mark the CS2 workshop items that must not be loaded on overstrike.example.com, the page players find their maps on.

Source of truth: bridge/broken.json, the same file the bridge reads as BROKEN_FILE. Two lists in it:
  "maps"   real maps that crash or freeze the server. A row on the page gets a full-width red banner carrying the
           entry's own `evidence` text, a BROKEN sash over the thumbnail, a danger-coloured copy button and a
           "Hide broken" toggle in the toolbar; in the game's menu, picking one asks the same player to confirm.
  "packs"  items with no playable map in them: skin and model packs, configs, sound and viewmodel packs, empty
           uploads, CS:GO-era uploads CS2 cannot load. They are not on the map list; a second, non-default "Not maps"
           tab lists each with its evidence (no copy button) so a player can recognise one. The game's menu leaves them
           out and refuses their IDs.
  "removed" maps gone from the Workshop (deleted, made private or banned by their author or Valve): nobody can download
           them, so a client joining while one is on gets ERROR 15. The row keeps its place with a "Removed from the
           Workshop" banner and a REMOVED sash in the broken banners' style; the server refuses it outright. The
           bridge's daily Workshop check blocks and unblocks maps by itself (/data/removed.json on the server); the page
           only learns of one when it is added here and the page is re-stamped.
This script never writes broken.json: edit that file, then re-run this, and the page follows.

And on every page, whatever broken.json says, the guide: how to play on the server, the first thing a visitor sees -
joining, the in-game menu in the five groups of the HUD's Help page (bridge/Hud.cs Help and Rules, which the bridge's
--selftest finds on this page word for word; the page may say more, never something else), the fair queue, how voting
counts and when the dice roll, and - since 2026-10-24, the owner's 5a - the menu's whole How it works page (Hud.How), word for
word, with every number on it one of the bridge's constants (docs/reference.md "Keeping the rules in sync": a rule change
changes Hud.cs, this guide and the code together, and the bridge's --selftest fails until all three agree). Since 2026-10-21
the menu is the server's whole interface: there are no chat commands. It is stamped over the
builder's masthead: the <h1> moves into a hero beside the Join box, the "Where to type it" and "Joining" cards go
(their content is in the guide and the standfirst now), and the standfirst stops promising an instant switch. It lives
here rather than in the builder's template so that editing the manual is one re-run of this script on repo files: the
builder also needs the Workshop scrape and the thumbnails, which live only in .work scratch. The phone layout of the
whole page rides with it as one more CSS block (PHONE-CSS, last in the sheet): rows, banners, the Not maps tab, the
toolbar and the guide re-laid out up to 560 px by CSS alone, so every row's markup stays the builder's.

Inputs, all repo files by default:
  --broken   bridge/broken.json (above). Strict: an unknown evidence kind, a missing id, a duplicate, or an id on
             both lists is a hard failure - nothing is classified on a guess.
  --maps     bridge/maps.json, the bridge's table. Every page row's rank must still name the same workshop id there,
             or a map number found on the page would name a different map in the game. A re-ranked or backfilled table is a
             rebuild of the rows (build-1000.py beside this script), never something to stamp over.
  --src      the page to stamp, stamped or not (default: index.html beside this script, the page the
             site serves).
             Since 2026-10-22 the page is split (pagesplit.py beside this script): index.html holds rows #1-#1016 and
             more.html the rest, which the page's script fetches lazily. A split --src is joined with the more.html
             beside it, stamped whole, and written split again at the same cut; an unsplit one is written unsplit.
  --img      the site's thumbnail folder, read only to know which thumbnails exist (default: OVERSTRIKE2_SITE_IMG, else img/ beside this script).

Discipline: line-oriented on a 1.7 MB page, every anchor asserted to match exactly once, output written beside this
script for review; it never touches the server. Reversible and re-runnable: every injection is marked and
strip_page() undoes it; a pack moved out of the list goes back to its rank with its copy button regenerated from a
template that was asserted on the way out, and the header facts (map count, combined subscriptions, maps under 25 MB)
are recomputed from the rows the map list actually holds, both ways. With nothing broken and no packs, the page comes
out byte-identical but for the guide - asserted, not intended.

Usage (from the repo root):
  python site/broken-maps.py                       # re-stamp the repo page in place
  python site/broken-maps.py --src rebuilt.html    # stamp a page build-1000.py just rebuilt
  python site/broken-maps.py --mock 3084291314,3453526792 --out .work/<dir>   # + a cut-down mock
  python site/broken-maps.py --clear --out .work/<dir>                        # the un-stamped page
Deploying is a file copy of index.html and more.html (and any new img/<id>.webp) to the server, documented with the
stack (docs/reference.md).

Provenance of the rows: build-1000.py (beside this script) wrote them from a Workshop scrape, and again for the
rank-stable refresh - the non-maps' ranks refilled, in popularity order, from the tested-good spare pool, every other
rank untouched. Its input, the scrape, is not in the repository, and neither are the thumbnails (the site's img folder).
"""
import argparse
import html as htmllib
import json
import os
import re
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path


def _site():
    """The site's host, the address players connect to and the server's name: configuration, never code - the deployment's own
    (deploy/config.example.toml: site.url, site.connect), read from the environment; the placeholders without it, which is what
    the committed pages carry (deploy/site.py fills them in when it ships the site)."""
    url = os.environ.get("OVERSTRIKE2_SITE_URL") or "https://overstrike.example.com"
    connect = os.environ.get("OVERSTRIKE2_CONNECT") or "play.example.com"
    name = os.environ.get("OVERSTRIKE2_SERVER_NAME") or "Over Strike 2"
    return url.split("://")[-1].strip("/"), connect, name


SITE_HOST, CONNECT, SERVER_NAME = _site()
SERVER_HTML = SERVER_NAME.replace(" ", "&nbsp;")
TAGLINE = "Over the top &ndash; On the Huis"  # the owner's tagline (2026-11-01), as the HUD shows it at a join

HERE = Path(__file__).resolve().parent
REPO = HERE.parent                           # site/ -> the repo root
BROKEN_SRC = REPO / "bridge" / "broken.json"
BRIDGE_MAPS = REPO / "bridge" / "maps.json"
OURS_SRC = REPO / "bridge" / "ours.json"   # the server's own maps: the Our maps section (and the HUD's Our maps filter)
SITE_SRC = HERE / "index.html"
SITE_IMG = os.environ.get("OVERSTRIKE2_SITE_IMG") or str(HERE / "img")  # the served thumbnails (deploy config), or a local copy
STEAM = "https://steamcommunity.com/sharedfiles/filedetails/?id={}"

TEST, DESC = "confirmed by test", "identified from description"
WSF = "identified from its Workshop file"   # a CS:GO-era upload: the Workshop's own record names a CS:GO .bsp (2026-11-01, #13)
EVIDENCE = {TEST: TEST, "test": TEST, "tested": TEST, "content": TEST, "content scan": TEST,
            DESC: DESC, "description": DESC, "packtext": DESC, WSF.lower(): WSF}
KINDS = {"pack", "empty", "stub", "legacy"}   # legacy: a CS:GO-era upload holding only a _legacy.bin, no CS2 map (2026-10-24)
TAB_HASH = "#not-maps"

RE_LI = re.compile(r'^<li class="row" data-band="')
RE_RANK = re.compile(r'^\s*<div class="rank"><span>(\d+)</span></div>\s*$')
RE_SHOT = re.compile(r'^\s*<a class="shot" href="[^"]*[?&]id=(\d+)"')
RE_NAME = re.compile(r'^\s*<h2 class="name" dir="auto">(.*)</h2>\s*$')
LIST_OPEN, LIST_CLOSE = '  <ol class="list" id="list">', "  </ol>"

# Markers. Every block this script adds sits between a start and an end marker, and strip_page() cuts on them.
SENTINEL, CSS_END = "BROKEN-MAPS-START", "  /* ---------- BROKEN-MAPS-END ---------- */"
PK_CSS, PK_CSS_END = "SKIN-PACKS-CSS-START", "  /* ---------- SKIN-PACKS-CSS-END ---------- */"
PK_TABS, PK_TABS_END = "  <!-- SKIN-PACKS-TABS-START -->", "  <!-- SKIN-PACKS-TABS-END -->"
PK_PANEL, PK_PANEL_END = "  <!-- SKIN-PACKS-PANEL-START -->", "  <!-- SKIN-PACKS-PANEL-END -->"
PK_JS, PK_JS_END = "<script>/* SKIN-PACKS-JS-START */", "/* SKIN-PACKS-JS-END */</script>"
GD_CSS, GD_CSS_END = "GUIDE-CSS-START", "  /* ---------- GUIDE-CSS-END ---------- */"
GD_TOP, GD_TOP_END = "    <!-- GUIDE-START", "    <!-- GUIDE-END -->"
PH_CSS, PH_CSS_END = "PHONE-CSS-START", "  /* ---------- PHONE-CSS-END ---------- */"
OU_TOP, OU_TOP_END = "  <!-- OUR-MAPS-START", "  <!-- OUR-MAPS-END -->"
OU_CSS, OU_CSS_END = "OUR-MAPS-CSS-START", "  /* ---------- OUR-MAPS-CSS-END ---------- */"
OU_JS, OU_JS_END = "<script>/* OUR-MAPS-JS-START */", "/* OUR-MAPS-JS-END */</script>"
MARKERS = (SENTINEL, PK_CSS, PK_TABS, PK_PANEL, PK_JS, GD_CSS, GD_TOP, PH_CSS, OU_TOP, OU_CSS, OU_JS)


def clean_text(s):
    return re.sub(r"\s+", " ", str(s or "")).strip()


def first(entry, *keys):
    return next((entry[k] for k in keys if entry.get(k) not in (None, "")), None)


# --------------------------------------------------------------------------- inputs

def positive_id(value, where):
    wid = clean_text(value)
    assert wid.isdigit() and int(wid) > 0, f"{where}: no usable workshop id ({value!r})"
    return wid


def read_broken(path):
    """broken.json -> (crashers, packs, sweep), each list keyed by workshop id as a string. Strict about everything it
    would otherwise have to guess: the bridge reads the same file, so a malformed entry here is one there too."""
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    assert isinstance(doc, dict) and isinstance(doc.get("maps"), list) and isinstance(doc.get("packs"), list), \
        f"{path}: expected an object with a 'maps' list and a 'packs' list"

    crashers = {}
    for n, e in enumerate(doc["maps"]):
        assert isinstance(e, dict), f"{path} maps[{n}]: the banner needs evidence, so a bare id is not enough ({e!r})"
        wid = positive_id(e.get("id"), f"{path} maps[{n}]")
        assert wid not in crashers, f"{path}: map {wid} is listed twice"
        evidence = clean_text(e.get("evidence"))
        assert evidence, f"{path} maps[{n}] ({wid}): no evidence text for the banner"
        crashes = e.get("crashes", 0)
        assert isinstance(crashes, int) and crashes >= 0, f"{path} maps[{n}] ({wid}): crashes must be a count"
        rank = e.get("rank")
        assert rank is None or (isinstance(rank, int) and rank > 0), f"{path} maps[{n}] ({wid}): rank {rank!r}"
        crashers[wid] = {"id": int(wid), "rank": rank, "name": clean_text(e.get("name")), "crashes": crashes,
                         "evidence": evidence}

    packs, quiet = {}, []
    for n, e in enumerate(doc["packs"]):
        assert isinstance(e, dict), f"{path} packs[{n}]: a non-map needs its evidence, so a bare id is not enough ({e!r})"
        wid = positive_id(first(e, "id", "workshop_id", "publishedfileid"), f"{path} packs[{n}]")
        raw = clean_text(first(e, "evidence", "kind_of_evidence")).lower()
        assert raw in EVIDENCE, (f"{path} packs[{n}] ({wid}): evidence {raw!r} is none of {TEST!r}, {DESC!r} and {WSF!r}; "
                                 "refusing to classify on a guess")
        kind = clean_text(e.get("kind")) or "pack"
        assert kind in KINDS, f"{path} packs[{n}] ({wid}): kind {kind!r} is not one of {sorted(KINDS)}"
        assert wid not in packs, f"{path}: non-map {wid} is listed twice"
        assert wid not in crashers, f"{path}: {wid} is both a broken map and a non-map - pick one"
        packs[wid] = {"id": int(wid), "name": clean_text(first(e, "name", "title")), "kind": kind,
                      "evidence": EVIDENCE[raw], "phrase": clean_text(e.get("phrase")),
                      "detail": clean_text(e.get("detail")), "preview": clean_text(e.get("preview")),
                      "description": clean_text(e.get("description"))}
        if EVIDENCE[raw] == DESC and not packs[wid]["phrase"]:
            quiet.append(wid)
    if quiet:
        print(f"warning: {len(quiet)} non-map(s) identified from description carry no matching phrase: {', '.join(quiet)}")
    sweep = doc.get("sweep") if isinstance(doc.get("sweep"), dict) else {}
    return crashers, packs, sweep


def read_removed(path, crashers, packs):
    """broken.json's optional "removed" list -> {wid: info}. Strict like the others: an id, no duplicates, and on no other list."""
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    removed = {}
    for n, e in enumerate(doc.get("removed") or []):
        assert isinstance(e, dict), f"{path} removed[{n}]: the banner needs an entry, not a bare id ({e!r})"
        wid = positive_id(e.get("id"), f"{path} removed[{n}]")
        assert wid not in removed, f"{path}: removed map {wid} is listed twice"
        assert wid not in crashers and wid not in packs, f"{path}: {wid} is on the removed list and on another - pick one"
        rank = e.get("rank")
        assert rank is None or (isinstance(rank, int) and rank > 0), f"{path} removed[{n}] ({wid}): rank {rank!r}"
        removed[wid] = {"id": int(wid), "rank": rank, "name": clean_text(e.get("name")), "since": clean_text(e.get("since")),
                        "evidence": clean_text(e.get("evidence")) or "removed from the Workshop - nobody can download it"}
    return removed


def read_ours(path):
    """ours.json -> [(workshop id, name)] in the file's order; no file (or no path) is none of our own."""
    if not path or not Path(path).exists():
        return []
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    assert isinstance(doc, dict) and isinstance(doc.get("maps"), list), f"{path}: expected an object with a 'maps' list"
    out = []
    for n, e in enumerate(doc["maps"]):
        wid = positive_id(e.get("id") if isinstance(e, dict) else e, f"{path} maps[{n}]")
        assert wid not in (w for w, _ in out), f"{path}: {wid} is listed twice"
        out.append((wid, clean_text(e.get("name")) if isinstance(e, dict) else ""))
    return out


def read_bridge_maps(path):
    return {int(m["rank"]): str(m["id"]) for m in json.loads(Path(path).read_text(encoding="utf-8"))} if path else None


# --------------------------------------------------------------------------- the page: rows

@dataclass
class Row:
    start: int  # line of the <li>
    end: int    # line of its </li>
    rank: int
    wid: str
    name: str


def cmd_lines(rank, wid):
    """A map row's copy button, exactly as the page builder writes it. Asserted on every row on the way in, so a
    pack row that loses it can have it back byte for byte. Since 2026-10-21 it hands over the map's Workshop ID: the
    chat commands are gone, and an ID pasted into the game chat opens the in-game menu on that map (rank is unused)."""
    return [f'  <button class="cmd" type="button" data-chat="{wid}" data-console="host_workshop_map {wid}" '
            'title="Copy, then paste into the game chat: the menu opens on this map">',
            f'    <code class="chat">{wid}</code>',
            f'    <code class="console">host_workshop_map {wid}</code>',
            '    <span class="copy" aria-hidden="true">Copy</span>',
            "  </button>"]


def list_bounds(L):
    opens = [i for i, l in enumerate(L) if l == LIST_OPEN]
    assert len(opens) == 1, f"expected one map list, found {len(opens)}"
    return opens[0], next(i for i in range(opens[0] + 1, len(L)) if L[i] == LIST_CLOSE)


def parse_rows(L):
    """Every row of #list on an unstamped page, with the structure each one must have asserted on the way."""
    open_at, close_at = list_bounds(L)
    rows, i = [], open_at + 1
    while i < close_at:
        assert RE_LI.match(L[i]), f"line {i + 1} of #list is not the start of a clean row: {L[i][:80]!r}"
        rank_m, shot_m = RE_RANK.match(L[i + 1]), RE_SHOT.match(L[i + 2])
        assert rank_m and shot_m, f"row at line {i + 1}: no rank div or shot anchor where expected"
        rank, wid = int(rank_m.group(1)), shot_m.group(1)
        j = i + 3
        while L[j] != "</li>":
            j += 1
            assert j < close_at, f"row #{rank} never closes"
        assert L[j - 5:j] == cmd_lines(rank, wid), f"row #{rank}: the copy button is not the template strip_page() regenerates"
        names = [RE_NAME.match(l).group(1) for l in L[i:j] if RE_NAME.match(l)]
        assert len(names) == 1, f"row #{rank}: {len(names)} name headings"
        rows.append(Row(i, j, rank, wid, htmllib.unescape(names[0])))
        i = j + 1
    return open_at, close_at, rows


def check_against_bridge(rows, table, full):
    """Every row's rank must name the same workshop id in the bridge's maps.json. The page's map numbers ARE the
    bridge's ranks - the number a player types on the menu's keyboard - so the day maps.json is re-ranked or backfilled
    this page goes stale, and map 210 from the page would be a different map in the game. That is a rebuild of the rows (build-1000.py), never something to stamp over."""
    bad = [(r.rank, r.wid, table.get(r.rank)) for r in rows if table.get(r.rank) != r.wid]
    assert not bad, (f"the page and the bridge's maps.json disagree on {len(bad)} ranks, e.g. {bad[:3]} (rank, page id, "
                     "bridge id): a map number found on this page would name the wrong map in the game. Rebuild the page "
                     "from the bridge's maps.json (build-1000.py) before stamping it.")
    if full:
        assert sorted(table) == [r.rank for r in rows], (f"the bridge lists {len(table)} ranks and the page {len(rows)}: "
                                                         "one of them is not the list the other was built from")


# The header facts build-1000.py writes from its rows: the map count (meta description, standfirst, the Maps fact and the
# pre-rendered #shown counter), the combined subscriptions and the maps under 25 MB. They describe whatever #list holds,
# so a stamp that moves rows out of the list recomputes them, and the strip recomputes them again once the rows are back.
# (The first version of this script moved 44 packs out and left "Combined subs", "Under 25 MB" and "The 1,000 ..." stale.)
# The list is the 1,000 most-subscribed Arms Race maps (ranks 1-1000, rank-stable since 2026-10-03), then maps picked by hand
# after them: the 16 glass maps of 2026-10-21 are #1001-#1016; then, since 2026-10-22, every other Arms Race map that loads
# (#1017 and up, by subscribers). The meta description and the standfirst count them apart ("The 1,000 most-subscribed ... ,
# plus 16 hand-picked glass maps and 2,548 more Arms Race maps,"); the Maps fact counts every row. Kept the same in
# build-1000.py and broken-maps.py: the builder writes the sentence, the stamper re-stamps it from the rows it finds.
MOST_SUBSCRIBED = 1000
EXTRA_WHAT = "hand-picked glass maps"
GLASS_LAST = 1016
MORE_WHAT = "more Arms{}Race maps"


def extra_text(glass, more, nbsp=" "):
    """', plus 16 hand-picked glass maps' / ', plus 16 hand-picked glass maps and 2,548 more Arms Race maps' / ''."""
    parts = ([f"{glass:,} {EXTRA_WHAT}"] if glass else []) + ([f"{more:,} {MORE_WHAT.format(nbsp)}"] if more else [])
    return ", plus " + " and ".join(parts) if parts else ""


RE_FACTS = [
    ("meta", re.compile(r'(<meta name="description" content="The )([\d,]+)( most-subscribed )')),
    ("meta_extra", re.compile(r'( most-subscribed Counter-Strike 2 Arms Race workshop maps)((?:, plus [^"<]*?)?)(, with verified )')),
    ("standfirst", re.compile(r'(<p class="standfirst">The )([\d,]+)( most-subscribed )')),
    ("standfirst_extra", re.compile(r'( most-subscribed Arms&nbsp;Race maps on the Steam Workshop)((?:, plus [^<]*?,)?)( that)')),
    ("maps", re.compile(r"(      <div><dt>Maps</dt><dd>)([\d,]+)(</dd></div>)")),
    ("subs", re.compile(r"(      <div><dt>Combined subs</dt><dd>)([\d,]+)(</dd></div>)")),
    ("tiny", re.compile(r"(      <div><dt>Under 25 MB</dt><dd>)([\d,]+)(</dd></div>)")),
    ("shown", re.compile(r'(<span class="shown" id="shown">)(\d+ of \d+)(</span>)')),
]
RE_SUBS = re.compile(r'<dt>Subscribers</dt><dd class="num">([\d,]+)</dd>')


def row_facts(blocks):
    """Count, the hand-picked ones among them, combined subscriptions and maps under 25 MB of some row blocks, read back the
    way build-1000.py wrote them."""
    subs = tiny = extra = more = 0
    for block in blocks:
        found = [m.group(1) for m in map(RE_SUBS.search, block) if m]
        assert len(found) == 1, f"a row with {len(found)} Subscribers stats: {block[0][:60]!r}"
        subs += int(found[0].replace(",", ""))
        tiny += 'data-band="tiny"' in block[0]
        ranks = [int(m.group(1)) for m in map(RE_RANK.match, block) if m]
        assert len(ranks) == 1, f"a row with {len(ranks)} rank divs: {block[0][:60]!r}"
        extra += ranks[0] > MOST_SUBSCRIBED
        more += ranks[0] > GLASS_LAST
    return {"count": len(blocks), "extra": extra, "more": more, "subs": subs, "tiny": tiny}


def facts_text(f):
    n, ranked, extra, more = f"{f['count']:,}", f"{f['count'] - f['extra']:,}", f["extra"], f.get("more", 0)
    return {"meta": ranked, "meta_extra": extra_text(extra - more, more),
            "standfirst": ranked, "standfirst_extra": extra_text(extra - more, more, "&nbsp;") + ("," if extra else ""),
            "maps": n, "subs": f"{f['subs']:,}", "tiny": f"{f['tiny']:,}", "shown": f"{f['count']} of {f['count']}"}


def page_facts(html):
    got = {}
    for label, rx in RE_FACTS:
        found = rx.findall(html)
        assert len(found) == 1, f"facts: expected one {label} line, found {len(found)}"
        got[label] = found[0][1]
    return got


def set_facts(html, facts):
    text = facts_text(facts)
    for label, rx in RE_FACTS:
        html, n = rx.subn(lambda m: m.group(1) + text[label] + m.group(3), html, count=1)
        assert n == 1, f"facts: no {label} line to rewrite"
    return html


def list_blocks(L):
    """The row blocks #list holds right now (any row class: plain, broken, or a pack row not yet moved)."""
    open_at, close_at = list_bounds(L)
    blocks, i = [], open_at + 1
    while i < close_at:
        assert L[i].startswith('<li class="row'), f"line {i + 1} of #list is not a row start: {L[i][:60]!r}"
        j = L.index("</li>", i)
        blocks.append(L[i:j + 1])
        i = j + 1
    return blocks


# --------------------------------------------------------------------------- the page: broken maps

def css_block(nl):
    """One appended override block. It touches no existing rule and declares its own palette across all
    three places this page states a theme (:root, the prefers-color-scheme block, and [data-theme]), so
    the stamp reads correctly in light, dark and forced-dark without editing the theme blocks."""
    return nl.join([
        "",
        f"  /* ---------- {SENTINEL}: rows for maps that crash the server ---------- */",
        "  :root { --broken: #BE2016; --broken-ink: #FFFFFF; --broken-soft: #FFF1EF; }",
        "  @media (prefers-color-scheme: dark) {",
        "    :root:not([data-theme=\"light\"]) { --broken: #FF5F4D; --broken-ink: #1B0C0A; --broken-soft: #2B1411; }",
        "  }",
        "  :root[data-theme=\"dark\"] { --broken: #FF5F4D; --broken-ink: #1B0C0A; --broken-soft: #2B1411; }",
        "",
        "  /* the row: a tinted body and a solid rail down the left edge (inset, so the grid does not shift) */",
        "  .row.broken {",
        "    grid-template-areas: \"flag flag flag flag\" \"rank shot meta stats\" \". cmd cmd cmd\";",
        "    background: var(--broken-soft);",
        "    box-shadow: inset 5px 0 0 var(--broken);",
        "  }",
        "  .row.broken:hover { background: color-mix(in srgb, var(--broken) 14%, var(--broken-soft)); }",
        "",
        "  /* the banner: full row width, so it is still there once the thumbnail has scrolled past */",
        "  .broken-flag {",
        "    grid-area: flag; display: flex; flex-wrap: wrap; align-items: baseline; gap: .5rem .9rem;",
        "    margin: 0 0 .35rem; padding: .5rem .8rem;",
        "    background: var(--broken); color: var(--broken-ink);",
        "    font-family: var(--f-mono); font-size: .78rem; line-height: 1.3;",
        "  }",
        "  .broken-flag strong {",
        "    font-family: var(--f-display); font-size: 1.15rem; font-weight: 600;",
        "    letter-spacing: .14em; text-transform: uppercase;",
        "  }",
        "  .broken-flag span { letter-spacing: .04em; text-transform: uppercase; }",
        "  .broken-flag em { margin-left: auto; font-style: normal; opacity: .82; white-space: nowrap; }",
        "",
        "  /* the sticker: the screenshot is flattened to grey first, so the sash reads the same over all",
        "     1000 of them rather than depending on whatever the map author photographed */",
        "  .row.broken .shot { position: relative; overflow: hidden; border-color: var(--broken); }",
        "  .row.broken .shot img { filter: grayscale(1) brightness(.46) contrast(.9); }",
        "  .row.broken .shot::before, .row.broken .shot::after { position: absolute; pointer-events: none; }",
        "  .row.broken .shot::before { content: \"\"; inset: 0; box-shadow: inset 0 0 0 2px var(--broken); }",
        "  .row.broken .shot::after {",
        "    content: \"BROKEN\"; left: -22%; right: -22%; top: 50%;",
        "    transform: translateY(-50%) rotate(-11deg);",
        "    background: var(--broken); color: var(--broken-ink);",
        "    font-family: var(--f-display); font-size: 1.2rem; font-weight: 600;",
        "    letter-spacing: .22em; text-align: center; padding: .2em 0;",
        "    box-shadow: 0 0 0 1px color-mix(in srgb, var(--broken-ink) 30%, transparent);",
        "  }",
        "  .row.broken .shot:hover { border-color: var(--broken); }",
        "",
        "  /* the copy button keeps working and keeps its label: the warning is a pill in front of the",
        "     command, never the .copy span, because the clipboard handler rewrites that text */",
        "  .row.broken .cmd {",
        "    border-color: var(--broken);",
        "    background: color-mix(in srgb, var(--broken) 10%, var(--surface-2));",
        "  }",
        "  .row.broken .cmd::before {",
        "    content: \"BROKEN\"; flex: none;",
        "    background: var(--broken); color: var(--broken-ink);",
        "    font-family: var(--f-mono); font-size: .62rem; letter-spacing: .12em;",
        "    padding: .2em .45em; border-radius: 2px;",
        "  }",
        "  .row.broken .cmd code { color: var(--broken); }",
        "  .row.broken .cmd:hover { border-color: var(--broken); }",
        "  .row.broken .cmd:hover .copy { color: var(--broken); }",
        "  .row.broken .cmd.done { border-color: var(--tiny); }  /* the copied-confirmation still shows */",
        "",
        "  .facts dd.broken-count { color: var(--broken); }",
        "",
        "  /* a map gone from the Workshop: the same banner and sash, saying what it is */",
        "  .row.removed .shot::after { content: \"REMOVED\"; }",
        "  .row.removed .cmd::before { content: \"REMOVED\"; }",
        "",
        "  /* the banner needs its own grid row at each of the page's two breakpoints */",
        "  @media (max-width: 900px) {",
        "    .row.broken { grid-template-areas: \"flag flag flag\" \"rank shot meta\" \". stats stats\" \". cmd cmd\"; }",
        "  }",
        "  @media (max-width: 560px) {",
        "    .row.broken { grid-template-areas: \"flag flag\" \"rank shot\" \"rank meta\" \"stats stats\" \"cmd cmd\"; }",
        "    .broken-flag em { margin-left: 0; }",
        "  }",
        CSS_END,
    ])


WARN_OLD = [
    '      <section class="warn">',
    "        <h3>If the server crashes</h3>",
    "        <p>Roughly 1 in 20 workshop maps crashes it. It restarts by itself within a minute and the",
    "          chat tells you which map did it. Pick another; a known crasher asks you to confirm first.</p>",
    "      </section>",
]


def warn_new(n, sweep, gone=0):
    """The how-to card, rewritten so the banners are explained rather than just appearing. Every map on the list was
    load-tested (the sweep in broken.json), so a row without a banner is a map that loaded, not merely an untested one."""
    when = f" on {sweep['date']}" if sweep.get("date") else ""
    lines = ['      <section class="warn">', "        <h3>Maps that crash it</h3>"]
    if n:
        lines += [
            f"        <p><strong>{n:,} map{'' if n == 1 else 's'} on this list {'is' if n == 1 else 'are'} flagged for crashing or "
            "freezing the server</strong>",
            "          &mdash; a red banner says what the test saw, and picking one in the game&rsquo;s menu asks you to confirm",
            "          before it switches.</p>",
        ]
    if gone:
        lines += [
            f"        <p><strong>{gone:,} map{'' if gone == 1 else 's'} {'was' if gone == 1 else 'were'} removed from the Workshop</strong>",
            "          &mdash; nobody can download them, so the server refuses them; it checks the Workshop daily.</p>",
        ]
    lines += [
        f"        <p>Every map on the list was load-tested on the server{when}; no banner means it loaded. If one still",
        "          takes the server down, it restarts by itself within a minute and the screen says which map did it.</p>",
        "      </section>",
    ]
    return lines


def broken_edits(src_lines, nl, n, sweep, gone=0):
    """The broken-map whole-text edits, as (label, original, injected); each original must appear exactly once."""
    verified = [l for l in src_lines if "<dt>Verified</dt>" in l]
    assert len(verified) == 1, f"expected one Verified fact line, found {len(verified)}"
    console = [l for l in src_lines if 'id="adv"' in l and "toggle" in l]
    assert len(console) == 1, f"expected one Console toggle line, found {len(console)}"

    fact = nl.join(([f'      <div><dt>Known broken</dt><dd class="broken-count">{n:,}</dd></div>'] if n else [])
                   + ([f'      <div><dt>Gone from the Workshop</dt><dd class="broken-count">{gone:,}</dd></div>'] if gone else []))
    toggle = ('    <label class="toggle" title="Leave out the maps that crash the server or are gone from the Workshop">'
              f'<input type="checkbox" id="hidebroken" /> Hide broken ({n + gone:,})</label>')
    adv_js = "  adv.addEventListener('change', function () { list.classList.toggle('adv', adv.checked); });"
    return [
        ("css", "</style>", css_block(nl) + nl + "</style>"),
        ("facts-count", verified[0], fact + nl + verified[0]),
        ("howto-warn", nl.join(WARN_OLD), nl.join(warn_new(n, sweep, gone))),
        ("toolbar-toggle", console[0], toggle + nl + console[0]),
        ("script-vars", adv_js, nl.join([
            adv_js,
            "  var hideBroken = false; // bm",
            "  var hb = document.getElementById('hidebroken'); // bm",
            "  hb.addEventListener('change', function () { hideBroken = hb.checked; apply(); }); // bm",
        ])),
        # #shown and the empty-list notice both come off this one `show`, so the counter stays truthful
        # ("997 of 1000") the moment broken rows are hidden -- no second counter to keep in step.
        ("script-filter", SHOW_CLEAN, SHOW_STAMPED),
    ]


def flag_html(info):
    """The banner: broken.json's own evidence text for this map, so each banner says what the test actually saw."""
    evidence = htmllib.escape(info["evidence"]).replace(" - ", " &mdash; ")
    return ('  <div class="broken-flag" data-bm="1"><strong>Broken</strong>'
            f"<span>{evidence}</span>"
            "<em>Picking it asks you to confirm</em></div>")


def removed_flag_html(info):
    """The banner for a map gone from the Workshop, in the broken banner's style."""
    evidence = htmllib.escape(info["evidence"]).replace(" - ", " &mdash; ")
    return ('  <div class="broken-flag" data-bm="1"><strong>Removed from the Workshop</strong>'
            f"<span>{evidence}</span>"
            "<em>The server refuses it</em></div>")


SHOW_STAMPED = "      var show = okBand && okTerm && (!hideBroken || !row.classList.contains('broken')); // bm"
SHOW_CLEAN = "      var show = okBand && okTerm;"


def strip_broken(html, nl):
    """Undo the broken-map stamp. Every injection is removed by the marker it was given. Also drops every other
    data-bm line on the page, which is how the pack evidence lines go -- strip_packs() runs after this."""
    html = html.replace(SHOW_STAMPED, SHOW_CLEAN)  # first: the rewritten line also carries the // bm marker
    out = [l for l in html.split(nl)
           if 'data-bm="1"' not in l and 'id="hidebroken"' not in l and "<dt>Known broken</dt>" not in l
           and "<dt>Gone from the Workshop</dt>" not in l and not l.endswith("// bm")]
    html = nl.join(l.replace('<li class="row broken removed" ', '<li class="row" ').replace('<li class="row broken" ', '<li class="row" ') for l in out)
    start = html.find(f"{nl}{nl}  /* ---------- {SENTINEL}")
    if start != -1:
        html = html[:start] + html[html.index(CSS_END, start) + len(CSS_END):]
    card = re.search(r'      <section class="warn">.*?      </section>', html, re.S)
    if card and "Maps that crash it" in card.group(0):
        html = html.replace(card.group(0), nl.join(WARN_OLD), 1)
    return html


# --------------------------------------------------------------------------- the page: not maps (the "packs" list)

def pk_css(nl):
    """Tabs and the not-maps listed in them, as one more appended block with its own palette in all three theme
    places. A neutral slate, not the red: a non-map is not a danger on the map list any more, it is simply not a map."""
    return nl.join([
        "",
        f"  /* ---------- {PK_CSS}: the second tab, and the not-maps listed in it ---------- */",
        "  :root { --pack: #44505B; --pack-ink: #FFFFFF; }",
        "  @media (prefers-color-scheme: dark) {",
        "    :root:not([data-theme=\"light\"]) { --pack: #A9B8C4; --pack-ink: #131619; }",
        "  }",
        "  :root[data-theme=\"dark\"] { --pack: #A9B8C4; --pack-ink: #131619; }",
        "",
        "  /* tabs are their own buttons, never .chip: the chip handler sets the band from data-band on every .chip",
        "     it finds, and a tab has none, so a chip-tab would empty the whole list */",
        "  .tabs { display: flex; gap: .25rem; margin: 2.5rem 0 0; border-bottom: 1px solid var(--line); }",
        "  .tabs + .toolbar { margin-top: 0; }",
        "  .tab {",
        "    font-family: var(--f-mono); font-size: .78rem; letter-spacing: .08em; text-transform: uppercase;",
        "    cursor: pointer; padding: .75rem 1rem .65rem; margin-bottom: -1px; color: var(--text-dim);",
        "    background: none; border: 0; border-bottom: 2px solid transparent;",
        "  }",
        "  .tab span { margin-left: .35rem; color: var(--text-faint); font-variant-numeric: tabular-nums; }",
        "  .tab:hover { color: var(--text); }",
        "  .tab[aria-selected=\"true\"] { color: var(--text); font-weight: 600; border-bottom-color: var(--accent); }",
        "  .tab:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }",
        "",
        "  /* the switch is one class on <body>; the map list, its toolbar and its empty notice keep their own",
        "     state underneath it, so a search, a size band or Hide broken survive a trip to the other tab */",
        "  .packs { display: none; }",
        "  body.show-packs .packs { display: block; }",
        "  body.show-packs .toolbar, body.show-packs #list, body.show-packs #empty { display: none; }",
        "  .packs-intro { max-width: 70ch; margin: 1.6rem 0 .4rem; font-size: .92rem; color: var(--text-dim); }",
        "",
        "  /* a pack row: picture, name, description and the evidence; no rank, no stats, no copy button */",
        "  .packlist .row.pack { grid-template-columns: 168px minmax(0, 1fr); grid-template-areas: \"shot meta\" \"shot evid\"; }",
        "  .packlist .row.pack .rank, .packlist .row.pack .stats { display: none; }",
        "  .pack-evid {",
        "    grid-area: evid; display: flex; flex-wrap: wrap; align-items: baseline; gap: .35rem .85rem;",
        "    font-size: .84rem; min-width: 0;",
        "  }",
        "  .pk-kind {",
        "    font-family: var(--f-mono); font-size: .66rem; letter-spacing: .1em; text-transform: uppercase;",
        "    padding: .2em .5em; border: 1px solid var(--pack); border-radius: 2px; white-space: nowrap;",
        "  }",
        "  .pk-kind.pk-test { background: var(--pack); color: var(--pack-ink); }  /* filled: seen failing on the server */",
        "  .pk-kind.pk-desc { color: var(--pack); }                               /* outlined: read from its description */",
        "  .pk-detail { color: var(--text-dim); }",
        "  .pk-phrase { font-family: var(--f-mono); font-size: .78rem; color: var(--text); overflow-wrap: anywhere; }",
        "  .pk-id { font-family: var(--f-mono); font-size: .8rem; color: var(--accent); }",
        "  .pk-chat { font-family: var(--f-mono); font-size: .72rem; color: var(--text-faint); }",
        "  .facts dd.pack-count { color: var(--pack); }",
        "  @media (max-width: 560px) {",
        "    .packlist .row.pack { grid-template-columns: minmax(0, 1fr); grid-template-areas: \"shot\" \"meta\" \"evid\"; }",
        "  }",
        PK_CSS_END,
    ])


def evidence_html(info, rank=None):
    e = htmllib.escape
    kind = info["evidence"]
    bits = [f'<span class="pk-kind {"pk-test" if kind == TEST else "pk-desc"}">{e(kind[0].upper() + kind[1:])}</span>']
    if info.get("detail"):  # broken.json is plain ASCII for the bridge; " - " is a dash on the page
        bits.append(f'<span class="pk-detail">{e(info["detail"]).replace(" - ", " &mdash; ")}</span>')
    if info.get("phrase"):
        bits.append(f'<span class="pk-phrase">&ldquo;{e(info["phrase"])}&rdquo;</span>')
    bits.append(f'<a class="pk-id" href="{STEAM.format(info["id"])}" target="_blank" rel="noopener noreferrer">'
                f'Workshop {info["id"]}</a>')
    bits.append('<span class="pk-chat">Not in the game&rsquo;s menu, and its ID is refused</span>')
    return '  <div class="pack-evid" data-bm="1">' + "".join(bits) + "</div>"


def pack_row(block, info, rank):
    """A map row turned into a pack row: the same markup with `pack` added to its class, the evidence line after
    the <li>, and the five lines of the copy button gone (strip_packs() regenerates them from cmd_lines)."""
    assert block[-1] == "</li>" and block[-6:-1] == cmd_lines(rank, str(info["id"]))
    return ([block[0].replace('<li class="row" ', '<li class="row pack" ', 1), evidence_html(info, rank)]
            + block[1:-6] + ["</li>"])


def generated_row(info, have_img):
    """A non-map that is not a row on the page (all of them, once the list is rank-stable): one marked line, built from
    broken.json's own name, preview and description."""
    e, wid, name = htmllib.escape, str(info["id"]), info["name"]
    link = f'href="{STEAM.format(wid)}" target="_blank" rel="noopener noreferrer"'
    if have_img:
        shot = (f'<a class="shot" {link}><img src="img/{wid}.webp" alt="Preview of {e(name)}" loading="lazy" '
                'decoding="async" width="336" height="189" /></a>')
    elif info.get("preview"):
        shot = (f'<a class="shot" {link}><img src="{e(info["preview"])}" alt="Preview of {e(name)}" loading="lazy" '
                'decoding="async" width="336" height="189" referrerpolicy="no-referrer" /></a>')
    else:
        shot = f'<a class="shot missing" {link}>No preview</a>'
    desc = info.get("description") or ""
    meta = (f'<div class="meta"><h2 class="name" dir="auto">{e(name)}</h2>'
            + (f'<p class="desc" dir="auto">{e(desc)}</p>' if desc else "") + "</div>")
    return f'<li class="row pack gen" data-bm="1">{shot}{meta}{evidence_html(info).strip()}</li>'


def tabs_block(n_maps, n_packs):
    return [
        PK_TABS,
        '  <div class="tabs" role="tablist" aria-label="Lists on this page">',
        f'    <button class="tab" type="button" role="tab" id="tab-maps" aria-controls="list" aria-selected="true">'
        f'Maps<span>{n_maps:,}</span></button>',
        f'    <button class="tab" type="button" role="tab" id="tab-packs" aria-controls="packs" aria-selected="false" '
        f'tabindex="-1">Not maps<span>{n_packs:,}</span></button>',
        "  </div>",
        PK_TABS_END,
    ]


def packs_panel(moved, generated):
    return [
        "",
        PK_PANEL,
        '  <section class="packs" id="packs" role="tabpanel" aria-labelledby="tab-packs">',
        '    <p class="packs-intro">Workshop items that carry the Arms&nbsp;Race tag but hold no playable map: skin and',
        "      model packs, configs, sound and viewmodel packs, empty uploads, and CS:GO-era uploads with no CS2 map in them. Loading one leaves the server frozen",
        "      or crashes it, so the game&rsquo;s menu leaves them out and the server refuses their IDs. They are listed here only so you",
        "      can recognise one.</p>",
        '    <ol class="list packlist" id="packlist">',
        *[line for block in moved for line in block],
        *generated,
        "    </ol>",
        "  </section>",
        PK_PANEL_END,
    ]


def tabs_script():
    """The tab switch, as its own script: it needs nothing from the map list's script and touches nothing in it."""
    return [
        PK_JS,
        "(function () {",
        "  var tabs = Array.prototype.slice.call(document.querySelectorAll('.tabs [role=\"tab\"]'));",
        "  function show(name, remember) {",
        "    document.body.classList.toggle('show-packs', name === 'packs');",
        "    tabs.forEach(function (t) {",
        "      var on = t.id === 'tab-' + name;",
        "      t.setAttribute('aria-selected', String(on));",
        "      t.tabIndex = on ? 0 : -1;",
        "    });",
        "    if (remember) {",
        "      try { history.replaceState(null, '', name === 'packs' ? '#not-maps' : location.pathname + location.search); }",
        "      catch (e) { /* a file:// page may refuse; the tab has switched either way */ }",
        "    }",
        "  }",
        "  tabs.forEach(function (t, i) {",
        "    t.addEventListener('click', function () { show(t.id.slice(4), true); });",
        "    t.addEventListener('keydown', function (ev) {",
        "      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;",
        "      var next = tabs[(i + (ev.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length];",
        "      next.focus();",
        "      show(next.id.slice(4), true);",
        "    });",
        "  });",
        "  show(location.hash === '#not-maps' ? 'packs' : 'maps', false);",
        "})();",
        PK_JS_END,
    ]


def pack_edits(nl, total, n_maps, n_packs, moved, generated):
    """The not-maps whole-text edits, as (label, original, injected); each original must appear exactly once. The Maps
    fact is asserted to read `total` (the rows the page came with) as the anchor; the counts themselves are set by
    set_facts() afterwards, from the rows the list really holds."""
    maps_fact = f"      <div><dt>Maps</dt><dd>{total:,}</dd></div>"
    empty = '  <p class="empty" id="empty">No map matches that filter.</p>'
    toolbar = '  <div class="toolbar">'
    return [
        ("pk-css", "</style>", pk_css(nl) + nl + "</style>"),
        ("pk-facts", maps_fact, maps_fact + nl + f'      <div><dt>Not maps</dt><dd class="pack-count">{n_packs:,}</dd></div>'),
        ("pk-tabs", toolbar, nl.join(tabs_block(n_maps, n_packs)) + nl + toolbar),
        ("pk-panel", empty, empty + nl + nl.join(packs_panel(moved, generated))),
        ("pk-script", "</body>", nl.join(tabs_script()) + nl + "</body>"),
    ]


def strip_packs(html, nl):
    """Undo the not-maps stamp: the moved rows go back into #list at their rank with their copy button
    regenerated, the header facts are recomputed from the rows the list holds again, and every added block is cut
    out marker to marker. Runs after strip_broken(), so the evidence lines are already gone."""
    start = html.find(f"{nl}{nl}{PK_PANEL}")
    if start == -1:
        return html
    end = html.index(PK_PANEL_END, start) + len(PK_PANEL_END)
    panel = html[start:end].split(nl)
    html = html[:start] + html[end:]

    restored, i = [], 0
    while i < len(panel):
        if panel[i].startswith('<li class="row pack" '):
            j = panel.index("</li>", i)
            body = [l for l in panel[i + 1:j] if 'data-bm="1"' not in l]
            rank, wid = int(RE_RANK.match(body[0]).group(1)), RE_SHOT.match(body[1]).group(1)
            restored.append((rank, [panel[i].replace('<li class="row pack" ', '<li class="row" ', 1), *body,
                                    *cmd_lines(rank, wid), "</li>"]))
            i = j + 1
        else:
            i += 1
    restored.sort(key=lambda t: t[0])

    L = html.split(nl)
    open_at, close_at = list_bounds(L)
    out, k = L[:open_at + 1], open_at + 1
    while k < close_at:
        assert RE_LI.match(L[k]), f"strip: line {k + 1} of #list is not a clean row start"
        rank = int(RE_RANK.match(L[k + 1]).group(1))
        while restored and restored[0][0] < rank:
            out.extend(restored.pop(0)[1])
        j = L.index("</li>", k)
        out.extend(L[k:j + 1])
        k = j + 1
    for _, block in restored:
        out.extend(block)
    out.extend(L[close_at:])
    html = nl.join(l for l in out if "<dt>Not maps</dt>" not in l and "<dt>Skin packs</dt>" not in l)
    html = set_facts(html, row_facts(list_blocks(html.split(nl))))
    for a, b in ((PK_TABS, PK_TABS_END + nl), (PK_JS, PK_JS_END + nl)):
        s = html.find(a)
        if s != -1:
            html = html[:s] + html[html.index(b, s) + len(b):]
    s = html.find(f"{nl}{nl}  /* ---------- {PK_CSS}")
    if s != -1:
        html = html[:s] + html[html.index(PK_CSS_END, s) + len(PK_CSS_END):]
    return html


# --------------------------------------------------------------------------- the page: the guide (how to play)

# The builder's masthead lines the guide stamps over: asserted on the way in, put back verbatim by strip_guide().
GD_EYEBROW = f'    <p class="eyebrow">Counter-Strike 2 &middot; {SERVER_NAME} &middot; connect {CONNECT}</p>'
GD_H1 = f"    <h1>{SERVER_HTML}</h1>"
GD_HOWTO = '    <div class="howto">'
GD_CARDS_OLD = [  # "Where to type it" and "Joining": both now said by the guide and the standfirst
    "      <section>",
    "        <h3>Where to type it</h3>",
    "        <p>In the game chat, not the console. Press <code>Y</code>, paste, Enter. Anyone on the",
    "          server can do it; the map changes after an 8-second countdown and the chat says who picked it.</p>",
    "        <p>Running your own server? Flip <em>Console</em> in the toolbar to get the",
    "          <code>host_workshop_map</code> command instead.</p>",
    "      </section>",
    "      <section>",
    "        <h3>Joining</h3>",
    f"        <p><code>connect {CONNECT}</code> in the CS2 console, or look for",
    f"          <em>{SERVER_NAME}</em> in the community browser. No password.</p>",
    "      </section>",
]
GD_STANDFIRST_OLD = [  # promised an instant switch and told you to type !map; the menu replaced the chat commands (2026-10-21)
    "      actually load in Counter-Strike&nbsp;2. Every ID verified live against Valve's API. Click a",
    "      map's <code>!map</code> command to copy it, then paste it into the game chat on the",
    f"      {SERVER_HTML} server: it switches after a short countdown and tells everyone who picked it.</p>",
]
GD_STANDFIRST_NEW = [
    "      actually load in Counter-Strike&nbsp;2. Every ID verified live against Valve's API. In the game it all",
    "      happens in the menu: hold TAB and press E, then find a map by its number here, its name or its",
    "      tags, and click Play. Or copy a map's Workshop ID here and paste it into the game chat: the menu",
    "      opens on that map. Running your own server? Flip <em>Console</em> in the toolbar for the",
    "      <code>host_workshop_map</code> command instead.</p>",
]

GUIDE_CSS = """
  /* ---------- GUIDE-CSS-START: how to play, the first thing on the page ---------- */
  /* Only the page's own variables, so it follows all three theme places with no palette of its own. Every class is
     gd-; the one borrowed is .cmd on the Join copy button, for the map rows' exact look - it sits outside #list, so
     the rows' copy handler never sees it. Sized so the five menu groups end above a laptop's fold (~600px). */
  header.mast { padding-top: 1.75rem; }
  .gd-hero {
    display: grid; grid-template-columns: minmax(0, 1fr) minmax(19rem, 26rem);
    align-items: end; gap: 1.25rem 2.5rem;
  }
  .gd-title h1 { margin: 0; }
  .gd-join {
    background: var(--surface); border: 1px solid var(--line); border-top: 3px solid var(--accent);
    box-shadow: var(--shadow); padding: .8rem 1rem .95rem;
  }
  .gd-label, .gd-group h3, .gd-rules h3, .gd-how-grid h3 {
    font-family: var(--f-mono); font-size: .7rem; font-weight: 400; letter-spacing: .13em;
    text-transform: uppercase; color: var(--text-faint); margin: 0 0 .55rem;
  }
  .gd-join .gd-label { color: var(--accent); }
  .gd-join .cmd code { font-size: 1rem; }
  .gd-join p { margin: .55rem 0 0; font-size: .86rem; line-height: 1.45; color: var(--text-dim); }

  .gd { margin: 1.5rem 0 0; }
  .gd-h {
    display: flex; flex-wrap: wrap; align-items: baseline; gap: .15rem .9rem; margin: 0 0 .65rem;
    font-family: var(--f-display); font-weight: 600; font-size: 1.4rem; line-height: 1.2;
  }
  .gd-h span { font-family: var(--f-body); font-weight: 400; font-size: .9rem; color: var(--text-dim); }
  .gd kbd {
    font-family: var(--f-mono); font-size: .82em; padding: 0 .4em; color: var(--text);
    background: var(--surface); border: 1px solid var(--line); border-bottom-width: 2px; border-radius: 3px;
  }
  .gd-b {   /* a place to click in the menu, drawn as a small button */
    display: inline-block; padding: 0 .4em; font-family: var(--f-body); font-weight: 600; font-size: .86rem;
    color: var(--accent); background: var(--surface-2); border: 1px solid var(--line); border-radius: 3px;
  }
  .gd-sep { color: var(--text-faint); font-weight: 400; padding: 0 .1em; }
  .gd code { font-family: var(--f-mono); font-size: 1em; }
  .gd-groups {
    display: grid; grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 1px; background: var(--line-soft); border: 1px solid var(--line-soft);
  }
  .gd-group { background: var(--surface); padding: .95rem 1.05rem 1.05rem; min-width: 0; }
  .gd-group dl { margin: 0; display: grid; gap: .6rem; }
  .gd-group dt {
    font-family: var(--f-mono); font-size: .88rem; font-weight: 600; line-height: 1.3;
    color: var(--accent); overflow-wrap: anywhere;
  }
  .gd-group dt i, .gd-also i { font-style: normal; font-weight: 400; color: var(--text-dim); }
  .gd-group dd { margin: .1rem 0 0; font-size: .83rem; line-height: 1.4; color: var(--text-dim); }
  .gd-also { font-family: var(--f-mono); font-size: .74rem; color: var(--text-faint); white-space: nowrap; }
  .gd-also.gd-line { display: block; margin-top: .1rem; white-space: normal; }
  .gd-also code { color: var(--accent); }

  .gd-rules {
    display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.25fr);
    gap: 1px; background: var(--line-soft); border: 1px solid var(--line-soft); border-top: 0;
  }
  .gd-rules section { background: var(--surface-2); padding: 1rem 1.25rem 1.15rem; }
  .gd-rules p, .gd-rules li { margin: 0; font-size: .86rem; line-height: 1.45; color: var(--text-dim); }
  .gd-rules p + h3 { margin-top: 1.1rem; }
  .gd-rules .gd-rule {
    margin-bottom: .35rem; font-family: var(--f-display); font-size: 1.08rem; line-height: 1.3; color: var(--text);
  }
  .gd-rules ul { margin: 0; padding-left: 1.05rem; }
  .gd-rules li + li { margin-top: .3rem; }
  .gd-rules li::marker { color: var(--accent); }
  .gd-rules code { color: var(--accent); }
  /* How it works: the menu's page of the same name, every chance in picking a map, in five sections (Hud.How) */
  .gd-how { margin: 1.5rem 0 0; }
  .gd-how-grid {
    display: grid; grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 1px; background: var(--line-soft); border: 1px solid var(--line-soft);
  }
  .gd-how-grid section { background: var(--surface-2); padding: .95rem 1.05rem 1.05rem; min-width: 0; }
  .gd-how-grid ul { margin: 0; padding-left: 1.05rem; }
  .gd-how-grid li { margin: 0; font-size: .83rem; line-height: 1.42; color: var(--text-dim); }
  .gd-how-grid li + li { margin-top: .3rem; }
  .gd-how-grid li::marker { color: var(--accent); }
  .gd-maps { margin: 2.75rem 0 .6rem; }

  @media (max-width: 1100px) {   /* 3 + 2 on six tracks: no empty cell for the hairline background to show through */
    .gd-groups, .gd-how-grid { grid-template-columns: repeat(6, minmax(0, 1fr)); }
    .gd-group, .gd-how-grid section { grid-column: span 2; }
    .gd-group:nth-child(n+4), .gd-how-grid section:nth-child(n+4) { grid-column: span 3; }
  }
  @media (max-width: 900px) {
    .gd-hero, .gd-rules { grid-template-columns: minmax(0, 1fr); }
    .gd-join { max-width: 34rem; }
  }
  @media (max-width: 700px) {
    .gd-groups, .gd-how-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .gd-group, .gd-group:nth-child(n+4), .gd-how-grid section, .gd-how-grid section:nth-child(n+4) { grid-column: auto; }
    .gd-group:last-child, .gd-how-grid section:last-child { grid-column: 1 / -1; }
  }
  @media (max-width: 480px) {
    header.mast { padding-top: 1.5rem; }
    .gd-groups, .gd-how-grid { grid-template-columns: minmax(0, 1fr); }
  }
  /* ---------- GUIDE-CSS-END ---------- */"""

PHONE_CSS = """
  /* ---------- PHONE-CSS-START: the whole page on a phone (360-430 px: everything up to the page's 560 px breakpoint) ---------- */
  /* At every width: a long unbreakable string in Workshop text (a URL, a ===== rule, a model path) breaks rather than
     widening the page. Nothing on the desktop page is that long, so desktop looks the same with it. */
  .name, .desc, .broken-flag span, .pack-evid, .packs-intro, .standfirst, footer { overflow-wrap: anywhere; }
  .pack-evid > * { min-width: 0; }
  /* Dark form controls in the dark theme (the toggles' checkboxes were white), in all three places it is stated. */
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { color-scheme: dark; } }
  :root[data-theme="dark"] { color-scheme: dark; }

  @media (max-width: 560px) {
    .wrap { padding: 0 1rem 4rem; }
    .eyebrow { letter-spacing: .1em; }

    /* the guide: a little less padding, so the Join command keeps to one line at 360 px */
    .gd-join { padding: .75rem .8rem .85rem; }
    .gd-join .cmd { gap: .5rem; }
    .gd-join .cmd code { font-size: .9rem; }
    .gd-group { padding: .9rem 1rem 1rem; }
    .gd-rules section, .gd-how-grid section { padding: .9rem 1rem 1rem; }

    /* facts: three across, not a ragged wrap */
    .facts {
      display: grid; grid-template-columns: repeat(3, auto); justify-content: space-between;
      gap: 1rem .75rem; margin-top: 1.6rem;
    }
    .facts dd { font-size: 1.25rem; white-space: nowrap; }

    /* no label smaller than 11.5 px */
    .eyebrow, .facts dt, .howto h3, .gd-label, .gd-group h3, .gd-rules h3, .gd-how-grid h3, .stat dt, .tag, .cmd .copy, .pk-kind,
    .shot.missing { font-size: .72rem; }

    /* tabs: two equal halves. The toolbar: a filter panel that scrolls away with the page rather than a sticky bar
       taking a fifth of the screen - the search on its own line (16 px, or iOS zooms into the field), the size chips
       as one row across, then the toggles and the count; the ::before is the line break between those two rows. */
    .tab { flex: 1 1 0; min-height: 44px; }
    .toolbar { position: static; backdrop-filter: none; gap: .5rem; padding: .9rem 0 1rem; }
    .toolbar::before { content: ""; order: 2; flex-basis: 100%; height: 0; }
    #q { order: 0; flex: 1 0 100%; min-height: 44px; font-size: 16px; }
    #q::placeholder { font-family: var(--f-body); font-size: .8rem; }
    .chip { order: 1; flex: 1 1 auto; min-width: 44px; min-height: 42px; padding: .4rem .3rem; letter-spacing: .02em; }
    .toggle, .shown { order: 3; }
    .toggle { min-height: 42px; }
    .toggle input { width: 18px; height: 18px; }

    /* a map row: the thumbnail beside its number and name, then the description, tags, stats and copy button across
       the full width. .meta steps aside (display: contents) so its name, description and tags are placed on their
       own: layout only, the row markup is the builder's, untouched. */
    .row {
      grid-template-columns: minmax(0, 9fr) minmax(0, 11fr);
      grid-template-rows: auto 1fr repeat(4, auto);
      grid-template-areas: "shot rank" "shot name" "desc desc" "tags tags" "stats stats" "cmd cmd";
      gap: .5rem .85rem; padding: 1.15rem 0 1.25rem;
    }
    .row .meta { display: contents; }
    .row .shot { align-self: start; }
    .rank span { font-size: 1.05rem; text-align: left; padding: 0; }
    .rank span::before { content: "#"; }
    .row .name { grid-area: name; margin: 0; font-size: 1.15rem; }
    .row .desc { grid-area: desc; margin: 0; max-width: none; }
    .row .tags { grid-area: tags; }
    .stats { gap: 1.25rem; }
    .cmd { min-height: 46px; padding: .55rem .8rem; }

    /* a broken row: the banner on top, the rest as above, inset from the red rail */
    .row.broken {
      grid-template-rows: auto auto 1fr repeat(4, auto);
      grid-template-areas: "flag flag" "shot rank" "shot name" "desc desc" "tags tags" "stats stats" "cmd cmd";
      padding: .8rem .75rem 1rem 1.05rem;
    }
    .broken-flag { margin: 0 0 .3rem; }
    .broken-flag em { flex-basis: 100%; white-space: normal; }
    .row.broken .cmd::before { font-size: .68rem; }

    /* a not-map row: the same thumbnail-and-name head, then the description and the evidence */
    .packlist .row.pack {
      grid-template-columns: minmax(0, 9fr) minmax(0, 11fr);
      grid-template-rows: none;
      grid-template-areas: "shot name" "desc desc" "evid evid";
    }
    .pk-id { display: inline-flex; align-items: center; min-height: 40px; }
  }
  /* ---------- PHONE-CSS-END ---------- */"""

# The menu's places and their words follow the HUD's Help page, rules and How it works page (bridge/Hud.cs Help, Rules and
# How): the bridge's --selftest finds every one of those words on this page, and fails on any number in the guide that is not
# one of its constants. More words are fine, other words are not (README "Keeping the rules in sync").
# @@EYEBROW@@ and @@H1@@ are the builder's own two lines, moved into the hero rather than copied.
GUIDE_TOP = """\
    <!-- GUIDE-START: how to play, the first thing on the page. broken-maps.py stamps it, and puts the builder's eyebrow and <h1> back when it strips the page -->
    <div class="gd-hero">
      <div class="gd-title">
        @@EYEBROW@@
        @@H1@@
      </div>
      <div class="gd-join">
        <h2 class="gd-label">Join</h2>
        <button class="cmd gd-copy" type="button" data-copy="connect @@CONNECT@@" title="Copy, then paste into the CS2 console">
          <code>connect @@CONNECT@@</code>
          <span class="copy" aria-hidden="true">Copy</span>
        </button>
        <p>In the CS2 console, no password. Or find <em>@@SERVER_HTML@@</em> in the community server browser.</p>
        <p class="gd-tag">@@TAGLINE@@</p>
      </div>
    </div>
    <section class="gd" aria-labelledby="gd-menu">
      <h2 class="gd-h" id="gd-menu">The menu <span>Everything is a click: hold <kbd>TAB</kbd> for the board, <kbd>TAB</kbd> + <kbd>E</kbd> for the full menu. Every action has a chat command too: <code>!help</code> lists them.</span></h2>
      <div class="gd-groups">
        <section class="gd-group">
          <h3>Maps</h3>
          <dl>
            <div><dt><b class="gd-b">Maps</b><span class="gd-sep">&rsaquo;</span><b class="gd-b">Play</b></dt><dd>Pick any map: every map with its picture, size and votes</dd></div>
            <div><dt><b class="gd-b">Search</b></dt><dd>The keyboard on the Maps page: name, tags or a Workshop ID <span class="gd-also gd-line">or paste a Workshop link into the game chat</span></dd></div>
            <div><dt><b class="gd-b">Next</b></dt><dd>On the board or the menu: a random map when nobody holds this one</dd></div>
            <div><dt><b class="gd-b">Queue</b><span class="gd-sep">&rsaquo;</span><b class="gd-b">Previous</b></dt><dd>Back to the last map</dd></div>
          </dl>
        </section>
        <section class="gd-group">
          <h3>Queue</h3>
          <dl>
            <div><dt><b class="gd-b">Queue</b></dt><dd>Who is next &ndash; least play time goes first</dd></div>
            <div><dt><b class="gd-b">Cancel</b></dt><dd>Drop one of your requests, or all of them</dd></div>
            <div><dt><b class="gd-b">I&rsquo;m done</b></dt><dd>End your own map: the queue moves on</dd></div>
            <div><dt><b class="gd-b">Vote to skip</b></dt><dd>Someone else&rsquo;s map: a majority of the others skips it</dd></div>
          </dl>
        </section>
        <section class="gd-group">
          <h3>Voting</h3>
          <dl>
            <div><dt><b class="gd-b">Like</b> <b class="gd-b">Dislike</b></dt><dd>On the map that is on &ndash; the same again undoes it</dd></div>
            <div><dt><b class="gd-b">Too big</b></dt><dd>A dislike: only 1 random pick in 100 is a too-big map <span class="gd-also gd-line">beside Like and Dislike, and Big on every map card; it marks the map too big</span></dd></div>
            <div><dt><b class="gd-b">Ads</b></dt><dd>A dislike for ads or a server redirect: never a random pick <span class="gd-also gd-line">beside Too big, and on every map card; anybody can still pick the map</span></dd></div>
            <div><dt><b class="gd-b">Maps</b><span class="gd-sep">&rsaquo;</span>arrows</dt><dd>Vote on any map&rsquo;s card: up likes it, down dislikes it</dd></div>
            <div><dt><b class="gd-b">Sort</b><span class="gd-sep">&rsaquo;</span><b class="gd-b">Votes</b></dt><dd>The most liked maps first</dd></div>
          </dl>
        </section>
        <section class="gd-group">
          <h3>Game</h3>
          <dl>
            <div><dt><b class="gd-b">Grenade drops</b></dt><dd>On or off, for everyone <span class="gd-also gd-line">back on after 10 minutes with nobody on</span></dd></div>
            <div><dt><b class="gd-b">Match length</b></dt><dd>10 or 60 minutes, from the next match</dd></div>
            <div><dt><b class="gd-b">New maps</b></dt><dd>Always the new maps, Half a coin flip, Never the original list <span class="gd-also gd-line">where random picks come from, for everyone: the new maps are #1017 and up, the original list #1&ndash;#1016</span></dd></div>
            <div><dt><b class="gd-b">Restart</b></dt><dd>Reboot a stuck server &ndash; click twice to confirm <span class="gd-also gd-line">the whole server, not the match</span></dd></div>
            <div><dt><b class="gd-b">Fun</b></dt><dd>Friendly fire, low gravity and more, until the map changes <span class="gd-also gd-line">sixteen settings anybody switches for everyone, and the news line says who; friendly fire, Instakill and Grenades x10 start every map on</span></dd></div>
          </dl>
        </section>
        <section class="gd-group">
          <h3>The board</h3>
          <dl>
            <div><dt>Hold <kbd>TAB</kbd></dt><dd>The live board beside the scoreboard, with the mouse <span class="gd-also gd-line">Next, your vote, the queue</span></dd></div>
            <div><dt><kbd>TAB</kbd> + <kbd>E</kbd></dt><dd>The full menu <span class="gd-also gd-line">or the board&rsquo;s Open full menu</span></dd></div>
            <div><dt><code>@@HOST@@</code></dt><dd>This page: the rules and the maps</dd></div>
            <div><dt>Chat &middot; <code>!help</code></dt><dd>Every action has a chat command too - !help lists them</dd></div>
          </dl>
        </section>
      </div>
      <div class="gd-rules">
        <section>
          <h3>Random maps</h3>
          <p class="gd-rule">When a match ends everybody votes for the next map: 5 maps and Random, for 60 s or until a majority agrees; your queued map is on it with your vote in.</p>
          <p>Random maps - at start, and when nobody votes - are biased by Workshop popularity and by the votes.</p>
          <h3>How voting counts</h3>
          <p class="gd-rule">Each upvote doubles a map's odds on the dice (max 5), each downvote quarters them, Workshop rank gives up to 3x.</p>
          <p>One vote per player per map, and it is kept.</p>
        </section>
        <section>
          <h3>Fair queue</h3>
          <ul>
            <li>Whoever has had the least play time on their own requests goes next, so one player's five requests wait behind everyone else's first.</li>
            <li>A map two or more players asked for goes first.</li>
            <li>A requested map stays until its match ends, its requester clicks <b class="gd-b">I&rsquo;m done</b>, a skip vote passes, or the requester has been gone over a minute while others wait.</li>
            <li>Up to 5 requests each, and only while you're on the server.</li>
          </ul>
        </section>
      </div>
    </section>
    <section class="gd gd-how" aria-labelledby="gd-how">
      <h2 class="gd-h" id="gd-how">How it works <span>Every chance in picking a map, word for word as the menu&rsquo;s How it works page has it.</span></h2>
      <div class="gd-how-grid">
        <section>
          <h3>Picking a map</h3>
          <ul>
            <li>Play on a card is a request. On a map nobody holds it plays at once; on somebody's map it waits in the queue.</li>
            <li>The queue: a map two or more players asked for goes first, then whoever has had the least play time on their own requests.</li>
            <li>A requested map stays until its match ends, its requester clicks I'm done, a skip vote passes, or the requester has been gone over a minute while others wait.</li>
            <li>Up to 5 requests each, only while you're on the server. Previous asks for the map before this one.</li>
          </ul>
        </section>
        <section>
          <h3>Next and random maps</h3>
          <ul>
            <li>Next rolls a random map when nobody holds this one; on your own map it is I'm done, on somebody else's a vote to skip.</li>
            <li>When a match ends everybody votes for 60 s, or until a majority agrees: 5 maps and Random. A queued map is on the ballot with its requesters&#39; votes in.</li>
            <li>Each other mode gets a map on it: Deathmatch, and Wingman up to 4 players. Ties are drawn; no votes is Random. Arms Race after 10 minutes with nobody on.</li>
            <li>The server rolls one at start. The vote's map stays until its match ends or a skip vote passes. A map a player picked or queued is never random.</li>
          </ul>
        </section>
        <section>
          <h3>Which maps a random pick takes</h3>
          <ul>
            <li>The New maps setting (Game page, anybody): Always - the new maps, #1017 and up. Half - a coin flip per pick. Never - the original #1-#1016.</li>
            <li>A list with nothing left to roll hands the pick to the other list, and the news line says so. 10 minutes with nobody on: back to Always.</li>
            <li>1 random pick in 100 takes a too-big map (measured XL, or marked Too big by a player), the other 99 one that is not; an empty group hands the pick to the other.</li>
          </ul>
        </section>
        <section>
          <h3>Weights and votes</h3>
          <ul>
            <li>Inside that group, Workshop rank gives up to 3x, each Like doubles a map's odds (up to 5 Likes), each Dislike or Too big quarters them.</li>
            <li>One vote per player per map, kept: Like, Dislike, Too big or Ads. The same button again takes it back.</li>
            <li>Nothing else counts: a map's download size never makes it more or less likely.</li>
          </ul>
        </section>
        <section>
          <h3>Never rolled</h3>
          <ul>
            <li>The map that is on, and the last 10 maps played (while others are left).</li>
            <li>Maps that crashed the server or are on the broken list, blocked maps, and maps gone from the Workshop.</li>
            <li>Maps a player marked Ads (they show ads or send players to another server) - anybody can still pick them.</li>
          </ul>
        </section>
      </div>
    </section>
    <h2 class="gd-h gd-maps">The maps</h2>
    <script>
    (function () {  /* the Join copy button: the map rows' copy behaviour, for the one button outside #list */
      var btn = document.querySelector('.gd-copy'), label = btn.querySelector('.copy'), text = btn.dataset.copy;
      function done() {
        label.textContent = 'Copied';
        btn.classList.add('done');
        setTimeout(function () { label.textContent = 'Copy'; btn.classList.remove('done'); }, 1400);
      }
      function fallback() {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); done(); } catch (e) { label.textContent = 'Press Ctrl+C'; }
        document.body.removeChild(ta);
      }
      btn.addEventListener('click', function () {
        if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(done, fallback); }
        else { fallback(); }
      });
    })();
    </script>
    <!-- GUIDE-END -->"""


def guide_top(nl):
    block = (GUIDE_TOP.replace("@@EYEBROW@@", GD_EYEBROW.strip()).replace("@@H1@@", GD_H1.strip())
             .replace("@@CONNECT@@", CONNECT).replace("@@HOST@@", SITE_HOST).replace("@@SERVER_HTML@@", SERVER_HTML).replace("@@TAGLINE@@", TAGLINE))
    assert "@@" not in block
    return nl.join(block.split("\n"))


def guide_edits(nl):
    """The guide's whole-text edits, as (label, original, injected); each original must appear exactly once."""
    return [
        ("guide-css", "</style>", nl.join(GUIDE_CSS.split("\n")) + nl + "</style>"),
        ("phone-css", "</style>", nl.join(PHONE_CSS.split("\n")) + nl + "</style>"),
        ("guide-top", GD_EYEBROW + nl + GD_H1, guide_top(nl)),
        ("guide-standfirst", nl.join(GD_STANDFIRST_OLD), nl.join(GD_STANDFIRST_NEW)),
        ("guide-cards", GD_HOWTO + nl + nl.join(GD_CARDS_OLD), GD_HOWTO),
    ]


def add_guide(html, nl):
    """Stamp the guide, and the phone layout with it. The last stamp, so every anchor it takes is still the builder's
    own line, and the phone CSS comes last in the sheet, after the blocks whose rows it lays out."""
    assert GD_TOP not in html and GD_CSS not in html and PH_CSS not in html, "the guide is already on this page"
    out = html
    for label, original, injected in guide_edits(nl):
        count = out.count(original)
        assert count == 1, f"{label}: expected exactly one match for the anchor, found {count}"
        out = out.replace(original, injected, 1)
    assert strip_guide(out, nl) == html, "the guide does not strip back to the page it came from"
    return out


def strip_guide(html, nl):
    """Undo the guide: its three blocks cut marker to marker (eyebrow and <h1> put back), the builder's lines swapped in."""
    if GD_TOP not in html:
        return html
    start = html.index(GD_TOP)
    html = html[:start] + GD_EYEBROW + nl + GD_H1 + html[html.index(GD_TOP_END, start) + len(GD_TOP_END):]
    css = html.index(f"{nl}{nl}  /* ---------- {GD_CSS}")
    html = html[:css] + html[html.index(GD_CSS_END, css) + len(GD_CSS_END):]
    css = html.find(f"{nl}{nl}  /* ---------- {PH_CSS}")
    if css != -1:  # a page stamped before the phone layout existed has the guide without it
        html = html[:css] + html[html.index(PH_CSS_END, css) + len(PH_CSS_END):]
    standfirst = nl.join(GD_STANDFIRST_NEW)
    assert html.count(standfirst) == 1, "strip: the guide's standfirst is not on the page exactly once"
    html = html.replace(standfirst, nl.join(GD_STANDFIRST_OLD), 1)
    assert nl.join(GD_CARDS_OLD) not in html and html.count(GD_HOWTO) == 1, "strip: the how-to cards are not as stamped"
    return html.replace(GD_HOWTO, GD_HOWTO + nl + nl.join(GD_CARDS_OLD), 1)


def strip_page(html, nl):
    """Undo every stamp this script makes, the guide first, then broken maps, then not-maps, and prove nothing is left."""
    html = strip_packs(strip_broken(strip_guide(strip_ours(html, nl), nl), nl), nl)
    for residue in MARKERS + (CSS_END, PK_CSS_END, PK_TABS_END, PK_PANEL_END, PK_JS_END, 'data-bm="1"', 'id="hidebroken"',
                              "<dt>Known broken</dt>", "<dt>Gone from the Workshop</dt>", "<dt>Skin packs</dt>", "<dt>Not maps</dt>",
                              '<li class="row broken"', '<li class="row broken removed"',
                              '<li class="row pack', "// bm", "Maps that crash it", "show-packs", TAB_HASH,
                              GD_CSS_END, GD_TOP_END, PH_CSS_END, 'class="gd', "gd-copy", GD_STANDFIRST_NEW[1],
                              OU_TOP_END, OU_CSS_END, OU_JS_END, "ours-card", "ours-cmd"):
        assert residue not in html, f"strip left residue: {residue}"
    return html


def stamped(html):
    return any(m in html for m in MARKERS)


# --------------------------------------------------------------------------- the page: our maps (ours.json)

RE_DESC = re.compile(r'^\s*<p class="desc" dir="auto">(.*)</p>\s*$')


def ours_cards(L, rows, ours):
    """Our maps that are rows on the page, each as its row has it: rank, name, the picture's link and image, the description.
    One that is not a row (not on the list yet) is left out - the section shows only what the list below can load."""
    by_id = {r.wid: r for r in rows}
    cards = []
    for wid, _ in ours:
        r = by_id.get(wid)
        if r is None:
            continue
        block = L[r.start:r.end + 1]
        shot = block[2:block.index("  </a>", 2) + 1]
        desc = next((RE_DESC.match(l).group(1) for l in block if RE_DESC.match(l)), "")
        cards.append({"id": wid, "rank": r.rank, "name": r.name, "shot": shot, "desc": desc})
    return cards


def ours_css(nl):
    return nl.join([
        "",
        f"  /* ---------- {OU_CSS}: the server's own maps, above the lists (ours.json) ---------- */",
        "  .ours { margin: 1.4rem 0 .4rem; }",
        "  body.show-packs .ours { display: none; }",
        "  .ours-h { font-family: var(--f-display); font-weight: 600; font-size: 1.15rem; margin: 0 0 .25rem; }",
        "  .ours-h span { margin-left: .5rem; font-family: var(--f-mono); font-size: .72rem; color: var(--text-faint); }",
        "  .ours-intro { margin: 0 0 .8rem; font-size: .86rem; color: var(--text-dim); max-width: 70ch; }",
        "  .ours-list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--gap);",
        "    grid-template-columns: repeat(auto-fill, minmax(min(100%, 15.5rem), 1fr)); }",
        "  .ours-card { display: flex; flex-direction: column; gap: .45rem; min-width: 0; padding: .7rem;",
        "    background: var(--surface); border: 1px solid var(--line); border-top: 3px solid var(--accent); border-radius: var(--radius); }",
        "  .ours-card .name { font-size: 1.1rem; margin: 0; }",
        "  .ours-card .rank { font-family: var(--f-mono); font-size: .7rem; color: var(--text-faint); margin-left: .4rem; }",
        "  .ours-card .desc { margin: 0; font-size: .82rem; }",
        "  .ours-card .cmd { margin-top: auto; }",
        OU_CSS_END,
    ])


def ours_block(cards, nl):
    e = htmllib.escape
    out = [f"{OU_TOP}: the server's own maps (bridge/ours.json), as their rows below have them; broken-maps.py stamps it -->",
           '  <section class="ours" id="ours" aria-labelledby="ours-h">',
           f'    <h2 class="ours-h" id="ours-h">Our maps<span>{len(cards)}</span></h2>',
           '    <p class="ours-intro">Made for this server. They are in the list below too, and on the HUD&rsquo;s Our maps filter: '
           'paste an ID into the game chat and the menu opens on that map.</p>',
           '    <ol class="ours-list">']
    for c in cards:
        out += ['      <li class="ours-card">',
                *["    " + l for l in c["shot"]],
                f'        <h3 class="name" dir="auto">{e(c["name"])}<span class="rank">#{c["rank"]:,}</span></h3>',
                *([f'        <p class="desc" dir="auto">{c["desc"]}</p>'] if c["desc"] else []),
                f'        <button class="cmd ours-cmd" type="button" data-chat="{c["id"]}" title="Copy, then paste into the game chat: the menu '
                'opens on this map">',
                f'          <code class="chat">{c["id"]}</code>',
                '          <span class="copy" aria-hidden="true">Copy</span>',
                '        </button>',
                '      </li>']
    out += ['    </ol>', '  </section>', OU_TOP_END]
    return nl.join(out)


def ours_script(nl):
    """The section's copy button, on its own: the map list's handler listens on #list only."""
    return nl.join([
        OU_JS,
        "(function () {",
        "  var box = document.getElementById('ours');",
        "  if (!box) return;",
        "  box.addEventListener('click', function (ev) {",
        "    var btn = ev.target.closest('.cmd');",
        "    if (!btn) return;",
        "    var text = btn.dataset.chat, label = btn.querySelector('.copy');",
        "    var done = function () {",
        "      label.textContent = 'Copied';",
        "      btn.classList.add('done');",
        "      setTimeout(function () { label.textContent = 'Copy'; btn.classList.remove('done'); }, 1400);",
        "    };",
        "    var fallback = function () {",
        "      var t = document.createElement('textarea');",
        "      t.value = text; t.setAttribute('readonly', ''); t.style.position = 'fixed'; t.style.opacity = '0';",
        "      document.body.appendChild(t); t.select();",
        "      try { if (document.execCommand('copy')) done(); } catch (e) { /* nothing copied: the ID is on the button */ }",
        "      document.body.removeChild(t);",
        "    };",
        "    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(done, fallback);",
        "    else fallback();",
        "  });",
        "})();",
        OU_JS_END,
    ])


def add_ours(html, nl, cards):
    """Stamp the Our maps section: after the header, above the tabs and the toolbar, its CSS before the phone layout (which stays
    last in the sheet) and its script before </body>. The last stamp of all, so strip_page() takes it off first."""
    if not cards:
        return html
    assert OU_TOP not in html and OU_CSS not in html and OU_JS not in html, "the Our maps section is already on this page"
    out = html
    phone = f"{nl}{nl}  /* ---------- {PH_CSS}"
    css_at = out.find(phone)
    out = (out[:css_at] + ours_css(nl) + out[css_at:]) if css_at != -1 else out.replace("</style>", ours_css(nl) + nl + "</style>", 1)
    for label, anchor, injected in (("ours-section", "  </header>" + nl, "  </header>" + nl + nl + ours_block(cards, nl) + nl),
                                    ("ours-script", "</body>", ours_script(nl) + nl + "</body>")):
        count = out.count(anchor)
        assert count == 1, f"{label}: expected exactly one match for the anchor, found {count}"
        out = out.replace(anchor, injected, 1)
    assert strip_ours(out, nl) == html, "the Our maps section does not strip back to the page it came from"
    return out


def strip_ours(html, nl):
    """Undo the Our maps section: its three blocks cut marker to marker."""
    if OU_TOP not in html:
        return html
    start = html.index(nl + nl + OU_TOP)
    html = html[:start] + html[html.index(OU_TOP_END, start) + len(OU_TOP_END):]
    css = html.index(f"{nl}  /* ---------- {OU_CSS}")
    html = html[:css] + html[html.index(OU_CSS_END, css) + len(OU_CSS_END):]
    js = html.index(OU_JS)
    return html[:js] + html[html.index(OU_JS_END, js) + len(OU_JS_END) + len(nl):]


# --------------------------------------------------------------------------- the page: both

def stamp(src, crashers, packs, sweep, nl, table=None, img_exists=lambda wid: False, full=True, removed=None, ours=None):
    """Page in (stamped or not), stamped page out, plus a summary. Idempotent: it strips first, then re-applies."""
    clean = strip_page(src, nl) if stamped(src) else src
    L = clean.split(nl)
    open_at, close_at, rows = parse_rows(L)
    if full:
        assert [r.rank for r in rows] == list(range(1, len(rows) + 1)), "page ranks are not 1..N with no gaps"
    if table is not None:
        check_against_bridge(rows, table, full)
    assert page_facts(clean) == facts_text(row_facts([L[r.start:r.end + 1] for r in rows])), \
        "the page's header facts do not describe its rows: rebuild it with build-1000.py rather than stamp over it"
    page = {r.wid for r in rows}
    removed = removed or {}
    assert not set(crashers) & set(packs), "an id on both lists"  # read_broken() refuses this too; stamp() is public
    assert not set(removed) & (set(crashers) | set(packs)), "an id on the removed list and on another"

    out, moved, kept = L[:open_at + 1], [], []
    for r in rows:
        block = L[r.start:r.end + 1]
        if r.wid in packs:
            moved.append(pack_row(block, packs[r.wid], r.rank))
            continue
        kept.append(block)
        if r.wid in removed:
            assert removed[r.wid].get("rank") in (None, r.rank), \
                f"broken.json says removed {r.wid} is rank {removed[r.wid].get('rank')}, the page says {r.rank}"
            out += [block[0].replace('<li class="row" ', '<li class="row broken removed" ', 1), removed_flag_html(removed[r.wid])] + block[1:]
        elif r.wid in crashers:
            assert crashers[r.wid].get("rank") in (None, r.rank), \
                f"broken.json says {r.wid} is rank {crashers[r.wid].get('rank')}, the page says {r.rank}"
            out += [block[0].replace('<li class="row" ', '<li class="row broken" ', 1), flag_html(crashers[r.wid])] + block[1:]
        else:
            out += block
    out += L[close_at:]
    html = nl.join(out)

    off_page = [w for w in packs if w not in page]
    generated = [generated_row(packs[w], img_exists(w)) for w in off_page]  # broken.json's own order
    summary = {"rows": len(rows), "broken_stamped": sum(1 for w in crashers if w in page), "removed_stamped": sum(1 for w in removed if w in page),
               "broken_off_page": sorted(w for w in crashers if w not in page),
               "packs_moved": len(moved), "packs_generated": len(generated), "maps_left": len(kept)}
    for w in crashers:
        if w not in page:
            assert crashers[w].get("rank") is None, f"broken.json gives {w} rank {crashers[w]['rank']}, but no row has it"

    if not summary["broken_stamped"] and not summary["removed_stamped"] and not packs:
        # Nothing broken on the page and no packs: no banner, no toggle, no tab, no palette, no script. The page
        # must come out exactly as it went in but for the guide, and that is an assertion rather than an intention.
        assert html == clean, "an empty stamp still changed the page"
        html = add_ours(add_guide(html, nl), nl, ours_cards(L, rows, ours or []))
        assert strip_page(html, nl) == clean, "the guide does not strip back to the page it came from"
        return html, summary

    edits = []
    if summary["broken_stamped"] or summary["removed_stamped"]:
        edits += broken_edits(L, nl, summary["broken_stamped"], sweep, summary["removed_stamped"])
    if packs:
        edits += pack_edits(nl, len(rows), summary["maps_left"], len(packs), moved, generated)
    for label, original, injected in edits:
        count = html.count(original)
        assert count == 1, f"{label}: expected exactly one match for the anchor, found {count}"
        html = html.replace(original, injected, 1)
    facts = row_facts(kept)
    html = set_facts(html, facts)
    summary.update(subs=facts["subs"], under_25mb=facts["tiny"])

    in_list = html[html.index(LIST_OPEN):html.index(LIST_CLOSE, html.index(LIST_OPEN))]
    # a row is its own map's thumbnail link: a description may quote another item's Workshop URL (a CS2 port linking its CS:GO original)
    row_ids = {m.group(1) for m in (RE_SHOT.match(line) for line in in_list.split(nl)) if m}
    leaked = [w for w in packs if str(w) in row_ids]
    assert not leaked, f"non-maps still in the map list: {leaked}"
    assert row_facts(list_blocks(html.split(nl))) == facts, "the header facts do not describe the list they head"
    assert html.count('<li class="row broken" ') == summary["broken_stamped"]
    assert html.count('<li class="row pack" ') == len(moved) and html.count('<li class="row pack gen" ') == len(generated)
    assert html.count('<li class="row broken removed" ') == summary["removed_stamped"]
    assert html.count('<li class="row" ') == summary["maps_left"] - summary["broken_stamped"] - summary["removed_stamped"]
    assert html.count('<button class="cmd" type="button" data-chat="') == summary["maps_left"], "a map row lost its copy button, or a pack kept one"
    assert html.count('data-console="host_workshop_map ') == summary["maps_left"]
    assert html.count('src="img/') == clean.count('src="img/') + sum(1 for w in off_page if img_exists(w)), \
        "a thumbnail went missing"
    cards = ours_cards(L, rows, ours or [])
    summary.update(ours=len(cards), ours_off_page=[w for w, _ in ours or [] if w not in page])
    html = add_ours(add_guide(html, nl), nl, cards)
    assert strip_page(html, nl) == clean, "the stamp does not strip back to the page it came from"
    return html, summary


# --------------------------------------------------------------------------- the mock

def build_mock(clean, nl, out_dir, mock_ids, packs, sweep, table, img_exists, img_dir, rows=6):
    """A standalone cut-down page: the real head, CSS, masthead and toolbar, the first `rows` rows with `mock_ids`
    stamped broken, plus every pack row of the real page, real thumbnails copied in. Faithful because it is the
    real stamper on real markup; the cut gets its own header facts first, exactly as the full page has its own."""
    L = clean.split(nl)
    open_at, close_at, all_rows = parse_rows(L)
    keep = all_rows[:rows] + [r for r in all_rows[rows:] if r.wid in packs]
    blocks = [L[r.start:r.end + 1] for r in keep]
    cut = set_facts(nl.join(L[:open_at + 1] + [l for b in blocks for l in b] + L[close_at:]), row_facts(blocks))
    wanted = {r.wid: {"id": int(r.wid), "rank": r.rank, "name": r.name, "crashes": 3,
                      "evidence": "mock - an invented crasher, to show the banner"}
              for r in all_rows[:rows] if r.wid in mock_ids}
    assert wanted, f"none of {sorted(mock_ids)} is in the first {rows} rows"

    html, summary = stamp(cut, wanted, packs, sweep, nl, table, img_exists, full=False)
    for old, new in [
        ("<title>Gun Game Rotation</title>", "<title>Gun Game Rotation &mdash; mock: broken rows and the Not maps tab</title>"),
        (GD_EYEBROW.strip(),  # no indent: the guide moves the eyebrow into its hero
         '<p class="eyebrow">Mock &middot; nothing deployed &middot; the two &ldquo;broken&rdquo; maps are invented, '
         f'the {len(packs)} not-maps are real</p>'),
    ]:
        assert html.count(old) == 1, f"mock: {old[:40]!r} matched {html.count(old)} times"
        html = html.replace(old, new, 1)
    (out_dir / "img").mkdir(parents=True, exist_ok=True)
    copied = 0
    for wid in [r.wid for r in keep] + [w for w in packs if img_exists(w)]:
        src = Path(img_dir) / f"{wid}.webp"
        if src.exists():
            shutil.copyfile(src, out_dir / "img" / f"{wid}.webp")
            copied += 1
    return html, summary, copied


# --------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--broken", default=str(BROKEN_SRC), help="broken.json: the crashers and the non-maps (default: the repo's)")
    ap.add_argument("--maps", default=str(BRIDGE_MAPS), help="the bridge's maps.json to check ranks against ('' skips the check)")
    ap.add_argument("--ours", default=str(OURS_SRC), help="ours.json: the server's own maps, for the Our maps section ('' = none)")
    ap.add_argument("--src", default=str(SITE_SRC), help="page to stamp (default: the repo master beside this script)")
    ap.add_argument("--img", default=SITE_IMG, help="the site's thumbnails, to know which exist ('' = none do)")
    ap.add_argument("--out", default=str(HERE), help="output directory (default: beside this script)")
    ap.add_argument("--mock", default="", help="also write a standalone mock with these ids (from the first 6 rows) stamped broken")
    ap.add_argument("--clear", action="store_true", help="write the un-stamped page instead of a stamped one")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    sys.path.insert(0, str(HERE))
    import pagesplit  # the site is index.html + more.html (pagesplit.py): joined here, stamped whole, written split again
    src, cut = pagesplit.read_page(args.src)  # keeps the page's line endings intact
    if cut:
        print(f"note: {args.src} is split at #{cut} (pagesplit.py); stamping the joined page, writing it split again")
    nl = "\r\n" if "\r\n" in src else "\n"
    if stamped(src):
        print(f"note: {args.src} already carries a stamp; re-stamping from the stripped page")
    clean = strip_page(src, nl) if stamped(src) else src

    crashers, packs, sweep = read_broken(args.broken)
    removed = read_removed(args.broken, crashers, packs)
    table = read_bridge_maps(args.maps)
    on_list = set(table.values()) if table else set()
    listed = [w for w in packs if w in on_list]
    if listed:
        print(f"note: {len(listed)} non-map(s) are still on maps.json; they move from the map list to the Not maps tab")
    img_exists = (lambda wid: (Path(args.img) / f"{wid}.webp").exists()) if args.img else (lambda wid: False)
    print(f"broken.json: {len(crashers)} crasher(s), {len(removed)} removed from the Workshop, {len(packs)} non-map(s); sweep {sweep or 'not stated'}")

    # Everything is computed and asserted before anything is written.
    if args.clear:
        html, summary = clean, None
    else:
        html, summary = stamp(clean, crashers, packs, sweep, nl, table, img_exists, removed=removed, ours=read_ours(args.ours))
    mock = (build_mock(clean, nl, out_dir, {i.strip() for i in args.mock.split(",")}, packs, sweep, table, img_exists, args.img)
            if args.mock else None)

    written = pagesplit.write_page(html, out_dir, cut)
    print(f"wrote {' + '.join(str(p) for p in written)}: {len(src):,} -> {len(html):,} characters")
    if args.clear:
        print("        cleared: the page with every stamp removed")
    if summary:
        print("        " + ", ".join(f"{k}={v}" for k, v in summary.items()))
        if summary["broken_off_page"]:
            print(f"        {len(summary['broken_off_page'])} crasher(s) are not rows on the page: on the server's lists only "
                  f"({', '.join(summary['broken_off_page'])})")
        if html == src:
            print("        unchanged: the page already carried exactly this stamp")

    if mock:
        mock_html, mock_summary, copied = mock
        mock_out = out_dir / "mock-broken-row.html"
        with open(mock_out, "w", encoding="utf-8", newline="") as fh:
            fh.write(mock_html)
        print(f"wrote {mock_out}: {mock_summary['rows']} rows -> {mock_summary['maps_left']} maps "
              f"({mock_summary['broken_stamped']} broken) + {mock_summary['packs_moved'] + mock_summary['packs_generated']} "
              f"not-maps; {copied} thumbnails copied")
        for theme in ("light", "dark"):  # the theme pinned, so both can be seen without changing an OS setting
            forced = mock_html.replace('<html lang="en">', f'<html lang="en" data-theme="{theme}">', 1)
            assert forced != mock_html, "mock: no <html lang=\"en\"> to force a theme on"
            with open(out_dir / f"mock-{theme}-theme.html", "w", encoding="utf-8", newline="") as fh:
                fh.write(forced)
        print(f"wrote {out_dir / 'mock-light-theme.html'} and {out_dir / 'mock-dark-theme.html'} (theme pinned)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
