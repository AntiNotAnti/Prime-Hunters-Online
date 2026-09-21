using System;
using System.Buffers;
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

        public ReceivedPacket(IPEndPoint sender, byte[] data, int length, long arrivedAt = 0,
            bool pooled = false)
        {
            Sender = sender;
            Data = data;
            Length = length;
            ArrivedAt = arrivedAt == 0 ? Stopwatch.GetTimestamp() : arrivedAt;
            Pooled = pooled;
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
        private readonly UdpClient? _socket;
        private readonly Thread? _worker;
        private readonly ConcurrentQueue<ReceivedPacket> _inbox = new();
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
        private const int MaxQueuedPackets = 2048;

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

        private bool TryCoalesceRealtimeState(ReceivedPacket packet)
        {
            if (!_coalesceRealtimeState || _lagWorker != null)
            {
                return false;
            }
            lock (_stateLock)
            {
                if (packet.Type == PacketType.Snapshot)
                {
                    if (_latestSnapshot.HasValue)
                    {
                        _latestSnapshot.Value.Release();
                        Interlocked.Increment(ref _statePacketsCoalesced);
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
                            _latestSlotIntent[slot]!.Value.Release();
                            Interlocked.Increment(ref _statePacketsCoalesced);
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
                // A system that refuses the size keeps its default; the
                // session still works, it just tolerates less of a stall.
            }
            // Only so the worker notices _running going false; nothing waits
            // on this in normal operation.
            _socket.Client.ReceiveTimeout = 500;
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
            while (_running)
            {
                try
                {
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
                    byte[] data = ArrayPool<byte>.Shared.Rent(NetConfig.MaxPacketSize);
                    bool handedOff = false;
                    try
                    {
                        EndPoint remote = any;
                        int length;
                        try
                        {
                            length = _socket!.Client.ReceiveFrom(data, 0, NetConfig.MaxPacketSize,
                                SocketFlags.None, ref remote);
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            continue;
                        }
                        if (length == 0 || length > NetConfig.MaxPacketSize
                            || remote is not IPEndPoint sender)
                        {
                            continue;
                        }

                        if (_autoPong && (PacketType)data[0] == PacketType.Ping)
                        {
                            Send(sender, PacketType.Pong, data.AsSpan(1, length - 1),
                                _lagWorker != null
                                    ? (long)(NetLag.RoundTripMs / 2.0 * Stopwatch.Frequency / 1000)
                                    : 0);
                            continue;
                        }

                        var received = new ReceivedPacket(sender, data, length,
                            Stopwatch.GetTimestamp(), pooled: true);

                        // The worker rather than NetLag.Active, so the two
                        // halves cannot disagree: fault-injected traffic keeps
                        // every datagram and therefore deliberately bypasses
                        // latest-state coalescing.
                        if (_lagWorker != null)
                        {
                            // NetFaultQueue may deliberately drop or duplicate
                            // this value. A pooled buffer cannot safely have
                            // two owners (or no owner), so fault-injected
                            // traffic keeps an ordinary exact-size array.
                            byte[] heldCopy = data.AsSpan(0, length).ToArray();
                            lock (_heldLock)
                            {
                                _heldIn.Enqueue(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
                                    new ReceivedPacket(sender, heldCopy, heldCopy.Length,
                                        received.ArrivedAt));
                            }
                            continue;
                        }

                        if (TryCoalesceRealtimeState(received))
                        {
                            handedOff = true;
                            continue;
                        }

                        if (Volatile.Read(ref _inboxCount) >= MaxQueuedPackets)
                        {
                            if (_inbox.TryDequeue(out ReceivedPacket dropped))
                            {
                                Interlocked.Decrement(ref _inboxCount);
                                dropped.Release();
                            }
                            PacketsDropped++;
                            Interlocked.Increment(ref TotalPacketsDropped);
                        }
                        Interlocked.Increment(ref _inboxCount);
                        _inbox.Enqueue(received);
                        handedOff = true;
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
                    // Transient: an ICMP unreachable from a peer that left.
                    // Keep serving the peers that are still here.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        /// <summary>Drain everything received since the last call. Called once per frame.</summary>
        public IEnumerable<ReceivedPacket> Drain()
        {
            if (_lagWorker != null)
            {
                PromoteHeldArrivals();
            }
            while (_inbox.TryDequeue(out ReceivedPacket packet))
            {
                Interlocked.Decrement(ref _inboxCount);
                if (_socket == null) _playbackBytes -= packet.Length;
                try
                {
                    yield return packet;
                }
                finally
                {
                    packet.Release();
                }
            }

            if (_coalesceRealtimeState)
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
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            while (true)
            {
                ReceivedPacket packet;
                lock (_heldLock)
                {
                    if (!_heldIn.TryDequeue(now, out packet))
                    {
                        return;
                    }
                }
                Interlocked.Increment(ref _inboxCount);
                _inbox.Enqueue(new ReceivedPacket(packet.Sender, packet.Data, packet.Length,
                    pooled: packet.Pooled));
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
        public void EnqueueForPlayback(byte[] data, int length)
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
            _inbox.Enqueue(new ReceivedPacket(_playbackSender, data, length));
        }

        /// <param name="extraHoldTicks">
        /// More simulated line to hold this datagram behind, on top of the
        /// outbound half. Only the automatic Pong uses it; see the call site.
        /// </param>
        public void Send(IPEndPoint target, PacketType type, ReadOnlySpan<byte> payload,
            long extraHoldTicks = 0)
        {
            Span<byte> buffer = stackalloc byte[NetConfig.MaxPacketSize];
            buffer[0] = (byte)type;
            payload.CopyTo(buffer[1..]);
            if (_lagWorker != null)
            {
                byte[] copy = buffer[..(payload.Length + 1)].ToArray();
                lock (_heldLock)
                {
                    _heldOut.Enqueue(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
                        (target, copy, copy.Length), extraHoldTicks * 1000.0 / Stopwatch.Frequency);
                }
                return;
            }
            SendNow(target, buffer[..(payload.Length + 1)]);
        }

        private void SendNow(IPEndPoint target, ReadOnlySpan<byte> datagram)
        {
            if (_socket == null) return;
            try
            {
                _socket.Send(datagram, target);
                Interlocked.Increment(ref TotalPacketsSent);
            }
            catch (SocketException)
            {
                // Same rationale as above: one unreachable peer must not
                // take down the session for everyone else.
            }
            catch (ObjectDisposedException)
            {
                // The session ended while the simulated line still held this.
            }
        }

        public void Dispose()
        {
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
            _cancel.Dispose();
        }
    }
}
