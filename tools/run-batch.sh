#!/usr/bin/env bash
# A randomised sweep of networked matches, looking for what a fixed scenario
# cannot find.
#
#   ./run-batch.sh <runs> [seed]
#
# Each run picks its own map, hunter roster, player count, match length and
# latency, so the things a single hand-picked scenario holds constant -- who
# shoots whom, which hunter is the authority, whether anybody morphs near a
# jump pad -- vary instead. Every run's logs are kept under batch-<seed>/NN/,
# and the summary at the end lists only the runs that failed.
set -u
cd "$(dirname "$0")"
export LD_LIBRARY_PATH="$PWD/bin" MESA_GL_VERSION_OVERRIDE=4.5COMPAT ALSOFT_DRIVERS=null PULSE_SERVER=
DN="$HOME/.dotnet/dotnet"
RUNS="${1:-10}"
SEED="${2:-$RANDOM}"
RANDOM=$SEED
OUT="batch-$SEED"
mkdir -p "$OUT"
NAMES=(ALPHA BRAVO CHARLIE DELTA ECHO FOXTROT GOLF HOTEL)
HUNTERS=(Samus Kanden Trace Sylux Noxus Spire Weavel)
# The six First Hunt "biodefense chamber" rooms have no player spawn points, so
# a match there places nobody. Everything else in -rooms is fair game.
MAPS=(
  "AD1 TRANSFER LOCK BT" "AD1 TRANSFER LOCK DM" "AD2 ALINOS PERCH" "AD2 MAGMA VENTS"
  "CTF1 FAULT LINE - EXPANDED" "CTF1_FAULT LINE" "E3 FIRST HUNT" "Gorea Prison"
  "MP1 SANCTORUS" "MP10 OVERLOAD" "MP11 BREAKTHROUGH" "MP12 SIC TRANSIT"
  "MP13 ACCELERATOR" "MP14 OUTER REACH" "MP2 HARVESTER" "MP3 PROVING GROUND"
  "MP4 HIGHGROUND" "MP4 HIGHGROUND - EXPANDED" "MP5 FUEL SLUICE" "MP6 HEADSHOT"
  "MP7 PROCESSOR CORE" "MP8 FIRE CONTROL" "MP9 CRYOCHASM" "UNIT 3 VESPER STARPORT"
  "UNIT 4 ARCTERRA BASE" "UNIT1 ALINOS LANDFALL" "UNIT2 LANDING BAY"
)
# One way, so the round trip is twice these. 250 is a bad mobile connection
# from another continent; the point of the top of the range is that everything
# timing-related in the protocol -- press history, snapshot ordering, the
# respawn handshake -- is asked a question it cannot answer by being fast.
LAGS=(0 15 30 60 100 150 250)
# A few runs also lose packets outright. UDP does, the press history and the
# damage counter exist because of it, and nothing here had ever been measured
# against any.
LOSSES=(0 0 0 1 2 3)

BUILD=~/MphRead-dev/src/MphRead/bin/Release/net10.0
cp "$BUILD"/ProjectPrime.dll "$BUILD"/ProjectPrime.deps.json "$BUILD"/ProjectPrime.runtimeconfig.json bin/ || exit 1
GAME=bin/ProjectPrime.dll

cleanup() {
  for q in $(pgrep -f "[u]dp-lag.py" 2>/dev/null); do kill -9 "$q" 2>/dev/null; done
  for p in $(pgrep -x dotnet); do
    tr '\0' ' ' < /proc/$p/cmdline 2>/dev/null | grep -q -- "-server" && kill -9 "$p" 2>/dev/null
  done
}
trap cleanup EXIT

echo "batch seed $SEED, $RUNS run(s), output in $OUT" | tee "$OUT/summary.txt"
FAILED=0
for i in $(seq 1 "$RUNS"); do
  RUNDIR="$OUT/$(printf %02d "$i")"
  mkdir -p "$RUNDIR"
  PLAYERS=$(( RANDOM % 5 + 2 ))                 # 2..6 clients on one box
  (( i % 5 == 0 )) && PLAYERS=8                 # and one full house every fifth
  MAP="${MAPS[$(( RANDOM % ${#MAPS[@]} ))]}"
  LAG="${LAGS[$(( RANDOM % ${#LAGS[@]} ))]}"
  LOSS="${LOSSES[$(( RANDOM % ${#LOSSES[@]} ))]}"
  SECS=$(( RANDOM % 60 + 70 ))
  # A short match length and a low point goal so some runs rotate mid-run,
  # which is the case every "is this a new match" bug hides in.
  MINUTES=$(( RANDOM % 3 + 1 ))
  GOAL=$(( RANDOM % 4 + 2 ))
  ROSTER=()
  for _ in $(seq 1 "$PLAYERS"); do ROSTER+=("${HUNTERS[$(( RANDOM % 7 ))]}"); done

  echo "== run $i: $PLAYERS players on '$MAP', ${LAG}ms each way, ${LOSS}% loss, ${SECS}s, ${MINUTES}min/${GOAL}pts, ${ROSTER[*]}" \
    | tee -a "$OUT/summary.txt"
  printf '%s | Battle | %s | %s\n' "$MAP" "$MINUTES" "$GOAL" > bin/maprotation.txt

  cleanup; sleep 1
  setsid "$DN" "$GAME" -server -port 27999 -players 8 -nomaster > "$RUNDIR/server.log" 2>&1 < /dev/null &
  sleep 3
  PORT=27999
  if [ "$LAG" -gt 0 ] || [ "$LOSS" -gt 0 ]; then
    setsid python3 udp-lag.py 27998 127.0.0.1 27999 "$LAG" $(( LAG / 5 )) "$LOSS" > "$RUNDIR/lag.log" 2>&1 < /dev/null &
    PORT=27998
    sleep 1
  fi

  logs=(); clients=(); index=0
  for hunter in "${ROSTER[@]}"; do
    name="${NAMES[$index]}"
    logs+=("$RUNDIR/$name.log")
    ( "$DN" "$GAME" -netcheck 127.0.0.1 -port "$PORT" -name "$name" -hunter "$hunter" \
        -seconds "$(( SECS - index * 3 ))" -size 320x180 > "$RUNDIR/$name.log" 2>&1 ) &
    clients+=($!)
    index=$(( index + 1 ))
    sleep 3
  done
  wait "${clients[@]}"
  cleanup

  python3 compare-reports.py "${logs[@]}" > "$RUNDIR/cross.txt" 2>&1
  # What counts as a problem: a crash, a client that never joined, a feature
  # that did not cross, a visible teleport, or a position the local player had
  # to be dragged back from.
  {
    grep -hE "Unhandled exception|Stack trace" "$RUNDIR"/*.log | head -3
    grep -h "could not join" "$RUNDIR"/*.log
    grep -h "FAIL:" "$RUNDIR"/*.log
    grep -hE "^MISMATCH" "$RUNDIR/cross.txt"
    grep -h "teleport(s), worst jump" "$RUNDIR"/*.log | grep -v "0 teleport(s)"
  } > "$RUNDIR/problems.txt" 2>/dev/null
  grep -c "pulled back" bin/netlog-*.txt 2>/dev/null | grep -v ":0$" >> "$RUNDIR/problems.txt"
  cp bin/netlog-*.txt "$RUNDIR/" 2>/dev/null

  if [ -s "$RUNDIR/problems.txt" ]; then
    FAILED=$(( FAILED + 1 ))
    echo "   PROBLEMS:" | tee -a "$OUT/summary.txt"
    sed 's/^/     /' "$RUNDIR/problems.txt" | sort | uniq -c | sort -rn | head -12 | tee -a "$OUT/summary.txt"
  else
    echo "   clean" | tee -a "$OUT/summary.txt"
  fi
  rm -f bin/netlog-*.txt
done
echo "== $FAILED of $RUNS run(s) reported something" | tee -a "$OUT/summary.txt"
