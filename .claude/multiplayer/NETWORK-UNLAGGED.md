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

Rewind depth = `authority NetFrame − (AckFrame + AckSubFrame/256)`, clamped to
`MaxRewindFrames` (**default 45 = 750 ms** since protocol 7, movable with
`-maxrewind N`; it was 24 = 400 ms up to protocol 6). History is
`HistoryFrames` **128 ≈ 2.13 s**, which is deeper than Zandronum's
`UNLAGGEDTICS 35` because this ring has to cover the round trip *plus* whatever
a client is holding its puppets back by to smooth them, with the ceiling
comfortably inside it rather than against its edge.

**The sub-frame is protocol 7's.** A client that interpolates its puppets
(`NETWORK-SMOOTHING.md`) is not drawing any one snapshot: it draws a point
between two of them, and `IntentPacket.AckSubFrame` says how far past the named
one. `Reconcile` lerps its own history between the same two frames by the same
fraction, so the shooter and the authority are looking at exactly the same
world — more exactly than before, since an integer ack was itself a rounding of
up to a frame. Zero from a client that does not interpolate, which is what
every build before 7 was.

### The ceiling is ours, and it is 2.5x tighter than Q-Zandronum's

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
in ten came back as body shots. Protocol 7 moved the default to **45 frames
(750 ms)**: past that distribution with room for the smoothing delay on top,
still inside a history twice as deep, and still under the one second
Q-Zandronum allows. The same scenario at 45 reads **clamped 0, worst asked 42**.
`NetUnlagged.LegacyMaxRewindFrames` is the old number, for the baseline arm.

**45 is adequate at 320 ms and no more than adequate.** The jump-pad arm at
320 +- 80 with 2% loss reads `clamped 1 (1.4%), worst asked 45` -- one shot in
seventy reached the new ceiling exactly. At the top of the 250-400 ms band this
work is about, or with a client buffering more than three frames, it will start
folding again. If that shows up, raise it rather than wonder: the history is
128 frames and `MaxRewindCeiling` allows 120, so there is room. What bounds the
number is not the ring, it is how far back a player is willing to be shot after
breaking line of sight.

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
- **Client-side prediction with input replay.** Zandronum's `cl_pred.cpp`
  rewinds the local player to the server's position and replays stored
  `ticcmd`s. This game does not need it and could not use it as written: a
  player's own position is *sent*, not derived (`IntentPacket.Position` —
  "whoever is playing a character is the one who knows where it is"), so there
  is no server correction to replay against. The existing `DesyncDistance`
  backstop covers corruption.

  **This is about movement, and only movement.** Predicting a client's own
  *hits* is a different question with a different answer, and it is
  implemented — `NETWORK-PREDICTION.md`. It is possible precisely because of
  the rewind described above: the authority resolves against the frame the
  shooter had applied, which is the world the shooter's machine already holds,
  so the client can run the same test itself and be right.

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

## Protocol 17 policy migration

The default ceiling remains 45 frames with 128 history frames and press-age
recovery. WeaponLagPolicies centralizes actual mechanics without retuning their
timing. LagCompensationPolicy defaults to Shadow and preserves fractional ACK
rewind exactly; Release builds reject enforcement. Read-only historical trace
comparisons never apply damage or mutate the live scene. See NETWORK-LAGCOMP-POLICY.md
and NETWORK-WEAPON-POLICIES.md for timing sources, coverage and release gates.
