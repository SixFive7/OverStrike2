"""The menu's map thumbnails: one small JPEG per map, the stylesheet that gives each one its class, and the list that numbers them.

    python thumbs.py [--ahead FILE] [--workers 4]

The pictures are the maps' own Workshop previews, from Steam's image CDN: the preview_url the public Web API gives for an item
(ISteamRemoteStorage/GetPublishedFileDetails, no key), or the preview an --ahead file already names. Reads bridge/maps.json,
bridge/Modes.cs (the official maps of every mode) and, with --ahead, a JSON array of {"id", "preview"}: maps that are not listed yet
but will be, most popular first, so their pictures can ship before they are listed. Writes:

  hud/thumbs/<id>.jpg                 336x189, quality 82 (about 12 KB each) - gitignored, other authors' pictures; build.ps1 copies them
                                      into the addon, where the resource compiler stores each JPEG as it is (VTEX JPEG_DXT5).
                                      Black bars baked into a Workshop preview (letterbox or pillarbox) are filled with a blurred copy
                                      of the picture first (debar), since 2026-10-21: about a fifth of the maps had them. A JPEG that
                                      exists is kept as it is: build.ps1 holds the ones the last release shipped to their bytes
  bridge/HudThumbList.cs              committed: every map that ever had a picture, in the order they got one. A map's place in it is
                                      its picture's number, for good: new maps go at the end and nothing moves, so a player whose CS2
                                      still holds an older addon sees no picture for a newer map - never another map's picture
  hud/styles/gungame_hud_thumbs.css   committed: one rule per picture, ".th<n / 64>.tl<n % 64>" - two classes out of a small set
                                      instead of one per map, because the game interns every class name the server ever sets on the
                                      layout entity and stops at 1,024 of them. A listed map's rule points at its JPEG, an official
                                      map's at the game's own 360p screenshot (nothing to ship)

A map listed after the release that ships its picture has no number: its card keeps the plain backdrop, and the button on it shows the
Workshop preview in the game's own message panel instead (HudPreview in bridge/HudBrowser.cs). HudThumbs in bridge/HudBrowser.cs makes the
same class names out of HudThumbList; the bridge's --selftest checks that every map has its rule and that every rule is its own map's.
"""
import argparse
import io
import json
import math
import re
import sys
import threading
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter

HERE = Path(__file__).resolve().parent
MAPS = HERE.parent / "bridge" / "maps.json"
MODES = HERE.parent / "bridge" / "Modes.cs"
OUT = HERE / "thumbs"
RELEASE = re.search(r'Release = "(\w+)"', (HERE.parent / "bridge" / "Hud.cs").read_text(encoding="utf-8")).group(1)  # Hud.Release
CSS = HERE / "styles" / f"gungame_hud_thumbs_{RELEASE}.css"
LIST = HERE.parent / "bridge" / "HudThumbList.cs"
BASE = 64  # HudThumbs.Base: picture n has the classes th<n / 64> and tl<n % 64>
MAX_HIGH = 256  # HudThumbs.MaxHigh: th0 ... th255, 16,384 pictures, tested in a real client on 2026-11-01; 320 class names at the very most
SIZE = (336, 189)
QUALITY = 82
DETAILS = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
SIZING = {"imw", "imh", "ima", "impolicy", "imcolor", "imsharp", "letterbox"}  # Steam's sizing query: without it, the original upload
AGENT = {"User-Agent": "OverStrike2-thumbs/1.0"}
MAX_BYTES = 32 * 1024 * 1024

HEAD = """/*
  Over Strike 2 - the menu's map thumbnails, written by hud/thumbs.py: do not edit by hand.

  A card's picture panel gets two classes, th<n / 64> and tl<n % 64>, where n is the map's place in bridge/HudThumbList.cs
  (HudThumbs in bridge/HudBrowser.cs). Two classes out of a small set rather than one per map: the game counts every class name
  the server ever sets on the layout entity and takes no more than 1,024 of them.

  A map's picture is its Workshop preview, a JPEG in the addon (file://{images}/... makes the resource compiler build it, storing
  the JPEG as it is); an official map's is the game's own 360p screenshot, so it costs the addon nothing. Maps that are not listed yet
  have theirs here too, so a map the nightly sync lists later has its picture without a new HUD.
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


def original(url):
    """The preview's own upload: Steam's sizing query dropped, anything else in it kept."""
    split = urllib.parse.urlsplit(url or "")
    if split.scheme not in ("http", "https") or not split.netloc:
        return ""
    query = [(k, v) for k, v in urllib.parse.parse_qsl(split.query, keep_blank_values=True) if k.lower() not in SIZING]
    return urllib.parse.urlunsplit((split.scheme, split.netloc, split.path, urllib.parse.urlencode(query), ""))


def cover(data):
    """The upload scaled to cover 336x189 and cut from the middle: never letterboxed, never squashed."""
    with Image.open(io.BytesIO(data)) as src:
        src.load()
        if src.mode in ("RGBA", "LA") or (src.mode == "P" and "transparency" in src.info):
            rgba = src.convert("RGBA")
            img = Image.new("RGB", rgba.size, (0, 0, 0))
            img.paste(rgba, mask=rgba.split()[-1])
        else:
            img = src.convert("RGB")
    scale = max(SIZE[0] / img.width, SIZE[1] / img.height)
    w, h = max(SIZE[0], int(math.ceil(img.width * scale))), max(SIZE[1], int(math.ceil(img.height * scale)))
    img = img.resize((w, h), Image.LANCZOS)
    left, top = (w - SIZE[0]) // 2, (h - SIZE[1]) // 2
    return img.crop((left, top, left + SIZE[0], top + SIZE[1]))


def get(url):
    req = urllib.request.Request(url, headers=AGENT)
    with urllib.request.urlopen(req, timeout=30) as r:
        data = r.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("a preview larger than 32 MB")
    return data


def previews_from_steam(ids):
    """Each id's preview_url from the Web API: 100 ids a call, one call at a time."""
    found = {}
    ids = list(ids)
    for i in range(0, len(ids), 100):
        batch = ids[i:i + 100]
        form = {"itemcount": str(len(batch)), **{f"publishedfileids[{k}]": v for k, v in enumerate(batch)}}
        req = urllib.request.Request(DETAILS, data=urllib.parse.urlencode(form).encode(), headers=AGENT)
        with urllib.request.urlopen(req, timeout=30) as r:
            details = json.load(r)["response"]["publishedfiledetails"]
        for d in details:
            if d.get("preview_url"):
                found[str(d["publishedfileid"])] = d["preview_url"]
        time.sleep(1)
    return found


def known_keys():
    """The list as committed: the keys in their order (empty when there is none yet)."""
    if not LIST.exists():
        return []
    body = LIST.read_text(encoding="utf-8")
    return re.findall(r'^\s*"([^"]+)",\s*$', body[body.index("Keys ="):], re.M)


def officials():
    """Every official map of every mode, from Modes.cs: the game's own screenshot is its picture."""
    names = re.findall(r'"((?:ar|de|cs)_[a-z0-9_]+)"', MODES.read_text(encoding="utf-8"))
    return list(dict.fromkeys(names))


def rule(n, url):
    """Picture n's rule: (n, text), so the stylesheet can list them in number order."""
    return n, f'.th{n // BASE}.tl{n % BASE}\n{{\n\tbackground-image: url( "{url}" );\n}}\n'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ahead", help="a JSON array of {id, preview}: maps not listed yet, most popular first")
    ap.add_argument("--workers", type=int, default=4, help="downloads at a time from Steam's image CDN")
    a = ap.parse_args()
    maps = json.loads(MAPS.read_text(encoding="utf-8"))
    ahead = json.loads(Path(a.ahead).read_text(encoding="utf-8")) if a.ahead else []
    OUT.mkdir(exist_ok=True)
    keys = known_keys()
    number = {k: i for i, k in enumerate(keys)}
    if len(number) != len(keys):
        sys.exit(f"{LIST.name} names a map twice - fix it by hand before anything else")
    official = officials()
    wanted = [str(m["id"]) for m in sorted(maps, key=lambda m: int(m["rank"]))] + [str(e["id"]) for e in ahead]
    url_of = {str(e["id"]): e.get("preview") or "" for e in ahead}
    # Every numbered map keeps its picture; every map wanted gets one. Only a JPEG that is missing is fetched.
    workshop = list(dict.fromkeys([k for k in keys if k.isdigit()] + wanted))
    missing = [k for k in workshop if not (OUT / f"{k}.jpg").exists()]
    ask = [k for k in missing if not url_of.get(k)]
    if ask:
        print(f"asking the Web API for {len(ask)} preview(s)", flush=True)
        url_of.update(previews_from_steam(ask))
    lock = threading.Lock()
    made, failed, debarred = [], [], []
    started = time.time()

    def make(key):
        url = original(url_of.get(key, ""))
        try:
            if not url:
                raise ValueError("no preview url")
            for attempt in range(3):
                try:
                    data = get(url)
                    break
                except Exception:
                    if attempt == 2:
                        raise
                    time.sleep(2 + attempt * 3)
            im, box = debar(cover(data))
            buf = io.BytesIO()
            im.save(buf, "JPEG", quality=QUALITY, optimize=True, progressive=False)
            (OUT / f"{key}.jpg").write_bytes(buf.getvalue())
            with lock:
                made.append(key)
                if box:
                    debarred.append(f"{key} kept {box}")
                if len(made) % 250 == 0:
                    print(f"  {len(made)} of {len(missing)} in {time.time() - started:.0f} s", flush=True)
        except Exception as e:  # a map without a picture keeps the card's plain backdrop and the picture on request
            with lock:
                failed.append(key)
            print(f"no picture for {key}: {e}", file=sys.stderr, flush=True)

    if missing:
        print(f"fetching {len(missing)} preview(s) from Steam's image CDN, {a.workers} at a time", flush=True)
        with ThreadPoolExecutor(max_workers=max(1, a.workers)) as pool:
            list(pool.map(make, missing))
    if failed:  # an author changed the preview since the address was noted: ask Steam for the one it has now, once
        again, failed[:] = list(failed), []
        print(f"asking the Web API again for {len(again)} preview(s) that failed", flush=True)
        url_of.update(previews_from_steam(again))
        for key in again:
            make(key)
    added = 0
    for key in official + workshop:
        if key not in number and (not key.isdigit() or (OUT / f"{key}.jpg").exists()):
            number[key] = len(keys)
            keys.append(key)
            added += 1
    if len(keys) > BASE * MAX_HIGH:
        sys.exit(f"{len(keys)} pictures do not fit {BASE} x {MAX_HIGH} numbers: raise MAX_HIGH here and in HudThumbs")
    rules, total = [], 0
    for key, n in number.items():
        if not key.isdigit():
            rules.append(rule(n, f"s2r://panorama/images/map_icons/screenshots/360p/{key}_png.vtex"))
        elif (OUT / f"{key}.jpg").exists():
            total += (OUT / f"{key}.jpg").stat().st_size
            rules.append(rule(n, f"file://{{images}}/custom_game/gungame_hud/thumbs/{key}.jpg"))
    rules.sort()
    NL = "\n"
    CSS.write_text(HEAD + NL + NL.join(r for _, r in rules), encoding="utf-8", newline=NL)
    LIST.write_text(LIST_HEAD + "".join(f'        "{k}",{NL}' for k in keys) + "    ];" + NL + "}" + NL, encoding="utf-8", newline=NL)
    classes = len({n // BASE for n, _ in rules}) + len({n % BASE for n, _ in rules})
    if debarred:
        with open(OUT / "debarred.txt", "a", encoding="utf-8", newline=NL) as f:
            f.write(NL.join(debarred) + NL)
        print(f"black bars filled on {len(debarred)} new picture(s): {OUT / 'debarred.txt'}")
    print(f"{len(rules)} rules: {sum(1 for k in number if k.isdigit() and (OUT / f'{k}.jpg').exists())} JPEGs, {total / 1e6:.1f} MB; {len(made)} new, {len(failed)} failed "
          f"({', '.join(failed[:8])}); {sum(1 for k in number if not k.isdigit())} official; {len(keys)} numbered ({added} new); {classes} class names; {CSS.name} and {LIST.name} written")


if __name__ == "__main__":
    main()
