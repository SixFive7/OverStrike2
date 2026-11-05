#!/usr/bin/env bash
# pre.sh - the hook the game image's entry script SOURCES as the steam user right before it launches the engine. It hands over to
# os2-server: `install` (the plugin layer, the HUD addon's delivery, the maps folder) and then `watch` (the restart watcher) in the
# background. The program is copied first: a read-only or no-exec mount cannot run it. Sourced, so it never exits and never sets -e
# or -u, and a failure costs the plugin layer and the watcher, never the server.
os2_from="${OS2_SERVER:-/home/steam/overstrike2/os2-server}" os2_bin="${STEAMAPPDIR:-/home/steam/cs2-dedicated}/update-watch/os2-server"
if mkdir -p "${os2_bin%/*}" && cp -f "$os2_from" "$os2_bin.new" && chmod 755 "$os2_bin.new" && mv -f "$os2_bin.new" "$os2_bin"; then
    "$os2_bin" install --from "$os2_from" || echo "pre.sh: WARNING: os2-server install failed - the engine starts anyway"
    setsid "$os2_bin" watch </dev/null &
    echo "pre.sh: restart watcher started (pid $!); it logs as os2-server watch:"
else
    echo "pre.sh: WARNING: $os2_from could not be copied to $os2_bin - no plugin layer update, no restart watcher"
fi
unset os2_from os2_bin
