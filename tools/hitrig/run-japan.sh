#!/usr/bin/env bash
# One arm of the headshot A/B against the bench server in Japan.
#
#   run-japan.sh <label> <mode: jump|sniper> <seconds> <maxrewind> [server flags]
#
# The arm's flag belongs to the *server*, because the server is the authority
# and the rewind is entirely its business -- so switching arms means restarting
# it, which this does over SSH before the clients start. The alternative,
# running two bench servers on two ports and picking one, would compare two
# processes rather than two settings.
#
# The authority's own report is pulled back afterwards and dropped in beside
# the clients' as authority.log: on a dedicated server the clients say
# "nothing to compensate" -- correctly, they compensate nothing -- and every
# rewind number in the run exists only in that journal.
#
# Credentials come from the environment, never this file:
#   HITRIG_JP_PASS=... run-japan.sh ...
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/tools/hitrig"
HOST="${HITRIG_JP_HOST:-13.78.14.98}"
USER="${HITRIG_JP_USER:-livetek}"
PORT="${HITRIG_JP_PORT:-27891}"
SERVICE=fruityprime-bench
REMOTE=/opt/fruityprime-bench
SSH="$HERE/ssh-pw.py"

LABEL="${1:?label}"; MODE="${2:?jump|sniper}"; SECS="${3:?seconds}"
MAXREWIND="${4:-24}"; shift 4 || shift $#
EXTRA="${*:-}"
OUT="${HITRIG_OUT:-/tmp/hitrig}/$LABEL"
mkdir -p "$OUT"

remote() {
  python3 "$SSH" "$HOST" "$USER" "${HITRIG_JP_PASS:?set HITRIG_JP_PASS}" "$1"
}

echo "== $LABEL: restarting the authority with -maxrewind $MAXREWIND ${EXTRA:-(no extra flags)}"
# A restart is also what zeroes the counters. Reading a delta out of a running
# server's cumulative totals would work, and it would silently stop working the
# first time a rotation reset them mid-arm.
remote "printf 'ARM_FLAGS=-maxrewind $MAXREWIND $EXTRA\n' > $REMOTE/arm.env \
  && sudo systemctl restart $SERVICE && sleep 6 && systemctl is-active $SERVICE" \
  | tail -2

HITRIG_OUT="${HITRIG_OUT:-/tmp/hitrig}" HITRIG_ROSTER="ALPHA:Samus BRAVO:Trace" \
  "$HERE/run-hit.sh" "$LABEL" "$HOST" "$PORT" "$SECS" -hitrig "$MODE"

# Everything the authority logged for this arm, which is everything about the
# rewind. --since is the arm and not the day: the service has been up for other
# arms, and the last line of a journal is not the last line of a run.
remote "sudo journalctl -u $SERVICE --no-pager --since '-$(( SECS + 60 )) seconds'" \
  > "$OUT/authority.log"
echo "== $LABEL -> $OUT"
grep -oE "sim: (lag compensation|rewind depths).*" "$OUT/authority.log" | tail -2
