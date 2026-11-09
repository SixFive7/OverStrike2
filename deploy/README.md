# Deploying Over Strike 2

Builds happen on a workstation and ship straight to the server's Docker and folders - no CI, no registry. Every script reads
the deployment from a private config: [`config.example.toml`](config.example.toml) shows every key; the filled copy lives at
`.private/config.toml` in your checkout (gitignored) or wherever `OVERSTRIKE2_CONFIG` points, and credentials come from the
environment (`"env:NAME"` values). Nothing here prints a credential.

Every step runs on one program, `os2-deploy` ([`OverStrike2.Deploy/`](OverStrike2.Deploy/), below): `dotnet run --project
deploy/OverStrike2.Deploy -- <command> ...` from the repository's root, or a build of it.

| Command | Does | Restarts |
|---|---|---|
| `bridge check` | what would ship, the running bridge and its image, players, the server lock | nothing |
| `bridge build [--tag T]` | builds the committed `bridge/` and `core/` with WSL containers or Docker at below normal priority, saves the image and loads it into the server's Docker as `<image>:<commit>` and `<image>:candidate` (or `:T`) | nothing |
| `bridge swap` / `update` / `rollback` | replaces the bridge container's image with `:candidate` (`update` = check, build, swap), verified, keeping the old one as `:previous`; a stopped bridge is swapped too and started on the new image; everything else of the container stays, the settings in its environment among it | the bridge alone, for seconds; nobody is disconnected |
| `bridge images` / `prune [--apply]` | the image's tags on the server; its old tags off it, a dry run unless `--apply` - the images `:latest`, `:previous` and `:candidate` point at stay, and any a container uses | nothing |
| `plugin build` / `ship [--apply]` | builds and tests `plugin/OverStrike2`, copies it to the server's plugin folder | the game server must restart to load it: by hand only at 0 players; the restart watcher's announced restart is exempt - at the end of the match, at once with nobody on |
| `data [--apply] [--force]` | the hand-made files - `ours.json`, `sizes.json`, `blocked.json` - to the bridge's data folder; `maps.json`, `broken.json` and `search.json` only to a server that has none yet, because from then on the Workshop sync writes them there | nothing: the bridge reads them again by itself |
| `server build [--image]` | publishes `os2-server` and `os2-sync`, one self-contained linux-x64 file each, from the committed sources, and runs both self-tests; `--image` runs them in the game server's image as well, where the checks that need Linux run too | nothing |
| `server ship [--apply]` | `os2-server`, `os2-sync` and the launcher `pre.sh` to the server's program folder, the three mode cfgs to its config folder: the programs renamed into place, `pre.sh` and the cfgs written in place, because a container mounts each of those as a single file and would stay on the old one. `--apply` fails when `server.program_dir` is empty: nothing would ship | the restart watcher takes a new `os2-server`, `pre.sh` or cfg in by itself - at the end of the match, at once with nobody on; `os2-sync` at the sync container's next start |
| `server restart-sync` | the Workshop sync's container restarted between runs, so it starts the `os2-sync` a ship put there, seen through to the program it runs | the sync alone |
| `workshop ...`, `thumbs ...` | the HUD's thumbnails, build and Workshop update; our maps' Workshop items | players fetch a new HUD version when they join |
| `release live <tag> [--no-plugin] [--under-lock]` | a release, at 0 humans and under the server lock: the bridge swapped to that image tag, the plugin shipped, the engine quit once and checked when it is back (plugin loaded, its files the build's, the HUD item of `hud/workshop_id.txt` handed out), then the deployment's check: `check verify`, or the command `release.verify` names | the game server once (none with `--no-plugin`) |
| `release publish --note ... --client-test <run>` | a content update of the HUD item, after a real-client test (a run folder of [tools/client/](../tools/client/README.md), the harness that reads this same config) and the server's held-item check, waiting for the upload and Valve's approval | nothing |
| `check guard [--allow-kick]` | read-only, before every redeploy of the whole stack: GO (exit 0) only with nobody on, NO (exit 3) with players on or no A2S answer; `--allow-kick` only when the owner said so. It also shows the server lock and the drift - the game server's image against its tag, its service in the compose file as deployed against now (`[stack]`) - which decides nothing | nothing |
| `check verify` | read-only, after a deploy or a release: the bridge on `<image>:latest` and in its compose project; the game server and the sync running, production and not a test copy (`[check]`), on its map group, VAC-secured; the bridge's health from inside the game server; the settings text identical in every container, the bridge's secret and the RCON password the same in every place they are written (compared, never printed); the roll-call of the compose file and the deployment's own audits. Exit 0 ALL GOOD, 1 something to look at | nothing |
| `probe ...`, `testcopy ...`, `bridgepost ...`, `rehearse ...` | the client harness's server half, the test copy and its candidate bridge, what only the watcher or other players send, the release rehearsal (below) | see each |

Without `--apply`, the copying commands only show what would change. Every command ships committed files only. A redeploy of the whole stack
(a change to its compose file) may restart the game server whatever changed: `check guard` right before it, `check verify` after.
[`compose.example.yml`](compose.example.yml) is the shape of a host they ship to: the folders they fill, the settings inline as one environment variable.

A host that builds everything itself needs none of these: [`compose.yml`](compose.yml) builds the bridge and an installer image
(`installer/`), which puts the plugin, both programs and `pre.sh` into the volumes the other services mount
([docs/install.md](../docs/install.md)).

## The bridge image

The compose file names the image under `localhost/`, a registry no pull can reach, and never pulls it:

```yaml
  bridge:
    image: localhost/overstrike2-bridge
    pull_policy: never
```

`os2-deploy bridge build` makes `localhost/overstrike2-bridge:<commit>` (immutable) and points `:candidate` at it; `bridge swap` points
`:latest` - the tag the compose file means - at the candidate and recreates the container from its own configuration, so a
later redeploy of the stack runs the same image. `:previous` is what the last swap replaced (`bridge rollback`).

The scripts take the bridge's secret from the game container's `CS2_LOG_HTTP_URL` and the RCON password from its `CS2_RCONPW`, the image's own
variables, and fall back on the bridge's `LOG_SECRET` and `RCON_PASSWORD`. The settings of a deployment like `compose.example.yml` are an environment
variable, `OVERSTRIKE2_SETTINGS_CONTENT`, so a swap keeps them with the rest of the container's configuration, as does anything else that recreates a container
from its inspect. Compose's inline `configs: content:` would not do: a read-only service never runs with it, and such a recreate loses it (docs/settings.md).

## os2-deploy

[`OverStrike2.Deploy/`](OverStrike2.Deploy/) is one program, `os2-deploy`, ported from the Python deploy scripts and
[tools/client/](../tools/client/README.md)'s Python - the same commands, private config, words and exit codes. Since release 193 it is the
only release tool: `data.py`, `release.py`, `server.py` and `workshop.py` left `deploy/` then. Since 2026-11-09 it is the
client harness's only server half too: `tools/client/probe/`'s and `tools/client/docshots/`'s Python left the repository, and with them the
library they imported (`os2.py`, `bridge.py`, `plugin.py`, `check.py`, `test_deploy.py`, `test_check.py`). The harness (`cs2client.ps1`) runs
`os2-deploy probe` from a build of this program (its own newest, or the one `$OS2_DEPLOY` names), never through `dotnet run`.

```sh
dotnet run --project deploy/OverStrike2.Deploy -- <command> ...      # from the repository's root; or build once and run bin/.../os2-deploy
```

| Command | Ported from |
|---|---|
| `bridge check\|build\|images\|swap\|update\|rollback [--tag T]`, `bridge prune [--apply]` | `bridge.py`, gone since 2026-11-09 (no `build-remote` or `promote`: nothing used them); `prune` is new here: the old tags off the server, a dry run unless `--apply` |
| `data [--apply] [--force]`, `plugin build\|ship`, `server build\|ship\|restart-sync`, `workshop ...` | `data.py bridge`, `plugin.py`, `server.py`, `workshop.py` (`plugin.py` gone since 2026-11-09, the rest since 193); `server restart-sync` is new here: the sync restarted between runs, so it starts the os2-sync shipped |
| `thumbs ...` | `hud/thumbs.py`, its pictures pixel for pixel |
| `release live\|publish ...`, `check guard\|verify` | `release.py` (gone since 193), `check.py` (gone since 2026-11-09) |
| `probe`, `testcopy`, `bridgepost` | `tools/client/probe/`'s three, gone since 2026-11-09; `probe exec\|logs <game\|bridge\|sync>` is new here: production's containers read for a post-check, every secret scrubbed |
| `docshots ...` (and `docshots publish\|name\|merge\|maskview\|sheets`) | `tools/client/docshots/`'s docshots.py, publish.py, docname.py, merge_fix.py, maskview.py, review_sheet.py, gone since 2026-11-09 |
| `rehearse prep\|backup\|stage\|up\|compose\|hold\|down\|restore\|clean\|logs\|sh\|rcon\|ship\|status` | the release rehearsal: production's game server, bridge and sync set aside under the server lock with nobody on, this checkout's builds run as a throwaway stack (`[rehearse]` in the config), everything put back. `hold on\|off` (or `up --hold`) keeps the rehearsal's bridge on its map for the live lab - the bridge's maintenance flag in the rehearsal's copy of its data, never production's; `down` and `restore` lift it |

`os2-deploy --selftest` runs every case of the retired Python tests, ported under the same names (`--selftest <suite>` one of them).
