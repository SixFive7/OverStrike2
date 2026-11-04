#!/usr/bin/env python3
"""The bridge's image, built here and shipped to the server's Docker - no registry, no CI.

    python deploy/bridge.py check                 read-only: what would ship, the running bridge, players, the server lock
    python deploy/bridge.py build [--tag T]       build the committed bridge/ locally, save it, load it into the server's Docker
    python deploy/bridge.py build-remote [--tag T]   the same image, built by the server's Docker from an uploaded context
    python deploy/bridge.py images                read-only: the bridge image's tags on the server
    python deploy/bridge.py swap                  recreate the bridge container on <image>:candidate, verified; the game keeps running
    python deploy/bridge.py update                check, build, swap
    python deploy/bridge.py rollback              swap back to <image>:previous
    python deploy/bridge.py promote               point <image>:latest at <image>:candidate and touch no container: for a
                                                  redeploy of the whole stack (when the compose file's bridge service changed,
                                                  or for the first switch from a compose-built bridge to this image)

Tags on the server: <image>:<commit> for every build (immutable), <image>:candidate (the newest build, not yet running),
<image>:latest (what the compose file names and the container runs), <image>:previous (what the last swap replaced).
--tag builds <image>:<T> instead and leaves candidate alone (a test build).

The local build engine is build.engine in the private config: "wslc" (WSL containers) or "docker". The build runs at below
normal priority. What ships is the committed bridge/ at HEAD, never uncommitted files.

The swap does for the bridge alone what compose would do, so nobody is disconnected: the container is stopped, renamed aside,
created anew under its own name from its own inspect (environment, labels - compose's included, so it stays in its stack -
mounts, restart policy, read-only root, capabilities, user and its network settings; only the image differs) and started,
then verified: running, on the IP and MAC it is configured with, GET /health answering from inside the game container, a log push
arriving, and the game container untouched. Only then is the old container removed; on any failure it is put back as it was. A
stopped bridge is swapped the same way - checked against its configured network, not the runtime address it lacks - and put back
stopped; one already on the target image is started.
While the bridge is down (seconds) what it keeps only in memory is lost - the request queue, a countdown, airtime - so
pick a moment between matches. A later redeploy of the stack runs <image>:latest, which is the same image.
"""
import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.parse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import os2  # noqa: E402

BELOW_NORMAL, NO_WINDOW = 0x00004000, 0x08000000

INNER_GET = "import sys,urllib.request;print(urllib.request.urlopen(sys.argv[1],timeout=5).read().decode())"
INNER_RCON = r"""
import socket,struct,sys
def pkt(i,t,b): b=b.encode()+b"\0\0"; return struct.pack("<iii",len(b)+8,i,t)+b
s=socket.create_connection(("127.0.0.1",int(sys.argv[3])),5)
def read():
    n=struct.unpack("<i",s.recv(4))[0]; d=b""
    while len(d)<n: d+=s.recv(n-len(d))
    return struct.unpack("<ii",d[:8])+(d[8:-2].decode(errors="replace"),)
s.sendall(pkt(1,3,sys.argv[1])); r=read()
if r[1]==0: r=read()
if r[0]==-1: sys.exit("rcon auth refused")
s.sendall(pkt(2,2,sys.argv[2])); print(read()[2])
"""


class Bridge:
    def __init__(self, cfg):
        self.cfg, self.d = cfg, os2.Docker(cfg)
        self.repo = cfg.get("bridge.image", "localhost/overstrike2-bridge")
        self.name = cfg.get("bridge.container", required=True)
        self.game = cfg.get("bridge.game_container", required=True)

    # ---- what ships

    def context(self):
        """The committed bridge/ at HEAD as a tar, without what bridge/.dockerignore keeps out."""
        commit = os2.clean_tree(["bridge"])
        raw = subprocess.run(["git", "-C", os2.REPO, "archive", "--format=tar", "HEAD:bridge"], capture_output=True).stdout
        src = tarfile.open(fileobj=io.BytesIO(raw))
        names = {m.name for m in src.getmembers()}
        ignore = []
        if ".dockerignore" in names:
            ignore = [l.strip().rstrip("/") for l in src.extractfile(".dockerignore").read().decode().splitlines()
                      if l.strip() and not l.startswith("#")]
        out = io.BytesIO()
        with tarfile.open(fileobj=out, mode="w") as dst:
            for m in src.getmembers():
                if any(m.name == p or m.name.startswith(p + "/") for p in ignore):
                    continue
                dst.addfile(m, src.extractfile(m) if m.isfile() else None)
        os2.log(f"build context: the committed bridge/ at {commit} ({len(out.getvalue()) // 1024} KB), left out: {', '.join(ignore) or 'nothing'}")
        return commit, out.getvalue()

    # ---- build

    def build_local(self, tag):
        commit, ctx = self.context()
        engine = self.cfg.get("build.engine", "wslc")
        image = f"{self.repo}:{tag or commit}"  # a test build carries only its own tag
        os.makedirs(os.path.join(os2.REPO, ".work"), exist_ok=True)  # the repo's own scratch (gitignored)
        work = tempfile.mkdtemp(prefix="bridge-build-", dir=os.path.join(os2.REPO, ".work"))
        tarfile.open(fileobj=io.BytesIO(ctx)).extractall(work, filter="data")
        archive = os.path.join(work, "image.tar")
        flags = BELOW_NORMAL | NO_WINDOW if os.name == "nt" else 0
        run = lambda argv, what: (os2.log("  " + " ".join(argv[:2]) + " ..."),
                                  subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=flags))[1]
        os2.log(f"building {image} locally with {engine} (below normal priority)")
        t0 = time.time()
        r = run([engine, "build", "-t", image, "-l", f"org.opencontainers.image.revision={commit}", "-l", "org.opencontainers.image.title=Over Strike 2 bridge", work], "build")
        if r.returncode != 0:
            os2.fail(f"{engine} build: exit {r.returncode}\n" + (r.stdout + r.stderr)[-3000:])
        os2.log(f"built in {time.time() - t0:.0f} s; saving")
        r = run([engine, "save", "-o", archive, image], "save")
        if r.returncode != 0:
            os2.fail(f"{engine} save: exit {r.returncode}\n" + (r.stdout + r.stderr)[-2000:])
        size = os.path.getsize(archive)
        os2.log(f"image archive {size / 1e6:.1f} MB; loading it into the server's Docker")
        with open(archive, "rb") as fh:
            lines = self.d.stream("POST", "/images/load?quiet=1", fh.read(), "application/x-tar")
        errors = [l["error"] for l in lines if l.get("error")]
        if errors:
            os2.fail("image load: " + "; ".join(errors))
        os2.log("server: " + ("; ".join((l.get("stream") or "").strip() for l in lines if l.get("stream")) or "loaded"))
        self.finish(commit, tag, image)
        shutil.rmtree(work, ignore_errors=True)
        return commit

    def build_remote(self, tag):
        commit, ctx = self.context()
        image = f"{self.repo}:{tag or commit}"
        q = {"t": image, "rm": "1", "forcerm": "1",
             "labels": json.dumps({"org.opencontainers.image.revision": commit, "org.opencontainers.image.title": "Over Strike 2 bridge"})}
        if self.cfg.get("build.network_host", False):
            q["networkmode"] = "host"
        os2.log(f"building {image} on the server - nothing restarts")
        lines = self.d.stream("POST", "/build?" + urllib.parse.urlencode(q), ctx, "application/x-tar")
        errors = [l["error"] for l in lines if l.get("error")]
        if errors:
            tail = [(l.get("stream") or "").strip() for l in lines if l.get("stream")][-12:]
            os2.fail("build: " + "; ".join(errors) + "\n" + "\n".join(tail))
        self.finish(commit, tag, image)
        return commit

    def finish(self, commit, tag, image):
        built = self.d.inspect("images", image)
        if not built:
            os2.fail(f"{image} is not on the server after the build")
        if not tag:  # a release build: the candidate is the newest build, for swap
            self.d.must("POST", f"/images/{built['Id']}/tag?repo={self.repo}&tag=candidate", f"tag {self.repo}:candidate")
        labels = (built.get("Config") or {}).get("Labels") or {}
        os2.log(f"on the server: {image}{'' if tag else f' = {self.repo}:candidate'} = {built['Id'][7:19]}, "
                f"{built.get('Size', 0) / 1e6:.0f} MB, revision {labels.get('org.opencontainers.image.revision', '?')}")

    # ---- read-only

    def check(self):
        commit = os2.git("rev-parse", "--short=12", "HEAD")
        dirty = os2.git("status", "--porcelain", "--", "bridge")
        os2.log(f"would ship: the committed bridge/ at {commit}" + (" (uncommitted changes in bridge/ are NOT shipped - commit first)" if dirty else ""))
        address = self.cfg.get("server.address", "")
        if address:
            on = os2.humans(address)
            os2.log(f"humans on the server: {'unknown (no A2S answer)' if on is None else on} - a swap disconnects nobody")
        note = os2.lock_note(self.cfg)
        if note:
            os2.log("server lock: " + note.replace("\n", " | ")[:300])
        b = self.d.inspect("containers", self.name)
        if not b:
            os2.fail(f"no container {self.name} on the server")
        img = self.d.inspect("images", b["Image"]) or {}
        rev = ((img.get("Config") or {}).get("Labels") or {}).get("org.opencontainers.image.revision", "?")
        os2.log(f"running bridge: container {b['Id'][:12]}, image {b['Image'][7:19]} ({b['Config']['Image']}, revision {rev}), "
                f"started {b['State']['StartedAt'][:19]}Z")
        return commit

    def images(self):
        s, imgs = self.d.call("GET", "/images/json?filters=" + urllib.parse.quote(json.dumps({"reference": [self.repo]})))
        for i in imgs if isinstance(imgs, list) else []:
            os2.log(f"{i['Id'][7:19]}  {', '.join(i.get('RepoTags') or [])}  {i.get('Size', 0) / 1e6:.0f} MB  "
                    f"created {time.strftime('%Y-%m-%d %H:%M', time.localtime(i.get('Created', 0)))}")

    # ---- swap

    def create_body(self, old, old_image, new_image_id):
        c, h = old["Config"], old["HostConfig"]
        img = old_image.get("Config") or {}
        config = {
            "Image": c.get("Image") or self.repo,  # the reference compose names; it points at the new image by now
            "Env": [e for e in c.get("Env") or [] if e not in set(img.get("Env") or [])],  # what compose set; the image brings its own
            # every label, the old image's included: compose stamps project and service on the images IT builds, and without
            # them the container would drop out of its stack. The old image's own description goes, so the new one's shows.
            "Labels": {**{k: v for k, v in (c.get("Labels") or {}).items() if not k.startswith("org.opencontainers.image.")},
                       "com.docker.compose.image": new_image_id},
        }
        for k in ("User", "ExposedPorts", "StopTimeout", "AttachStdout", "AttachStderr", "Tty", "OpenStdin", "Domainname"):
            if c.get(k) not in (None, "", {}, [], False):
                config[k] = c[k]
        for k in ("Entrypoint", "Cmd", "WorkingDir", "Healthcheck", "StopSignal"):  # only where compose overrode the image
            if c.get(k) is not None and c.get(k) != img.get(k):
                config[k] = c[k]
        endpoints = {}
        for net, v in old["NetworkSettings"]["Networks"].items():
            e = {k: v[k] for k in ("IPAMConfig", "Aliases", "MacAddress", "DriverOpts", "Links") if v.get(k)}
            if not e.get("MacAddress") and c.get("MacAddress"):
                e["MacAddress"] = c["MacAddress"]   # an older engine keeps a stopped container's MAC in its config only
            endpoints[net] = e
        return {**config, "HostConfig": h, "NetworkingConfig": {"EndpointsConfig": endpoints}}

    @staticmethod
    def addresses(c):
        """[(network, IP, MAC)] a container is on now: its runtime address, which a stopped container does not have."""
        return sorted((n, v.get("IPAddress"), v.get("MacAddress")) for n, v in ((c or {}).get("NetworkSettings", {}).get("Networks") or {}).items())

    @staticmethod
    def pinned(c):
        """[(network, IP, MAC)] a container is configured with: its IPAM address and its MAC - what a stopped container still names
        while its runtime address is empty, so a swap of a stopped bridge has something to recreate it on and to check it against."""
        cfg_mac = ((c or {}).get("Config") or {}).get("MacAddress") or ""
        return sorted((n, (v.get("IPAMConfig") or {}).get("IPv4Address") or v.get("IPAddress") or "", v.get("MacAddress") or cfg_mac)
                      for n, v in ((c or {}).get("NetworkSettings", {}).get("Networks") or {}).items())

    def health(self, bridge):
        port = os2.env_of(bridge, "HTTP_PORT") or "8080"
        return self.d.exec_in(self.game, ["python3", "-c", INNER_GET, f"http://{self.name}:{port}/health/{os2.env_of(bridge, 'LOG_SECRET')}"])

    def verify(self, game_before, want):
        problems = []
        time.sleep(8)
        new = self.d.inspect("containers", self.name)
        if not new or not new["State"]["Running"] or new["RestartCount"]:
            return [f"not running ({(new or {}).get('State', {}).get('Status', 'gone')}, restarts {(new or {}).get('RestartCount')})"]
        got = self.addresses(new)
        (os2.log if got == want else problems.append)(f"{self.name} on {got}" + ("" if got == want else f" - it was on {want}"))
        game = self.d.inspect("containers", self.game)
        if (game["Id"], game["State"]["StartedAt"]) != game_before:
            problems.append(f"{self.game} is not the container it was before - it restarted")
        else:
            os2.log(f"{self.game} untouched: container {game['Id'][:12]}, started {game['State']['StartedAt'][:19]}Z")
        h = self.health(new)
        if not h.startswith("gg-health"):
            return problems + ["GET /health from the game container: " + h.strip()[:200]]
        pushes = lambda text: int(m.group(1)) if (m := re.search(r"log pushes (\d+)", text)) else 0
        if pushes(h) == 0:  # say something, so the server has a line to push
            port = os2.env_of(game, "RCON_PORT") or "27015"
            self.d.exec_in(self.game, ["python3", "-c", INNER_RCON, os2.env_of(new, "RCON_PASSWORD"), "say The map bot was updated.", port])
            for _ in range(12):
                time.sleep(5)
                if pushes(h := self.health(new)):
                    break
        os2.log("bridge /health:\n  " + "\n  ".join(h.strip().splitlines()[1:]))
        if pushes(h) == 0:
            problems.append("no log push has reached the new bridge within a minute")
        return problems

    def swap(self, source_tag, keep_tag):
        old = self.d.inspect("containers", self.name)
        if not old:
            os2.fail(f"no container {self.name}")
        target = self.d.inspect("images", f"{self.repo}:{source_tag}")
        if not target:
            os2.fail(f"no image {self.repo}:{source_tag} on the server")
        # a stopped bridge is swapped too (2026-11-04: a release stopped it to edit its state): what it is checked against is the
        # network it is configured with, not the runtime address it has only while it runs - and it is put back stopped
        running = bool((old.get("State") or {}).get("Running"))
        want = self.pinned(old)
        if not want or any(not ip or not mac for _net, ip, mac in want):
            os2.fail(f"{self.name} names no pinned IP and MAC to recreate it on ({want}) - nothing changed; "
                     f"{'start it once, or ' if not running else ''}redeploy the stack")
        game = self.d.inspect("containers", self.game)
        game_before = (game["Id"], game["State"]["StartedAt"])
        if target["Id"] == old["Image"]:
            if running:
                os2.log(f"{self.name} already runs {self.repo}:{source_tag} ({target['Id'][7:19]}) - nothing to do")
                return 0
            os2.log(f"{self.name} is stopped on {self.repo}:{source_tag} ({target['Id'][7:19]}) already - starting it")
            self.d.must("POST", f"/containers/{old['Id']}/start", "start the bridge")
            problems = self.verify(game_before, want)
            if not problems:
                os2.log(f"DONE: {self.name} runs {target['Id'][7:19]}")
                return 0
            os2.log("PROBLEMS: " + "; ".join(problems) + " - stopping it again, as it was")
            self.d.call("POST", f"/containers/{old['Id']}/stop?t=10", timeout=60)
            return 1
        if not running:
            os2.log(f"{self.name} is stopped - swapped all the same, onto the network it is configured with: {want}")
        old_image = self.d.inspect("images", old["Image"]) or {}
        body = self.create_body(old, old_image, target["Id"])
        running_ref = (old["Config"].get("Image") or self.repo)
        ref_repo, _, ref_tag = running_ref.partition(":")
        ref_tag = ref_tag or "latest"
        self.d.must("POST", f"/images/{old['Image']}/tag?repo={self.repo}&tag={keep_tag}", f"tag the old image {self.repo}:{keep_tag}")
        self.d.must("POST", f"/images/{target['Id']}/tag?repo={ref_repo}&tag={ref_tag}", f"tag {running_ref}")
        os2.log(f"{self.repo}:{keep_tag} = {old['Image'][7:19]} ({'running' if running else 'stopped'}), "
                f"{ref_repo}:{ref_tag} = {target['Id'][7:19]} (new)")
        aside = f"{self.name}-replaced-{time.strftime('%H%M%S')}"
        new_id = None
        try:
            if running:
                self.d.must("POST", f"/containers/{old['Id']}/stop?t=15", "stop the old bridge", timeout=60)
            self.d.must("POST", f"/containers/{old['Id']}/rename?name={aside}", "rename the old bridge aside")
            new_id = self.d.must("POST", f"/containers/create?name={self.name}", "create the new bridge", body=body)["Id"]
            self.d.must("POST", f"/containers/{new_id}/start", "start the new bridge")
            os2.log(f"new {self.name} {new_id[:12]} started (the old one is stopped, as {aside})")
            problems = self.verify(game_before, want)
        except SystemExit as e:
            problems = [str(e)]
        if not problems:
            self.d.must("DELETE", f"/containers/{old['Id']}", "remove the old bridge")
            os2.log(f"DONE: {self.name} runs {target['Id'][7:19]}; rollback: python deploy/bridge.py rollback")
            return 0
        os2.log("PROBLEMS: " + "; ".join(problems) + " - putting the old bridge back")
        if new_id:
            self.d.call("POST", f"/containers/{new_id}/stop?t=10", timeout=60)
            self.d.call("DELETE", f"/containers/{new_id}?force=1")
        self.d.call("POST", f"/containers/{old['Id']}/rename?name={self.name}")
        self.d.call("POST", f"/images/{old['Image']}/tag?repo={ref_repo}&tag={ref_tag}")
        if not running:
            back = self.d.inspect("containers", self.name)
            os2.log(f"old bridge {'back, stopped as it was' if back and back['Id'] == old['Id'] else 'NOT back under its name - rename it by hand'}")
            return 1
        s, _ = self.d.call("POST", f"/containers/{old['Id']}/start")
        back = self.d.inspect("containers", self.name)
        os2.log(f"old bridge {'back up' if back and back['Id'] == old['Id'] and back['State']['Running'] else 'NOT back - start it by hand'} (start HTTP {s})")
        return 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["check", "build", "build-remote", "images", "swap", "update", "rollback", "promote"])
    ap.add_argument("--tag", help="build <image>:<TAG> (a test build) instead of <image>:candidate")
    ap.add_argument("--config", help="the private config (default: $OVERSTRIKE2_CONFIG or .private/config.toml)")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    b = Bridge(os2.Config(args.config))
    if args.command == "check":
        b.check()
    elif args.command == "images":
        b.images()
    elif args.command == "build":
        b.build_local(args.tag)
    elif args.command == "build-remote":
        b.build_remote(args.tag)
    elif args.command == "swap":
        return b.swap("candidate", "previous")
    elif args.command == "update":
        b.check()
        b.build_local(None)
        return b.swap("candidate", "previous")
    elif args.command == "rollback":
        return b.swap("previous", "undone")
    elif args.command == "promote":
        cand = b.d.inspect("images", f"{b.repo}:candidate")
        if not cand:
            os2.fail(f"no {b.repo}:candidate on the server - build first")
        old = b.d.inspect("images", f"{b.repo}:latest")
        if old and old["Id"] != cand["Id"]:
            b.d.must("POST", f"/images/{old['Id']}/tag?repo={b.repo}&tag=previous", f"tag {b.repo}:previous")
        b.d.must("POST", f"/images/{cand['Id']}/tag?repo={b.repo}&tag=latest", f"tag {b.repo}:latest")
        os2.log(f"{b.repo}:latest = {cand['Id'][7:19]}" + (f", {b.repo}:previous = {old['Id'][7:19]}" if old and old["Id"] != cand["Id"] else "")
                + " - no container changed; a redeploy of the stack now runs it")
    return 0


if __name__ == "__main__":
    sys.exit(main())
