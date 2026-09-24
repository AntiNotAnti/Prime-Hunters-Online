# Protocol 19 combat consistency and telemetry

Protocol 19 combines continuous Shock Coil target identity, Weavel turret state,
CombatAck, and anonymous diagnostics. Protocol 18 peers must reconnect with the
same build. The simulation remains 60 Hz. Movement ownership, weapon damage,
claim grace, smoothing, and the 45-frame production rewind limit are unchanged.
`LagCompensationPolicy.Configure("Enforce")` is refused in every build.

## Combat state

`PlayerState` is 117 bytes. Its final three bytes contain `HalfturretActive` and
little-endian `HalfturretHealth`. Fast replication and WorldBootstrap use the
same canonical fields; the maximum eight-player fast datagram is 927 bytes.
Generation/life acceptance precedes application. Form transitions create the
entity. A health update never creates one. A predicted destroyed turret remains
an inert, hidden replica while its owner is alive in alt form, allowing a newer
matching authoritative sample to correct it. Bootstrap restores body and turret
health after form creation and before WorldReady.

Turret-contact claims carry a flag and damage before the body/turret split.
Rescue enters the existing damage function once, with damage multipliers already
applied. This preserves the cartridge split and its one-body-HP floor.

Packet type 31 now carries CombatAck (the `HitVerdict` name remains a source
compatibility alias). Its 15-byte stream/shooter header is followed by up to 16
19-byte entries. Each includes ClaimId, result, victim generation/life, actual
body damage, resulting body/turret health, damage sequence, and outcome flags.
The damage sequence currently uses the existing per-life 16-bit event counter,
carried in a 32-bit field; comparisons use the existing wrap-safe ordering.

The resolved-hit ledger captures the outcome at the end of TakeDamage, including
resulting afflictions. Claim rescue captures its result after affliction restore.
An AlreadyResolved answer uses that stored record, never the victim's later
health. Terminal results persist in the bounded claim cache and are repeated
when the claim is retried. Combat acknowledgements are unreliable application
traffic, outside the reliable control queue.

Prediction settles by ClaimId. A received exact outcome retires that prediction's
debit immediately and can correct the health display. Its presentation value is
fenced by life and damage sequence, and a same/newer snapshot replaces it.
Acknowledgements do not replay damage, award scores, kill, or resurrect replicas.

## Server configuration and privacy

Dedicated servers default to Aggregate telemetry with local storage and no
upload. Set `PRIME_TELEMETRY_CONFIG` to a JSON file using
[`telemetry.example.json`](telemetry.example.json). Use `-notelemetry` to disable,
`-netstudy` for per-event research capture, or `-netstudyverbose` for developer
capture. Off takes precedence. Malformed configuration disables recording.
Verbose currently records the same production events as Study; the independent
`PRIME_CONTINUOUS_TRACE` developer ring can supply additional target geometry.

Public servers may collect anonymous network, combat, and performance
measurements to improve multiplayer quality. Stored identifiers are random per
match and slot-local player numbers. Telemetry contains no IP addresses, player
names, account identifiers, chat, tokens, or persistent device identifiers.
Existing opt-in debug logs are separate from this telemetry subsystem.

## Bounded pipeline

Simulation emits a value struct into a preallocated ring (8,192–32,768 entries;
default 16,384). A failed non-waiting lock attempt or a full ring drops the event.
The simulation never serializes JSON, opens a telemetry file, waits for a writer,
or performs an HTTP request. A single background writer owns compression,
aggregation, retention and optional uploads. Ending a match only completes the
queue; process shutdown gives the worker a bounded 500 ms opportunity to drain.
An unusually stalled prior writer causes the next recording to be skipped,
rather than creating an unbounded number of writer threads.

The writer streams `telemetry/YYYY-MM-DD/match-<random>.jsonl.gz` and produces a
`.summary.json`. Every header includes schema, protocol, build metadata,
platform, map, mode and configured player capacity. `buildCommit` is `unknown`
when the assembly does not include an informational commit suffix. Connection
and lifecycle records show actual participating slots, including late joiners.
Raw retention defaults to 14 days and is clamped to 7–30 days. Startup cleanup
removes only this subsystem's raw files. Raw output is also capped per file;
summaries remain available longer. Failure to write raw events does not stop
aggregation. `eventsWritten` counts records consumed into the summary, including
Aggregate mode records that intentionally are not written individually.

Counters expose queued, consumed, dropped, high-water, writer failures and upload
failures. They are diagnostics only. Histograms use fixed memory; timing and
rewind quantiles are quantized. Samples outside a histogram's finite range fall
in its last bin; the exact maximum is retained separately.

## Schema 1 records

All raw events have frame, player/victim sample IDs, weapon, generation/life,
record ID, result/flags and numeric fields A–H. Absent timing/displacement uses
-1, not an invented zero.

| Event | Numeric payload |
|---|---|
| Connection | RTT, recent minimum RTT, jitter, transport received/sent, queue drops/current/high-water |
| Shot | requested/served/plausible rewind, recovered press age; result is geometry shadow category |
| AuthorityResult | actual body damage, body health, turret health; flags include headshot/lethal/afflictions |
| Claim | body damage and resulting health; explicit terminal reason, including capacity |
| CombatAck | settlement milliseconds, damage correction, health correction, headshot correction |
| ContinuousTarget | reported/selected target encoding, phase, cone or collision damage; collision flags |
| Form | mismatch duration, snapshot frame, intent frame; episode ID and correction reason |
| Lifecycle | packet/phase event; 200 identifies accepted WorldReady |
| ServerStep | engine step milliseconds, cumulative dropped ticks, allocated bytes, GC generation counts |
| LagStudy | requested/served/plausible rewind, displacement, RTT/jitter, horizontal/vertical displacement |

A CombatStudy packet (type 51) reports bounded, lossy client correction samples
once per second: at most 32 samples/399 bytes. It uses background packet priority,
is stream/lifecycle fenced and rate limited at the server. Its records are
explicitly marked client-reported (flag 128). They never affect gameplay.
Production counters can therefore include client settlement latency without
storing client debug logs. Dropped samples are not inferred to be exact predictions.

Lag timing shares one evaluator across beam launch (including Imperialist and
historical area mechanics), claim evaluation and historical contact. Requested
fractional ACK time and recovered press age remain intact. Connection bounds are
shadow observations only; claim age/grace rules are not replaced by those bounds.
Timing samples and later impact/rescue observations are distinct, so multiple
hits from one projectile do not multiply the shot timing denominator. RTT buckets
are 0–50, 50–100, 100–150, 150–200, 200–250, 250–300, 300–400 and 400+ ms,
plus unknown. Jitter buckets are 0–10, 10–25, 25–50, 50–80 and 80+ ms, plus unknown.
Weapons and alt contact remain separate.

The existing read-only Imperialist geometry comparison reports a counterfactual
where historical geometry is available. Full counterfactual ballistic, homing,
continuous damage, turret and dynamic-world outcomes are **not** simulated a
second time: these remain `HistoricalDataUnavailable`, not inferred misses.
Actual hit/rescue window membership and historical target displacement can still
be collected. This limitation must be considered before any enforcement decision.

Form reconciliation keeps an absolute 90-frame mismatch episode independently
of animation flag flicker. It starts a new episode only when desired form changes
or a new mismatch follows a resolved episode; lifecycle reset clears it. Existing
form methods retain position conversion and avoid repeating entity creation on
an already completed transition.

## Optional aggregate collector

Uploads are disabled by default. Configure a full endpoint such as
`https://collector.example/api/net-telemetry/v1/matches` and keep the server API
token in `PRIME_TELEMETRY_TOKEN`. Only aggregate summaries enter the uploader.
It retains a bounded pending directory, persists exponential retry deadlines,
limits each match-end batch to four attempts, and times out HTTP independently
of gameplay. Local raw files are never uploaded. Retries occur after subsequent
matches; no gameplay timer waits for them.

An optional standard-library collector is provided:

```sh
PRIME_TELEMETRY_COLLECTOR_TOKEN='<server token>' python3 tools/telemetry/collector.py \
  --directory /var/lib/prime-telemetry --listen 127.0.0.1 --port 8099
```

It accepts only schema-1 aggregate shapes, caps request size, file count and disk
usage, authenticates server tokens, and does not log request addresses/headers.
Use the deployment's HTTPS reverse proxy for external access. No collector or
production server is deployed by this change.

## Validation and release gates

Run `nettest --protocol19` for codecs, MTU, mismatch episodes, timing boundaries,
zero-allocation/full-queue emission, blocked writer, invalid directory, storage
failures, HTTP 500, offline/slow upload, and draining shutdown. Run
`nettest --protocol19-scene <game-data-directory>` for actual Weavel split,
rescue/retry, stored historical outcomes and immediate prediction correction.
`--bootstrap-scene` checks damaged turret health before WorldReady.

The checked-in validation record identifies actual builds and completed runs.
The prior Shock Coil WAN hit-count gate remains open even when accepted target
identity agrees. Scripted local processes cannot substitute for thousands of
real-player matches across maps, weapons, RTT and jitter buckets. Keep this
release in study/review until those data and the remaining counterfactual coverage
are reviewed. No stricter rewind enforcement is enabled by these measurements.
