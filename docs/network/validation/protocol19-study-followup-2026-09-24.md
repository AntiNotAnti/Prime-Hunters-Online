# Protocol 19 follow-up validation and VPS study

The combined PR now includes the study collector/report and an isolated VPS
canary. Local regressions and the deployment smoke pass. Real-player collection
has **zero human matches** at handoff. Some individual Shock Coil impairment
profiles remain below the requested hit-count target, and general counterfactual
projectile/secondary-body simulation remains unavailable. Keep the PR in draft
and lag compensation in Shadow.

[Machine-readable evidence](protocol19-study-followup-2026-09-24.json) contains
per-profile counters, paired traces, full performance summaries, and the VPS
collector results. This supplements, rather than replaces, the earlier validation
record. Earlier telemetry performance runs are superseded by the corrected
partial-configuration measurements below.

## Implementation and tests

Gameplay identity changes are in `513da99896ea1a32ecb926806d1973fe5d95c618`.
Telemetry configuration/rollover fixes are in
`5c5c8ec1bd6702bcead1d191354cb55876532984`, the deployed runtime commit.

- Shock Coil claims identify the continuous firing tick separately from the
  historical world ACK. Duplicate claims and delayed physical copies reuse the
  stored outcome within lifecycle/stream/grace fences. Damage tables, ramp,
  collision rules and grace limits are unchanged.
- Telemetry schema 2 records connection distributions, lifecycle durations,
  correction counts and missing outcomes. Rescue window measurements are captured
  at claim admission, before arbitration grace. Health-correction telemetry counts
  the actual presentation change rather than comparing an absent outcome to zero.
- Partial JSON configuration preserves defaults. This fixes the null token-variable
  name discovered on Linux and restores omitted queue/retention/upload settings.
- The next match can record while one prior writer drains/uploads. Two unfinished
  workers are the hard maximum; shutdown shares a 500 ms total wait budget.
- All 25 regression/asset suites pass, with focused telemetry/scene checks rerun
  after the final fixes: **141 Protocol 19 checks, 58 combat scene checks,
  3,338,737 health/shot assertions**, and **2,016 transport benchmark scenarios**.
- Replay: **2,710 format checks and all 12 multiplayer modes** pass, including
  detached restore, continuation and file/frozen-clip seeks.
- Harvester and Proving Ground also pass the eight-player bootstrap fixture.
- All 13 hosted checks pass for the deployed `5c5c8ec` commit, including Windows,
  macOS, Linux and Android build jobs.
- Five Python collector/report tests pass. A blocked-consumer emission test
  records zero producer allocations; the JSON records the measured enqueue cost.

## Shock Coil process matrix

Twenty 20-second profiles, two clients each, seed 431, all 40 client exits zero.
All **18,509 accepted paired target decisions agree**. Final collision winners
are now distinguished from candidate player overlaps in the traces.

Across the complete matrix, 1,761 hits were confirmed and 91 were unpredicted:
95.1% observed-hit coverage (`confirmed / (confirmed + unpredicted)`). Of 1,841
predictions, 1,761 were confirmed, 78 denied and two remained unsettled when the
runs ended. These are different denominators; neither is exact per-hit identity
agreement. The aggregate improvement does **not** close a per-profile 95% gate.

| Configured RTT/jitter | Loss | Predicted | Confirmed | Denied | Unpredicted | Observed coverage |
|---|---:|---:|---:|---:|---:|---:|
| 0/0 ms | 0% | 144 | 144 | 0 | 0 | 100.0% |
| 0/0 ms | 1% | 83 | 83 | 0 | 0 | 100.0% |
| 0/0 ms | 2% | 87 | 87 | 0 | 0 | 100.0% |
| 0/0 ms | 5% | 91 | 91 | 0 | 0 | 100.0% |
| 100/20 ms | 0% | 87 | 87 | 0 | 0 | 100.0% |
| 100/20 ms | 1% | 89 | 88 | 1 | 0 | 100.0% |
| 100/20 ms | 2% | 113 | 107 | 4 | 1 | 99.1% |
| 100/20 ms | 5% | 88 | 86 | 2 | 0 | 100.0% |
| 250/40 ms | 0% | 94 | 85 | 9 | 0 | 100.0% |
| 250/40 ms | 1% | 78 | 72 | 6 | 13 | 84.7% |
| 250/40 ms | 2% | 145 | 135 | 10 | 12 | 91.8% |
| 250/40 ms | 5% | 82 | 82 | 0 | 5 | 94.3% |
| 320/80 ms | 0% | 56 | 52 | 4 | 6 | 89.7% |
| 320/80 ms | 1% | 99 | 92 | 7 | 0 | 100.0% |
| 320/80 ms | 2% | 66 | 65 | 1 | 20 | 76.5% |
| 320/80 ms | 5% | 65 | 64 | 1 | 15 | 81.0% |
| 400/80 ms | 0% | 94 | 89 | 5 | 0 | 100.0% |
| 400/80 ms | 1% | 86 | 83 | 3 | 8 | 91.2% |
| 400/80 ms | 2% | 92 | 81 | 11 | 11 | 88.0% |
| 400/80 ms | 5% | 102 | 88 | 14 | 0 | 100.0% |

## Eight-player telemetry cost

Sequential 40-second eight-player Shock Coil runs at 320/80 ms, 2% loss, 3%
reorder, 1% duplication, seed 8128. Partial-config defaults are fixed; compressed
raw output is verified present in Aggregate and Study. All 24 client exits are
zero. The table is the periodic 1,800-step window, not the full-match interval.

| Mode | Mean ms | p95 ms | p99 ms | Max ms | Allocated bytes | Dropped ticks |
|---|---:|---:|---:|---:|---:|---:|
| Off | 0.93 | 1.01 | 14.91 | 34.0 | 380,580,088 | 0 |
| Aggregate | 0.92 | 0.99 | 15.36 | 34.1 | 379,657,168 | 0 |
| Study | 0.93 | 0.97 | 14.98 | 35.0 | 383,033,656 | 0 |

Aggregate consumed 75,136 events, dropped two, and wrote 40,236 compressed raw
bytes. Study consumed 73,240 events, dropped three, and wrote 2,910,278 compressed
raw bytes. Both report zero writer failures. Combat runs are not identical;
these observations do not establish a causal overhead bound. Whole-engine
allocation totals are separate from the zero-allocation emission check.

## VPS verification

An isolated server listens on **51.161.113.128:27921 UDP**. The collector listens
only on **127.0.0.1:8099**. The server, collector and report timer are active; the
existing public server and master remain active and unchanged. Matching Protocol
19 clients connect directly to the study port. The normal study rotation uses
Sanctorus, Harvester and Proving Ground, eight minutes each.

Deployed executable SHA-256:
`27e65811e5bf5cf4d54d65d963dd69887aae3e02d4a50a8afa33c6f39c18a87e`.

Two local clients completed a 95-second real-WAN smoke against the VPS, including
map transition. After correcting the configuration defect, two VPS-local clients
completed a further 95-second upload-cycle smoke. The final committed build
produced three valid collected summaries and 18,682 parseable compressed JSON
lines. The active scripted match consumed 18,617 events without telemetry drops
or writer failures. The final collector rejects wrong authentication with 401
and invalid aggregate payloads with 400; automatic server uploads succeed.

All scripted telemetry/aggregates/reports were moved under
`/home/ubuntu/projectprime-study/staging/final-build-smoke` (earlier diagnostic
runs are under `pre-release-smoke`). The human-study collector directory and
report start empty. Empty server sessions are excluded from the report's match
count. No real-player sample-size or enforcement conclusion is claimed.

The [deployment runbook](../study-deployment.md) covers paths, operation,
collection limits and rollback. Full counterfactual ballistic, homing,
continuous-damage, turret and dynamic-world comparisons remain an explicit gap;
observed current-policy hits outside a proposed window are not proof that the
alternate simulation would miss.
