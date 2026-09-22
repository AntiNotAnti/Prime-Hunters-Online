# Replay and Map Studio architecture migration

## Baselines

- Target main: `c4cbf9b204af0c9c5d7f60183ffc48ddccb8645d` (2026-09-22).
- Reborn reference: `1a3726ee764393a295d80152b55e7a8e6ec58a49`.
- Recent target work reviewed: #23 (Map Studio), #26/#31 (replay/killcam),
  #37 (presentation), #38 (protocol rollback), #39 (Android surface lifecycle).
- Protocol remains 16; replay v2/v3 and map formats are unchanged.

## Timeline foundation

`ReplayTimeline/` is independent of UI, renderer, sockets and file formats. Facts
copy their payloads; restore points and frozen clips expose read-only collections.
The simulation thread owns the recorder and mutable timeline. Freeze creates a
stable view for consumers on other threads.

The default retention target is 45 seconds, with the immediately preceding
baseline retained for warmup. The 64 MiB budget counts payload and conservative
record overhead, including empty markers. Byte eviction removes complete
segments. If even the current segment cannot fit, it is invalidated and capture
waits for a new baseline: missing facts never masquerade as a complete clip.

Accepted match, roster and filtered snapshot facts feed a single recorder. Kill
markers fence match, authority epoch, server tick, damage event, both occupants
and victim life. An attacker generation of zero means attribution was unavailable;
consumers must not substitute the current occupant of that slot.

**Network baselines are not scene checkpoints.** Current snapshots carry roster,
player/RNG/clock/health state, but do not contain all historical projectiles,
objectives, animation and effects. They are tagged `NetworkBaseline`. No passive
killcam may treat one as `ReplicaCheckpoint`.

The existing replay, clip and killcam paths remain in service during migration.
No fallback is removed before the plan's runtime acceptance gate.

## Remaining replay work

- Complete detached scene restore schema and authoritative world/event capture.
- Complete historical presentation/event state and scene service coverage for all modes;
  replica simulation, replication, silent audio and GL resource ownership are implemented.
- Detached checkpoint seek and consumer attachment for instance-owned passive playback.
- Replay-based personal/final killcams and audio/input/HUD ownership.
- Migrate full playback, instant clips, Studio, thumbnails and video export.
- Desktop/Android runtime stress and determinism/performance acceptance.

## Existing editor integration

`StartScreen.OpenMapStudio` already launches the real editor on the desktop shell,
including create/import/recovery/build/validate/playtest/package. Preserve this
implementation instead of replacing it with another editor. Android currently
has a platform fallback; it is not a renderer-backed editor viewport.

## Validation

- `dotnet run --project tools/replay-timeline-check -c Release`
- `dotnet run --project src/MphRead -c Release -- -replayformatcheck`
- `dotnet run --project tools/nettest -c Release -- --lifecycle`

The initial target failed compilation because ReplayFormatCheck still referenced
SnapshotWire, removed by #38. Its fixture now uses protocol 16's actual snapshot
layout, without restoring the reverted wire protocol.

## Editor history migration

Dirty tracking compares `DocumentStateId` with `SavedStateId` in O(1). Undo entries
carry their before/after identities, so branching, pruning and undo-to-save do not
confuse stack position with document state. History is capped at 500 commands and
256 MiB of conservative estimated retention, including redo entries.

Viewport transforms store numeric transforms only. Create/delete/duplicate and
object/property edits retain only affected objects; material edits retain one
material. Explicit transaction keys coalesce continuous transform/property edits,
with save boundaries preventing coalescing across the saved identity. Dragging
still commits once on release. Whole-project replacement remains a bounded
fallback for bulk environment/import/upgrade/recovery operations.

`dotnet run --project tools/map-editor-check -c Release` exercises save identity,
branching, undo/redo, polymorphic objects, coalescing, locked/no-op transforms,
failed edit atomicity and history bounds.

## Viewport invalidation migration

The viewport subscribes to typed invalidations instead of rebuilding on every
`Changed` notification. Native faces are cached by object identity; affected
objects alone are recompiled. Imported architecture has a separate cache and is
cleared only when import inputs change. Entity representations, selection,
overlays and navigation carry independent counters. Save and camera navigation
leave geometry caches alone. Stable CPU meshes now feed the existing game renderer
through `MapRenderFrame`. Per-object GPU meshes survive camera, selection, overlay
and layout changes. World-ray picking and captures share the camera/layout contract;
Avalonia overlays composite above GPU geometry. Readback occurs only for explicit
preview capture. Headless screenshots and renderer failure retain the CPU fallback.

## Runtime map build migration

`MapBuildSnapshot` detaches editor graphs before work is queued. The runtime build
scheduler runs off the UI thread, deduplicates identical in-flight fingerprints,
limits active workers to two and pending unique jobs to 32, and treats cancellation
as cancellation of one caller's wait. Shared work completes and can satisfy other
callers or populate the cache. Failures are structured diagnostics.

Fingerprints use the in-memory recipe and dependency content, including bundles,
Q3 sources, texture packs, collision, custom assets/audio and borrowed base-room
model/texture inputs. Existing runtime manifests use the same analyzer. The cache
stores all five binaries plus integrity hashes and diagnostics at
`ProjectPrime/map-cache/<fingerprint>`. Publication is staged and guarded across
processes. Corrupt/incomplete entries are rebuilt. External dependency changes
during a job reject publication; cached installs also verify input identity and
output integrity. Import/base-content reader access is serialized.

Map Studio Build, Playtest, validation, navigation and packaging use this scheduler,
as do the CLI, package installer and synchronous client/server room preparation.
Different consumers reuse a private compiler cache capped at eight entries and
128 MiB of estimated retention; returned geometry is immutable and navigation is
copied for each consumer. Fingerprints, packages, package reference checks and
Save As share the analyzer's portable asset set. Independent scheduler owners wait
for file publication; installation is also serialized. The cache is per-user and
is not part of release packaging. The raw collision diagnostic deliberately reads
geometry outside the publishing validation gate.

## Scope and acceptance status

The initiative is **not complete**. The full requested plan is preserved in
[replay-map-upgrade-plan.md](replay-map-upgrade-plan.md). This table separates
shipped foundations from unfinished architecture; passing builds are not evidence
that the missing replay features work.

| Plan area | Status |
| --- | --- |
| P0A timeline | Implemented bounded immutable records/segments, freeze and identity mapping; complete scene restore schema still missing |
| P0B recorder | Accepted match/roster/player snapshots and existing semantic events integrated; full world/objective/presentation event capture missing |
| P0C isolated session/services | Instance reader/transport/hosts and passive decoder implemented; scene-owned players, match state, RNG, camera sequences and pools extracted; private replication, silent audio, fixed stepping and GL rendering implemented; complete world checkpoints remain |
| P0D/P0E personal/final killcams | Existing implementation retained; replay-scene replacement and runtime acceptance outstanding |
| P1 playback/seek/interpolation | Studio facades now delegate reader/clock/transport to a session; seeks schedule at most 120 steps; isolated scenes/checkpoints/interpolation migration outstanding |
| P1 shared clips/highlights/export | Existing functionality retained; shared-timeline migration outstanding |
| P2A history | State IDs, bounded delta commands and transaction coalescing implemented for common actions |
| P2B viewport | Per-object native mesh and independent imported/entity/selection invalidation implemented |
| P2C renderer viewport | Existing game renderer consumes stable meshes; DPI/picking/capture contract, overlay composition and viewport resource lifetime verified |
| P2D snapshots | Detached graph snapshots implemented |
| P2E/F scheduler/cache | Runtime Build/Playtest, validation, navigation, packaging, installer and synchronous room preparation share a bounded queue, compiler cache and integrity-checked runtime cache |
| P2G dependencies | Content analyzer and portable asset set shared by build/cache, packaging, package reader and Save As |
| P2H hub | Desktop already opens the real editor on target main; preserved |
| P3 diagnostics/profiling | Timeline/cache/history counters, automated checks and editor microbenchmark added; gameplay/runtime profiling outstanding |
| Cleanup | No replay fallback or feature removed; removal remains gated on replacement acceptance |

### Local validation (macOS arm64, .NET 10.0.401)

- Desktop Release build: passes with 18 pre-existing warnings.
- Timeline: 36 checks.
- Replay v2/v3 format, metadata, recovery, extraction and malformed input, passive session/scene ownership: 556 checks.
- Network lifecycle: 3,681 assertions.
- Health/shot behavior: 2,967,760 assertions.
- Editor/history/cache/build: 88 checks, including projection/ray agreement across DPI scales, real synthetic-texture compilation,
  package roundtrip, five-file output, dependency changes, cache corruption,
  deterministic packages, cancellation, mixed queue bounds, shared compilation,
  independent scheduler publication and two-worker concurrency.
- Map Studio rendered successfully at 1440×900 and 960×600.
- Renderer viewport: 28 GL integration checks including three window sizes, Retina
  pointer selection, overlays, preview readback, imported collision meshes, resource
  cleanup and foreground ownership. Native and DUST2 captures inspected visually.
- A fresh 1,800-frame protocol-16 recording passes linear/reset-forward gameplay
  hashes, sampled scalar state, randomized seeks, all playback rates and frozen EOF.
- Replay controls/camera/Studio checks and 406 controller checks pass.
- Two rendered network clients completed 30 seconds and recorded replays. The
  harness reported missing damage coverage (neither actor took a hit on Sanctorus),
  so this run is not combat acceptance. The harness now uses the game's macOS GL setup.
- Live-match replay/killcam visual correctness and Android gameplay/pause-resume
  acceptance have not been tested for the proposed replacement (it is not implemented).

### Reproducible editor microbenchmark

Run `dotnet run --project tools/map-editor-check -c Release -- --benchmark`.
The fixture uses 1,000 native objects. Its legacy path reproduces target-main's
serialized dirty check and clone/compare/replace transform algorithm. The current
path measures document commands without attaching the UI/viewport. These numbers
are not end-to-end frame times or a promise of the same speedup on every machine.

| Operation | Legacy ms/op | Current ms/op | Legacy allocated/op | Current allocated/op |
| --- | ---: | ---: | ---: | ---: |
| Dirty check (100 iterations) | 3.110 | below 0.001 | 1,456,223 B | 0 B |
| One-object transform (20 iterations) | 38.214 | 0.004 | 11,780,501 B | 3,876 B |

The benchmark exposed full-map wrapper allocation in the initial delta path.
Direct identity lookup removed it; geometry vertices are never copied by numeric
transform commands. Timings vary with JIT, GC and machine load.

The renderer microbenchmark (`-mapviewportcheck DIR -mapproject FILE`) used DUST2
with 11,056 render faces and 1,902 collision faces on macOS arm64. Average GPU draw
including driver completion was 1.453 ms over 20 draws; the CPU fallback's Avalonia
polygon raster averaged 51.603 ms over five renders. Upload is excluded. These are
component costs rather than end-to-end editor frame times. Selection, camera and
collision changes reused all GPU meshes; moving one native object uploaded one mesh.

## Playback session ownership

`ReplayPlaybackSession` owns the file cursor, clock, errors and `ReplayTransport`.
`DemoPlayback` and `ReplayController` are foreground compatibility facades.
Only `TheatreReplaySessionHost` can bridge the existing socket-free NetSession
pipeline and Studio presentation singletons. `PassiveReplaySessionHost` decodes
packet-visible values with its own roster, lifecycle trackers, player states,
intents and clocks; it ignores connection-control traffic. Opening, advancing,
seeking and disposing passive sessions is tested against foreground identity,
RNG and transport sentinels. `PassiveReplayScene` loads and renders a private world; two interleaved 1,801-frame
passes (357 frames with projectiles) agree and preserve foreground sentinels.
Rendered images remain byte-identical after sibling disposal. Detached checkpoints
and complete historical objective/effect state are still missing.
