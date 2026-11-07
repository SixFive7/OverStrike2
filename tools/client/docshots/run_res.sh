#!/bin/bash
# One resolution's run of a docshots session: run_res.sh <WxH> full|review - the full table (the docs, 1920x1080) or the review set; --bridge-copy --renamed
# --keep-raw, the build's commit recorded. OUT: the session's output folder (required; never inside the repository's tracked tree), BUILT: the commit the
# session's plugin, bridge and HUD were built from (required). Its log goes to $OUT/logs/run-<WxH>.log.
D="$(cd "$(dirname "$0")" && pwd)"
: "${OUT:?set OUT to the output folder of the session}" "${BUILT:?set BUILT to the commit the build under test came from}"
RUN_OUT="$OUT/docs"
EXTRA=""
[ "$2" = "review" ] && RUN_OUT="$OUT/review" && EXTRA="--review-set"
mkdir -p "$RUN_OUT" "$OUT/logs"
echo "start $(date +%T) $1 $2"
python -I "$D/docshots.py" --res "$1" --out "$RUN_OUT" --bridge-copy --renamed --keep-raw --built "$BUILT" $EXTRA > "$OUT/logs/run-$1.log" 2>&1
code=$?
echo "end $(date +%T) $1 $2 exit $code"
grep -E "^Done:|FAILED|REFUSED|Traceback" "$OUT/logs/run-$1.log" | cut -c1-240 | tail -40
exit $code
