#!/usr/bin/env python3
"""The map list site: render the committed pages with this deployment's addresses, check for drift, ship, confirm it is live.

    python deploy/site.py render [--out DIR]          the committed site/index.html and more.html, filled in (default: .work/site)
    python deploy/site.py ship [--img-src DIR]        what would change on the site (drift check included)
    python deploy/site.py ship --apply [--img-src DIR]   ship: new thumbnails first, then more.html, then index.html; then the
                                                      live site must serve exactly those bytes within a minute

The pages in the repository carry placeholders - overstrike.example.com for the site and play.example.com for the address
players connect to - so the deployment's own addresses never enter the repository. Rendering puts site.url's host and
site.connect in their place and re-stamps index.html's link to more.html (the ?v= of pagesplit.py: the first 12 hex of
more.html's SHA-256). Drift: the page live now must be the rendering of one of the last 50 commits of site/index.html;
anything else was edited on the server, and shipping over it is refused (--force overrides, after you have looked).
Thumbnails (img/<workshop id>.webp, other authors' Workshop previews) are never in the repository: --img-src names a local
folder holding the ones the site lacks.
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

SITE_PLACEHOLDER, CONNECT_PLACEHOLDER = "overstrike.example.com", "play.example.com"
RE_MORE = re.compile(r'data-src="more\.html\?v=[0-9a-f]{12}"')


def render(index, more, cfg):
    host, connect = cfg.site_host(), cfg.get("site.connect", required=True)
    fill = lambda t: t.replace(CONNECT_PLACEHOLDER, connect).replace(SITE_PLACEHOLDER, host)
    more = fill(more)
    digest = hashlib.sha256(more.encode("utf-8")).hexdigest()[:12]
    index, n = RE_MORE.subn(f'data-src="more.html?v={digest}"', fill(index))
    if n != 1:
        os2.fail(f"index.html names more.html {n} times, not once")
    for name, text in (("index.html", index), ("more.html", more)):
        if "example.com" in text:
            os2.fail(f"{name} still holds an example.com placeholder after rendering")
    return index, more


def committed(commit, name):
    r = subprocess.run(["git", "-C", os2.REPO, "show", f"{commit}:site/{name}"], capture_output=True)
    return r.stdout.decode("utf-8") if r.returncode == 0 else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["render", "ship"])
    ap.add_argument("--out", default=os.path.join(os2.REPO, ".work", "site"))
    ap.add_argument("--img-src")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--config")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    cfg = os2.Config(args.config)
    commit = os2.clean_tree(["site/index.html", "site/more.html"])
    index, more = render(committed("HEAD", "index.html"), committed("HEAD", "more.html"), cfg)
    if args.command == "render":
        os.makedirs(args.out, exist_ok=True)
        for name, text in (("index.html", index), ("more.html", more)):
            with open(os.path.join(args.out, name), "w", encoding="utf-8", newline="") as fh:
                fh.write(text)
        os2.log(f"rendered site/ at {commit} into {args.out}")
        return 0

    dest = cfg.get("site.dir", required=True)
    live = os.path.join(dest, "index.html")
    live_text = open(live, encoding="utf-8", newline="").read() if os.path.exists(live) else None
    if live_text == index:
        os2.log("the site already serves this commit's pages")
    elif live_text is not None:
        match = None
        for c in subprocess.run(["git", "-C", os2.REPO, "log", "--format=%H", "-n", "50", "--", "site/index.html"],
                                capture_output=True, text=True).stdout.split():
            i, m = committed(c, "index.html"), committed(c, "more.html")
            if i and m and render(i, m, cfg)[0] == live_text:
                match = c
                break
        if match:
            os2.log(f"drift check: the live page is the rendering of {match[:12]}")
        elif not args.force:
            os2.fail("DRIFT: the live index.html is not the rendering of any of the last 50 commits - look at it, then --force")
        else:
            os2.log("DRIFT overridden (--force)")
    # thumbnails the pages show and the site lacks
    img_dir = cfg.get("site.img", "") or os.path.join(dest, "img")
    ids = set(re.findall(r'<img src="img/(\d+)\.webp"', index + more))
    have = set(os.listdir(img_dir)) if os.path.isdir(img_dir) else set()
    lacking = sorted(i for i in ids if i + ".webp" not in have)
    os2.log(f"thumbnails: {len(ids)} on the pages, {len(lacking)} not on the site")
    if lacking and args.img_src:
        for i in lacking:
            src = os.path.join(args.img_src, i + ".webp")
            if os.path.exists(src):
                os2.log(f"  {i}.webp: {os2.copy_verified(src, os.path.join(img_dir, i + '.webp'), dry=not args.apply)}")
            else:
                os2.log(f"  {i}.webp: not in {args.img_src} - the row shows no picture")
    if not args.apply:
        os2.log("dry run - add --apply to ship")
        return 0
    os.makedirs(args.out, exist_ok=True)
    for name, text in (("more.html", more), ("index.html", index)):  # more.html first: index.html names it by its hash
        staged = os.path.join(args.out, name)
        with open(staged, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
        os2.log(f"{name}: {os2.copy_verified(staged, os.path.join(dest, name))}")
    want = hashlib.sha256(index.encode("utf-8")).hexdigest()
    url = cfg.get("site.url", required=True).rstrip("/") + "/"
    for _ in range(6):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                if hashlib.sha256(r.read()).hexdigest() == want:
                    os2.log(f"LIVE: {url} serves the new index.html")
                    return 0
        except Exception as e:
            os2.log(f"  {e}")
        time.sleep(10)
    os2.fail("the site does not serve the new page after a minute")


if __name__ == "__main__":
    sys.exit(main())
