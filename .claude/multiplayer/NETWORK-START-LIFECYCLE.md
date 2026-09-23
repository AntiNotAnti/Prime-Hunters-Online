# Reliable loading and start lifecycle

Each start freezes MatchId, AuthorityEpoch, a nonzero uint StartGeneration, match
configuration and the eligible participant mask. Internal stages are Preparing,
Loading, Countdown and InMatch; the public session remains Starting until the
last stage. The authority marks itself ready only after synchronous room creation.
The 15-second client grace starts after that creation, and the 1.5-second countdown
starts only after every remaining expected participant is loaded.

SessionState carries the start generation/stage and reliable match definition.
MatchLoaded and MatchLoadFailed include all three identity fields and use the
reliable channel. A client sends one loaded event per identity; transport retries
replace the old 100ms application loop. Duplicate loads are idempotent and stale
loads cannot release a barrier. Starting gameplay remains frozen. InMatch is the
only authoritative reveal boundary; estimated countdown timing never unfreezes
simulation on its own.

Timeout removes missing peers through the normal authoritative removal path.
Explicit load failure removes that participant. Existing team-validity rules may
cancel a start if removals leave an invalid match. Owner removal transfers lobby
ownership normally. A new arrival never enlarges an active barrier. After InMatch,
an individual late join loads and sends its own identity-fenced ready event before
the authority accepts its gameplay intent. It receives the current full snapshot.
Continuous rotations use the same barrier as persistent lobby rematches.

`--load-lifecycle` combines virtual-time boundary tests with the real UDP lobby
suite (admission, ownership, commands, rematches, teams, late join and rotation).
Asset-free tests prove control-plane behavior; rendered loading/first-frame quality
still requires extracted game assets and an actual multiplayer run.
