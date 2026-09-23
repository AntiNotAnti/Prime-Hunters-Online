# Netcode modernization evidence

Baseline: main 427199c, protocol 16, .NET 10.0.401 on macOS arm64.
The supplied plan's reviewed commit was c4cbf9b; main now also includes the
replay/map migration. The implementation branch was subsequently rebased onto
main 1a2863d (including both input-edge fixes). Owner movement, full snapshots and current replay ownership
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

The asset-backed runs use eight real hidden OpenGL clients and an authoritative
server, with the existing gameplay script, native collision/damage and replay
capture. Impairment is locally injected; this is not geographically separate WAN
validation or human gameplay-feel review. Human gameplay-feel acceptance remains
external to these automated checks.

`tools/nettest/run-assets.py` stages binaries and only the configured paths file;
it reads existing extracted game assets without copying or downloading them.
For example, after building Release:

```sh
python3 tools/nettest/run-assets.py --game-data /path/to/game-data --out /tmp/net-assets

dotnet run --project tools/nettest/nettest.csproj -c Release -- \
  --combat-scene /path/to/game-data
```

The first command runs LAN for five minutes and each impaired arm for two minutes;
`--scenario severe --seconds 300` extends the severe coverage. Output includes
per-client logs, server logs, exit statuses, profiles and a JSON summary. The combat
scene check passed 98 assertions through production spawning, damage and lifecycle
paths, including Omega. No proprietary assets are checked in.

The initial two-minute severe arm failed because seven observers never saw slot 0
take authoritative damage. The local owner's three reported damage events were
predicted locally; the session damage replay counters for that slot remained zero.
That failed run is retained, and extended coverage is reported separately. A
longer passing run must not be presented as making the initial result disappear.

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


## Final regression verification

All 12 network commands passed after the loaded-scene/replay fixes: architecture,
allocations, protocol17, reliable, queue-budget, load-lifecycle, lagcomp-shadow,
weapon-policy, transport-stress, lifecycle, health-shots and network-benchmark.
The suites include 2,912 real-UDP lobby assertions, 3,680 lifecycle assertions,
3,338,728 health/shot assertions (7,776 weapon/profile cases), 2,352 shadow profiles,
and 1,620 weapon policy profiles. Extended codecs covered 2,016 scenarios. Replay
timeline/format suites passed 36/703 assertions; architecture now also initializes
the actual checkpoint schema so a removed reflected field cannot hide behind those
format-only checks. The new replay fingerprint correctly rejects older incompatible
world capsules. The homing target retains its existing serialized backing field.

The final rebase changed only upstream pointer handling. Release build, pointer
regressions, architecture and the 98-assertion asset-backed combat check passed
after that rebase. The five-minute severe rerun uses this build. A subsequent transport-only fix
makes ordinary reliable queue exhaustion disconnect visibly; the complete network
suite is rerun for that change, including a production transport saturation test.

`network-v17.json` records the final codec benchmark alongside both protocol-16
baselines. Smoke allocation totals fell from 67,568–300,736 bytes before P0-B to
368–32,832 after it; protocol 17 measures 480–32,944 including harness queue setup.
Do not interpret these scenario totals as bytes per packet. The separately warmed
hot-path tests measure 0 B/op. Historical benchmark commit IDs identify the actual
pre-rebase measurements, rather than claiming a new run on a rewritten commit.

Wall-clock server startup/GC/render contention is reported, not hard-gated as
virtual-time tick loss. Deterministic tick/queue/packet bounds remain hard gates.
Shadow would-clamp metrics are collected without changing damage; unsupported
geometry is explicitly unavailable, never counted as agreement. The rendered
script does not guarantee every weapon/charge/headshot/respawn combination in each
arm, so the focused combat and lifecycle checks remain part of acceptance.


## Rendered measurements

All five profiles have a passing eight-client run. The severe arm required the
five-minute coverage run; its initial two-minute failure is retained below and in
`tools/nettest/baselines/rendered-v17.json`. The failure is consistent with sparse
scripted damage coverage, but a longer pass alone does not prove the short-run
behavior cannot recur. The original assertions were not weakened.

| Profile | Scripted seconds | Client reports passed | Last server steps | Mean step ms | Dropped ticks | Queue high / drops |
|---|---:|---:|---:|---:|---:|---|
| lan | 300 | 8/8 | 19799 | 0.46 | 1 | unavailable |
| moderate | 120 | 8/8 | 7199 | 0.52 | 1 | 16 / 0 |
| poor | 120 | 8/8 | 7199 | 0.47 | 1 | 30 / 0 |
| severe-initial | 120 | 1/8 | 7195 | 0.59 | 5 | 33 / 0 |
| mixed | 120 | 8/8 | 7198 | 0.56 | 2 | 18 / 0 |
| severe-extended | 300 | 8/8 | 19799 | 0.53 | 1 | 53 / 0 |

Every run recorded zero simulation failures, zero server stalls and zero remote
position snaps. Server step samples cover wall-clock time, including startup and
client exit, so their counts need not equal scripted client frames. The severe
extended run reached the 45-step catch-up bound with zero truncations. Its shadow
summary and unavailable geometry counts are preserved in the JSON. Mean server
step cost stayed below 1 ms; occasional dropped wall-clock ticks are not hidden.
Human gameplay-feel acceptance remains before calling the entire release
acceptance complete. The injected profiles do not establish behavior on every
geographically separate Internet route.
