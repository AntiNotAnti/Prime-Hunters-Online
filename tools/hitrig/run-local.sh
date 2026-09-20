#!/usr/bin/env bash
# The rig against a server on this box, with latency injected into the clients.
#
#   run-local.sh <label> <mode: jump|sniper> <seconds> <netlag-ms> <maxrewind> [map]
#
# The point of the local arm is that the server's own knob (-maxrewind) can be
# moved between runs without a deploy, and the injected latency is a number the
# run chose rather than one the internet is choosing while it is measured. The
# real line is the other arm; this one is where a scenario is proven to work at
# all before it is spent on a 270 ms round trip.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# The staged copy, not the build tree: a rebuild during a run replaces the
# assembly under the running processes and takes them down. See stage.sh.
BUILD="${HITRIG_BIN:-$ROOT/tools/hitrig/bin}"
DN="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export LD_LIBRARY_PATH="$BUILD" MESA_GL_VERSION_OVERRIDE=4.5COMPAT
export ALSOFT_DRIVERS=null PULSE_SERVER=

LABEL="${1:?label}"; MODE="${2:?jump|sniper}"; SECS="${3:?seconds}"
LAG="${4:-250}"; MAXREWIND="${5:-24}"; MAP="${6:-TEST ARENA}"
PORT="${HITRIG_PORT:-27997}"
OUT="${HITRIG_OUT:-/tmp/hitrig}/$LABEL"
mkdir -p "$OUT"
cd "$BUILD" || exit 1

cleanup() {
  for p in $(pgrep -f "FruityPrime.dll -server -port $PORT" 2>/dev/null); do kill -9 "$p" 2>/dev/null; done
  for p in $(pgrep -f "FruityPrime.dll -netcheck 127.0.0.1 -port $PORT" 2>/dev/null); do kill -9 "$p" 2>/dev/null; done
}
trap cleanup EXIT
cleanup
# Wait for the socket to actually be gone, not for a second and a hope. A
# previous arm's server still holding the port makes the new one fail to bind,
# and the clients then find nothing there -- which is a run whose logs are
# empty rather than a run that failed, and it cost two arms of a benchmark
# before this loop existed. A stale client still connected is the other half:
# it joins the new server as a third slot and quietly changes the scenario.
for _ in $(seq 1 30); do
  ss -lun 2>/dev/null | grep -q ":$PORT " || break
  sleep 1
done
if ss -lun 2>/dev/null | grep -q ":$PORT "; then
  echo "port $PORT is still held; refusing to start an arm that cannot bind" >&2
  exit 1
fi

# One match, long enough that the run never rotates underneath itself: a
# rotation clears every counter here (NetHitPrediction.ForgetSlot), and half a
# run's statistics are worse than none.
printf '%s | Battle | 15 | 99\n' "$MAP" > maprotation.txt
# HITRIG_SERVER_EXTRA carries the arm's own flag (-pressage, say). Unquoted on
# purpose: it is a list of flags, and quoting it would hand the server one
# argument that happens to contain spaces.
# shellcheck disable=SC2086
setsid "$DN" FruityPrime.dll -server -port "$PORT" -players 8 -simulate -nomaster \
    -noautoupdate -maxrewind "$MAXREWIND" ${HITRIG_SERVER_EXTRA:-} \
    -servername "hitrig $LABEL" \
    > "$OUT/server.log" 2>&1 < /dev/null &
# Wait for the server to be *listening*, not for a guess.
#
# Five seconds was the guess and it is not enough on a loaded box: the server
# has to load the room before it binds anything a client can join, every custom
# map in maps/ is built on startup, and a client whose Hello goes unanswered
# gives up after eight seconds and writes "the server may be off, or UDP may be
# blocked". That reads as a scenario that produced nothing rather than as an
# arm that never started, and it cost the first protocol-7 comparison its
# baseline. Thirty seconds is well past a cold start here and still bounded.
for _ in $(seq 1 30); do
  ss -lun 2>/dev/null | grep -q ":$PORT " && break
  sleep 1
done
if ! ss -lun 2>/dev/null | grep -q ":$PORT "; then
  echo "the server never bound $PORT; see $OUT/server.log" >&2
  exit 1
fi
# And a moment past that for the room: binding happens first.
sleep 3

pids=()
i=0
for spec in ALPHA:Samus BRAVO:Trace; do
  name="${spec%%:*}"; hunter="${spec##*:}"
  "$DN" FruityPrime.dll -netcheck 127.0.0.1 -port "$PORT" -name "$name" \
      -hunter "$hunter" -seconds "$(( SECS - i * 3 ))" -size 320x180 \
      -hitrig "$MODE" -netlag "$LAG" ${HITRIG_CLIENT_EXTRA:-} \
      > "$OUT/$name.log" 2>&1 &
  pids+=($!)
  i=$(( i + 1 ))
  sleep 3
done
wait "${pids[@]}"
# The reports are printed on the way out; killing the moment wait returns
# lost the second client's entire report on the first run of this.
sleep 6
echo "== $LABEL ($MODE, ${LAG}ms rtt injected, ceiling $MAXREWIND, $MAP) -> $OUT"
