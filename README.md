# Over Strike 2

*Over the top - On the Huis*

Over Strike 2 turns a Counter-Strike 2 dedicated server into an Arms Race (gun game) server that plays the Steam Workshop:
thousands of community maps, picked from an in-game menu, shared out fairly between the players who are on, voted on, and
rolled at random when nobody asks. Everything is in the menu; the chat commands work too.

- **An in-game menu**, a CS2-style window drawn by a Workshop addon: every map with its picture, size and votes, full-text
  search on an on-screen keyboard, any Workshop map by its id, the queue, the game settings, help, rules and how it all works.
  Hold TAB for a live board beside the scoreboard - vote, skip, see what is next - with the mouse.
- **A chat command for every action**, listed by `!help`. The words players know from other servers work too (`!map`, `!rtv`),
  and the reply teaches the server's own: `You typed !map 12 - ran !play 12`. A vote is a card's number in chat, and a click on a
  card shows in chat as that command, so menu users learn the chat way.
- **Sixteen fun settings** anybody can switch until the map changes, each with its own word (`!gravity low`): low gravity,
  bunny hopping, friendly fire, Laser guns, Vampire and more. Grenades drop where players die, and players who fall behind get a
  catch-up boost.
- **A fair queue.** A pick replaces a random map at once and otherwise waits its turn: least play time first, a map several
  players want first, only players who are on the server. A majority of the others can vote a map off.
- **A vote at every match's end.** Five maps and Random on the menu's cards for a minute: the queued requests (their
  requesters' votes already cast), a map of each other game mode that fits the players on - Deathmatch, and Wingman for four
  or fewer - and maps rolled at random, weighted by Workshop popularity and the players' likes; disliked, oversized,
  advertising and broken maps never make the ballot.
- **Maps that break things are handled.** A map that crashes the server asks for confirmation (and the crash is counted),
  items that are not maps are refused, maps gone from the Workshop are blocked by a daily check, and whatever a map changes
  in the game's settings is put back when it ends.
- **A server that looks after itself**: restarts for game updates and once a day, only between matches; a frozen engine is
  killed so it restarts; Workshop maps are prefetched at night; and a nightly sync finds new Arms Race, Deathmatch and Wingman
  maps on the Workshop, scans them for adverts, load-tests them on a test copy of the server and lists the ones that pass.
- **Its own maps**, generated from code: glass towers, a moon base, a village full of ambushes.
- **A map list site**: every map with its picture, the rules and how to join.

## How it works

```
 players ──► CS2 dedicated server ──── log lines over HTTP ────► bridge (sidecar)
              │ Metamod + CounterStrikeSharp                      │ queue, votes, dice, crash and Workshop checks
              │ OverStrike2 plugin: menu, HUD, grenades,  ◄───────┤ RCON: map changes, notices
              │   map settings, fun settings                      │ HTTP: the menu's live state, the clicks
              └ MultiAddonManager hands every player the HUD addon (a Steam Workshop item)
```

- **The bridge** (`bridge/`) is a small C# service (.NET 10, base class library only, native AOT, a chiseled image that runs
  read-only as a non-root user). The game server pushes its log lines to it; it decides what a click asks for and talks back
  over RCON. Its state (votes, history, crash counts) is a few JSON files in one directory.
- **The plugin** (`plugin/OverStrike2/`) is a CounterStrikeSharp plugin inside the game server: it draws the menu and the board,
  hands out grenades, puts map settings back, applies the fun settings and checks the Workshop. Much of its logic is shared
  source with the bridge, so the bridge's self-test covers it.
- **The HUD** (`hud/`) is a Panorama layout and stylesheet compiled into a Workshop addon. Players do not subscribe: the server
  hands it to their game when they join.
- **The server files** (`server/`) are the install hook the [joedwards32/cs2](https://github.com/joedwards32/CS2) image runs
  before every start (it installs Metamod:Source, CounterStrikeSharp and MultiAddonManager at pinned versions and copies the
  plugin in), the Arms Race settings, and the restart watcher.

The full design - every rule, every setting, every log line the bridge reads - is in [docs/reference.md](docs/reference.md).

## Repository layout

| Folder | What |
|---|---|
| [`bridge/`](bridge/) | The bridge: C# sources, its `--selftest`, the `Dockerfile`, and the data files - `maps.json` (the map list), `broken.json` (crashers, non-maps, removed and blocked maps), `sizes.json` (measured map sizes), `search.json` (the menu's search index) |
| [`plugin/`](plugin/) | The CounterStrikeSharp plugin `OverStrike2` and the self-test of its grenade logic |
| [`hud/`](hud/) | The in-game HUD addon: layout, stylesheets, the thumbnail generator, the build script and the Workshop publisher |
| [`maps/`](maps/) | Our own maps: one generator per map and the Workshop publisher |
| [`server/`](server/) | The game server's install hook (`pre.sh`), each game mode's settings, the restart watcher (`update-watch.py`) and the nightly Workshop sync (`workshop-sync.py`), each with its tests |
| [`site/`](site/) | The map list site: the page and the scripts that build and stamp it (titles, tags and sizes; other authors' descriptions are not kept) |
| [`deploy/`](deploy/) | Deployment scripts and an example compose file; every deployment value comes from a private config |
| [`tools/scan/`](tools/scan/) | The scanner that keeps secrets and private details out of this repository |
| [`docs/`](docs/) | The reference: every part, rule and setting |

## Build and test

Requirements: the .NET 10 SDK, Python 3.12+, and a container builder (Docker, or WSL containers on Windows) for the bridge's
image. The HUD and the maps need Windows with Counter-Strike 2 and its Workshop Tools.

```sh
cd bridge && dotnet build && dotnet run -- --selftest        # the bridge, the shared HUD logic, the rules on the site
cd plugin/OverStrike2 && dotnet build -c Release              # the plugin (warnings are errors)
cd plugin/OverStrike2.SelfTest && dotnet run                  # the grenade logic
docker build -t localhost/overstrike2-bridge bridge                     # the bridge's image (native AOT inside the SDK image)
docker run --rm -v "$PWD/server:/repo:ro" -w /repo python:3.9-slim python test_update_watch.py   # the restart watcher
python server/test_workshop_sync.py                           # the nightly Workshop sync
python bridge/search-index.py --check                         # the search index matches the site's map list
```

The site's pages and the menu's map pictures are built from a Workshop scrape and other authors' preview images, which are
not part of this repository; [docs/reference.md](docs/reference.md) ("Files") describes how they are made.

## Configure

Two containers - the game server and the bridge - and optionally a third for the nightly Workshop sync. [`deploy/compose.example.yml`](deploy/compose.example.yml) is a complete
example with every setting explained; the essentials:

| Where | Setting | What |
|---|---|---|
| both | `RCON_PASSWORD` / `CS2_RCONPW` | the same RCON password on the bridge and the game server |
| both | `LOG_SECRET` | a random path segment: the game server pushes its log to `http://bridge:8080/log/<LOG_SECRET>` (`CS2_LOG_HTTP_URL`) |
| bridge | `SITE_URL` | your map list site; the menu and the welcome point players at it (the plugin gets it from the bridge) |
| game server | `CS2_PLUGINS` | `1` installs and loads the plugin layer; `0` runs vanilla (after a game update broke a dependency) |
| game server | `GUNGAME_HUD_ADDON` | the HUD addon's Workshop id; empty falls back to a simple text panel |
| game server | `CS2_AUTOUPDATE`, `CS2_RESTART_AT`, `CS2_FREEZE_RECOVERY`, `CS2_WARMUP`, `CS2_PREFETCH_AT` | the restart watcher's jobs |
| sync | `SYNC_AT`, `SYNC_TAGS`, the `SYNC_*` limits, a Steam Web API key file | the nightly Workshop sync (optional: a third container) |
| bridge | `MAPS_FILE`, `BROKEN_FILE` | the map list and the broken list on the data volume, so a new list is a file copy (read again within seconds) |
| bridge | `HUD_TAB`, `HUD_CHECK`, `RANDOM_ON_MATCH_END`, `RANDOM_ON_START` | how the board and the dice behave |

Every bridge setting is in [docs/reference.md](docs/reference.md#configuration-environment), and the restart watcher's in its
own section there.

## Deploy

Builds happen on a workstation; there is no CI and no registry. The scripts in [`deploy/`](deploy/) build the bridge's image
locally, load it into the server's Docker and swap the running bridge without disconnecting anybody; copy the plugin, the data
files and the server files to the server; render and ship the site; and build and publish the Workshop items. They read every
address, path, container name and credential from a private config that never enters this repository - see
[`deploy/README.md`](deploy/README.md) and [`deploy/config.example.toml`](deploy/config.example.toml).

The HUD is one Workshop item, updated in place: every new version waits for Valve's moderation, and a new item is never
created ([`hud/PUBLISH.md`](hud/PUBLISH.md)).

## Keeping this repository clean

This repository is public, and the server it was built for is not. A pre-commit and a pre-push hook run
[`tools/scan/scan.py`](tools/scan/scan.py): gitleaks for secrets, and rules against addresses, host paths, shares, local
paths, Steam ids, e-mail addresses, compiled game files and unreviewed binaries, plus a private denylist kept outside the
repository. Enable the hooks once per clone:

```sh
git config core.hooksPath .githooks
```

No game files are included: builds read what they need from a local Counter-Strike 2 installation.

## License

[Apache License 2.0](LICENSE). The CounterStrikeSharp plugin and the bridge sources it compiles in are also available under
the MIT License ([LICENSE-MIT](LICENSE-MIT)), as CounterStrikeSharp's license asks of plugins. Third-party notices are in
[NOTICE](NOTICE).

Counter-Strike 2 and the Steam Workshop are Valve's. This project is not affiliated with or endorsed by Valve. The map titles
and tags in the data files are their authors'; their descriptions are not kept here.
