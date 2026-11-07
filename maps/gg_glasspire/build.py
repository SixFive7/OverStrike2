#!/usr/bin/env python3
"""Builds gg_glasspire with maps/build_map.py: runs generate.py, compiles the two glass materials, then the map.

  python build.py                              # the published build
  python build.py [--cs2 <path>] [--log <file>] [--layout <file>] [--addon <folder>] [--nolight] [--vis]
                  [--lightmap 1024] [--quality 0] [--threads 4] [--nogen]

--vis adds the visibility build, which this map does without: on it the build ran for more than 4 minutes without
finishing (the open plaza inside the skybox shell is most of the volume), and the map is small enough to draw whole.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from build_map import main  # noqa: E402 (maps/build_map.py: what every map's build does)

if __name__ == '__main__':
    sys.exit(main('gg_glasspire', materials=True))
