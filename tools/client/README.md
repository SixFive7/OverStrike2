# CS2 client test harness

Runs the CS2 client of whoever works at this PC for tests, **with no window on their screen, no sound and no focus**, at below-normal
priority, while they keep working on the same PC. It launches CS2, joins a server, takes screenshots, reads the console log and quits
cleanly, and it puts back every config file CS2 rewrites. With it a HUD build is tried in a real client before a release
([hud/PUBLISH.md](../../hud/PUBLISH.md)), and the documentation's screenshots are taken by script ([docshots/](docshots/)).

Windows only: Steam installed and logged in, PowerShell 7 (`pwsh`), Python 3.12+ (Pillow for the screenshots' masks), and the .NET SDK
for the two test plugins. Everything that touches the server reads the deployment from the **private config** - the same file the deploy
scripts use ([deploy/README.md](../../deploy/README.md)): `OVERSTRIKE2_CONFIG`, else `.private/config.toml`, its credentials `env:`
values in the environment. Nothing here prints a credential.

```powershell
pwsh -NoProfile -File tools\client\cs2client.ps1 start -Connect default -Purpose "what you are testing"   # returns once in game; CS2 keeps running
pwsh -NoProfile -File tools\client\cs2client.ps1 shot -Name ingame                                         # PNG in the run folder
pwsh -NoProfile -File tools\client\cs2client.ps1 log -Pattern 'S2C_CONNECTION|Map: "'                     # the game's console log
pwsh -NoProfile -File tools\client\cs2client.ps1 stop                                                      # clean quit, settings restored, lock freed
```

Every verb returns promptly; CS2 and its watchdog run detached and hold none of the caller's handles. From bash the same, with forward
slashes.

## What it reads from the private config

| Key | For |
|---|---|
| `server.address` | `-Connect default`, the A2S checks, the test copy's waits |
| `server.lock_file` | the server lock: taken by `probe.py lock`, checked before every join and every server-side step (the harness refuses a production action without a lock file named) |
| `bridge.game_container`, `bridge.container` | the game server's and the bridge's containers: RCON runs inside the first, the test copy and the candidate bridge take their names |
| `bridge.image`, `bridge.data_dir` | a candidate bridge's image (`<image>:<tag>`); the maintenance flag (`<data_dir>/maintenance`) |
| `docker.*` | the Docker Engine API (or a proxy of it), its key an `env:` value |
| `client.test_labels` | extra labels every test container gets - a container monitor's mute, say (optional) |
| `client.private_names` | names that must never reach a screenshot, a command or a log, beside the client's own Steam name, which is found by itself (optional) |

The RCON password is the game container's own (`CS2_RCONPW` in its inspect): `probe.py` hands it to one exec in its environment and RCON
runs inside the container, so no RCON port has to reach this PC. The bridge's secret comes from the bridge's inspect the same way.

The harness's state - the session, the client lock, the compiled Win32 helper, the run folders and the test copy's saved inspects - lives in
`.work\cs2-client\` of the main checkout (shared by every worktree), or wherever `CS2CLIENT_STATE` points.

## VAC safety (read first)

The account of whoever runs the harness must never be put at risk of a VAC ban. Every rule below exists for that, and none may be weakened.

- **What the harness does to cs2.exe, and nothing else:** starts it (`CreateProcessW` on the hidden desktop, below-normal priority),
  reads its windows (`EnumDesktopWindows`), captures its window (`PrintWindow`), posts `WM_CLOSE`, reads its audio level from the
  Windows audio service, reads its pid, start time and priority (query rights only), sets its priority back to below normal, puts it
  in a job object (quota rights) and, as the last resort of a stop, terminates it. **No memory read or write, no injection, no hook,
  no debugger, no module list** (`Cs2Native.cs`; `Get-Cs2Processes` uses `Get-Process`, not WMI, whose `CommandLine` is read out of
  the target's memory).
- **A secure session is a plain client:** the published HUD from the Workshop, no loose files, no extra content, no extra launch
  options. Only secure sessions ever join production (VAC-secured).
- **Loose files only in an insecure session.** `-HudLocal` forces `-insecure` (VAC off for that session, so it cannot ban anything),
  and an insecure session joins only a server whose A2S says it is **not** VAC-secured - the insecure test copy
  (`probe\testcopy.py`), never production. The game itself also refuses a secure server in insecure mode.
- **Never used:** `-allow_third_party_software` (lowers Trust Factor), `-tools`, edits to `gameinfo.gi`, loose maps, capture or
  overlay tools that hook into cs2.exe (RivaTuner, Afterburner, OBS, Bandicam, ...), memory tools.

**The self-check** (`Test-Cs2Integrity`, run by every `start`; `cs2client.ps1 check` alone) refuses the launch when any of these holds:

| Check | Refused when |
|---|---|
| Launch options | `-tools`, `-allow_third_party_software`, `-nosteam`, `-vconsole`, `-dev`, `-hijack`, `+sv_cheats`; outside an insecure session also `-insecure`, `-netconport`, `-netconpassword`, `-vconport`, `-novconsole` |
| gameinfo | `gameinfo.gi` or `gameinfo_branchspecific.gi` names Metamod, was changed after Steam last updated the game (`appmanifest_730.acf` `LastUpdated`), or differs from what it was earlier in the same Steam build (the baseline in the state folder's `integrity.json`) |
| Loose content | any compiled game file (`*_c`) under `game\csgo` outside the VPKs (the Workshop Tools' own `lighting\postprocessing\editor\` excepted), a `game\csgo\addons` folder |
| Loose maps | any VPK without a signature section in `game\csgo\maps` or `game\csgo` (Valve's map VPKs are v2 and signed; a self-built one is not) |
| Proxy DLLs | `d3d9/10/11/12.dll`, `dxgi.dll`, `dinput8.dll`, `version.dll`, `winmm.dll`, `opengl32.dll`, `vulkan-1.dll`, `winhttp.dll`, `xinput1_3/1_4.dll`, `dsound.dll`, `msimg32.dll`, `wininet.dll` or any `*.asi` beside the game's binaries |
| Hook tools running | RivaTuner (`RTSS`, its hooks loader and encoder), MSI Afterburner, OBS, Bandicam, Fraps, Dxtory, Mirillis Action, Overwolf, Medal, Outplayed, PlayClaw, XSplit, ReShade, Special K, Cheat Engine, x64dbg/x32dbg/OllyDbg/IDA, Process Hacker / System Informer |

Warnings, not refusals: Discord and the NVIDIA App. **Discord's overlay does try to hook a CS2 it sees as visible** - its own
`%APPDATA%\discord\logs\discord_hook.log` records such an attempt. It has never been seen trying a hidden-desktop session: it sees the game
but reports it as `visibleGame=null` and starts nothing. The watchdog follows that hook log and ends our cs2 at the first attempt. The
NVIDIA App's overlay is not visible from here without touching cs2 (Valve tolerates it). The Steam overlay (`gameoverlayrenderer64.dll`) is
Valve's own.

## Rules (read before the first run)

1. **Only one CS2 per Steam account.** `start` refuses while any `cs2.exe` runs (the user's, or anyone's) and never touches a CS2 it
   did not start. If the user starts CS2 while a session runs, the watchdog quits ours within about 2 s (see "The watchdog"). Never
   use `steam.exe -applaunch 730` while a harness session runs: the watchdog takes it as the user and quits.
2. **Before every run that joins production:**
   - the server lock must be free, or held by you for this test. `start -Connect` refuses while it is held (exit 2, the lock's text
     printed) unless you pass `-AllowServerLock` (only when **you** hold it). A test copy of production answers on the same address
     under the same name, so a client can join a test copy while somebody else's test holds the lock.
   - `cs2client.ps1 server` must show the game container as the only one publishing the server's port, and RCON `status` must look like
     production (compare with what you expect, not with an earlier run).
   - say in your report which server each run hit.
3. **Production actions need the server lock**: loading the probe plugin, RCON commands that change anything (`css_guide`,
   `gg_notice`, ...). Read-only RCON (`status`, `css_gghud`, `css_plugins list`) does not. `probe\probe.py lock|unlock` takes and
   releases it with this harness as the owner.
4. Do not set archived convars (`volume`, `fps_max`, binds, ...) with `-Cmd` unless you need to: the files are restored at the
   end, but the cloud copy of the user convars is uploaded by CS2 itself while the session runs (see "Settings").
5. Do not run Hammer, the Workshop Tools or anything with `-tools`.

## Verbs

| Verb | What it does |
|---|---|
| `start [-Connect default\|ip:port] [-Cmd "<console commands, ; separated>"] [-Purpose "..."] [-WaitFor menu\|ingame\|window\|none] [-TimeoutSec 240] [-HudLocal [-HudSource dir \| -HudAddon name] [-IncludeThumbs]] [-Resolution 1920x1080\|native] [-LeaseMinutes 15] [-MaxMinutes 60] [-AllowServerLock]` | Preflight (Steam logged in, no `cs2.exe`, harness free, server lock), backs up the settings, installs a loose HUD build if asked, writes the boot cfg, launches CS2 on the hidden desktop, starts the watchdog (and fails, stopping cs2, if the watchdog does not report within 20 s), and waits. `menu` (the default): the menu state, the signon of the menu's own background map, and a frame that is not black - about 12 s; its intro animation (the "COUNTER STRIKE 2" logo) plays for a few more seconds, so wait ~5 s before a menu screenshot. `ingame` (the default with `-Connect`): after the **last** connection (a join that fetches the HUD addon connects twice) the game state is in game, stays so for 3 s, and a frame is not black - about 15 s. `-Connect default` is the private config's `server.address`. |
| `wait [-WaitFor menu\|ingame] \| -Pattern <regex> [-FromLine n] [-TimeoutSec 60]` | Waits for a state, or for a console-log line. `-FromLine` = a line count `log` printed earlier, to wait only for new lines. |
| `shot [-Name x] [-MaxWidth 1920] [-OutDir dir]` | Screenshot of the game window as PNG (default `<run>\shots\<name>.png`), scaled to `-MaxWidth` (0 = full size). Prints the size and the mean brightness, and a WARNING on a black frame (a map loading: take it again). Each call costs about 2-3 s (a PowerShell start), which matters for messages that last a few seconds. |
| `log [-Tail 40] [-Pattern regex] [-FromLine n]` | Lines of this session's console log, numbered, then `lines: N`. |
| `status` | The session, the window, the lease, the guard's audio and focus figures, the server lock. |
| `quiet` | Proof of quiet right now: input desktop, the user's foreground, what the hidden desktop sees as foreground, cs2 windows on the user's desktop (must be 0), cs2's priority, cs2's audio sessions with their peak meters. |
| `stop [-Reason "..."]` | WM_CLOSE (a normal quit: CS2 disconnects, saves and exits in about 0.5 s), then the clean-up: console log moved to the run folder, boot cfg and loose HUD files removed, settings restored, client lock freed. Prints what it restored and the guard totals. A quit that lands while a map is loading (`SwitchToLoop game` until its `Signon traffic`) hangs after `Source2Shutdown`, so `stop` first lets a load in progress finish (up to 30 s); if WM_CLOSE still has not ended our cs2 after 30 s it is terminated (our pid, start time checked; nothing is lost: a killed CS2 saves nothing and the files are restored anyway). |
| `check [-Insecure]` | The VAC-safety self-check alone (above). Exit 2 when it would refuse. |
| `start ... -Insecure [-Console]` | An insecure session: `-insecure` on the client. It joins only a server whose A2S says VAC off (`-Connect` is refused otherwise, production included); without `-Console` it also gets `-novconsole`, so nothing listens. `-Console` opens netcon (random port, random password, kept in `session.json`) and VConsole (random port) for `cmd`. `-HudLocal` (and `-HudAddon`) implies `-Insecure`. |
| `cmd -Cmd "<console command>" [-WaitMs 1500]` | Insecure sessions started with `-Console` only: runs the command on the client and prints its console output (netcon). |
| `server` | The server lock, the running containers publishing the server's port and the game container (Docker API, read-only), and RCON `status`. |
| `rcon -Rcon "<command>"` | A server console command (RCON inside the game container, `probe.py rcon`). A production action when it changes anything. |
| `cleanup` | Finishes a session whose watchdog died; removes a leftover boot cfg or loose HUD files. `start` does this by itself. |

Exit codes: 0 ok, 1 error, 2 refused (CS2 already running, harness in use, server lock held), 3 timed out.

The state folder holds `session.json`, `client.lock`, the compiled Win32 helper in `bin\` and one folder per session in
`runs\<yyyyMMdd-HHmmss>-<purpose>\`: `console.log`, `shots\`, `watchdog.log`, `guard.json`, `result.json`, `backup\` (the settings as they
were before the launch).

## Testing an unpublished HUD build

The game reads loose compiled Panorama files under `game\csgo\panorama\...` before the Workshop addon's copy of the same path, so a
build can be tried before it is published. **Loose files never meet production** (VAC safety, above): the client runs with
`-insecure`, against an **insecure test copy** of the server that `probe\testcopy.py` puts in production's place for the test.
Production is down meanwhile - only under the server lock, only with nobody on. Panorama caches layouts for the whole game session,
so the files must be in place **before** CS2 starts - the harness installs them as part of `start`.

1. **Build without `-Local`**: `powershell -File hud\build.ps1` ([hud/PUBLISH.md](../../hud/PUBLISH.md), step 1). The harness copies the
   files itself so it can remove exactly them; `start -HudLocal` refuses if loose files from a `-Local` build are already in the game
   folder, and the self-check refuses every secure session while any are there. A build of another commit into an addon folder of its own:
   `docshots\build_hud.ps1`, then `-HudAddon <its name>`.
2. **Take the server lock**: `python -I tools\client\probe\probe.py lock "verify HUD <release>"`; `cs2client.ps1 server`.
3. **The insecure test copy**: `python -I tools\client\probe\testcopy.py prep` (refuses with a human on A2S; sets the bridge's
   maintenance flag; production stopped by Docker - no RCON quit, which lets the restart policy start it again underneath - and
   renamed aside), then `testcopy.py run` (a test copy from production's own inspect - same image, plugins and HUD addon - with
   `-insecure` added; it waits until A2S answers with VAC off). **The plugin of the same commit**, when the HUD build needs it: before
   `run` (before `prep` too: it builds while production still runs), `testcopy.py plugin <commit>` - the commit must be on main. It builds
   the plugin from that commit as committed (`git archive` of `plugin/`, `bridge/` and `core/` into the state folder, `dotnet build -c
   Release`) and stages what `deploy/plugin.py` ships of it in a folder beside production's plugins folder (`<its bind's source>-candidate`,
   written by a short-lived helper container: production's other plugin folders as they are, `OverStrike2` replaced, each file checked
   against the build). `run` then mounts that folder in the place of production's plugins folder, which no test ever writes, and checks
   that the copy's game tree holds the staged DLL and the plugin is loaded. `testcopy.py status` says which plugin a copy gets;
   `plugin-rm` goes back to production's.
4. **Hand out no addon on the copy**, before the client starts: `cs2client.ps1 rcon -Rcon 'mm_extra_addons ""; mm_client_extra_addons "";
   changelevel <the start map>'`, one line - both lists (the server mounts the HUD item through `mm_extra_addons`, older launchers sent it
   through `mm_client_extra_addons`), then a map load. While the server hands the Workshop addon out, the client mounts it and **its** copy
   of the layout wins over the loose files: a client on the copy then shows the published layout with the loose build in place. **A level
   keeps the addons it was loaded with**: emptying the lists alone leaves the level that is on handing the item to every client that joins
   it (`S2C_CONNECTION ... [addons:'<item>']` in the client's log, then `Mounting addon`), and a `changelevel` on its own puts
   `mm_extra_addons` back - emptied in the same line as the map change, the new level has none, and the list stays empty (measured
   2026-11-07) - but on a copy that has just started the plugin writes the list itself at its first look at the item, so read
   `mm_extra_addons` after the load and do it again if it is back. Check the client's log (`cs2client.ps1 log -Pattern S2C_CONNECTION`):
   `[addons:'']`. Start the client only then - Panorama keeps the layout it loaded for the whole game session - and give it
   `-IncludeThumbs`: without the item it has no map pictures but the build's.
5. **Start with the build**: `cs2client.ps1 start -Connect default -HudLocal -AllowServerLock -Purpose "verify HUD <release>"`.
   `-HudLocal` makes it an insecure session; the start checks A2S and refuses unless the server at that address is not VAC-secured.
   It copies every compiled file (`*_c`) under `game\csgo_addons\gungame_hud\panorama\` - layout, stylesheets, textures - into
   `game\csgo\panorama\...`, recording each file and directory in the state folder's `hud-local.json`. The map thumbnails are left out,
   as `build.ps1 -Local` leaves them out (they come from the published addon); `-IncludeThumbs` copies them too (thousands of files).
   `-HudSource <dir>` takes another built addon folder, `-HudAddon <name>` one under `game\csgo_addons\`. Add `-Console` for the client
   console (`cmd`).
6. **What shows up by itself** after `start` returns (in game): the live HUD - the welcome window on its Start page (the bridge sends
   `css_guide` on a real join; it stays until its X), the top line, the countdown banner during a map change. `shot -Name live`.
7. **The window**: `cs2client.ps1 rcon -Rcon "css_guide <account> first"` opens it on its Start page for this account only (what a
   first-time player sees), then `shot -Name window` after 2-3 s. `<account>` is the n of the client's `[U:1:n]`, the account Steam is
   logged in with. While the server waits for this game's first click the window's X is the version button, so
   `css_gghud press <account> <its id>` closes it as a player would (the id is `HudVersion.Button`'s, in the layout beside
   `gg_btn_close`), and `css_gghud release` ends it at once. `gg_notice <seconds> <text>` puts a line on everybody's top line, in chat and
   on the board's Lately list.
8. **Stop the client**: `cs2client.ps1 stop`. Check `loose HUD files: removed N loose HUD file(s) ...`; `cs2client.ps1 check` must say
   OK afterwards (it finds any loose file left).
9. **Production back**: `testcopy.py rm`, then `testcopy.py restore` (production renamed back and started; waits for A2S with VAC ON;
   checks its network endpoint against the inspect saved at prep, and that its start copied its own plugins folder into the game's tree,
   file for file - the copy ran its plugin from the same tree; removes the maintenance flag only when all that holds, and then a staged
   plugin's folder). Then `probe.py unlock`.

**What cannot be done from here:** clicks - the tabs, the buttons, the TAB board, the keyboard. That includes CS2's own **Confirm
Workshop Download** dialog: joining a server that is on a Workshop map this PC does not have, the client asks before fetching it, nobody
answers, and the session sits at `SIGNONSTATE_CONNECTED` until the server times it out (about 2 minutes, `NETWORK_DISCONNECT_TIMEDOUT`).
Test on an official map or one the PC already has (`steamapps\workshop\content\730\<id>`). The hidden client never has input focus, so
nothing that needs a click or a key can be screenshotted unless the server can show it. The server can, through the plugin's `css_gghud`
(`plugin/OverStrike2`): `css_gghud show <account> <page>` opens the window on a page, `css_gghud press <account> <button>` clicks a button
through the real click path (a tab, a filter, a Fun choice, a vote card), `css_gghud content <account> <level>` says which content that
game holds, as if it had clicked, and `css_gghud level <n> <account>` fakes the server's content level for the version banner.
`css_gghud click <account> <button>` only replays a button's bridge command (Start a vote, a like, ...) for that account. In an insecure
session with `-Console`, the client console types chat too: `cmd -Cmd "say !help"`.

**What only the bridge's own callers say: `probe\bridgepost.py`.** The restart watcher's notices - a restart announced or taken back
(`restart`, `restart-cancel`), an update done (`updated`), its quit notice (`quit-notice`) - and other players: fake players who join,
leave, time out, click and vote (`fake-on`, `fake-off`, `fake-crash`, `act`, `vote`), and the real client out of the bridge's lists and back
(`client-off`, `client-on`); and `names-check`, which refuses when a private name - the client's own Steam name, `client.private_names` - is among
the vote's names in the bridge's state, printing none. **A candidate bridge on a test copy only**: before every call it checks the server lock, the copy's label, the
candidate's label and its `-candidate` data, and production and its bridge aside and stopped, and refuses otherwise; no verb takes a route
or free text. The call goes from inside the game container to the bridge's container by name, the secret from the candidate's own inspect,
in the exec's environment, never printed. A notice counting down (`restart ... now`) blocks every vote until `restart-cancel`; a fake
player's second `act restart` quits the server 10 s later unless `quit-notice` comes first.

**Three traps on the test copy:** `css_plugins reload OverStrike2` leaves the old instance's listeners running: one chat line gets several
answers, and the instances remove each other's layout entity (`gghud: no HUD entity`), so test a new plugin on a fresh copy
(`testcopy.py rm`, `testcopy.py plugin <commit>`, `testcopy.py run`). Removing the maintenance flag rolls the dice within a second, and the
roll is usually a Workshop map, which puts the client in the dialog above; `changelevel <official map>`, then `disconnect` and `connect` in
the client's console, gets it back. A press's RCON call returns some seconds after the press has happened, so start a burst of
screenshots (`Save-Cs2Screenshot` in a loop, about 3 a second; 20 a second at 480 px wide through `[Cs2Harness.Native]::CaptureWindow`)
before the press rather than taking one after it.

**Two more on production with a plain client:** a plain client can be refused at the reconnect after a server-side change to a Workshop
map: `NETWORK_DISCONNECT_KICKED_INSECURECLIENT (Secure host does not allow insecure connection.)`, right after `S2C_CHALLENGE`, no
`C2S_CONNECT` sent, and CS2's dialog says it was launched in insecure mode from outside of Steam - with the HUD handed out and without it,
so neither the HUD nor the plugin causes it; the same client's second connect at a join goes through. Whether a client that Steam itself
launched has this is not known. So a plain session stays on one map: to open a vote, join on an official map other than the start map
(`changelevel ar_baggage`, maintenance flag on), then take the flag off, which then rolls nothing. And emptying `mm_client_extra_addons` on
production and setting it back leaves joiners without the addon until a restart of the game server: never empty it outside a test copy.

## Commands at runtime: what reaches the client

**At start, anything**: `-Cmd "<commands>"` goes into `game\csgo\cfg\cs2harness_boot.cfg`, run with `+exec` once the game is up,
before the connect. A `+connect` on the command line never runs - `gameinfo.gi`'s `DelayedConCommands` holds `connect` back and,
for this hidden, never-focused client, never releases it - which is why the harness connects from the cfg (`connect` there runs at
once). `echo` output does not reach the console log, so do not use it as a marker.

**While it runs, in an insecure session: the full console.** `start -Insecure -Console` (or `-HudLocal -Console`) opens netcon and
`cmd -Cmd "<command>"` runs anything and prints the output (`status` answers with the build and `insecure public`; `echo` arrives here
too). How it works: with `-insecure` the engine loads its VConsole module (`vconcomm.dll`, gated by `-tools` or `-insecure`, read from
`engine2.dll`), which starts WinSock early enough for `-netconport` to listen as well; with `-novconsole` neither listens. **Both listen
on every interface**: netcon gets a random port and a random 24-hex password (`-netconpassword`; a connection without `PASS` is refused),
VConsole a random port and has **no password** - anyone on the LAN who finds the port can run console commands on this insecure client
while the session lasts. Hence `-Console` is off by default, and the watchdog's lease ends a forgotten session. Never on production: an
insecure client never joins it.

**While it runs, in a secure session: nothing client-side.** Four channels were tried and none works for a VAC-secured client that
never has focus:

| Channel | Result |
|---|---|
| `-netconport` (TCP console) | No listener in a secure session. The engine opens it in `Source2PreInit`; without the VConsole module nothing has started WinSock yet, so `socket()` fails and the message is printed before logging starts. Measured: no TCP socket (and with `-insecure -novconsole`: none either). |
| VConsole (`vconcomm`) | Loaded only with `-tools` or `-insecure` (the token hashes in `engine2.dll`). `-tools` is never used and an insecure client never joins production. `-vconsole` alone: no listener, no module. |
| Keys posted to the window | Ignored. SDL3 gives the window keyboard focus only when `GetForegroundWindow()` is the window, and on a desktop nobody looks at that call returns 0 (`quiet` prints it). A posted console key opened nothing. |
| `exec_async` + `sleep` (a polling script) | Queued, never run: async scripts need cheats (`perftest.cfg` says so; measured at the main menu). |

What does reach a running client: **the server**, through what the server can show it. `rcon` runs any server command (one RCON
line carries about 512 characters - longer ones vanish without an error); `probe\probe.py` loads a tiny test plugin (GgClientProbe,
from `/tmp` in the game container, never the plugins folder) that sends centre-screen HTML to one account only (`html`, or `htmlfile` for
any length). The server cannot run client commands: `ExecuteClientCommand` of `toggleconsole` or `exec` is refused by the client
(`Cannot execute concommand 'exec', missing required FCVAR flag`, measured).

```
python -I tools\client\probe\probe.py lock "<purpose>"                  # the server lock, this harness as owner
python -I tools\client\probe\probe.py load                              # build, copy to <game>:/tmp/ggprobe, css_plugins load
python -I tools\client\probe\probe.py html <account> 20 "<b>hello</b>"  # or "@file.html"; re-sent every tick for 20 s
python -I tools\client\probe\probe.py htmlfile <account> 20 long.html
python -I tools\client\probe\probe.py unload                            # unload, delete /tmp/ggprobe, verify, release the lock (kept on a test copy)
```

What centre-screen HTML can do: pictures from URLs show; one message holds about 1,500 bytes; the panel has a fixed size.

## Why it is quiet

| | How | Measured |
|---|---|---|
| **No window** | `cs2.exe` is started directly (`-steam`, `SteamAppId=730` in its environment) with `CreateProcessW` and `STARTUPINFO.lpDesktop = WinSta0\Cs2Harness`, a desktop created right before every launch and never switched to (`Cs2Native.cs`, adapted from StationeersPlus' TestRig). `CreateProcessW` silently falls back to the caller's desktop if the named one does not exist, hence the create-before-launch. The child gets its own inheritable desktop handle, and that handle only (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`), so it inherits none of the caller's pipes. | The watchdog enumerates the user's desktop every second: 0 cs2 windows in every session. The game window lives on `Cs2Harness` (`[SDL_app]` 1920x1080). |
| **No focus** | Same desktop: a window there cannot reach the user's foreground or input. `SW_SHOWNOACTIVATE` as belt and braces. Nothing in the harness calls `SwitchDesktop`, `SetForegroundWindow`, `ShowWindow`, `SetWindowPos` or `AttachThreadInput`. | The user's foreground is sampled every second in every session: never cs2. `GetForegroundWindow()` seen from the hidden desktop: 0. |
| **No sound** | `-nosound` (`soundsystem.dll` checks it before mixing); `snd_mute_losefocus` (the window never has focus); and the watchdog, which reads the peak meter of every audio session cs2 owns about ten times a second and kills our cs2 at the first sample above 0.0005. Nothing is muted through Windows: Windows remembers a per-app volume and mute for the app's next start, the user's. | Peak 0.000000 in every session, at the menu and in game. |
| **Below-normal priority** | `BELOW_NORMAL_PRIORITY_CLASS` at creation; the watchdog puts it back every 2 s if the game raises it. Windowed 1920x1080 by default (`-sw -w 1920 -h 1080`): a quarter of the pixels of a 4K screen; unfocused, the game sleeps between frames as `engine_no_focus_sleep` says. | `quiet`: `cs2 priority: BelowNormal`. |
| **No popups** | The launcher sets `SEM_FAILCRITICALERRORS \| SEM_NOGPFAULTERRORBOX \| SEM_NOOPENFILEERRORBOX` before `CreateProcessW`; the child inherits it, so a loader or crash error box is not raised. Anything the game itself opens (its crash reporter, a Steam message inside the game) is on the hidden desktop with it. | Not provoked. |
| **Never outlives its guard** | The watchdog puts cs2 into a job object with `KILL_ON_JOB_CLOSE`: if the watchdog dies, however it dies, cs2 dies with it. | `job tie ... ok` in `watchdog.log`. |
| **No Steam dialog** | Not launched through Steam (`-applaunch` can show a launch-options or custom-arguments dialog on the user's desktop). | - |

## Settings: nothing changes for good

CS2 writes its config files on its own - at start, many times while running (every `Writing configuration for slot 0`), and at quit.
The harness sets nothing archived; it backs up every file in `userdata\<account>\730\local\cfg\`, the cloud copies
`userdata\...\730\remote\*.vcfg` and `game\csgo\cfg\*.vcfg` before the launch, and at the end puts back each one whose content changed,
timestamp included, and lists it. What CS2 changes by itself, every session:

- `cs2_machine_convars.vcfg` (local only): the main menu adds `"ui_playsettings_custom_preset" "mg_rush_001"` - restored.
- `cs2_video.txt` (local only): `-w/-h/-sw` are written at start (1920x1080, no border) - restored; `-Resolution native` avoids it.
- The user convars and keys, local and cloud copies: rewritten with identical content (reported as `rewritten identically`).
- `-con_logfile cs2harness_console.log`: the session's log is `game\csgo\cs2harness_console.log` while it runs, moved to the run
  folder at the end (`log` then reads that copy). The user's own `console.log` is never touched.

A cloud file whose content changed would be restored too, and reported: CS2 uploads its user convars to Steam Cloud itself while it
runs, so such a change would have reached the cloud and the restored copy goes back up at Steam's next sync. A session in game at
1920x1080 has been seen to make CS2 rescale the crosshair convars in the cloud copy too; the harness restored them, every settings file
matched the backup afterwards, and Steam's record showed the user's values.

## The watchdog

`watchdog.ps1`, started detached by `start` (no window, no console, nothing inherited), runs as long as the session's cs2:

- **every ~100 ms**: cs2's audio peak meters - above 0.0005 kills our cs2 at once.
- **every second**: the user's foreground and their desktop's windows - cs2 in front, or a cs2 window on their desktop, kills it at once;
  so does a new hook attempt in Discord's `discord_hook.log`.
- **every 2 s**: another `cs2.exe` (the user's), a CS2 launch in Steam's `logs\console_log.txt` (`GameAction [AppID 730`), or "logged in
  elsewhere" in the game's log: ours quits (WM_CLOSE, terminated after 20 s), theirs is never touched. The **lease**: no harness verb for
  `-LeaseMinutes` (15) quits ours; every verb renews it. `-MaxMinutes` (60) is the hard limit.
- **when cs2 has exited**, however: the clean-up (`Invoke-Cs2PostExit`): log copied, boot cfg and loose HUD files removed, settings
  restored, `result.json`, client lock freed.

Its log is `<run>\watchdog.log`; `guard.json` holds its running figures.

## Screenshots

`PrintWindow(hwnd, PW_CLIENTONLY | PW_RENDERFULLCONTENT)` from a thread of the harness that attaches itself to the hidden desktop
(`SetThreadDesktop` on that thread only - the desktop the user sees does not change). `PW_RENDERFULLCONTENT` hands over what the game's
DirectX swap chain last presented; the game keeps rendering on a desktop nobody looks at. CS2's own `png_screenshot` /
`jpeg_screenshot` commands exist but cannot be issued at runtime (above).

## The documentation's screenshots

`docshots\docshots.py` takes every picture the documentation shows ([AGENTS.md](../../AGENTS.md#the-documentation-shows-the-whole-hud---keep-it-current-with-every-change)):
one table of rows - every page, tab and state of the HUD, and every message of `docshots\messages.txt` - each with the exact commands that
reach it, held to the committed code (its button ids, its words, its content version) before anything runs. It drives only the verbs
`rcon`, `cmd`, `shot` and `log`, and `bridgepost.py`; it masks the client's avatar, chat and own name before a picture is written
(`--renamed`: the client plays under a test name, through GgDocName); `--selfcheck` holds the whole run to its dry run offline. Its
docstring has every option. A session:

1. **The build under test**, from a clean checkout of the commit the pictures show: `OUT=<session folder> docshots/build_docs.sh <checkout> <map pictures>`
   - the plugin, the bridge's image `docs-<commit12>` (`deploy/bridge.py build`), the HUD into its own addon (`docshots\build_hud.ps1`,
   `os2docs`).
2. **The server** under the lock: `probe.py lock`, `testcopy.py plugin <commit>` (the plugin of the commit the pictures show; step 3
   above), `testcopy.py prep`, `bridge-prep`, `bridge-run docs-<commit12> --keep-flag`, `run`, the addon lists emptied with a map load (step 4 above),
   `docshots\docname.py load`.
3. **A client per resolution**: `docshots/client.sh start <WxH>`, then `OUT=... BUILT=<commit> docshots/run_res.sh <WxH> full` - the full
   table at 1920x1080 for the docs; `docshots/review_all.sh` the review set at the other six, for the owner's review only.
4. **Review** every picture by eye (`docshots\review_sheet.py` makes contact sheets; `maskview.py` checks a mask's box on a real frame; a
   retake's rows go into the main run with `merge_fix.py`, then `--remask`), list them in `<res>\reviewed.txt`, then
   `docshots\publish.py --out <run folder>`: the pages under `docs/hud/`, the gallery, README's index, every picture into
   `tools/scan/allow.txt` with its hash. Only the pictures that really changed are written: each is compared with the committed one on
   what it is about (its window, the big vote window or its crop), all but what differs on every shot (`docshots.volatile_boxes`: the
   clocks, timers and counters, the ballot's random tiles), and one that shows the same keeps its committed bytes; `--all` writes every
   one anew. The gallery links its pictures, files of their own under `docs/hud/img/gallery/`.
5. **Back**: `client.sh stop`, `docname.py unload`, `testcopy.py rm`, `bridge-rm`, `bridge-restore`, `restore`, `probe.py unlock`.

## Files

| File | |
|---|---|
| `cs2client.ps1` | The verbs. |
| `Cs2Client.psm1` | Everything they do: launch, waits, log, shots, settings backup and restore, loose HUD files, stop and clean-up. |
| `watchdog.ps1` | The guard (above). |
| `Cs2Native.cs` | The Win32 side, compiled by the module with `Add-Type` into the state folder's `bin\` (keyed by its hash). |
| `probe\probe.py`, `probe\GgClientProbe\` | The server half: the private config, the server lock, the Docker API and RCON for the verbs; and the probe plugin for tests that need the server to show this client something. |
| `probe\testcopy.py` | The insecure test copy of production for loose-file and console tests (`prep`, `run [--env K=V] [--downloads]`, `status`, `rm`, `restore`), a candidate bridge beside it - a new bridge image on a copy of the bridge's data, production's bridge set aside and its state never written, the copy reading the candidate's data (`bridge-prep`, `bridge-run <tag>`, `bridge-rm`, `bridge-restore`; the order is in its docstring) - and the plugin of a commit of main on the copy, production's plugins folder never written (`plugin <commit>`, `plugin-rm`). `run` sets the watcher's automatic actions, the deletion and the HUD item's page off in the copy's `OVERSTRIKE2_SETTINGS_CONTENT` (a production without that text gets no copy: nothing else turns the watcher off), and a candidate bridge has production's text in its environment; `--env` reaches only the image's own variables. `--downloads` keeps the stock's background downloads production's, so the watcher stays up for them and sends the bridge its ready reports (a bridge check: the Sync page's stock on disk, a background download); refused while production's watcher has a restart of its own to judge. |
| `probe\bridgepost.py` | What only the restart watcher or other players say to the bridge, to a candidate bridge on a test copy only. |
| `probe\test_*.py`, `probe\simconfig.py` | The offline tests: an in-memory Docker, a made-up private config, no server (AGENTS.md runs them). |
| `docshots\` | The documentation's screenshots: `docshots.py` and its `messages.txt`, `publish.py`, the session's scripts, GgDocName. |
