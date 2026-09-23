# Reliable loading and start lifecycle

Each start freezes MatchId, AuthorityEpoch, a nonzero uint StartGeneration, match
configuration and the eligible participant mask. Internal stages are Preparing,
Loading, Countdown and InMatch; the public session remains Starting until the
last stage. The authority marks itself ready only after synchronous room creation.
A 15-second boundary now marks/logs a slow loader without removing it; the hard
stuck-loader deadline is 60 seconds and starts after authority creation. The
1.5-second countdown starts only after every remaining expected participant is loaded.

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
the authority accepts its gameplay intent. It receives the current full snapshot.
Continuous rotations use the same barrier as persistent lobby rematches.

Persistent lobby room prewarm is single-flight with the actual load: Start joins
an in-flight selected-room compile/read/decode instead of racing a second copy.
The one-room cache remains bounded to the currently advertised room and survives
the match so a same-map rematch can reuse it; selecting or editing a different
room invalidates/replaces it.

`--load-lifecycle` combines virtual-time boundary tests with the real UDP lobby
suite (admission, ownership, commands, rematches, teams, late join and rotation).
Asset-free tests prove control-plane behavior; rendered loading/first-frame quality
still requires extracted game assets and an actual multiplayer run.
