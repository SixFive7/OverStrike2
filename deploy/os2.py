"""Shared plumbing for the deploy scripts: the private config, the Docker Engine API, the player check, careful copies.

Nothing here knows a deployment. Every address, path, name and credential comes from the private config (TOML), found at
$OVERSTRIKE2_CONFIG, else .private/config.toml in this checkout (gitignored). deploy/config.example.toml shows every key.
A value written "env:NAME" is read from the environment variable NAME, so credentials stay out of the file too.
Nothing printed here contains a credential.
"""
import filecmp
import hashlib
import json
import os
import shutil
import socket
import ssl
import subprocess
import sys
import time
import tomllib
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def fail(msg, code=1):
    log("FAILED: " + msg)
    sys.exit(code)


# ------------------------------------------------------------------------------------------------------------- the config

class Config:
    def __init__(self, path=None):
        self.path = path or os.environ.get("OVERSTRIKE2_CONFIG") or os.path.join(REPO, ".private", "config.toml")
        if not os.path.exists(self.path):
            fail(f"no private config at {self.path}: copy deploy/config.example.toml there (or set OVERSTRIKE2_CONFIG) and fill it in", 2)
        with open(self.path, "rb") as fh:
            self.data = tomllib.load(fh)

    def get(self, dotted, default=None, required=False):
        node = self.data
        for part in dotted.split("."):
            if not isinstance(node, dict) or part not in node:
                if required:
                    fail(f"{self.path} has no '{dotted}'", 2)
                return default
            node = node[part]
        if isinstance(node, str) and node.startswith("env:"):
            value = os.environ.get(node[4:], "")
            if required and not value:
                fail(f"'{dotted}' reads the environment variable {node[4:]}, which is not set", 2)
            return value
        if required and node in ("", None):
            fail(f"'{dotted}' is empty in {self.path}", 2)
        return node

    def site_host(self):
        return self.get("site.url", required=True).split("://")[-1].rstrip("/")


# ------------------------------------------------------------------------------------------------------ the Docker API

class Docker:
    """A Docker Engine API base URL - the daemon's own, or a proxy that forwards it - plus any headers the proxy wants."""

    def __init__(self, cfg):
        self.base = cfg.get("docker.api", required=True).rstrip("/")
        self.headers = {k: (os.environ.get(v[4:], "") if isinstance(v, str) and v.startswith("env:") else v)
                        for k, v in (cfg.get("docker.headers", {}) or {}).items()}
        for k, v in self.headers.items():
            if not v:
                fail(f"docker.headers.{k} is empty (its environment variable is not set?)", 2)
        self.ctx = ssl.create_default_context()
        if not cfg.get("docker.tls_verify", True):
            self.ctx.check_hostname = False
            self.ctx.verify_mode = ssl.CERT_NONE

    def call(self, method, path, body=None, raw=None, ctype="application/json", timeout=60):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        r = urllib.request.Request(self.base + path, data=data, method=method, headers={**self.headers, "Content-Type": ctype})
        try:
            with urllib.request.urlopen(r, context=self.ctx, timeout=timeout) as resp:
                out = resp.read()
                return resp.status, (json.loads(out) if out[:1] in (b"{", b"[") else out)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode(errors="replace")

    def must(self, method, path, what, ok=(200, 201, 204, 304), **kw):
        status, out = self.call(method, path, **kw)
        if status not in ok:
            fail(f"{what}: HTTP {status} {str(out)[:300]}")
        return out

    def stream(self, method, path, raw, ctype, timeout=1800):
        """POST a large body (a build context, an image archive) and read the JSON-lines progress the daemon answers with."""
        r = urllib.request.Request(self.base + path, data=raw, method=method, headers={**self.headers, "Content-Type": ctype})
        lines = []
        with urllib.request.urlopen(r, context=self.ctx, timeout=timeout) as resp:
            for line in resp:
                try:
                    lines.append(json.loads(line))
                except ValueError:
                    pass
        return lines

    def inspect(self, kind, name):
        status, out = self.call("GET", f"/{kind}/{name}/json")  # unquoted on purpose: some proxies refuse an encoded slash
        return out if status == 200 else None

    def exec_in(self, container, argv, timeout=60):
        status, e = self.call("POST", f"/containers/{container}/exec", {"AttachStdout": True, "AttachStderr": True, "Cmd": argv})
        if status not in (200, 201):
            return f"(exec create {status})"
        status, raw = self.call("POST", f"/exec/{e['Id']}/start", {"Detach": False, "Tty": False}, timeout=timeout)
        if not isinstance(raw, bytes):
            return f"(exec start {status})"
        out, i = [], 0
        while i + 8 <= len(raw):
            n = int.from_bytes(raw[i + 4:i + 8], "big")
            out.append(raw[i + 8:i + 8 + n])
            i += 8 + n
        return (b"".join(out) if out else raw).decode("utf-8", "replace")


def env_of(container, key):
    for e in container["Config"].get("Env") or []:
        if e.startswith(key + "="):
            return e.split("=", 1)[1]
    return ""


# -------------------------------------------------------------------------------------------------- is anybody playing?

def humans(address):
    """Players on the server minus bots, by an A2S_INFO query; None when it does not answer."""
    host, _, port = address.rpartition(":")
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
        return d[o + 2] - d[o + 4]
    except Exception:
        return None


def lock_note(cfg):
    path = cfg.get("server.lock_file", "")
    if path and os.path.exists(path):
        return open(path, encoding="utf-8").read().strip()
    return None


# ----------------------------------------------------------------------------------------------------------- careful copies

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_verified(src, dst, dry=False):
    """Copy when the destination lacks the file or holds other bytes; compare after. Returns 'same', 'copied' or 'would copy'."""
    if os.path.exists(dst) and filecmp.cmp(src, dst, shallow=False):
        return "same"
    if dry:
        return "would copy"
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    if not filecmp.cmp(src, dst, shallow=False):
        fail(f"{dst} is not byte-identical to {src} after the copy")
    return "copied"


def git(*args):
    return subprocess.run(["git", "-C", REPO, *args], capture_output=True, text=True, encoding="utf-8").stdout.strip()


def clean_tree(paths):
    """The deploy ships committed bytes only: refuse when any of these paths has uncommitted changes."""
    dirty = git("status", "--porcelain", "--", *paths)
    if dirty:
        fail("uncommitted changes in what would ship - commit them first:\n" + dirty)
    return git("rev-parse", "--short=12", "HEAD")
