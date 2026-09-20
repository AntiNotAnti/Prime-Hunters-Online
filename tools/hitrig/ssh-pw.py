#!/usr/bin/env python3
"""Run one command over SSH with a password, without sshpass.

    ssh-pw.py <host> <user> <password> <command>

sshpass is not installed on the box this rig runs from and installing it needs
root, so the password is typed into a pty the way a person would. Nothing is
written to disk and the password is never a shell word in the remote command --
it goes to the terminal ssh opens, and only when ssh asks for it.
"""
import os
import pty
import select
import sys
import time

TIMEOUT = float(os.environ.get("SSH_PW_TIMEOUT", "300"))


def main(host, user, password, command):
    argv = [
        "ssh", "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=/dev/null",
        # Password only. Without this an agent key that exists but is not
        # authorised burns the one prompt ssh was going to give us.
        "-o", "PreferredAuthentications=password",
        "-o", "PubkeyAuthentication=no",
        "-o", "ConnectTimeout=15",
        f"{user}@{host}", command,
    ]
    pid, fd = pty.fork()
    if pid == 0:
        os.execvp("ssh", argv)
        os._exit(1)
    out = b""
    typed = False
    deadline = time.time() + TIMEOUT
    while time.time() < deadline:
        ready, _, _ = select.select([fd], [], [], 2.0)
        if ready:
            try:
                chunk = os.read(fd, 8192)
            except OSError:
                break
            if not chunk:
                break
            out += chunk
            if not typed and b"assword" in out:
                os.write(fd, (password + "\n").encode())
                typed = True
            continue
        try:
            done, _ = os.waitpid(pid, os.WNOHANG)
        except ChildProcessError:
            break
        if done:
            break
    # The prompt ssh printed is in here too; the caller wants the command's
    # output, and stripping a line that merely contains "password" would eat a
    # legitimate one. Left in, and tailed by callers.
    sys.stdout.write(out.decode(errors="replace"))


if __name__ == "__main__":
    if len(sys.argv) != 5:
        print(__doc__)
        raise SystemExit(2)
    main(*sys.argv[1:5])
