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
  python -I testcopy.py run [--env K=V]  the test copy: production's inspect, the same image, plugins and HUD addon, with -insecure added to
                                         CS2_ADDITIONAL_ARGS, the restart watcher's automatic actions off, restart policy "no", the labels
                                         cs2.test-config + client.test_labels + a poisoned compose config-hash, production's network endpoint;
                                         waits until A2S answers with VAC off. --env K=V (repeatable) sets one more of the image's variables for
                                         this copy alone - CS2_MAPGROUP=mg_ar_shoots, say; never a password, secret or token
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
which writes into the copy.

THE SETTINGS TEXT. The watcher and the plugin read the settings text, production's environment variable OVERSTRIKE2_SETTINGS_CONTENT. So
run sets SETTINGS_OFF on that text in the copy's environment: the watcher's automatic actions, the deletion and the Workshop page visits
off, every other value production's. A text that cannot be read stops the copy before it is created, because a copy without its
overrides is production. A candidate bridge has production's text in its environment as it is. A production without that variable (the
older programs, which read the variables in OFF) gets OFF alone.

Prints no secret: the saved inspects (they hold the RCON password and the bridge's secret) stay in the harness's state folder
(probe.state_dir(), testcopy/) and are deleted once production is itself again.
"""
import copy, json, re, socket, sys, time
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
TEST_LABEL = "cs2.test-config"
TAG = "insecure client-test copy (tools/client/probe/testcopy.py): -insecure, restart watcher off"
BRIDGE_TAG = "candidate bridge (tools/client/probe/testcopy.py): a new image on a copy of the bridge's data"
OFF = {"CS2_FREEZE_RECOVERY": "0", "CS2_AUTOUPDATE": "0", "CS2_RESTART_AT": "off", "CS2_WARMUP": "0", "CS2_PREFETCH_AT": "off",
       "CS2_PREFETCH_RESTART": "0",
       "HUD_ITEM_PAGE": "off"}  # the plugin never visits the HUD item's page from a test copy: a copy's restarts must not add visits
CONTENT = "OVERSTRIKE2_SETTINGS_CONTENT"           # the settings text in every container's environment
SETTINGS_OFF = {"restarts": {"updates": False, "newVersions": False, "freezeRecovery": False, "daily": "off", "downloadRestart": False},
                "maps": {"delete": "off", "backgroundDownloads": False, "downloadLine": False},
                "hud": {"itemPage": False}}   # OFF's work in the settings text: with restarts and these two maps keys off the watcher exits at once


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


def settings_for_copy(entry):
    """Production's settings text, an entry of its Env, with SETTINGS_OFF set on it. Its comments go, if it has any; references (env:, file:)
    stay for the copy to read. Exits on a text that cannot be read: a copy without its overrides is production."""
    try:
        settings = jsonc(entry.split("=", 1)[1])
    except ValueError:
        settings = None
    if not isinstance(settings, dict):
        sys.exit("REFUSED: production's settings text cannot be read - no test copy without its overrides")
    for section, values in SETTINGS_OFF.items():
        settings.setdefault(section, {}).update(values)
    return f"{CONTENT}={json.dumps(settings)}"


def body(extra=None):
    d = json.loads(PROD_SAVED.read_text())
    c, h = d["Config"], copy.deepcopy(d["HostConfig"])
    over = {**OFF, **(extra or {})}
    env = [settings_for_copy(e) if e.startswith(CONTENT + "=") else e
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


def cmd_run(argv=()):
    probe.need_lock()
    extra = env_overrides(argv)
    if inspect(ASIDE) is None:
        sys.exit("REFUSED: production is not aside - run prep first")
    if inspect(GAME) is not None:
        sys.exit(f"REFUSED: a {GAME} container exists")
    if extra:
        log("for this copy alone:", ", ".join(f"{k}={v}" for k, v in extra.items()))
    new = body(extra)
    if any(e.startswith(CONTENT + "=") for e in new["Env"]):
        log("production holds the settings text: the copy has it with", ", ".join(f"{s}.{k}" for s, v in SETTINGS_OFF.items() for k in v), "overridden")
    st, b = probe.req(f"/containers/create?name={GAME}", "POST", new)
    log("create test copy ->", st, json.loads(b).get("Id", "")[:12])
    st, _ = probe.req(f"/containers/{GAME}/start", "POST")
    log("start ->", st, "net", pinned(inspect(GAME)))
    i, t = wait_a2s(0, 480)
    log(f"A2S after {t:.0f} s:", i, "- VAC OFF, the insecure test copy is up" if i and i[3] == 0 else "- NOT an insecure server: check before any insecure client joins")


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
    if FLAG_MINE.exists():
        if i and i[3] == 1 and ok_net:
            FLAG.unlink(missing_ok=True)
            FLAG_MINE.unlink()
            log("maintenance flag removed")
        else:
            log("maintenance flag LEFT ON: production is not verified")
    if ok_net:
        PROD_SAVED.unlink(missing_ok=True)   # it holds secrets; production is itself again


# ---------------------------------------------------------------------------------------------------- the candidate bridge

def data_mount(d):
    """The host folder a bridge container has at /data (a bind), or None."""
    for b in (d.get("HostConfig") or {}).get("Binds") or []:
        src, _, rest = b.partition(":")
        if rest.split(":")[0] == "/data":
            return src
    for m in (d.get("HostConfig") or {}).get("Mounts") or []:
        if m.get("Target") == "/data" and m.get("Type") == "bind":
            return m.get("Source")
    return next((m.get("Source") for m in d.get("Mounts") or [] if m.get("Destination") == "/data" and m.get("Type") == "bind"), None)


def copy_dir(src):
    """Where the candidate's copy goes: beside production's data, <src>-candidate. Never production's own folder."""
    if not src or not src.startswith("/") or ".." in src.split("/") or src.rstrip("/") in ("", "/mnt"):
        sys.exit(f"REFUSED: cannot place a copy beside {src!r}")
    return src.rstrip("/") + "-candidate"


def helper(name, binds, script):
    """One short-lived container from the game image, as root, without a network: runs `script` with `binds`, is removed, and
    returns (exit code, its output)."""
    image = json.loads(PROD_SAVED.read_text())["Image"]
    st, b = probe.req(f"/containers/create?name={name}", "POST",
                      {"Image": image, "User": "0:0", "Entrypoint": ["/bin/bash", "-c"], "Cmd": [script], "Labels": {TEST_LABEL: "helper: " + name},
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


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    verbs = {"prep": cmd_prep, "run": cmd_run, "status": cmd_status, "rm": cmd_rm, "restore": cmd_restore,
             "bridge-prep": cmd_bridge_prep, "bridge-run": cmd_bridge_run, "bridge-rm": cmd_bridge_rm, "bridge-restore": cmd_bridge_restore}
    if len(sys.argv) < 2 or sys.argv[1] not in verbs:
        print(__doc__)
        sys.exit(0 if len(sys.argv) < 2 else 1)
    verbs[sys.argv[1]](sys.argv[2:]) if sys.argv[1] in ("run", "bridge-run") else verbs[sys.argv[1]]()
