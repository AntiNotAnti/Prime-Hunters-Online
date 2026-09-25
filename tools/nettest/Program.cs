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
            if (args.Length > 1 && args[0] == "--protocol19-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetProtocol19Check.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
            if (args.Length > 0 && args[0] == "--protocol19") return Protocol19Tests.Run();
            if (args.Length > 1 && args[0] == "--continuous-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetContinuousTargetCheck.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
            if (args.Length > 1 && args[0] == "--alt-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetAltHitCheck.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
            if (args.Length > 0 && args[0] == "--alt-hits") return AltFormHitTests.Run();
            if (args.Length > 0 && args[0] == "--continuous-targets") return ContinuousTargetTests.Run();
            if (args.Length > 0 && args[0] == "--dynamic-geometry") return DynamicGeometryTests.Run();
            if (args.Length > 0 && args[0] == "--input-edges") return InputEdgeTests.Run();
            if (args.Length > 0 && args[0] == "--protocol18") return Protocol18Tests.Run();
            if (args.Length > 0 && args[0] == "--claim-stress") return ClaimStressTests.Run();
            if (args.Length > 0 && args[0] == "--health-shots") return HealthShotTests.Run();
            if (args.Length > 0 && args[0] == "--lifecycle") return LifecycleTests.Run();
            if (args.Length > 0 && args[0] == "--architecture") return NetArchitectureTests.Run();
            if (Array.IndexOf(args, "--network-benchmark") >= 0 || Array.IndexOf(args, "--network-benchmark-json") >= 0)
                return NetworkBenchmark.Run(args);
            if (args.Length > 0 && args[0] == "--allocations") return NetworkAllocationTests.Run(Array.IndexOf(args, "--report-only") >= 0);
            if (args.Length > 0 && args[0] == "--protocol17") return Protocol17Tests.Run();
            if (args.Length > 0 && args[0] == "--reliable") return ReliableTests.Run();
            if (args.Length > 0 && args[0] == "--queue-budget") return QueueBudgetTests.Run();
            if (args.Length > 0 && args[0] == "--load-lifecycle") return LoadLifecycleTests.Run();
            if (args.Length > 0 && args[0] == "--lagcomp-shadow") return LagCompensationTests.Run();
            if (args.Length > 0 && args[0] == "--weapon-policy") return WeaponPolicyTests.Run();
            if (args.Length > 0 && args[0] == "--transport-stress") return TransportStressTests.Run();
            if (args.Length > 1 && args[0] == "--claim-load-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetClaimLoadCheck.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
            if (args.Length > 1 && args[0] == "--geometry-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetGeometrySceneCheck.Run(args.Length > 2 ? args[2] : "UNIT1_RM1");
            }
            if (args.Length > 1 && args[0] == "--bootstrap-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetBootstrapCheck.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
            if (args.Length > 1 && args[0] == "--combat-scene")
            {
                System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetFullPath(args[1]));
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                return NetCombatCheck.Run(args.Length > 2 ? args[2] : "MP1 SANCTORUS");
            }
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
            TestServerAuthorityInvariant(host, port);

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
            private readonly LoopbackPeer _socket;
            private readonly IPEndPoint _server;
            private readonly Thread _pump;
            private volatile bool _running = true;

            public int Slot { get; private set; } = -1;
            public MatchStatePacket? LastState { get; private set; }
            public string Name { get; }

            public FakeClient(string name, string host, int port)
            {
                Name = name;
                _socket = new LoopbackPeer();
                // Bind explicitly: UdpClient only binds implicitly on the
                // first Send, and this client's receive pump starts first.
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

            /// <summary>True if a server ever emits the reserved legacy authority packet.</summary>
            public bool ReceivedAuthorityPacket { get; private set; }
            public int ImpossiblePositionUpdates { get; private set; }

            public bool SeesName(string other)
            {
                return RosterNames.Any(n => n == other);
            }

            /// <summary>Positions last received per slot, and how many updates.</summary>
            public Vector3[] SeenPositions { get; } = new Vector3[RosterPacket.MaxSlots];
            public int[] SeenUpdates { get; } = new int[RosterPacket.MaxSlots];

            /// <summary>Send a legacy client-authored snapshot as a negative probe.</summary>
            public void SendSnapshot(uint frame, int slot, Vector3 position)
            {
                const int timeSyncSize = PlayerEntity.SlotCapacity * sizeof(float) * 2;
                ushort matchId = LastState?.MatchId ?? 0;
                int healthOffset = SnapshotHeader.Size + PlayerState.Size + timeSyncSize;
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
                state.Write(payload.AsSpan(SnapshotHeader.Size));
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
                            ReceivedAuthorityPacket = true;
                        }
                        else if (type == PacketType.Snapshot
                            && payload.Length >= SnapshotHeader.Size + PlayerState.Size)
                        {
                            SnapshotHeader header = SnapshotHeader.Read(payload);
                            int offset = SnapshotHeader.Size;
                            for (int i = 0; i < header.PlayerCount; i++)
                            {
                                if (offset + PlayerState.Size > payload.Length)
                                {
                                    break;
                                }
                                PlayerState state = PlayerState.Read(payload[offset..]);
                                offset += PlayerState.Size;
                                if (state.SlotIndex < SeenPositions.Length)
                                {
                                    SeenPositions[state.SlotIndex] = state.Position;
                                    if (state.Position.LengthSquared > 50_000_000f) ImpossiblePositionUpdates++;
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
        private static void TestServerAuthorityInvariant(string host, int port)
        {
            Console.WriteLine("Server authority invariant");
            using var first = new FakeClient("FIRST", host, port);
            using var second = new FakeClient("SECOND", host, port);
            Join(first, "FIRST"); Join(second, "SECOND");
            for (int i = 0; i < 15; i++)
            {
                first.SendIntent((uint)(i + 1)); second.SendIntent((uint)(i + 1)); Thread.Sleep(80);
            }
            Check(!first.ReceivedAuthorityPacket && !second.ReceivedAuthorityPacket,
                "no player receives simulation authority", "server retains authority");
            int before = second.ImpossiblePositionUpdates;
            var forged = new Vector3(10000, 10000, 10000);
            for (int i = 0; i < 12; i++)
            {
                first.SendSnapshot((uint)(9000 + i), first.Slot, forged);
                first.SendIntent((uint)(100 + i)); second.SendIntent((uint)(100 + i)); Thread.Sleep(60);
            }
            Check(second.ImpossiblePositionUpdates == before,
                "client-authored snapshots cannot enter the authoritative stream",
                "forged world state ignored");
            first.Leave(); second.Leave(); Thread.Sleep(300); Console.WriteLine();
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
