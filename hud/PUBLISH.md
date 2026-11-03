# Building and publishing the HUD addon

The HUD is **one** Steam Workshop item, **3823993566** ([`workshop_id.txt`](workshop_id.txt)), Public. A release is a content
update of that item - never a new item, under any name (see [AGENTS.md](../AGENTS.md#cs2-in-game-hud--never-create-another-workshop-item)).
Every new version waits in Valve's moderation queue, so publish only with the owner's go for that release, bundle every waiting
layout change into it, and publish while nobody plays.

Everything here runs on Windows, with Counter-Strike 2 installed and Steam running and logged in.

## 1. Build

1. Install the **Counter-Strike 2 Workshop Tools** if `game\bin\win64\resourcecompiler.exe` is missing from the CS2 folder:
   start CS2, Settings, search for "Install Counter-Strike Workshop Tools", then let Steam download them.
2. The map pictures: `python hud\thumbs.py --ahead maps.json` (needs Pillow) makes one small JPEG per map of `bridge/maps.json` in
   `hud\thumbs\`, plus one per map the `--ahead` file names (a JSON array of `{"id", "preview"}`, most popular first: the maps the
   sync has queued and the other modes' maps, so a map has its picture before it is listed). All from the maps' Workshop previews, on
   Steam's image CDN. They are other authors' pictures and are never committed. The addon holds 16,384 pictures at most; a release
   brings about 113 MB for 9,200 of them.
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

While an update waits for Valve, go easy on the item's page: nothing may risk the owner's address or Steam account. The Web API (`time_updated`,
`hcontent_file`) is the signal, and the plugin's `css_gghud` says what it knows without a request from you. At most one visit to the page every 15
minutes, and after a 429 none for an hour; the plugin keeps to the same. Reviews have taken 9 to 63 minutes.

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
6. **Dislike** a map: the reasons stand where it was clicked (content v4_4_0) - over a card's picture, in the window in place of Like and
   Dislike, on the TAB board - and a dislike typed in chat with the window closed gets the popup at the left. No Too big or Ads buttons.
7. **The Sync tab** (content v4_3_0): the Workshop sync's dashboard - now, the queue, the history; the pagers page the queue, the runs
   and the map events. Every word on it is the bridge's (bridge/HudDash.cs).
8. **The vote** (content v4_4_0): a match end or Next opens it on the Maps page - at least eight cards, each with its mode's label and
   colour, Keep playing first when Next started it, Random's button and the gold Open the map browser beside the filters, the pager and
   its gold hint when there are more cards than a page holds; a Play in the browser puts its map on the vote.
9. **The Fun tab**'s Random and Default (content v4_4_0), and the Maps page's Our maps button, which glows slowly until it is chosen.

## Visibility

Keep the item **Public**. Other players' games refuse to download an Unlisted, Friends-only or Private item, and while an item or
its newest version waits for moderation, everybody but its owner gets an error page instead of it - which is why the plugin
checks the item and never disconnects players over the HUD while Steam does not show it to everybody.

## Updating the layout later

CS2 caches a layout by its path for a whole game session, so a layout release needs new file names: raise `Hud.Release` in
`bridge/Hud.cs` and rename `layout/gungame_hud_<release>.xml` and the stylesheets to match (`thumbs.py`, `build.ps1`, the plugin
and `--selftest` follow `Hud.Release`; a content update within a release raises `Hud.Content` and `HudVote.ContentLevel` instead,
and copies the layout it replaces into `reference/`, which `--selftest` holds the new one to). Build, publish while nobody plays,
wait for Valve's approval, then ship the plugin built for it and restart the game server at 0 players: the plugin's level is what
tells a game holding the older content to update, so it never ships before the content does.

Steam on a player's PC looks for changed items every 45 minutes and then updates the item by itself, so for up to about an hour after the
approval a game may still hold the older content (31 and 40 minutes on this project's own PC): a look in a real client right after the approval
can show the older HUD and say nothing about the new one.
