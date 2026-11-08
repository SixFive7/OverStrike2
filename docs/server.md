# The server program

`os2-server` ([`server/OverStrike2.Server/`](../server/OverStrike2.Server/)) is the game server's own program: one self-contained
Linux file that runs in the game server's container beside the engine: the install step and the restart watcher. The Workshop sync
has a program of its own, `os2-sync` ([below](#the-workshop-sync-os2-sync)). It reads the settings file ([settings.md](settings.md))
and the image's own variables where the image needs them.

| Command | What it does |
|---|---|
| `os2-server install --from <file>` | Before every engine start: the plugin layer (Metamod, CounterStrikeSharp and MultiAddonManager at their pinned versions, the `gameinfo.gi` search path, the plugins copied in), the HUD addon's delivery (below), the rotation of an overgrown `console.log`, the maps folder, and a record of what it put in place. `<file>` is the program as shipped, which the watcher watches for a new version. |
| `os2-server organize` | The maps folder, the hand-over and the deletion (below). |
| `os2-server watch` | The restart watcher, for the life of the container. |
| `os2-server doctor` | What the program sees: the settings as read, the paths, the installed version and the one on disk, the watcher's counters. |
| `os2-server version` | Its version. |

Every command takes `--settings <file>` (else `OVERSTRIKE2_SETTINGS`, else the file's text in `OVERSTRIKE2_SETTINGS_CONTENT`, else `/etc/overstrike2/overstrike2.jsonc`).

## How it starts

The image's entry script sources `pre.sh` right before it launches the engine. `pre.sh`
([`server/OverStrike2.Server/pre.sh`](../server/OverStrike2.Server/pre.sh)) is a launcher: it copies the program from where it is
shipped (`OS2_SERVER`, else `/home/steam/overstrike2/os2-server`; a read-only or no-exec mount cannot run it) to
`<game>/update-watch/os2-server`, runs `install`, and starts `watch` in the background. A failure costs the plugin layer and the
watcher, never the server, with two exceptions, because a server without the HUD cannot exist: `install` refuses the settings (no
usable `hud.addon`, or none it can use; [settings.md](settings.md#how-each-part-reads-it)), and it refuses to go on when MultiAddonManager
is not on disk after its download was tried, so a new volume's first start needs GitHub. Either way it logs one `ALERT` line and exits
with code 78, and on exactly that code `pre.sh` waits 5 minutes and ends the image's entry script. The engine does not start, Docker's
restart policy tries again, and the log says why. The wait is there because every try begins with the image's anonymous SteamCMD login,
and Docker restarts at once a container that ran for 10 s. `watch` and `organize` refuse settings the same way; `doctor` and `version`
still work and say so. `plugin.enabled` false, the emergency switch, asks for no MultiAddonManager. A download that stalls ends after
60 s without a byte, or 10 minutes in all, so it never holds the start for long.

## The HUD addon

`install` loads MultiAddonManager and has the server mount the HUD's Workshop item, `hud.addon` (`mm_extra_addons`). Every map
change then carries it to every client - a changelevel to an official map included, which dropped a client-only addon
(`mm_client_extra_addons`) every time - at the cost of one reconnect per change (an official map change took 5-6 s) and about 5 MiB of
the server's memory. The server's copy is not downloaded again at a mount (`mm_addon_mount_download 0`): clients use their own Steam
copy, which works even while the server's copy is one release behind. A client that has the addon is not sent it again within the
server process (`mm_cache_clients_with_addons 1`). `hud.addon` is required: the program does not run without it (above).

## When the server restarts

| Reason | With players on | With nobody on |
|---|---|---|
| **A CS2 update** (`restarts.updates`): Valve's UpToDateCheck calls the installed build stale | At the end of the current match | At once |
| **A new version of our own parts** (`restarts.newVersions`, below) | At the end of the current match | At once |
| **The daily restart** (`restarts.daily`, local time) | Waits for an empty server, up to 2 hours after its time; never a kick, never a match end | At once |
| **Stuck Workshop downloads** (`restarts.downloadRestart`): the engine's Steam client has no download source left | Waits for an empty server | At once |
| **A frozen engine** (`restarts.freezeRecovery`) | Killed after `restarts.freezeGraceSeconds` of silence | The same |

**What the players see.** A restart that waits for the end of the match is announced to the bridge when it becomes due, and again
every 5 minutes while it waits; the bridge shows it on the HUD. At the end of the match the bridge counts down 10 seconds, holds its
own map change, and the quit follows. A CS2 update tells the players to restart CS2 to update; our own new version says when the
server is back. After the restart, once the engine answers, the bridge hears what changed and says it on the top line for 60 s. A bridge that does
not take these notices (down, or one that does not know them yet) means nobody is warned: the watcher has no words of its own for players,
and without the bridge the menu is dead anyway. The daily restart and the download restart happen only with
nobody on and say nothing to players. A new version of the HUD needs no restart (the plugin's banner).

**Every planned restart** quits the engine over RCON, so the image runs SteamCMD again; the bridge hears of it right before
(`/quitting`) and takes it as a clean shutdown. A planned restart inside the daily restart's window counts as that day's.

**Loop protection** (`update-watch/state.json`, schema 3; the older schema 2 is read as it is): planned restarts at least
10 minutes apart and at most 4 in any 24 hours; an update restart that left the build unchanged is a miss, retried after 30 minutes
and then 2 hours, and the third latches the update breaker for that build; a version restart that did not install the new version is
not repeated for that version. Freeze kills at least 3 minutes apart and at most 10 in 24 hours; two kills after which the server
never came back latch the freeze breaker. A damaged state file, an unwritable state folder or no RCON password disable every restart.

## A new version of our own parts

What `install` put in place is recorded in `update-watch/installed.json`; the watcher compares what is on disk with it every minute.
Five parts, each a SHA-256 of what it is made of:

- **plugin**: every file of the plugins' folder (`paths.plugins`) and its path;
- **server**: the program as shipped (`install --from`);
- **pre.sh**: `<game>/pre.sh`;
- **cfgs**: `game/csgo/cfg/*_server.cfg`, the operator's mode settings the game runs after its own;
- **settings**: the settings file's values the engine side reads - every key the plugin reads, and `plugin.enabled`. A key only the
  bridge or the sync reads restarts nothing.

A difference is a new version once what is on disk has been still for 2 minutes: a deploy in progress is not one. A file touched
but not changed is the same version. `os2-server doctor` shows both versions and which parts differ.

After the restart a new cfg brings, RCON reads what the cfgs set, with one exception to expect: `mp_warmuptime` reads 5 on Arms Race
and Deathmatch, whose cfgs set 1 - the engine raises it to its floor of 5 s (seen after release 188's restart, 2026-11-07). Wingman's
15 stands.

## The bridge's side

The watcher posts plain text to the bridge at `<bridge.url>/<path>/<bridge.secret>` (else the address in the image's
`CS2_LOG_HTTP_URL`). An answer that starts with `ok` means taken. Every path and body here comes from one file both ends compile,
[`core/BridgeWire.cs`](../core/BridgeWire.cs), and core's self-test holds them to the words below.

| Path | Body | When |
|---|---|---|
| `/restart-notice/` | `os2-restart\t1\t<kind>\t<when>\t<seconds>` | `kind`: `game-update`, `our-update`, `daily`, `downloads`. `when`: `match-end` (the restart waits for the end of the current match; `seconds` is the countdown that will follow it, 10) or `now` (the quit follows in `seconds`: 10 at a match end, 0 with nobody on). The match-end notice comes again every 5 minutes while the restart waits. |
| `/restart-notice/` | `os2-restart\t1\tcancel\t<kind>` | The announced restart is off: no longer due, or its quit could not be delivered. |
| `/quitting/` | `os2-quit\t1\t<kind>`, `os2-quit\t1\tcancel\t<kind>` | Right before the quit (`kind`: `update`, `version`, `daily`, `workshop`); the cancel when the quit could not be delivered. A freeze kill sends nothing: it is the crash it looks like. |
| `/updated/` | `os2-updated\t1\t<what>\t<from>\t<to>` | After a planned restart, once the engine answers: `game-update` with the CS2 builds, `our-update` with the versions of our parts. Tried again every minute, up to 10 times. |
| `/ready/` | `os2-ready\t1`, then one Workshop id a line; while a background download runs, a last line `current\t<id>\t<percent or ->\t<title>` | The maps `queues.json` names that are on the server's disk, at least every 2 minutes and whenever that or the download's progress changes. An answer that ends in `; hold: <why>` holds the next background download. |

## Downloads a player waits for

Steam runs one Workshop download at a time, and one that is running cannot be stopped. When a player picks the map the watcher is
downloading in the background, it becomes the player's download - no second one - and the HUD's top line shows its progress.
When a player picks another map while one downloads in the background, the change waits for it, and the line says so ("Server is finishing
the background download of X first", with its progress), then shows the chosen map's own progress. No background download starts while a
download somebody waits for runs or while the bridge says hold. The progress is the engine's own for a download it runs, and
MultiAddonManager's for a background one: exact, where the lists' sizes are often far off (a map listed at 763 MB downloaded 63 MB).
Steam's client often stalls near the end of a download for up to a minute; the line then stays at its last percent.

## The maps folder, the hand-over and the deletion

Before every engine start `install` (or `os2-server organize`) keeps each Workshop map in a real folder per type -
`maps/<armsrace|deathmatch|wingman|own|hud|other>/<id>-<name>/` - with Steam's `content/730/<id>` a relative link to it, takes in the
Workshop sync's complete hand-overs, and with `maps.delete` on deletes every stored map outside the keep set
([what the server keeps](reference.md#what-the-server-keeps)): never our own maps, the HUD or a map `queues.json` names, and never when
the keep set names a problem. Each run writes `update-watch/keep.json` and
`update-watch/deleted-<time>-<done, dryrun or refused>.tsv`.

`maps.preview` is for a rehearsal on production's volume: whatever `maps.layout` says, organize changes nothing - no map moved, linked
or renamed, no hand-over taken in, no `INDEX.tsv`, no acf and no acf backup written, nothing deleted - and writes only what a start with
`maps.layout` folders and the rest of the settings would do: `keep-preview.json` (the keep set, what it would do with `maps.delete` as set,
the hand-overs it would take in) and `deleted-<time>-preview.tsv` (the maps it would delete, as a start's own list would name them), into
`paths.preview`, else `update-watch/preview/`. Set `maps.layout` off beside it: a build that predates the key ignores it as unknown and
then leaves the maps alone.

## Build and test

```sh
dotnet build server/OverStrike2.Server
dotnet run --project server/OverStrike2.Server/SelfTest      # links and /proc need Linux: SKIP elsewhere
dotnet publish server/OverStrike2.Server -c Release -r linux-x64 -o <folder>          # the one file to ship
dotnet publish server/OverStrike2.Server/SelfTest -c Release -r linux-x64 -o <folder>   # the self-test, to run in the game image
```

The self-test drives the watcher's own loop on a fake clock with a fake engine and a fake bridge: its rules, the restarts above,
both download collisions, the install.

`os2-deploy server build` publishes both programs (this one and [os2-sync](#the-workshop-sync-os2-sync)) and runs both
self-tests, with `--image` in the game image too; `ship --apply` puts `os2-server`, `os2-sync` and `pre.sh` in the server's program
folder, which the containers mount read-only at `/home/steam/overstrike2` ([deploy/README.md](../deploy/README.md)).

## The Workshop sync (os2-sync)

`os2-sync` ([`server/OverStrike2.Sync/`](../server/OverStrike2.Sync/)) is the Workshop sync as one self-contained Linux file, run in
a container of its own (the game server's image: its SteamCMD, the libraries a test engine needs). Its steps are discovery, the check,
the ratings, the hand-over to the game server, downloads, the scan, the load tests and the publish, with their limits, stops and mail
([the Workshop sync](reference.md#the-workshop-sync)). Its files - the state, the Web API ledger, the reports, the hand-overs, the
bridge's files - keep their keys and the shapes of their values, so every reader takes them as it always has, and the bridge's lists keep
their layout (a map a line), so a diff of one shows the maps that changed. It reads the
settings file (`sync.*`, `paths.*`, `server.*`, `bridge.*`, `hud.addon`, `restarts.daily`, `steam.apiGapSeconds`); HOME is the
image's own. Every Steam Web API call goes through the program's one door (one at a time, `steam.apiGapSeconds` apart, also from one
run to the next) and is booked in the ledger before it is sent. Its keep set is the restart watcher's: the same code, with the queues
as long as `bridge.discoveryStock`.

Besides the map lists, it writes the bridge's `workshop.json` (version 1) after the check and after every publish: every listed map
with the Workshop's numbers as the check saw them (`created`, `updated`, `content`, `subs`, `lifetime`, `favs`, `lifetimeFavs`, `views`)
and `passed`, the `time_updated` of the content the sync accepts - the version its last passing test was of; our own maps' newest. The
bridge's discovery stock takes only maps whose current content passed (`passed` at least `updated`).

| Command | What it does |
|---|---|
| `os2-sync serve` | The service: a run whenever the game server has had nobody on for `sync.quietMinutes` and there is work, never around the daily restart; after a run nothing stopped and that had nothing it could do, it waits for the next check or a new day; `<state>/run-now` starts a manual run at once. |
| `os2-sync once [JSON]` | One run now, then exit. JSON lowers its limits: `api_run_cap`, `max_pages`, `batch`; `publish` false; `discover` false; `api` true or false; `only_ids`. |
| `os2-sync status` | The Web API ledger's counters and the state's totals. |
| `os2-sync export` | What it has published, as entries a republish from the repository can merge, each with the sync's verdict on its map (`ws`: ok, removed, hidden or banned - the last check's, as the bridge keeps it in `removed.json`), and the discovery stock as `queues.json` holds it (`queues`), which the HUD's pictures take first. The pictures skip a map whose verdict is not ok. |
| `os2-sync doctor` | What it sees: the settings as read, the paths, whether there is a key, the bridge, the mail. |
| `os2-sync version` | Its version. |

Every command takes `--settings <file>`.

```sh
dotnet build server/OverStrike2.Sync
dotnet run --project server/OverStrike2.Sync/SelfTest       # process groups, /proc, links, real processes: SKIP elsewhere
dotnet publish server/OverStrike2.Sync -c Release -r linux-x64 -o <folder>          # the one file to ship
dotnet publish server/OverStrike2.Sync/SelfTest -c Release -r linux-x64 -o <folder>   # the self-test, to run in the game image
```

The self-test holds the sync's rules and the settings file, the JSON it writes (read back as written, the lists' layout) and its words, the program's one Web API door, the
command line, the SMTP client, SteamCMD's driver against a stand-in, the test tree, and a hand-over taken in by the restart watcher's
own code.
