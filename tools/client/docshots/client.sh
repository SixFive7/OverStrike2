#!/bin/bash
# The test client for one resolution of a docshots session: start it (in game on the test copy, the HUD of the session's build as loose files,
# the map pictures included), or stop it and run the VAC self-check. The server lock is the session's (os2-deploy probe lock): -AllowServerLock.
#   client.sh start <WxH> [max minutes]      client.sh stop [why]
# HUD_ADDON: the addon folder build_hud.ps1 compiled the session's HUD into (default os2docs).
H="$(cd "$(dirname "$0")/.." && pwd)/cs2client.ps1"
ADDON="${HUD_ADDON:-os2docs}"
case "$1" in
  start) timeout 600 pwsh -NoProfile -File "$H" start -Connect default -HudAddon "$ADDON" -IncludeThumbs -AllowServerLock -Console -Resolution "$2" -MaxMinutes "${3:-100}" -Purpose "docshots $2" 2>&1 | tr -d '\r' | grep -v '^$' | tail -25 | cut -c1-220 ;;
  stop) timeout 200 pwsh -NoProfile -File "$H" stop -Reason "docshots: ${2:-resolution done}" 2>&1 | tr -d '\r' | grep -E "WM_CLOSE|loose|guard|exit|run dir|LEFT|removed" | cut -c1-200
        timeout 120 pwsh -NoProfile -File "$H" check 2>&1 | tr -d '\r' | tail -2 | cut -c1-200 ;;
  *) echo "client.sh start <WxH> [minutes] | stop [why]" ;;
esac
