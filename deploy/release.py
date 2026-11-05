#!/usr/bin/env python3
"""A release in two commands: `live` puts a new bridge and plugin on the server, `publish` makes a content update of the HUD item.
It drives the other deploy scripts with their private config, so it holds no credential of its own: the Docker API key comes from
the environment, as for every deploy script, and the RCON password from the game container's own environment, never printed.

    python -I deploy/release.py live <bridge-tag> [--no-plugin] [--under-lock] [--dry-run]
    python -I deploy/release.py publish --note "<what changed>" --client-test <run folder> [--under-lock] [--dry-run]

live - after the release passed its tests (and after publish, when it has HUD content):
  1. 0 humans: A2S twice, 10 s apart, and RCON's status;
  2. the server lock taken (or, with --under-lock, the one already held for this release used and left held);
  3. the bridge swapped to that exact image tag - a commit tag from bridge.py build, or candidate - by bridge.py's own swap: the
     game keeps running, nobody is disconnected, a failed swap puts the old bridge back;
  4. the plugin shipped (deploy/plugin.py ship --apply: the build that was tested - nothing is built here);
  5. the maintenance flag on (the bridge then counts no crash and makes no automatic map change), 0 humans once more, one RCON quit;
  6. the new engine: a new container start, A2S answering, OverStrike2 <version> LOADED, the files in the game container those
     of the build (sha256), and the HUD item among the addons the server hands out - mounted by the server (mm_extra_addons) or
     sent to the clients alone (mm_client_extra_addons);
  7. the flag off - in a finally, so a failure halfway never leaves it on - the deployment's own check (release.verify in the
     private config), the lock released (kept after a failure, and the way back printed);
  8. the record's lines, for the deployment's own log.
  --no-plugin: steps 1-3 and 7 only - the swap alone, nothing restarts.

publish - a content update of the one HUD item (hud/workshop_id.txt; a new item is never created):
  1. --client-test names the real-client test's run folder (with its shots/): the update ships only once a real client saw it work;
  2. the server lock, as for live;
  3. the held-item check on the live server: css_gghud page 3999999999 - an id that does not exist - must read "NOT shown" in the
     plugin's log, or the server would not recognise Steam's page for an item under review and would hold joiners at a download
     that cannot succeed;
  4. deploy/workshop.py hud-publish --note ... --user-approved-update (hud/build.ps1 first, by hand);
  5. the item's time_updated from the Web API's keyless GetPublishedFileDetails, one call at a time, a minute apart, until it
     moves (the upload) and moves again (Valve's approval) or --wait-minutes pass.

--dry-run: every check that only reads; nothing that writes - no lock, no swap, no copy, no flag, no quit, no upload.

release.verify, optional: the deployment's own check after `live`, as a command (["python", "-I", "/path/check.py", "verify"]) or
the path of a Python script; exit 0 means all good. Without it the record says the check was not run.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402
import bridge as bridge_mod  # noqa: E402
import plugin as plugin_mod  # noqa: E402

OS2_DIR = os2.REPO
OWNER = "Over Strike 2 release (deploy/release.py)"
VERIFY = None   # release.verify's default; a deployment's own wrapper may set it
PLUGIN_DIR = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/plugins/" + plugin_mod.NAME
CSS_LOGS = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/logs"
NO_SUCH_ITEM = "3999999999"
PLAYERS_RX = re.compile(r"^\s*players\s*:\s*([0-9]+)\s+humans,\s*([0-9]+)\s+bots", re.M)   # CS2 1.41's status
ADDON_LISTS = ("mm_extra_addons", "mm_client_extra_addons")   # mounted by the server; sent to the clients alone
DETAILS = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
INNER_SHA = ("import hashlib,os,sys,json\nd=sys.argv[1]\nprint(json.dumps({f:hashlib.sha256(open(os.path.join(d,f),'rb').read()).hexdigest()"
             " for f in sys.argv[2:] if os.path.isfile(os.path.join(d,f))}))")
INNER_GREP = ("import glob,os,sys\nfs=sorted(glob.glob(os.path.join(sys.argv[1],'*.txt')),key=os.path.getmtime)[-2:]\n"
              "print('\\n'.join(l.rstrip() for f in fs for l in open(f,errors='replace') if sys.argv[2] in l))")


class Stop(Exception):
    """A step refused or failed: what is printed, and the exit code 1."""


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


class Release:
    def __init__(self, cfg, d=None, humans=None, run=None, sleep=time.sleep, fetch=None, dry=False):
        self.cfg, self.dry = cfg, dry
        self.d = d or os2.Docker(cfg)
        self.humans_a2s = humans or (lambda: os2.humans(cfg.get("server.address", required=True)))
        self.run = run or (lambda argv: subprocess.run(argv, cwd=OS2_DIR).returncode)
        self.sleep, self.fetch = sleep, fetch or fetch_details
        self.game = cfg.get("bridge.game_container", required=True)
        self.bridge_name = cfg.get("bridge.container", required=True)
        self.repo = cfg.get("bridge.image", "localhost/overstrike2-bridge")
        self.lock = cfg.get("server.lock_file", "")
        self.flag = os.path.join(cfg.get("bridge.data_dir", required=True), "maintenance")
        self.verify_with = cfg.get("release.verify", None) or VERIFY
        self.took_lock = self.set_flag = False
        self.record = []

    # ---- reads

    def rcon(self, command):
        game = self.d.inspect("containers", self.game) or {}
        pw = os2.env_of(game, "CS2_RCONPW") if game else ""
        if not pw:
            pw = os2.env_of(self.d.inspect("containers", self.bridge_name) or {"Config": {}}, "RCON_PASSWORD")
        port = (os2.env_of(game, "RCON_PORT") if game else "") or "27015"
        return self.d.exec_in(self.game, ["python3", "-c", bridge_mod.INNER_RCON, pw, command, port], timeout=30)

    def nobody(self, why):
        """0 humans by A2S twice, 10 s apart (a map change shows 0 for a moment), and by RCON's status where it parses."""
        first = self.humans_a2s()
        self.sleep(10)
        second = self.humans_a2s()
        m = PLAYERS_RX.search(self.rcon("status") or "")
        by_rcon = int(m.group(1)) if m else None
        log(f"humans {why}: A2S {first}, {second}; RCON status {by_rcon if by_rcon is not None else 'unreadable (A2S decides)'}")
        if first != 0 or second != 0 or by_rcon:
            raise Stop(f"REFUSED: somebody is on (A2S {first}, {second}, RCON {by_rcon}) - a release happens only at 0 humans")

    def lock_text(self):
        return open(self.lock, encoding="utf-8", errors="replace").read() if self.lock and os.path.exists(self.lock) else None

    def hud_item(self, game):
        """The HUD item the game container is configured with: OS2_HUD_ADDON, else GUNGAME_HUD_ADDON; "" when neither says."""
        return (os2.env_of(game, "OS2_HUD_ADDON") or os2.env_of(game, "GUNGAME_HUD_ADDON")).strip() if game else ""

    # ---- the lock and the flag

    def take_lock(self, under_lock, purpose):
        if not self.lock:
            log("no server.lock_file in the private config - no lock taken")
            return
        held = self.lock_text()
        if held is not None:
            if not under_lock:
                raise Stop("REFUSED: the server lock is held - " + held.replace("\n", " | ")[:300] + " (--under-lock if it is this release's)")
            log("server lock held for this release, used and left held: " + held.splitlines()[0])
            return
        if under_lock:
            raise Stop("REFUSED: --under-lock, but the server lock is free")
        if self.dry:
            log(f"dry run: would take the server lock ({self.lock})")
            return
        fd = os.open(self.lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        now = time.time()
        os.write(fd, (f"owner: {OWNER}\nsince: {time.strftime('%Y-%m-%dT%H:%M:%S', time.localtime(now))}\n"
                      f"expected end: {time.strftime('%Y-%m-%dT%H:%M:%S', time.localtime(now + 1800))}\npurpose: {purpose}\n").encode())
        os.close(fd)
        self.took_lock = True
        log("server lock taken: " + purpose)

    def drop_lock(self):
        if self.took_lock and (self.lock_text() or "").startswith(f"owner: {OWNER}"):
            os.remove(self.lock)
            self.took_lock = False
            log("server lock released")

    def flag_on(self, why):
        if os.path.exists(self.flag):
            log("maintenance flag already set by someone else - left as it is")
            return
        with open(self.flag, "x", encoding="utf-8") as fh:
            fh.write(f"{OWNER}: {why}, {time.strftime('%Y-%m-%dT%H:%M:%S')}\n")
        self.set_flag = True
        log("maintenance flag on; 12 s for a map change already counting down")
        self.sleep(12)

    def flag_off(self):
        if self.set_flag:
            try:
                os.remove(self.flag)
                log("maintenance flag off")
            except OSError as error:
                log(f"WARNING: the maintenance flag could not be removed ({error}) - remove {self.flag} by hand")
            self.set_flag = False

    # ---- live

    def live(self, tag, ship_plugin, under_lock):
        target = self.d.inspect("images", f"{self.repo}:{tag}")
        if not target:
            raise Stop(f"REFUSED: no image {self.repo}:{tag} on the server")
        running = self.d.inspect("containers", self.bridge_name) or {}
        rev = ((target.get("Config") or {}).get("Labels") or {}).get("org.opencontainers.image.revision", "?")
        log(f"bridge: {self.repo}:{tag} = {target['Id'][7:19]} (revision {rev}); running now {str(running.get('Image'))[7:19]}")
        version, files = (plugin_version(), plugin_files()) if ship_plugin else (None, {})
        if ship_plugin:
            if not files:
                raise Stop(f"REFUSED: no plugin build in {plugin_mod.OUT} - deploy/plugin.py build first (the build that was tested)")
            log(f"plugin: {plugin_mod.NAME} {version}, {len(files)} file(s), {plugin_mod.NAME}.dll sha256 {files.get(plugin_mod.NAME + '.dll', '?')[:12]}")
        self.nobody("before the release")
        hud = self.hud_item(self.d.inspect("containers", self.game) or {})
        if self.dry:
            self.take_lock(under_lock, "dry run")
            log(f"dry run: would swap the bridge to {self.repo}:{tag}" + (f", ship plugin {version}, set the flag, quit the engine once" if ship_plugin else ""))
            return 0
        self.take_lock(under_lock, f"release: bridge {tag}" + (f", plugin {version}" if ship_plugin else ""))
        t0 = time.strftime("%H:%M")
        ok = False
        try:
            if target["Id"] != running.get("Image") or not (running.get("State") or {}).get("Running"):
                try:
                    r = self.bridge_obj().swap(tag, "previous")
                except SystemExit as e:       # bridge.py's refusals end in sys.exit: a step that failed, not this script's end
                    r = e.code
                if r != 0:
                    raise Stop("the bridge swap failed - bridge.py put the old one back (its lines above)")
            now = self.d.inspect("containers", self.bridge_name) or {}
            self.record.append(f"bridge image `{now.get('Image')}` (revision {rev}) by `release.py live {tag}`")
            if ship_plugin:
                if self.run([sys.executable, "-I", os.path.join(OS2_DIR, "deploy", "plugin.py"), "ship", "--apply"]) != 0:
                    raise Stop("deploy/plugin.py ship --apply failed - nothing restarted; the server may hold part of the new plugin")
                self.record.append(f"plugin {version}, `{plugin_mod.NAME}.dll` sha256 `{files.get(plugin_mod.NAME + '.dll', '?')[:12]}...` shipped")
                self.flag_on(f"release, plugin {version}")
                self.nobody("right before the quit")
                started = (self.d.inspect("containers", self.game) or {}).get("State", {}).get("StartedAt")
                log("RCON quit")
                try:
                    self.rcon("quit")
                except Exception as error:    # the engine and the exec in it end together: no answer is the normal case
                    log(f"(the quit's exec ended without an answer: {error.__class__.__name__})")
                self.record.append("maintenance flag on, one RCON `quit`")
                self.back(started, version, files, hud)
            ok = True
        finally:
            self.flag_off()
            if ship_plugin and self.record:
                self.record.append("flag off")
        verdict = self.verify()
        self.record.append(f"the deployment's check: {verdict}")
        if verdict not in ("ALL GOOD", "none configured"):
            ok = False
        if ok:
            self.drop_lock()
        log(f"RECORD - live ({t0}-{time.strftime('%H:%M')}, under the lock, 0 humans): " + "; ".join(self.record))
        return 0 if ok else 1

    def bridge_obj(self):
        b = bridge_mod.Bridge.__new__(bridge_mod.Bridge)
        b.cfg, b.d, b.repo, b.name, b.game = self.cfg, self.d, self.repo, self.bridge_name, self.game
        return b

    def addons(self):
        """{list: the Workshop ids in it} for the two addon lists, as the running server holds them (None: unreadable)."""
        out = {}
        for name in ADDON_LISTS:
            m = re.search(name + r'"?\s*=\s*"?([0-9,]*)', self.rcon(name) or "")
            out[name] = [i for i in m.group(1).split(",") if i] if m else None
        return out

    def back(self, started, version, files, hud):
        """The new engine after the quit: a new container start, A2S, the plugin LOADED, its files, the HUD addon."""
        for _ in range(36):           # 3 min for the container to start again (pre.sh then installs, organizes, starts the engine)
            self.sleep(5)
            if (self.d.inspect("containers", self.game) or {}).get("State", {}).get("StartedAt") not in (None, "", started):
                break
        else:
            raise Stop("the game container did not start again within 3 minutes of the quit - look now")
        for _ in range(60):           # 10 min for A2S
            self.sleep(10)
            if self.humans_a2s() is not None:
                break
        else:
            raise Stop("A2S has not answered for 10 minutes after the restart - look now")
        plugins = ""
        for _ in range(18):           # 3 min for the plugins
            plugins = self.rcon("css_plugins list") or ""
            if any('"' + plugin_mod.NAME + '"' in l and "LOADED" in l for l in plugins.splitlines()):
                break
            self.sleep(10)
        line = next((l.strip() for l in plugins.splitlines() if '"' + plugin_mod.NAME + '"' in l), "")
        if "LOADED" not in line or version not in line:
            raise Stop(f"{plugin_mod.NAME} {version} is not LOADED: {line or 'not listed'}")
        out = self.d.exec_in(self.game, ["python3", "-c", INNER_SHA, PLUGIN_DIR, *sorted(files)])
        try:
            there = json.loads(out.strip().splitlines()[-1])
        except (ValueError, IndexError):
            raise Stop(f"the plugin's files in the game container could not be read: {out.strip()[:200]}")
        wrong = sorted(f for f in files if there.get(f) != files[f])
        if wrong:
            raise Stop(f"the game container's plugin files are not the build's: {', '.join(wrong)}")
        lists = self.addons()
        shown = ", ".join(f"`{n} = {','.join(v) if v is not None else '?'}`" for n, v in lists.items())
        if hud and not any(hud in (v or []) for v in lists.values()):
            raise Stop(f"the server hands out no HUD item {hud}: {shown}")
        self.record.append(f"{line}; {shown}; the shipped files' hashes those of the build")
        log(f"back: {line}, {shown}, {len(files)} file(s) as built")

    def verify(self):
        """The deployment's own check (release.verify): 'ALL GOOD', 'LOOK ...', 'NOT RUN' (named but missing: a failure), or
        'none configured' (check the server by hand; not a failure)."""
        cmd = self.verify_with
        if not cmd:
            log("no release.verify in the private config - check the server by hand")
            return "none configured"
        argv = list(cmd) if isinstance(cmd, (list, tuple)) else [sys.executable, "-I", cmd]
        script = next((a for a in argv[1:] if a.endswith(".py")), None)
        if script and not os.path.isfile(script):
            log(f"release.verify: {script} not found - check the server by hand")
            return "NOT RUN"
        return "ALL GOOD" if self.run(argv) == 0 else "LOOK (its lines above)"

    # ---- publish

    def publish(self, note, client_test, under_lock, wait_minutes):
        if not client_test or not os.path.isdir(os.path.join(client_test, "shots")):
            raise Stop("REFUSED: --client-test must name the real-client test's run folder, with its shots/ - a HUD update ships only "
                       "once a real client saw it work")
        if not note:
            raise Stop("REFUSED: --note, the change note Steam shows with the update")
        hud = open(os.path.join(OS2_DIR, "hud", "workshop_id.txt"), encoding="utf-8").read().strip()
        if not hud.isdigit():
            raise Stop("REFUSED: hud/workshop_id.txt names no item - there is no create path")
        before = self.fetch(hud)
        log(f"the HUD item {hud} now: {describe(before)}")
        self.take_lock(under_lock, "release: HUD content update")
        try:   # publish changes nothing on the server: the lock it took is released whatever happens
            seen = self.held_check()
            if self.dry:
                log(f"dry run: would publish a content update of {hud} with the note {note!r}, then wait for time_updated to move twice")
                return 0
            if self.run([sys.executable, "-I", os.path.join(OS2_DIR, "deploy", "workshop.py"), "hud-publish", "--note", note,
                         "--user-approved-update"]) != 0:
                raise Stop("deploy/workshop.py hud-publish failed (its lines above)")
            moves, last = [], (before or {}).get("time_updated") or 0
            for _ in range(wait_minutes):      # one call a minute, each after the last has ended: one at a time
                if len(moves) == 2:
                    break
                self.sleep(60)
                now = self.fetch(hud)
                if now and (now.get("time_updated") or 0) > last:
                    last = now["time_updated"]
                    moves.append(last)
                    log(("the upload" if len(moves) == 1 else "Valve's approval, most likely") + f": time_updated {hm(last)} - {describe(now)}")
            if len(moves) < 2:
                raise Stop(f"time_updated moved {len(moves)} time(s) in {wait_minutes} min - "
                           + ("no upload seen" if not moves else "the update waits in Valve's queue; look again later (hud-query)"))
            log(f"RECORD - publish: a content update of {hud}, client test {os.path.basename(client_test.rstrip(os.sep))}; the held-item "
                f"check: `{seen}`; time_updated {hm(moves[0])} (the upload), then {hm(moves[1])}: Valve's approval, "
                f"{(moves[1] - moves[0]) // 60} minutes after the upload")
            return 0
        finally:
            self.drop_lock()

    def held_check(self):
        """css_gghud page <an id that does not exist>: the plugin's verdict, from its log a few seconds later, must be NOT shown."""
        key = f"HUD item probe: {NO_SUCH_ITEM}"
        grep = lambda: [l for l in (self.d.exec_in(self.game, ["python3", "-c", INNER_GREP, CSS_LOGS, key]) or "").splitlines() if key in l]
        old = len(grep())
        log(f"held-item check: css_gghud page {NO_SUCH_ITEM} -> {(self.rcon(f'css_gghud page {NO_SUCH_ITEM}') or '').strip()[:160]}")
        for _ in range(12):
            self.sleep(5)
            lines = grep()
            if len(lines) > old:
                seen = lines[-1].split(key, 1)[1].strip(" :")
                log("held-item check: " + seen)
                if "-> NOT shown" not in seen:
                    raise Stop("REFUSED: the held-item check does not read NOT shown - the server would not recognise an item under review")
                return seen
        raise Stop("REFUSED: the held-item check's answer did not reach the plugin's log within a minute")


def plugin_version():
    src = open(os.path.join(plugin_mod.PROJECT, plugin_mod.NAME + ".cs"), encoding="utf-8").read()
    m = re.search(r'public const string Version = "([^"]+)"', src)
    return m.group(1) if m else "?"


def plugin_files():
    """{file: sha256} of what plugin.py ships, from its build output; {} without a build."""
    if not os.path.isfile(os.path.join(plugin_mod.OUT, plugin_mod.NAME + ".dll")):
        return {}
    return {f: os2.sha256(os.path.join(plugin_mod.OUT, f)) for f in plugin_mod.ship_list()}


def fetch_details(item):
    """The item's details from the keyless Web API (one call; the caller keeps them a minute apart), or None."""
    body = urllib.parse.urlencode({"itemcount": 1, "publishedfileids[0]": item}).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(DETAILS, data=body), timeout=20) as r:
            return (json.loads(r.read()).get("response", {}).get("publishedfiledetails") or [None])[0]
    except (OSError, ValueError):
        return None


def hm(t):
    return time.strftime("%H:%M:%S", time.localtime(t)) if t else "never"


def describe(d):
    if not d:
        return "no answer"
    return f"result {d.get('result')}, visibility {d.get('visibility')}, file_size {d.get('file_size')}, time_updated {hm(d.get('time_updated'))}"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter, allow_abbrev=False)
    ap.add_argument("command", choices=["live", "publish"])
    ap.add_argument("tag", nargs="?", help="live: the bridge image tag to run (a commit tag, or candidate)")
    ap.add_argument("--no-plugin", action="store_true", help="live: the bridge swap alone; nothing restarts")
    ap.add_argument("--note", help="publish: the change note Steam shows")
    ap.add_argument("--client-test", help="publish: the real-client test's run folder (with shots/)")
    ap.add_argument("--wait-minutes", type=int, default=120)
    ap.add_argument("--under-lock", action="store_true", help="the server lock is held for this release already: use it, leave it")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--config")
    args = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    rel = Release(os2.Config(args.config), dry=args.dry_run)
    try:
        if args.command == "live":
            if not args.tag:
                raise Stop("live needs the bridge image tag")
            return rel.live(args.tag, not args.no_plugin, args.under_lock)
        return rel.publish(args.note, args.client_test, args.under_lock, args.wait_minutes)
    except Stop as stop:
        log(str(stop))
        if rel.took_lock:
            log(f"the server lock stays held ({rel.lock}) - the way back: `python deploy/bridge.py rollback` for the bridge "
                "(:previous is what this run replaced), the previous plugin build shipped again and one quit at 0 humans; then remove the lock")
        return 1


if __name__ == "__main__":
    sys.exit(main())
