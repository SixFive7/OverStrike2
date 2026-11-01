"""pagesplit.py - the site page as two files, so a phone does not load all 3,564 rows (5.6 MB) at once.

index.html holds the whole page with the rows up to a cut (#1-#1016 by default: the 1,000 most-subscribed maps and the 16
glass maps) and, where the rows after the cut were, one placeholder row (<li class="more" id="more">). more.html holds the
rows after the cut, line for line as build-1000.py and broken-maps.py wrote them. The page's own script (build-1000.py's
SCRIPT) fetches more.html when the reader scrolls near the placeholder, types in the filter box, picks a size chip,
ticks Hide broken or clicks the placeholder's button, and puts its rows where the placeholder was: the page is then the
full one. The placeholder carries the cut, the row count and a hash of more.html; the hash is the fetch URL's ?v=, so a
browser never pairs a new index.html with an old cached more.html, and join() refuses a pair that does not belong together.

The tools work on the joined page: broken-maps.py joins a split --src and writes its stamped page split again at the
same cut; bridge/search-index.py reads the rows of both files. By hand (from the repo root):
  python site/pagesplit.py split FULL.html --out DIR [--cut 1016]   # FULL.html -> DIR/index.html + DIR/more.html
  python site/pagesplit.py join --out FULL.html [--src DIR]          # the pair -> the full page
  python site/pagesplit.py check [--src DIR]                         # the pair joins and re-splits byte for byte
DIR defaults to this script's folder (the repo master of the site). Deploy both files and img/ (docs/reference.md).
"""
import argparse
import hashlib
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
LIST_OPEN, LIST_CLOSE = '  <ol class="list" id="list">', "  </ol>"
RE_RANK = re.compile(r'^\s*<div class="rank"><span>(\d+)</span></div>\s*$')
RE_MARK = re.compile(r'^<li class="more" id="more" data-after="(\d+)" data-count="(\d+)" data-src="more\.html\?v=([0-9a-f]{12})"')
MORE_NAME = "more.html"
MORE_HEAD = "<!-- more.html: the {n:,} rows after #{cut} of index.html, fetched by its script; pagesplit.py joins them back -->"
DEFAULT_CUT = 1016
MARK_STYLE = "padding:1.6rem 1rem;text-align:center;color:var(--text-dim);border-bottom:1px solid var(--line-soft)"
BUTTON_STYLE = ("margin:.5rem 0 0 .6rem;padding:.45rem .95rem;font:inherit;color:var(--accent);background:var(--surface);"
                "border:1px solid var(--line);border-radius:var(--radius);cursor:pointer")


def newline(text):
    return "\r\n" if "\r\n" in text else "\n"


def mark_of(text):
    """The placeholder row's match, or None for a one-file page."""
    found = [m for m in (RE_MARK.match(l) for l in text.split(newline(text))) if m]
    assert len(found) <= 1, f"{len(found)} placeholder rows on one page"
    return found[0] if found else None


def digest(more):
    return hashlib.sha256(more.encode("utf-8")).hexdigest()[:12]


def row_blocks(L):
    """(first line, last line, rank) of every row of #list - each one the lines from <li class="row... to </li>."""
    opens = [i for i, l in enumerate(L) if l == LIST_OPEN]
    assert len(opens) == 1, f"expected one map list, found {len(opens)}"
    open_at = opens[0]
    close_at = next(i for i in range(open_at + 1, len(L)) if L[i] == LIST_CLOSE)
    out, i = [], open_at + 1
    while i < close_at:
        assert L[i].startswith('<li class="row'), f"line {i + 1} of #list does not start a row: {L[i][:80]!r}"
        j = i
        while L[j] != "</li>":
            j += 1
            assert j < close_at, f"the row at line {i + 1} never closes"
        ranks = [int(m.group(1)) for m in map(RE_RANK.match, L[i:j]) if m]
        assert len(ranks) == 1, f"the row at line {i + 1} has {len(ranks)} rank divs"
        out.append((i, j, ranks[0]))
        i = j + 1
    return open_at, close_at, out


def split(full, cut=DEFAULT_CUT):
    """The full page -> (index.html, more.html); more.html is None when no row is after the cut."""
    assert mark_of(full) is None, "the page is split already"
    nl = newline(full)
    L = full.split(nl)
    open_at, close_at, blocks = row_blocks(L)
    ranks = [b[2] for b in blocks]
    assert ranks == sorted(set(ranks)), "the list's ranks are not in increasing order"
    after = [b for b in blocks if b[2] > cut]
    if not after:
        return full, None
    first = after[0][0]
    more = nl.join([MORE_HEAD.format(cut=cut, n=len(after))] + L[first:close_at]) + nl
    lo, hi, n = after[0][2], after[-1][2], len(after)
    mark = (f'<li class="more" id="more" data-after="{cut}" data-count="{n}" data-src="{MORE_NAME}?v={digest(more)}" '
            f'style="{MARK_STYLE}"><span>{n:,} more maps, #{lo}&ndash;#{hi}, load when you scroll down to them or '
            f'search.</span><button type="button" style="{BUTTON_STYLE}">Show them now</button></li>')
    index = nl.join(L[:first] + [mark] + L[close_at:])
    assert join(index, more) == full, "split does not join back to the page it came from"
    return index, more


def join(index, more):
    """(index.html, more.html) -> the full page, byte for byte the one split() was given."""
    nl = newline(index)
    L = index.split(nl)
    at = [i for i, l in enumerate(L) if RE_MARK.match(l)]
    assert len(at) == 1, f"expected one placeholder row in index.html, found {len(at)}"
    m = RE_MARK.match(L[at[0]])
    cut, n, v = int(m.group(1)), int(m.group(2)), m.group(3)
    assert digest(more) == v, f"{MORE_NAME} is not the one this index.html was split with (hash {digest(more)}, index.html wants {v})"
    M = more.split(nl)
    assert M[0] == MORE_HEAD.format(cut=cut, n=n) and M[-1] == "", f"{MORE_NAME} does not start with its header line"
    return nl.join(L[:at[0]] + M[1:-1] + L[at[0] + 1:])


def read_text(path):
    with open(path, encoding="utf-8", newline="") as fh:  # newline="" keeps the page's line endings intact
        return fh.read()


def write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


def read_page(path):
    """A page file -> (the full page, the cut it was split at or None). A split index.html is joined with the more.html
    beside it."""
    path = Path(path)
    text = read_text(path)
    m = mark_of(text)
    if m is None:
        return text, None
    return join(text, read_text(path.parent / MORE_NAME)), int(m.group(1))


def write_page(full, out_dir, cut):
    """Write the full page to out_dir: split at `cut` into index.html + more.html, or as one index.html when cut is None.
    Returns the paths written."""
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    if cut is None:
        write_text(out_dir / "index.html", full)
        return [out_dir / "index.html"]
    index, more = split(full, cut)
    write_text(out_dir / "index.html", index)
    if more is None:
        return [out_dir / "index.html"]
    write_text(out_dir / MORE_NAME, more)
    return [out_dir / "index.html", out_dir / MORE_NAME]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("split", help="a full page -> index.html + more.html")
    s.add_argument("full")
    s.add_argument("--out", default=str(HERE))
    s.add_argument("--cut", type=int, default=DEFAULT_CUT, help=f"the last rank kept in index.html (default {DEFAULT_CUT})")
    j = sub.add_parser("join", help="index.html + more.html -> the full page")
    j.add_argument("--src", default=str(HERE))
    j.add_argument("--out", required=True)
    c = sub.add_parser("check", help="the pair joins, and splits back byte for byte")
    c.add_argument("--src", default=str(HERE))
    args = ap.parse_args()

    if args.cmd == "split":
        full = read_text(args.full)
        for p in write_page(full, args.out, args.cut):
            print(f"wrote {p}: {p.stat().st_size:,} bytes")
        return 0
    full, cut = read_page(Path(args.src) / "index.html")
    if args.cmd == "join":
        write_text(args.out, full)
        print(f"wrote {args.out}: {len(full.encode('utf-8')):,} bytes (split at #{cut})" if cut else f"wrote {args.out}: not split")
        return 0
    if cut is None:
        print("index.html is one file: nothing to check")
        return 0
    index, more = split(full, cut)
    same = index == read_text(Path(args.src) / "index.html") and more == read_text(Path(args.src) / MORE_NAME)
    print(f"{args.src}: {'the pair joins and re-splits byte for byte' if same else 'MISMATCH'} (cut #{cut})")
    return 0 if same else 1


if __name__ == "__main__":
    sys.exit(main())
