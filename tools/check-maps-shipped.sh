#!/usr/bin/env bash
# Fail if a custom map would reach a player without the level it converts.
#
# A map in maps/ is the recipe, the level it converts and the textures baked
# from that level -- in a folder, or cooked into one .fpmap. The recipe alone
# registers a room that the game then declines to build -- the player sees the
# 27 cartridge rooms and no sign that anything was meant to be there. That is
# the right behaviour for a level nobody may publish, and a silent regression
# for one we ship on purpose, which is why it is checked rather than assumed.
#
#   tools/check-maps-shipped.sh                    # the repository
#   tools/check-maps-shipped.sh publish/win-x64    # a build we are about to release
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

root="${1:-}"
if [ -n "$root" ]; then
  maps="$root/maps"
  what="$root"
else
  maps="maps"
  what="the repository"
fi

fail=0
found=0
if [ ! -d "$maps" ]; then
  echo "no maps/ in $what; nothing to check"
  exit 0
fi

# A bundle is a map and its level in one file, so there is nothing beside it to
# look for: what has to be true is that the level is inside. Read the index
# without unpacking anything -- unzip -l is in every runner image, and a bundle
# with no .bsp in it is exactly the failure this script exists to catch.
while IFS= read -r file; do
  found=$((found + 1))
  name=$(basename "$file")
  if unzip -l "$file" 2>/dev/null | grep -qiE '\.bsp$'; then
    echo "ok:      $name carries its level inside it"
  else
    echo "MISSING: $name is a bundle with no level in it"
    fail=1
  fi
  # And its textures, which are the half that goes missing quietly. The pack is
  # baked from the level's own art, so it is derived and not in git -- a bundle
  # cooked where nobody had baked one carried a level and no art, and the room
  # it built had no materials at all: listed in the launcher, and a crash when
  # picked. The recipe inside names the pack; the pack has to be in there too.
  recipe=$(unzip -p "$file" '*.json' 2>/dev/null)
  textures=$(printf '%s' "$recipe" \
    | grep -oiE '"textures"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/')
  if [ -n "$textures" ]; then
    if unzip -l "$file" 2>/dev/null | grep -qiF "$textures"; then
      echo "ok:      $name carries its textures ($textures)"
    else
      echo "MISSING: $name names $textures and does not carry it"
      fail=1
    fi
  elif printf '%s' "$recipe" | grep -qE '"Materials"[[:space:]]*:[[:space:]]*\[\]'; then
    echo "MISSING: $name has no textures in it and borrows none, so its room has no materials"
    fail=1
  else
    echo "ok:      $name wears a shipped room's textures"
  fi
done < <(find "$maps" -name '*.fpmap' | sort)

# The level a map converts is named by import.source. Read it without a JSON
# parser: the field is one line in every file this writes, and a dependency on
# jq for a five-line check is worse than a grep.
#
# A recipe that has been cooked into a bundle beside it is the bundle's
# business, not its own: the working copy of a map keeps somebody's .pk3 in a
# folder, and that folder is not what ships.
while IFS= read -r file; do
  found=$((found + 1))
  name=$(basename "$file")
  source=$(grep -oiE '"source"[[:space:]]*:[[:space:]]*"[^"]+"' "$file" \
    | head -n1 | sed -E 's/.*"source"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
  if [ -z "$source" ]; then
    echo "ok:      $name builds from its own description, no level needed"
    continue
  fi
  bundle="$maps/$(basename "$file" .json).fpmap"
  if [ -f "$bundle" ]; then
    echo "ok:      $name ships as $(basename "$bundle")"
  elif [ -f "$(dirname "$file")/$source" ]; then
    echo "ok:      $name has $source beside it"
  else
    echo "MISSING: $name converts $source, which is not in $(dirname "$file")"
    fail=1
  fi
done < <(find "$maps" -name '*.json' -not -name '*.example' | sort)

if [ "$found" -eq 0 ]; then
  echo "no maps in $what"
  exit 0
fi
if [ "$fail" -ne 0 ]; then
  echo
  echo "A map whose level is absent is left out of the room list at startup; one"
  echo "that arrives without its textures is listed and cannot be built. Either"
  echo "ship what it needs beside it, or take the map file out."
  exit 1
fi
echo "every map in $what has what it needs"
