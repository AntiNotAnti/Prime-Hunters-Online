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
leave geometry caches alone. The CPU drawing backend is retained; migration to
the game renderer is still outstanding.

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

Map Studio runtime Build and Playtest use this scheduler. Validation/navigation,
package generation and existing synchronous server preparation still use their
current detached compiler paths; unifying those consumers remains outstanding.
The cache is per-user and is not part of release packaging.
