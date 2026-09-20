#!/usr/bin/env bash
# Protocol 7's hit registration against protocol 6's, on the same box, on two
# maps, at a latency somebody would actually complain about.
#
#   bench-p7.sh [seconds-per-arm] [netlag] [loss]
#
# Four arms, two per map, and the two differ by nothing but a set of flags:
#
#   p6   what every build up to protocol 6 did -- the 400 ms rewind ceiling,
#        puppets written from relayed intents, no interpolation, no hit claims,
#        no predicted kills on anybody else
#   p7   the defaults: a 750 ms ceiling, puppets owned by the snapshot and read
#        off a playout clock with a sub-frame ack, hit claims arbitrated by the
#        authority, predicted kills back on
#
# Two maps because the fault is about players who are moving and the two say
# different things about how much:
#
#   TEST ARENA   flat, forty units square, the runner jumps on its own
#   TEST PADS    the same room with four jump pads throwing hunters across it,
#                so a large share of the run is spent shooting at somebody
#                crossing at 0.3 units a frame or more -- against a headshot
#                band 0.3 units tall
#
# The p7 arms run with -debuglog, because the jump-pad case is the one where
# "the shot went through him" has to be told from "the rewind went to the wrong
# frame", and only the log separates them.
#
# Serial, for bench.sh's reason: two matches on one box share a CPU, and a
# server that misses its step is a server whose frame counter -- which every
# ack is measured against -- is not the clock it claims to be.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/tools/hitrig"
SECS="${1:-120}"
# 320 ms with 80 of jitter: inside the 250-400 ms band this work is about, and
# jittery, which is what pushes the tail of the requested-rewind distribution
# into a ceiling. A flat number never reaches one.
LAG="${2:-320:80}"
LOSS="${3:-2}"
OUT="${HITRIG_OUT:-/tmp/hitrig-p7}"
export HITRIG_OUT="$OUT"

# Everything protocol 6 did, asked for explicitly. -relayedpuppets also turns
# the playout clock off, so -nointerp is redundant with it and is passed anyway
# to say what the arm means.
P6_CLIENT="-noclaims -nointerp -relayedpuppets -nodeathprediction"

run() {
  local label="$1" map="$2" ceiling="$3" client="$4" server="${5:-}"
  echo "== $label ($map, ceiling $ceiling, client '${client:-defaults}', server '${server:-none}')"
  HITRIG_SERVER_EXTRA="$server" \
  HITRIG_CLIENT_EXTRA="-netloss $LOSS $client" \
    "$HERE/run-local.sh" "$label" jump "$SECS" "$LAG" "$ceiling" "$map" \
    > /dev/null 2>&1
}

run arena-p6 "TEST ARENA" 24 "$P6_CLIENT"
run arena-p7 "TEST ARENA" 45 ""
run pads-p6  "TEST PADS"  24 "$P6_CLIENT"
# -debuglog on both ends for the arm the jump-pad question is asked of: the
# client's netlog carries the playout clock and its own claims, and the
# *server's* -- which only exists under this flag -- carries the rewind depth
# it served, what the ceiling refused, and a line per rescued hit.
run pads-p7  "TEST PADS"  45 "-debuglog" "-debuglog"

echo
echo "== ${SECS}s per arm, $LAG ms round trip and ${LOSS}% loss on both clients"
python3 "$HERE/summarise.py" "$OUT/arena-p6" "$OUT/arena-p7" \
    "$OUT/pads-p6" "$OUT/pads-p7"
echo
echo "== what the authority did with the claims"
for arm in arena-p6 arena-p7 pads-p6 pads-p7; do
  printf '%-10s ' "$arm"
  grep -h "hit claims" "$OUT/$arm/server.log" 2>/dev/null | tail -1 \
    | sed 's/.*hit claims/hit claims/' || echo "(none)"
done
echo
echo "== how the opponents moved"
for arm in arena-p6 arena-p7 pads-p6 pads-p7; do
  printf '%-10s ' "$arm"
  grep -h "puppet smoothing" "$OUT/$arm"/*.log 2>/dev/null | tail -1 \
    | sed 's/.*puppet smoothing/puppet smoothing/' || echo "(none)"
done
echo
echo "== the jump-pad arm's own logs, which is where a single shot can be followed"
echo "   client:  \$HITRIG_BIN/netlog-ALPHA.txt  (read point, buffer, its claims)"
echo "   server:  \$HITRIG_BIN/netlog-server.txt (rewind served, ceiling, rescues)"

