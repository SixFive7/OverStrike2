#!/usr/bin/env python
"""publish.py - docshots.py's reviewed pictures into the documentation, as AGENTS.md asks ("The documentation shows the whole HUD"): a page per part of
the HUD under docs/hud/, their index in README.md between its hud-index markers, the gallery of every on-screen message (docs/hud/messages.html), and every
picture reviewed into tools/scan/allow.txt with its hash. Writes the working tree only: no git command, no commit.

    python -I publish.py --out <docshots run folder> [--repo <checkout>] [--res 1920x1080] [--all]      (--repo: this one by default)
    python -I publish.py --relink [--repo <checkout>]       a gallery that still carries its pictures inside it: each one a file of its own, linked

The history keeps every picture forever, so a retake commits only the pictures that really changed. Each picture is compared with the committed one at
its path (docshots.same_picture): on what it is about - its window, the big vote window, or its crop - and all but what differs on every shot - the
clocks, timers, counters and random tiles docshots.volatile_boxes names, JPEG's noise, the game world dimmed behind a translucent panel, and the
game a crop takes in around its panel: the top line's pill, the banners, the board and the reasons dialog are compared on the panel alone
(docshots.panel_of), and a panel found at another width is a change. Over the game a gallery's where picture, which shows its look's crop
smaller, goes as its look goes. A picture that shows
the same keeps its committed bytes; only one that differs is written anew. --all writes every picture anew (a change to the HUD's whole look, say). Pictures no page and no message uses
any more are removed.

It refuses unless <out>/<res>/reviewed.txt names every picture the run wrote, one file name a line: the review is by eye, for names, ids and addresses, and
this file says it was done. The pages' pictures go in as JPEG at full size (quality 85, no chroma subsampling: the HUD's words stay sharp), a page takes a
row's first and last frame. The gallery's are files of their own under docs/hud/img/gallery/, which it links by relative path: for each message, how it
looks (the crop, full size) and where it is on the screen (the whole frame, 800 px wide, its place marked) - one frame a row.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import io
import json
import re
import sys
import urllib.parse
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import docshots as d  # noqa: E402

PAGES = [  # README's parts, in its order: (slug, the part as README names it, what the page says first, its rows)
    ("board", "The board", "Hold TAB: the board beside CS2's scoreboard - the map that is on, Like and Dislike, Start a vote, the way into the menu, Lately.",
     ["board-default", "board-liked", "board-why", "board-big", "board-ads", "board-wait"]),
    ("start", "START", "The window's first page, and the welcome at a join.",
     ["page-start", "welcome-first", "welcome-back", "join-tagline", "check-click-to-play", "top-over-window", "window-restart-strip", "updated-joiner", "mode-armsrace-back"]),
    ("maps-browse", "MAPS, Browse", "The map browser: cards, search and keyboard, sorting, filters, a Workshop map's card, a risky map's card.",
     ["page-maps", "maps-sort-votes", "maps-sort-name", "maps-sort-size", "maps-sort-size-desc", "maps-mode-armsrace", "maps-mode-deathmatch", "maps-mode-wingman", "maps-size-tiny",
      "maps-size-small", "maps-size-mid", "maps-size-large", "maps-liked", "maps-official", "maps-ours", "maps-page-2", "maps-page-last", "maps-keyboard", "maps-search",
      "maps-search-keyboard", "maps-search-empty", "maps-workshop-refused", "maps-workshop-ready", "maps-liked-card", "why-card", "maps-risky-card", "play-asks-first"]),
    ("maps-vote", "MAPS, Vote", "The vote for the next map: everybody's window opens on the ballot, frozen, until it is decided.",
     ["vote-open", "vote-limit", "vote-my-vote", "vote-inactivity", "vote-kept", "vote-cannot-call", "vote-browse", "vote-picked-card", "vote-decided", "now-playing", "vote-started-line",
      "vote-random-lit", "vote-waiting", "vote-pager", "vote-limit-low", "vote-tie-lead", "chat-vote-clicked"]),
    ("fun", "FUN", "The fun settings: every one at its default, set by somebody, greyed when impossible, idle in a mode, and the rows of the other modes.",
     ["page-fun", "fun-set-by", "fun-refused", "fun-refused-tip", "fun-already", "fun-idle", "fun-many", "mode-deathmatch-fun", "mode-wingman-fun"]),
    ("server-players", "SERVER, Players", "Who is on, and which HUD their game shows.", ["page-players", "players-older", "players-check", "vote-players"]),
    ("server-game", "SERVER, Game", "The match length, and the two restarts: the match or the whole server.",
     ["page-game", "game-time-10", "game-restart-armed", "rematch-countdown", "mode-deathmatch-game", "mode-wingman-game"]),
    ("server-sync", "SERVER, Sync", "The Workshop sync's dashboard: now, the queue, the history.", ["page-sync", "sync-queue-next", "sync-history-next", "sync-maps-next", "sync-maps-last"]),
    ("help", "HELP, Help", "Where everything is.", ["page-help"]),
    ("how", "HELP, How it works", "Every rule of the vote and every chance of a random pick.", ["page-how"]),
]
GALLERY = "messages.html"
DOCS = Path("docs") / "hud"
IMG = DOCS / "img"
GALLERY_IMG = IMG / "gallery"           # the gallery's own pictures: <frame>-look.jpg (the crop), <frame>-where.jpg (the frame, its place marked)
WHERE_WIDTH = 800
ALLOW_HEAD = "# The documentation's screenshots (docshots.py, a real client against a test copy): each looked at for names, Steam ids and addresses; test names only"
DROPPED: set = set()   # <res>/dropped.txt: frames the review threw out (a loading screen, a menu over the subject); they go nowhere


def picture(png, width=None, box=None):
    """A frame or a crop as it is published: RGB, the gallery's place marked in gold, at most width pixels wide."""
    from PIL import Image, ImageDraw
    with Image.open(png) as opened:
        im = opened.convert("RGB")
    if box:
        ImageDraw.Draw(im).rectangle(box, outline=(233, 180, 76), width=max(3, round(im.size[1] / 270)))
    if width and im.size[0] > width:
        im = im.resize((width, round(im.size[1] * width / im.size[0])), Image.LANCZOS)
    return im


def encode(im, quality=85):
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=quality, subsampling=0, optimize=True)
    return buf.getvalue()


def scaled(box, f):
    return tuple(round(v * f) for v in box)


def frames_of(man, rid):
    return [f for f in man["rows"].get(rid, {}).get("frames", []) if f["file"] not in DROPPED]


class Pictures:
    """The pictures under docs/hud/img/ as this publish leaves them: each one compared with the committed one at its path and kept when it shows the
    same (its bytes untouched), written when it differs or is new; what is not used any more goes at the end."""

    def __init__(self, repo, all_anew):
        self.repo, self.all_anew = repo, all_anew
        self.used, self.kept, self.changed, self.new = set(), [], [], []
        self.last = ""      # what the last put did: kept, changed or new

    def put(self, rel, im, quality, region, volatile=(), panel=None, follow=None):
        """rel: the path under the repository; im: the picture to publish; region, volatile and panel (docshots.panel_of): what a comparison looks
        at and leaves out, in im's pixels. follow: the verdict of the picture this one shows smaller (a gallery's where, its look's: True kept, False
        written), which decides instead of a comparison at the small size. Returns the path relative to docs/hud/, for a link."""
        path = self.repo / rel
        self.used.add(rel.as_posix())
        if path.exists() and not self.all_anew:
            if follow is None:
                from PIL import Image
                with Image.open(path) as opened:
                    old = opened.convert("RGB")
                same, why = d.same_picture(im, old, region, volatile, panel)
            else:
                same, why = follow, "as its look"
            if same:
                self.kept.append(rel.as_posix())
                self.last = "kept"
                return rel.relative_to(DOCS).as_posix()
            self.changed.append(f"{rel.as_posix()} ({why})")
            self.last = "changed"
        else:
            self.new.append(rel.as_posix())
            self.last = "new"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(encode(im, quality))
        return rel.relative_to(DOCS).as_posix()

    def sweep(self):
        gone = []
        for p in sorted((self.repo / IMG).rglob("*.jpg")):
            rel = p.relative_to(self.repo).as_posix()
            if rel not in self.used:
                p.unlink()
                gone.append(rel)
        return gone


def write_allow(repo):
    """allow.txt's pictures: every file under docs/hud/img/ by its hash, under one heading; the lines of pictures that are gone go with them."""
    hashes = {p.relative_to(repo).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted((repo / IMG).rglob("*.jpg"))}
    allow = repo / "tools" / "scan" / "allow.txt"
    lines = [l for l in allow.read_text(encoding="utf-8").splitlines() if not l.startswith(f"binary {IMG.as_posix()}/") and l != ALLOW_HEAD]
    while lines and lines[-1] == "":
        lines.pop()
    lines += ["", ALLOW_HEAD]
    lines += [f"binary {path} {digest}  # a documentation screenshot: the HUD as a test client drew it on a test copy, test names only" for path, digest in sorted(hashes.items())]
    allow.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return len(hashes)


def relink(repo):
    """A gallery that carries its pictures inside it (data URLs, every byte %XX): each picture a file of its own under docs/hud/img/gallery/, its very
    bytes, named as publish names them (<frame>-look.jpg, <frame>-where.jpg from its alt text), and the gallery linking them; allow.txt takes their hashes."""
    page = repo / DOCS / GALLERY
    text = page.read_text(encoding="utf-8")
    made = []

    def file_of(m):
        attrs, url, alt = m.group(1), m.group(2), html.unescape(m.group(3))
        where = 'class="where"' in attrs
        stem = alt[: -len(" on the screen")] if where and alt.endswith(" on the screen") else alt
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", stem):
            raise SystemExit(f"{GALLERY}: a picture whose alt text names no frame: {alt[:60]!r}")
        rel = GALLERY_IMG / f"{stem}-{'where' if where else 'look'}.jpg"
        data = urllib.parse.unquote_to_bytes(url[len("data:image/jpeg,"):])
        if not data.startswith(b"\xff\xd8"):
            raise SystemExit(f"{GALLERY}: {alt[:60]!r} is not a JPEG")
        (repo / rel).parent.mkdir(parents=True, exist_ok=True)
        (repo / rel).write_bytes(data)
        made.append(rel.as_posix())
        return f'<img {attrs}src="{html.escape(rel.relative_to(DOCS).as_posix(), quote=True)}" alt="{m.group(3)}">'

    text = re.sub(r'<img ((?:class="where" )?)src="(data:image/jpeg,[^"]*)" alt="([^"]*)">', file_of, text)
    if "data:image" in text:
        raise SystemExit(f"{GALLERY}: a picture is left inside it that this does not read")
    if len(made) != len(set(made)):
        raise SystemExit(f"{GALLERY}: two pictures for one file name")
    page.write_text(text, encoding="utf-8", newline="\n")
    n = write_allow(repo)
    print(f"docs/hud/{GALLERY}: {len(made)} pictures out of it into {GALLERY_IMG.as_posix()}/, linked; {len(text)} characters now")
    print(f"tools/scan/allow.txt: {n} pictures under docs/hud/img/ with their hashes")


def main():
    ap = argparse.ArgumentParser(description="docshots.py's reviewed pictures into the documentation")
    ap.add_argument("--out")
    ap.add_argument("--repo", default=str(d.OS2), help="the checkout whose docs/ take the pictures (default: this one)")
    ap.add_argument("--res", default="1920x1080")
    ap.add_argument("--all", action="store_true", help="every picture written anew, none compared with the committed one")
    ap.add_argument("--relink", action="store_true", help="the committed gallery's pictures out of it, each a file of its own, and linked")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    repo = Path(args.repo)
    if args.relink:
        return relink(repo)
    if not args.out:
        ap.error("--out <docshots run folder>, or --relink")
    out = Path(args.out)
    res_dir = out / args.res
    w, h = d.parse_res(args.res)
    man = json.loads((res_dir / "manifest.json").read_text(encoding="utf-8"))
    info = d.validate()
    by_id = {r.id: r for r in d.ROWS}
    by_name = info["by_name"]
    DROPPED.update((res_dir / "dropped.txt").read_text(encoding="utf-8").split() if (res_dir / "dropped.txt").exists() else [])
    wrote = [f[k] for g in man["rows"].values() for f in g.get("frames", []) if f["file"] not in DROPPED for k in ("file", "crop") if f.get(k)]
    reviewed = set((res_dir / "reviewed.txt").read_text(encoding="utf-8").split()) if (res_dir / "reviewed.txt").exists() else set()
    missing = [x for x in wrote if x not in reviewed]
    if missing:
        raise SystemExit(f"REFUSED: {len(missing)} of {len(wrote)} pictures are not in {res_dir / 'reviewed.txt'}: {', '.join(missing[:8])}")
    taken = f"at {args.res}, from OverStrike2 {man.get('built') or man.get('commit')} (HUD content {man.get('content')}, plugin {man.get('plugin')})"
    pics = Pictures(repo, args.all)

    placed = set()
    for slug, part, lead, ids in PAGES:
        lines = [f"# The HUD: {part}", "", lead, "",
                 f"A real game client draws every picture here against a test copy of the server, taken by script {taken}. Test names only: the avatar is blurred."
                 " The index of every part is in the [README](../../README.md#the-hud-page-by-page); every kind of on-screen message is in the [gallery](messages.html).", ""]
        for rid in ids:
            r = by_id[rid]
            got = frames_of(man, rid)
            if not got:
                continue
            placed.add(rid)
            lines += [f"## {r.shows}", ""]
            for f in (got if len(got) <= 2 else [got[0], got[-1]]):      # a page takes a row's first and last frame: the gallery and the owner's review have them all
                stem = Path(f["file"]).stem
                link = pics.put(IMG / f"{stem}.jpg", picture(res_dir / f["file"]), 85, d.compare_region(r, w, h), d.volatile_boxes(r, w, h), d.panel_of(r, w, h))
                lines += [f"![{html.escape(r.shows, quote=False)}]({link})", ""]   # Markdown alt text: < > & escaped, quotes as they are
            if r.pick:
                lines += [f"The message: {by_name[r.pick].name}. Where it goes: {by_name[r.pick].surfaces}", ""]
        (repo / DOCS / f"{slug}.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8", newline="\n")
        print(f"docs/hud/{slug}.md: {sum(1 for x in ids if frames_of(man, x))} of {len(ids)} rows with pictures")

    # the gallery: every message of the message list, how it looks (the crop, full size) and where (the whole screen, smaller, the place marked)
    parts = ["<!doctype html><meta charset=utf-8><title>Over Strike 2 - every kind of on-screen message</title>",
             "<style>body{font:14px system-ui,sans-serif;background:#111;color:#ddd;margin:24px;max-width:1500px}h1{color:#E9B44C}h2{color:#E9B44C;margin-top:40px;border-top:1px solid #333;padding-top:12px}"
             "h3{margin:22px 0 6px}figure{margin:6px 0 18px}figcaption{color:#99a;margin-top:4px}img{max-width:100%;border:1px solid #333}.where{width:640px}.none{color:#c96}</style>",
             "<h1>Over Strike 2 - every kind of on-screen message</h1>",
             f"<p>One section per message, as the message system has it: how it looks, and where on the screen it appears (the gold box). A real game client drew each one against a test copy of"
             f" the server, taken by script {html.escape(taken)}. Test names only. Messages without a picture say why.</p>"]
    heading = ""
    shown = 0
    f_where = min(1.0, WHERE_WIDTH / w)
    for p in info["picks"]:
        if p.heading != heading:
            heading = p.heading
            parts.append(f"<h2>{html.escape(heading[0] + heading[1:].lower())}</h2>")
        parts.append(f"<h3>{html.escape(p.name)}</h3><p>{html.escape(p.surfaces)}</p>")
        rows = [r for r in d.ROWS if r.pick == p.name]
        pictured = False
        for r in rows:
            for f in frames_of(man, r.id):
                stem = Path(f["file"]).stem
                full = res_dir / f["file"]
                box = d.place(r.crop, w, h) if r.crop else None
                follow = None
                if f.get("crop"):
                    look = picture(res_dir / f["crop"])
                    link = pics.put(GALLERY_IMG / f"{stem}-look.jpg", look, 85, d.compare_region(r, w, h, crop=True), d.volatile_boxes(r, w, h, crop=True),
                                    d.panel_of(r, w, h, crop=True))
                    if d.panel_of(r, w, h):   # over the game the where shows the look's crop, smaller: it goes as its look goes (a window's shows the window)
                        follow = {"kept": True, "changed": False}.get(pics.last)
                    parts.append(f'<figure><img src="{html.escape(link, quote=True)}" alt="{html.escape(stem, quote=True)}"><figcaption>How it looks: {html.escape(r.shows)}</figcaption></figure>')
                where = picture(full, WHERE_WIDTH, box)
                link = pics.put(GALLERY_IMG / f"{stem}-where.jpg", where, 75, scaled(d.compare_region(r, w, h), f_where), [scaled(v, f_where) for v in d.volatile_boxes(r, w, h)],
                                follow=follow)
                parts.append(f'<figure><img class="where" src="{html.escape(link, quote=True)}" alt="{html.escape(stem, quote=True)} on the screen"><figcaption>Where: {html.escape(r.shows)}</figcaption></figure>')
                pictured = True
                break      # one frame a row is enough for the gallery; the pages have them all
        if pictured:
            shown += 1
        else:
            why = next((r.reason for r in rows if r.status != d.READY and r.reason), "") or next((man["rows"].get(r.id, {}).get("failed", "") for r in rows if man["rows"].get(r.id, {}).get("failed")), "")
            parts.append(f'<p class="none">No picture: {html.escape(why or "not taken in this run")}</p>')
    (repo / DOCS / GALLERY).write_text("\n".join(parts) + "\n", encoding="utf-8", newline="\n")
    print(f"docs/hud/{GALLERY}: {shown} of {len(info['picks'])} messages with a picture")

    # README's index: the Page column
    readme = repo / "README.md"
    text = readme.read_text(encoding="utf-8")
    a, b = text.index("<!-- hud-index:begin"), text.index("<!-- hud-index:end -->")
    block = text[a:b]
    for slug, part, lead, ids in PAGES:
        block = re.sub(rf"^(\| {re.escape(part)} \|[^|\n]*\|) [^|\n]* \|$", rf"\1 [{slug}](docs/hud/{slug}.md) |", block, flags=re.M)
    block = re.sub(r"^(\| Every kind of on-screen message \|[^|\n]*\|) [^|\n]* \|$", rf"\1 [the gallery](docs/hud/{GALLERY}) |", block, flags=re.M)
    readme.write_text(text[:a] + block + text[b:], encoding="utf-8", newline="\n")
    print("README.md: the Page column filled")

    gone = pics.sweep()
    n = write_allow(repo)
    print(f"pictures: {len(pics.kept)} kept as committed (the same, all but what differs on every shot), {len(pics.changed)} written anew, {len(pics.new)} new, {len(gone)} no longer used and removed")
    for c in pics.changed:
        print("  changed:", c)
    print(f"tools/scan/allow.txt: {n} pictures under docs/hud/img/ with their hashes; rows on no page: {', '.join(sorted(r for r in man['rows'] if r not in placed and not by_id[r].pick)) or 'none'}")


if __name__ == "__main__":
    main()
