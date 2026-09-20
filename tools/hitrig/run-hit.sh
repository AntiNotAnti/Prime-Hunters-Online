#!/usr/bin/env bash
# Two scripted clients against a real server, reported side by side.
#
#   run-hit.sh <label> <host> <port> <seconds> [extra client flags...]
#
# Everything the harness needs is exported here rather than assumed from the
# calling shell: a backgrounded client inherits only what this file sets, and
# a missing DOTNET_SYSTEM_GLOBALIZATION_INVARIANT is a FailFast before main().
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# The staged copy, not the build tree. See stage.sh: a rebuild replaces the
# assembly under the running processes.
BUILD="${HITRIG_BIN:-$ROOT/tools/hitrig/bin}"
DN="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export LD_LIBRARY_PATH="$BUILD"
export MESA_GL_VERSION_OVERRIDE=4.5COMPAT ALSOFT_DRIVERS=null PULSE_SERVER=

LABEL="${1:?label}"; HOST="${2:?host}"; PORT="${3:?port}"; SECS="${4:?seconds}"
shift 4
# NAME:HUNTER pairs, space separated. A plain string rather than an array so a
# caller can set it in the environment -- bash arrays do not survive export,
# and the rig is driven from other scripts as often as by hand.
ROSTER="${HITRIG_ROSTER:-ALPHA:Samus BRAVO:Trace}"
OUT="${HITRIG_OUT:-/tmp/hitrig}/$LABEL"
mkdir -p "$OUT"
cd "$BUILD" || exit 1
[ -f paths.txt ] || { echo "paths.txt missing beside $BUILD/FruityPrime.dll"; exit 1; }

pids=()
i=0
for spec in $ROSTER; do
  name="${spec%%:*}"; hunter="${spec##*:}"
  # Staggered, because two clients admitted in the same datagram burst take
  # the same slot decision from a server that has not yet published the first.
  # HITRIG_CLIENT_EXTRA carries the arm's client-side flag (-clientpin, say).
  # Unquoted on purpose: it is a list of flags, not one argument.
  # shellcheck disable=SC2086
  "$DN" FruityPrime.dll -netcheck "$HOST" -port "$PORT" -name "$name" \
      -hunter "$hunter" -seconds "$(( SECS - i * 3 ))" -size 320x180 \
      ${HITRIG_CLIENT_EXTRA:-} "$@" \
      > "$OUT/$name.log" 2>&1 &
  pids+=($!)
  i=$(( i + 1 ))
  sleep 3
done
wait "${pids[@]}"
echo "== $LABEL: $OUT"
