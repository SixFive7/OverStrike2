# Over Strike 2

*Over the top - On the Huis*

Over Strike 2 turns a Counter-Strike 2 dedicated server into an Arms Race (gun game) server that plays the Steam Workshop:
thousands of community maps, picked from an in-game menu or from chat, shared out fairly between the players who are on, voted
on, and rolled at random when nobody asks. Deathmatch and Wingman come in through the vote for the next map. Players install
nothing: the server hands their game the menu when they join.

The rest of this page says what it does for players, how it looks after itself, how it is built and tested, and how to run your own.
Every rule, setting and log line is in [docs/reference.md](docs/reference.md).

## For players

### The game modes

**Arms Race** is the server's game: a kill earns the next gun, and a kill with the golden knife on the last rung wins the match.
**Deathmatch** and **Wingman** are on every ballot, Wingman while four players or fewer are on. A map loads in its
own mode, with that mode's settings. Each mode has Valve's official maps and its own list of Workshop maps. The server goes back
to Arms Race after every restart, and after 10 minutes with nobody on. Grenade drops, the match length, the catch-up boosts and
friendly fire's settings are Arms Race's own; outside it the menu leaves them out.

### The menu

Hold **TAB** for the live board beside the scoreboard: the map that is on and who picked it, Like and Dislike, Next, what is queued,
and the way into the full menu. It takes the mouse for as long as TAB is down, and letting go, a hit or death gives the mouse back.
**TAB + E** opens the full menu, a CS2-style window. It closes by itself after 30 seconds without a click and the moment you are hit,
so nobody is left standing in it. Its pages:

| Page | What is on it |
|---|---|
| **Start** | The welcome, three steps and how to reach the menu. A first-time player gets this window at their first spawn; a returning one a short card. |
| **Maps** | Eight cards a page, each with the map's picture, number, size and votes, its Play button and vote arrows, and the mode it plays in: a label on its picture and the card's colour (Arms Race gold, Deathmatch red, Wingman blue). Full-text search on an on-screen keyboard over names, tags and Workshop ids; sort by number, votes, name or size; filter by size (under 25 MB, 25-120, 120-400, 400 and up), by your own likes, by official maps, by Our maps (its button glows) and by game mode. A map with no picture in the HUD has a button on its card that shows its Workshop preview on request. Any Workshop map can be played by its id, typed on the keyboard or pasted in chat: its card asks the Workshop about it, and **Download and play** fetches it to the server, with the progress on the card, while nobody's game is interrupted; then it plays like any pick. |
| **Queue** | Who is next and who asked, Cancel on your own requests or all of them, Previous for the map before this one. |
| **Game** | The match length (10 or 60 minutes, from the next match), new maps in random picks (always, half, never), and Restart for a stuck server, twice to confirm. |
| **Fun** | The fun settings, the grenade drops and catch-up, with **Random** (every setting drawn at random) and **Default** (everything back to normal); a setting off its default is gold with a dot. |
| **Help** | Where everything is, and what it does. |
| **Rules** | The vote, how votes count, the fair queue. |
| **How it works** | Every chance in a map pick: the queue, Next, the vote, the lists, the weights and everything that is never rolled. |
| **Sync** | The Workshop sync's dashboard: what it does now (with the step on a bar), what waits in its queue, the random queues on the server's disk, and its history of runs and map events, newest first, a page at a time. |

Everybody also sees a news line (who picked what, the queue moving, a vote started, a crash, a map's blocked settings), a countdown
banner before a map change, a now-playing card at the first spawn on each map, and the name and tagline for a few seconds on
joining. Each player has a private note line, which answers what they just clicked.

### Chat commands

A chat line that starts with `!` or `/` does exactly what the matching click does, with the same refusals and confirmations. `!help`
lists the primaries; the table with all of them is in [docs/reference.md](docs/reference.md#the-menu-and-the-commands).

| Group | Commands |
|---|---|
| Maps | `!play` `!find` `!maps` `!next` `!random` `!back` |
| Queue | `!q` `!cancel` `!done` |
| Voting | `!up` `!down` `!toobig` `!ads` `!vote` |
| Game | `!fun` (`!fun random`, `!fun default`) `!nades` `!catchup` `!time` `!newmaps` `!restart` |
| Menu | `!menu` `!help` `!rules` `!how` `!sync` |

**Aliases teach.** Every command has its aliases, and the words players know from other servers are among them (`!map`, `!rtv`,
`!nominate`, `!skip`). An alias runs the action, and its private reply shows what ran: `You typed !map 12 - ran !play 12`. A `/` line is hidden
from the others; a `!` line stays as normal chat. A pasted Workshop link or id opens that item's card.

### The fair queue

Play on a card is a request; on your own queued map Play takes it back (on a ballot card, **Remove mine**; or `!cancel`). On a map nobody holds, a random map or the start map, it plays at once; on somebody's map it waits in the
queue. A map two or more players on the server want goes first, then whoever has had the least play time from their own requests
this session, so one player's five requests wait behind everyone else's first. Each player has up to five requests, and only players on the
server are queued. A requested map stays until its match ends, its requester says **I'm done**, a vote replaces it, or its requester has been
gone for over a minute while others wait. **Next** starts the vote for the next map, with Keep playing on it; on your own map it is I'm done.

### The vote for the next map

When a match ends everybody votes on the Maps page's cards, for 60 seconds or until everybody on the server has voted (alone, your vote ends it
at once). The ballot holds at least eight maps, and Random:

1. the queue: every request, with each connected requester's vote already in on their first;
2. a map of each other game mode that suits the players on, unless a request is one;
3. maps of the server's own mode, rolled like every random pick, up to eight maps in all;
4. Random, a button of its own: the dice after the vote.

Every card shows its mode, and while the vote runs its share of the votes as a bar and its voters' names; the leading card is lit (or a tie
shows as a tie), a strip across the top splits the votes by card, and a line names everybody who has not voted yet. A match-end vote stays on
screen until it is over. Eight cards make a page, and a longer ballot pages, with a hint you cannot miss. **Open the map browser** lets you put
any map on the ballot for everybody, with your vote on it (one map each; `!play` in chat does the same). Click a card's Vote, or type its number:
a bare `3`, or `!vote 3`. A click also shows in chat, as that player's `!vote 3`, so menu users learn the chat way. The most votes win, a tie is
drawn at random, and no votes at all is Random. A queued map that loses stays queued. The map a vote chose stays until its match ends or another
vote replaces it.

**Next starts the same vote at any time**, with one card more: **Keep playing** the map that is on. Only your own window opens; the others see
it on the news line and a gold line at the top of the screen, and TAB or `!vote` opens it. If Keep wins, or nobody votes, the map stays - a request stays its requester's - and you wait 2 minutes before starting
another. Any other winner replaces the map, even somebody's request: that is the only way to cut one short. `!random` is the same vote with
your vote on Random. On your own map Next is I'm done, and the vote that follows has no Keep playing.

While a vote is open `!play` adds its map to it and `!random` votes Random; `!next`, `!back` and `!done`, with all their aliases, only answer
with what to type, and requests reopen after the vote.

### Votes and why

**Like** and **Dislike** vote on the map that is on, the arrows on any map's card. One vote per player per map, and a vote steps: Like on a
disliked map takes the dislike back first, and says so ("Your dislike of ... is removed - like it again to add a like"); the next Like casts it.
The same the other way round. The same vote again only says you voted so already: repeating a vote never takes it back.

**A Dislike asks why**, right where you clicked it: over the card's picture, in the window in place of Like and Dislike, or on the TAB board,
for 15 seconds. A dislike typed in chat while the window is closed gets the list at the left of the screen. Chat answers it too (`!down big`),
and names one directly on any map (`!down 12 big`); a reason on a map you liked takes the like back first. With no answer the vote stays
"I did not like the map". Too big and Ads are reasons now, not buttons of their own.

| Word | Reason | What it does |
|---|---|---|
| `meh` | I did not like the map | A dislike |
| `big` | Map too big | A dislike, and only 1 random pick in 100 takes a too-big map |
| `ads` | Shows ads or sends players away | A dislike, and the map is never a random pick |
| `small` | Map too small | A dislike |
| `broken` | Map is broken | A dislike |
| `slow` | Runs badly | A dislike |

### Random maps

The dice roll at a server start and for Random on the ballot. Each Like doubles a map's odds, up to five; each Dislike quarters them; Workshop
popularity gives up to three times. The **new maps** setting on the Game page says which list a pick comes from: always the newer maps, half a coin
flip, never the original list. Never rolled: the map that is on, the last 10 maps played while others are left, maps that crashed the server or a
player's game, broken and blocked maps, maps gone from the Workshop, and maps marked as showing ads. A map's download size plays no part. After 10 minutes
with nobody on, the new-maps setting, the grenade drops and every fun setting go back to their defaults.

The dice are rolled ahead: the bridge keeps a queue of 20 rolls for each list (the new maps, the original list, Deathmatch, Wingman), and the server
downloads them in the background while you play, so at least 10 maps of every mode are on its disk at all times and a ballot's cards never wait
for a download. A pick takes the first
entry that is still eligible and already on the server's disk; the vote's random maps come from the same queues, and a map shown in a vote and not
chosen comes back no sooner than the dice would bring it.

### Fun settings

Sixteen settings anybody can switch until the map changes, each with its own word; a bare `!fun` opens the page and writes the words in chat.
Every change shows centre screen for everybody for 10 seconds, with who made it, and a setting off its default is gold with a dot on the page.
**Catch-up** has its own row there too: on by default, off for everybody until the map changes (`!catchup off`).
**Random** on the page (`!fun random`) draws every setting from its own choices and the grenade drops on or off, never a combination the page would
not make; **Default** (`!fun default`) puts everything back as 10 minutes with nobody on does. Either says in chat who did it.

| Word | Setting | Choices |
|---|---|---|
| `!gravity` | Gravity | normal, low, moon |
| `!jump` | Super jump | normal, high |
| `!bhop` | Bunny hopping | off, on, turbo |
| `!fall` | Fall damage | on, off |
| `!ice` | Ice floor | off, on |
| `!aim` | Laser guns: no spread and no recoil | off, on |
| `!ammo` | Infinite ammo | off, on |
| `!hs` | Headshots only | off, on |
| `!vampire` | Vampire: spawn with 1,000 health, the damage you deal heals you up to 2,000 | off, on |
| `!ghost` | Ghost: invisible for 2 seconds after every kill | off, on |
| `!nades` | Big grenades, a wider blast | normal, big, huge |
| `!damage` | Double damage | off, on |
| `!ffa` | Free-for-all | off, on |
| `!ff` | Friendly fire, on by default | on, off |
| `!instakill` | Instakill: any bullet or knife hit on a teammate kills, on by default | on, off |
| `!ffnades` | Grenades x10 on teammates, on by default | on, off |

**Grenade drops** sit on the same page, and `!nades on` or `!nades off` switches them: every player gets one HE grenade after spawning, and it drops
where they die. The news line says who switched what. A map that sets its own value of a setting keeps it until a player changes it, and gets it back.

### Catch-up boosts

In Arms Race a player who falls behind the leader gets a boost at spawn, more the further behind, and a private message says what it is and how many
kills behind they are.

| Kills behind | Health | Damage taken | Damage dealt | Lifesteal | Regeneration | Also |
|---|---|---|---|---|---|---|
| 2 | +30% | -15% | +15% | 15% | 3% a second, after 4 s unhurt | |
| 4 | +60% | -25% | +25% | 25% | 5% a second, after 3 s | a second chance once per life, 2 s more spawn protection |
| 7 | +100% | -35% | +40% | 35% | 8% a second, after 2 s | a second chance, 3 s more spawn protection, infinite ammo, a one-hit knife and Zeus |

The health is a share of what the life spawned with: 1,000 under Vampire, never Vampire's 2,000 ceiling. A leader 2 kills or more ahead of second place
takes more damage from enemies: 10% for every 2 kills of lead, up to 30%. A cap keeps the sum of it all within what the scoreboard shows: a boosted
player may be at most twice the leader's shown edge, (the leader's kills + 1) / (theirs + 1), and never more than 3.5 times as strong on paper. For
the leader's enemies the bounty counts first inside that cap; the leader's own teammates, who never fight him, get the whole of it. A team kill
under friendly fire takes the killer down a level, as Arms Race does. The kill count comes from the match's own events, and every kill is in the
plugin's log with the counts after it ([`plugin/CatchUp/`](plugin/CatchUp/)).

### Broken maps and why

Thousands of community maps include some that break things, so every broken map says why, on its card and in the confirm before anyone loads it.

- **Maps that crash the server** are counted when it happens: a pick asks to confirm, and the dice never roll them.
- **Maps that crash players' games** mark themselves: three players timing out within 60 seconds of entering a map, while the server keeps answering, mark it
  broken, and the dice take the server to another map at once.
- **The broken list** names crashers, freezers and maps that advertise, each with its reason. A pick asks to confirm, and the dice never roll them.
- **Items that are not maps** (skins, models, empty uploads) are refused, **maps gone from the Workshop** are refused and shown as unavailable, and **blocked maps**
  are refused for good, each with its reason.
- **What a map changes** in the game's settings is put back when it ends. **The game-changers** no map may set (no spread, radar off, a mode switch, cheats) are put
  back within a second, per game mode, and the news line tells the players.

### The HUD's version

The window says when a player's HUD is older or newer than the server's, by comparing version numbers in the layout itself, and an older game is told how
to update: quit CS2 and start it again, and Steam updates it. A game holding older content keeps working: what it lacks gets no words until it has shown that it
has them, by a click on a button only that content has or by asking for the page in chat.

The server also checks that a player's game really shows the HUD. The only proof is a click, so a player not seen clicking on this release gets the window with one gold button,
**Click to play**; one who has not clicked 90 seconds after their first spawn is warned in chat and disconnected. It never disconnects anybody while Steam does not show the HUD's Workshop
item to everybody, or before a real click has proved the check works on that release.

### Our maps

The server's own maps have a filter on the Maps page and a tag on their cards. Which maps they are is data (`bridge/ours.json`); these three are generated from code
([`maps/`](maps/)), geometry, props, spawns and breakable glass:

- **gg_glasspire**: a square glass tower around an open atrium, with ramps spiralling up the outer walls.
- **gg_moonglass**: a moon base under a faceted glass dome, with glass bridges, rooms and tunnels.
- **gg_vinceville**: small houses scattered over a grassy field, with ponds deep enough to swim in and a sewer that links every house.

### What is in which HUD

All of this is what the code does with the HUD content it is built for. The reasons popup and the grenade drops on the Fun page come with content `v4_1_0`; the pictures of maps not
listed yet, the picture button and the game-mode filter with `v4_2_0`; the Sync page with `v4_3_0`; the vote's pages, Random's button and the map browser that adds to the
vote, the cards' mode labels and colours, the reasons where a Dislike was clicked, the Fun page's Random and Default and the Our maps glow with `v4_4_0`. Each version waits
for Valve's review first, and a game holding an older one keeps working without them (an older game shows the ballot's first eight maps, and its other numbers can be typed).

## A server that looks after itself

- **The restart watcher** runs inside the game server's container. It restarts for game updates and once a day, only between matches, kills a frozen engine so Docker restarts it,
  warms the Steam client up after every start, downloads the bridge's random queues and the queued requests in the background, and prefetches at night the maps the server keeps that
  are missing or have a newer Workshop copy. A process whose Workshop downloads get stuck is restarted when nobody is on. All of it has loop protection.
- **What the server keeps.** Every map with at least one up vote; per mode three lists of 100 playable maps together - the Workshop's best-rated, its most subscribed and its
  most popular over the last 30 days - so up to 300 a mode; our own maps, the HUD, the random queues and the queued requests; everything else goes at the next engine start.
  The deletion refuses when its inputs are missing, the scores are stale, a list is short or the set looks too small, and never touches our maps, the HUD or a queued map.
- **The download line.** While the server downloads a map somebody is waiting for, the news line says so, with the progress.
- **The Workshop sync** ([`server/workshop-sync.py`](server/workshop-sync.py), an optional third container) finds new Arms Race, Deathmatch and Wingman maps through the
  Steam Web API, and works through every queued map: the ones that would make one of their mode's three lists first, then new uploads, listed maps that changed, and everything
  else by popularity. It fetches the Workshop's scores, subscriptions and trend once a day, and hands the server the maps it keeps but lacks. It downloads anonymously with SteamCMD,
  one map at a time, scans each download for what it is and for commands that advertise another server or a gambling site, loads each map in a test copy of the server, and lists the
  ones that pass; the rest go on the broken list with their reasons. It works at any hour while nobody plays and stops within seconds when somebody joins; it has no daily download cap
  and stops when Steam pushes back, keeps hard Web API limits in the code that the configuration can lower and never raise, and, given a mail relay, mails only its problems - each once a day at most, pointing to your dashboard.
  What it is doing is on the menu's Sync page for everybody, and the bridge writes the same as a small website (an `index.html` and its data) for a web server behind your login.
- **The daily Workshop check** asks the public Web API about every listed map, so a map that is gone, private or banned is refused at once.

## How it works

```
 players ──► CS2 dedicated server ──── log lines over HTTP ────► bridge (sidecar)
              │ Metamod + CounterStrikeSharp                      │ queue, votes, dice, crash and Workshop checks
              │ OverStrike2 plugin: menu, HUD, grenades,  ◄───────┤ RCON: map changes, notices
              │   map settings, fun settings, catch-up            │ HTTP: the menu's live state, the clicks
              └ MultiAddonManager hands every player the HUD addon (a Steam Workshop item)
```

- **The bridge** ([`bridge/`](bridge/)) is a small C# service: .NET 10, base class library only, native AOT, a chiseled image that runs read-only as a non-root user and stays off the internet. The
  game server pushes its log lines to it; it decides what a click or a chat command asks for and talks back over RCON. Its state (votes, history, crash counts) is a few JSON files in one directory, and
  its map lists are data files it reads again by itself when they change.
- **The plugin** ([`plugin/OverStrike2/`](plugin/OverStrike2/)) is one CounterStrikeSharp plugin inside the game server: the menu and the board, chat, grenades, the settings put back and blocked, the fun
  settings, the catch-up boosts and the Workshop checks. Much of its logic is source shared with the bridge, so the bridge's self-test covers it.
- **The HUD** ([`hud/`](hud/)) is a Panorama layout and stylesheet compiled into one Workshop addon, with every map's picture inside it, because CS2 refuses web pictures in a custom HUD. The pictures of the Workshop maps still queued ship ahead, so a map the nightly sync lists later has its picture without a new HUD. Nearly every word on it comes from
  the plugin, so most changes need no new HUD. A new version adds to the panels of the old one, so one plugin drives a game holding any of them.
- **The server files** ([`server/`](server/)) are the install hook the [joedwards32/cs2](https://github.com/joedwards32/CS2) image runs before every start (it installs Metamod:Source, CounterStrikeSharp and
  MultiAddonManager at pinned versions and copies the plugin in), each game mode's settings, the restart watcher and the Workshop sync.

## Repository layout

| Folder | What |
|---|---|
| [`bridge/`](bridge/) | The bridge: C# sources, its `--selftest`, the `Dockerfile`, and the data files - `maps.json` (the map list), `broken.json` (crashers, non-maps, removed and blocked maps), `sizes.json` (measured map sizes), `search.json` (the menu's search text: titles, ids and tags), `ours.json` (our maps) |
| [`plugin/`](plugin/) | The CounterStrikeSharp plugin `OverStrike2`, its self-test, and the catch-up module `CatchUp` with its own |
| [`hud/`](hud/) | The in-game HUD addon: layout, stylesheets, the thumbnail generator, the build script and the Workshop publisher |
| [`maps/`](maps/) | Our own maps: one generator per map and the Workshop publisher |
| [`server/`](server/) | The game server's install hook (`pre.sh`), each game mode's settings, the restart watcher (`update-watch.py`) and the Workshop sync (`workshop-sync.py`), each with its tests |
| [`deploy/`](deploy/) | Deployment scripts and an example compose file; every deployment value comes from a private config |
| [`tools/scan/`](tools/scan/) | The scanner that keeps secrets and private details out of this repository |
| [`docs/`](docs/) | The reference: every part, rule and setting |

## Build and test

Requirements: the .NET 10 SDK, Python 3.12+, and a container builder (Docker, or WSL containers on Windows) for the bridge's image. The HUD and the maps need Windows with Counter-Strike 2 and its Workshop Tools.

```sh
cd bridge && dotnet build && dotnet run -- --selftest        # the bridge, the shared HUD logic, the rules, the chat table against these docs
cd plugin/OverStrike2 && dotnet build -c Release              # the plugin (warnings are errors)
cd plugin/OverStrike2.SelfTest && dotnet run                  # the grenade logic
cd plugin/CatchUp/SelfTest && dotnet run                      # the catch-up rules, replayed against a real match's log
docker build -t localhost/overstrike2-bridge bridge           # the bridge's image (native AOT inside the SDK image)
docker run --rm -v "$PWD/server:/repo:ro" -w /repo python:3.9-slim python test_update_watch.py   # the restart watcher
python server/test_workshop_sync.py                           # the Workshop sync
python bridge/search-index.py --check                         # the search text is in step with the map list
```

**The tests.** `--selftest` runs hundreds of checks on a handed clock, with no server: the log parser, the queue, the votes, the ballot, the dice, the HUD's words and wiring, every panel id and class against the layout,
the older HUD contents against the panels sent to them, and every number in the menu's rules against the code's constants. It also holds the chat table in
[docs/reference.md](docs/reference.md#the-menu-and-the-commands) to the code, so a rule change fails until the words follow.

**A real client.** A layout cannot be fully tested without the game, so a new HUD is also tried in a real CS2 client before a release: a private harness, not in this repository because it drives its owner's own client and
server, starts the client with no window on screen, joins a test copy of the server, loads the local HUD build there with VAC off, takes screenshots, and drives the menu through server-side clicks. On the live server only the
published HUD ever runs, in a plain client.

## Configure

Two containers - the game server and the bridge - and optionally a third for the Workshop sync. [`deploy/compose.example.yml`](deploy/compose.example.yml) is a complete example with every setting explained; the essentials:

| Where | Setting | What |
|---|---|---|
| both | `RCON_PASSWORD` / `CS2_RCONPW` | the same RCON password on the bridge and the game server |
| both | `LOG_SECRET` | a random path segment: the game server pushes its log to `http://bridge:8080/log/<LOG_SECRET>` (`CS2_LOG_HTTP_URL`) |
| game server | `CS2_PLUGINS` | `1` installs and loads the plugin layer; `0` runs vanilla (after a game update broke a dependency) |
| game server | `GUNGAME_HUD_ADDON` | the HUD addon's Workshop id; empty falls back to a simple text panel |
| game server | `CS2_AUTOUPDATE`, `CS2_RESTART_AT`, `CS2_FREEZE_RECOVERY`, `CS2_WARMUP`, `CS2_PREFETCH_AT` | the restart watcher's jobs |
| sync | `SYNC_TAGS`, `SYNC_RUN_HOURS`, `SYNC_PREFETCH_AT` and `SYNC_DAILY_RESTART` (the watcher's times), the `SYNC_*` limits, a Steam Web API key file | the Workshop sync (optional: a third container) |
| bridge | `MAPS_FILE`, `BROKEN_FILE` | the map list and the broken list on the data volume, so a new list is a file copy (read again within seconds) |
| bridge | `HUD_TAB`, `HUD_CHECK`, `RANDOM_ON_MATCH_END`, `RANDOM_ON_START`, `MATCH_END_VOTE` | how the board, the HUD check, the dice and the vote behave |

Every bridge setting is in [docs/reference.md](docs/reference.md#configuration-environment), and the restart watcher's in its own section there.

## Deploy

Builds happen on a workstation; there is no CI and no registry. The scripts in [`deploy/`](deploy/) build the bridge's image locally, load it into the server's Docker and swap the running bridge without disconnecting anybody; copy the
plugin, the data files and the server files to the server; and build and publish the Workshop items. They read every address, path, container name and credential from a private config that never enters this repository - see
[`deploy/README.md`](deploy/README.md) and [`deploy/config.example.toml`](deploy/config.example.toml).

What restarts what: data files reload by themselves, a bridge swap disconnects nobody, and the plugin, the install hook and the game server's settings need a game server restart, which disconnects everybody.

The HUD is one Workshop item, updated in place: every new version waits for Valve's moderation, and a new item is never created ([`hud/PUBLISH.md`](hud/PUBLISH.md)).

## Keeping this repository clean

This repository is public, and the server it was built for is not. A pre-commit and a pre-push hook run [`tools/scan/scan.py`](tools/scan/scan.py): gitleaks for secrets, and rules against addresses, host paths, shares, local paths, Steam ids,
e-mail addresses, compiled game files and unreviewed binaries, plus a private denylist kept outside the repository. Enable the hooks once per clone:

```sh
git config core.hooksPath .githooks
```

No game files are included: builds read what they need from a local Counter-Strike 2 installation.

## License

[Apache License 2.0](LICENSE). The CounterStrikeSharp plugin and the bridge sources it compiles in are also available under the MIT License ([LICENSE-MIT](LICENSE-MIT)), as CounterStrikeSharp's license asks of plugins. Third-party notices are in
[NOTICE](NOTICE).

Counter-Strike 2 and the Steam Workshop are Valve's. This project is not affiliated with or endorsed by Valve. The map titles and tags in the data files are their authors'; their descriptions are not kept here.
