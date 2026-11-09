# Building and publishing the HUD addon

The HUD is **one** Steam Workshop item, **3823993566** ([`workshop_id.txt`](workshop_id.txt)), Public. A release is a content
update of that item - never a new item, under any name (see [AGENTS.md](../AGENTS.md#cs2-in-game-hud--never-create-another-workshop-item)).
Every new version waits in Valve's moderation queue, so publish only with the owner's go, standing or per release, bundle every waiting
layout change into it, and publish while nobody plays.

Everything here runs on Windows, with Counter-Strike 2 installed and Steam running and logged in.

## 1. Build

1. Install the **Counter-Strike 2 Workshop Tools** if `game\bin\win64\resourcecompiler.exe` is missing from the CS2 folder:
   start CS2, Settings, search for "Install Counter-Strike Workshop Tools", then let Steam download them.
2. The map pictures: `python hud\thumbs.py --export export.json` (needs Pillow) makes one small JPEG per map in `hud\thumbs\`.
   `export.json` is what `os2-sync export` prints on the server: every mode's list with its previews, and the discovery stock, first in
   line. A map the sync's verdict says is gone from the Workshop (`ws`: removed, hidden, banned) gets no picture and nothing is asked for it:
   its preview went with it. Without `--export`: `bridge/maps.json`, with every preview asked of the Web API. `--ahead FILE` (a JSON array of `{"id", "preview"}`,
   most popular first) adds maps that are not listed yet, so a map has its picture before it is listed. All from the maps' Workshop
   previews, on Steam's image CDN. They are other authors' pictures and are never committed. An official map's picture is the game's own
   screenshot. The addon holds 16,384 pictures at most; a release brings about 113 MB for 9,200 of them.
3. The knife icon: `hud\images\knife.png` is in the repository, an original drawing (no game art): 512 x 256, white on transparent,
   tip to the right, which the stylesheet washes gold. A new one is a binary file, reviewed into `tools\scan\allow.txt` with its hash.
4. `powershell -File hud\build.ps1` copies the layout, the stylesheets, the images and the pictures into the Workshop Tools'
   content folder and compiles them into `game\csgo_addons\gungame_hud\`. It must end with an `OK` line for the layout, each
   stylesheet, the knife and the pictures. `-Local` also copies the result into your own client to try it before publishing;
   `-NoThumbs` builds without pictures.
5. After a publish: `build.ps1 -MarkShipped` records the shipped pictures in `thumbs-shipped.txt` - commit it.

## 2. Publish: a content update of 3823993566, with the owner's go

```powershell
powershell -File hud\publish.ps1 -Query                                   # the item as Steam has it
powershell -File hud\publish.ps1 -Public                                  # whether a visitor sees it (moderation): a visit to the page, see below
powershell -File hud\publish.ps1 -Note "What changed" -UserApprovedUpdate # ONLY with the owner's go, standing or per release
```

`publish.ps1` builds the publisher (`publisher\`, the Steamworks UGC API through CS2's own `steam_api64.dll`; it never sees a
password: it attaches to the running Steam client), packs the built addon and uploads it as a content update. Its Steam calls and the VPK it packs
are the ones the maps' publisher uses ([`tools/workshop/`](../tools/workshop/)), which declare no create path; `build.ps1` and `publish.ps1` find CS2
the same way too. It refuses without
`-UserApprovedUpdate`, without a change note, and whenever `workshop_id.txt` names no item. The title and the description live in
`publisher\Program.cs` (`Title`, `Description`) and are set again on every update, so a change to them goes out with the next
content update; the item's preview image is `hud\workshop_preview.jpg`: the Fun page as a test client drew it, on a plain ground, with no other
authors' pictures.

`os2-deploy release publish` drives all of it with the private config: it asks for the real-client test's run first, checks that the live server
recognises Steam's page for a held item, publishes, and waits for the upload and Valve's approval ([deploy/README.md](../deploy/README.md)).

While an update waits for Valve, go easy on the item's page: nothing may risk the owner's address or Steam account. The Web API (`time_updated`,
`hcontent_file`) is the signal, and the plugin's `os_hud` says what it knows without a request from you. At most one visit to the page every 15
minutes, and after a 429 none for an hour; the plugin keeps to the same. Reviews have taken 9 to 63 minutes.

## 3. The server's setting

`hud.addon` in the settings file ([docs/settings.md](../docs/settings.md)) names the item, and is required: without a usable one the game server does not start. MultiAddonManager
has the server mount it, so every player who joins gets the addon (they do not subscribe) and keeps it across every map change, and the plugin draws the menu
([docs/server.md](../docs/server.md#the-hud-addon)). An update keeps the id, so the setting stays as it is.

## 4. What to look at in game

The same list for every release, in a real client against a test copy of the server: [tools/client/](../tools/client/README.md#testing-an-unpublished-hud-build)
runs it, its run folder is what `os2-deploy release publish --client-test` asks for. What the release brings goes in the change note and the Workshop description.

1. **A join:** the welcome opens by itself on START and stays until its X, through damage, death and a respawn. The X has "Close this to play" beside it while the server waits for this
   game's first click, and that click shows the HUD's version on SERVER, Players.
2. **Hold TAB:** the board beside the scoreboard takes clicks while TAB is down - Like, Dislike, Start a vote, the way into the menu - with Lately at its foot, and letting go gives the mouse back.
3. **TAB + E:** the window, on its five tabs and their sub-tabs, closing by itself after 30 s without a click.
4. **MAPS, Browse:** eight cards with their pictures, the mode's label and colour, size, likes and every tag. The sorts and the filters; the search area's hint, Press Y and type a map name, whose
   click says how on the window's line and in chat; a chat line typed on Browse searches it when its words find a map (gg and the like do not), and says so on the window's line when
   they find none; `!s` and a few words in chat open the browser on the matches from anywhere; a Workshop id typed in chat gets its card, with Download and play.
5. **Start a vote:** everybody's window opens on MAPS, Vote, and nobody can move, shoot or close it. Ten cards, Keep playing first; Random's button; where the votes go, who has
   not voted yet and both timers. A click votes, another moves the vote, a click on your own takes it back. Browse puts a map on the vote, with your vote.
6. **The vote's end:** Keep says so for 8 s, Closes in counting down, then everybody unfreezes; another map counts down with everybody frozen. At a match end Keep reads Play this map again, and nobody voting is Random.
7. **Dislike** a map: the reasons in one small dialog at the centre, shown only while you have a cursor, the window's or TAB's; a dislike typed in chat with the window closed shows it once TAB is held.
8. **FUN:** every row with its choices, a setting off its default standing out with who set it, every change said for everybody; Random and Default; Moon gravity greying Super jump
   High with the reason on hover, and a click made just as the other setting changed refused with a bubble at the button. In Deathmatch and Wingman the rows that are Arms Race's own are gone.
   **FUN, Bots:** the sub-tabs Settings and Bots; every row with its choices, bots joining as set, who set a row; the presets lit when they show; Kick all bots and Default;
   Arms Race greying the Weapons row and Wingman the counts past its room, each with its reason.
9. **SERVER:** Players with everybody's HUD version; Game with the match length and both restarts, the server's and the match's; Sync - now, the queue, the history, the pagers. Every word on Sync is the bridge's
   (`bridge/Shared/HudDash.cs`).
10. **HELP:** Help and How it works, every row filled.
11. **`!help`** in chat: the chat commands; a pasted Workshop link opens that item's card.
12. **A map change:** the countdown banner says what comes and who picked it, with the map's picture; at the first spawn the now-playing card under the crosshair.
13. **An older game:** its banner says it is out of date and names both versions (`os_hud level` fakes a newer server), and a game holding the older content still works.
14. **Every message** of the [catalogue](../docs/reference.md#in-game-hud) against the gallery in the [README's index](../README.md#the-hud-page-by-page): how it looks, and where on the screen.

## Visibility

Keep the item **Public**. Other players' games refuse to download an Unlisted, Friends-only or Private item, and while an item or
its newest version waits for moderation, everybody but its owner gets an error page instead of it - which is why the plugin
checks the item and never disconnects players over the HUD while Steam does not show it to everybody
([The item watch](../docs/reference.md#the-huds-version-and-the-hud-check)).

## Updating the layout later

CS2 caches a layout by its path for a whole game session, so a layout release needs new file names: raise `Hud.ReleaseFile` in
`bridge/Shared/Hud.cs` and rename `layout/gungame_hud_<ReleaseFile>.xml` and the stylesheets to match (`thumbs.py`, `build.ps1`, the plugin
and `--selftest` follow `Hud.ReleaseFile`; those names keep the old form, `v3_0_0`, because every shipped content has only those files). A content update keeps the path and the file names. It adds one line to `HudVersion.Contents` in `bridge/Shared/HudVersion.cs`, its version and the next level (after v4.7.0 comes 48, whether the version is v4.8.0, v4.7.1 or v14.5.27), which `HudVersion.Content` and `HudVersion.Level` read. It puts that version in the layout, in its words and its version button's id, starts the stylesheet's banner rules one level higher,
and copies the layout it replaces into `reference/`, which `--selftest` holds the new one to panel by panel, with the layouts of the contents still drawn for
(`HudVersion.Oldest` on: v4.8.0 now, the first whose panels are named `os_`; raising it is what drops an older content, which then gets no live HUD until
Steam updates it). `reference/` keeps those and no other, and `--selftest` fails on one missing: a layout no check reads any more leaves it, and git keeps
it. Since release 193 nothing goes out under the `gg_` names of the contents before v4.8.0. Build, publish while nobody plays,
wait for Valve's approval, then ship the plugin built for it and restart the game server at 0 players (`os2-deploy release live`): the plugin's level is what
tells a game holding the older content to update, so it never ships before the content does.

**How far a content's outdated banner reaches.** The server names the newest content it knows as one class, `latest-<level>`, and a content's stylesheet has a rule for each level above its own up to `HudVersion.LatestLast`, now 99. A shipped stylesheet never changes, and every release takes a level, patches included. So v4.7.0 shows its banner for the next 52 releases, levels 48 to 99. A game still holding it at level 100 shows none, though it keeps working and the Players page still calls it older.
At every release, count what is left from the new content's level up to `LatestLast`. Raising `LatestLast` means adding the rules to the new content's stylesheet, which widens that content and later ones, never one already shipped. `--selftest` fails once a content's level reaches `LatestLast`, because then its own banner could never show.

Steam on a player's PC looks for changed items every 45 minutes and then updates the item by itself, so for up to about an hour after the
approval a game may still hold the older content (31 and 40 minutes on this project's own PC): a look in a real client right after the approval
can show the older HUD and say nothing about the new one.
