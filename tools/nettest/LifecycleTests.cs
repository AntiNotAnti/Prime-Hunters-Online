using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest
{
    // Runs the production packet parser, session, lifecycle, prediction, damage
    // and history code without proprietary assets or a graphics/audio device.
    internal static class LifecycleTests
    {
        private static int _checks;
        private static Scene _scene = null!;
        public static int Run()
        {
            try
            {
                _scene = new Scene(new Vector2i(256, 192),
                    Mods.Input.SyntheticInput.CreateKeyboard(), Mods.Input.SyntheticInput.CreateMouse(),
                    _ => { }, () => { }, initializeRuntime: false);
                Wire();
                PresentationStateSafety();
                RelaySnapshotValidation();
                StateMachine();
                LoopbackAdmission();
                TransportCoalescing();
                PacketOrdering();
                Prediction();
                DamageHistory();
                PresentationClock();
                HistoryBoundaries();
                ClaimBoundaries();
                FaultStream();
                Console.WriteLine($"PASS: {_checks} lifecycle assertions (production code, deterministic seed 8128)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally { NetSession.Stop(); }
        }

        private static void Check(bool ok, string name)
        {
            _checks++;
            if (!ok) throw new InvalidOperationException($"FAIL: {name}");
        }

        private static PlayerState State(ushort life, ushort health = 50, ushort generation = 10) => new()
        {
            SlotIndex = 1, SlotGeneration = generation, LifeId = life, Health = health,
            Flags = (byte)(PlayerState.FlagActive | (health > 0 ? PlayerState.FlagSpawned : 0)),
            Position = new Vector3(10, 2, 3), Facing = Vector3.UnitZ
        };

        private static void Wire()
        {
            Check(NetUnlagged.PressAgeEnabled, "recovered trigger pulls include their age by default");
            Check(PlayerState.Size == 114, "compact player wire size includes four event history entries");
            Check(1 + SnapshotHeader.Size + PlayerState.Size * PlayerEntity.SlotCapacity <= NetConfig.MaxPacketSize
                && NetConfig.MaxPacketSize <= 1472, "eight-player snapshot fits one Ethernet UDP datagram");
            byte[] buffer = new byte[NetConfig.MaxPacketSize];
            var state = State(ushort.MaxValue, 99, 65400);
            state.DamageEventId = 65535;
            state.Damage3 = new DamageEvent { EventId = 65535,
                AttackerSlot = 0, AttackerGeneration = 123, Damage = 32,
                Beam = 2, Flags = 7, Direction = new Vector3(.25f, .1f, 0) };
            state.Write(buffer);
            PlayerState read = PlayerState.Read(buffer);
            Check(read.LifeId == 65535 && read.SlotGeneration == 65400 && read.Damage3.Damage == 32
                && (read.Damage3.Direction - state.Damage3.Direction).Length < 0.0002f
                && read.Damage3.AttackerGeneration == 123 && read.AttackerSlot == 0,
                "player and damage event round trip");
            var intent = new IntentPacket { MatchId = 51, AuthorityEpoch = 9, SlotGeneration = 22,
                LifeId = 65535, Frame = uint.MaxValue, Position = state.Position, Aim = state.Facing,
                ChargeLevel = 99, ShotFlags = 2, HomingTarget = 0x82, AckSubFrame = 77 };
            intent.Write(buffer);
            IntentPacket input = IntentPacket.Read(buffer.AsSpan(0, IntentPacket.FullSize));
            Check(input.MatchId == 51 && input.AuthorityEpoch == 9 && input.SlotGeneration == 22
                && input.LifeId == 65535 && input.ChargeLevel == 99 && input.HomingTarget == 0x82
                && input.AckSubFrame == 77, "intent round trip");
            var claim = new HitClaimPacket { MatchId = 51, AuthorityEpoch = 3, ShooterGeneration = 5,
                ShooterLifeId = 8, VictimGeneration = 10, VictimLifeId = 9, HitPoint = state.Position,
                ClaimId = 65535, Damage = 127, LaunchFrame = 72,
                Direction = new Vector3(.35f, .05f, -.1f) };
            claim.Write(buffer);
            var hit = HitClaimPacket.Read(buffer);
            Check(hit.MatchId == 51 && hit.AuthorityEpoch == 3 && hit.ShooterLifeId == 8
                && hit.VictimLifeId == 9 && hit.VictimGeneration == 10 && hit.HitPoint == state.Position
                && hit.Damage == 127 && hit.LaunchFrame == 72
                && (hit.Direction - claim.Direction).Length < 0.0002f, "claim round trip");
            var match = new MatchStatePacket { MatchId = 51, AuthorityEpoch = 638900000000000000UL,
                RoomKey = new string('r', 40), NextRoomKey = new string('n', 40) };
            match.Write(buffer);
            var control = MatchStatePacket.Read(buffer);
            Check(control.NextRoomKey == match.NextRoomKey && control.AuthorityEpoch == 638900000000000000UL, "epoch does not overlap room names");
            var roster = Roster(10, 11);
            roster.Write(buffer);
            var people = RosterPacket.Read(buffer);
            Check(people.Generations[1] == 10 && people.Revision == 11 && people.Names[1] == "victim", "roster identity round trip");
        }

        private static void PresentationStateSafety()
        {
            var camera = new CameraInfo();
            camera.Reset();
            camera.Position = Vector3.Zero;
            camera.Target = Vector3.UnitY;
            camera.UpVector = Vector3.UnitY;
            camera.Update();
            Check(Single.IsFinite(camera.Facing.X) && Single.IsFinite(camera.Facing.Y)
                && Single.IsFinite(camera.Facing.Z) && Single.IsFinite(camera.Field48)
                && Single.IsFinite(camera.Field4C) && Single.IsFinite(camera.ViewMatrix.Row0.X),
                "vertical camera basis remains finite");

            camera.Target = camera.Position;
            camera.Update();
            Check(Single.IsFinite(camera.ViewMatrix.Row0.X)
                && Single.IsFinite(camera.ViewMatrix.Row1.Y)
                && Single.IsFinite(camera.ViewMatrix.Row2.Z),
                "coincident camera target repairs itself");

            PlayerEntity player = Player(1, 100);
            FieldInfo frozen = typeof(PlayerEntity).GetField("_frozenTimer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo frozenGfx = typeof(PlayerEntity).GetField("_frozenGfxTimer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            frozen.SetValue(player, (ushort)20);
            frozenGfx.SetValue(player, (ushort)0);
            typeof(PlayerEntity).GetMethod("ModSetFrozen",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, new object[] { true });
            Check((ushort)frozenGfx.GetValue(player)! >= 30,
                "replicated freeze keeps the visible ice alive");

            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.HudDisruptedState))!
                .SetValue(player, (byte)2);
            FieldInfo disruptionTimer = typeof(PlayerEntity).GetField("_hudDisruptedTimer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            disruptionTimer.SetValue(player, (ushort)0);
            typeof(PlayerEntity).GetMethod("UpdateDisruptedState",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, Array.Empty<object>());
            Check(player.HudDisruptedState == 3 && (ushort)disruptionTimer.GetValue(player)! == 0,
                "zero disruption timer cannot underflow into a stuck distorted HUD");
        }

        private static void LoopbackAdmission()
        {
            NetSession.Stop();
            using var cancel = new System.Threading.CancellationTokenSource();
            var server = new DedicatedServer(0) { RunsTheMatch = false };
            var running = System.Threading.Tasks.Task.Run(() => server.Run(cancel.Token));
            try
            {
                Check(System.Threading.SpinWait.SpinUntil(() => server.BoundPort != 0 || running.IsCompleted, 3000)
                    && !running.IsCompleted, "hosted server starts without assets");
                using var socket = new LoopbackPeer();
                socket.Client.ReceiveTimeout = 2000;
                var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.BoundPort);
                byte[] hello = new byte[7] { (byte)PacketType.Hello, NetConfig.ProtocolVersion, 255, 42, 0, 0, 0 };
                socket.Send(hello, hello.Length, endpoint);
                bool welcome = false, match = false, roster = false, grant = false;
                MatchStatePacket admission = default;
                for (int i = 0; i < 20 && !(welcome && match && roster && grant); i++)
                {
                    var from = endpoint;
                    byte[] packet = socket.Receive(ref from);
                    var body = packet.AsSpan(1);
                    switch ((PacketType)packet[0])
                    {
                        case PacketType.Welcome:
                            welcome = body.Length == 17 && BinaryPrimitives.ReadUInt32LittleEndian(body[1..]) == 42
                                && BinaryPrimitives.ReadUInt64LittleEndian(body[7..]) > uint.MaxValue
                                && BinaryPrimitives.ReadUInt16LittleEndian(body[15..]) == 1;
                            break;
                        case PacketType.MatchState:
                            admission = MatchStatePacket.Read(body);
                            match = admission.AuthorityEpoch > uint.MaxValue; break;
                        case PacketType.Roster:
                            var members = RosterPacket.Read(body);
                            roster = members.AuthorityEpoch > uint.MaxValue && members.Generations[0] == 1; break;
                        case PacketType.Authority:
                            grant = body.Length == 13 && BinaryPrimitives.ReadUInt64LittleEndian(body[3..]) > uint.MaxValue
                                && BinaryPrimitives.ReadUInt16LittleEndian(body[11..]) == 1; break;
                    }
                }
                Check(welcome && match && roster && grant, "real UDP admission agrees on 64-bit epoch and occupant");
                hello[1]--;
                socket.Send(hello, hello.Length, endpoint);
                bool refused = false;
                for (int i = 0; i < 20 && !refused; i++)
                {
                    var from = endpoint;
                    refused = socket.Receive(ref from)[0] == (byte)PacketType.Refused;
                }
                Check(refused, "old protocol is explicitly refused");
                // Drain the admission responses, then bracket each end request
                // with Hello so its response reports the server's resulting state.
                while (socket.Available > 0) { var from = endpoint; socket.Receive(ref from); }
                MatchStatePacket EndRequest(ushort requestedMatch, ulong requestedEpoch)
                {
                    byte[] end = new byte[11]; end[0] = (byte)PacketType.MatchEnd;
                    BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(1), requestedMatch);
                    BinaryPrimitives.WriteUInt64LittleEndian(end.AsSpan(3), requestedEpoch);
                    socket.Send(end, end.Length, endpoint);
                    hello[1] = NetConfig.ProtocolVersion;
                    socket.Send(hello, hello.Length, endpoint);
                    for (int i = 0; i < 20; i++)
                    {
                        var from = endpoint; byte[] reply = socket.Receive(ref from);
                        if (reply[0] == (byte)PacketType.MatchState)
                        {
                            var state = MatchStatePacket.Read(reply.AsSpan(1));
                            bool expectedEnding = requestedMatch == admission.MatchId && requestedEpoch == admission.AuthorityEpoch;
                            if (state.Ending == expectedEnding) return state;
                        }
                    }
                    throw new InvalidOperationException("No match state after end request");
                }
                Check(!EndRequest((ushort)(admission.MatchId + 1), admission.AuthorityEpoch).Ending,
                    "stale match-end cannot finish another match");
                while (socket.Available > 0) { var from = endpoint; socket.Receive(ref from); }
                Check(!EndRequest(admission.MatchId, admission.AuthorityEpoch - 1).Ending,
                    "previous authority cannot finish current match");
                while (socket.Available > 0) { var from = endpoint; socket.Receive(ref from); }
                Check(EndRequest(admission.MatchId, admission.AuthorityEpoch).Ending,
                    "current authority can finish its own match");
            }
            finally
            {
                cancel.Cancel();
                Check(running.Wait(3000), "loopback server stops cleanly");
            }
        }

        private static void RelaySnapshotValidation()
        {
            // Drive the production handler without UDP timing or game assets.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var server = new DedicatedServer(0) { RunsTheMatch = false };
            var owner = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 31001);
            var other = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 31002);
            T Field<T>(string name) => (T)typeof(DedicatedServer).GetField(name, flags)!.GetValue(server)!;
            void Send(System.Net.IPEndPoint sender, PacketType type, byte[] body)
            {
                byte[] data = new byte[body.Length + 1]; data[0] = (byte)type; body.CopyTo(data, 1);
                typeof(DedicatedServer).GetMethod("Handle", flags)!.Invoke(server,
                    new object[] { new ReceivedPacket(sender, data, data.Length), 1.0 });
            }
            void Hello(System.Net.IPEndPoint sender, uint id)
            {
                byte[] body = new byte[6]; body[0] = NetConfig.ProtocolVersion; body[1] = 255;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), id);
                Send(sender, PacketType.Hello, body);
            }
            byte[] Snapshot(uint frame, params PlayerState[] states)
            {
                int playersEnd = SnapshotHeader.Size + states.Length * PlayerState.Size;
                const int timeSyncSize = PlayerEntity.SlotCapacity * sizeof(float) * 2;
                byte[] body = new byte[playersEnd + timeSyncSize + NetHealthSync.HeaderSize];
                ushort matchId = Field<ushort>("_matchId");
                new SnapshotHeader { MatchId = matchId, AuthorityEpoch = Field<ulong>("_authorityEpoch"),
                    Frame = frame, PlayerCount = (byte)states.Length }.Write(body);
                for (int i = 0; i < states.Length; i++)
                    states[i].Write(body.AsSpan(SnapshotHeader.Size + i * PlayerState.Size));
                // The relay validates the same snapshot tails production sends.
                // Zeroed match clocks are valid; an empty health-spawn section
                // consists of the current match id plus a zero entry count.
                BinaryPrimitives.WriteUInt16LittleEndian(
                    body.AsSpan(playersEnd + timeSyncSize), matchId);
                return body;
            }
            Hello(owner, 1); Hello(other, 2);
            var state = new PlayerState { SlotIndex = 0, SlotGeneration = 1, LifeId = 1, Health = 50,
                Flags = PlayerState.FlagActive | PlayerState.FlagSpawned, Facing = Vector3.UnitZ };
            Send(owner, PacketType.Snapshot, Snapshot(1, state));
            Check(Field<uint>("_snapshotFrame") == 1, "valid authority establishes relay frame");
            byte[] previous = Field<byte[]>("_lastSnapshot");
            Send(other, PacketType.Snapshot, Snapshot(500, state));
            Check(Field<uint>("_snapshotFrame") == 1, "non-authority cannot advance relay frame");
            state.LifeId = 2;
            Send(owner, PacketType.Snapshot, Snapshot(100, state, state));
            Check(Field<uint>("_snapshotFrame") == 1 && Field<ushort[]>("_slotLives")[0] == 1
                && ReferenceEquals(previous, Field<byte[]>("_lastSnapshot")),
                "duplicate slots cannot commit frame, life or cached snapshot");
            state.LifeId = 1;
            Send(owner, PacketType.Snapshot, Snapshot(2, state));
            Check(Field<uint>("_snapshotFrame") == 2, "valid lower frame survives malformed higher frame");
        }

        private static void ClaimBoundaries()
        {
            Session(local: true);
            var shooter = State(2, 50, 9); shooter.SlotIndex = 0;
            NetPlayerLifecycle.AcceptState(shooter, 1);
            NetPlayerLifecycle.AcceptState(State(8), 1);
            typeof(NetSession).GetProperty(nameof(NetSession.IsAuthority))!.SetValue(null, true);
            try
            {
                var claim = new HitClaimPacket { MatchId = 51, AuthorityEpoch = 4, ShooterGeneration = 9,
                    ShooterLifeId = 2, VictimSlot = 1, VictimGeneration = 10, VictimLifeId = 7, ClaimId = 20000 };
                byte[] body = new byte[1 + HitClaimPacket.Size]; body[0] = 1;
                claim.Write(body.AsSpan(1));
                NetHitClaims.Receive(0, body);
                Check(NetPlayerLifecycle.OldLifeClaims == 1 && NetHitClaims.AppliedHere == 0,
                    "delayed claim cannot damage victim's next life");
                claim.VictimLifeId = 8; claim.ShooterLifeId = 1;
                claim.Write(body.AsSpan(1)); NetHitClaims.Receive(0, body);
                Check(NetPlayerLifecycle.OldLifeClaims == 2, "claim from previous shooter life refused");
                claim.ShooterLifeId = 2; claim.ClaimId = 1;
                claim.Write(body.AsSpan(1)); NetHitClaims.Receive(0, body);
                Check(NetHitClaims.RepeatsHere == 0, "stale-life claim does not poison current claim ordering");
                NetHitClaims.Receive(0, body);
                Check(NetHitClaims.RepeatsHere == 1, "current claim retransmission is deduplicated");
            }
            finally { typeof(NetSession).GetProperty(nameof(NetSession.IsAuthority))!.SetValue(null, false); }
        }

        private static void StateMachine()
        {
            var tracker = new NetLifecycleTracker();
            tracker.SetOccupant(10);
            Check(tracker.Accept(10, 7, NetworkPlayerState.Alive, out bool fresh) == LifecycleRejection.None && fresh, "first life");
            Check(tracker.Accept(10, 7, NetworkPlayerState.Dead, out _) == LifecycleRejection.None, "death");
            Check(tracker.Accept(10, 7, NetworkPlayerState.Alive, out _) == LifecycleRejection.InvalidResurrection, "same-life resurrection refused");
            tracker.Accept(10, 7, NetworkPlayerState.Spectating, out _);
            Check(tracker.Accept(10, 7, NetworkPlayerState.Alive, out _) == LifecycleRejection.InvalidResurrection, "spectator cannot erase death");
            Check(tracker.Accept(10, 8, NetworkPlayerState.Alive, out fresh) == LifecycleRejection.None && fresh, "respawn");
            Check(tracker.Accept(10, 7, NetworkPlayerState.Dead, out _) == LifecycleRejection.OldLife, "old death refused");
            tracker.SetOccupant(11);
            Check(tracker.Accept(10, 9, NetworkPlayerState.Alive, out _) == LifecycleRejection.WrongGeneration, "old occupant refused");
            Check(NetLifecycleTracker.Next(65535) == 1 && NetLifecycleTracker.Newer((ushort)1, (ushort)65535)
                && !NetLifecycleTracker.Newer((ushort)65535, (ushort)1), "life wrap skips zero");
            Check(NetLifecycleTracker.Newer(0u, uint.MaxValue), "frame wrap");
        }

        private static RosterPacket Roster(ushort victimGeneration = 10, uint revision = 1)
        {
            var roster = RosterPacket.Create();
            roster.MatchId = 51; roster.AuthorityEpoch = 4; roster.Revision = revision; roster.Count = 2;
            roster.Slots[0] = 0; roster.Generations[0] = 9; roster.Names[0] = "shooter";
            roster.Slots[1] = 1; roster.Generations[1] = victimGeneration; roster.Names[1] = "victim";
            return roster;
        }

        private static void Session(bool local = false)
        {
            NetSession.StartPlayback();
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 51, AuthorityEpoch = 4 }, false);
            NetSession.ApplyRoster(Roster());
            if (local)
            {
                byte[] welcome = new byte[18]; welcome[0] = (byte)PacketType.Welcome;
                BinaryPrimitives.WriteUInt32LittleEndian(welcome.AsSpan(2), NetSession.ClientId);
                BinaryPrimitives.WriteUInt16LittleEndian(welcome.AsSpan(6), 51);
                BinaryPrimitives.WriteUInt64LittleEndian(welcome.AsSpan(8), 4);
                BinaryPrimitives.WriteUInt16LittleEndian(welcome.AsSpan(16), 9);
                Deliver(welcome);
                Check(NetSession.LocalSlot == -1, "socket-free playback refuses a valid local admission");
                // Prediction/claim fixtures need a local actor but do not test
                // admission. Real UDP admission is covered by LoopbackAdmission.
                typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, 0);
            }
        }

        private static byte[] Packet(uint frame, PlayerState state, ushort match = 51, ulong epoch = 4)
        {
            const int timeSyncSize = PlayerEntity.SlotCapacity * sizeof(float) * 2;
            int payloadSize = SnapshotHeader.Size + PlayerState.Size + timeSyncSize + NetHealthSync.HeaderSize;
            byte[] bytes = new byte[1 + payloadSize];
            bytes[0] = (byte)PacketType.Snapshot;
            new SnapshotHeader { MatchId = match, AuthorityEpoch = epoch, Frame = frame, PlayerCount = 1 }
                .Write(bytes.AsSpan(1));
            state.Write(bytes.AsSpan(1 + SnapshotHeader.Size));
            int healthOffset = 1 + SnapshotHeader.Size + PlayerState.Size + timeSyncSize;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(healthOffset), match);
            return bytes;
        }
        private static void Deliver(byte[] bytes)
        {
            NetSession.InjectPlaybackPacket(bytes, bytes.Length);
            NetSession.Update(0);
        }

        private static void TransportCoalescing()
        {
            using var transport = new NetTransport(0);
            transport.EnableRealtimeStateCoalescing();
            using var sender = new LoopbackPeer();
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,
                transport.LocalPort);

            Protocol17Tests.Connect(sender.Transport, transport);
            sender.Send(new byte[] { (byte)PacketType.Snapshot, 1 }, 2, endpoint);
            sender.Send(new byte[] { (byte)PacketType.Snapshot, 2 }, 2, endpoint);
            sender.Send(new byte[] { (byte)PacketType.Snapshot, 3 }, 2, endpoint);
            sender.Send(new byte[] { (byte)PacketType.SlotIntent, 1, 4 }, 3, endpoint);
            sender.Send(new byte[] { (byte)PacketType.SlotIntent, 1, 5 }, 3, endpoint);
            sender.Send(new byte[] { (byte)PacketType.Bye }, 1, endpoint);

            Check(System.Threading.SpinWait.SpinUntil(
                () => transport.StatePacketsCoalesced >= 3, 2000),
                "transport actually coalesces burst state");
            // The counter above proves every replaceable packet arrived; give
            // the ordered Bye immediately behind them one scheduler slice too.
            System.Threading.Thread.Sleep(20);

            int snapshots = 0, intents = 0, byes = 0;
            byte newestSnapshot = 0, newestIntent = 0;
            foreach (ReceivedPacket packet in transport.Drain())
            {
                if (packet.Type == PacketType.Snapshot)
                {
                    snapshots++;
                    newestSnapshot = packet.Payload[0];
                }
                else if (packet.Type == PacketType.SlotIntent)
                {
                    intents++;
                    newestIntent = packet.Payload[1];
                }
                else if (packet.Type == PacketType.Bye)
                {
                    byes++;
                }
            }
            Check(snapshots == 1 && newestSnapshot == 3,
                "latest snapshot wins after a receive burst");
            Check(intents == 1 && newestIntent == 5,
                "latest slot intent wins per slot");
            Check(byes == 1, "control traffic is not coalesced");
            Check(transport.PacketsDropped == 0,
                "coalescing burst does not overflow transport queue");
        }

        private static void PacketOrdering()
        {
            Session();
            Deliver(Packet(100, State(7)));
            Deliver(Packet(102, State(7, 0)));
            Deliver(Packet(101, State(7)));
            Check(NetSession.RemoteStates[1].Health == 0 && NetSession.SnapshotsOutOfOrder == 1, "reordered alive snapshot cannot resurrect");
            Deliver(Packet(103, State(7)));
            Check(NetPlayerLifecycle.InvalidResurrections == 1 && !NetSession.RemoteStateValid[1], "newer frame cannot resurrect same life");
            Deliver(Packet(104, State(8)));
            Deliver(Packet(105, State(7, 0)));
            Check(NetPlayerLifecycle.Get(1) == 8 && NetPlayerLifecycle.StaleLifeStates == 1, "delayed death cannot kill new life");
            uint last = NetSession.LastSnapshotFrame;
            Deliver(Packet(10000, State(9), 50));
            Check(NetSession.LastSnapshotFrame == last && NetPlayerLifecycle.CrossMatch == 1, "old match cannot poison frame ordering");
            Deliver(Packet(10000, State(9), 51, 3));
            Check(NetSession.LastSnapshotFrame == last && NetPlayerLifecycle.CrossAuthority == 1, "old authority cannot poison ordering");
            NetSession.ApplyRoster(Roster(11, 2));
            Deliver(Packet(106, State(1, 99, 11)));
            NetSession.ApplyRoster(Roster(10, 1));
            Deliver(Packet(107, State(8)));
            Check(NetPlayerLifecycle.Generation(1) == 11 && NetPlayerLifecycle.Get(1) == 1, "old roster/snapshot cannot restore former occupant");
            var input = new IntentPacket { MatchId = 51, AuthorityEpoch = 4, SlotGeneration = 10, LifeId = 8, Frame = 5000 };
            NetSession.AcceptSlotIntent(1, input);
            Check(!NetSession.RemoteIntentValid[1], "previous occupant intent rejected");
            input.SlotGeneration = 11; input.LifeId = 1; input.Frame = 1;
            NetSession.AcceptSlotIntent(1, input);
            Check(NetSession.RemoteIntentValid[1], "rejected packet did not advance intent ordering");
            input.LifeId = 2; input.Frame = 2;
            NetSession.AcceptSlotIntent(1, input);
            Check(NetSession.RemoteIntents[1].Frame == 1, "unallocated life intent rejected");
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 51, AuthorityEpoch = 5 }, false);
            var nextRoster = Roster(11, 1); nextRoster.AuthorityEpoch = 5;
            NetSession.ApplyRoster(nextRoster);
            Deliver(Packet(1, State(1, 99, 11), 51, 5));
            Check(NetSession.LastSnapshotFrame == 1 && NetSession.RemoteStateValid[1], "new epoch permits new frame origin");
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 50, AuthorityEpoch = 4 }, false);
            Check(NetSession.CurrentMatchId == 51 && NetSession.AuthorityEpoch == 5, "old control cannot roll back stream");
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 1, AuthorityEpoch = 6 }, false);
            nextRoster.MatchId = 1; nextRoster.AuthorityEpoch = 6; nextRoster.Generations[1] = 1;
            NetSession.ApplyRoster(nextRoster);
            Deliver(Packet(1, State(1, 99, 1), 1, 6));
            Deliver(Packet(20000, State(1, 0, 1), 1, 5));
            Check(NetSession.RemoteStateValid[1] && NetSession.RemoteStates[1].Health == 99,
                "restarted authority can reset counters without accepting old epoch");
            byte[] malformed = Packet(30000, State(1, 0, 1), 1, 6);
            Deliver(malformed[..^1]);
            Check(NetSession.LastSnapshotFrame == 1, "truncated snapshot cannot advance stream");
        }

        private static PlayerEntity Player(int slot, int health)
        {
            // Only plain state is used; spawning/rendering requires cartridge assets.
            var player = (PlayerEntity)RuntimeHelpers.GetUninitializedObject(typeof(PlayerEntity));
            typeof(EntityBase).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(player, _scene);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.SlotIndex))!.SetValue(player, slot);
            player.Health = health;
            return player;
        }

        private static void Prediction()
        {
            Session(local: true);
            GameState.Mode = GameMode.Battle;
            var localState = State(2, 50, 9); localState.SlotIndex = 0;
            NetPlayerLifecycle.AcceptState(localState, 1);
            Deliver(Packet(1, State(7)));
            var shooter = Player(0, 50); var victim = Player(1, 50);
            NetHitPrediction.DeathEnabled = true;
            uint damage = 100; DamageFlags flags = DamageFlags.Death;
            var impact = new Vector3(.35f, .05f, 0);
            NetHitPrediction.NoteHit(victim, shooter, ref flags, ref damage,
                BeamType.Missile, direction: impact);
            Check(damage == 49 && !flags.HasFlag(DamageFlags.Death)
                && NetHitPrediction.DeathsPredicted == 0 && !NetHitPrediction.HeldDead(1), "remote lethal clamps even with death flag/option");
            byte[] claims = new byte[NetConfig.MaxPacketSize];
            int length = NetHitClaims.Compose(claims);
            Check(length > 0, "lethal prediction still declares hit");
            var claim = HitClaimPacket.Read(claims.AsSpan(1));
            Check(claim.Damage == 100 && claim.VictimLifeId == 7 && claim.ShooterLifeId == 2
                && (claim.Direction - impact).Length < 0.0002f,
                "claim preserves full lethal damage, identities and impact momentum");
            byte[] verdict = new byte[HitVerdictPacket.HeaderSize + HitVerdictPacket.EntrySize];
            HitVerdictPacket.Write(verdict, new[] { (claim.ClaimId, HitVerdictPacket.ResultRefused) }, 51, 4, 9, 2);
            NetHitClaims.ApplyVerdicts(verdict);
            Check(NetHitPrediction.HealthFor(1, 50) == 50 && NetHitPrediction.LethalDenied == 1,
                "denied lethal restores health without a death and records one denial");
            NetHitClaims.ApplyVerdicts(verdict);
            Check(NetHitPrediction.LethalDenied == 1, "duplicate verdict does not settle prediction twice");
            damage = 20; flags = 0;
            NetHitPrediction.NoteHit(victim, shooter, ref flags, ref damage);
            Check(NetHitPrediction.HealthFor(1, 50) == 30, "normal predicted damage immediate");
            Deliver(Packet(2, State(8, 99)));
            Check(NetHitPrediction.HealthFor(1, 99) == 99, "new life clears prediction debit and floor");
            damage = 100; flags = DamageFlags.Death;
            NetHitPrediction.NoteHit(shooter, shooter, ref flags, ref damage);
            Check(damage == 100 && flags.HasFlag(DamageFlags.Death) && NetHitPrediction.SelfDeathsPredicted == 1,
                "self death remains immediate");
            Check(!NetPlayerLifecycle.CanSpawn, "client engine cannot allocate spawn");
            var projectile = (BeamProjectileEntity)RuntimeHelpers.GetUninitializedObject(typeof(BeamProjectileEntity));
            typeof(EntityBase).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(projectile, _scene);
            projectile.Owner = shooter;
            NetPlayerLifecycle.StampProjectile(projectile);
            Check(NetPlayerLifecycle.CurrentProjectile(projectile), "projectile remembers firing life");
            localState.LifeId++;
            NetPlayerLifecycle.AcceptState(localState, 3);
            Check(NetPlayerLifecycle.CurrentProjectile(projectile), "registered projectile retains its launch life after shooter respawn");
            GameState.Points[0] = 4;
            using (new NetDamage.PredictionScoreScope(true)) GameState.Points[0]++;
            Check(GameState.Points[0] == 4, "predicted death cannot award score");
        }

        private static void DamageHistory()
        {
            Session();
            var state = State(7, 0);
            NetPlayerLifecycle.AcceptState(state, 1);
            var player = Player(1, 0); // already down: count events without rendering feedback
            NetDamage.BeginLife(1, state);
            state.DamageEventId = 3;
            state.Damage1 = new DamageEvent { EventId = 1 };
            state.Damage2 = new DamageEvent { EventId = 2 };
            state.Damage3 = new DamageEvent { EventId = 3 };
            NetDamage.Replay(player, state);
            NetDamage.Replay(player, state);
            Check(NetDamage.Replayed[1] == 3, "three hits replay exactly once despite duplicate snapshot");
            state.DamageEventId = 65534;
            NetDamage.BeginLife(1, state);
            state.DamageEventId = 1;
            state.Damage1.EventId = 65535; state.Damage2.EventId = 1; state.Damage3 = default;
            NetDamage.Replay(player, state);
            NetDamage.Replay(player, state);
            Check(NetDamage.Replayed[1] == 5, "damage event wrap skips zero and deduplicates");
            var next = State(8, 0);
            NetPlayerLifecycle.AcceptState(next, 2);
            NetDamage.Replay(player, next);
            NetDamage.Replay(player, state);
            Check(NetDamage.Replayed[1] == 5 && NetPlayerLifecycle.OldLifeDamage == 1, "old damage cannot cross life boundary");
        }

        private static void PresentationClock()
        {
            Session();
            PlayerState a = State(7);
            a.Position = new Vector3(10, 2, 3);
            Deliver(Packet(100, a));

            // Rebuild the smoothing history with deterministic 60 Hz arrival
            // times so this test measures presentation arithmetic, not how
            // quickly the test process happened to execute three statements.
            NetSmoothing.Reset();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long step = System.Diagnostics.Stopwatch.Frequency / 60;
            NetSmoothing.Record(100, new[] { a }, now);

            PlayerState b = a;
            b.Position = new Vector3(11, 2, 3);
            NetSmoothing.Record(101, new[] { b }, now + step);
            PlayerState c = a;
            c.Position = new Vector3(12, 2, 3);
            NetSmoothing.Record(102, new[] { c }, now + step * 2);

            NetSmoothing.Tick();
            NetSmoothing.Tick();
            NetSmoothing.PreparePresentation(.5);
            Check(NetSmoothing.SamplePresentation(1, out Vector3 presented, out _),
                "high-refresh playout has a presentation sample");
            Check(NetSmoothing.AckPoint(out uint ackFrame, out byte ackSub),
                "presentation playout has an ack");
            double ackPoint = ackFrame + ackSub / 256.0;
            double positionPoint = 100 + (presented.X - 10);
            Check(Math.Abs(ackPoint - positionPoint) < .02,
                "rendered puppet and sub-frame ack name the same world");

            NetSmoothing.PreparePresentation(1);
            Check(NetSmoothing.Sample(1, out Vector3 current, out _)
                && NetSmoothing.SamplePresentation(1, out Vector3 atCurrent, out _)
                && (current - atCurrent).Length < .0001f,
                "60 Hz/current presentation keeps the simulation playout point");

            // A missing authority frame must hold a world the client actually
            // received, and the ack must name that held world rather than the
            // fractional point the authority could have reconstructed.
            NetSmoothing.Reset();
            NetSmoothing.Record(100, new[] { a }, now);
            NetSmoothing.Record(102, new[] { c }, now + step * 2);
            NetSmoothing.Tick();
            NetSmoothing.Tick();
            NetSmoothing.PreparePresentation(.5);
            Check(NetSmoothing.SamplePresentation(1, out Vector3 held, out _)
                && Math.Abs(held.X - 10) < .0001f,
                "lost snapshot holds the nearest recorded presentation");
            Check(NetSmoothing.AckPoint(out ackFrame, out ackSub)
                && ackFrame == 100 && ackSub == 0,
                "held presentation acks the world the client actually received");


        }

        private static void HistoryBoundaries()
        {
            Session();
            Deliver(Packet(100, State(7)));
            NetSmoothing.Record(101, new[] { State(7) });
            var spawn = State(8); spawn.Position = new Vector3(150, 20, -70);
            Deliver(Packet(102, spawn));
            Check(!NetSmoothing.Sample(1, out var position, out _) || position == spawn.Position,
                "smoothing never returns old life position");
            var players = PlayerEntity._players;
            var saved = (PlayerEntity[])players.Clone();
            try
            {
                for (int i = 0; i < players.Length; i++) players[i] = Player(i, i == 1 ? 50 : 0);
                players[1].LoadFlags = LoadFlags.Active | LoadFlags.Spawned;
                NetUnlagged.Record(200);
                Check(NetUnlagged.PositionAt(1, 200, 10, 8, out _), "rewind finds current life");
                Check(!NetUnlagged.PositionAt(1, 200, 10, 7, out _)
                    && !NetUnlagged.PositionAt(1, 200, 9, 8, out _), "rewind refuses old life and occupant");
                NetUnlagged.ResetSlot(1);
                Check(!NetUnlagged.PositionAt(1, 200, 10, 8, out _), "slot reset clears rewind");
            }
            finally { Array.Copy(saved, players, players.Length); }
        }

        private static void FaultStream()
        {
            static List<int> Trace(int seed)
            {
                var queue = new NetFaultQueue<int>(seed, 200, 80, .02, .05, .02);
                for (int i = 0; i < 1000; i++) queue.Enqueue(i * 16.0, i);
                var result = new List<int>();
                while (queue.TryDequeue(20000, out int value)) result.Add(value);
                return result;
            }
            Check(Trace(8128).SequenceEqual(Trace(8128)) && !Trace(8128).SequenceEqual(Trace(17)), "seeded fault schedule reproducible");
            Session();
            var stream = new NetFaultQueue<byte[]>(8128, 200, 80, .02, .05, .02);
            ushort lastLife = 0;
            for (int frame = 1; frame <= 3700; frame++)
            {
                if (frame <= 3600)
                {
                    ushort life = (ushort)((frame - 1) / 90 + 1);
                    ushort health = (ushort)((frame - 1) % 90 < 70 ? 50 : 0);
                    stream.Enqueue(frame * 1000.0 / 60, Packet((uint)frame, State(life, health)));
                }
                while (stream.TryDequeue(frame * 1000.0 / 60, out byte[] packet))
                {
                    Deliver(packet);
                    ushort life = NetPlayerLifecycle.Get(1);
                    Check(life >= lastLife, "fault stream never regresses life");
                    lastLife = life;
                }
            }
            Check(lastLife == 40 && NetSession.RemoteStates[1].Health == 0 && NetPlayerLifecycle.InvalidResurrections == 0,
                "400ms RTT profile: forty lives, no invalid resurrection");
            Check(stream.Dropped > 0 && stream.Duplicated > 0 && stream.Reordered > 0 && NetSession.SnapshotsOutOfOrder > 0,
                "loss, duplication and reordering actually exercised");
            var bounded = new NetFaultQueue<int>(1, 200, 0, 0, 0, 1, 4);
            for (int i = 0; i < 100; i++) bounded.Enqueue(0, i);
            Check(bounded.Count == 4 && bounded.Dropped > 0, "fault queue bounded under stalled consumer");
        }
    }
}
