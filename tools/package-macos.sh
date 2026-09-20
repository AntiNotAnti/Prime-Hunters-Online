#!/usr/bin/env bash
set -euo pipefail
[[ $# == 4 ]] || { echo 'usage: package-macos.sh <publish-directory> <dist-directory> <rid> <version>' >&2; exit 1; }
[[ $(uname -s) == Darwin ]] || { echo 'error: packaging requires macOS' >&2; exit 1; }
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$1" && pwd)
mkdir -p "$2"
dist=$(cd "$2" && pwd)
rid=$3
version=${4#v}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'error: version must be X.Y.Z' >&2; exit 1; }
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'error: unsupported RID' >&2; exit 1 ;; esac
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
app="$stage/package/Fruity Prime.app"
contents="$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources"
# Keep the publish layout intact: .NET and native dependencies probe beside
# the apphost. Apple's signer treats subdirectories of MacOS as nested code,
# so map and controller data belong in Resources, resolved by AppPaths.ResourceDirectory.
ditto "$root" "$contents/MacOS"
if [[ -d "$contents/MacOS/maps" ]]; then
    mv "$contents/MacOS/maps" "$contents/Resources/maps"
fi
for resource in gamecontrollerdb.txt gamecontrollerdb.LICENSE; do
    if [[ -f "$contents/MacOS/$resource" ]]; then
        mv "$contents/MacOS/$resource" "$contents/Resources/$resource"
    fi
done
cp "$repo/src/MphRead/Platforms/macOS/Info.plist" "$contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleShortVersionString string $version" "$contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleVersion string $version" "$contents/Info.plist"
plutil -lint "$contents/Info.plist"
iconset="$stage/FruityPrime.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$repo/src/MphRead/Assets/fruity-prime-mark.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$repo/src/MphRead/Assets/fruity-prime-mark.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$contents/Resources/FruityPrime.icns"
cp "$repo/LICENSE" "$stage/package/LICENSE"
cp "$repo/tools/macos-README.txt" "$stage/package/README.txt"
chmod +x "$contents/MacOS/FruityPrime"
"$repo/tools/sign-macos.sh" "$contents/MacOS"
codesign --force --sign - --entitlements "$repo/src/MphRead/Platforms/macOS/FruityPrime.entitlements" "$app"
codesign --verify --deep --strict --verbose=4 "$app"
"$repo/tools/check-macos-build.sh" "$contents/MacOS" "$rid"
"$repo/tools/check-no-game-assets.sh" "$stage/package"
"$repo/tools/check-maps-shipped.sh" "$contents/Resources"
"$repo/tools/run-macos-smoke.sh" "$contents/MacOS" "$rid"
archive="$stage/FruityPrime-v$version-$rid.tar.gz"
COPYFILE_DISABLE=1 tar -czf "$archive" -C "$stage/package" .
# Validate what a player extracts, including mode bits and resource seals.
mkdir "$stage/extracted"
tar -xzf "$archive" -C "$stage/extracted"
codesign --verify --deep --strict --verbose=4 "$stage/extracted/Fruity Prime.app"
"$repo/tools/run-macos-smoke.sh" "$stage/extracted/Fruity Prime.app/Contents/MacOS" "$rid"
cp "$archive" "$dist/"
