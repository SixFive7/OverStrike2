# Over Strike 2

*Over the top - On the Huis*

Over Strike 2 turns a Counter-Strike 2 dedicated server into an Arms Race (gun game) server that plays the Steam Workshop: thousands of
community maps, chosen together in a vote that freezes everybody until it is decided, with Deathmatch and Wingman on every ballot and dice
that learn from what the players like. Players install nothing: the server hands their game the menu when they join.

- **Run your own:** [docs/install.md](docs/install.md) - one Docker host, a settings file, `docker compose up -d --build`.
- **See the HUD:** [the HUD, page by page](#the-hud-page-by-page).
- **Every rule and number:** [docs/reference.md](docs/reference.md). **Every setting:** [docs/settings.md](docs/settings.md).
- **The server programs:** [docs/server.md](docs/server.md). **Shipping from a workstation:** [deploy/README.md](deploy/README.md).

## What players get

- **Arms Race, and more by vote.** A kill earns the next gun, and a kill with the golden knife on the last rung wins the match. Deathmatch, and
  Wingman up to four players, are on every ballot. The server is back on Arms Race after every restart and after 10 minutes with nobody on.
  ([The game modes](docs/reference.md#the-game-modes))
- **A menu in the game.** Hold **TAB** for the live board beside the scoreboard, press **TAB + E** for the full window: every map with its picture
  and full-text search, the vote, the fun settings, who is on, the Workshop sync's dashboard, and every rule. Any Workshop map can be played by its
  id. ([In-game HUD](docs/reference.md#in-game-hud))
- **Chat commands.** A line that starts with `!` or `/` does what the click does, and `!help` lists them. The words players know from other servers
  work and teach (`!map`, `!rtv`, `!nominate`, `!skip`). ([The commands](docs/reference.md#the-menu-and-the-commands))
- **The vote is how a map is chosen.** Play on a card, or **Start a vote**, and everybody's window opens on a ballot of ten cards: Keep playing,
  every map a player put on it, one map of each other mode, a fresh Workshop map, a surprise new to the server, and favourites. Everybody is frozen
  until all have voted, 60 s at the most. Every card but a player's own pick is on the server's disk, so the winner loads at once.
  ([The vote](docs/reference.md#the-vote-and-the-game-modes))
- **Cards that say why.** Every card wears its mode's colour and tags: OUR MAP, LIKED BY US, NEW UPLOAD, UPDATED, TRENDING, TOP RATED, POPULAR,
  NEW TO US. ([Tags](docs/reference.md#in-game-hud))
- **Dice that learn.** Each Like doubles a map's odds, up to five; each Dislike quarters them, and asks why; Workshop popularity is worth up to 16 times.
  A map shown and not chosen steps back for a while. ([Random picks](docs/reference.md#random-picks), [Likes and dislikes](docs/reference.md#likes-and-dislikes))
- **Fun settings.** Eighteen switches anybody can flip for everybody, until the map changes: gravity, bunny hopping, Vampire, Ghost, Laser guns and more,
  with Random and Default. Every combination but one works together, and the self-test walks all 884,736. ([Fun settings](docs/reference.md#fun-settings))
- **Catch-up.** In Arms Race a player who falls behind gets a boost that grows with every kill of the gap, and a runaway leader a bounty.
  ([Catch-up](docs/reference.md#catch-up))
- **Maps that break things say why.** A map that crashed the server or players' games shows its record and asks for a second click, and the dice skip it;
  non-maps, blocked maps and maps gone from the Workshop are refused. ([Broken maps](docs/reference.md#broken-maps))
- **A server that looks after itself.** It restarts for game updates and for new versions of its own parts, kills a frozen engine, keeps its disk to the maps
  worth keeping, and finds, scans and load-tests new Workshop maps while nobody plays. ([What the server keeps](docs/reference.md#what-the-server-keeps),
  [the Workshop sync](docs/reference.md#the-workshop-sync), [restarts](docs/server.md#when-the-server-restarts))
- **Our own maps.** [gg_glasspire, gg_moonglass and gg_vinceville](maps/README.md), generated from code: geometry, props, spawns and breakable glass.

## The HUD, page by page

Every part of the interface, as a real game client draws it against a test copy of the server, taken by script. The pages and their pictures live under
[`docs/`](docs/); this is their index. Until a part has its page, its rules are in [In-game HUD](docs/reference.md#in-game-hud).

<!-- hud-index:begin - the screenshot script fills the Page column; keep this table's parts in step with the HUD, and keep these two markers -->
| Part | What it shows | Page |
|---|---|---|
| The board | Hold TAB: the map that is on, Like and Dislike, Start a vote, the way into the menu | to come |
| START | The welcome | to come |
| MAPS, Browse | The map browser: cards, search and keyboard, sorting, filters, a Workshop map's card | to come |
| MAPS, Vote | The ballot and its states: open, everybody voted, decided, with its pages | to come |
| FUN | The fun settings, off their default, idle, refused | to come |
| SERVER, Players | Who is on, and which HUD their game shows | to come |
| SERVER, Game | The match length and the stuck-server restart | to come |
| SERVER, Sync | The Workshop sync's dashboard: now, the queue, the history | to come |
| HELP, Help | Where everything is | to come |
| HELP, How it works | Every rule of the vote and every chance of a random pick | to come |
| Every kind of on-screen message | One gallery: how each message looks and where on the screen it appears | to come |
<!-- hud-index:end -->

## Configure

Two files, beside [`deploy/compose.yml`](deploy/compose.yml) ([docs/install.md](docs/install.md) has the steps):

- **`overstrike2.jsonc`, your settings.** Every part reads this one file; a key you leave out has its default. [`deploy/overstrike2.example.jsonc`](deploy/overstrike2.example.jsonc)
  is a complete example, and [docs/settings.md](docs/settings.md) has every key, its default and who reads it.
- **`.env`, your secrets and the game image's own variables.** [`deploy/example.env`](deploy/example.env). The settings file names a secret by reference (`"env:CS2_RCONPW"`,
  `"file:/run/secrets/steam-webapi-key.txt"`), so nobody writes one twice.

The essentials:

| Where | What | For |
|---|---|---|
| `.env` | `CS2_RCONPW`, `OVERSTRIKE2_BRIDGE_SECRET` | The RCON password, and the secret in the bridge's addresses. Both are required |
| `.env` | `CS2_SERVERNAME`, `SRCDS_TOKEN`, `TZ` | The name players see; a Game Server Login Token to list the server publicly; local time for the daily restart and the logs |
| settings | `hud.addon` | The HUD's Workshop id, 3823993566 in the example |
| settings | `hud.tab`, `hud.check` | Whether TAB takes the mouse for the board; whether a player whose game shows no HUD is told (`warn`), disconnected (`enforce`), or neither |
| settings | `bridge.discoveryStock` | How many maps new to the server are kept ready per mode |
| settings | `plugin.enabled` | `false` runs the server vanilla, for the days after a game update breaks Metamod or CounterStrikeSharp |
| settings | `restarts.*`, `maps.*` | The daily restart, restarts for updates, freeze recovery; the maps folder, the deletion outside the keep set, background downloads |
| settings | `sync.*`, `sync.webApiKey`, `sync.mail.*` | The Workshop sync: a Steam Web API key for its discovery and ratings, its limits, a mail relay for its problems |

## How it works

```
 players ──► CS2 dedicated server ──── log lines over HTTP ────► bridge (sidecar)
              │ Metamod + CounterStrikeSharp                      │ the vote, the dice, likes, crash marks, the lists
              │ OverStrike2 plugin: HUD, chat, the freeze,  ◄─────┤ RCON: map changes, plugin commands
              │   fun settings, catch-up, map settings            │ HTTP: the HUD's live state, the clicks
              │ MultiAddonManager mounts the HUD (a Workshop item)│
              │ os2-server: install, restart watcher ─────────────┤ HTTP: what is on disk, restarts coming
              │                                                   │
 Workshop ◄── os2-sync (optional): find, download, scan,  ──────► │ files: the map lists, ratings, its status
              load-test, list, hand over                          └ web/: a website, behind your login
```

- **The bridge** ([`bridge/`](bridge/)) is a small C# service: .NET 10, native AOT, a chiseled image that runs read-only as a non-root user and never reaches the internet. It decides what a click or a
  chat command asks for, writes nearly every word on the HUD, and talks back over RCON. A swap of it disconnects nobody.
- **The plugin** ([`plugin/OverStrike2/`](plugin/OverStrike2/)) is one CounterStrikeSharp plugin inside the game server. Much of its logic is source shared with the bridge, so the bridge's self-test covers it.
- **The HUD** ([`hud/`](hud/)) is a Panorama layout and stylesheet compiled into one Workshop addon, with every map's picture inside it, because CS2 refuses web pictures in a custom HUD. One plugin drives a game
  holding any version of it.
- **The server programs** ([`server/`](server/)) are `os2-server`, beside the engine in the game server's container, and `os2-sync`, in a container of its own: each one self-contained file, reading the same settings
  file ([docs/server.md](docs/server.md)). The game image is [joedwards32/cs2](https://github.com/joedwards32/CS2).

All of it, part by part: [The parts](docs/reference.md#the-parts).

## Repository layout

| Folder | What |
|---|---|
| [`bridge/`](bridge/) | The bridge: C# sources, its `--selftest`, the `Dockerfile`, the website's page and the starting data - `maps.json` (the map list), `broken.json`, `blocked.json`, `sizes.json`, `search.json` (titles, ids and tags), `ours.json` |
| [`plugin/`](plugin/) | The CounterStrikeSharp plugin `OverStrike2`, its self-test, and the catch-up module `CatchUp` with its own |
| [`core/`](core/) | `OverStrike2.Core`, what the parts share: the settings file, the keep set, the game server query, Steam's file formats; its self-test |
| [`server/`](server/) | The programs `OverStrike2.Server` (`os2-server`) and `OverStrike2.Sync` (`os2-sync`), each with its self-test, and each game mode's settings |
| [`hud/`](hud/) | The in-game HUD addon: layout, stylesheets, the shipped layouts, the thumbnail generator, the build script and the Workshop publisher |
| [`maps/`](maps/) | Our own maps: one generator per map and the Workshop publisher |
| [`deploy/`](deploy/) | [`compose.yml`](deploy/compose.yml) with its installer, for a host that builds everything itself; [`compose.example.yml`](deploy/compose.example.yml) and the scripts, for a host a workstation ships to, where every deployment value comes from a private config |
| [`tools/scan/`](tools/scan/) | The scanner that keeps secrets and private details out of this repository |
| [`docs/`](docs/) | [install.md](docs/install.md), [settings.md](docs/settings.md), [server.md](docs/server.md), [reference.md](docs/reference.md) |

## Build and test

Requirements, what each self-test holds and how a new HUD is tried in a real client: [Build and test](docs/reference.md#build-and-test). The commands every commit runs are in
[AGENTS.md](AGENTS.md#build-and-test-before-every-commit-that-touches-code).

## Deploy

A host that builds everything itself needs one command: [docs/install.md](docs/install.md). A workstation that builds and ships to a server has the scripts in [`deploy/`](deploy/README.md), which read every address, path,
container name and credential from a private config that never enters this repository.

What restarts what: data files reload by themselves, a bridge swap disconnects nobody, and the plugin, the launcher and the mode settings need a game server restart, which the restart watcher does by itself - at the end of the
match, at once with nobody on ([docs/server.md](docs/server.md#a-new-version-of-our-own-parts)).

The HUD is one Workshop item, updated in place: every new version waits for Valve's moderation, and a new item is never created ([`hud/PUBLISH.md`](hud/PUBLISH.md)).

## Keeping this repository clean

This repository is public, and the server it was built for is not. A pre-commit and a pre-push hook run [`tools/scan/scan.py`](tools/scan/scan.py): gitleaks for secrets, and rules against addresses, host paths, shares, local paths, Steam ids,
e-mail addresses, compiled game files and unreviewed binaries, plus a private denylist kept outside the repository. Enable the hooks once per clone:

```sh
git config core.hooksPath .githooks
```

No game files are included: builds read what they need from a local Counter-Strike 2 installation. The rules for changing this repository are in [AGENTS.md](AGENTS.md).

## License

[Apache License 2.0](LICENSE). The CounterStrikeSharp plugin and the bridge sources it compiles in are also available under the MIT License ([LICENSE-MIT](LICENSE-MIT)), as CounterStrikeSharp's license asks of plugins. Third-party notices are in
[NOTICE](NOTICE).

Counter-Strike 2 and the Steam Workshop are Valve's. This project is not affiliated with or endorsed by Valve. The map titles and tags in the data files are their authors'; their descriptions are not kept here.
