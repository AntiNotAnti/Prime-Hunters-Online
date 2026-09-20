# Opponents that move instead of teleporting

Code: `Mods/Network/NetSmoothing.cs`. Introduced in protocol 7; current compatibility is defined by `NetConfig.ProtocolVersion`.

## The fault

*"En cas de lag élevé ou de mauvaise connexion, les clients se téléportent, ça
saccade."*

A snapshot is composed sixty times a second and arrives when the line lets it.
On a clean line that is sixty times a second and nobody notices. On a bad one
the gaps between arrivals are 0, 0, 3, 1, 0, 4 frames — and a puppet written
straight from each arrival stands still for three frames and then jumps three
frames' worth.

That is the stutter, and it is worth being clear about what it is *not*. It is
not lost packets: every one of those positions arrived. It is not a slow
machine. It is a 60 Hz stream played back at the rate it arrived rather than at
the rate it was made.

## The fix is a clock, not a filter

The standard answer, and the reason it is standard is that the alternatives are
all worse. Positions are buffered and read back on a clock of this client's own
that ticks once per simulation frame, held a few frames behind the newest
snapshot so there is always something on both sides of the read point. A late
packet has somewhere to land; a lost one is covered by the two either side.

What is deliberately **not** done is extrapolation. Guessing forward from the
last known position puts a player through a wall and then snaps them out of it,
which is a worse artefact than the one being removed. When the read point runs
past everything that has arrived, it is pinned and the puppet holds.

| | |
|---|---|
| `Record(frame, states)` | called from `NetSession.HandleSnapshot`, so what is buffered is exactly what the authority said — which is also what its own rewind history holds under that number |
| `Tick()` | `NetHooks.AfterSimulation`, once a simulation frame, before anything reads a position |
| `Sample(slot)` | `NetPlayerBridge.RestoreSnapshotPosition` |
| `AckPoint()` | `NetPlayerBridge.CaptureIntent` |

The read point advances one per frame and is steered toward `newest - Delay` by
a twentieth of the error — which closes half of any gap in fourteen frames at a
speed-up of at most 5%, under what an eye reads as motion being wrong. Past ten
frames of error it is put back rather than walked back: ten frames of drift is
a stall, a rejoin or a rotation, and gliding across it would be four seconds of
everybody moving at the wrong speed.

## Why it does not cost hit registration

**This is the thing to be careful of here, and it is why the protocol moved.**

Smoothing moves the puppet a shooter aims at away from the position the
authority filed under any one frame. The authority's rewind puts everybody back
*to a frame*. So a blend of two frames would be a shot resolved against
neither, and the whole of `NETWORK-UNLAGGED.md` would be undone by the cure.

The answer is that the read point is a **number**, so it can be sent.
`IntentPacket.AckSubFrame` is one byte, 1/256ths of a frame past `AckFrame`, and
`NetUnlagged.Reconcile` interpolates its own history between the same two frames
by the same fraction. The shooter and the authority are then looking at exactly
the same world — *more* exactly than before, since the old integer ack was
itself a rounding of up to a frame.

Both ends make the same three refusals before blending, or they would not agree:
both cells must hold that player alive, in the same form (a biped's position and
a morph ball's are measured from different centres), and near enough to be one
movement rather than a teleport.

That is also why this is not a render-only effect. Drawing a smoothed puppet
while collision ran against an unsmoothed one would put the hitbox somewhere the
player is not, which is the oldest mistake in netcode. **The smoothed position
is the position** — model, hitbox, shadow and shot.

## What it costs

The delay, added to the rewind depth the authority is asked for. Two frames on a
line that is behaving, up to eight when it is not.

The buffer grows by one frame when the read point runs dry and comes back down
one frame per four clean seconds. Two things about that are deliberate and were
both wrong in the first version:

- **it grows at most once every thirty frames.** A run of starved frames is one
  event — a datagram that did not arrive, or four — and growing once per frame
  of it read a tenth of a second of loss as ten separate reasons to buffer more.
  Measured: a single burst took the delay straight to its ceiling.
- **the ceiling is eight frames, not twelve.** Every frame of buffer is a frame
  of rewind on top of the round trip, and a rewind is how far back somebody can
  be shot after breaking line of sight. A line that wants more than 133 ms of
  buffering is one where a player will notice something whatever is done, and
  paying for it in rewind depth is the wrong place.

`-nointerp` turns it off; `-relayedpuppets` turns it off along with the snapshot
owning puppet positions at all, which is the full protocol-6 arm.

## Prerequisite: the snapshot owns the puppet

`NetHooks.SnapshotOwnsPuppets` is **on by default** since protocol 7. A playout
clock and a relayed intent writing the same puppet on alternate frames is the
stutter with extra steps, so the clock needs the snapshot to own the position
outright. The measurement that made it the default is in that property's own
comment and predates this file: a sniper's beams overlapped the target **11**
times on its own machine while the authority resolved **78** hits from the same
shots.

## Reading the numbers

```
puppet smoothing: on, 2 frames of buffer, 6233 interpolated / 199 held,
                  0 starved, 96 clock snaps; steps mean 0.0642 units,
                  worst 17.859, 44.7% of frames still (longest run 684)
```

- **interpolated / held** — samples served by blending, against samples that
  held a position because there was nothing on the far side or the far side was
  a jump. Held climbing is the buffer being too shallow.
- **starved** — frames the read point ran past everything that had arrived.
  Each one grows the buffer, at most once every thirty frames.
- **clock snaps** — read points put back rather than walked back. On a loaded
  box these are mostly *this* client failing to keep 60 Hz, not the line.
- **steps mean / worst / % still** — how far puppets actually moved per frame.
  This is the stutter measured rather than described: a stream written straight
  from its arrivals has most of its steps at zero and the rest at two or three
  times the mean.

**`% still` and `worst` are both contaminated and must not be read raw.** A
player standing still contributes still frames honestly — the scripted tour has
whole phases of it — and a respawn or a teleporter contributes a huge step. Use
them to compare two arms of the same scenario, never as absolutes.

## Traps

- **`Record` must be fed the snapshot, not the applied state.** The claim the
  whole thing rests on is that cell `F` holds what the authority published under
  `F`, because that is what its rewind history holds. Buffering anywhere else is
  off by however much the players moved in between.
- **Clear it on a room change.** A read point left pointing into the old match
  interpolates one room's positions into another's.
- **The ack overwrites, it does not compete.** When the clock is running, the
  read point is the only honest answer to "what was I looking at", so
  `CaptureIntent` replaces the snapshot-frame ack with it rather than choosing
  between them.
