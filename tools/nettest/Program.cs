using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest
{
    /// <summary>
    /// Headless conformance tests for the dedicated server.
    ///
    /// Simulated clients, no game window: the point is to prove the parts a
    /// human cannot easily verify by playing -- that concurrent joiners get
    /// distinct slots, that they all read the same match clock, and that the
    /// server's own accounting agrees with reality.
    ///
    /// Usage: nettest [host] [port]
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--health-shots") return HealthShotTests.Run();
            if (args.Length > 0 && args[0] == "--lifecycle") return LifecycleTests.Run();
            string host = args.Length > 0 ? args[0] : "127.0.0.1";
            int port = args.Length > 1 && Int32.TryParse(args[1], out int p)
                ? p : NetConfig.DefaultPort;

            Console.WriteLine($"=== MphRead server tests against {host}:{port} ===\n");

            TestHostnameResolution(host, port);
            TestReachable(host, port);
            TestDistinctSlots(host, port);
            TestClockAgreement(host, port);
            TestJoinInProgress(host, port);
            TestSlotReuseAfterLeave(host, port);
            TestStatusReporting(host, port);
            TestPeerSurvivesTimeoutWindow(host, port);
            TestPlayersSeeEachOther(host, port);
            TestRemoteMovementVisible(host, port);
            TestAuthorityIsDesignated(host, port);
            TestAuthorityRelayReachesViewer(host, port);

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "ALL TESTS PASSED"
                : $"{_failures} TEST(S) FAILED");
            return _failures;
        }

        private static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"
                + (detail.Length > 0 ? $"  --  {detail}" : ""));
            if (!ok)
            {
                _failures++;
            }
        }

        /// <summary>A simulated client that holds its slot until disposed.</summary>
        private sealed class FakeClient : IDisposable
        {
            private readonly UdpClient _socket;
            private readonly IPEndPoint _server;
            private readonly Thread _pump;
            private volatile bool _running = true;

            public int Slot { get; private set; } = -1;
            public MatchStatePacket? LastState { get; private set; }
            public string Name { get; }

            public FakeClient(string name, string host, int port)
            {
                Name = name;
                _socket = new UdpClient(AddressFamily.InterNetwork);
                // Bind explicitly: UdpClient only binds implicitly on the
                // first Send, and this client's receive pump starts first.
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                _socket.Client.ReceiveTimeout = 500;
                IPAddress ip = Dns.GetHostAddresses(host)
                    .First(a => a.AddressFamily == AddressFamily.InterNetwork);
                _server = new IPEndPoint(ip, port);
                _pump = new Thread(Pump) { IsBackground = true };
                _pump.Start();
            }

            public string[] RosterNames { get; } = new string[RosterPacket.MaxSlots];
            public bool[] RosterSlots { get; } = new bool[RosterPacket.MaxSlots];

            private readonly ushort[] _generations = new ushort[RosterPacket.MaxSlots];
            private readonly ushort[] _lives = new ushort[RosterPacket.MaxSlots];

            public void SendHello()
            {
                Send(PacketType.Hello, new byte[] { NetConfig.ProtocolVersion });
            }

            /// <summary>Announce a display name, the way a real client does.</summary>
            public void SendIdentify(string displayName)
            {
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(displayName);
                Send(PacketType.Identify, bytes.AsSpan(0,
                    Math.Min(bytes.Length, RosterPacket.MaxNameBytes)));
            }

            /// <summary>Set when the server designates us as authority.</summary>
            public bool IsAuthority { get; private set; }

            public bool SeesName(string other)
            {
                return RosterNames.Any(n => n == other);
            }

            /// <summary>Positions last received per slot, and how many updates.</summary>
            public Vector3[] SeenPositions { get; } = new Vector3[RosterPacket.MaxSlots];
            public int[] SeenUpdates { get; } = new int[RosterPacket.MaxSlots];

            /// <summary>
            /// Publish authoritative state, the way the authority client's
            /// NetHooks.AfterSimulation does. Only the authority's snapshots
            /// are relayed, so this is how movement reaches other players.
            /// </summary>
            public void SendSnapshot(uint frame, int slot, Vector3 position)
            {
                const int timeSyncSize = PlayerEntity.SlotCapacity * sizeof(float) * 2;
                ushort matchId = LastState?.MatchId ?? 0;
                int damageCountOffset = SnapshotHeader.Size + SnapshotWire.StateHeaderSize
                    + SnapshotWire.PlayerSize;
                int timeOffset = damageCountOffset + 1;
                int healthOffset = timeOffset + timeSyncSize;
                byte[] payload = new byte[healthOffset + NetHealthSync.HeaderSize];
                var header = new SnapshotHeader
                {
                    Frame = frame,
                    Rng1 = 0,
                    Rng2 = 0,
                    PlayerCount = 1,
                    MatchId = matchId,
                    AuthorityEpoch = LastState?.AuthorityEpoch ?? 0
                };
                header.Write(payload);
                var state = new PlayerState
                {
                    SlotIndex = (byte)slot,
                    Flags = PlayerState.FlagActive | PlayerState.FlagSpawned,
                    SlotGeneration = _generations[slot],
                    LifeId = _lives[slot] == 0 ? (ushort)1 : _lives[slot],
                    Position = position,
                    Speed = Vector3.Zero,
                    Facing = new Vector3(0, 0, 1),
                    Health = 100,
                    CurrentWeapon = 0,
                    Team = 0
                };
                SnapshotWire.WriteStateHeader(payload.AsSpan(SnapshotHeader.Size,
                    SnapshotWire.StateHeaderSize), true, (byte)(1 << slot), frame);
                state.WriteBase(payload.AsSpan(SnapshotHeader.Size + SnapshotWire.StateHeaderSize,
                    SnapshotWire.PlayerSize));
                payload[damageCountOffset] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(healthOffset), matchId);
                Send(PacketType.Snapshot, payload);
            }

            /// <summary>
            /// Keep the peer alive. The server drops silent peers, so a test
            /// client must behave like a real one and keep talking.
            /// </summary>
            public void SendIntent(uint frame)
            {
                var intent = new IntentPacket { Frame = frame, Buttons = IntentButtons.None,
                    MatchId = LastState?.MatchId ?? 0, AuthorityEpoch = LastState?.AuthorityEpoch ?? 0,
                    SlotGeneration = Slot < 0 ? (ushort)0 : _generations[Slot],
                    LifeId = Slot < 0 ? (ushort)0 : _lives[Slot] };
                byte[] payload = new byte[IntentPacket.Size];
                intent.Write(payload);
                Send(PacketType.Intent, payload);
            }

            private void Send(PacketType type, ReadOnlySpan<byte> payload)
            {
                Span<byte> buffer = stackalloc byte[payload.Length + 1];
                buffer[0] = (byte)type;
                payload.CopyTo(buffer[1..]);
                try
                {
                    _socket.Send(buffer, _server);
                }
                catch (SocketException)
                {
                    // Transient; the pump keeps running.
                }
            }

            private void Pump()
            {
                var any = new IPEndPoint(IPAddress.Any, 0);
                while (_running)
                {
                    try
                    {
                        IPEndPoint from = any;
                        byte[] data = _socket.Receive(ref from);
                        if (data.Length < 1)
                        {
                            continue;
                        }
                        var type = (PacketType)data[0];
                        ReadOnlySpan<byte> payload = data.AsSpan(1);
                        if (type == PacketType.Welcome && payload.Length >= 1)
                        {
                            Slot = payload[0];
                        }
                        else if ((type == PacketType.MatchState || type == PacketType.MapChange)
                            && payload.Length >= MatchStatePacket.Size)
                        {
                            LastState = MatchStatePacket.Read(payload);
                        }
                        else if (type == PacketType.Authority)
                        {
                            IsAuthority = true;
                        }
                        else if (type == PacketType.Snapshot
                            && payload.Length >= SnapshotHeader.Size + SnapshotWire.StateHeaderSize
                                + SnapshotWire.PlayerSize + 1)
                        {
                            SnapshotHeader header = SnapshotHeader.Read(payload);
                            int offset = SnapshotHeader.Size + SnapshotWire.StateHeaderSize;
                            for (int i = 0; i < header.PlayerCount; i++)
                            {
                                if (offset + SnapshotWire.PlayerSize > payload.Length)
                                {
                                    break;
                                }
                                PlayerState state = PlayerState.ReadBase(payload[offset..]);
                                offset += SnapshotWire.PlayerSize;
                                if (state.SlotIndex < SeenPositions.Length)
                                {
                                    SeenPositions[state.SlotIndex] = state.Position;
                                    _lives[state.SlotIndex] = state.LifeId;
                                    SeenUpdates[state.SlotIndex]++;
                                }
                            }
                        }
                        else if (type == PacketType.Roster
                            && payload.Length >= RosterPacket.Size)
                        {
                            RosterPacket roster = RosterPacket.Read(payload);
                            Array.Clear(RosterNames);
                            Array.Clear(RosterSlots);
                            for (int i = 0; i < roster.Count; i++)
                            {
                                int s = roster.Slots[i];
                                if (s >= 0 && s < RosterNames.Length)
                                {
                                    RosterNames[s] = roster.Names[i];
                                    _generations[s] = roster.Generations[i];
                                    RosterSlots[s] = true;
                                }
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        // Receive timeout: expected while idle.
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }

            public void Leave()
            {
                Send(PacketType.Bye, ReadOnlySpan<byte>.Empty);
            }

            public void Dispose()
            {
                _running = false;
                _socket.Dispose();
            }
        }

        /// <summary>
        /// Join and wait for a slot, re-sending the Hello while waiting.
        /// UDP drops packets, and a single-shot Hello made several tests
        /// fail intermittently for a reason that said nothing about the
        /// server. A real client keeps asking until it is admitted.
        /// </summary>
        private static bool Join(FakeClient client, string? identifyAs = null,
                                 int timeoutMs = 8000)
        {
            bool joined = WaitFor(() =>
            {
                if (client.Slot < 0)
                {
                    client.SendHello();
                }
                return client.Slot >= 0;
            }, timeoutMs);
            if (joined && identifyAs != null)
            {
                client.SendIdentify(identifyAs);
            }
            return joined;
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(25);
            }
            return false;
        }

        /// <summary>
        /// Regression guard: the client used IPAddress.Parse, which only
        /// accepts a literal, so a hostname threw and the join failed
        /// silently -- both players ended up offline in their own match.
        /// Anything that resolves a server address must handle a name.
        /// </summary>
        private static void TestHostnameResolution(string host, int port)
        {
            Console.WriteLine("Address handling");
            bool isLiteral = IPAddress.TryParse(host, out _);
            if (isLiteral)
            {
                Console.WriteLine($"  [SKIP] {host} is a literal address; "
                    + "pass a hostname to exercise resolution");
                Console.WriteLine();
                return;
            }
            bool resolved;
            try
            {
                resolved = Dns.GetHostAddresses(host)
                    .Any(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception)
            {
                resolved = false;
            }
            Check(resolved, $"hostname {host} resolves to IPv4");

            (bool ok, string message) = NetProbe.Probe(host, port);
            Check(ok, "a hostname (not just an IP) can reach the server", message);
            Thread.Sleep(300);
            Console.WriteLine();
        }

        private static void TestReachable(string host, int port)
        {
            Console.WriteLine("Reachability");
            (bool ok, string message) = NetProbe.Probe(host, port);
            Check(ok, "server answers a Hello", message);
            // The probe leaves; give the server a moment to release its slot
            // so it cannot pollute the slot-allocation test that follows.
            Thread.Sleep(300);
            Console.WriteLine();
        }

        private static void TestDistinctSlots(string host, int port)
        {
            Console.WriteLine("Slot allocation");
            using var a = new FakeClient("A", host, port);
            using var b = new FakeClient("B", host, port);

            Join(a);
            Join(b);

            Check(a.Slot >= 0, "client A received a slot", $"slot {a.Slot}");
            Check(b.Slot >= 0, "client B received a slot", $"slot {b.Slot}");
            Check(a.Slot != b.Slot, "the two clients hold different slots",
                $"A={a.Slot} B={b.Slot}");

            a.Leave();
            b.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        private static void TestClockAgreement(string host, int port)
        {
            Console.WriteLine("Match clock");
            using var a = new FakeClient("A", host, port);
            using var b = new FakeClient("B", host, port);
            // Retry rather than send once: UDP drops, and a lost Hello left
            // the client waiting forever with nothing to resend it. A real
            // client re-sends until it is admitted; the test must too.
            WaitFor(() =>
            {
                if (a.LastState == null)
                {
                    a.SendHello();
                }
                if (b.LastState == null)
                {
                    b.SendHello();
                }
                return a.LastState != null && b.LastState != null;
            }, 10000);

            bool both = a.LastState != null && b.LastState != null;
            Check(both, "both clients received match state");
            if (both)
            {
                MatchStatePacket sa = a.LastState!.Value;
                MatchStatePacket sb = b.LastState!.Value;
                Check(sa.RoomKey == sb.RoomKey, "both clients see the same map",
                    $"A={sa.RoomKey} B={sb.RoomKey}");
                // Both read the same server clock, so any gap is packet
                // timing, not two independent timers drifting apart.
                float gap = Math.Abs(sa.TimeRemaining - sb.TimeRemaining);
                Check(gap < 2.0f, "clocks agree within 2 s",
                    $"A={sa.TimeRemaining:0.0}s B={sb.TimeRemaining:0.0}s gap={gap:0.00}s");
            }
            a.Leave();
            b.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        private static void TestJoinInProgress(string host, int port)
        {
            Console.WriteLine("Join in progress");
            using var first = new FakeClient("first", host, port);
            Join(first);
            WaitFor(() => first.LastState != null, 8000);
            float atJoin = first.LastState?.TimeRemaining ?? 0;

            // Let the match run, then have a second client arrive: it must be
            // told the running map and a clock that has advanced, not a fresh
            // match of its own.
            Thread.Sleep(3000);
            first.SendIntent(1);

            using var late = new FakeClient("late", host, port);
            Join(late);
            bool got = WaitFor(() => late.LastState != null, 8000);
            Check(got, "late joiner received match state");
            if (got && first.LastState != null)
            {
                MatchStatePacket state = late.LastState!.Value;
                Check(state.RoomKey.Length > 0, "late joiner learned the running map",
                    state.RoomKey);
                Check(state.TimeElapsed > 1.0f,
                    "late joiner sees a match already under way",
                    $"elapsed={state.TimeElapsed:0.0}s");
                Check(state.TimeRemaining < atJoin,
                    "the clock advanced rather than restarting",
                    $"{atJoin:0.0}s -> {state.TimeRemaining:0.0}s");
            }
            first.Leave();
            late.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        private static void TestSlotReuseAfterLeave(string host, int port)
        {
            Console.WriteLine("Slot reuse");
            using var a = new FakeClient("A", host, port);
            Join(a);
            int firstSlot = a.Slot;
            a.Leave();
            Thread.Sleep(500);

            using var b = new FakeClient("B", host, port);
            Join(b);
            Check(b.Slot == firstSlot,
                "a freed slot is handed to the next client",
                $"freed {firstSlot}, reassigned {b.Slot}");
            b.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        /// <summary>
        /// Regression guard for the disconnect seen in the field: a client
        /// whose local player was not active sent no intents, so the server
        /// dropped it on the TimeoutSeconds deadline while it was perfectly
        /// healthy. A peer that keeps talking must still be connected well
        /// past that window.
        /// </summary>
        private static void TestPeerSurvivesTimeoutWindow(string host, int port)
        {
            Console.WriteLine("Keepalive");
            double window = NetConfig.TimeoutSeconds;
            using var a = new FakeClient("A", host, port);
            using var b = new FakeClient("B", host, port);
            Join(a);
            Join(b);

            // Talk for longer than the server's timeout, then ask the server
            // how many peers it thinks are present.
            var clock = Stopwatch.StartNew();
            uint frame = 0;
            while (clock.Elapsed.TotalSeconds < window + 3)
            {
                frame++;
                a.SendIntent(frame);
                b.SendIntent(frame);
                Thread.Sleep(100);
            }

            int reported = b.LastState?.PlayerCount ?? -1;
            Check(reported >= 2,
                $"both peers still connected after {window + 3:0} s of play",
                $"server reports {reported} peer(s)");

            a.Leave();
            b.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        /// <summary>
        /// The test that actually answers "are these two in the same match":
        /// each client announces a distinct name, and each must then see the
        /// other's name in the roster the server sends. Positions can look
        /// plausible while two clients sit alone in their own scenes, but a
        /// name can only reach your scoreboard if it came from the other
        /// machine.
        /// </summary>
        private static void TestPlayersSeeEachOther(string host, int port)
        {
            Console.WriteLine("Players see each other");
            const string nameA = "ALICE";
            const string nameB = "BOB";
            using var a = new FakeClient(nameA, host, port);
            using var b = new FakeClient(nameB, host, port);

            Join(a, nameA);

            Join(b, nameB);

            // Keep both alive while the roster propagates.
            bool mutual = WaitFor(() =>
            {
                a.SendIntent((uint)Environment.TickCount);
                b.SendIntent((uint)Environment.TickCount);
                return a.SeesName(nameB) && b.SeesName(nameA);
            }, 8000);

            Console.WriteLine($"    {nameA} (slot {a.Slot}) sees: "
                + Describe(a.RosterNames));
            Console.WriteLine($"    {nameB} (slot {b.Slot}) sees: "
                + Describe(b.RosterNames));

            Check(a.Slot != b.Slot, "the two players hold different slots",
                $"{nameA}={a.Slot} {nameB}={b.Slot}");
            Check(a.SeesName(nameB), $"{nameA} sees {nameB} on the scoreboard");
            Check(b.SeesName(nameA), $"{nameB} sees {nameA} on the scoreboard");
            Check(mutual, "both players see each other in the same match");

            a.Leave();
            b.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        /// <summary>
        /// The check that "connected" actually means "playing together":
        /// the authority walks its player along a path, and the other client
        /// must receive those coordinates and see them change. A static
        /// position would pass a naive presence test while the players are
        /// in fact frozen to each other.
        /// </summary>
        private static void TestRemoteMovementVisible(string host, int port)
        {
            Console.WriteLine("Remote movement is visible");
            using var mover = new FakeClient("MOVER", host, port);
            using var watcher = new FakeClient("WATCHER", host, port);

            Join(mover, "MOVER");
            Join(watcher, "WATCHER");

            // The first peer to join is the server's authority, and only its
            // snapshots are relayed onward.
            bool moverIsAuthority = mover.Slot < watcher.Slot;
            FakeClient authority = moverIsAuthority ? mover : watcher;
            FakeClient viewer = moverIsAuthority ? watcher : mover;
            int movingSlot = authority.Slot;

            var samples = new List<Vector3>();
            for (int step = 0; step < 40; step++)
            {
                var position = new Vector3(step * 0.5f, 1.0f, step * 0.25f);
                authority.SendSnapshot((uint)(step + 1), movingSlot, position);
                // Both keep talking so neither is dropped mid-test.
                mover.SendIntent((uint)(step + 1));
                watcher.SendIntent((uint)(step + 1));
                Thread.Sleep(60);
                if (viewer.SeenUpdates[movingSlot] > 0)
                {
                    Vector3 seen = viewer.SeenPositions[movingSlot];
                    if (samples.Count == 0 || samples[^1] != seen)
                    {
                        samples.Add(seen);
                    }
                }
            }

            int updates = viewer.SeenUpdates[movingSlot];
            Console.WriteLine($"    authority = slot {movingSlot}, "
                + $"viewer = slot {viewer.Slot}");
            Console.WriteLine($"    viewer received {updates} position update(s), "
                + $"{samples.Count} distinct");
            if (samples.Count > 0)
            {
                Console.WriteLine($"    first={Format(samples[0])} last={Format(samples[^1])}");
            }

            Check(updates > 0, "viewer receives the other player's position",
                $"{updates} update(s)");
            Check(samples.Count >= 5,
                "the received position actually changes over time",
                $"{samples.Count} distinct position(s)");
            if (samples.Count >= 2)
            {
                float travelled = (samples[^1] - samples[0]).Length;
                Check(travelled > 1.0f,
                    "the other player is seen to move a real distance",
                    $"{travelled:0.00} units");
            }
            else
            {
                Check(false, "the other player is seen to move a real distance",
                    "not enough samples");
            }

            mover.Leave();
            watcher.Leave();
            Thread.Sleep(300);
            Console.WriteLine();
        }

        /// <summary>
        /// Regression guard: on a dedicated server every peer is
        /// NetRole.Client, so the client code gated snapshot broadcasting on
        /// IsHost and nothing was ever published -- players were connected
        /// but frozen to each other. The server must tell exactly one peer
        /// that it owns the simulation.
        /// </summary>
        private static void TestAuthorityIsDesignated(string host, int port)
        {
            Console.WriteLine("Authority designation");
            using var first = new FakeClient("FIRST", host, port);
            Join(first);
            bool told = WaitFor(() => first.IsAuthority, 5000);
            Check(told, "the first peer is told it is the authority",
                first.IsAuthority ? $"slot {first.Slot}" : "never notified");

            using var second = new FakeClient("SECOND", host, port);
            Join(second);
            // Keep both alive briefly so any stray notification would land.
            for (int i = 0; i < 15; i++)
            {
                first.SendIntent((uint)(i + 1));
                second.SendIntent((uint)(i + 1));
                Thread.Sleep(100);
            }
            Check(!second.IsAuthority,
                "a later peer is not also made authority",
                second.IsAuthority ? "wrongly notified" : "correctly not notified");

            first.Leave();
            second.Leave();
            Thread.Sleep(500);
            Console.WriteLine();
        }

        /// <summary>
        /// The full path a real session uses: the designated authority
        /// publishes a snapshot to the *server*, and the server relays it to
        /// the other peer. Distinct from TestRemoteMovementVisible, which
        /// checks the same hop but does not assert that the sender was the
        /// peer the server actually designated -- the mismatch that let a
        /// silent-authority bug ship.
        /// </summary>
        private static void TestAuthorityRelayReachesViewer(string host, int port)
        {
            Console.WriteLine("Authority relay reaches the other player");
            using var one = new FakeClient("ONE", host, port);
            Join(one, "ONE");
            WaitFor(() => one.IsAuthority, 5000);

            using var two = new FakeClient("TWO", host, port);
            Join(two, "TWO");

            // Only the designated authority's snapshots are relayed; send
            // from it and confirm the other peer receives them.
            FakeClient authority = one.IsAuthority ? one : two;
            FakeClient viewer = one.IsAuthority ? two : one;
            Check(authority.IsAuthority, "one peer is the designated authority",
                $"slot {authority.Slot}");

            for (int step = 0; step < 30; step++)
            {
                authority.SendSnapshot((uint)(step + 1), authority.Slot,
                    new Vector3(step, 0, 0));
                one.SendIntent((uint)(step + 1));
                two.SendIntent((uint)(step + 1));
                Thread.Sleep(60);
            }

            int received = viewer.SeenUpdates[authority.Slot];
            Check(received > 0,
                "the other player receives the authority's state through the server",
                $"{received} update(s) for slot {authority.Slot}");

            // And a non-authority's snapshots must not be relayed, or any
            // client could overwrite everyone's view of the world.
            int before = authority.SeenUpdates[viewer.Slot];
            for (int step = 0; step < 15; step++)
            {
                viewer.SendSnapshot((uint)(step + 1), viewer.Slot,
                    new Vector3(0, step, 0));
                Thread.Sleep(60);
            }
            int leaked = authority.SeenUpdates[viewer.Slot] - before;
            Check(leaked == 0,
                "a non-authority peer's state is not relayed",
                leaked == 0 ? "correctly ignored" : $"{leaked} leaked");

            one.Leave();
            two.Leave();
            Thread.Sleep(400);
            Console.WriteLine();
        }

        private static string Format(Vector3 v)
        {
            return $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";
        }

        private static string Describe(string[] names)
        {
            var parts = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                {
                    parts.Add($"slot {i}={names[i]}");
                }
            }
            return parts.Count == 0 ? "(nobody)" : string.Join(", ", parts);
        }

        private static void TestStatusReporting(string host, int port)
        {
            Console.WriteLine("Player accounting");
            var clients = new List<FakeClient>();
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var client = new FakeClient($"C{i}", host, port);
                    clients.Add(client);
                    Join(client);
                    Thread.Sleep(150);
                }
                // Keep them alive long enough for a periodic state broadcast
                // to reflect all three.
                for (int tick = 0; tick < 20; tick++)
                {
                    foreach (FakeClient client in clients)
                    {
                        client.SendIntent((uint)(tick + 1));
                    }
                    Thread.Sleep(100);
                }

                var slots = clients.Select(c => c.Slot).ToList();
                Check(slots.All(s => s >= 0), "all three clients were admitted",
                    string.Join(", ", slots));
                Check(slots.Distinct().Count() == slots.Count,
                    "all three slots are distinct", string.Join(", ", slots));

                FakeClient last = clients[^1];
                int reported = last.LastState?.PlayerCount ?? -1;
                // At least, not exactly: a live server may have real players
                // on it while these tests run, and demanding an exact count
                // would fail for a reason that says nothing about the server.
                Check(reported >= clients.Count,
                    "server reports at least the clients this test connected",
                    $"reported {reported}, this test added {clients.Count}");
            }
            finally
            {
                foreach (FakeClient client in clients)
                {
                    client.Leave();
                    client.Dispose();
                }
            }
            Thread.Sleep(300);
            Console.WriteLine();
        }
    }
}
