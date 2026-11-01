# Over Strike 2 - repository guide

Rules for anyone (and any agent) changing this repository. The project itself is described in [README.md](README.md) and,
in full, in [docs/reference.md](docs/reference.md).

## This repository is public

The server it runs on is not. Nothing about the deployment may enter this repository: no passwords, keys or tokens; no IP or
MAC addresses; no host names, domains or subdomains of the deployment; no host paths, share names, container names or network
layout; no local paths of a developer's machine; no Steam ids or player names. Placeholders stand in for them -
`overstrike.example.com` (the site), `play.example.com` (the address players connect to) - and the real values live in the
private config (`.private/`, gitignored; see [deploy/README.md](deploy/README.md)), from which the deploy scripts fill them in.

- **Commit in UTC** (`TZ=UTC git commit ...`): a commit's timestamp carries its author's time zone.
- **The hooks are mandatory.** `git config core.hooksPath .githooks` once per clone. Pre-commit scans what is staged, pre-push
  scans the commits being pushed ([tools/scan/scan.py](tools/scan/scan.py)). Never `--no-verify`, never disable a hook.
- **A finding is fixed at its cause**: scrub the text, or move the value into the private config. An entry in
  [tools/scan/allow.txt](tools/scan/allow.txt) is only for text that is genuinely not private (a version number that looks
  like an address, a constant that looks like a Steam id), each with its reason. Exceptions that name private text go in the
  private denylist, never here.
- **No game files.** Never commit anything extracted from Counter-Strike 2 (textures, models, compiled resources `*_c`,
  `.vpk` packages, icons): a build reads it from the local installation at build time. Every binary file must be reviewed into
  `allow.txt` with its hash.
- **No other authors' pictures.** Map thumbnails (`hud/thumbs/`, the site's `img/`) are made from Workshop previews and are
  never committed.
- **No other authors' text.** The data files and the site keep each Workshop item's title, id, tags and size - never its
  description (people put addresses, handles and worse in them). Strip descriptions whenever the data is regenerated:
  `search.json` is rebuilt from the site's rows, which carry none.

## Build and test before every commit that touches code

```sh
cd bridge && dotnet build && dotnet run -- --selftest      # must end SELFTEST PASSED
cd plugin/OverStrike2 && dotnet build -c Release            # zero warnings: warnings are errors
cd plugin/OverStrike2.SelfTest && dotnet run
docker run --rm -v "$PWD/server:/repo:ro" -w /repo python:3.9-slim python test_update_watch.py   # Linux only
python bridge/search-index.py --check
```

The rules are said in three places - the menu's How it works and Help pages, and the site's guide - and `--selftest` holds
all three to the code, number for number (docs/reference.md, "Keeping the rules in sync"): a rule change changes the code,
the words and the site together.

## Shipping

Only through [deploy/](deploy/), with the private config, and only committed code. What restarts what: data files reload by
themselves; a bridge swap disconnects nobody; the plugin, `pre.sh` and the game server's settings need a game server restart,
which disconnects everybody - only at 0 players.

## cs2 in-game HUD — NEVER create another Workshop item

Never publish another HUD Workshop item, under any name, and never publish a new version of the live one without the owner's
go for that release. Valve holds new Workshop items and new versions for moderation, and near-identical new items in place of
an update are exactly what Valve frowns on: while one is held, players cannot download the HUD.

- The one HUD item is **3823993566** ([hud/workshop_id.txt](hud/workshop_id.txt)). A release is a content update of it; the
  publisher has no create path.
- If a layout change looks necessary: ask the owner before running the publisher, spell out the moderation hold, and bundle
  every waiting layout change into that one release. Ship updates while nobody plays.
- Deleting an old, unused HUD item at the owner's request is not a replacement.

Our own maps follow the same rule: a new version of a map item only with the owner's go
([maps/publisher/](maps/publisher/)).

## Scratch

Local scratch goes in `.work/` (gitignored), one folder per task.
