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
- Move PlayerEntity registries, GameState, RNG, scene services and presentation
  ownership out of process globals before creating simultaneous replay scenes.
- Instance-owned passive/theatre playback and deterministic bounded seek.
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
