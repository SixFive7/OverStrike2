#!/usr/bin/env python3
"""The stack's two checks, both read-only: guard before a redeploy of the whole stack, verify after a deploy or a release.

    python -I deploy/check.py guard                 before EVERY redeploy of the stack: exit 0 GO (nobody on), exit 3 NO
                                                    (somebody is on, or A2S did not answer)
    python -I deploy/check.py guard --allow-kick    only when the owner said so for this redeploy
    python -I deploy/check.py verify                after a deploy or a release (release.py live runs it): exit 0 ALL GOOD,
                                                    1 something to look at (printed)
    --config <file>   the private config (default: $OVERSTRIKE2_CONFIG, else .private/config.toml); exit 2 without a usable one

guard. A redeploy of the whole stack may recreate every service in it, the game server included, whatever changed - a stack manager
that force-recreates on every redeploy does - and so disconnect everybody on it. The guard decides by the players alone: humans by
A2S_INFO (players minus bots), and no answer is "unknown", which is not a go. What it shows besides decides nothing: the server lock,
whether the redeploy moves the game server to a newer image, and, with [stack] in the private config, whether the game server's
service in the compose file changed since the commit last deployed. Most changes need no redeploy: the bridge ships with bridge.py
update (the game server keeps running), the data files reload by themselves, and the plugin, pre.sh, os2-server and the mode cfgs
are taken in at the game server's next start, which the restart watcher brings (deploy/README.md).

verify. The bridge on its image (<bridge.image>:latest) and in the game server's compose project; the game server and the sync
running; the game server production, not a test copy (check.test_label, check.aside, and A2S answering VAC-secured); started on
check.mapgroup; the bridge's health from inside the game server (the path the log push takes); the settings and the secrets (below);
the roll-call of the compose file (stack.file); and the deployment's own audits ([[check.audit]]), each a command.

verify, the settings and the secrets. Each container's settings text is its OVERSTRIKE2_SETTINGS_CONTENT, read from its inspect: the
texts must be identical, bridge.secret as each container reads it (a reference resolved in that container) must be the secret in the
game server's CS2_LOG_HTTP_URL, and server.rconPassword must be the game server's CS2_RCONPW - compared, never printed: after a
rotation every place must have the new one. Some containers with the text and some without is a look. With the text in none of them
the parts run on environment variables, as with the Python programs that os2-server and os2-sync replaced: it says so when those are
mounted, and compares the variables that hold the secret.

Every name and address comes from the private config (deploy/config.example.toml has every key), the Docker API key from the
environment, as for every deploy script. Nothing printed here holds a credential.
"""
import argparse
import json
import os
import re
import socket
import subprocess
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402
import bridge as bridge_mod  # noqa: E402  (its INNER_GET: the health request made from inside the game server)

CONTENT = "OVERSTRIKE2_SETTINGS_CONTENT"     # the settings text, in each container's environment (docs/settings.md)
PROGRAM_FOLDER = "/home/steam/overstrike2"   # where the game server and the sync mount os2-server and os2-sync (compose.example.yml)
OLD_PROGRAMS = ("update-watch.py", "keepset.py", "workshop-sync.py")   # the Python programs os2-server and os2-sync replaced
MAPGROUP = "mg_ar_shoots"   # check.mapgroup's default: Valve's one-map Arms Race group, so CS2 shows no VOTING tab of its own at Game Over
PROJECT, SERVICE = "com.docker.compose.project", "com.docker.compose.service"
CONTAINER_NAME = re.compile(r"^\s*container_name:\s*['\"]?([\w.-]+)")   # a commented service's lines start with '#'


# ----------------------------------------------------------------------------------------------------------------- reading

def env_of(c, key):
    """A container's environment variable, None when it has none (os2.env_of says "" for both)."""
    return next((e.split("=", 1)[1] for e in ((c or {}).get("Config") or {}).get("Env") or [] if e.startswith(key + "=")), None)


def labels_of(c):
    return ((c or {}).get("Config") or {}).get("Labels") or {}


def a2s(address):
    """(humans, vac) by A2S_INFO - players minus bots, and the VAC flag; None when it does not answer."""
    host, _, port = (address or "").rpartition(":")
    try:
        q = b"\xff\xff\xff\xffTSource Engine Query\x00"
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(3)
        s.sendto(q, (host, int(port)))
        d = s.recv(4096)
        if d[4:5] == b"A":  # a challenge: ask again with it
            s.sendto(q + d[5:9], (host, int(port)))
            d = s.recv(4096)
        o = 6
        for _ in range(4):  # name, map, folder, game
            o = d.index(b"\x00", o) + 1
        return d[o + 2] - d[o + 4], d[o + 8]   # after the app id: players, max, bots, type, os, visibility, vac
    except Exception:
        return None


def docker_problem(cfg):
    """Why the Docker API cannot be used, or "" when it can: no docker.api, or a header whose variable is not set. Names, never values."""
    if not cfg.get("docker.api", ""):
        return "no docker.api in the private config"
    for k, v in (cfg.get("docker.headers", {}) or {}).items():
        if isinstance(v, str) and v.startswith("env:") and not os.environ.get(v[4:]):
            return f"docker.headers.{k} reads {v[4:]}, which is not set"
    return ""


def dotted(data, path):
    """A value in parsed JSON by its dotted path ("a.b.c"); "" when it is not there."""
    for part in (path or "").split("."):
        if not isinstance(data, dict) or part not in data:
            return ""
        data = data[part]
    return data if isinstance(data, str) else ""


def jsonc(text):
    """The settings text as Python values: JSON with // and /* */ comments and trailing commas, what OverStrike2's reader takes. A syntax
    error is a ValueError whose words quote nothing of the text."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        else:
            if c in "}]":
                k = len(out) - 1
                while k >= 0 and out[k].isspace():
                    k -= 1
                if k >= 0 and out[k] == ",":
                    del out[k]
            out.append(c)
            i += 1
    return json.loads("".join(out))


def mounted(c):
    """[(source, target)] of a container's mounts, as its inspect names them."""
    return [(m.get("Source") or "", m.get("Destination") or "") for m in (c or {}).get("Mounts") or []]


def old_programs(c):
    """The older Python programs a container mounts."""
    return sorted({s for s in OLD_PROGRAMS for source, _target in mounted(c) if source.replace("\\", "/").endswith("/" + s)})


def resolve(value, container):
    """A settings value as its container reads it: "env:NAME" from that container's environment, else itself. What finds nothing is empty,
    as in OverStrike2's reader, and so is "file:PATH", which is inside the container, where this check does not look: that place then
    counts as holding no value, and is named."""
    if not isinstance(value, str) or value.startswith("file:"):
        return ""
    if value.startswith("env:"):
        return env_of(container, value[4:]) or ""
    return value


# ------------------------------------------------------------------------------------------------------- the guard's decision

def decide(on, allow_kick, game="the game server"):
    """(exit code, line): GO only with nobody on, or with the owner's --allow-kick."""
    if on == 0:
        return 0, "GO: nobody is on"
    who = on if on is not None else "an unknown number of"
    if allow_kick:
        return 0, f"GO (--allow-kick): {game} restarts with {who} player(s) on - the owner said so"
    return 3, f"NO: a redeploy would restart {game} with {who} player(s) on. Wait for an empty server, or ask the owner for --allow-kick."


def service_changed(old_text, new_text, service):
    """(changed, by the service alone) between two versions of the compose file: the service's own definition, anchors resolved, when
    PyYAML is there and reads both; else the whole file. None for either text: that version could not be read - changed."""
    if old_text is None or new_text is None:
        return True, False
    try:
        import yaml  # PyYAML, if installed: the service as compose reads it
        pick = lambda text: ((yaml.safe_load(text) or {}).get("services") or {}).get(service)
        return pick(old_text) != pick(new_text), True
    except Exception:  # no PyYAML, or a text it cannot read
        return old_text != new_text, False


# ----------------------------------------------------------------------------------------------------- the settings and secrets

def mapgroup_ok(game, want=MAPGROUP, name="the game server"):
    """(ok, line): the game server starts on that map group (CS2_MAPGROUP, which the image passes as +mapgroup)."""
    group = env_of(game, "CS2_MAPGROUP")
    one_map = want == MAPGROUP
    if group == want:
        return True, f"{name} starts on the map group {want}" + (" (one map: no CS2 VOTING tab at Game Over)" if one_map else "")
    return False, (f"{name}'s CS2_MAPGROUP is {group!r}, not {want}" + (": CS2 shows its own VOTING tab with that group's maps at Game Over" if one_map else "")
                   + " (the compose file sets it; a redeploy of the stack applies it)")


def secret_places(c, n, settings=None):
    """{place: value} of every place the bridge's secret is written: the secret in the game server's CS2_LOG_HTTP_URL (its log push takes
    it from there); every variable that still holds it (the bridge's LOG_SECRET, OVERSTRIKE2_BRIDGE_SECRET, SYNC_BRIDGE_SECRET); and
    bridge.secret as each container reads its settings text (settings: {role: value}). With no settings text the bridge's LOG_SECRET
    counts even when it is missing. c: {role: inspect}, n: {role: container name}, for game, bridge and sync. An empty value: that place
    has none."""
    places = {}
    bridge, game = c.get("bridge"), c.get("game")
    if settings is None or env_of(bridge, "LOG_SECRET") is not None:
        places[f"{n['bridge']} LOG_SECRET"] = env_of(bridge, "LOG_SECRET") or ""
    m = re.search(r"/log/([A-Za-z0-9]+)$", (env_of(game, "CS2_LOG_HTTP_URL") or "").replace("\\/", "/").strip())
    places[f"{n['game']} CS2_LOG_HTTP_URL"] = m.group(1) if m else ""
    for role in ("game", "sync", "bridge"):
        if not n.get(role):
            continue
        for key in ("OVERSTRIKE2_BRIDGE_SECRET", "SYNC_BRIDGE_SECRET"):
            if env_of(c.get(role), key) is not None:
                places[f"{n[role]} {key}"] = env_of(c.get(role), key)
    for role, value in (settings or {}).items():
        places[f"{n[role]} settings"] = value
    return places


def secrets_agree(places, bridge):
    """(ok, line) - one secret, the same in every place; the line names places, never a value. bridge: the bridge's container name."""
    ref = places.get(f"{bridge} settings", places.get(f"{bridge} LOG_SECRET", ""))
    off = [p for p, v in places.items() if not v or v != ref]
    if not off:
        return True, f"one bridge secret in all {len(places)} places ({', '.join(places)})"
    return False, f"the bridge secret is not the bridge's own in: {', '.join(off)} (of {len(places)} places)"


def settings_lines(c, n):
    """[(ok, line)] about the settings text and the secrets; ok None is a note. c: {role: inspect}, n: {role: container name}, for game,
    bridge and sync (no sync name: the stack has none). Every line names places, never a value."""
    roles = [r for r in ("game", "bridge", "sync") if n.get(r)]
    runners = [r for r in ("game", "sync") if n.get(r)]   # the two that run os2-server and os2-sync
    both = lambda rs: " and ".join(n[r] for r in rs)
    texts = {r: env_of(c.get(r), CONTENT) for r in roles}
    held = [r for r in roles if texts[r] is not None]
    missing = [r for r in roles if texts[r] is None]
    if not held:
        scripts = {r: old_programs(c.get(r)) for r in runners}
        if all(scripts.values()):
            lines = [(None, "the older Python programs run: no container holds the settings text; "
                      + "; ".join(f"{n[r]} mounts {', '.join(s)}" for r, s in scripts.items()))]
        else:
            lines = [(False, f"no container holds the settings text ({CONTENT}), and the older Python programs are not mounted either ("
                      + ", ".join(n[r] for r, s in scripts.items() if not s) + "): neither way of running is in place")]
        return lines + [secrets_agree(secret_places(c, n), n["bridge"])]
    lines = []
    if missing:
        lines.append((False, f"only {', '.join(n[r] for r in held)} hold the settings text ({CONTENT}), {', '.join(n[r] for r in missing)} do not: "
                      "compose gives them all the same, so a container was made another way; a redeploy of the stack at 0 humans puts it right"))
    identical = len({texts[r] for r in held}) == 1
    lines.append((identical, f"the settings text is identical in {', '.join(n[r] for r in held)}" if identical
                  else f"the settings text differs between {', '.join(n[r] for r in held)}: compose gives them one text, so one was made another way"))
    parsed = {}
    for r in held:
        try:
            parsed[r] = jsonc(texts[r])
        except ValueError:
            lines.append((False, f"{n[r]}'s settings text does not parse"))
    section = lambda r, name: parsed[r].get(name) if isinstance(parsed.get(r), dict) and isinstance(parsed[r].get(name), dict) else {}
    secrets = {r: resolve(section(r, "bridge").get("secret"), c.get(r)) for r in roles}
    lines.append(secrets_agree(secret_places(c, n, secrets), n["bridge"]))
    rcon = env_of(c.get("game"), "CS2_RCONPW") or ""
    off = [r for r in ("game", "bridge") if not rcon or resolve(section(r, "server").get("rconPassword"), c.get(r)) != rcon]
    lines.append((not off, f"server.rconPassword as {both(('game', 'bridge'))} read it is {n['game']}'s CS2_RCONPW" if not off
                  else f"server.rconPassword is not {n['game']}'s CS2_RCONPW in: {', '.join(n[r] for r in off)}"))
    left = {r: old_programs(c.get(r)) for r in roles}
    lines.append((not any(left.values()), "no container mounts one of the older Python programs" if not any(left.values())
                  else "still mounted: " + "; ".join(f"{n[r]}: {', '.join(s)}" for r, s in left.items() if s)))
    no_folder = [r for r in runners if not any(target == PROGRAM_FOLDER for _source, target in mounted(c.get(r)))]
    lines.append((not no_folder, f"{both(runners)} mount the program folder at {PROGRAM_FOLDER}" if not no_folder
                  else f"no program folder at {PROGRAM_FOLDER} in: {', '.join(n[r] for r in no_folder)}"))
    return lines


# ------------------------------------------------------------------------------------------------------------------ the checks

class Check:
    def __init__(self, cfg, d=None, humans=None, a2s_info=None, run=None, fetch=None):
        self.cfg = cfg
        self.names = {"game": cfg.get("bridge.game_container", required=True), "bridge": cfg.get("bridge.container", required=True),
                      "sync": cfg.get("bridge.sync_container", "") or ""}
        self._d = d
        self.humans = humans or os2.humans
        self.a2s = a2s_info or a2s
        self.run = run or (lambda argv, env=None: subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                                                  env=env, timeout=600))
        self.fetch = fetch or self.get_json

    @property
    def d(self):
        if self._d is None and not docker_problem(self.cfg):
            self._d = os2.Docker(self.cfg)
        return self._d

    def get_json(self, url):
        """An HTTP GET with the Docker API's headers and TLS settings (the stack manager is usually the same server), as JSON."""
        d = self.d
        req = urllib.request.Request(url, headers=dict(d.headers) if d else {})
        with urllib.request.urlopen(req, context=d.ctx if d else None, timeout=30) as r:
            return json.loads(r.read())

    def git(self, repo, *args):
        r = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
        return r.stdout if r.returncode == 0 else None

    # ---- guard

    def guard(self, allow_kick):
        on = self.humans(self.cfg.get("server.address", required=True))   # no address: exit 2, never a go
        print(f"humans on the server: {'unknown (no A2S answer)' if on is None else on}")
        note = os2.lock_note(self.cfg)
        if note:
            print("  server lock held: " + note.replace("\n", " | ")[:300])
        print(f"  a redeploy of the stack may recreate every service in it, {self.names['game']} included, whatever changed: this decides by the players alone")
        problem = docker_problem(self.cfg) if self._d is None else ""
        if problem:
            print(f"  ({problem}: the drift is not shown - it decides nothing)")
        else:
            try:  # information only
                for line in self.drift():
                    print("  " + line)
            except Exception as e:
                print(f"  (could not read the drift: {e.__class__.__name__}: {str(e)[:200]})")
        print(f"  for the bridge alone: python deploy/bridge.py update ({self.names['game']} keeps running); for data files: nothing - the bridge reloads them")
        code, line = decide(on, allow_kick, self.names["game"])
        print(line)
        return code

    def drift(self):
        """The guard's information, line by line: the game server's image against its tag, and its service in the compose file, as last
        deployed against what a redeploy would take now (stack.ref, fetched first)."""
        d, name = self.d, self.names["game"]
        game = d.inspect("containers", name)
        if not game:
            yield f"no container {name} on the server"
            return
        tag = game["Config"]["Image"]
        tagged = d.inspect("images", tag) or {}
        yield (f"{name} image: container {game['Image'][7:19]}, tag {tag} -> {str(tagged.get('Id'))[7:19]}"
               + (" (the redeploy also moves it to the newer image)" if tagged and game["Image"] != tagged.get("Id") else ""))
        repo, file, ref = self.cfg.get("stack.repo", ""), self.cfg.get("stack.file", ""), self.cfg.get("stack.ref", "origin/main")
        url, field = self.cfg.get("stack.deployed_url", ""), self.cfg.get("stack.deployed_field", "")
        if not (repo and file):
            yield "(no stack.repo and stack.file in the private config: the compose file's drift is not shown)"
            return
        deployed = dotted(self.fetch(url), field).strip() if url and field else ""
        if not deployed:
            yield f"(the commit last deployed is not known{'' if url and field else ': no stack.deployed_url and stack.deployed_field'})"
            return
        if "/" in ref:
            self.git(repo, "fetch", "-q", ref.split("/", 1)[0])
        service = labels_of(game).get(SERVICE) or name
        path = file.replace("\\", "/")
        changed, alone = service_changed(self.git(repo, "show", f"{deployed}:{path}"), self.git(repo, "show", f"{ref}:{path}"), service)
        yield (f"the {service} service in {file}: deployed {deployed[:7]} vs {ref}: {'changed' if changed else 'unchanged'}"
               + ("" if alone else " (by the whole file: PyYAML is not installed, or a version could not be read)"))

    # ---- verify

    def verify(self):
        n = self.names
        problem = docker_problem(self.cfg) if self._d is None else ""
        if problem:
            print(f"LOOK  {problem} - nothing checked")
            print("RESULT: 1 to look at")
            return 1
        d, bad = self.d, []

        def say(ok, text):
            print(("note  " if ok is None else "ok    " if ok else "LOOK  ") + text)
            if ok is False:
                bad.append(text)

        note = os2.lock_note(self.cfg)
        if note:
            say(None, "the server lock is held - a test may have the server: " + note.replace("\n", " | ")[:300])

        # 1. the bridge on its image, in its stack
        image = self.cfg.get("bridge.image", "localhost/overstrike2-bridge")
        latest = d.inspect("images", f"{image}:latest") or {}
        b = d.inspect("containers", n["bridge"]) or {}
        game = d.inspect("containers", n["game"]) or {}
        sync = (d.inspect("containers", n["sync"]) or {}) if n["sync"] else None
        say(bool(b) and b.get("Image") == latest.get("Id"),
            f"{n['bridge']} runs {str(b.get('Image'))[7:19]} ({(b.get('Config') or {}).get('Image')}); {image}:latest is {str(latest.get('Id'))[7:19]}")
        state = b.get("State") or {}
        say(bool(b) and bool(state.get("Running")) and b.get("RestartCount", 0) == 0,
            f"{n['bridge']} state {state.get('Status')}, restarts {b.get('RestartCount')}, started {(state.get('StartedAt') or '')[:19]}Z")
        project, service, home = labels_of(b).get(PROJECT), labels_of(b).get(SERVICE), labels_of(game).get(PROJECT)
        say(bool(project) and project == home and bool(service),
            f"{n['bridge']} belongs to compose project {project!r}, service {service!r}" + ("" if project == home else f" - {n['game']}'s project is {home!r}"))

        # 2. the game server and the sync; the game server is production, not a test copy
        for name, c in ((n["game"], game), (n["sync"], sync)):
            if name:
                st = (c or {}).get("State") or {}
                say(bool(c) and bool(st.get("Running")),
                    f"{name} state {st.get('Status')}, started {(st.get('StartedAt') or '')[:19]}Z, image {((c or {}).get('Config') or {}).get('Image')}")
        if not n["sync"]:
            say(None, "no bridge.sync_container in the private config: a stack without the Workshop sync")
        label, aside = self.cfg.get("check.test_label", ""), [a for a in self.cfg.get("check.aside", []) or [] if a]
        if label:
            tests = [f"{name} ({labels_of(c).get(label)})" for name, c in ((n["game"], game), (n["bridge"], b), (n["sync"], sync)) if name and labels_of(c).get(label)]
            say(not tests, f"production: no {label} label on {', '.join(x for x in n.values() if x)}" if not tests else "a TEST COPY, not production: " + "; ".join(tests))
        if aside:
            found = {a: d.inspect("containers", a) for a in aside}
            there = [f"{a} ({(c.get('State') or {}).get('Status')})" for a, c in found.items() if c is not None]
            say(not there, f"nothing set aside: no {', '.join(aside)}" if not there else f"set aside for a test: {', '.join(there)} - production is not what runs")
        if not label and not aside:
            say(None, "no check.test_label or check.aside in the private config: a test copy shows only in A2S's VAC flag")
        group = self.cfg.get("check.mapgroup", MAPGROUP)
        if group:
            say(*mapgroup_ok(game, group, n["game"]))
        info = self.a2s(self.cfg.get("server.address", ""))
        say(info is not None, "the game server answers A2S: " + ("no answer" if info is None else f"humans {info[0]}, VAC {info[1]}"))
        say(info is not None and info[1] == 1, f"A2S says VAC-secured: {'yes' if info and info[1] == 1 else 'NO - an insecure server'}")

        # 3. the bridge's health from inside the game server (the path the log push takes)
        if b and game:
            secret = os2.bridge_secret(game, b)
            h = d.exec_in(n["game"], ["python3", "-c", bridge_mod.INNER_GET, f"http://{n['bridge']}:{os2.bridge_port(game, b)}/health/{secret}"]) or ""
            first = h.strip().splitlines()[0] if h.strip() else "(no answer)"
            m = re.search(r"log pushes (\d+)", h)
            say(h.startswith(bridge_mod.HEALTH_WORDS), f"bridge /health from {n['game']}: " + (first.replace(secret, "<secret>") if secret else first))
            say(bool(m) and int(m.group(1)) > 0, f"log pushes reaching the bridge: {m.group(1) if m else '?'} (0 right after a boot is normal for a minute)")

        # 4. the settings text and the secrets: in every place, the same - compared, never printed
        for ok, line in settings_lines({"game": game, "bridge": b, "sync": sync}, n):
            say(ok, line)

        # 5. the roll-call of the compose file, and 6. the deployment's own audits
        self.roll_call(d, say, game)
        self.audits(say)

        print("RESULT: " + ("ALL GOOD" if not bad else f"{len(bad)} to look at"))
        return 0 if not bad else 1

    def roll_call(self, d, say, game):
        """Every container_name in the compose file (stack.repo + stack.file, as it is on disk) against what runs, the names the private
        config gives among them; and, as a note, containers of the game server's compose project that the file does not declare."""
        repo, file = self.cfg.get("stack.repo", ""), self.cfg.get("stack.file", "")
        if not (repo and file):
            say(None, "no stack.repo and stack.file in the private config: no roll-call of the compose file")
            return
        try:
            with open(os.path.join(repo, *file.replace("\\", "/").split("/")), encoding="utf-8") as fh:
                declared = [m.group(1) for line in fh if (m := CONTAINER_NAME.match(line))]
        except OSError as e:
            say(False, f"roll-call: {file} cannot be read ({e.__class__.__name__})")
            return
        _status, listed = d.call("GET", "/containers/json?all=1")
        live = {c["Names"][0].lstrip("/"): c for c in (listed if isinstance(listed, list) else []) if c.get("Names")}
        undeclared = [x for x in self.names.values() if x and x not in declared]
        missing = [x for x in declared if x not in live]
        stopped = [f"{x} ({live[x].get('Status')})" for x in declared if x in live and live[x].get("State") != "running"]
        wrong = ([f"not declared there, though the private config names it: {', '.join(undeclared)}"] if undeclared else []) \
            + ([f"missing: {', '.join(missing)}"] if missing else []) + ([f"not running: {', '.join(stopped)}"] if stopped else [])
        say(not wrong, f"roll-call, {file}: " + ("; ".join(wrong) if wrong else f"{', '.join(declared) or 'nothing'} declared and running"))
        project = labels_of(game).get(PROJECT)
        extra = sorted(x for x, c in live.items() if project and (c.get("Labels") or {}).get(PROJECT) == project and x not in declared)
        if extra:
            say(None, f"in compose project {project!r} but declared nowhere in {file}: {', '.join(extra)}")

    def audits(self, say):
        """The deployment's own read-only audits, [[check.audit]] in the private config: name, argv ("python" first is this Python), env
        (variables the command needs; "env:NAME" read from this environment, never printed), and how its output is judged: look, a regex -
        a line that matches is something to look at, and then the exit code is only shown; done, a regex - the line that says the audit
        ran to its end. Without look the exit code decides: 0 is all good."""
        audits = self.cfg.get("check.audit", []) or []
        if not audits:
            say(None, "no [[check.audit]] in the private config: no audit of the deployment's own")
            return
        for a in audits:
            name = a.get("name") or "audit"
            argv = [sys.executable if i == 0 and x == "python" else str(x) for i, x in enumerate(a.get("argv") or [])]
            if not argv:
                say(False, f"{name}: no argv in the private config")
                continue
            env, unset = dict(os.environ), []
            for k, v in (a.get("env") or {}).items():
                value = os.environ.get(v[4:], "") if isinstance(v, str) and v.startswith("env:") else str(v)
                if not value:
                    unset.append(f"{k} ({v[4:]} is not set)" if isinstance(v, str) and v.startswith("env:") else f"{k} (empty)")
                env[k] = value
            if unset:
                say(False, f"{name}: not run - {', '.join(unset)}")
                continue
            try:
                r = self.run(argv, env)
            except (OSError, subprocess.SubprocessError) as e:
                say(False, f"{name}: not run ({e.__class__.__name__})")
                continue
            lines = [l.strip() for l in ((r.stdout or "") + "\n" + (r.stderr or "")).splitlines() if l.strip()]
            look, done = a.get("look"), a.get("done")
            ended = next((l for l in lines if re.search(done, l)), None) if done else None
            if done and ended is None:
                say(False, f"{name}: did not finish (exit {r.returncode})" + (f": {lines[-1][:200]}" if lines else ""))
            elif look:
                looked = [l for l in lines if re.search(look, l)]
                say(not looked, f"{name}: " + ("; ".join(looked) if looked else f"no line matches /{look}/"))
                say(None, f"{name}: exit {r.returncode}; {len(lines) - len(looked)} other line(s)" + (f"; {ended}" if ended else ""))
            else:
                say(r.returncode == 0, f"{name}: exit {r.returncode}" + (f"; {ended or lines[-1][:200]}" if lines else ""))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter, allow_abbrev=False)
    ap.add_argument("command", choices=["guard", "verify"])
    ap.add_argument("--allow-kick", action="store_true", help="guard: GO with players on - only when the owner said so for this redeploy")
    ap.add_argument("--config", help="the private config (default: $OVERSTRIKE2_CONFIG or .private/config.toml)")
    args = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    check = Check(os2.Config(args.config))
    if args.command == "guard":
        return check.guard(args.allow_kick)
    return check.verify()


if __name__ == "__main__":
    sys.exit(main())
