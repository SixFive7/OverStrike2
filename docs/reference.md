# Over Strike 2 - reference

How the server works, what every part does and how to configure it. The project's front page is the [README](../README.md);
the code is the final word, and the bridge's `--selftest` holds most of what is written here to it.

## The parts

| Part | Where | What it does |
|---|---|---|
| The game server | the `joedwards32/cs2` image, with [`server/pre.sh`](../server/pre.sh) | A Counter-Strike 2 dedicated server. Before every start the install hook installs Metamod:Source, CounterStrikeSharp and MultiAddonManager at pinned versions, keeps `gameinfo.gi` pointing at Metamod, and copies the plugin in. |
| The plugin | [`plugin/OverStrike2/`](../plugin/OverStrike2/) | One CounterStrikeSharp plugin in the game server: the menu and the board on screen, the welcome, the HUD check, grenade drops, map settings put back, the game-changers block, the fun settings, the catch-up boost and the daily Workshop check. |
| The bridge | [`bridge/`](../bridge/) | A small sidecar. The game server pushes its log lines to it over HTTP; it keeps the queue, the votes and the history, rolls the dice, counts crashes, and sends map changes and notices back over RCON. It serves the plugin the menu's live state and takes its clicks. |
| The HUD | [`hud/`](../hud/) | A Panorama layout and stylesheet published as one Steam Workshop item. MultiAddonManager hands it to every player who joins; every word on it comes from the plugin. |
| The restart watcher | [`server/update-watch.py`](../server/update-watch.py) | Runs inside the game server's container: restarts for game updates and once a day, between matches; kills a frozen engine; warms the Steam client up; prefetches Workshop maps at night. |
| The nightly Workshop sync | [`server/workshop-sync.py`](../server/workshop-sync.py) | An optional third container: finds new maps on the Workshop, downloads, scans and load-tests them, and lists the ones that pass. |
| The site | [`site/`](../site/) | A static page of every map with its picture and the rules, and how to join. |

The bridge is .NET 10 with the base class library only, published native AOT into a chiseled image; it runs read-only, with all
capabilities dropped, as a non-root user, and writes only to its state directory. It does not reach the internet: everything
that needs Steam (the Workshop checks, downloads) happens in the game server's container, and the bridge only hears the
verdicts.

## The menu and the commands

The HUD's window is the whole interface: every map with its picture, size and votes, a search over names, tags and Workshop ids
on an on-screen keyboard, any Workshop map by its id, the queue, the game settings, the fun settings, help, the rules and how it
all works. Holding **TAB** shows a board beside the scoreboard - Like, Dislike, Next and the queue - that takes the mouse while
TAB is down; TAB + E opens the full window.

A chat line that starts with `!` or `/` is a command too, and does exactly what the matching click does (the same action, the
same refusals and confirmations). A line stays visible as the player's normal chat line; only a `/` line is hidden, which is
CounterStrikeSharp's own silent trigger. A pasted Workshop link or id opens that item's card.

One table in the code (`ChatCommands.All`), which `--selftest` holds this one to. `!help` lists the first column only. An alias runs
the action, and its private reply teaches the primary by showing what ran: `You typed !map 12 - ran !play 12`.

| Command | What it does | Aliases |
|---|---|---|
| `!play <number, name or Workshop ID>` | Play a map now, or queue it | `!map` `!p` `!nominate` `!load` `!go`, and `!pick` |
| `!find <words>` | Search the maps: the menu opens on the matches | `!search` `!s` `!lookup`, and `!maps <words>` |
| `!maps` | Open the map browser | `!browse` `!list` `!maplist` `!m` `!top` (sorted by votes) |
| `!next` | The board's Next: a random map, I'm done on your own, a skip vote on another's | `!n` `!rtv` `!change` `!skipmap`, and `!new` with no argument |
| `!random` | A random map, while nobody holds this one | `!roll` `!dice` `!rand` `!any` `!shuffle` |
| `!back` | Back to the last map | `!previous` `!prev` `!last` `!undo` `!again` |
| `!q` | Open the queue | `!queue` `!upcoming` `!whatsnext` `!lineup` `!order` |
| `!cancel [number]` | Drop your request for that map, or all of them | `!unqueue` `!remove` `!drop` `!withdraw` `!uq` |
| `!done` | End your own map: the queue moves on | `!finish` `!end` `!release` `!letgo` `!over` |
| `!skip` | Vote to skip someone else's map | `!voteskip` `!vs` `!boo` |
| `!up [number]` | Like the map that is on, or that map | `!like` `!+` `!good` `!love` `!thumbsup` |
| `!down [number] [reason]` | Dislike it, and why (see Voting) | `!dislike` `!-` `!bad` `!meh` `!thumbsdown` |
| `!toobig [number]` | Too big: only 1 random pick in 100 | `!big` `!huge` `!toolarge` `!size` `!xl` |
| `!ads [number]` | It shows ads or sends players away: never a random pick | `!ad` `!spam` `!advert` `!report` `!scam` |
| `!vote <1-6>` | Your choice in the vote at a match's end | `!v` `!mv` `!choose`, and `!pick` while a vote is open |
| `!fun` | The Fun page, and each setting's own word | `!set` `!settings` `!toggle` `!mode` |
| `!nades <on\|off>` | Grenade drops on or off | `!grenades` `!nade` `!he` `!drops` `!boom` |
| `!time <10\|60>` | Match length in minutes, from the next match | `!length` `!minutes` `!timelimit` `!roundtime` `!duration` |
| `!newmaps <always\|half\|never>` | New maps in random picks | `!fresh` `!pool` `!mix` `!newmap`, and `!new` with an argument |
| `!restart` | Restart a stuck server - twice to confirm | `!reboot` `!fix` `!unstuck` `!stuck` `!reset` |
| `!menu` | Open the full menu | `!gg` `!hud` `!os` `!os2` `!open` |
| `!help` | The Help page, and this list | `!commands` `!cmds` `!h` `!?` `!info` |
| `!rules` | The rules | `!rule` `!r` `!fair` `!howto` `!policy` |
| `!how` | How it works: every chance in map picks | `!howitworks` `!odds` `!chances` `!why` `!explain` |

Three words depend on what follows them, by rule, and nothing else is two things: `!pick` is a vote while a vote is open and a
number was given, otherwise it plays; `!new` with an argument is `!newmaps`, without one `!next`; `!maps` (and its aliases) with
words is `!find`.

**Each fun setting is a word of its own**, with the setting's id and its choices: `!gravity low`, `!vampire on`, `!bhop turbo`. A bare `!fun`
opens the Fun page and writes the words in chat, for the settings the page shows in the game mode that is on. `!fun gravity low`
still works and teaches the short word. `!nades` is both: `on` and `off` are the grenade drops, `normal`, `big` and `huge` the
blast of the grenades (Big grenades); `!fun nades on` and `!fun nades big` work too.

**A vote in chat.** While the end-of-match vote is open a bare number from 1 to 6 counts as a vote, like `!vote 3`, and the line
still shows as typed. A click on a card prints the same thing for everybody, as `[ALL] <name>: !vote 3` in the player's team's
colour, so that menu users learn the chat way. Every card shows its number.

The actions the menu sends the bridge (`/act`) are a closed list (`Hud.IsAction`): nothing typed or clicked reaches the server
console.

## Requests and the fair queue

A pick is a request. Whether it plays at once depends on who put the current map there:

1. **A map nobody holds is replaced at once** - a random map, the start map, or a map its requesters let go of.
2. **A requested map is held** by whoever asked for it until the match ends, they say they are done, they have been off the
   server for over 60 s while others wait, or a majority of the other players votes to skip it.
3. **The order:** a map that two or more players on the server want goes first; then one map at a time to whoever has had the
   least play time from their own requests this session (a queued request counts as 10 minutes ahead of time; your own requests
   play in the order you made them).
4. **No hoarding:** somebody who starts requesting is lifted to at least 10 minutes below the least-served player waiting, so
   sitting out buys one turn, not five in a row. After 30 minutes with nobody on, play time goes back to zero.
5. **Only players on the server are queued.** A player who leaves drops out after 60 s; a map change is a reload, not leaving,
   and has 10 minutes to finish.
6. One request per map, five each. Asking for a queued map joins it and moves it up; asking for the map that is on claims it.
7. Every change to the queue is announced on the HUD's news line, merged into one line per burst.

The design is the scheduling idea of fair-share CPU schedulers: play time is charged as maps play, the least-served requester
goes next, and newcomers start just below the least-served active requester.

## The end-of-match vote and the game modes

When a match ends there is a vote: every player gets a ballot on the menu's map cards for 60 s, or until a strict majority of
the players present has chosen one option. The ballot holds up to five maps plus **Random**:

1. the queue - each connected requester's first request, with that requester's vote already cast;
2. one map of each other game mode that suits the players on (Deathmatch; Wingman only for four players or fewer);
3. maps of the server's own mode, rolled like every random pick;
4. Random.

A player votes by clicking a card's **Vote**, or in chat with its number (`3`, or `!vote 3`). The winner plays; a queued map that
loses stays queued. The game modes are switched by the plugin (`os_mode
armsrace|deathmatch|wingman`), each with its own server cfg (`server/gamemode_*_server.cfg`) and block list
(`plugin/OverStrike2/blocked*.txt`).

## Random picks

The dice roll at a server start (`RANDOM_ON_START`), at a match end with nobody queued (`RANDOM_ON_MATCH_END`) and on **Next**.
Without the bridge a finished match replays the same map: the Arms Race cfg sets `mp_match_end_restart 1` and
`mp_match_end_changelevel 0`, and the bridge acts on the log's `Game Over:` line.

The candidates are the listed maps, minus the map that is on, every map with a crash recorded or on the broken list, every
non-map, every map marked **Ads** by any player, and the last 10 maps played. Of those:

- **The list:** the newer maps (list numbers from `FirstNewRank` up) or the original list, by the server-wide setting **new
  maps in random picks** - always (the default), half (a coin per roll) or never.
- **Too big:** a map whose measured area class in `sizes.json` is XL, or that a player marked too big, comes up in 1 random pick
  in 100.
- **The weights:** `weight = rank × 2^min(up, 5) × 0.25^down`, with `rank = 1 + 2 × (N − r) / (N − 1)` for rank `r` of `N`:
  each upvote doubles a map's odds (up to 5), each downvote quarters them, Workshop popularity gives up to 3×.

After 10 minutes with nobody on, the new-maps setting, the grenade drops and every fun setting a player changed go back to
their defaults.

## Voting

**Like** and **Dislike** vote on the map that is on, the arrows on any map's card; the same vote again takes it back, one vote
per player per map. **Too big** is a dislike that also marks the map too big (1 random pick in 100); **Ads** is a dislike that
keeps the map out of random picks for good (it shows adverts or sends players to another server). Votes live in
`STATE_DIR/votes.json`; a file that does not parse is moved aside, never overwritten.

**A dislike keeps its reason**, as the vote's value in `votes.json` (`DownReasons`). A plain Dislike counts at once as the first one:

| Word | Reason | Value | What it does |
|---|---|---|---|
| `meh` | I did not like the map | -1 | A dislike |
| `big` | Map too big | -2 | As **Too big** |
| `ads` | Shows ads or sends players away | -3 | As **Ads** |
| `small` | Map too small | -4 | A dislike |
| `broken` | Map is broken | -5 | A dislike |
| `slow` | Runs badly | -6 | A dislike |

Chat steers it directly: `!down big` on the map that is on, `!down <number> <reason>` on any map (`!down 12 big`), a new vote with its
reason if there was none. A reason replaces the one before and never takes the vote back; the same one again changes nothing.
Plain `!down` keeps the toggle: on a reason it goes back to the plain dislike, on the plain dislike it takes the vote back. The
health page counts the votes per reason.

### Keeping the rules in sync

The rules are said in three places: the menu's How it works page, its Help page, and the site's how-to-play guide. The
bridge's `--selftest` holds all three to the code: every number those words state must be one of the code's constants, the
site's guide must carry How it works and every Help row word for word, and a rule change fails the selftest until the code, the
menu's words (`bridge/Hud.cs`) and the site (`site/broken-maps.py` stamps the guide) agree again.

## Maintenance flag

While `STATE_DIR/maintenance` exists, the bridge makes no automatic map change: no start roll, no match-end roll, no queue
advance. Requests still queue. Remove the file and everything resumes.

## Crash handling and the map lists

- **Crashes.** A few seconds after every map start the bridge reads RCON `status`; its `spawn` counter tells whether the server
  process is new. A new process that did not quit cleanly is a crash, counted against the map it was loading. A map with a crash
  asks the player to confirm a pick, and the dice skip it.
- **`broken.json`** (`BROKEN_FILE`) holds four lists: `maps` (maps that crash or freeze the server, or advertise: a pick asks to
  confirm, the dice never roll them, each says why), `packs` (Workshop items with no map in them - skins, models, empty
  uploads: refused), `removed` (maps gone from the Workshop: refused; a seed the daily check keeps current) and `blocked`
  (refused for good, each with a reason).
- **The daily Workshop check.** The plugin asks the bridge which ids to check, asks the public Steam Web API about them, and
  hands the verdicts back; a map that is gone, private or banned is refused at once (every client would fail to download it).
- **`ours.json`** names the server's own maps (the menu's Our maps filter and card tag).

The data files (`maps.json`, `broken.json`, `sizes.json`, `search.json`, `ours.json`, and the mode lists) are read again
about 10 seconds after they change: a new list is a file copy, nothing restarts. A file that does not parse changes nothing.

## Configuration (environment)

The bridge:

| Variable | Default | Meaning |
|---|---|---|
| `RCON_HOST` | `cs2` | The game server's host name on the container network. |
| `RCON_PORT` | `27015` | RCON TCP port. |
| `RCON_PASSWORD` | (required) | The game server's `rcon_password` (`CS2_RCONPW` on the image). |
| `LOG_SECRET` | (required) | The path segment the game server pushes its log to: `http://<bridge>:8080/log/<LOG_SECRET>`. Anything else gets a 404. |
| `HTTP_PORT` | `8080` | The listen port (above 1024: the container is unprivileged). |
| `STATE_DIR` | `/data` | `state.json`, `votes.json` and the maintenance flag; the only path the bridge writes to. |
| `MAPS_FILE` | `/app/maps.json` in the image | The map list; the other data files are read from beside it. |
| `BROKEN_FILE` | (empty) | `broken.json`. Empty or missing: nothing is marked broken. |
| `OFFICIAL_MAPS` | `ar_baggage,ar_pool_day,ar_shoots,ar_shoots_night` | Official maps installed on the server, loaded with `changelevel`. |
| `ADMIN_STEAMIDS` | (empty) | SteamIDs allowed admin actions. |
| `CHANGE_DELAY_SECONDS` | `8` | Seconds between the announcement and the map change. |
| `CHANGE_COOLDOWN_SECONDS` | `60` | Minimum time between two map changes. |
| `RESTART_COOLDOWN_SECONDS` | `300` | Minimum time between two restart requests. |
| `RANDOM_ON_MATCH_END` | `1` | Roll a random map when a match ends and nobody is queued. |
| `RANDOM_ON_START` | `1` | Roll a random map when a new server process has loaded its start map. |
| `START_MAP` | `ar_shoots` | The game server's start map (`CS2_STARTMAP`): a start of it the bridge did not ask for means a restart. |
| `HUD_TAB` | `down` | What TAB does: `down`, `hold`, `window`, `on`, `menu` or `off`. |
| `HUD_CHECK` | `off` | The HUD check: `off`, `dryrun` (warn, and enforce from the first real click on a release) or `enforce`. |
| `GUIDE_ON_JOIN` | `1` | Ask the plugin to show the welcome on every real join. |
| `SITE_URL` | `https://overstrike.example.com` | Your site; the menu, the welcome and the hints show it without the scheme. |
| `TZ` | `UTC` | Local times in notices. |

The game server (`joedwards32/cs2` variables, and these of ours):

| Variable | Meaning |
|---|---|
| `CS2_LOG: "on"`, `CS2_LOG_HTTP_URL` | Push every log line to the bridge (`http://bridge:8080/log/<LOG_SECRET>`); the plugin finds the bridge through it too. |
| `CS2_LOG_ECHO: 1`, `CS2_ADDITIONAL_ARGS: -condebug` | The console in `console.log`, where the restart watcher reads match ends and freezes. |
| `CS2_PLUGINS` | `1` installs and loads the plugin layer; `0` runs vanilla after a game update broke a dependency. |
| `GUNGAME_HUD_ADDON` | The HUD addon's Workshop id; empty: a simple text panel instead of the menu, and MultiAddonManager stays unloaded. |
| `GUNGAME_GRENADE_WIN` | Leave `0`: CS2 does not end an Arms Race match on a last-level grenade kill. |
| `CS2_AUTOUPDATE`, `CS2_RESTART_AT`, `CS2_FREEZE_RECOVERY`, `CS2_FREEZE_GRACE_SECONDS`, `CS2_WARMUP`, `CS2_WARMUP_ITEM`, `CS2_PREFETCH_AT`, `CS2_PREFETCH_MINUTES`, `CS2_PREFETCH_RESTART`, `CS2_BRIDGE_DATA` | The restart watcher (below). |

[`deploy/compose.example.yml`](../deploy/compose.example.yml) sets all of them, with a comment on each.

## The plugin layer

`server/pre.sh` is sourced by the image right before the game launches. With `CS2_PLUGINS=1` it installs the pinned Metamod,
CounterStrikeSharp (with its .NET runtime) and MultiAddonManager once per version, clears an executable-stack request from
their libraries if one appears, re-applies the `gameinfo.gi` search path (every game update rewrites that file), loads
MultiAddonManager only while `GUNGAME_HUD_ADDON` is set, copies every plugin folder in, and starts the restart watcher. Nothing
in it can stop the launch: every failure is a logged warning and a server without that component. With `CS2_PLUGINS=0` it only
removes the search path, and the server runs vanilla - the switch for the days after a game update breaks Metamod or
CounterStrikeSharp. To bump a pin: change the version in `pre.sh`, the same CounterStrikeSharp version in the plugin's
`.csproj`, rebuild the plugin and restart.

The plugin's modules:

- **The HUD host** draws the window and the board from the bridge's live state, sends clicks back, shows the welcome on a real
  join, and runs the **HUD check** (a player whose game has not loaded the HUD is warned, and with enforcement disconnected)
  and the **version check** (the window says when the player's HUD is older or newer than the server's). What a newer HUD content
  added gets its words only for a game that has shown it holds that content: a click on a button only it has, or a chat command
  that opens such a page (`!fun`, `!how`, `!help`), so those pages have their rows at once.
- **Grenade drops** give every player one HE grenade after spawning; the engine drops it where they die.
- **Map settings**: whatever a map changes in the game's settings is put back when it ends; the game-changers no map may set at
  all (no spread, radar off, a mode switch, cheats - `blocked.txt`) are put back within a second.
- **Fun settings**: sixteen settings anybody switches from the menu until the map changes - gravity, super jump, bunny hopping,
  fall damage, ice floor, laser guns, infinite ammo, headshots only, vampire, ghost, big grenades, double damage, friendly fire
  and more. The news line says who switched what.
- **Catch-up**: players who fall behind in Arms Race get a boost.
- **The daily Workshop check** and the item check of the HUD's own Workshop item (the public Web API first; the item's page
  only right after a change, sparsely).

## The restart watcher

| Switch | Job |
|---|---|
| `CS2_AUTOUPDATE` | Every 30 minutes ask Valve whether the installed build is current, and restart a stale one between matches so the image runs SteamCMD again. |
| `CS2_RESTART_AT` | A daily restart at that local time (`off` disables it), only between matches, announced on the HUD. |
| `CS2_FREEZE_RECOVERY`, `CS2_FREEZE_GRACE_SECONDS` | Kill a frozen engine so Docker restarts it: after the error-map signature in `console.log` and silence, or after A2S and RCON both went silent. |
| `CS2_WARMUP`, `CS2_WARMUP_ITEM` | After every engine start, download a throwaway item in the background, so the Steam client's re-check of every installed item runs before a player needs it. |
| `CS2_PREFETCH_AT`, `CS2_PREFETCH_MINUTES`, `CS2_PREFETCH_RESTART` | At night, while nobody is on, install every listed map that is missing and update every one with a newer Workshop copy; optionally in a fresh engine process. |
| `CS2_WORKSHOP_RESTART` | Restart an engine process whose Workshop downloads are stuck (every download ends without a connection). |

Restarts happen between matches; an empty server restarts at once. Loop protection is kept in `update-watch/state.json` on the
game volume: planned restarts at least 10 minutes apart and at most 4 a day, freeze kills at least 3 minutes apart and at most
10 a day, and breakers that stop retrying a restart that does not help.

**Workshop downloads that get stuck.** The Steam client in a server process downloads from a ranked list of content servers;
a server that fails is dropped for the life of the process, and once none is left every download fails. Two things lead
there: a full manifest re-check in a new container (avoided by keeping `~/Steam/depotcache` on a volume - mount it), and
content servers the client ranks first but cannot reach (keep every one reachable). The watcher restarts a process in that state.

## The nightly Workshop sync

An optional container running [`server/workshop-sync.py`](../server/workshop-sync.py) in the game server's image, once a
night inside a window and only while nobody plays:

1. **Find** new maps with the Workshop's tags (Arms Race, Deathmatch, Wingman) through the Steam Web API - the only step that
   needs a Web API key, read from a secret file mounted read-only; without one the step is skipped.
2. **Check** every listed map (gone, hidden, banned, updated) and hand the verdicts to the bridge.
3. **Download** with SteamCMD's anonymous login, one item at a time.
4. **Scan and load-test**: read each download's packages (a map, a model pack, an empty upload, an old CS:GO upload; and any
   commands that advertise another server or post a gambling ad), then load each map in a test copy of the server inside the
   container - its own port, passwords and Workshop folder, no plugins, no log push.
5. **Publish** the passes into the bridge's data (Arms Race onto `maps.json` and `search.json`, the other modes onto their own
   lists, the failures onto `broken.json`), ask the bridge to reload, and send an optional summary mail.

Hard limits in code: one Web API call at a time, at least 2 s apart; at most 10,000 calls in any 24 hours and a nightly cap; any
429, 403 or 401, or a burst of server errors, stops every API step for the night; at most 50 downloads and 3 GB a night. The
configuration can lower each limit, never raise it. Every setting is in the script's docstring.

## Log-line grammar

The parser is one method (`Parse` in `bridge/Program.cs`) and `--selftest` shows exactly what it accepts. The game server
pushes its log with `logaddress_add_http`: a `POST` per tick, one or more lines, each prefixed `MM/DD/YYYY - HH:MM:SS.mmm - `.

- A map start is `Loading map "<name>"`, followed by `Started:` (the name comes from the request's `X-Game-Map` header).
- A clean quit is the echo of an RCON `quit` or `_restart`.
- Player lines: `"<name><uid><[U:1:n]><team>" say "..."`, `connected`, `entered the game`, `disconnected (reason "...")` - a
  reason containing `SHUTDOWN` is a map change's reload, not leaving.
- A match end is the `Game Over:` line, once per match and last in its block; chat cannot fake it.
- A match restart (the same map again) logs only team switches, so it is never mistaken for a map change.
- `X-Server-Instance-Token` is not renewed per process, so restarts are recognised by the `spawn` counter instead.

## Build, test, run

```sh
cd bridge
dotnet build                     # zero warnings expected
dotnet run -- --selftest         # the parser, the lists, the queue, the votes, the dice, the HUD, the rules in sync; exit 1 on failure
docker build -t localhost/overstrike2-bridge .
```

Native AOT does not cross-compile between operating systems, so the image's build stage is the Linux publish. To run the bridge
on Windows against a local server: start the server with `+log on +logaddress_add_http "http://127.0.0.1:8080/log/dev"`,
set `RCON_HOST=127.0.0.1`, `RCON_PASSWORD`, `LOG_SECRET=dev` and `STATE_DIR`, and `dotnet run`. The self-test reads the
plugin's, the HUD's and the site's files from beside `bridge/`; set `OVERSTRIKE2_COMPOSE_FILE` to your compose file to have it
checked as well.

## Files

| Path | What |
|---|---|
| `bridge/*.cs` | The bridge; `SelfTest*.cs` its self-test. `Hud*.cs`, `Guide.cs`, `Fun.cs`, `BlockList.cs` and `Workshop.cs` are compiled into the plugin too. |
| `bridge/maps.json` | The map list: `rank`, `id`, `name`, `sizeMb`. Append-only: a number, once given, keeps its map. |
| `bridge/broken.json` | Crashers, non-maps, removed and blocked maps (above). |
| `bridge/sizes.json` | Each map's measured walking area and size class, for the too-big rule. |
| `bridge/search.json` | The menu's search text per map: title, Workshop id and tags, built by `bridge/search-index.py` from the site's rows. |
| `bridge/ours.json` | The server's own maps. |
| `plugin/OverStrike2/` | The plugin; `blocked*.txt` the block lists, `convars.txt` every game convar of the current build. |
| `hud/` | The HUD addon ([`hud/PUBLISH.md`](../hud/PUBLISH.md)); `thumbs.py` makes the map pictures, which are never committed. |
| `maps/` | Our own maps' generators and their Workshop publisher ([`maps/README.md`](../maps/README.md)). |
| `server/` | The install hook, the mode cfgs, the restart watcher and the Workshop sync, with their tests. |
| `site/` | The site's page (`index.html` and `more.html`, the rows after the first 1,016) and the scripts that build and stamp it. Other authors' descriptions are not kept: each row carries its title, id, tags and size. |
| `deploy/` | Building and shipping from a workstation, with a private config. |
| `tools/scan/` | The scanner the hooks run. |
