# Over Strike 2 - reference

How the server works: every part, rule and number, each in one place. The other pages have their own topics: [install.md](install.md)
runs your own, [settings.md](settings.md) has every setting, [server.md](server.md) the server programs' commands, restarts and wire
formats, [deploy/README.md](../deploy/README.md) shipping from a workstation, and [hud/PUBLISH.md](../hud/PUBLISH.md) building and
publishing the HUD. The project's front page is the [README](../README.md). The code is the final word, and the bridge's `--selftest`
holds most of what is written here to it.

## The parts

| Part | Where | What it does |
|---|---|---|
| The game server | the `joedwards32/cs2` image | A Counter-Strike 2 dedicated server. SteamCMD updates the game at every container start, and the image sources the launcher `pre.sh` right before the engine starts. |
| The server program | [`server/OverStrike2.Server/`](../server/OverStrike2.Server/) | `os2-server`, beside the engine in the game server's container. Before every engine start it installs the plugin layer, delivers the HUD addon, and keeps the maps folder; then it is the restart watcher. See [server.md](server.md). |
| The plugin | [`plugin/OverStrike2/`](../plugin/OverStrike2/) | One CounterStrikeSharp plugin inside the engine: the HUD on screen, the chat commands, the vote's freeze, the HUD check, grenade drops, map settings put back, the game-changers block, the fun settings and their effects, the cloak, catch-up ([`plugin/CatchUp/`](../plugin/CatchUp/)), the download of a map that is not listed, and the watch on the HUD's Workshop item. |
| The bridge | [`bridge/`](../bridge/) | A small sidecar that decides. The game server pushes its log lines to it over HTTP; it runs the vote, the dice and the discovery stock, keeps the likes, counts crashes, knows the lists and the game modes, writes nearly every word on the HUD, and sends map changes and notices back over RCON. |
| The HUD | [`hud/`](../hud/) | A Panorama layout and stylesheet, with every map's picture, published as one Steam Workshop item. The server hands it to every player who joins. |
| The Workshop sync | [`server/OverStrike2.Sync/`](../server/OverStrike2.Sync/) | `os2-sync`, optional, in a container of its own: finds new maps on the Workshop, downloads, scans and load-tests them, lists the ones that pass, and hands the server what it keeps but lacks. See [server.md](server.md#the-workshop-sync-os2-sync). |
| The core library | [`core/`](../core/) | `OverStrike2.Core`, what the parts share: the settings file, the keep set, the game server query, Steam's file formats. |

The bridge is .NET 10 with the base class library and `core/` only, published native AOT into a chiseled image; it runs read-only, with
all capabilities dropped, as a non-root user, and writes only to its data folder (`paths.bridgeData`). It never reaches the internet:
everything that needs Steam happens in the game server's container or the sync, and the bridge only hears the verdicts. So it outlives
engine restarts and crashes, counts crashes from outside, and swaps in seconds without disconnecting anybody. Much of the plugin's logic
is source shared with the bridge, so the bridge's self-test covers it.

How they talk (`<secret>` is `bridge.secret`):

| Channel | From, to | What it carries |
|---|---|---|
| Log feed (`logaddress_add_http`) | engine to bridge, `POST /log/<secret>` | Every log line: joins, chat, map starts, match ends, quits |
| RCON | bridge to engine | Map changes, mode settings, plugin commands, `status` |
| RCON | watcher to its own engine | Quit, players, a liveness echo, background downloads |
| HTTP | plugin to bridge | `GET /hud/<secret>`, the live state, a long poll with versions; `/catalog/`, `/search/` and `/dash/` when their versions move; `POST /act/<secret>`, a click |
| HTTP | watcher to bridge | `POST /ready/`, `/quitting/`, `/restart-notice/`, `/updated/` ([server.md](server.md#the-bridges-side)) |
| HTTP | sync to bridge | `POST /reload/`: read the lists now; `POST /removed/`: the Workshop's verdicts |
| HTTP | deploy tools to bridge | `GET /health/<secret>` |
| The bridge's data folder | bridge, sync, watcher | [The data files](#the-data-files) |
| Game volume | sync to watcher | `workshop-sync/handover/<id>`, taken in at the next engine start |
| Console log (`-condebug`) | engine to watcher | Match ends, freezes, download progress |
| A2S (UDP) | watcher, sync, deploy tools to engine | Is anybody on |
| Steam | engine, watcher, sync, plugin | The game and map downloads; the Web API, one request at a time, at least 2 s apart, per program |
| SMTP | sync to a mail relay | Problem mails |

## In-game HUD

The [README's index](../README.md#the-hud-page-by-page) is the way to every part's page and pictures.

**The board.** Holding **TAB** shows a board beside the scoreboard: the map that is on and who picked it, Like and Dislike, the vote in
one line, **Start a vote** and the way into the menu. It takes the mouse while TAB is down; letting go, a hit, death or the window opening
gives it back. `hud.tab` `off` leaves TAB alone (`HudTab.cs`).

**The window.** **TAB + E** opens the full menu, on five tabs. It closes after 30 s without a click, and damage never closes it. A vote
opens it for everybody and holds it open ([the freeze](#the-freeze)).

| Tab | Page | What is on it |
|---|---|---|
| START | | The welcome, three steps and how to reach the menu. |
| MAPS | Browse | The map browser: eight cards a page, each with the map's picture, its place on its list, size, likes and tags, its Play button and like arrows, and the mode it plays in, as a label on its picture and the card's colour (Arms Race gold, Deathmatch red, Wingman blue). Full-text search on an on-screen keyboard over names, tags and Workshop ids; sorting by place on the list, likes, name or size; filters by size (under 25 MB, 25-120, 120-400, 400 and up), by the player's own likes, by official maps, by Our maps and by game mode. Previous puts the map before this one to the vote. |
| MAPS | Vote | While a vote is on, the ballot ([below](#the-ballot)); Browse then adds to the vote. |
| FUN | | The [fun settings](#fun-settings), with Random and Default. |
| SERVER | Players | Who is on, their team, the HUD their game showed, the HUD check's clock, their vote while one is open, and how long they have been on. |
| SERVER | Game | The match length, 10 or 60 minutes (Arms Race's own), and who set it; Restart for a stuck server, twice to confirm. |
| SERVER | Sync | The [Workshop sync's dashboard](#the-dashboard). |
| HELP | Help | Where everything is. |
| HELP | How it works | Every rule of the vote and every chance of a random pick: the one set of rule texts in the game ([Keeping the rules in sync](#keeping-the-rules-in-sync)). |

**A map that is not listed.** Any Workshop map can be played by its id, typed on the keyboard or pasted in chat: its card asks the
Workshop about it (the plugin, through the public Web API), and **Download and play** fetches it into the server's own Workshop cache,
with the progress on the card, while nobody's game is interrupted (`HudWorkshop.cs`, `WorkshopPlay.cs`). Once it is installed it is
selected like any map: to the vote.

**The pictures** are inside the addon, because CS2 refuses web pictures in a custom HUD: each map's Workshop preview as a small JPEG, an
official map's the game's own screenshot. A map listed after the release that shipped its picture shows the card's plain backdrop. Each map
keeps its picture's number for good (`HudThumbList.cs`), so an older HUD never shows a wrong picture.

**The spawn cloak.** A player whose window is open while they are on their spawn is hidden from everybody else, in every game mode, for as
long as it stays open (`CloakPlan.cs`): the model, every weapon they carry, wearables and shadows. Moving more than 64 units sideways or 72
up or down, attacking, or a vote's freeze starting or ending spends it until the next spawn; closing the window only ends it until the
window opens again. A player killed with the window open is hidden again at the respawn. What is no model - the radar's dot, the name when
aimed at, sounds, muzzle flashes - still shows.

**Messages.** What happens for everybody shows on screen for everybody: who changed what, a map change counting down, a restart coming, a
download somebody waits for, a crash. What answers one player's click goes to that player alone. A now-playing card greets each map at the
first spawn, and a joining player is welcomed. Nearly every word comes from the bridge and the plugin as dialog variables, so most changes
need no new HUD (`Hud.cs`).

**Tags.** Every card says why it is there: every tag that applies, in this order, everywhere a map appears (`MapTags`, one table for the
cards, the lines on screen and in chat, the website and the Sync page; a line carries the first "why" tag, in words).

| Family | Tag | When |
|---|---|---|
| The vote | NEXT MAP, LEADING, TIE, ON NOW | The vote chose it; the most votes now; tied for the most; the map that is on |
| Caution | TOO BIG, ADS | Measured XL or marked by a player; marked by a player |
| A player | PICKED | A player put it on this ballot |
| Us | OUR MAP, LIKED BY US | `ours.json`; at least one like, and more likes than dislikes |
| Fresh | NEW UPLOAD, UPDATED | Uploaded in the last 30 days; its content changed in the last 30 days and that content passed the sync's test |
| The Workshop | TRENDING, TOP RATED, POPULAR | In its mode's 30-day Most Popular, Top Rated or Most Subscribed top 100 |
| Discovery | NEW TO US | Never shown or played here |

The mode keeps the card's colour and label, and a map's record - crashed, broken with its reason, removed from the Workshop - is a strip
across its picture, in the map browser and on the ballot.

## The menu and the commands

A chat line that starts with `!` or `/` does exactly what the matching click does (the same action, the same refusals and
confirmations). A line stays visible as the player's normal chat line; only a `/` line is hidden, which is CounterStrikeSharp's own silent
trigger. A pasted Workshop link or id opens that item's card.

One table in the code (`ChatCommands.All`), which `--selftest` holds this one to. `!help` lists the first column only. An alias runs the
action, and its private reply teaches the primary by showing what ran: `You typed !map 12 - Ran !play 12`.

Chat's colours help reading, and each means one thing: a command is Lime, and only what can be typed is one; an argument, what a command or a setting is about, is
LightBlue; a value is Gold; a player's name has their team's colour; other words are the chat's own colour. A line that refuses is LightRed around those colours, a line that says
it worked Green. For example `You typed !fun gravity low - Ran !gravity low` shows all three kinds of word apart, and `Unknown command !foo - !help lists them` is a refusal
in which only `!help` is Lime. Every section of a line starts with a capital, and a line holds at most about 120 characters.

| Command | What it does | Aliases |
|---|---|---|
| `!play <number, name or Workshop ID>` | Put a map to the vote, with your vote on it; while a vote is open, onto that ballot | `!map` `!p` `!nominate` `!load` `!go` `!pick` |
| `!find <words>` | Search the maps: the menu opens on the matches | `!search` `!s` `!lookup`, and `!maps <words>` |
| `!maps` | Open the map browser | `!browse` `!list` `!maplist` `!m` `!top` (sorted by likes) |
| `!next` | Start a vote for the next map, Keep playing on it; while a vote is open, back to it | `!n` `!rtv` `!change` `!skipmap` `!skip` `!voteskip` `!vs` `!boo` `!done` `!finish` `!end` `!release` `!letgo` `!over` `!new` |
| `!random` | The same vote, with your vote on Random; while a vote is open, your vote on Random | `!roll` `!dice` `!rand` `!any` `!shuffle` |
| `!back` | The map before this one, to the vote | `!previous` `!prev` `!last` `!undo` `!again` |
| `!up [number]` | Like the map that is on, or that map | `!like` `!+` `!good` `!love` `!thumbsup` |
| `!down [number] [reason]` | Dislike it, and why ([Likes and dislikes](#likes-and-dislikes)) | `!dislike` `!-` `!bad` `!meh` `!thumbsdown` |
| `!vote` | Back to the vote for the next map: a vote is a click on its card | `!v` `!mv` `!choose` |
| `!fun` | The Fun page, and each setting's own word; `!fun random` and `!fun default` | `!set` `!settings` `!toggle` `!mode` `!randomize` `!funrandom` `!defaults` `!fundefault` |
| `!nades <on\|off>` | Grenade drops on or off (`!drops on\|off` is the setting's own word) | `!grenades` `!nade` `!he` `!boom` |
| `!catchup <on\|off>` | Catch-up boosts on or off, until the map changes | `!catch` `!boost` `!boosts` `!comeback` |
| `!time <10\|60>` | Match length in minutes | `!length` `!minutes` `!timelimit` `!roundtime` `!duration` |
| `!restart` | Restart a stuck server - Twice to confirm | `!reboot` `!fix` `!unstuck` `!stuck` `!reset` |
| `!menu` | Open the full menu | `!gg` `!hud` `!os` `!os2` `!open` |
| `!help` | The Help page, and this list | `!commands` `!cmds` `!h` `!?` `!info` |
| `!how` | How it works: every rule of the vote and every chance of a random pick | `!howitworks` `!odds` `!chances` `!why` `!explain` `!rules` `!rule` `!r` `!fair` `!howto` `!policy` |
| `!sync` | The Sync page: what the map sync does now, its queue and its history | `!status` `!dashboard` |

One word depends on what follows it, by rule: `!maps` (and its aliases) with words is `!find`; and `!nades` is two things, `on` and `off`
the grenade drops, `normal`, `big` and `huge` the blast of the grenades. The words players know from other servers are among the aliases:
`!skip` and `!rtv` start the vote, and `!done` and `!new`, whose old meanings are gone, do the same and teach it. `!randomize` and
`!funrandom` run `!fun random`, `!defaults` and `!fundefault` run `!fun default`, and teach them as every alias teaches its primary.

While a vote is open `!play` - like a card's Play in the map browser - puts its map on the ballot for everybody, with that player's vote, and
`!back` does the same with the map before this one; `!random` moves that player's vote to Random; `!next` and a bare `!vote` bring its window
back. The cards have no numbers (`MapVote.Numbers`): a `!vote` with a number answers that a vote is a click on a card, and a bare number stays
plain chat. The commands that do not touch the map stay as they are, `!restart` among them: a stuck server does not wait for a vote.

**Each fun setting is a word of its own**, with the setting's id and its choices: `!gravity low`, `!vampire on`, `!bhop turbo`. A bare `!fun`
opens the Fun page and writes the words in chat, for the settings the page shows in the game mode that is on. `!fun gravity low` still works
and teaches the short word.

The actions the menu sends the bridge (`/act/`) are a closed list (`Hud.IsAction`, `MapVote.IsAction`): nothing typed or clicked reaches the
server console.

## The vote and the game modes

**Putting a map to the vote is how a map is chosen**; there is no other way but the dice's few [automatic changes](#the-automatic-changes).

### How a vote opens

- **A player selects a map:** Play on a card, `!play`, Previous or `!back`, or a Workshop card's Download and play. The vote opens with that
  map on the ballot and that player's vote on it; while a vote is open, the map goes onto that ballot instead. The map that is on is not
  selected: it is Keep, on every ballot.
- **A player starts one without a map:** **Start a vote** on the board or in the window, or `!next`; `!random` does it with that player's vote
  on Random.
- **A match ends:** the vote opens by itself. A vote that is open when a match ends runs on as a match end's. No vote opens with nobody on,
  while a change counts down or a map loads, when a restart waits for that match's end, or under the [maintenance flag](#maintenance-flag).

Nobody can call a vote while a map change counts down or a map loads, while a restart counts down, or for **2 minutes after the last vote**; a
match end opens its vote anyway. A risky map - a crash on record, on the broken list - asks that player for a second click within 30 s first.
With more than four on a Wingman map cannot win, and one asked for from another mode is refused.

### The freeze

Every vote opens everybody's window on the ballot and freezes everybody: nobody moves, shoots, takes damage or closes the window, bots
included (`VoteFreeze.cs`). A player who joins meanwhile is frozen and counted. Everybody unfreezes at the same moment: when the vote keeps
the map, at once; when it changes the map, after the countdown (`bridge.changeDelaySeconds`, 8 s), everybody frozen until the map changes. The
plugin frees everybody by itself if the bridge stops answering for 40 s, on a new map, and after 5 minutes of one freeze, so a lost bridge
never leaves anybody frozen.

### Voting

A vote is a click on a card, changed or taken back as often as the player likes: a click on another card moves it, a click on their own takes
it back. A voter who leaves takes their vote along.

Once everybody present has voted, the **inactivity timer** closes the vote 5 s after the last activity - a vote cast, changed or taken back, a
map put on the ballot, a page, a sort, a filter or a search in the map browser - and the screen names who reset it last. The vote's **limit is
60 s**, whichever comes first. The most votes win, a tie is drawn at random, and nobody voting keeps the map mid-match and is Random at a match
end. A Wingman map cannot win with more than four on: the next most votes do. The window shows where the votes go, who has not voted yet and
both timers.

### The ballot

One page of ten cards, a second only when what must be on it does not fit (48 at the most); a game known to hold a content before v4.6.0 pages
it in eights. In this order (`OpenBallot`, `VoteRound.cs`):

1. **Keep**: Keep playing this map while the match is on, Play this map again once it is over;
2. **every map a player selected**, tagged PICKED;
3. **one map of each other game mode that suits the players on** (Wingman with four players or fewer), from that mode's kept maps on the server's
   disk, unless a pick stands for that mode;
4. **the server's own mode to fill the page:** one **fresh** map, unless a card above is fresh already; one **surprise** from the discovery stock,
   new to us; and **favourites**, the kept maps by points. An empty slot - no fresh map on the disk, the stock not downloaded yet - is a favourite.

**Random** is a button of its own: the dice after the vote, in the mode the server is in (Arms Race when Wingman does not suit the players on). The
cards show the most votes first. A map a player selects while the vote is open takes the place of a random card of the server's mode nobody voted
for - favourites first, then the surprise, the fresh card last; the other modes' cards keep their place. A map a player put on the ballot that has no
votes left leaves it, until somebody puts it on again. A map selected that is on the ballot already only gets that player's vote. Every card but a
player's pick is on the server's disk, so it loads at once. A card whose map has become unplayable meanwhile leaves the ballot; a winner that became
unplayable hands over to the dice.

### The automatic changes

The dice change the map by themselves only when nobody can vote: at a server start (`bridge.randomOnStart`), on the empty server's way back to Arms
Race, when the map that is on has just gone from the Workshop, when it has just crashed the players' games, and at a match end on a server that
draws no HUD, where nobody would see a ballot. Without the bridge a finished match replays the same map: the Arms Race cfg sets
`mp_match_end_restart 1` and `mp_match_end_changelevel 0`, and the bridge acts on the log's `Game Over:` line.

### The game modes

**Arms Race** is the server's game: a kill earns the next gun, and a kill with the golden knife on the last rung wins the match. It is the default,
after every server restart and after 10 minutes with nobody on. **Deathmatch** and **Wingman** come in through the vote.

Each mode has Valve's official maps for it (`bridge/Modes.cs`) and the Workshop maps of its list: `maps.json` for Arms Race (whose official maps are
playable but never rolled), `maps-deathmatch.json` and `maps-wingman.json` for the others. A map loads in its own mode: the bridge tells the plugin
with `os_mode armsrace|deathmatch|wingman` and sets the mode's convars right before the map command. A map several modes list plays in the mode the
map browser's filter shows it in, else in the mode the server is in when that mode lists it, else in the first mode that does. Each mode has its own
server cfg (`server/gamemode_*_server.cfg`) and its own values in the block list (`plugin/OverStrike2/blocked.txt`). The grenade drops, the match
length, catch-up and friendly fire's settings are Arms Race's own, and the menu leaves them out elsewhere.

## Random picks

A random pick is any map the dice choose: the vote's random cards, its Random, and the [automatic changes](#the-automatic-changes). Never a map a
player selected.

**Points set the odds; each point doubles them** (`Votes.cs`, `weight = 2^points`):

| What | Points |
|---|---|
| Workshop popularity | Up to 4: 4 times the popularity number, from 0 to 1 |
| Each like | +1, up to 5 |
| Each dislike, whatever its reason | -2 |
| Each time it was shown as a card and not chosen | -1, 2 at most, fading to nothing in a straight line over 28 days |

**The popularity number**, per map among its mode's listed Workshop maps (`Popularity.cs`), is the mean of three families: **Rated**, its percentile
by the Workshop's score; **Played**, the mean of its percentiles by subscribers, lifetime subscribers, favourites, lifetime favourites and views;
**Hot**, its percentile in the 30-day trend order, the maps past the fetched list tied at the bottom. A family without data is left out. Valve's official
maps of Deathmatch and Wingman and our own maps count as 0.75; a map with no data at all as 0.5. A map's download size plays no part.

**Too big:** a map whose measured area class in `sizes.json` is XL, or that a player marked too big, comes up in 1 random pick in 100; the other 99 take
one that is not; an empty group hands the pick to the other.

**Never rolled:** the map that is on; the last 10 maps played, while others are left; a map with a crash on record or marked by the players' crashes; a
map on the broken list; a non-map; a blocked map; a map gone from the Workshop; a map a player marked **Ads**. Anybody can still put a map marked Ads
to the vote.

**Only what is on the server's disk**, so whichever card wins loads at once (`RandomQueue.cs`). On the disk means: an official map; a **kept map** (the
[keep set](#what-the-server-keeps)) when it is liked, ours or within the first 90 of one of its mode's lists - the margin covers a list that moved since
the server last stored it - unless the restart watcher's report says it is not there, or its content changed since the version the sync accepts; or a
**stock entry** the watcher reports. Only when none of those is eligible do the dice roll over every eligible map. Without `ratings.json` (no sync, or
before its first run) there is no keep set to go by: every eligible listed map counts, and a winner may have to download first.

**Fresh** means uploaded in the last 30 days, or its content changed in the last 30 days and that content passed the sync's test. Without `workshop.json`
no map is fresh.

**The discovery stock** brings what the keep set does not hold: new uploads and updates without traction yet, and the long tail. Per mode `bridge.discoveryStock`
(10, from 0 to 40; 0 turns it off) entries, rolled with the same dice from the mode's listed maps that the keep set does not hold, that were never shown or
played here and whose current Workshop content passed the sync's test: half of a lane fresh while there are fresh ones, the rest the long tail. An entry is
used up once it is shown or played, and its lane rolls the next at once, which the restart watcher downloads in the background between votes. An entry that is
no longer eligible, whose content changed untested, whose too-big mark changed, or that has not reached the disk in 2 hours is dropped; one whose likes went
down since its roll stays with the odds that left it. A liked surprise becomes a kept map.

`queues.json` is what the watcher works from, version 1: `requested`, the Workshop maps on the open ballot, the most votes first, which it downloads before
anything else; and `random`, the lanes under the keys `armsrace-new`, `armsrace-original` (both Arms Race's lane), `deathmatch` and `wingman`.

## Likes and dislikes

**Like** and **Dislike** mark the map that is on, the arrows on any map's card: one mark per player per map. A mark steps: the opposite one on a marked map
takes that mark back - to none - and says how to cast it ("Your dislike of #12 x is removed - Like it again to add a like"); the next one casts it. The same one
again changes nothing and says so: repeating a mark never takes it back. Every place that shows a mark shows no mark as well. Marks live in `votes.json` in the
bridge's data folder (the file keeps its name; "vote" means the vote for the next map alone); a file that does not parse is moved aside, never overwritten.

**A dislike keeps its reason**, as the mark's value in `votes.json` (`DownReasons`). Any plain Dislike - the board's button, a card's arrow, `!down`,
`!down <number>`, its aliases - on a map that player has not disliked counts at once as the first one, and that player gets a popup listing them for 15 s. It
stands where the Dislike was clicked: over the picture of the card it came from, in the window's live column in place of Like and Dislike, or on the TAB board in
place of its buttons; the popup at the left is left for a dislike typed in chat while the window is closed. The reasons appear only when a dislike is cast - never
for the click that takes a like back:

| Word | Reason | Value | What it does |
|---|---|---|---|
| `meh` | I did not like the map | -1 | A dislike |
| `big` | Map too big | -2 | A dislike, and the map is too big: 1 random pick in 100 |
| `ads` | Shows ads or sends players away | -3 | A dislike, and the map is never a random pick |
| `small` | Map too small | -4 | A dislike |
| `broken` | Map is broken | -5 | A dislike |
| `slow` | Runs badly | -6 | A dislike |

Choosing a reason updates that mark. With no choice the mark stays as it is; the plain dislike is what is kept and logged. Taking a mark back takes its effect with
it. The popup takes no mouse of its own, so it never stops anybody playing: a click needs the cursor the player already has - the window's, or TAB's. A map change, or
another mark by that player on that map, closes it.

Chat steers it directly: `!down big` on the map that is on, `!down <number> <reason>` on any map (`!down 12 big`), a new mark with its reason if there was none, and no
popup. While a popup is open `!down big` sets the reason of the mark it asks about, without a second one. A reason replaces the one before; the same one again changes
nothing and says so. A reason on a map that player liked takes the like back first and says how to dislike it with that reason (`!down big again to dislike it: too big`).
A plain `!down` on a map already disliked keeps the reason, says it is down already and leaves a popup that is open running; `!down meh` puts a reason back to the plain
dislike. The health page counts the marks per reason. The answer to a plain Dislike names the words as well, for a game that shows no popup.

## Fun settings

Anybody switches them, at any time, a vote open or not, for everybody, until the map changes; a map that sets its own value of a setting keeps it until a player changes
it, and gets it back. After 10 minutes with nobody on they all go back to their defaults, like at every map end. A change says on screen for everybody and in chat who
made it. A setting off its default stands out on the page, and names who set it.

| Word | Setting | Choices |
|---|---|---|
| `!gravity` | Gravity | normal, low, moon |
| `!jump` | Super jump: jumps twice as high | normal, high |
| `!bhop` | Bunny hopping; turbo adds strong air strafing | off, on, turbo |
| `!fall` | Fall damage | on, off |
| `!ice` | Ice floor | off, on |
| `!aim` | Laser guns: no spread and no recoil | off, on |
| `!ammo` | Infinite ammo: no reloads | off, on |
| `!hs` | Headshots only | off, on |
| `!vampire` | Vampire: spawn with 1,000 health, the damage you deal heals you up to 2,000 | off, on |
| `!ghost` | Ghost: hidden for 2 seconds after every kill | off, on |
| `!nades` | Big grenades, a wider blast | normal, big, huge |
| `!damage` | Double damage | off, on |
| `!ffa` | Free-for-all | off, on |
| `!ff` | Friendly fire, on by default; Arms Race's | on, off |
| `!instakill` | Instakill: any bullet or knife hit on a teammate kills, on by default; Arms Race's | on, off |
| `!ffnades` | Grenades x10 on teammates; Arms Race's | off, on |
| `!drops` | Grenade drops: every player spawns with one HE grenade, dropped where they die, on by default; Arms Race's (`!nades on\|off` too) | on, off |
| `!catchup` | [Catch-up](#catch-up), on by default; Arms Race's | on, off |

Instakill and Grenades x10 do nothing while friendly fire is off, and friendly fire's three nothing while free-for-all is on; the page says so and they can still be
switched. Ghost hides a player the way the [spawn cloak](#in-game-hud) does (`Cloak.cs`); the plugin's own effects (Vampire, Ghost, no recoil, the drops, catch-up) are kept
like convars, so they are undone like one.

**Random** (`!fun random`) sets every setting the page shows in the mode that is on to one of its own choices, each equally likely, and catch-up on three times in four - never a
combination the page would not make. **Default** (`!fun default`) is what 10 minutes with nobody on does: everything back to its default. Either says in chat, in one line for
everybody, who did it and what changed.

**Every combination works together:** the fun settings touch no convar twice, so any mix of them is simply each one's own values, and `--selftest` walks every combination there
is - 884,736 in Arms Race, 27,648 in Deathmatch and Wingman - checking that each reads back as made, that the page calls idle exactly what does nothing then, and that Random hands
out each allowed one. One combination is refused: **Moon gravity with Super jump High**, a jump about 506 units high, nine times a normal one, out over the walls of most maps. The
page, chat and `gg_fun` refuse the second of the two with the reason ("Super jump cannot be High while Gravity is Moon: together they throw players out of most maps"), and Random
keeps super jump normal on moon gravity. The one-hit knife and the Zeus keep working under headshots only.

## Catch-up

In Arms Race a player who falls behind the leader gets a boost at spawn, more the further behind, and a private message says what it is and how many kills behind they are. The
numbers are `plugin/CatchUp/catchup.json`; the module is [`plugin/CatchUp/`](../plugin/CatchUp/).

| Kills behind | Health | Damage taken | Damage dealt | Lifesteal | Regeneration | Also |
|---|---|---|---|---|---|---|
| 2 | +5% | | | 5% | 3% a second, after 4 s unhurt | |
| 3 | +15% | -10% | +5% | 5% | 3% a second, after 4 s | |
| 4 | +35% | -15% | +15% | 5% | 5% a second, after 3 s | a second chance once per life, 2 s more spawn protection |
| 5 | +50% | -25% | +20% | 10% | 5% a second, after 3 s | the same |
| 6 | +75% | -30% | +25% | 20% | 5% a second, after 3 s | the same |
| 7 | +95% | -35% | +35% | 25% | 8% a second, after 2 s | a second chance, 3 s more spawn protection, infinite ammo, a one-hit knife and Zeus |
| 8 and more | +120% | -40% | +40% | 35% | 8% a second, after 2 s | the same |

The health is a share of what the life spawned with: 1,000 under Vampire, never Vampire's 2,000 ceiling. The boost is decided again a frame after every change to the ladder - a kill,
a team kill, a demotion, a player leaving - silently, so it shrinks as soon as its gap closes and grows when the leader pulls away. Within a life health only goes up, and when the
boost shrinks the other values take the cut. A leader 2 kills or more ahead of second place takes more damage from enemies: 10% for every 2 kills of lead, up to 30%. Against the leader a
boosted player's duel factor (health with lifesteal, over the damage they take, times the damage they deal and the bounty) stays within `MaxDuelFactor` (8).

The module counts the ladder from the match's kills, since the game exposes no ladder position, by CS2's own rules as real matches show them: a team kill under friendly fire takes the
killer down a level, and every kill goes to the plugin's log as `CatchUp: kill:` with both teams and both counts after it. Its self-test replays real match logs against them. `!catchup off`
switches it off for everybody until the map changes; `gg_catchup` is the console's word.

## Broken maps

Thousands of community maps include some that break things, so every broken map says why, on its card, before anybody loads it.

- **Crashes.** A few seconds after every map start the bridge reads RCON `status`; its `spawn` counter tells whether the server process is new. A new process that did not quit cleanly is a
  crash, counted against the map it was loading. A map with a crash asks the player for a second click, and the dice skip it.
- **A clean quit** is one the bridge was told of: the restart watcher's notice right before its planned quit (`POST /quitting/<secret>`; a cancel takes it back when the quit could not be
  delivered after all), the bridge's own Restart as it sends its quit, or the echo of an RCON `quit` or `_restart` in the log stream. The echo alone was not enough: the engine segfaults on
  quit, and the last log batch can die with the process. A second word of the same quit within two minutes changes nothing. A freeze kill gets no notice and stays the crash it is.
- **Maps that crash the players' games.** Three players timing out within 60 s of entering a map, while the server keeps answering, mark it broken by itself, in `state.json` with its reason;
  the dice then take the server to another map.
- **A Workshop update lifts a mark**, a crash's or a player crash's: the author may have fixed it.
- **`broken.json`** holds three lists: `maps` (maps that crash or freeze the server, or advertise: a pick asks for a second click, the dice never roll them, each says why), `packs` (Workshop items with
  no map in them - skins, models, empty uploads: refused) and `blocked` (refused for good, each with a reason). **`blocked.json`** is a hand-made list of the same kind.
- **Maps gone from the Workshop.** Every 4 hours the sync checks every listed map through the public Web API and posts the verdicts to the bridge (`POST /removed/<secret>`, kept in `removed.json`): a
  map that is gone, private or banned is refused, leaves an open ballot, stops a countdown to it, and if it is the map that is on, the dice roll another, since nobody new could download it. A report
  calling more than 10% of 50 or more ids gone is not believed. Without the sync nothing is ever marked gone.
- **What a map changes** in the game's settings is put back when it ends: `host_workshop_map` resets no game convar by itself. **The game-changers** no map may set at all (no spread, radar off, a mode
  switch, cheats: `plugin/OverStrike2/blocked.txt`, a value per game mode) are put back within a second, and the players are told. A player's choice on the Fun page is never blocked.

## The HUD's version and the HUD check

A game keeps the HUD content it last downloaded, and Steam takes up to about an hour to update it, so one plugin drives them all: it sends what the newest content has, and an older one simply lacks what it
lacks. Every shipped layout is kept in `hud/reference/`, which `--selftest` holds the new one to. The HUD itself says when a player's game holds an older or a newer content than the server's, by comparing
version numbers in the layout, and an older game is told how to update: quit CS2 and start it again, and Steam updates it.

**The HUD check.** MultiAddonManager makes everybody download the addon, but whether the layout loaded the server cannot see. The only proof is a click: a player not seen clicking since their last real join
gets the window at their first spawn on a map, and any button in it counts. The click also says which content the game holds (SERVER, Players). With `hud.check` `warn`, the default, one who has not
clicked 90 s after that spawn is told in chat what to do and nothing more; `enforce` disconnects them; `off` asks nothing. A verification lives in memory, per connection: a reconnecting player may have a
new HUD. No fresh word from the bridge means off.

**The item watch.** While Steam does not show the HUD's Workshop item to everybody - Valve reviews every new version, and a held item is an error page for everybody but its owner - the server must not make
joiners download it, and nobody is disconnected. The plugin reads the Web API every minute, which says whether the item is gone, private or banned and when a new version went up, but not whether Valve
holds it. So the item's page is visited right after a change, then at most every 15 minutes while it is not shown, for 3 hours (`ItemPlan`, `hud.itemPage`). A verdict of not shown takes the item off the
addons joiners must download, and players are told; the visit that finds it shown puts it back.

## Maintenance flag

While the file `maintenance` exists in the bridge's data folder, the bridge makes no automatic map change: no start roll, no match-end vote, no way back to Arms Race, and a vote nobody called or nobody voted
in changes nothing. A vote a player called still decides. No crash is recorded, and the sync waits. Remove the file and everything resumes. `deploy/release.py` sets it around its quit; a restart by hand should
too, or it counts as a crash.

## What the server keeps

The **keep set** (`core/KeepSet.cs`) is the Workshop maps the game server stores: every map with at least one like, however many dislikes it has; per mode three lists of 100 playable maps together, so up to 300 - the
best Workshop score (`vote_data.score`, the Workshop's "Top Rated"), the most current subscriptions ("Most Subscribed") and Steam's trend over the last 30 days ("Most Popular": `QueryFiles` RankedByTrend with
`days=30`), all from `ratings.json`, which the sync fetches daily; our own maps and the HUD item; the discovery stock and the maps on an open ballot.

Before every engine start ([server.md](server.md#the-maps-folder-the-hand-over-and-the-deletion)) the server sorts its maps into real folders per type - `maps/<armsrace|deathmatch|wingman|own|hud|other>/<id>-<name>/`,
with `maps/INDEX.tsv` listing them all - with Steam's `content/730/<id>` a relative link to each. Steam loads, updates and re-checks a map through the link, and a new download lands as a real folder where Steam expects it,
which the next start moves. If the bridge's lists cannot be read, or a move fails, the engine starts anyway. The same run takes in the maps the sync hands over (only a complete one, its folder, link, acf entries and manifest, so
Steam's client holds it as its own download) and then deletes every stored map outside the keep set: its acf entries first, backed up, then its link and folder. Never our own maps, the HUD, the stock or a map on an open
ballot; refused, with the reason logged, when an input is missing, the scores are over 3 days old or cover under 90% of a list, any of a mode's three lists is under 50 maps, a lane of the stock is under its minimum, or there is no
own map or HUD item. Each run writes `update-watch/keep.json` and its list, `update-watch/deleted-<time>-<done, dryrun or refused>.tsv`.

**Downloads.** Steam runs one Workshop download at a time in a server process, and one that is running cannot be stopped. The restart watcher downloads the maps on an open ballot and the stock in the background, one at a time, also
while people play, and holds while a change counts down or a map loads; a download a player waits for goes first, and its progress shows on screen ([server.md](server.md#downloads-a-player-waits-for)). Only the engine's own
downloader can put a map on the running server; what the sync hands over is taken in at the next engine start.

**Downloads that get stuck.** The Steam client in a server process downloads from a ranked list of content servers; a server that fails is dropped for the life of the process, and once none is left every download fails. Two things
lead there: a full manifest re-check in a new container (avoided by keeping `~/Steam/depotcache` on a volume - mount it), and content servers the client ranks first but cannot reach (keep every one reachable). The watcher restarts a
process in that state once nobody is on.

## The Workshop sync

`os2-sync serve`, in a container of its own: the game server's image, for its SteamCMD and the libraries a test engine needs ([commands, files and the shadow run](server.md#the-workshop-sync-os2-sync)). It has no time windows: a run
starts whenever the server has had nobody on for `sync.quietMinutes` (10) and there is work, and lasts at most `sync.runHours` (3). No run starts from 15 minutes before the daily restart to 10 after it, or with under 20 minutes
left before that cut. Discovery, the check and the ratings run at most every `sync.apiEveryHours` (4); downloads, tests and publishing repeat in batches of `sync.batch`, so a long run publishes as it goes.

1. **Find** new maps with the Workshop's tags (Arms Race, Deathmatch, Wingman) through the Steam Web API. It needs a Web API key (`sync.webApiKey`); without one the step is skipped.
2. **Check** every listed map (gone, hidden, banned, updated) and hand the verdicts to the bridge. Once a day also fetch the Workshop's score and subscriptions of every listed map and every waiting one, and each mode's Most Popular
   order, into `ratings.json`. Write each listed map's Workshop numbers and the version the sync accepts into `workshop.json`.
3. **Hand over** the maps the server keeps but lacks, and the ones it stores in an older version than one a test passed (`<work>/handover/`). A hand-over waits for SteamCMD's acf entry of its download, and a map already staged with one
   goes without a new download.
4. **Download** with SteamCMD's anonymous login, one item at a time, through everything that is queued: the maps whose score would make one of their mode's three lists first, then new uploads, listed maps that changed, and everything
   else strictly by subscribers, whatever the mode. Nothing is skipped for its size. A tested map the server keeps is handed over instead of deleted.
5. **Scan and load-test:** read each download's packages (a map, a model pack, an empty upload, an old CS:GO upload; and any commands that advertise another server or post a gambling ad), then load each map in a test copy of the server
   inside the container - its own port, passwords and Workshop folder, no plugins, no log push - and hold it `sync.holdSeconds` with two bots.
6. **Publish** the passes into the bridge's data (Arms Race onto `maps.json` and `search.json`, the other modes onto their own lists, the failures onto `broken.json`), and ask the bridge to reload. The optional mail is for problems only:
   a run stopped early or a step that errored, Steam pushing back, the test engine failing to start, a safety check refusing, the disk running low (`sync.diskLowGb`), a failed hand-over, any other error. Each says what went wrong and what
   the sync did about it, and points to `sync.mail.dashboardUrl`; a problem that persists is mailed once a day at most. A run without one mails nothing; its report stays in the state folder's `reports/` and in `sync-status.json`. The state folder keeps the newest 100 reports, and in `backups/` the bridge's lists as the newest 10 publishes found them.

Hard limits in code: one Web API call at a time, at least 2 s apart, through one door; at most 10,000 calls in any 24 hours and 500 a run; any 429, 403 or 401, or a burst of server errors, stops every API step for the rest of the day. The
configuration can lower each limit, never raise it. There is no daily download cap: an HTTP 429, 403 or 401, a SteamCMD rate limit or two Access Denied in a row end the day's downloads, three failed downloads in a row or a second stall end
the run's, and a bug brake downloads a map at most twice a day. Three failed downloads of one version of a map are its last, until the Workshop has a newer one. A listed map whose new version failed its test is tested again at most once a
day, until a version passes; a newer version is tested at once. One SteamCMD download at a time, one test engine. When a player joins, the test engine stops at once and no new download starts; the run goes on only after a quiet spell with
nobody on.

The sync also writes `sync-status.json` into the bridge's data folder (schema 1) for the in-game status page: what it does now and why (`now.state` is one of idle, waiting, discovery, check, ratings, serve, download, test, publish, stopped),
today's counts, the totals, the queue with its next maps in the order they are taken, the keep set's parts, and the history of runs and map events. It is written atomically at every phase change, at least every 15 s during a run and every
minute otherwise; fields may be added to it, never renamed or dropped.

### The dashboard

`sync-status.json` for everybody (`bridge/SyncDash.cs`). The bridge reads it whenever it changed (it looks every 2 s), adds the discovery stock, how many of its maps the restart watcher reports on the server's disk and the background download, and makes two
things of it. The menu's **Sync** page: what the sync does now (its state, why, the map, the step on a bar, the run, today's counts, the totals and the file's age), its queue (the counts per mode, the finish estimate, what the server itself still
lacks, the stock, the next maps six a page) and its history (the runs three a page and the map events eight a page, newest first). The bridge writes every word of it and serves the block on `GET /dash/<secret>`, its version in the live state; the plugin
pages each list per player and passes the rest through (`bridge/HudDash.cs`), so a change to the page's words or rows needs a bridge alone. And a website: `index.html` and `dashboard.json` in `web/` in the bridge's data folder, rewritten when they change and
every minute, for a static web server behind a login (the page reads the JSON every 5 s and searches the whole history) - the bridge's own HTTP port is never reachable from outside. A file that is missing, does not parse or is older than 15 minutes is said as such,
and the last good one stays on screen meanwhile.

## The data files

In the bridge's data folder (`paths.bridgeData`, `/data` by default). One writer per file.

| File | Written by | What |
|---|---|---|
| `state.json` | the bridge | The map that is on, its mode, the history, the crash marks, the stock, the cards shown, who was seen |
| `votes.json` | the bridge | The likes and dislikes |
| `removed.json` | the bridge, from the sync's verdicts | Maps gone from the Workshop |
| `queues.json` | the bridge | What the restart watcher downloads and the keep set holds |
| `maintenance` | whoever stops the server on purpose | [The flag](#maintenance-flag) |
| `web/` | the bridge | The [website](#the-dashboard) |
| `maps.json`, `maps-deathmatch.json`, `maps-wingman.json`, `broken.json`, `search.json` | the sync | The lists. The repository's are the starting lists, which the installer and `deploy/data.py` put only on a server that has none yet |
| `ratings.json`, `workshop.json`, `sync-status.json` | the sync | The Workshop's numbers; the sync's state |
| `ours.json`, `sizes.json`, `blocked.json` | by hand: this repository | Our own maps; each map's measured walking area and size class; the maps blocked for good |

The bridge reads these again about 10 s after they change, so a new list is a file copy and nothing restarts; `POST /reload/<secret>` reads them at once. A file that does not parse changes nothing, nor does a list cut by more than half. What a reload does at
once: the menu's map list follows, a card or a countdown for a map that has just become unplayable goes, and a map that is on and has just gone from the Workshop makes way for the dice.

`maps.json`'s entries are `rank`, `id`, `name`, `sizeMb` and the like, append-only: a number, once given, keeps its map. No Workshop description enters this repository: `search.json` here has titles, ids and tags only, and `bridge/search-index.py` keeps it in step
with `maps.json`.

## Configuration

Every part reads one settings file, `/etc/overstrike2/overstrike2.jsonc`: [settings.md](settings.md) has every key, its default and the variable it replaces, and the game image's own variables. Until the install step writes that file, the bridge and the plugin
fall back on those variables when there is no file or the file cannot be used, and each logs at its start which one it used, never a value. [`deploy/overstrike2.example.jsonc`](../deploy/overstrike2.example.jsonc) is a complete example, and
[`deploy/compose.yml`](../deploy/compose.yml) sets the rest.

## The plugin layer

Before every engine start `os2-server install` puts in the pinned Metamod, CounterStrikeSharp (with its .NET runtime) and MultiAddonManager once per version, re-applies the `gameinfo.gi` search path (every game update rewrites that file), has MultiAddonManager
mount the HUD's Workshop item (`hud.addon`, [server.md](server.md#the-hud-addon)), and copies every plugin folder in. Only settings with no usable `hud.addon`, or no MultiAddonManager on disk after its download was tried, stop the launch ([server.md](server.md#how-it-starts)): every other failure is a logged warning and a server without that component. With `plugin.enabled` false it only removes the search path, and the server runs vanilla - the
switch for the days after a game update breaks Metamod or CounterStrikeSharp. The bridge goes on rolling maps then. To bump a pin: change the version in `server/OverStrike2.Server/Install.cs`, build the plugin against the same CounterStrikeSharp version, and ship both.

## Log-line grammar

The parser is one method (`Parse` in `bridge/Program.cs`) and `--selftest` shows exactly what it accepts. The game server pushes its log with `logaddress_add_http`: a `POST` per tick, one or more lines, each prefixed `MM/DD/YYYY - HH:MM:SS.mmm - `.

- A map start is `Loading map "<name>"`, followed by `Started:` (the name comes from the request's `X-Game-Map` header).
- A clean quit's echo is the echo of an RCON `quit` or `_restart` (one of the three signs of a clean quit, [above](#broken-maps)).
- Player lines: `"<name><uid><[U:1:n]><team>" say "..."`, `connected`, `entered the game`, `disconnected (reason "...")` - a reason containing `SHUTDOWN` is a map change's reload, not leaving.
- A match end is the `Game Over:` line, once per match and last in its block; chat cannot fake it.
- A match restart (the same map again) logs only team switches, so it is never mistaken for a map change.
- `X-Server-Instance-Token` is not renewed per process, so restarts are recognised by the `spawn` counter instead.

## Build and test

Requirements: the .NET 10 SDK, Python 3.12+, and a container builder (Docker, or WSL containers on Windows) for the bridge's image. The HUD and the maps need Windows with Counter-Strike 2 and its Workshop Tools. [AGENTS.md](../AGENTS.md#build-and-test-before-every-commit-that-touches-code) has the commands every
commit runs.

| Test | What it holds |
|---|---|
| `dotnet run -- --selftest` in `bridge/` | Hundreds of checks on a handed clock, with no server: the log parser, the votes, the ballot, the dice, the stock, the tags, the HUD's words and wiring, every panel id and class against the layout, the older HUD contents against the panels sent to them, the rules below, and the chat table above |
| `plugin/OverStrike2.SelfTest` | The freeze, the spawn cloak, where the plugin's settings come from, the block file, the Web API's one door |
| `plugin/CatchUp/SelfTest` | The catch-up rules, replayed against real matches' logs |
| `core/SelfTest` | The settings file and what the parts share; it holds [settings.md](settings.md)'s tables to the code (`-- --write` makes them) |
| `server/OverStrike2.Server/SelfTest`, `server/OverStrike2.Sync/SelfTest` | The two programs, their Linux-only checks in the game image ([server.md](server.md#build-and-test)) |
| `python bridge/search-index.py --check` | The search text is in step with the map list |

**A real client.** A layout cannot be fully tested without the game, so a new HUD is also tried in a real CS2 client before a release: a private harness, not in this repository because it drives its owner's own client and server, starts the client with no window on
screen, joins a test copy of the server, loads the local HUD build there with VAC off, takes screenshots, and drives the menu through server-side clicks. On the live server only the published HUD ever runs, in a plain client.

**The bridge alone.** Native AOT does not cross-compile between operating systems, so the image's build stage is the Linux publish: `docker build -t localhost/overstrike2-bridge -f bridge/Dockerfile .` from the repository root. To run the bridge on Windows against a local
server: start the server with `+log on +logaddress_add_http "http://127.0.0.1:8080/log/dev"`, set `RCON_HOST=127.0.0.1`, `RCON_PASSWORD`, `LOG_SECRET=dev` and `STATE_DIR`, and `dotnet run`. The self-test reads the plugin's and the HUD's files from beside `bridge/`; set
`OVERSTRIKE2_COMPOSE_FILE` to your compose file to have it checked as well.

### Keeping the rules in sync

The rules are said in the menu: its How it works and Help pages and the start page. The bridge's `--selftest` holds them to the code: every number those words state must be one of the code's constants, so a rule change fails the selftest until the code and the menu's words
(`bridge/Hud.cs`) agree again. It holds the chat table on this page to `ChatCommands.All` the same way.

## Files

| Path | What |
|---|---|
| `bridge/*.cs` | The bridge; `SelfTest*.cs` its self-test. The shared ones - the words, the vote's and the modes' tables, the HUD's logic - are compiled into the plugin too (`plugin/OverStrike2/OverStrike2.csproj` names them). |
| `bridge/maps.json`, `broken.json`, `blocked.json`, `sizes.json`, `search.json`, `ours.json` | The [data files](#the-data-files) the repository starts from. |
| `bridge/dashboard.html` | The website's page, built into the bridge. |
| `plugin/OverStrike2/` | The plugin; `blocked.txt` the block list, `convars.txt` the floor of game convars the plugin watches; at every start it adds what the running build has beyond it (`ConVarNames.cs`). |
| `plugin/CatchUp/` | The catch-up boosts and their self-test; the plugin compiles them in. |
| `core/` | The library the parts share, and its self-test. |
| `hud/` | The HUD addon ([`hud/PUBLISH.md`](../hud/PUBLISH.md)); `reference/` the shipped layouts; `thumbs.py` makes the map pictures, which are never committed. |
| `maps/` | Our own maps' generators and their Workshop publisher ([`maps/README.md`](../maps/README.md)). |
| `server/` | The two programs, with the launcher `pre.sh` and their self-tests, and each game mode's cfg. |
| `deploy/` | A host that builds everything itself (`compose.yml`), and building and shipping from a workstation, with a private config. |
| `tools/scan/` | The scanner the hooks run. |
