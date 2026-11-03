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
| The Workshop sync | [`server/workshop-sync.py`](../server/workshop-sync.py) | An optional third container: finds new maps on the Workshop, downloads, scans and load-tests them, and lists the ones that pass. |

The bridge is .NET 10 with the base class library only, published native AOT into a chiseled image; it runs read-only, with all
capabilities dropped, as a non-root user, and writes only to its state directory. It does not reach the internet: everything
that needs Steam (the Workshop checks, downloads) happens in the game server's container, and the bridge only hears the
verdicts.

## The menu and the commands

The HUD's window is the whole interface, on nine pages: Start, Maps, Queue, Game, Help, Rules, How it works, Fun and Sync. Maps has every map with
its picture, size and votes, a search over names, tags and Workshop ids on an on-screen keyboard, sorting by number, votes, name or size,
and filters by size, by the player's own likes, by official maps, by Our maps and by game mode (`HudBrowser.cs`). A map with no picture in the
addon has a button on its card that shows its Workshop preview on request, in the game's own message panel. Any Workshop map can be played by
its id: its card asks the Workshop about it and downloads it to the server first, with the progress on the card (`HudWorkshop.cs`). Holding
**TAB** shows a board beside the scoreboard - Like, Dislike, Next and the queue - that takes the mouse while TAB is down; TAB + E opens the full
window, which closes after 30 s without a click and the moment its player is hurt.

A chat line that starts with `!` or `/` is a command too, and does exactly what the matching click does (the same action, the
same refusals and confirmations). A line stays visible as the player's normal chat line; only a `/` line is hidden, which is
CounterStrikeSharp's own silent trigger. A pasted Workshop link or id opens that item's card.

One table in the code (`ChatCommands.All`), which `--selftest` holds this one to. `!help` lists the first column only. An alias runs
the action, and its private reply teaches the primary by showing what ran: `You typed !map 12 - ran !play 12`.

| Command | What it does | Aliases |
|---|---|---|
| `!play <number, name or Workshop ID>` | Play a map now, or queue it | `!map` `!p` `!nominate` `!load` `!go` `!pick` |
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
| `!vote <1-6>` | Your choice in the vote at a match's end | `!v` `!mv` `!choose` |
| `!fun` | The Fun page, and each setting's own word | `!set` `!settings` `!toggle` `!mode` |
| `!nades <on\|off>` | Grenade drops on or off | `!grenades` `!nade` `!he` `!drops` `!boom` |
| `!time <10\|60>` | Match length in minutes, from the next match | `!length` `!minutes` `!timelimit` `!roundtime` `!duration` |
| `!newmaps <always\|half\|never>` | New maps in random picks | `!fresh` `!pool` `!mix` `!newmap`, and `!new` with an argument |
| `!restart` | Restart a stuck server - twice to confirm | `!reboot` `!fix` `!unstuck` `!stuck` `!reset` |
| `!menu` | Open the full menu | `!gg` `!hud` `!os` `!os2` `!open` |
| `!help` | The Help page, and this list | `!commands` `!cmds` `!h` `!?` `!info` |
| `!rules` | The rules | `!rule` `!r` `!fair` `!howto` `!policy` |
| `!how` | How it works: every chance in map picks | `!howitworks` `!odds` `!chances` `!why` `!explain` |

Two words depend on what follows them, by rule, and nothing else is two things: `!new` with an argument is `!newmaps`, without one
`!next`; `!maps` (and its aliases) with words is `!find`.

While a vote is open only the vote picks a map: `!vote`, its aliases and the bare number vote, and `!play`, `!next`, `!random`, `!back`,
`!done` and `!skip`, each with all its aliases, do nothing but answer that player with what to type: "Vote now: type !vote 1-6 or just the number"
and "Requests reopen after the vote." A `!vote` with a number that is not on the ballot gets the same answer, and the
same clicks in the menu a note saying so. The commands that do not touch the map stay as they are, `!restart` among them: a stuck server
does not wait for a vote.

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

A player votes by clicking a card's **Vote**, or in chat with its number (`3`, or `!vote 3`). The most votes win, a tie is drawn at random, and no
votes at all is Random. The winner plays; a queued map that loses stays queued, and a rolled or Random winner is held by the vote until its match
ends or a skip vote passes. With no plugin polling that draws the vote, or with `MATCH_END_VOTE=off`, a match end serves the queue or rolls the dice.

**The game modes.** Arms Race is the default: after every server restart, and again after 10 minutes with nobody on. Each mode has Valve's official
maps for it (`bridge/Modes.cs`) and the Workshop maps of its list: `maps.json` for Arms Race, `maps-deathmatch.json` and `maps-wingman.json` for the
others, read like every data file. A map loads in its own mode: the bridge tells the plugin with `os_mode armsrace|deathmatch|wingman` and sets the
mode's convars right before the map command. A map several modes list plays, from its card, in the mode the Maps page's filter shows it in, else in the
mode the server is in when that mode lists it, else in the first mode that does. Each mode has its own server cfg (`server/gamemode_*_server.cfg`) and block list
(`plugin/OverStrike2/blocked*.txt`). Grenade drops, the match length, the catch-up boosts and friendly fire's settings are Arms Race's own, and the
menu leaves them out elsewhere.

## Random picks

The dice roll at a server start (`RANDOM_ON_START`), at a match end with nobody queued (`RANDOM_ON_MATCH_END`) and on **Next**.
Without the bridge a finished match replays the same map: the Arms Race cfg sets `mp_match_end_restart 1` and
`mp_match_end_changelevel 0`, and the bridge acts on the log's `Game Over:` line.

**Rolled ahead (since 2026-11-03).** The bridge keeps four random queues of `RANDOM_QUEUE` (15) in `state.json` - Arms Race's
newer maps, its original list, Deathmatch, Wingman - each rolled one entry at a time with exactly the rules below on its own list,
never a map twice in one queue. A random pick takes from its mode's queue (the new-maps setting picks Arms Race's: always the
newer maps' queue, never the original's, half a coin between them): the first entry that is still eligible and that the restart
watcher reports on the server's disk (`POST /ready/<LOG_SECRET>`, one id a line after `gg-ready	1`; an official map always is,
and without a report for 5 minutes every entry counts), else the first eligible entry, else - nothing eligible in the queue - a
roll on the spot. An entry no longer eligible, played since its roll or with its too-big mark changed is dropped; one whose
votes went down is kept with the odds the vote left it. What a pick takes leaves the queue and is rolled anew at once. The vote's
rolled options and its Random come from the queues the same way, shown or chosen alike. `/data/queues.json` (version 1: the four
queues' keys in play order and the requested Workshop ids) is what the watcher downloads in the background and what the server
keeps.

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

**Like** and **Dislike** vote on the map that is on, the arrows on any map's card: one vote per player per map, and no way back to no vote.
The same vote again changes nothing and says it is up or down already; the opposite vote replaces it. **Too big** is a dislike that also marks the map too big (1 random pick in 100); **Ads** is a dislike that
keeps the map out of random picks for good (it shows adverts or sends players to another server). Votes live in
`STATE_DIR/votes.json`; a file that does not parse is moved aside, never overwritten.

**A dislike keeps its reason**, as the vote's value in `votes.json` (`DownReasons`). Any plain Dislike - the board's button, a card's arrow,
`!down`, `!down <number>`, its aliases - on a map that player has not disliked counts at once as the first one, and that player gets a popup listing them for 15 s:

| Word | Reason | Value | What it does |
|---|---|---|---|
| `meh` | I did not like the map | -1 | A dislike |
| `big` | Map too big | -2 | As **Too big** |
| `ads` | Shows ads or sends players away | -3 | As **Ads** |
| `small` | Map too small | -4 | A dislike |
| `broken` | Map is broken | -5 | A dislike |
| `slow` | Runs badly | -6 | A dislike |

Choosing a reason updates that vote. With no choice the vote stays as it is; the plain dislike is what is kept and logged. The popup
takes no mouse of its own, so it never stops anybody playing: it stands at the left, clear of the window and the scoreboard, and a click
needs the cursor the player already has - the window's, or TAB's. A map change, or another vote by that player on that map, closes it.

Chat steers it directly: `!down big` on the map that is on, `!down <number> <reason>` on any map (`!down 12 big`), a new vote with its
reason if there was none, and no popup. While a popup is open `!down big` sets the reason of the vote it asks about, without a second
vote. A reason replaces the one before; the same one again changes nothing and says so. A plain `!down` on a map already disliked keeps
the reason, says it is down already and leaves a popup that is open running; `!down meh` puts a reason back to the plain dislike. The
health page counts the votes per reason. A game holding an older HUD has no popup: the player's note names the words instead, and every player's does.

### Keeping the rules in sync

The rules are said in the menu: its How it works, Help and Rules pages and the start page. The bridge's `--selftest` holds them to
the code: every number those words state must be one of the code's constants, so a rule change fails the selftest until the code
and the menu's words (`bridge/Hud.cs`) agree again.

## Maintenance flag

While `STATE_DIR/maintenance` exists, the bridge makes no automatic map change: no start roll, no match-end roll, no queue
advance. Requests still queue. Remove the file and everything resumes.

## Crash handling and the map lists

- **Crashes.** A few seconds after every map start the bridge reads RCON `status`; its `spawn` counter tells whether the server
  process is new. A new process that did not quit cleanly is a crash, counted against the map it was loading. A map with a crash
  asks the player to confirm a pick, and the dice skip it.
- **Maps that crash the players' games.** Three players timing out within 60 s of entering a map, while the server keeps answering, mark it broken by
  itself, in `state.json` with its reason; the dice then take the server to another map.
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
| `MATCH_END_VOTE` | `auto` | The end-of-match vote: `auto` while a plugin that draws it is polling, `on` always, `off` never. |
| `START_MAP` | `ar_shoots` | The game server's start map (`CS2_STARTMAP`): a start of it the bridge did not ask for means a restart. |
| `HUD_TAB` | `down` | What TAB does: `down`, `hold`, `window`, `on`, `menu` or `off`. |
| `HUD_CHECK` | `off` | The HUD check: `off`, `dryrun` (warn, and enforce from the first real click on a release) or `enforce`. |
| `GUIDE_ON_JOIN` | `1` | Ask the plugin to show the welcome on every real join. |
| `TZ` | `UTC` | Local times in notices. |

The game server (`joedwards32/cs2` variables, and these of ours):

| Variable | Meaning |
|---|---|
| `CS2_LOG: "on"`, `CS2_LOG_HTTP_URL` | Push every log line to the bridge (`http://bridge:8080/log/<LOG_SECRET>`); the plugin finds the bridge through it too. |
| `CS2_LOG_ECHO: 1`, `CS2_ADDITIONAL_ARGS: -condebug` | The console in `console.log`, where the restart watcher reads match ends and freezes. |
| `CS2_PLUGINS` | `1` installs and loads the plugin layer; `0` runs vanilla after a game update broke a dependency. |
| `GUNGAME_HUD_ADDON` | The HUD addon's Workshop id; empty: a simple text panel instead of the menu, and MultiAddonManager stays unloaded. |
| `GUNGAME_GRENADE_WIN` | Leave `0`: CS2 does not end an Arms Race match on a last-level grenade kill. |
| `CS2_AUTOUPDATE`, `CS2_RESTART_AT`, `CS2_FREEZE_RECOVERY`, `CS2_FREEZE_GRACE_SECONDS`, `CS2_WARMUP`, `CS2_WARMUP_ITEM`, `CS2_PREFETCH_AT`, `CS2_PREFETCH_MINUTES`, `CS2_PREFETCH_RESTART`, `CS2_WORKSHOP_RESTART`, `CS2_DOWNLOAD_LINE`, `CS2_MAPS_LAYOUT`, `CS2_BRIDGE_DATA` | The restart watcher (below). |

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
- **Catch-up** (`plugin/CatchUp/`, its numbers in `catchup.json`): in Arms Race a player 2, 4 or 7 kills behind the leader gets a boost at spawn, with
  a private message that says what it is. Health +20, +40 or +75%, damage taken -10, -20 or -30%, damage dealt +10, +20 or +30%, lifesteal 10, 20 or 30%,
  regeneration 2, 4 or 6% a second after a few seconds unhurt; from 4 behind a second chance once per life and more spawn protection, at 7 behind infinite ammo
  and a one-hit knife and Zeus. A leader 2 kills or more ahead of second place takes 10% more damage per 2 kills of lead, up to 30%. A cap keeps a boosted
  player from coming out stronger than the leader on paper: never above 3 times, and never above the scoreboard's own ratio. The module counts the ladder from the
  match's kills, since the game exposes no ladder position.
- **The daily Workshop check** and the item check of the HUD's own Workshop item (the public Web API first; the item's page
  only right after a change, sparsely).

## The restart watcher

| Switch | Job |
|---|---|
| `CS2_AUTOUPDATE` | Every 30 minutes ask Valve whether the installed build is current, and restart a stale one between matches so the image runs SteamCMD again. |
| `CS2_RESTART_AT` | A daily restart at that local time (`off` disables it), only between matches, announced on the HUD. |
| `CS2_FREEZE_RECOVERY`, `CS2_FREEZE_GRACE_SECONDS` | Kill a frozen engine so Docker restarts it: after the error-map signature in `console.log` and silence, or after A2S and RCON both went silent. |
| `CS2_WARMUP`, `CS2_WARMUP_ITEM` | After every engine start, download a throwaway item in the background, so the Steam client's re-check of every installed item runs before a player needs it. |
| `CS2_PREFETCH_AT`, `CS2_PREFETCH_MINUTES`, `CS2_PREFETCH_RESTART` | At night, while nobody is on, install the kept maps and the queued ones that are missing and update the ones with a newer Workshop copy; optionally in a fresh engine process. |
| `CS2_READY` | Download the bridge's random queues and the queued requests in the background, one at a time and silently, also while people play, and tell the bridge which are on disk. |
| `CS2_MAPS_DELETE` | `on` deletes, at every engine start, every stored map outside the keep set (below); `dryrun` only writes the list; `off` keeps everything. |
| `CS2_WORKSHOP_RESTART` | Restart an engine process whose Workshop downloads are stuck (every download ends without a connection). |
| `CS2_DOWNLOAD_LINE` | While the server downloads a map somebody is waiting for, say so on the HUD's news line, with the progress. Never for the watcher's own downloads, never with nobody on. |
| `CS2_MAPS_LAYOUT` | `folders` (the default) keeps the maps in order before every engine start, see below; `off` leaves them where Steam puts them. |

Restarts happen between matches; an empty server restarts at once. Loop protection is kept in `update-watch/state.json` on the
game volume: planned restarts at least 10 minutes apart and at most 4 a day, freeze kills at least 3 minutes apart and at most
10 a day, and breakers that stop retrying a restart that does not help.

**Workshop downloads that get stuck.** The Steam client in a server process downloads from a ranked list of content servers;
a server that fails is dropped for the life of the process, and once none is left every download fails. Two things lead
there: a full manifest re-check in a new container (avoided by keeping `~/Steam/depotcache` on a volume - mount it), and
content servers the client ranks first but cannot reach (keep every one reachable). The watcher restarts a process in that state.

**The maps folder.** Before every engine start `update-watch.py --organize` moves each Workshop map to a real folder per type on the
game volume, `maps/<type>/<id>-<name>/`: `armsrace`, `deathmatch`, `wingman`, `own`, `hud` and `other`, by the bridge's lists, with
`maps/INDEX.tsv` listing them all. Steam's `content/730/<id>` becomes a relative link to that folder. Steam loads, updates and
re-checks a map through the link, and a new download lands as a real folder where Steam expects it, which the next start moves.
Tested on a side-by-side engine: loads, genuine updates, a fresh download onto a link and the re-check with a full and an empty
depotcache. If the bridge's lists cannot be read, or a move fails, the engine starts anyway.

**What the server keeps** (the keep set, `keep_set` in both server scripts): every map with at least one up vote; per mode three
lists of 100 playable maps together, so up to 300 - the best Workshop score (`vote_data.score`, the Workshop's "Top Rated"), the
most current subscriptions ("Most Subscribed") and Steam's trend over the last 30 days ("Most Popular": `QueryFiles` RankedByTrend
with `days=30`), all from `ratings.json`, which the sync fetches daily; the own maps and the HUD item, the random queues and the
requested maps. The same run takes in the maps the sync hands over (`workshop-sync/handover/<id>/`, its
manifest, and last `<id>.json` with its acf entries and what the folder holds: only a complete one) - folder, link, acf entries,
manifest, so Steam's client holds it as its own download - and then, with `CS2_MAPS_DELETE=on`, deletes every stored map outside
the keep set: its acf entries first (backed up), then its link and folder. Never the own maps, the HUD, the warm-up item or a
queued map; refused, with the reason logged, when an input is missing, the scores are over 3 days old or cover under 90% of a
list, any of a mode's three lists is under 50 maps or a queue under 10. Each run writes `update-watch/keep.json` and its list,
`update-watch/deleted-<time>-<done, dryrun or refused>.tsv`.

## The Workshop sync

An optional container running [`server/workshop-sync.py`](../server/workshop-sync.py) in the game server's image. It has no time windows: it
starts a run whenever the server has had nobody on for a quiet spell and there is work, a run lasts at most `SYNC_RUN_HOURS` (3), and no run
starts or lasts into the restart watcher's nightly prefetch (`SYNC_PREFETCH_AT`, until the watcher stamps it done) or its daily restart
(`SYNC_DAILY_RESTART`). Discovery, the check and the ratings run at most every `SYNC_API_EVERY_HOURS` (4). Downloads, tests and publishing
repeat in batches, so a long run publishes as it goes:

1. **Find** new maps with the Workshop's tags (Arms Race, Deathmatch, Wingman) through the Steam Web API - the only step that
   needs a Web API key, read from a secret file mounted read-only; without one the step is skipped.
2. **Check** every listed map (gone, hidden, banned, updated) and hand the verdicts to the bridge; once a day also fetch the
   Workshop's score and subscriptions of every listed map and every waiting one (`IPublishedFileService/GetDetails` with
   `includevotes`, the key) and each mode's Most Popular order (`QueryFiles` RankedByTrend, 30 days) into the bridge's
   `ratings.json`. Then download the maps the server keeps but lacks and hand them over (`<work>/handover/`): each waits for
   SteamCMD's acf entry of its download, and a map already staged with one is handed over without a new download. Before
   every SteamCMD session the staging acf forgets every entry without a folder, because SteamCMD downloads such a map again by
   itself at its next login.
3. **Download** with SteamCMD's anonymous login, one item at a time, through everything that is queued: the maps whose score
   would make one of their mode's three lists first, then new uploads, listed maps that changed, and everything else strictly
   by subscribers, whatever the mode. Nothing is skipped for its size. A tested map the server keeps is handed over instead of
   deleted.
4. **Scan and load-test**: read each download's packages (a map, a model pack, an empty upload, an old CS:GO upload; and any
   commands that advertise another server or post a gambling ad), then load each map in a test copy of the server inside the
   container - its own port, passwords and Workshop folder, no plugins, no log push.
5. **Publish** the passes into the bridge's data (Arms Race onto `maps.json` and `search.json`, the other modes onto their own
   lists, the failures onto `broken.json`), and ask the bridge to reload. The optional mail is for problems only: a run
   stopped early or a step that errored, Steam pushing back, the test engine failing to start, a safety check refusing,
   the disk running low (`SYNC_DISK_LOW_GB`), a failed hand-over, any other error. Each says what went wrong and what the
   sync did about it, and points to `SYNC_DASHBOARD_URL`; a problem that persists is mailed once a day at most. A run
   without one mails nothing; its report stays in the state folder's `reports/` and in `sync-status.json`.

Hard limits in code: one Web API call at a time, at least 2 s apart; at most 10,000 calls in any 24 hours and 500 a run; any
429, 403 or 401, or a burst of server errors, stops every API step for the rest of the day. The configuration can lower each
limit, never raise it. There is no daily download cap: an HTTP 429, 403 or 401, a SteamCMD rate limit or two Access Denied in a
row end the day's downloads, three failed downloads in a row end the run's, and a bug brake downloads a map at most twice a day.
One SteamCMD download at a time, one test engine. When a player joins, the test engine stops at once and no new download starts;
the run goes on only after a quiet spell with nobody on. Every setting is in the script's docstring.

The sync also writes `sync-status.json` into the bridge's data folder (schema 1, `Status` in the script) for the in-game status
page: what it does now and why (`now.state` is one of idle, waiting, discovery, check, ratings, serve, download, test, publish,
stopped), today's counts, the totals, the queue with its next maps in the order they are taken, the keep set's parts, and the
history of runs and map events. It is written atomically at every phase change, at least every 15 s during a run and every minute
otherwise; fields may be added to it, never renamed or dropped.

**The dashboard** is that file for everybody (`bridge/SyncDash.cs`). The bridge reads it whenever it changed (it looks every 2 s), adds the
random queues and how many of their maps the restart watcher reports on the server's disk, and makes two things of it. The menu's **Sync**
page: what the sync does now (its state, why, the map, the step on a bar, the run, today's counts, the totals and the file's age), its queue
(the counts per mode, the finish estimate, what the server itself still lacks, the random queues, the next maps six a page) and its history
(the runs three a page and the map events eight a page, newest first). The bridge writes every word of it and serves the block on
`GET /dash/<secret>`, its version in the live state; the plugin pages each list per player and passes the rest through as dialog
variables and classes on `gg_screen` (`bridge/HudDash.cs`), so a change to the page's words or rows needs a bridge alone. And a website:
`index.html` and `dashboard.json` in `/data/web`, rewritten when they change and every minute, for a static web server behind a login (the
page reads the JSON every 5 s and searches the whole history) - the bridge's own HTTP port is never reachable from outside. A file that
is missing, does not parse or is older than 15 minutes is said as such, and the last good one stays on screen meanwhile.

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
dotnet run -- --selftest         # the parser, the lists, the queue, the votes, the dice, the HUD, the rules; exit 1 on failure
docker build -t localhost/overstrike2-bridge .
```

Native AOT does not cross-compile between operating systems, so the image's build stage is the Linux publish. To run the bridge
on Windows against a local server: start the server with `+log on +logaddress_add_http "http://127.0.0.1:8080/log/dev"`,
set `RCON_HOST=127.0.0.1`, `RCON_PASSWORD`, `LOG_SECRET=dev` and `STATE_DIR`, and `dotnet run`. The self-test reads the
plugin's and the HUD's files from beside `bridge/`; set `OVERSTRIKE2_COMPOSE_FILE` to your compose file to have it
checked as well.

## Files

| Path | What |
|---|---|
| `bridge/*.cs` | The bridge; `SelfTest*.cs` its self-test. `Hud*.cs`, `Guide.cs`, `Fun.cs`, `BlockList.cs` and `Workshop.cs` are compiled into the plugin too. |
| `bridge/maps.json` | The map list: `rank`, `id`, `name`, `sizeMb`. Append-only: a number, once given, keeps its map. |
| `bridge/broken.json` | Crashers, non-maps, removed and blocked maps (above). |
| `bridge/sizes.json` | Each map's measured walking area and size class, for the too-big rule. |
| `bridge/search.json` | The menu's search text per map: title, Workshop id and tags; `bridge/search-index.py` keeps it in step with `maps.json`. |
| `bridge/ours.json` | The server's own maps. |
| `plugin/OverStrike2/` | The plugin; `blocked*.txt` the block lists, `convars.txt` the floor of game convars the plugin watches; at every start it adds what the running build has beyond it (`ConVarNames.cs`). |
| `plugin/CatchUp/` | The catch-up boosts and their self-test; the plugin compiles them in. |
| `hud/` | The HUD addon ([`hud/PUBLISH.md`](../hud/PUBLISH.md)); `thumbs.py` makes the map pictures, which are never committed. |
| `maps/` | Our own maps' generators and their Workshop publisher ([`maps/README.md`](../maps/README.md)). |
| `server/` | The install hook, the mode cfgs, the restart watcher and the Workshop sync, with their tests. |
| `deploy/` | Building and shipping from a workstation, with a private config. |
| `tools/scan/` | The scanner the hooks run. |
