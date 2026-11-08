#!/usr/bin/env python3
"""Offline tests of thumbs.py's reading of the export and the queues. The export: Arms Race's list first, then every mode's, and a map whose
sync verdict ("ws") is not ok left out, since its preview went with it; an older os2-sync's export, with no verdicts, leaves nothing out and
says so in one line. The queues: queues.json version 2 gives the open ballot's maps first, then every lane of the stock in its name's order;
anything else - an older bridge's version 1, a file with no version, not an object - is no queues, said in one line, and the run goes on
without the head start (the pictures delete nothing, so stopping would protect nothing). The same cases as the C# port's ThumbsTests. No
network and no pictures written; it needs Pillow only because thumbs.py imports it, as thumbs.py itself does.

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


def exported(export):
    """from_export's ids, previews and gone maps, and what it printed."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        ids, previews, gone = thumbs.from_export(export)
    return ids, previews, gone, out.getvalue()


ids, previews, gone, said = exported({"maps.json": [{"id": "1", "rank": 1, "preview": "p1", "ws": "ok"}, {"id": "5", "rank": 2, "preview": "p5", "ws": "removed"}],
                                      "modes": {"wingman": [{"id": "3", "ws": "hidden"}, {"id": "7", "ws": None}],
                                                "deathmatch": [{"id": "2", "preview": "p2", "ws": "ok"}, {"id": "6", "preview": "p6", "ws": "banned"}]}})
check("export: Arms Race's list first, then every mode's in their names' order, with their previews; a map whose sync verdict is not ok is "
      "left out and named, one without a verdict kept; nothing said", ids == ["1", "2", "7"] and previews == {"1": "p1", "2": "p2"}
      and gone == {"3": "hidden", "5": "removed", "6": "banned"} and said == "")
ids, previews, gone, said = exported({"maps.json": [{"id": "1", "rank": 1, "preview": "p1"}], "modes": {"wingman": [{"id": "3"}], "deathmatch": [{"id": "2", "preview": "p2"}]}})
check("export: an older os2-sync's, with no verdicts - every map kept, and one line says so",
      ids == ["1", "2", "3"] and len(previews) == 2 and gone == {} and len(said.splitlines()) == 1 and "no map's Workshop verdict" in said)

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
