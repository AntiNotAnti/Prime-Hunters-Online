using System;
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

        public ReceivedPacket(IPEndPoint sender, byte[] data, int length, long arrivedAt = 0)
        {
            Sender = sender;
            Data = data;
            Length = length;
            ArrivedAt = arrivedAt == 0 ? Stopwatch.GetTimestamp() : arrivedAt;
        }

        public PacketType Type => Length > 0 ? (PacketType)Data[0] : default;
        public ReadOnlySpan<byte> Payload => Data.AsSpan(1, Length - 1);
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
                    IPEndPoint sender = any;
                    byte[] data;
                    try
                    {
                        data = _socket!.Receive(ref sender);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
                    }
                    if (data.Length == 0 || data.Length > NetConfig.MaxPacketSize)
                    {
                        continue;
                    }
                    // Answered here rather than from the game loop, when the
                    // session asked for it.
                    //
                    // A Pong needs no game state: it echoes the id back so the
                    // server can match the reply to the ping it sent. Waiting
                    // for the next frame to do that added anything up to a
                    // whole frame -- half of one on average, more when the
                    // frame ran long -- to a measurement whose entire purpose
                    // is to describe the network. Every player's ping read
                    // about a frame worse than their connection.
                    if (_autoPong && data.Length >= 1 && (PacketType)data[0] == PacketType.Ping)
                    {
                        // The reply carries *both* halves of a simulated line.
                        // This ping was answered here, on the transport
                        // thread, before the hold below ever looked at it --
                        // so without the extra half the round trip the server
                        // measures comes out at half what was asked for, and
                        // the one number a latency run is read by would be
                        // describing the instrument.
                        Send(sender, PacketType.Pong, data.AsSpan(1),
                            _lagWorker != null ? (long)(NetLag.RoundTripMs / 2.0 * Stopwatch.Frequency / 1000) : 0);
                        continue;
                    }
                    if (Volatile.Read(ref _inboxCount) >= MaxQueuedPackets)
                    {
                        // Drop rather than grow -- but drop the *oldest*, not
                        // this one. In a real-time protocol the newest packet
                        // is the one worth having: it carries where the player
                        // is aiming now. Discarding arrivals while a queue of
                        // stale ones drains is how a backlogged client ends up
                        // seeing a third of an opponent's turn.
                        if (_inbox.TryDequeue(out _))
                        {
                            Interlocked.Decrement(ref _inboxCount);
                        }
                        PacketsDropped++;
                        Interlocked.Increment(ref TotalPacketsDropped);
                    }
                    // The worker rather than NetLag.Active, so the two
                    // halves cannot disagree: nothing may be held back unless
                    // there is something running that lets it out again.
                    if (_lagWorker != null)
                    {
                        lock (_heldLock)
                        {
                            _heldIn.Enqueue(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
                                new ReceivedPacket(sender, data, data.Length));
                        }
                        continue;
                    }
                    Interlocked.Increment(ref _inboxCount);
                    _inbox.Enqueue(new ReceivedPacket(sender, data, data.Length));
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
                yield return packet;
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
                _inbox.Enqueue(new ReceivedPacket(packet.Sender, packet.Data, packet.Length));
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
            _cancel.Dispose();
        }
    }
}
