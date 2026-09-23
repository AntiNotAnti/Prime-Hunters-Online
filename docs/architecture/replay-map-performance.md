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
