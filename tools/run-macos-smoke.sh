#!/usr/bin/env bash
set -euo pipefail
[[ $# == 2 ]] || { echo 'usage: run-macos-smoke.sh <publish-directory> <rid>' >&2; exit 1; }
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$1" && pwd)
case "$2:$(uname -m)" in
    osx-arm64:arm64|osx-x64:x86_64) ;;
    *) echo "error: $2 must be executed on its matching native runner" >&2; exit 1 ;;
esac
# A fresh HOME and unrelated cwd exercise a first launch without touching a
# developer's settings. A native deadlock must fail CI instead of hanging it.
graphics=true
if "$repo/tools/check-macos-graphics.sh"; then
    :
else
    status=$?
    [[ $status == 77 ]] || exit "$status"
    graphics=false
    echo '::warning::No accelerated macOS OpenGL renderer: rendered launcher/thumbnail checks NOT RUN. Native library/startup checks still required; hardware acceptance remains pending.'
fi
python3 - "$root/ProjectPrime" "$graphics" <<'PY'
import os
import subprocess
import sys
import tempfile
with tempfile.TemporaryDirectory(prefix="fruity-smoke-") as temp:
    env = dict(os.environ, HOME=temp, DOTNET_CLI_HOME=temp)
    result = subprocess.run([sys.argv[1], "-smoketest"], cwd=temp, env=env, timeout=120)
    if result.returncode:
        sys.exit(result.returncode)
    result = subprocess.run([sys.argv[1], "-glfwpathcheck"], cwd=temp, env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=120)
    print(result.stdout, end="")
    if result.returncode or "GLFW extraction path check passed." not in result.stdout:
        sys.exit(result.returncode or 1)
    if sys.argv[2] != "true":
        sys.exit(0)
    for flag, marker in [("-windowcheck", "Launcher window check passed."),
                         ("-thumbnailwindowcheck", "Thumbnail window check passed.")]:
        result = subprocess.run([sys.argv[1], flag], cwd=temp, env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, timeout=120)
        print(result.stdout, end="")
        if result.returncode or marker not in result.stdout:
            sys.exit(result.returncode or 1)
PY
