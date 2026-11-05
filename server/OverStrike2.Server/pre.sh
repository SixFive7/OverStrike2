#!/usr/bin/env bash
# pre.sh - the hook the game image's entry script SOURCES as the steam user right before it launches the engine. It hands over to
# os2-server: `install` (the plugin layer, the HUD addon's delivery, the maps folder) and then `watch` (the restart watcher) in the
# background. The program is copied first: a read-only or no-exec mount cannot run it. Sourced, so it never sets -e or -u, and a
# failure costs the plugin layer and the watcher, never the server. The one exception: a server without the HUD cannot exist, so when
# `install` refuses (exit 78: no usable hud.addon or no MultiAddonManager, its ALERT line says why) this file exits, which ends the entry
# script: the engine does not start, and Docker's restart policy tries again. Exactly that code, and nothing else, does this. It waits
# first: Docker restarts at once a container that ran for 10 s, and the image runs SteamCMD before this file, so without the wait a refused
# start means an anonymous Steam login every few seconds until somebody fixes it. OS2_REFUSED_PAUSE (seconds) is for a test.
os2_from="${OS2_SERVER:-/home/steam/overstrike2/os2-server}" os2_bin="${STEAMAPPDIR:-/home/steam/cs2-dedicated}/update-watch/os2-server"
if mkdir -p "${os2_bin%/*}" && cp -f "$os2_from" "$os2_bin.new" && chmod 755 "$os2_bin.new" && mv -f "$os2_bin.new" "$os2_bin"; then
    "$os2_bin" install --from "$os2_from"
    os2_rc=$?
    if [ "$os2_rc" = 78 ]; then
        echo "pre.sh: ALERT: os2-server refused the start (its line above says why) - the engine does not start; the next try in ${OS2_REFUSED_PAUSE:-300} s"
        sleep "${OS2_REFUSED_PAUSE:-300}"
        exit 78
    fi
    [ "$os2_rc" = 0 ] || echo "pre.sh: WARNING: os2-server install failed - the engine starts anyway"
    setsid "$os2_bin" watch </dev/null &
    echo "pre.sh: restart watcher started (pid $!); it logs as os2-server watch:"
else
    echo "pre.sh: WARNING: $os2_from could not be copied to $os2_bin - no plugin layer update, no restart watcher"
fi
unset os2_from os2_bin os2_rc
