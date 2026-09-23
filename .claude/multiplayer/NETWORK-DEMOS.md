# Replay system

The [architecture acceptance report](../../docs/architecture/replay-map-upgrade-status.md)
and [measurements](../../docs/architecture/replay-map-performance.md) describe the
completed migration. Older packet-stream design notes are retained separately in
[NETWORK-DEMOS-HISTORY.md](NETWORK-DEMOS-HISTORY.md); those are historical.

## Ownership and accepted facts

`ReplayCapture` feeds one `ReplayRecorder` with accepted match/configuration,
roster, lifecycle-filtered snapshots, remote intents, local submitted input and
semantic events. Local input is presentation evidence, never hit authority.
`ReplayTimeline` is independent of UI, renderer, sockets and files. Values own
their payloads; frozen clips remain valid after reset. History retains at least
45 seconds, grows to cover configured clips/post-roll, and is capped at 64 MiB.
Eviction removes whole restore segments. A missing fact invalidates continuation.

`ReplayLiveWorld` consumes those accepted facts in a private canonical replica,
steps on the scene owner and captures world checkpoints every 300 frames. Pending
facts are bounded to 8,192 records/4 MiB. Failure invalidates capture until reset;
it cannot invent projectiles predating the initial capture boundary. Quiet frames
advance availability. Network baselines are not complete world checkpoints.

`PassiveReplayScene` owns its player registry, match state, RNG, camera sequences,
projectile pools, replication/lifecycle/order histories, HUD messages and silent
sound routing. It never polls live input, opens a socket, resolves authoritative
damage or rebinds foreground facades. Replica nodes, materials, meshes, matrices,
portals and room collision activation belong to that scene. Immutable asset
geometry/animation definitions may be shared; native resources have explicit
owner lifetimes. Disposing a sibling cannot change the foreground or another replay.

`DemoPlayback` and `ReplayController` bridge foreground Studio presentation and
transport to `PassiveReplayPlayer`. Both files and frozen clips use that player.
The only legacy network-host adapter is private to `ReplayFormatCheck`, where it
checks old packet compatibility. There is no production theatre fallback, pose
killcam ring, live historical draw substitution or replay sampler in `NetSmoothing`.

## Historical world and checkpoints

`ReplayWorldCheckpoint` is a bounded explicit value contract (8 MiB/32,768 graph
objects), with decoder/order/lifecycle/input-age state, entity membership/pool
order, links, queues, animation, effects/particles, clocks and RNG. World version 2
also preserves mutable model/material/node state and room/portal activation;
version 1 remains readable. Payloads use asset identities and construction anchors,
never live references or GPU handles. Restore targets an unpublished replica with
matching room content and construction baseline, rebinds its own resources and
publishes only after validation. Unknown contracts fail closed.

Optional protocol-16 packet 42 supplies replay-only authoritative world facts:
flags and carriers, capture nodes/progress/occupants, pickup spawners, stable-ID
dropped items, doors/collision, team scores, Prime actor, match phase/clock and
ending cause. The bounded extension uses atomic fragment assembly; live clients
record it without applying it to gameplay. Legacy recordings still reconstruct
from the evidence their original format actually contains. Semantic events include
headshots, flag/node captures, Prime changes and match point; no overtime event is
fabricated for modes that have no overtime rule.

The player caches at most 128 checkpoints/64 MiB and chooses the nearest usable
in-memory or durable file checkpoint. Every host update performs at most 120 seek
steps; audio and presentation stay suppressed until ready. Invalid optional file
checkpoints are rejected and reconstruction uses a valid earlier source. The
original recording is never modified. New files retain at most 4,096 durable
checkpoints/256 MiB compressed, spooled to a private temporary file during capture.
Seek diagnostics show source, restore frame, work, elapsed time and rejected entries.

## Killcams

`KillCam` reads live lifecycle and routes boundary input/presentation to an
instance `KillcamController`. Personal replays freeze 120 pre-death frames and
hold EOF for at most 15 ticks. Authoritative respawn, occupant/life change,
disconnect, disable, match/epoch change and skip end them immediately. Gameplay
continues behind the private scene. Held fire must be released before a new press
can skip; the transition clears input so it cannot fire into the live match.
Android callbacks enqueue skip requests; only the scene owner disposes GL/audio.

Kill identity includes match, authority epoch, server tick, damage event, killer
and victim generations, and victim life. Final candidates freeze up to 300 frames,
play at 2x and fit the existing match-ending window. New authorities identify the
exact ending cause/kill. Older servers use a bounded causal/timed fallback; a stale
unrelated kill cannot become the final replay. Missing history simply skips replay.
The replay owns its HUD and versioned audio lease. Every projectile, effect and
attacker animation comes from that historical scene, with no live draw substitutions.

## Controls and clock

`ReplayController` schedules complete 1/60-second engine steps. Rates are
0.25, 0.5, 1, 2 and 4; neither physics constants nor recorded frame numbers are
scaled. Paused and ended replays run camera/input presentation only. Particle
and fade timers are stepped for every replay simulation frame, including seek
batches. The final frame remains visible and restart/exit stay available.

The replay packet clock also advances while the session is waiting for a match
to start, so a recorded `InMatch` packet can release the load barrier. Gameplay
remains frozen during these steps; this needs no live socket or load acknowledgement.

Replay transport keyboard and controller bindings are configurable under
Settings -> Replays. Defaults are Space play/pause, period/comma step, brackets
speed, arrows seek five seconds and Home restart; controller defaults are
A play/pause, X step, D-pad seek/speed, LB/RB players and Y camera mode.
Replay controller actions are a separate semantic context from gameplay, so
shared physical buttons do not conflict or suppress live-match input. Android
also exposes PLAY/PAUSE and five-second seek controls directly on the touch HUD.
F toggles free camera, C selects chase, O selects orbit, 1-8 selects a player,
and mouse buttons cycle players. The replay HUD advertises the active input
source's configured transport controls. The replay pause menu opens Replay
Studio for timeline/editor/camera/export work. B/N save/preview camera keyframes. Up to 64
frame-indexed keys persist in a checksummed `.ppdemo.camera` sidecar, atomically
replaced and bound to the replay's size and modification time. Track v2 stores
linear/smooth/Catmull-Rom spline interpolation, ease-in/ease-out/ease-in-out,
roll, optional constant-speed arc-length remapping and look-at targets.
Orientation uses quaternion interpolation. Presentation mode can collision-test
the interpolated free-camera path before applying a key. Keys survive
seek/restart and are drawn on the Replay Studio timeline. The shared
desktop/Android menu can save, preview or remove a key at the current frame,
adjust FOV/roll/interpolation/easing/look-at, and explicitly enable track
playback.

Faithful is the default camera profile; Presentation optionally smooths the
chase camera and enables authored tracks. Neither profile changes packets,
entity state, simulation timing or network smoothing. The director scores
recent kills, objectives, damage exchanges, proximity, low-health pressure and
late-match action, then applies a minimum shot hold and switch margin so focus
does not thrash between players.

The replay HUD shows time, duration, rate, watched player/camera, event marks,
and stopped/error state; it dims after inactivity. Presentation is suppressed
and audio is muted/stopped during fast-forward seek batches.

## Presentation and export

`ReplayPoseStream` has its own accepted-snapshot cursor with six-frame lookahead,
12-frame history and at most 24 poses per slot. It never reads live arrival jitter
or advances simulation/RNG. Body/camera interpolation fences occupant/life,
spawn/death, form changes and teleports. Watching another actor changes only the
view; replica stepping fixes its simulation perspective to keep RNG deterministic.
Camera tracks sample fractional recorded frames.

Export walks 60 Hz simulation and produces genuine 30/60/120 FPS images; 120 FPS
uses half-frame presentation samples. Native world/HUD targets support 720p, 1080p,
1440p and 4K independently of the preview window. Jobs preserve image sequences and
an exact encoding command; available FFmpeg encodes H.264 MP4. Transport/export
status overlays stay out of movies. The exporter remains video-only, with no
new deterministic audio track. Replay Lab explicitly detaches a selected actor
into offline practice and refuses takeover while a live connection exists.

## Storage, clips and library

New full client/server recordings and instant clips use `.ppdemo` v4. V2/v3 remain
readable. V4 extends the v3 CRC/index envelope with an exact initial world, source
origin, hidden warmup and an optional durable checkpoint index. Metadata retains
room/mode/content hash, protocol/build, rules, roster, UTC date and replay type.
Protocol mismatch is refused before decoding; identified room content must match.
Map and package formats are unchanged.

Chunks are independently compressed and bounded. Readers validate order, extents,
lengths, CRCs, counts and decompression before accepting data. Metadata inspection
alone does not mark a replay healthy: that requires a complete integrity scan.
Malformed/corrupt/truncated input has an explicit result. A legacy v2 stream cut
exactly at a record boundary can lack enough evidence to detect truncation.

Recordings flush complete chunks to `.ppdemo.part`; successful close writes the
footer and atomically publishes without replacing another file. I/O failure stops
recording without ending the match. Recovery writes a separate file and preserves
the source, retaining valid chunks and optional valid checkpoints. Rotation closes
the previous map before opening another recording.

`DemoClip` freezes the shared timeline, with 15/30/60/120-second windows (default
30) and 0/2/3/5-second post-roll (default 3). A second save finishes the pending
request and starts a distinct one; disconnect saves available post-roll. Disk
serialization runs on a worker with frozen values; world construction/disposal
stays on the scene owner. There is no duplicate pooled packet history. Extraction
and nested clips preserve exact initial worlds and required hidden warmup, including
v2/v3 compatibility reconstruction; frames/events are rebased without rerecording.

Events/annotations do not drive simulation. Studio derives deterministic highlights
and analytics from accepted events. `.studio.json` sidecars retain bookmarks,
named ranges and annotations without rewriting replay facts.

The Replay Studio library displays versioned metadata and offers watch, display rename,
favorite, delete, export, folder reveal on desktop, and `.part` recovery.
It supports live search across names/maps/modes/players and user annotations,
filters for full replays/clips/favorites/recovery, and newest/oldest/name/longest
sorting. Grid mode uses up to three opportunistically captured gameplay stills
with the map thumbnail as immediate fallback; list mode is the denser alternative.
A persistent size/mtime-keyed index avoids reopening every replay header on
each library rebuild. Display names/favorites are sidecars. Imported files stay in place.

`.ppclip` virtual clips store only source replay + frame range + display name.
They share packet data with the source until watch/export needs a materialized
`.ppdemo`, which is cached separately. Automatic highlights can create these
non-destructive clips in one action. Virtual-clip playback caches are mapped back
to their logical `.ppclip` descriptor for annotations, and cutting another
virtual clip from one is flattened back to the original replay with rebased
frames rather than depending on a temporary cache file. The replay settings page exposes a storage
limit and pruning policy; favorites are always protected and materialized clips
are protected unless the user explicitly allows clip pruning.

## Validation commands

Asset-free checks:

- `dotnet run --project tools/replay-timeline-check -c Release`
- `ProjectPrime -replayformatcheck`: v2/v3/v4 compatibility, bounds, CRC, recovery,
  extraction, world extension, semantic identity, session and socket isolation.
- `ProjectPrime -replaycontrolcheck`: rates, pause/step, fractional camera tracks,
  discontinuities, event analytics/highlights, annotations and controller context.
- `dotnet run --project tools/nettest -c Release -- --lifecycle`
- `dotnet run --project tools/nettest -c Release -- --health-shots`

Checks with locally extracted game assets:

- `-replayreplicacheck FILE [-shots DIR]`: interleaved worlds, foreground sentinels,
  mutable asset isolation, immediate restored hashes/images, continuation and seeks.
- `-replayworldcheck FILE [-output DIR]`: eight actors/all hunters in all 12 modes,
  afflictions, alt forms, projectiles, death/respawn and detached restores.
- `-replaylivecheck FILE`: accepted recorder facts through frozen world playback,
  source reset and backward seeks.
- `-replaykillcamcheck FILE [-shots DIR]`: use a world-coverage fixture; historical
  state, repeated lifecycle/skip/disconnect, resize, authority/slot changes,
  controller disconnect, final freeze and versioned audio handoff.
- `-replaytheatrecheck FILE [-shots DIR]`: normal Studio routing, cameras, transport,
  live-state isolation and offline Replay Lab handoff.
- `-replaydeterminism FILE [-replayhashout OUTPUT.ppdemo]`: linear/reconstructed,
  random seek/rates/EOF comparisons and optional versioned reference hashes.
- `-replayclipcheck SOURCE -clip CLIP -start FRAME`: every-frame source/clip
  gameplay and presentation equality.
- `-replaydurablecheck FILE -output DIR`: cold indexed seeks, nested ranges,
  corrupt optional checkpoint fallback and recovery.
- `-replayexportcheck FILE -output DIR`: repeated rendered images, true half-frame
  samples, cadence/seek consistency, output dimensions and gameplay invariance.
- `-replaybenchmark FILE -output DIR`: indexed/unindexed seek costs, CPU,
  allocations, timeline retention and private killcam memory.
- `-replayvalidate FILE`, `-replayrecover FILE`, `-demoinfo FILE [-replay]`:
  integrity/recovery and recorded fact-cadence diagnostics.

Gameplay hashes (schema 3) cover the explicit gameplay projection; a separate
projection covers animation, trails and particles. These compare reconstructed
replay worlds, not a predicting live client's hidden state. Reference verification
requires a matching engine/hash schema; old schemas do not prevent packet playback.
See the acceptance report for runtime coverage and platform limitations.
