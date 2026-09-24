# Hit claims, and who decides a kill

## Protocol 18 arbitration

Pending storage is partitioned into **64 claims per shooter**, 512 total. Park
first drains due work; a still-full shooter partition receives terminal
`ClaimCapacity` (12), and the corresponding prediction is denied. Another
shooter cannot consume its partition. The client outbox is bounded at 128 and
explicitly settles an evicted prediction.

The resolved ledger has **64 entries per attacker/victim pair**. Each entry
stores the shooter's `ShotKey`, victim generation/life, authority frame, damage
and consumed state. Expired, invalid-lifecycle or consumed entries are reusable;
unmatched entries within the 72-frame grace are never overwritten. If all 64
are still matchable, preserve them and refuse rescue for that pair until the
unrecorded hit is outside grace. Capacity refusal is visible, never silent.

Matching first consumes one entry with the exact launch identity. A direct hit
and splash retain separate entries. The time/ACK fallback is allowed only when
at least one launch stamp is unavailable; differing nonzero launches never
match. Imperialist body/headshot correction remains in place. The rescued-flight
ledger holds 512 identities per pair for its longer flight lifetime and fails
closed before applying damage if retention is unavailable.

Diagnostics include pending/current high water, resolved/current high water,
expired unused entries, zero unmatched overwrites, capacity refusals, launch
versus fallback matches, and rescued-flight occupancy. `--claim-stress` covers
retention through the full grace, multiplicity and independent shooter capacity.
`--claim-load-scene` exercises 43,200 claims across eight real player entities
through actual damage/arbitration at 0/50/150/250/320/400 ms RTT, with up to
80 ms jitter, 5% loss, 3% reordering, 1% duplication and 400 ms queued stalls.
Every claim terminates, physical/rescued damage pays exactly once, and ordinary
load has zero capacity refusals or unmatched overwrites. Its collision placement
is controlled; the rendered tour separately exercises aiming and flight.
Historical measurements below describe their original builds.

Code: `Mods/Network/NetHitClaims.cs`. Hit claims were introduced in protocol 7; the current protocol is defined by `NetConfig.ProtocolVersion` (18). The third piece of the same
machine as [lag compensation](NETWORK-UNLAGGED.md) and
[instant hit registration](NETWORK-PREDICTION.md), and the one that answers the
complaint those two leave standing.

## The fault

The rewind fixed *where* a shot is resolved. The prediction fixed *when* the
shooter is told. Neither can do anything about the cases where the shooter and
the authority are not running the same test at all:

| | |
|---|---|
| **the rewind hit its ceiling** | measured on this box at 320 ms with 80 ms of jitter and 2% loss, over two scenarios: **80.0% and 88.9% of shots clamped**, 2.8 and 3.2 frames of rewind refused each. The requested-depth histogram's *mode* was two frames past the ceiling in both — the distribution folded onto it, not a tail touching it. In the first, the shooter's own machine resolved 17 of the 23 hits the authority credited it with, and 2 of its 9 headshots came back as body shots |
| **the trigger pull was recovered from a press history** | so the packet carrying it acks a newer world than the one the finger moved in |
| **the shooter was killed during the round trip** | the authority never runs the shot: its copy of that player was already dead when the intent arrived, and a dead player's presses do nothing |

The third is the one players call unfair rather than merely laggy: the shooter
can resolve a legitimate hit in the world they saw while the authority never
runs that shot at all because its copy of the shooter is already dead. Current
prediction does **not** drop a remote body locally; it preserves immediate hit
feedback and holds lethal damage at one health until the authority decides.

## What a claim is

`PacketType.HitClaim`, client → authority, is currently 49 bytes. It carries
claim id; shooter frame; authority `AckFrame`; projectile `LaunchFrame`; victim
slot; beam; damage; flags (headshot, lethal, frozen, burning, disrupted); hit
point; match/authority identity; and shooter/victim generation + life IDs. The
stream/lifecycle identities prevent a delayed claim from crossing a match,
authority epoch, slot occupant or respawn boundary.

`PacketType.HitVerdict` comes back with explicit outcomes including applied,
already resolved, dead shooter/victim, too old, wrong lifecycle, geometry,
damage limit, invalid launch and no-damage. Refusals are diagnostic, not one
undifferentiated "miss".

Claims are declared from `NetHitPrediction.NoteHit` — every hit this machine
resolves for its own player on somebody else, **every weapon**, not only the
Imperialist. The sniper is the instrument the rig measures with because its
headshot band is 0.3 units and its damage is a one-shot kill; the mechanism has
nothing to do with which gun fired.

## What the authority checks

Before `Judge`, the receiver validates match/authority stream identity and
shooter/victim lifecycle identity. `Judge` then performs the cheap checks first:

1. the slots are real, and nobody claims a hit on themselves;
2. the acknowledged frame exists inside rewind history;
3. `LaunchFrame` does not follow the frame in which the hit resolved;
4. claimed damage is within the weapon's bounded maximum;
5. the victim existed in the claimed generation/life and is worth damaging;
6. **the victim's body, as the authority's own history holds it at that frame,
   is within the reconciliation radius of the claimed hit point.**

Five is the one that makes the rest safe, and it is worth being precise about
what it compares. It is *not* a tolerance on aim. The claim carries where the
shooter's copy of the victim stood; the authority compares it against its own
record of where that player stood in the frame the shooter was looking at. So
the only thing a client can assert is that the two machines agreed about where
somebody was — which the authority can check, because it wrote the number. A
claim can only ever rescue a hit the authority's own history says was there to
be had.

2.0 units against a hunter 0.90 across and 1.60 tall: it covers a client whose
puppet is a frame or two of physics from the authority's (measured at up to
0.377 units a frame for a player in the air) and refuses a claim about somebody
two body-lengths away. 4.0 for a hit with no beam behind it — an alt form's
scythe, spin or trail, a bomb — which land at arm's length or in a blast.

## The grace window, and why a claim is not applied when it arrives

The authority may independently resolve the same projectile before or after the
claim arrives. Applying a validated claim immediately would therefore double a
real hit. Claims wait in a bounded grace window and are paired with the
authority's own hit by shooter/victim and, for beams, the exact
`LaunchFrame`.

The grace is **dynamic**, not the old fixed 18 frames. `GraceFor` is based on
the measured round trip plus margin, bounded by `MinGraceFrames = 24` and
`MaxGraceFrames = 72`. The client's prediction hold uses the same authority
latency model so presentation does not expire before a rescue/verdict can
arrive. A duplicate authority resolution retires the claim without applying
its damage again; only a validated claim the authority did not resolve is
rescued.

## The arbitration

> *Si client A fait un headshot sur client B, malgré la latence le kill doit
> toujours être pris en compte. Sauf si client B a tué client A avant.*

A beam claim carries both the acknowledged world in which the hit resolved and
the **launch frame of the shot**. Kill arbitration orders travelling shots by
launch world, not packet arrival and not the later impact ack. That is the
clock two people who killed each other are separated by:

- **dead now is not the test.** A player killed during the round trip still
  gets the shot they took before it happened. That is the whole point.
- a shot is **void** if its shooter had been put down by a hit aimed at a
  **strictly earlier** world than the one this shot was aimed at;
- two shots aimed at the same world **both count** — a trade, which is the
  honest answer to a trade.

Because the ordering is by the shooters' own stamps and not by which datagram
won the race, the outcome does not depend on arrival order. That is why claims
are resolved **in fire-frame order at the end of their grace window** rather
than one at a time as they land: applied on arrival, A-then-B and B-then-A give
opposite winners for the same duel.

The stamp is taken in `NetDamage.Note` — the authority runs it for every hit it
resolves — and `TrackDeaths` copies whatever is standing there onto the slot the
frame it goes down. A death with nothing behind it (the void, a crusher, the
clock) is stamped with the present, which is the truth for something nobody
aimed.

## Current interaction with prediction and Imperialist headshots

**Remote-player deaths remain authority-owned.** A local predicted lethal hit
is clamped to one health; the hit/feedback is immediate, but the body falls only
when authoritative state confirms it. `DeathEnabled` and the historical death
prediction flags are compatibility no-ops.

**Imperialist headshot classification has an authoritative reconciliation
exception.** If a validated shooter claim says Imperialist headshot and the
authority paired the same `LaunchFrame` to a body hit, the authority applies
only the missing damage difference exactly once. This preserves the validated
headshot without double-applying the body damage. Speculative HEADSHOT HUD text
is suppressed until the authoritative classification agrees.

**Historical note:** the rewind ceiling moved from 24 frames to 45 (400 ms → 750 ms) during the protocol-7 work. A claim is
a backstop, not a substitute: the cheapest hit registration is still the one the
authority finds itself, and the histogram above says the old ceiling was
refusing nine shots in ten at the latencies this work is about.

## Reading the numbers

The client's line:

```
hit claims: 16 declared, 2 applied, 14 already resolved (100.0% stood),
            0 void (dead shooter), 0 void (victim down), 0 refused,
            0 unanswered, 2 repeats
```

**`already resolved` is the healthy majority and `applied` is the rescue.** A
run where everything is `already resolved` is one where the rewind did the whole
job unaided, which is what a clean line should look like. `refused` climbing is
the number to chase: on a clean conscience it means the two machines' copies of
somebody have drifted more than 2 units apart.

The authority's, in the server log every 30 s:

```
sim: hit claims (as authority): 28 received, 2 applied (13 damage, 0 kills,
     0 headshots rescued), 24 already resolved, 0 from a shooter already dead,
     0 on a victim already down, 0 refused, 0 too old, 2 repeats
```

`kills rescued` and `headshots rescued` are the ones the complaint is about.
Each is a shot that landed on the shooter's screen and would have counted for
nothing.

## Historical measurements: 2026-09-14 (WSL, loopback with latency injected)

| Check | Result |
|---|---|
| `run-check.sh 70 Samus Weavel Sylux`, **no lag** | **0 mismatches**; 56 claims received, **54 already resolved**, 0 applied, 0 refused, 0 unanswered. This is what a clean line is supposed to look like: the rewind does the whole job and the claim rescues nothing |
| the same with `-noclaims -nointerp -relayedpuppets -nodeathprediction` | the one pre-existing `damage-taken` mismatch reproduces identically (26 against 13/15), so it is not this |
| `-hitrig duel`, **350 ms ± 80 with 2% loss**, 150 s, `TEST PADS` | **26 received, 12 applied — 1338 damage, 12 kills, 12 headshots rescued — against 3 already resolved.** The authority's own simulation found three of the fifteen real hits in that duel |
| the same run, client side | **`headshots: 14 predicted, 12 agreed by the authority (100.0%), 0 downgraded to body shots`**. The contemporaneous `kills predicted / undone` counters describe the historical lethal-prediction experiment and are not current behavior. The protocol-6 arms of the same rig read 63.6% and 75%, with 2-4 downgraded each |
| rewind, all protocol-7 arms | **clamped 0**, worst asked 40-43 against the 45-frame ceiling, 0 history misses |

**The duel is the run that matters and it is worth saying why.** In a duel at
350 ms the authority discards almost every shot, because whoever is hit first
has their return fire thrown away -- their copy on the machine keeping score is
already dead when the intent arrives. `3 already resolved` against `12 applied`
is that, measured. Neither the rewind nor the prediction can reach it; only a
claim can.

**`0 from a shooter already dead` in every run so far.** That is the
arbitration's permissive branch working -- two shots aimed at the same world
both count -- and the refusal branch is still unexercised. See
`.claude/KNOWN-GAPS.md`.

These runs shared the box with an unrelated 27-room sweep for part of the
afternoon, and three earlier arms were killed outright by the memory pressure.
Treat the timing figures as indicative and the counts as exact.

## The arbitration is about when the trigger was pulled

A claim carries two frames and they are not the same question:

| | what it names | used for |
|---|---|---|
| `AckFrame` | the world the shooter's screen was showing when the hit **resolved** | looking the victim up in the authority's history, and the age check |
| `LaunchFrame` | the world the shot was **fired** in (`BeamProjectileEntity.ModLaunchFrame`) | pairing the claim with the authority's own hit, and **the arbitration** |

The arbitration used the ack, and that is wrong for anything that travels. A
Missile is in the air for the better part of a second: judging it by the ack
asks *"were you already dead when your rocket landed"* instead of *"were you
already dead when you fired it"*, and voids a shot that left the gun before the
shot that killed its shooter had even been aimed.

**It is invisible on a fast weapon**, whose two frames are within a frame of
each other, which is exactly the shape the complaint arrived in: kills undone
with the Missile and the Magmaul, none with the Power Beam or the Imperialist.
Measured against the Japan server at 250 ms, `void (dead shooter)` went from
**20 to 0** in a three-minute run.

The same error had a second half on the authority's own side.
`NoteAuthorityHit` stamped a victim's death with `FireFrameOf(attacker)` --
the attacker's ack *now*, at impact -- so a slow projectile's kill recorded the
wrong world in `_lastHitFire`, and that number is what every later claim on
that player is judged against. It takes the launch frame too.

`LaunchFrameFor` produces the same quantity on both machines, which is what
makes the comparison sound: the authority's rewind target for somebody else's
shot, and the playout read point for this machine's own -- the same number the
intent acks.

**What is left in that bucket is a different question.** `void (victim down)`
is now the largest refusal (11 in the same run) and it is not arbitration: it
is `TakeDamage` refusing to hurt a corpse, judged at the moment the claim
arrives. It decides who gets the *credit* for a kill that happened either way,
not whether a body gets up, and it has not been touched.

## Two things a verdict is also good for

### Retiring the shooter's prediction exactly

A claim's id names one hit. Nothing else on this wire does: the snapshot
carries a count of hits on a victim and the slot of only the **last** attacker,
so a client's own hit followed inside one snapshot window by somebody else's is
never matched, and `NetHitPrediction.Confirm` is not even called for it. Its
debit then stays on the shooter's books while the authority's own health
already has it, the victim is drawn lower than they are, and a shot or two
later the client predicts a kill on somebody comfortably alive — reported as
*"my client thinks three missiles killed him"*, which is three points of stale
debit against a 99-health hunter.

So every verdict now retires the prediction its claim was declared under:
`Applied` and `Duplicate` as confirmed, every refusal and the six-send timeout
as denied. See `NETWORK-PREDICTION.md`, *Retiring a prediction*.

### Measuring whether the two machines agree about the damage

A claim carries the number the **shooter** computed for a shot, and the
authority already pairs it with its own hit for the same shot in order to
refuse it as a duplicate. So the ledger keeps the authority's damage beside
each entry, and `TakeLedger` hands it back on a match: the pair is a free,
exact measurement of whether the shooter's prediction and the authority's
resolution agree, per weapon, in a real match.

`DescribeAgreement` prints it on the server's `sim:` report, and a disagreement
is logged by name the first twenty times. Both sides run the same table over
the same weapon, so anything short of 100% means one of them is reading a
quantity the other was never sent. The five that existed were the charge tier,
double damage, the alt-form ram, the damage level and the affinity-weapons
rule — all five now travel (see `NETWORK-PREDICTION.md`, *The same test needs
the same inputs*). The one left is Weavel's halfturret health, which is why a
hit split with a turret is not allowed to predict a death.

A zero on the authority's side is not a disagreement: it means the ledger entry
carries no damage, which is every entry from a build before this and every hit
applied out of a claim.

### Imperialist headshot/body reconciliation

A matched duplicate normally means the authority already applied the shot and
the claim must contribute nothing. The Imperialist has one important exception:
the shooter can resolve the narrow head band while the authority's rewind
resolves the **same launch** as a body hit. In that case the ledger previously
discarded the 200-damage headshot claim as a duplicate of the 72-damage body
hit, leaving the victim alive even though the shot itself was confirmed.

For a claim that has already passed the ordinary stream, lifecycle, age,
damage-ceiling and geometry checks, and whose launch frame matches an
authoritative Imperialist hit, a headshot claim may reconcile **only the
difference** between claimed and already-applied damage. The body hit is not
applied twice: 72 already resolved plus 128 reconciled becomes the validated
200-damage headshot. The server report counts these separately as duplicate
headshots corrected.

## What it depends on, and what turns it off by accident

- **`-nohitprediction` turns claims off too.** A claim is a hit *this machine
  resolved*, and without prediction a client resolves none — `NetDamage.Suppress`
  throws them all away before `NoteHit` is ever reached. That is not a coupling
  to remove: there is nothing to declare.
- **`-nounlagged` turns claims off too, and more quietly.** `NetUnlagged.Record`
  returns early when the rewind is off, so the authority has no history, and
  `Judge` cannot check a claim against nothing — every one comes back
  `ResultTooOld`. Both switches are controls for measuring, and the pair is
  really one arm: without the rewind there is no world to agree about.
- **A server that is relaying rather than simulating drops claims.** It has no
  history either. The client repeats six times, gives up, and plays the game
  every build before protocol 7 played.

## Traps

- **A claim is not applied through a beam, because there is no beam.** It goes
  through `TakeDamage` with the shooter as the source, so the damage sequence,
  the scoreboard, the death and the snapshot that carries all three are the
  paths that already work. `NetDamage.SetClaimedBeam` is what stops the victim
  replaying a nameless hit; the Judicator's freeze travels as a claim flag and
  is re-applied by hand, since the affliction is produced inside `TakeDamage`
  from a beam entity that only ever existed on the shooter's machine. The
  knockback is **not** rescued: it is a velocity the authority's own resolution
  would have supplied and this hit did not have one.
- **`NetHitClaims.Tick` must run before `NetHooks.AfterSimulation`.** That is
  where the authority applies what it is rescuing, and a hit applied after
  `BroadcastSnapshot` sits a whole frame waiting for the next one.
- **The verdicts are buffered for a frame.** They arrive in two places —
  `Receive` answers what it can refuse on sight, `Tick` answers what waited out
  its grace — and a client repeating six claims in one packet would otherwise
  be answered with six datagrams.
- **The legacy `RunsTheMatch=false` compatibility path cannot authoritatively
  judge claims** because it has no server simulation history. Normal hosting
  never selects that path; normal hosted matches use isolated authoritative
  server processes.
- **Repeats are counted separately on both ends** or the outcomes do not add up
  to what was received, and a line reading `8 received, 1 applied, 2 already
  resolved` looks like five lost claims rather than five repeated ones.

### Impact momentum on rescued hits

Protocol 16 adds the hit's compact impact vector to `HitClaimPacket`. Before
this, an authority rescue called `TakeDamage` with a null direction: damage,
death and afflictions were restored, but knockback was silently discarded.
Missiles and affinity Battlehammer made that especially visible because their
splash direction is part of movement, not decoration. The shooter now sends the
exact vector its collision resolved; the authority bounds its magnitude against
the named weapon's damage-direction metadata before applying it. Non-beam claims
remain damage-only until they have an equally checkable impulse ceiling.
