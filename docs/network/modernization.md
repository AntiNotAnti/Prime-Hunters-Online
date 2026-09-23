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

## Known baseline conflict

`PlayerReplicationBridge.ApplyState` contains an old extreme-divergence owner
position correction (30 units for 60 updates). P0 records the current behavior;
modernization must remove this separately before meeting the requested invariant.

## External validation

Asset-backed LAN/WAN, mixed-quality eight-player runs and human gameplay-feel
checks must be recorded separately from virtual-time codecs and headless tests.
A passing synthetic workload is not evidence that those checks were run.
