#!/usr/bin/env bash
# Fail if a custom map would reach a player without the level it converts.
#
# A map in maps/ is the recipe, the level it converts and the textures baked
# from that level -- in a folder, or cooked into one .ppmap. The recipe alone
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

# A package may be either native geometry or an imported map. Native packages
# intentionally contain no BSP; imported packages must carry the source path
# named by project.json. Read the archive without extracting it.
while IFS= read -r file; do
  found=$((found + 1))
  name=$(basename "$file")
  recipe=$(unzip -p "$file" project.json 2>/dev/null || true)
  if [ -z "$recipe" ]; then
    # Legacy one-recipe packages have no v2 manifest/project naming.
    recipe=$(unzip -p "$file" '*.json' 2>/dev/null | head -c 8388608)
  fi
  if [ -z "$recipe" ]; then
    echo "MISSING: $name has no readable map project"
    fail=1
    continue
  fi

  source=$(printf '%s' "$recipe" \
    | grep -oiE '"source"[[:space:]]*:[[:space:]]*"[^"]+"' \
    | head -n1 | sed -E 's/.*"source"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
  if [ -n "$source" ]; then
    if unzip -Z1 "$file" 2>/dev/null | grep -qiFx "$source"; then
      echo "ok:      $name carries its imported level ($source)"
    else
      echo "MISSING: $name names imported level $source and does not carry it"
      fail=1
    fi
  else
    echo "ok:      $name is native geometry; no BSP required"
  fi

  textures=$(printf '%s' "$recipe" \
    | grep -oiE '"textures"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/')
  if [ -n "$textures" ]; then
    if unzip -Z1 "$file" 2>/dev/null | grep -qiFx "$textures"; then
      echo "ok:      $name carries its textures ($textures)"
    else
      echo "MISSING: $name names $textures and does not carry it"
      fail=1
    fi
  else
    echo "ok:      $name needs no imported texture pack"
  fi
done < <(find "$maps" -name '*.ppmap' | sort)

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
  bundle="$maps/$(basename "$file" .json).ppmap"
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
