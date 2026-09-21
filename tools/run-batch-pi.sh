#!/usr/bin/env bash
# The randomised sweep, against the real dedicated server instead of a loopback
# one.
#
#   ./run-batch-pi.sh <runs> [seed] [max-extra-ms]
#
# Why this exists next to run-batch.sh: a loopback server reproduces neither
# the reordering a real path does, nor the jitter of somebody's line, nor the
# Pi's own processor with eight clients on it. What it does give is a latency
# you chose, and the Pi answers in 7-17 ms from here whether you like it or not.
#
# So both: the clients talk to udp-lag.py, and udp-lag.py talks to the Pi. The
# real path is underneath and the chosen delay is on top of it. With
# max-extra-ms 0 the relay is skipped and the clients reach the Pi directly.
#
# The map, match length and point goal come from the server's rotation file, so
# each run writes one over SSH and restarts the service -- and the original is
# put back at the end, whatever happens.
set -u
cd "$(dirname "$0")"
export LD_LIBRARY_PATH="$PWD/bin" MESA_GL_VERSION_OVERRIDE=4.5COMPAT ALSOFT_DRIVERS=null PULSE_SERVER=
DN="$HOME/.dotnet/dotnet"
RUNS="${1:-8}"
SEED="${2:-$RANDOM}"
MAX_EXTRA="${3:-150}"
RANDOM=$SEED
HOST="${MPH_SERVER_HOST:-51.161.113.128}"
USER_="${MPH_SERVER_USER:-livetek}"
PASS="${MPH_SERVER_PASS:-}"
PORT=27888
OUT="batchpi-$SEED"
mkdir -p "$OUT"
NAMES=(ALPHA BRAVO CHARLIE DELTA ECHO FOXTROT GOLF HOTEL)
HUNTERS=(Samus Kanden Trace Sylux Noxus Spire Weavel)
MAPS=("MP1 SANCTORUS" "MP2 HARVESTER" "MP3 PROVING GROUND" "MP4 HIGHGROUND" "MP6 HEADSHOT"
      "MP9 CRYOCHASM" "MP11 BREAKTHROUGH" "MP12 SIC TRANSIT" "MP13 ACCELERATOR" "MP14 OUTER REACH")
EXTRAS=(0 0 30 60 100 "$MAX_EXTRA")

BUILD=~/MphRead-dev/src/MphRead/bin/Release/net10.0
cp "$BUILD"/ProjectPrime.dll "$BUILD"/ProjectPrime.deps.json "$BUILD"/ProjectPrime.runtimeconfig.json bin/ || exit 1
GAME=bin/ProjectPrime.dll

ssh_pi() {
  if [ -n "$PASS" ]; then sshpass -p "$PASS" ssh -o StrictHostKeyChecking=no "$USER_@$HOST" "$@"
  else ssh -o StrictHostKeyChecking=no "$USER_@$HOST" "$@"; fi
}

LAGPID=""
stop_relay() { [ -n "$LAGPID" ] && kill "$LAGPID" 2>/dev/null; LAGPID=""; }
restore() {
  stop_relay
  echo "restoring the server's own rotation"
  ssh_pi "cd ~/mphread-server && [ -f maprotation.batch ] && mv maprotation.batch maprotation.txt && sudo systemctl restart mphread-server" >/dev/null 2>&1
}
trap restore EXIT

echo "batch-pi seed $SEED, $RUNS run(s) against $HOST:$PORT, output in $OUT" | tee "$OUT/summary.txt"
ssh_pi "cd ~/mphread-server && [ -f maprotation.batch ] || cp maprotation.txt maprotation.batch" >/dev/null 2>&1 \
  || { echo "cannot reach the server over SSH; nothing done" | tee -a "$OUT/summary.txt"; exit 1; }

for i in $(seq 1 "$RUNS"); do
  RUNDIR="$OUT/$(printf %02d "$i")"; mkdir -p "$RUNDIR"
  PLAYERS=$(( RANDOM % 4 + 2 ))
  (( i % 4 == 0 )) && PLAYERS=6
  MAP="${MAPS[$(( RANDOM % ${#MAPS[@]} ))]}"
  EXTRA="${EXTRAS[$(( RANDOM % ${#EXTRAS[@]} ))]}"
  SECS=$(( RANDOM % 50 + 80 ))
  MINUTES=$(( RANDOM % 3 + 1 ))
  GOAL=$(( RANDOM % 4 + 2 ))
  ROSTER=(); for _ in $(seq 1 "$PLAYERS"); do ROSTER+=("${HUNTERS[$(( RANDOM % 7 ))]}"); done

  echo "== run $i: $PLAYERS players on '$MAP' via $HOST, +${EXTRA}ms, ${SECS}s, ${MINUTES}min/${GOAL}pts, ${ROSTER[*]}" \
    | tee -a "$OUT/summary.txt"
  ssh_pi "cd ~/mphread-server && printf '%s | Battle | %s | %s\n' '$MAP' '$MINUTES' '$GOAL' > maprotation.txt && sudo systemctl restart mphread-server" >/dev/null 2>&1
  sleep 4

  TARGET_HOST="$HOST"; TARGET_PORT="$PORT"
  stop_relay
  if [ "$EXTRA" -gt 0 ]; then
    setsid python3 udp-lag.py 27998 "$HOST" "$PORT" "$EXTRA" $(( EXTRA / 5 )) 0 > "$RUNDIR/lag.log" 2>&1 < /dev/null &
    LAGPID=$!
    TARGET_HOST=127.0.0.1; TARGET_PORT=27998
    sleep 1
  fi

  logs=(); clients=(); index=0
  for hunter in "${ROSTER[@]}"; do
    name="${NAMES[$index]}"; logs+=("$RUNDIR/$name.log")
    ( "$DN" "$GAME" -netcheck "$TARGET_HOST" -port "$TARGET_PORT" -name "$name" -hunter "$hunter" \
        -seconds "$(( SECS - index * 3 ))" -size 320x180 > "$RUNDIR/$name.log" 2>&1 ) &
    clients+=($!); index=$(( index + 1 )); sleep 3
  done
  wait "${clients[@]}"
  stop_relay

  python3 compare-reports.py "${logs[@]}" > "$RUNDIR/cross.txt" 2>&1
  {
    grep -hE "Unhandled exception" "$RUNDIR"/*.log | head -2
    grep -h "could not join" "$RUNDIR"/*.log
    grep -h "FAIL:" "$RUNDIR"/*.log
    grep -hE "^MISMATCH" "$RUNDIR/cross.txt"
    grep -h "teleport(s), worst jump" "$RUNDIR"/*.log | grep -v "0 teleport(s)"
  } > "$RUNDIR/problems.txt" 2>/dev/null
  grep -c "pulled back" bin/netlog-*.txt 2>/dev/null | grep -v ":0$" >> "$RUNDIR/problems.txt"
  cp bin/netlog-*.txt "$RUNDIR/" 2>/dev/null; rm -f bin/netlog-*.txt

  if [ -s "$RUNDIR/problems.txt" ]; then
    echo "   PROBLEMS:" | tee -a "$OUT/summary.txt"
    sed 's/^/     /' "$RUNDIR/problems.txt" | sort | uniq -c | sort -rn | head -10 | tee -a "$OUT/summary.txt"
  else
    echo "   clean" | tee -a "$OUT/summary.txt"
  fi
done
echo "== finished" | tee -a "$OUT/summary.txt"
