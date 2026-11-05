# Running your own

[`deploy/compose.yml`](../deploy/compose.yml) runs the whole server on one Docker host and builds everything there: the game server
([joedwards32/cs2](https://github.com/joedwards32/CS2), which downloads Counter-Strike 2 with SteamCMD), the bridge, the Workshop sync,
and an installer that builds the plugin and the two server programs and puts them into the volumes the others mount.

You need:

- Docker Engine 26 or newer with Compose 2.27 or newer (the sync mounts a subpath of a volume);
- disk space: about 75 GB for the game, and the Workshop maps on top (the keep set, [server.md](server.md)), on the disk Docker keeps
  its volumes on;
- UDP port 27015 open to your players;
- optional: a Steam Web API key (<https://steamcommunity.com/dev/apikey>), for the sync's discovery of new maps and their ratings, and a
  Game Server Login Token for app 730 (<https://steamcommunity.com/dev/managegameservers>), to list the server publicly.

## Quick start

```sh
git clone <this repository's URL> OverStrike2
cd OverStrike2/deploy
cp overstrike2.example.jsonc overstrike2.jsonc   # your settings: docs/settings.md has every key; hud.addon is required
cp example.env .env                              # your secrets: CS2_RCONPW and OVERSTRIKE2_BRIDGE_SECRET at least
mkdir secrets                                    # optional: your Steam Web API key in secrets/steam-webapi-key.txt
docker compose up -d --build
```

The first start takes a while: SteamCMD downloads the game, and the installer builds the plugin and the programs. `docker compose logs
-f cs2` shows the game server; it is ready when it reports its map. Players join with `connect <your address>:27015` in the console.
`overstrike2.jsonc`, `.env` and `secrets/` stay yours: they are gitignored.

`hud.addon` in your settings is required, and the example has the public HUD's id. Without a usable one, or with a settings file that
cannot be used, the game server does not start: `docker compose logs cs2` shows an `ALERT` line that says why and how to fix it, and
Docker tries again every few minutes. Fix the file, then `docker compose up -d --force-recreate cs2 sync bridge`.

## Updating

```sh
git pull
docker compose up -d --build
```

The installer runs again. The game server's restart watcher takes a new program, `pre.sh` or plugin in by itself: at the end of the
match with players on, at once with nobody on, announced on the HUD first ([server.md](server.md), "A new version of our own parts").
A changed setting takes effect when its containers are created again: `docker compose up -d --force-recreate cs2 sync bridge`, with
nobody on. A new CS2 build is the watcher's job too: it restarts the engine, and the image's SteamCMD updates the game.

## What runs

| Service | What it is |
|---|---|
| `cs2` | The game server. Before every engine start the image sources `pre.sh`, which copies `os2-server` out of the program volume and runs `install` (Metamod, CounterStrikeSharp, MultiAddonManager, the plugin, the HUD addon, the maps folder) and then the restart watcher ([server.md](server.md)). |
| `bridge` | The bridge: the vote, the dice, the HUD's data and the dashboard; it drives the game server over RCON and reads its log. |
| `sync` | The Workshop sync (`os2-sync serve`): new maps found, downloaded, scanned and load-tested while nobody plays, and listed when they pass. Leave it out (`docker compose up -d cs2 bridge`) to run with the lists from this repository. |
| `installer` | Builds the plugin and both programs, puts them into the volumes with `pre.sh` and the bridge's hand-made data, and exits. |

## Where things live

| Volume | Mounted in | What |
|---|---|---|
| `game` | `cs2` (and read-only in `sync`) | SteamCMD's install, the Workshop maps in `maps/<type>/`, `pre.sh`, the restart watcher's state in `update-watch/`, and the sync's work folder `workshop-sync/` (mounted read-write in `sync`) |
| `program` | `cs2`, `sync`, read-only | `os2-server`, `os2-sync`, `pre.sh` |
| `plugins` | `cs2`, read-only | the plugin, `OverStrike2/` |
| `data` | `bridge`, `sync`; read-only in `cs2` | the bridge's state and the map lists (`maps.json`, the mode lists, `broken.json`, `search.json`), which the sync writes |
| `sync-state` | `sync` | the Web API ledger, the sync's state and reports |
| `depotcache` | `cs2` | the Steam client's verified Workshop manifests |

The mode settings - `server/gamemode_*_server.cfg` - are mounted from your checkout, so `git pull` brings their changes; the game runs
a mode's file again at every map load.

## The other way

The server this was built for runs the same containers from its own compose file, with the images and programs built on a workstation
and shipped by the scripts in [`deploy/`](../deploy/README.md): no SDK on the server, every deployment value in a private config.
