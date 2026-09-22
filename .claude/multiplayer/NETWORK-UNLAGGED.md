# Lag compensation

Ported from [Q-Zandronum](https://github.com/IgeNiaI/Q-Zandronum)'s
`unlagged.cpp` — itself Spleen's Skulltag work, with Q-Zandronum's own
addition on top. Code: `Mods/Network/NetUnlagged.cs`.

## The fault

Not Doom's fault, because the topology is not Doom's. In the current
architecture the gameplay server runs the simulation and every client feeds it
intent. Historical measurements below also include the former
first-client-authority relay; those are control data, not current production
topology. For a client **B** shooting at **C**:

- B's screen shows C where the last snapshot put them — composed by the
  authority one downstream trip ago, from an intent C sent one upstream trip
  before *that*.
- B presses fire. The intent reaches the authority one upstream trip later, and
  the authority spawns the beam against the C it holds **now**.

The two differ by **B's full round trip**, and it is entirely one-sided: B aims
at a hunter and the shot is resolved against wherever that hunter walked to in
the meantime. At the Pi's 15 ms nobody notices. At 150 ms a strafing player is
most of their own width from where they are being shot at — the "my shots go
through people" report — and no amount of smoothing the puppets touches it.
The puppets are right; the clock is wrong.

**The authority is already consistent and gets a rewind of zero.** It aims at
the puppets it holds and resolves against those same puppets. Compensation is
owed to exactly the people who are not running the simulation, in exactly the
amount they are behind it.

## The mechanism

| Piece | Where |
|---|---|
| `IntentPacket.AckFrame` | the newest snapshot frame the sender had applied when it composed the packet — "what I was looking at" |
| `NetUnlagged.Record(frame)` | called **only** from `NetSession.BroadcastSnapshot`, so history[F] is exactly what snapshot F said |
| `NetUnlagged.BeginShot` / `EndShot` | wrap the single `BeamProjectileEntity.Spawn` call in `PlayerInput.cs`, next to the existing `NetDamage.NoteFired` |
| `PlayerEntity.ModPlaceAt` | position + hitbox + room node, in `PlayerEntityNetAim.cs` |

Raw rewind depth = `authority NetFrame − (AckFrame + AckSubFrame/256)`.
Protocol 18 applies a defender-aware curve instead of granting that entire age:

- **0-15 frames (0-250 ms):** full compensation.
- **15-24 frames (250-400 ms):** additional age is compensated at 50%.
- **beyond 24 frames:** additional age is compensated at 25%.
- **30 frames (500 ms) raw** is the default request ceiling, so the default
  maximum *served* rewind is 21 frames = **350 ms**.

`-maxrewind N` changes the raw request ceiling for A/B work; it does not turn
the tapered regions back into full compensation. History remains
`HistoryFrames` **128 ≈ 2.13 s**, comfortably deeper than every served rewind
and still useful for diagnostics and validated claim history.

**The sub-frame is protocol 7's.** A client that interpolates its puppets
(`NETWORK-SMOOTHING.md`) is not drawing any one snapshot: it draws a point
between two of them, and `IntentPacket.AckSubFrame` says how far past the named
one. `Reconcile` lerps its own history between the same two frames by the same
fraction, so the shooter and the authority are looking at exactly the same
world — more exactly than before, since an integer ack was itself a rounding of
up to a frame. Zero from a client that does not interpolate, which is what
every build before 7 was.

### The fairness curve is ours

Worth stating plainly, because it was not: **Q-Zandronum has no second clamp.**
`UNLAGGED_Gametic` bounds the rewind by the history and by nothing else --

```c
int unlaggedGametic = ( ... CLIENTFLAGS_PING_UNLAGGED ) ?
        gametic - ( player->ulPing * TICRATE / 1000 ) :
        pClient->lLastServerGametic + 1;
if ( unlaggedGametic > gametic ) unlaggedGametic = gametic;
if ( (gametic - unlaggedGametic) >= UNLAGGEDTICS)
        unlaggedGametic = gametic - UNLAGGEDTICS + 1;      // UNLAGGEDTICS 35
```

-- so its ceiling **is** its history: one second, `doomdef.h:66`. The 400 ms
here was a bound this port added on top, and on an intercontinental line it was
the binding one. The Japan server reported a mean of 19.6 frames with the worst
pinned at exactly 24 over runs of nine thousand shots: a distribution standing
against its ceiling, not a tail touching it.

**Measured here, and worse than that.** Two clients at a 320 ms round trip with
80 ms of jitter and 2% loss, the sniper scenario, under the 24-frame ceiling:

```
rewound 99   mean 399 ms   clamped 88 (88.9%)   3.2 frames refused each
depths asked: 23:4 24:7<-ceiling 25:14 26:28 27:24 28:7 29:8 30:2 32:2 37:1 39:1 40:1
```

The distribution's **mode is two frames past the ceiling**. Nine shots in ten
were resolved against a world their shooter never saw; the shooter's own machine
resolved 16 of the 26 hits the authority credited it with, so four shots in ten
landed with nothing happening on the screen that fired them, and two headshots
in ten came back as body shots. Protocol 7 therefore moved the historical
default to **45 frames (750 ms)**; that removed the shooter-side clamp almost
entirely, but it also allowed very old views to remain fully authoritative long
after a defender had reached cover.

Protocol 18 keeps that measurement as the reason ordinary latency is compensated,
but changes the tradeoff: full compensation ends at 250 ms and older views taper
toward the defender's newer server-owned position. `PreviousMaxRewindFrames`
retains 45 for diagnostics and `LegacyMaxRewindFrames` retains the older 24.
The ring is still 128 frames deep; its size is no longer the fairness policy.

Two smaller differences in the same function, for the record: Q-Zandronum
rewinds to `lastServerGametic + 1`, one tic *shallower* than the raw ack -- the
opposite direction from "give the shooter more" -- and it offers a
ping-derived depth as an alternative, which this port deliberately does not
(see *Why the ack and not the ping*).

`NetUnlagged` now counts what the ceiling refuses: `ShotsClamped`,
`FramesRefused`, `WorstRequested`, a histogram of requested depths, and -- for
every clamped shot -- the distance between where the rewind went and where it
was asked to go, since frames are not the quantity that decides a headshot and
units are. `.claude/testing/HITRIG.md`.

### Why the ack and not the ping

Zandronum's `UNLAGGED_Gametic` offers both: the client's acknowledged server
tic, or a figure derived from its measured ping (`cl_ping_unlagged`). Only the
ack is kept here. A ping is a smoothed average of a quantity that is not
smooth, measured over a path the intent did not necessarily take; the ack is
the exact frame the client is answering. Having one source means there is one
thing to be wrong.

### The projectile half — Q-Zandronum's contribution

Doom's unlagged is built around hitscan, where rewinding the targets is the
whole fix because the trace resolves in the instant it is fired. **Almost
nothing here is hitscan**: a Missile, a Battlehammer round and a Magmaul shot
all travel. Rewinding the world and spawning into it fixes only the first frame
and then leaves the projectile crawling out of a muzzle a round trip late —
visibly behind its owner's screen, and easy to walk out of.

So `EndShot` is `UNLAGGED_DoUnlagActors`: the beam is stepped once per frame it
is owed, with the world **re-reconciled at each step**, so anything it runs
into on the way is checked against where that player was at that moment. What
arrives in the present is a shot already the right distance down range, having
hit whatever it would have hit. The loop stops the moment every new beam is
done — collided or out of lifespan — so a point-blank shot costs one step, not
twenty-four.

New beams are identified by diffing `EquipInfo.Beams[i].Lifespan > 0` across
the `Spawn` call: the pool is picked from by exactly that test
(`BeamProjectileEntity.cs`), so a false→true flip is this shot's.

## What is deliberately not ported

- **Sectors and polyobjects.** Zandronum reconciles them because a Doom map's
  floor is a moving hitbox. Nothing in an MPH room moves that a shot is stopped
  by in the same way, and rewinding room geometry would mean unwinding the
  collision structures the whole engine indexes against.
- **`cl_ping_unlagged`.** See above.
- **`wasJustUnlagged`.** Q-Zandronum sets it so the actor skips its next
  `Tick()`, having already been ticked during catch-up. Not carried: it costs
  an upstream field and an upstream branch in `BeamProjectileEntity.Process`
  to save the beam being one frame further down range than asked for, which is
  within the error the whole mechanism is correcting.
- **Client-side movement prediction/reconciliation.** Protocol 18 makes the
  server's engine movement authoritative while preserving immediate local
  control. Each snapshot echoes the newest owner input frame actually simulated
  for every slot. The client keeps exact position/velocity history by input
  frame, compares the authority with the prediction from that same frame, and
  carries the historical error forward into the current prediction. Small
  errors are eased; collision/teleport-scale errors snap. No ping-derived
  "where was I probably?" comparison is used.

  **Hit prediction remains separate.** `NETWORK-PREDICTION.md` still handles
  immediate outgoing-hit feedback. Normal authority shots and hit-claim rescue
  now both use this document's same tapered rewind policy, so the claim path
  cannot resurrect a raw old-world hit that the authority intentionally refused.

## Measuring it

In the current server-authoritative topology the **server** is the machine that
rewinds shots, so its lag-compensation/claim diagnostics are the authoritative
measurement. Clients may be given different synthetic lines with
`-netlag MS[:JITTER]` / `-netloss PCT`; no client becomes authority because
it joined first.

Older tables below used a private `run-unlagged.sh` wrapper and a
client-authority topology. Keep them as historical A/B evidence, not as current
instructions. Current server logs report the same core line:

```
lag compensation: 420 shots rewound, mean 9.3 frames (156 ms), worst 15,
                  catch-up 1375 steps / 50 hits, history misses 0
```

**The mean rewind is the end-to-end check on the ack path.** At `-netlag 150`
it reads 156 ms; if it read 0, or the round trip of the wrong peer, the ack is
not arriving. `history misses` climbing during a match means acks are older
than `HistoryFrames` and those shots are being resolved with no compensation at
all — non-zero right after a join is ordinary, since a fresh client has acked
nothing.

`-nounlagged` turns it off for a controlled comparison, and is the only switch;
it is on by default, as Zandronum's `sv_nounlagged` is.

**A rewind fixes where a shot is resolved, not when the shooter is told.**
That second half is `NETWORK-PREDICTION.md`, which is built on this one and is
measured beside it; `-nohitprediction` is its control.

**And it fixes neither when the two machines cannot run the same test at all.**
A rewind that hits its ceiling, a trigger pull recovered from a press history,
and above all a shooter killed during the round trip — whose shot the authority
never runs, because its copy of that player was already dead when the intent
arrived — are what `NETWORK-HITCLAIMS.md` is for. A claim is a backstop, not a
substitute: the cheapest hit registration is still the one the authority finds
itself, which is what raising the ceiling buys.

### Verified 2026-09-06 (WSL, loopback)

| Check | Result |
|---|---|
| 3 clients, 120 s, no lag (`run-check.sh`) | **0 mismatches**, scoreboards agree within 0 events (twice) |
| 3 clients, 150 ms on two of them, on | mean rewind **151 / 156 / 161 / 151 ms** across four runs against 150 injected; worst 12-15 frames; **0 history misses** every time |
| damage pipeline, on | authority resolved 19/44/52; both clients replayed 19/44/51 — nothing lost |
| catch-up depth | 4.3 beam steps per compensated shot (3.3 before the off-by-one below was fixed) |

**The mean-vs-injected agreement is the result worth trusting**: it is an
independent measurement of a number the test chose, and it came out within
7 ms of it four times running. Hit-rate comparisons between an `on` and an
`off` run are much noisier — the scripted tour does not fire the same number of
shots twice (475 vs 579 across one pair) — so do not quote a pair as the value
of the feature. For what it is worth, two pairs both favoured `on`: 33.9% vs
26.3% and 24.7% vs 19.7% of shots landing. Suggestive, not measured.

## Traps

- **The hitbox is a cached field.** `_volume` is recomputed once a frame in
  `PlayerProcess`; a rewind applied mid-frame that only assigns `Position`
  moves the model and leaves the hitbox behind, which is a rewind that does
  nothing at all. `ModPlaceAt` goes through `ModRefreshNodeRef`, which does
  both.
- **Form matters.** A position recorded while its owner was a morph ball and
  applied to a biped is out by the difference between the two collision
  centres — most of a chest. `NetPlayerBridge.InFormFor` converts.
- **Record from `BroadcastSnapshot` and nowhere else.** The rewind is the claim
  that cell `F` holds the picture the client saw. Recording anywhere else in
  the frame makes that claim off by however much players moved in between,
  which is precisely the error being corrected.
- **Only the simulating machine rewinds.** Everybody spawns beams — a client
  fires its own gun for its own eyes — but only the authority's collide with
  anybody. `NetUnlagged.Simulating` gates it; rewinding on a spectator's
  machine would move other players' hitboxes underneath the person watching
  them for no gain.
- **`NetUnlagged.Reset()` on every session reset.** The history is indexed by
  frame and the frame counter restarts; a stale cell stamped with the same
  number from the previous match is a shot resolved against a room nobody is
  standing in.
- **The frame a shot is fired in is not in the history yet.** `Record` runs
  from `NetHooks.AfterSimulation`, i.e. *after* the step, so at shot time the
  newest cell is `NetFrame - 1`. The last step of every catch-up therefore asks
  for a frame that does not exist, and the right world for it is the present —
  which is what the catch-up is catching up to. Treating that as a history miss
  (the first version did) silently stopped every shot one step short. `_newest`
  is what tells that from a genuine gap.
- **Protocol 5.** `AckFrame` is appended, so no existing offset moved — but the
  packet is longer, and a version 4 authority would read it correctly and then
  resolve every remote shot against the present. Deploy the server before
  handing out clients.
