# Lag-compensation plausibility

Protocol 17 retains AckFrame, AckSubFrame, recovered press age, the 128-frame
history and the default 45-frame hard ceiling. `LagCompensationPolicy` is a
server diagnostic around that timeline. Default `Shadow` never changes the
fractional time passed to reconciliation. `-lagcompplausibility off|shadow|enforce`
selects the mode; Release builds reject Enforce.

The bound includes a **full RTT**: the displayed server frame travels downstream,
and the intent carrying its acknowledgement travels upstream. Add the shooter's
actual presentation delay, recovered edge age (0–7), bounded RTT variance and two
scheduler frames. Recent minimum RTT is the minimum of the last 64 ACK samples;
an isolated delayed ACK cannot supply an unlimited allowance. The hard ceiling
still applies. Ping/pong remains a secondary RTT observation; without ACK variance
or a fresh delay report the shadow bound is unavailable.

Clients report actual NetSmoothing.Delay once per second in an unreliable
14-byte PeerTiming packet (match, epoch, float delay). The endpoint-bound server
authorizes only finite values 0–8 for the current match and epoch, expiring them
after three seconds. Zero supports disabled smoothing. These are diagnostic
client reports, not a trust mechanism suitable for enabling production enforcement.
No invented two- or six-frame default fills missing observations.

Fixed match-scoped arrays group samples by shooter, weapon, RTT (<50/<100/<150/
<250/<350/350+ ms), jitter (<20/<50/50+ ms), and presentation delay (<3/<6/6–8).
Missing RTT, jitter and delay have distinct unavailable buckets. Capture APIs
return immutable values without clearing counters. Record requested/hard/shadow
rewind, would-clamp, refused frames and worst requests. Reset on match reset.

Read-only outcome categories cover same outcome, hit→miss, head→body, body→head,
miss→hit, different victim and unavailable history. The initial practical geometry
comparison is **Imperialist's first travel segment against historical bipeds and
current room geometry**. It uses the production cylinder collision primitive.
This is a geometry diagnostic, not a replay of damage or a prediction of the
entire projectile outcome. Other weapons, missing history, alt forms, detached
turrets, enemies, closed doors and active force fields report unavailable.
No live player moves and no projectile Process/damage callback runs for shadow
comparisons. Use these categories with their coverage limitations when analyzing
results; never count unavailable cases as agreements.

Enforcement requires real shot data by weapon and connection quality, including
hit/headshot changes and post-cover depth. No acceptance percentage or production
clamp is selected by this migration.
