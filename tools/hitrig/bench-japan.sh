#!/usr/bin/env bash
# The same A/B against the bench server in Japan, over the real line.
#
#   HITRIG_JP_PASS=... bench-japan.sh <seconds-per-arm>
#
# Two arms rather than the local four. The local run is where a change is
# isolated, because the latency there is a number the run chose and can hold
# still; this one exists to answer whether the isolated change survives 270 ms
# of somebody else's internet, and every extra arm is another quarter of an
# hour of a line nobody controls drifting underneath the comparison.
#
# The authority is /opt/fruityprime-bench on port 27891 -- not 27890, which is
# the dev server people join, and not 27888, which is production. Restarting it
# between arms is what zeroes its counters.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/tools/hitrig"
SECS="${1:-240}"
OUT="${HITRIG_OUT:-/tmp/hitrig-jp}"
export HITRIG_OUT="$OUT"

for mode in jump sniper; do
  # v0.8.0's behaviour: the 24-frame ceiling, no client pin.
  HITRIG_CLIENT_EXTRA="" "$HERE/run-japan.sh" "jp-$mode-base" "$mode" "$SECS" 24
  # Everything on.
  HITRIG_CLIENT_EXTRA="-clientpin" \
    "$HERE/run-japan.sh" "jp-$mode-all" "$mode" "$SECS" 30 -pressage
done

echo
echo "== ${SECS}s per arm against 13.78.14.98:27891, real line"
python3 "$HERE/summarise.py" \
  "$OUT/jp-jump-base" "$OUT/jp-jump-all" \
  "$OUT/jp-sniper-base" "$OUT/jp-sniper-all"
