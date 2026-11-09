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
one line, **Start a vote** and the way into the menu, and at its foot **Lately**: the last three lines for everybody or for this player, the newest first, each with its age
(Now, 4 min, 2 h). It takes the mouse while TAB is down; letting go, a hit, death or the window opening gives it back. `hud.tab` `off` leaves TAB alone (`HudTab.cs`).

**The window.** **TAB + E** opens the full menu, on five tabs. It closes after 30 s without a click, a gold line running out over that time, and damage, death and a respawn
never close it. A vote opens it for everybody and holds it open ([the freeze](#the-freeze)).

**The welcome** is that window, opening by itself at every real join: on START, 1.5 s after the player is alive, at their next spawn if they are not, and after 20 s if they never
spawn. It stays until its X: no idle close, through damage, death and a respawn. A vote takes it over and hands it back on START. The X is also the
[HUD check](#the-huds-version-and-the-hud-check)'s click, so unless `hud.check` is `off` a player the server has not seen click since that join gets the window again at their first spawn on each map.

| Tab | Page | What is on it |
|---|---|---|
| START | | The welcome: the greeting ("Welcome, Bob", "Welcome back, Bob") as its large text, three steps and how to reach the menu; for a player seen before, the map that is on, who picked it, its likes and how to mark it. The window's head carries the logo, the title and, beside it, the tagline. |
| MAPS | Browse | The map browser: eight cards a page, each with the map's picture, its place on its list, size, likes and tags, its Play button and like arrows, and the mode it plays in, as a label on its picture and the card's colour (Arms Race gold, Deathmatch red, Wingman blue). Full-text search over names, tags and Workshop ids, whatever the case ("aim" finds FUN GAME AİM), typed in chat - the one text input there is: the HUD has none, and the server cannot open a player's chat box (the client refuses `messagemode` from it). With the window on MAPS > Browse a chat line searches the browser (release 199): press Y, type part of a map name, press Enter. All chat and team chat alike, and the line still shows in chat as typed; when its words find a map they become the search, the best matches first, and when they find none the cards stay and the window's line says `No map matches "<words>" - Try fewer letters or other words`. No search: a command (! or /), a Workshop link or id (its card opens, as anywhere), a bare number while a vote is on, and a line of nothing but chat words (`gg gl hf glhf wp ty thx lol xd ok nice ns np ez`); never on the ballot or another page. From anywhere, `!s` and a few words (or `!find`, `!search`, `!maps <words>`) open the browser on the matches. The search area says `Press Y and type a map name`, and a click on it says how on the window's line and in chat; its cross clears a search that is on. From HUD content v4.11.0 that click also helps the search for 20 s (`HudBrowser.HelpSeconds`, or until a search or a second click), and so does the player's own chat while it is open (the client's `HUD_CHAT_VISIBLE`, which no incoming chat line lights): the box lights up - with the chat open it says `Type a map name, then press Enter` -, a row of words takes the filters' place (the player's last three searches that found maps, and the six words most map names share, `HudSearch.Popular` - a click searches one), the window lifts 40 px, and a pointer beside Valve's chat line at the bottom left says `Press Y: type a map name here, then Enter`. Never on the ballot; sorting by place on the list, likes, name or size; filters by size (under 25 MB, 25-120, 120-400, 400 and up), by the player's own likes, by official maps, by Our maps and by game mode. Previous puts the map before this one to the vote. |
| MAPS | Vote | While a vote is on, the ballot ([below](#the-ballot)); Browse then adds to the vote. |
| FUN | | The [fun settings](#fun-settings), with Random and Default. |
| SERVER | Players | Who is on, their team, the HUD their game showed, the HUD check's clock, their vote while one is open, and how long they have been on. |
| SERVER | Game | The match length - 10 or 60 minutes in Arms Race and Deathmatch, 8 or 16 rounds in Wingman - and who set it; Restart the server, for a stuck one, and Restart the match, each twice to confirm ([below](#the-match-length-and-the-restarts)). |
| SERVER | Sync | The [Workshop sync's dashboard](#the-dashboard). |
| HELP | Help | Where everything is. |
| HELP | How it works | Every rule of the vote and every chance of a random pick: the one set of rule texts in the game ([Keeping the rules in sync](#keeping-the-rules-in-sync)). |

**A map that is not listed.** Any Workshop map can be played by its id, typed in chat (`!play <id>`, or the bare id) or pasted there as a link: its card asks the
Workshop about it (the plugin, through the public Web API), and **Download and play** fetches it into the server's own Workshop cache,
with the progress on the card, while nobody's game is interrupted (`WorkshopCard.cs`, `WorkshopPlay.cs`). Once it is installed it is
selected like any map: to the vote. What the server refuses anyway is refused on the card before the Workshop is asked: a non-map, a
blocked map or one gone from the Workshop, and the HUD's own item (`hud.addon`), which is no map either.

**The pictures** are inside the addon, because CS2 refuses web pictures in a custom HUD: each map's Workshop preview as a small JPEG, an
official map's the game's own screenshot. A map listed after the release that shipped its picture shows the card's plain backdrop. Each map
keeps its picture's number for good (`HudThumbList.cs`), so an older HUD never shows a wrong picture.

**The spawn cloak.** A player whose window is open while they are on their spawn is hidden from everybody else, in every game mode, for as
long as it stays open (`CloakPlan.cs`): the model, every weapon they carry, wearables and shadows. Moving more than 64 units sideways or 72
up or down, attacking, or a vote's freeze starting or ending spends it until the next spawn; closing the window only ends it until the
window opens again. A player killed with the window open is hidden again at the respawn. The welcome a real join opens waits for its X, so a
game that cannot draw our window - a content before v4.8.0, a download that failed, a joiner while the item is held - would keep it open on the
server all map and be hidden at every spawn; the end of [the HUD check's](#the-huds-version-and-the-hud-check) 90 s closes it instead, with
`warn` and with the check off, and the cloak ends with it ("the menu window closed"). What is no model - the radar's dot, the name when
aimed at, sounds, muzzle flashes - still shows. The cloak hides a player from people, not from the [bots](#bots), which aim by what the server knows:
so while it hides a player - the spawn cloak, Ghost, `os_cloak hide` - no bot's damage hurts them (`BotGuard.cs`), until their own first attack in that
hide: a shot, a throw or a hit on somebody else ends that shield, though not the hide, and the next hide shields again. A player's damage lands as ever.

**Messages.** Every message a player can get is one line of one catalogue (`bridge/Shared/Messages.cs`): where it goes on screen and who sees it there, who gets a copy in chat and on
the board's Lately list, and how long it stays. The bridge and the plugin both post through it, so one message cannot go two ways. What happens for everybody shows for everybody;
what answers one player's click goes to that player alone. Nearly every word comes from the bridge and the plugin as dialog variables, so most changes need no new HUD (`bridge/Shared/Hud.cs` and its siblings there).

The places on screen: the **top stack**, top centre, holds the countdown banner, the status banner, the outdated and the newer banner, and the top line; above the open window only
the top line stays. The **card stack**, under the crosshair, grows upward and draws above the window: the now-playing card and catch-up's rows. A **window line** lies over the foot of
the window's page. The **centre text** and the **alert** are CS2's own.

| Message | Where | Who sees it | How long | Chat | Lately |
|---|---|---|---|---|---|
| The welcome, also the HUD check's window | The window, on START | That player | Until its X | | |
| The tagline | The window's head, beside the title | That player | | That player | |
| "Welcome back", with the map that is on | A line on the welcome page | A player seen before | With the window | | |
| Now playing, with the map's picture | The card | Each player | 8 s, at their first spawn on a map | | Everybody |
| A player joined with this HUD version, at the first click that says it | The centre text | Everybody | 8 s | Everybody | Everybody |
| Your HUD is out of date, with both versions | Its banner, red | That player | As long as it is | | |
| Your HUD is newer than the server's | Its banner, red | That player | 15 s at the join | | |
| Steam does not show the HUD ([the item watch](#the-huds-version-and-the-hud-check)) | The status banner, red | Everybody | 45 s | | |
| The HUD is back on Steam | The status banner, green | Everybody | 30 s | | |
| No menu during that hold, and the menu back | Chat alone | | | A player without the HUD | |
| The HUD check's warning | The alert, two lines | That player | 15 s | That player | |
| The vote | The window, on the ballot | Everybody | Until it is over | | |
| Who started the vote | The window's header | Everybody | While it is open | Everybody | Everybody |
| The inactivity timer and who reset it, the 60 s limit, who has not voted | The window's header | Everybody | While it is open | | |
| A vote clicked: who voted and who still has to | Chat and Lately alone | | | Everybody | Everybody |
| The vote decided, and the change counting down | The window's header | Everybody | Until the map changes | Everybody | Everybody |
| The vote kept the map | The window's header | Everybody | 8 s | Everybody | Everybody |
| A vote cannot be called now | A window line, in the player's own words | The player who tried | 8 s | Everybody, naming them | Everybody, naming them |
| A fun setting changed, catch-up and the grenade drops among them, a bot setting too; Random; Default | The top line | Everybody | 10 s | Everybody | Everybody |
| A map's game-changer undone | The top line | Everybody | 10 s | Everybody | Everybody |
| [Bots](#bots): a preset started, and what it set; Kick all bots; Default | The top line | Everybody | 10 s | Everybody | Everybody |
| A Fun click refused | A bubble at the button | That player | 8 s | That player | That player |
| Catch-up's boost | The card, a green row per item | That player | 5 s | That player | That player |
| The leader's bounty | The card, a red row per item | That player | 5 s | That player | |
| A map change counting down | The countdown banner, with the map's picture | Everybody | Until the map changes | Everybody | Everybody |
| The server downloading a map, or finishing a background download first; a download failed | The top line | Everybody | 10 s, or as the restart watcher asks | Everybody, at its start and each quarter | Everybody, one line that replaces itself |
| A Workshop card downloading to the server | The card in the map browser | That player | While it downloads | Everybody, at its start and each quarter | Everybody, one line that replaces itself |
| Play asks first: a risky map's padlock | A window line | That player | 30 s | That player | |
| Why the dislike? ([below](#likes-and-dislikes)) | One dialog at the centre, only with a cursor | That player | 15 s | | |
| The server restarts after this match | The status banner | Everybody | Until then | Everybody | Everybody |
| A restart counting down: the server's, a player's Restart the server, Restart the match | The countdown banner, with the project's logo | Everybody | 10 s | Everybody | Everybody |
| A CS2 update | The top line | Everybody | Until the restart | Everybody | Everybody |
| Updated to a new version | The top line | Everybody | 60 s | Everybody | Everybody |
| The same, for a player who joins within 10 minutes | A window line | That player | 15 s | That player | That player |
| A plain news item | The top line | Everybody | 8 s | Everybody | Everybody |
| The server crashed on a map | The top line | Everybody | 30 s | Everybody | Everybody |
| The same, for a player who joins within 10 minutes | A line on the welcome page, and a window line | That player | The window line 30 s | That player | That player |
| The answer to your own click: refused, click again, already set | A window line | That player | 8 s | That player | That player |
| Command replies: an unknown command, an alias taught, `!help`'s list | Chat alone | | | That player | |

The top line holds 240 characters at most, a window line 140 and the centre text 80.

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
| `!time [10\|60]` | Match length in minutes, this match too - Wingman's in rounds, 8 or 16 - Alone it switches | `!length` `!minutes` `!timelimit` `!roundtime` `!duration` |
| `!restart` | Restart a stuck server - Twice to confirm | `!reboot` `!fix` `!unstuck` `!stuck` `!reset` |
| `!rematch` | Restart the match - Twice to confirm | `!restartmatch` `!matchrestart` `!restartgame` `!rr` |
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
back. The cards have no numbers: a `!vote` with a number answers that a vote is a click on a card, and a bare number stays
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

Nobody can call a vote while a map change counts down or a map loads, while a restart of the server or the match counts down, while [Steam is not showing the HUD](#the-huds-version-and-the-hud-check),
or for **2 minutes after the last vote**; a match end opens its vote anyway, except while Steam is not showing the HUD. A player who tries reads why in their own line, and chat and the board name them for everybody; during the 2 minutes the button
says how long is left. A risky map - a crash on record, on the broken list - is locked on its card: the first click opens the padlock, and a second click within 30 s puts it to the vote.
A Wingman map takes as many a side as it has [spawn points](#wingman-and-its-spawn-points): with more on it cannot win, and it is refused when asked for.

### The freeze

Every vote opens everybody's window on the ballot and freezes everybody: nobody moves, shoots, takes damage or closes the window, bots
included (`VoteFreeze.cs`). A player who joins meanwhile is frozen and counted. Everybody unfreezes at the same moment: when the vote keeps
the map, once the window has said so for 8 s; when it changes the map, after the countdown (`bridge.changeDelaySeconds`, 8 s), everybody frozen until the map changes. The
plugin frees everybody by itself if the bridge stops answering for 40 s, on a new map, and after 5 minutes of one freeze, so a lost bridge
never leaves anybody frozen.

### Voting

A vote is a click on a card, changed or taken back as often as the player likes: a click on another card moves it, a click on their own takes
it back - except in the first 2 s after their vote landed there (`HudVote.KeepSeconds`): a double click, or a click to make sure, keeps the vote
and says "Your vote is on this map". A voter who leaves takes their vote along.

**No card moves while the vote is open:** each keeps the place it went on in, so a click lands on the map the player aimed at; once the vote is
decided, the cards show with the most votes first. A click counts for the map the player saw on the card. Each card has a number of its own that the
vote never gives again, so a click on a card that has just left - a pick that lost its votes, a card a pick took the place of - counts for nothing
and says "That map left the vote - Click again", never a vote for the map in its place. The one exception is a player's own Add: a click on the
place where the card their Add just put on stands, made before their game drew it there (the plugin's grace after a change, `HudVote.OwnCardNow`;
or with the old card's number, which the bridge matches to the card that took its place), says "Your vote is on this map" and changes nothing -
as does a click on the card their vote is on that the bridge gets before that player's game showed the vote there. The second click of a double
click on the map browser's Play counts for nothing (`HudFrame.SecondClick`, within 0.5 s): the Add turns that card into the ballot's card in its
place, and the click would otherwise take the vote off the map just added.

**No vote click is lost on the way:** the plugin keeps at most 8 clicks on their way to the bridge at once (`BridgeLink.cs`); while that is full, a
vote, a map put to the vote, a vote started and the HUD check's click wait - one a player and kind, a newer one taking its place - and go out as
room comes; any other click is given up and its player told "The server did not get that click - Click again". Every vote click, every map put to
the vote and every click given up is a line of the plugin's log (`Over Strike 2 vote: ...`), and the bridge's log keeps the same in its day files
([The data files](#the-data-files)), which outlive a swap of the bridge.

Once everybody present has voted, the **inactivity timer** closes the vote 5 s after the last activity - a vote cast, changed or taken back, a
map put on the ballot, a page, a sort, a filter or a search in the map browser - and the screen names who reset it last. The vote's **limit is
60 s**, whichever comes first. The most votes win, a tie is drawn at random, and nobody voting keeps the map mid-match and is Random at a match
end. A Wingman map with spawn points for fewer a side than are on cannot win: the next most votes do. The window shows who started the vote, where the votes go, who has not voted yet and
both timers, then what came of it and the countdown. Every vote - cast, changed or taken back - says in chat and on the board who voted and who still has to.

### The ballot

**Your own vote** (HUD content v4.11.0): the card your vote is on has a white frame and glow and a white YOUR VOTE badge with a check at its picture's top right, which reads
CLICK TO TAKE BACK under the mouse; LEADING and TIE keep their chips but no frame, so on an open ballot the only framed card is yours, and Random's button lights the same white
while your vote is on Random. White differs from every other state by brightness, not hue. A Wingman map whose spawn points take fewer a side than the players on greys its Play
(class `unfit` on its card, from the bridge), in the browser and on the ballot alike.

One page of ten cards, a second only when what must be on it does not fit (48 at the most). In this order (`OpenBallot`, `VoteRound.cs`):

1. **Keep**: Keep playing this map while the match is on, Play this map again once it is over;
2. **every map a player selected**, tagged PICKED;
3. **one map of each other game mode that suits the players on** (for Wingman a map with spawn points for them), from that mode's kept maps on the server's
   disk, unless a pick stands for that mode;
4. **the server's own mode to fill the page:** one **fresh** map, unless a card above is fresh already; one **surprise** from the discovery stock,
   new to us; and **favourites**, the kept maps by points. An empty slot - no fresh map on the disk, the stock not downloaded yet - is a favourite.

**Random** is a button of its own: the dice after the vote, in the mode the server is in (Arms Race when no Wingman map has spawn points for the players on; in Wingman only a map that has). While
the vote is open every card keeps its place, Keep first; once it is decided, the cards show with the most votes first. A map a player selects while
the vote is open takes the place of a random card of the server's mode nobody voted for - its very place on the screen, with a number of its own -
favourites first, then the surprise, the fresh card last; the other modes' cards keep their place; chat says which card it replaced ("Bob added
Lake to the vote in place of Poseidon"). The window of a player who added it from the map browser goes back to the ballot, on the page of that
card. A map a player put on the ballot that has no votes left leaves it, until somebody puts it on again: the random card it replaced comes back in
its place, with its own number, and whoever put it on is told. A map selected that is on the ballot already only gets that player's vote. Every card but a
player's pick is on the server's disk, so it loads at once. A card whose map has become unplayable meanwhile leaves the ballot (a pick gives the card
it replaced its place back); a winner that became unplayable hands over to the dice.

### The automatic changes

The dice change the map by themselves only when nobody can vote: at a server start with nobody on (with players on a vote opens for everybody instead, and nobody voting rolls the dice), on the empty server's way back to Arms
Race, when the map that is on has just gone from the Workshop, when it has just crashed the players' games, and at a match end when no plugin draws the HUD or
[Steam is not showing it](#the-huds-version-and-the-hud-check), because a vote freezes everybody and not everybody would see the ballot. Without the bridge a finished match replays the same map: the Arms Race cfg sets
`mp_match_end_restart 1` and `mp_match_end_changelevel 0`, and the bridge acts on the log's `Game Over:` line.

### The match length and the restarts

Anybody may change the length and press the two restarts, on the Game page or in chat; each says for everybody who did it. The fairness restart is the server's own.

- **The match length** (`!time 10`, `!time 60`, or a bare `!time` for the other one; Arms Race's and Deathmatch's, `bridge/Shared/MatchLength.cs`) applies to the match that is on, not only the next: each of the two modes is one long
  round, so the plugin (`os_matchlength <minutes>`, from the bridge) sets that round's time, counted from its start. A match already past the new length, or with less than a minute of it left, ends **60 s from now**, not at once. The rounds to come read
  `mp_roundtime`. Each mode keeps its own length, and both start at 60 minutes, which each mode's cfg sets at every load (Valve's Deathmatch has 10); the bridge puts a chosen 10 back 2 s
  later - in Deathmatch it puts the length back after every load, by the mode it knows 2 s after the start, also after an operator's switch (since release 197; HUD content v4.10.0 shows the row in Deathmatch, an older content only in Arms Race).
  **Wingman** plays a match of rounds, where a round's time of 10 minutes would only make one round long, so its length is how many rounds (release 199): `!time 8` or `!time 16`
  (a bare `!time` the other one; minutes are refused there), 16 to begin with - Valve's, first to 9, the sides switching after 8 - which its cfg sets at every load; 8 is first to 5,
  switching after 4. The sides switch at half the count, so a new count takes the match that is on only while neither count's halftime has come: fewer rounds played than half
  the shorter (`MatchLength.ThisMatch`; the plugin's `os_matchlength <rounds>` decides, and sets `mp_maxrounds` itself). Otherwise it starts with the next match: the bridge sets
  `mp_maxrounds` when a match ends - a kept map's next match starts without a load -, right before its Restart match, and 2 s after every Wingman load. The Game page shows
  Wingman's row from HUD content v4.11.0 on, its buttons saying 8 and 16 rounds: the contents up to v4.10.0 collapse it there, and their buttons say minutes.
- **Restart the match** (`!rematch`) starts the match that is on over, in any mode: a second click within 30 s confirms, then 10 s count down on the countdown banner with the player's name,
  and the bridge sends `os_restart 1`, the plugin's own restart, which its restart guard lets through also while it holds `mp_restartgame` (`mp_restartgame 1` to a plugin that has no
  `os_restart`; a restart the game refuses is said to everybody). In Arms Race the ladder, the time and catch-up start over; in Deathmatch the time and the scores; in Wingman the rounds and the scores. The map, the
  mode and the fun settings stay. It is refused while the server's restart counts down, a map change counts down or loads, a vote is on or the match has just ended, and for 60 s after
  the last one was pressed; a match end, a map start or the server going down during its countdown ends it.
- **The fairness restart** (`bridge/Shared/EarlyJoin.cs`, the owner's request of 2026-11-09): Arms Race and Deathmatch have no warmup - their cfgs set
  `mp_warmup_online_enabled 0`, so a match is live the moment the first player picks a team - and a player who loads the map more slowly would come into a match that is
  already on, at level 0 in Arms Race. So a player who comes into a match during its first **60 s** - their first spawn on the map, not on a team when it went live: a player of
  the map before who took longer to load, or a new joiner; never a bot - starts it over, **once per map**. The plugin (`EarlyJoinWatch.cs`) waits until every player of the
  map before has spawned (one who leaves for real is not waited for), at most until the 60 s are over, and then asks the bridge, naming the last one in; the bridge takes
  Restart match's path and its refusals - none while a vote is on, a map change counts down or loads, the server's restart counts down, the match has just ended, or within
  60 s of a Restart match - counts **5 s** down on the countdown banner, "The match restarts for fairness - Bob is in", says it in chat and on the board, and sends
  `os_restart 1`. What starts over is Restart match's. Wingman keeps its warmup (15 s) instead, and a player who comes in within 30 s of a round's start still plays it
  (its cfg's `mp_join_grace_time 30`). A plugin loaded during a map does nothing on that map; `os_fair` says where the map stands, and `os_fair live`, the client harness's,
  takes that moment as the match going live, so a lone test game's spawn is a late join.
- **Restart the server** (`!restart`) is for a stuck server: a second click within 30 s confirms, then the same 10 s countdown with the player's name, and the bridge quits the game process
  (a clean quit, [below](#broken-maps)), which the container's restart policy starts again. A vote that is open and a map change counting down end at the second click. The least time
  between two is `bridge.restartCooldownSeconds`.

### The game modes

**Arms Race** is the server's game: a kill earns the next gun, and a kill with the golden knife on the last rung wins the match. It is the default,
after every server restart and after 10 minutes with nobody on. **Deathmatch** and **Wingman** come in through the vote.

Arms Race and Deathmatch have no warmup (`mp_warmup_online_enabled 0`): a match is live the moment the first player picks a team, and [the fairness
restart](#the-match-length-and-the-restarts) starts it over once if somebody comes in during its first minute. Wingman warms up for 15 s, from the first team pick.
Both deathmatch-style modes spawn players at random, each spawn point tested for line of sight to the enemies (`mp_randomspawn_los 1`): the game picks one that no
enemy sees, where the map has one - Valve's Deathmatch cfg sets it, and ours sets it for Arms Race too (Valve's Arms Race cfg does not).

Each mode has Valve's official maps for it (`bridge/Shared/Modes.cs`) and the Workshop maps of its list: `maps.json` for Arms Race (whose official maps are
playable but never rolled), `maps-deathmatch.json` and `maps-wingman.json` for the others. A map loads in its own mode: the bridge tells the plugin
with `os_mode armsrace|deathmatch|wingman` and sets the mode's convars right before the map command, and an operator switches the same way, `os_mode`
first. A `changelevel` from a Workshop map to an official one brings back the mode CS2 saved on the way to the Workshop maps (`host_workshop_map` and a
change from an official map bring back nothing), so such a map can come up in the old mode: it is then loaded once more in its own, by the bridge a few
seconds after the start of a change it made, and by the plugin 8 s after the start of any other (`plugin/OverStrike2/ModePlan.cs`). Bare `os_mode`
says which mode the plugin was told and which one the map loaded in, and changes nothing; after a map start it did not ask for, the bridge asks
that and takes the told mode, and reads `game_type` and `game_mode` only when the plugin cannot say (a plugin older than bare `os_mode`). A map several
modes list plays in the mode the map browser's filter shows it in, else in the mode the server is in when that mode lists it, else in the first mode
that does. Each mode has its own server cfg (`server/gamemode_*_server.cfg`) and its own values in the block list (`plugin/OverStrike2/blocked.txt`).
The grenade drops, catch-up and friendly fire's settings are Arms Race's own, and the menu leaves them out elsewhere; the match length is every mode's, each its own
([above](#the-match-length-and-the-restarts)): minutes in Arms Race and Deathmatch, rounds in Wingman.

### Wingman and its spawn points

Wingman is two teams of two in Valve's matchmaking, but the game takes more: it holds a side at its spawn points ("The Terrorist team is full"), and the
cfgs keep the sides within one of each other (`mp_limitteams 1`), so N players need the larger half of N spawn points a side. A Wingman map takes as many a
side as it has (`Modes.WingmanFits`): the bridge offers it on the ballot, lets it win, rolls it as Random and accepts it when asked for only while the players on
fit; otherwise the line says what it takes ("de_inferno has spawn points for 4 a side in Wingman, and 9 are on - Not this map now"), and a card's Play in the map
browser says it in place of Put to the vote ("Wingman: 2 a side"), greyed (class `unfit`). The ballot's cards are drawn to fit; one that no longer takes the
players present, because more came in during the vote, cannot win, and its Vote says what it takes, greyed. Valve's maps take 4 a side or more - de_inferno,
de_nuke and de_overpass their 2v2 sets of 4, de_poseidon 5, de_debris 9, de_eldorado 10, de_vertigo 15 (read from their entity lumps, 2026-11-09;
`Modes.WingmanOfficialSpawns`), and 3v3 on de_inferno was tried on a test copy the same day: every player spawned. A Workshop map's own count is `spawnsT` and
`spawnsCt` in its entry of `maps-wingman.json`, which the Workshop sync writes and the bridge reads. The sync's scan counts them in the map's entity lumps
(`Spawns.cs`, its `spawns`), the same way: the map's `spawnpoints.2v2` set where it has one, else every enabled team spawn. A listed map scanned before the
scan counted them is read once from its files, the sync's own download or else the game server's copy, while nobody is on. A map not counted - on neither disk
yet, or a lump the sync cannot read (`spawns_error`) - counts as 2 a side, Valve's 2v2. Of the 140 Workshop Wingman maps the server kept on 2026-11-09, 105 had 3 or more a side,
94 had 4 or more, and 35 had fewer than 3 - 9 of them 1v1 maps with one.

## Random picks

A random pick is any map the dice choose: the vote's random cards, its Random, and the [automatic changes](#the-automatic-changes). Never a map a
player selected.

**Points set the odds; each point doubles them** (`Dice.cs`, `weight = 2^points`):

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

**Only what is on the server's disk**, so whichever card wins loads at once (`Stock.cs`). On the disk means: an official map; a **kept map** (the
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

`queues.json` is what the watcher works from, version 2: `ballot`, the Workshop maps on the open ballot, the most votes first, which it downloads before
anything else; and `stock`, a lane per mode under its name, `armsrace`, `deathmatch` and `wingman`. Every reader takes version 2 alone: a file of another
version - an older bridge's, for the seconds of a release before the new bridge writes its own - is nothing to fetch, and the keep set refuses it, so no
map is deleted on it.

## Likes and dislikes

**Like** and **Dislike** mark the map that is on, the arrows on any map's card: one mark per player per map. A mark steps: the opposite one on a marked map
takes that mark back - to none - and says how to cast it ("Your dislike of #12 x is removed - Like it again to add a like"); the next one casts it. The same one
again changes nothing and says so: repeating a mark never takes it back. Every place that shows a mark shows no mark as well. Marks live in `votes.json` in the
bridge's data folder (the file keeps its name; "vote" means the vote for the next map alone); a file that does not parse is moved aside, never overwritten.

**A dislike keeps its reason**, as the mark's value in `votes.json` (`DownReasons`). Any plain Dislike - the board's button, a card's arrow, `!down`,
`!down <number>`, its aliases - on a map that player has not disliked counts at once as the first one, and that player gets a popup listing them for 15 s: **Why the dislike?**, one
small dialog at the centre of the screen that names the map and shows only while the player has a cursor, the window's or TAB's, so a dislike typed in chat with the window closed shows
it once TAB is held. The reasons appear only when a dislike is cast - never for the click that takes a like back:

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
it, and gets it back. After 10 minutes with nobody on they all go back to their defaults, like at every map end, and so do they when the server's plugin is
reloaded, which says so to everybody ("A plugin reload put every fun setting back to normal"). A change is one message for everybody, the same for every setting: the top line,
chat and the TAB board's Lately list say who made it and that it lasts until the map changes. A setting off its default stands out on the page, and its row names who set it.

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
| `!vampire` | Vampire: every hit on an enemy heals 10% of its damage, every kill of one 40 more, up to 150 health | off, on |
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
combination the page would not make. **Default** (`!fun default`) puts every setting of the page back to its default - the bots stay, the [Bots page](#bots) has a Default of
its own - and 10 minutes with nobody on puts back everything, the bots too. Either is one message like a single change: who did it and what changed.

**Every combination works together:** the fun settings touch no convar twice, so any mix of them is simply each one's own values, and `--selftest` walks every combination there
is - 884,736 in Arms Race, 27,648 in Deathmatch and Wingman - checking that each reads back as made, that the page calls idle exactly what does nothing then, and that Random hands
out each allowed one. One combination is refused: **Moon gravity with Super jump High**, a jump about 506 units high, nine times a normal one, out over the walls of most maps. The
page greys the second of the two, and the mouse on it shows the reason beside it ("Super jump cannot be High while Gravity is Moon: together they throw players out of most maps"); a
click on it does nothing. A click made just as the other setting changed, before the grey could be seen, is refused at that button for 8 seconds, saying who changed what ("Cara just
set Gravity to Moon, so Super jump cannot be High"), and in that player's chat. Chat and `os_fun` refuse it with the reason too, and Random keeps super jump normal on moon gravity. The one-hit knife and the Zeus keep working under headshots only.

### Bots

FUN has two sub-tabs: **Settings**, the page above, and **Bots**, where anybody puts bots in the game for everybody (`bridge/Shared/Bots.cs`, the page
`bridge/Shared/HudBots.cs`). There are none unless somebody puts them in: the server's cfgs set `bot_quota 0` at every map load, as they always have. Every row
works as the Fun page's: a change lasts until the map changes, the page says who set it, a plugin reload undoes it and says so, the bridge's reset after
10 minutes with nobody on puts it back (and like a fun setting it outlives the player who set it: with nobody on, CS2 takes the bots out within
seconds, and they come back with the next player while the row still says so), and a player's value is a choice, never a map's - the game-changers block, which keeps `bot_quota` and
`bot_quota_mode` where the server has them, leaves a player's alone. `os_fun` takes the rows too (`os_fun bots 4`, `os_fun botlevel expert`).

| Row | Choices | What |
|---|---|---|
| Bots | Off, 1, 2, 4, 8 | That many bots beside the players (`bot_quota`, `bot_quota_mode normal`) |
| Fill to | Off, 4, 8, 12, 16 | Bots fill the game up to that many players, and make room as players join (`bot_quota_mode fill`) |
| Side | Any, T, CT | Which side every bot joins (`bot_join_team`), with the team-size limit and the team balance off while one is set (`mp_limitteams 0`, `mp_autoteambalance 0`) |
| Difficulty | Normal, Easy, Hard, Expert | How well they aim, react and think (`bot_difficulty`) |
| Weapons | Any, Knives, Pistols, Shotguns, Snipers | What they may fight with (`bot_allow_*`; Knives: none of them) |
| Chatter | Normal, Radio, Quiet | What they say on the radio (`bot_chatter`) |
| Hold fire | Off, On | They never pull the trigger (`bot_dont_shoot`) |
| Crouch | Off, On | They crouch wherever they go (`bot_crouch`) |
| Movement | Normal, Walk, Frozen | They only walk, or stay where they stand (`bot_walk`, `bot_freeze`) |
| Copycats | Off, On | They copy every move of the player who switched it on (`bot_mimic`, that player's entity index) |
| Bot war | Off, On | They fight each other and leave the players alone (`bot_ignore_players`), stray shots included: the plugin refuses a bot's damage to a player |
| Name tag | None, Bot, Zombie | A word before every bot's name (`bot_prefix`) |

Bots and Fill to both set the count, so one of them shows a number at a time. The side, difficulty, the weapon rules and the name tag are what a bot gets
as it joins, so a change of one while bots play has them join again (`bot_kick`, then the count again).

Two rows do more than their bot convar, which alone does not cover all they promise. **Side**: the server's `mp_limitteams 1` lets a team hold one
player more than the other, so with Side T against one player on CT only two bots of four would join T, and `mp_autoteambalance` would move bots across at a
round start; while a side is set both are off, and they come back with it - at Any, Default, the map's end and a reload. A map with too few spawn points
for a side can still hold bots out ("Team is full"). **Bot war**: `bot_ignore_players` covers the bots' sight only - its help text is "Bots will not see
non-bot players" - so a bot never aims at a player, while its shots at another bot, its grenades and its fire are not covered. While it is on, the plugin's
damage hook refuses a bot's damage to a player (`BotGuard.cs`, the rule `Bots.Spares`), as it does for a player the cloak hides ([the spawn
cloak](#in-game-hud)); a player's shots at the bots land as ever.

**Presets** set every bot setting at once - their picks, the rest back to normal: **Zombie horde** (8 expert bots with knives, tagged ZOMBIE, quiet),
**Sniper duel** (1 expert bot with snipers), **Target practice** (8 easy bots, frozen, holding their fire), **Copycats** (4 bots copying the player who
clicked, holding their fire), **Bot war** (8 hard bots fighting each other, tagged BOT) and **Pistol party** (4 bots with pistols). A preset is lit while
the live values show it. **Kick all bots** sends every bot away and puts both counts back to Off; **Default** puts every bot setting back to normal. One
message for everybody says each: a row's change like a fun setting's, a preset's with what it set, Kick all bots and Default with who.

What a mode cannot make is greyed, its reason the row's second line: in **Arms Race** the bots climb the ladder like everybody and get each rung's weapon,
so the Weapons row is greyed there and a preset leaves its weapon rule out (Zombie horde is 8 expert zombies with the ladder's guns) - a bot can win the
match. **Wingman**, with room for 4 players, greys Bots past 3, Fill to past 4 and every preset with more bots than that: Sniper duel alone fits. The Fun
page's Default leaves the bots alone, and the Bots page's leaves the Fun page alone; the map's end, a reload and the bridge's reset put back both.

## Catch-up

In Arms Race a player behind gets a boost against every player ahead of them, more the further ahead that player is, while two players within a kill of each other fight
as in plain Counter-Strike - whatever either's gap to the leader. Every enemy hit looks the gap up between its two players on the ladder at that moment: the one behind hits
harder and takes less from the one ahead. A card under the crosshair says it at every spawn - how many kills behind the leader, and what they get against the players ahead, the
most against the leader - a green row per item for 5 s, with the same line in their chat; a leader's bounty gets the same card in red. A game known to hold a content the plugin
no longer draws for (before v4.8.0) gets CS2's centre panel instead, as does a player who joined while Steam does not hand the HUD out and has not clicked in it; the chat line
comes either way. A game [the window's X](#the-huds-version-and-the-hud-check) has not said anything about yet gets the card. The numbers are `plugin/CatchUp/catchup.json`,
and its self-test holds these two tables and the bounty's sentence below to it; the module is [`plugin/CatchUp/`](../plugin/CatchUp/).

The fight, by the kills between the two players - the one ahead's place on the ladder minus the one behind's:

| Kills between you | Your damage on them | Their damage on you | Lifesteal on them | Also, against them |
|---|---|---|---|---|
| 2 | | -5% | 5% | |
| 3 | +5% | -22% | 5% | |
| 4 | +15% | -37% | 5% | a second chance once per life |
| 5 | +20% | -50% | 10% | the same |
| 6 | +25% | -60% | 20% | the same |
| 7 | +35% | -67% | 25% | a second chance, a one-hit knife and Zeus |
| 8 and more | +40% | -73% | 35% | the same |

Nobody's health changes: what earlier versions gave as extra health is part of the damage the player ahead does, beside the damage taken (+120% health and -40% damage
taken are 27% of the damage, -73%), since a pool of health could not tell one attacker from the next. Two players who are both far behind the leader but close to each
other - teammates of a runaway leader and the other team, often - fight with normal damage, no lifesteal and no second chance; until plugin 5.8.0 two players 8 or more behind needed 2.6 times the damage
to kill each other, and more with the second chance and lifesteal.

Each player's own, by their kills behind the leader, whoever they fight:

| Kills behind the leader | Regeneration | Also |
|---|---|---|
| 2-3 | 3% a second, after 4 s unhurt | |
| 4-6 | 5% a second, after 3 s | 2 s more spawn protection |
| 7 and more | 8% a second, after 2 s | 3 s more spawn protection, infinite ammo |

Regeneration and lifesteal heal up to the health the life spawned with, never past it. These three are decided again a frame after every change to the ladder - a kill, a team
kill, a demotion, a player leaving - silently, and the fight needs no deciding: each hit looks its own gap up, so a boost shrinks as soon as a gap closes and grows when the other
pulls away. A leader 2 kills or more ahead of second place takes more damage from enemies: 10% for every 2 kills of lead, up to 30%.

The module counts the ladder from the match's kills, since the game exposes no ladder position, by CS2's own rules as real matches show them: a team kill under friendly fire takes the
killer down a level, and every kill goes to the plugin's log as `CatchUp: kill:` with both teams and both counts after it. Its self-test replays real match logs against them. A gun
kill on a rung the counter did not expect places the player on that rung with the count nearest the one they had, so a count a kill off (a kill the log never shows) is right again from
that kill on. Every life's end goes to the log as `CatchUp: life:` - every enemy's hits on it, how many to the head, the damage they did to health and armour (a killing hit's in full) and catch-up's
multiplier on them, its own shots, its heals and how it ended - so a fight can be checked afterwards. `!catchup off` switches it off for everybody until the map changes; `os_catchup` is the
console's word.

## Broken maps

Thousands of community maps include some that break things, so every broken map says why, on its card, before anybody loads it.

- **Crashes.** A few seconds after every map start the bridge reads RCON `status`; its `spawn` counter tells whether the server process is new. A new process that did not quit cleanly is a
  crash, counted against the map it was loading. A map with a crash asks the player for a second click, and the dice skip it.
- **A clean quit** is one the bridge was told of: the restart watcher's notice right before its planned quit (`POST /quitting/<secret>`; a cancel takes it back when the quit could not be
  delivered after all), the bridge's own Restart the server as it sends its quit, or the echo of an RCON `quit` or `_restart` in the log stream. The echo alone was not enough: the engine segfaults on
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
  The mode's `game_type` and `game_mode` are put back without a word to the players when no map changed them: after an `os_mode`, and when CS2 brings
  back the mode it saved before the Workshop maps with a map change.
- **Only our own code restarts the game** (`plugin/OverStrike2/RestartGuard.cs`). AWP Lego 2 (3817962966) sets `mp_restartgame 1` from a logic_auto's OnMapSpawn,
  which fires again after every restart: on 2026-11-08 the match restarted every 3 s from the first team join until the server was quit. A map's `mp_restartgame`
  goes back to 0 from the server's frame - before the world updates, before the entities think, and at the tick - never inside the convar's own change callback,
  which only notes the set (a write there crashed the engine, twice, in release 195's rehearsal). A set the game takes before any of those looks escapes: its one
  restart goes through, and `mp_restartgame` is held until the map ends - flagged a cheat setting, which the game refuses to the map's next set. Four round starts
  each within 5 s of the one before that we did not cause are a restart loop: logged, put back and held the same way. What goes through: the bridge's Restart match,
  once, around the moment its countdown ends (the live state names it; not while the convar is held), and `os_restart [seconds]`, the console's and the tools'
  restart (the docs' harness uses it; `os_restart hold` and `free` hold and free the convar by hand). `os_settings` says what was refused, escaped and held.

## The HUD's version and the HUD check

A game keeps the HUD content it last downloaded, and Steam takes up to about an hour to update it - at the player's next start of CS2. The plugin draws for content v4.8.0 and newer
(`HudVersion.Oldest`): every panel's id begins `os_` and every class `os-`, and each goes out by that one name. The contents shipped before v4.8.0 have theirs baked in with `gg_` and `gg-`;
since release 193 nothing goes out under those names, so a game still holding one gets no live HUD at all - no window, no board, no banner - until Steam has updated it, and a click from
one names a `gg_` button, which is none of ours. Such a game has the HUD check's chat lines and CS2's own alert, which tell its player to quit CS2 and let Steam update the HUD.
`hud/reference/` keeps the one shipped layout `--selftest` reads, the one this content replaced, and the self-test holds the new one to it panel by panel.
The HUD itself says when a player's game holds an older or a newer content than the server's, by comparing
its own level with the server's - a number each release takes in turn, whatever its version says - and an older game is told how to update: quit CS2 and start it again, and Steam updates it. An older game's
banner stays and names both versions; a newer game's shows for 15 s at the join.

**The HUD check.** MultiAddonManager makes everybody download the addon, but whether the layout loaded the server cannot see. The only proof is a click, and it comes from [the welcome](#in-game-hud): until the
server knows which content a game holds, the window's X is the version button, its id naming the content, with "Close this to play" beside it. Its click proves the game shows the HUD, says which content it holds
(SERVER, Players) and closes the window. Any other button proves the HUD without saying which. A player not
seen clicking since their last real join gets the window at their first spawn on a map, and has 90 s from that spawn. 15 s before the end one who has not clicked gets CS2's alert text and the steps in chat. With
`hud.check` `warn`, the default, that is all; `enforce` disconnects them at the end; `off` asks nothing. With `warn` and with the check off, the end of the 90 s also closes that player's window - the
welcome too, which otherwise waits for its X - so the [spawn cloak](#in-game-hud) cannot hide a game that cannot draw the window for longer than that (`HudGate.ClosesWindow`); a player who has not
clicked by then sees the window close. A verification lives in memory, per connection: a reconnecting player may have a new HUD. No fresh word from the bridge for `HudCheck.StaleSeconds` means off
(no state, no disconnects) - and the plugin asks the bridge only while a human is on, so the check reads off whenever the server is empty and its mode again from the first answer after a join; the
clock's grace runs on through such a switch, and the step back up re-arms everybody still waiting with a full grace, at most once a map.

**The item watch.** While Steam does not show the HUD's Workshop item to everybody - Valve reviews every new version, and a held item is an error page for everybody but its owner - the server must not make
joiners download it, and nobody is disconnected. The plugin reads the Web API every minute, which says whether the item is gone, private or banned and when a new version went up, but not whether Valve
holds it. So the item's page is visited right after a change, then at most every 15 minutes while it is not shown, for 3 hours (`ItemPlan`, `hud.itemPage`). A verdict of not shown takes the item off the
addons joiners must download, and that is **a hold**: the HUD check goes off, and the status banner says for 45 s that new players join without the menu, that everybody loses it at the next map change and that the game plays on. Nobody can
call a vote during a hold, because it would freeze players with no ballot to see, and a match end rolls the dice. A player without the HUD has no mouse taken and gets one chat line saying why. The visit that finds the item shown puts it back: a green
banner for 30 s says everybody gets the menu again at the next map change, or now by reconnecting, and those without it read the same in chat.

## Maintenance flag

While the file `maintenance` exists in the bridge's data folder, the bridge makes no automatic map change: no start roll, no match-end vote, no way back to Arms Race, and a vote nobody called or nobody voted
in changes nothing. A vote a player called still decides. No crash is recorded, and the sync waits. Remove the file and everything resumes. `os2-deploy release live` sets it around its quit; a restart by hand should
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

`os2-sync serve`, in a container of its own: the game server's image, for its SteamCMD and the libraries a test engine needs ([commands and files](server.md#the-workshop-sync-os2-sync)). It has no time windows: a run
starts whenever the server has had nobody on for `sync.quietMinutes` (10) and there is work, and lasts at most `sync.runHours` (3). A start of the service counts as somebody on, since it cannot know who just left. No run starts from 15 minutes before the daily restart to 10 after it, or with under 20 minutes
left before that cut. Discovery, the check and the ratings run at most every `sync.apiEveryHours` (4); downloads, tests and publishing repeat in batches of `sync.batch`, so a long run publishes as it goes.

1. **Find** new maps with the Workshop's tags (Arms Race, Deathmatch, Wingman) through the Steam Web API. It needs a Web API key (`sync.webApiKey`); without one the step is skipped.
2. **Check** every listed map (gone, hidden, banned, updated) and hand the verdicts to the bridge. Once a day also fetch the Workshop's score and subscriptions of every listed map and every waiting one, and each mode's Most Popular
   order, into `ratings.json`. Write each listed map's Workshop numbers and the version the sync accepts into `workshop.json`.
3. **Hand over** the maps the server keeps but lacks, and the ones it stores in an older version than one a test passed (`<work>/handover/`). A hand-over waits for SteamCMD's acf entry of its download, and a map already staged with one
   goes without a new download.
4. **Download** with SteamCMD's anonymous login, one item at a time, through everything that is queued: the maps whose score would make one of their mode's three lists first, then new uploads, listed maps that changed, and everything
   else strictly by subscribers, whatever the mode. Nothing is skipped for its size. A tested map the server keeps is handed over instead of deleted.
5. **Scan and load-test:** read each download's packages (a map, a model pack, an empty upload, an old CS:GO upload; and any commands that advertise another server or post a gambling ad, in its
   entity lumps - every KV3 format CS2's maps use: v5 and v4 with LZ4, and Zstandard - its cfgs and its map scripts, cs_script, since release 197; and its team spawn points, which
   Wingman's list carries), then load each map in a test copy of the server
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
pages each list per player and passes the rest through (`bridge/Shared/HudDash.cs`), so a change to the page's words or rows needs a bridge alone. And a website: `index.html` and `dashboard.json` in `web/` in the bridge's data folder, rewritten when they change and
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
| `logs/bridge-<YYYY-MM-DD>.log` | the bridge | Its own log, a file a day, every line with its date and time, the last 14 days kept (`LogFile.cs`): the console's log goes with the bridge's container at every swap, these stay. GET /health names today's file, or why it is not written |
| `maps.json`, `maps-deathmatch.json`, `maps-wingman.json`, `broken.json`, `search.json` | the sync | The lists. The repository's are the starting lists, which the installer and `os2-deploy data` put only on a server that has none yet |
| `ratings.json`, `workshop.json`, `sync-status.json` | the sync | The Workshop's numbers; the sync's state |
| `ours.json`, `sizes.json`, `blocked.json` | by hand: this repository | Our own maps; each map's measured walking area and size class; the maps blocked for good |

The bridge reads these again about 10 s after they change, so a new list is a file copy and nothing restarts; `POST /reload/<secret>` reads them at once. A file that does not parse changes nothing, nor does a list cut by more than half. What a reload does at
once: the menu's map list follows, a card or a countdown for a map that has just become unplayable goes, and a map that is on and has just gone from the Workshop makes way for the dice.

`maps.json`'s entries are `rank`, `id`, `name`, `sizeMb` and the like, append-only: a number, once given, keeps its map. No Workshop description enters this repository: `search.json` here has titles, ids and tags only, and the bridge's `--search-index` (`bridge/SearchIndexFile.cs`) keeps it in step
with `maps.json`.

## Configuration

Every part reads one settings file: its text inline, in the environment variable `OVERSTRIKE2_SETTINGS_CONTENT`, or the file `/etc/overstrike2/overstrike2.jsonc` ([how it gets into the containers](settings.md#how-the-file-gets-into-the-containers)). [settings.md](settings.md) has every key, its default and the parts that read it, and the game image's own variables. `hud.addon` is required: without a usable one the game server does not start ([server.md](server.md#how-it-starts)). No part runs without its
settings or on settings it cannot use, and the variables they replaced stand in for nothing; the bridge and the plugin log at their start where theirs came from, never a value. [`deploy/overstrike2.example.jsonc`](../deploy/overstrike2.example.jsonc) is a complete example; [`deploy/compose.yml`](../deploy/compose.yml) mounts the file and
[`deploy/compose.example.yml`](../deploy/compose.example.yml) has the text inline.

## The plugin layer

Before every engine start `os2-server install` puts in the pinned Metamod, CounterStrikeSharp (with its .NET runtime) and MultiAddonManager once per version, re-applies the `gameinfo.gi` search path (every game update rewrites that file), has MultiAddonManager
mount the HUD's Workshop item (`hud.addon`, [server.md](server.md#the-hud-addon)), and copies every plugin folder in. Only settings with no usable `hud.addon`, or no MultiAddonManager on disk after its download was tried, stop the launch ([server.md](server.md#how-it-starts)): every other failure is a logged warning and a server without that component. With `plugin.enabled` false it only removes the search path, and the server runs vanilla - the
switch for the days after a game update breaks Metamod or CounterStrikeSharp. The bridge goes on rolling maps then. To bump a pin: change the version in `server/OverStrike2.Server/Install.cs`, build the plugin against the same CounterStrikeSharp version, and ship both.

## Log-line grammar

The parser is one method (`Parse` in `bridge/LogStream.cs`) and `--selftest` shows exactly what it accepts. The game server pushes its log with `logaddress_add_http`: a `POST` per tick, one or more lines, each prefixed `MM/DD/YYYY - HH:MM:SS.mmm - `.

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
| `dotnet run -- --selftest` in `bridge/` | Hundreds of checks on a handed clock, with no server: the log parser, the votes, the ballot, the dice, the stock, the tags, the HUD's words and wiring, the frame the plugin draws (HudFrame, against a recorded screen), every panel id and class against the layout, the older HUD contents against the panels sent to them, the rules below, and the chat table above |
| `plugin/OverStrike2.SelfTest` | The freeze, the spawn cloak, where the plugin's settings come from, the block file, the Web API's one door |
| `plugin/CatchUp/SelfTest` | The catch-up rules, replayed against real matches' logs |
| `core/SelfTest` | The settings file and what the parts share; it holds [settings.md](settings.md)'s tables to the code (`-- --write` makes them) |
| `server/OverStrike2.Server/SelfTest`, `server/OverStrike2.Sync/SelfTest` | The two programs, their Linux-only checks in the game image ([server.md](server.md#build-and-test)) |
| `dotnet run -- --search-index --check` in `bridge/` | The search text is in step with the map list |

**A real client.** A layout cannot be fully tested without the game, so a new HUD is also tried in a real CS2 client before a release: the harness in [tools/client/](../tools/client/README.md) starts the client of whoever works at that PC with no window on
screen, no sound and no focus, joins a test copy of the server, loads the local HUD build there with VAC off, takes screenshots, and drives the menu through server-side clicks.
On the live server only the published HUD ever runs, in a plain client. It reads the server from the private config, as the deploy scripts do, and takes the
documentation's screenshots by script ([tools/client/docshots/](../tools/client/docshots/), `os2-deploy docshots`); its offline tests are in AGENTS.md's list.

**The bridge alone.** Native AOT does not cross-compile between operating systems, so the image's build stage is the Linux publish: `docker build -t localhost/overstrike2-bridge -f bridge/Dockerfile .` from the repository root. To run the bridge on Windows against a local
server: start the server with `+log on +logaddress_add_http "http://127.0.0.1:8080/log/dev"`, write a settings file with `server.host` 127.0.0.1, `server.rconPassword`, `bridge.secret` dev and
`paths.bridgeData` (a folder holding the data files, written from the drive's root: `/os2-data`), set `OVERSTRIKE2_SETTINGS` to it, and `dotnet run`. The self-test reads the plugin's and the HUD's files from beside `bridge/`; set
`OVERSTRIKE2_COMPOSE_FILE` to your compose file to have it checked as well.

### Keeping the rules in sync

The rules are said in the menu: its How it works and Help pages and the start page. The bridge's `--selftest` holds them to the code: every number those words state must be one of the code's constants, so a rule change fails the selftest until the code and the menu's words
(`bridge/Shared/Hud.cs`) agree again. It holds the chat table on this page to `ChatCommands.All` the same way.

## Files

| Path | What |
|---|---|
| `bridge/*.cs` | The bridge. |
| `bridge/Shared/` | What the plugin compiles in too, one type per file - the words, the message catalogue, the vote's and the modes' tables, the HUD's logic; the plugin's project takes the whole folder (`plugin/OverStrike2/OverStrike2.csproj`). |
| `bridge/Tests/` | The bridge's `--selftest`. |
| `bridge/Shared/Messages.cs` | The [message catalogue](#in-game-hud): every message a player can get, one line each. |
| `bridge/maps.json`, `broken.json`, `blocked.json`, `sizes.json`, `search.json`, `ours.json` | The [data files](#the-data-files) the repository starts from. |
| `bridge/dashboard.html` | The website's page, built into the bridge. |
| `plugin/OverStrike2/` | The plugin; `blocked.txt` the block list, `convars.txt` the floor of game convars the plugin watches; at every start it adds what the running build has beyond it (`ConVarNames.cs`). |
| `plugin/CatchUp/` | The catch-up boosts and their self-test; the plugin compiles them in. |
| `core/` | The library the parts share, and its self-test. |
| `hud/` | The HUD addon ([`hud/PUBLISH.md`](../hud/PUBLISH.md)); `reference/` the shipped layouts the self-test reads; `thumbs.py` makes the map pictures, which are never committed. |
| `maps/` | Our own maps' generators and their Workshop publisher ([`maps/README.md`](../maps/README.md)). |
| `server/` | The two programs, with the launcher `pre.sh` and their self-tests, and each game mode's cfg. |
| `deploy/` | A host that builds everything itself (`compose.yml`), and building and shipping from a workstation, with a private config. |
| `tools/scan/` | The scanner the hooks run. |
| `tools/workshop/` | What the HUD's and the maps' Workshop scripts share: `FindCs2.ps1` finds CS2 on this PC, `Workshop.cs` holds the Steam calls (no create path) and the VPK both publishers compile in. |
| `tools/client/` | The CS2 client test harness, the insecure test copy of the server and a candidate bridge beside it, and the documentation's screenshots ([README](../tools/client/README.md)). |
