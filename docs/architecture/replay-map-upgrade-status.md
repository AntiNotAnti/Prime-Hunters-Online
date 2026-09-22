# Replay and Map Studio architecture migration

## Baselines

- Target main: `c4cbf9b204af0c9c5d7f60183ffc48ddccb8645d` (2026-09-22).
- Reborn reference: `1a3726ee764393a295d80152b55e7a8e6ec58a49`.
- Recent target work reviewed: #23 (Map Studio), #26/#31 (replay/killcam),
  #37 (presentation), #38 (protocol rollback), #39 (Android surface lifecycle).
- Protocol remains 16. Replay v2/v3 remain readable; v4 adds an explicit world envelope. Map formats are unchanged.

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

Accepted match, configuration, roster, filtered snapshot and remote intent facts,
plus submitted local input for presentation, feed a single recorder. Baselines
retain held input only for their occupant generation and life. Kill
markers fence match, authority epoch, server tick, damage event, both occupants
and victim life. An attacker generation of zero means attribution was unavailable;
consumers must not substitute the current occupant of that slot.

**Network baselines are not scene checkpoints.** Current snapshots carry roster,
player/RNG/clock/health state, but do not contain all historical projectiles,
objectives, animation and effects. They are tagged `NetworkBaseline`. No passive
killcam may treat one as `ReplicaCheckpoint`.

Studio, full client/server recording, clips and killcams use the private timeline pipeline.
The legacy theatre host and pose killcam remain diagnostic/fallback adapters during migration.
No fallback is removed before the plan's runtime acceptance gate.

## Remaining replay work

- Complete authoritative world/event and presentation coverage. Live accepted facts
  now drive a canonical private world and detached timeline checkpoints; live combat
  acceptance and consumer attachment remain.
- Complete historical presentation/event state and scene service coverage for all modes;
  replica simulation, replication, silent audio and GL resource ownership are implemented.
- Complete presentation interpolation, export and remaining stress acceptance.
- Complete killcam stress acceptance on larger matches and Android; private-scene controllers and audio/input/HUD ownership are implemented.
- Verify all rendered Studio, thumbnail and video export workflows on the shared player.
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
| P0A timeline | Bounded immutable records/segments, freeze and identity mapping; detached world restore and live canonical checkpoint production implemented |
| P0B recorder | Accepted match/configuration/roster/player snapshots, remote intents, submitted local input and existing semantic events integrated; full world/objective/presentation event capture missing |
| P0C isolated session/services | Instance reader/transport/hosts, private replication, silent audio, fixed stepping, GL rendering and detached world checkpoints implemented; broader mode/combat acceptance remains |
| P0D/P0E personal/final killcams | Instance controller and private replay presentation implemented; two-client personal/final combat checked with latency/loss; larger matches and Android runtime acceptance remain |
| P1 playback/seek/interpolation | Studio facades delegate reader/clock/transport to a session; passive file/frozen-clip player uses detached checkpoints and at most 120 seek steps; foreground Studio, bounded accepted-snapshot presentation and deterministic export implemented; broader runtime/platform acceptance remains |
| P1 shared clips/highlights/export | Client/server full recordings and instant clips share accepted facts; v4 durable initial worlds and exact nested/legacy ranges implemented; rendered export, native offscreen targets and interpolated 120 FPS verified |
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
- Replay v2/v3 format, metadata, recovery, extraction and malformed input, passive session/scene ownership and projections: 586 checks.
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
Gameplay hash schema 3 covers projectiles, bombs, pickups and gameplay RNG as well
as players/objectives. Separate per-frame presentation hashes cover animation,
trails and effect particles. All 1,801 frames agree in headless and OpenGL passes;
linear/reconstructed playback, randomized seeks, rates and EOF also pass schema 3.
Rendered images remain byte-identical after sibling disposal. Live authoritative
world/objective capture and consumer migration remain outstanding.

The detached decoder component now roundtrips all 1,801 recorded frames, including
ordering and lifecycle tombstones. The same interleaved run performs 13,568 exact
model-animation restores after deliberately changing animation frames and active
flags, in headless and OpenGL modes. Its payloads contain values/asset identities,
with no live scene/model/GPU references. Invalid decoder payloads fail atomically.
The subsequent world assembly also passes seven detached restores (including an
in-flight projectile), 1,500 continuation frames and 61 file/frozen-clip seek
comparisons. Seven immediate restored OpenGL images match the linear source byte
for byte. The clip starts at frame 1,150 after 250 warmup steps split across host
updates, ends at 1,200, supports a backward seek and survives source timeline reset.
EOF remains frozen. These fixtures do not establish all-mode or live combat acceptance.

`-replayworldcheck FILE [-output DIR]` generates synthetic eight-actor recordings
in the source file's room for all 12 multiplayer modes. It exercises all seven
hunters, weapon and alt-form transitions, freeze/disruption/burn, death and respawn.
All 21,612 frames pass the interleaved gameplay/presentation, detached restore,
continued simulation and file/frozen-clip seek checks. This exposed non-unique
effect-element names: checkpoint resource binding now uses the asset's element
ordinal and verifies its name. Timed objective modes apply their time goal before
room construction; recorded match clocks advance between accepted updates and
preserve unlimited/ending semantics. Synthetic coverage is not live combat acceptance.
The eight-actor Battle fixture also passes the rendered check, including seven
byte-identical checkpoint images and unchanged output after sibling disposal.

## Live canonical world

`ReplayLiveWorld` subscribes to accepted recorder facts and advances a private
replica once per live simulation frame. Its state is reconstructed from those
facts, never copied from mutable live entities. It writes a complete checkpoint
every 300 frames and markers for quiet frames. The production timeline therefore
contains `ReplicaCheckpoint` segments rather than advertising network baselines
as worlds. Initialization cannot recover projectiles predating the first accepted
facts, so history begins with this recorder's reconstruction.

Pending facts are bounded to 8,192 records/4 MiB. Failure invalidates the timeline
and disables capture until reset; it does not terminate the match. Scene resources
are created, advanced and disposed on the simulation/GL owner. A match reset clears
pending facts and schedules private-world disposal. Scene shutdown releases it.
The shared retention target remains 45 seconds and grows for the existing 60/120
second clip preferences, with the same 64 MiB hard cap.

`-replaylivecheck FILE` feeds accepted protocol facts through the production owner,
freezes a world clip, resets the source match and compares every visible replay
frame with the captured world. The eight-actor fixture passes 1,801 source frames,
seven checkpoints, 251 clip frames and backward seeking, preserving foreground
sentinels. It retained 7,594,747 timeline bytes; the sampled last capture was 9.05 ms
and its replica step 0.394 ms on this machine. These are component timings.

## Detached world checkpoints and passive seeking

`ReplayWorldSchemas` is an explicit field contract rather than a traversal of an
arbitrary live object graph. The capsule contains decoder state, the construction
baseline, entity/pool membership and links, model animation, effects/particles,
queued messages, replication history, continuous-fire phase, clocks and RNG.
Resource assets are identified by keys; native binding names, sockets, hardware
input and delegates are excluded. Two-pass object binding reconnects local links.
Room content, construction baseline and contract fingerprint must agree. Unsupported
objects fail capture instead of being silently omitted. Restore only targets an
unpublished replica, whose caller disposes it on failure.

Each capsule is bounded to 8 MiB and 32,768 graph objects. `PassiveReplayPlayer`
retains at most 128 capsules/64 MiB, captures every 300 frames, selects a preceding
checkpoint, replaces the scene after successful restore and advances at most 120
steps per update. Frozen timeline clips must carry `ReplicaCheckpoint`; network
baselines are rejected. The file and clip paths share the same replica stepping.

In the local 1,801-frame fixture, capsules occupied about 480 KiB (decimal bytes:
490,985–491,613). Seven cached capsules retained 3,437,580 bytes including cache
overhead. Warm headless capture took approximately 10–14 ms; fresh scene creation
plus restore took 22–33 ms in this run. Seeking to frame 1,799 from checkpoint
1,500 required 299 steps over three updates. These are component measurements,
not a claim about live killcam startup or Android frame time.

## Replay killcam presentation

`KillcamController` consumes immutable frozen world clips. The live scene keeps
receiving snapshots and simulating while a private scene owns the picture. Kill
identity fences match/epoch, occupant generation and victim life; respawn, slot
reuse, disconnect and match changes cancel presentation. A queued Android/back
skip is consumed on the scene owner. Shoot must be released before it can skip,
and input is cleared before intent serialization.

Personal playback uses two seconds of pre-roll and a quarter-second terminal
hold to fit the existing three-second respawn. Final playback uses up to five
seconds at 2× within the existing three-second GameOver window. Causal score/
survival kills must be within two seconds; timed endings accept a kill within
eight seconds. No stale kill is substituted. The temporary developer fallback
remains until the full acceptance matrix passes.

The camera follows a generation-checked historical attacker (victim fallback)
with collision clipping. The private scene owns its banner font and render
interpolation fraction. Versioned audio leases rebind the listener and prevent a
stale release from stopping a newer presentation/device. Accepted damage events
now drive private flinch/death presentation without resolving combat again.

Validation: `-replaykillcamcheck FILE [-shots DIR]` passes 358 checks including
24 death/skip/respawn/disconnect cycles, 134 historical frame comparisons, resize,
match reset, final eligibility, frozen source reset and audio handoff. Rendered
checks pass. All 12 eight-actor mode fixtures pass 21,612 frames after the damage
presentation change, and all 586 format checks pass. Two real clients in TEST
ARENA showed personal and final replay scenes under 100 ms round-trip latency
and 2% packet loss; HUD captures were inspected. One retry was needed after GLFW
crashed querying a missing macOS monitor before either client loaded a scene.
The network harness now includes fatal health drops in its damage counter.

## Shared disk sinks and foreground Studio

Normal playback now uses `PassiveReplayPlayer`; `DemoPlayback` is a presentation
facade. The root window scene routes drawing, input and resize to the ready private
scene. Readers, seek reconstruction and historical entities remain separate from
live `NetSession`. Watching another slot keeps the replica simulation perspective
fixed, so camera changes cannot alter RNG or weapon effects. Versioned audio leases
mute seek/paused worlds and follow scene replacement. Replay Lab explicitly detaches
an alive historical actor into an offline practice scene, refusing a live connection.

Client and dedicated-server recording subscribe to `ReplayRecorder.Accepted`.
Instant clips select the same immutable timeline and prepare an exact initial
world with bounded warmup. Disk serialization then runs on a worker; completion and
GL cleanup run on the owner. Pending post-roll is truncated and preserved on reset.
The old independent packet ring has been removed.

`.ppdemo` v4 retains v3 CRC chunks, indexing and atomic `.part` publication, with
bounded initial-world bytes, source-frame origin and hidden lead-in in its header.
A fixed codec revision and field contract replace commit-SHA checkpoint rejection.
File-only semantic records preserve exact kill identities without changing protocol
16. Frame markers preserve quiet EOF. Extracted ranges retain their source world
and required facts, including nested ranges. V2 uses its original construction
preflight decoder; short clips cannot accidentally choose a different roster.

Validation: 591 format checks pass. The live-capture check passes 1,801 accepted
frames, seven checkpoints, 251 frozen comparisons, durable v4 and nested clips,
and v2/v3 source-range fidelity. Saved live clips and v3 recordings pass every
frame's gameplay/presentation projection, 14 cold/cached seeks, all five rates,
frozen EOF and injected-divergence detection. A real v4 recording passes 620
rendered Studio frames, camera/player changes, four seeks and pause with foreground
and network sentinels unchanged. Replay Lab's offline handoff also passes.
Two real clients saved clips during combat without capture/playback errors; one
missed a harness shot-count threshold, so that run is clip validation, not a full
network acceptance pass. Larger-match and server recording checks continue.

The four-client 90-second latency/loss run completed 22 killcam starts and 2,761
visible frames, with clips from every client and no capture/playback errors.
Dedicated-server recording survived rotation; its first v4 file passes all 2,843
frames, rates, seeks and EOF checks. The general network harness flagged shot-count
coverage and transient position gaps in that run; those are still under investigation
and are not counted as a full network acceptance pass. All 12 synthetic multiplayer
fixtures also pass again after the Studio and disk-sink migration (21,612 frames).

## Recorded presentation and export

`ReplayPoseStream` uses an independent bounded cursor with six frames of lookahead
and at most 24 accepted poses per slot. It samples source-frame timestamps, never
receive wall time, and cannot advance the simulation or RNG. Slot/life, spawn,
death, form and teleport discontinuities prevent blending. File and frozen-clip
views use the same sampler; a malformed/oversized presentation cursor falls back
to deterministic entity history. Body, chase, killcam and first-person cameras use
the presentation pose. Entity rotation interpolation uses quaternion slerp.

Video jobs render a deterministic sample sequence: alternate ticks at 30 FPS,
every tick at 60, and exact half-frame/end-frame pairs at 120. Camera tracks accept
fractional frames, orbit uses recorded time, and track collision anchors are
recorded keyframes. Export skips wall-time camera damping. Gameplay remains 60 Hz.
Version-2 export manifests tell FFmpeg the actual sample frequency.

A scene-owned composite framebuffer provides native output dimensions, including
HUD, independently of the window size and gameplay resolution-scale preference.
A scaled blit supplies the window preview. The replay transport/export-status
banner is excluded from movies. Scene/GL cleanup releases the export target.

`-replayexportcheck FILE -output DIR` passes repeated byte-identical 30/60/120 FPS
exports with different wall-clock cadence, distinct half-frame images, unchanged
gameplay hashes and native 720p/4K HUD dimensions. Both an eight-actor fixture and
a real dedicated-server v4 recording pass; FFmpeg produced and decoded a real
1280×720, 120 FPS movie. Native 4K HUD images were inspected. Rendered replica tests
still pass all 1,801 gameplay/presentation frames, seven restored images and 61
file/clip seeks; controller checks include fractional tracks and discontinuities.

## Authoritative world and semantic coverage

Protocol 16's existing gameplay layouts are unchanged. Optional packet 42 carries
schema-2 replay-only world values (schema 1 remains readable) at 10 Hz and phase transitions. Old peers ignore
it; it never mutates a live client scene. Full states cover Prime identity,
flag/carrier/drop state, node ownership/progress/occupants, all item spawners,
dropped items and door/portal/collision activation. Actor links fence generation
and life. Fragment assembly is limited to one 48 KiB state and 512 entries per
entity category, validates before publication and heals loss with the next state.
Only complete states enter the common recorder. The private decoder checkpoint
has an explicit version 2; version 1 remains readable.

Accepted counters and damage produce Headshot, FlagCapture, NodeCapture,
PrimeChanged and MatchPoint events. They feed Studio markers, analytics and
highlights. Overtime has an explicit event value but no producer: the current
game has no overtime rule. Match-ending facts retain a confirmed causal kill or
identify time/objective/other endings; new-server final killcams require that
cause. A bounded legacy-server fallback remains for protocol-16 servers that do
not send the optional world extension.

Validation: 705 format/decoder/network-fragment checks; four 1,801-frame objective
fixtures (602 flag and 1,204 node facts) with linear/world/checkpoint/file/clip
comparisons; rendered Capture world restores and seek comparisons; an actual
9,038-frame server recording with all playback rates and randomized seeks. An
8-client 100-ms RTT/2%-loss five-minute run completed 32 killcam starts and 3,516
visible replay frames without capture/playback errors; all eight clients saved
clips. The general harness's position failures were traced to comparing the old
fading room against the next match's snapshots before its load barrier. The
measurement now observes the same GameplayReady fence as state application.

## Durable seek checkpoints

Version 4 recordings now append bounded compressed world checkpoints with an indexed
footer (up to 4,096 entries / 256 MiB). Metadata scans read only the index. Playback
loads the nearest checkpoint on demand and simulates at most 120 frames per host
update. File and in-memory checkpoints share the same detached restore path; a bad
optional checkpoint falls back to the earlier baseline. Recovery omits corrupted
optional checkpoints. Range extraction preserves source clocks and nested ranges.

`-replaydurablecheck SOURCE -output DIR` checks cold/backward seeks, nested ranges,
CRC rejection and recovery. The 1,801-frame Nodes fixture passes with six durable
checkpoints; cold seeks reconstruct 0–199 frames. An older v4 recording with the
version-1 decoder passes all 2,843 gameplay/presentation frames, randomized seeks
and playback rates after decoder-baseline normalization.

The five-minute eight-client Battle stress run (100 ms RTT, 2% loss, all seven
hunters) passes on all eight clients. Each client saved a shared-timeline clip;
killcam capture/playback reported no errors. Full source recordings and logs are
local test artifacts, never release assets.
