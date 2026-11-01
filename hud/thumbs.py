"""The menu's map thumbnails: one small JPEG per map on maps.json, the stylesheet that gives each one its class, and the list that
numbers them.

    python thumbs.py [--src DIR]

Reads bridge/maps.json and the site's own thumbnails (img/<id>.webp, 336x189), from --src when given (a folder holding them,
e.g. the site's own img folder), otherwise from https://overstrike.example.com/img/. Writes:

  hud/thumbs/<id>.jpg                 336x189, quality 82 (about 13 KB each) - gitignored, rebuilt from the site; build.ps1 copies
                                      them into the addon, where the resource compiler stores each JPEG as it is (VTEX JPEG_DXT5).
                                      Black bars baked into a Workshop preview (letterbox or pillarbox) are filled with a blurred copy
                                      of the picture first (debar), since 2026-10-21: about a fifth of the maps had them. Delete the
                                      folder to rebuild every one
  bridge/HudThumbList.cs          committed: every map that ever had a picture, in the order they got one. A map's place in it is
                                      its picture's number, for good: new maps go at the end and nothing moves, so a player whose CS2
                                      still holds an older addon sees no picture for a newer map - never another map's picture
  hud/styles/gungame_hud_thumbs.css   committed: one rule per picture, ".th<n / 64>.tl<n % 64>" - two classes out of a small set
                                      instead of one per map, because the game interns every class name the server ever sets on the
                                      layout entity and stops at 1,024 of them; this way 1,000 maps cost it 80. A listed map's rule
                                      points at its JPEG, an official map's at the game's own 360p screenshot (nothing to ship)

HudThumbs in bridge/HudBrowser.cs makes the same class names out of HudThumbList; the bridge's --selftest checks that every map has
its rule and that every rule is its own map's.
"""
import argparse
import io
import json
import os
import re
import sys
import urllib.request
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter

HERE = Path(__file__).resolve().parent
MAPS = HERE.parent / "bridge" / "maps.json"
OUT = HERE / "thumbs"
SITE_URL = (os.environ.get("SITE_URL") or "https://overstrike.example.com").rstrip("/")  # where img/<id>.webp is served
RELEASE = re.search(r'Release = "(\w+)"', (HERE.parent / "bridge" / "Hud.cs").read_text(encoding="utf-8")).group(1)  # Hud.Release
CSS = HERE / "styles" / f"gungame_hud_thumbs_{RELEASE}.css"
LIST = HERE.parent / "bridge" / "HudThumbList.cs"
BASE = 64  # HudThumbs.Base: picture n has the classes th<n / 64> and tl<n % 64>
OFFICIAL = ["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]  # the bridge's OFFICIAL_MAPS default
SIZE = (336, 189)
QUALITY = 82

HEAD = """/*
  Over Strike 2 - the menu's map thumbnails, written by hud/thumbs.py from bridge/maps.json: do not edit by hand.

  A card's picture panel gets two classes, th<n / 64> and tl<n % 64>, where n is the map's place in bridge/HudThumbList.cs
  (HudThumbs in bridge/HudBrowser.cs). Two classes out of a small set rather than one per map: the game counts every class name
  the server ever sets on the layout entity and takes no more than 1,024 of them.

  A listed map's picture is the site's own, a JPEG in the addon (file://{images}/... makes the resource compiler build it, storing
  the JPEG as it is); an official map's is the game's own 360p screenshot, so it costs the addon nothing.
*/
"""

LIST_HEAD = """// Written by hud/thumbs.py - do not edit by hand. Every map that ever had a picture in the menu, in the order
// they got one: a key's place in Keys is its picture's number (HudThumbs), and gungame_hud_thumbs.css has the rule for each number.
// Append-only on purpose: a player whose CS2 still holds an older addon then sees no picture for a newer map, never another map's.
static class HudThumbList
{
    public static readonly string[] Keys =
    [
"""


DARK, MIN_BAR = 24, 4  # a bar line: every pixel's luminance at most DARK; a side is cut only when its bar is at least MIN_BAR px


def debar(im):
    """The picture without the black bars baked into its Workshop preview: whole edge rows or columns that are near-black from end to
    end are found, at most a third of each side, and the picture inside them is fitted whole into 336x189 on a blurred, darkened copy
    of itself - the way video players fill bars. Nothing of the picture is cut, so a title or a logo near an edge stays whole (cutting
    it to fill the frame did lose them); a dark scene keeps its edges, since some pixel in an edge line of a real picture is lighter
    than DARK. Returns the picture and the box that held the picture (None when there were no bars)."""
    g = im.convert("L")
    w, h = g.size
    px = g.load()

    def dark_col(x):
        return max(px[x, y] for y in range(h)) <= DARK

    def dark_row(y):
        return max(px[x, y] for x in range(w)) <= DARK

    left = 0
    while left < w // 3 and dark_col(left):
        left += 1
    right = w
    while right > w - w // 3 and dark_col(right - 1):
        right -= 1
    top = 0
    while top < h // 3 and dark_row(top):
        top += 1
    bottom = h
    while bottom > h - h // 3 and dark_row(bottom - 1):
        bottom -= 1
    left, top = (left if left >= MIN_BAR else 0), (top if top >= MIN_BAR else 0)
    right, bottom = (right if w - right >= MIN_BAR else w), (bottom if h - bottom >= MIN_BAR else h)
    if (left, top, right, bottom) == (0, 0, w, h):
        return im, None
    inner = im.crop((left, top, right, bottom))
    cw, ch = inner.size
    cover = max(SIZE[0] / cw, SIZE[1] / ch)
    bg = inner.resize((max(SIZE[0], round(cw * cover)), max(SIZE[1], round(ch * cover))), Image.LANCZOS)
    bg = bg.crop(((bg.width - SIZE[0]) // 2, (bg.height - SIZE[1]) // 2, (bg.width - SIZE[0]) // 2 + SIZE[0], (bg.height - SIZE[1]) // 2 + SIZE[1]))
    bg = ImageEnhance.Brightness(bg.filter(ImageFilter.GaussianBlur(14))).enhance(0.55)
    fit = min(SIZE[0] / cw, SIZE[1] / ch)
    fg = inner.resize((round(cw * fit), round(ch * fit)), Image.LANCZOS)
    bg.paste(fg, ((SIZE[0] - fg.width) // 2, (SIZE[1] - fg.height) // 2))
    return bg, (left, top, right, bottom)


def fetch(map_id, src):
    if src:
        return Image.open(Path(src) / f"{map_id}.webp")
    with urllib.request.urlopen(f"{SITE_URL}/img/{map_id}.webp", timeout=30) as r:
        return Image.open(io.BytesIO(r.read()))


def known_keys():
    """The list as committed: the keys in their order (empty when there is none yet)."""
    if not LIST.exists():
        return []
    body = LIST.read_text(encoding="utf-8")
    return re.findall(r'^\s*"([^"]+)",\s*$', body[body.index("Keys ="):], re.M)


def rule(n, url):
    """Picture n's rule: (n, text), so the stylesheet can list them in number order."""
    return n, f'.th{n // BASE}.tl{n % BASE}\n{{\n\tbackground-image: url( "{url}" );\n}}\n'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", help="folder with the site's <id>.webp thumbnails (default: download them from the site)")
    a = ap.parse_args()
    maps = json.loads(MAPS.read_text(encoding="utf-8"))
    OUT.mkdir(exist_ok=True)
    keys = known_keys()
    number = {k: i for i, k in enumerate(keys)}
    if len(number) != len(keys):
        sys.exit(f"{LIST.name} names a map twice - fix it by hand before anything else")
    rules, made, kept, total, added, debarred = [], 0, 0, 0, 0, []

    def numbered(key):
        nonlocal added
        if key not in number:
            number[key] = len(keys)
            keys.append(key)
            added += 1
        return number[key]

    for m in sorted(maps, key=lambda m: m["rank"]):
        jpg = OUT / f"{m['id']}.jpg"
        if not jpg.exists():
            try:
                im = fetch(m["id"], a.src).convert("RGB")
            except Exception as e:  # a map without a picture keeps the card's plain backdrop
                print(f"no thumbnail for #{m['rank']} {m['id']}: {e}", file=sys.stderr)
                continue
            if im.size != SIZE:
                im = im.resize(SIZE, Image.LANCZOS)
            im, box = debar(im)
            if box:
                debarred.append(f"#{m['rank']} {m['id']} kept {box}")
            im.save(jpg, "JPEG", quality=QUALITY, optimize=True, progressive=False)
            made += 1
        else:
            kept += 1
        total += jpg.stat().st_size
        rules.append(rule(numbered(str(m["id"])), f"file://{{images}}/custom_game/gungame_hud/thumbs/{m['id']}.jpg"))
    for name in OFFICIAL:
        rules.append(rule(numbered(name), f"s2r://panorama/images/map_icons/screenshots/360p/{name}_png.vtex"))
    if len(keys) > BASE * BASE:
        sys.exit(f"{len(keys)} pictures do not fit {BASE} x {BASE} numbers: raise BASE here and in HudThumbs")
    rules.sort()
    NL = "\n"
    CSS.write_text(HEAD + NL + NL.join(r for _, r in rules), encoding="utf-8", newline=NL)
    LIST.write_text(LIST_HEAD + "".join(f'        "{k}",{NL}' for k in keys) + "    ];" + NL + "}" + NL, encoding="utf-8", newline=NL)
    classes = len({n // BASE for n, _ in rules}) + len({n % BASE for n, _ in rules})
    if debarred:
        (OUT / "debarred.txt").write_text(NL.join(debarred) + NL, encoding="utf-8")
        print(f"black bars cut off {len(debarred)} new picture(s): {OUT / 'debarred.txt'}")
    print(f"{len(rules) - len(OFFICIAL)} map thumbnails ({made} new, {kept} kept), {total / 1e6:.1f} MB of JPEG; {len(OFFICIAL)} official maps; "
          f"{len(keys)} numbered ({added} new); {classes} class names; {CSS.name} and {LIST.name} written")


if __name__ == "__main__":
    main()
