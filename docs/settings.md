# The settings file

Every part of Over Strike 2 reads one file: the bridge, the plugin and the server program (the restart watcher in the game server's
container, and the Workshop sync in its own). It replaces the environment variables each part read before. The only variables
left are the game image's own, the ones the image needs before any of our code runs (below).

- **Where:** `/etc/overstrike2/overstrike2.jsonc` in each of the three containers, read-only. `OVERSTRIKE2_SETTINGS` points a test at
  another file; nothing else needs it.
- **Format:** JSON with comments and trailing commas, `"schema": 1` at the top. [`deploy/overstrike2.example.jsonc`](../deploy/overstrike2.example.jsonc)
  is a complete example. [`settings.schema.json`](settings.schema.json) is its JSON schema: an editor that knows JSON schemas checks
  the file as you type when the file says `"$schema": "<path to settings.schema.json>"`.
- **A key you leave out has its default.** The tables below give every key, its default and the variable it replaces.
- **Secrets stay where they are kept.** A string `"env:NAME"` is read from the environment variable `NAME`, and `"file:PATH"` from
  that file, trimmed. The RCON password, for instance, is `"env:CS2_RCONPW"`: the game image needs that variable anyway, so the
  bridge's container gets the same variable and nobody writes the password twice. A reference that finds nothing is an empty
  value and a warning.

## How the file gets into the containers

With docker compose, either inline in the compose file, so the values live in it:

```yaml
configs:
  overstrike2:
    content: |
      { "schema": 1, ... }
```

or as a file beside the compose file:

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

Compose copies the file into the container when it creates it, so a change takes effect when the containers are created again
(a redeploy), as with an environment variable. A deployment that cannot use `configs:` mounts the file read-only at the same
path. The file must exist before the containers are created: Docker makes a folder of a bind mount whose source is missing,
and the programs then refuse it.

## How each part reads it

- **Each part checks the keys it uses** (the "Read by" column) and leaves the rest alone.
- **A value of the wrong type or out of its range keeps its default**, and the part says so in its log. The server program also
  reports it on the dashboard. A typo must never quietly widen a safety limit.
- **Hard limits clamp:** at most 10,000 Steam Web API calls in 24 hours, and never less than 2 s between two calls of one program.
- **A key nobody knows is a warning, never fatal.** A part released ahead of the others can bring a new key, and an older part
  only warns about it.
- **A file that cannot be read, is no JSON object or names a schema the part does not know is not used at all.** The server
  program then does nothing, and logs why. No file at all means every value is its default.

The parts also check the file against the image's own variables at start, and log any disagreement: `server.port` against
`CS2_PORT`, `server.startMap` against `CS2_STARTMAP`, and `bridge.url` and `bridge.secret` against `CS2_LOG_HTTP_URL`.

## The game image's own variables

These stay environment variables of the game server's container. The image (`joedwards32/cs2`) reads them in its `entry.sh`,
some of them before any of our code runs: `CS2_SERVERNAME`, `CS2_PORT`, `CS2_RCONPW`, `CS2_PW`, `CS2_MAXPLAYERS`, `CS2_GAMETYPE`,
`CS2_GAMEMODE`, `CS2_MAPGROUP`, `CS2_STARTMAP`, `CS2_LAN`, `CS2_SERVER_HIBERNATE`, `CS2_LOG`, `CS2_LOG_ECHO`, `CS2_LOG_FILE`,
`CS2_LOG_HTTP_URL` (the bridge's `<bridge.url>/log/<bridge.secret>`), `CS2_ADDITIONAL_ARGS`, `SRCDS_TOKEN`, `TV_*` and
`STEAMAPPVALIDATE`. `TZ` sets every container's local time: the daily restart and the logs use it.

## The keys

<!-- keys:begin (made from core/Settings.cs: dotnet run --project core/SelfTest -- --write) -->

### `server`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `server.name` | string | `CS2 server` | bridge, server | SYNC_SERVER_NAME | The server's name in the sync's mails and on the dashboard (the image's CS2_SERVERNAME is the one players see). |
| `server.host` | string | `cs2` | bridge, server | RCON_HOST, SYNC_GAME_HOST | How the bridge and the sync reach the game server: its container's name on their network. |
| `server.port` | integer, 1..65535 | `27015` | bridge, server | RCON_PORT, SYNC_GAME_PORT | The game server's port: A2S (UDP) and RCON (TCP). The image's CS2_PORT must say the same. |
| `server.rconPassword` | string | (empty) | bridge, server | RCON_PASSWORD, CS2_RCONPW | The RCON password. Write "env:CS2_RCONPW": the image needs it as that variable anyway. A secret: write it as a reference. |
| `server.startMap` | string | `ar_shoots` | bridge, server | START_MAP | The map a server start loads; the image's CS2_STARTMAP must say the same. |

### `bridge`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `bridge.url` | string | `http://bridge:8080` | plugin, server | CS2_BRIDGE_URL, SYNC_BRIDGE_URL, CS2_LOG_HTTP_URL | How the game server (the plugin, the restart watcher) and the sync reach the bridge. |
| `bridge.port` | integer, 1..65535 | `8080` | bridge | HTTP_PORT | The port the bridge listens on. |
| `bridge.secret` | string | (empty) | bridge, plugin, server | LOG_SECRET, CS2_BRIDGE_SECRET, SYNC_BRIDGE_SECRET | The secret in the bridge's addresses (/log, /ready, /quitting, /removed, /reload ...): letters and digits. The image's CS2_LOG_HTTP_URL ends in /log/<it>. A secret: write it as a reference. |
| `bridge.admins` | string list | `[]` | bridge | ADMIN_STEAMIDS | SteamID64s of the players who may use the admin commands. |
| `bridge.officialMaps` | string list | `["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]` | bridge | OFFICIAL_MAPS | The official maps the votes and the random picks may offer. |
| `bridge.randomOnStart` | boolean | `true` | bridge | RANDOM_ON_START | A new server process rolls a random map once its start map is up, unless somebody is queued. |
| `bridge.randomQueue` | integer, 1..100 | `20` | bridge | RANDOM_QUEUE | How many random picks each queue rolls ahead. |
| `bridge.changeDelaySeconds` | integer, 0..120 | `8` | bridge | CHANGE_DELAY_SECONDS | The countdown before a map change. |
| `bridge.restartCooldownSeconds` | integer, 0..86400 | `300` | bridge | RESTART_COOLDOWN_SECONDS | The least time between two !restart requests. |
| `bridge.guideOnJoin` | boolean | `true` | bridge | GUIDE_ON_JOIN | Ask the plugin to put the on-screen guide up on every real join. |

### `hud`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `hud.addon` | string | (empty) | bridge, plugin, server | GUNGAME_HUD_ADDON | The Workshop id of the HUD addon every client downloads; empty: no HUD (a text panel instead). |
| `hud.tab` | off \| menu \| on \| down \| hold \| window | `down` | bridge | HUD_TAB | The TAB chord: what holding TAB does with the board. |
| `hud.check` | off \| dryrun \| enforce | `off` | bridge | HUD_CHECK | The HUD check: warn, then disconnect a player not seen clicking in the HUD of this release. |
| `hud.itemPage` | boolean | `true` | plugin | HUD_ITEM_PAGE | Visit the HUD item's Workshop page to see whether Steam shows it to everybody (off on a test copy). |

### `plugin`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `plugin.enabled` | boolean | `true` | server | CS2_PLUGINS | Install Metamod:Source, CounterStrikeSharp and the plugin before every start; false runs the server vanilla. |
| `plugin.grenadeWin` | boolean | `true` | plugin | GUNGAME_GRENADE_WIN | Arms Race: a grenade kill on the last level wins the match. |
| `plugin.grenadeWinKeepKnife` | boolean | `false` | plugin | GUNGAME_GRENADE_WIN_KEEP_KNIFE | Hand out the grenade on the last level but leave the knife in play. |

### `restarts`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `restarts.daily` | time | `07:00` | server | CS2_RESTART_AT, SYNC_DAILY_RESTART | The daily restart, local time, only while nobody is on; "off" for none. The sync keeps clear of it. |
| `restarts.updates` | boolean | `true` | server | CS2_AUTOUPDATE | Restart for a new CS2 build: at the end of the match, or at once with nobody on. |
| `restarts.updateCheckMinutes` | integer, 10..1440 | `30` | server | CS2_AUTOUPDATE_CHECK_MINUTES | How often Valve is asked whether the build is current. |
| `restarts.newVersions` | boolean | `true` | server | (new) | Restart for a deployed new version of the server's own parts the same way: at the end of the match, or at once with nobody on. |
| `restarts.freezeRecovery` | boolean | `true` | server | CS2_FREEZE_RECOVERY | Kill a frozen engine so Docker starts it again. |
| `restarts.freezeGraceSeconds` | integer, 5..600 | `10` | server | CS2_FREEZE_GRACE_SECONDS | The silence before that kill. |
| `restarts.downloadRestart` | boolean | `true` | server | CS2_WORKSHOP_RESTART | Restart an engine process whose Workshop downloads are stuck, only while nobody is on. |

### `maps`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `maps.layout` | folders \| off | `folders` | server | CS2_MAPS_LAYOUT | Keep the maps in folders per type, linked from Steam's folder; off leaves Steam's layout alone. |
| `maps.delete` | on \| dryrun \| off | `on` | server | CS2_MAPS_DELETE | At every engine start, delete the maps outside the keep set; dryrun lists them only. |
| `maps.backgroundDownloads` | boolean | `true` | server | CS2_READY | Download the maps the bridge's queues will play next, in the background, also while people play. |
| `maps.downloadLine` | boolean | `true` | server | CS2_DOWNLOAD_LINE | Say on the HUD's news line what the server downloads for a waiting player, with its progress. |

### `sync`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `sync.enabled` | boolean | `true` | server | SYNC_ENABLED | Runs at all; a manual run (run-now) works either way. |
| `sync.publish` | boolean | `true` | server | SYNC_PUBLISH | Write the results into the bridge's data; false runs every step and writes nothing. |
| `sync.runHours` | integer, 1..12 | `3` | server | SYNC_RUN_HOURS | The longest run; the next starts while nobody plays and work waits. |
| `sync.apiEveryHours` | integer, 1..48 | `4` | server | SYNC_API_EVERY_HOURS | Discovery, the check and the ratings at most this often. |
| `sync.tags` | string list | `["Armsrace", "Deathmatch", "Wingman"]` | server | SYNC_TAGS | The Workshop tags searched. |
| `sync.excludeIds` | string list | `[]` | server | SYNC_EXCLUDE_IDS | Workshop ids that are never candidates (the HUD item, hud.addon, never is). |
| `sync.excludeCreators` | string list | `[]` | server | SYNC_EXCLUDE_CREATORS | SteamID64s of Workshop authors whose maps are listed by hand (your own). |
| `sync.changedSince` | string | (empty) | server | SYNC_CHANGED_SINCE | A listed map updated after this day (YYYY-MM-DD) counts as changed at its first check; empty: none does. |
| `sync.modeBackfill` | boolean | `false` | server | SYNC_MODE_BACKFILL | Listed Arms Race maps that carry a mode's tag join that mode's list too, untested in it. |
| `sync.apiRunCap` | integer, 0..10000 | `500` | server | SYNC_API_RUN_CAP | Web API calls in one run, at most (never above sync.apiDayCap). |
| `sync.apiDayCap` | integer, 0..10000 | `10000` | server | SYNC_API_DAY_CAP | Web API calls in any 24 hours, at most; 10,000 is the hard limit (10% of Valve's daily allowance per key). |
| `sync.maxPages` | integer, 1..2000 | `200` | server | SYNC_MAX_PAGES | Discovery's pages of 100, at most. |
| `sync.downloadPauseSeconds` | number, 2..600 | `2` | server | SYNC_DOWNLOAD_PAUSE | The pause between two SteamCMD downloads; at least 2 s. |
| `sync.holdSeconds` | integer, 5..300 | `20` | server | SYNC_HOLD_SECONDS | How long the test engine must hold a map with two bots for a pass. |
| `sync.batch` | integer, 1..1500 | `100` | server | SYNC_BATCH | Downloads per batch; a batch is downloaded, tested and published before the next. |
| `sync.quietMinutes` | integer, 0..240 | `10` | server | SYNC_QUIET_MINUTES | How long the server must have had nobody on before a run, after somebody was seen. |
| `sync.testPort` | integer, 1024..65535 | `27215` | server | SYNC_TEST_PORT | The test engine's port inside the sync's container. |
| `sync.engineStartsMax` | integer, 1..400 | `200` | server | SYNC_ENGINE_STARTS_MAX | Test engine starts in one run, at most; each is an anonymous game-server logon. |
| `sync.ratings` | boolean | `true` | server | SYNC_RATINGS | Fetch the Workshop's scores, subscriptions and trend once a day (the keep set's top lists). |
| `sync.handover` | boolean | `true` | server | SYNC_HANDOVER | Hand the keep set's maps and their tested updates over to the game server. |
| `sync.diskLowGb` | integer, 1..100000 | `100` | server | SYNC_DISK_LOW_GB | Less free space than this on the game's volume is a problem for the mail. |
| `sync.webApiKey` | string | `file:/run/secrets/steam-webapi-key.txt` | server | STEAM_WEBAPI_KEY_FILE | The Steam Web API key, for discovery and the ratings; without one those steps are skipped. A secret: write it as a reference. |
| `sync.mail.smtpHost` | string | (empty) | server | SYNC_SMTP_HOST | The SMTP relay for the problem mails; empty: no mail. |
| `sync.mail.smtpPort` | integer, 1..65535 | `25` | server | SYNC_SMTP_PORT | Its port (STARTTLS when offered). |
| `sync.mail.from` | string | (empty) | server | SYNC_MAIL_FROM | The mails' sender. |
| `sync.mail.to` | string list | `[]` | server | SYNC_MAIL_TO | The mails' recipients. |
| `sync.mail.dashboardUrl` | string | (empty) | server | SYNC_DASHBOARD_URL | The dashboard's address every problem mail points to. |

### `steam`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `steam.apiGapSeconds` | number, 2..600 | `2` | plugin, server | SYNC_API_GAP_SECONDS | Each program's Steam Web API calls go one at a time, at least this long after the last one ended; never under 2 s. |

### `paths`

| Key | Type | Default | Read by | Replaces | What |
|---|---|---|---|---|---|
| `paths.bridgeData` | string | `/data` | bridge, server | STATE_DIR, CS2_BRIDGE_DATA, SYNC_BRIDGE_DATA | The bridge's data folder: read-write in the bridge and the sync, read-only in the game server - mounted at the same path in all three. |
| `paths.gameDir` | string | `/home/steam/cs2-dedicated` | server | STEAMAPPDIR, SYNC_GAME_DIR | The game server's install (the image's STEAMAPPDIR); the sync mounts it read-only at the same path. |
| `paths.handover` | string | `/home/steam/cs2-dedicated/workshop-sync/handover` | server | CS2_HANDOVER_DIR | The hand-over folder as the game server sees it: inside paths.gameDir, so a take-in is one rename. |
| `paths.syncState` | string | `/home/steam/sync-state` | server | SYNC_STATE_DIR | The sync's state, ledger, reports and backups (kept, backed up). |
| `paths.syncWork` | string | `/home/steam/workshop-sync` | server | SYNC_WORK_DIR | The sync's staging, test engine tree and hand-overs (<it>/handover is paths.handover in the game server); on the game's volume. |
| `paths.syncCfg` | string | (empty) | server | SYNC_CFG_DIR | Mode cfgs copied over the test engine's own; empty: none. |
| `paths.steamCmd` | string | `/home/steam/steamcmd/steamcmd.sh` | server | SYNC_STEAMCMD | SteamCMD in the sync's container (the image's own). |
| `paths.depotcache` | string | `/home/steam/Steam/depotcache` | server | CS2_DEPOTCACHE, SYNC_DEPOTCACHE | Where the Steam client keeps the manifests it verified; on a volume, so a new container re-checks nothing. |

<!-- keys:end -->
