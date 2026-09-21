# Instant hit registration

Code: `Mods/Network/NetHitPrediction.cs`. The other half of
[lag compensation](NETWORK-UNLAGGED.md), and it only works because that
exists.

## The fault

`NETWORK-UNLAGGED.md` fixed *where* a client's shot is resolved. It did
nothing about *when* the answer comes back, and said so:

> What it does **not** buy is a shorter wait for your own hit to register --
> that is a round trip wherever the authority sits.

So a client fires, the intent goes upstream, the authority resolves it, the
snapshot comes back, and only then does the victim flinch. At the Pi's 15 ms
nobody notices. At 150 ms a player empties a clip into somebody who reacts a
fifth of a second after each shot -- and the shots were landing the whole
time. The complaint is not "my shots miss" any more, it is "nothing happens
when I shoot".

**The authority never had this.** Its own gun resolves in the frame it is
fired. That asymmetry is the whole of what is removed here: everybody who is
not the authority resolves their own shots locally, immediately, and the
authority's answer arrives afterwards to confirm or overrule it.

With the normal server-authority architecture no player is the authority, so
this is what makes a shooter's own hit feedback immediate without giving that
player authoritative control.

## Why it is sound here

Because the rewind is already there. The authority puts every other player
back to the snapshot frame the shooter had applied -- which is exactly the
world the shooter's own machine is holding at the moment it fires. The local
resolution and the authority's rewound one are therefore the same test against
the same positions. Prediction is not a guess about what the authority will
say; it is the same calculation, run earlier, on the machine that already has
the inputs.

Without `NetUnlagged` underneath it this would mispredict as often as shots
used to miss, and it would be worse than useless: a hit shown and then taken
away is more confusing than a hit shown late.

### The same test needs the same inputs

"The same calculation, run earlier" is only true while both machines are
running it over the same numbers, and four of the inputs to a shot's damage
were not on the wire at all. Each was re-derived on the authority from the
buttons in the intent, or read out of a *local* setting -- which is the same
mistake the aim deltas and the ammo count were fixed by, with the same
symptom: the client runs a victim's health down faster (or slower) than the
authority does, and the shot after that predicts a kill nobody else sees.

| Input | What it does to the damage | Was | Is |
|---|---|---|---|
| `EquipInfo.ChargeLevel` | picks the charge tier -- and on a **partial-charge** weapon (the Power Beam: 6 damage at 36 frames of hold, 36 at 60, and every value between) it is a continuous multiplier | a count of frames the *relayed* trigger had been held, so the owner's count give or take the send interval, the jitter and whatever was dropped | sent, latched at the frame of the release | 
| `_doubleDmgTimer` | x2 on every weapon | a pickup, collected by each machine's own copy of the items on its own respawn timer, so the authority's copy of a shooter can simply not have one | sent as a state, re-asserted every packet |
| `_boostDamage` | the whole of an alt-form ram | rebuilt from the relayed boost button | sent, latched the same way |
| `GameState.DamageLevel` | x0.75 / x1 / x1.25 on **every hit of every weapon** | each machine's own `settings.json` | **pinned to medium, x1**, and published as such |
| `GameState.AffinityWeapons` | a different row of the damage table (an affinity Battlehammer deals 18 where the plain one deals 12) | each machine's own `settings.json` | published with it |

The first three ride in four bytes appended **past** `IntentPacket.Size`
(`StateSize`), so nothing about the protocol moves: every receiver reads
exactly `Size` bytes and then asks whether there are four more, and a build
from before this finds none and behaves as it always did. `HasState` is what a
receiver checks -- writing zeros for a sender that said nothing would take a
puppet's charge and powerups *away*. Applied on the authority only, through
`PlayerEntity.ModSetShotState`, for the same reason the alt-form state is: a
client correcting a puppet from two sources at once is pulled both ways.

The last two ride in spare bits 4-6 of a flags byte that was already being
sent, with **zero meaning "this server did not say"** -- so an older server
changes nothing, and the rule only takes effect once the server is redeployed.
The damage level is what marks the packet as stating anything at all, and it
always says medium: it is pinned to x1 on every machine
(`GameState.DamageLevel` gets 1 and discards what it is set to), because the
only thing three answers ever bought was three ways for two machines to
disagree about every shot in the match. Upstream's console menu still has the
row; assigning it does nothing.
`-affinityweapons` sets the one that is still a choice; a hosted game takes the
host's own Match rules. There is no flag for the damage level: three answers
only ever bought three ways for two machines to disagree about every shot in
the match, so there is one, and the broadcast is what puts right a client that
has another from somewhere.

The one input still not replicated is Weavel's `_halfturret.Health`, which
decides how a hit on him is split. See *Not through a halfturret* below.

### Measuring whether they agree

The authority can check this for nothing, because a hit claim already carries
the number the **shooter** computed for a shot and the authority already pairs
it with its own hit for the same shot in order to refuse it as a duplicate. So
the pair is compared, per weapon, and printed:
`NetHitClaims.DescribeAgreement`, on the server's `sim:` report. Both sides run
the same table, so anything short of 100% agreement means one of them is
reading a quantity the other was never sent -- and the weapon it happens on
says which.

## Current rules

1. **Only the local player's own outgoing hit is predicted.** Incoming damage
   from another player waits for authority. Local self-damage and environmental
   self-death are special because source, target and input are local.
2. **Remote lethal prediction is clamped to 1 HP.** Flinch, hit marker and
   nonlethal damage can appear immediately, but the authority owns the actual
   remote death. `DeathEnabled` is now a compatibility no-op;
   `-deathprediction` and `-nodeathprediction` are accepted so old scripts keep
   running, but do not change this rule.
3. **Prediction never owns durable score or match outcome.** Authoritative
   snapshots/state settle health/score and only authoritative state may end the
   match.
4. **Every remote prediction is reconciled.** Hit claims/verdicts identify the
   exact prediction and retire its hold whether the authority resolved it,
   rescued it, or refused it.
5. **Headshot presentation waits for authority confirmation.** The client may
   locally classify a headshot for reconciliation, but speculative HEADSHOT HUD
   text is suppressed until the authoritative result agrees.

### Historical lethal-prediction experiment

Protocol-7-era builds briefly allowed a predicted hit to kill another player
locally. Real-line testing exposed the worst failure mode: a local body could
fall, the authority could disagree, and a later snapshot could stand it back
up. Current code deliberately leaves remote victims at one health until the
authority confirms death while preserving immediate nonlethal feedback. Dated
tables below that mention `DeathsPredicted`, `DeathsUndone`,
`-deathprediction` or `-nodeathprediction` describe that historical A/B, not
current behavior.

## What is held

Predicting a hit and then letting the next snapshot assign the authority's
health straight over it is a prediction that lasts one frame. The flinch is
instant, the mark lands -- and the bar springs back up, because
`ApplyState`'s `player.Health = state.Health` is describing a world one round
trip old. At the Pi's 15 ms nobody sees it; at Japan's 270 ms it is most of
what "it is not registering" is actually describing.

So three things are held against the snapshot until the authority catches up:

| Held | How | Where |
|---|---|---|
| the victim's health | the authority's number less the damage of every prediction still outstanding for that slot | `HealthFor`, called from `ApplyState` |
| the victim's health, again | never above what was last drawn, while this machine is still predicting hits on that slot | `_shownHealth`, in `HealthFor` |
| this machine's own player after a predicted self-death | the snapshot is not allowed to resurrect that local self-death until lifecycle catches up. Remote victims are not predicted dead in the current build; remote lethal damage is held at 1 HP for authority | `HeldDead`, checked in the `!wasInPlay` branch |
| this machine's own drained health | the authority's number plus every drain credit still outstanding | `LocalHealthFor` |

### The floor, and why the debit is not enough

The debit alone does not hold a continuous beam, and the Shock Coil is where
that is impossible to miss: **drain somebody, release the trigger, and their
bar visibly climbs back up.** It is not a mispredicted hit.

`Confirm` retires as many predictions as the snapshot says landed, and the
authority resolves several hits of a Shock Coil for every one this machine
resolves -- the damage is divided by 32 and dithered off the frame counter, so
the parity that produces a damaging hit is not the same parity on two machines
(the authority landed 131 where a client resolved 28, measured below). One
snapshot therefore retires *everything* outstanding, the debit falls to
nothing, and what is drawn is the authority's number: correct, and half a round
trip behind what this machine has already shown. While the trigger is held the
next hit covers the gap. The moment it is released nothing does.

So the number is also floored by what was last drawn, for as long as this
machine is still predicting hits on that slot (`HoldFrames` since the last
one). The floor only ever refuses a **rise** -- it cannot hide damage, and
every point the authority takes off still shows the moment it is reported. It
costs at most one hold window of lag on a victim who picks up health while
being shot at, which is the trade this file makes everywhere else too.

It does not make the two machines *agree*; it stops the disagreement being
drawn as a health bar going the wrong way. Making a continuous weapon resolve
the same hits on both machines is a separate piece of work -- see
`KNOWN-GAPS.md`.

`HealthFor` never returns zero on its own account: assigning zero health is
not a death -- it skips the whole death path -- so a hold that ran the bar to
the bottom would produce a player who is neither alive nor dead. A local predicted self-kill goes through `TakeDamage` like every other hit,
and `HeldDead` is what keeps that local player down until authoritative
lifecycle catches up. Remote victims never enter this state from prediction.

**The hold window is not the pending window.** `PendingFrames` is 120 -- two
seconds, deliberately generous, because a confirmation that arrives late is
still a confirmation and counting it as a miss would flatter nothing.
`HoldFrames` is one measured round trip (`NetSession.SlotPing` for the local
slot) plus twelve frames, **plus `NetHitClaims.GraceFrames` whenever claims are
live**, clamped to 15-120. The grace is there because a rescued hit is applied
at the end of the authority's duplicate window rather than on arrival, so its
confirmation is one grace later than an ordinary hit's -- and a hold that
expires first is the resurrection the claim exists to stop, reintroduced by the
clock instead of by the authority. A mispredicted hit is a *wrong
health bar*, and a wrong health bar has to right itself in about the time the
authority takes to answer rather than in the time it takes to be certain it
never will. At Japan's 270 ms that is 28 frames; with no ping measured yet it
is the 15-frame floor.

### Retiring a prediction: by name, not by the snapshot

A prediction has to come *off* the books the moment the authority accounts for
the hit, or its debit is taken off a health that already has it in -- and the
snapshot cannot do that job. `PlayerState` carries a **count** of hits on a
victim since the last one and the slot of only the **last** attacker, so
`NetDamage.Replay` calls `Confirm` at all only when `state.AttackerSlot` is
this machine. A hit of this machine's own, followed inside one snapshot window
by somebody else's, is never matched. Its debit stays, the victim is drawn
lower than they are, and since `HealthFor` floors at 1 it takes only a few
points of stale debit to have the next shot predict a kill on somebody who is
comfortably alive.

That is exactly the report **"my client thinks three missiles killed him"**.
Three uncharged missiles are 96 damage against a hunter's 99: three points of
stale debit are the whole of the error, and in any fight where more than one
person is shooting the same target they accumulate.

So each prediction now carries the id of the hit claim it was declared under
(`_pendingClaim`), and a verdict retires that exact one (`Settle`):

| Verdict | What it means | What it does here |
|---|---|---|
| `Applied` | the authority made this claim real | retire, count confirmed |
| `Duplicate` | the authority resolved the same shot itself | retire, count confirmed |
| `DeadShooter` / `DeadVictim` / refused | the arbitration says it did not happen | retire, count denied, drop `_shownHealth`'s floor |
| no answer at all, after six sends | the verdict went missing, not necessarily the claim | retire, count denied |

Both paths stay idempotent: retiring clears the claim id, so a later verdict
for it finds nothing, and `Settle` marks the entry **spent**, so the snapshot
walking the head past it does not count it twice. The verdict and the snapshot
race each other, and whichever loses is absorbed by `_settledCredit` rather
than counted as a second event -- without it every claim answered by its
verdict first read as "the authority credited a hit this machine never
predicted", and `Unpredicted` climbed to roughly the size of `Confirmed`.

### The floor was where the error actually lived

Measured against the Japan server at 250 ms with three clients, before any of
this: **the drawn bar sat a mean 26 and a worst 61 points below the
authority's**, and it was never above it -- the client always overestimated the
damage, never under. Since `lethal` in `NoteHit` is decided against that drawn
number, and `HealthFor` floors it at 1, that is a client that kills people the
authority refuses to kill. Reported as *"la prédiction est mauvaise comparé à
ce que j'ai avec mon client"*, and it is not a damage calculation at all.

Two causes, both in the floor, and the tally splits them: `floor held` is how
much the floor lowered the drawn number, and `disagreed with nothing
outstanding` is how much of that was left when the debit was empty. The two
were the same number.

- **The floor refused a rise the authority itself was reporting.** A victim who
  picks up health or respawns has a bar that went up for a reason that has
  nothing to do with this machine's predictions -- and the floor is re-armed by
  every hit predicted on that slot, so with a fast or continuous weapon it
  never lifts at all. `_lastAuthorityHealth` now lifts it the moment the
  authority's own number rises.
- **A verdict settled the picture as well as the books.** `Duplicate` means the
  authority resolved the shot itself; it does *not* mean the snapshot carrying
  the lower health has arrived, and those are half a round trip apart. Dropping
  the debit on the verdict left the bar standing on the floor alone for that
  window -- a charged missile's 48 points of it. A confirmed settle now marks
  the entry answered and **keeps its damage in the debit**, to be retired by
  the snapshot that actually carries the health. A refusal still empties it at
  once, which is the whole point of a verdict.

| run (Japan, 250 ms, 3 clients) | JP-A | JP-B | JP-C (Shock Coil) |
|---|---|---|---|
| before | 2770 pts, worst 48 | 4369 pts, worst 61 | 4193 pts, worst 32 |
| floor lifted on a rise | 5301, worst 48 | 137, worst 18 | 2118, worst 10 |
| + debit kept to the snapshot | **0, worst 0** | **6, worst 6** | **3, worst 1** |

`floor held` fell from 120/218/172 samples to 0/1/5: with the debit doing its
job properly the floor is very nearly never needed, which is the sign that it
was covering for the imprecise retirement all along. 0 kills undone, scoreboards
agree, 90-100% of predictions confirmed.

**`DescribeHealth` is the line that found this**, and nothing before it printed
the two bars side by side. Read `drawn low by` first: it is the direction that
predicts kills the authority refuses, and `high by` is the harmless one.

### A shot that travelled does not decide a death

The one fault that accounting could not reach, and it is not an accounting
fault at all.

The authority spawns a shot into the world its shooter was looking at and then
walks it forward to the present in one go (`NetUnlagged`'s catch-up), so it can
have the whole flight resolved inside the frame the trigger was pulled. The
shooter's own copy is an ordinary projectile crossing the room against puppets
held a few frames behind the newest snapshot. **For anything that travels, the
authority's answer -- and the health that comes with it -- routinely arrives
first.** The client adopts a bar that already contains the hit, and a moment
later its own copy of the same shot lands on top of it.

Caught with both logs side by side, local server, 250 ms injected:

```
20:53:48.325  SERVER  resolve: 32, launch 4955, health 35 -> 3
20:53:48.45   client adopts health 3
20:53:48.542  CLIENT  lethal: 32 damage, drawn health 3, authority last said 3
20:53:50.134  CLIENT  kill undone (64 frames old, hold 64)
```

The claim for that hit is then matched as a duplicate of the authority's own
and answered `already resolved`, so it is counted **confirmed** -- which is why
every tally read 100% while six kills in seven were being undone.

**The snapshot carries a count of hits and no identity for the shot behind
them**, so a client cannot tell its own already-resolved shot from its next one
except by counting and by time. Three rounds of exactly that were tried -- a
preemption credit keyed on unpredicted hits, an expiring settle credit, a
"beam was already in flight" test -- and they took the undone kills from six in
seven to about one in two and stopped there. The last of them also suppressed
**46 good predictions out of 49 on loopback**, because at a low ping the
authority beats a client by a frame on almost everything. All three were
removed. Making this exact needs `ModLaunchFrame` in `PlayerState`, which is a
protocol change.

What is exact is the flight time itself. `BeamProjectileEntity.Age` at the
moment of the hit says whether there was a race to lose, so a shot older than
`TravelFlight` (three frames) does not predict a death: the damage is clamped
to leave the victim on one point and counted in `LethalHeld`, exactly as
`-nodeathprediction` does for everything. The hit is still instant -- flinch,
knockback, mark and bar all land on the frame it is fired -- and only the body
falling waits for the authority.

A Power Beam bolt or an Imperialist round covers a duel's range in one or two
frames and is untouched, **which is the split the complaint arrived in**: kills
undone with the Missile and the Magmaul, none with those two.

| Missile volley, Japan, 250 ms | kills predicted | undone |
|---|---|---|
| before | 7 | 6 |
| after | 0 | 0 |

`-hitrig missile` is the rig: one client empties one weapon into one target at
16 units and nobody else fires, so the authority's health drop for that victim
is this client's damage and nobody else's.

### Not through a halfturret

Weavel's lower half takes part of every hit that reaches him, and how much
depends on `_halfturret.Health` -- which lives on the authority and is in no
packet. A client splitting that damage is splitting it against a number it made
up. The hit is still predicted and still claimed, because the flinch and the
mark are right either way; what it may not do is decide a **death** on
arithmetic the authority will redo differently, so a lethal prediction carrying
`DamageFlags.Halfturret` on a victim with a live turret is clamped to leave
them on one point and counted in `LethalHeld`.

## Your own splash, on you

A rocket jump is not damage that arrives late -- it is a jump that does not
happen. The push comes out of `TakeDamage` (`Speed += direction * 0.4f`), so
suppressing the hit suppressed the jump with it, and at Japan's 270 ms the
player left the ground a fifth of a second after the missile went off. Same for
a bomb jump, and for every weapon carrying `WeaponFlags.SelfDamageUncharged` --
the Missile, the Magmaul, the Battlehammer and their charged forms.

There is **no shooter-side recoil in this engine**: nothing pushes you for
firing, and the only `Recoil` in the tree is a platform's. The push a player
means by "recoil" is this one -- their own splash, on themselves.

`Predicts` used to refuse any hit whose victim was the local player. That
refusal is rule 2 and it is already made by the line under it: damage from
somebody else has an owner who is not this slot. What the extra clause actually
excluded was the one hit that is **entirely** local -- source, target and input
all on this machine, nothing to guess about anybody, and no rewind to bet on.
It is arithmetic, not a prediction, and it is the hit whose feedback matters
most on the frame it happens.

What had to move with it:

| | |
|---|---|
| `Predicts` | drops the victim clause; the owner clause is the rule |
| `NoteHit` | no longer returns early on `attacker == victim`, and a self-inflicted lethal hit **kills**, whatever `DeathEnabled` says |
| the mark | not raised for a self-hit: the X answers "did that land on somebody" |
| `LocalHealthFor` | subtracts the outstanding self-debit as well as adding the drain credit -- nothing calls `HealthFor` for the local slot, so without this the health came off for one frame and the next snapshot handed it back |
| `NetDamage.Replay` | `mine` no longer excludes the local slot, or the authority's copy of a hit this machine has already applied would take the health twice |
| `SelfPredicted` / `SelfConfirmed` | counted apart from `Predicted`/`Confirmed`: the percentage is a claim about shots aimed at other people over a wire, and a hit resolved on the machine that fired it would only flatter it |
| `NoteRespawn` | now called for the local slot too, so a debit from the last life cannot come off the health of the new one |

**A self-inflicted prediction is the one that still kills, and this is not
`DeathEnabled`.** It used to be the other way round: the lethal case was
clamped on the grounds that the local death path -- the death camera,
`_deathCountdown`, `PausePrevented`, the respawn -- is far more to take back
than a puppet lying down. That is true, and it is not the question. The
question is how often there is anything *to* take back, and for a hit whose
source, target and input are all on this machine the answer is never: the
authority runs the same arithmetic on the same inputs and reaches the same
number a round trip later. Meanwhile the cost of clamping it was a player who
had already watched themselves go over the edge and then stood there for a
quarter of a second waiting to be told.

**And the void is the case that made it worth doing.** Falling out of the map
is `TakeDamage(0, DamageFlags.Death, direction: null, source: null)` -- no
attacker, no projectile, nothing `OwnerOf` can resolve -- so `Predicts` refused
it outright and every fall on every client waited a full round trip. `Predicts`
now takes the damage flags and lets a source-less hit through **for this
machine's own player, and only when it carries `DamageFlags.Death`**: the void,
a kill plane, a crusher, a room sending `Message.Death`. The chip damage from
standing in lava carries no such flag and stays with the authority, where a
rate that depends on frame parity cannot make two machines disagree about a
health bar.

What that costs, in exchange:

| | |
|---|---|
| `Predicts` | takes `DamageFlags`, and returns true for a source-less lethal hit on the local slot |
| `NetDamage.Suppress` | takes and forwards them |
| `NoteHit` | takes them too -- a fall is zero damage, so `damage >= victim.Health` would file the one death that is certainly right as a scratch. `DamageFlags.Death` is lethal whatever the number says. It also no longer returns early on a null attacker, because a fall has none |
| `HeldDead` | consulted for the local slot as well, and ignores `DeathEnabled` there. Without it the next snapshot stands the corpse straight back up -- the resurrection this whole switch exists to stop, on the one player who is looking at it |
| `LocalHealthFor` | returns 0 rather than the authority's number while the local player is held dead. Belt to `HeldDead`'s brace: the `ApplyState` branch that would call it returns first |
| `NetHitPrediction.NoteDeath` | called from `ApplyState`'s `!spawned` branch when the authority reports health 0. A fall names no attacker, so nothing else retires the pending lethal entry, and `HeldDead` would go on refusing the respawn that follows it a moment later |
| `SelfDeathsPredicted` | counted apart from `DeathsPredicted`, for the same reason `SelfPredicted` is counted apart from `Predicted` |

## The drain

The Shock Coil -- `WeaponFlags.LifeDrainUncharged`, Sylux's affinity weapon --
gives whatever it deals to whoever fired it, in `BeamProjectileEntity`'s
player-collision branch. On a client that heal ran locally and was assigned
straight back off again by the next snapshot, so the one weapon in the game
whose whole point is the health it buys was the one weapon whose payoff
arrived a round trip late while its damage arrived instantly.

`NoteDrain` records what the shooter *actually gained* -- `Health` before and
after `GainHealth`, not the damage that was offered, because the halfturret
splits a heal in two and a full tank takes none of it -- and `LocalHealthFor`
adds the outstanding credit on top of the authority's number.

**A credit, not an absolute.** Holding "the health I think I have" would hide a
rocket that lands while the beam is running; adding a credit to what the
authority says means every point of damage taken still shows the moment it is
reported, and a stale credit is worth a point or two for a fraction of a
second. The credit expires on `HoldFrames` like everything else here, which is
about when the authority's own number starts including it.

The Shock Coil resolves a hit **every other frame** for as long as the trigger
is held, which is why `PendingCapacity` is 24 rather than the 8 it started at:
at 300 ms of round trip nine hits are outstanding before the first answer
arrives, and an overflow no longer merely loses a statistic -- it drops that
hit's damage out of the health the victim is being held at.

## The mechanism

| Piece | Where |
|---|---|
| `NetDamage.Suppress(victim, source)` | the top of `TakeDamage`. Asks `NetHitPrediction.Predicts` before throwing the hit away, so a "no" costs exactly what it always cost |
| `NetHitPrediction.NoteHit(victim, attacker, ref damage)` | beside the existing `NetDamage.Note`, which is the last point at which the damage is final and the death has not been decided. Clamps, records the pending prediction, and raises the mark |
| `NetHitPrediction.Confirm(slot, landed)` | inside `NetDamage.Replay`, **before** its "already down here" return: a hit the authority credits this machine's player with, on a victim it already predicted, is consumed and **not** shown a second time. `landed` is the snapshot's own count of hits since the last one, so two of this machine's hits inside one snapshot window retire two predictions rather than one -- capped at what is outstanding, so a burst that included somebody else's hits cannot retire more than this machine predicted |
| `NetHitPrediction.HealthFor` / `HeldDead` / `LocalHealthFor` | `NetPlayerBridge.ApplyState`, on the three lines that used to assign the authority's health and spawn a puppet unconditionally |
| `NetHitPrediction.NoteDrain(healer, gained)` | `BeamProjectileEntity`'s life-drain branch, beside the `GainHealth` it is reporting |
| `NetHitPrediction.ForgetSlot` / `ForgetPending` | `NetSlotManager.Activate`/`Deactivate` and `NetRoomChange`: a prediction describes a hit on a particular player in a particular room, and a lethal one kept across either would hold the slot's next occupant dead on this screen |
| `NetHitPrediction.Settle(slot, claimId, confirmed)` | `NetHitClaims.ApplyVerdicts` and its outbox timeout: the **exact** retirement, by the id the claim was declared under. The snapshot's is approximate and silently misses a hit followed by somebody else's inside one window |
| `NetHitPrediction.Tick()` | `Renderer.OnSimulationFrame`, next to `NetHooks.AfterSimulation` -- outside the network hooks because the mark is drawn offline too |
| `NetHitPrediction.DescribeByWeapon()` | the netcheck report. The tally a weapon at a time, which is the only form of it that can answer "the prediction is wrong with X" -- one weapon resolving differently on the authority is invisible in an aggregate dominated by whatever was fired most |

Nothing is rolled back, because nothing durable is ever written. Health is
assigned from the snapshot on the very next `ApplyState` -- the same line that
has always corrected it -- and the afflictions are flags on the wire that the
same snapshot re-asserts or clears. A prediction the authority never confirms
simply ages out.

### What counts as a shot

`OwnerOf` resolves a damage source to the player who aimed it:

- a **beam** whose owner is a player,
- a **beam** whose owner is a halfturret (Weavel's turret shoots for him),
- a **bomb**, which belongs to whoever laid it,
- a **player**, which is how an alt form's attack is delivered -- Weavel's
  scythe, Spire's spin, Sylux's trail. These are the hits that feel worst
  late, because they land at arm's length: the whole attack is over before the
  answer to it comes back. Missing this case was worth 11 predictions of 11 in
  one run, and until it was added a Weavel client predicted **nothing at all**
  while the authority credited it with hits.

### The mark

Four bars in an X around the crosshair, `Renderer.DrawHitMarker`, twelve
frames with a fade over the last six. Same flat-fill trick as
`DrawCustomCrosshair` -- the RTT shader's `fade_color` path, no asset, no
sprite -- and sized off the crosshair's own scale, so a player who asked for a
big crosshair gets a mark to match. Drawn over whichever reticle is in use,
because "did that land" is not a question about which crosshair somebody
picked.

It is drawn on **every** machine and in every match, offline included: on the
authority and in an offline match the hit has actually happened, and there is
no reason the confirmation should depend on which machine is running the
match. Not in the story mode, which is the DS's game and has no such mark.

## Measuring it

Every `-netcheck` report carries a line:

```
hit prediction: 26 predicted, 24 confirmed (92.3%), 2 denied, 0 unpredicted,
                0 kills left to the authority
```

- **confirmed** -- the authority agreed. This is the number.
- **denied** -- predicted here, never confirmed. An upper bound on
  mispredictions rather than a count of them: a snapshot names only the *last*
  attacker, so a hit of yours that landed in the same snapshot window as
  somebody else's is invisible to `Confirm` and times out looking like a miss.
- **unpredicted** -- the opposite error, and the one that costs nothing: the
  authority credited a hit this machine did not resolve locally, so it is
  shown when it arrives, which is what every hit used to do. Weavel's
  halfturret produces these on purpose -- it picks its own targets on every
  machine, so its shots are not the same shots.
- **kills left to the authority** -- remote lethal hits clamped to one point of
  health (`LethalHeld`). This is the current behavior. Older report formats may
  also expose predicted/undone kill counters from the historical lethal A/B;
  current normal runs keep those legacy counters at zero.
- **self-kills predicted** -- appended whenever the local player dies from a
  locally resolved self/environment event. This remains separate from remote
  lethal handling.
- **health drained ahead** -- points of Shock Coil drain credited before the
  authority reported them. Absent when the run never fired one.

`-nohitprediction` is the control and `-nohitmarker` turns off only the mark;
prediction and lag compensation are enabled by default. Remote-player deaths
are authority-owned. The legacy death-prediction switches are accepted no-ops
and do not affect self-damage/self-death.

`hit claims:` is the line to read beside this one. A prediction and a claim are
the same event seen from two ends -- the prediction is what this machine showed,
the claim is what it asked the authority to make real -- so `denied` climbing
while `refused` stays at zero means the two machines disagree about a hit
neither of them is arguing about, which is a different fault.

### Historical measurements: 2026-09-08/09 (WSL, loopback)

| Check | Result |
|---|---|
| `run-check.sh 120 Samus Weavel Sylux`, no lag | **97.6%** confirmed (42 predicted), and **87.0%** on a second run (23 predicted) |
| `run-unlagged.sh 90 150 on`, 150 ms on two clients | **92.3% / 88.9%** on one run and **86.0%** (114 predicted, the largest sample taken) on another; the rewind itself unmoved at 163 ms against 150 injected, 0 history misses |
| `run-serverauth.sh 100 Samus Weavel Sylux`, simulating server | **0 mismatches**, scoreboards agree, and all three clients predicting: 100%, 100%, 88.9% |
| 5% packet loss and 120:30 ms of jittery lag on every client | **89.0%** (118 predicted) and 100% (16 predicted); damage pipeline exact (118/9/29 resolved, 118/9/29 replayed); 0 history misses at a 311 ms mean rewind |
| damage pipeline | matched exactly on the runs where it was clean before (24/26/9 resolved, 24/26/9 replayed) |
| the same scenario with `-nohitprediction` | the pre-existing `shooting` mismatch reproduces identically, so it is not this |

**Take the range, not the best run: 86-98% confirmed, and the small samples
not at all.** The scripted tour does not fire the same shots twice -- one pair
of runs differed by a factor of five in predictions made -- so a run with six
predictions in it says nothing, and the honest summary is that between one in
seven and one in forty predictions is not confirmed, with the share rising
with latency. The 114-prediction run at 150 ms is the one to quote.

Before the alt-form case was added, the same 150 ms instrument read 70% and
81%; the difference is entirely Weavel's scythe being predicted rather than
waited for.

Two mismatches the harness reports on these runs -- `shooting` against the
authority, and `damage-taken` between clients -- are both in the rig's own
history from before any of this, `damage-taken` with the identical numbers
(17 against 7). So is the authority's `Resolved` count reading lower than the
clients' `Replayed`, which reproduces exactly with `-nohitprediction`. None of
the three is this feature; all three are worth someone's time on their own.

### Historical measurements: 2026-09-09 against Japan (`13.78.14.98:27890`, simulating, 267-274 ms)

The held prediction, predicted death and the drain credit were written and
measured against a real line rather than a loopback with lag injected into it.
Two scripted clients, 70 s, `MP3 PROVING GROUND`:

| Client | Result |
|---|---|
| Sylux (Shock Coil) | **28 predicted, 28 confirmed (100%), 0 denied**, 103 unpredicted, **23 health drained ahead** of the authority |
| Samus | **3 predicted, 3 confirmed (100%)**, 0 denied, 14 unpredicted |
| three clients, 45 s, over a rotation | **1 kill predicted, 0 undone**; Sylux 3/3 and 3 health drained ahead |
| two clients, 60 s, with self-damage predicted | Samus **3 self-hits predicted, 2 confirmed** (its own missile splash); Sylux 8/8 and 7 health drained ahead; both `PASS` |

**0 denied at 270 ms, against the 86-98% the loopback instrument used to
read.** That is the batched `Confirm` rather than the hold: retiring one
prediction per snapshot left the rest to time out looking like misses, and
`landed` is how many actually landed. Do not read the drop in `denied` as a
change in how often a prediction is *right*.

`unpredicted` was high on Sylux in this run: the authority landed 131 Shock Coil
hits where this client resolved 28. The `Continuous` damage dither then used
each machine's `scene.FrameCount`, so their damaging frames could differ. It now
uses a per-slot firing clock seeded from the owner's fresh intent and advanced
once per simulation frame, shared by ammo, base damage, ramp and the beam's
enemy hit gate. Later intents do not re-anchor a held stream. Offline and
stale/invalid intents keep scene timing.
Protocol 8 refuses mixed builds with the previous timing; packet layout is unchanged.
`dotnet run --project tools/continuous-phase-check/continuous-phase-check.csproj`
checks the shared phase sequence, stale/invalid fallback, full dither cycle and
counter rollover without game assets. It does not measure network hit agreement.

The network measurement it does not do has since been run, and it does not
support the 131-against-28 above. `-hitrig shockcoil` is one client emptying a
held Shock Coil into one target, which makes the authority's health drop for
that victim this client's damage and nobody else's, so the two totals subtract.
13 arms of 180 s, interleaved, against the Japan box at a measured 275 ms with
a simulating server on TEST ARENA. Every arm is self-validating: a protocol 7
client cannot connect to a protocol 8 server at all, so a run that produced
numbers was necessarily against the matching build -- and five arms later in
the campaign proved it by being refused outright when a redeploy silently
failed.

| | before (p7) | after (p8) | rel |
|---|---|---|---|
| hit-count agreement | 70.7% +/- 5.7 | 74.2% +/- 2.9 | +4.9% |
| damage ledger | 69.5% +/- 8.6 | 72.9% +/- 3.3 | +4.9% |
| damage *per hit* agreement | 97.8% +/- 5.2 | 98.2% +/- 2.7 | +0.4% |
| unpredicted hits | 31.5% +/- 6.7 | 27.1% +/- 2.5 | -13.7% |

Read the third row first, because it changes what the other three mean.
**Damage per hit already agreed to within 2% before any of this**, so the
disagreement was never about what a landed hit was worth -- it is entirely
about which frames landed one, which is what a shared phase is for. It also
retires the obvious next suspect before anybody spends a week on it: the homing
ramp adds 0 to 4 on a base of 10 out of `ShockCoilTimer`, which is local state
on no packet and reset by each machine's own target test, and if that were
diverging it would show up in this row. It does not.

**The means are not significant** at this sample size -- every |t| < 2 -- and
the honest summary is that all four move the right way and none is proven.
**The spread is the robust result**, and it is the signature the mechanism
predicts rather than a second way of saying the same thing: the old dither ran
off each machine's own frame counter, so every session drew its own offset and
landed anywhere in a wide band. Hit-count agreement spans 18.0 points before
and 8.1 after (F = 3.8); unpredicted hits span 22.4 and 6.5 (F = 6.9). A weapon
that behaves the same way twice is worth more than five points of mean.

What none of it explains is the residual: the client still resolves about a
quarter fewer hits than the authority, on both builds. That is target
acquisition or the beam's own collision test, not the clock, and it is
untouched here.

The one `RESULT: FAIL` on these runs is `their form stayed wrong for 69 frames
in a row`, which is the open question in `.claude/KNOWN-GAPS.md` about form
reconciliation with a simulating server, measured there at 78 frames. Not this.

**The box is the instrument's limit, not the server.** Three clients on this
WSL machine run the tour at about 9 fps, so snapshots arrive faster than they
are consumed (16-20k received against 2700-4200 frames) and every number above
is a small sample. Two clients is the honest maximum here.

### Verified 2026-09-09 against Japan, with predicted kills taken back out

`13.78.14.98:27890`, simulating, 262-278 ms. Two scripted clients per run, a
few seconds apart, `-netdebug`, 35 s (2100 frames) unless said otherwise.
Every hunter, and the tour's fifteen phases, so alt forms and the whole weapon
rotation are in each run.

| Pair | Shots, own against what the other client saw | Prediction |
|---|---|---|
| Samus / Sylux | 20↔21 and 216↔208 | Sylux **5/5 (100%)**, 0 denied, **1 self-kill predicted**, 5 health drained ahead. Both PASS |
| Kanden / Trace | 33↔35 and 26↔21 | one hit apiece; nothing predicted. Both PASS |
| Noxus / Spire | 19↔20 and 6↔6 | nothing predicted. Noxus FAIL: see below |
| Weavel / Guardian | 27↔30 and 65↔56 | Weavel **2/2**, **1 self-kill predicted**, 2 self-hits (2 confirmed); Guardian **7 predicted, 6 confirmed (85.7%), 0 denied, 1 kill left to the authority**. Deaths cross exactly, 4↔4 and 2↔2. Weavel FAIL: see below |

Beam-frames track the shots (683↔660, 374↔380, 77↔77), and so do the weapon
switches, the bombs and the alt-form frames within the tour's usual spread.

**The two FAILs are the harness flagging a feature the other player never
performed**, and both clients agree it never happened: Spire spent 1571 frames
in alt form and unmorphed **0** times (the Kanden/Spire entry in
`KNOWN-GAPS.md`), so "theirs 0 unmorph" is correct; Guardian's own report says
`alt form on 0 frame(s)`, so "theirs 0 alt form" is correct too.

**`1 kills left to the authority` is the change working on a real line** --
`LethalHeld`, a killing blow clamped to leave the victim on one point of
health, with the flinch and the mark still landing on the frame it was fired.
**`1 self-kill predicted`**, twice, is the other half.

### The lethal half both ways, 70 s each

Samus and Sylux, same server, once on the new default and once with
`-deathprediction`:

| | Result |
|---|---|
| default | Samus 6 predicted / **0** confirmed / 6 denied; Sylux 3/1/2. **0 kills left to the authority** in both -- no killing blow was landed in that run |
| `-deathprediction` | Samus 4/4 (100%), **0 kills predicted**; Sylux 5/4 (80%), 1 denied, **1 kill predicted, 0 undone** |

**Read almost nothing into those confirmation rates.** Six predictions and
three predictions are the sample sizes this file has always warned about, and
one client reports `Replayed` as zero for the slot it was shooting at while
claiming confirmations on it -- an open question in `KNOWN-GAPS.md` that has to
be settled before any percentage measured from a first-joining client means
anything. The numbers that *are* clean here are the ones that do not depend on
it: deaths cross exactly on every run that had any, and `undone` is 0.

**What these runs do not do is reproduce the fault that took the switch out.**
Killing the same opponent twice for one kill was seen while playing; the
scripted tour kills once or twice in seventy seconds and never landed a
misprediction to undo. The case for the default is the played report and the
argument in *The rule that came back*, not a measurement -- and the honest
statement is that `DeathsUndone` has never been caught above zero by this
instrument, on either setting.

### The check after `NoteDeath` was made surgical

40 s, Samus and Sylux, MP3 PROVING GROUND, 276-280 ms, no rotation. The point
of it is the arithmetic: `NoteDeath` used to clear the slot outright, which
dropped predictions nobody had answered yet and left them counting as neither
confirmed nor denied.

| Client | Result |
|---|---|
| Samus | **3 predicted, 3 confirmed (100%), 0 denied**, 1 kill left to the authority |
| Sylux | **2 predicted, 2 confirmed (100%), 0 denied**, 0 kills left to the authority |

`Predicted == Confirmed + Denied` on both, which is what was being checked.
Deaths cross exactly (2↔2 and 3↔3) and shots cross 31↔37 and 368↔332. The one
FAIL is `bombs mine 114 theirs 0`, which is the bursty-feature skew in
`KNOWN-GAPS.md`.

**And it moved the `Replayed` anomaly rather than reproducing it.** This time
the *observer* reads 12 for the other client's slot and the slot's own machine
reads 0 -- the opposite way round from every run above. Whatever that counter
is doing, "the observer loses the other player's damage" is not it. See
`KNOWN-GAPS.md`.

**The box is still the limit.** Two clients run the tour at 7-12 fps here, and
the 70 s pair took 364 s and 575 s of wall clock to play 4200 frames. Both of
those runs also report the form-reconciliation FAIL (67 and 90 frames in a
row), which is the open question in `KNOWN-GAPS.md`, measured there at 78.

## Traps

- **`HealthFor` must never return zero.** A health assignment is not a death:
  it skips `TakeDamage` and everything the death path does, leaving a player
  who is neither alive nor dead and who no snapshot will respawn (the
  authority thinks they are up). The hold floors at 1 and the dying is left to
  `TakeDamage`, which is where a predicted kill goes anyway.
- **`Confirm` has to run before `Replay`'s "already down here" return.** That
  return exists because a victim already dead on this machine has nothing to
  replay -- but with kills predicted, a victim this machine killed *is* dead
  here, so the authority's confirmation of that very kill would hit the return
  and never retire the prediction. It would count as denied, and the corpse
  would be held down for the whole hold window rather than until the answer
  arrived.
- **A lethal snapshot still replays even when the hit was predicted.**
  Reaching that line with `state.Health == 0` means this machine's prediction
  did not kill them -- it was clamped, or the killing blow was somebody
  else's -- so returning early would leave a player alive here and dead
  everywhere else. A kill this machine did predict never reaches the line; it
  is the "already down" return.
- **`HealthFor` is called for its side effect as well as its answer.** It
  records what it returned, in `_shownHealth`, and that record is what floors
  the next call. A caller that skips it -- or a second caller that wants "what
  would this be" without drawing it -- moves the floor. There is one caller,
  in `ApplyState`, and it should stay that way.
- **A predicted self-kill has to be released by something.** The authority's
  report of a fall names no attacker, so `Confirm` never sees it and the
  pending lethal entry outlives its own confirmation; `HeldDead` then refuses
  the respawn that arrives a moment later and the player lies in the void for
  the rest of the hold window. `NoteDeath`, from the `!spawned` branch, is what
  releases it. `NetDamage.Replay` runs *before* that branch, so a hit the
  authority does credit is still confirmed and counted first. **It clears the
  lethal flags and nothing else** -- clearing the slot outright drops
  predictions nobody has answered yet, and they then count as neither confirmed
  nor denied, which breaks the one arithmetic that makes those numbers
  readable (`Predicted == Confirmed + Denied`). `NoteRespawn` is what clears
  the slot, at the respawn, where the life really has ended.
- **The kill streak is the one part of the death path the snapshot does not
  correct.** `Points`, `Kills` and `Deaths` are assigned from every snapshot;
  `GameState.KillStreak` is not on the wire, so a mispredicted kill leaves this
  machine's own streak one too high and can ring the "5 in a row" line early.
  It is local, cosmetic and rare -- `DeathsUndone` is the number that bounds
  it -- but it is the one thing here that does not put itself right.
- **`NetDamage.Note` must stay silent on a predicting machine.** A prediction
  is not a resolution. Letting it through would put a damage sequence and a
  `Resolved` count on a machine that decides nothing -- and the whole damage
  pipeline measurement is the comparison between the one machine that resolves
  and the ones that replay. `NetHitPrediction.Predicting` is the guard, and it
  reproduces exactly what `Suppress` returning true used to guarantee.
- **Consume the confirmation, do not skip the kill.** `Confirm` is called for
  every hit the authority credits this machine with, including the lethal one,
  so the pending entry is retired either way -- but the replay is only skipped
  when the hit is *not* lethal. Returning early on a lethal confirmation would
  leave a player alive on the shooter's screen and dead on everyone else's.
- **The clamp has to be at `Note`, not at `Suppress`.** `TakeDamage` applies
  the beam's effectiveness multiplier, the damage level and the halfturret
  split *after* the suppression check, so a clamp at the top of the function
  is a clamp on a number that is not the damage yet.
- **A frozen puppet stops taking its owner's positions.** A mispredicted
  freeze therefore stalls a puppet locally until the next snapshot's
  `ModSetFrozen(false)` thaws it -- which is one frame, and is why the freeze
  is left to predict along with everything else rather than special-cased.
- **`Tick` lives in the simulation step.** Both the pending ages and the
  mark's countdown are measured in frames; a picture with no step behind it
  must not advance either. See `render/FRAME-PACING.md`.
- **Loss costs nothing.** A confirmation is not a packet of its own -- it is
  the damage sequence in whatever snapshot next arrives -- so a lost snapshot
  delays a confirmation rather than destroying it, and `landed` picks up both
  hits when the next one lands. Only a sequence jump big enough to trip
  `MaxCatchUp` skips a confirmation outright, and that path shows nothing
  either way, so there is no double feedback to be had. Measured above.
- **No protocol change.** Nothing new is sent, nothing existing moved, and
  `NetConfig.ProtocolVersion` stays at 6. A predicting client and a server
  built before this interoperate; the client simply predicts against whatever
  the server tells it.

### Self splash and rescued momentum

Self splash stays local and immediate. A Missile fired into the floor already
runs the normal splash collision on the shooter's machine, including the upward
component that produces a rocket jump, and that path is not converted into a
claim. What protocol 16 changes is the complementary case: when a locally
predicted hit on another player has to be rescued by the authority, the exact
validated impact vector now survives the claim instead of being replaced with a
directionless damage event.
