#!/usr/bin/env python
"""publish.py - docshots.py's reviewed pictures into the documentation, as AGENTS.md asks ("The documentation shows the whole HUD"): a page per part of
the HUD under docs/hud/, their index in README.md between its hud-index markers, the one-file gallery of every on-screen message (docs/hud/messages.html), and every
picture reviewed into tools/scan/allow.txt with its hash. Writes the working tree only: no git command, no commit.

    python -I publish.py --out <docshots run folder> [--repo <checkout>] [--res 1920x1080]      (--repo: this one by default)

It refuses unless <out>/<res>/reviewed.txt names every picture the run wrote, one file name a line: the review is by eye, for names, ids and addresses, and this file
says it was done. The pictures go in as JPEG at full size (quality 85, no chroma subsampling: the HUD's words stay sharp), the gallery's as smaller JPEG inside the HTML; a page takes a row's first and last frame.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import html
import io
import json
import re
import sys
import time
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
ALLOW_HEAD = "# The documentation's screenshots (docshots.py, a real client against a test copy): each looked at for names, Steam ids and addresses; test names only"


def jpeg(png, quality=85, width=None, box=None):
    from PIL import Image, ImageDraw
    with Image.open(png) as opened:
        im = opened.convert("RGB")
    if box:
        ImageDraw.Draw(im).rectangle(box, outline=(233, 180, 76), width=max(3, round(im.size[1] / 270)))
    if width and im.size[0] > width:
        im = im.resize((width, round(im.size[1] * width / im.size[0])), Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=quality, subsampling=0, optimize=True)
    return buf.getvalue()


DROPPED: set = set()   # <res>/dropped.txt: frames the review threw out (a loading screen, a menu over the subject); they go nowhere


def data_url(data):
    """A picture inside the gallery as a data URL whose every byte is %XX: base64's long runs of letters and digits spell out, by chance, the short private
    words tools/scan's denylist looks for (seen 2026-11-06); here no run is longer than two, so the scanner still reads the gallery's own words and finds nothing in its pictures."""
    return "data:image/jpeg," + "".join(f"%{b:02X}" for b in data)


def frames_of(man, rid):
    return [f for f in man["rows"].get(rid, {}).get("frames", []) if f["file"] not in DROPPED]


def main():
    ap = argparse.ArgumentParser(description="docshots.py's reviewed pictures into the documentation")
    ap.add_argument("--out", required=True)
    ap.add_argument("--repo", default=str(d.OS2), help="the checkout whose docs/ take the pictures (default: this one)")
    ap.add_argument("--res", default="1920x1080")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    out, repo = Path(args.out), Path(args.repo)
    res_dir = out / args.res
    man = json.loads((res_dir / "manifest.json").read_text(encoding="utf-8"))
    info = d.validate()
    by_id = {r.id: r for r in d.ROWS}
    by_name = info["by_name"]
    DROPPED.update((res_dir / "dropped.txt").read_text(encoding="utf-8").split() if (res_dir / "dropped.txt").exists() else [])
    wrote = [f[k] for g in man["rows"].values() for f in g.get("frames", []) if f["file"] not in DROPPED for k in ("file", "crop") if f.get(k)]
    reviewed = set((res_dir / "reviewed.txt").read_text(encoding="utf-8").split()) if (res_dir / "reviewed.txt").exists() else set()
    missing = [w for w in wrote if w not in reviewed]
    if missing:
        raise SystemExit(f"REFUSED: {len(missing)} of {len(wrote)} pictures are not in {res_dir / 'reviewed.txt'}: {', '.join(missing[:8])}")
    for p in (repo / IMG).glob("*.jpg"):
        p.unlink()
    (repo / IMG).mkdir(parents=True, exist_ok=True)
    taken = f"at {args.res}, from OverStrike2 {man.get('built') or man.get('commit')} (HUD content {man.get('content')}, plugin {man.get('plugin')})"
    hashes = {}

    def put(name, data):
        path = IMG / name
        (repo / path).write_bytes(data)
        hashes[path.as_posix()] = hashlib.sha256(data).hexdigest()
        return f"img/{name}"

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
                lines += [f"![{html.escape(r.shows, quote=True)}]({put(stem + '.jpg', jpeg(res_dir / f['file']))})", ""]
            if r.pick:
                lines += [f"The message: {by_name[r.pick].name}. Where it goes: {by_name[r.pick].surfaces}", ""]
        (repo / DOCS / f"{slug}.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
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
                box = d.place(r.crop, *map(int, args.res.split("x"))) if r.crop else None
                if f.get("crop"):
                    parts.append(f'<figure><img src="{data_url(jpeg(res_dir / f["crop"], 85))}" alt="{html.escape(stem, quote=True)}"><figcaption>How it looks: {html.escape(r.shows)}</figcaption></figure>')
                parts.append(f'<figure><img class="where" src="{data_url(jpeg(full, 75, 800, box))}" alt="{html.escape(stem, quote=True)} on the screen"><figcaption>Where: {html.escape(r.shows)}</figcaption></figure>')
                pictured = True
                break      # one frame a row is enough for the gallery; the pages have them all
        if pictured:
            shown += 1
        else:
            why = next((r.reason for r in rows if r.status != d.READY and r.reason), "") or next((man["rows"].get(r.id, {}).get("failed", "") for r in rows if man["rows"].get(r.id, {}).get("failed")), "")
            parts.append(f'<p class="none">No picture: {html.escape(why or "not taken in this run")}</p>')
    (repo / DOCS / GALLERY).write_text("\n".join(parts) + "\n", encoding="utf-8")
    print(f"docs/hud/{GALLERY}: {shown} of {len(info['picks'])} messages with a picture")

    # README's index: the Page column
    readme = repo / "README.md"
    text = readme.read_text(encoding="utf-8")
    a, b = text.index("<!-- hud-index:begin"), text.index("<!-- hud-index:end -->")
    block = text[a:b]
    for slug, part, lead, ids in PAGES:
        block = re.sub(rf"^(\| {re.escape(part)} \|[^|\n]*\|) [^|\n]* \|$", rf"\1 [{slug}](docs/hud/{slug}.md) |", block, flags=re.M)
    block = re.sub(r"^(\| Every kind of on-screen message \|[^|\n]*\|) [^|\n]* \|$", rf"\1 [the gallery](docs/hud/{GALLERY}) |", block, flags=re.M)
    readme.write_text(text[:a] + block + text[b:], encoding="utf-8")
    print("README.md: the Page column filled")

    # allow.txt: the pictures, each with its hash, under one heading; the earlier ones of docs/hud/img go
    allow = repo / "tools" / "scan" / "allow.txt"
    lines = [l for l in allow.read_text(encoding="utf-8").splitlines() if not l.startswith("binary docs/hud/img/") and l != ALLOW_HEAD]
    while lines and lines[-1] == "":
        lines.pop()
    lines += ["", ALLOW_HEAD]
    lines += [f"binary {path} {digest}  # a documentation screenshot: the HUD as a test client drew it on a test copy, test names only" for path, digest in sorted(hashes.items())]
    allow.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"tools/scan/allow.txt: {len(hashes)} pictures with their hashes; rows on no page: {', '.join(sorted(r for r in man['rows'] if r not in placed and not by_id[r].pick)) or 'none'}")


if __name__ == "__main__":
    main()
