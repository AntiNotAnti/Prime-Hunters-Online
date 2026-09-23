# Netcode modernization evidence

Baseline: main 427199c, protocol 16, .NET 10.0.401 on macOS arm64.
The supplied plan's reviewed commit was c4cbf9b; main now also includes the
replay/map migration. Owner movement, full snapshots and current replay ownership
remain the compatibility boundary.

## P0-A

- `--architecture`: independent protocol-16 intent byte fixture, owner-position
  adoption through the production bridge, full standalone snapshot decode and
  forbidden protocol-type guards.
- `--lifecycle`: 3,680 baseline assertions.
- `--health-shots`: 2,967,760 baseline assertions; 6,912 weapon/profile cases.
- `--network-benchmark --smoke`: 16 seeded production codec/impairment-queue
  scenarios. `--extended` covers the full 2/4/8 player, RTT, jitter, loss,
  reorder and duplication matrix. `--network-benchmark-json PATH` exports results.
- `tools/nettest/baselines/network-v16.json` records the pre-optimization run.
  Seeded delivery counts are reproducible; allocation totals include harness queue
  growth; timings are observations, not portable CI thresholds. Missing gameplay
  metrics are explicitly unavailable, never invented zeros.

## Owner movement invariant

The baseline contained an extreme-divergence owner correction (30 units for
60 updates). A separate behavior fix removes it; a functional production-bridge
test applies 180 divergent same-life snapshots and verifies position, previous
position and velocity remain unchanged. Lifecycle spawn placement is preserved.

## External validation

Asset-backed LAN/WAN, mixed-quality eight-player runs and human gameplay-feel
checks must be recorded separately from virtual-time codecs and headless tests.
A passing synthetic workload is not evidence that those checks were run.

## P0-B

Warmed 10,000-operation loops measured IntentPacket.Read at 56 B/op before
and 0 B/op after inline press history. Intent.Write, PlayerState.Read/Write,
snapshot compose/decode each measure 0 B/op. CaptureIntent copies inline values.
DedicatedServer keeps an owner-local snapshot buffer; replay still owns copies.
The byte fixture, full lifecycle/health suite and 16 benchmark cases pass.
`network-v16-optimized.json` records the post-change workload.

## P0-C

`NetTelemetry.Capture` returns value snapshots bridging transport, accepted and
rejected intents, snapshots, NetSmoothing, NetUnlagged and combat diagnostics.
Transport metrics belong to each transport and use atomic counters. Intent match
and session counters have distinct reset methods; life changes clear arrival/frame
baselines. Slot replacement resets occupant counters. Unmeasured ages/percentiles
are null. Legacy combat/rewind counters retain their producer's reset scope.
`-netdebug` prints the unified surface once per second. The benchmark consumes
`NetTransportTelemetry` too. No gameplay decision consumes diagnostic counters.

## P1-A

Protocol 17 uses an endpoint-bound random connection ID and 24-byte envelope.
Receive-window tests cover all 32 bits, duplicate/reorder/loss, exact 32/33 jumps,
uint wrap, stale IDs and wrong endpoints. Localhost UDP tests use the production
transport and validate admission, payloads, ACK-derived RTT and spoofed endpoints.
The lifecycle suite still passes with its loopback peer using the new transport.

## P1-B

Reliable events have independent event IDs, 32 ordinary + 8 reserved pending
slots, bounded exponential retries, 256-ID receive history and sender span guards.
The impaired delivery test applies all 40 events exactly once; realtime intent
traffic continues. Idle ACKs complete control delivery. Application revisions
remain authoritative. Exhaustion/expiry disconnects instead of hiding divergence.

## P1-C

Priority queues reserve lifecycle capacity, pump work is bounded, dedicated
background work follows simulation, and peer token buckets isolate intent floods.
`--queue-budget` covers a 10,000-packet burst and one abusive plus seven healthy
senders. Fault injection operates before transport ACK/dedup, with bounded
promotion. No unbounded receive drain remains in live transports.

## P2-A

A server-owned NetMatchStart freezes the participant set and fences load reports
by MatchId/AuthorityEpoch/StartGeneration. Preparation, loading, countdown and
InMatch are explicit. Missing participants are removed rather than admitted on a
black screen; late joins use individual readiness. Continuous rotation shares the
barrier. The broader existing lobby suite is now invoked by `--load-lifecycle`.
It exposed stale fixture assumptions about ready defaults, truncated test names,
replay error wording and the old packet budget. Fixtures now assert current behavior.
The full eight-player/56-health-spawn snapshot requires 1418 bytes with the transport
envelope, so protocol 17 uses the IPv4 Ethernet UDP limit of 1472.

## P2-B

Shadow plausibility uses ACK RTT, variance, recent minimum and fresh per-shooter
presentation-delay reports. Release rejects enforcement. Fractional existing
rewind remains unchanged; missing timing/history is explicit. Historical biped
Imperialist first-segment comparisons are read-only geometric diagnostics, not
second damage applications or full projectile outcome predictions.

## P2-C

Weapon policy resolves actual MP mechanics and charge flags, including charged
affinity ice-wave area timing. It preserves projectile catch-up for Imperialist
and new continuous beams. Fixed limits, early termination, missing-history stops
and shot/step/collision/truncation counters bound work. All 18 MP entries pass
1,620 timing-policy profiles. Health/shot impairment coverage includes Omega.

## Additional integration fixes

Production intent ordering and eight-edge recovery now handle uint frame zero
without reopening duplicate actions. A live ClientId cannot claim a new endpoint.
Pending Hello permits a server restart to replace the connection incarnation while
rejecting delayed Welcome packets for superseded IDs. Graceful shutdown retains
ACK/retry service for a bounded two seconds. Warmed connected UDP sends improved
from 72 B/op to 0 B/op by caching the connection's native address.
