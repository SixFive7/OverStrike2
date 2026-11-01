#!/usr/bin/env bash
# pre.sh - Metamod:Source + CounterStrikeSharp layer for the cs2 stack.
#
# The joedwards32/cs2 entry script SOURCES this file as the steam user right before it launches the
# game, after SteamCMD has updated the game tree and with the working directory at $STEAMAPPDIR/game.
# Deployed to <config dir>/pre.sh, bind-mounted read-only at
# /home/steam/cs2-dedicated/pre.sh (source of truth: server/ in this repository).
#
# Driven by CS2_PLUGINS (set in deploy/compose.example.yml):
#   1  install the pinned Metamod + CounterStrikeSharp + MultiAddonManager once (a marker file per component
#      records the installed version), clear any executable-stack request in their libraries (glibc 2.41
#      refuses to load one), re-apply the gameinfo.gi search path on EVERY start because SteamCMD updates
#      rewrite that file, load MultiAddonManager only while the in-game HUD is switched on (GUNGAME_HUD_ADDON),
#      then copy our plugins from the read-only mount into the game tree (and remove the ones it copied
#      before that are gone from the mount).
#   0  the kill switch after a breaking Valve update: remove the gameinfo.gi search path so the
#      server runs vanilla. Files stay on disk; set 1 again once the pins below have been bumped.
# Then, independent of CS2_PLUGINS, it starts the restart watcher (server/update-watch.py) in the background.
#
# Because this file is sourced, it must never exit, set -e or set -u: that would kill the launch.
# Every step logs what it did and a failure only costs the plugin layer, never the server.

# Pinned artefacts. To bump: read https://github.com/alliedmodders/metamod-source/releases for the newest 2.0.0.<build>
# Metamod release (mms.alliedmods.net/mmsdrop, where the dev builds used to be, answers 404 since October 2026), https://github.com/roflmuffin/CounterStrikeSharp/releases for the current CounterStrikeSharp
# version and https://github.com/Source2ZE/MultiAddonManager/releases for MultiAddonManager, change the version
# strings below (the marker file name changes with them, so the next start extracts the new build over the old
# one), rebuild plugin/GunGameNades, GunGameWelcome and MapSettingsReset against the same CounterStrikeSharp.API NuGet
# version, copy the DLLs to the plugins share and restart the container. Rolling back is the same edit the other
# way round: only the current version's marker is kept, so an older pin is extracted again.
CS2_PLUGINS_MM_VERSION="2.0.0-git1483" # Metamod:Source 2.0 dev build 1483 (Linux, 2026-10-18): SourceHook API 18 and KHook, which CounterStrikeSharp 1.0.375+ and MultiAddonManager 1.6+ need (1411 and older: API 17; 1460 segfaults on current CS2). Since 1473: a KHook bump, "Avoid closing null plugin library handles" (#292) and "Don't erase Unloader class too early". Loaded cleanly with CounterStrikeSharp 1.0.377 on 2026-10-21. Bump with CounterStrikeSharp.
CS2_PLUGINS_MM_URL="https://github.com/alliedmodders/metamod-source/releases/download/${CS2_PLUGINS_MM_VERSION/-git/.}/mmsource-${CS2_PLUGINS_MM_VERSION}-linux.tar.gz"
CS2_PLUGINS_CSS_VERSION="1.0.379" # CounterStrikeSharp v1.0.379 with the .NET 10 runtime (Linux), 2026-10-31: schema 1.41.9.3, ConVar<T> (#935; the old ConVar class is marked obsolete, unchanged), a failed entity-list lookup no longer cached for the process's life (#1475), networked schema flags resolved late (#1473). Same gamedata.json and the same join-path engine calls as 1.0.378 (checked 2026-11-01). 1.0.378 (2026-10-23): "fix: Game Update 1.41.9.2" (#1470) - built on alliedmodders/s2sdk with the IVEngineServer2 slot fix (s2sdk#116), so the client-authorization calls that 1.0.377 sent to the wrong engine functions on 14192 (#1466) hit the right ones; the two Linux signatures 14192 broke (CCSPlayerPawnBase_PostThink, GetCSWeaponDataFromKey) are new. 1.0.376: per-player HUD text survives a join (#1434). 1.0.374's library asked for an executable stack, 1.0.375 and later do not.
CS2_PLUGINS_CSS_URL="https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v${CS2_PLUGINS_CSS_VERSION}/counterstrikesharp-with-runtime-linux-${CS2_PLUGINS_CSS_VERSION}.zip"
CS2_PLUGINS_MAM_VERSION="1.7.3" # MultiAddonManager 1.7.3 (2026-10-27), the SteamRT4 build for this image's runtime: hands the HUD's Workshop addon to every client. Loaded only while GUNGAME_HUD_ADDON is set. 1.7.2 reads the server's addon string and client list through the SDK instead of the hard-coded offsets 376 and 616 (the same offsets in the binary, checked 2026-11-01) and tracks the Workshop map when a level really loads (an IEngineServiceMgr::SwitchToLoop hook); 1.7.3 fixes that tracking after an IVEngineServer::ChangeLevel (#82). 1.7 is main 7c18b08 (the ReplyConnection signature 14192 made ambiguous; 1.6.2 crashed the server ~5 s after every start on 14192) built on s2sdk with the slot fix; 1.7.1: the cfg is executed once at load again, not on every map start (which undid a runtime mm_client_extra_addons).
CS2_PLUGINS_MAM_URL="https://github.com/Source2ZE/MultiAddonManager/releases/download/v${CS2_PLUGINS_MAM_VERSION}/MultiAddonManager-v${CS2_PLUGINS_MAM_VERSION}-steamrt4.tar.gz"
CS2_PLUGINS_ROOT="${STEAMAPPDIR:-/home/steam/cs2-dedicated}"
CS2_PLUGINS_SRC="${CS2_PLUGINS_SRC:-/home/steam/cs2-plugins}" # Read-only mount of <plugins dir>, one folder per plugin.

cs2_plugins_log() { echo "pre.sh: $*"; }

# cs2_plugins_install <name> <version> <url> <tar|zip>: download and extract into game/csgo/ unless the
# marker for this exact version already sits in the component's addons folder (deleting the folder reinstalls).
# Only the marker of the version just extracted is kept, so pinning an older version again extracts it again.
cs2_plugins_install() {
    local csgo="$CS2_PLUGINS_ROOT/game/csgo" marker="$CS2_PLUGINS_ROOT/game/csgo/addons/$1/.pre.sh-installed-$2" tmp
    if [[ -f "$marker" ]]; then cs2_plugins_log "$1 $2 already installed"; return 0; fi
    tmp=$(mktemp -d "$CS2_PLUGINS_ROOT/pre.sh-download.XXXXXX") \
        || { cs2_plugins_log "WARNING: cannot create a download directory, $1 not installed"; return 1; }
    cs2_plugins_log "downloading $1 $2 from $3"
    if curl -fsSL --retry 3 -o "$tmp/archive" "$3" && mkdir -p "$csgo" && {
           { [[ "$4" == tar ]] && tar xzf "$tmp/archive" -C "$csgo"; } || { [[ "$4" == zip ]] && unzip -oq "$tmp/archive" -d "$csgo"; }; } \
       && rm -f "$csgo/addons/$1"/.pre.sh-installed-* && : > "$marker"; then
        cs2_plugins_log "$1 $2 installed into $csgo/addons"
    else
        cs2_plugins_log "WARNING: $1 $2 download or extraction failed; the server starts without it"
    fi
    rm -rf "$tmp"
}

# cs2_plugins_gameinfo on|off: keep exactly one "Game csgo/addons/metamod" search path, right after the
# Game_LowViolence line that heads the list, or none at all. Idempotent, so it can run on every start.
cs2_plugins_gameinfo() {
    local gi="$CS2_PLUGINS_ROOT/game/csgo/gameinfo.gi"
    if [[ ! -f "$gi" ]]; then cs2_plugins_log "WARNING: $gi is missing, nothing patched"; return 1; fi
    if [[ "$1" == on ]]; then
        if grep -q 'csgo/addons/metamod' "$gi"; then cs2_plugins_log "gameinfo.gi already lists csgo/addons/metamod"; return 0; fi
        sed -i -E 's#^([[:space:]]*)Game_LowViolence[[:space:]]+csgo_lv.*$#&\n\1Game\tcsgo/addons/metamod#' "$gi"
        if grep -q 'csgo/addons/metamod' "$gi"; then cs2_plugins_log "gameinfo.gi: csgo/addons/metamod search path added"
        else cs2_plugins_log "WARNING: gameinfo.gi has no Game_LowViolence line to anchor on (Valve changed the file?); Metamod will not load"; return 1; fi
    else
        if grep -q 'csgo/addons/metamod' "$gi"; then
            sed -i '\#csgo/addons/metamod#d' "$gi" && cs2_plugins_log "gameinfo.gi: csgo/addons/metamod search path removed"
        else cs2_plugins_log "gameinfo.gi already vanilla"; fi
    fi
}

# cs2_plugins_copy: replace every plugin folder from the read-only mount in CounterStrikeSharp's plugins directory, and
# remove the folders an earlier start copied from that same mount which are gone from it now, so a plugin taken off the
# share stops loading at the next start. What was copied is listed in .pre.sh-copied beside the folders (its first line
# names the mount); a folder pre.sh never copied - CounterStrikeSharp's own files, anything put there by hand - is never
# touched, and a mount with no plugin folder at all (an empty or missing share) removes nothing.
cs2_plugins_copy() {
    local dst="$CS2_PLUGINS_ROOT/game/csgo/addons/counterstrikesharp/plugins" dir name copied="" removed=""
    local list="$CS2_PLUGINS_ROOT/game/csgo/addons/counterstrikesharp/plugins/.pre.sh-copied"
    local -a names=()
    local -A here=()
    if [[ ! -d "$dst" ]]; then cs2_plugins_log "WARNING: $dst is missing (CounterStrikeSharp not installed?), no plugins copied"; return 1; fi
    for dir in "$CS2_PLUGINS_SRC"/*/; do
        [[ -d "$dir" ]] || continue # The glob matched nothing.
        name=$(basename "$dir")
        names+=("$name"); here["$name"]=1
        if rm -rf "$dst/$name" && cp -r "$dir" "$dst/$name"; then copied="$copied $name"
        else cs2_plugins_log "WARNING: plugin $name could not be copied"; fi
    done
    if (( ${#names[@]} == 0 )); then
        cs2_plugins_log "plugins: $CS2_PLUGINS_SRC holds no plugin folder - nothing copied, nothing removed"; return 0
    fi
    if [[ -f "$list" && "$(head -n 1 "$list")" == "src $CS2_PLUGINS_SRC" ]]; then
        while IFS= read -r name; do
            [[ -n "$name" && "$name" != */* && "$name" != .* && -z "${here[$name]:-}" && -d "$dst/$name" ]] || continue
            if rm -rf "${dst:?}/$name"; then removed="$removed $name"
            else cs2_plugins_log "WARNING: plugin $name is gone from $CS2_PLUGINS_SRC but could not be removed"; fi
        done < <(tail -n +2 "$list")
    fi
    printf '%s\n' "src $CS2_PLUGINS_SRC" "${names[@]}" > "$list" || cs2_plugins_log "WARNING: could not write $list"
    cs2_plugins_log "plugins copied from $CS2_PLUGINS_SRC:${copied:- (none)}${removed:+; removed, gone from there:$removed}"
}

# cs2_plugins_execstack: glibc 2.41 (this image's Steam Runtime 4) refuses to load a shared object that asks for an
# executable stack ("cannot enable executable stack as shared object requires"); CounterStrikeSharp 1.0.374's library
# did, and a hand patch on the volume was all that kept the layer loading. So on every start, every ELF library under
# addons/ that sets the X bit of PT_GNU_STACK gets it cleared - four bytes in place, what execstack -c does, never
# patchelf (reported to corrupt the file). Idempotent: a clean library is only read, and one already patched stays so.
cs2_plugins_execstack() {
    local out
    if ! command -v python3 >/dev/null 2>&1; then cs2_plugins_log "WARNING: python3 not found, executable-stack check skipped"; return 1; fi
    out=$(python3 - "$CS2_PLUGINS_ROOT/game/csgo/addons" 2>&1 <<'PY'
import os, re, struct, sys
checked, cleared, failed = 0, [], []
for root, _, names in os.walk(sys.argv[1]):
    for name in names:
        if not re.search(r"\.so(\.[0-9]+)*$", name):  # libraries only: not the hand patch's counterstrikesharp.so.bak-execstack
            continue
        path = os.path.join(root, name)
        try:
            with open(path, "rb") as f:
                head = f.read(64)
                if len(head) < 64 or head[:4] != b"\x7fELF" or head[4] != 2:
                    continue
                checked += 1
                phoff = struct.unpack_from("<Q", head, 32)[0]
                size, count = struct.unpack_from("<HH", head, 54)
                hits = []
                for i in range(count):
                    f.seek(phoff + i * size)
                    kind, flags = struct.unpack("<II", f.read(8))
                    if kind == 0x6474E551 and flags & 1:
                        hits.append((phoff + i * size + 4, flags & ~1))
            if hits:
                with open(path, "r+b") as f:
                    for at, flags in hits:
                        f.seek(at)
                        f.write(struct.pack("<I", flags))
                cleared.append(os.path.relpath(path, sys.argv[1]))
        except OSError as e:
            failed.append(f"{os.path.relpath(path, sys.argv[1])} ({e.strerror})")
print(f"{checked} libraries checked, {len(cleared)} cleared" + (": " + " ".join(cleared) if cleared else "")
      + (f"; WARNING could not patch: {' '.join(failed)}" if failed else ""))
PY
)
    cs2_plugins_log "executable stack: $out"
}

# cs2_plugins_hud: the in-game HUD's switch, GUNGAME_HUD_ADDON on the cs2 service - the Workshop id of the published
# HUD addon (hud/). Set: MultiAddonManager is loaded and hands that addon to every
# client, and the GunGameWelcome plugin draws the HUD instead of its centre panel. Empty (the default): Metamod does not
# load MultiAddonManager at all - its files stay on disk - so the layer is exactly what it was before the HUD existed.
cs2_plugins_hud() {
    local csgo="$CS2_PLUGINS_ROOT/game/csgo" id="${GUNGAME_HUD_ADDON:-}"
    local vdf="$csgo/addons/metamod/multiaddonmanager.vdf" cfg="$csgo/cfg/multiaddonmanager/multiaddonmanager.cfg"
    if [[ -z "$id" ]]; then rm -f "$vdf"; cs2_plugins_log "HUD off (GUNGAME_HUD_ADDON is empty): MultiAddonManager not loaded"; return 0; fi
    if [[ ! "$id" =~ ^[0-9]{6,12}$ ]]; then rm -f "$vdf"; cs2_plugins_log "WARNING: GUNGAME_HUD_ADDON='$id' is not a Workshop id; HUD addon not delivered"; return 1; fi
    if [[ ! -f "$csgo/addons/multiaddonmanager/bin/multiaddonmanager.so" ]]; then
        rm -f "$vdf"; cs2_plugins_log "WARNING: MultiAddonManager is not installed; HUD addon $id cannot reach the players"; return 1
    fi
    mkdir -p "$csgo/cfg/multiaddonmanager" && cat > "$cfg" <<EOF && printf '"Metamod Plugin"\n{\n\t"alias"\t"multiaddonmanager"\n\t"file"\t"addons/multiaddonmanager/bin/multiaddonmanager"\n}\n' > "$vdf" \
        && cs2_plugins_log "HUD on: MultiAddonManager $CS2_PLUGINS_MAM_VERSION loaded, every client gets Workshop addon $id" \
        || { rm -f "$vdf"; cs2_plugins_log "WARNING: could not write the MultiAddonManager config; HUD addon not delivered"; return 1; }
// Written by pre.sh on every start, from GUNGAME_HUD_ADDON in deploy/compose.example.yml (source: server/pre.sh). Edits here do not survive.
mm_extra_addons ""                  // The server mounts nothing extra: the HUD layout lives on the clients only.
mm_client_extra_addons "$id"        // The HUD addon, downloaded by every client on its first connect.
mm_extra_addons_timeout 10
mm_addon_connection_timeout 0       // Never kick a client for taking long: a first download of a big workshop map can take minutes.
mm_addon_mount_download 0
mm_cache_clients_with_addons 1      // A client that has the addon is not sent it again on every map change (one reconnect less each time).
mm_cache_clients_duration 0         // ...for the life of the server process.
mm_block_disconnect_messages 0      // On, 1.6.1 crashed clients (MultiAddonManager #77); off costs a "left the game" line while someone downloads.
mm_addon_debug 0
EOF
}

cs2_plugins_main() {
    local hud="off"
    case "${CS2_PLUGINS:-0}" in
        1)
            cs2_plugins_install metamod "$CS2_PLUGINS_MM_VERSION" "$CS2_PLUGINS_MM_URL" tar
            cs2_plugins_install counterstrikesharp "$CS2_PLUGINS_CSS_VERSION" "$CS2_PLUGINS_CSS_URL" zip
            cs2_plugins_install multiaddonmanager "$CS2_PLUGINS_MAM_VERSION" "$CS2_PLUGINS_MAM_URL" tar
            cs2_plugins_execstack
            cs2_plugins_gameinfo on
            cs2_plugins_hud && [[ -n "${GUNGAME_HUD_ADDON:-}" ]] && hud="on (Workshop addon $GUNGAME_HUD_ADDON)"
            cs2_plugins_copy
            cs2_plugins_log "SUMMARY: CS2_PLUGINS=1, Metamod $CS2_PLUGINS_MM_VERSION + CounterStrikeSharp $CS2_PLUGINS_CSS_VERSION" \
                "layer active, HUD $hud" \
                "(check 'meta list' and 'css_plugins list' on the server console)"
            ;;
        *)
            [[ "${CS2_PLUGINS:-0}" == 0 ]] || cs2_plugins_log "WARNING: CS2_PLUGINS='$CS2_PLUGINS' is neither 0 nor 1, treating it as 0"
            cs2_plugins_gameinfo off
            cs2_plugins_log "SUMMARY: CS2_PLUGINS=0, vanilla server; Metamod and CounterStrikeSharp files are left on disk but not loaded"
            ;;
    esac
}

# cs2_watch_start: launch server/update-watch.py in the background - update restarts, the daily restart and
# freeze recovery, each switched in deploy/compose.example.yml. It never holds up the launch: no waiting, and every
# failure is one log line. It dies with the container; its counters live on the volume (update-watch/state.json).
CS2_WATCH_SCRIPT="/home/steam/cs2-update-watch.py" # Read-only mount of <config dir>/update-watch.py.
cs2_watch_start() {
    local log size
    if [[ "${CS2_AUTOUPDATE:-1}" == 0 && "${CS2_FREEZE_RECOVERY:-1}" == 0 && "${CS2_RESTART_AT:-07:00}" == off \
          && ( "${CS2_WARMUP:-1}" == 0 || -z "${CS2_WARMUP_ITEM:-}" ) && "${CS2_PREFETCH_AT:-off}" == off ]]; then
        cs2_plugins_log "restart watcher: updates, daily restart, freeze recovery, warm-up and prefetch all switched off, not started"; return 0
    fi
    if [[ ! -f "$CS2_WATCH_SCRIPT" ]]; then cs2_plugins_log "WARNING: $CS2_WATCH_SCRIPT is missing, restart watcher not started"; return 0; fi
    if ! command -v python3 >/dev/null 2>&1; then cs2_plugins_log "WARNING: python3 not found, restart watcher not started"; return 0; fi
    # -condebug appends to console.log for ever: rotate it now, while nothing holds it open (the engine starts after this file returns).
    for log in "$CS2_PLUGINS_ROOT/game/csgo/console.log" "$CS2_PLUGINS_ROOT/game/console.log"; do
        size=$(stat -c %s "$log" 2>/dev/null) || continue
        if (( size > 52428800 )); then mv -f "$log" "$log.1" && cs2_plugins_log "rotated $log (over 50 MB) to $log.1"; fi
    done
    # Forget the warm-up item now, while no engine holds the workshop acf (update-watch.py --warmup-reset: its folder and
    # its two acf entries), so the watcher's warm-up after this boot is a real download - the job that runs the Steam
    # client's re-check of every installed item, before a player needs it.
    if [[ "${CS2_WARMUP:-1}" == 1 && -n "${CS2_WARMUP_ITEM:-}" ]]; then
        python3 "$CS2_WATCH_SCRIPT" --warmup-reset || cs2_plugins_log "WARNING: update-watch.py --warmup-reset failed"
    fi
    setsid python3 -u "$CS2_WATCH_SCRIPT" </dev/null &
    cs2_plugins_log "restart watcher started (pid $!); it logs as update-watch:"
}

cs2_plugins_main || true
cs2_watch_start || true
unset -f cs2_plugins_main cs2_plugins_copy cs2_plugins_gameinfo cs2_plugins_install cs2_plugins_execstack cs2_plugins_hud cs2_plugins_log cs2_watch_start
unset CS2_PLUGINS_MM_VERSION CS2_PLUGINS_MM_URL CS2_PLUGINS_CSS_VERSION CS2_PLUGINS_CSS_URL CS2_PLUGINS_MAM_VERSION CS2_PLUGINS_MAM_URL CS2_PLUGINS_ROOT CS2_PLUGINS_SRC CS2_WATCH_SCRIPT
