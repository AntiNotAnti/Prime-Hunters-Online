# Reliable loading and start lifecycle

Each start freezes MatchId, AuthorityEpoch, a nonzero uint StartGeneration, match
configuration and the eligible participant mask. Internal stages are Preparing,
Loading, Synchronizing, Countdown and InMatch; the public session remains Starting until the
last stage. The authority marks itself ready only after synchronous room creation.
A 15-second boundary now marks/logs a slow loader without removing it; the hard
stuck-loader deadline is 60 seconds and starts after authority creation. The
1.5-second countdown starts only after every remaining expected participant has applied its authoritative world baseline.

SessionState carries the start generation/stage and reliable match definition.
MatchLoaded and MatchLoadFailed include all three identity fields and use the
reliable channel. A client sends one loaded event per identity; transport retries
replace the old 100ms application loop. Duplicate loads are idempotent and stale
loads cannot release a barrier. Clients also report identity-fenced load stages
(StartReceived, Preflight, WorldBuild, PresentationLoad and SceneReady) so a slow
machine is diagnosable without confusing it with a dead connection.

Starting gameplay remains frozen through preparation/loading. Countdown is a
server commitment made ahead of time: a disposable MatchStartCommit refreshes
remaining time every 100 ms, and each client arms the same release edge using
minimum RTT plus a bounded jitter safety margin. The commit is identity-fenced
and may legitimately arrive before the reliable SessionState that first says
Countdown; it is accepted in that order, while duplicate/reordered older commits
cannot move the edge. InMatch remains the authoritative server phase and confirms
the transition instead of deciding the client's first playable frame by
packet-arrival timing.

Fresh commitments use their monotonic socket-arrival timestamp, so time spent
queued behind a client load or render hitch does not restart the countdown.
Release is latched for the start identity: a late refresh cannot freeze gameplay
again after the committed edge. A new start generation resets that latch.

Both desktop and Android pump control traffic on every frozen render frame,
without advancing the scene or NetFrame. Reconnect retries keep their wall-clock
cadence while frozen and are disabled for playback. They reset the fixed-step accumulator
through the barrier and discard the render interval crossing release. Headless
scene stepping has the same simulation guard. MatchLoaded is emitted only by
scene-load completion paths; stepping an old scene while a new match is Starting
must not acknowledge that new scene.

The initial Starting publication sends three independent datagram sequences for
one reliable event before authority construction begins. Identical outstanding
payloads retain their event ID and receiver deduplication still applies once.
The socket worker also services ordinary reliable retries during a synchronous
authority build; the burst avoids waiting for its retransmission deadline.

A late-join scene that finishes before SessionState retains its MatchId/epoch
until the matching start generation arrives. Teardown or a different match drops
that pending readiness; a delayed packet cannot ready an unrelated scene.

The 15-second slow boundary only logs. At 60 seconds a still-missing participant
is treated as stuck and removed through the normal authoritative removal path.
Queued Pong/load-progress liveness is drained before that timeout decision, and
the authority refreshes participant liveness after its own synchronous room build
so clients are not penalized for a server-side stall. Explicit load failure
removes that participant immediately. Existing team-validity rules may
cancel a start if removals leave an invalid match. Owner removal transfers lobby
ownership normally. A new arrival never enlarges an active barrier. After InMatch,
an individual late join loads and sends its own identity-fenced ready event before
the authority accepts its gameplay intent. It receives a frozen full baseline through the same three-lane bootstrap.
Continuous rotations use the same barrier as persistent lobby rematches.

Persistent lobby room prewarm is single-flight with the actual load: Start joins
an in-flight selected-room compile/read/decode instead of racing a second copy.
The one-room cache remains bounded to the currently advertised room and survives
the match so a same-map rematch can reuse it; selecting or editing a different
room invalidates/replaces it.

Authority initialization and match-runtime teardown preserve this lobby-owned
cache. Full NetSession.Stop and dedicated-server shutdown release it. Read's
ordinary scene caches still clear at teardown, keeping retention bounded to the
one prewarmed room.

`--load-lifecycle` combines virtual-time boundary tests with the real UDP lobby
suite (admission, ownership, commands, rematches, teams, late join and rotation).
Asset-free tests prove control-plane behavior; rendered loading/first-frame quality
still requires extracted game assets and an actual multiplayer run.
The suite also covers a lost first start notice with immediate wire copies,
exactly-once burst delivery, delayed countdown draining and release latching,
frozen scene/RNG/network clocks, and lazy prewarm reuse across authority restarts.

## Protocol 18 world readiness

`MatchLoaded` means scene construction finished. It sets SceneLoaded/Loaded,
never MatchReady. The authority constructs initial player lives/spawns without
advancing the simulation and sends three reliable `WorldBootstrap` envelopes:
fast player state, player slow state and full world state. Each envelope carries
MatchId, AuthorityEpoch, StartGeneration, BootstrapRevision, recipient slot
generation, AuthorityFrame, SlowRevision and WorldRevision. Each peer retains a
stable baseline until it acknowledges it; retries run every 250 ms in addition
to transport reliability.

A client buffers all three matching lanes while frozen, checks roster/lifecycle,
applies owner and remote spawn/health/form/weapon/score, applies pickup state,
and initializes its applied frame and live lane caches. Only then does it echo
that exact identity in reliable `WorldReady`. Missing lanes, stale generations,
malformed duplicates and conflicting revisions cannot ready a client. Repeated
valid baselines are idempotent and re-ACKed. Room/session teardown clears both
pending and applied baseline state.

Synchronizing separates the Loaded mask from the WorldReady mask. Only the
latter releases countdown. Client FreezeGameplay additionally requires the
local applied baseline even if a start commitment arrived early; the authority
rejects intent and claims from unready peers. A late join follows this same
application/acknowledgement path without enlarging the original participant
barrier. The UI distinguishes Loading world, Synchronizing world and Ready;
server diagnostics identify the slot, stage and loaded/ready masks.

`--bootstrap-scene <game-data>` exercises eight real asset-backed player
entities through the production baseline decoder while simulation stays frozen:
missing/reordered/duplicate/malformed lanes, roster gating, exact owner spawn,
health/weapon/score, all hunter types, settled alt form/halfturret, random state,
initial nonzero ACK and
stale rematch rejection. `--load-lifecycle` covers the UDP control plane;
its asset-free scene-application stand-in is not a render/first-frame test.

The loading pump also applies any newer fast snapshot received after release in
the same drain, before the renderer draws without a simulation step. This closes
the observed first-picture old-spawn race during late join. The asset bootstrap
check covers that packet ordering and asserts that simulation time stays frozen.

MatchLoaded deduplication includes the local slot and its generation as well as
the match/start identity. Re-admission with a new occupant can reuse the loaded
scene but sends MatchLoaded again and waits for a fresh WorldReady baseline. A
duplicate Welcome for the same occupant leaves readiness intact. The asset
bootstrap regression covers both paths without advancing the scene.
