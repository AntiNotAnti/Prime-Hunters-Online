# Map Studio and custom-map projects

Map Studio is a desktop authoring surface in the existing single GLFW window. Open **Map Studio** on the front screen or use `-mapstudio`. Android and dedicated servers share validation, compilation, assets, packages, and transfer; they do not host the editor viewport.

## Ownership and source compatibility

`MapDocument` owns a detached `MapProject`. Editor commands replace snapshots; background operations receive separate snapshots. Geometry compiles into `BuiltFace`, then the established `BuiltMap -> MapPacker` pipeline writes ordinary MPH model, animation, collision, entity, and node files. MapGen has no Avalonia dependency. The lightweight viewport does not pack collision or regenerate navigation during a drag.

Missing `FormatVersion` means v1. Discovery never upgrades or rewrites old recipes. New templates use v2, a persistent `MapId`, runtime `Name`, optional `InGameName`, author, version and description. **Upgrade project** assigns identity in memory; Save persists it. Names are limited to 40 ASCII letters, digits, spaces, underscores and hyphens, with filesystem device names rejected. Built-in room keys cannot be replaced.

The v1 material/brush/spawn/pad/item fields stay compatible. V2 adds Box, Wedge, Prism and closed ConvexBrush geometry with a `kind` discriminator, normalized quaternion transforms, UV defaults and optional face overrides, editor labels/layers/visibility/locking, team spawns, mode capabilities, audio and assets. Initial entity classes share `MapEntityDefinition`; the existing typed lists remain the serialized compatibility contract.

## Authoring workflow

1. Create Blank Arena, Simple Box Arena or Team Arena, or import a BSP/PK3 through the Q3 wizard.
2. Select objects in the hierarchy or viewport. Shift selects multiple objects. RMB orbits, MMB pans, wheel zooms, WASD/QE moves, and F frames the selection. Top/front/side views provide orthographic editing.
3. Use Move, Rotate or Scale, axis handles and numeric properties. Snapping settings expose position, angle and scale steps plus local/world transforms. Defaults are 0.25 units, 15 degrees and 0.25 scale.
4. The inspector edits environment, geometry, entities, UVs, named materials, textures and music. Game materials show their source, name, dimensions, format and thumbnail. Custom textures are baked to a single-texture `.tex` pack.
5. Validate for full geometry/source/budget checks. Basic source validation runs after 500 ms of inactivity. Problems select their associated object. The Navigation overlay builds the bot graph on demand, colors connected regions and shows directed routes. Explicit traversal links connect nearby walkable nodes; they do not create physical teleporters or platforms.
6. Save writes a JSON project atomically. Save As from a package materializes only referenced assets. Build writes runtime files and registers the map for Play without restarting. Replacement invalidates the room model/collision caches while retaining old GL lists until scene cleanup. Build .ppmap captures the viewport preview and produces a portable package. Play from here uses an isolated room snapshot and one camera-position spawn; return to the launcher to resume editing. Run map test runs the existing 22-second, eight-player bot/gameplay/render audit in a child process and reports its actual exit status.

Undo/redo retains 50 commands. A drag is one command. Autosaves occur after three seconds of inactivity in `maps/.autosave`, with source context stored beside the recovery JSON. Opening an existing project offers Restore/Discard/Inspect; Library > Recover unsaved also finds never-saved documents. Autosave never overwrites the source. Delete and unsaved-discard actions require confirmation.

The library refreshes its own catalog and shows previews, metadata, source type, diagnostics and build status. Package and source precedence is identity-aware. Background jobs lock editing, accept cancellation, and publish UI results on the UI thread. Existing Q3 conversion/packing calls finish their current stage before cancellation is observed; audit cancellation terminates only its child process.

## Validation and reproducible output

`MapOutputSet` owns all five filenames. A missing output, damaged output, changed source/import/texture/asset, schema or compiler setting makes the build stale. `map.build.json` records SHA-256 fingerprints and output hashes. Every output is prepared before publication; the sidecar is written last, so interruption causes regeneration on the next launch.

Diagnostics carry stable `FP-MAP-*` codes, severity and optional object ID. Validators check geometry transforms/ranges/topology, materials/assets, spawn placement/headroom, multiplayer pickups, jump-pad settings, modes, imported dependencies and collision budgets. Runtime limits produce warnings at 70%/90% and errors at 100%. `-mapinspect` also generates navigation and reports node/edge budgets.

Closed convex brushes must enclose a nonzero volume; a flattened tetrahedron is invalid even when every edge belongs to two faces. Relative import and texture paths resolve beside the project before the working-directory and library fallbacks, so an unrelated same-named file cannot change the map being compiled or packaged.

```text
ProjectPrime -mapvalidate "TEST ARENA" -mapdir maps
ProjectPrime -mapinspect "TEST PADS" -mapdir maps
ProjectPrime -mapbuild "TEST ARENA" -mapdir maps -out build-map
ProjectPrime -mapbundle all -mapdir maps
ProjectPrime -mapstudio
```

Validate/inspect do not require launcher setup for native maps or imports with their own texture packs. Runtime builds using borrowed materials require the user's extracted game files. Validation cannot verify a borrowed material index against unavailable cartridge data; packing checks it before replacing any output.

## Packages and multiplayer

V2 `.ppmap` is a ZIP containing `manifest.json` and `project.json`; import, texture, audio and preview directories are optional. Native maps need no BSP. Legacy one-recipe packages still load. Imported packages carry a trimmed `import/level.bsp` and baked `textures/map.tex`. Packaging copies only declared map assets, never extracted game audio or borrowed cartridge textures.

Entries are sorted, timestamps fixed, and the canonical content hash includes each path, length and bytes except the manifest. A separate archive SHA-256 can verify the exact package file. The reader bounds compressed size to 128 MiB, expanded content to 256 MiB, individual entries to 64 MiB, projects to 8 MiB and entries to 2048. It rejects traversal, rooted/device paths, duplicates, unsupported types, inconsistent identity, missing assets and hash mismatches. Files are read from the archive rather than extracted over the library.

Device-name checks apply before the first dot in each path component on every platform, including multi-extension names such as `CON.backup.tex`.

The package reader/installer are shared infrastructure for future map distribution, but this port does **not** enable PR #53's old protocol-8 network-transfer layer. Project Prime's multiplayer protocol has evolved since that PR, so automatic server-to-client custom-map delivery must be integrated separately against the current networking stack rather than reviving the old packet/version changes. Local Map Studio build/package/install flows remain fully verified and atomic.

## Verification and current limits

`dotnet run --project tools/mapcheck -c Release` exercises migration, all-output invalidation, content hashes independent of timestamps, malformed/bomb packages, identity, primitives and outward normals, collision/entity readback, custom textures, package Save As, undo/redo, recovery, navigation and transfer framing/retries. `--fixtures DIR` writes a synthetic arena for `-maptest`; no proprietary data is included. `--join PORT` is an opt-in private loopback client requiring a local server and paths.txt in the test output directory. `-mapstudioshot DIR` captures 1440x900 and 960x600 editor layouts.

The viewport uses shaded authoring polygons, not runtime textured lighting. Imported architecture is read-only; imports expose settings and editable authored entities, and reject added native primitives rather than silently dropping them. Convex brushes support numeric transforms and serialized vertices/faces; full vertex/edge modeling, reusable prefabs, CSG and scripting are outside this first editor. Spawn weighting and per-mode entity filtering are not yet runtime features. Mode capabilities reject unsupported objective modes instead of producing nonfunctional matches. Custom audio uses the existing player with volume/loop/pause support; decode failure falls back to room music.

Large maps still use the proven single-room-part output. A partitioning follow-up must split render groups into spatial cells while preserving collision plane/index ownership, bot graph connectivity, RoomMetadata node names, remote-player NodeRef mapping and portal visibility. Gate it with the existing all-spawn render sweep, node agreement probes and DUST2 regression before enabling it. The current compiler budgets provide the guardrails; no alternate runtime map engine is introduced.
