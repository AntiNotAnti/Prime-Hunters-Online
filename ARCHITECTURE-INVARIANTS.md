# Architecture invariants

This file is the short, machine-oriented source of truth for architectural assumptions that must survive refactors.

**Current code and tests outrank prose.** If this file disagrees with implementation, verify the implementation first, fix the documentation in the same change, and do not revive historical behavior merely because an older design note describes it.

## Network protocol

- The current wire protocol is **16** (`NetConfig.ProtocolVersion`).
- Protocol mismatches are refused during the Hello handshake. Do not make incompatible wire or simulation changes without a protocol bump.
- Dated protocol 6/7/8 measurements in `.claude/` are historical A/B evidence, not the current architecture.

## Authority and simulation

- Normal online matches are **server authoritative**.
- A normal player is never the simulation authority. Standalone dedicated servers simulate the match themselves.
- "Host on this computer", directory-hosted games and regional/overflow hosted games run each match in an **isolated dedicated-server process** so every match gets its own static `NetSession`.
- `PacketType.Authority`, `RunsTheMatch = false`, `NetSession.StartHost` and client-authority handover code remain only for compatibility/tests/legacy paths. Do not route normal launcher hosting through them.
- The server is authoritative for combat, health, score, match state and match end. Player movement position is still supplied by the owning client's `IntentPacket.Position`; this is not a fully server-derived movement model.
- A dedicated game server requires the user's extracted game data and a valid `paths.txt` beside the server binary. It must refuse to start rather than silently fall back to client authority when those files are unavailable.
- The directory/master server does not simulate a match and does not require game files.

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

- Replay format v3 is protocol-bound and validated before playback.
- Authoritative dedicated servers record canonical replays when enabled.
- Client replay recording, rolling clip capture, Replay Studio, checkpoints, highlight derivation and deterministic frame export all exist. Do not describe replay/clip recording as a future feature.
- Replay playback must not open a live gameplay socket or mutate the recorded session through reconnect/authority control packets.

## Updates and release packaging

- Source, tags and public release binaries live together in `AntiNotAnti/Prime-Hunters-Online`; clients use that repository's anonymous GitHub Releases API as the update source.
- One-click Windows/Linux/Android installs require a GitHub-provided SHA-256 asset digest before executing/replacing files.
- Android in-place updates additionally require the release APK signer to match the installed app.
- macOS opens the release page instead of replacing files inside the signed app bundle.
- Dedicated servers update only at a safe lifecycle point.
- Release/server documentation must never claim that a dedicated **game server** needs no game files.

## Naming

- The public product name is **Project Prime**.
- Desktop binaries/packages use `ProjectPrime` / `ProjectPrimeServer`; Android uses `com.projectprime.game`; macOS uses the **Project Prime** bundle identity.
- The GitHub repository currently retains the legacy `AntiNotAnti/Prime-Hunters-Online` slug as an infrastructure locator for releases and updates.
- The C# root namespace remains `MphRead` to minimize upstream merge churn.

## Documentation policy

- Current-behavior sections must describe the current code, not the build in which a feature was introduced.
- Dated measurements and old protocol comparisons are valuable, but must be labeled **historical** when the architecture they measured is no longer current.
- `KNOWN-GAPS.md` contains only unresolved/unverified items. Move fixed items out instead of leaving them as warnings.

## Replay timeline migration

- Rolling timeline records own immutable payload copies and evict whole restore segments.
- A dropped fact invalidates its dependent continuation; clips must not cross gaps.
- Protocol network baselines are explicitly not complete replica-scene checkpoints.
- Replay readers and transport scheduling belong to session instances. Passive hosts own their decoded lifecycle state and never access live NetSession. The foreground theatre host alone bridges legacy process services.
- Each scene owns its player registry, match state, random streams, camera sequences and enemy/platform beam pools. Legacy static facades refer only to the foreground scene. Replica construction and cleanup never rebind those facades. Replica simulation remains gated until its network, presentation and resource services are isolated.
- Exact kill markers fence match, authority, event, server tick, occupant generations and victim life. Ambiguous cumulative deaths are not exact kill candidates.
- Keep current replay/Studio/killcam paths until isolated scene and runtime acceptance gates pass. See `docs/architecture/replay-map-upgrade-status.md` for remaining work.

## Map Studio ownership

- Dirty state is a document state-ID comparison, not project serialization or history depth.
- Common transforms and edits use bounded delta history; undo-to-save and branching preserve state identity.
- Selection, entity edits and camera movement do not rebuild unrelated geometry.
- Build workers receive detached snapshots; cancelling one waiter must not cancel shared work.
- Runtime cache publication validates content fingerprints and output integrity. Cache files contain locally generated content and are never release inputs.
