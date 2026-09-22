# The server as the simulation authority

Current architecture: every normal online match is server-authoritative. Code:
`Mods/Network/ServerSim.cs`, `HostedServerProcess.cs`, `NetHostSession.cs`,
`HostPool.cs`, `NetMaster.cs`, `Mods/Headless.cs`, and the `NetRole.Server`
branches in `NetSession.cs` / `DedicatedServer.cs`.

`-simulate` and `-authority` are compatibility no-ops. `RunsTheMatch=false`
and `PacketType.Authority` remain for legacy protocol/testing paths, not normal
launcher hosting.

## Current authority model

- **Standalone dedicated server:** the server simulates the match itself and
  refuses startup if it cannot build the world.
- **Host on this computer:** `NetHostSession` starts a dedicated-server child
  process and joins it over loopback as an ordinary client.
- **Directory/overflow hosting:** each hosted game gets an isolated server
  process via `HostedServerProcess`, so the static `NetSession` belongs to that
  match alone.
- **No normal player is simulation authority.** The historical first-client
  authority/hand-over path is compatibility coverage only.
- **Combat, movement, health, score, match state and match end are server
  authoritative.** Remote controls run through the same engine movement and
  collision code on the server. `IntentPacket.Position` remains telemetry and
  an observer fallback only; it cannot place an authoritative player or muzzle.
- **Responsiveness is predicted locally.** A player's own movement still runs
  immediately on their client. Snapshots echo the newest owner input frame the
  server actually simulated, paired with the end-of-tick position/velocity
  from that first simulation. The client compares its history at the same
  boundary. Input aim is sampled after the tick applies mouse/controller
  rotation; buttons and charge retain their pre-simulation values. `NetHitPrediction`
  does the analogous job for outgoing hit feedback.
- **Rolling and flick boosts are input.** Intents and observer bundles carry
  a normalized roll basis plus a bounded, repeated flick event with its input
  frame and world direction. Receivers consume each flick once. The server
  computes boost charge, velocity and ram damage; owner boost damage cannot
  overwrite that result. These wire and simulation changes require protocol 19.

## Historical transition

Older builds made the first client the simulation authority. That created a
zero-latency advantage for one player, tied match survival to that player's
connection, and required authority handover. The server-authority work removed
those properties. Measurements below that compare client authority with server
authority are retained as dated evidence, not as current topology.

## How it works

The engine's simulation turns out to need no GL context at all.
`Scene.OnSimulationFrame` and everything under it -- input, the entity step,
collision, beams, damage -- contains no GL call anywhere, because the frame
split (`render/FRAME-PACING.md`) had already put every one of them in
`Scene.OnDrawFrame`. `grep -l "GL\." src/MphRead/Entities/ src/MphRead/GameState.cs
src/MphRead/Scene.cs src/MphRead/SceneSetup.cs src/MphRead/HUD/` finds
**nothing**. So the server runs the real engine rather than a model of it,
and the old objection -- "a reimplementation on the server would be a second
answer free to disagree with the first" -- disappears: it is the same answer,
compiled from the same file.

Spire's alt attack has one simulation-owned exception to the old draw-derived
pose: after the alt animation frame update, `PlayerProcess` animates its rock
nodes and stores both world-space collision positions. `PlayerDraw` animates
the nodes again for the picture but does not write collision positions. This
keeps the headless server's slam collision current without changing the startup
positions set when the attack begins or the render transforms. The
`-spireposecheck` headless probe verifies moving rock positions with no draw.

| Piece | What it does |
|---|---|
| `Mods.Headless.Active` | one switch, in the shape `ThumbnailMode` established. Turns off only work whose output is a picture, a sound or an answer to a person |
| `SyntheticInput` | a `KeyboardState` and a `MouseState` built by reflection, because `Scene`'s constructor demands a pair and OpenTK only makes them from a window. Nobody ever presses them |
| `NetRole.Server` | authority, `LocalSlot = -1`, no socket of its own |
| `NetSession.StartServerAuthority(sink, matchEnded)` | takes the role; the finished snapshot is handed to the relay in this same process rather than sent as a datagram |
| `ServerSim.Advance(now)` | the same fixed-step accumulator the game window runs. A server's loop is woken by packets, at no fixed rate, which is exactly what an accumulator is for |
| `DedicatedServer.RunsTheMatch` | true for normal game servers. False exists only for compatibility/tests |

The current wire protocol is defined only by `NetConfig.ProtocolVersion`
(currently 16). Normal server-authority matches never send
`PacketType.Authority` to a player. The packet is still understood so legacy
compatibility tests can exercise the old topology; it is not a normal hosting
mechanism.
`HandleIntent` feeds `NetSession.AcceptSlotIntent` one hop earlier than a
client authority got the same bytes, through the same call, so the ordering
rule that guards a rejoining player's restarted frame counter is the one that
has already been debugged.

**The simulation follows the server the way a client does.** The roster and the
match state are applied to it through `NetSession.ApplyRoster` /
`ApplyMatchState` -- the very packets it is about to broadcast. A rotation is
therefore an ordinary `NetRoomChange` transition, carrying the intro-camera and
settling fixes that path already has, rather than a second implementation free
to disagree with every client at once.

## Traps

- **`NetSlotManager.Sync` and `NetPlayerSetup.ApplyOnce` both returned early on
  `LocalSlot < 0`.** That guard is a client's "not admitted yet". A server is
  never admitted, because none of the slots is its own, so it held for the
  whole match: no slot was ever activated, the room was simulated empty, and
  the snapshot went out with a header and no players in it. This is the first
  thing to check if a simulating server publishes 13-byte snapshots.
- **`NetHooks.LocalSlot` must be -1, not 0.** It fell through to 0 for anything
  that was not demo playback, which would make slot 0 -- a real player, on
  somebody else's machine -- the one slot on the server exempt from every "this
  belongs to somebody else" test in the engine: its intent dropped, its
  keyboard read from a keyboard nobody is holding, its shots fired from the
  origin.
- **The camera mode is forced to `Roam`, and that is load-bearing.**
  `IsMainPlayer` is `this == Main && camera mode is Player`, and
  `PlayerEntity.Main` on a server is slot 0 because something has to be. In
  `Player` mode that would make one slot in eight "the main player" and send it
  down 35 branches in `PlayerProcess` alone that no other slot takes --
  including one that skips `UpdateNodeRefVolume` entirely. `Roam` makes
  `IsMainPlayer` false everywhere, so all eight slots are treated identically,
  which is what a machine playing none of them should do.
- **The multiplayer intro camera sequence must not run.** It is a camera flying
  round the room for the person about to play in it. On a server it flew for
  nobody and applied `BlockFormSwitch` and blocked input to slot 0 alone --
  and asserted (`current.PartIndex != -1`) inside `RoomEntity.UpdateNodeRef`,
  which in a Debug build kills the server outright on the first join. Every
  client still runs its own.
- **`SendMatchEnd` had nobody to send to.** A match won on points is decided by
  the simulation, and on a server that is this process; without the
  `NetRole.Server` branch the sim reached the point goal, told no one, and the
  match ran until the clock did -- which on a rotation entry with no time limit
  is for ever.
- **A simulating server may not sleep 20 ms when idle.** It owes a step every
  16.7 ms whether or not anybody is connected.
- **The two simulation timers that live in the draw pass never ran, and one of
  them is what changes room.** `UpdateFade` and the effect advance are called
  from `GetDrawItems`/`UpdateUniforms` -- correctly, because a fade and a
  particle are drawn -- and both were already careful to consume *steps owed*
  rather than pictures drawn (`_pendingFadeSteps`, `_pendingEffectSteps`).
  Nobody had to think about a caller that draws no pictures at all. The counts
  simply climbed.

  A rotation is `SetFade(..., AfterFade.LoadRoom)`, and the load happens in
  `EndFade`, which only `UpdateFade` reaches -- so **the server went on
  simulating the first map of the session for ever** while every client
  rotated correctly. It did not look broken from any client: a live player's
  position is their own report, not the server's, so the players still saw
  each other, the scoreboards still agreed, and `compare-reports.py` said
  0 mismatches. What was wrong was invisible and total -- every shot resolved
  against the collision of a room nobody was standing in. `ModStepDrawPassTimers`
  runs both from the end of the step when headless.

  **The check that catches this is the server's own log**, not a client's:
  `sim: <room>` must name the room the *scene* is in (read off `Scene.RoomId`
  every time, never remembered) and it must change when the rotation line
  above it does.
- **`UnloadModel` deletes GL objects, and a room transition calls it.** Guarded
  like the rest -- but only its GL half: `Read.RemoveModel` underneath is what
  makes a rotating server's memory reach a plateau instead of holding every map
  it has ever played. Unguarded, the first rotation threw inside `EndFade`,
  left the fade half-ended, and every subsequent step threw in the same place
  -- 1800 failures a minute, which is what `StepFailures` in the periodic log
  is for.

## Cost, measured

`-simcheck "ROOM" [-players N] [-seconds N]` runs the headless simulation on
its own, with nobody connected and synthetic intents, and reports what a room
costs in memory and in milliseconds a step. It is not a network test --
`run-check.sh` is that -- it measures the part that used to run on a player's
PC and now has to run on a server.

Every saving below is work whose only output was a picture:

| Cut | Where | Peak RSS |
|---|---|---|
| (a full client, `-maptest`, 8 players, for comparison) | | **337 MB** |
| GL context, shaders, display lists, texture upload | `InitTextures`, `GenerateLists`, `OnLoad` | 183 MB |
| the SFX bank (every sample in the game, decoded) | `Sfx.Load` takes the stub `ThumbnailMode` uses | *included above* |
| texture **pixel data**, never decoded because nothing binds it | `Read.cs`, empty `TextureData` per texture | 118 MB |
| render instructions -- the geometry of the picture. The simulation collides against the room's *collision* file and never against its render meshes | `Read.cs`, empty list per display list | **110 MB** |

MP3 PROVING GROUND, 8 players, x86-64, Debug: **110 MB peak, 0.31 ms mean
step, 2% of the 16.7 ms budget**, load 0.5 s. The .NET runtime alone is 50 MB
of that, so the room and eight players cost about 60 MB.

MP1 SANCTORUS, 8 players, on the Pi 3B (ARM64, Release, self-contained single
file): **131 MB peak, 8.04 ms mean step -- 48% of the budget**, load 11.9 s,
20 s of match simulated in 9.7 s of wall clock. It fits, with the Pi showing
225 MB still available afterwards, and it is the machine that decides the
ceiling: the same room costs a fifteenth of the budget on a desktop.

That 48% is the **worst** case and reads worse than the machine is. It is
eight players all firing on the same schedule, which is what the synthetic
intents do; a real three-player match on the same Pi ran at **0.90 ms a step**
after ten minutes, and held **exactly 1800 steps per 30 seconds** the whole
way. The `-simcheck` mean is also dragged up by its first step (1252 ms once,
540 ms another time), which is the JIT compiling the entity path rather than
the simulation -- five steps in twelve hundred overran at all. Publishing the
ARM64 server with ReadyToRun would take most of that and a good part of the
11.9 s load with it, and has not been tried.

**A room load blocks the relay**, because the sim is stepped from the server's
own loop. Measured on the Pi: about **4 seconds** for a reload with the file
cache warm (the 11.9 s figure is a cold process, JIT included), against the
30 s `NetConfig.TimeoutSeconds` that would start dropping peers. It happens
twice in a session's life -- once when the first player joins an empty server,
because `_matchId++` there is a deliberate clean restart, and once per
rotation -- and both are moments every client is loading the same room anyway,
so most of it overlaps with a wait they are already having. It is fine as it
stands; moving the sim to a thread of its own is the answer if it ever is not,
at the price of putting a thread boundary between the sim and the peer table.

The counts are preserved where they are dropped -- one empty `TextureData` per
`Texture`, one empty instruction list per display list -- because `Recolor`
asserts one entry per texture and `mesh.DlistId` indexes the lists by
position.

## Measured against the relay, on the Pi

Three scripted clients, 150 s, MP1 SANCTORUS on `51.161.113.128`, with 150 ms
injected on two of the three (`run-remote-lag.sh`). The same scenario twice,
the only difference being where the simulation lives.

| | relay (a client is the authority) | server authority |
|---|---|---|
| cross-client mismatches | **1** | **0** |
| scoreboards | agree within 0 events | agree within 0 events |
| remote position snaps | 0 | 0 |
| per-client verdict | 3 PASS | 2 PASS, 1 FAIL -- see below |

A separate run, two clients over three one-minute matches on the Pi: **two
rotations followed, 0 mismatches**, the server's own log naming each new room
as it reached it, and 1800 steps per 30 seconds held straight through both
room changes.

The relay run's mismatch is `CHARLIE shooting: they did 2578, ALPHA saw 557` --
the authority seeing a fifth of a laggy player's trigger frames. It is gone
under server authority.

**The one FAIL is the interesting result and it is not a regression.** ALPHA
reported *"their form stayed wrong for 78 frames in a row -- authority wanted
biped, puppet alt/Morph/ended"*. In the relay run ALPHA does not report it
because ALPHA **is** the authority, and `NetFeatureCheck` skips this check on
the authority: it is the one machine with nothing to compare against.
At the time of this run, `NetPlayerBridge` reconciled a puppet's form against
`IntentButtons.AltFormState` only on the authority. Clients replayed the morph
press but had no snapshot-based correction.

So the refactor did not create this. It removed slot 0's exemption from it:
what ALPHA reported is what the other seven players had already seen. The
current bridge runs Morph controls through the authority's real transition
rules and reconciles the authority's snapshot on each client. Reported owner
form no longer forces an authoritative unmorph through a low ceiling. The
transition-aware guard lets a puppet finish morphing while an older state is still in flight, then corrects
a lasting mismatch. This 78-frame result predates that change; a restart-free
latency run is still needed to measure it in play. See
`NETWORK-DIAGNOSTICS.md` and `.claude/KNOWN-GAPS.md`.

## Every normal hosted match has server authority

`RunsTheMatch` defaults to true and the standalone `-server` path never changes
it, so a dedicated server simulates or does not start. `-simulate` and
`-authority` are accepted and do nothing, which keeps deployed service units
and launch scripts compatible.

`NetSession` is still static, which means one process can still simulate only
one match. The old workaround for the other hosting paths was to set
`RunsTheMatch = false` and promote the first client to `PacketType.Authority`.
That made the first player special: their hit resolution was authoritative in
the same frame while everybody else paid the network trip and rewind.

The workaround is gone. Hosting paths that need an additional match now start
that match in an **isolated server process**:

| Caller | What it does now |
|---|---|
| `NetHostSession` | starts a local dedicated-server process, then joins it over loopback as an ordinary client |
| `NetMaster` | starts each requested online match as its own server process |
| `HostPool` | starts each regional/overflow match as its own server process |

Each child has its own static `NetSession`, so it can run
`ServerSim.Start` normally and no player needs to become the simulation
authority. Lobby policy, owner token, match format, ready requirement,
join-in-progress rule, friendly fire, Shadow Freeze and affinity-weapons state
are passed into the child command line so process isolation does not change the
match rules.

`DedicatedServer.NotifyAuthority` and the `RunsTheMatch = false` compatibility
path still exist for old protocol/testing code, but the normal hosting paths do
not select them.

## Current constraints and validation gaps

- **Game files are required for a game server.** The operator supplies extracted
  data and a valid `paths.txt`; no Nintendo data ships in server packages. A
  server without them exits with an actionable error instead of falling back to
  client authority. Historical pruning measured the headless subset at roughly
  52 MB of extracted data, but that measurement is not a packaging contract.
- **Movement prediction is intentionally client-side; movement authority is
  not.** The local player moves without waiting for the round trip, while the
  server derives canonical position/velocity from controls and collision.
  Reconciliation uses the snapshot's per-slot processed-input frame rather than
  a ping estimate, so a delayed authority state is compared with the local
  prediction from the same instant. The history is a bounded circular buffer.
  Hard corrections adopt position, form coordinates and velocity together
  before collision; both hard and velocity corrections invalidate outstanding
  predictions so older acknowledgements cannot apply the same error again.
  Expired history recovers from the current authoritative snapshot.
- **Full input replay remains unimplemented.** The authority consumes the
  latest input each tick rather than a queue of every client command. The
  client corrects state without restoring all physics timers/contact state
  and replaying unacknowledged commands. This is not yet traditional rollback
  prediction, and latency/loss plus hunter-specific movement need live testing.
- **Real Windows authoritative gameplay still deserves manual coverage with
  extracted data.** CI validates the Windows server binary/startup contract,
  but cannot ship proprietary game files into Actions.
- **Hosted-server process isolation is intentional.** `NetSession` remains
  static; do not move hosted matches back in-process unless that state is first
  made instance-safe.
