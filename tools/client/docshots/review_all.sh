#!/bin/bash
# The review set at the other resolutions, one after the other: the client stopped and started at each, then run_res.sh <res> review. Stops at the first client that
# does not come up in game, or at the file STOP in $OUT (a clean point: between two resolutions). OUT and BUILT as for run_res.sh; RESOLUTIONS to pick others.
D="$(cd "$(dirname "$0")" && pwd)"
: "${OUT:?set OUT to the session's output folder}" "${BUILT:?set BUILT to the commit the build under test came from}"
mkdir -p "$OUT/logs"
for RES in ${RESOLUTIONS:-3840x2160 2560x1440 2560x1080 1680x1050 1600x1200 1280x720}; do
  [ -f "$OUT/STOP" ] && { echo "$(date +%T) STOP file: not starting $RES"; exit 3; }
  echo "$(date +%T) ==== $RES: client stop"
  bash "$D/client.sh" stop "next resolution" > "$OUT/logs/client-stop-before-$RES.log" 2>&1
  echo "$(date +%T) ==== $RES: client start"
  bash "$D/client.sh" start "$RES" 40 > "$OUT/logs/client-$RES.log" 2>&1
  if ! grep -q "ready (ingame)" "$OUT/logs/client-$RES.log"; then
    echo "$(date +%T) $RES: the client did not come up in game - stop"; tail -5 "$OUT/logs/client-$RES.log"; exit 2
  fi
  bash "$D/run_res.sh" "$RES" review
done
echo "$(date +%T) ==== all done"
