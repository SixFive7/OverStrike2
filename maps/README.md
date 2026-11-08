# Our own maps

Three Arms Race maps, each generated from a script: the geometry, the props, the spawns and the breakable glass are all code.
Each folder holds `generate.py` (writes the Hammer map source into the CS2 Workshop Tools' content folder), `build.py`
(generates, then compiles, lights and packs the map with the Workshop Tools), `preview.py` (renders the Workshop preview from
the generated layout), `workshop.json` (the Workshop item's title, description and tags, and the addon folder its build is in: the
publisher uploads that folder, and refuses another) and `workshop_id.txt` (the item).
`vmapkit.py` holds what the generators share: the ids and the writer of the Hammer map source. `build_map.py` is what every `build.py`
runs, with the map's own settings, and `previewkit.py` the renderer every `preview.py` draws with. Each map has a README of its own -
[gg_glasspire](gg_glasspire/README.md), [gg_moonglass](gg_moonglass/README.md), [gg_vinceville](gg_vinceville/README.md): what the
script builds, how the shipped build was compiled and checked, and its known gaps.

| Map | Workshop item | What it is |
|---|---|---|
| `gg_glasspire` | 3831189574 | A square glass tower around an open atrium: four floors and a roof deck joined by ramps that spiral up the outer walls, 292 panes of breakable glass. |
| `gg_moonglass` | 3831231522 | A moon base under a faceted glass dome: modules, bridges and tunnels, most of it glass that breaks. |
| `gg_vinceville` | 3831242232 | A village of small houses scattered over a grassy field, with a barn and its hay loft, two houses with an upper floor, two ponds, haystacks and low stone walls. |

## Building

On Windows, with Counter-Strike 2 and its Workshop Tools installed (Python 3.12+, numpy and Pillow for the generators and the
previews):

```powershell
python maps\gg_glasspire\build.py          # --cs2 <path to 'Counter-Strike Global Offensive'>, or CS2_DIR, or the install Steam knows of
```

Each compile waits until no other `resourcecompiler.exe` runs on the PC. `--addon <folder>` builds into another addon folder - a
staging copy, say, while the published build is under review - and leaves the published one as it is.

The maps use only the game's own materials and models, by reference, and nothing of the game goes into this repository.

## Checking

After any change under `maps/`, run the self-check (it is the last line of the list in [AGENTS.md](../AGENTS.md), and needs
neither CS2 nor the Workshop Tools):

```powershell
python -I maps\selfcheck.py                    # each generate.py's files against selfcheck.json, each preview against workshop_preview.jpg
```

It regenerates every map into a scratch folder and compares each file it writes with the sha256 recorded in `selfcheck.json`,
then renders every preview from that layout and compares it with the committed `workshop_preview.jpg`, byte for byte, and runs
gg_vinceville's own `checks.py` (no holes, no overlapping faces, spawns that work) and `walkcheck.py` on its layout. A change that
should leave the maps as they are must leave all of it as committed. A change that is meant to alter a map is recorded with
`python -I maps\selfcheck.py --record`, and its preview is rendered anew with the map's `preview.py` and committed with its line
in `tools/scan/allow.txt`; `--keep <folder>` keeps what a run wrote. The recorded hashes hold for the Python, numpy and Pillow
named in `selfcheck.json`: another version can write other bytes without any change to a map.

## Publishing

```powershell
powershell -File maps\publisher\publish.ps1 -Map gg_glasspire -Query        # read-only: the item as Steam has it
powershell -File maps\publisher\publish.ps1 -Map gg_glasspire -Update -Note "What changed" -UserApprovedUpdate
```

A new version of a map item waits in Valve's moderation queue like the HUD's: update only with the owner's go, while nobody
plays. `os2-deploy workshop map ...` runs the same script with the CS2 folder from the private config.
