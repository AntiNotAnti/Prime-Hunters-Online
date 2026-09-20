#!/usr/bin/env bash
# Build the ARM64 dedicated server and push it to the Pi.
#
# The service is stopped before the binary is replaced: systemd holds the
# executable open while it runs, so overwriting it in place fails.
#
# Credentials come from the environment, not this file:
#   MPH_SERVER_HOST=net.livetek.fr MPH_SERVER_USER=livetek ./deploy-server.sh
# With an SSH key installed, no password is needed at all -- which is the
# setup worth moving to.
set -euo pipefail

HOST="${MPH_SERVER_HOST:-net.livetek.fr}"
USER="${MPH_SERVER_USER:-livetek}"
REMOTE_DIR="${MPH_SERVER_DIR:-/home/$USER/mphread-server}"
SERVICE="mphread-server"
# The same binary also runs the server directory the launcher's browser asks.
# One upload, two units; set MPH_DEPLOY_MASTER=0 to leave the directory alone.
MASTER_SERVICE="mphread-master"
DEPLOY_MASTER="${MPH_DEPLOY_MASTER:-1}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/MphRead"
STAGE="$ROOT/publish/server-arm64"
# The project used to be MphRead and the Pi has been running a binary of that
# name under systemd since before the rename. Both names appear below: the new
# one is what gets installed, the old one is what has to be cleaned up and what
# the existing units still point at until they are rewritten.
BINARY="FruityPrime"
OLD_BINARY="MphRead"

# sshpass is only used when a password is supplied; a key-based setup skips it.
ssh_run() {
  if [ -n "${MPH_SERVER_PASS:-}" ]; then
    sshpass -p "$MPH_SERVER_PASS" ssh -o StrictHostKeyChecking=no "$USER@$HOST" "$@"
  else
    ssh -o StrictHostKeyChecking=no "$USER@$HOST" "$@"
  fi
}

scp_put() {
  if [ -n "${MPH_SERVER_PASS:-}" ]; then
    sshpass -p "$MPH_SERVER_PASS" scp -o StrictHostKeyChecking=no "$1" "$USER@$HOST:$2"
  else
    scp -o StrictHostKeyChecking=no "$1" "$USER@$HOST:$2"
  fi
}

echo "==> building linux-arm64"
# -p:MphReadServer=true: this box runs the server and the directory and nobody
# plays on it, so the launcher and the UI toolkit behind it are left out.
dotnet publish "$PROJECT" -c Release -r linux-arm64 -p:MphReadServer=true \
  --self-contained true -p:PublishSingleFile=true -o "$STAGE" \
  | grep -E "error|-> " || true
test -f "$STAGE/$BINARY" || { echo "build produced no $BINARY" >&2; exit 1; }

# Install a unit the first time, and leave a hand-edited one alone after that:
# an operator who changed the server name or the port on the box should not
# have it overwritten by a deploy.
#
# The rename is the one exception. A unit that still starts the old binary
# would keep starting it after this deploy -- the file would still be there,
# one release behind, refusing every client at Hello -- so a unit whose
# ExecStart names the old binary is rewritten in place. Only that line: an
# edited port or server name is preserved by patching rather than replacing.
install_unit() {
  local name="$1" template="$ROOT/tools/systemd/$1.service"
  if ssh_run "test -f /etc/systemd/system/$name.service"; then
    if ssh_run "grep -q '$REMOTE_DIR/$OLD_BINARY ' /etc/systemd/system/$name.service"; then
      echo "==> $name.service still starts $OLD_BINARY; pointing it at $BINARY"
      ssh_run "sudo sed -i 's|$REMOTE_DIR/$OLD_BINARY |$REMOTE_DIR/$BINARY |' \
        /etc/systemd/system/$name.service && sudo systemctl daemon-reload"
    fi
    return 0
  fi
  echo "==> installing $name.service"
  sed -e "s|__USER__|$USER|g" -e "s|__DIR__|$REMOTE_DIR|g" "$template" \
    | ssh_run "cat > /tmp/$name.service"
  ssh_run "sudo mv /tmp/$name.service /etc/systemd/system/$name.service \
    && sudo systemctl daemon-reload && sudo systemctl enable $name"
}

echo "==> stopping $SERVICE"
ssh_run "sudo systemctl stop $SERVICE" || true
if [ "$DEPLOY_MASTER" = "1" ]; then
  ssh_run "sudo systemctl stop $MASTER_SERVICE" || true
fi

echo "==> uploading"
scp_put "$STAGE/$BINARY" "$REMOTE_DIR/$BINARY.new"
ssh_run "chmod +x $REMOTE_DIR/$BINARY.new && mv $REMOTE_DIR/$BINARY.new $REMOTE_DIR/$BINARY"

# The units have to be pointing at the new binary before the old one is taken
# away, or a deploy that stops half way leaves a box with neither.
install_unit "$SERVICE"
if [ "$DEPLOY_MASTER" = "1" ]; then
  install_unit "$MASTER_SERVICE"
fi

if [ "$BINARY" != "$OLD_BINARY" ]; then
  if ssh_run "test -f $REMOTE_DIR/$OLD_BINARY"; then
    echo "==> removing the old $OLD_BINARY binary"
    ssh_run "rm -f $REMOTE_DIR/$OLD_BINARY"
  fi
fi

echo "==> starting $SERVICE"
ssh_run "sudo systemctl start $SERVICE"
if [ "$DEPLOY_MASTER" = "1" ]; then
  echo "==> starting $MASTER_SERVICE"
  ssh_run "sudo systemctl start $MASTER_SERVICE"
fi
sleep 3
ssh_run "systemctl is-active $SERVICE && journalctl -u $SERVICE -n 5 --no-pager | tail -4"
if [ "$DEPLOY_MASTER" = "1" ]; then
  ssh_run "systemctl is-active $MASTER_SERVICE \
    && journalctl -u $MASTER_SERVICE -n 5 --no-pager | tail -4"
fi

echo "==> done"
echo
echo "The browser in the launcher asks net.livetek.fr:27889 by default."
echo "That name has to resolve to this machine, and UDP 27889 has to reach it,"
echo "before any server shows up in anybody's list."
