#!/usr/bin/env bash
# Prove the dedicated-server startup contract and the server directory in a
# build actually run.
#
# A standalone dedicated server now runs the match itself. It therefore needs
# the game files, which cannot be put in this public repository or its CI
# artifacts. Publishing something is not enough to prove that contract: the
# binary must fail cleanly, before it can advertise a server which would hand
# the match to a client as the old relay did.
#
# So: start a directory and a standalone server without game files, require
# the server's actionable refusal and exit status, and ensure it never reaches
# the directory. Then query the directory itself. A separate integration run
# with an operator's extracted game files is what exercises an actual match.
#
#   tools/check-dedicated-server.sh publish/linux-x64
#   tools/check-dedicated-server.sh publish/win-x64-server   # Git Bash
#
# The Windows server is the same claim and gets the same check, rather than a
# PowerShell translation of it that would then have to be kept in step. What
# differs there is spelled out where it is handled: the binary is called
# MphReadServer.exe, python3 may only be `python`, and a path this script
# makes has to be converted before it is handed to a .NET process.
set -uo pipefail

DIR="${1:-publish/linux-x64}"
# The server binary first: on Windows the dedicated server is its own
# console binary, and a publish directory may hold both. The old MphRead
# names are still accepted so this can check a build from before the rename.
BIN=""
for candidate in FruityPrimeServer.exe FruityPrimeServer FruityPrime.exe FruityPrime \
                 MphReadServer.exe MphReadServer MphRead.exe MphRead; do
  if [ -x "$DIR/$candidate" ]; then
    BIN="$DIR/$candidate"
    break
  fi
done
[ -n "$BIN" ] || BIN="dotnet $DIR/FruityPrime.dll"

PYTHON="python3"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON="python"

# Git Bash hands out POSIX paths; a .NET process reads one as a path on the
# current drive and writes the rotation somewhere that does not exist.
topath() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s' "$1"
  fi
}

WORK="$(mktemp -d)"
SERVER_PORT=27888
MASTER_PORT=27889
FAILED=0

cleanup() {
  [ -n "${SERVER_PID:-}" ] && kill "$SERVER_PID" 2>/dev/null
  [ -n "${MASTER_PID:-}" ] && kill "$MASTER_PID" 2>/dev/null
  wait 2>/dev/null
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "FAIL: $*"; FAILED=1; }
pass() { echo "ok:   $*"; }

echo "checking the dedicated server in $DIR"

$BIN -masterserver -port "$MASTER_PORT" >"$WORK/master.log" 2>&1 &
MASTER_PID=$!
$BIN -server -port "$SERVER_PORT" -players 8 \
     -servername "CI smoke test" \
     -server_replays=true -server_replay_storage_gb=7 \
     -server_replay_retention_days=3 -server_replay_keep_last=9 \
     -master 127.0.0.1 -masterport "$MASTER_PORT" \
     -rotation "$(topath "$WORK/maprotation.txt")" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!

# The directory binds before it logs. The server binds before it checks whether
# it can build the authoritative world, then exits with the explanation rather
# than publishing a dead room.
for _ in $(seq 1 30); do
  grep -q "listening on UDP" "$WORK/master.log" 2>/dev/null \
    && grep -q "cannot run the match:" "$WORK/server.log" 2>/dev/null && break
  sleep 0.5
done

kill -0 "$MASTER_PID" 2>/dev/null || fail "the directory exited immediately"
grep -q "listening on UDP $MASTER_PORT" "$WORK/master.log" || fail "the directory never bound its port"
grep -q "listening on UDP $SERVER_PORT" "$WORK/server.log" \
  || fail "the server never reached its startup check"
grep -q "canonical server recording enabled; storage 7 GB, retention 3 days, keep newest 9" "$WORK/server.log" \
  && pass "server replay retention flags reached the recorder policy" \
  || fail "server replay retention flags did not reach the recorder policy"
grep -q "cannot run the match:" "$WORK/server.log" \
  && pass "the server explained that it cannot run without game files" \
  || fail "the server did not explain why it refused to start"
grep -q "Put the game files on this machine and paths.txt beside the binary" "$WORK/server.log" \
  && pass "the refusal tells an operator how to fix the installation" \
  || fail "the refusal did not tell an operator how to fix the installation"

if kill -0 "$SERVER_PID" 2>/dev/null; then
  fail "the server stayed up without the game files"
  kill "$SERVER_PID" 2>/dev/null || true
fi
wait "$SERVER_PID"
SERVER_STATUS=$?
[ "$SERVER_STATUS" -eq 1 ] \
  && pass "the server exited with status 1" \
  || fail "the server exited with status $SERVER_STATUS instead of 1"

"$PYTHON" - "$MASTER_PORT" <<'PY' || FAILED=1
import socket, sys
master_port = int(sys.argv[1])
ok = True

def check(label, condition):
    global ok
    print(("ok:   " if condition else "FAIL: ") + label)
    ok = ok and condition

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.settimeout(3)

# MasterQuery -> MasterList: the directory is still runnable without game
# files, but a standalone server that refused to start must not be listed.
try:
    s.sendto(bytes([18, 3]), ("127.0.0.1", master_port))
    d, _ = s.recvfrom(2048)
    check("the directory answered a list query", len(d) >= 3 and d[0] == 19)
    check("it listed no server that refused to start", len(d) >= 3 and d[1] == 0 and d[2] == 0)
except socket.timeout:
    check("the directory answered a list query", False)

sys.exit(0 if ok else 1)
PY

if [ "$FAILED" -ne 0 ]; then
  echo
  echo "--- server log ---"; cat "$WORK/server.log"
  echo "--- directory log ---"; cat "$WORK/master.log"
  exit 1
fi
echo "the dedicated-server startup contract and the directory both work in this build"
