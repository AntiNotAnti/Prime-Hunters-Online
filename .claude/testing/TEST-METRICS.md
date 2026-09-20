# Testing — metrics and interpreting results

This file describes how to read netcheck and maptest results and common traps.

Key lines and their meaning

| Line | Reads as |
|---|---|
| `damage pipeline (resolved here / replayed here)` | the two ends of the damage path. The authority shows `N/0`, everyone else `0/N`. They must match — a shortfall means hits were resolved and never reached the victim |
| `remote position snaps` | visible teleports (bad). Smoothed catch-up is invisible; a snap isn't. Healthy is 0-3 per client per 100 s, worst ones are respawns. A run with rotations in it is not comparable to one without — clients don't finish loading at the same instant |
| `late=N` in the packets line | snapshots that arrived after a newer one and were refused — not loss, these are reordered, and applying them would run health/score/damage backwards |
| `scoreboards agree (within N event)` | N > 1 means the clients are keeping different scores. Clients stop a few seconds apart, so one event of difference is timing |
| `form disagreed ... longest run` | one morph animation's worth is normal. A long run means a puppet is stuck in the wrong form |
| `FAIL: no beam can hurt these players` | `BeamEffectiveness` is all-zero — the player is literally invulnerable (see spawning in `MECHANICS.md`) |
| `untested` | the feature was never performed, so nothing is being claimed — not a pass, and not a fail either; it's a question about the harness |
| `dropped=N` in the packets line | this client couldn't keep up with what it was sent. Non-zero here reads on *other* clients' reports as "they never saw me turn" |
| `pings: slot 0 12 ms ...` | what the server measured per slot, which is what the scoreboard draws |
| `restreams=N` in the packets line | snapshot-stream re-bases. In normal server-authoritative play this should stay 0 across ordinary player joins/leaves; non-zero belongs to an explicit legacy/handover test or signals an authority/stream reset worth investigating |
| `server silence: N re-announce(s), longest gap X s, M authority stand-down(s)` | dropped-connection diagnostics. In normal server-authoritative play `authority stand-downs` should be 0; non-zero belongs only to the legacy/client-authority compatibility path |
| `scoreboard mid-run:` | the board sampled two thirds of the way in, while everyone was still connected. **This is the one to compare between clients** -- the final board cannot be compared, because a departing player's score is cleared on everybody else's copy and clients stop seconds apart |
| `spectating: N frame(s)` vs `slot K was spectating on M of my frame(s)` | what a spectator meant to do, against what reached everyone else. The two must be close; they were 6001 against 189 before `PlayerEntity.Spawn`'s flag wipe was fixed |
| `alt-attack` | rising edges on the alt-attack button, both sides — separates "the press never arrived" from "it arrived and laid no bomb" |
| `node lookups unresolved: N` | frames on which a player's room node could not be worked out from its position at all, even after the body and half a unit either side were tried. Non-zero means that map has places the portal volumes do not cover, and the players there are drawn without culling rather than hidden — see the AD2 ALINOS PERCH section in NETWORK-DIAGNOSTICS |
| `SIMULATED LINE: ...` at the top of a report | this client was run with `-netlag`/`-netloss`. **Every number under it describes a line the client made up**, and none of it may be quoted as a measurement of the real thing |
| `chat: sent=N received=M` | M counts the server's own join/leave notices as well as what players typed, and clients start three seconds apart, so it is not predictable from the client count. Zero on every client is the number that means something |
| `jumppads 7 (7/7 launched)` | seven in the room, seven tried, seven launched the player standing on them |
| `afflicted freeze 1 burn 2 disrupt 0` | how many players were frozen/burned/disrupted at least once during the run |
| `(probe freeze ok burn nohit disrupt FAIL)` | the affliction probe: `ok` inflicted it, `nohit` the charged shot missed, `FAIL` hit with no state, `n/a` that hunter wasn't in the match |
| `hit claims: N declared, A applied, D already resolved (P% stood)` | what this client asked the authority to make real and what came back. **`already resolved` is the healthy majority** -- the authority found the hit itself and the claim changed nothing -- and `applied` is a hit it never would have found. On loopback with no lag the split should be all `already resolved`: 56 received and 54 already resolved, measured. `refused` climbing is the number to chase; `unanswered` non-zero means verdicts are not arriving |
| `hit claims (as authority): ... (X damage, K kills, H headshots rescued)` | the server's side, every 30 s in its log. These three are the complaint answered directly: each is a shot that landed on somebody's screen and would otherwise have counted for nothing |
| `void (dead shooter)` | the kill arbitration refusing a shot whose shooter had already been put down in a strictly earlier world. **Not a fault** -- it is the rule working. Zero in every run so far, because nothing in the harness stages a mutual kill on purpose; `-hitrig duel` is what does |
| `puppet smoothing: on, N frames of buffer` | the playout clock. `interpolated / held` is samples blended against samples that held a position; `starved` is the read point running past everything that arrived, and each one grows the buffer |
| `% of frames still (longest run N)` in the smoothing line | **not a stutter measurement on its own.** A player standing still contributes still frames honestly and the tour has whole phases of it. Compare between two arms of the same scenario, never as an absolute -- and the same goes for `worst`, which a respawn fills with a room-sized step |
| `clock snaps` in the smoothing line | read points put back rather than walked back. On a loaded box these are mostly *this* client failing to hold 60 Hz, not the line |

Common traps

- Comparing raw totals without normalising for join times or rotations.
- Assuming a hit is a hit: afflictions require a charged shot; some weapons require holding `FullCharge * 2` frames.
- Standing at an entity's position expecting its trigger to notice — volumes are relative to entities.
- Writing a button twice in one frame: edges must be computed once, at the end of the frame, from the state at its start — computing them inside each write lets a later clear wipe the edge an earlier press produced (a charged weapon fires on release, so nothing charged ever fired).
- A tolerance of 1.0 silently turns a check into decoration — if a check stops failing, make sure it can still fail.
- Judging a Sylux against a Weavel's abilities: only three hunters lay bombs, only Weavel leaves a halfturret.
- Testing a rotation with only one map in it misses nothing — testing it with only *more than one* map misses the "is this a new match" class of bug, which needs the single-map case too. Test both `maprotation-test.txt` (three maps) and `maprotation-one.txt` (one), each with a two-point goal so a match ends inside a run.
- A run with match restarts in it is not a clean read of the tour: `NetTestScript`'s 15 phases key to the *server's* clock, and a restart resets it, so clients that finish loading a fraction of a second apart land in different phases. Form reconciliation now uses elapsed simulation frames: eight for a mismatch without a transition, then a bounded attempt/force fallback, while observed transitions receive a latency allowance. A `their form stayed wrong for N frames` result still needs a restart-free run to interpret.
- Reading one client's column of a cross-check as a defect before checking the others: low for *every* observer is systematic (a rate, a sampling difference); low for one is that client's own story, usually a late join.
- Comparing final scoreboards in any run where somebody leaves. A leaver's
  score is cleared on every other client (`NetSlotManager.Deactivate`, and
  deliberately -- somebody who has gone is not on the board), and every run
  staggers its exits, so the last client to print is the only row still
  standing. One churn run read "scoreboards disagree by up to 11" for exactly
  this reason and nothing else. Compare `scoreboard mid-run:`.
- Reading a cross-check where both sides derive the number from the same
  place. `spectating` originally counted the entity flag on both the owner's
  machine and the observers', so when a respawn wiped that flag at the source
  both sides counted the same zero and agreed perfectly about a feature that
  was reaching nobody. A cross-check is only worth having if the two sides can
  disagree: the owner's side now counts what it *decided* (`SpectatorMode`).
- Reading `damage pipeline` as healthy just because both ends are non-zero: `25/0` against `0/258` is a byte counter that ran backwards and nearly wrapped forward, not noise on a working pipeline — the three-digit number is the tell.
- **Reading any timing number from a run that shared the box.** Measured
  2026-09-14: an unrelated 27-room `-maptest -players 8 -renderprobe` sweep in
  another terminal took this machine from 4 GB free to 159 MB, and three
  consecutive hitrig arms died mid-match with no exception, no report and a
  debug log that simply stops — which reads exactly like a scenario that
  produced nothing. `free -m` before a run, and again if an arm comes back
  empty. The counts (claims, hits, kills) survive contention; the milliseconds
  do not.
- Reading `hit claims: N applied` on a clean line as the feature doing
  something. It should be **zero** there: a claim only rescues what the
  authority's own rewind could not reach, and on loopback it reaches
  everything. `applied` climbing is a measurement of the line, not of the
  code.

## Frame pacing verification (2026-09-05)

The decoupled frame loop (`.claude/render/FRAME-PACING.md`): the simulation
pinned at 60 Hz under a picture drawn at the display's rate.

| Check | Result |
|---|---|
| `-frametimingcheck` | all 9 cases pass. 60.000 Hz of simulation under 60 / 144 / 165 / 240 Hz displays and under jitter; **60.000 Hz under a 40 Hz display**, where the old single-rate loop played in slow motion; a 2 s stall returns 1 step, not 120; alpha visits 10/10 tenths at 144 Hz |
| `-maptest -drawrate 1 / 3 / 4`, TEST ARENA + MP1 SANCTORUS + AD2 ALINOS PERCH | MAPTEST line **identical** at every draw rate; `draws advancing the game: 0`; blend ratio 0.666 at N=3 and 0.749 at N=4, the predicted `(N-1)/N` |
| Real `RenderWindow`, `-fpscap display` under Mesa | sim **60.05 Hz** while the picture ran at **180 Hz** (Mesa ignores VSync here, which makes it the better test), measured over 22 s from the debug log. 0 dropped, 0 stalls |
| Real `RenderWindow`, `-fpscap 120` / `-fpscap 144` | draw 119.9 / 143.8 Hz, sim 59.93 Hz, histogram a clean alternation of 0 and 1 step per frame |
| Performance against the unmodified build | 1832 identical frames in **32.9 s vs 33.1 s** -- marginally faster, not slower |
| `run-check.sh 130`, 3 clients, x3 | **0 mismatches every run**, matching the 2026-08-23 baseline |
| `run-check.sh 150`, 6 clients, x4 | 5, 5, 3, 0 mismatches -- inside the unmodified build's own range, see below |
| Output frame rate, `-room "MP3 PROVING GROUND" -fpscap 240 -debuglog` (WSL, Mesa llvmpipe) | **sim 60.14 Hz / draw 81.0 Hz**, steps per frame `[534, 1310, 3, 0, 0, 1]` -- 534 of 1848 frames drew with no step behind them, which a 60 Hz loop could not produce. 81 is this box's software rasteriser, not the loop: a quarter render scale only reached 88 |
| Scoreboard, 8 players, `-bots`, every game mode, players 2-8, pro HUD on and off | no crash and no `MAPFAIL` anywhere. The reported Android crash **was not reproduced here** |
| Android head | builds (`-p:AndroidSdkDirectory=$HOME/android-sdk`); **not run on a device** -- no emulator here has game files, so a match cannot be loaded |

### The effect bursts that failed their own parity check

Reported after the first fix attempt shipped, and it is the one that mattered
most: *"quand je charge un missile l'animation au bout du weapon ne s'affiche
pas et les explosions sur les murs ne sont pas visibles."*

Effects spawn particles on every other simulation step, and an element records
the parity it was created with so its **first** advance spawns -- which is where
a burst lives. Upstream incremented `_frameCount` *after* `GetDrawItems`, so the
spawn and the advance that followed it saw the same number; the 120 fps split
moved the increment into the step, before the draw, and every element's first
advance began failing its own check. Bursts stopped; the continuous smoke and
debris of the same effects did not, so it read as "part of it is missing".

**Look for this shape wherever two halves of one frame read the same counter**
-- splitting the frame moved the counter between them. `_effectFrame` is now a
clock the effect system owns.

| Same tour, MP3 PROVING GROUND, 8 players | Effect particles |
|---|---|
| Broken | 776 |
| Fixed | 1254 |

Confirmed visually as well, on the same frame of the same deterministic run: a
missile kill with **no fireball at all** before, and the full explosion after.
`-maptest` reports `effect particles` now and fails on zero, because every other
number in the run was unmoved by this.

### The particles that were cleared in the wrong pass

Reported as three separate faults after the 120 fps change -- wall impacts not
appearing, the first Shock Coil of a fight missing, and shot artifacts drifting
in all directions while moving and firing -- and they were two causes.

**Draw state cleared in the simulation step.** `_singleParticleCount = 0` stayed
in `OnSimulationFrame` when the step and the picture stopped being one call, so
a picture with no step behind it drew the previous frame's particles again where
they were then, and kept appending until the 200-entry table was full and
started dropping new ones. **Look for this shape in anything with per-frame draw
state: ask where it is cleared, and if the answer is "in the step", it is
wrong.**

**Interpolation against a pooled entity's previous life.** Beams and impact
effects come off a free list still holding their last life's transform history,
and the 24-unit guard only declines a reuse *further* away than that. A reuse
nearer -- the common case in a firefight -- was blended between two unrelated
places. Interpolation is gone entirely; `.claude/render/FRAME-PACING.md` has
what it would take to bring it back.

### Verified 2026-09-14 -- prediction retired by claim id, damage inputs replicated

`run-check.sh 150 Samus Weavel Sylux` on this box (3 clients, nothing else
running), against the local authoritative server:

```
0 mismatches, scoreboards agree (within 0 event)
ALPHA    9 predicted, 9 confirmed (100%), 0 denied, 2 kills predicted, 0 undone
BRAVO   10 predicted, 10 confirmed (100%), 0 denied, 1 kill predicted, 0 undone
CHARLIE  6 predicted, 6 confirmed (100%), 0 denied
```

and the authority's own comparison of each client's number against its own,
which is new (`NetHitClaims.DescribeAgreement`, on the `sim:` report):

```
sim: predicted vs resolved damage:
sim:   PowerBeam   17 paired, 17 agreed (100%), 0 differed -- 102 claimed against 102 resolved
sim:   Missile      1 paired,  1 agreed (100%), 0 differed --  48 claimed against  48 resolved
sim:   ShockCoil    2 paired,  2 agreed (100%), 0 differed --   2 claimed against   2 resolved
```

Two things to read this with:

- **Loopback cannot fail this test.** Every input the replication now carries
  -- charge level, double damage, the alt-form ram, the damage level, the
  affinity rule -- is identical on both machines when they are the same box
  with the same settings file and a 1 ms round trip. 100% here says the
  plumbing is right, not that the bug is gone; the case it was reported from is
  a real line with jitter, which is `hard/run-latency.sh` and the Pi.
- **`Predicted == Confirmed + Denied` holds again.** It did not: self-hits are
  counted apart (`SelfPredicted`), and their timeouts were being counted in the
  shared `Denied`, so a run with three unanswered rocket-jump splashes read
  "13 predicted, 12 confirmed, 2 denied". The per-weapon breakdown is what made
  it visible, since its columns add up to the aggregate exactly.

A **four**-client run on this box, taken while two `dotnet publish` jobs were
running, produced one `damage-taken` mismatch. That is the paragraph below,
not a regression: read a mismatch count here only against a same-day run of
the build being compared with.

### Verified 2026-09-14 against the Japan server (250 ms, 3 clients)

`run-jap.sh 180 Samus Weavel Sylux` against `13.78.14.98:27892`, which is the
run that matters: loopback cannot fail the prediction tests, because every
input is identical on both ends at 1 ms.

```
JP-A  21 predicted, 19 confirmed (90.5%), 2 denied, 2 kills predicted, 0 undone
JP-B  35 predicted, 35 confirmed (100%),  0 denied, 3 kills predicted, 0 undone
JP-C  89 predicted, 89 confirmed (100%),  0 denied, 2 kills predicted, 0 undone
0 mismatches, scoreboards agree
health bars: drawn low by 0 / 6 / 3 points (worst 0 / 6 / 1), high by 0
```

Against the same run before the floor was fixed: **2770 / 4369 / 4193 points
low, worst 48 / 61 / 32**. See `NETWORK-PREDICTION.md`, *The floor was where the
error actually lived*.

### The 6-client mismatch count on this box measures the box

Worth writing down because it cost an hour and will cost it again. Running
`run-check.sh 150` with six clients on this WSL machine, on the **unmodified**
build, over seven runs:

```
0, 1, 0, 2, 3, 6, 9 mismatches      clients at 39.6-44.6 fps
```

Every one of them on `zoom` or `unmorph`, plus one `damage-taken`. Those are
frame counts of an observed state, scoped to a **server-clock phase**, and
`NetFeatureCheck` says so itself where `alt-form-total` is defined: the
phase-scoped count "depends on both clients agreeing where a phase boundary
falls, and a difference there looks exactly like a replication failure."

Six clients plus a server do not fit on this box: they run the tour at ~41 fps,
so each drifts against the server clock, and the later-starting clients
(CHARLIE, DELTA, FOXTROT) spend fewer frames inside each phase and count less.
The mismatch count is a measurement of that starvation.

**So do not read a 6-client mismatch count here as a pass or a fail without a
same-day run of the build you are comparing against.** Three clients fit and
give 0 reliably; that is the local instrument. Eight clients against the Pi
(`run-remote.sh`) is the real one.

## Historical hard-case baseline (2026-09-01)

This table predates the current always-server-authoritative / isolated-hosted-
process architecture. Keep it as regression history, **not as the latest
capacity, handover or hosting status**. New current measurements must name the
build/topology they exercised.

Full detail, including the faults it found, is in
`.claude/testing/TEST-HARD-CASES.md`.

| Check | Result |
|---|---|
| 8 real clients, 220 s, against the Pi | 0 mismatches, 0 position snaps, 2 ms pings, `dropped=0` |
| Map rotation with real clients | **was killing every client**; fixed (`NodeRef.None` on rebuild), 0 of 3 crashed afterwards and both rotations were followed |
| A player's line cut for 1, 3, 8 and 40 s | historical client-authority result: under 30 s the connection recovered; at 40 s the peer timed out and the old topology moved authority. Current normal server-authority runs must not hand authority between players |
| Latency 100 / 200 / 300 / 500 ms (kernel `netem`) | scoreboards identical at every level; snaps 0/0/18/190 |
| Loss 5 / 15 / 30 %, both legs | no divergence; hit registration is what degrades |
| Every player spectating | replicates properly now; nobody can be hit; match and rotation carry on |
| Six clients recording demos | 13 KiB/s each, every file replays |
| Ninth player at a full server | `Refused` reason 1 in 11 ms (server deployed 2026-09-01); a protocol-3 Hello gets reason 2 |
| Pi 3B hosted ceiling | **historical in-process/client-authority result**: 20 matches / 160 synthetic players. Not a current capacity claim for isolated authoritative child processes |
| Server journal across 3.5 h of hostile testing | zero exceptions or errors |

## Previous verified status (2026-08-23)

| Check | Result |
|---|---|
| 3 clients, 130 s, `run-check.sh` | PASS on all three, 0 mismatches, scoreboards agree within 0 events |
| Damage pipeline on that run | 69/56/49 resolved on the authority, 69/56/48-49 replayed on both observers (shortfall = one hit still in flight at cutoff) |
| Match won on points, 3-map rotation, 4 clients | every rotation announced once, all four peers carried across. **Superseded 2026-08-31: with real clients on the Pi, a rotation to a DIFFERENT room killed every client outright** -- `ArgumentOutOfRangeException` in `RoomEntity.DrawRoomParts`, a pooled player's `NodeRef` into the old room indexing the new room's part arrays. Reproduced 3/3 on the build before this batch's changes and 0/3 after `NetRoomChange.RebuildPlayers` hands each slot `NodeRef.None`. A rotation to the SAME room (a one-map rotation) never hit it, which is why five consecutive matches on one map passed while the first change of map did not |
| Match won on points, one-map rotation | correctly reloads the same room — the case that used to strand everybody |
| Authority leaving mid-session | **historical compatibility result** from the former client-authority topology. Normal current matches have no player-authority promotion |
| `tools/check-dedicated-server.sh` | the directory starts; a gameplay server without proprietary game files reaches its startup contract, explains why authoritative simulation cannot start, exits 1, and never advertises a dead room |

Last full map pass (2026-08-20):

| Check | Result |
|---|---|
| Every multiplayer map, 8 players / 8 AI bots, 22-24 s each | 33/33, zero crashes, both modes |
| Players placed | 8/8 on 27 maps; 0/8 on the six First Hunt "biodefense chamber" rooms (no spawn points — the only `MAPFAIL`s) |
| `moved N/M (furthest X units)` | how far each player actually got, summed per frame and ignoring respawn jumps. The metric that separates a bot that cannot navigate from one that is losing: a bot standing still still counts as spawned, and never firing or dying reads the same either way. DUST2 with generated node data gets 7/8 and 785 units over 150 s; MP3 PROVING GROUND gets 8/8 and 369 over 60 |
| Jump pads / teleporters, stood on in turn | every pad on all 24 maps that have one; both of AD1 TRANSFER LOCK's teleporters moved the player |
| Afflictions, probed per map | freeze on 14 maps, disrupt on 13, burn on 6 — a sample, not a verdict: ~2 charged shots per state per map, and the probe's first (uncharged) press of each cycle can land a hit that registers as "hit, no affliction," which understates burn's column most |
| 6 clients against the Pi, 110 s | 3 mismatches, all late-joiner/stop-skew artifacts; pings 6-16 ms, `dropped=0` |
| Pi 3B under 6 clients | server process 11-16% of one core, system 65-75% idle |
| Invulnerable players (`BeamEffectiveness` all-zero) | none |
