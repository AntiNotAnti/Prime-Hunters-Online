#!/usr/bin/env bash
# Negative regressions for the gates used by both workflows; no game assets.
set -euo pipefail
[[ $(uname -s) == Darwin ]] || { echo 'error: tests require macOS' >&2; exit 1; }
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
case "$(uname -m)" in
    arm64) rid=osx-arm64; other=x86_64 ;;
    x86_64) rid=osx-x64; other=arm64 ;;
    *) exit 1 ;;
esac
project="$repo/src/MphRead/MphRead.csproj"
[[ $(dotnet msbuild "$project" -getProperty:MphReadAudioRid) == "$rid" ]] \
    || { echo 'error: local build selected the wrong audio RID' >&2; exit 1; }
for target in osx-arm64 osx-x64; do
    [[ $(dotnet msbuild "$project" -p:RuntimeIdentifier="$target" -getProperty:MphReadAudioRid) == "$target" ]] \
        || { echo 'error: explicit audio RID was ignored' >&2; exit 1; }
done
root="$temp/publish with spaces"
mkdir -p "$root/nested"
# This synthetic packaging fixture acknowledges the wrapper's success marker;
# the publish jobs separately run the real launcher's rendered window check.
printf '#include <stdio.h>\nint main(void) { puts("Launcher window check passed."); puts("GLFW extraction path check passed."); puts("Thumbnail window check passed."); return 0; }\n' > "$temp/main.c"
printf 'int native_probe(void) { return 42; }\n' > "$temp/native.c"
clang "$temp/main.c" -o "$root/FruityPrime"
clang -dynamiclib "$temp/native.c" -o "$root/libopenal.1.dylib"
clang -dynamiclib "$temp/native.c" -o "$root/nested/extensionless-native"
"$repo/tools/sign-macos.sh" "$root"
"$repo/tools/check-macos-build.sh" "$root" "$rid"
expect_failure() {
    if "$@" > "$temp/failure.log" 2>&1; then
        cat "$temp/failure.log"
        echo "error: unexpectedly accepted: $*" >&2
        exit 1
    fi
    cat "$temp/failure.log"
}
expect_failure "$repo/tools/check-macos-build.sh" "$root" unsupported
chmod -x "$root/FruityPrime"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
chmod +x "$root/FruityPrime"
mv "$root/libopenal.1.dylib" "$temp/openal"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
mv "$temp/openal" "$root/libopenal.1.dylib"
clang -arch "$other" -dynamiclib "$temp/native.c" -o "$root/nested/wrong.dylib"
codesign --force --sign - "$root/nested/wrong.dylib"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
rm "$root/nested/wrong.dylib"
codesign --remove-signature "$root/nested/extensionless-native"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
"$repo/tools/sign-macos.sh" "$root"
# A fresh executable proves the missing-entitlement case independently of
# any metadata preserved while replacing an existing code signature.
clang "$temp/main.c" -o "$root/NoJit"
codesign --force --sign - "$root/NoJit"
mv "$root/NoJit" "$root/FruityPrime"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
printf '<plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><false/></dict></plist>' > "$temp/false.plist"
codesign --force --sign - --entitlements "$temp/false.plist" "$root/FruityPrime"
expect_failure "$repo/tools/check-macos-build.sh" "$root" "$rid"
# A compatible universal library must pass, including both signed slices.
clang -arch "$other" -dynamiclib "$temp/native.c" -o "$temp/other.dylib"
lipo -create "$root/libopenal.1.dylib" "$temp/other.dylib" -output "$temp/universal.dylib"
mv "$temp/universal.dylib" "$root/libopenal.1.dylib"
"$repo/tools/sign-macos.sh" "$root"
"$repo/tools/check-macos-build.sh" "$root" "$rid"
echo 'macOS signing gate regressions passed.'

# Reproduce the full app layout, including non-code map data. A flat publish
# can sign and launch successfully while its enclosing app cannot be signed.
fixture="$temp/package-input"
mkdir -p "$fixture/maps"
cp "$root/FruityPrime" "$root/libopenal.1.dylib" "$fixture/"
# Synthetic data keeps the packaging gate independent of controller feature branches.
printf '# Controller mapping packaging fixture\n' > "$fixture/gamecontrollerdb.txt"
printf 'Controller mapping license fixture\n' > "$fixture/gamecontrollerdb.LICENSE"
printf '{"Name":"PACKAGING TEST"}\n' > "$fixture/maps/fixture.json"
"$repo/tools/package-macos.sh" "$fixture" "$temp/dist" "$rid" 1.2.3
mkdir "$temp/unpacked"
tar -xzf "$temp/dist/FruityPrime-v1.2.3-$rid.tar.gz" -C "$temp/unpacked"
app="$temp/unpacked/Fruity Prime.app"
[[ -f "$app/Contents/Resources/maps/fixture.json" ]] || exit 1
[[ ! -e "$app/Contents/MacOS/maps" ]] || exit 1
for resource in gamecontrollerdb.txt gamecontrollerdb.LICENSE; do
    cmp "$fixture/$resource" "$app/Contents/Resources/$resource"
    [[ ! -e "$app/Contents/MacOS/$resource" ]] || exit 1
done
codesign --verify --deep --strict "$app"
printf '\n' >> "$app/Contents/Resources/gamecontrollerdb.txt"
expect_failure codesign --verify --deep --strict "$app"
cp "$fixture/gamecontrollerdb.txt" "$app/Contents/Resources/gamecontrollerdb.txt"
codesign --verify --deep --strict "$app"
printf '\n' >> "$app/Contents/Resources/maps/fixture.json"
expect_failure codesign --verify --deep --strict "$app"
dotnet run --project "$repo/tools/platformtest/platformtest.csproj" -c Release
echo 'macOS bundle resource regressions passed.'
