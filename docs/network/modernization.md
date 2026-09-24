# Netcode modernization evidence

## Protocol 19 continuous targeting and combat telemetry (2026-09-24)

Implemented together with combat telemetry on `feature/net-v19-combat-telemetry`, including the historical
alt-form prerequisite from `fix/alt-form-lag-compensation`. Owner-selected Shock Coil
targets now carry generation/life identity, historical validation, explicit none,
lifecycle resets, bounded diagnostics and deterministic/real-scene tests. The same
identity serves charged Volt Driver. Intent payload increases from 92 to 96 bytes.

The paired traces exposed and fixed the post-input newest-snapshot overwrite of the
displayed world and a dedicated initial phase offset. Damage/ramp/cones remain
unchanged. Accepted target identities agree, but impaired hit-count acceptance is
still open; do not describe this as meeting the full 95% criterion. See
[continuous-targeting.md](continuous-targeting.md) for mechanics, commands and
measurement boundaries.


The coordinated follow-up adds Weavel turret fast/bootstrap state, stored CombatAck outcomes and immediate correction, absolute form mismatch episodes, and bounded anonymous telemetry. See [combat-telemetry.md](combat-telemetry.md).

## Protocol 18 enhancement (2026-09-24)

Implemented on `feature/net-v18-combat-lifecycle` from main
`d375d9c147da7cd49488d0e956be7fe5eeb4f45b`. The first five commits isolate the
claim, bootstrap, replication, input and geometry changes; the following commits
contain regression coverage and documentation. This section supersedes the
historical protocol-16/17 report below.

The simulation remains 60 Hz, rewind remains bounded to 45 frames with 128 frames
of history, and movement remains client-owned. Damage values, weapon policy,
remote hit prediction and smoothing tuning are unchanged.

### Updated-main compatibility

Rebased onto main `b59facfd39271cea94a92f077aef05042c97194a`, preserving its
hit-affliction provenance and authority fixes, hosting hardening, Adventure fixes
and launcher changes. Protocol 18 still intentionally requires matching clients
and servers; compatibility with main here describes source integration.

Integration testing exposed a same-match re-admission deadlock: a replacement
slot generation reused the previous MatchLoaded deduplication state. MatchLoaded
now includes slot identity and generation, and a new welcome reboots readiness
for the already-loaded scene. Duplicate welcomes for the same occupant preserve
readiness. Full roster/session retries also supersede obsolete revisions using
fresh event IDs, leaving room for bootstrap lanes during startup bursts without
changing command delivery or reliable queue bounds. Focused regressions cover
both paths, including delayed ACKs and 96 state revisions.

The compatibility manifest in `tools/nettest/baselines/compatibility-v18.json`
records the tested source and assembly, 19 passing network/asset suites, the
16-scenario smoke benchmark, replay and UI checks, and rendered integration runs.
The existing extended benchmark and six-profile results below retain their
original pre-rebase provenance. Gamepad/Adventure UI checks use an isolated user
data directory so an existing save does not trigger overwrite confirmation.

The final five-minute rendered extreme run (400 ms configured RTT, 80 ms jitter,
5% loss, 3% reorder, 1% duplication) passed all eight feature reports, with no
simulation failures, claim capacity refusals, ledger overwrites or input queue
overflows. Cross-observer alt-attack counts still differed by up to two; the cause
is unresolved. The manifest retains the initial failed run and short diagnostic,
not just the successful rerun. The initial disconnect cause was not established;
the re-admission deadlock and state-retry pressure path have focused regressions.

### Implemented architecture

- Claims reserve 64 pending entries per shooter and 64 authoritative resolutions
  per attacker/victim pair. Valid launch identities match exactly; temporal
  fallback cannot match two conflicting launch stamps. Capacity refusal is a
  terminal verdict. Direct/splash multiplicity, Imperialist correction and
  rescued-flight suppression remain supported. No still-matchable resolution is
  overwritten to admit a new hit.
- Starts now pass through `Synchronizing`. `Loaded` and `WorldReady` are separate
  masks. The client applies a roster/lifecycle-fenced fast, slow and world baseline
  while frozen, restores spawn, settled form, weapon, health, score, damage ACK,
  clock, pickups and RNG, then echoes its exact bootstrap identity. Countdown and
  late-join input admission require this acknowledgment.
- Three independently decodable full-state lanes replace the 60 Hz full snapshot:
  fast at 60 Hz, slow at 10 Hz plus changes, world at 4 Hz plus meaningful changes.
  Worst-case datagrams, including the 24-byte envelope, are **903 / 183 / 433
  bytes**. Rare legacy control seeds still use the larger canonical snapshot;
  replay also keeps that canonical representation. Realtime Intent, HitClaim,
  HitVerdict and fast snapshot sizes are hard-tested below 1,200 bytes.
- Sixteen two-byte sequenced edges occupy the original 32-byte intent-history
  budget. A receive window and bounded consumption queue retain repeated actions,
  suppress duplicates and preserve recovered Shoot age. The intent remains 92
  payload bytes. Slot/life/match replacement resets event state.
- Door/connector state, force-field activity and Platform/Object collision meshes
  share the player rewind frame and 128-frame history. Continuous transforms
  interpolate translation, rotation and scale; discrete state uses the sampled
  frame. Catch-up samples each historical step, and disposal/exception paths
  restore the exact live collision state. Inventory is bounded to 512 components.
  `-netgeometryshadow` compares without applying production rewind; production
  rewind is enabled by default after the shadow and asset checks.

Details live in `.claude/multiplayer/NETWORK-{HITCLAIMS,START-LIFECYCLE,TRANSPORT,
UNLAGGED,PREDICTION,SMOOTHING}.md`. Protocol compatibility is bumped once to 18;
replay world-schema accessors and their fingerprint include the new edge state.

### Reproducible validation

`tools/nettest/baselines/network-v18.json` contains all 2,016 seeded codec/network
profiles: 2/4/8 players, RTT 0/50/100/150/250/320/400 ms, jitter 0/20/40/80 ms,
loss 0/1/2/5%, reorder 0/1/3%, and duplication 0/1%. These use production codecs
and a virtual-time impairment queue. Allocation totals include harness storage;
separate warmed checks measure zero allocations for the hot codecs, connected
UDP send, lane decode and collision rewind.

The 19 headless/asset commands and their exact outcome summaries are retained in
`tools/nettest/baselines/validation-v18.json`. Coverage includes:

- 3,680 lifecycle assertions; more than 2,900 real-UDP lobby/barrier assertions; 3,338,728
  health/shot assertions across 7,776 weapon/profile cases and two delivery streams.
- Capacity exhaustion, shooter isolation, expired ledger reuse, exact/fallback
  identity, direct/splash, headshot and rescue suppression. The real-entity claim
  load check resolves 43,200 claims through production damage paths, with eight
  shooters, sustained Shock Coil, up to 400 ms RTT/80 ms jitter/5% loss/3%
  reordering/1% duplication and two queued 400 ms stalls. Every received claim
  settles once and each victim loses exactly 900 health per profile. This is a
  controlled collision fixture, not a substitute for rendered projectile flight.
- Eight real bootstrap players representing all seven hunters, including settled
  Kanden/Weavel forms and Weavel's turret; frozen placement, health/weapon/score,
  RNG restoration, lane loss/reorder/duplicates, missing roster, malformed lanes,
  first ACK, stale rematch identity, late join and participant removal. A release-
  pump regression also delivers a newer spawn in the bootstrap packet batch and
  verifies it is applied before the first draw without advancing simulation.
- Repeated identical edges, simultaneous different actions, 0–3-packet loss
  bursts, reordering/duplicates, sequence wrap, lifecycle reset and Shoot ages
  0–7. Production health/shot and rendered checks provide integration coverage.
- 98 real-asset combat assertions, 2,352 lag-compensation shadow profiles and
  1,620 weapon-policy profiles. `UNIT1_RM1` supplies 12 real doors, 11 fields and
  nine mesh components for historical obstruction and restoration checks.
  Synthetic cases add fractional rotation/scale, history wrap, catch-up and
  exception restoration.
- Replay format (2,710 checks), control, timeline (43 checks) and all 12
  multiplayer world modes. Each world mode verifies 1,801 recorded frame hashes,
  seven detached restores, 1,650 continuation frames and file/frozen-clip seeks.
  Existing form (699 checks), continuous-weapon phase and platform checks pass.

Build and run commands (a configured extracted-assets directory is required for
commands ending in `-scene` and rendered/replay world checks):

```sh
dotnet build tools/nettest/nettest.csproj -c Release

dotnet tools/nettest/bin/Release/net10.0/nettest.dll --protocol18
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --input-edges
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --dynamic-geometry
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --claim-stress
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --load-lifecycle
# Other suite switches are listed in validation-v18.json.
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --claim-load-scene /path/to/game-data
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --bootstrap-scene /path/to/game-data
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --geometry-scene /path/to/game-data
dotnet tools/nettest/bin/Release/net10.0/nettest.dll --combat-scene /path/to/game-data

dotnet tools/nettest/bin/Release/net10.0/nettest.dll --network-benchmark --extended \
  --network-benchmark-json /tmp/network-v18.json
python3 tools/nettest/run-assets.py --game-data /path/to/game-data \
  --out /tmp/net-v18-rendered --scenario all --seconds 300
```

The rendered runner stages application data and uses an empty custom-map output
directory, avoiding concurrent writes to the desktop app's global generated maps.
It reads the existing extracted assets without copying or downloading them.
Each arm saves server, client and diagnostic logs plus a summary with assembly
SHA-256. Packet-size and claim/input overflow checks are part of its exit verdict.

### Rendered results and acceptance limits

All six profiles now have a passing eight-client run. The final LAN, moderate
and extreme runs use the build with the release-pump fix; poor, severe and mixed
passed on the earlier build. Their separate assembly hashes are retained below
in the JSON evidence. Initial failures and unresolved observation differences
remain documented rather than being erased by reruns.

| Run | Seconds | Reports passed | Mean step ms | Dropped ticks | Queue high / drops |
|---|---:|---:|---:|---:|---|
| geometry-shadow/lan | 300 | 8/8 | 0.44 | 3.0 | 35 / 0 |
| final/extreme | 300 | 7/8 | 0.33 | 5.0 | 57 / 0 |
| final/lan | 300 | 6/8 | 0.32 | 5.0 | 43 / 0 |
| final/mixed | 300 | 8/8 | 0.35 | 0.0 | 26 / 0 |
| final/moderate | 300 | 7/8 | 0.34 | 1.0 | 49 / 0 |
| final/poor | 300 | 8/8 | 0.36 | 0.0 | 41 / 0 |
| final/severe | 300 | 8/8 | 0.33 | 0.0 | 79 / 0 |
| repeat/extreme | 300 | 8/8 | 0.32 | 2.0 | 13 / 0 |
| repeat/lan | 600 | 7/8 | 0.33 | 2.0 | 59 / 0 |
| repeat/moderate | 300 | 8/8 | 0.37 | 6.0 | 27 / 0 |
| release/lan | 300 | 8/8 | 0.29 | 1.0 | 14 / 0 |

`tools/nettest/baselines/rendered-v18.json` preserves per-run profiles, assembly
hashes, failures, step/queue/catch-up measurements and cross-observer edge counts.
The initial LAN bomb-coverage failure, moderate form mismatch and extreme startup
position failure remain visible. The ten-minute LAN run reproduced the startup
race; a bootstrap followed by a newer spawn in one loading pump updated received
state before the first draw could apply it. The release pump now applies that
newer state without simulation, and an asset-backed regression covers the order.
The original feature assertions were not weakened.

The original moderate run's 124-frame form mismatch has no established cause.
A subsequent passing run is evidence for that run, not proof that an intermittent
mismatch cannot recur. Initial LAN bomb coverage was four frames at the source
and four/five at observers against a five-frame threshold; longer LAN coverage
passed that check. In the ten-minute LAN run, all 1,227 alt-attack presses were
counted identically by every observer. The original poor/mixed arms had up to
two/four fewer alt-attack observations for one source respectively. These tour
counts include lifecycle boundaries; their cause is not established, so they are
not presented as proof of loss-free action delivery throughout those arms. The
focused sequence suite establishes exact-once recovery within its tested loss
window, while per-life reset deliberately discards stale actions.

All measured production runs kept fast datagrams at 903 bytes and recorded zero
claim-capacity refusals, unmatched-ledger overwrites, input overflows, simulation
failures, server stalls and receive-queue drops. Mean server step times stayed
below the corresponding protocol-17 baseline plus 10%; wall-clock overruns and
dropped ticks remain in the table rather than being treated as zero. The extreme
arm reached the 45-step catch-up bound without truncation.

The shadow LAN run passed all eight clients for 300 seconds before production
geometry was enabled. SANCTORUS has no dynamic obstacles; its shadow availability
is not evidence of historical-door correctness. The separate `UNIT1_RM1` asset
fixtures provide that evidence. The automated matrix runs eight hidden OpenGL
clients and a real authority on one macOS host with locally injected impairments.
It does not replace geographically separate WAN/mobile/VPN testing or human
assessment of game feel. No merge into main is performed by this task.

## Historical protocol 16/17 modernization report

The remainder records the earlier work and its original measurements. References
to protocol 16/17, the full 60 Hz snapshot and loading-only barriers below describe
that historical architecture, not the current protocol-18 implementation.


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


## Historical alt collision follow-up (2026-09-24)

Player rewinds now restore historical collision form and Kanden segments, with
lossless collision-only restoration. Separate ACK-timed contact queries cover
Samus, Spire, Noxus, Trace and Weavel while preserving owner movement, live
physical separation, damage values and Protocol 18. Contact sweeps cross accepted
movement intervals without rewinding the actual physics world. Kanden/Sylux bombs
retain their existing entity path. Evidence, boundaries and commands are in
[alt-form-validation.md](alt-form-validation.md).
