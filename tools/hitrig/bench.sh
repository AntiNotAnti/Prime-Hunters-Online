#!/usr/bin/env bash
# The headshot A/B, one arm at a time, against a server on this box.
#
#   bench.sh <seconds-per-run> [netlag-ms]
#
# Three arms and two scenarios. The arms differ by one server flag each and by
# nothing else -- same binary, same map, same scripted duel, same injected
# latency -- so a difference between them is that flag.
#
#   base      the ceiling every build up to v0.8.0 had: 24 frames, 400 ms
#   ceiling   the ceiling raised past what a 270 ms line actually asks for
#   both      that, plus rewinding a recovered trigger pull by its own age
#
# Serial rather than parallel: two matches on one box share a CPU, and a
# server that misses its step is a server whose frame counter -- which every
# ack is measured against -- is not the clock it claims to be.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/tools/hitrig"
SECS="${1:-240}"
# 270 ms with 60 of jitter and 2% loss each way, which is the Japan line as it
# actually measures rather than as a single number. The jitter is the part that
# matters: a fixed 270 ms never asks for more than 17 frames of rewind and so
# never reaches the ceiling at all, while the real distribution -- mean 19.6,
# worst pinned at 24 -- is jitter pushing the tail into it. Without the loss
# the press-age arm has nothing to correct, since a press is only ever stale
# when the packet that carried it did not arrive.
LAG="${2:-270:60}"
LOSS="${3:-2}"
OUT="${HITRIG_OUT:-/tmp/hitrig}"
export HITRIG_OUT="$OUT"

# 30 frames is 500 ms: past the 270 ms this line measures, past the 325 ms mean
# the Japan server reports, and still well short of the second of history the
# ring holds -- and short of the second Q-Zandronum itself allows, which bounds
# the rewind by its history and by nothing else (UNLAGGEDTICS 35, one second at
# 35 Hz). The point is a ceiling the room does not stand against.
RAISED=30

# Three arms, each a superset of the one before, so a change can be read where
# it lands rather than only in the total.
#
#   base   what v0.8.0 does: the 400 ms ceiling, puppets positioned from
#          relayed intents on every machine, and an ack naming the newest
#          snapshot received rather than the one applied
#   snap   + the client's puppets positioned from the snapshot alone -- the
#          world it draws, and the world the authority's history holds -- with
#          the ack that names it. A client-side change only
#   all    + the ceiling raised past what a 320 ms line asks for, and a
#          recovered trigger pull rewound by its own age. Server-side
#
# -clientpin is not an arm: -snapshotpuppets subsumes it, since a puppet the
# snapshot owns is never placed from an intent for the movement step to carry
# away in the first place.
run() {
  local label="$1" mode="$2" ceiling="$3" server="$4" client="$5"
  echo "== $label ($mode, ceiling $ceiling, server '${server:-none}', client '${client:-none}')"
  HITRIG_SERVER_EXTRA="$server" \
  HITRIG_CLIENT_EXTRA="-netloss $LOSS $client" \
    "$HERE/run-local.sh" "$label" "$mode" "$SECS" "$LAG" "$ceiling" > /dev/null 2>&1
}

ARMS="base:24::  snap:24::-snapshotpuppets  all:$RAISED:-pressage:-snapshotpuppets"

for mode in jump sniper; do
  for arm in $ARMS; do
    IFS=: read -r name ceiling server client <<< "$arm"
    run "$mode-$name" "$mode" "$ceiling" "$server" "$client"
  done
done

echo
echo "== ${SECS}s per arm, ${LAG} ms round trip and ${LOSS}% loss injected on both clients"
dirs=""
for mode in jump sniper; do
  for arm in $ARMS; do
    IFS=: read -r name _ _ _ <<< "$arm"
    dirs="$dirs $OUT/$mode-$name"
  done
done
# shellcheck disable=SC2086
python3 "$HERE/summarise.py" $dirs
