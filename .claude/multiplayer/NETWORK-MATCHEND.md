# Multiplayer — match end, rotation and the double-counted kill

A match that somebody won used to end the session for that client alone:
`GameState.ProcessFrame` ran the winner's camera, then the scoreboard, then
faded to black -- correct offline, and on a server it meant every client
dropped back to its own launcher while the server's rotation, which had never
heard anybody won, kept counting down a map nobody was still playing.

| Piece | What |
|---|---|
| `NetMatchEnd` | on the authority, sends `PacketType.MatchEnd` when `GameState.MatchState` leaves `InProgress`, repeating until the server's own state answers `FlagEnding`. Any client that sees `FlagEnding` while it still thinks the match is running sets `MatchTime = 0`, so results play out normally rather than cutting away |
| `DedicatedServer` intermission | Clock and score endings use `GameState.MatchFinalCameraSeconds + GameState.MatchEndingSeconds + 1`: 5 seconds of final/winner camera, 10 seconds of results and 1 second of safety, totaling 16 seconds. The constants are shared with the client. |
| `CameraSequence.Intro` reloaded per rotation | it is a **static**, loaded in one place -- `SceneSetup.LoadNewRoom`, which a rotation does not go through. So after the first map of a session, every results screen flew the sequence belonging to the map the session *started* on, whose keyframes hand the camera a `NodeRef` resolved against a level no longer in memory. Out of range that is an `ArgumentOutOfRangeException` in `RoomEntity.DrawRoomParts` killing every client at once; in range it is a black room. `NetRoomChange.ReloadIntroCamSeq` does what SceneSetup does, with the same room-id arithmetic, and calls `Initialize()` so every keyframe is re-resolved |
| `RoomEntity.ModCanPlace` | the backstop: a `NodeRef` is three indices into one particular room's arrays and nothing about it says which room, so before anything is indexed with it the indices must address something this room has and `NodeRef.RoomName` must be this room or one of its connectors. Refusing leaves `_partVisInfoHead` null, which `GetDrawInfo` already reads as "draw every part" -- the correct picture, merely uncalled. It fires nowhere in the rotation harness now that the cause above is fixed, which is the point of a backstop |
| no culling once the match is over | the end-of-match camera is the one camera in the game that is not out of somebody's eyes: an authored orbit of the winner, free to sit outside every room part in the level. That is a portal walk reaching nothing and a results screen that is black with stray polygons in the corners -- reported on MP2 HARVESTER, and *not* a stale reference. `UpdateRoomParts` returns early whenever `MatchState != InProgress` in a multiplayer match. It costs nothing worth having: nobody is playing |
| `MatchStatePacket.MatchId` | counts matches from the server's start. The room key alone can't answer "is this a new match" -- a one-map rotation (what **Host a game** builds) plays the same room repeatedly, and a client watching only the name sat on its results screen for the rest of the session |
| `MatchStatePacket.PointGoal` | the score that wins, from the rotation file; belongs to the server for the same reason the clock does |

Two things this needed underneath:

- **`NetMatchSync` must not adopt the clock during results.** `MatchTime` is
  the countdown the results sequence itself runs on; putting the old map's
  remaining time back on top of it every frame meant the sequence never
  finished.
- **The authority must stop reporting once the server has heard.** A client
  lingers in results for a second or two after the server has rotated; during
  that window the authority was reporting the end of the *new* match the
  instant rotation landed -- one whole map skipped per rotation, visible as
  two `match over` lines in the server log in the same second.

## The double-counted kill (2026-08-23)

Reported as "6-2 on the scoreboard and the match was already over for the
second client" -- which then had to be hunted down as a motionless zombie and
killed to reach 7, and crashed at the rotation that followed.

**A replayed kill was counted twice.** `NetDamage.Replay` runs the authority's
hit through `PlayerEntity.TakeDamage` so the victim feels it (indicator,
flinch, banner) -- but `TakeDamage` also *awards* the kill (`Points`/`Kills`
for the attacker, `Deaths` for the victim). The authority had already done all
three and the snapshot being applied already carried the result, so on every
machine except the authority the kill landed on the scoreboard twice, for one
frame -- enough, because `EndIfPointGoalReached` reads `TeamPoints`, and
`UpdateState` rebuilds `TeamPoints` from `Points` at the end of the same
frame, after the replay and before the next snapshot corrects it. On the sixth
kill of a seven-point match the client saw seven, ended its match, and sat on
`6-2` for the rest of the round while its player stood frozen in everyone
else's game (`UpdateScene` doesn't run outside `MatchState.InProgress`).
Reproduced exactly: one client at `matchState=GameOver score=1/2p` while the
authority was still at `score=1/1p`, 23 seconds apart.

Fix is **not** "assign the authority's scores after the replay instead of
before" -- that looks equivalent and isn't, since the replay runs on the
victim's slot and moves the *attacker's* row, so it only works if the attacker
happens to apply after the victim. `NetDamage` saves and restores the three
score arrays around the replay instead, so ordering stops mattering.

**Then the rotation exposed the same bug from a different angle.** A client
resets its scores on loading the next room, but the two machines don't change
room on the same frame -- so a client that finished loading first was still
receiving the *finished* match's snapshots, carrying the winning scores, and
applied them to a fresh match at zero. `ApplyState` now ignores peer scores
while `NetRoomChange.Settling`, for the same reason it already ignores peer
positions there: a match starts at zero on every machine, so there's nothing
to learn from the authority during that second.

**The rule that makes a third variant of this survivable:** only the machine
that keeps score may decide the score has been reached
(`NetMatchEnd.MayEndOnScore`, checked by `EndIfPointGoalReached` and
`ModeStateDefender`). Every other client's `Points` comes from the authority's
snapshot, so a client reaching the goal "first" is disagreeing with the only
copy of the scoreboard that counts, and nothing returns `MatchState` to
`InProgress` until the next rotation.

`NetMatchEnd.RecoverIfStranded` is the backstop under all of it: a client
showing an ending for `MatchFinalCameraSeconds + MatchEndingSeconds + 4`
(19 seconds) while the server says the match is running is put back into play.
This threshold exceeds the normal 16-second intermission.

Final replay footage covers up to 300 pre-kill frames at 1x. GameOver waits for
preparation/playback to reach EOF before entering results, so preparation does
not consume the five seconds of footage. Skip/failure uses the winner-camera
fallback and the normal timer. Server match/epoch transitions still invalidate
stale replay identity; the one-second safety margin is not an unlimited grace
period for a stalled client.

**The crash after rotation is unexplained** -- no log, no stack from that
client. What's known: it spent two minutes in `MatchState.Ending` while
`NetHooks.AfterSimulation` kept applying snapshots (spawns, replayed deaths,
effects, sounds) into a scene whose `UpdateScene` never ran to process or
retire them. That's the state the fixes above prevent, so it may go with them
-- not reproduced since, and not claimed fixed.

The netlog couldn't show any of this and now can: `STATE` lines carry
`matchState=` and `goal=`, and each slot carries
`score=<points>/<teampoints>p <kills>k<deaths>d` -- which is what made the
double count visible as `1/2p` in a single line.

## Persistent lobby lifecycle (2026-09-20)

Persistent lobbies now use the lobby itself as the between-match decision
point. After the normal winner camera and results report, a
`ServerSessionPolicy.Lobby` session goes `PostMatch -> Lobby` on the same UDP
session. It does **not** open the results map ballot and it does not
automatically start another match. The launcher sees `IsInLobby`, tears down
the rendered match, and exposes the same lobby with the same connected slots.
The owner starts the next round explicitly. Continuous-rotation servers keep
their existing post-match rotation and ballot behavior.

The lifecycle cleanup is deliberately server-owned:

- A voluntary `Bye` and a timeout both converge on `Remove`, so roster,
  load-barrier masks, owner migration, votes and simulation cleanup share one
  path.
- If the last player leaves a standalone persistent lobby while it is
  `Starting`, `InMatch` or `PostMatch`, the abandoned world is stopped
  immediately and the server returns to a clean empty lobby. A later joiner
  can never inherit the previous match.
- If the last player leaves a launcher/directory-hosted lobby, the child server
  exits immediately instead of waiting for the host-pool reap grace. Shutdown
  sends the directory Farewell and releases the port.
- Process ownership follows lobby ownership when the hosted owner disconnects
  or explicitly transfers ownership. Otherwise a surviving lobby owner could
  close the room but leave an ownerless child process behind.
- Returning to the lobby finalizes the canonical replay, stops the
  authoritative simulation, clears the hit-verdict sink, load masks, map
  ballot and mid-match vote state. No per-match state is allowed to leak into
  the next round.

## The next map, voted on the results screen (2026-09-13)

This ballot is now **continuous-session only**. Persistent lobby sessions do
not open it; they show the report/hunter picker and then return to the lobby,
where the next map and settings can be changed before another explicit start.
The mid-match vote remains available in both policies.

`callvote map`, moved to the one moment nobody is playing. The mid-match vote
(`MapVote`, `PacketType.Vote`/`VoteState`, F1/F2) is unchanged and still there;
this is the same question asked during the intermission, where it interrupts
nothing and needs neither a cooldown nor a minimum player count to stop it
being a nuisance.

**Every map, and the most votes wins.** Two decisions that are easy to undo by
accident and were both arrived at the hard way:

- The first shape of this was a four-map ballot the server drew up. It asked
  the wrong question -- "where next" is not multiple choice and the four on
  offer were never the one somebody had in mind. So the list is the client's
  own room list, scrolled, and what travels is only the tally. Maps with votes
  are **pulled to the top**, which is what makes a vote visible at all: four
  rows of a twenty-seven-map list are otherwise a window onto nothing.
- There is **no threshold**. A mid-match vote needs one because it interrupts
  seven people who did not ask to be asked; an intermission interrupts nobody,
  and a bar to clear only produces the outcome nobody voted for -- a room that
  picked three maps between them and is sent to a fourth because none of the
  three reached seventy per cent.

| Piece | What |
|---|---|
| `PacketType.MapChoices` (28) | server -> clients: whether the ballot is open, how many players there are, and up to eight (room key, votes) pairs -- the maps somebody has picked, most-wanted first. Broadcast on the same one-second tick as everything else and only while the ballot is open, for `MatchStatePacket`'s reason: UDP drops, and a client that missed the one packet would sit out the whole intermission with no tally |
| `PacketType.MapPick` (29) | client -> server: one room key, or empty to take the pick back. Re-sendable, unlike a vote's ballot -- this is asked with a countdown on screen, so changing your mind while the picture is up is the normal case rather than a way to game a race. The client repeats its pick once a second in case one was lost |
| server validation | any map `ResolveRoomKey` can load, except the one being played. There is no short list to check against any more, and that is the only check that was ever load-bearing |
| `DedicatedServer.ApplyLeader` | the most-voted map becomes `_rotation.PlayNext` **as the picks arrive**, not when the countdown ends -- `NextRoomKey` is read off the rotation, so the NEXT line and the row marked NEXT are one fact rather than two guesses. It falls back the other way too (`MapRotation.ClearPending`), which is what the last pick being taken back, or the only voter leaving (`ReviewPicks`), has to do. Ties go to whoever got there first, which is what `Recount`'s stable order is for |
| `Mods/MapPick.cs` | the client's list, cursor, scroll and mirror of the tally. The order is **the server's** for the voted part: two machines sorting equal counts differently would put a different row under each player's cursor while they are clicking. The cursor tracks the *row* rather than the index, since the order moves under it every time anybody votes |
| `Mods/Render/PlayerEntityMapPick.cs` | four rows under the hunter picker, in the same column: a preview, the name, `3 OF 8`, and NEXT on the leader. A scroll bar down the outer edge, because four rows of a long list look exactly like a short list. The row count is chosen to fit the space left under the picker rather than stated -- that space is not a number anybody can write down, since the picker's height is derived from its contents and is half again as tall on a phone |
| `Mods/Render/MapThumbnail.cs` | the launcher's own PNG previews, decoded, box-filtered to 256x144 and bound as a scene texture. **Thrown away on both edges of the results screen**: texture names are counted per room (`Scene._textureCount`) and a binding kept across a map change is a name the next room will overwrite. One decode per frame, so twenty-seven files never land on one frame |
| `Scene.DrawHudTexture` | `DrawHudFlatBox`'s rectangle with a texture on it. `DrawHudObject` derives its destination from the *source's* dimensions, which is right for art authored at one texel per DS pixel and useless for a photograph that has to land where the layout says |
| the scoreboard | squeezed left while the screen is up (`ModScoreSqueeze`), and the ping column dropped -- both it and the radar live in the corner the pickers do. The squeeze is worked out from where the panel actually is, since that edge moves with the window's shape |

**Three traps, and the previews hit all three before they drew.**

1. The per-frame bookkeeping (`EndScreen.Tick`, the thumbnail cache's
   one-decode-per-frame reset) was first put in `RenderWindow.OnRenderFrame`,
   which **every harness client skips** -- they drive `Scene.OnUpdateFrame`
   directly -- so the previews were blank under `-netcheck` while the list
   beside them was correct. It lives in `Scene.OnDrawFrame` now.
2. `Scene.BindGetTexture` hands back the next value of `_textureCount`, which
   is **also what the next model loaded takes** -- and the next model loaded is
   the hunter the results screen puts in its own preview window, built on the
   very frame the cache is filled. The thumbnails were quietly overwritten with
   pieces of Samus's armour a frame after they were bound. They take reserved
   names of their own now (1_100_000 upwards, in a ring of 64), which is
   `UiOverlay`'s trap and `UiOverlay`'s answer.
3. **`StbImage.Load(..., StbiImageFormat.Rgba)` gives a span whose length is
   the *file's* channel count, not the format asked for.** These PNGs are
   opaque, so the buffer is four bytes a pixel while the span reports three
   quarters of it: every read at a three-byte stride lands a channel further
   into the row than the last, which is a picture of vertical red, green and
   blue stripes. Asking for `Rgb` -- what the file already is, and the call
   `MapTextureBake` has been making correctly all along -- makes the two agree.

Additive in both directions, so **no protocol bump**: a server built before
this never opens a ballot and the results screen shows the NEXT line it always
showed; a client built before it drops an unknown type on the floor.

Offline the same list is offered and the answer starts the next match --
`GameState.PlayPickedMap` -> `Shell.PlayAnother`, which is the pause menu's
"Leave match" and the front screen's "Start" sent on one frame, so the launcher
is rebuilt and hidden again without being drawn. One player is the whole room.

Historical measurement used the private `run-mapvote.sh` wrapper (a copy of `run-rotate.sh` with
`-mapvote`): three clients, four 30-second matches, **every vote cast carried
by the server** (9/9 and 11/11 across two runs), three rotations followed by
every client, zero crashes, zero node refs outliving their room, zero feature
mismatches.
The harness votes the way the feature is meant to be used -- agree with
whatever is in front, propose row N only when nothing is -- which is both the
shape of the thing and the only rule that converges, since a voted map moves
to the top of everybody's list. `MphRead -netcheck ... -mapvote N` is off by
default on purpose: a scripted client that votes changes what a real server
plays next, and the hard-case batch runs against the public one.
`-netcheck ... -hudshots` is the other half of the instrument and is new with
this: a visible window and `SaveWindow`, so the HUD -- which is the whole of
what a results screen is -- can be photographed from a networked client at all.
