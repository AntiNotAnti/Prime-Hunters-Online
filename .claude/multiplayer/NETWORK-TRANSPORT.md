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

Max datagram remains 1232 bytes; max established payload is 1208. Replay stores
application packets, not live UDP envelopes. Replay-world fragmentation and
snapshot tails respect the new payload budget. Historical v16 baselines remain
in tools/nettest/baselines; protocol-bound replay compatibility rules are retained.

Each connection keeps a bounded 512-attempt sent ring. ACK samples yield smoothed
RTT, variance and minimum observed RTT. Overwritten unacknowledged attempts count
as *estimated* losses, not proof of wire loss. Reporting uses value snapshots and
never drives gameplay authority. Transport state is protected by one instance lock.
