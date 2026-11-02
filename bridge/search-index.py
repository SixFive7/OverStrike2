#!/usr/bin/env python3
"""search-index.py - the full-text index of the HUD's map browser: search.json, beside maps.json.

For every map on maps.json, the words a search finds it by: its name, its Workshop id and its Workshop tags, lowercased.
Other authors' descriptions never enter this file (AGENTS.md).

The bridge reads search.json like maps.json - at start, and again about 10 s after it changes (Reload.cs) - and serves
it to the plugin on /search/<secret>; HudSearch.cs (shared by both) normalises each text and matches the typed words
against the name first, then this text. A missing file leaves names-only search, which still works. On a server the
nightly sync (server/workshop-sync.py) adds a text for every map it publishes, of every mode; this script keeps the committed
file in step with the committed maps.json (which holds the Arms Race list: the other modes' maps get theirs on the server).

Usage (from the repo root):
  python bridge/search-index.py                  # write bridge/search.json: maps.json's order, a line for every map, no other
  python bridge/search-index.py --export F.json  # the same, a new map's tags from what `workshop-sync.py --export` printed
  python bridge/search-index.py --check          # exit 1 when search.json is not in step with maps.json
A map new on maps.json gets its name and id, and its tags when --export names them. A map that left maps.json loses its line.
Deploy: deploy/data.py copies it to the bridge's data folder when the server has none yet.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
MAPS = HERE / "maps.json"
OUT = HERE / "search.json"
MAX_TEXT = 300  # a title, an id and tags fit well inside this (the longest is about 120); anything longer is not what belongs here


def text_of(name, wid, tags=()):
    """The words a search finds a map by: its name, its Workshop id, its tags - lowercased, one space between."""
    words = str(name).lower().split() + [str(wid)]
    for tag in tags:
        words += str(tag).lower().split()
    return " ".join(words)


def build(maps, known, export=None):
    """search.json's content for this maps.json: a known map keeps its text, a new one gets its own, in maps.json's order."""
    tags = {str(e["id"]): e.get("tags") or [] for e in (export or {}).get("maps.json", [])}
    return {str(m["id"]): known.get(str(m["id"])) or text_of(m["name"], m["id"], tags.get(str(m["id"]), ())) for m in maps}


def problems(maps, index):
    """What is wrong with search.json for this maps.json: empty when they are in step."""
    listed = [str(m["id"]) for m in maps]
    found = []
    missing = [w for w in listed if w not in index]
    if missing:
        found.append(f"{len(missing)} map(s) of maps.json have no text, e.g. {missing[:5]}")
    stray = [w for w in index if w not in set(listed)]
    if stray:
        found.append(f"{len(stray)} text(s) for maps not on maps.json, e.g. {stray[:5]}")
    if not missing and not stray and list(index) != listed:
        found.append("the texts are not in maps.json's order")
    no_id = [w for w, t in index.items() if w not in t.split()]
    if no_id:
        found.append(f"{len(no_id)} text(s) without their own Workshop id, e.g. {no_id[:5]}")
    long = [w for w, t in index.items() if len(t) > MAX_TEXT]
    if long:
        found.append(f"{len(long)} text(s) over {MAX_TEXT} characters - more than a title, an id and tags, e.g. {long[:5]}")
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--maps", default=str(MAPS), help="the bridge's maps.json (default: the repo's)")
    ap.add_argument("--out", default=str(OUT), help="the search.json to check or write (default: beside this script)")
    ap.add_argument("--export", help="what `workshop-sync.py --export` printed: the tags of the maps it published")
    ap.add_argument("--check", action="store_true", help="write nothing; exit 1 when --out is not in step with --maps")
    args = ap.parse_args()

    maps = json.loads(Path(args.maps).read_text(encoding="utf-8"))
    out = Path(args.out)
    known = json.loads(out.read_text(encoding="utf-8")) if out.exists() else {}
    if args.check:
        found = problems(maps, known)
        print(f"{out}: {'in step' if not found else 'NOT in step - ' + '; '.join(found)} ({len(known)} texts, {len(maps)} maps)")
        return 1 if found else 0
    export = json.loads(Path(args.export).read_text(encoding="utf-8")) if args.export else None
    index = build(maps, known, export)
    found = problems(maps, index)
    if found:
        print("refused: " + "; ".join(found), file=sys.stderr)
        return 1
    text = json.dumps(index, ensure_ascii=False, indent=0, sort_keys=False) + "\n"
    out.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {out}: {len(index)} maps, {sum(len(t) for t in index.values()):,} characters of text ({len(text.encode('utf-8')):,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
