# Deploying Over Strike 2

Builds happen on a workstation and ship straight to the server's Docker and folders - no CI, no registry. Every script reads
the deployment from a private config: [`config.example.toml`](config.example.toml) shows every key; the filled copy lives at
`.private/config.toml` in your checkout (gitignored) or wherever `OVERSTRIKE2_CONFIG` points, and credentials come from the
environment (`"env:NAME"` values). Nothing here prints a credential.

| Script | Does | Restarts |
|---|---|---|
| `bridge.py check` | what would ship, the running bridge and its image, players, the server lock | nothing |
| `bridge.py build [--tag T]` | builds the committed `bridge/` with WSL containers or Docker at below normal priority, saves the image and loads it into the server's Docker as `<image>:<commit>` and `<image>:candidate` (or `:T`) | nothing |
| `bridge.py build-remote [--tag T]` | the same image, built by the server's Docker from an uploaded build context | nothing |
| `bridge.py swap` / `update` / `rollback` | replaces the bridge container's image with `:candidate` (`update` = check, build, swap), verified, keeping the old one as `:previous`; a stopped bridge is swapped too and started on the new image | the bridge alone, for seconds; nobody is disconnected |
| `bridge.py promote` | points `:latest` at `:candidate` without touching a container, for a redeploy of the whole stack (the bridge's compose service changed, or the first switch to this image) | nothing until the redeploy - which restarts the game server too |
| `plugin.py build` / `ship [--apply]` | builds and tests `plugin/OverStrike2`, copies it to the server's plugin folder | the game server must restart to load it - only at 0 players |
| `data.py bridge [--apply]` | the hand-made files - `ours.json`, `sizes.json`, `blocked.json` - to the bridge's data folder; `maps.json`, `broken.json` and `search.json` only to a server that has none yet, because from then on the Workshop sync writes them there | nothing: the bridge reads them again by itself |
| `data.py server [--apply]` | for a server that still runs the Python scripts: `pre.sh`, the three mode cfgs, the restart watcher, the Workshop sync and the keep set both read (`keepset.py`) to the server's config folder (when the config names one) | at the game server's (or the sync's) next start |
| `server.py build [--image]` | publishes `os2-server` and `os2-sync`, one self-contained linux-x64 file each, from the committed sources, and runs both self-tests; `--image` runs them in the game server's image as well, where the checks that need Linux run too | nothing |
| `server.py ship [--apply]` | `os2-server`, `os2-sync` and the launcher `pre.sh` to the server's program folder, the three mode cfgs to its config folder, each renamed into place | the restart watcher takes a new `os2-server`, `pre.sh` or cfg in by itself - at the end of the match, at once with nobody on; `os2-sync` at the sync container's next start |
| `workshop.py ...` | the HUD's thumbnails, build and Workshop update; our maps' Workshop items | players fetch a new HUD version when they join |

Without `--apply`, the copying scripts only show what would change. Every script ships committed files only.

## The bridge image

The compose file names the image under `localhost/`, a registry no pull can reach, and never pulls it:

```yaml
  bridge:
    image: localhost/overstrike2-bridge
    pull_policy: never
```

`bridge.py build` makes `localhost/overstrike2-bridge:<commit>` (immutable) and points `:candidate` at it; `bridge.py swap` points
`:latest` - the tag the compose file means - at the candidate and recreates the container from its own configuration, so a
later redeploy of the stack runs the same image. `:previous` is what the last swap replaced (`bridge.py rollback`).
