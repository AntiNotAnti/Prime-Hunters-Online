# Replay and Map Studio architecture acceptance

The implementation of [the requested plan](replay-map-upgrade-plan.md) is complete
in the stacked implementation branches. The changes preserve the current Studio,
network protocol and map formats while replacing replay ownership, history,
seeking and editor build/invalidation internals. Merge the PRs in dependency order;
they have not been merged automatically.

## Baselines and compatibility

- Target main: `c4cbf9b204af0c9c5d7f60183ffc48ddccb8645d` (2026-09-22).
- Reborn reference: `1a3726ee764393a295d80152b55e7a8e6ec58a49`.
- Existing target work retained: Map Studio #23, replay/killcam #26/#31,
  presentation #37, protocol rollback #38 and Android surface lifecycle #39.
- Network protocol remains 16. Optional packet 42 records authoritative replay
  world facts; it does not alter live gameplay. Unknown optional packets remain
  ignorable by older peers.
- Replay v2/v3 remain readable. New v4 recordings add explicit initial worlds,
  source origins/hidden lead-in and optional durable checkpoint indexes. World
  capsule v2 adds mutable asset/activation state and retains a v1 reader.
- Map project, package, compiler and runtime room formats remain compatible.
  No cartridge assets or asset-bearing recordings/screenshots are committed.

## Completed plan areas

| Area | Result |
| --- | --- |
| P0A timeline | Immutable accepted records, identity mapping, frozen clips, whole-segment eviction and 64 MiB bound; network baselines distinguished from full worlds |
| P0B recorder | One accepted-fact recorder, exact kill identity, semantic events, canonical private live world and periodic detached checkpoints; authority extension covers objectives, items, doors, team/Prime/match/end state |
| P0C session/services | Private readers, transport, decoder, scene/player/match/RNG/replication/audio/HUD/native resources; detached bounded world and mutable-asset restore |
| P0D/E killcams | Personal/final controllers use frozen historical scenes, owned HUD/audio, release-before-skip input, authoritative respawn/cancellation and exact final cause/identity |
| P1 playback/seeking | Studio uses the same private file/clip player, fixed 60 Hz simulation, durable and memory checkpoints, at most 120 seek steps/update, clear failure/fallback diagnostics |
| P1 presentation/export | Bounded independent pose lookahead, lifecycle/form/teleport fences, fractional camera tracks and native-size 30/60/120 FPS exports with true half-frame samples |
| P1 Studio/clips | Library/search/sort/favorites/rename/recovery, annotations/bookmarks/highlights/analytics, virtual/nested clips, camera/director, transport/controller/touch, thumbnails and offline Replay Lab retained |
| P1 cleanup | Pose ring, historical player/camera substitutions, live-projectile suppression, duplicate clip pages, old graph-reference checkpoint cache, production legacy theatre routing and network replay smoother removed |
| P2A history | O(1) document/saved state identity, bounded delta commands for common edits, transaction coalescing, atomic no-ops/failures, undo-to-save and branching |
| P2B/C viewport | Independent per-object/imported/entity/selection/overlay/navigation invalidation, stable meshes in the existing GPU renderer, shared DPI/picking/capture contract |
| P2D–G builds | Detached snapshots, two-worker/32-job single-flight scheduler, caller-only cancellation, bounded compiler cache, integrity-checked five-file content cache and one dependency analyzer |
| P2H/I editor | Existing real desktop editor and launcher flows retained; hierarchy/multiselect/duplicate/hide/lock/groups/filter/numeric transforms/overlays/diagnostics remain available, with live history/cache/build statistics |
| P3 | Replay timeline/seek/frame/alpha/killcam diagnostics and editor rebuild/history/fingerprint/build metrics, plus reproducible CPU/allocation/seek/render benchmarks |

The optional UX ideas in P2I are not a requirement to introduce a second editor.
The existing modern desktop editor is the production authoring workspace. Android
retains its existing platform editor fallback; this work does not claim to add a
full Android GPU authoring viewport.

## Runtime and deterministic acceptance

Validation ran on macOS arm64 with .NET SDK 10.0.401/runtime 10.0.12, with Windows,
Linux, macOS and Android builds in GitHub Actions. Release builds retain existing
UI obsolete-API/unreachable-code warnings. CI also checks that no game assets are
packaged and runs the repository's input/startup compatibility contracts.

| Check | Evidence |
| --- | --- |
| Timeline | 36 bounded history/freeze/identity checks |
| Replay format | 705 v2/v3/v4, metadata, CRC, bounds, recovery, extraction, authority-extension and session/socket isolation checks |
| Controls/Studio | Rate/step/pause/seek scheduling, camera interpolation/persistence/corruption, analytics/highlights, annotations and input-context checks; existing 406 controller checks |
| Network | 3,680 lifecycle assertions after removal of the obsolete replay-smoother assertion; 2,967,760 health/shot assertions |
| Editor/build/package | 88 checks covering state identity/deltas/coalescing/history bounds, fine invalidation, DPI/rays, detached snapshots, dependencies, cache corruption, package roundtrips and concurrent publication |
| GPU editor | 28 GL checks across three window sizes, Retina pointer selection, overlays/readback, native and imported geometry, mesh reuse, disposal and foreground isolation |
| Mode coverage | 12 multiplayer modes × 1,801 frames, eight actors and all seven hunters, including alt forms, afflictions, weapons, death/respawn, objectives, detached restore and continued playback |
| Final replica regression | 1,801 gameplay/presentation comparisons, 13,459 animation restores, seven full world restores, 1,650 continuation frames, 1,191 projectile frames and 61 file/clip seek comparisons; headless and OpenGL |
| Mutable assets | Room layers/materials/meshes, matrices, collision/portal activation remain private; checkpoint bytes and rendered images roundtrip; sibling disposal preserves output |
| Final killcam regression | 364 checks, 24 repeated death/skip/respawn/disconnect cycles, 134 historical-frame comparisons, resize, match/authority/slot changes, controller removal, final freeze and versioned audio handoff; headless and OpenGL |
| Foreground Studio | 620 frames against the independent private player, actor/camera changes, four seeks, pause, foreground/network sentinels and offline Replay Lab handoff |
| Durable files/clips | Cold targets between checkpoints require 0–199 simulation steps in batches ≤120; nested ranges compare 801 and 351 frames; corrupt optional checkpoint fallback/recovery passes |
| Export | Native 720p/4K HUD targets, byte-identical repeated samples at 30/60/120 FPS, distinct half-frames and unchanged gameplay hashes; a real 120 FPS H.264 output was encoded and inspected |

Gameplay hash schema 3 and a separate animation/trail/particle projection compare
reconstructed historical worlds. They do not equate predicted live-client internals
with authoritative replay state. Old expected-hash schemas remain explicit
compatibility boundaries without disabling their packet playback.

Live runs covered two, four and eight rendered clients, stock rooms and the
custom TEST ARENA, late join, round/match rotation, recording and instant clips.
The five-minute eight-client Battle run used 100 ms RTT and 2% loss: all eight
clients passed, each saved two clips, and no replay capture/presentation error was
reported. Two-client personal/final killcams were also exercised through the
existing short match-ending window. Authority/occupant changes and controller
removal are deterministic lifecycle checks; the dedicated-server architecture does
not rely on electing a new player host in normal operation.

### Android acceptance

The release arm64 APK ran on the Android 35 emulator with SwiftShader at 960×540.
Replay Studio loaded a v4 recording, played to EOF and accepted touch play/pause
and five-second seeks. Five background/resume cycles retained the replay scene.
A real UDP match against the desktop test client produced visible personal
killcams showing the historical attacker, Imperialist/headshot label and progress.
Six further HOME/resume cycles included an active killcam; the app returned to
live gameplay and subsequent killcams without crashing.

This is emulator lifecycle/control coverage, not physical-device performance
certification. SwiftShader shows the already-documented texture/launcher artifacts;
a surface teardown emitted `EGL_BAD_SURFACE` while gameplay successfully resumed.
Native Android device rendering/performance remains hardware-specific follow-up,
not evidence supplied by this emulator run.

## Performance

The [measurement report](replay-map-performance.md) includes commands, fixtures,
raw-metric definitions and limits. Selected component results:

- Cold +5-minute seek: 565.52 ms without durable indexes → 22.43 ms indexed,
  with matching gameplay hashes. Off-checkpoint reconstruction remains bounded.
- Five-minute timeline retention: 15.15 MB, below 64 MiB. Frozen 120-frame killcam
  preparation: 23.87 ms headless, 8.74 MB private retained managed world and
  1.15 MB frozen data; shared/native resources excluded.
- 1,000-object dirty checks: 3.108 ms → below 0.001 ms and zero allocation.
  One-object transform: 42.945 ms → 0.005 ms with 3,876 B allocated.
- DUST2 viewport draw: 51.603 ms CPU polygon fallback → 1.453 ms GPU including
  driver completion, excluding upload. Selection/camera/overlays reuse meshes.
- Synthetic compile miss/hit: 13.806/0.306 ms; 80×80 navigation: 10.439 ms.

These are measured component costs, not end-to-end FPS guarantees. The replay
comparison uses indexed/unindexed copies in the new engine, not an older binary.

## Reproduce and review

Asset-free checks:

```sh
dotnet build src/MphRead -c Release
dotnet run --project tools/replay-timeline-check -c Release
dotnet run --project src/MphRead -c Release -- -replayformatcheck
dotnet run --project src/MphRead -c Release -- -replaycontrolcheck
dotnet run --project tools/nettest -c Release -- --lifecycle
dotnet run --project tools/nettest -c Release -- --health-shots
dotnet run --project tools/map-editor-check -c Release
```

The complete asset-backed command inventory and current ownership contracts are
in [NETWORK-DEMOS.md](../../.claude/multiplayer/NETWORK-DEMOS.md). Map authoring/build
contracts are in [MAP-STUDIO.md](../../.claude/mapgen/MAP-STUDIO.md). Runtime fixtures
are generated from the tester's local assets and are deliberately not committed.

The PR stack starts at [#40](https://github.com/AntiNotAnti/Prime-Hunters-Online/pull/40).
Each branch targets the preceding branch, preserving reviewable increments:

| PRs | Scope |
| --- | --- |
| #40 | Timeline foundation |
| #41–44 | Editor history, invalidation, scheduler and regression checks |
| #45–47 | Session, scene and replay-service ownership |
| #48–49 | Build consumers and GPU editor viewport |
| #50–54 | World state, detached restore, modes and live capture |
| #55–57 | Killcam controller, shared Studio/clips and presentation/export |
| #58–60 | Authoritative world facts, durable seeks and mutable asset isolation |
| #61 | Profiling and diagnostics |
| Final cleanup | Remove gated legacy paths, strengthen lifecycle checks, reconcile documentation |

No protocol rollback is undone and no newer target feature is replaced by an older
reference implementation. The remaining compatibility host is contained inside
format verification; v2/v3 production playback uses the private player.
