#!/usr/bin/env python3
"""Builds gg_moonglass with maps/build_map.py: runs generate.py, compiles the addon's materials (the glass, the regolith,
the white panels and the Earth), then the map.

  python build.py --novis                      # how the published build was compiled
  python build.py [--cs2 <path>] [--log <file>] [--layout <file>] [--addon <folder>] [--nolight] [--vis | --novis]
                  [--lightmap 1024] [--quality 0] [--threads 4] [--nogen]

The visibility build is on unless --novis: on this map it ran 12 minutes without finishing, and ignored -threads (it used
about 17 cores), so the published build has none; the map then draws whole.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from build_map import main  # noqa: E402 (maps/build_map.py: what every map's build does)

if __name__ == '__main__':
    sys.exit(main('gg_moonglass', materials=True, vis=True))
