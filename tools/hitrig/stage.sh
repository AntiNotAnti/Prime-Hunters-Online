#!/usr/bin/env bash
# Freeze the current build into a directory the rig runs from.
#
#   stage.sh [dest]
#
# Not a convenience. .NET maps its assemblies into memory, so a rebuild that
# replaces ProjectPrime.dll while a run is in flight takes every client and the
# server down mid-match -- silently, leaving empty logs and a summary that
# reads as "the scenario produced nothing". Two runs of this rig were lost that
# way before it existed. A staged copy is what makes "build the next arm while
# this one is running" safe, and what makes an arm reproducible afterwards:
# the binary that produced a result is still sitting beside it.
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD="$ROOT/src/MphRead/bin/Release/net10.0"
DEST="${1:-$ROOT/tools/hitrig/bin}"
[ -f "$BUILD/ProjectPrime.dll" ] || { echo "no build at $BUILD"; exit 1; }
rm -rf "$DEST"
mkdir -p "$DEST"
cp -r "$BUILD"/. "$DEST"/
# paths.txt has to sit beside the DLL that is actually being run, and it is not
# a build output, so a fresh bin/Release never has it.
[ -f "$DEST/paths.txt" ] || cp "$HOME/mph-test/paths.txt" "$DEST"/ 2>/dev/null || true
[ -f "$DEST/paths.txt" ] || { echo "paths.txt missing: copy ~/mph-test/paths.txt into $DEST"; exit 1; }
echo "staged $(cd "$ROOT" && git rev-parse --short HEAD) -> $DEST"
