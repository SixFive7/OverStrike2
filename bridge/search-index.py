#!/usr/bin/env python3
"""search-index.py - the full-text index of the HUD's map browser: search.json, beside maps.json.

For every map on maps.json, the words the site's own filter box searches: its name, Workshop ID, Workshop tags, author
and up to 800 characters of its full Workshop description - build-1000.py's data-search corpus, which every row of
site/index.html carries. The page is the source because it is committed and holds all of it; the
Workshop scrape behind it lives only in .work scratch. Ids that are not on maps.json (the Not maps tab) never enter.
Since 2026-10-22 the page is two files (site/pagesplit.py): index.html with rows #1-#1016 and more.html
with the rest, read together.

The bridge reads search.json like maps.json - at start, and again about 10 s after it changes (Reload.cs) - and serves
it to the plugin on /search/<secret>; HudSearch.cs (shared by both) normalises each text and matches the typed words
against the name first, then this text. A missing file leaves names-only search, which still works.

Usage (from the repo root):
  python bridge/search-index.py            # write bridge/search.json
  python bridge/search-index.py --check    # exit 1 when the committed search.json does not match the page
Deploy: copy bridge/search.json to the bridge's data share, beside maps.json:
  <share>   (/data/search.json in bridge)
Re-run it whenever the page's rows or maps.json change, and commit the result with them.
"""
import argparse
import html
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PAGE = HERE.parent / "site" / "index.html"
MAPS = HERE / "maps.json"
OUT = HERE / "search.json"

LIST_OPEN, LIST_CLOSE = '<ol class="list" id="list">', "</ol>"
ROW = re.compile(r'<li class="row[^"]*"[^>]*\sdata-search="([^"]*)"[^>]*>.*?<a class="shot[^"]*" href="[^"]*[?&]id=(\d+)"', re.S)


def build(page_text, maps):
    start = page_text.index(LIST_OPEN)
    rows = page_text[start:page_text.index(LIST_CLOSE, start)]
    found = {}
    for corpus, wid in ROW.findall(rows):
        assert wid not in found, f"workshop id {wid} is on two rows of the page"
        found[wid] = " ".join(html.unescape(corpus).split())
    listed = [str(m["id"]) for m in maps]
    missing = [w for w in listed if w not in found]
    assert not missing, f"{len(missing)} maps.json id(s) have no row on the page, e.g. {missing[:5]}: rebuild the page first"
    return {w: found[w] for w in listed}  # maps.json's order: rank 1 first


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--page", default=str(PAGE), help="the site page whose rows carry the corpus (default: the repo's)")
    ap.add_argument("--maps", default=str(MAPS), help="the bridge's maps.json (default: the repo's)")
    ap.add_argument("--out", default=str(OUT), help="where to write search.json (default: beside this script)")
    ap.add_argument("--check", action="store_true", help="write nothing; exit 1 when --out differs from what would be written")
    args = ap.parse_args()

    maps = json.loads(Path(args.maps).read_text(encoding="utf-8"))
    sys.path.insert(0, str(PAGE.parent))
    import pagesplit  # since 2026-10-22 the page is index.html + more.html (the rows after #1016): the rows of both count
    index = build(pagesplit.read_page(args.page)[0], maps)
    text = json.dumps(index, ensure_ascii=False, indent=0, sort_keys=False) + "\n"
    chars = sum(len(t) for t in index.values())
    if args.check:
        same = Path(args.out).exists() and Path(args.out).read_text(encoding="utf-8") == text
        print(f"{args.out}: {'up to date' if same else 'STALE - re-run search-index.py'} ({len(index)} maps)")
        return 0 if same else 1
    Path(args.out).write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {args.out}: {len(index)} maps, {chars:,} characters of text ({len(text.encode('utf-8')):,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
