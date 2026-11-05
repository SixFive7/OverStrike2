# The server program

`os2-server` ([`server/OverStrike2.Server/`](../server/OverStrike2.Server/)) is the game server's own program: one self-contained
Linux file that runs in the game server's container beside the engine. It takes over from the restart watcher
(`server/update-watch.py`) and the install hook's shell work (`server/pre.sh`); the Workshop sync has a program of its own,
`os2-sync` ([below](#the-workshop-sync-os2-sync)). It reads the settings file ([settings.md](settings.md)) and the image's own
variables where the image needs them.

| Command | What it does |
|---|---|
| `os2-server install --from <file>` | Before every engine start: the plugin layer (Metamod, CounterStrikeSharp and MultiAddonManager at their pinned versions, the `gameinfo.gi` search path, the plugins copied in), the HUD addon's delivery (below), the rotation of an overgrown `console.log`, the maps folder, and a record of what it put in place. `<file>` is the program as shipped, which the watcher watches for a new version. |
| `os2-server organize [--dry-run]` | The maps folder, the hand-over and the deletion (below). `--dry-run` changes nothing and writes its list and `keep.json` into its own state folder; a download not yet moved into its type's folder counts as the real run would count it. |
| `os2-server watch [--observe]` | The restart watcher, for the life of the container. `--observe` watches beside the acting watcher and does nothing: every action is a log line saying what it would have done, and its counters live in a state folder of its own. |
| `os2-server doctor` | What the program sees: the settings as read, the paths, the installed version and the one on disk, the watcher's counters. |
| `os2-server version` | Its version. |

Every command takes `--settings <file>` (else `OVERSTRIKE2_SETTINGS`, else `/etc/overstrike2/overstrike2.jsonc`); `watch --observe` and
`organize --dry-run` take `--state <folder>` (else `<paths.gameDir>/update-watch/observe`).

## How it starts

The image's entry script sources `pre.sh` right before it launches the engine. The new `pre.sh`
([`server/OverStrike2.Server/pre.sh`](../server/OverStrike2.Server/pre.sh)) is a launcher: it copies the program from where it is
shipped (`OS2_SERVER`, else `/home/steam/overstrike2/os2-server`; a read-only or no-exec mount cannot run it) to
`<game>/update-watch/os2-server`, runs `install`, and starts `watch` in the background. A failure costs the plugin layer and the
watcher, never the server.

## The HUD addon

With `hud.addon` set, `install` loads MultiAddonManager and has the server mount the HUD's Workshop item (`mm_extra_addons`). Every map
change then carries it to every client - a changelevel to an official map included, which dropped a client-only addon
(`mm_client_extra_addons`) every time - at the cost of one reconnect per change (an official map change took 5-6 s) and about 5 MiB of
the server's memory. The server's copy is not downloaded again at a mount (`mm_addon_mount_download 0`): clients use their own Steam
copy, which works even while the server's copy is one release behind. A client that has the addon is not sent it again within the
server process (`mm_cache_clients_with_addons 1`). Empty `hud.addon`: MultiAddonManager is not loaded at all.

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
server is back. After the restart, once the engine answers, the bridge hears what changed and shows a short note. A bridge that does
not take these notices (down, or one that does not know them yet) gets the watcher's own lines on the HUD instead: the warning when
the restart becomes due and the last line at the end of the match. The daily restart and the download restart happen only with
nobody on and say nothing to players. A new version of the HUD needs no restart (the plugin's banner).

**Every planned restart** quits the engine over RCON, so the image runs SteamCMD again; the bridge hears of it right before
(`/quitting`) and takes it as a clean shutdown. A planned restart inside the daily restart's window counts as that day's.

**Loop protection** (`update-watch/state.json`, schema 3; the Python watcher's schema 2 is read as it is): planned restarts at least
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

## The bridge's side

The watcher posts plain text to the bridge at `<bridge.url>/<path>/<bridge.secret>` (else the address in the image's
`CS2_LOG_HTTP_URL`). An answer that starts with `ok` means taken.

| Path | Body | When |
|---|---|---|
| `/restart-notice/` | `gg-restart\t1\t<kind>\t<when>\t<seconds>` | `kind`: `game-update`, `our-update`, `daily`, `downloads`. `when`: `match-end` (the restart waits for the end of the current match; `seconds` is the countdown that will follow it, 10) or `now` (the quit follows in `seconds`: 10 at a match end, 0 with nobody on). The match-end notice comes again every 5 minutes while the restart waits. |
| `/restart-notice/` | `gg-restart\t1\tcancel\t<kind>` | The announced restart is off: no longer due, or its quit could not be delivered. |
| `/quitting/` | `gg-quit\t1\t<kind>`, `gg-quit\t1\tcancel\t<kind>` | Right before the quit (`kind`: `update`, `version`, `daily`, `workshop`); the cancel when the quit could not be delivered. A freeze kill sends nothing: it is the crash it looks like. |
| `/updated/` | `gg-updated\t1\t<what>\t<from>\t<to>` | After a planned restart, once the engine answers: `game-update` with the CS2 builds, `our-update` with the versions of our parts. Tried again every minute, up to 10 times. |
| `/ready/` | `gg-ready\t1`, then one Workshop id a line; while a background download runs, a last line `current\t<id>\t<percent or ->\t<title>` | The queued maps on the server's disk (job 9), at least every 2 minutes and whenever that or the download's progress changes. An answer that ends in `; hold: <why>` holds the next background download. |

## Downloads a player waits for

Steam runs one Workshop download at a time, and one that is running cannot be stopped. When a player picks the map the watcher is
downloading in the background, it becomes the player's download - no second one - and the HUD's download line shows its progress.
When a player picks another map while one downloads in the background, the change waits for it, and the line says so ("finishing the
background download of X first", with its progress), then shows the chosen map's own progress. No background download starts while a
download somebody waits for runs or while the bridge says hold. The progress is the engine's own for a download it runs, and
MultiAddonManager's for a background one: exact, where the lists' sizes are often far off (a map listed at 763 MB downloaded 63 MB).
Steam's client often stalls near the end of a download for up to a minute; the line then stays at its last percent.

## The maps folder, the hand-over and the deletion

Before every engine start `install` (or `os2-server organize`) keeps each Workshop map in a real folder per type -
`maps/<armsrace|deathmatch|wingman|own|hud|other>/<id>-<name>/` - with Steam's `content/730/<id>` a relative link to it, takes in the
Workshop sync's complete hand-overs, and with `maps.delete` on deletes every stored map outside the keep set: never our own maps, the
HUD or a queued one, and never when the keep set names a problem. Each run writes `update-watch/keep.json` and
`update-watch/deleted-<time>-<done, dryrun or refused>.tsv`.

## Build and test

```sh
dotnet build server/OverStrike2.Server
dotnet run --project server/OverStrike2.Server/SelfTest      # links and /proc need Linux: SKIP elsewhere
dotnet publish server/OverStrike2.Server -c Release -r linux-x64 -o <folder>          # the one file to ship
dotnet publish server/OverStrike2.Server/SelfTest -c Release -r linux-x64 -o <folder>   # the self-test, to run in the game image
```

The self-test drives the watcher's own loop on a fake clock with a fake engine and a fake bridge: every check of the Python
watcher's tests, the restarts above, both download collisions, the install, the observing run.

## The Workshop sync (os2-sync)

`os2-sync` ([`server/OverStrike2.Sync/`](../server/OverStrike2.Sync/)) is the Workshop sync as one self-contained Linux file, run in
a container of its own (the game server's image: its SteamCMD, the libraries a test engine needs). It takes over from
`server/workshop-sync.py` step for step: discovery, the check, the ratings, the hand-over to the game server, downloads, the scan,
the load tests and the publish, with the same limits, stops and mail. It keeps the Python sync's files as they are - the state, the
Web API ledger, the reports, the hand-overs, the bridge's files byte for byte - so either reads what the other wrote. It reads the
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
| `os2-sync serve` | The service: a run whenever the game server has had nobody on for `sync.quietMinutes` and there is work, never around the daily restart; `<state>/run-now` starts a manual run at once. |
| `os2-sync once [JSON]` | One run now, then exit. JSON lowers its limits: `api_run_cap`, `max_pages`, `batch`; `publish` false; `discover` false; `api` true or false; `only_ids`. |
| `os2-sync status` | The Web API ledger's counters and the state's totals. |
| `os2-sync export` | What it has published, as entries a republish from the repository can merge, and the random queues as `queues.json` holds them (`queues`), which the HUD's pictures take first. |
| `os2-sync doctor` | What it sees: the settings as read, the paths, whether there is a key, the bridge, the mail. |
| `os2-sync version` | Its version. |

Every command takes `--settings <file>`. `serve` and `once` also take:

- `--dry-run`: publishing off, and nothing written for anybody else - no file in the bridge's data (the status file goes to the state
  folder), nothing posted to the bridge, no mail. What it would publish - each file, the check's verdicts, a problem mail - goes to
  `<state>/would-publish/<local time>-<name>`, to compare with what the live sync publishes. Unless told otherwise it keeps to folders
  and a port of its own: `<paths.syncState>/dry-run`, `<paths.syncWork>/dry-run` and `sync.testPort` + 10.
- `--state <folder>` and `--work <folder>`: the state folder (ledger, state, reports) and the work folder (staging, test tree,
  hand-overs), else `paths.syncState` and `paths.syncWork`.
- `--test-port <n>`: the test engine's port, else `sync.testPort`.
- `--no-handover`: no downloads for the game server in this run.

**A shadow run** beside the live sync is a `--dry-run`: it shares no ledger, staging, test tree, hand-over folder or test port with the
live one. It still downloads with SteamCMD (into its own staging, as the same user, so not while the live sync's SteamCMD runs) and
starts a test engine of its own (each an anonymous logon); `--no-handover` leaves out the keep set's downloads for the game server.

```sh
dotnet build server/OverStrike2.Sync
dotnet run --project server/OverStrike2.Sync/SelfTest       # process groups, /proc, links, real processes: SKIP elsewhere
dotnet publish server/OverStrike2.Sync -c Release -r linux-x64 -o <folder>          # the one file to ship
dotnet publish server/OverStrike2.Sync/SelfTest -c Release -r linux-x64 -o <folder>   # the self-test, to run in the game image
```

The self-test holds every check of the Python sync's tests (`server/test_workshop_sync.py`), by the same names, and the port's own:
the settings file, the Python formats it keeps byte for byte (JSON, numbers, words), the program's one Web API door, the dry run, the
command line, the SMTP client, SteamCMD's driver against a stand-in, the test tree, and a hand-over taken in by the restart watcher's
own code.
