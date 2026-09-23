# Replay and editor measurements

Measured on macOS arm64, .NET SDK 10.0.401 / runtime 10.0.12, September 22,
2026. Timings vary with JIT, GC, disk and other work. These are component
measurements, not end-to-end frame-rate claims.

## Replay

`-replaybenchmark FILE -output DIR` writes inspectable JSON and two local copies
of the same recording, with and without durable checkpoint indexes. It checks
that indexed/unindexed seeks and linear playback produce identical gameplay
hashes. This compares seeking strategies within the new engine; it does not
claim to benchmark an older executable or a pose-only killcam against a full world.

The input was an 18,817-frame / 313.6-second eight-client Battle recording with
100 ms RTT and 2% packet loss. All eight clients passed the live combat harness.
JIT/asset caches were warmed. Seek/startup figures are medians of three runs.
Rendering, texture uploads and GPU memory are excluded.

| Operation | Unindexed v4 | Indexed v4 | Work after indexed restore |
| --- | ---: | ---: | ---: |
| Open initial world | 21.34 ms | 9.96 ms | 0 ticks |
| Cold seek +30 seconds | 51.07 ms | 16.30 ms | 0 ticks |
| Cold seek +5 minutes | 565.52 ms | 22.43 ms | 0 ticks |
| Backward seek to +30 seconds | 31.79 ms | 16.25 ms | 0 ticks |

The targets deliberately coincide with checkpoints. The separate durable seek
fixture checks targets between them: 1–199 ticks, split into updates of at most
120 ticks. The unindexed backward seek can use its in-memory checkpoint cache.
The startup timing difference is cache/measurement variation; both formats have
the same initial-world load path.

Linear indexed playback used 672.08 ms process CPU for 18,000 ticks, approximately
0.037 ms/tick, and allocated 1,363,519,928 bytes (75,751 B/tick), including periodic
world checkpoint capture. Headless preparation of a frozen 120-frame killcam
window took 23.87 ms with 96 warmup ticks. Its retained private world was
8,743,968 managed bytes, measured by full GC before/after; the frozen data was
1,153,288 bytes. These figures exclude shared engine assets and native resources.

After five minutes the rolling timeline held 15,149,295 bytes, 29,255 records and
10 restore points, below its 64 MiB limit. The indexed file was 13,320,569 bytes;
the identical unindexed facts occupied 8,065,908 bytes. New capture retains at
most 4,096 durable checkpoints / 256 MiB of compressed checkpoints per file.
The live killcam diagnostics report actual creation-to-visible latency, frozen
bytes, state and termination reason.

## Frame pacing changes (September 23, 2026)

Base: GitHub main `55e78f4` (fetched again before publishing). Tests used macOS
arm64, SDK 10.0.401/runtime 10.0.12 and protocol-17 TEST ARENA fixtures generated
by `-replayworldcheck synthetic -output DIR`. No replay-format or protocol version
changed. The older measurements above describe the previous implementation.

| Warmed checkpoint capture | Main baseline | Updated implementation |
| --- | ---: | ---: |
| Mean of 100 captures after 5 warmups | 8.391 ms | 3.483 ms |
| Managed bytes per capture | 14,120,311 B | 7,304 B |

Both use the same 1,801-frame/eight-actor Battle fixture and final world. Baseline
instrumentation only adds the timing/allocation loop to `ReplayLiveCaptureCheck`.
These are single runs while other local tests were active, not isolated hardware
benchmarks. Pool growth/retained checkpoint buffers are excluded by warmup; those
buffers remain charged against timeline/queue capacity. This removes about 99.95%
of checkpoint temporary allocation, not all allocations in the game.

On that fixture, replay step allocation fell from 56,336 to 922 B/frame after
reusing collision/camera scratch, avoiding collision/intent boxing and consuming
accepted payload spans directly. Warmed authority capture plus encoding measures
0 B/operation (1,000 captures); encoded bytes match the reference implementation.
Quiet frontier advancement and timestamp measurement allocate no objects.

Validation completed:

- Release build: zero errors, 16 existing warnings.
- 43 timeline ownership/bounds/frontier checks; 2,708 replay-format checks,
  including byte-identical worker output, count/byte overflow, lease draining and
  recovery while storage is deliberately held.
- All 12 modes: detached restore/continuation, interleaved worlds, file/frozen seeks,
  and generated/reference checkpoint byte parity.
- Live capture: 1,801 accepted frames, 251 frozen-frame comparisons, reset isolation,
  disable/re-enable with a fresh history boundary, bounded clip warmup and v2/v3/v4
  nested-clip fidelity.
- 1,643 killcam checks: 300-frame personal/final playback at 1x, progress, weapon and
  headshot identity, short history, skip/respawn/disconnect and match/authority fences.
- Durable cold/backward seeks, corruption fallback/recovery, transport controls and
  frame timing checks pass. Main and updated builds produced matching seven indexed
  gameplay hashes for the same fixture; each passed 1,801-frame deterministic
  reconstruction internally.
- Network architecture, protocol 17, reliability, queue budget, load lifecycle,
  lag compensation, weapon policy, transport stress and allocation suites pass.

`-netcheck HOST -nographics -recorddemo -netdebug -seconds 630 -netlag 100
-netloss 2` permits an eight-client network/storage soak without GL. It runs real
client simulation and the private replay world because recording is active;
`MPHREAD_CLIP_TEST=1` requests two clips at 60 and 66 seconds (earlier for shorter runs). The server uses a ten-minute
Battle match with canonical recording and an isolated local UDP port. This is
simulation evidence only: headless mode does not present killcams or draw frames.
All eight clients completed 37,800 ticks, observed seven moving peers and reported
no capture or recording errors. Near tick 30,000, the previous 3,600 simulation
samples measured p50 0.185–0.209 ms, p95 0.550–0.580 ms and p99 1.382–2.141 ms.
The [machine-readable results](replay-frame-pacing-results.json) include every
client. After tick 600 there were three rate-limited >20 ms frame reports across
all clients, each with zero checkpoint time. This supports removal of the repeated
checkpoint spike in this scenario; it does not prove all stutters are gone.
Whole-run maxima were 86–139 ms, including initial JIT/world creation. Cumulative
checkpoint-frame means were 3.50–4.19 ms versus ordinary-frame means 0.28–0.29 ms.
Checkpoint work is still measurable, especially relative to high-refresh budgets.
The initial soak reached match teardown before its late clip trigger; a separate
90-second eight-client run exercises two clip saves per client during live play.
That run exposed 32–57 ms save-time scene construction on ALPHA despite bounded
warmup. The final implementation therefore saves the frozen checkpoint/facts on
a worker using existing v4 hidden lead-in. It builds no Scene during saving; the
live fidelity check verifies every visible frame, exact start/EOF, backward seek
and nested extraction of this path. Repeating the 90-second eight-client run
completed 5,400 ticks and two successful saves per client. All 16 clip files
validated, and no >20 ms frame report occurred in their save windows (ticks
3,500–4,500). These logs are rate-limited diagnostics, not exhaustive GPU traces.

### Rendered and Android follow-up

The native GL blocker cleared on a later run. Real rendered tests now pass:
1,643 killcam checks, Replay Studio's 620 isolated frames/four seeks, and repeated
30/60/120 FPS exports with identical images, real intermediate pictures and
unchanged gameplay hashes. `-replaycadencecheck FILE` renders 600 simulation
frames at each virtual 60/120/144/240/360/540 Hz cadence and verifies identical
hashes after every simulation step and before/after every draw. All pass without
dropped steps. This does not measure physical display pacing.

Eight rendered clients each completed 37,800 ticks with recording, 100 ms RTT
and 2% packet loss. All passed, reported no capture/playback error and saved two
valid clips each. The host simultaneously ran an emulator, builds and diagnostics:
clients achieved only 48.2–49.5 callbacks per wall-clock second. This is functional
soak coverage, **not** a stable high-refresh performance pass. The JSON records
per-client simulation summaries and every rate-limited spike report.

A three-version comparison ran eight rendered clients for 5,400 ticks each,
with the first 600 excluded from measurements. Settings were TEST ARENA, 400×300,
100 ms RTT, 2% loss, client/server recording and a requested 120 FPS cap. The
network harness itself schedules at 60 Hz. Identical instrumentation brackets
its render callback, including simulation, drawing and SwapBuffers but excluding
the pacing wait. Values below are means of the eight client summaries; p99 is
not a pooled percentile and these are not display-interval FPS measurements.

| Callback measurement | v0.1.11 | Main 55e78f4 | Branch 1b8b286 |
| --- | ---: | ---: | ---: |
| Mean work (ms) | 17.45 | 20.37 | 20.17 |
| Mean client p99 (ms) | 63.37 | 71.01 | 75.17 |
| Mean client p99.9 (ms) | 70.09 | 82.40 | 84.72 |
| Bytes/callback | 68,677 | 163,555 | 72,501 |
| Gen0 collections/client | 40.5 | 89.4 | 34.5 |

These are instrumented archived source builds, not untouched release binaries.
The v0.1.11 harness needed a test-only GL context change from 3.2 compatibility
to macOS-compatible 2.1 to start. Its eight clients completed the run but failed
historical combat coverage assertions; main and updated passed. Runs were
sequential on a shared host with other work active. Allocation falls about 56%
versus main, but aggregate tail timing does **not** improve. This comparison
cannot establish restored v0.1.11 frame consistency; that acceptance remains open.

The Android CI APK from run 35830812016 was installed on an API-35 arm64 emulator.
Live network killcam/HUD, background/resume, full recording, instant clip saving,
and playback of the resulting clip to EOF work. Both Android-generated files
validate on desktop. SwiftShader shows visual banding/noise, so this is not GPU
quality or physical-device performance evidence. Testing exposed a retained
Replay Studio preview referencing a disposed bitmap after detachment. The fix
clears its Image source before disposal and reloads on reattachment, with a
regression in the headless UI suite. A fresh APK lifecycle retest is still required.

Remaining acceptance: controlled frametime/FPS-distribution comparisons, the full
scenario matrix on physical high-refresh displays and physical Android devices.
Checkpoint capture and initial scene creation/restore remain owner-thread work;
startup/JIT spikes remain visible. The optional 1 ms player budget limits
reconstruction steps, not indivisible creation/restore. No result here proves
all stutters eliminated.

## Editor

Run `dotnet run --project tools/map-editor-check -c Release -- --benchmark`.
The 1,000-object fixture reproduces target-main's serialized dirty check and
clone/compare/replace transform baseline. Transform timings exclude viewport
subscriptions. Selection/full/partial rebuilds measure the CPU authoring cache.

| Operation | Baseline ms/op | Current ms/op | Current allocated/op |
| --- | ---: | ---: | ---: |
| Dirty check | 3.108 | <0.001 | 0 B |
| One-object transform | 42.945 | 0.005 | 3,876 B |
| Selection invalidation | 7.685 | <0.001 | 392 B |
| Full CPU geometry rebuild | — | 8.738 | 5,885,085 B |
| One-object CPU geometry rebuild | — | 0.124 | 63,096 B |
| Undo + redo | — | <0.001 | 672 B |
| Save 1,000 objects | — | 12.270 | 5,142,495 B |

The synthetic-texture runtime compile took 13.806 ms on a cache miss and 0.306 ms
on a hit. Navigation on an 80×80 floor took 10.439 ms and produced 100 nodes.
The synthetic fixture requires no cartridge assets.

The rendered DUST2 viewport fixture (11,056 render faces, 1,902 collision faces)
measured 1.453 ms GPU draw including driver completion, versus 51.603 ms CPU
Avalonia polygon rendering. GPU upload was excluded. Camera, selection and
overlay changes reused GPU meshes; one native-object move uploaded one mesh.

Map Studio's Statistics inspector exposes geometry/selection/entity/collision/
navigation counters, history bytes/count, build fingerprint/duration/hit status,
queue sharing and compiler-cache retention. Replay network diagnostics expose
session/frame/interpolation, seek source/work/time, timeline age/bytes/records,
restore count and killcam status. These are local diagnostics, not telemetry.
