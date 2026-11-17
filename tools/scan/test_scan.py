#!/usr/bin/env python3
"""Offline tests of scan.py's bidi rule: a raw bidi control character (U+202A-U+202E, U+2066-U+2069) is refused in a file, in a file's name
and in a commit message, and the report names it rather than showing it; its escape and its neighbours are no finding; an allow entry
allows its own line only; the history entries of allow.txt allow exactly the published lines they name. And the pre-push hook's ranges, on a
scratch repository of its own: a new branch scans the commits no remote ref reaches, an update the ones the remote's ref lacks, and a scan that
finds anything refuses the push. No history of this repository, no gitleaks, no private denylist: it runs on a fresh clone before any build.

    python -I tools/scan/test_scan.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scan  # noqa: E402

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


def chars(text):
    """Test text with its special characters spelled out, so this file holds none: <U+XXXX> is that code point, <BS> a backslash."""
    return re.sub(r"<U\+([0-9A-F]{4,6})>|<BS>", lambda m: chr(int(m.group(1), 16)) if m.group(1) else chr(92), text)


class NoAllow(scan.Allow):
    def __init__(self, lines=()):
        self.rules, self.binaries = [], {}
        for line in lines:
            rule, glob, rx = line.split(None, 2)
            self.rules.append((rule, glob, re.compile(rx), 0))


def findings(text, path="src/x.cs", allow=None):
    s = scan.Scan(allow or NoAllow(), scan.Deny(os.path.join(HERE, "no-such-denylist.txt")))
    s.text(path, text)
    return [f for f in s.findings if ": bidi: " in f]


BIDI = [0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069]
OTHERS = [0x200E, 0x200F, 0x061C, 0x2028, 0x2029, 0x2065, 0x206A, 0x202F, 0x00E9, 0x1F600]

for cp in BIDI:
    got = findings("var s = \"a" + chr(cp) + "b\";\n")
    check("bidi: U+%04X in a file is refused, named, and never shown raw" % cp,
          len(got) == 1 and ("U+%04X" % cp) in got[0] and ("<U+%04X>" % cp) in got[0] and chr(cp) not in got[0])
check("bidi: a direction mark, a line or paragraph separator and the code points beside the ranges are not this rule's",
      all(findings("x" + chr(cp) + "y\n") == [] for cp in OTHERS))
check("bidi: escapes of one are no finding", findings(chars("a = \"<BS>u202E\"; b = (char)0x202E; c = \"<BS>U0000202e\";\n")) == [])
check("bidi: every one on a line is a finding of its own", len(findings("a" + chr(0x2067) + "b" + chr(0x2069) + "c" + chr(0x202E) + "\n")) == 3)
check("bidi: in a commit message too", len(findings("Sync: a fix" + chr(0x202E) + "\n", path="(commit)")) == 1)
s = scan.Scan(NoAllow(), scan.Deny(os.path.join(HERE, "no-such-denylist.txt")))
s.blob("docs/evil" + chr(0x202E) + "txt.md", b"plain\n")
check("bidi: in a file's name, named", len(s.findings) == 1 and "file's name" in s.findings[0] and "<U+202E>" in s.findings[0] and chr(0x202E) not in s.findings[0])

line = "x = \"a" + chr(0x202E) + "b\"  # a fixture"
allow = NoAllow([chars("bidi src/x.cs x = \"a<BS>u202eb\"  <BS># a fixture")])
check("bidi: an allow entry allows the whole line it names", findings("    " + line + "\n", allow=allow) == [])
check("bidi: ... not another line of the same file with the same character", len(findings(chars("y = \"a<U+202E>b\"\n"), allow=allow)) == 1)
check("bidi: ... nor its line in another file", len(findings(line + "\n", path="src/y.cs", allow=allow)) == 1)

real = scan.Allow(scan.ALLOW_FILE)
published = [
    ("server/workshop-sync.py", "BIDI_RX = re.compile(\"[<U+202A>-<U+202E><U+2066>-<U+2069>]\")"),
    ("server/OverStrike2.Sync/SelfTest/NewChecks.cs",
     "{\"f\": [0.1, 1e16, 1.5e-07, 1793500000.0, 0.547, 2.675, 1e-05, 123456789.125, -0.0, 1e15], \"s\": \"a<BS>\"b<BS><BS>c<BS>nd<BS>te<BS>u0001<BS>u007f"
     "<U+00E9><U+202E><U+1F600>\","),
    ("server/OverStrike2.Sync/SelfTest/NewChecks.cs",
     "Py.Compact(tricky) == \"{<BS>\"f<BS>\":[0.1,1e+16,1.5e-07,1793500000.0,0.547,2.675,1e-05,123456789.125,-0.0,1000000000000000.0],<BS>\"s<BS>\":<BS>\"a"
     "<BS><BS><BS>\"b<BS><BS><BS><BS>c<BS><BS>nd<BS><BS>te<BS><BS>u0001<BS>u007f<U+00E9><U+202E><BS>U0001f600<BS>\",<BS>\"n<BS>\":null,<BS>\"t<BS>\":true,"
     "<BS>\"e<BS>\":[],<BS>\"o<BS>\":{},<BS>\"i<BS>\":[1,-2,12345678901234]}\");"),
    ("server/OverStrike2.Sync/SelfTest/NewChecks.cs",
     "Text.Clean(\"  a<BS>u0000b<U+202E>c<BS>t<BS>td  \" + string.Concat(Enumerable.Repeat(\"word \", 30)), 50) == \"a bc d word word word word word word word "
     "word<U+2026>\""),
]
check("bidi: allow.txt's history entries allow the published lines with a raw one, in their files",
      all(findings("        " + chars(text) + "\n", path=path, allow=real) == [] for path, text in published))
check("bidi: ... and nothing else there: another override in those files is refused",
      all(len(findings(chars("x = \"<U+202E>\";\n"), path=path, allow=real)) == 1 for path, _ in published))
check("bidi: allow.txt itself holds no raw one", findings(open(scan.ALLOW_FILE, encoding="utf-8").read(), path="tools/scan/allow.txt", allow=real) == [])

# ---- the pre-push hook: what it hands the scan, in a scratch repository with a stand-in scan.py that says what it was given ----
import shutil
import subprocess
import tempfile

HOOK = os.path.join(os.path.dirname(os.path.dirname(HERE)), ".githooks", "pre-push")


def sh_git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, check=True).stdout.strip()


if shutil.which("sh") is None or shutil.which("git") is None:
    print("SKIP pre-push: no sh or no git on this machine to run the hook with")
else:
    scratch = tempfile.mkdtemp(prefix="os2-prepush-")
    try:
        repo = os.path.join(scratch, "repo")
        os.makedirs(os.path.join(repo, "tools", "scan"))
        sh_git(scratch, "init", "-q", repo)
        for k, v in (("user.name", "Test"), ("user.email", "test@example.invalid"), ("commit.gpgsign", "false"), ("core.hooksPath", "no-hooks")):
            sh_git(repo, "config", k, v)
        given_file = os.path.join(scratch, "given.txt")
        verdict_file = os.path.join(scratch, "verdict.txt")
        stand_in = [  # writes the arguments it was given, a line a call, and exits as the test tells it
            "import sys",
            "open(%r, 'a', encoding='utf-8').write(' '.join(sys.argv[1:]) + chr(10))" % given_file,
            "sys.exit(int(open(%r).read()))" % verdict_file,
        ]
        with open(os.path.join(repo, "tools", "scan", "scan.py"), "w", encoding="utf-8") as fh:
            fh.write(chr(10).join(stand_in) + chr(10))

        def commit(name):
            with open(os.path.join(repo, name), "w", encoding="utf-8") as fh:
                fh.write(name + chr(10))
            sh_git(repo, "add", name)
            sh_git(repo, "commit", "-q", "-m", name)
            return sh_git(repo, "rev-parse", "HEAD")

        def push(lines, exit_code=0):
            open(given_file, "w").close()
            with open(verdict_file, "w") as fh:
                fh.write(str(exit_code))
            r = subprocess.run(["sh", HOOK, "origin", "https://example.invalid/repo.git"], input="".join(l + chr(10) for l in lines).encode(),
                               cwd=repo, capture_output=True)  # bytes: git hands the hook bare newlines, as text mode on Windows would not
            return r.returncode, [l for l in open(given_file, encoding="utf-8").read().splitlines() if l]

        zero = "0" * 40
        a = commit("a.txt")
        sh_git(repo, "update-ref", "refs/remotes/origin/main", a)  # what the remote has: A
        b = commit("b.txt")
        c = commit("c.txt")
        code, given = push(["refs/heads/x %s refs/heads/x %s" % (c, zero)])
        check("pre-push: a new branch scans only the commits no remote ref reaches, not the history the remote has: %s" % given,
              code == 0 and given == ["--range %s --not --remotes" % c])
        check("pre-push: ...B and C, as git rev-list takes the range's words (scan.range_args); A..B is one word",
              sh_git(repo, "rev-list", *scan.range_args("%s --not --remotes" % c)).split() == [c, b] and scan.range_args("%s..%s" % (a, c)) == ["%s..%s" % (a, c)])
        code, given = push(["refs/heads/main %s refs/heads/main %s" % (c, a)])
        check("pre-push: an update scans what the remote's ref lacks, remote..local: %s" % given, code == 0 and given == ["--range %s..%s" % (a, c)])
        code, given = push(["refs/heads/old %s refs/heads/old %s" % (a, zero)])
        check("pre-push: a new branch at a commit a remote ref reaches brings nothing new, and scans nothing", code == 0 and given == [])
        code, given = push(["refs/heads/x %s refs/heads/x %s" % (c, zero)], exit_code=1)
        check("pre-push: a scan that finds anything refuses the push", code == 1 and len(given) == 1)
        code, given = push(["refs/heads/x %s refs/heads/x %s" % (c, zero), "refs/heads/main %s refs/heads/main %s" % (c, a)], exit_code=1)
        check("pre-push: ...every ref of the push is scanned, and one finding refuses it all", code == 1 and len(given) == 2)
        code, given = push(["(delete) %s refs/heads/gone %s" % (zero, a)])
        check("pre-push: a deletion pushes no content and scans nothing", code == 0 and given == [])
    finally:
        shutil.rmtree(scratch, ignore_errors=True)

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
