# Alt-form collision validation

## Reproduction and checks

```sh
dotnet run --project tools/nettest -c Release -- --alt-hits
dotnet run --project tools/nettest -c Release -- --alt-scene /path/to/game-data-directory
python3 tools/hitrig/run-alt.py --runtime /path/to/staged/runtime \
  --data /path/to/game-data-directory --output /path/to/logs
```

The data directory contains the operator's `paths.txt`; game assets are not
included. The runner isolates writable preferences/logs, uses stock SANCTORUS by
default, and terminates only its own child processes. `--map` and `--mapdir` can
select a prebuilt custom arena. It runs real 60 Hz headless clients, with no
rendering claim. Never rebuild a runtime while those processes are using it.

`-hitrig alt-static`, `alt-lateral`, `alt-morph`, `alt-contact` and `alt-crossing`
exercise stationary/rolling targets, both transition directions, an attacker
against a stationary opponent, and two attacking rollers. Select the hunter with
`-hunter`; Kanden tail and individual Spire rocks have deterministic geometry
checks because the live rig cannot guarantee a tail-only or individual-rock hit.

## Deterministic evidence (macOS arm64, Release, 2026-09-24)

`--alt-hits`: seven hunter collision volumes; both form boundaries; chain head,
middle and rear hits; exact restoration including cached volumes and previous
position; Spire rock extremes; Noxus edge/vertical bounds; Samus/Trace/Weavel
pass-throughs; lifecycle/form/discontinuity sweep fences; ACK fractions and bounds.
100,000 contact geometry queries allocated zero bytes (about 0.004 ms for 56
pairs in this run).

`--alt-scene`: actual eight-player scene, production ring and rewind; exception
restoration; slot reuse, spectator exclusion and respawn; actual authority boost
damage and attack completion with unchanged victim position. Eight-player history plus contact
resolution allocated zero bytes and took approximately 0.0033 ms/frame.

The scene test also uses `NetFaultQueue` for two-way seeded delivery around the
production history and geometry. This is controlled simulation evidence, not a
measurement of rendered clients. The old beam arm applies the historical position
in the current form; the new arm uses the historical form and chain. Contact's
old arm is the live endpoint; its new arm is the historical target plus sweep.

| Profile | RTT/jitter ms | Loss/reorder | Accepted probes | Visible beam hits | Authority beam hits | Old beam disagreements | New disagreements | Visible contact overlaps | Historical contact hits | Live endpoint hits | History misses |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| LAN | 0/0 | 0/0% | 239 | 119 | 119 | 0 | 0 | 119 | 119 | 0 | 0 |
| Moderate | 100/20 | 1/1% | 232 | 113 | 113 | 7 | 0 | 113 | 113 | 21 | 0 |
| Severe | 250/40 | 2/1% | 222 | 110 | 110 | 20 | 0 | 110 | 110 | 5 | 0 |
| Extreme | 320/80 | 5/3% | 209 | 106 | 106 | 24 | 0 | 106 | 106 | 5 | 0 |

Mean rewind depths: 0, 8.38, 18.36, 25.20 frames. Sweep distance is four units.
The contact probe deliberately passes completely through its target: LAN's zero
live endpoint hits expose tunneling rather than latency. Historical-only contacts
were 119/92/105/101; live-only contacts were zero. Missing/dropped/reordered probes
are not counted as accepted attacks.

## Real client/server impairment runs

The final staged runtime ran 40 seconds per client, two real headless clients per
arm, on stock SANCTORUS. All 12 arms / 24 clients passed and all servers reported
zero simulation failures:

| Scenario | LAN | Moderate | Severe | Extreme |
|---|---|---|---|---|
| Samus boost against stationary target | pass | pass | pass | pass |
| Morph/unmorph target under beam fire | pass | pass | pass | pass |
| Two boosting players crossing | pass | pass | pass | pass |

The scenario checks require actual attack attempts and permit an intentionally
stationary target. The headless loop advances the existing presentation clock
once per simulation step, as a rendered client would; without that update, ACKs
remain stuck on the initial presentation and produce meaningless ceiling-clamped
rewinds. The production renderer/smoother is unchanged.

Full per-arm contact diagnostics, client reports, server timing/rewind samples
and seeded comparison results are checked in at
[`alt-collision-20260924.json`](../../tools/nettest/baselines/alt-collision-20260924.json).
Client overlap counts are geometry checks across frames, not unique attacks or
damage awards; do not divide them by authority damage events to claim accuracy.
The seeded test above supplies the controlled before/after geometry comparison.

Initial harness attempts exposed a wrong Samus control, an inappropriate
moving-peer assertion for stationary targets, stale headless presentation ACKs,
and unrelated custom-map generation lock contention. They are excluded from
these gameplay comparisons and recorded in the baseline notes. Stock-room runs
use an empty custom-map catalog to avoid compiling unrelated maps during startup.

Regression checks passed: Protocol 18, lifecycle, health/shot arbitration,
architecture, dynamic geometry, input edges, claim stress, weapon policies,
lag-compensation shadow comparisons, allocations, actual impaired eight-peer UDP,
and the existing asset-backed combat suite. The new scene suite passed 984 checks.
Release builds retain 16 existing warnings and zero errors.

## Remaining validation boundaries

No radius, speed, weapon damage, simulation frequency, movement ownership or wire
format changed. Detached turret collision remains live. ACK snapshots cannot
reconstruct unsent owner animation poses; attack state is the authority's accepted
attack and geometry, with corrected attachments. The full Cartesian impairment
matrix and human rendered gameplay-feel acceptance remain broader release checks;
these four profiles are the minimum requested impairment arms.
