# Our own maps

Three Arms Race maps, each generated from a script: the geometry, the props, the spawns and the breakable glass are all code.
Each folder holds `generate.py` (writes the Hammer map source into the CS2 Workshop Tools' content folder), `build.py`
(generates, then compiles, lights and packs the map with the Workshop Tools), `preview.py` (renders the Workshop preview from
the generated layout), `workshop.json` (the Workshop item's title, description and tags) and `workshop_id.txt` (the item).
`vmapkit.py` holds what the generators share: the ids and the writer of the Hammer map source. `build_map.py` is what every `build.py`
runs, with the map's own settings, and `previewkit.py` the renderer every `preview.py` draws with. [gg_glasspire](gg_glasspire/README.md)
and [gg_moonglass](gg_moonglass/README.md) have a README of their own: what the script builds, how the shipped build was compiled and
checked, and its known gaps.

| Map | Workshop item | What it is |
|---|---|---|
| `gg_glasspire` | 3831189574 | A square glass tower around an open atrium: four floors and a roof deck joined by ramps that spiral up the outer walls, 292 panes of breakable glass. |
| `gg_moonglass` | 3831231522 | A moon base under a faceted glass dome: modules, bridges and tunnels, most of it glass that breaks. |
| `gg_vinceville` | 3831242232 | Small houses scattered over a big grassy field, with trees, ponds deep enough to swim in, and a sewer under the field that links every house. |

## Building

On Windows, with Counter-Strike 2 and its Workshop Tools installed (Python 3.12+, numpy and Pillow for the generators and the
previews):

```powershell
python maps\gg_glasspire\build.py          # --cs2 <path to 'Counter-Strike Global Offensive'>, or CS2_DIR, or Steam's default folder
```

Each compile waits until no other `resourcecompiler.exe` runs on the PC. `--addon <folder>` builds into another addon folder - a
staging copy, say, while the published build is under review - and leaves the published one as it is.

The maps use only the game's own materials and models, by reference. Where a map needs one of the game's files in its addon
(gg_vinceville's underwater look, from `ar_pool_day`'s map package), the generator reads it from the local installation at
build time and writes it into the build output, never into this repository.

## Publishing

```powershell
powershell -File maps\publisher\publish.ps1 -Map gg_glasspire -Query        # read-only: the item as Steam has it
powershell -File maps\publisher\publish.ps1 -Map gg_glasspire -Update -Note "What changed" -UserApprovedUpdate
```

A new version of a map item waits in Valve's moderation queue like the HUD's: update only with the owner's go, while nobody
plays. `python deploy\workshop.py map ...` runs the same script with the CS2 folder from the private config.
