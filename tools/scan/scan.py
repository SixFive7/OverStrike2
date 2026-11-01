#!/usr/bin/env python3
"""scan.py - keeps secrets, addresses and private details out of this public repository.

    python tools/scan/scan.py                  the working tree: every file git would commit (tracked, and untracked but not ignored)
    python tools/scan/scan.py --staged         what is staged (the pre-commit hook)
    python tools/scan/scan.py --history        every blob of every commit, every commit message and identity, every tag
    python tools/scan/scan.py --range A..B     the commits in A..B (the pre-push hook)
    options: --report FILE (also write the findings there)   --no-gitleaks   --public-only

Exit codes: 0 clean, 1 findings, 2 the scan could not run (gitleaks missing, or the private denylist missing without
--public-only). The hooks treat anything but 0 as a stop.

Three layers, all of which must pass:
  1. gitleaks (installed separately): secrets by their known formats and by entropy.
  2. The public rules in this file: any IPv4/IPv6 address, MAC address, host path (/mnt/...), network share, local user path,
     Steam id, e-mail address, token-shaped secret, IANA time zone, phone or national id number, mention of an internet
     provider or a compose hosts block, clock time beside a date, quoted words of the owner, compiled Valve file (*_c, .vpk),
     and any binary file not reviewed in allow.txt.
     Documentation addresses (RFC 5737, RFC 3849), loopback and example.com are allowed by design.
  3. The private denylist: the deployment's own names, domains, addresses and secrets. It lives outside this repository
     (.private/scan-deny.txt, gitignored, or the file OVERSTRIKE2_SCAN_DENY names), because listing those values here would
     publish them. Secrets are listed there only as SHA-256 hashes.

Reviewed exceptions to the public rules: tools/scan/allow.txt (one per line; each with its reason).
"""
import argparse
import fnmatch
import glob
import hashlib
import ipaddress
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True).stdout.strip() or os.getcwd()
HERE = os.path.dirname(os.path.abspath(__file__))
ALLOW_FILE = os.path.join(HERE, "allow.txt")
DENY_FILE = os.environ.get("OVERSTRIKE2_SCAN_DENY") or os.path.join(ROOT, ".private", "scan-deny.txt")
MAX_LINE = 400  # report excerpt length

# ---------------------------------------------------------------------------------------------------------- public rules

RE_IPV4 = re.compile(r"(?<![\w.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\w]|\.\d)")
RE_IPV6_CAND = re.compile(r"(?<![\w:.])[0-9A-Fa-f:]{2,39}(?:%\w+)?(?![\w:])")
RE_MAC = re.compile(r"(?i)(?<![\w:-])[0-9a-f]{2}([:-])[0-9a-f]{2}(?:\1[0-9a-f]{2}){4}(?![\w:-])")
RULES = [
    ("host-path", re.compile(r"/mnt/[\w.-]|/boot/config\b|/var/lib/docker\b")),
    ("share", re.compile(r"(?<![\\\w])\\\\[A-Za-z0-9][\w.-]*\\[\w$.-]+|\bsmb://\S+")),
    ("user-path", re.compile(r"(?i)\b[a-z]:[\\/]{1,2}(?:users|source|home)[\\/]|(?<![\w.])/[a-z]/(?:users|source)/|/home/(?!steam\b)[a-z][\w.-]*/")),
    ("drive-path", re.compile(r"(?i)(?<![\w/\\])[d-z]:[\\/]{1,2}[\w ]")),
    ("steam64", re.compile(r"(?<!\d)7656119\d{10}(?!\d)")),
    ("steamid", re.compile(r"\[U:1:\d{6,}\]|\bSTEAM_[0-5]:[01]:\d{5,}\b")),
    ("e-mail", re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[A-Za-z]{2,}")),
    ("token", re.compile(r"\bptr_[A-Za-z0-9+/=]{16,}|(?<![0-9a-fA-F])[0-9a-f]{32}(?![0-9a-fA-F])|(?<![0-9a-fA-F])[0-9A-F]{32}(?![0-9a-fA-F])|"
                         r"-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}|\bAKIA[0-9A-Z]{16}\b")),
    # a 16-hex value with letters and digits: the shape of a server instance token or a short key
    ("hex16", re.compile(r"(?<![0-9a-fA-F])(?=[0-9a-f]{0,15}[a-f])(?=[0-9a-f]{0,15}[0-9])[0-9a-f]{16}(?![0-9a-fA-F])")),
    # an IANA time zone: it says where the deployment is (the code defaults to UTC; the tests set their own zone)
    ("timezone", re.compile(r"\b(?:Africa|America|Antarctica|Asia|Atlantic|Australia|Europe|Indian|Pacific)/[A-Z][A-Za-z_]+(?:/[A-Z][A-Za-z_]+)?")),
    # personal data in other people's text: phone numbers (international, or a Chinese mobile), national id numbers
    ("phone", re.compile(r"(?<![\w+])\+\d{1,3}[ .-]?\(?\d{1,4}\)?(?:[ .-]?\d{2,4}){2,4}(?![\w])|(?<![\w])1[3-9]\d{9}(?![\w])")),
    ("id-number", re.compile(r"(?<![\w])(?<!ugc/)\d{17}[\dXx](?![\w])")),  # not a Steam CDN path (images.../ugc/<18 digits>/)
    # how a deployment reaches Steam's content servers (whose caches it uses, a DNS override in its compose file) is the
    # deployment's business, not the code's: any internet provider, a compose hosts block, a numbered cache host name
    ("cdn-route", re.compile(r"(?i)\bISPs?\b|\bextra_hosts\b|\bcache\d+-[\w{]|\bprovider'?s? caches?\b")),
    # a clock time beside a date (ISO, or a Valve log line's), a local one, or one "at HH:MM:SS": it says when people played, and
    # beside a public time, the deployment's zone. Fixtures use made-up ones (allow.txt says which).
    ("clock-time", re.compile(r"\b20\d\d-[01]\d-[0-3]\d[ T][0-2]\d:[0-5]\d|\b[0-2]?\d:[0-5]\d(?::[0-5]\d)? on 20\d\d-[01]\d-[0-3]\d\b"
                              r"|\b[0-2]?\d:[0-5]\d(?::[0-5]\d)? local\b|\b[01]\d/[0-3]\d/20\d\d - [0-2]\d:[0-5]\d"
                              r"|\bat [0-2]?\d:[0-5]\d:[0-5]\d\b")),
    # the owner's own words, quoted in a comment (a decision is described, never quoted)
    ("quote", re.compile(r"\b[Tt]he owner's\s*\"|\b[Tt]he owner(?:'s \w+)?(?:, 20\d\d-[01]\d-[0-3]\d)?:\s*\"|\b(?:user|owner's) decision [\d-]+ \""
                         r"|\"\s*\((?:the )?owner\)|\b[Tt]he owner's\b[^\n\"]{0,80}?:\s*[\"“]")),
]
DOC_V4 = [ipaddress.ip_network(n) for n in ("192.0.2.0/24", "198.51.100.0/24", "203.0.113.0/24")]
DOC_V6 = ipaddress.ip_network("2001:db8::/32")
VALVE_COMPILED = re.compile(r"(?i)(_c|\.vpk)$")
SKIP_TEXT_RULES_FOR_BINARY = True


def ipv4_ok(text):
    a = ipaddress.ip_address(text)
    return a.is_loopback or a.is_unspecified or any(a in n for n in DOC_V4)


def ipv6_hits(line):
    for m in RE_IPV6_CAND.finditer(line):
        t = m.group(0)
        if "::" not in t and t.count(":") != 7:
            continue
        try:
            a = ipaddress.IPv6Address(t.split("%")[0])
        except ValueError:
            continue
        if a.is_loopback or a.is_unspecified or a in DOC_V6:
            continue
        yield m


def email_ok(text):
    domain = text.rsplit("@", 1)[1].lower()
    return domain in ("example.com", "example.org", "example.net") or domain.endswith((".example", ".invalid", ".test", ".localhost"))


# ------------------------------------------------------------------------------------------------------- allow and deny

class Allow:
    """allow.txt: '<rule> <path glob> <regex for the matched text>  # reason', or 'binary <path> <sha256>  # reason'."""

    def __init__(self, path, origin):
        self.rules, self.binaries, self.origin = [], {}, origin
        if not os.path.exists(path):
            return
        for n, raw in enumerate(open(path, encoding="utf-8"), 1):
            line = raw.split("  #", 1)[0].strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split(None, 2)
            if parts[0] == "binary" and len(parts) == 3:
                self.binaries[(parts[1], parts[2].lower())] = n
            elif len(parts) == 3:
                self.rules.append((parts[0], parts[1], re.compile(parts[2]), n))
            else:
                raise SystemExit(f"{path}:{n}: cannot read '{raw.strip()}'")

    def allows(self, rule, path, text):
        return any((r == rule or r == "*") and fnmatch.fnmatch(path, g) and rx.fullmatch(text) for r, g, rx, _ in self.rules)


class Deny:
    """The private denylist. Lines: 'word <text>' (case-insensitive substring), 're <regex>' (case-insensitive unless the regex
    says (?-i:...)), 'sha256 <hex> <token regex>' (tokens matching the regex whose SHA-256 is <hex>: a secret, never written out),
    'allow <path glob> <regex>' (a reviewed exception to the private rules, kept private because it names the denied text)."""

    def __init__(self, path):
        self.patterns, self.hashes, self.allow = [], [], []
        self.loaded = os.path.exists(path)
        if not self.loaded:
            return
        for n, raw in enumerate(open(path, encoding="utf-8"), 1):
            line = raw.split("  #", 1)[0].strip()
            if not line or line.startswith("#"):
                continue
            kind, _, rest = line.partition(" ")
            rest = rest.strip()
            if kind == "word":
                self.patterns.append((f"private:{n}", re.compile(re.escape(rest), re.I)))
            elif kind == "re":
                self.patterns.append((f"private:{n}", re.compile(rest, re.I)))
            elif kind == "sha256":
                digest, rx = rest.split(None, 1)
                self.hashes.append((f"private-secret:{n}", digest.lower(), re.compile(rx)))
            elif kind == "allow":
                glob_, rx = rest.split(None, 1)
                self.allow.append((glob_, re.compile(rx)))
            else:
                raise SystemExit(f"{path}:{n}: unknown kind '{kind}'")

    def allowed(self, path, text):
        return any(fnmatch.fnmatch(path, g) and rx.fullmatch(text) for g, rx in self.allow)


# ------------------------------------------------------------------------------------------------------------ the scan

class Scan:
    def __init__(self, allow, deny):
        self.allow, self.deny, self.findings, self.items = allow, deny, [], 0

    def add(self, where, rule, text, line, secret=False):
        shown = "<redacted>" if secret else text
        excerpt = line.strip()
        if secret:
            excerpt = excerpt.replace(text, "<redacted>")
        self.findings.append(f"{where}: {rule}: {shown}  |  {excerpt[:MAX_LINE]}")

    def text(self, path, content, where=None):
        """Every rule over one text: a file's content, a commit message, an identity."""
        self.items += 1
        own = path.replace("\\", "/") == "tools/scan/allow.txt"
        for n, line in enumerate(content.splitlines(), 1):
            at = f"{where or path}:{n}"
            if own:  # an entry of the exceptions file names the very text it allows: that is the entry, not a leak
                entry = line.split("  #", 1)[0].split(None, 2)
                if len(entry) == 3 and entry[0] != "binary":
                    try:
                        rx = re.compile(entry[2])
                    except re.error:
                        rx = None
                    line = rx.sub("<allowed>", line) if rx else line
            for m in RE_IPV4.finditer(line):
                if not ipv4_ok(m.group(0)) and not self.allow.allows("ipv4", path, m.group(0)):
                    self.add(at, "ipv4", m.group(0), line)
            for m in ipv6_hits(line):
                if not self.allow.allows("ipv6", path, m.group(0)):
                    self.add(at, "ipv6", m.group(0), line)
            for m in RE_MAC.finditer(line):
                if not self.allow.allows("mac", path, m.group(0)):
                    self.add(at, "mac", m.group(0), line)
            for rule, rx in RULES:
                for m in rx.finditer(line):
                    t = m.group(0)
                    if rule == "e-mail" and email_ok(t):
                        continue
                    if not self.allow.allows(rule, path, t):
                        self.add(at, rule, t, line, secret=(rule == "token"))
            for rule, rx in self.deny.patterns:
                for m in rx.finditer(line):
                    if not self.deny.allowed(path, m.group(0)):
                        self.add(at, rule, m.group(0), line)
            for rule, digest, rx in self.deny.hashes:
                for m in rx.finditer(line):
                    if hashlib.sha256(m.group(0).encode()).hexdigest() == digest:
                        self.add(at, rule, m.group(0), line, secret=True)

    def blob(self, path, data, where=None):
        if VALVE_COMPILED.search(path):
            self.findings.append(f"{where or path}: valve-compiled: a compiled Valve resource or package - build it from source, never commit it")
        if b"\0" in data[:8192] or not _utf8(data):
            self.items += 1
            digest = hashlib.sha256(data).hexdigest()
            if (path, digest) not in self.allow.binaries:
                self.findings.append(f"{where or path}: binary: not reviewed - add 'binary {path} {digest}  # what it is and why it may be public' to "
                                     f"tools/scan/allow.txt once checked (no extracted game files, no other authors' art, nothing private)")
            return
        self.text(path, data.decode("utf-8"), where)


def _utf8(data):
    try:
        data.decode("utf-8")
        return True
    except UnicodeDecodeError:
        return False


def git(*args, binary=False):
    r = subprocess.run(["git", "-C", ROOT, *args], capture_output=True)
    if r.returncode != 0:
        raise SystemExit(f"git {' '.join(args)}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout if binary else r.stdout.decode("utf-8", "replace")


def cat_blobs(ids):
    """{object id: bytes} through one git cat-file --batch."""
    if not ids:
        return {}
    p = subprocess.run(["git", "-C", ROOT, "cat-file", "--batch"], input=("\n".join(ids) + "\n").encode(), capture_output=True)
    out, data, i = {}, p.stdout, 0
    while i < len(data):
        nl = data.index(b"\n", i)
        header = data[i:nl].decode().split()
        if len(header) < 3:
            i = nl + 1
            continue
        oid, size = header[0], int(header[2])
        out[oid] = data[nl + 1:nl + 1 + size]
        i = nl + 1 + size + 1
    return out


def scan_tree(s):
    files = [f for f in git("ls-files", "-co", "--exclude-standard", "-z").split("\0") if f]
    for f in files:
        full = os.path.join(ROOT, f)
        if os.path.isfile(full):
            s.blob(f, open(full, "rb").read())
    return files


def scan_staged(s):
    names = [f for f in git("diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR").split("\0") if f]
    entries = {}
    for line in git("ls-files", "-s", "-z", "--", *names).split("\0") if names else []:
        if line:
            meta, path = line.split("\t", 1)
            entries[path] = meta.split()[1]
    blobs = cat_blobs(list(set(entries.values())))
    for path, oid in entries.items():
        s.blob(path, blobs.get(oid, b""), f"(staged) {path}")
    return names


def scan_commits(s, revs):
    """Messages, identities and every blob of the given commits (the whole tree of each)."""
    seen = set()
    for c in revs:
        meta = git("show", "-s", "--format=%an <%ae>%n%cn <%ce>%n%B", c)
        s.text("(commit)", meta, f"commit {c[:12]}")
        for line in git("ls-tree", "-r", "-z", c).split("\0"):
            if not line:
                continue
            info, path = line.split("\t", 1)
            mode, kind, oid = info.split()
            if kind == "blob" and (oid, path) not in seen:
                seen.add((oid, path))
    by_oid = cat_blobs(sorted({o for o, _ in seen}))
    for oid, path in sorted(seen, key=lambda t: t[1]):
        s.blob(path, by_oid.get(oid, b""), f"{path} (blob {oid[:12]})")
    for t in [x for x in git("tag", "-l").split("\n") if x]:
        s.text("(tag)", git("cat-file", "-p", t), f"tag {t}")
    return revs


def find_gitleaks():
    exe = shutil.which("gitleaks")
    if exe:
        return exe
    hits = glob.glob(os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages", "Gitleaks*", "gitleaks.exe"))
    return hits[0] if hits else None


def run_gitleaks(mode, rng=None):
    exe = find_gitleaks()
    if not exe:
        return None, "gitleaks not found (winget install Gitleaks.Gitleaks), or pass --no-gitleaks"
    report = tempfile.NamedTemporaryFile(suffix=".json", delete=False)
    report.close()
    common = ["--no-banner", "--redact", "--exit-code", "0", "--report-format", "json", "--report-path", report.name, "--log-level", "error"]
    cfg = os.path.join(ROOT, ".gitleaks.toml")
    if os.path.exists(cfg):
        common += ["--config", cfg]
    tmpdir = None
    try:
        if mode == "tree":
            tmpdir = tempfile.mkdtemp(prefix="os2scan-")
            for f in [f for f in git("ls-files", "-co", "--exclude-standard", "-z").split("\0") if f]:
                src = os.path.join(ROOT, f)
                if os.path.isfile(src):
                    dst = os.path.join(tmpdir, f)
                    os.makedirs(os.path.dirname(dst), exist_ok=True)
                    shutil.copyfile(src, dst)
            cmd = [exe, "dir", tmpdir, *common]
        elif mode == "staged":
            cmd = [exe, "git", ROOT, "--staged", "--pre-commit", *common]
        elif mode == "history":
            cmd = [exe, "git", ROOT, "--log-opts=--all", *common]
        else:
            cmd = [exe, "git", ROOT, f"--log-opts={rng}", *common]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            return None, f"gitleaks failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:300]}"
        leaks = json.load(open(report.name, encoding="utf-8")) or []
        out = []
        for l in leaks:
            where = l.get("File", "?").replace("\\", "/")
            if tmpdir:
                where = where.replace(tmpdir.replace("\\", "/"), "").lstrip("/")
            out.append(f"{where}:{l.get('StartLine')}: gitleaks:{l.get('RuleID')}: <redacted>" + (f"  (commit {l['Commit'][:12]})" if l.get("Commit") else ""))
        return out, None
    finally:
        os.unlink(report.name)
        if tmpdir:
            shutil.rmtree(tmpdir, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--staged", action="store_true")
    g.add_argument("--history", action="store_true")
    g.add_argument("--range")
    ap.add_argument("--report")
    ap.add_argument("--no-gitleaks", action="store_true")
    ap.add_argument("--public-only", action="store_true", help="run without the private denylist (contributors without it)")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    deny = Deny(DENY_FILE)
    if not deny.loaded and not (args.public_only or os.environ.get("OVERSTRIKE2_SCAN_PUBLIC_ONLY") == "1"):
        print(f"scan: the private denylist is missing ({DENY_FILE}). It holds the deployment's own names and addresses and is what\n"
              "      stops them reaching this public repository. Restore it, or run with --public-only if you have none.")
        return 2
    s = Scan(Allow(ALLOW_FILE, "allow.txt"), deny)
    mode = "staged" if args.staged else "history" if args.history else "range" if args.range else "tree"
    if mode == "tree":
        scope = f"{len(scan_tree(s))} files in the working tree"
    elif mode == "staged":
        scope = f"{len(scan_staged(s))} staged files"
    elif mode == "history":
        revs = [c for c in git("rev-list", "--all").split() if c]
        scan_commits(s, revs)
        scope = f"{len(revs)} commits (every ref), their messages, identities and every blob"
    else:
        revs = [c for c in git("rev-list", args.range).split() if c]
        scan_commits(s, revs)
        scope = f"{len(revs)} commits in {args.range}"
    gl_note = "gitleaks: skipped (--no-gitleaks)"
    if not args.no_gitleaks:
        leaks, err = run_gitleaks(mode, args.range)
        if err:
            print("scan: " + err)
            return 2
        s.findings += leaks
        gl_note = f"gitleaks: {len(leaks)} finding(s)"
    head = [f"Over Strike 2 scan ({mode}): {scope}; {s.items} texts checked",
            f"public rules: tools/scan/scan.py + allow.txt; private denylist: {'loaded' if deny.loaded else 'NOT LOADED (--public-only)'}"
            f" ({len(deny.patterns)} patterns, {len(deny.hashes)} secret hashes, {len(deny.allow)} private exceptions); {gl_note}",
            f"RESULT: {'CLEAN' if not s.findings else str(len(s.findings)) + ' FINDING(S)'}"]
    text = "\n".join(head + s.findings) + "\n"
    print(text if len(s.findings) <= 200 else "\n".join(head + s.findings[:200]) + f"\n... {len(s.findings) - 200} more (see --report)\n")
    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, "a", encoding="utf-8") as fh:
            fh.write(text + "\n")
    return 1 if s.findings else 0


if __name__ == "__main__":
    sys.exit(main())
