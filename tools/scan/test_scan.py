#!/usr/bin/env python3
"""Offline tests of scan.py's bidi rule: a raw bidi control character (U+202A-U+202E, U+2066-U+2069) is refused in a file, in a file's name
and in a commit message, and the report names it rather than showing it; its escape and its neighbours are no finding; an allow entry
allows its own line only; the history entries of allow.txt allow exactly the published lines they name. No git history, no gitleaks, no
private denylist: it runs on a fresh clone before any build.

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

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
