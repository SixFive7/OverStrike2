"""Offline test of publish.py's retake rule, on made-up frames in a scratch copy of the docs: a first publish writes every picture, a retake of the
same frames keeps every committed byte, a retake with one changed word writes that one picture alone, a clock that ticked changes nothing, the gallery
links its pictures and carries none, and a picture no longer used goes with its allow.txt line. No server, no client, no git; Pillow.

    python -I tools/client/docshots/test_publish.py
"""
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import docshots as d  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

work = d.WORK
work.mkdir(parents=True, exist_ok=True)
tmp = Path(tempfile.mkdtemp(prefix="publish-sim-", dir=str(work)))
repo, res = tmp / "repo", tmp / "run" / "1920x1080"
(repo / "tools" / "scan").mkdir(parents=True)
(repo / "docs" / "hud").mkdir(parents=True)
res.mkdir(parents=True)
(repo / "README.md").write_text("# x\n<!-- hud-index:begin -->\n| HELP, Help | where | page |\n<!-- hud-index:end -->\n", encoding="utf-8")
(repo / "tools" / "scan" / "allow.txt").write_text("# allow\nbinary docs/hud/img/old.jpg 00  # a picture of a run before\n", encoding="utf-8")
rows = {r.id: r for r in d.ROWS}
W, H = 1920, 1080
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    fails += 0 if ok else 1


BASE = Image.effect_noise((W, H), 25).convert("RGB")     # the game world behind: one made-up picture for every frame of this test


def frame(seed, word=None, clock=None):
    """A made-up frame: noise, the same for a seed; a word painted in the window, or the idle bar's clock painted at a length."""
    from random import Random
    rnd = Random(seed)
    im = BASE.copy()
    dr = ImageDraw.Draw(im)
    for _ in range(40):   # some "HUD" for the frame to be about
        x, y = rnd.randrange(360, 1500), rnd.randrange(150, 900)
        dr.rectangle((x, y, x + 60, y + 12), fill=(233, 180, 76))
    if word:
        dr.rectangle(word, fill=(250, 250, 250))
    if clock:
        b = d.place(d.V_IDLE, W, H)
        dr.rectangle((b[0] + 30, b[1] + 8, b[0] + 30 + clock, b[1] + 16), fill=(233, 180, 76))
    return im


def run(frames, extra=()):
    """One docshots run made of these frames (row id -> image, with a crop where the row has one), then publish.py on it."""
    shutil.rmtree(res)
    res.mkdir(parents=True)
    man_rows = {}
    for rid, im in frames.items():
        im.save(res / f"{rid}.png")
        crop = ""
        if rows[rid].crop:
            im.crop(d.place(rows[rid].crop, W, H)).save(res / f"{rid}-crop.png")
            crop = f"{rid}-crop.png"
        man_rows[rid] = {"frames": [{"file": f"{rid}.png", "crop": crop}]}
    (res / "manifest.json").write_text(json.dumps({"res": "1920x1080", "commit": "test", "content": "v0", "plugin": "0", "taken": "test", "rows": man_rows}), encoding="utf-8")
    (res / "reviewed.txt").write_text("\n".join(x for g in man_rows.values() for f in g["frames"] for x in (f["file"], f["crop"]) if x), encoding="utf-8")
    r = subprocess.run([sys.executable, "-I", str(HERE / "publish.py"), "--out", str(tmp / "run"), "--repo", str(repo), *extra], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=600)
    if r.returncode:
        print(r.stdout[-1500:], r.stderr[-1500:])
    return r.returncode, r.stdout


def hashes():
    return {p.relative_to(repo).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in (repo / "docs" / "hud" / "img").rglob("*.jpg")}


picked = ("page-help", "join-tagline", "vote-open")   # a page frame, a message with a crop on a page, a big vote window
first = {rid: frame(i) for i, rid in enumerate(picked)}
code, out = run(first)
h1 = hashes()
check(code == 0 and "docs/hud/img/page-help.jpg" in h1 and "docs/hud/img/gallery/join-tagline-look.jpg" in h1 and "docs/hud/img/gallery/join-tagline-where.jpg" in h1,
      f"a first publish writes the pages' pictures and the gallery's: {sorted(h1)}")
gallery = (repo / "docs" / "hud" / "messages.html").read_text(encoding="utf-8")
check("data:" not in gallery and 'src="img/gallery/join-tagline-look.jpg"' in gallery and 'src="img/gallery/join-tagline-where.jpg"' in gallery,
      "the gallery links its pictures by relative path and carries none")
allow = (repo / "tools" / "scan" / "allow.txt").read_text(encoding="utf-8")
check(all(f"binary {p} {v}" in allow for p, v in h1.items()) and "old.jpg" not in allow, "allow.txt holds every picture by its hash, and no more the one that is gone")

code, out = run({rid: frame(i, clock=(40 if rid == "page-help" else None)) for i, rid in enumerate(picked)})
check(code == 0 and hashes() == h1 and "0 written anew, 0 new" in out, f"a retake of the same frames, a clock ticked: every committed byte kept ({out.strip().splitlines()[-2][:120]})")

word = (900, 500, 960, 512)
code, out = run({rid: frame(i, word=word if rid == "page-help" else None) for i, rid in enumerate(picked)})
h3 = hashes()
check(code == 0 and h3["docs/hud/img/page-help.jpg"] != h1["docs/hud/img/page-help.jpg"] and all(h3[k] == h1[k] for k in h1 if k != "docs/hud/img/page-help.jpg"),
      "a changed word: that picture written anew, every other one kept")

code, out = run({rid: frame(i) for i, rid in enumerate(picked) if rid != "vote-open"})
h4 = hashes()
check(code == 0 and "docs/hud/img/vote-open.jpg" not in h4 and "vote-open.jpg" not in (repo / "tools" / "scan" / "allow.txt").read_text(encoding="utf-8"),
      "a picture no page uses any more is removed, its allow.txt line with it")

code, out = run({rid: frame(i) for i, rid in enumerate(picked)}, ["--all"])
check(code == 0 and "0 kept as committed" in out, "--all writes every picture anew")

# a message over the game: the top line's pill, retaken over another game - its look and where kept; a changed word in it - both written anew
def pill_frame(world, word=None):
    im = world.copy()
    dr = ImageDraw.Draw(im)
    dr.rectangle((730, 150, 1190, 186), fill=(37, 43, 45))
    dr.rectangle((738, 158, 800, 178), fill=(233, 182, 79))
    dr.rectangle((820, 165, 1150, 172), fill=(235, 235, 235))
    if word:
        dr.rectangle(word, fill=(37, 43, 45))
    return im


game_a, game_b = Image.effect_noise((W, H), 25).convert("RGB"), Image.effect_noise((W, H), 25).convert("RGB")
code, out = run({"top-plain": pill_frame(game_a)})
t1 = hashes()
code, out = run({"top-plain": pill_frame(game_b)})
t2 = hashes()
look, where = "docs/hud/img/gallery/top-plain-look.jpg", "docs/hud/img/gallery/top-plain-where.jpg"
check(code == 0 and look in t1 and t2[look] == t1[look] and t2[where] == t1[where], "a pill retaken over another game: its look and where keep their committed bytes")
code, out = run({"top-plain": pill_frame(game_b, word=(1000, 165, 1040, 172))})
t3 = hashes()
check(code == 0 and t3[look] != t1[look] and t3[where] != t1[where], "a changed word in the pill: its look and its where written anew")

shutil.rmtree(tmp)
print("failures:", fails)
sys.exit(1 if fails else 0)
