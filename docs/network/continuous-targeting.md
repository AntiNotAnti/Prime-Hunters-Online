# Protocol 19 continuous player targeting

Shock Coil now uses the owner's production target decision. The receiver validates
that particular player and either accepts it or selects no player. It never runs a
second player competition after rejection. Offline selection, bots, other beams,
non-player candidates and the cartridge cone formulas remain in their normal paths.

## Wire and capture

`IntentPacket.Size` remains 88. The state tail is now eight bytes and `FullSize` is
96 (120 with the transport envelope). Offsets 88–90 carry charge, boost damage and
shot flags. Offset 91 carries the encoded slot, 92–93 its generation, and 94–95 its
life, with ushort values little endian. `0x80/0/0` explicitly means no player;
`0x81` through `0x88` identify slots zero through seven. A player identity requires
nonzero generation and life. An absent or malformed decision cannot trigger an
independent player selection. Live ingress requires the complete v19 tail. Earlier
protocol peers and recordings are refused; the v18 packet format was not extended
silently. Charged affinity Volt Driver uses the same `NetTargetIdentity` structure
and preserves its one-release selection flow with the added lifecycle fence.

Shock Coil captures the actual result of `CheckHomingTargets`; it does not rerun a
selector in `CaptureIntent`. Normal input/shot state is prepared before simulation.
For held Shock Coil, dispatch is deferred until after the production selector has
filled the target. Aim, shooter position and the displayed ACK are latched at that
same evaluation, before movement can change them. A frame without a spawn reports
explicit none. These remain normal unreliable intents, repeated every send interval.

## Authority validation and retention

The authority checks match/authority stream, owner generation/life, input age,
weapon/held state, target slot/generation/life, in-play/spectator/targetable state,
team eligibility and the shared targeting predicate. During `BeginShot`, it uses
the exact fractional, ceiling-limited historical world already chosen for the shot.
The existing `HistoricalPlayerPose` supplies position, collision form and Kanden
chain geometry. History additionally records morph eligibility, team and targetable
state. Missing historical identity/pose fails closed.

The shared predicate keeps the continuous selector's unusual range behavior:
there is **no hard range cutoff**. Distance tightens the angular threshold toward
4094/4096. Alt/morph targets additionally require 4006/4096. No cone, radius,
damage, ammo formula, affinity bonus, DPS, ramp, rewind ceiling or simulation rate
was changed. Equal player angles resolve by the lowest slot, independent of entity
traversal order. Non-player candidates keep the existing selector behavior when no
synchronized player was accepted.

`ContinuousTargetState` records None/Acquired/Held/Lost/Changed, acquisition frame,
last validation and last report. Repeated reports revalidate the same identity;
A→B rejection drops A. Explicit none drops the player immediately. The existing
ordered intent ingress rejects duplicate and older reports, including wrapping
frames. The latest accepted report can be reused only through the continuous
phase's existing 30-frame freshness limit. Lifecycle ownership clears state on
slot/life/session/room boundaries; input observation clears it on release, weapon
change, death and stale input. Target identity is checked again before beam
processing. Replay schemas and generated accessors include the new value state.

## Two timing faults exposed by traces

1. `TryApplyRemoteInput` restored the displayed puppet read point, but the subsequent
   lifecycle `ApplyRemoteStates` pass wrote the newest snapshot pose over it.
   Targeting could therefore run about one smoothing buffer ahead of its ACK.
   After lifecycle adoption, the existing presentation restore now runs again
   before local targeting. Smoothing parameters and movement authority are unchanged.
2. Dedicated input arrives before the server advances its frame. Seeding the
   continuous clock with the raw arrival age counted that enclosing step as an
   additional owner tick. Initial seeding now removes that one step on the dedicated
   path only. Freshness still uses the unmodified age, and held clocks still advance
   monotonically without jitter-driven re-anchoring. Golden damage/ammo cadence is
   unchanged. Jitter can still associate a newer/older report with a different held
   firing phase; this is measured rather than concealed.

## Diagnostics

`NetContinuousTargetDiagnostics` keeps a 4,096-entry bounded value ring. Recording,
selector validation and retention allocate nothing after warmup. Entries contain
role, owner/frame/lifecycle, launch key, logical phase, reported/selected/previous
identity, selector geometry, historical form/eligibility, rejection reason, ramp
timer, collision geometry, overlap, damage gate and attempted amount. Collision
amounts describe the beam's damage call, not a second ledger of confirmed health.

`continuous target:` logs evaluations, reports, acceptance, explicit none, rejection,
identity agreement, owner-only/authority-only/different/lifecycle and rejection
counts. First rejection per active firing burst is identified. Set
`PRIME_CONTINUOUS_TRACE=/absolute/path.jsonl` in diagnostic runs to export the
bounded ring. Headless clients export on completion; dedicated servers export with
netlog snapshots. Formatting/export is opt-in diagnostic work and is excluded from
zero-allocation target-validation measurements.

`tools/hitrig/compare-continuous.py` pairs owner evaluation frames with authority
report frames under the same owner generation/life. It reports unpaired records
explicitly, accepted-decision agreement, phase offsets, rejection reasons, and the
first disagreement in each retained firing burst. It distinguishes identity,
lifecycle, eligibility, angle, form, collision, phase and damage-gate disagreements.
Repeated authority evaluations are evaluations, not unique player decisions or hits.

## Reproduction

```sh
dotnet run --project tools/nettest -- --continuous-targets
dotnet run --project tools/nettest -- --continuous-scene /path/containing/paths.txt
dotnet run --project tools/continuous-phase-check
python3 tools/hitrig/run-continuous.py --runtime /path/to/staged/runtime \
  --data /path/containing/paths.txt --output /tmp/coil --seconds 30
python3 tools/hitrig/run-continuous.py --runtime /path/to/staged/runtime \
  --data /path/containing/paths.txt --output /tmp/coil-eight --players 8 \
  --modes shockcoil,shockcoil-cluster,shockcoil-morph,shockcoil-all \
  --profiles rtt320-loss2 --seconds 40
python3 tools/hitrig/compare-continuous.py /tmp/coil --output /tmp/coil-pairs.json
```

The two-player matrix crosses 0/100/250/320/400 ms with 0/1/2/5% loss, paired
jitter 0/20/40/80/80 ms, 3% reorder and 1% duplication. Synthetic stream tests also
cover a 400 ms pump stall, lost target-change/loss reports, duplicate/reordered
acquisition, fresh reacquisition, and delayed lifecycle mismatch. Scene fixtures
cover every hunter/form, morph boundaries, target switches, explicit none, reversed
entity traversal at equal angles and warmed production validation allocations.

## Measurement and acceptance

See the checked-in continuous-v19 validation evidence for per-arm results. Initial
runs are retained separately from the corrected timing runs; a passing rerun does
not erase an earlier failure. The first corrected 30-second LAN arm had 151 locally
predicted hits, all 151 confirmed, and zero unpredicted hits. Raw server log totals
include the trailing interval after the owner exits and must not be equated to the
owner's shorter prediction window.

Accepted target identity agreement is separate from rejection rate and hit
agreement. In the corrected 320 ms/80 ms/2% loss arm, paired accepted identities
matched 100%, but 147 confirmed and 73 unpredicted hits imply only 66.8% local share
of authority-confirmed hits. That does **not** meet the plan's 95% impaired hit-count
criterion. Phase offsets dominate retained paired disagreements. The work therefore
provides the targeting implementation and classified evidence, but does not establish
full gameplay acceptance under impairment. Timer replication remains unjustified;
the next investigation is continuous firing-phase/collision/claim correspondence.
The former turret-state gap is addressed by the coordinated Protocol 19 combat
work. An eight-player TEST PADS impairment run completed; human eight-player
gameplay remains distinct from these automated runs.
