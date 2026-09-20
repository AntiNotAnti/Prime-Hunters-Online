#!/usr/bin/env bash
# Assert what a Windows binary's PE header says it is.
#
# MphRead ships two Windows executables and the difference between them is one
# 16-bit field in the PE header:
#
#   MphRead.exe        GUI     double-clicking it opens the launcher and no
#                              terminal appears behind it
#   MphReadServer.exe  console it holds a terminal, a shell waits for it, and
#                              its exit code reaches %ERRORLEVEL%
#
# Neither is observable from a compile, both come out of the same csproj
# depending on one property, and getting either wrong is invisible until
# somebody double-clicks the game and gets a black window, or runs the server
# from a terminal and gets the prompt straight back with the log arriving on
# top of whatever they type next. So it is asserted.
#
#   tools/check-subsystem.sh console publish/win-x64-server/MphReadServer.exe
#   tools/check-subsystem.sh gui     publish/win-x64/MphRead.exe
#
# Reads the header only, so it runs on the machine that built the binary
# whether or not that machine is Windows.
set -uo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <gui|console> <path to .exe>" >&2
  exit 2
fi

PYTHON="python3"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON="python"

"$PYTHON" - "$1" "$2" <<'PY'
import struct, sys

want, path = sys.argv[1], sys.argv[2]
# IMAGE_SUBSYSTEM_WINDOWS_GUI / _CUI, from the PE spec.
NAMES = {2: "gui", 3: "console"}
EXPECTED = {"gui": 2, "console": 3}
if want not in EXPECTED:
    sys.exit(f"FAIL: {want!r} is not gui or console")

with open(path, "rb") as handle:
    head = handle.read(4096)

# e_lfanew at 0x3C points at the PE signature; the optional header follows the
# 4-byte signature and the 20-byte COFF header, and Subsystem sits 68 bytes
# into it -- at the same offset for PE32 and PE32+, which is why the magic
# does not have to be read to find it.
pe = struct.unpack_from("<I", head, 0x3C)[0]
if head[pe:pe + 4] != b"PE\0\0":
    sys.exit(f"FAIL: {path} is not a PE executable")
found = struct.unpack_from("<H", head, pe + 4 + 20 + 68)[0]

if found != EXPECTED[want]:
    sys.exit(f"FAIL: {path} is {NAMES.get(found, found)}, expected {want}")
print(f"ok:   {path} is a {want} binary")
PY
