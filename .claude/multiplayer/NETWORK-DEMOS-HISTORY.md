# Historical v2 replay implementation

These notes describe the original implementation before the isolated replay
migration. They explain old measurements and fixes; they are not the current
ownership or file-format contract. See [NETWORK-DEMOS.md](NETWORK-DEMOS.md).

# Demos: recording a match and watching it back

`Mods/Network/DemoRecorder.cs`, `DemoFile.cs`, `DemoPlayback.cs`,
`DemoInfo.cs`. Started and stopped from the pause menu ("Record replay",
online matches only) and from `-netcheck ... -recorddemo`; watched from the
front screen's Replay Studio library, which runs `MatchStart.LaunchDemo`.

## The design, in one line

A demo is **every packet this client received, verbatim**, replayed into the
same `NetSession` on the same frame it originally arrived on. Nothing is
re-encoded, so every packet-type handler, room transition and match-end
sequence runs unchanged during playback; the player decides only *when* a
packet is handed over, never what it means.

That decision has consequences worth knowing before touching any of it.

## Two things a demo has to synthesize

A client does not receive everything it knows. Two holes, both filled by
writing the packet this machine was about to send in the shape it would have
arrived in:

| Hole | Why | Filled by |
|---|---|---|
| This player's own input | the server never relays your `SlotIntent` back to you; you already know what you pressed | `DemoRecorder.RecordOwnIntent`, from `NetSession.SendIntent` |
| The authority's own snapshot | `DedicatedServer.HandleSnapshot` forwards to every peer **except the sender** | `DemoRecorder.RecordOwnSnapshot`, from `NetSession.BroadcastSnapshot` |

The second one was missing and it mattered more than it sounds. The
authority is whichever client connected first, which is normally whoever set
the match up, so it is the common case rather than a corner one. Measured on
the harness: an authority recording for 30 s **received 31 snapshots** (the
once-a-second `NotifyAuthority` echo) while sending 1800. And the snapshot is
not one stream among several -- it is the only carrier of health, score, the
damage sequence and the spawn flag, and `NetPlayerBridge.ApplyState` is the
only thing during playback that ever calls `ModNetSpawn`. So the host's demo
did not look thin, it opened on **an empty room**: nobody was ever placed,
nothing was ever hit, no score ever moved.

## Frames, not milliseconds

Format version 1 stamped each record with `Environment.TickCount64` and the
player released them against a `Stopwatch`. Three separate faults, all
visible as the same complaint -- the replay is choppy and drops things:

- **The engine's clock is not the wall clock.** `Renderer` advances the
  simulation by a fixed 1/60 s per frame however long the frame took, so a
  replay at 58 fps consumed 60 frames of recording every 60 frames, fell
  behind real time, and caught up in bursts. Only the newest of a burst
  survives: `RemoteStates` and `RemoteIntents` are one slot each.
- **`TickCount64` ticks every 15.6 ms on Windows.** A 60 Hz stream stamped on
  a 64 Hz clock clumps and drifts against the frame boundaries, producing the
  same bursts on a machine holding a perfect 60 fps.
- **The room loads after the stopwatch starts.** `DemoPlayback.Join` returns,
  `renderer.AddRoom` takes seconds, nothing is pumped, and the first frame
  afterwards released all of it at once -- so the replay opened several
  seconds in with everything between discarded.

Version 2 stamps `NetSession.NetFrame - startFrame` and `PumpFrame` releases
one frame's worth per simulated frame. None of the three can happen: the
recorder counts the frames the simulation counts, and a load in the middle
costs nothing because no time passes.

Measured with `-demoinfo FILE -replay` on a real 27 s recording:
**99.5% of replayed frames got exactly one fresh snapshot, 1 frame in 1499
got more than one, longest run with none: 6** (a hitch that was in the
recording, faithfully reproduced).

`Join` also stopped waiting on a clock. It reads records until the match
state is known plus a 120-frame grace for the roster, which is a few hundred
frames of parsing rather than the up-to-8-second wall-clock wait it was.

### And then it rewinds

The search is not free: it hands its records to the session to be acted on,
and there is no scene yet to act on them. So the first one to three seconds
of every recording were parsed and thrown away, and the replay opened that
far in. Reported as **"the first shot isn't in the demo"** -- a charged
missile fired right after pressing record. It was in the demo; it was never
played.

`DemoPlayback.Rewind` reopens the file at frame 0 once the room key is known,
and `NetSession.RewindPlayback` clears the bookkeeping that would otherwise
refuse the rewound packets as stale -- `_lastSnapshotFrame`,
`_lastSlotIntentFrame`, the bridge and damage baselines -- while **keeping**
what the search was for: `ServerMatch`, `SlotOccupied`, `SlotHunter`,
`GameState.Nicknames`, all of which `NetLaunch.BuildPlayers` reads on the
next line. Re-delivering the same `MatchState` is a no-op (`HandleMatchState`
only raises `MapChanged` when the room key differs), so nothing reloads.

Measured on a 25 s authority recording, frames 1-1500 with 1410 SlotIntent
records:

| | frames replayed | intents applied |
|---|---|---|
| before | 1499 of 1620 | 1496 of 1666 |
| after | **1501 of 1500** | **1410 of 1410** |

Every intent in the file now reaches the simulation, and 99.9% of frames get
exactly one snapshot with no frame taking two.

## The file

```
"PPDM" | version (2) | protocol      <- 6 bytes, never compressed
--- deflate ---
[frame delta: 1 byte, 0xFF = escape + uint32] [length: uint16] [packet bytes]
...
```

Deflated because the stream is 60 snapshots a second whose neighbours differ
in a few floats. Flushed every 15 frames rather than per record: a sync flush
costs 14% at one per record and a fraction of a percent at this rate, and a
quarter of a second is what a demo that dies with the game loses.

Measured on the harness, 2 players:

| | v1 rules | v2 |
|---|---|---|
| non-authority, 27 s | ~381 KiB (14.1 KiB/s) | **76.6 KiB** (2.8 KiB/s), 4.7x |
| authority, 30 s | ~138 KiB *and no snapshots* | **68.6 KiB** (2.3 KiB/s), 5.5x |

So the authority's demo became correct -- 1831 snapshots instead of 31 -- and
still came out at half the size of the broken one.

**Version 1 files are refused, not read.** Their timestamps mean something
else and their body is not compressed, so there is nothing that could read
one by accident.

## Checking a demo

```bash
MphRead -demoinfo "path/to/x.ppdemo"            # what is in it
MphRead -demoinfo "path/to/x.ppdemo" -replay    # and how it lands, frame by frame
# Optional private wrapper, when available: run-demo.sh 30 authority
```

`-demoinfo` needs no game files, no window and no server. Read it in this
order: the `Snapshot` row (none means an empty room -- it says so), then the
`KiB/s`, then, with `-replay`, the percentage of frames that got a snapshot.
Exit code 1 for a demo with no snapshots, or a replay where more than 5% of
frames took a burst or a gap ran past 10 frames.

Note the working directory: `ConsoleSetup.Run` does
`Directory.SetCurrentDirectory(BaseDirectory)`, so a relative path is
relative to the binary, not to the shell. Pass an absolute one.

## Gotchas

- **`LocalSlot` stays -1 for the whole playback session**, on purpose
  (`NetSession.StartPlayback`). There is no local player, so every slot is a
  puppet driven by the recorded intent stream, and `NetHooks.LocalSlot`
  returns -1 rather than falling through to 0 the way "connected, Welcome
  hasn't landed" does.
- **The viewer is a spectator and cannot be anything else.** `Renderer`
  starts `SpectatorMode` on the first frame anyone is available; F
  toggles a free camera on top of it.
- **Nothing spawns without a snapshot.** `NetHooks.ForceSpawn` returns false
  during playback -- neither host nor authority -- so placement comes only
  from `ApplyState`'s `FlagSpawned` branch. This is why the missing authority
  snapshots produced an empty room rather than a degraded one.
- **A demo recorded on a listen host (`NetSession.StartHost`) has no
  `MatchState` in it** and so cannot be played back: `Join` needs a room key
  and only a dedicated server sends one. Every path the launcher offers goes
  through a dedicated server, in-process or otherwise, so this is a note
  rather than a bug.
