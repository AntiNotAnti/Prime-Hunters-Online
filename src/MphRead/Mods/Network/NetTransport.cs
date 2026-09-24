using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MphRead.Mods.Network
{
    public readonly struct ReceivedPacket
    {
        public readonly IPEndPoint Sender;
        public readonly byte[] Data;
        public readonly int Length;
        public readonly long ArrivedAt;
        internal readonly bool Pooled;
        public readonly ulong ConnectionId;
        public readonly uint Sequence;

        public ReceivedPacket(IPEndPoint sender, byte[] data, int length, long arrivedAt = 0,
            bool pooled = false, ulong connectionId = 0, uint sequence = 0)
        {
            Sender = sender;
            Data = data;
            Length = length;
            ArrivedAt = arrivedAt == 0 ? Stopwatch.GetTimestamp() : arrivedAt;
            Pooled = pooled; ConnectionId = connectionId; Sequence = sequence;
        }

        public PacketType Type => Length > 0 ? (PacketType)Data[0] : default;
        public ReadOnlySpan<byte> Payload => Data.AsSpan(1, Length - 1);

        internal void Release()
        {
            if (Pooled)
            {
                ArrayPool<byte>.Shared.Return(Data);
            }
        }
    }

    /// <summary>
    /// UDP transport on a dedicated worker thread.
    ///
    /// The game loop never touches a socket: it only drains a bounded
    /// concurrent queue. This mirrors the threading decision documented in
    /// ndsrecomp's wifi_net.cpp, and it matters for the same reason -- a
    /// blocking recv on the simulation thread turns a network hiccup into a
    /// frame hitch. Bounded, because an unbounded queue converts a flood
    /// into unbounded memory growth instead of dropped packets.
    /// </summary>
    public sealed class NetTransport : IDisposable
    {
        public NetTransportTelemetry Telemetry { get; } = new();
        // Connection state belongs to this transport. The socket worker and
        // simulation sender serialize access, including sequence allocation.
        private readonly object _connectionLock = new();
        private readonly Dictionary<IPEndPoint, NetConnection> _connections = new();
        private readonly Dictionary<IPEndPoint, uint> _pendingConnections = new();
        private readonly HashSet<ulong> _supersededIds = new(); // bounded by 64 reconnects per transport

        public NetReliableSnapshot? ReliableStats(IPEndPoint endpoint)
        { lock (_connectionLock) return _connections.TryGetValue(endpoint, out var peer) ? peer.Reliable.Capture(NowMilliseconds) : null; }
        public NetConnectionSnapshot? ConnectionStats(IPEndPoint endpoint)
        { lock (_connectionLock) return _connections.TryGetValue(endpoint, out var peer) ? peer.Capture() : null; }
        private readonly IPEndPoint?[] _expiredConnections = new IPEndPoint?[64];
        public void RetireConnection(IPEndPoint endpoint)
        { lock (_connectionLock) if (_connections.TryGetValue(endpoint, out var peer)) peer.RetiredAt = NowMilliseconds; }
        public void ForgetConnection(IPEndPoint endpoint)
        { lock (_connectionLock) { _connections.Remove(endpoint); _pendingConnections.Remove(endpoint); } }
        private static bool Unsequenced(PacketType type) => type is PacketType.Hello or PacketType.StatusQuery
            or PacketType.StatusReply or PacketType.MasterQuery or PacketType.MasterList or PacketType.MasterHeartbeat
            or PacketType.HostRequest or PacketType.HostReply;
        private readonly UdpClient? _socket;
        private readonly Thread? _worker;
        // Playback remains lossless and ordered. Live queue reserves 128 control
        // slots plus nine bounded coalescing cells within the 2048 packet ceiling.
        private readonly ConcurrentQueue<ReceivedPacket> _inbox = new();
        private readonly NetPacketQueue _liveInbox = new(2048 - 9, 128);
        private readonly ConcurrentQueue<ReceivedPacket> _connectionFailures = new();
        private NetTokenBucket _discoveryBudget;
        private readonly CancellationTokenSource _cancel = new();
        private volatile bool _running;
        private int _inboxCount;
        private int _playbackBytes;

        // Real-time clients do not benefit from processing six obsolete
        // snapshots after a hitch. Keep only the newest full snapshot and the
        // newest SlotIntent for each remote slot. Control/event traffic remains
        // ordered in _inbox, and playback/fault-injection never enables this.
        private volatile bool _coalesceRealtimeState;
        private readonly object _stateLock = new();
        private ReceivedPacket? _latestSnapshot;
        private readonly ReceivedPacket?[] _latestSlotIntent =
            new ReceivedPacket?[MphRead.Entities.PlayerEntity.SlotCapacity];
        private long _statePacketsCoalesced;
        public long StatePacketsCoalesced => Interlocked.Read(ref _statePacketsCoalesced);

        /// <summary>
        /// How many received packets may wait for the game loop.
        ///
        /// The number that matters is how long a frame can take while the
        /// queue still holds everything that arrived during it. Eight clients
        /// on one machine produce roughly two thousand packets a second
        /// between them, so 256 covers a 130 ms frame -- which sounds
        /// generous until eight copies of the engine share one CPU and a
        /// frame takes exactly that long. At that point the queue overflows,
        /// and what was dropped was a player's aim.
        /// </summary>
        public const int MaxQueuedPackets = 2048;

        /// <summary>
        /// Bytes the OS may hold before the worker thread gets to them. The
        /// default (64 KB) is the other half of the same problem: the worker
        /// is quick but it is still one thread, and a scheduling hiccup of
        /// fifty milliseconds with eight clients talking is a lot of bytes.
        /// </summary>
        private const int SocketBufferBytes = 1 << 20;

        private volatile bool _autoPong;

        /// <summary>
        /// Packets held back because <see cref="NetLag"/> is on: arrivals
        /// waiting to be handed to the game loop, and sends waiting to leave.
        ///
        /// Both are plain FIFOs and both are drained from the head only while
        /// the head is due, so a jittered hold can delay a datagram but never
        /// overtake the one in front of it. Reordering is a different fault
        /// with different guards against it, and mixing the two would make a
        /// run that reproduced one impossible to read.
        /// </summary>
        private readonly NetFaultQueue<ReceivedPacket> _heldIn = NetLag.CreateQueue<ReceivedPacket>(outbound: false);
        private readonly NetFaultQueue<(IPEndPoint Target, byte[] Data, int Length)> _heldOut
            = NetLag.CreateQueue<(IPEndPoint Target, byte[] Data, int Length)>(outbound: true);
        private readonly object _heldLock = new();
        private Thread? _lagWorker;

        /// <summary>
        /// Answer Ping on this thread instead of queueing it for the game
        /// loop. Opt-in: a session that has a frame to wait for wants this
        /// and a directory server, whose replies are part of its own
        /// bookkeeping, does not.
        /// </summary>
        public void AnswerPingsImmediately() => _autoPong = true;

        public void EnableRealtimeStateCoalescing() => _coalesceRealtimeState = true;

        private static bool OlderThan(in ReceivedPacket next, in ReceivedPacket previous) => next.ConnectionId != 0
            && next.ConnectionId == previous.ConnectionId && !SequenceMath.Newer(next.Sequence, previous.Sequence);

        private bool TryCoalesceRealtimeState(ReceivedPacket packet)
        {
            if (!_coalesceRealtimeState || _lagWorker != null)
            {
                return false;
            }
            lock (_stateLock)
            {
                if (packet.Type is PacketType.Snapshot or PacketType.SnapshotFast)
                {
                    if (_latestSnapshot.HasValue)
                    {
                        if (OlderThan(packet, _latestSnapshot.Value)) { packet.Release(); Telemetry.Coalesce(); return true; }
                        _latestSnapshot.Value.Release();
                        Interlocked.Increment(ref _statePacketsCoalesced);
                        Telemetry.Coalesce();
                    }
                    _latestSnapshot = packet;
                    return true;
                }
                if (packet.Type == PacketType.SlotIntent && packet.Length > 1)
                {
                    int slot = packet.Data[1];
                    if ((uint)slot < _latestSlotIntent.Length)
                    {
                        if (_latestSlotIntent[slot].HasValue)
                        {
                            if (OlderThan(packet, _latestSlotIntent[slot]!.Value)) { packet.Release(); Telemetry.Coalesce(); return true; }
                            _latestSlotIntent[slot]!.Value.Release();
                            Interlocked.Increment(ref _statePacketsCoalesced);
                        Telemetry.Coalesce();
                        }
                        _latestSlotIntent[slot] = packet;
                        return true;
                    }
                }
            }
            return false;
        }

        public int LocalPort { get; }
        public long PacketsDropped { get; private set; }

        /// <summary>
        /// Packets dropped by every transport in this process, for the test
        /// harness: "the other client never saw me turn" has two very
        /// different causes, and this is what tells them apart.
        /// </summary>
        public static long TotalPacketsDropped;

        /// <summary>
        /// Packets sent by any transport in this process. Used by the
        /// headless client tests to prove the authority is actually
        /// publishing rather than silently doing nothing.
        /// </summary>
        public static long TotalPacketsSent;

        public NetTransport(int port, bool playbackOnly = false)
        {
            // Playback uses the normal inbox/handlers without opening a UDP listener.
            // A replay cannot receive real datagrams or send gameplay traffic.
            if (playbackOnly) return;
            _socket = new UdpClient(AddressFamily.InterNetwork);
            if (OperatingSystem.IsWindows())
            {
                // SIO_UDP_CONNRESET. Without it, a peer that vanishes makes
                // Windows raise ConnectionReset on the *next* receive, which
                // would kill the worker. The control code is Windows-only and
                // throws PlatformNotSupportedException elsewhere, so it is
                // guarded rather than swallowed.
                _socket.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            try
            {
                _socket.Client.ReceiveBufferSize = SocketBufferBytes;
                _socket.Client.SendBufferSize = SocketBufferBytes;
            }
            catch (SocketException)
            {
                Telemetry.Error();
                // A system that refuses the size keeps its default; the
                // session still works, it just tolerates less of a stall.
            }
            // Only so the worker notices _running going false; nothing waits
            // on this in normal operation.
            _socket.Client.ReceiveTimeout = 50;
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            LocalPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
            _running = true;
            _worker = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MphRead net"
            };
            _worker.Start();
            if (NetLag.Active)
            {
                // Only when asked for. A thread that wakes a thousand times a
                // second to look at an empty queue is not something a real
                // session should be paying for.
                _lagWorker = new Thread(LagLoop)
                {
                    IsBackground = true,
                    Name = "MphRead net lag"
                };
                _lagWorker.Start();
            }
        }

        /// <summary>
        /// Let out whatever the simulated line has finished holding.
        ///
        /// Only the send side needs a thread of its own: arrivals are promoted
        /// by <see cref="Drain"/>, which the game loop calls every frame
        /// anyway, and a send held until the next frame would put a frame of
        /// latency on top of the one being simulated.
        /// </summary>
        private void LagLoop()
        {
            while (_running)
            {
                double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                while (true)
                {
                    (IPEndPoint Target, byte[] Data, int Length) held;
                    lock (_heldLock)
                    {
                        if (!_heldOut.TryDequeue(now, out held))
                        {
                            break;
                        }
                    }
                    SendNow(held.Target, held.Data.AsSpan(0, held.Length));
                }
                Thread.Sleep(1);
            }
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            double lastMaintenance = 0;
            while (_running)
            {
                try
                {
                    double now = NowMilliseconds;
                    if (now - lastMaintenance >= 50) { lastMaintenance = now; ServiceConnections(); }
                    // Blocking, with a timeout only so shutdown is prompt.
                    //
                    // This used to poll Available and Thread.Sleep(1) between
                    // passes, which put an arrival delay on the front of every
                    // packet the session ever received -- a millisecond at
                    // best, and Sleep(1) is not a millisecond on Windows,
                    // where the scheduler's tick is 15.6 ms unless something
                    // in the process has raised the timer resolution. The
                    // cost showed up as ping: the number on the scoreboard is
                    // a round trip through two of these loops, so a server
                    // one millisecond away by ICMP was reported at rather
                    // more. Blocking costs nothing -- the thread exists for
                    // this and does nothing else -- and hands the packet over
                    // the moment the kernel has it.
                    byte[] data = ArrayPool<byte>.Shared.Rent(NetConfig.MaxPacketSize + 1);
                    bool handedOff = false;
                    try
                    {
                        EndPoint remote = any;
                        int length;
                        try
                        {
                            length = _socket!.Client.ReceiveFrom(data, 0, NetConfig.MaxPacketSize + 1,
                                SocketFlags.None, ref remote);
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            continue;
                        }
                        Telemetry.Received(length);
                        if (length == 0 || length > NetConfig.MaxPacketSize
                            || remote is not IPEndPoint sender)
                        {
                            Telemetry.Invalid(length > NetConfig.MaxPacketSize);
                            continue;
                        }
                        if (_lagWorker != null)
                        {
                            // Faults act on original datagrams BEFORE sequence/ACK
                            // processing. Duplicate entries share only immutable bytes.
                            byte[] heldCopy = data.AsSpan(0, length).ToArray();
                            lock (_heldLock) _heldIn.Enqueue(NowMilliseconds,
                                new ReceivedPacket(sender, heldCopy, heldCopy.Length), lossOverride: NetLag.LossPercent / 100);
                            continue;
                        }
                        handedOff = AcceptDatagram(sender, data, length);

                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            ArrayPool<byte>.Shared.Return(data);
                        }
                    }
                }
                catch (SocketException)
                {
                    Telemetry.Error();
                    // Transient: an ICMP unreachable from a peer that left.
                    // Keep serving the peers that are still here.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private bool AcceptDatagram(IPEndPoint sender, byte[] data, int length)
        {
            NetHeader.TryRead(data.AsSpan(0, length), out var header);
            if (!Unwrap(sender, data, ref length)) return false;
            if (_autoPong && (PacketType)data[0] == PacketType.Ping)
            { Send(sender, PacketType.Pong, data.AsSpan(1, length - 1)); return false; }
            var packet = new ReceivedPacket(sender, data, length, pooled: true,
                connectionId: header.ConnectionId, sequence: header.Sequence);
            if (TryCoalesceRealtimeState(packet)) return true;
            if (!_liveInbox.TryEnqueue(packet))
            { Telemetry.Drop(); PacketsDropped++; Interlocked.Increment(ref TotalPacketsDropped); return false; }
            Telemetry.Queue(_liveInbox.Count);
            return true;
        }

        /// <summary>Bounded live pump; playback retains its lossless ordered drain.</summary>
        public IEnumerable<ReceivedPacket> Drain(NetPumpBudget? budget = null)
        {
            ServiceConnections();
            if (_lagWorker != null) PromoteHeldArrivals();
            NetPumpBudget limits = budget ?? NetPumpBudget.Default;
            int failureBudget = limits.Critical;
            while (failureBudget-- > 0 && _connectionFailures.TryDequeue(out var failure)) yield return failure;
            if (_socket == null)
            {
                while (_inbox.TryDequeue(out var playback))
                {
                    Interlocked.Decrement(ref _inboxCount); _playbackBytes -= playback.Length;
                    try { yield return playback; } finally { playback.Release(); }
                }
                yield break;
            }
            for (int category = 0; category < 3; category++)
            {
                int remaining = category == 0 ? Math.Max(0, failureBudget + 1)
                    : category == 1 ? Math.Max(0, limits.Realtime - 9) : limits.Background;
                while (remaining-- > 0 && _liveInbox.TryDequeue((NetPacketPriority)category, out var packet))
                {
                    Telemetry.Queue(_liveInbox.Count);
                    long started = Stopwatch.GetTimestamp();
                    try { yield return packet; }
                    finally { Telemetry.Processed(Stopwatch.GetTimestamp() - started); packet.Release(); }
                }
            }

            if (_coalesceRealtimeState && limits.Realtime >= 9)
            {
                for (int slot = 0; slot < _latestSlotIntent.Length; slot++)
                {
                    ReceivedPacket? latest;
                    lock (_stateLock)
                    {
                        latest = _latestSlotIntent[slot];
                        _latestSlotIntent[slot] = null;
                    }
                    if (latest.HasValue)
                    {
                        ReceivedPacket value = latest.Value;
                        try { yield return value; }
                        finally { value.Release(); }
                    }
                }

                ReceivedPacket? snapshot;
                lock (_stateLock)
                {
                    snapshot = _latestSnapshot;
                    _latestSnapshot = null;
                }
                if (snapshot.HasValue)
                {
                    ReceivedPacket value = snapshot.Value;
                    try { yield return value; }
                    finally { value.Release(); }
                }
            }
        }

        private void PromoteHeldArrivals()
        {
            double now = NowMilliseconds;
            for (int work = 0; work < 256; work++)
            {
                ReceivedPacket packet;
                lock (_heldLock) if (!_heldIn.TryDequeue(now, out packet)) return;
                byte[] copy = ArrayPool<byte>.Shared.Rent(NetConfig.MaxPacketSize + 1);
                bool owned = false;
                try
                {
                    packet.Data.AsSpan(0, packet.Length).CopyTo(copy);
                    owned = AcceptDatagram(packet.Sender, copy, packet.Length);
                }
                finally { if (!owned) ArrayPool<byte>.Shared.Return(copy); }
            }
        }

        private static readonly IPEndPoint _playbackSender = new(IPAddress.Loopback, 0);

        /// <summary>
        /// Feeds a packet read back from a demo file into the same queue a
        /// real receive would have used, so <see cref="Drain"/> and
        /// everything downstream of it (<c>NetSession.Handle</c> and every
        /// packet-type handler) runs completely unchanged during playback --
        /// the sender endpoint is never checked by any of it, only the
        /// bytes. The socket this transport opened is never used for real
        /// traffic in that mode; it exists only because the constructor
        /// always binds one.
        /// </summary>
        public void EnqueueForPlayback(byte[] data, int length, long arrivedAt = 0)
        {
            if (length < 1 || length > data.Length || length > ushort.MaxValue)
                throw new System.IO.InvalidDataException("Invalid replay packet length.");
            // Unlike live UDP, silently dropping a replay packet changes the recording.
            // Allow large recorded bursts, with an explicit failure for pathological files.
            if (Volatile.Read(ref _inboxCount) >= 65536)
                throw new System.IO.InvalidDataException("Replay exceeds 65536 packets on one frame.");
            if (length > 32 * 1024 * 1024 - _playbackBytes)
                throw new System.IO.InvalidDataException("Replay exceeds 32 MiB of packets on one frame.");
            _playbackBytes += length;
            Interlocked.Increment(ref _inboxCount);
            _inbox.Enqueue(new ReceivedPacket(_playbackSender, data, length,
                arrivedAt: arrivedAt));
        }

        /// <param name="extraHoldTicks">
        /// More simulated line to hold this datagram behind, on top of the
        /// outbound half. Only the automatic Pong uses it; see the call site.
        /// </param>
        /// <param name="immediateCopies">One to three independent datagrams for
        /// a reliable event. Extra copies preserve the event's dedup identity
        /// and are reserved for latency-sensitive startup publication.</param>
        public void Send(IPEndPoint target, PacketType type, ReadOnlySpan<byte> payload,
            long extraHoldTicks = 0, int immediateCopies = 1)
        {
            if (immediateCopies < 1 || immediateCopies > 3
                || (immediateCopies != 1 && !NetReliableChannel.IsReliable(type)))
                throw new ArgumentOutOfRangeException(nameof(immediateCopies));
            Span<byte> buffer = stackalloc byte[NetConfig.MaxPacketSize];
            int length;
            lock (_connectionLock)
            {
                if (type == PacketType.Hello && payload.Length >= 1 && payload[0] == NetConfig.ProtocolVersion)
                {
                    uint clientId = payload.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]) : 0;
                    if (_pendingConnections.Count < 64 || _pendingConnections.ContainsKey(target)) _pendingConnections[target] = clientId;
                }
                if (type == PacketType.Welcome && payload.Length == 17)
                {
                    uint clientId = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                    if (!_connections.TryGetValue(target, out var existing) || existing.ClientId != clientId || existing.RetiredAt.HasValue)
                    {
                        if (_connections.Count >= 64) throw new InvalidOperationException("Connection capacity exceeded");
                        _connections[target] = new NetConnection(target, NetConnection.NewId(), clientId);
                    }
                }
                if (!Unsequenced(type) && _connections.TryGetValue(target, out var connection))
                {
                    if (payload.Length > NetConfig.MaxPayloadSize) throw new ArgumentOutOfRangeException(nameof(payload));
                    if (NetReliableChannel.IsReliable(type))
                    {
                        // Send has no backpressure return value. Losing an ordinary
                        // command/result must surface through the same disconnect
                        // path as critical exhaustion, rather than silently diverge.
                        if (!connection.Reliable.TryQueue(type, payload, NowMilliseconds, out uint eventId,
                            expedite: immediateCopies > 1))
                            connection.Reliable.Fail();
                        FlushReliable(connection, NowMilliseconds, eventId, immediateCopies);
                        return;
                    }
                    connection.Send(type, NowMilliseconds).Write(buffer);
                    payload.CopyTo(buffer[NetHeader.Size..]); length = NetHeader.Size + payload.Length;
                }
                else
                {
                    // Before admission only discovery/Hello/refusal and probe pings
                    // are allowed. Established gameplay never has an unbound path.
                    if (!Unsequenced(type) && type is not (PacketType.Refused or PacketType.Ping or PacketType.Pong)) return;
                    if (payload.Length > NetConfig.MaxPacketSize - 1) throw new ArgumentOutOfRangeException(nameof(payload));
                    buffer[0] = (byte)type; payload.CopyTo(buffer[1..]); length = payload.Length + 1;
                }
            }
            Dispatch(target, buffer[..length], extraHoldTicks);
        }

        private static double NowMilliseconds => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        private void Dispatch(IPEndPoint target, ReadOnlySpan<byte> buffer, long extraHoldTicks = 0)
        {
            // Send consumes the caller's span before return. Fault injection owns
            // an exact copy because its lifetime crosses this stack frame.
            if (_lagWorker != null)
            {
                byte[] copy = buffer.ToArray();
                lock (_heldLock) _heldOut.Enqueue(NowMilliseconds, (target, copy, copy.Length),
                    extraHoldTicks * 1000.0 / Stopwatch.Frequency, lossOverride: NetLag.LossPercent / 100);
                return;
            }
            SendNow(target, buffer);
        }

        private bool Unwrap(IPEndPoint sender, byte[] data, ref int length)
        {
            lock (_connectionLock)
            {
                if (data[0] != NetHeader.Marker)
                {
                    var type = (PacketType)data[0];
                    if (Unsequenced(type) || type == PacketType.Refused && !_connections.ContainsKey(sender)
                        || type is PacketType.Ping or PacketType.Pong && !_connections.ContainsKey(sender))
                    {
                        if (_discoveryBudget.Take(NowMilliseconds, 300, 128)) return true;
                        Telemetry.Drop(); return false;
                    }
                    Telemetry.Invalid(); return false;
                }
                if (!NetHeader.TryRead(data.AsSpan(0, length), out var header)) { Telemetry.Invalid(); return false; }
                _connections.TryGetValue(sender, out var connection);
                if (connection == null || connection.Id != header.ConnectionId)
                {
                    const int bootstrapOffset = NetHeader.Size + 4;
                    if (header.Type != PacketType.Welcome || (header.Flags & NetHeaderFlags.Reliable) == 0
                        || length != bootstrapOffset + 17 || _supersededIds.Contains(header.ConnectionId)
                        || !_pendingConnections.TryGetValue(sender, out uint clientId)
                        || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bootstrapOffset + 1)) != clientId
                        || connection != null && _supersededIds.Count >= 64)
                    { Telemetry.Invalid(); return false; }
                    // A server restart can answer an explicit Hello with a new
                    // incarnation at the same endpoint. Old incarnations never
                    // replace it, even while a subsequent Hello is outstanding.
                    if (connection != null) _supersededIds.Add(connection.Id);
                    connection = new NetConnection(sender, header.ConnectionId, clientId);
                    _connections[sender] = connection; _pendingConnections.Remove(sender);
                }
                else if (header.Type == PacketType.Welcome) _pendingConnections.Remove(sender);
                if (!connection.Accepts(sender, header)) { Telemetry.Invalid(); return false; }
                if ((header.Flags & NetHeaderFlags.AckOnly) == 0 && !connection.Allow(header.Type, NowMilliseconds))
                { Telemetry.Drop(); return false; }
                bool reliable = (header.Flags & NetHeaderFlags.Reliable) != 0;
                uint eventId = 0;
                if (!reliable && NetReliableChannel.IsReliable(header.Type)) { Telemetry.Invalid(); return false; }
                if (reliable)
                {
                    if (!NetReliableChannel.IsReliable(header.Type) || length < NetHeader.Size + 4
                        || (header.Flags & NetHeaderFlags.AckOnly) != 0) { Telemetry.Invalid(); return false; }
                    eventId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(NetHeader.Size));
                    // Do not ACK delivery unless a first application can enter
                    // the bounded inbox. A new attempt will retry with a new sequence.
                    if (!connection.Reliable.AlreadyReceived(eventId) && !_liveInbox.CanAcceptCritical)
                    { Telemetry.Drop(); return false; }
                }
                var result = connection.Receive(header, NowMilliseconds);
                if (reliable) connection.AckPending = true;
                if (connection.RetiredAt.HasValue) return false;
                if (result is SequenceResult.Duplicate or SequenceResult.TooOld) return false;
                if ((header.Flags & NetHeaderFlags.AckOnly) != 0) return false;
                if (reliable && !connection.Reliable.Receive(eventId)) return false;
                int offset = NetHeader.Size + (reliable ? 4 : 0);
                data.AsSpan(offset, length - offset).CopyTo(data.AsSpan(1));
                data[0] = (byte)header.Type; length -= offset - 1;
                return true;
            }
        }

        private void FlushReliable(NetConnection connection, double now, uint burstEventId = 0, int copies = 1)
        {
            Span<byte> bytes = stackalloc byte[NetConfig.MaxPacketSize];
            int budget = copies > 1 ? NetReliableChannel.Capacity : 4;
            for (int i = 0; i < budget && connection.Reliable.TrySend(now, out var eventPacket); i++)
            {
                // One application event, independent datagram sequences. ACKing
                // any copy completes delivery; the receiver applies it only once.
                int transmissions = eventPacket.EventId == burstEventId ? copies : 1;
                for (int copy = 0; copy < transmissions; copy++)
                {
                    connection.Send(eventPacket.Type, now, NetHeaderFlags.Reliable, eventPacket.EventId).Write(bytes);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[NetHeader.Size..], eventPacket.EventId);
                    eventPacket.Payload.Span.CopyTo(bytes[(NetHeader.Size + 4)..]);
                    Dispatch(connection.Endpoint, bytes[..(NetHeader.Size + 4 + eventPacket.Payload.Length)]);
                }
            }
        }

        private void ServiceConnections()
        {
            lock (_connectionLock)
            {
                double now = NowMilliseconds;
                Span<byte> ack = stackalloc byte[NetHeader.Size];
                int expiredCount = 0;
                foreach (var connection in _connections.Values)
                {
                    if (connection.RetiredAt.HasValue && (connection.Reliable.Capture(now).Pending == 0
                        || now - connection.RetiredAt.Value >= NetReliableChannel.LifetimeMilliseconds))
                    { _expiredConnections[expiredCount++] = connection.Endpoint; continue; }
                    FlushReliable(connection, now);
                    if (connection.Reliable.Failed && !connection.FailureReported && !connection.RetiredAt.HasValue)
                    {
                        connection.FailureReported = true;
                        Console.Error.WriteLine($"[net] reliable control failed for {connection.Endpoint}; disconnecting");
                        // A local failure is an authoritative disconnect decision,
                        // delivered on the normal simulation thread, never a socket callback.
                        _connectionFailures.Enqueue(new ReceivedPacket(connection.Endpoint, new byte[] { (byte)PacketType.Bye }, 1));
                    }
                    if (connection.AckPending)
                    {
                        connection.Send(0, now, NetHeaderFlags.AckOnly).Write(ack);
                        Dispatch(connection.Endpoint, ack);
                    }
                }
                for (int i = 0; i < expiredCount; i++)
                { _connections.Remove(_expiredConnections[i]!); _expiredConnections[i] = null; }
            }
        }

        private void SendNow(IPEndPoint target, ReadOnlySpan<byte> datagram)
        {
            if (_socket == null) return;
            try
            {
                SocketAddress address;
                lock (_connectionLock)
                    address = _connections.TryGetValue(target, out var connection) ? connection.SendAddress : target.Serialize();
                _socket.Client.SendTo(datagram, SocketFlags.None, address);
                Telemetry.Sent(datagram.Length);
                Interlocked.Increment(ref TotalPacketsSent);
            }
            catch (SocketException)
            {
                Telemetry.Error();
                // Same rationale as above: one unreachable peer must not
                // take down the session for everyone else.
            }
            catch (ObjectDisposedException)
            {
                // The session ended while the simulated line still held this.
            }
        }

        private int _disposed;
        public int UnacknowledgedCloseEvents { get; private set; }
        private int PendingCloseEvents()
        {
            lock (_connectionLock)
            {
                int count = 0;
                foreach (var connection in _connections.Values) if (connection.Reliable.HasPending(PacketType.Bye)) count++;
                return count;
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Keep the socket/ACK worker alive for bounded graceful close.
            // Disposing immediately after Send(Bye) would cancel its retries.
            double deadline = NowMilliseconds + 2000;
            while (_running && PendingCloseEvents() > 0 && NowMilliseconds < deadline)
            {
                ServiceConnections();
                if (_lagWorker != null) PromoteHeldArrivals();
                Thread.Sleep(5);
            }
            UnacknowledgedCloseEvents = PendingCloseEvents();
            if (UnacknowledgedCloseEvents > 0) NetLog.Event("graceful close deadline expired; remote timeout will finish removal");
            _running = false;
            _cancel.Cancel();
            _socket?.Dispose();
            if (_worker != null && !_worker.Join(TimeSpan.FromSeconds(1)))
            {
                // Background thread; the process can exit regardless.
            }
            while (_inbox.TryDequeue(out ReceivedPacket packet))
            {
                packet.Release();
            }
            for (int priority = 0; priority < 3; priority++)
                while (_liveInbox.TryDequeue((NetPacketPriority)priority, out var queued)) queued.Release();
            lock (_stateLock)
            {
                if (_latestSnapshot.HasValue) _latestSnapshot.Value.Release();
                _latestSnapshot = null;
                for (int i = 0; i < _latestSlotIntent.Length; i++)
                {
                    if (_latestSlotIntent[i].HasValue) _latestSlotIntent[i]!.Value.Release();
                    _latestSlotIntent[i] = null;
                }
            }
            _lagWorker?.Join(TimeSpan.FromSeconds(1));
            _cancel.Dispose();
        }
    }
}
