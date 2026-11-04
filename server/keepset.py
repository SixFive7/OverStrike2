"""keepset.py - THE KEEP SET: the Workshop maps the game server stores (the owner's rules of 2026-11-03).

One file for both server scripts (the owner's 76 a, 2026-11-04): update-watch.py deletes what is outside it at every engine start;
workshop-sync.py hands the server what it lacks and the updates a test passed (the owner's 71 a), tests the maps that will stay first
and shows the set on its dashboard. the deployment's compose file mounts this file read-only beside each of them, in /home/steam, so each imports it by name. Each guards the
import: without this file its keep set is empty and says why, so nothing is deleted or handed over on it. Python 3.9, standard
library only, no infrastructure detail.

Its parts (keep_set): every map with at least one player's up vote; per mode, three lists of its KEEP_TOP playable maps - the best
Workshop score (Top Rated), the most current subscriptions (Most Subscribed) and Steam's trend order over the last 30 days (Most
Popular), from ratings.json, which the sync fetches daily - up to 300 a mode (the owner's 35); the own maps and the HUD item; the
bridge's random queues and the maps players queued. Its problems say when it is not to be trusted with a deletion.
"""
import json
import os

KEEP_TOP = 100                    # per mode: the best-rated playable maps that stay
KEEP_RATINGS_MAX_AGE = 3 * 86400  # ratings older than this (the sync fetches them daily): nothing is deleted
KEEP_RATED_SHARE = 0.9            # the ratings must score this share of each mode's playable maps
KEEP_MIN_TOP = 50                 # a mode's top list shorter than this, with more playable maps listed: nothing is deleted
KEEP_MIN_QUEUE = 10               # each random queue must hold this many entries (the bridge keeps 20)
KEEP_MODES = (("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json"))
KEEP_QUEUES = ("armsrace-new", "armsrace-original", "deathmatch", "wingman")
KEEP_FILES = ("maps.json", "maps-deathmatch.json", "maps-wingman.json", "broken.json", "blocked.json", "removed.json", "votes.json",
              "ours.json", "ratings.json", "queues.json", "state.json")


def keep_inputs(data_dir):
    """The keep set's inputs: the bridge's data files by name, each None when it is missing or does not parse."""
    out = {}
    for name in KEEP_FILES:
        try:
            with open(os.path.join(data_dir, name), "r", encoding="utf-8") as handle:
                out[name] = json.load(handle)
        except (OSError, ValueError):
            out[name] = None
    return out


def keep_set(inputs, now, hud=""):
    """THE KEEP SET: the Workshop maps the server stores (official maps are part of the game). Its parts:
      up       every map with at least one player's up vote in votes.json - one vote per player and map, up or down, the opposite
               replaces it; too-big and ads votes are down votes - however many down votes it has besides
      top      per mode, the union of three lists of its KEEP_TOP playable maps (on that mode's list; not broken: broken.json's lists,
               blocked.json, removed.json's verdicts, the bridge's crash counts), all from ratings.json, which the sync fetches daily: "rated", the
               best Workshop score (vote_data.score, the Workshop's Top Rated order); "subscribed", the most current subscriptions
               (Most Subscribed); "popular", Steam's trend order over the last 30 days (Most Popular). Ties by list order. Up to 300
               maps a mode (the owner's 35, 2026-11-03)
      own, hud ours.json's maps and the HUD item
      queues   the bridge's random queues and the maps players have queued (queues.json)
    Returns {"up", "top": {mode: [ids]}, "lists": {mode: {"rated", "subscribed", "popular": [ids]}}, "cutoff": {mode: {"score",
    "subscriptions": the 100th's, or None}}, "own", "hud", "queues": {name: [ids]}, "requested", "all": set, "scored", "problems"}.
    problems: why the set is not to be trusted with a deletion - an input missing or unreadable, ratings older than
    KEEP_RATINGS_MAX_AGE, scores or subscriptions for under KEEP_RATED_SHARE of a list, no Most Popular order for a mode, any of a
    mode's three lists under KEEP_MIN_TOP, a queue under KEEP_MIN_QUEUE, no own map, no HUD item. Pure: the same inputs give the
    same set."""
    problems = []

    def need(name, kind):
        value = inputs.get(name)
        if not isinstance(value, kind):
            problems.append("%s is missing or unreadable" % name)
            return kind()
        return value

    def digits(value):
        return str(value) if str(value or "").isdigit() else None

    lists = {}
    for mode, name in KEEP_MODES:
        lists[mode] = list(dict.fromkeys(digits(e.get("id")) for e in need(name, list) if isinstance(e, dict) and digits(e.get("id"))))
    if not lists["armsrace"] and "maps.json is missing or unreadable" not in problems:
        problems.append("maps.json lists no map")
    bad = set()
    broken = need("broken.json", dict)
    for section in ("maps", "packs", "removed", "blocked"):
        for entry in broken.get(section) or []:
            wid = digits(entry.get("id") if isinstance(entry, dict) else entry)
            if wid:
                bad.add(wid)
    blocked = inputs.get("blocked.json")  # the maps blocked for good, a file of their own (77 a); optional - without it, more stays
    entries = blocked.get("blocked") if isinstance(blocked, dict) else blocked
    for entry in entries if isinstance(entries, list) else []:
        wid = digits(entry.get("id") if isinstance(entry, dict) else entry)
        if wid:
            bad.add(wid)
    bad |= set(str(w) for w, v in (need("removed.json", dict).get("verdicts") or {}).items() if v != "ok")
    bridge = need("state.json", dict)
    bad |= set(str(k) for k, v in (bridge.get("crashes") or {}).items() if isinstance(v, int) and not isinstance(v, bool) and v > 0)
    bad |= set(str(k) for k in (bridge.get("playerCrashes") or {}))
    up = set(str(k) for k, v in need("votes.json", dict).items() if digits(k) and isinstance(v, dict)
             and any(isinstance(x, int) and not isinstance(x, bool) and x > 0 for x in v.values()))
    ratings = need("ratings.json", dict)
    scores, subs = {}, {}
    for wid, value in (ratings.get("scores") or {}).items() if isinstance(ratings.get("scores"), dict) else []:
        score = value[0] if isinstance(value, list) and value else value
        if digits(wid) and isinstance(score, (int, float)) and not isinstance(score, bool):
            scores[str(wid)] = float(score)
        count = value[3] if isinstance(value, list) and len(value) > 3 else None
        if digits(wid) and isinstance(count, int) and not isinstance(count, bool):
            subs[str(wid)] = count
    trend = ratings.get("trend") if isinstance(ratings.get("trend"), dict) else {}
    if ratings:
        fetched = ratings.get("fetched")
        age = now - fetched if isinstance(fetched, (int, float)) and not isinstance(fetched, bool) else None
        if age is None or age > KEEP_RATINGS_MAX_AGE:
            problems.append("ratings.json is %s old (at most %d h)" % ("%d h" % (age // 3600) if age is not None else "of no known age",
                                                                       KEEP_RATINGS_MAX_AGE // 3600))
    top, cutoff, three = {}, {}, {}
    for mode, _name in KEEP_MODES:
        playable = [w for w in lists[mode] if w not in bad]
        place = dict((w, i) for i, w in enumerate(playable))
        rated = [w for w in playable if w in scores]
        counted = [w for w in playable if w in subs]
        for what, have in (("scores", rated), ("subscriptions", counted)):
            if playable and len(have) < KEEP_RATED_SHARE * len(playable):
                problems.append("ratings.json has the %s of %d of the %d playable %s maps (%d%% needed)" % (
                    what, len(have), len(playable), mode, round(KEEP_RATED_SHARE * 100)))
        order = trend.get(mode) if isinstance(trend.get(mode), list) else None
        if order is None:
            problems.append("ratings.json has no Most Popular order for %s" % mode)
        best = {"rated": sorted(rated, key=lambda w: (-scores[w], place[w]))[:KEEP_TOP],
                "subscribed": sorted(counted, key=lambda w: (-subs[w], place[w]))[:KEEP_TOP],
                "popular": [w for w in dict.fromkeys(str(x) for x in order or []) if w in place][:KEEP_TOP]}
        three[mode] = best
        top[mode] = list(dict.fromkeys(best["rated"] + best["subscribed"] + best["popular"]))
        cutoff[mode] = {"score": scores[best["rated"][-1]] if len(best["rated"]) >= KEEP_TOP else None,
                        "subscriptions": subs[best["subscribed"][-1]] if len(best["subscribed"]) >= KEEP_TOP else None}
        for what, ids in best.items():
            if len(ids) < min(KEEP_MIN_TOP, len(playable)):
                problems.append("the %s %s list holds %d maps (%d playable listed)" % (mode, what, len(ids), len(playable)))
    own = set(digits(e.get("id")) for e in need("ours.json", dict).get("maps") or [] if isinstance(e, dict) and digits(e.get("id")))
    if not own:
        problems.append("ours.json names no own map")
    hud_ids = set([str(hud)]) if digits(hud) else set()
    if not hud_ids:
        problems.append("no HUD item is set")
    queues = need("queues.json", dict)
    if queues and queues.get("version") != 1:
        problems.append("queues.json is not version 1")
    random = queues.get("random") if isinstance(queues.get("random"), dict) else {}
    qs = {}
    for name in KEEP_QUEUES:
        entries = [str(x) for x in random.get(name) or []] if isinstance(random.get(name), list) else []
        if len(entries) < KEEP_MIN_QUEUE:
            problems.append("the %s queue holds %d entries (at least %d)" % (name, len(entries), KEEP_MIN_QUEUE))
        qs[name] = [x for x in entries if x.isdigit()]
    requested = [str(x) for x in queues.get("requested") or [] if digits(x)] if isinstance(queues.get("requested"), list) else []
    every = up | own | hud_ids | set(requested)
    for ids in list(top.values()) + list(qs.values()):
        every |= set(ids)
    return {"up": sorted(up), "top": top, "lists": three, "cutoff": cutoff, "own": sorted(own), "hud": sorted(hud_ids), "queues": qs,
            "requested": requested, "all": every, "scored": len(scores), "problems": problems}


def empty(why):
    """A keep set that holds nothing and says why: what a caller acts on when the set cannot be computed (nothing deleted, nothing
    handed over). Every problem that starts "the keep set could not be computed" stops the hand-over as an error."""
    return {"up": [], "top": {}, "lists": {}, "cutoff": {}, "own": [], "hud": [], "queues": {}, "requested": [], "all": set(), "scored": 0,
            "problems": ["the keep set could not be computed: %s" % why]}


def safe_keep_set(inputs, now, hud="", log=None):
    """keep_set, but an input whose shape it does not expect (a list where it reads a dict, say) is logged and counted as a problem
    instead of raising: the keep set is then empty and says why, so nothing is handed over or deleted on it."""
    try:
        return keep_set(inputs, now, hud)
    except Exception as error:     # a reshaped bridge file must never crash a run or an engine start
        if log:
            log("keep set: not computed (%r) - an input has a shape this code does not read; nothing is decided on it" % error)
        return empty("%r" % error)
