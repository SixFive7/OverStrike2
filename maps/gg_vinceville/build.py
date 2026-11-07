#!/usr/bin/env python3
"""Builds gg_vinceville with maps/build_map.py: runs generate.py, then compiles the map. It uses stock materials only, so
there is nothing else to compile and no materials folder to ship.

  python build.py                              # the published build
  python build.py [--log <file>] [--layout <file>] [--cs2 <path>] [--addon <folder>] [--spawns-from <layout.json>]
                  [--nolight] [--vis] [--lightmap 1024] [--quality 0] [--threads 4] [--nogen]

--spawns-from keeps an earlier build's spawn points (generate.py's option).
--vis adds the visibility build; without it the map draws whole, which is how the published build was compiled.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from build_map import main  # noqa: E402 (maps/build_map.py: what every map's build does)

if __name__ == '__main__':
    sys.exit(main('gg_vinceville', materials=False, generator_options=('--spawns-from',)))
