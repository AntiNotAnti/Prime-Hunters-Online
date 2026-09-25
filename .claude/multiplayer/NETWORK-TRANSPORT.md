# Protocol 19 transport

## Protocol 19 target identity

Intent is now 96 payload bytes: the eight-byte shot-state tail ends with encoded
player slot, ushort generation and ushort life. Explicit `0x80/0/0` means no player;
malformed or absent decisions cannot enable player fallback. Live ingress requires
the entire v19 tail and admission refuses older peers. Sequenced edges retain their layout. PlayerState adds three turret bytes; CombatAck adds exact terminal outcomes. See [continuous targeting](../../docs/network/continuous-targeting.md).


CombatAck (31) has a 15-byte stream header and up to sixteen 19-byte outcomes (343 bytes including the transport envelope). CombatStudy (51) is bounded background diagnostics, outside reliable control. See [combat telemetry](../../docs/network/combat-telemetry.md).

## Replication lanes

| Packet | Cadence | Contents | Maximum datagram |
|---|---|---|---|
| SnapshotFast (48) | 60 Hz | Frame/RNG/lifecycle, pose, health, weapon/status and four damage events per player | 927 bytes, eight players |
| PlayerSlowState (49) | 10 Hz + meaningful changes | Full generation-tagged team/points/kills/deaths and match clock data | 183 bytes |
| WorldState (50) | 4 Hz + pickup state changes | Full NetHealthSync state | 433 bytes at 56 spawns |

Slow/world streams carry MatchId, AuthorityEpoch and a monotonically ordered
nonzero revision. Each packet is independently useful; no base-delta dependency
chains exist. Fast packets merge the latest generation-matched slow state and
world state into the canonical in-process snapshot. Replay retains that canonical
representation. Live bootstrap (46) sends the same three lanes and WorldReady
(47) echoes their required revisions; the largest bootstrap fits under 1200 too.
`--protocol18` tests populated worst-case packets, byte-exact reconstruction,
revision fencing and allocation-free warmed decoding. The diagnostic lane counters
report aggregate sender packets/s, bytes/s, average and maximum fast sizes.

## Sequenced input edges

Protocol 18 kept its 32-byte edge budget and overall packet size: 16 two-byte events
replace eight frame masks. Low byte = wrapping event sequence; high byte = three
age bits (0–7) and five action bits (zero means empty). Nonzero action IDs are the
one-based IntentButtons bit positions. Sender history is oldest first and keeps
each edge for eight frames. Overflow retains newer input and preferentially drops
the oldest repeated action, with telemetry.

Shoot, Jump, Morph, AltAttack, ScanVisor and four directional Roll presses are
edge events. Movement and Boost remain held buttons; zoom/form and WeaponSelect
are absolute state. Shoot retains both held and edge semantics. The receiver has
a 64-sequence deduplication window and a bounded 128-event execution queue. It
emits at most one edge per action per simulation step, retaining additional
identical edges for later steps. Recovered Shoot age tracks its source frame.
Reset all receive/queued state on life, slot generation, disconnect and match
teardown. The replay checkpoint schema includes sender/receiver state and has a
new fingerprint. Protocol-17 peers/replays cannot interpret the new codec.

Established UDP datagrams have a 24-byte little-endian header: marker D7, packet
type, flags, protocol, 64-bit random connection ID, uint sequence, uint ACK and
32 ACK bits. ACK bit zero is ACK-1. Sequence ordering uses signed modular distance;
32-step and larger jumps are handled explicitly. ACK validity has its own flag;
sequence zero is valid. Protocol 18 changes gameplay payloads as described below.

Hello/discovery/directory traffic is unsequenced. An admitted Welcome creates a
random nonzero ID at the server. The client accepts it only from its requested
endpoint with the pending Hello's client ID. Known IDs from another endpoint and
old IDs at a reused endpoint are rejected before ACK processing. ClientId alone
no longer silently migrates a connection to another endpoint. A rejoin requires
new admission; it does not authenticate an account or transport encryption.

Rare control datagrams retain a 1472-byte maximum and 1448-byte payload budget.
Realtime gameplay is independently bounded at 1200 bytes. Legacy admission/authority
seed paths can still carry the canonical Snapshot as rare control traffic; normal
60 Hz publication and WorldReady use the new lanes. Replay stores
application packets, not live UDP envelopes. Replay-world fragmentation and
snapshot tails respect the new payload budget. Historical v16 baselines remain
in tools/nettest/baselines; protocol-bound replay compatibility rules are retained.

Each connection keeps a bounded 512-attempt sent ring. ACK samples yield smoothed
RTT, variance and minimum observed RTT. Overwritten unacknowledged attempts count
as *estimated* losses, not proof of wire loss. Reporting uses value snapshots and
never drives gameplay authority. Transport state is protected by one instance lock.

## Queue and scheduling budgets

Live traffic has FIFO critical/realtime/background queues with a hard 2048-packet
budget: 2039 queued entries plus nine latest-state cells. Normal traffic stops
128 entries before the queue ceiling. Critical events are never evicted to make
room for state. New reliable events are not ACKed if retention is unavailable.
The default pump handles at most 128 critical, 256 realtime and 32 background
packets. Dedicated servers drain background after owed simulation work. Playback
keeps its independent lossless ordered path.

Per-connection token buckets limit intents to 180/s (64 burst), downstream state
to 2000/s (512 burst), control to 60/s (128 burst), and background to 100/s (32
burst). Preconnection discovery uses a bounded shared 300/s bucket. Drops have
transport counters and never emit per-packet strings. These are conservative
initial bounds, not a substitute for asset-backed load measurement.

Fault injection now delays original envelopes before ACK/dedup processing.
Duplicated fault entries hold immutable bytes and obtain separate pooled decode
buffers. Promotion is bounded to 256 arrivals per pump. Reliable servicing sends
at most four due attempts per connection/pass and also runs every 50 ms on the
receive worker, so a synchronous room load cannot stop control retransmission.

An explicit pending Hello allows a restarted server to supply a new connection
incarnation on the existing client socket. Superseded connection IDs cannot be
restored by a delayed Welcome, including during another outstanding Hello.
The remembered superseded-ID set is bounded at 64; a transport must be recreated
before a 65th server incarnation. ClientId alone never moves a live admission to
a different endpoint. A secure endpoint-rebinding handshake is outside this train.

Connected sends cache a connection-owned SocketAddress and use synchronous
Socket.SendTo over the caller's span. This removes the measured 72 B/send endpoint
serialization allocation. Discovery/admission, delayed fault injection and replay
ownership copies are separately scoped; the live send allocation test excludes them.

During scene loading, newer full `Roster` and `SessionState` publications
supersede older pending retries of that same type. Each replacement receives a
new reliable event ID; the application revision still rejects reordered older
state. Commands, results, Welcome and the three distinct WorldBootstrap lanes
are never superseded. The 40-event capacity, 256-ID dedup span and explicit
failure behavior remain bounded. `--reliable` covers a 96-revision startup burst,
old ACK isolation, all three bootstrap lanes and ordinary-command exhaustion.
