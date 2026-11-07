#!/usr/bin/env python3
"""Offline tests of thumbs.py's reading of the queues: queues.json version 2 gives the open ballot's maps first, then every lane of the stock
in its name's order; anything else - an older bridge's version 1, a file with no version, not an object - is no queues, said in one line,
and the run goes on without the head start (the pictures delete nothing, so stopping would protect nothing). The same cases as the C# port's
ThumbsTests. No network and no pictures written; it needs Pillow only because thumbs.py imports it, as thumbs.py itself does.

    python -I hud/test_thumbs.py
"""
import contextlib
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import thumbs  # noqa: E402

FAILS = []


def check(name, ok):
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        FAILS.append(name)


def queued(queues):
    """from_queues' ids, whether it ended the run, and what it printed."""
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            return thumbs.from_queues(queues), None, out.getvalue()
    except SystemExit as e:
        return None, e.code, out.getvalue()


ids, ended, said = queued({"version": 2, "ballot": ["9"], "stock": {"b": ["7", 8], "a": ["6", "x"], "c": {"no": "lane"}}})
check("queues: the open ballot's first, then every lane of the stock in its name's order, numbers or text; a lane of another shape is empty; "
      "nothing said", ids == ["9", "6", "7", "8"] and ended is None and said == "")
for name, queues in [("an older bridge's (version 1)", {"version": 1, "requested": ["9"], "random": {"a": ["6"]}}),
                     ("one with no version", {"stock": {"a": ["6"]}}),
                     ("not an object", ["9"])]:
    ids, ended, said = queued(queues)
    check(f"queues: {name} is no queues - one line says so, and the run goes on with no head start",
          ids == [] and ended is None and len(said.splitlines()) == 1 and "not queues.json version 2" in said)

print("\n%d failure(s)" % len(FAILS))
sys.exit(1 if FAILS else 0)
