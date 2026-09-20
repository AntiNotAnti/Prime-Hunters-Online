# macOS builds and startup

Both workflows publish on actual Macs: `macos-latest` for `osx-arm64`,
`macos-15-intel` for `osx-x64`. Each artifact must execute on its matching
runner; structural verification is not counted as a successful launch.

`MphReadAudioRid` uses the explicit RID when provided, otherwise the Mac SDK's
RID. Running an x64 SDK under Rosetta therefore gets x64 audio, matching the
process. Windows/Linux local defaults are unchanged.

The build pipeline cooks maps, publishes, sets executable mode, signs, checks
architecture and signatures, and executes `-smoketest`. The release pipeline
resolves one tag first; the Ubuntu and Mac jobs build that tag independently.
The release publish job waits for all platform packages to pass before uploading them. Tag pushes publish; a manual release run may explicitly leave the release as a draft.

## Signing and packaging

- `tools/sign-macos.sh DIRECTORY` signs every Mach-O file recursively, then
  framework containers inside out, then FruityPrime. Only the apphost gets
  `com.apple.security.cs.allow-jit`; no additional runtime entitlements are used.
- `tools/check-macos-build.sh DIRECTORY RID` checks executable mode, requires
  OpenAL, uses `lipo -verify_arch` on every Mach-O (universal files are allowed),
  prints `otool -L`, verifies signatures, and checks the JIT entitlement's value.
- `tools/package-macos.sh DIRECTORY DIST RID VERSION` preserves the publish
  native layout inside `Fruity Prime.app/Contents/MacOS`; maps go in
  `Contents/Resources/maps` because Apple's signer treats subdirectories of
  MacOS as nested code. It versions Info.plist from
  the build version, generates an ICNS from the existing project mark, signs
  components and the app, then verifies the bundle. `--deep` is verification
  only. It runs the existing asset/map guards over the staged package.
- The resulting tar.gz preserves executable permissions. The script extracts
  it again, verifies the resource seal and executes the extracted apphost.

When present in a publish, `gamecontrollerdb.txt` and `gamecontrollerdb.LICENSE`
are also moved into `Contents/Resources` before signing. They are data, not
nested code; controller feature branches must load the database from
`AppPaths.ResourceDirectory`. The packaging fixture checks both files and
proves that changing the database invalidates the app's resource seal.

These are ad-hoc signatures, not Developer ID signatures or notarization.
Gatekeeper can still require the user's approval for a downloaded archive.
See `tools/macos-README.txt`; quarantine removal is scoped to the app only.

## OpenGL startup

macOS uses an OpenGL 2.1 context with no profile hint. Its OpenGL 3.2+
contexts are core-only; the renderer's GLSL 1.20, immediate mode and fixed
function UI require legacy GL. Requesting 3.2 compatibility aborted inside
`_glfwCreateContextNSGL` before any launcher frame on Apple Silicon.
Windows/Linux retain the existing 3.2 compatibility request. A startup error
callback is installed before GLFW initialization so window-creation failures
report their native reason and return to OpenTK's managed failure check instead
of throwing through a native callback and aborting without a useful message.

GLFW initialization must set `CocoaChdirResources=false` before creating any
window or polling controllers. Its default changes cwd to the bundle Resources
directory: the extraction child still writes to Application Support, but the
launcher then reads paths.txt from the wrong directory and reports missing files.
The init hint preserves the writable working directory for settings and saves too.

## Smoke coverage and limits

`-smoketest` exits before game setup and checks configuration access, real
Avalonia headless initialization, Skia rasterization, embedded launcher assets,
map discovery, and macOS native loads/exports for OpenAL, GLFW, miniaudio,
Skia, HarfBuzz and AvaloniaNative. It also calls GLFW's version binding.
Each failure returns a nonzero exit code. No audio device or game files are
required. `run-macos-smoke.sh` runs with a fresh HOME and unrelated working
directory, with a 120-second process timeout. On a graphics-capable host it also
runs `-windowcheck`: the real launcher renders, compiles world/composite/cel/
disruption shaders, checks non-black readback and GL errors, resizes, and closes.
The packaged and re-extracted app receive the same check.

`-glfwpathcheck` is mandatory even without a GPU. In a temporary writable
fixture it creates an empty extraction directory and relative paths.txt entry,
initializes the actual GLFW/monitor path, and verifies cwd and launcher readiness
are preserved. Staged and re-extracted .app bundles run it too; no game data is used.

`check-macos-graphics.sh` queries CGL's renderer list before attempting graphics.
Exit 77 means no accelerated renderer (GLFW requires one), so rendered checks
are explicitly reported as **NOT RUN**. Native library/startup and signature
checks remain mandatory. Query errors and application failures on hosts with
an accelerated renderer still fail. Hosted virtual Macs can report only a
software renderer; a green build on those hosts leaves hardware rendering
acceptance pending.

This does not test a rendered game, an audio device, or Finder/Gatekeeper's
handling of a quarantined Internet download. Those require manual Mac checks.

## Paths and writable state

`Mods/Platform/AppPaths.cs` owns installation and desktop user-data roots.
Native libraries resolve from `AppContext.BaseDirectory`; maps resolve from
Contents/Resources in an app bundle and beside the executable otherwise. macOS
preferences, controls, paths.txt, extracted files, generated maps, saves,
thumbnails and logs use `~/Library/Application Support/Fruity Prime/`.
ConsoleSetup retains the caller's directory for explicit relative CLI inputs,
then sets cwd to user data so upstream's relative writes cannot alter the app.
Windows/Linux keep their portable behavior; Android keeps its activity overrides.

Existing portable Mac data is not moved or deleted automatically. Copy it into
the user-data directory or rerun extraction as described in the installation
note. Custom map sources still resolve from the bundle; `-mapdir` explicitly
overrides that location. The application bundle's dependencies are not moved
to Frameworks/ or Resources/, where native probing would need separate testing.

macOS disables the file-copy desktop updater: changing signed bundle resources
in place invalidates their seal. The update badge uses the existing release-page
fallback, and replacing the whole app leaves user data intact.

Every Mac startup records platform, process/runtime architecture, paths and
native library locations in `logs/platform-startup.log` under user data.
Native loader failures record their full exception and `file` architecture
description when available; audio retains its existing silent fallback.

## Background thumbnail workers

The launcher's automatic preview generation starts separate processes. Their
hidden windows use `DesktopGlContext` too: fixing only `RenderWindow` leaves
the worker requesting unsupported 3.2 compatibility on macOS, producing a crash
popup while the main app stays open. Shared settings preserve early error
logging, GL 2.1, default context flags and the writable working directory.

`-glfwpathcheck` checks the actual thumbnail settings without requiring a GPU.
On accelerated hosts, `-thumbnailwindowcheck` creates a separate hidden context
with those settings, draws a legacy primitive into an offscreen framebuffer,
and checks pixel readback and GL errors without using cartridge data. The Mac
packaging wrapper runs it against flat, staged and re-extracted packages.

Preview diagnostics must check GL 4.3 or `GL_KHR_debug` support and a nonzero
`glDebugMessageCallback` address before enabling debug output. The Mac legacy
context lacks that optional feature; calling its null pointer causes SIGSEGV,
which a managed exception handler cannot catch. Context descriptions query
flags only on GL 3.0+ and profile masks only on 3.2+ to avoid legacy GL errors.
The hidden-thumbnail test executes these diagnostics before drawing. Linux CI
also runs it on Mesa forced to GL 2.1 with KHR_debug disabled, verifying both
safe diagnostic skipping and successful rendering even without a Mac GPU runner.

Mac thumbnail batches use one background worker for the whole room list, even
when a higher job count is requested. They disable GLFW's Cocoa menu creation,
drain both redirected output streams with capped logging, and terminate workers
after five minutes or when the parent app exits. Nonzero exits/timeouts stop the batch and suppress further
worker launches for that app session; they are never retried automatically.
Concurrent callers serialize and recheck the preview cache before spawning.
`tools/thumbnail-batch-check` uses synthetic child processes to verify output
flooding, crash suppression, timeouts, process cleanup, and overlapping requests;
Mac CI also verifies the single-worker cap without requiring a GPU or game data.
