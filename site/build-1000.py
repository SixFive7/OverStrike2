#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build-1000.py -- build the Gun Game Rotation page from a scraped maps-raw.json.

Produces, in --out:

    index.html      the page. Design is byte-identical to the hand-built 100-map
                    original (same <style>, masthead, toolbar, row grid, footer
                    and JS); only the data and the screenshot <img src> differ.
    img/<id>.webp   one 336x189 cover-cropped WebP per map that had a usable
                    preview. Screenshots are files now, not inlined data: URIs.
    maps.json       [{"rank":N,"id":N,"name":"...","sizeMb":N}, ...] -- the table
                    the CS2 bridge reads with a source-generated JSON context.
                    Key names and types are a contract: do not "improve" them.
                    sizeMb is rounded and floored at 1, because the bridge
                    prints it raw and "(0 MB)" in game chat reads like a bug.
                    The page itself is not floored: it still shows 0.1 MB and
                    still bands such a map as 'tiny'.

Input entries look like:

    {"rank":1,"id":3084291314,"name":"AIM Map","subscriptions":1409115,
     "sizeMb":112,"updated":"2026-07-06","tags":["Classic","Deathmatch"],
     "description":"...","descriptionFull":"...","author":"...",
     "preview":"https://..."}

sizeMb may be a float below 100; description, descriptionFull, author and
preview may be "" or absent. Descriptions and authors are
not published: a row shows the title, tags and size, and the filter box searches titles, tags and IDs.

Usage:
    python build-1000.py --maps maps-raw.json --out dist
    python build-1000.py --maps maps-raw.json --out dist --workers 12
    python build-1000.py --maps maps-raw.json --out dist --no-download

Downloads are cached: a re-run skips any img/<id>.webp that already decodes at
336x189, so rebuilding the page after a data tweak costs no network at all.

Requires: requests, pillow.
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import html
import io
import json
import os
import math
import random
import re
import sys
import time
from pathlib import Path
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

try:
    import requests
except ImportError:  # pragma: no cover
    sys.exit("build-1000.py needs 'requests'  ->  pip install requests pillow")

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    sys.exit("build-1000.py needs 'pillow'  ->  pip install requests pillow")


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


# --------------------------------------------------------------------------
# constants
# --------------------------------------------------------------------------

SHOT_W, SHOT_H = 336, 189          # 16:9, matches the width/height attrs in the markup
WEBP_QUALITY = 80
MAX_IMAGE_BYTES = 32 * 1024 * 1024  # refuse a preview larger than this

# A plain browser UA: Valve's CDN 403s some default library UAs.
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
)

# Steam preview URLs carry sizing/letterbox params that return a padded thumbnail.
# Dropping exactly these gives the original upload; anything else in the query
# (tokens, signatures) is preserved.
SIZING_QS_KEYS = {
    "imw", "imh", "ima", "impolicy", "imcolor", "imsharp",
    "letterbox", "allowanimated", "imv",
}

# Size bands. Thresholds match the original page and the chip labels in the
# toolbar: <25 / 25-120 / 120-400 / 400+ MB.
BANDS = ((25, "tiny"), (120, "small"), (400, "mid"))

# Generic Steam tags the original page kept in the search index but did not
# render as chips (every row carried them, so they were pure noise).
TAG_STOPLIST = {"map", "cs2"}

# Displayed descriptions are one-line summaries; the original's longest was 181
# chars. The hidden search corpus carries the full description instead, capped
# because it is the only unbounded field and every row pays for it.
DESC_MAX = 180
SEARCH_DESC_MAX = 800

# maps.json only: never hand the bridge a 0, which it would print as "(0 MB)".
BRIDGE_MIN_MB = 1

# Control characters, and the bidi overrides/isolates that would otherwise leak
# out of a row and reorder the rest of the page. RTL *letters* are kept.
_CTRL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]")
_BIDI_RE = re.compile("[\u202a-\u202e\u2066-\u2069]")
_WS_RE = re.compile(r"\s+")


# --------------------------------------------------------------------------
# page template -- lifted verbatim from the deployed gungame/index.html
# --------------------------------------------------------------------------

HEAD = """\
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>@@SERVER@@</title>
<meta name="viewport" content="width=device-width, initial-scale=1" />
<meta name="description" content="The @@RANKED@@ most-subscribed Counter-Strike 2 Arms Race workshop maps@@EXTRA_META@@, with verified host_workshop_map console commands." />"""

STYLE = """\
<style>
  :root {
    --ground:      #ECEDE9;
    --surface:     #FFFFFF;
    --surface-2:   #F4F5F1;
    --line:        #D6D9D2;
    --line-soft:   #E4E7E0;
    --text:        #171A1C;
    --text-dim:    #5C656B;
    --text-faint:  #8A9299;
    --accent:      #B4560A;
    --accent-soft: #F6E4D2;
    --tiny:        #2F7D51;
    --small:       #4A7C8C;
    --mid:         #A8721A;
    --large:       #A8412C;
    --shadow:      0 1px 2px rgba(20,23,26,.06), 0 4px 14px rgba(20,23,26,.05);

    --f-display: "Bahnschrift", "DIN Alternate", "Haettenschweiler", "Franklin Gothic Medium", system-ui, sans-serif;
    --f-body: "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Helvetica Neue", sans-serif;
    --f-mono: "Cascadia Mono", "Cascadia Code", Consolas, "SF Mono", ui-monospace, monospace;

    --gap: 1.5rem;
    --radius: 3px;
  }

  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --ground:      #131619;
      --surface:     #1B2025;
      --surface-2:   #222930;
      --line:        #2E373F;
      --line-soft:   #252D34;
      --text:        #E3E7EA;
      --text-dim:    #939DA5;
      --text-faint:  #6B757D;
      --accent:      #F0902F;
      --accent-soft: #3A2A16;
      --tiny:        #5FBE87;
      --small:       #7FB6C9;
      --mid:         #E0AC4C;
      --large:       #E07A63;
      --shadow:      0 1px 2px rgba(0,0,0,.4), 0 6px 18px rgba(0,0,0,.3);
    }
  }

  :root[data-theme="dark"] {
    --ground:      #131619;
    --surface:     #1B2025;
    --surface-2:   #222930;
    --line:        #2E373F;
    --line-soft:   #252D34;
    --text:        #E3E7EA;
    --text-dim:    #939DA5;
    --text-faint:  #6B757D;
    --accent:      #F0902F;
    --accent-soft: #3A2A16;
    --tiny:        #5FBE87;
    --small:       #7FB6C9;
    --mid:         #E0AC4C;
    --large:       #E07A63;
    --shadow:      0 1px 2px rgba(0,0,0,.4), 0 6px 18px rgba(0,0,0,.3);
  }

  * { box-sizing: border-box; }

  body {
    margin: 0;
    background: var(--ground);
    color: var(--text);
    font-family: var(--f-body);
    font-size: 16px;
    line-height: 1.5;
    -webkit-font-smoothing: antialiased;
  }

  .wrap { max-width: 1180px; margin: 0 auto; padding: 0 1.25rem 5rem; }

  /* ---------- masthead ---------- */
  header.mast { padding: 3.5rem 0 2rem; border-bottom: 2px solid var(--text); }
  .eyebrow {
    font-family: var(--f-mono); font-size: .72rem; letter-spacing: .16em;
    text-transform: uppercase; color: var(--accent); margin: 0 0 .9rem;
  }
  h1 {
    font-family: var(--f-display); font-weight: 600;
    font-size: clamp(2.6rem, 7vw, 4.75rem); line-height: .95;
    letter-spacing: -.01em; margin: 0 0 1rem; text-wrap: balance;
  }
  .standfirst {
    max-width: 62ch; margin: 0; color: var(--text-dim); font-size: 1.06rem;
  }
  .standfirst code { font-family: var(--f-mono); font-size: .92em; color: var(--text); }

  .facts {
    display: flex; flex-wrap: wrap; gap: 0 2.75rem;
    margin: 2rem 0 0; padding: 0; list-style: none;
  }
  .facts div { display: flex; flex-direction: column; gap: .15rem; }
  .facts dt {
    font-family: var(--f-mono); font-size: .68rem; letter-spacing: .13em;
    text-transform: uppercase; color: var(--text-faint);
  }
  .facts dd {
    margin: 0; font-family: var(--f-display); font-size: 1.75rem;
    font-variant-numeric: tabular-nums; line-height: 1.1;
  }

  /* ---------- how-to ---------- */
  .howto {
    display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: 1px; background: var(--line-soft);
    border: 1px solid var(--line-soft); margin: 2.5rem 0 0;
  }
  .howto section { background: var(--surface); padding: 1.15rem 1.25rem; }
  .howto h3 {
    font-family: var(--f-mono); font-size: .7rem; letter-spacing: .13em;
    text-transform: uppercase; color: var(--text-faint);
    margin: 0 0 .55rem; font-weight: 400;
  }
  .howto p { margin: 0; font-size: .89rem; color: var(--text-dim); }
  .howto p + p { margin-top: .5rem; }
  .howto code {
    font-family: var(--f-mono); font-size: .84em;
    background: var(--surface-2); padding: .1em .35em; border-radius: 2px; color: var(--text);
  }
  .warn { border-left: 3px solid var(--large); }
  .warn h3 { color: var(--large); }

  /* ---------- toolbar ---------- */
  .toolbar {
    position: sticky; top: 0; z-index: 20;
    display: flex; flex-wrap: wrap; align-items: center; gap: .6rem;
    padding: .85rem 0; margin: 2.5rem 0 0;
    background: color-mix(in srgb, var(--ground) 92%, transparent);
    backdrop-filter: blur(10px);
    border-bottom: 1px solid var(--line);
  }
  #q {
    flex: 1 1 15rem; min-width: 0;
    font-family: var(--f-mono); font-size: .88rem;
    padding: .55rem .8rem; color: var(--text);
    background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius);
  }
  #q::placeholder { color: var(--text-faint); }
  #q:focus-visible, .chip:focus-visible, .cmd:focus-visible, .shot:focus-visible {
    outline: 2px solid var(--accent); outline-offset: 2px;
  }
  .chip {
    font-family: var(--f-mono); font-size: .74rem; letter-spacing: .06em;
    text-transform: uppercase; cursor: pointer; white-space: nowrap;
    padding: .5rem .7rem; color: var(--text-dim);
    background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius);
  }
  .chip:hover { color: var(--text); border-color: var(--text-faint); }
  .chip[aria-pressed="true"] {
    background: var(--accent); border-color: var(--accent); color: var(--ground); font-weight: 600;
  }
  .shown {
    font-family: var(--f-mono); font-size: .74rem; color: var(--text-faint);
    font-variant-numeric: tabular-nums; margin-left: auto; white-space: nowrap;
  }

  /* ---------- rows ---------- */
  ol.list { list-style: none; margin: 0; padding: 0; }
  .row {
    display: grid;
    grid-template-columns: 3.25rem 168px minmax(0, 1fr) auto;
    grid-template-areas: "rank shot meta stats" ". cmd cmd cmd";
    gap: .85rem var(--gap);
    align-items: start;
    padding: 1.35rem 0;
    border-bottom: 1px solid var(--line-soft);
  }
  .row:hover { background: color-mix(in srgb, var(--surface) 55%, transparent); }
  .row.hidden { display: none; }

  .rank { grid-area: rank; }
  .rank span {
    display: block; font-family: var(--f-display);
    font-size: 1.65rem; line-height: 1; color: var(--text-faint);
    font-variant-numeric: tabular-nums; text-align: right; padding-top: .1rem;
  }
  .row:nth-child(-n+3) .rank span { color: var(--accent); }

  .shot { grid-area: shot; display: block; border: 1px solid var(--line); background: var(--surface-2); }
  .shot img {
    display: block; width: 100%; height: auto; aspect-ratio: 16 / 9; object-fit: cover;
  }
  .shot:hover { border-color: var(--accent); }
  .shot.missing {
    display: flex; align-items: center; justify-content: center;
    aspect-ratio: 16 / 9; text-align: center; text-decoration: none;
    font-family: var(--f-mono); font-size: .6rem; letter-spacing: .1em;
    text-transform: uppercase; color: var(--text-faint);
  }

  .meta { grid-area: meta; min-width: 0; }
  .name {
    font-family: var(--f-display); font-weight: 600; font-size: 1.3rem;
    line-height: 1.15; margin: 0 0 .3rem; text-wrap: balance;
  }
  .desc {
    margin: 0 0 .5rem; font-size: .86rem; line-height: 1.45; color: var(--text-dim);
    max-width: 58ch;
  }
  .tags { display: flex; flex-wrap: wrap; gap: .3rem; }
  .tag {
    font-family: var(--f-mono); font-size: .65rem; letter-spacing: .07em;
    text-transform: uppercase; padding: .16em .45em;
    color: var(--text-faint); border: 1px solid var(--line);
  }

  .stats { grid-area: stats; display: flex; gap: 1.4rem; }
  .stat { display: flex; flex-direction: column; gap: .1rem; }
  .stat dt {
    font-family: var(--f-mono); font-size: .62rem; letter-spacing: .1em;
    text-transform: uppercase; color: var(--text-faint); white-space: nowrap;
  }
  .stat dd.num {
    margin: 0; font-family: var(--f-display); font-size: 1.05rem;
    font-variant-numeric: tabular-nums; white-space: nowrap;
  }
  .size-tiny  { color: var(--tiny); }
  .size-small { color: var(--small); }
  .size-mid   { color: var(--mid); }
  .size-large { color: var(--large); }

  .cmd {
    grid-area: cmd; display: flex; align-items: center; gap: .75rem;
    width: 100%; text-align: left; cursor: pointer;
    font: inherit; padding: .5rem .7rem;
    background: var(--surface-2); color: var(--text);
    border: 1px solid var(--line); border-radius: var(--radius);
  }
  .cmd code { font-family: var(--f-mono); font-size: .87rem; color: var(--accent); }
  .cmd code.console { display: none; }
  .adv .cmd code.chat { display: none; }
  .adv .cmd code.console { display: inline; }
  .toggle {
    display: flex; align-items: center; gap: .4rem; cursor: pointer; white-space: nowrap;
    font-family: var(--f-mono); font-size: .74rem; letter-spacing: .06em;
    text-transform: uppercase; color: var(--text-dim);
  }
  .toggle input { accent-color: var(--accent); margin: 0; }
  .cmd .copy {
    margin-left: auto; font-family: var(--f-mono); font-size: .68rem;
    letter-spacing: .11em; text-transform: uppercase; color: var(--text-faint);
  }
  .cmd:hover { border-color: var(--accent); }
  .cmd:hover .copy { color: var(--accent); }
  .cmd.done { border-color: var(--tiny); }
  .cmd.done .copy { color: var(--tiny); }

  .empty {
    display: none; padding: 4rem 0; text-align: center; color: var(--text-faint);
    font-family: var(--f-mono); font-size: .85rem;
  }
  .empty.on { display: block; }

  footer {
    margin-top: 3rem; padding-top: 1.5rem; border-top: 1px solid var(--line);
    font-size: .83rem; color: var(--text-faint); max-width: 78ch;
  }
  footer code { font-family: var(--f-mono); }
  footer a { color: var(--accent); }

  @media (max-width: 900px) {
    .row {
      grid-template-columns: 2.5rem 132px minmax(0, 1fr);
      grid-template-areas: "rank shot meta" ". stats stats" ". cmd cmd";
    }
    .stats { gap: 1.1rem; }
  }
  @media (max-width: 560px) {
    .row {
      grid-template-columns: 2.25rem minmax(0, 1fr);
      grid-template-areas: "rank shot" "rank meta" "stats stats" "cmd cmd";
    }
    .shot { max-width: 100%; }
    .facts { gap: 0 1.5rem; }
  }
  @media (prefers-reduced-motion: reduce) {
    * { animation: none !important; transition: none !important; }
  }
</style>"""

MAST = """\
<div class="wrap">
  <header class="mast">
    <p class="eyebrow">Counter-Strike 2 &middot; @@SERVER@@ &middot; connect @@HOST@@</p>
    <h1>@@SERVER_HTML@@</h1>
    <p class="standfirst">The @@RANKED@@ most-subscribed Arms&nbsp;Race maps on the Steam Workshop@@EXTRA_STANDFIRST@@ that
      actually load in Counter-Strike&nbsp;2. Every ID verified live against Valve's API. Click a
      map's <code>!map</code> command to copy it, then paste it into the game chat on the
      @@SERVER_HTML@@ server: it switches after a short countdown and tells everyone who picked it.</p>
    <dl class="facts">
      <div><dt>Maps</dt><dd>@@COUNT@@</dd></div>
      <div><dt>Combined subs</dt><dd>@@SUBS@@</dd></div>
      <div><dt>Under 25 MB</dt><dd>@@TINY@@</dd></div>
      <div><dt>Verified</dt><dd>@@VERIFIED@@</dd></div>
    </dl>

    <div class="howto">
      <section>
        <h3>Where to type it</h3>
        <p>In the game chat, not the console. Press <code>Y</code>, paste, Enter. Anyone on the
          server can do it; the map changes after an 8-second countdown and the chat says who picked it.</p>
        <p>Running your own server? Flip <em>Console</em> in the toolbar to get the
          <code>host_workshop_map</code> command instead.</p>
      </section>
      <section>
        <h3>Joining</h3>
        <p><code>connect @@HOST@@</code> in the CS2 console, or look for
          <em>@@SERVER@@</em> in the community browser. No password.</p>
      </section>
      <section>
        <h3>First time on a map?</h3>
        <p>The server downloads a map the first time anyone picks it, so a 500&nbsp;MB one takes
          a minute. After that it stays cached and switches instantly.</p>
      </section>
      <section class="warn">
        <h3>If the server crashes</h3>
        <p>Roughly 1 in 20 workshop maps crashes it. It restarts by itself within a minute and the
          chat tells you which map did it. Pick another; a known crasher asks you to confirm first.</p>
      </section>
    </div>
  </header>"""

TOOLBAR = """\
  <div class="toolbar">
    <input id="q" type="search" placeholder="Filter by name, tag or workshop ID&hellip;" aria-label="Filter maps" autocomplete="off" />
    <button class="chip" type="button" data-band="all" aria-pressed="true">All</button>
    <button class="chip" type="button" data-band="tiny" aria-pressed="false">&lt;25 MB</button>
    <button class="chip" type="button" data-band="small" aria-pressed="false">25&ndash;120</button>
    <button class="chip" type="button" data-band="mid" aria-pressed="false">120&ndash;400</button>
    <button class="chip" type="button" data-band="large" aria-pressed="false">400 MB+</button>
    <label class="toggle" title="Show the server-console command instead of the map&#x27;s Workshop ID"><input type="checkbox" id="adv" /> Console</label>
    <span class="shown" id="shown">@@COUNT_PLAIN@@ of @@COUNT_PLAIN@@</span>
  </div>"""

TAIL = """\
  </ol>

  <p class="empty" id="empty">No map matches that filter.</p>

  <footer>
    <p>Ranked by current subscriber count, which measures general popularity rather than fitness for
      Arms&nbsp;Race &mdash; the aim maps at the top are used for solo warmup by people who never touch the
      mode, while purpose-built <code>ar_</code> maps sit far lower. At four players, physical size
      matters more than rank: Arms&nbsp;Race forces <code>mp_randomspawn 1</code>, so on a full
      defusal-sized map everyone spawns far apart and the ladder never advances. Filter to
      <strong>&lt;25 MB</strong> for a session that keeps moving.</p>
    <p>Every entry returned <code>result=1</code>, <code>consumer_app_id=730</code> and an empty
      <code>filename</code> field from
      <code>ISteamRemoteStorage/GetPublishedFileDetails</code>. That last check is what matters:
      CS:GO-era uploads return <code>mymaps/&lt;name&gt;.bsp</code> and will not load in CS2, and
      several of them outrank the real maps in Steam search &mdash; including a
      1.08M-subscriber &ldquo;Aim Map&rdquo; that is not the one in this list. Screenshots are
      the authors' own, mirrored from Valve's CDN.</p>
  </footer>
</div>"""

SCRIPT = """\
<script>
(function () {
  var list = document.getElementById('list');
  var rows = rowList();
  var q = document.getElementById('q');
  var shown = document.getElementById('shown');
  var empty = document.getElementById('empty');
  var chips = Array.prototype.slice.call(document.querySelectorAll('.chip'));
  var adv = document.getElementById('adv');
  adv.addEventListener('change', function () { list.classList.toggle('adv', adv.checked); });
  var band = 'all';
  // The rows after the cut can live in more.html (pagesplit.py), with #more in their place: fetched once the reader
  // nears the end of the list, filters, or clicks its button, then put where #more was. A one-file page has no #more.
  var more = document.getElementById('more');
  var loading = false;

  function rowList() {
    return Array.prototype.filter.call(list.children, function (el) { return el.classList.contains('row'); });
  }

  function loadMore() {
    var m = more;
    if (!m || loading) return;
    loading = true;
    var label = m.querySelector('span');
    label.textContent = 'Loading ' + m.dataset.count + ' more maps…';
    fetch(m.dataset.src).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.text();
    }).then(function (text) {
      m.insertAdjacentHTML('beforebegin', text);
      list.removeChild(m);
      more = null;
      rows = rowList();
      apply();
    }).catch(function () {
      loading = false;
      if (more) label.textContent = 'The other ' + m.dataset.count + ' maps did not load.';
    });
  }

  function apply() {
    var term = q.value.trim().toLowerCase();
    if (more) loadMore();
    var n = 0;
    rows.forEach(function (row) {
      var okBand = band === 'all' || row.dataset.band === band;
      var okTerm = !term || row.dataset.search.indexOf(term) !== -1;
      var show = okBand && okTerm;
      row.classList.toggle('hidden', !show);
      if (show) n++;
    });
    shown.textContent = n + ' of ' + (rows.length + (more ? +more.dataset.count : 0));
    empty.classList.toggle('on', n === 0 && !more);
  }

  if (more) {
    more.querySelector('button').addEventListener('click', loadMore);
    if ('IntersectionObserver' in window) {
      new IntersectionObserver(function (seen, watch) {
        if (seen.some(function (e) { return e.isIntersecting; })) { watch.disconnect(); loadMore(); }
      }, { rootMargin: '1500px 0px' }).observe(more);
    }
  }

  var debounce = 0;
  q.addEventListener('input', function () {
    clearTimeout(debounce);
    debounce = setTimeout(apply, 120);
  });
  chips.forEach(function (chip) {
    chip.addEventListener('click', function () {
      band = chip.dataset.band;
      chips.forEach(function (c) { c.setAttribute('aria-pressed', String(c === chip)); });
      apply();
    });
  });

  list.addEventListener('click', function (ev) {
    var btn = ev.target.closest('.cmd');
    if (!btn) return;
    var text = adv.checked ? btn.dataset.console : btn.dataset.chat;
    var label = btn.querySelector('.copy');
    var done = function () {
      label.textContent = 'Copied';
      btn.classList.add('done');
      setTimeout(function () { label.textContent = 'Copy'; btn.classList.remove('done'); }, 1400);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, fallback);
    } else { fallback(); }
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
  });
})();
</script>"""


# --------------------------------------------------------------------------
# text helpers
# --------------------------------------------------------------------------

def esc(value) -> str:
    """HTML-escape for both text nodes and double-quoted attributes."""
    return html.escape(str(value), quote=True)


def clean(value, limit: int | None = None) -> str:
    """Flatten scraped text to one safe single-line string."""
    text = _CTRL_RE.sub(" ", str(value if value is not None else ""))
    text = _BIDI_RE.sub("", text)
    text = _WS_RE.sub(" ", text).strip()
    if limit and len(text) > limit:
        cut = text[:limit].rstrip()
        space = cut.rfind(" ")
        if space > limit * 0.6:
            cut = cut[:space].rstrip()
        text = cut.rstrip(",;:.-") + "\u2026"
    return text


def fmt_int(value) -> str:
    try:
        return "{:,}".format(int(round(float(value))))
    except (TypeError, ValueError):
        return "0"


def to_mb(value) -> float:
    """sizeMb as a float; anything unparseable is 0."""
    try:
        mb = float(value)
    except (TypeError, ValueError):
        return 0.0
    return mb if mb > 0 and math.isfinite(mb) else 0.0


def fmt_size(mb: float) -> str:
    """'112 MB', '5.6 MB', '2.47 GB' -- the original page's formatting."""
    if mb <= 0:
        return "&mdash;"
    if mb >= 1000:
        return "{:.2f} GB".format(mb / 1000.0)
    if abs(mb - round(mb)) < 0.05:
        return "{:d} MB".format(int(round(mb)))
    return "{:.1f} MB".format(mb)


def band_of(mb: float) -> str:
    for edge, name in BANDS:
        if mb < edge:
            return name
    return "large"


def round_mb(mb: float) -> int:
    """Round-half-up to an int for maps.json (never banker's rounding)."""
    return int(math.floor(mb + 0.5))


def bridge_mb(mb: float) -> int:
    """sizeMb for maps.json, floored at BRIDGE_MIN_MB.

    The bridge formats it as `SizeMb >= 1000 ? "{x/1000:0.0} GB" : "{x} MB"`, so
    a rounded-down 0 would announce a map as "(0 MB)" in game chat and read like
    a bug. Only maps.json is floored: the page keeps showing the true 0.1 MB and
    the size bands still key off the real value, so sub-1 MB maps stay 'tiny'.
    """
    return max(BRIDGE_MIN_MB, round_mb(mb))


def search_blob(name: str, map_id: int, tags: list, author, description,
                desc_limit: int = SEARCH_DESC_MAX) -> str:
    """The full-text corpus behind the filter box.

    Lowercased, whitespace-collapsed name + id + tags + author + full
    description, space separated. The description is capped (default 800 chars)
    because it is the only unbounded field and it is carried on every row.
    """
    parts = clean(name).lower().split()
    parts.append(str(map_id))
    for tag in tags:
        parts.extend(clean(tag).lower().split())
    parts.extend(clean(author).lower().split())
    if desc_limit != 0:
        body = clean(description, limit=None).lower()
        if desc_limit > 0:
            body = body[:desc_limit]
        parts.extend(body.split())
    return " ".join(p for p in parts if p)


def entry_name(entry: dict) -> str:
    """The map's display name. 'title' is accepted as a fallback because that is
    what the scrape stage calls it -- see COERCIONS: every fallback is counted
    and shouted about at the end of the build, so a contract mismatch cannot
    hide behind a page that happens to look fine."""
    return (clean(entry.get("name")) or clean(entry.get("title"))
            or ("Workshop item %d" % int(entry["id"])))


# Fields that did not arrive in the documented shape. Counted while loading,
# reported as WARNING lines at the end of the build.
COERCIONS = {}
COERCION_TEXT = {
    "title": "used 'title' instead of 'name'",
    "no-name": "had neither 'name' nor 'title' (row falls back to the id)",
    "string-id": "had a non-integer 'id' (coerced)",
    "missing-rank": "had no 'rank' (numbered by input order)",
    "string-size": "had a non-numeric 'sizeMb' (coerced)",
    "no-size": "had no usable 'sizeMb' (treated as 0)",
    "string-subs": "had a non-numeric 'subscriptions' (coerced)",
    "no-subs": "had no 'subscriptions' (shown as 0)",
}


def note(kind: str) -> None:
    COERCIONS[kind] = COERCIONS.get(kind, 0) + 1


def is_number(value) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def display_tags(tags: list) -> list:
    """Chip labels: drop the generic tags, keep order, cap nothing."""
    out = []
    for tag in tags:
        text = clean(tag)
        if not text or text.lower() in TAG_STOPLIST:
            continue
        if text.islower():
            text = text[:1].upper() + text[1:]
        if text not in out:
            out.append(text)
    return out


# --------------------------------------------------------------------------
# images
# --------------------------------------------------------------------------

def normalize_preview(url) -> str:
    """Strip Steam's sizing/letterbox query so we fetch the original upload."""
    raw = clean(url)
    if not raw:
        return ""
    try:
        split = urlsplit(raw)
    except ValueError:
        return ""
    if split.scheme not in ("http", "https") or not split.netloc:
        return ""
    query = [
        (k, v) for k, v in parse_qsl(split.query, keep_blank_values=True)
        if k.lower() not in SIZING_QS_KEYS
    ]
    return urlunsplit((split.scheme, split.netloc, split.path, urlencode(query), ""))


def cached_ok(path: Path) -> bool:
    """True if path already holds a decodable WebP at the right size."""
    try:
        if not path.is_file() or path.stat().st_size == 0:
            return False
        with Image.open(path) as probe:
            probe.verify()
        with Image.open(path) as probe:
            return probe.size == (SHOT_W, SHOT_H)
    except Exception:
        return False


def fetch(session: requests.Session, url: str, timeout: float, retries: int) -> bytes:
    """GET with retries. 4xx that will never succeed are not retried."""
    last = "unknown error"
    for attempt in range(retries + 1):
        try:
            with session.get(url, timeout=timeout, stream=True) as resp:
                if resp.status_code == 200:
                    buf = io.BytesIO()
                    for block in resp.iter_content(64 * 1024):
                        buf.write(block)
                        if buf.tell() > MAX_IMAGE_BYTES:
                            raise ValueError("preview larger than %d bytes" % MAX_IMAGE_BYTES)
                    data = buf.getvalue()
                    if data:
                        return data
                    last = "empty body"
                elif resp.status_code in (400, 401, 403, 404, 410):
                    raise ValueError("HTTP %d" % resp.status_code)
                else:
                    last = "HTTP %d" % resp.status_code
        except ValueError:
            raise
        except Exception as exc:  # network/TLS/timeouts
            last = "%s: %s" % (type(exc).__name__, exc)
        if attempt < retries:
            time.sleep(min(8.0, 0.6 * (2 ** attempt)) + random.uniform(0, 0.4))
    raise ValueError(last)


def cover_crop(data: bytes) -> Image.Image:
    """Scale to cover 336x189 and centre-crop -- never letterbox, never squash."""
    with Image.open(io.BytesIO(data)) as src:
        src.load()
        if src.mode in ("RGBA", "LA") or (src.mode == "P" and "transparency" in src.info):
            rgba = src.convert("RGBA")
            flat = Image.new("RGB", rgba.size, (0, 0, 0))
            flat.paste(rgba, mask=rgba.split()[-1])
            img = flat
        else:
            img = src.convert("RGB")

        scale = max(SHOT_W / img.width, SHOT_H / img.height)
        new_w = max(SHOT_W, int(math.ceil(img.width * scale)))
        new_h = max(SHOT_H, int(math.ceil(img.height * scale)))
        img = img.resize((new_w, new_h), Image.LANCZOS)

    left = (new_w - SHOT_W) // 2
    top = (new_h - SHOT_H) // 2
    return img.crop((left, top, left + SHOT_W, top + SHOT_H))


def build_images(entries: list, img_dir: Path, workers: int, timeout: float,
                 retries: int, download: bool) -> dict:
    """Populate img/<id>.webp. Returns {map_id: True/False} -- has a file or not."""
    img_dir.mkdir(parents=True, exist_ok=True)
    have: dict = {}
    todo = []

    for entry in entries:
        map_id = int(entry["id"])
        path = img_dir / ("%d.webp" % map_id)
        if map_id in have:
            continue                      # duplicate id: one file serves both rows
        if cached_ok(path):
            have[map_id] = True
            continue
        url = normalize_preview(entry.get("preview"))
        if not url or not download:
            have[map_id] = False
            continue
        have[map_id] = False              # flipped to True on success
        todo.append((map_id, url, path))

    stats = {
        "total": len(entries),
        "cached": sum(1 for v in have.values() if v),
        "downloaded": 0,
        "no_preview": sum(1 for e in entries if not normalize_preview(e.get("preview"))),
        "failed": [],
    }
    if not todo:
        return {"have": have, "stats": stats}

    session = requests.Session()
    session.headers.update({
        "User-Agent": USER_AGENT,
        "Accept": "image/avif,image/webp,image/png,image/*,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9",
        "Connection": "keep-alive",
    })

    def work(job):
        map_id, url, path = job
        try:
            image = cover_crop(fetch(session, url, timeout, retries))
            tmp = path.with_suffix(".webp.part")
            image.save(tmp, "WEBP", quality=WEBP_QUALITY, method=6)
            tmp.replace(path)
            return map_id, True, ""
        except Exception as exc:
            return map_id, False, str(exc)[:160]

    done = 0
    log("downloading %d previews with %d workers" % (len(todo), workers))
    with cf.ThreadPoolExecutor(max_workers=workers) as pool:
        for map_id, ok, err in pool.map(work, todo):
            done += 1
            if ok:
                have[map_id] = True
                stats["downloaded"] += 1
            else:
                stats["failed"].append((map_id, err))
            if done % 100 == 0 or done == len(todo):
                log("  %d/%d  (%d ok, %d failed)"
                    % (done, len(todo), stats["downloaded"], len(stats["failed"])))
    session.close()
    return {"have": have, "stats": stats}


# --------------------------------------------------------------------------
# html
# --------------------------------------------------------------------------

def row_html(entry: dict, has_image: bool, desc_max: int,
             search_desc_max: int = SEARCH_DESC_MAX) -> str:
    map_id = int(entry["id"])
    rank = int(entry["rank"])
    name = entry_name(entry)
    # Short field for display, full field (when present) for search only.
    desc = ""  # other authors' text is not published (docs/reference.md): a row shows title, tags and size
    desc_full = ""
    tags = list(entry.get("tags") or [])
    mb = to_mb(entry.get("sizeMb"))
    band = band_of(mb)
    href = "https://steamcommunity.com/sharedfiles/filedetails/?id=%d" % map_id
    updated = clean(entry.get("updated"))
    updated_cell = esc(updated) if updated else "&mdash;"

    out = [
        '<li class="row" data-band="%s" data-search="%s">'
        % (band, esc(search_blob(name, map_id, tags, None,
                                 desc_full, search_desc_max))),
        '  <div class="rank"><span>%d</span></div>' % rank,
    ]

    if has_image:
        out += [
            '  <a class="shot" href="%s" target="_blank" rel="noopener noreferrer">' % href,
            '    <img src="img/%d.webp" alt="In-game view of %s" loading="lazy" '
            'decoding="async" width="%d" height="%d" />' % (map_id, esc(name), SHOT_W, SHOT_H),
            '  </a>',
        ]
    else:
        # No screenshot: a CSS placeholder tile, not a broken image. The span is
        # also the link's accessible name.
        out += [
            '  <a class="shot missing" href="%s" target="_blank" rel="noopener noreferrer">' % href,
            '    <span>No preview</span>',
            '  </a>',
        ]

    out.append('  <div class="meta">')
    out.append('    <h2 class="name" dir="auto">%s</h2>' % esc(name))
    if desc:
        out.append('    <p class="desc" dir="auto">%s</p>' % esc(desc))
    chips = "".join('<span class="tag">%s</span>' % esc(t) for t in display_tags(tags))
    out.append('    <div class="tags">%s</div>' % chips)
    out.append('  </div>')

    out += [
        '  <div class="stats">',
        '    <div class="stat"><dt>Subscribers</dt><dd class="num">%s</dd></div>'
        % fmt_int(entry.get("subscriptions")),
        '    <div class="stat"><dt>Download</dt><dd class="num size-%s">%s</dd></div>'
        % (band, fmt_size(mb)),
        '    <div class="stat"><dt>Updated</dt><dd class="num">%s</dd></div>' % updated_cell,
        '  </div>',
    ]

    # The copy button (since 2026-10-21, when the server's chat commands made way for the in-game menu): the map's Workshop ID,
    # which pasted into the game chat opens the menu on that map; Console mode copies the command for a server of your own.
    # broken-maps.py's cmd_lines() is this template, asserted on every row.
    out += [
        '  <button class="cmd" type="button" data-chat="%d" '
        'data-console="host_workshop_map %d" title="Copy, then paste into the game chat: the menu opens on this map">'
        % (map_id, map_id),
        '    <code class="chat">%d</code>' % map_id,
        '    <code class="console">host_workshop_map %d</code>' % map_id,
        '    <span class="copy" aria-hidden="true">Copy</span>',
        '  </button>',
        '</li>',
    ]
    return "\n".join(out)


# The list is the 1,000 most-subscribed Arms Race maps (ranks 1-1000, rank-stable since 2026-10-03), then maps picked by hand
# after them: the 16 glass maps of 2026-10-21 are #1001-#1016; then, since 2026-10-22, every other Arms Race map that loads
# (#1017 and up, by subscribers). The meta description and the standfirst count them apart ("The 1,000 most-subscribed ... ,
# plus 16 hand-picked glass maps and 2,548 more Arms Race maps,"); the Maps fact counts every row. Kept the same in
# build-1000.py and broken-maps.py: the builder writes the sentence, the stamper re-stamps it from the rows it finds.
MOST_SUBSCRIBED = 1000
EXTRA_WHAT = "hand-picked glass maps"
GLASS_LAST = 1016
MORE_WHAT = "more Arms{}Race maps"


def extra_text(glass: int, more: int, nbsp: str = " ") -> str:
    """', plus 16 hand-picked glass maps' / ', plus 16 hand-picked glass maps and 2,548 more Arms Race maps' / ''."""
    parts = ([f"{glass:,} {EXTRA_WHAT}"] if glass else []) + ([f"{more:,} {MORE_WHAT.format(nbsp)}"] if more else [])
    return ", plus " + " and ".join(parts) if parts else ""


def build_html(entries: list, have: dict, verified: str, desc_max: int,
               search_desc_max: int = SEARCH_DESC_MAX) -> str:
    count = len(entries)
    extra = sum(1 for e in entries if int(e["rank"]) > MOST_SUBSCRIBED)
    more = sum(1 for e in entries if int(e["rank"]) > GLASS_LAST)
    subs = sum(int(e.get("subscriptions") or 0) for e in entries)
    tiny = sum(1 for e in entries if band_of(to_mb(e.get("sizeMb"))) == "tiny")

    subst = {
        "@@COUNT@@": fmt_int(count),
        "@@RANKED@@": fmt_int(count - extra),
        "@@EXTRA_META@@": extra_text(extra - more, more),
        "@@EXTRA_STANDFIRST@@": extra_text(extra - more, more, "&nbsp;") + ("," if extra else ""),
        # the filter JS writes `n + ' of ' + rows.length` with no separator, so
        # the initial counter must not have one either or it reformats on the
        # first keystroke.
        "@@COUNT_PLAIN@@": str(count),
        "@@SUBS@@": fmt_int(subs),
        "@@TINY@@": fmt_int(tiny),
        "@@VERIFIED@@": esc(verified),
        "@@SERVER_HTML@@": SERVER_HTML,   # before @@SERVER@@, which it starts with
        "@@SERVER@@": esc(SERVER_NAME),
        "@@HOST@@": esc(SITE_HOST),
    }

    def fill(text: str) -> str:
        for key, value in subst.items():
            text = text.replace(key, value)
        return text

    rows = "\n".join(
        row_html(e, have.get(int(e["id"]), False), desc_max, search_desc_max)
        for e in entries
    )

    return "".join([
        fill(HEAD), "\n",
        STYLE, "\n",
        "</head>\n<body>\n\n",
        fill(MAST), "\n\n",
        fill(TOOLBAR), "\n\n",
        '  <ol class="list" id="list">\n',
        rows, "\n",
        TAIL, "\n",
        SCRIPT, "\n</body>\n</html>\n",
    ])


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------

def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def load_entries(path: Path) -> list:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if isinstance(data, dict):
        for key in ("maps", "entries", "items", "data"):
            if isinstance(data.get(key), list):
                data = data[key]
                break
    if not isinstance(data, list):
        sys.exit("%s is not a JSON array of map entries" % path)

    out = []
    for i, raw in enumerate(data):
        if not isinstance(raw, dict) or "id" not in raw:
            log("  ! entry %d has no id, skipped" % i)
            continue
        try:
            raw = dict(raw)
            if not is_number(raw["id"]):
                note("string-id")
            raw["id"] = int(raw["id"])
            if not raw.get("rank"):
                note("missing-rank")
            raw["rank"] = int(raw.get("rank") or (i + 1))
        except (TypeError, ValueError):
            log("  ! entry %d has a non-numeric id/rank, skipped" % i)
            continue

        # every other shape mismatch we silently tolerate gets counted here
        if not clean(raw.get("name")):
            note("title" if clean(raw.get("title")) else "no-name")
        size = raw.get("sizeMb")
        if not is_number(size):
            note("string-size" if str(size or "").strip() else "no-size")
        subs = raw.get("subscriptions")
        if not is_number(subs):
            note("string-subs" if str(subs or "").strip() else "no-subs")
        out.append(raw)
    return out


def human(nbytes: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if nbytes < 1024 or unit == "GB":
            return "%.1f %s" % (nbytes, unit) if unit != "B" else "%d B" % nbytes
        nbytes /= 1024.0
    return "%d B" % nbytes


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Build the Gun Game Rotation page.")
    ap.add_argument("--maps", required=True, type=Path, help="maps-raw.json")
    ap.add_argument("--out", required=True, type=Path, help="output directory")
    ap.add_argument("--workers", type=int, default=12, help="parallel downloads (default 12)")
    ap.add_argument("--retries", type=int, default=3, help="retries per preview (default 3)")
    ap.add_argument("--timeout", type=float, default=25.0, help="per-request timeout seconds")
    ap.add_argument("--verified", default="2026-09-17", help="masthead 'Verified' date")
    ap.add_argument("--desc-max", type=int, default=DESC_MAX,
                    help="displayed description clamp (chars, default %d)" % DESC_MAX)
    ap.add_argument("--search-desc-max", type=int, default=SEARCH_DESC_MAX,
                    help="description chars folded into data-search "
                         "(default %d; 0 = leave descriptions out of search)" % SEARCH_DESC_MAX)
    ap.add_argument("--limit", type=int, default=0, help="only the first N entries (testing)")
    ap.add_argument("--no-download", action="store_true",
                    help="skip the network; use whatever is already in img/")
    args = ap.parse_args(argv)

    started = time.time()
    entries = load_entries(args.maps)
    if args.limit:
        entries = entries[:args.limit]
    if not entries:
        sys.exit("no usable entries in %s" % args.maps)

    ids = [e["id"] for e in entries]
    dupes = {i for i in ids if ids.count(i) > 1} if len(set(ids)) != len(ids) else set()
    if dupes:
        log("  ! %d duplicate workshop id(s), e.g. %s -- rows share one image"
            % (len(dupes), sorted(dupes)[:3]))

    out_dir = args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    result = build_images(entries, out_dir / "img", max(1, args.workers),
                          args.timeout, max(0, args.retries), not args.no_download)
    have, stats = result["have"], result["stats"]

    page = build_html(entries, have, args.verified, args.desc_max, args.search_desc_max)
    (out_dir / "index.html").write_text(page, encoding="utf-8", newline="\n")

    bridge = [
        {"rank": int(e["rank"]), "id": int(e["id"]),
         "name": entry_name(e),
         "sizeMb": bridge_mb(to_mb(e.get("sizeMb")))}
        for e in entries
    ]
    floored = sum(1 for e in entries if round_mb(to_mb(e.get("sizeMb"))) < BRIDGE_MIN_MB)
    (out_dir / "maps.json").write_text(
        json.dumps(bridge, ensure_ascii=False, indent=1) + "\n",
        encoding="utf-8", newline="\n")

    img_files = sorted((out_dir / "img").glob("*.webp"))
    img_bytes = sum(f.stat().st_size for f in img_files)
    missing = [e["id"] for e in entries if not have.get(e["id"], False)]

    log("")
    log("maps            : %d" % len(entries))
    log("images          : %d downloaded, %d cached, %d failed, %d without a preview url"
        % (stats["downloaded"], stats["cached"], len(stats["failed"]), stats["no_preview"]))
    log("placeholders    : %d row(s) render .shot.missing" % len(missing))
    for map_id, err in stats["failed"][:10]:
        log("  failed %d: %s" % (map_id, err))
    if len(stats["failed"]) > 10:
        log("  ... and %d more" % (len(stats["failed"]) - 10))
    log("index.html      : %s (%d bytes)"
        % (human((out_dir / 'index.html').stat().st_size),
           (out_dir / 'index.html').stat().st_size))
    log("img/            : %s across %d files" % (human(img_bytes), len(img_files)))
    log("maps.json       : %d entries%s"
        % (len(bridge),
           (", %d sizeMb floored to %d MB (rounded below it)" % (floored, BRIDGE_MIN_MB))
           if floored else ""))
    log("elapsed         : %.1fs" % (time.time() - started))

    # Loud, last, and counted: a field that did not arrive in the documented
    # shape still built a working page, which is exactly how a contract
    # mismatch stays invisible.
    for kind, count in sorted(COERCIONS.items(), key=lambda kv: -kv[1]):
        log("WARNING: %d entries %s" % (count, COERCION_TEXT.get(kind, kind)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
