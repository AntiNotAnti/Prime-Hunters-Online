#!/usr/bin/env bash
# Fail if anything that came out of a Nintendo cartridge is about to be
# published -- in the repository, or in a build we are going to release.
#
# The rule this enforces: MphRead ships code. Every map, model, sound, font
# and *map preview* is produced on the player's own machine from the player's
# own dump, and none of it belongs in git or in a release archive. Map
# previews are the easy one to get wrong: they are rendered locally into
# thumbnails/ and look like ordinary screenshots.
#
# A custom map's level file is the exception, and deliberately so: what this
# is for is keeping somebody else's commercial data out, and a map made by a
# person who wants it played is not that. See BANNED_LEVEL below for the one
# case still refused by name.
#
#   tools/check-no-game-assets.sh              # what git is tracking
#   tools/check-no-game-assets.sh publish/win-x64 ...   # what we are about to ship
#
# An entry in tools/asset-guard-allow.txt (one glob per line, # for comments)
# exempts a path -- for a logo or a screenshot of the launcher itself, which
# are ours. Nothing is exempt by default.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

ALLOW_FILE="tools/asset-guard-allow.txt"
# Extensions that only ever come from the game's files, plus the picture
# formats a preview would be saved as.
BANNED_EXT='nds|bin|arc|narc|sdat|sbin|spc|wav|mp3|ogg|brstm|png|jpg|jpeg|gif|bmp|tga|dds'
# A level for the map importer is allowed, because the thing this guard exists
# to stop is publishing somebody else's *commercial* data -- the cartridge, and
# a game somebody bought. A custom map is neither: it is one person's work, and
# a map you have the right to publish belongs with the map file that converts
# it. That makes what a level may contain a judgement this script cannot make,
# so it makes the one it can: id Software's own paks are refused by name.
BANNED_LEVEL='(^|/)pak[0-9]+\.pk3$'
# A level is legitimately larger than source; nothing else under maps/ is.
BIG_OK='(^|/)maps/.*\.(pk3|bsp)$'
# Directories the extraction and the preview cache write into.
BANNED_PATH='(^|/)(thumbnails|files|_archives|Savedata|netcheck-shots)/|(^|/)paths\.txt$|(^|/)netlog-[^/]*\.txt$'
# Nothing *tracked by git* should be this big; an asset dump would be. Build
# output is exempt: a self-contained binary is a hundred megabytes of runtime.
MAX_BYTES=$((2 * 1024 * 1024))
CHECK_SIZE=1

allowed() {
  [ -f "$ALLOW_FILE" ] || return 1
  local path="$1" pattern
  while IFS= read -r pattern; do
    case "$pattern" in ''|'#'*) continue ;; esac
    # shellcheck disable=SC2254
    case "$path" in $pattern) return 0 ;; esac
  done < "$ALLOW_FILE"
  return 1
}

fail=0
report() {
  echo "REFUSED: $1 -- $2"
  fail=1
}

check_list() {
  local what="$1"
  shift
  local path
  for path in "$@"; do
    [ -n "$path" ] || continue
    if allowed "$path"; then
      continue
    fi
    if echo "$path" | grep -qiE "\.($BANNED_EXT)$"; then
      report "$path" "a game asset, or a picture of one ($what)"
      continue
    fi
    if echo "$path" | grep -qiE "$BANNED_LEVEL"; then
      report "$path" "one of id Software's own paks, which is theirs and not ours ($what)"
      continue
    fi
    if echo "$path" | grep -qE "$BANNED_PATH"; then
      report "$path" "lives where extracted files and previews go ($what)"
      continue
    fi
    if echo "$path" | grep -qiE "$BIG_OK"; then
      continue
    fi
    if [ "$CHECK_SIZE" -eq 1 ] && [ -f "$path" ]; then
      local size
      size=$(wc -c < "$path")
      if [ "$size" -gt "$MAX_BYTES" ]; then
        report "$path" "$size bytes, too big to be source ($what)"
      fi
    fi
  done
}

if [ "$#" -eq 0 ]; then
  echo "== checking what git is tracking =="
  tracked=()
  while IFS= read -r -d '' path; do tracked+=("$path"); done < <(git ls-files -z)
  check_list "tracked by git" "${tracked[@]}"
  if ! grep -q '^thumbnails/$' .gitignore; then
    report ".gitignore" "no longer ignores thumbnails/, so previews can be committed"
  fi
else
  CHECK_SIZE=0
  for dir in "$@"; do
    echo "== checking $dir =="
    if [ ! -d "$dir" ]; then
      report "$dir" "not a directory"
      continue
    fi
    found=()
    while IFS= read -r -d '' path; do found+=("${path#./}"); done < <(find "$dir" -type f -print0)
    check_list "in $dir" "${found[@]}"
  done
fi

if [ "$fail" -ne 0 ]; then
  echo
  echo "Nothing from the game may be published. If one of these is ours --"
  echo "a logo, a screenshot of the launcher -- add it to $ALLOW_FILE."
  exit 1
fi
echo "clean: nothing from the game is being published"
