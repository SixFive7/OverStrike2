"""An INSECURE test copy of the production game server, for client tests that must never meet a VAC-secured server - an unpublished HUD
build as loose files, the console of an insecure client (../README.md, "Testing an unpublished HUD build"). Production's game container is
stopped and renamed aside, a test copy is created under production's name from production's own inspect with overrides, and afterwards
production is renamed back and started. Production is down meanwhile: only under the server lock, only with nobody on.

The deployment comes from the private config (probe.py, deploy/os2.py): the game server's container (bridge.game_container) and the
bridge's (bridge.container), the bridge's image (bridge.image), the bridge's data folder with its maintenance flag (bridge.data_dir),
the server's query address (server.address), extra labels for the test containers (client.test_labels). Production's network endpoint -
its networks, IP, MAC and aliases - comes from its own inspect, so the copy answers where production does and restore checks that
production is back on it.

Both prep and rm stop the container with a Docker stop alone, never an RCON quit first. A quit ends the game, so the image's entry
script ends, and the restart policy starts the container again underneath before the stop reaches it: that runs the launcher's start,
its map organize included, for real. A Docker stop is never followed by a restart. The entry script does not pass SIGTERM on to the
game, so the stop ends it with SIGKILL after the grace period, as every redeploy does.

  python -I testcopy.py prep             refuses with a human on A2S or without the harness's server lock; maintenance flag on (the bridge
                                         makes no automatic map change, counts no crash); production's inspect saved; a Docker stop (no RCON
                                         quit), renamed aside to <game>-prod-aside only once it is exited
  python -I testcopy.py run [--env K=V] [--downloads]
                                         the test copy: production's inspect, the same image, plugins and HUD addon, with -insecure added to
                                         CS2_ADDITIONAL_ARGS, the restart watcher's automatic actions off, bots allowed (plugin.testBots: RCON's
                                         bot_add stays), restart policy "no", the labels
                                         cs2.test-config + client.test_labels + a poisoned compose config-hash, production's network endpoint;
                                         waits until A2S answers with VAC off. --env K=V (repeatable) sets one more of the image's variables for
                                         this copy alone - CS2_MAPGROUP=mg_ar_shoots, say; never a password, secret or token. --downloads keeps
                                         maps.backgroundDownloads at production's value (on by default): the restart watcher stays up for the
                                         stock's background downloads alone - into the game's tree, as production makes them - and sends the
                                         bridge its ready reports (the Sync page's stock on disk). Refused while production's watcher has a
                                         restart of its own to judge (its state's pending record), which the copy's watcher would judge instead
  python -I testcopy.py status           A2S (VAC flag), which container is the game server, whether production is aside
  python -I testcopy.py rm               a Docker stop (no RCON quit) and delete the test copy (refuses a game container without the test label)
  python -I testcopy.py restore          production renamed back and started; waits for A2S with VAC ON and a map; checks its network endpoint
                                         against the saved inspect, and the plugins; maintenance flag off (only if this script set it)

A CANDIDATE BRIDGE beside the test copy, so a new bridge build meets the client without ever touching production's state:
production's bridge is set aside the same way, a candidate runs the new image under the bridge's name on a copy of the bridge's data,
and both come back after. The order: prep, bridge-prep, bridge-run <tag>, run (the candidate is up when the copy boots); afterwards rm,
bridge-rm, bridge-restore, restore (which refuses while production's bridge is not back: production never meets a candidate).

  python -I testcopy.py bridge-prep      after prep (production's game server aside), under the lock: production's bridge inspect saved; a
                                         Docker stop; renamed aside to <bridge>-prod-aside only once it is exited
  python -I testcopy.py bridge-run <tag> [--keep-flag]
                                         a copy of production's bridge data, made by a short-lived helper container (the game image as root,
                                         no network, production's data read-only) in <data dir>-candidate - without the maintenance flag
                                         unless --keep-flag; then the candidate from production's bridge inspect with the image
                                         <bridge.image>:<tag>, its /data on that copy, restart policy "no", the test labels, production's
                                         IP, MAC and aliases (the test copy reaches it by name)
  python -I testcopy.py bridge-rm        a Docker stop and delete the candidate (refuses a bridge without the candidate's label), then the
                                         copy (a second helper)
  python -I testcopy.py bridge-restore   production's bridge renamed back and started; checks it is the same container, on its IP and MAC

Production's state.json is never written by a candidate: production's bridge is stopped while it is aside, the candidate's /data is
the copy, and the copy helper mounts production's data read-only. A Workshop sync that posts to the bridge's name reaches the candidate,
which writes into the copy. A test copy run beside a candidate reads the candidate's copy, read-only as production's game server reads
production's data (paths.bridgeData): what the copy's programs read of the bridge's data - the queues the watcher downloads, the lists -
is what the candidate writes. A candidate started after run leaves the copy on production's data.

THE PLUGIN OF A COMMIT OF MAIN on the test copy, so a plugin change meets the client before it ships: the copy mounts a staged plugins
folder in place of production's, and production's own plugins folder is never written. Production's game server copies its plugins into
the game's tree at every start, and the copy does the same from the staged folder, so production's next start - restore - puts its own
plugin back; restore checks that it did. Any time under the lock, before prep too (it builds while production still runs), and before run:

  python -I testcopy.py plugin <commit>  the plugin built from <commit>, which must be on main (origin/main or main): plugin/, bridge/ and
                                         core/ as committed (git archive) into the state folder, dotnet build -c Release there; what
                                         deploy/plugin.py ships of it (the DLL, its .pdb, the config files the project declares) staged by a
                                         short-lived helper container (the game image as root, no network) in <plugins dir>-candidate, beside
                                         production's plugins folder as the game server's bind names it: production's other plugin folders as
                                         they are, its OverStrike2 folder replaced, owned as production's folder is, checked byte for byte.
                                         From then on run mounts it in the copy's place of the plugins folder, and checks that the copy runs
                                         that DLL
  python -I testcopy.py plugin-rm        the staged folder deleted (a helper), and run mounts production's plugins folder again; refuses while
                                         a test copy mounts it. restore does it by itself once production is back and runs its own plugin

THE SETTINGS TEXT. The watcher and the plugin read the settings text, production's environment variable OVERSTRIKE2_SETTINGS_CONTENT. So
run sets SETTINGS_OFF on that text in the copy's environment: the watcher's automatic actions, the deletion and the Workshop page visits
off, bots allowed (plugin.testBots: the plugin's block leaves bot_quota and bot_quota_mode to RCON, so bot_add gives the live lab bots - one
bot_add_ct gives two, one a side, and bot_quota 2: CS2's own), and the maps folder left alone - maps.layout off, so the copy's start
organizes nothing: no hand-over of the sync's taken in, no map moved, linked or deleted, no write of Steam's appworkshop_730.acf and no
backup beside it. Those are production's, in the game's tree the copy
shares; a copy took them in early once, and a candidate os2-server would have edited them with untested code. Every other value is
production's. A text that cannot be read stops the copy before it is created, because a copy without its
overrides is production, and so does a production without the text: no program reads anything else that would turn the watcher off
(the older programs' variables are read by none). A candidate bridge has production's text in its environment as it is.

Prints no secret: the saved inspects (they hold the RCON password and the bridge's secret) stay in the harness's state folder
(probe.state_dir(), testcopy/) and are deleted once production is itself again.
"""
import copy, hashlib, io, json, os, re, shlex, shutil, socket, subprocess, sys, tarfile, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402  (the private config, the server lock, the Docker API and RCON)
from probe import jsonc  # noqa: E402  (the settings text's syntax)

CFG = probe.cfg()
GAME = probe.game()                                   # production's game server container, and the test copy's name
BRIDGE = probe.bridge()
ASIDE, BRIDGE_ASIDE = GAME + "-prod-aside", BRIDGE + "-prod-aside"
BRIDGE_REPO = CFG.get("bridge.image", "localhost/overstrike2-bridge")
FLAG = Path(CFG.get("bridge.data_dir", required=True)) / "maintenance"
HOST, PORT = probe.host_port()
EXTRA_LABELS = probe.test_labels()
STATE = probe.state_dir() / "testcopy"
PROD_SAVED = STATE / "prod-inspect.json"
FLAG_MINE = STATE / "flag-set-by-testcopy"
BRIDGE_SAVED = STATE / "bridge-inspect.json"
STAGED = STATE / "plugin-staged.json"                 # the staged plugin: its commit, folder, files and their hashes (no secret in it)
BUILDS = STATE / "plugin-build"                       # the commits' exports and builds, one folder each
PLUGIN = "OverStrike2"                                # the plugin's folder, DLL and name (deploy/plugin.py's NAME)
PLUGINS_AT = "/home/steam/cs2-plugins"                # paths.plugins' default (core/Settings.cs): where the game server reads the plugins to install
GAME_DIR_AT = "/home/steam/cs2-dedicated"             # paths.gameDir's default: the game's install, its plugins under game/csgo/addons/counterstrikesharp
BRIDGE_DATA_AT = "/data"                              # paths.bridgeData's default: the bridge's data, read-only in the game server
DOWNLOADS = ("maps", "backgroundDownloads")           # run --downloads: the one switch of SETTINGS_OFF a copy may keep at production's value
TEST_LABEL = probe.TEST_LABEL                         # cs2.test-config: a test copy, a candidate, a helper - never production
TAG = "insecure client-test copy (tools/client/probe/testcopy.py): -insecure, restart watcher off"
BRIDGE_TAG = "candidate bridge (tools/client/probe/testcopy.py): a new image on a copy of the bridge's data"
CONTENT = "OVERSTRIKE2_SETTINGS_CONTENT"           # the settings text in every container's environment
SETTINGS_OFF = {"restarts": {"updates": False, "newVersions": False, "freezeRecovery": False, "daily": "off", "downloadRestart": False},
                "maps": {"delete": "off", "backgroundDownloads": False, "downloadLine": False, "layout": "off"},
                "hud": {"itemPage": False},
                "plugin": {"testBots": True}}   # with restarts and these maps keys off the watcher exits at once (run --downloads keeps
                                                # maps.backgroundDownloads production's: the watcher then stays up for it); maps.layout off:
                                                # the start's organize (os2-server's MapsFolder) returns before it touches production's maps
                                                # folder, hand-overs and acf; no visit to the HUD item's page from a copy; plugin.testBots:
                                                # the copy's bots are left to RCON (bot_add), bots for the live lab - production
                                                # never sets it


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def inspect(name):
    return probe.inspect(name)


def a2s(timeout=2.0):
    """(name, map, humans, vac) or None."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(timeout)
    q = b"\xff\xff\xff\xffTSource Engine Query\x00"
    try:
        s.sendto(q, (HOST, PORT)); d = s.recv(1400)
        if d[4] == 0x41:
            s.sendto(q + d[5:9], (HOST, PORT)); d = s.recv(1400)
        i, f = 6, []
        for _ in range(4):
            j = d.index(b"\0", i); f.append(d[i:j].decode(errors="replace")); i = j + 1
        i += 2
        return f[0], f[1], d[i] - d[i + 2], d[i + 6]
    except Exception:
        return None
    finally:
        s.close()


def wait_a2s(want_vac, limit):
    t0 = time.time()
    while time.time() - t0 < limit:
        i = a2s()
        if i and i[3] == want_vac and i[1]:
            return i, time.time() - t0
        time.sleep(3)
    return a2s(), time.time() - t0


def is_test(d):
    return bool(d and (d["Config"].get("Labels") or {}).get(TEST_LABEL))


def endpoints(d):
    """A container's network endpoint as its inspect has it, for a create: each network with its pinned IP (IPAMConfig), aliases and MAC."""
    c = d.get("Config") or {}
    out = {}
    for net, v in ((d.get("NetworkSettings") or {}).get("Networks") or {}).items():
        e = {k: v[k] for k in ("IPAMConfig", "Aliases", "MacAddress") if v.get(k)}
        if not e.get("MacAddress") and c.get("MacAddress"):
            e["MacAddress"] = c["MacAddress"]
        out[net] = e
    return out


def pinned(d):
    """[(network, IP, MAC)] a container is configured with."""
    return sorted((n, (v.get("IPAMConfig") or {}).get("IPv4Address") or v.get("IPAddress") or "", v.get("MacAddress") or "")
                  for n, v in ((d or {}).get("NetworkSettings") or {}).get("Networks", {}).items())


def stop_container(name, t):
    """A Docker stop of `name`, never an RCON quit first (see the top of this file). Waits until it has exited; returns its inspect."""
    t0 = time.time()
    try:
        st, _ = probe.req(f"/containers/{name}/stop?t={t}", "POST")
        log(f"{name}: stop -> {st} after {time.time() - t0:.0f} s")
    except Exception as e:   # the stop goes on in Docker even when the call gives up first: its outcome is read below
        log(f"{name}: stop: {e} - waiting for it to exit")
    d = inspect(name)
    while d is not None and d["State"]["Status"] != "exited" and time.time() - t0 < t + 60:
        time.sleep(2)
        d = inspect(name)
    return d


def stop_game(t):
    return stop_container(GAME, t)


def cmd_prep():
    probe.need_lock()
    d = inspect(GAME)
    if d is None:
        sys.exit(f"no {GAME} container")
    if is_test(d):
        sys.exit(f"{GAME} is already a test copy - nothing to prepare")
    i = a2s()
    if i is None or i[2] > 0:
        sys.exit(f"REFUSED: A2S {'does not answer' if i is None else f'shows {i[2]} human(s)'} - production is only set aside with nobody on")
    if i[3] != 1:
        sys.exit(f"REFUSED: production's A2S says VAC={i[3]} - expected a VAC-secured production, look first")
    if inspect(ASIDE) is not None:
        sys.exit(f"REFUSED: {ASIDE} already exists - a previous test copy was not restored; run restore first")
    STATE.mkdir(parents=True, exist_ok=True)
    if not FLAG.exists():
        FLAG.write_text(f"client-test harness: insecure test copy of the game server, {time.strftime('%Y-%m-%dT%H:%M:%S')}\n")
        FLAG_MINE.write_text("1")
        log("maintenance flag set; waiting 12 s for a change already counting down")
        time.sleep(12)
    else:
        log("maintenance flag already set by someone else - left as it is")
    PROD_SAVED.write_text(json.dumps(d, indent=1))
    log("production inspect saved", d["Id"][:12], "image", d["Image"][7:19], "net", pinned(d))
    if d["State"]["Status"] != "exited":
        log("production: a Docker stop, no RCON quit (after a quit the restart policy starts it again underneath)")
        after = stop_game(30)
        if after is None or after["State"]["Status"] != "exited":
            sys.exit(f"REFUSED: production is {'gone' if after is None else after['State']['Status']} after the stop - not renamed aside. "
                     "Look at it first; the maintenance flag is still on.")
        if after["RestartCount"] != d["RestartCount"]:
            log(f"WARNING: production restarted underneath meanwhile (RestartCount {d['RestartCount']} -> {after['RestartCount']}): "
                "read its log before the test - its launcher ran again")
    st, _ = probe.req(f"/containers/{GAME}/rename?name={ASIDE}", "POST")
    log(f"production renamed to {ASIDE} ->", st, inspect(ASIDE)["State"]["Status"])


def settings_for_copy(entry, keep=()):
    """Production's settings text, an entry of its Env, with SETTINGS_OFF set on it - but the (section, key) pairs in `keep`, which stay
    production's. Its comments go, if it has any; references (env:, file:) stay for the copy to read. Exits on a text that cannot be read: a
    copy without its overrides is production."""
    try:
        settings = jsonc(entry.split("=", 1)[1])
    except ValueError:
        settings = None
    if not isinstance(settings, dict):
        sys.exit("REFUSED: production's settings text cannot be read - no test copy without its overrides")
    for section, values in SETTINGS_OFF.items():
        settings.setdefault(section, {}).update({k: v for k, v in values.items() if (section, k) not in keep})
    return f"{CONTENT}={json.dumps(settings)}"


def body(extra=None, downloads=False, bridge_copy=None):
    """The test copy's create body. `downloads`: maps.backgroundDownloads stays production's (run --downloads). `bridge_copy`: a candidate
    bridge's data folder, which the copy reads in the place of production's bridge data."""
    d = json.loads(PROD_SAVED.read_text())
    c, h = d["Config"], copy.deepcopy(d["HostConfig"])
    staged = staged_plugin()
    if staged:   # the staged plugins folder in the place of production's, its options (read-only) kept
        at = plugins_at(d)
        src = bind_source(d, at)
        if not src or staged["dir"] != copy_dir(src) or swap_bind(h, at, src, staged["dir"]) != 1:
            sys.exit(f"REFUSED: the staged plugin cannot take the place of production's plugins folder at {at} - no copy, which would run "
                     "production's plugin (plugin-rm, then plugin <commit> again)")
    if bridge_copy:   # beside a candidate bridge: the candidate's copy of the bridge's data in the place of production's, read-only as it is
        at = bridge_data_at(d)
        src = bind_source(d, at)
        if src and (copy_dir(src) != bridge_copy or swap_bind(h, at, src, bridge_copy) != 1):
            sys.exit(f"REFUSED: the candidate bridge's data is not the copy beside the bridge data production's game server reads at {at} - "
                     "no copy, which would read production's bridge data beside a candidate")
    if not any(e.startswith(CONTENT + "=") for e in c["Env"]):
        sys.exit("REFUSED: production has no settings text - nothing would turn the copy's restart watcher off, so no copy")
    over = dict(extra or {})
    keep = (DOWNLOADS,) if downloads else ()
    env = [settings_for_copy(e, keep) if e.startswith(CONTENT + "=") else e
           for e in c["Env"] if e.split("=", 1)[0] not in over and not e.startswith("CS2_ADDITIONAL_ARGS=")]
    args = next((e.split("=", 1)[1] for e in c["Env"] if e.startswith("CS2_ADDITIONAL_ARGS=")), "")
    env += [f"{k}={v}" for k, v in over.items()] + [f"CS2_ADDITIONAL_ARGS={(args + ' -insecure').strip()}"]
    labels = dict(c.get("Labels") or {})
    labels.update(EXTRA_LABELS)
    labels[TEST_LABEL] = TAG
    labels["com.docker.compose.config-hash"] = "test-config-not-from-main"   # any redeploy recreates it from main
    h["RestartPolicy"] = {"Name": "no", "MaximumRetryCount": 0}
    h.pop("ConsoleSize", None)
    return {"Image": d["Image"], "Env": env, "Cmd": c.get("Cmd"), "Entrypoint": c.get("Entrypoint"), "User": c.get("User"),
            "WorkingDir": c.get("WorkingDir"), "Labels": labels, "ExposedPorts": c.get("ExposedPorts"), "StopTimeout": c.get("StopTimeout"),
            "AttachStdout": True, "AttachStderr": True, "Tty": bool(c.get("Tty")), "OpenStdin": bool(c.get("OpenStdin")), "HostConfig": h,
            "NetworkingConfig": {"EndpointsConfig": endpoints(d)}}


def env_overrides(argv):
    """run's --env K=V pairs: more of the image's variables for this copy alone. Never a credential (those stay production's)."""
    out, it = {}, iter(argv)
    for a in it:
        kv = next(it, "") if a == "--env" else a[6:] if a.startswith("--env=") else None
        if kv is None:
            sys.exit(f"run: unknown argument {a!r}")
        k, sep, v = kv.partition("=")
        if not sep or not re.fullmatch(r"[A-Z][A-Z0-9_]*", k) or re.search(r"PASS|SECRET|TOKEN|RCON|KEY|_PW$", k) or k in ("CS2_ADDITIONAL_ARGS", CONTENT):
            sys.exit(f"run: --env takes K=V for one of the image's ordinary variables, not {k or kv!r}")
        out[k] = v
    return out


def bridge_data_at(d):
    """Where the game server reads the bridge's data (paths.bridgeData)."""
    return setting_path(d, "bridgeData", BRIDGE_DATA_AT)


def watcher_pending(d):
    """Whether production's restart watcher has a restart of its own recorded - update-watch/state.json's "pending", which the next watcher
    to start judges: True, False (no record, or no state yet), None when it cannot be read. Read by a helper, the game's tree read-only."""
    src = bind_source(d, setting_path(d, "gameDir", GAME_DIR_AT, "STEAMAPPDIR"))
    if not src:
        return None
    code, out = helper(f"{GAME}-watch-read", [f"{src}:/g:ro"], "cat /g/update-watch/state.json 2>/dev/null || echo '{}'")
    try:
        state = json.loads(out) if code == 0 else None
    except ValueError:
        state = None
    return None if not isinstance(state, dict) else state.get("pending") is not None


def downloads_words(d):
    """run --downloads: production's own maps.backgroundDownloads, in words. Exits when a copy cannot keep it: no settings text, or a
    restart of production's watcher still to be judged (the copy's watcher would judge it in its place, with the copy's versions)."""
    text = env_of(d, CONTENT)
    if text is None:
        sys.exit("REFUSED: --downloads keeps a value of production's settings text, and production has none")
    try:
        value = ((jsonc(text) or {}).get("maps") or {}).get("backgroundDownloads", True)
    except (ValueError, AttributeError):
        sys.exit("REFUSED: production's settings text cannot be read - no test copy without its overrides")
    pending = watcher_pending(d)
    if pending is None:
        sys.exit("REFUSED: --downloads: production's restart watcher state (update-watch/state.json) cannot be read - no watcher on the copy")
    if pending:
        sys.exit("REFUSED: --downloads: production's restart watcher has a restart of its own to judge (update-watch/state.json, pending) - "
                 "a copy's watcher would judge it in its place; run the copy without --downloads")
    if value is True:
        return "maps.backgroundDownloads at production's value (on): the restart watcher stays up for the background downloads alone and reports to the bridge"
    return f"maps.backgroundDownloads is {json.dumps(value)} in production too: the copy's restart watcher still has nothing to do and exits"


def cmd_run(argv=()):
    probe.need_lock()
    argv = list(argv)
    downloads = "--downloads" in argv
    extra = env_overrides([a for a in argv if a != "--downloads"])
    if inspect(ASIDE) is None:
        sys.exit("REFUSED: production is not aside - run prep first")
    if inspect(GAME) is not None:
        sys.exit(f"REFUSED: a {GAME} container exists")
    prod = json.loads(PROD_SAVED.read_text())
    cand = inspect(BRIDGE)
    bridge_copy = data_mount(cand) if is_test(cand) else None
    if is_test(cand) and not bridge_copy:
        sys.exit("REFUSED: the candidate bridge has no bind at /data - no copy beside it")
    said = downloads_words(prod) if downloads else None
    if extra:
        log("for this copy alone:", ", ".join(f"{k}={v}" for k, v in extra.items()))
    new = body(extra, downloads, bridge_copy)
    if any(e.startswith(CONTENT + "=") for e in new["Env"]):
        log("production holds the settings text: the copy has it with",
            ", ".join(f"{s}.{k}" for s, v in SETTINGS_OFF.items() for k in v if not (downloads and (s, k) == DOWNLOADS)), "overridden")
    if said:
        log(said)
    if bridge_copy:
        log("beside the candidate bridge: the copy reads its copy of the bridge's data (<data>-candidate) in the place of production's"
            if bind_source(prod, bridge_data_at(prod)) else "beside the candidate bridge: production's game server reads no bridge data, nor does the copy")
    staged = staged_plugin()
    log(f"plugin: the build of {staged['commit'][:12]}, staged, in the place of production's plugins folder" if staged else "plugin: production's")
    st, b = probe.req(f"/containers/create?name={GAME}", "POST", new)
    log("create test copy ->", st, json.loads(b).get("Id", "")[:12])
    st, _ = probe.req(f"/containers/{GAME}/start", "POST")
    log("start ->", st, "net", pinned(inspect(GAME)))
    i, t = wait_a2s(0, 480)
    log(f"A2S after {t:.0f} s:", i, "- VAC OFF, the insecure test copy is up" if i and i[3] == 0 else "- NOT an insecure server: check before any insecure client joins")
    if staged and i:
        time.sleep(5)
        ok, words = plugin_on_copy(staged)
        log(words if ok else words + " - LOOK before testing: the copy does not run the staged plugin")


def cmd_status():
    print("lock:", (probe.lock_text() or "free").splitlines()[0])
    b = inspect(BRIDGE)
    print(f"{BRIDGE}:", "none" if b is None else f"{'CANDIDATE' if is_test(b) else 'production'} {b['State']['Status']} image {b['Image'][7:19]}")
    print(f"{BRIDGE_ASIDE}:", "none" if inspect(BRIDGE_ASIDE) is None else inspect(BRIDGE_ASIDE)["State"]["Status"])
    d = inspect(GAME)
    print(f"{GAME}:", "none" if d is None else f"{'TEST COPY' if is_test(d) else 'production'} {d['State']['Status']} image {d['Image'][7:19]}")
    a = inspect(ASIDE)
    print(f"{ASIDE}:", "none" if a is None else a["State"]["Status"])
    print("A2S:", a2s(), "(name, map, humans, VAC)")
    print("maintenance flag:", FLAG.exists(), "(set by testcopy)" if FLAG_MINE.exists() else "")
    s = staged_plugin()
    print("plugin for a copy:", f"the build of {s['commit'][:12]}, staged {s['staged']} ({len(s['files'])} files)" if s else "production's (none staged)")


def cmd_rm():
    probe.need_lock()
    d = inspect(GAME)
    if d is None:
        log(f"no {GAME} container")
        return
    if not is_test(d):
        sys.exit(f"REFUSED: {GAME} is NOT a test copy")
    if d["State"]["Status"] != "exited":
        stop_game(10)   # no RCON quit: a copy with a restart policy (a run that tests the watcher's restarts) would start again
    probe.req(f"/containers/{GAME}?force=true", "DELETE")
    log("test copy removed")


def cmd_restore():
    probe.need_lock()
    if inspect(GAME) is not None:
        sys.exit(f"REFUSED: a {GAME} container exists - rm the test copy first")
    if inspect(ASIDE) is None:
        sys.exit(f"REFUSED: no {ASIDE}")
    b = inspect(BRIDGE)
    if inspect(BRIDGE_ASIDE) is not None or is_test(b):
        sys.exit("REFUSED: production's bridge is not back - bridge-rm and bridge-restore first (production never meets a candidate bridge)")
    saved = json.loads(PROD_SAVED.read_text()) if PROD_SAVED.exists() else None
    st, _ = probe.req(f"/containers/{ASIDE}/rename?name={GAME}", "POST")
    log(f"{ASIDE} -> {GAME}:", st)
    st, _ = probe.req(f"/containers/{GAME}/start", "POST")
    log("start ->", st)
    i, t = wait_a2s(1, 600)
    log(f"A2S after {t:.0f} s:", i, "- production, VAC ON" if i and i[3] == 1 else "- PRODUCTION NOT BACK AS EXPECTED: look now")
    d = inspect(GAME)
    ok_net = saved is not None and d["Id"] == saved["Id"] and pinned(d) == pinned(saved)
    log("production", d["Id"][:12], d["State"]["Status"], "test label" if is_test(d) else "no test label", "net", pinned(d),
        "OK: as saved at prep" if ok_net else f"NOT AS SAVED AT PREP ({pinned(saved) if saved else 'no saved inspect'})")
    time.sleep(5)
    plugins = [l.strip() for l in probe.rcon("css_plugins list").splitlines() if "LOADED" in l]
    log("plugins:", plugins)
    ok_plugins, words = plugins_as_installed(d)   # its start copied its own plugins over whatever a copy ran
    log("production's plugins in the game's tree:", words, "- OK: what its plugins folder holds" if ok_plugins else "- NOT WHAT ITS PLUGINS FOLDER HOLDS: look now")
    if FLAG_MINE.exists():
        if i and i[3] == 1 and ok_net and ok_plugins:
            FLAG.unlink(missing_ok=True)
            FLAG_MINE.unlink()
            log("maintenance flag removed")
        else:
            log("maintenance flag LEFT ON: production is not verified")
    if ok_net:
        PROD_SAVED.unlink(missing_ok=True)   # it holds secrets; production is itself again
    staged = staged_plugin()
    if staged and ok_net and ok_plugins:
        remove_staged(staged)


# ---------------------------------------------------------------------------------------------------- the candidate bridge

def bind_source(d, target):
    """The host folder a container has at `target` (a bind), or None."""
    target = target.rstrip("/")
    for b in (d.get("HostConfig") or {}).get("Binds") or []:
        src, _, rest = b.partition(":")
        if rest.split(":")[0].rstrip("/") == target:
            return src
    for m in (d.get("HostConfig") or {}).get("Mounts") or []:
        if (m.get("Target") or "").rstrip("/") == target and m.get("Type") == "bind":
            return m.get("Source")
    return next((m.get("Source") for m in d.get("Mounts") or [] if (m.get("Destination") or "").rstrip("/") == target and m.get("Type") == "bind"), None)


def data_mount(d):
    """The host folder a bridge container has at /data (a bind), or None."""
    return bind_source(d, "/data")


def copy_dir(src):
    """Where the candidate's copy goes: beside production's data, <src>-candidate. Never production's own folder."""
    if not src or not src.startswith("/") or ".." in src.split("/") or src.rstrip("/") in ("", "/mnt"):
        sys.exit(f"REFUSED: cannot place a copy beside {src!r}")
    return src.rstrip("/") + "-candidate"


def helper(name, binds, script, image=None):
    """One short-lived container from the game image (production's, as saved at prep, unless `image`), as root, without a network: runs
    `script` with `binds`, is removed, and returns (exit code, its output)."""
    image = image or json.loads(PROD_SAVED.read_text())["Image"]
    st, b = probe.req(f"/containers/create?name={name}", "POST",
                      {"Image": image, "User": "0:0", "Entrypoint": ["/bin/bash", "-c"], "Cmd": [script], "Labels": {**EXTRA_LABELS, TEST_LABEL: "helper: " + name},
                       "HostConfig": {"Binds": binds, "NetworkMode": "none", "RestartPolicy": {"Name": "no"}}})
    cid = json.loads(b).get("Id", "")
    try:
        probe.req(f"/containers/{cid}/start", "POST")
        st, b = probe.req(f"/containers/{cid}/wait", "POST", timeout=600)
        code = json.loads(b).get("StatusCode", -1)
        st, raw = probe.req(f"/containers/{cid}/logs?stdout=1&stderr=1")
        return code, demux(raw)
    finally:
        probe.req(f"/containers/{cid}?force=true", "DELETE")


def demux(raw):
    return probe.demux(raw).strip()


def bridge_body(d, image, data_copy):
    """The candidate: production's bridge inspect with the new image, its /data on the copy, restart policy "no", the test labels and
    production's network endpoint (IP, MAC, aliases). The environment is what compose set; the new image brings its own."""
    c, h = d["Config"], copy.deepcopy(d["HostConfig"])
    st, raw = probe.req(f"/images/{d['Image']}/json")
    img_env = set(((json.loads(raw) if isinstance(raw, (str, bytes)) else raw).get("Config") or {}).get("Env") or [])
    src = data_mount(d)
    h["Binds"] = [(data_copy + b[len(src):]) if b.startswith(src + ":") else b for b in h.get("Binds") or []]
    for m in h.get("Mounts") or []:
        if m.get("Target") == "/data":
            m["Source"] = data_copy
    h["RestartPolicy"] = {"Name": "no", "MaximumRetryCount": 0}
    h.pop("ConsoleSize", None)
    labels = {k: v for k, v in (c.get("Labels") or {}).items() if not k.startswith("org.opencontainers.image.")}
    labels.update(EXTRA_LABELS)
    labels.update({TEST_LABEL: BRIDGE_TAG, "com.docker.compose.config-hash": "test-config-not-from-main"})
    body = {"Image": image, "Env": [e for e in c.get("Env") or [] if e not in img_env], "Labels": labels, "HostConfig": h,
            "NetworkingConfig": {"EndpointsConfig": endpoints(d)}}
    for k in ("User", "ExposedPorts", "StopTimeout", "Tty", "OpenStdin"):
        if c.get(k) not in (None, "", {}, [], False):
            body[k] = c[k]
    return body


def cmd_bridge_prep():
    probe.need_lock()
    if inspect(ASIDE) is None or not PROD_SAVED.exists():
        sys.exit("REFUSED: production's game server is not aside - run prep first (a candidate bridge only beside a test copy)")
    d = inspect(BRIDGE)
    if d is None:
        sys.exit(f"no {BRIDGE} container")
    if is_test(d):
        sys.exit(f"REFUSED: {BRIDGE} is a candidate already - bridge-rm first")
    if inspect(BRIDGE_ASIDE) is not None:
        sys.exit(f"REFUSED: {BRIDGE_ASIDE} already exists - a previous candidate was not restored; run bridge-restore first")
    if data_mount(d) is None:
        sys.exit(f"REFUSED: {BRIDGE} has no bind at /data - nothing to copy")
    STATE.mkdir(parents=True, exist_ok=True)
    BRIDGE_SAVED.write_text(json.dumps(d, indent=1))
    log("production's bridge inspect saved", d["Id"][:12], "image", d["Image"][7:19])
    if d["State"]["Status"] != "exited":
        after = stop_container(BRIDGE, 15)
        if after is None or after["State"]["Status"] != "exited":
            sys.exit(f"REFUSED: production's bridge is {'gone' if after is None else after['State']['Status']} after the stop - not renamed aside")
    st, _ = probe.req(f"/containers/{BRIDGE}/rename?name={BRIDGE_ASIDE}", "POST")
    log(f"production's bridge renamed to {BRIDGE_ASIDE} ->", st, inspect(BRIDGE_ASIDE)["State"]["Status"])


def cmd_bridge_run(argv=()):
    probe.need_lock()
    args = list(argv)
    keep_flag = "--keep-flag" in args
    tags = [a for a in args if not a.startswith("--")]
    if len(tags) != 1 or [a for a in args if a.startswith("--") and a != "--keep-flag"] or not re.fullmatch(r"[\w.-]+", tags[0]):
        sys.exit("usage: testcopy.py bridge-run <image tag> [--keep-flag]")
    image = f"{BRIDGE_REPO}:{tags[0]}"
    if inspect(BRIDGE_ASIDE) is None or not BRIDGE_SAVED.exists():
        sys.exit("REFUSED: production's bridge is not aside - run bridge-prep first")
    if inspect(BRIDGE) is not None:
        sys.exit(f"REFUSED: a {BRIDGE} container exists")
    try:
        probe.req(f"/images/{image}/json")
    except Exception as e:
        sys.exit(f"REFUSED: no image {image} on the server ({e})")
    d = json.loads(BRIDGE_SAVED.read_text())
    src = data_mount(d)
    dst = copy_dir(src)
    owner = d["Config"].get("User") or ""
    script = ("set -e; find /dst -mindepth 1 -delete; cp -a /src/. /dst/; " + ("" if keep_flag else "rm -f /dst/maintenance; ")
              + (f"chown {owner} /dst; " if re.fullmatch(r"\d+(:\d+)?", owner) else "")
              + "echo files: $(find /dst -type f | wc -l), $(du -sh /dst | cut -f1); ls -ln /dst/state.json 2>/dev/null || echo 'no state.json'")
    code, out = helper(f"{BRIDGE}-copy", [f"{src}:/src:ro", f"{dst}:/dst"], script)
    log(f"copy of the bridge's data -> <data>-candidate: exit {code}; " + " | ".join(out.splitlines()[-2:]))
    if code != 0:
        sys.exit("REFUSED: the copy failed - no candidate started")
    st, b = probe.req(f"/containers/create?name={BRIDGE}", "POST", bridge_body(d, image, dst))
    log("create candidate ->", st, json.loads(b).get("Id", "")[:12], image)
    st, _ = probe.req(f"/containers/{BRIDGE}/start", "POST")
    time.sleep(5)
    now = inspect(BRIDGE)
    log("start ->", st, now["State"]["Status"], "restarts", now.get("RestartCount"), "net", pinned(now),
        "- the same as production's" if pinned(now) == pinned(d) else f"- NOT production's {pinned(d)}")


def cmd_bridge_rm():
    probe.need_lock()
    d = inspect(BRIDGE)
    if d is not None:
        if (d["Config"].get("Labels") or {}).get(TEST_LABEL) != BRIDGE_TAG:
            sys.exit(f"REFUSED: {BRIDGE} is NOT a candidate")
        if d["State"]["Status"] != "exited":
            stop_container(BRIDGE, 10)
        probe.req(f"/containers/{BRIDGE}?force=true", "DELETE")
        log("candidate bridge removed")
    else:
        log(f"no {BRIDGE} container")
    if BRIDGE_SAVED.exists():
        dst = copy_dir(data_mount(json.loads(BRIDGE_SAVED.read_text())))
        parent, name = dst.rsplit("/", 1)
        code, out = helper(f"{BRIDGE}-copy-rm", [f"{parent}:/p"], f"rm -rf -- '/p/{name}' && echo removed")
        log(f"the copy <data>-candidate: exit {code}, {(out.splitlines() or ['?'])[-1]}")


def cmd_bridge_restore():
    probe.need_lock()
    if inspect(BRIDGE) is not None:
        sys.exit(f"REFUSED: a {BRIDGE} container exists - bridge-rm the candidate first")
    if inspect(BRIDGE_ASIDE) is None:
        sys.exit(f"REFUSED: no {BRIDGE_ASIDE}")
    saved = json.loads(BRIDGE_SAVED.read_text()) if BRIDGE_SAVED.exists() else None
    st, _ = probe.req(f"/containers/{BRIDGE_ASIDE}/rename?name={BRIDGE}", "POST")
    log(f"{BRIDGE_ASIDE} -> {BRIDGE}:", st)
    st, _ = probe.req(f"/containers/{BRIDGE}/start", "POST")
    time.sleep(5)
    d = inspect(BRIDGE)
    ok = d["State"]["Status"] == "running" and not is_test(d) and (saved is None or (d["Id"] == saved["Id"] and pinned(d) == pinned(saved)))
    log(f"production's bridge {d['Id'][:12]} {d['State']['Status']}, image {d['Image'][7:19]}, net {pinned(d)}",
        "- OK" if ok else "- NOT AS SAVED: look now")
    if ok:
        BRIDGE_SAVED.unlink(missing_ok=True)   # it holds secrets; production's bridge is itself again


# ---------------------------------------------------------------------------------------------------- the plugin of a commit

def env_of(d, name):
    return next((e.split("=", 1)[1] for e in (d.get("Config") or {}).get("Env") or [] if e.startswith(name + "=")), None)


def setting_path(d, key, default, older=None):
    """paths.<key> as the game server reads it: its settings text, else (no text: the older programs) the variable `older`, else the default."""
    text = env_of(d, CONTENT)
    if text is not None:
        try:
            value = ((jsonc(text) or {}).get("paths") or {}).get(key)
        except (ValueError, AttributeError):
            value = None
    else:
        value = env_of(d, older) if older else None
    return value.rstrip("/") if isinstance(value, str) and value.startswith("/") else default


def plugins_at(d):
    """Where the game server reads the plugins it installs at every start (paths.plugins)."""
    return setting_path(d, "plugins", PLUGINS_AT, "CS2_PLUGINS_SRC")


def game_plugins_dir(d):
    """CounterStrikeSharp's plugins in the game's tree, where every start copies them to (paths.gameDir, os2-server's CopyPlugins)."""
    return setting_path(d, "gameDir", GAME_DIR_AT, "STEAMAPPDIR") + "/game/csgo/addons/counterstrikesharp/plugins"


def swap_bind(h, target, src, new_src):
    """HostConfig h with its bind of `src` at `target` taken from `new_src` instead, its options kept; returns how many it changed."""
    target, n = target.rstrip("/"), 0
    if h.get("Binds"):
        binds = []
        for b in h["Binds"]:
            s, _, rest = b.partition(":")
            if s == src and rest.split(":")[0].rstrip("/") == target:
                b, n = new_src + ":" + rest, n + 1
            binds.append(b)
        h["Binds"] = binds
    for m in h.get("Mounts") or []:
        if m.get("Type") == "bind" and m.get("Source") == src and (m.get("Target") or "").rstrip("/") == target:
            m["Source"], n = new_src, n + 1
    return n


def staged_plugin():
    """The staged plugin's record, or None."""
    return json.loads(STAGED.read_text()) if STAGED.exists() else None


def production_inspect():
    """Production's game server: the inspect saved at prep while it is aside, else the running container (only read); never a copy's."""
    if PROD_SAVED.exists() and inspect(ASIDE) is not None:
        return json.loads(PROD_SAVED.read_text())
    d = inspect(GAME)
    if d is None or is_test(d):
        sys.exit(f"REFUSED: production's game server is neither aside (with the inspect prep saved) nor {GAME} - no plugin staged")
    return d


def git(*args, timeout=120, text=True):
    return subprocess.run(["git", "-C", str(probe.REPO), *args], capture_output=True, text=text, stdin=subprocess.DEVNULL, timeout=timeout)


def commit_of_main(rev):
    """The full id of `rev`, which must be a commit on main (origin/main or main); exits otherwise."""
    r = git("rev-parse", "--verify", "--quiet", rev + "^{commit}")
    sha = r.stdout.strip()
    if r.returncode != 0 or not re.fullmatch(r"[0-9a-f]{40}", sha):
        sys.exit(f"REFUSED: {rev!r} is no commit of this repository")
    if not any(git("merge-base", "--is-ancestor", sha, ref).returncode == 0 for ref in ("origin/main", "main")):
        sys.exit(f"REFUSED: {sha[:12]} is not on main (origin/main or main) - only a commit of main goes on a test copy")
    return sha


BUILD_FLAGS = 0x00004000 | 0x08000000 if os.name == "nt" else 0   # below normal priority, no window (deploy/plugin.py's)


def build_plugin(sha):
    """{file name: bytes}: the plugin built from commit sha as committed - plugin/, bridge/ and core/ (what it compiles) exported with git
    archive into the state folder and built there with dotnet build -c Release (warnings are errors) - and of its output what deploy/plugin.py
    ships: the DLL, its .pdb and the config files the project declares."""
    where = BUILDS / sha[:12]
    for old in (BUILDS.iterdir() if BUILDS.exists() else []):   # one build kept: this one, and its log
        shutil.rmtree(old, ignore_errors=True)
    where.mkdir(parents=True)
    r = git("archive", "--format=tar", sha, "plugin", "bridge", "core", timeout=300, text=False)
    if r.returncode != 0:
        sys.exit(f"REFUSED: git archive {sha[:12]} failed: {r.stderr.decode(errors='replace').strip()[:300]}")
    with tarfile.open(fileobj=io.BytesIO(r.stdout)) as tar:
        tar.extractall(where, filter="data")
    build_log = where / "build.log"
    log(f"building the plugin of {sha[:12]} (dotnet build -c Release; its log: {build_log.name} in the state folder)")
    with open(build_log, "wb") as out:   # a file, not a pipe: nothing the build leaves running can hold this call open
        code = subprocess.run(["dotnet", "build", f"plugin/{PLUGIN}", "-c", "Release", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
                              cwd=where, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=BUILD_FLAGS).returncode
    if code != 0:
        tail = build_log.read_text(encoding="utf-8", errors="replace").splitlines()[-12:]
        sys.exit(f"REFUSED: the plugin of {sha[:12]} does not build:\n" + "\n".join(tail))
    from plugin import declared_content   # deploy/plugin.py: the config files that ship beside the DLL
    out_dir = where / "plugin" / PLUGIN / "bin" / "Release" / "net10.0"
    declared = declared_content((where / "plugin" / PLUGIN / f"{PLUGIN}.csproj").read_text(encoding="utf-8"))
    names = [f"{PLUGIN}.dll", f"{PLUGIN}.pdb"] + [n for n in declared if (out_dir / n).exists()]
    if not all((out_dir / n).exists() for n in names[:2]):
        sys.exit(f"REFUSED: the build of {sha[:12]} left no {PLUGIN}.dll and .pdb")
    return {n: (out_dir / n).read_bytes() for n in names}


def plugin_tar(files):
    """The plugin's folder as a tar: <PLUGIN>/ and its files."""
    buf, now = io.BytesIO(), int(time.time())
    with tarfile.open(fileobj=buf, mode="w") as tar:
        d = tarfile.TarInfo(PLUGIN)
        d.type, d.mode, d.mtime = tarfile.DIRTYPE, 0o755, now
        tar.addfile(d)
        for name, data in sorted(files.items()):
            f = tarfile.TarInfo(f"{PLUGIN}/{name}")
            f.size, f.mode, f.mtime = len(data), 0o644, now
            tar.addfile(f, io.BytesIO(data))
    return buf.getvalue()


class Helper:
    """A short-lived container from the game image, as root and without a network, held open (it sleeps) for several commands and a file
    upload; removed on the way out, whatever happened."""

    def __init__(self, name, image, binds):
        self.name, self.image, self.binds, self.cid = name, image, binds, ""

    def __enter__(self):
        st, b = probe.req(f"/containers/create?name={self.name}", "POST",
                          {"Image": self.image, "User": "0:0", "Entrypoint": ["/bin/bash", "-c"], "Cmd": ["sleep 900"],
                           "Labels": {**EXTRA_LABELS, TEST_LABEL: "helper: " + self.name},
                           "HostConfig": {"Binds": self.binds, "NetworkMode": "none", "RestartPolicy": {"Name": "no"}}})
        self.cid = json.loads(b).get("Id", "")
        if not self.cid:
            sys.exit(f"REFUSED: the helper {self.name} was not created ({st})")
        probe.req(f"/containers/{self.cid}/start", "POST")
        return self

    def run(self, script):
        """(exit code, output) of one bash script in the helper."""
        out = probe.exec_in(self.cid, ["/bin/bash", "-c", "( " + script + " ) 2>&1; echo EXIT=$?"], timeout=600)
        m = re.search(r"EXIT=(\d+)\s*$", out)
        return (int(m.group(1)) if m else -1), re.sub(r"EXIT=\d+\s*$", "", out).strip()

    def put(self, data, path):
        probe.req(f"/containers/{self.cid}/archive?path={path}", "PUT", data, "application/x-tar", timeout=300)

    def __exit__(self, *exc):
        if self.cid:
            probe.req(f"/containers/{self.cid}?force=true", "DELETE")
        return False


def cmd_plugin(argv=()):
    probe.need_lock()
    args = list(argv)
    if len(args) != 1 or args[0].startswith("-"):
        sys.exit("usage: testcopy.py plugin <commit of main>")
    sha = commit_of_main(args[0])
    prod = production_inspect()
    at = plugins_at(prod)
    src = bind_source(prod, at)
    if not src:
        sys.exit(f"REFUSED: production's game server has no bind at {at}, its plugins folder - nothing to take the place of")
    dst = copy_dir(src)
    files = build_plugin(sha)
    want = {f"./{PLUGIN}/{n}": hashlib.sha256(b).hexdigest() for n, b in files.items()}
    log(f"built: {', '.join(f'{n} ({len(b):,} bytes)' for n, b in files.items())}")
    with Helper(f"{GAME}-plugin-stage", prod["Image"], [f"{src}:/src:ro", f"{dst}:/dst"]) as h:
        code, out = h.run(f"set -e; find /dst -mindepth 1 -delete; cp -a /src/. /dst/; rm -rf -- '/dst/{PLUGIN}'")
        if code != 0:
            sys.exit(f"REFUSED: the staged folder could not be made (exit {code}): {out[-300:]}")
        h.put(plugin_tar(files), "/dst")
        code, out = h.run("set -e; chown -R --reference=/src /dst; chmod -R u+rwX,go+rX,go-w /dst; cd /dst; "
                          "find . -type f -print0 | sort -z | xargs -0 -r sha256sum")
    got = {path: digest for digest, path in (l.split(None, 1) for l in out.splitlines() if re.fullmatch(r"[0-9a-f]{64}\s+\S.*", l))}
    mine = {k: v for k, v in got.items() if k.startswith(f"./{PLUGIN}/")}
    if code != 0 or mine != want:
        sys.exit(f"REFUSED: the staged folder does not hold the build byte for byte (exit {code}; {len(mine)} of {len(want)} files as built) - "
                 "run plugin-rm, then plugin again")
    others = sorted({k.split("/")[1] for k in got if not k.startswith(f"./{PLUGIN}/")})
    STATE.mkdir(parents=True, exist_ok=True)
    STAGED.write_text(json.dumps({"commit": sha, "dir": dst, "image": prod["Image"], "staged": time.strftime("%Y-%m-%dT%H:%M:%S"),
                                  "files": {n: hashlib.sha256(b).hexdigest() for n, b in files.items()}, "others": others}, indent=1))
    log(f"staged in <plugins>-candidate: {PLUGIN} of {sha[:12]} ({len(files)} files, each as built)"
        + (f", production's other plugin folders as they are: {', '.join(others)}" if others else ", no other plugin folder")
        + ". The next run mounts it in the place of production's plugins folder")


def plugin_on_copy(staged):
    """(ok, words): whether the test copy's game tree holds the staged DLL and CounterStrikeSharp has the plugin loaded."""
    d = inspect(GAME)
    out = probe.exec_in(GAME, ["sha256sum", f"{game_plugins_dir(d)}/{PLUGIN}/{PLUGIN}.dll"]).strip()
    have = out.split()[0] if out else ""
    loaded = [l.strip() for l in probe.rcon("css_plugins list").splitlines() if PLUGIN in l and "LOADED" in l and "UNLOADED" not in l]
    same = have == staged["files"][f"{PLUGIN}.dll"]
    ok = same and bool(loaded)
    return ok, (f"plugin on the copy: {'the' if ok else 'NOT the'} build of {staged['commit'][:12]} - "
                f"{'its' if same else 'another'} DLL in the game's tree, {loaded[0] if loaded else PLUGIN + ' not loaded'}")


def plugins_as_installed(d):
    """(ok, words): whether the game's tree holds what this container's plugins folder holds, file for file - its start copies them there."""
    at, tree = plugins_at(d), game_plugins_dir(d)
    script = (f"cd {shlex.quote(at)} || exit 3; n=0; bad=0; while IFS= read -r -d '' f; do n=$((n+1)); "
              f"cmp -s \"$f\" {shlex.quote(tree)}/\"${{f#./}}\" || {{ bad=$((bad+1)); echo \"DIFF ${{f#./}}\"; }}; "
              "done < <(find . -mindepth 2 -type f ! -path './.*' -print0); echo \"files $n differ $bad\"")
    out = probe.exec_in(GAME, ["bash", "-c", script]).strip()
    m = re.search(r"files (\d+) differ (\d+)", out)
    return bool(m) and m.group(2) == "0", " | ".join(out.splitlines()[-4:]) or "no answer"


def remove_staged(staged):
    """The staged folder deleted by a helper (its parent mounted, the folder by its own name), and the record."""
    parent, _, name = staged["dir"].rstrip("/").rpartition("/")
    if not name.endswith("-candidate") or not parent.startswith("/"):
        sys.exit(f"REFUSED: {staged['dir']!r} is not a staged folder")
    code, out = helper(f"{GAME}-plugin-rm", [f"{parent}:/p"], f"rm -rf -- '/p/{name}' && echo removed", image=staged["image"])
    log(f"the staged plugin <plugins>-candidate: exit {code}, {(out.splitlines() or ['?'])[-1]}")
    if code == 0:
        STAGED.unlink(missing_ok=True)


def cmd_plugin_rm():
    probe.need_lock()
    s = staged_plugin()
    if s is None:
        log("no plugin staged")
        return
    d = inspect(GAME)
    if is_test(d) and bind_source(d, plugins_at(d)) == s["dir"]:
        sys.exit("REFUSED: the test copy mounts the staged folder - rm it first")
    remove_staged(s)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    verbs = {"prep": cmd_prep, "run": cmd_run, "status": cmd_status, "rm": cmd_rm, "restore": cmd_restore,
             "bridge-prep": cmd_bridge_prep, "bridge-run": cmd_bridge_run, "bridge-rm": cmd_bridge_rm, "bridge-restore": cmd_bridge_restore,
             "plugin": cmd_plugin, "plugin-rm": cmd_plugin_rm}
    if len(sys.argv) < 2 or sys.argv[1] not in verbs:
        print(__doc__)
        sys.exit(0 if len(sys.argv) < 2 else 1)
    verbs[sys.argv[1]](sys.argv[2:]) if sys.argv[1] in ("run", "bridge-run", "plugin") else verbs[sys.argv[1]]()
