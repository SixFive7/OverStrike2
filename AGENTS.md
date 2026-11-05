# Over Strike 2 - repository guide

Rules for anyone (and any agent) changing this repository. The project itself is described in [README.md](README.md) and,
in full, in [docs/reference.md](docs/reference.md).

## This repository is public

The server it runs on is not. Nothing about the deployment may enter this repository: no passwords, keys or tokens; no IP or
MAC addresses; no host names, domains or subdomains of the deployment; no host paths, share names, container names or network
layout; no local paths of a developer's machine; no Steam ids or player names. Placeholders stand in for them -
`play.example.com` for the address players connect to - and the real values live in the private config (`.private/`, gitignored;
see [deploy/README.md](deploy/README.md)), from which the deploy scripts read them.

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
- **No other authors' pictures.** Map thumbnails (`hud/thumbs/`) are made from Workshop previews and are never committed. The
  one exception: the documentation's screenshots show the HUD as players see it, previews included (see the documentation below).
- **No other authors' text.** The data files keep each Workshop item's title, id, tags and size - never its description (people
  put addresses, handles and worse in them). Strip descriptions whenever the data is regenerated, and never take a server's
  `search.json` into this repository: `bridge/search-index.py` keeps the committed one in step with `maps.json`.

## Build and test before every commit that touches code

From the repository's root:

```sh
(cd bridge && dotnet build && dotnet run -- --selftest)         # must end SELFTEST PASSED
(cd plugin/OverStrike2 && dotnet build -c Release)              # zero warnings: warnings are errors
(cd plugin/OverStrike2.SelfTest && dotnet run)
(cd plugin/CatchUp/SelfTest && dotnet run)
dotnet run --project core/SelfTest                              # the settings file and what the parts share
dotnet run --project server/OverStrike2.Server/SelfTest         # the server program; its Linux-only checks in the game image (docs/server.md)
dotnet run --project server/OverStrike2.Sync/SelfTest           # the Workshop sync's program; its Linux-only checks in the game image too
python bridge/search-index.py --check
python -I deploy/test_deploy.py                                 # the deploy scripts, with no server and no Docker
python -I deploy/test_release.py
```

The rules are said in the menu's How it works and Help pages, and `--selftest` holds them to the code, number for number
(docs/reference.md, "Keeping the rules in sync"): a rule change changes the code and the words together.

## Shipping

Only through [deploy/](deploy/), with the private config, and only committed code. What restarts what: data files reload by
themselves; a bridge swap disconnects nobody; the plugin, `pre.sh` and the game server's settings need a game server restart,
which disconnects everybody: by hand only at 0 players; the watcher's announced restart is exempt, at once with nobody on, at the end
of the match with players on (docs/server.md).

## cs2 in-game HUD — NEVER create another Workshop item

Never publish another HUD Workshop item, under any name, and never publish a new version of the live one without the owner's
go, standing or per release. Valve holds new Workshop items and new versions for moderation, and near-identical new items in place of
an update are exactly what Valve frowns on: while one is held, players cannot download the HUD.

- The one HUD item is **3823993566** ([hud/workshop_id.txt](hud/workshop_id.txt)). A release is a content update of it; the
  publisher has no create path.
- If a layout change looks necessary: ask the owner before running the publisher, spell out the moderation hold, and bundle
  every waiting layout change into that one release. Ship updates while nobody plays.
- Deleting an old, unused HUD item at the owner's request is not a replacement.

Our own maps follow the same rule: a new version of a map item only with the owner's go
([maps/publisher/](maps/publisher/)).

## The documentation shows the whole HUD - keep it current with every change

The owner reviews the HUD from its documentation instead of in the game, and gives corrections from it, so it has to show
everything and match the code:

- **Every part of the interface, with screenshots:** every page, tab and state - the board, the menu window and each of its
  pages, the vote and its ballot states, the map browser, the Fun page, the Sync page and whatever comes next - so anyone can see
  how the HUD looks in game before installing it. The index lives in [README.md](README.md); the pages and their pictures live
  under [docs/](docs/), not all in the README.
- **Every kind of on-screen message, in one gallery:** a single self-contained HTML file with a screenshot of each notification
  type - how it looks and where on the screen it appears - from which the owner picks the types to keep and how to merge them.
- **Updated in the same change:** a change that alters what players see updates the affected screenshots and text in the same
  release. It is not done until its documentation matches.
- **Taken by script, on a test copy:** screenshots come from a real game client against a test copy of the server, never
  production, driven by script so every page and state can be reached without anybody playing.
- **Still public:** test names only - no player names, Steam ids, addresses or anything else of the deployment in a picture.
  Workshop map previews may show in these screenshots as the HUD shows them - the one exception
  to "No other authors' pictures", which still holds for everything else. Every picture is a binary file, reviewed into
  [tools/scan/allow.txt](tools/scan/allow.txt) with its hash.

## Scratch

Local scratch goes in `.work/` (gitignored), one folder per task.
