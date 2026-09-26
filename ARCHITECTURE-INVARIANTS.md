# Architecture invariants

This file is the short, machine-oriented source of truth for architectural assumptions that must survive refactors.

**Current code and tests outrank prose.** If this file disagrees with implementation, verify the implementation first, fix the documentation in the same change, and do not revive historical behavior merely because an older design note describes it.

## Network protocol

- The current wire protocol is **24** (`NetConfig.ProtocolVersion`).
- Protocol mismatches are refused during the Hello handshake. Do not make incompatible wire or simulation changes without a protocol bump.
- Dated protocol 6/7/8 measurements in `.claude/` are historical A/B evidence, not the current architecture.

## Authority and simulation

- Normal online matches are **server authoritative**.
- A normal player is never the simulation authority. Standalone dedicated servers simulate the match themselves.
- "Host on this computer", directory-hosted games and regional/overflow hosted games run each match in an **isolated dedicated-server process** so every match gets its own static `NetSession`.
- The client-authority fallback is removed. `DedicatedServer` has no `RunsTheMatch=false` mode, no player-authority slot/handover, and clients cannot publish authoritative snapshots or match results. `PacketType.Authority = 12` remains reserved only so wire IDs do not shift; current servers never send it and current clients ignore it.
- The server is authoritative for combat, health, score, match state and match end. Player movement position is still supplied by the owning client's `IntentPacket.Position`; this is not a fully server-derived movement model.
- A dedicated game server requires the user's extracted game data and a valid `paths.txt` beside the server binary. It must refuse to start rather than silently fall back to client authority when those files are unavailable.
- The directory/master server does not simulate a match and does not require game files.

## Netcode modernization boundaries

- Movement remains owner-reported through IntentPacket.Position; the server
  validates lifecycle/order and resolves combat/world state at 60 Hz. Protocol 20
  additionally relays controller MoveX/MoveY magnitude so puppet simulation,
  animation, and replay do not reconstruct analogue movement from digital bits.
  Protocol 21 additionally carries the owner's exact continuous firing tick so
  Shock Coil cadence is not reconstructed from packet arrival timing. Protocol 22
  assigns a spare shot-state bit to Samus' active morph-ball boost so the authority
  uses the owner's exact ram state/damage instead of reconstructing it from delayed input.
- Preserve `AckFrame`, `AckSubFrame`, eight rising-edge frames, MatchId,
  AuthorityEpoch, SlotGeneration and LifeId. Snapshots are independently decodable
  full states; remote presentation continues to use NetSmoothing.
- Never restore movement command streams, movement ACKs, prediction histories,
  reconciliation/rollback/input replay, server-derived owner movement, velocity
  reconciliation, defender-aware rewind or protocol-18 movement semantics.
- Never restore IntentBundle, observer bundling, snapshot keyframes/deltas,
  baseline reconstruction or damage sidecars from reverted PR #28.
- P0 wire optimizations must remain byte-identical to protocol 16. The protocol-17
  envelope/reliability/queue/start changes form one unreleased migration train.
- Spawn placements are lifecycle operations, not movement reconciliation.
  Same-life snapshots never correct the local owner collision body or velocity;
  the architecture suite exercises prolonged, extreme position disagreement.
- Scratch buffers belong to their session/server owner. Synchronous sends consume
  their spans before returning; delayed sends and replay records must own copies.

## Hit registration

- `NetUnlagged` rewinds authoritative hit resolution to the world the shooter acknowledged.
- `NetHitPrediction` is enabled by default for a client's own outgoing hits. Local feedback and nonlethal damage may appear immediately and are reconciled against authoritative results.
- Incoming damage from other players is not predicted locally.
- A predicted lethal hit on another player is currently held at 1 HP until the authority confirms the death. Self-damage/self-death may resolve locally because source, target and input are local.
- `NetHitClaims` lets the shooter declare locally resolved hits the authority did not independently resolve. Claims are lifecycle-, time-, geometry-, launch- and damage-bounded and receive explicit verdicts.
- Predictions never author the durable scoreboard or match result. Authoritative snapshots/state do.
- Imperialist headshots are reconciled authoritatively: a validated shooter headshot paired with the authority's body hit applies only the missing damage difference, exactly once. The optional unscoped penalty does **not** halve Imperialist headshot damage.
- Headshot HUD feedback must not be emitted from an unconfirmed speculative client result.

## Lobby and match lifecycle

- Persistent lobbies keep their socket/session across matches.
- Lobby match configuration is preserved across rematches unless the owner changes it.
- Post-match map selection can continue directly into the next match without requiring another Ready cycle.
- Map/rematch transitions must rebuild the room and send `MatchLoaded`; the server's Starting barrier releases when expected participants load or the bounded timeout expires.
- Only a launcher-authenticated owner token may terminate a locally spawned lobby server process. Ordinary ownership of a persistent dedicated lobby cannot kill the daemon.

## Timing and rendering

- Gameplay simulation is fixed at **60 Hz**.
- Render/presentation rate is independent of simulation rate and may run at the display rate or another configured cap.
- Do not make gameplay/network behavior depend on render frequency.
- Resolution scaling affects the 3D render target; HUD/UI remain presentation-space.

## Replay and clips

- Replay formats v3/v4 are protocol-bound and validated before playback.
- Authoritative dedicated servers record canonical replays when enabled.
- Client replay recording, rolling clip capture, Replay Studio, checkpoints, highlight derivation and deterministic frame export all exist. Do not describe replay/clip recording as a future feature.
- Replay playback must not open a live gameplay socket or mutate the recorded session through reconnect/authority control packets.

## Updates and release packaging

- Source, tags and public release binaries live together in `AntiNotAnti/Project-Prime`; clients use that repository's anonymous GitHub Releases API as the update source.
- One-click Windows/Linux/Android installs require a GitHub-provided SHA-256 asset digest before executing/replacing files.
- Android in-place updates additionally require the release APK signer to match the installed app.
- macOS opens the release page instead of replacing files inside the signed app bundle.
- Dedicated servers update only at a safe lifecycle point.
- Release/server documentation must never claim that a dedicated **game server** needs no game files.

## Naming

- The public product name is **Project Prime**.
- Desktop binaries/packages use `ProjectPrime` / `ProjectPrimeServer`; Android uses `com.projectprime.game`; macOS uses the **Project Prime** bundle identity.
- The GitHub repository uses `AntiNotAnti/Project-Prime` as the canonical infrastructure locator for releases and updates.
- The C# root namespace remains `MphRead` to minimize upstream merge churn.

## Documentation policy

- Current-behavior sections must describe the current code, not the build in which a feature was introduced.
- Dated measurements and old protocol comparisons are valuable, but must be labeled **historical** when the architecture they measured is no longer current.
- `KNOWN-GAPS.md` contains only unresolved/unverified items. Move fixed items out instead of leaving them as warnings.

## Replay ownership

- Rolling timeline records are values retaining immutable pooled payloads and evict whole restore segments. Pending reconstruction, frozen clips and writer commands hold independent leases. Eviction, reset, clip disposal and command completion release them; budgets include pooled capacity. Quiet frames advance the frontier without a fact.
- A dropped fact invalidates its dependent continuation; clips must not cross gaps.
- Protocol network baselines are explicitly not complete replica-scene checkpoints.
- Decoder and animation checkpoint components own detached payloads. Decoder restore validates before mutation; animation restore resolves groups against the destination asset. A component alone must never be advertised as a complete world checkpoint.
- `ReplayWorldCheckpoint` combines those components with an explicit entity/effect/clock/link field contract. Its payload contains values, asset keys and construction anchors, never live references or native handles. Restore is restricted to an unpublished replica with matching room content and construction baseline. Unknown contracts/anchors fail closed. Resource binding does not call gameplay initialization.
- `PassiveReplayPlayer` owns a 64 MiB/128-entry checkpoint cache. File and frozen-world-clip seeks share its fixed stepping, with at most 120 steps per host update. The presented scene is replaced only after reconstruction succeeds; a frozen clip remains valid after its timeline is reset.
- Replay readers and transport scheduling belong to session instances. Passive hosts own their decoded lifecycle state and never access live NetSession. Normal Studio uses the private player too. A private legacy host exists only inside the format diagnostic; production has no alternate replay path.
- Each scene owns its player registry, match state, random streams, camera sequences and enemy/platform beam pools. Legacy static facades refer only to the foreground scene. Replica construction and cleanup never rebind those facades. Replica scenes use an explicit fixed-step entry, instance replication/lifecycle histories, silent sound routing and private HUD queues. They cannot author outgoing input or resolve combat. Replica mutable model/material/node/mesh state and room portal/collision activation are private. Immutable definitions may be shared; GL resources have explicit owner lifetimes.
- Exact kill markers fence match, authority, event, server tick, occupant generations and victim life. Ambiguous cumulative deaths are not exact kill candidates.
- Timeline intent baselines must match the recorded occupant generation and life. Submitted local input is presentation evidence, never accepted hit/damage authority. Versioned gameplay hashes include world/projectile state; animation/effect projections are checked separately.
- Effect checkpoint assets use effect ID plus element ordinal; element names are not unique. Match rules apply before room construction; timed objective targets and the recorded clock retain their mode-specific meaning.
- Live checkpoint production consumes immutable accepted facts in a canonical private replica. Pending facts are bounded; gaps/failure invalidate history rather than yielding incomplete clips. Capture and private scene resource lifetime run on the scene owner; network callbacks never allocate or dispose GL resources. History cannot claim knowledge of projectiles predating capture.
- Replay checkpoint capture stays on the scene owner: one bounded pooled buffer, reused graph storage and checked-in typed accessors; no mutable Scene enters a worker. Generated accessors must preserve reference-serializer bytes and remain compatible with Android AOT.
- Client/server recording compression and file I/O run exclusively in `ReplayWritePump`. Gameplay never waits for storage. Count/byte overflow aborts the recording while retaining recoverable chunks. Instant clips save frozen checkpoints/facts on a worker through v4 hidden lead-in without constructing a Scene. Optional player preparation is limited to 24 steps or about 1 ms/update; construction/restore stays on the scene owner.
- Personal/final killcam footage is up to 300 frames at 1x. Results remain 10 seconds; server intermission derives from the shared 5-second final camera plus results plus 1 second. Preparation cannot consume the final footage window.
- Production replay and killcams share the private player. Do not reintroduce a pose ring, live historical draw substitution, duplicate clip history or live-network replay smoother. Acceptance evidence is in `docs/architecture/replay-map-upgrade-status.md`.

## Map Studio ownership

- Dirty state is a document state-ID comparison, not project serialization or history depth.
- Common transforms and edits use bounded delta history; undo-to-save and branching preserve state identity.
- Selection, entity edits and camera movement do not rebuild unrelated geometry.
- The editor submits stable CPU meshes to the existing scene renderer. GPU resources belong to the viewport lifetime; selection, camera and overlay changes reuse them. The shared logical/pixel camera contract drives projection, world-ray picking and captures. UI overlays composite above geometry; normal frames require no GPU readback.
- Build workers receive detached snapshots; cancelling one waiter must not cancel shared work.
- Runtime cache publication validates content fingerprints and output integrity. Cache files contain locally generated content and are never release inputs.

- Runtime builds, validation, navigation and packaging share a bounded queue and private compiled geometry cache. Editor consumers receive immutable geometry or detached navigation; cached compiler graphs never escape to mutable UI state. Synchronous runtime/server preparation must not hold catalog locks while waiting on a worker.
- One dependency analyzer defines content identities and portable assets for fingerprints, packages, package reference checks and Save As. Cartridge dependencies affect builds but are never packaged.

- Replay killcams own a frozen timeline clip, passive player, scene, HUD and versioned audio lease. They never replace live players or send replay input. UI/Android skip callbacks queue requests; GL disposal stays on the scene owner. Respawn and match/epoch/occupant/life changes invalidate the presentation.

- Full client/server recordings and instant clips consume the shared accepted-fact recorder. V4 initial worlds, frame origins and hidden lead-in preserve exact clip starts; v2/v3 are supported by explicit adapters. Clip disk writes consume frozen values on a worker; GL world creation/disposal stays on the owner.
- Camera/player selection must not affect replay simulation RNG. Replica stepping fixes its simulation perspective and restores viewing state afterwards. Replay Lab is an explicit offline detach and cannot take over while a live connection exists.

- Replay pose lookahead is bounded and presentation-only; it never advances simulation/RNG or uses live receive jitter. Do not blend across occupant/life, spawn/death, form or teleport boundaries.
- Export stays at 60 Hz gameplay. A 120 FPS movie renders deterministic half-frame samples, never duplicate-frame conversion or 120 Hz physics. Camera paths and export collision anchors use recorded time. Native-size world/HUD targets belong to the scene and release on its GL owner.

- Optional protocol-16 packet 42 is a replay-only authoritative world extension;
  it never changes live gameplay. Its explicit bounded value schema and atomic
  fragment assembly must validate before recording. Replays apply it only inside
  private scenes; actor links fence occupant generation and life. New-server final
  killcams use the confirmed ending cause and exact kill identity.

- World capsules are explicitly versioned (current v2, v1 readable). V4 files may index bounded durable checkpoints; invalid optional entries fall back to valid reconstruction. New capture spools at most 4,096 checkpoints / 256 MiB compressed and never stores live references or native handles.
