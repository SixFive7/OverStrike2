#!/bin/bash
# The build under test for a docshots session, from a clean checkout of the commit the pictures show: the plugin (dotnet build), the bridge's image
# docs-<commit12> on the server (deploy/bridge.py build, with the private config and the Docker API key in the environment, never printed), the HUD in
# its own addon with its map pictures (build_hud.ps1). The three run side by side; their logs go to $OUT/logs/build-<commit12>/.
#   OUT=<session folder> build_docs.sh <clean checkout> <folder of the map pictures (hud/thumbs.py's)>
: "${OUT:?set OUT to the session's output folder}"
D="$(cd "$(dirname "$0")" && pwd)"
W="${1:?the clean checkout of the commit under test}"
T="${2:?the folder of the map pictures}"
cd "$W" || exit 1
S=$(git rev-parse --short=12 HEAD)
L="$OUT/logs/build-$S"
mkdir -p "$L"
echo "commit $S $(date +%T)"
( dotnet build plugin/OverStrike2 -c Release -nodeReuse:false -p:UseSharedCompilation=false > "$L/plugin.log" 2>&1 && echo "plugin OK $(date +%T)" || echo "plugin FAILED $(date +%T)" ) &
( python -I deploy/bridge.py build --tag "docs-$S" > "$L/bridge.log" 2>&1 && echo "bridge OK $(date +%T)" || echo "bridge FAILED $(date +%T)" ) &
( powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$D/build_hud.ps1" 2>/dev/null || echo "$D/build_hud.ps1")" -Src "$(cygpath -w "$W/hud" 2>/dev/null || echo "$W/hud")" -Thumbs "$(cygpath -w "$T" 2>/dev/null || echo "$T")" > "$L/hud.log" 2>&1 && echo "hud OK $(date +%T)" || echo "hud FAILED $(date +%T)" ) &
wait
grep -E 'Warning\(s\)|Error\(s\)' "$L/plugin.log"
tail -2 "$L/bridge.log" | cut -c1-200
grep -E '^(OK|MISSING|thumb)' "$L/hud.log" | cut -c1-120 | head -8
grep -i -E 'error|fail|invalid' "$L/hud.log" | grep -v -E 'Warning\(s\)|thumbs/|_jpg' | cut -c1-200 | head -8
echo "build done $S $(date +%T)"
