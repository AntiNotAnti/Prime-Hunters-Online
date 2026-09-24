# Replay, killcam and Map Studio hardening

Implemented against `b093dd69e680464e3306fc585d5dab46c4bf5935` (the checkout's current main), following the supplied implementation plan. The plan's audited commit was older. Replay formats and the existing network protocol remain unchanged by this work.

## Killcam behavior

- The accepted kill marker anchors the recording clock. Playback covers 210 frames before the kill and 90 frames after it, at the fixed 60 Hz simulation rate. The controller waits for the post-roll before freezing a clip. Short histories clamp the beginning to available footage.
- The native five-second winner scene runs first. An eligible final kill replay follows before results. Server end-sequence allowance and the client's stranded timeout include the additional replay window.
- Malformed identities are rejected before player/team indexing. Match, epoch, slot generation, life, respawn, disconnect, disabling and skip retain their lifecycle fences.
- Linear killcam players allocate no seek checkpoint dictionary, create zero memory checkpoints, perform no durable checkpoint reads, and reject seeking through both player and transport APIs.
- Final candidates retain only marker metadata until final presentation; playback owns one frozen clip lease.
- Chase, first-person and cinematic camera choices are persisted in settings.
- HUD uses antialiased embedded Roboto Bold, translucent slate panels, cyan personal/gold final accents, names, weapon, confirmed headshot, attacker health, relative kill time and a timeline kill marker. The existing wording is preserved.

## Replay Studio

- Export owns rendering and FFmpeg until process exit and redirected pipes drain. Missing/nonzero encoders fail visibly. A zero exit without the requested movie is also a failure. Cancellation runs process termination and waiting away from the presentation thread. PNGs, command and log remain for diagnosis.
- Queued jobs serialize rendering and encoding. Rendering restores the prior camera before asynchronous encoding. UI exposes phase/progress, cancellation, retry, clear-pending, output-folder access and bounded recent failure history.
- Encoder probing runs once in the background; compatible software encoding remains the default. Hardware choices are opt-in and still report runtime encoder failures.
- Materialized clips and recovery use two bounded storage slots, independent readers, cancellation and stale-result checks. Large file copies and virtual-clip resolution run away from UI presentation.
- Thumbnail GL readback remains on its owner; black-frame inspection, PNG encoding and writing use a bounded worker.
- Scrubbing replaces pending targets while retaining the requested resume state. Timeline adds density shading, event snapping, range dragging, cached hover previews and camera keyframe dragging. Keyframe movement saves once on release and cannot overwrite another key.
- Library scans run in the background; search is debounced, sorting/filtering use the loaded model, and rows are virtualized. Existing replay-header indexing is retained and synchronized for worker access.

## Map Studio

- Autosave has one writer and one replaceable pending detached snapshot. Serialization and atomic writes run off the dispatcher; disposed screens cannot receive callbacks.
- Validation/build/autosave reuse a detached snapshot cached by document state, replacing dispatcher-side JSON cloning. Each worker gets its own mutable graph.
- Document identity, state and editor generation guard publication after asynchronous jobs. Cancellation propagates through BSP conversion, textures, geometry, navigation and packaging. Shared build work remains alive for other waiters; cancellation of the final waiter cancels work and prevents publication.
- Duplication copies selected objects only. Preview replacement is one history operation. Generated-file cleanup preserves current, history and recovery references and only removes files registered as editor-generated.
- Added axis/plane constraints, world/local gizmos, axis scaling, pivot modes, shared CPU/GPU preview transforms, alignment, distribution, floor/grid snapping, arrays and radial arrays. Transform previews are verified against committed geometry.
- Added spawn/team/warning labels, jump trajectory apex/flight/target markers, navigation path inspection, map health statistics, asset sizes/usages/logical names/replacement/reveal/cleanup, and detached camera/selected-spawn/team-spawn playtests. Existing editor-instance restoration after playtest is retained.

## Validation on macOS arm64

| Check | Result |
| --- | --- |
| Desktop Release build | Passed; existing warnings remain |
| Dedicated server Release build | Passed |
| Replay format/compatibility | 2,710 checks passed: v2/v3/v4, corruption, recovery, extraction, cancellation |
| Replay controls | Passed, including encoder success/failure/missing/cancellation |
| Replay timeline | 43 checks passed |
| Killcam | 2,352 checks passed, 24 lifecycle cycles, delayed marker/post-roll test, zero checkpoints, historical-state comparisons, resizing and audio ownership |
| Rendered export | Real FFmpeg output at 30/60/120 FPS, native 720p/4K HUD targets, deterministic pixels, three-job queue, multiple segments, restored camera and render cancellation |
| Replay world/replica | All 12 multiplayer modes passed with detached restore, continuation, file/frozen-clip seeks |
| Map editor | 119 checks passed, including autosave, cancellation, history, alignment, arrays, floor snapping and preview/commit agreement |
| GPU map viewport | 24 checks passed |
| Network lifecycle | 3,680 assertions passed |
| Network health/shots | 3,338,728 assertions passed |
| Library model/virtualization | 100/500/1,000/5,000 entries passed; 10 realized rows at each size |
| UI rendering | 44 launcher layouts captured; killcam personal/final and map viewport captures inspected |

The eight-actor rendered test fixture was regenerated using protocol 17, already present in this checkout. An older protocol-16 fixture was correctly rejected by file playback. No compatibility check was weakened to make the test pass.

FFmpeg was absent from the machine's normal PATH. Export tests used a temporary isolated FFmpeg 7.1 executable, with no system installation or application preference changes.

## Measurements and remaining acceptance

- Detached snapshot capture versus JSON clone, in one Release run: 1,000 objects 2.40 vs 8.41 ms; 5,000 objects 14.22 vs 42.80 ms; 10,000 objects 31.43 vs 113.47 ms. An earlier run measured 23.56 vs 89.76 ms at 10,000 objects. These are snapshot timings, not a claim that all dispatcher latency has disappeared.
- The 5,000-entry library model populated in 1.90 ms and searched in 1.36 ms, with only 10 realized rows. This measures loaded-model filtering/virtualization, not a cold scan of 5,000 physical replay files.
- Live Windows/Linux/Android acceptance, controller/touch interactions and real multiplayer native-scene-to-final-replay transitions still need device testing. Synthetic rendered checks are not a substitute for these.
- Android build was attempted but blocked by `NETSDK1147`: the .NET Android workload is not installed.
- The plan's conditional P3 work (GPU readback/PBOs, pose cursors, adaptive checkpoint cadence and broader CPU renderer allocation changes) remains deferred until profiling identifies a benefit. No nondeterministic wall-clock checkpoint policy was introduced.

Useful commands (with a .NET 10 SDK on PATH):

```sh
dotnet build src/MphRead -c Release
dotnet run --project tools/map-editor-check -c Release -- --benchmark
dotnet run --project tools/replay-timeline-check -c Release
dotnet run --project src/MphRead -c Release -- -replayformatcheck
dotnet run --project src/MphRead -c Release -- -replaycontrolcheck
dotnet run --project src/MphRead -c Release -- -replaylibrarycheck /tmp/prime-library-check
dotnet run --project tools/nettest -c Release -- --lifecycle
dotnet run --project tools/nettest -c Release -- --health-shots
```
