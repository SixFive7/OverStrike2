# Building and publishing the HUD addon

The HUD is **one** Steam Workshop item, **3823993566** ([`workshop_id.txt`](workshop_id.txt)), Public. A release is a content
update of that item - never a new item, under any name (see [AGENTS.md](../AGENTS.md#cs2-in-game-hud--never-create-another-workshop-item)).
Every new version waits in Valve's moderation queue, so publish only with the owner's go for that release, bundle every waiting
layout change into it, and publish while nobody plays.

Everything here runs on Windows, with Counter-Strike 2 installed and Steam running and logged in.

## 1. Build

1. Install the **Counter-Strike 2 Workshop Tools** if `game\bin\win64\resourcecompiler.exe` is missing from the CS2 folder:
   start CS2, Settings, search for "Install Counter-Strike Workshop Tools", then let Steam download them.
2. The map pictures: `python hud\thumbs.py --export export.json` (needs Pillow) makes one small JPEG per map in `hud\thumbs\`.
   `export.json` is what `os2-sync export` prints on the server: every mode's list with its previews, and the discovery stock, first in
   line. Without `--export`: `bridge/maps.json`, with every preview asked of the Web API. `--ahead FILE` (a JSON array of `{"id", "preview"}`,
   most popular first) adds maps that are not listed yet, so a map has its picture before it is listed. All from the maps' Workshop
   previews, on Steam's image CDN. They are other authors' pictures and are never committed. An official map's picture is the game's own
   screenshot. The addon holds 16,384 pictures at most; a release brings about 113 MB for 9,200 of them.
3. The knife icon: `hud\images\knife.png` is in the repository, an original drawing (`python hud\knife.py` draws it again; no game art).
4. `powershell -File hud\build.ps1` copies the layout, the stylesheets, the images and the pictures into the Workshop Tools'
   content folder and compiles them into `game\csgo_addons\gungame_hud\`. It must end with an `OK` line for the layout, each
   stylesheet, the knife and the pictures. `-Local` also copies the result into your own client to try it before publishing;
   `-NoThumbs` builds without pictures.
5. After a publish: `build.ps1 -MarkShipped` records the shipped pictures in `thumbs-shipped.txt` - commit it.

## 2. Publish: a content update of 3823993566, with the owner's go

```powershell
powershell -File hud\publish.ps1 -Query                                   # the item as Steam has it
powershell -File hud\publish.ps1 -Public                                  # whether a visitor sees it (moderation): a visit to the page, see below
powershell -File hud\publish.ps1 -Note "What changed" -UserApprovedUpdate # ONLY with the owner's go for this release
```

`publish.ps1` builds the publisher (`publisher\`, the Steamworks UGC API through CS2's own `steam_api64.dll`; it never sees a
password: it attaches to the running Steam client), packs the built addon and uploads it as a content update. It refuses without
`-UserApprovedUpdate`, without a change note, and whenever `workshop_id.txt` names no item. The title and the description live in
`publisher\Program.cs` (`Title`, `Description`) and are set again on every update, so a change to them goes out with the next
content update; the item's preview image is `hud\workshop_preview.jpg`: the Fun page as a test client drew it, on a plain ground, with no other
authors' pictures.

`deploy/release.py publish` drives all of it with the private config: it asks for the real-client test's run first, checks that the live server
recognises Steam's page for a held item, publishes, and waits for the upload and Valve's approval ([deploy/README.md](../deploy/README.md)).

While an update waits for Valve, go easy on the item's page: nothing may risk the owner's address or Steam account. The Web API (`time_updated`,
`hcontent_file`) is the signal, and the plugin's `css_gghud` says what it knows without a request from you. At most one visit to the page every 15
minutes, and after a 429 none for an hour; the plugin keeps to the same. Reviews have taken 9 to 63 minutes.

## 3. The server's setting

`hud.addon` in the settings file ([docs/settings.md](../docs/settings.md)) names the item. MultiAddonManager has the server mount it, so every player who
joins gets the addon (they do not subscribe) and keeps it across every map change, and the plugin draws the menu ([docs/server.md](../docs/server.md#the-hud-addon)).
An update keeps the id, so the setting stays as it is.

## 4. What to look at in game

The same list for every release, in a real client against a test copy of the server. What the release brings goes in the change note and the Workshop description.

1. **Hold TAB:** the board beside the scoreboard takes clicks while TAB is down - Like, Dislike, Start a vote, the way into the menu - and letting go gives the mouse back.
2. **TAB + E:** the window, on its five tabs and their sub-tabs. START: the welcome.
3. **MAPS, Browse:** eight cards with their pictures, the mode's label and colour, size, likes and every tag. The sorts and the filters; the search box opens the on-screen
   keyboard; a typed Workshop id gets its card, with Download and play.
4. **Start a vote:** everybody's window opens on MAPS, Vote, and nobody can move, shoot or close it. Ten cards, Keep playing first; Random's button; where the votes go, who has
   not voted yet and both timers. A click votes, another moves the vote, a click on your own takes it back. Browse puts a map on the vote, with your vote.
5. **The vote's end:** Keep unfreezes everybody at once; another map counts down with everybody frozen. At a match end Keep reads Play this map again, and nobody voting is Random.
6. **Dislike** a map: the reasons stand where it was clicked - over a card's picture, in the window in place of Like and Dislike, on the TAB board - and a dislike typed in chat with
   the window closed gets the popup at the left.
7. **FUN:** every row with its choices, a setting off its default standing out with who set it, every change said for everybody; Random and Default; Moon gravity refusing Super jump
   High with the reason. In Deathmatch and Wingman the rows that are Arms Race's own are gone.
8. **SERVER:** Players with everybody's HUD version; Game with the match length and Restart; Sync - now, the queue, the history, the pagers. Every word on Sync is the bridge's
   (`bridge/HudDash.cs`).
9. **HELP:** Help and How it works, every row filled.
10. **`!help`** in chat: the chat commands; a pasted Workshop link opens that item's card.
11. **A map change:** the screen says what comes and who picked it, and the banner counts down; at the first spawn the now-playing card.
12. **An older game:** the HUD says it is out of date (`css_gghud level` fakes a newer server), and a game holding the older content still works.
13. **Every message** against the gallery in the [README's index](../README.md#the-hud-page-by-page): how it looks, and where on the screen.

## Visibility

Keep the item **Public**. Other players' games refuse to download an Unlisted, Friends-only or Private item, and while an item or
its newest version waits for moderation, everybody but its owner gets an error page instead of it - which is why the plugin
checks the item and never disconnects players over the HUD while Steam does not show it to everybody
([The item watch](../docs/reference.md#the-huds-version-and-the-hud-check)).

## Updating the layout later

CS2 caches a layout by its path for a whole game session, so a layout release needs new file names: raise `Hud.Release` in
`bridge/Hud.cs` and rename `layout/gungame_hud_<release>.xml` and the stylesheets to match (`thumbs.py`, `build.ps1`, the plugin
and `--selftest` follow `Hud.Release`). A content update keeps the path and the file names: it raises `Hud.Content` and `HudVote.ContentLevel` instead,
and copies the layout it replaces into `reference/`, which `--selftest` holds the new one to. Build, publish while nobody plays,
wait for Valve's approval, then ship the plugin built for it and restart the game server at 0 players (`deploy/release.py live`): the plugin's level is what
tells a game holding the older content to update, so it never ships before the content does.

Steam on a player's PC looks for changed items every 45 minutes and then updates the item by itself, so for up to about an hour after the
approval a game may still hold the older content (31 and 40 minutes on this project's own PC): a look in a real client right after the approval
can show the older HUD and say nothing about the new one.
