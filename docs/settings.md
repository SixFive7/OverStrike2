# The settings file

Every part of Over Strike 2 reads one file: the bridge, the plugin, `os2-server` (the restart watcher and the maps folder, in the game
server's container) and `os2-sync` (the Workshop sync, in its own) - "server" and "sync" in the "Read by" column. It replaces the environment variables each part read before. The only variables
left are the game image's own, the ones the image needs before any of our code runs (below).

- **Where:** each of the three containers takes the first of these that says: a file named for a test (`--settings`, else
  `OVERSTRIKE2_SETTINGS`); the file's text in the environment variable `OVERSTRIKE2_SETTINGS_CONTENT`; the file
  `/etc/overstrike2/overstrike2.jsonc`.
- **Format:** JSON with comments and trailing commas, `"schema": 1` at the top. [`deploy/overstrike2.example.jsonc`](../deploy/overstrike2.example.jsonc)
  is a complete example. [`settings.schema.json`](settings.schema.json) is its JSON schema: an editor that knows JSON schemas checks
  the file as you type when the file says `"$schema": "<path to settings.schema.json>"`.
- **A key you leave out has its default**, except `hud.addon`, which is required (below). The tables below give every key, its default
  and the parts that read it.
- **Secrets stay where they are kept.** A string `"env:NAME"` is read from the environment variable `NAME`, and `"file:PATH"` from
  that file, trimmed. The RCON password, for instance, is `"env:CS2_RCONPW"`, its default: the game image needs that variable anyway, so
  the bridge's container gets the same variable and nobody writes the password twice. A reference that finds nothing is an empty
  value and a warning.

## How the file gets into the containers

Inline, as a multi-line environment variable of each of the three services, so the values live in the compose file. It is the file's
text, comments and all, read by the same rules; a value that cannot be used counts as a file that cannot be used. It works on a
read-only container, and a container recreated from its inspect (an update tool, a test copy) keeps it:

```yaml
x-settings: &settings |
  { "schema": 1, "hud": { "addon": "3823993566" }, ... }

services:
  cs2:
    environment:
      OVERSTRIKE2_SETTINGS_CONTENT: *settings
```

Compose expands `$` in it, as in any environment value, so a `"$schema"` key is written `"$$schema"`. Anybody who may inspect the
container can read the value: keep secrets out of it, as references. The parts log the variable's name, never its value.

The other way is a file beside the compose file, as [`deploy/compose.yml`](../deploy/compose.yml) does. Compose makes it a bind mount,
which is fine on a read-only service:

```yaml
configs:
  overstrike2:
    file: ./overstrike2.jsonc
```

and in each of the three services:

```yaml
    configs:
      - source: overstrike2
        target: /etc/overstrike2/overstrike2.jsonc
```

A deployment that cannot use `configs:` mounts the file read-only at the same path. The file must exist before the containers are
created: Docker makes a folder of a bind mount whose source is missing, and the programs then refuse it.

Not an inline compose config (`configs:` with `content:`): it lives in the container's own layer, so it does not run on a read-only
service and it is lost at a recreate from inspect.

Either way, a change takes effect when the containers are created again (a redeploy).

## How each part reads it

- **Each part checks the keys it uses** (the "Read by" column) and leaves the rest alone.
- **A value of the wrong type or out of its range keeps its default**, and the part says so in its log. A typo must never quietly
  widen a safety limit.
- **Hard limits clamp:** at most 10,000 Steam Web API calls in 24 hours, and never less than 2 s between two calls of one program.
- **A key nobody knows is a warning, never fatal.** A part released ahead of the others can bring a new key, and an older part
  only warns about it.
- **`hud.addon` is required: a server without the HUD cannot exist.** Missing, empty or not digits, the file cannot be used by the
  parts that read it (the plugin and the server programs), as in the next point. There is no built-in id: the example file holds the
  public HUD's. This looks at the file only, never at Steam: while Valve holds the HUD's item the game keeps playing, and one player
  without the HUD gets a warning only (`hud.check`, `warn` by default).
- **A file that cannot be read, is no JSON object or names a schema the part does not know is not used at all.** `os2-server` and
  `os2-sync` then do nothing: one `ALERT` line says why and how to fix it, and they exit with code 78. The bridge and the plugin refuse
  it the same way: the bridge exits with code 78, the plugin does not load, and their `settings:` line says why. `doctor` and `version` still
  work and say so. No settings at all are the same, for every part. On that code from `os2-server install`, `pre.sh` stops
  the container's start: the engine does not start, Docker's restart policy tries again 5 minutes later, and the log says why. All but one
  of the programs' other failures cost the plugin layer and the watcher, never the server ([server.md](server.md#how-it-starts)).
- **Only the settings count.** The environment variables the parts read before them stand in for nothing in any part. The
  bridge and the plugin log at their start where their settings came from - `settings: the settings file <path>` or `settings: the
  environment variable OVERSTRIKE2_SETTINGS_CONTENT`, naming the keys with a problem or a warning - and never a value. The bridge's map
  list and broken list have no key of their own: they are `maps.json` and `broken.json` in `paths.bridgeData`.

The parts also check the file against the image's own variables at start, and log any disagreement: `server.port` against
`CS2_PORT`, `server.startMap` against `CS2_STARTMAP`, and `bridge.url` and `bridge.secret` against `CS2_LOG_HTTP_URL`. Those
variables only ever warn: the settings decide.

## The game image's own variables

These stay environment variables of the game server's container. The image (`joedwards32/cs2`) reads them in its `entry.sh`,
some of them before any of our code runs: `CS2_SERVERNAME`, `CS2_PORT`, `CS2_RCONPW`, `CS2_PW`, `CS2_MAXPLAYERS`, `CS2_GAMETYPE`,
`CS2_GAMEMODE`, `CS2_MAPGROUP`, `CS2_STARTMAP`, `CS2_LAN`, `CS2_SERVER_HIBERNATE`, `CS2_LOG`, `CS2_LOG_ECHO`, `CS2_LOG_FILE`,
`CS2_LOG_HTTP_URL` (the bridge's `<bridge.url>/log/<bridge.secret>`), `CS2_ADDITIONAL_ARGS`, `SRCDS_TOKEN`, `TV_*` and
`STEAMAPPVALIDATE`. `TZ` sets every container's local time: the daily restart and the logs use it.

## The keys

<!-- keys:begin (made from core/Settings.cs: dotnet run --project core/SelfTest -- --write) -->

### `server`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `server.name` | string | `CS2 server` | sync | The server's name in the sync's mails and on the dashboard (the image's CS2_SERVERNAME is the one players see). |
| `server.host` | string | `cs2` | bridge, sync | How the bridge and the sync reach the game server: its container's name on their network. |
| `server.port` | integer, 1..65535 | `27015` | bridge, server, sync | The game server's port: A2S (UDP) and RCON (TCP). The image's CS2_PORT must say the same. |
| `server.rconPassword` | string | `env:CS2_RCONPW` | bridge, server | The RCON password: by default the image's own CS2_RCONPW, which it needs anyway, so the bridge's container gets that variable too. A secret: write it as a reference. |
| `server.startMap` | string | `ar_shoots` | bridge, server | The map a server start loads; the image's CS2_STARTMAP must say the same. |

### `bridge`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `bridge.url` | string | `http://bridge:8080` | plugin, server, sync | How the game server (the plugin, the restart watcher) and the sync reach the bridge. |
| `bridge.port` | integer, 1..65535 | `8080` | bridge | The port the bridge listens on. |
| `bridge.secret` | string | (empty) | bridge, plugin, server, sync | The secret in the bridge's addresses (/log, /ready, /quitting, /removed, /reload ...): letters and digits. The image's CS2_LOG_HTTP_URL ends in /log/<it>. A secret: write it as a reference. |
| `bridge.officialMaps` | string list | `["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]` | bridge | The official maps the votes and the random picks may offer. |
| `bridge.discoveryStock` | integer, 0..40 | `10` | bridge, server, sync | The discovery stock: per mode, how many maps new to the server the bridge rolls ahead (the vote's surprise, the automatic picks), which the game server downloads in the background; 0 turns it off. The keep set wants each of the stock's lanes this long. |
| `bridge.diceMaxMb` | integer, 0..100000 | `300` | bridge | The most a map the dice roll may take to download, in MB: the start roll, the way back to Arms Race, Random and nobody voting, a replaced card, and the discovery stock's rolls. A map new to a player's game asks them to confirm its download, and a big one keeps them waiting. Official maps always fit; when no eligible map fits, the dice take the smallest. 0: no cap. |
| `bridge.changeDelaySeconds` | integer, 0..120 | `8` | bridge | The countdown before a map change. |
| `bridge.restartCooldownSeconds` | integer, 0..86400 | `300` | bridge | The least time between two !restart requests. |

### `hud`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `hud.addon` | string | (required) | plugin, server, sync | Required: a server without the HUD cannot exist. The Workshop id of the HUD addon, digits only: the game server mounts it, and every client downloads it and keeps it across every map change. Missing, empty or not digits, the file cannot be used: the server programs refuse to run and the plugin does not load. |
| `hud.tab` | off \| down | `down` | bridge | The TAB chord: down puts the board down while TAB is held; off leaves TAB alone. |
| `hud.check` | off \| warn \| enforce | `warn` | bridge | The HUD check: warn a player not seen clicking in the HUD of this release, and with enforce disconnect them; warn by default, so every player's HUD version is learned at their first click. |
| `hud.itemPage` | boolean | `true` | plugin | Visit the HUD item's Workshop page to see whether Steam shows it to everybody (off on a test copy). |

### `plugin`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `plugin.enabled` | boolean | `true` | server | Install Metamod:Source, CounterStrikeSharp and the plugin before every start; false runs the server vanilla. |
| `plugin.testBots` | boolean | `false` | plugin | A test copy's: bots may play - the plugin's block leaves bot_quota and bot_quota_mode to RCON, so the live lab has bots to look with (one bot_add_ct gives two, one a side, and bot_quota 2: CS2's own). Off on production, where the only bots are the players' own, from FUN, Bots. |

### `restarts`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `restarts.daily` | time | `07:00` | server, sync | The daily restart, local time, only while nobody is on; "off" for none. The sync keeps clear of it. |
| `restarts.updates` | boolean | `true` | server | Restart for a new CS2 build: at the end of the match, or at once with nobody on. |
| `restarts.updateCheckMinutes` | integer, 10..1440 | `30` | server | How often Valve is asked whether the build is current. |
| `restarts.newVersions` | boolean | `true` | server | Restart for a deployed new version of the server's own parts the same way: at the end of the match, or at once with nobody on. |
| `restarts.freezeRecovery` | boolean | `true` | server | Kill a frozen engine so Docker starts it again. |
| `restarts.freezeGraceSeconds` | integer, 5..600 | `10` | server | The silence before that kill. |
| `restarts.downloadRestart` | boolean | `true` | server | Restart an engine process whose Workshop downloads are stuck, only while nobody is on. |

### `maps`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `maps.layout` | folders \| off | `folders` | server | Keep the maps in folders per type, linked from Steam's folder; off leaves Steam's layout alone. |
| `maps.delete` | on \| dryrun \| off | `on` | server | At every engine start, delete the maps outside the keep set; dryrun lists them only. |
| `maps.preview` | boolean | `false` | server | Change nothing and only write what a start would do - the keep set and the maps it would delete - into paths.preview, for a rehearsal on production's volume. Set maps.layout off beside it: a build that predates this key ignores it and then leaves the maps alone. |
| `maps.backgroundDownloads` | boolean | `true` | server | Download the maps of the bridge's discovery stock (queues.json) in the background, also while people play. |
| `maps.downloadLine` | boolean | `true` | server | Say on the HUD's top line what the server downloads for a waiting player, with its progress. |

### `sync`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `sync.enabled` | boolean | `true` | sync | Runs at all; a manual run (run-now) works either way. |
| `sync.publish` | boolean | `true` | sync | Write the results into the bridge's data; false runs every step and writes nothing. |
| `sync.runHours` | integer, 1..12 | `3` | sync | The longest run; the next starts while nobody plays and work waits. |
| `sync.apiEveryHours` | integer, 1..48 | `4` | sync | Discovery, the check and the ratings at most this often. |
| `sync.tags` | string list | `["Armsrace", "Deathmatch", "Wingman"]` | sync | The Workshop tags searched. |
| `sync.excludeIds` | string list | `[]` | sync | Workshop ids that are never candidates (the HUD item, hud.addon, never is). |
| `sync.excludeCreators` | string list | `[]` | sync | SteamID64s of Workshop authors whose maps are listed by hand (your own). |
| `sync.changedSince` | string | (empty) | sync | A listed map updated after this day (YYYY-MM-DD) counts as changed at its first check; empty: none does. |
| `sync.modeBackfill` | boolean | `false` | sync | Listed Arms Race maps that carry a mode's tag join that mode's list too, untested in it. |
| `sync.apiRunCap` | integer, 0..10000 | `500` | sync | Web API calls in one run, at most (never above sync.apiDayCap). |
| `sync.apiDayCap` | integer, 0..10000 | `10000` | sync | Web API calls in any 24 hours, at most; 10,000 is the hard limit (10% of Valve's daily allowance per key). |
| `sync.maxPages` | integer, 1..2000 | `200` | sync | Discovery's pages of 100, at most. |
| `sync.downloadPauseSeconds` | number, 2..600 | `2` | sync | The pause between two SteamCMD downloads; at least 2 s. |
| `sync.holdSeconds` | integer, 5..300 | `20` | sync | How long the test engine must hold a map with two bots for a pass. |
| `sync.batch` | integer, 1..1500 | `100` | sync | Downloads per batch; a batch is downloaded, tested and published before the next. |
| `sync.quietMinutes` | integer, 0..240 | `10` | sync | How long the server must have had nobody on before a run, after somebody was seen or the service started. |
| `sync.testPort` | integer, 1024..65535 | `27215` | sync | The test engine's port inside the sync's container. |
| `sync.engineStartsMax` | integer, 1..400 | `200` | sync | Test engine starts in one run, at most; each is an anonymous game-server logon. |
| `sync.ratings` | boolean | `true` | sync | Fetch the Workshop's scores, subscriptions and trend once a day (the keep set's top lists). |
| `sync.handover` | boolean | `true` | sync | Hand the keep set's maps and their tested updates over to the game server. |
| `sync.diskLowGb` | integer, 1..100000 | `100` | sync | Less free space than this on the game's volume is a problem for the mail. |
| `sync.webApiKey` | string | `file:/run/secrets/steam-webapi-key.txt` | sync | The Steam Web API key, for discovery and the ratings; without one those steps are skipped. A secret: write it as a reference. |
| `sync.mail.smtpHost` | string | (empty) | sync | The SMTP relay for the problem mails; empty: no mail. |
| `sync.mail.smtpPort` | integer, 1..65535 | `25` | sync | Its port (STARTTLS when offered). |
| `sync.mail.from` | string | (empty) | sync | The mails' sender. |
| `sync.mail.to` | string list | `[]` | sync | The mails' recipients. |
| `sync.mail.dashboardUrl` | string | (empty) | sync | The dashboard's address every problem mail points to. |

### `steam`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `steam.apiGapSeconds` | number, 2..600 | `2` | plugin, server, sync | Each program's Steam Web API calls go one at a time, at least this long after the last one ended; never under 2 s. |

### `paths`

| Key | Type | Default | Read by | What |
|---|---|---|---|---|
| `paths.bridgeData` | string | `/data` | bridge, server, sync | The bridge's data folder: read-write in the bridge and the sync, read-only in the game server - mounted at the same path in all three. |
| `paths.gameDir` | string | `/home/steam/cs2-dedicated` | server, sync | The game server's install (the image's STEAMAPPDIR); the sync mounts it read-only at the same path. |
| `paths.handover` | string | `/home/steam/cs2-dedicated/workshop-sync/handover` | server | The hand-over folder as the game server sees it: inside paths.gameDir, so a take-in is one rename. |
| `paths.syncState` | string | `/home/steam/sync-state` | sync | The sync's state, ledger, reports and backups (kept, backed up). |
| `paths.syncWork` | string | `/home/steam/workshop-sync` | sync | The sync's staging, test engine tree and hand-overs (<it>/handover is paths.handover in the game server); on the game's volume. |
| `paths.syncCfg` | string | (empty) | sync | Mode cfgs copied over the test engine's own; empty: none. |
| `paths.steamCmd` | string | `/home/steam/steamcmd/steamcmd.sh` | sync | SteamCMD in the sync's container (the image's own). |
| `paths.depotcache` | string | `/home/steam/Steam/depotcache` | server, sync | Where the Steam client keeps the manifests it verified; on a volume, so a new container re-checks nothing. |
| `paths.plugins` | string | `/home/steam/cs2-plugins` | server | The CounterStrikeSharp plugins to install, one folder each (read-only): copied into the game before every engine start. |
| `paths.preview` | string | (empty) | server | Where maps.preview writes keep-preview.json and its list of what a start would delete - a rehearsal's own folder; empty: preview/ in the game's update-watch. |

<!-- keys:end -->
