# Current protocol 17 transport (unreleased train)

Established UDP datagrams have a 24-byte little-endian header: marker D7, packet
type, flags, protocol, 64-bit random connection ID, uint sequence, uint ACK and
32 ACK bits. ACK bit zero is ACK-1. Sequence ordering uses signed modular distance;
32-step and larger jumps are handled explicitly. ACK validity has its own flag;
sequence zero is valid. Payload codecs retain the full-state v16 representation.

Hello/discovery/directory traffic is unsequenced. An admitted Welcome creates a
random nonzero ID at the server. The client accepts it only from its requested
endpoint with the pending Hello's client ID. Known IDs from another endpoint and
old IDs at a reused endpoint are rejected before ACK processing. ClientId alone
no longer silently migrates a connection to another endpoint. A rejoin requires
new admission; it does not authenticate an account or transport encryption.

Max datagram is 1472 bytes (IPv4 Ethernet UDP); max established payload is 1448.
The old 1232-byte budget could not hold eight players plus 56 health spawns. Replay stores
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
