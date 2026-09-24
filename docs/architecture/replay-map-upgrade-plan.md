# Project Prime Replay, Kill Cam, and Map Editor Architecture Upgrade Plan

## 1. Objective

Upgrade the current Project Prime replay, kill cam, and map-authoring systems by combining:

* The stronger architecture from `AntiNotAnti/Project-Prime-Reborn`
* The richer Replay Studio and tooling already present in the current Project Prime codebase
* The current networking, map compiler, package format, custom map features, UI, Android support, and modern launcher

The goal is **not** to revert the current codebase to Project-Prime-Reborn.

The goal is:

> **Reborn architecture + current Project Prime feature set**

The finished system should provide:

* Smooth and deterministic replay playback
* Reliable personal kill cams
* Reliable final kill cams
* Correct historical projectiles, effects, animation, and attacker state
* Replay playback isolated from live networking state
* Fast seeking and scrubbing
* One replay data pipeline shared by replays, clips, kill cams, highlights, and exports
* Faster and more scalable map editing
* Fine-grained viewport invalidation
* Efficient undo/redo
* Asynchronous, deduplicated map compilation
* Full integration with the modern launcher
* No regressions to existing Replay Studio functionality

---

# 2. Reference Repositories

## Architecture reference

Repository:

```text
AntiNotAnti/Project-Prime-Reborn
```

Primary reference areas:

```text
src/Shared.Replay/
src/Client.Presentation/Networking/
src/Client.Presentation/Replay/
src/Editor/
src/MapPlatform/
```

Particularly useful files:

```text
src/Shared.Replay/RollingReplayTimeline.cs
src/Client.Presentation/Networking/ReplayPlaybackSession.cs
src/Client.Presentation/Networking/ReplaySessionHost.cs
src/Client.Presentation/Replay/KillcamController.cs
src/Client.Presentation/Replay/ReplayPresentationController.cs

src/Editor/Documents/MapDocument.cs
src/Editor/Commands/EditorCommands.cs
src/Editor/Viewport/EditorViewport.cs
src/MapPlatform/Compilation/MapBuildSnapshot.cs
```

## Target repository

Current Project Prime repository:

```text
AntiNotAnti/Project-Prime
```

Before implementation begins:

1. Pull latest `main`.
2. Record the exact target SHA.
3. Record the exact Project-Prime-Reborn reference SHA.
4. Create the implementation branch from the latest target `main`.
5. Re-check any replay/map-related PRs merged since this plan was written.
6. Do not overwrite newer functionality merely because Reborn implemented the same concept differently.

---

# 3. Critical Design Rules

These requirements are architectural invariants.

## 3.1 Live gameplay must never be rewound for replay presentation

Kill cams, replay playback, highlights, and video export must never rewind or mutate the live gameplay scene.

A kill cam should operate as:

```text
Live match
    |
    +---- continues normally
    |
    +---- frozen replay clip
              |
              v
      passive replay session
              |
              v
       separate Scene
              |
              v
    separate presentation
```

---

## 3.2 Replay playback must not behave like a live network connection

Current replay playback reuses significant portions of the live `NetSession` pipeline.

The new architecture must separate:

```text
network transport
```

from:

```text
recorded authoritative state
```

Legacy replay compatibility may continue to use adapters temporarily.

New replay infrastructure must use explicit replica-only scene services.

---

## 3.3 One replay timeline should feed multiple features

These should no longer require unrelated implementations:

* Full replay recording
* Rolling instant replay
* Instant clips
* Personal kill cams
* Final kill cams
* Replay Studio
* Automatic highlights
* Video export
* Replay thumbnails
* Replay analysis

Target:

```text
Authoritative accepted gameplay facts
              |
              v
      Replay timeline layer
       /      |       \
      /       |        \
killcam     replay      clips
              |
       Replay Studio
              |
          exporter
```

---

## 3.4 Current Replay Studio features must be preserved

Do not regress:

* Replay library
* Search
* Sorting
* Filters
* Favorites
* Rename
* Import
* Delete
* Recovery
* Integrity validation
* Virtual clips
* `.ppclip`
* Annotations
* Bookmarks
* Named highlights
* Analytics
* Camera tracks
* Camera keyframes
* Interpolation modes
* Easing
* Look-at targeting
* Director
* Replay HUD
* Video export
* 720p / 1080p / 1440p / 4K
* 30 / 60 / 120 FPS exports
* Existing controller replay bindings
* Android replay controls
* Replay Lab functionality
* Existing replay deterministic verification tools where still applicable

These capabilities should become clients of the new replay architecture.

---

# 4. Existing Problems to Resolve

## Replay

The current replay architecture primarily feeds recorded network packets through the normal networking/session handlers.

This creates unnecessary coupling between:

* Playback
* Packet timing
* NetSession
* Network smoothing
* Scene setup
* Room transitions
* Replay camera presentation
* Process-global state

Symptoms include:

* Choppy playback
* Playback sensitivity to recorded packet cadence
* Extra smoothing logic specifically compensating for replay packet arrival
* More complex seek reconstruction
* Replay bugs leaking into unrelated network code

---

## Kill cam

The current kill cam records a small historical presentation ring containing primarily:

* Player position
* Player transform
* Facing
* Health
* Spawn status
* Alt form
* Weapon
* Camera information

It does not reconstruct a complete historical gameplay scene.

The renderer must therefore special-case live objects during kill cams.

Example problem:

```text
historical players
+
historical camera
+
current world
-
current projectiles
```

This cannot reliably reproduce the actual kill.

It also explains issues such as:

* Killer not visibly firing
* Projectile appearing without coherent historical context
* Missing visual effects
* Camera showing incomplete action
* Final kill cams failing to appear consistently

The kill cam should instead play a short actual replay.

---

## Map Editor

Current Map Studio contains useful functionality, but several architectural choices are expensive.

Current editing commonly:

1. Clones the map definition.
2. Clones it again.
3. Applies a modification.
4. Serializes large project state to determine whether it changed.
5. Stores large before/after snapshots for history.

The viewport also responds broadly to document changes.

Current behavior:

```text
Document.Changed
      |
      v
MapViewport.Rebuild()
      |
      v
MapViewportScene.Create()
```

That means small changes can trigger much larger reconstruction than required.

---

# 5. Target Replay Architecture

Introduce a layered replay architecture.

```text
                      LIVE GAME
                         |
                         v
             Accepted authoritative state
                         |
                         v
                 ReplayRecorder
                         |
              +----------+----------+
              |                     |
              v                     v
      RollingReplayTimeline      Disk writer
              |                     |
              |                .ppdemo v3+
              |
       +------+------+-------------------+
       |             |                   |
       v             v                   v
    Killcam       Quick Clip       Replay Studio
       |             |                   |
       +-------------+-------------------+
                     |
                     v
            ReplayPlaybackSession
                     |
                     v
              ReplaySceneServices
                     |
                     v
               Replica Scene
                     |
                     v
             ScenePresentation
```

---

# 6. Phase P0A: Introduce Replay Timeline Abstraction

## Goal

Create the common replay timeline infrastructure without changing player-visible replay behavior yet.

This becomes the foundation for everything else.

---

## 6.1 Add `IReplayTimeline`

Suggested location:

```text
src/MphRead/Mods/Network/ReplayTimeline/
```

or a similarly appropriate shared replay namespace.

Interface should expose concepts equivalent to:

```csharp
uint? FirstRecordingFrame
uint? LastRecordingFrame

bool TryGetRestorePoint(...)
bool TryFreeze(...)
bool TryMapServerTickToRecordingFrame(...)
bool TryMapKillToRecordingFrame(...)
```

The timeline must remain independent from:

* Renderer
* UI
* File picker
* Launcher
* Live network socket
* Replay Studio UI

---

## 6.2 Add replay timeline record model

Introduce models similar to:

```text
ReplayTimelineRecord
ReplayRestorePoint
ReplayTimelineClip
ReplayMarker
```

Each timeline record should know:

* Recording frame
* Authoritative/server tick
* Record/fact type
* Payload
* Optional event marker
* Approximate payload size

Do not store raw mutable references to live objects.

---

## 6.3 Add `RollingReplayTimeline`

Port the conceptual design from Reborn.

Default goals:

```text
Target history: 45 seconds
Maximum payload: 64 MiB
```

These may later become configurable.

The buffer should contain complete restorable segments.

Do not evict arbitrary data required to restore the next retained section.

Eviction should prefer:

```text
complete restore segment
+
its dependent timeline facts
```

rather than arbitrary packets.

---

## 6.4 Restore points

A restore point must contain enough state to create a valid replay scene.

Include at minimum:

* Match rules
* Match state
* Complete roster
* Player state
* World state
* Objective state
* Match clock
* Relevant RNG state
* Replay presentation state needed for deterministic continuation
* Perspective metadata where necessary
* Feedback/chat state where required by Replay Studio

Avoid capturing:

* Live sockets
* Network handles
* Window handles
* GPU objects
* Live entity references
* Shared mutable gameplay collections

---

## 6.5 Add strict memory bounds

Rolling history must be bounded.

Expose diagnostics:

```text
record count
restore point count
payload bytes
first frame
last frame
first server tick
last server tick
evicted segment count
freeze failures
```

No unbounded lists.

---

## 6.6 Tests

Add focused unit tests for:

* Empty timeline
* One restore point
* Multiple restore points
* Freeze valid range
* Freeze invalid range
* Eviction by age
* Eviction by bytes
* No partial restore segment retention
* Server tick mapping
* Kill mapping
* Timeline reset
* Match transition reset
* Memory bound enforcement

---

# 7. Phase P0B: Feed Accepted Authoritative State Into Timeline

## Goal

Build the rolling replay timeline from authoritative accepted gameplay facts instead of presentation-only pose snapshots.

---

## 7.1 Introduce/refactor recorder ownership

Create or evolve a single recorder responsible for:

```text
accepted match state
accepted roster
accepted snapshots
accepted world/objective state
accepted combat events
accepted match events
```

Suggested target:

```text
ReplayRecorder
```

The recorder should update:

```text
RollingReplayTimeline
```

continuously during live matches.

---

## 7.2 Record semantic events

At minimum:

```text
Kill
Death
Spawn
Damage
Headshot
Score change
Objective capture
Flag capture
Node capture
Prime change
Match point
Overtime
Match end
Join
Leave
```

Events must describe confirmed/accepted state.

Do not derive replay truth from speculative client hit prediction.

---

## 7.3 Capture exact kill identity

Kill events should contain sufficient identity fencing:

```text
match ID
event ID
server tick
killer slot
killer generation/identity
victim slot
victim generation/identity
victim life identity
weapon
damage/source classification
headshot flag
assist information where available
position where available
```

Use the current networking identity model.

Do not copy Reborn field names blindly if the target protocol already has stronger identifiers.

---

## 7.4 Create restore points periodically

Recommended:

```text
every 300 simulation frames
approximately every 5 seconds at 60 Hz
```

Restore points should also be generated at important boundaries where useful:

```text
new match
major room transition
possibly round transition
```

Do not generate excessive checkpoints.

---

# 8. Phase P0C: Add Instance-Owned `ReplayPlaybackSession`

## Goal

Stop treating replay playback as process-global network state.

---

## 8.1 Add session owner

Create:

```text
ReplayPlaybackSession
```

Each session owns:

```text
timeline/file reader
playback frame
decoded replay state
transport state
seek state
perspective
replay-specific scene services
highlight range where applicable
playback end range
errors
```

Avoid static global state inside the session.

---

## 8.2 Add replay host abstraction

Port the conceptual separation from:

```text
IReplaySessionHost
TheatreReplaySessionHost
PassiveReplaySessionHost
```

### Theatre host

Used while viewing a normal replay.

May bridge compatibility features that still require existing process-level functionality.

### Passive host

Used by:

* Kill cams
* Hidden replay scenes
* Thumbnail generation where appropriate
* Potential future preview surfaces

Passive replay must not mutate:

* Live NetSession
* Live lobby
* Live connection state
* Live reconnect state
* Live authority state
* Live slot ownership

---

## 8.3 Add `ReplaySceneServices`

Replay scene services must explicitly define replica behavior.

Examples:

```text
IsReplica = true
MayEndOnScore = false
ShouldLeaveAfterMatch = false
SuppressDamage = true
remote slots are replay controlled
no outgoing gameplay authority
no connection traffic
```

The replay scene should query its own session.

It must not silently fall back to global live state.

---

## 8.4 Preserve legacy replay support

Do not break old recordings.

Use adapters.

Target:

```text
legacy replay file
      |
legacy decoder
      |
ReplayPlaybackSession compatibility host
```

Modern/current recordings should use the cleaner path.

---

# 9. Phase P0D: Replace Kill Cam With Replay-Based Kill Cam

## Goal

Eliminate the pose-ring kill cam and replay the actual historical action.

This is the highest priority visual/gameplay improvement.

---

## 9.1 Introduce new `KillcamController`

Port and adapt the Reborn controller architecture.

The controller should own:

```text
live ScenePresentation reference
pending kill capture
frozen replay clip
ReplayPlaybackSession
replay Scene
replay ScenePresentation
kill identity
focus actor
camera mode
audio ownership
input ownership
HUD ownership
end reason
```

---

## 9.2 Killcam capture flow

When a confirmed local death is accepted:

```text
authoritative KillEvent
        |
        v
verify match/life/connection identity
        |
        v
locate exact kill in rolling timeline
        |
        v
freeze replay range
        |
        v
create passive ReplayPlaybackSession
        |
        v
create separate replay Scene
        |
        v
seek silently
        |
        v
present killcam
```

---

## 9.3 Recommended personal kill cam window

Initial values:

```text
Pre-kill: 5 seconds
Post-kill: 0.75 seconds
End freeze: 0.25 seconds
```

Equivalent at 60 Hz:

```text
300 frames pre-kill
45 frames post-kill
15 frame freeze
```

Tune later based on feel.

---

## 9.4 Do not delay respawn

Critical invariant:

> A kill cam must never keep the live player dead longer than the authoritative game state requires.

For immediate kill cams:

Stop the kill cam if:

```text
new life begins
match changes
connection changes
local slot changes
scene closes
playback fails
user skips
```

The live scene continues receiving network state behind the presentation.

---

## 9.5 Replay camera

Initial focus policy:

1. Exact killer identity if valid.
2. Chase camera.
3. First-person mode only if historically safe and visually correct.
4. Fallback to victim or neutral presentation if killer cannot be reconstructed.

Do not synthesize camera geometry from only current killer/victim positions if the replay camera system can follow the actual historical actor.

---

## 9.6 Kill cam projectiles/effects

Remove the current workaround where live projectiles are hidden during historical pose rendering.

Once kill cams are replay scenes, historical:

* Beams
* Missiles
* Bombs
* Explosions
* Weapon effects
* Hit effects
* Movement
* Hunter animation

must come from the replay scene.

This directly addresses the current missing-killer/missing-projectile behavior.

---

## 9.7 Input ownership

While kill cam is visible:

Gameplay input must not leak into live gameplay.

Supported skip surfaces:

```text
keyboard
mouse/fire bind
controller
Android touch
Android Back where appropriate
```

Only rising-edge actions should skip.

Holding a button must not repeatedly trigger transitions.

After kill cam closes:

```text
neutralize stale/held replay input
restore live input ownership
```

---

## 9.8 Audio ownership

During hidden seek/warmup:

```text
no historical audio
```

When kill cam becomes visible:

```text
presentation audio ownership -> replay scene
```

When kill cam exits:

```text
presentation audio ownership -> live scene
```

Do not allow stale replay cleanup to stop newly restored live audio.

Use ownership/version checks.

---

## 9.9 HUD ownership

Suppress live HUD while replay HUD is visible.

Kill cam HUD:

```text
KILLCAM
killer name
weapon
headshot where applicable
progress bar
skip instruction
```

No live HUD flicker during transition.

---

# 10. Phase P0E: Final Kill Cam Rework

## Goal

Use the same replay system for final kill cams instead of maintaining a separate historical snapshot mechanism.

---

## 10.1 Candidate tracking

Track accepted authoritative kill events throughout the terminal match window.

At match end:

1. Determine final candidate.
2. Validate candidate belongs to current match.
3. Validate killer/victim identities.
4. Validate it actually qualifies under current end condition.
5. Freeze the replay clip immediately.
6. Resolve final match result.
7. Start final replay only when eligible.

---

## 10.2 Avoid stale "last kill" bugs

Do not simply replay the newest local ring entry.

For score-goal or survival endings:

Require causal proximity to match termination.

For timed endings:

A recent valid enemy kill may be used according to intended design.

---

## 10.3 Final sequence states

Use explicit states:

```text
None
GameOver
Replay
AwaitCompletion
```

Scene completion rules must be explicit.

Do not allow:

```text
results screen
final kill cam
match transition
```

to compete for rendering ownership.

---

# 11. Phase P0 Acceptance Gate

Do not move the old kill cam path toward deletion until all of the following pass.

## Functional

* Personal kill cam appears reliably.
* Killer is visible when expected.
* Killer weapon action is historically coherent.
* Projectiles are historical.
* Explosions/effects are historical.
* Victim state matches the historical event.
* Skipping works.
* Respawn is not delayed.
* Match transition is not delayed.
* Disconnect during kill cam does not crash.
* Window resize does not crash.
* Controller disconnect during kill cam does not crash.
* Android pause/resume around kill cam does not crash.

## Stress

Run repeated cycles:

```text
death
killcam
skip
respawn
death
killcam
respawn
```

for many iterations.

Test:

```text
2 players
4 players
8 players
high ping
packet loss
late join
host migration/authority changes if applicable
round transition
match ending during killcam
```

---

# 12. Phase P1A: Move Normal Replay Playback Onto ReplayPlaybackSession

## Goal

Normal Replay Studio viewing should use the same session model introduced for kill cams.

---

## 12.1 Convert `DemoPlayback`

Do not delete it immediately.

Turn it into either:

```text
compatibility facade
```

or:

```text
legacy replay adapter
```

New code should target:

```text
ReplayPlaybackSession
```

instead of static `DemoPlayback`.

---

## 12.2 Remove live-socket assumptions

Playback must never open a gameplay connection.

Recorded connection-control packets must not:

* Assign a real local slot
* Change authority
* Reconnect
* Disconnect the live client
* Send acknowledgements
* Change lobby state

---

## 12.3 Replay simulation clock

Keep:

```text
60 Hz deterministic simulation
```

Playback rates:

```text
0.25x
0.5x
1x
2x
4x
```

Playback rate must alter scheduling only.

Do not change:

```text
physics delta
recorded frame numbers
simulation constants
```

---

# 13. Phase P1B: Replay Seeking Rework

## Goal

Improve replay scrubbing responsiveness while preserving deterministic state.

---

## 13.1 Persistent restore points

Prefer replay files containing durable restore checkpoints.

Seek:

```text
requested frame
     |
     v
nearest valid restore point
     |
     v
restore replica state
     |
     v
silent fixed-tick simulation
     |
     v
target frame
```

---

## 13.2 Bounded seek batches

Do not simulate thousands of historical frames in one UI update.

Recommended maximum:

```text
120 simulation steps per host update
```

Continue on subsequent updates until target is reached.

---

## 13.3 Seeking suppression

During seek:

Suppress:

* Rendering intermediate frames
* Gameplay input
* Historical confirmation audio
* Music restarts
* UI event spam

Continue simulation state needed for deterministic reconstruction.

---

## 13.4 Seek diagnostics

Track:

```text
target frame
restore frame
simulation steps
seek duration
checkpoint source
fallback path
checkpoint rejection
```

Expose in debug mode.

---

# 14. Phase P1C: Replay Rendering Smoothness

## Goal

Separate replay simulation frequency from display refresh.

Replay simulation remains:

```text
60 Hz
```

Presentation can render at:

```text
60
90
120
144
165
240+
```

where supported.

---

## 14.1 Presentation interpolation

Interpolation should live at the presentation/render boundary.

Do not use fake receive jitter to drive replay presentation.

For replay entities:

```text
previous deterministic simulation state
current deterministic simulation state
render alpha
```

Produce:

```text
render position
render orientation
render camera
```

---

## 14.2 Discontinuity protection

Never interpolate across:

* Respawn
* Death
* Teleport
* Form change where interpolation is visually invalid
* Room transition
* Identity/generation change
* Replay seek
* Checkpoint restore

Reset pose history at those boundaries.

---

## 14.3 Remove obsolete replay-specific network smoothing

Once equivalent presentation quality is verified:

Retire replay paths whose only purpose is to compensate for packet injection cadence.

Examples may include replay-specific searches around missing network frames.

Do not remove them prematurely.

Use before/after replay captures for verification.

---

# 15. Phase P1D: Merge Instant Clip and Kill Cam Capture Pipelines

## Goal

Retire duplicated rolling history systems.

Current concepts include:

```text
DemoClip
KillCam history
ReplayRecorder
```

Target:

```text
RollingReplayTimeline
```

---

## 15.1 Preserve existing clip settings

Keep:

```text
15 seconds
30 seconds
60 seconds
120 seconds
```

Post-roll:

```text
0
2
3
5 seconds
```

---

## 15.2 Clip save flow

New clip process:

```text
RollingReplayTimeline
        |
        v
freeze immutable clip
        |
        +---- async disk writer
        |
        +---- killcam
        |
        +---- Replay Studio
```

Do not block gameplay while writing a clip.

---

## 15.3 Clip serialization

Write from frozen timeline state.

Use temporary file then atomic rename.

On failure:

* Match continues.
* Original replay remains intact.
* Error is reported.
* Temporary file is cleaned where possible.

---

# 16. Phase P1E: Replay Studio Migration

## Goal

Preserve current Replay Studio functionality while changing the engine underneath.

---

## 16.1 Keep current studio UI

Retain:

```text
HubReplayStudioView
ReplayControlsView
ReplayStudio
ReplayAnnotations
ReplayWorkspace
ReplayCamera
ReplayVideoExporter
```

where practical.

Only refactor their data/playback dependencies.

---

## 16.2 Transport

Studio controls should target the active `ReplayPlaybackSession`.

Support:

```text
play
pause
step
seek
speed
previous event
next event
restart
```

---

## 16.3 Camera tracks

Preserve:

* Camera keyframes
* Linear interpolation
* Smooth interpolation
* Catmull-Rom
* Ease-in
* Ease-out
* Ease-in-out
* Roll
* FOV
* Look-at targets
* Constant-speed track behavior where currently supported
* Collision-aware camera track behavior

Camera state must be separate from replay simulation truth.

---

## 16.4 Virtual clips

Continue supporting:

```text
.ppclip
```

Virtual clips should reference:

```text
source replay
start frame
end frame
display metadata
```

Do not rewrite the replay stream merely to rename or annotate a clip.

---

## 16.5 Highlight system

Port/use the strongest semantics from Reborn while preserving current Studio functionality.

Highlights should be derived from authoritative recorded events.

Potential scoring inputs:

```text
kills
headshots
multi-kills
objective plays
match point
late-match action
damage exchanges
```

Highlight generation must remain deterministic for a given replay/event stream.

---

# 17. Phase P1F: Replay Video Export Migration

## Goal

Ensure video export uses the same deterministic replay session.

Flow:

```text
ReplayPlaybackSession
       |
       v
fixed target frame
       |
       v
render scene
       |
       v
capture frame
       |
       v
encoder/image sequence
```

Retain existing output modes.

---

## 17.1 Export rules

Video export must not depend on:

* Monitor refresh rate
* Wall-clock playback cadence
* Live network timing

For 120 FPS output from a 60 Hz replay:

Use deterministic presentation interpolation.

Do not simulate gameplay at 120 Hz.

---

# 18. Phase P1 Replay Acceptance Gate

Test full recordings for:

```text
short match
long match
2 players
4 players
8 players
custom map
stock map
all major modes
spectator replay
late join
round transition
match rotation
```

Validate:

* Playback determinism
* Smoothness
* Pause
* Step
* Rate changes
* Forward seek
* Backward seek
* Event seek
* Replay Studio
* Camera tracks
* Highlights
* Virtual clips
* Video export
* Android controls
* Controller controls

---

# 19. Phase P1 Cleanup

Only after acceptance:

Deprecate/remove:

```text
pose-ring killcam implementation
historical player draw substitutions
historical camera synthesis
killcam-specific projectile suppression
duplicated instant replay history
obsolete replay network timing workarounds
```

Keep compatibility shims if old replay files need them.

---

# 20. Phase P2A: MapDocument Architecture Upgrade

## Goal

Remove whole-project cloning/serialization from common editor operations.

---

## 20.1 Introduce document state IDs

Add:

```text
DocumentStateId
CurrentStateId
SavedStateId
Revision
```

Dirty state becomes:

```csharp
IsDirty => CurrentStateId != SavedStateId;
```

Do not serialize the entire project to determine dirty state.

---

## 20.2 Introduce delta editor commands

Add:

```text
IEditorCommand
EditorHistoryEntry
EditorChangeKind
EditorChangeState
```

Command types should include:

```text
Add object
Delete object
Duplicate object
Transform object
Change material
Change face material
Change UV
Modify entity
Modify environment
Modify mode support
Modify metadata
Toggle overlay
```

---

## 20.3 Transform commands

A transform command should store:

```text
object ID
before transform
after transform
```

It should not store:

```text
entire MapProject before
entire MapProject after
```

---

## 20.4 Coalesced continuous editing

Mouse drag or numeric spinner changes during one interaction should become one undo operation.

Use transaction keys.

Example:

```text
mouse down
    |
several transform updates
    |
mouse up
```

History:

```text
1 command
```

not:

```text
143 commands
```

---

## 20.5 History limits

Suggested starting limits:

```text
500 commands
256 MiB approximate history
```

Prune oldest history as needed.

Do not allow unbounded undo memory.

---

# 21. Phase P2B: Map Viewport Fine-Grained Invalidation

## Goal

Stop rebuilding the whole visual map for unrelated editor changes.

---

## 21.1 Add change domains

Suggested:

```text
Geometry
Transform
Material
Entity
Selection
Overlay
Environment
Navigation
Metadata
All
```

Each document edit increments only relevant invalidation domains.

---

## 21.2 Split viewport caches

Maintain independent caches for:

```text
geometry mesh
imported geometry
grid
collision
entities
navigation
selection overlay
world bounds
gizmos
```

---

## 21.3 Expected behavior

Selecting an object:

```text
rebuild selection overlay only
```

Changing an entity:

```text
rebuild entity representation
```

Toggling collision:

```text
rebuild/show collision overlay
```

Changing material:

```text
update relevant geometry/material state
```

Moving one native brush:

```text
rebuild affected native geometry
```

Changing camera:

```text
no map rebuild
```

---

## 21.4 Imported BSP maps

Imported architecture should remain cached and read-only unless import settings or source change.

Do not repeatedly re-import BSP geometry during ordinary editing.

---

# 22. Phase P2C: GPU/Renderer Viewport Path

## Goal

Move the editor away from excessive UI-framework polygon drawing for larger maps.

The Reborn viewport architecture should be used as reference.

Target:

```text
CPU map mesh caches
       |
       v
RenderFrame
       |
       v
existing Project Prime renderer
       |
       v
editor viewport
```

Do not build a second rendering engine.

Reuse current renderer infrastructure.

---

## 22.1 Viewport coordinate contract

Use one explicit layout object for:

```text
logical UI rectangle
pixel viewport
aspect ratio
mouse normalization
picking
preview capture
renderer destination
```

This prevents scaling/picking bugs under:

* DPI scaling
* window resize
* different monitor scaling
* non-16:9 editor layouts

---

# 23. Phase P2D: Map Build Snapshot

## Goal

Background map compilation must never operate directly on mutable editor state.

Introduce:

```text
MapBuildSnapshot
```

When Build is requested:

```text
mutable editor project
       |
       v
capture immutable/detached snapshot
       |
       v
background compilation
```

Edits made while the build is running must not alter the in-progress build.

---

# 24. Phase P2E: Map Build Scheduler

## Goal

Prevent redundant map compilation and UI blocking.

Create:

```text
IMapBuildScheduler
MapBuildScheduler
```

Responsibilities:

* Execute compile work away from UI thread.
* Deduplicate identical simultaneous build requests.
* Limit compilation concurrency.
* Share build result for identical fingerprint requests.
* Support cancellation of callers.
* Do not corrupt shared work when one caller cancels.
* Keep compiler exceptions structured.

Suggested global concurrency:

```text
2 builds
```

Serialize base-content reads if underlying readers are not thread safe.

---

# 25. Phase P2F: Content-Addressed Map Cache

## Goal

Avoid recompiling maps whose logical inputs have not changed.

Fingerprint should include:

```text
project schema
map project
geometry
materials
custom textures
import source
import settings
preview inputs where appropriate
compiler version
relevant packaging/build version
```

Cache:

```text
map-cache/<fingerprint>/
```

Build result should contain:

```text
Model.bin
Anim.bin
Collision.bin
Ent.bin
Node.bin
metadata/manifest
```

depending on current map compiler output.

---

# 26. Phase P2G: Single Dependency Analyzer

## Goal

Create one source of truth for map dependencies.

Dependencies include:

```text
base-game content
Q3 BSP/PK3 source
texture pack
custom images
preview image
custom audio if supported
```

Consumers:

```text
fingerprinting
validation
compiler
editor
packager
map installation
runtime preparation
server preparation
```

Do not let each subsystem discover dependencies differently.

---

# 27. Phase P2H: Map Editor Hub Integration

## Goal

Remove the "coming later" Map Editor destination from the modern hub.

Current modern hub should open the actual editor workflow.

Target user flow:

```text
HOME
  |
  v
MAP EDITOR
  |
  +-- My Maps
  +-- Create Map
  +-- Import Q3
  +-- Recover Autosave
  +-- Edit
  +-- Validate
  +-- Build
  +-- Playtest
  +-- Package
```

---

## 27.1 Avoid duplicate editor implementations

Do not maintain:

```text
modern placeholder editor
+
legacy Map Studio editor
```

Create one editor backend.

The modern hub becomes its primary launcher surface.

---

# 28. Phase P2I: Map Editor UX Enhancements

After architecture is stable, improve editor usability.

Potential additions:

* Better hierarchy filtering
* Multi-select inspector
* Copy/paste
* Duplicate
* Hide/show objects
* Lock objects
* Layer/group support
* Object search
* Better snapping UI
* World/local axis toggle
* Better gizmos
* Transform numeric inputs
* Face selection
* Material thumbnails
* Collision overlay
* Navigation overlay
* Spawn overlay
* Objective overlay
* Performance statistics
* Build status
* Validation issue list
* Click diagnostic to select problem object

Do not add these until the underlying invalidation/history architecture is correct.

---

# 29. Map Editor Acceptance Gate

## History

Verify:

* Undo transform
* Redo transform
* Undo create
* Undo delete
* Undo material
* Undo entity property
* Branch after undo
* Save dirty state
* Undo back to saved state
* History memory bound

---

## Performance

Instrument rebuild counts.

Acceptance examples:

### Selection

```text
Select brush
```

Expected:

```text
Geometry rebuilds: 0
Entity rebuilds: 0
Selection rebuilds: 1
```

### Camera movement

Expected:

```text
All map rebuilds: 0
```

### Entity move

Expected:

```text
Full geometry re-import: 0
```

### Overlay toggle

Expected:

```text
Geometry re-import: 0
```

---

# 30. Phase P3: Unified Observability and Diagnostics

Add debug statistics for replay and map systems.

## Replay

```text
timeline bytes
restore points
timeline records
capture age
seek restore frame
seek steps
seek milliseconds
session type
passive/theatre
current replay frame
render alpha
killcam state
killcam end reason
```

## Map Editor

```text
geometry rebuild count
selection rebuild count
entity rebuild count
collision rebuild count
navigation rebuild count
cache fingerprint
build duration
cache hit/miss
undo history bytes
undo command count
```

Keep high-cardinality data out of release telemetry.

---

# 31. Compatibility Requirements

## Replay formats

Do not silently break existing replay files.

Maintain support for current supported:

```text
v2
v3
```

unless a deliberate migration decision is separately approved.

If introducing a new format version:

* Reader must remain backward compatible where practical.
* New format must have explicit versioning.
* Protocol incompatibility must fail clearly.
* Do not silently reinterpret incompatible packet/fact layouts.

---

## Map formats

Preserve:

* Existing map projects
* Current custom map packages
* Q3 import
* Current `.ppmap` expectations
* Existing server map workflows
* Current custom map identity rules
* Existing local map library

Architecture changes must not arbitrarily invalidate existing creator content.

---

# 32. Threading Rules

## Replay

Live authoritative recorder:

```text
single logical owner
```

Frozen clips become immutable.

Disk writing may happen asynchronously.

Replay scenes should have clearly owned mutation threads.

---

## Map Editor

UI modifies mutable project state.

Build jobs receive detached snapshots.

Background jobs never modify the live project graph.

UI updates from jobs must return through the UI dispatcher.

---

# 33. Error Handling Rules

Failure in:

```text
replay recording
clip writing
killcam creation
highlight generation
video export
map build
map package
preview generation
```

must not crash or terminate a live match unless the underlying game itself cannot continue.

Prefer:

```text
feature failure
+
clear diagnostic
+
safe fallback
```

For kill cam failure:

```text
return immediately to live presentation
```

For final kill cam failure:

```text
show normal game-over/results flow
```

---

# 34. Testing Strategy

Tests should be divided by responsibility.

## Unit tests

* Timeline
* Restore points
* Event mapping
* Clip freezing
* Transport arithmetic
* Map delta commands
* Document state IDs
* Cache invalidation
* Fingerprints

## Integration tests

* Replay recording → playback
* Timeline → kill cam
* Timeline → instant clip
* Replay → seek
* Replay → Replay Studio
* Replay → video export
* Map project → snapshot → compile
* Map project → edit → undo → compile

## Runtime tests

* Desktop
* Android
* Dedicated server where relevant

---

# 35. Replay Determinism Verification

Keep and expand the current replay hash infrastructure.

Compare a stable gameplay projection including:

```text
player transform
position
health
form
weapon
spawn state
scores
match timer
objective ownership/state
relevant world state
```

Compare:

```text
linear playback
vs
checkpoint restored playback
```

at sampled and/or every available deterministic simulation frame.

Presentation-only values should not fail gameplay determinism checks.

---

# 36. Performance Benchmarks

Record before/after metrics before removing old code.

## Replay

Measure:

```text
replay startup time
seek to +30 seconds
seek to +5 minutes
backward seek
CPU time per playback tick
allocations per replay frame
killcam startup latency
killcam memory usage
```

## Editor

Measure:

```text
selection latency
single object transform latency
viewport rebuild time
full geometry rebuild time
large-map navigation overlay
undo/redo latency
map save latency
compile cache hit
compile cache miss
```

---

# 37. Recommended Pull Request Structure

Do not implement everything in one giant PR.

## PR 1: Replay Timeline Foundation

Implement:

* `IReplayTimeline`
* `RollingReplayTimeline`
* Restore points
* Timeline records
* Frozen clips
* Recorder feed
* Unit tests

No major player-facing behavior change.

---

## PR 2: Replay Session Isolation + New Kill Cam

Implement:

* `ReplayPlaybackSession`
* Replay session hosts
* Replay scene services
* Replica scene construction
* New personal kill cam
* New final kill cam
* Audio/HUD/input handoff
* Killcam tests

Keep old killcam behind temporary fallback or feature flag during verification.

---

## PR 3: Normal Replay Playback Migration

Implement:

* Replay Studio viewing through `ReplayPlaybackSession`
* Transport migration
* Seek migration
* Replay presentation interpolation
* Compatibility facade for old playback callers
* Determinism tests

---

## PR 4: Unified Clips + Replay Cleanup

Implement:

* Instant clips from rolling timeline
* Retire duplicate killcam history
* Retire old packet-page history where no longer required
* Remove killcam projectile suppression
* Remove historical player pose substitution
* Cleanup old replay-specific global state

---

## PR 5: Replay Studio Integration and Optimization

Implement/refine:

* Highlights
* Virtual clips
* Analytics
* Camera tracks
* Video export
* Library metadata
* Thumbnail generation
* Performance improvements

No feature regression.

---

## PR 6: MapDocument / History Refactor

Implement:

* `DocumentStateId`
* Delta commands
* Coalesced transactions
* Memory-bounded history
* Dirty state
* Command tests

Keep renderer behavior unchanged initially.

---

## PR 7: Map Viewport Cache Refactor

Implement:

* Change domains
* Independent viewport caches
* Fine-grained invalidation
* Better selection path
* Performance counters
* Tests

---

## PR 8: Map Build Scheduler / Cache Architecture

Implement:

* `MapBuildSnapshot`
* Build scheduler
* Single-flight builds
* Compile concurrency bound
* Content-addressed cache
* Shared dependency analyzer

---

## PR 9: Modern Map Editor Hub Integration

Implement:

* Replace Map Editor placeholder
* My Maps
* Create
* Import
* Recovery
* Edit
* Build
* Validate
* Playtest
* Package
* Modern UI integration

---

## PR 10: Final Cleanup and Optimization

Remove obsolete compatibility paths after verified replacement.

Update:

```text
README
architecture docs
Replay docs
Map Studio docs
developer docs
architecture invariants
test documentation
```

---

# 38. Migration Flags

During migration, temporary developer-only toggles are acceptable.

Examples:

```text
UseReplaySessionV2
UseReplayKillcam
UseTimelineClips
UseIncrementalMapViewport
```

Do not expose confusing experimental toggles to normal users.

Remove flags after acceptance.

---

# 39. Do Not Do

Do not:

* Copy Reborn files wholesale without adapting them to current architecture.
* Replace current Replay Studio UI with Reborn Theatre UI.
* Remove current video export.
* Remove virtual clips.
* Remove current camera track functionality.
* Reintroduce outdated protocol behavior.
* Make killcam rewind the live scene.
* Let killcam block authoritative respawn.
* Make replay create a live socket.
* Store GPU objects in replay checkpoints.
* Store live scene object references in timeline records.
* Serialize the complete map on every transform.
* Rebuild all editor geometry on selection.
* Run map compilation synchronously on the UI thread.
* Build a second custom-map renderer.
* Break existing `.ppdemo` files without explicit migration.
* Break existing `.ppmap` projects/packages.

---

# 40. Code Quality Expectations

All new code should prioritize:

```text
modern
simple
clean
efficient
optimized
testable
bounded
explicit ownership
```

Prefer:

```text
small state owners
immutable transfer objects
bounded buffers
explicit lifecycle
dependency injection where useful
clear compatibility adapters
```

Avoid creating new process-global mutable singletons unless absolutely necessary.

---

# 41. Final Target State

When this project is complete:

## Kill cams

Kill cams will be actual deterministic replay scenes rather than historical pose overlays.

They will correctly reproduce:

* Killer
* Victim
* Weapon
* Projectile
* Explosion
* Movement
* Relevant historical effects
* Historical scene timing

without rewinding live gameplay.

---

## Replay

Replay becomes a first-class replica simulation system.

```text
recorded authoritative facts
        |
        v
ReplayPlaybackSession
        |
        v
replica Scene
        |
        v
presentation
```

Normal replays, kill cams, clips, highlights, and exports use the same underlying system.

---

## Replay Studio

Replay Studio keeps all modern Project Prime features while gaining:

* Better playback smoothness
* Better isolation
* More reliable seeking
* Cleaner lifecycle
* Better kill cam integration
* Less dependence on network packet timing

---

## Map Editor

Map Editor becomes event/invalidation driven rather than whole-project driven.

Small actions stay small.

```text
select object
    ->
selection cache only
```

instead of:

```text
select object
    ->
rebuild map
```

---

# 42. Completion Criteria

This initiative is complete only when all of the following are true:

* Replay playback no longer requires a live gameplay socket.
* Kill cams use isolated replay scenes.
* Historical projectile/effect presentation works in kill cams.
* Personal kill cams do not delay respawn.
* Final kill cams do not break match completion.
* Full replay playback is smooth at high refresh rates.
* Replay seeking uses bounded restore/warmup.
* Replay Studio features remain available.
* Instant clips use the shared replay timeline.
* Duplicate killcam pose history is removed.
* Map dirty tracking does not serialize the whole project.
* Common editor actions use delta history.
* Selection does not rebuild map geometry.
* Entity-only edits do not rebuild imported geometry.
* Background map compilation uses detached snapshots.
* Duplicate concurrent map builds are deduplicated.
* The modern Map Editor menu opens the real editor.
* Existing supported replay files remain compatible.
* Existing supported map projects/packages remain compatible.
* Desktop build passes.
* Android build passes.
* Dedicated server build passes.
* Replay-focused tests pass.
* Map-platform/editor tests pass.
* Documentation reflects the new architecture.

---

# 43. Recommended Implementation Order Summary

```text
P0
│
├─ Replay timeline
├─ Authoritative recorder
├─ ReplayPlaybackSession
├─ Passive replay scene
├─ Personal killcam
└─ Final killcam

P1
│
├─ Main replay migration
├─ Seeking/checkpoints
├─ Presentation smoothing
├─ Instant clips
├─ Replay Studio integration
└─ Video export integration

P2
│
├─ MapDocument state IDs
├─ Delta undo/redo
├─ Viewport invalidation
├─ Renderer-backed editor viewport
├─ Build snapshots
├─ Build scheduler
├─ Content cache
└─ Modern hub integration

P3
│
├─ Diagnostics
├─ Profiling
├─ Cleanup
├─ Compatibility removal
└─ Documentation
```

The most important rule for the implementation agent is:

> **Do not trade the current codebase's richer feature set for Reborn's older feature set. Use Reborn to repair ownership, replay isolation, kill-cam correctness, editor history, viewport invalidation, and build scheduling, while keeping the modern Project Prime functionality layered on top.**
