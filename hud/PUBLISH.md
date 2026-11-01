# Building and publishing the HUD addon

The HUD is **one** Steam Workshop item, **3823993566** ([`workshop_id.txt`](workshop_id.txt)), Public. A release is a content
update of that item - never a new item, under any name (see [AGENTS.md](../AGENTS.md#cs2-in-game-hud--never-create-another-workshop-item)).
Every new version waits in Valve's moderation queue, so publish only with the owner's go for that release, bundle every waiting
layout change into it, and publish while nobody plays.

Everything here runs on Windows, with Counter-Strike 2 installed and Steam running and logged in.

## 1. Build

1. Install the **Counter-Strike 2 Workshop Tools** if `game\bin\win64\resourcecompiler.exe` is missing from the CS2 folder:
   start CS2, Settings, search for "Install Counter-Strike Workshop Tools", then let Steam download them.
2. The map pictures: `python hud\thumbs.py` (needs Pillow) makes one small JPEG per map of `bridge/maps.json` in `hud\thumbs\`,
   from the site's thumbnails (`--src <folder>`, or downloaded from `SITE_URL`'s `img/`). They are other authors' Workshop
   previews and are never committed. `python deploy\workshop.py hud-thumbs` runs it with the site from the private config.
3. The knife icon: `hud\images\knife.png` is not in the repository (it is derived from the game's own HUD art). Put your copy
   there; the path is gitignored.
4. `powershell -File hud\build.ps1` copies the layout, the stylesheets, the images and the pictures into the Workshop Tools'
   content folder and compiles them into `game\csgo_addons\gungame_hud\`. It must end with an `OK` line for the layout, each
   stylesheet, the knife and the pictures. `-Local` also copies the result into your own client to try it before publishing;
   `-NoThumbs` builds without pictures.
5. After a publish: `build.ps1 -MarkShipped` records the shipped pictures in `thumbs-shipped.txt` - commit it.

## 2. Publish: a content update of 3823993566, with the owner's go

```powershell
powershell -File hud\publish.ps1 -Query                                   # the item as Steam has it
powershell -File hud\publish.ps1 -Public                                  # whether a visitor sees it (moderation)
powershell -File hud\publish.ps1 -Note "What changed" -UserApprovedUpdate # ONLY with the owner's go for this release
```

`publish.ps1` builds the publisher (`publisher\`, the Steamworks UGC API through CS2's own `steam_api64.dll`; it never sees a
password: it attaches to the running Steam client), packs the built addon and uploads it as a content update. It refuses without
`-UserApprovedUpdate`, without a change note, whenever `workshop_id.txt` names no item, and while the item's description still
names the placeholder site: set `SITE_URL` to your site (`python deploy\workshop.py hud-publish` does). The title and the
description are set again on every update; the item's preview image is `hud\workshop_preview.jpg`, which is kept out of the
repository (gitignored) until a new one is made.

## 3. The server's switch

`GUNGAME_HUD_ADDON` on the game server names the item. While it is set, MultiAddonManager hands the addon to every player who
joins (they do not subscribe) and the plugin draws the menu; empty, the plugin shows a simple text panel. An update keeps the id,
so the setting stays as it is.

## 4. What to look at in game

1. **Hold TAB**: the board beside the scoreboard takes clicks while TAB is down - Like / Dislike, Next, the queue.
2. **TAB + E**: the window, on Maps - eight maps with their pictures, number, size and votes; the search box opens the on-screen
   keyboard.
3. **`!help`** in chat: the chat commands; a pasted Workshop link opens that item's card.
4. A map change: the news line says who picked what, and the banner counts down.
5. **The Fun tab**: the fun settings, the one in force lit, and the grenade drops above Big grenades.
6. **Dislike** a map: the popup at the left lists the reasons, and takes a click while TAB is held or the window is open.

## Visibility

Keep the item **Public**. Other players' games refuse to download an Unlisted, Friends-only or Private item, and while an item or
its newest version waits for moderation, everybody but its owner gets an error page instead of it - which is why the plugin
checks the item and never disconnects players over the HUD while Steam does not show it to everybody.

## Updating the layout later

CS2 caches a layout by its path for a whole game session, so a layout release needs new file names: raise `Hud.Release` in
`bridge/Hud.cs` and rename `layout/gungame_hud_<release>.xml` and the stylesheets to match (`thumbs.py`, `build.ps1`, the plugin
and `--selftest` follow `Hud.Release`; a content update within a release raises `Hud.Content` instead). Build, publish while
nobody plays, wait for Valve's approval, then ship the plugin built for it and restart the game server at 0 players.
