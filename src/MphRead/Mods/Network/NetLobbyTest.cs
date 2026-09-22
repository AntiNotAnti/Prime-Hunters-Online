using System;
using MphRead.Mods.Multiplayer;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Threading;

namespace MphRead.Mods.Network
{
    /// <summary>Asset-free, real UDP control-plane regression. It does not substitute for rendered match acceptance.</summary>
    public static class NetLobbyTest
    {
        private static int _checks;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _checks++;
        }

        public static int Run()
        {
            try
            {
                NetHealthSyncTest.Run();
                ProtocolChecks();
                DemoProtocolCheck();
                LayoutChecks();
                ClientStateChecks();
                Scenario();
                ReadyOptionalScenario();
                AbandonedLobbyScenario();
                HostedOwnerDepartureScenario();
                HostedOwnerTransferScenario();
                TeamScenario();
                CustomScenario();
                FourTeamScenario();
                ContinuousScenario();
                ClientSessionScenario();
                TeamGameplayTest.Run(Check);
                Console.WriteLine($"[netlobbytest] PASS: {_checks} assertions; protocol, UDP lifecycle, direct post-match lobby return, abandoned-session cleanup, hosted ownership, teams, rebind and continuous rotation.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[netlobbytest] FAIL after {_checks} assertions: {ex}");
                return 1;
            }
            finally { NetSession.Stop(); NetLag.Configure("0"); NetLag.ConfigureLoss("0"); }
        }

        private static void ProtocolChecks()
        {
            Check(NetConfig.ProtocolVersion == 16 && (byte)PacketType.SessionState == 36
                && (byte)PacketType.MapOffer == 32 && (byte)PacketType.MapDone == 35,
                "combined protocol and non-overlapping map/lobby IDs");
            var state = new SessionStatePacket { Phase = SessionPhase.Starting, Policy = ServerSessionPolicy.Lobby,
                OwnerSlot = 7, MaxPlayers = 8, Revision = ushort.MaxValue, MatchId = 19,
                RuleFlags = SessionRules.RequireReady | SessionRules.AllowJoinInProgress | SessionRules.LockTeams,
                WorldProfile = MatchWorldProfile.Resolve(8),
                ExpectedParticipants = 255, LoadedParticipants = 3,
                StartCountdownMilliseconds = 3000,
                Match = new MatchDefinition { RoomKey = new string('X', 40), Mode = GameMode.BattleTeams,
                    Format = MatchFormat.FourVsFour, TimeLimitSeconds = 600, PointGoal = 20,
                    FriendlyFire = true, AffinityWeapons = true, ShadowFreeze = true, HideOpponentHealth = true,
                    DisablePowerups = true } };
            byte[] data = new byte[SessionStatePacket.Size]; state.Write(data);
            Check(SessionStatePacket.TryRead(data, out var read) && read.Match == state.Match
                && read.Revision == state.Revision && read.LoadedParticipants == 3
                && read.StartCountdownMilliseconds == 3000,
                "session round trip/max room/revision/countdown");
            Check(MapResourceRules.IsPowerup(ItemType.DoubleDamage)
                && MapResourceRules.IsPowerup(ItemType.Cloak)
                && MapResourceRules.IsPowerup(ItemType.Deathalt)
                && !MapResourceRules.IsPowerup(ItemType.HealthBig),
                "multiplayer powerup classification");
            for (int length = 0; length < data.Length; length++)
                Check(!SessionStatePacket.TryRead(data.AsSpan(0, length), out _), "truncated session");
            foreach (int offset in new[] { 0, 1, 8, 9 })
            { byte saved = data[offset]; data[offset] = 254; Check(!SessionStatePacket.TryRead(data, out _), "invalid enum"); data[offset] = saved; }
            byte savedExpected = data[16], savedLoaded = data[17];
            data[16] = 1; data[17] = 2;
            Check(!SessionStatePacket.TryRead(data, out _), "loaded participant must belong to frozen barrier");
            data[16] = savedExpected; data[17] = savedLoaded;
            byte savedMax = data[7], savedOwner = data[6]; data[7] = 2; data[6] = 0;
            Check(!SessionStatePacket.TryRead(data, out _), "participant mask bounded by server slots");
            data[7] = savedMax; data[6] = savedOwner;
            Check(SessionStatePacket.IsNewer(0, ushort.MaxValue) && !SessionStatePacket.IsNewer(ushort.MaxValue, 0), "revision wrap ordering");
            foreach (LobbyCommandType type in Enum.GetValues<LobbyCommandType>())
            {
                var command = new LobbyCommandPacket { CommandId = 42, ExpectedRevision = 17, Type = type,
                    TargetSlot = 7, TeamIndex = -1, Ready = true, Configuration = state };
                byte[] bytes = new byte[LobbyCommandPacket.Size]; command.Write(bytes);
                Check(LobbyCommandPacket.TryRead(bytes, out var decoded) && decoded.Type == type
                    && decoded.CommandId == 42 && decoded.TeamIndex == -1 && decoded.Ready, "command round trip");
                for (int length = 0; length < bytes.Length; length++)
                    Check(!LobbyCommandPacket.TryRead(bytes.AsSpan(0, length), out _), "truncated command");
            }
            foreach (LobbyResultCode code in Enum.GetValues<LobbyResultCode>())
            {
                var result = new LobbyCommandResultPacket { CommandId = 42, ResultCode = code, CurrentRevision = 65535, Reason = "A reason" };
                byte[] bytes = new byte[LobbyCommandResultPacket.Size]; result.Write(bytes);
                Check(LobbyCommandResultPacket.TryRead(bytes, out var decoded) && decoded.ResultCode == code
                    && decoded.Reason == result.Reason && decoded.CurrentRevision == 65535, "result round trip");
                for (int length = 0; length < bytes.Length; length++) Check(!LobbyCommandResultPacket.TryRead(bytes.AsSpan(0, length), out _), "truncated result");
            }
            byte[] loadedBytes = new byte[MatchLoadedPacket.Size]; new MatchLoadedPacket(65535).Write(loadedBytes);
            Check(MatchLoadedPacket.TryRead(loadedBytes, out var loaded) && loaded.MatchId == 65535, "loaded round trip");
            Check(!MatchLoadedPacket.TryRead(loadedBytes.AsSpan(0, 1), out _), "truncated loaded");
            byte[] failedBytes = new byte[MatchLoadFailedPacket.Size]; new MatchLoadFailedPacket(17, "missing map").Write(failedBytes);
            Check(MatchLoadFailedPacket.TryRead(failedBytes, out var failed) && failed.MatchId == 17 && failed.Reason == "missing map", "failed round trip");
            for (int length = 0; length < failedBytes.Length; length++) Check(!MatchLoadFailedPacket.TryRead(failedBytes.AsSpan(0, length), out _), "truncated failure");
            var roster = RosterPacket.Create(); roster.Count = 8; roster.Revision = 123;
            for (int i = 0; i < 8; i++) { roster.Slots[i] = (byte)i; roster.Teams[i] = (sbyte)(i % 5 - 1); roster.LobbyReady[i] = i % 2 == 0; roster.Names[i] = $"Player{i}"; }
            byte[] rosterBytes = new byte[RosterPacket.Size]; roster.Write(rosterBytes);
            Check(RosterPacket.TryRead(rosterBytes, out var rr) && rr.Teams.SequenceEqual(roster.Teams)
                && rr.LobbyReady.SequenceEqual(roster.LobbyReady) && rr.Revision == 123, "roster team/ready/revision round trip");
            for (int length = 0; length < rosterBytes.Length; length++) Check(!RosterPacket.TryRead(rosterBytes.AsSpan(0, length), out _), "truncated roster");
            Check(!new HostRequestPacket().RequireReady, "host requests default ready off");
            var host = new HostRequestPacket { Protocol = NetConfig.ProtocolVersion, MaxPlayers = 8, RoomKey = "room", ServerName = "test",
                Policy = ServerSessionPolicy.Lobby, RequireReady = true, AllowJoinInProgress = true, Format = MatchFormat.FourVsFour };
            byte[] hostBytes = new byte[host.Length]; host.Write(hostBytes); var hr = HostRequestPacket.Read(hostBytes);
            Check(hr.Policy == host.Policy && hr.Format == host.Format && hr.RequireReady && hr.AllowJoinInProgress, "host options appended without rotation");
            var reply = new HostReplyPacket { Started = true, Port = 123, OwnerToken = Guid.NewGuid() };
            byte[] replyBytes = new byte[HostReplyPacket.Size]; reply.Write(replyBytes);
            Check(HostReplyPacket.Read(replyBytes).OwnerToken == reply.OwnerToken, "owner token round trip");
        }

        private static void DemoProtocolCheck()
        {
            string path = Path.Combine(Path.GetTempPath(), $"team-protocol-{Guid.NewGuid():N}{DemoFile.Extension}");
            try
            {
                // A valid demo container with an incompatible packet protocol.
                using (var writer = new DemoWriter(path)) { }
                byte[] bytes = File.ReadAllBytes(path);
                bytes[5] = (byte)(NetConfig.ProtocolVersion - 1);
                File.WriteAllBytes(path, bytes);
                Check(!DemoPlayback.Join(path) && !DemoPlayback.IsActive
                    && DemoPlayback.LastError?.Contains("requires protocol") == true,
                    "incompatible demo fails before scene or session construction");
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check(exclusive.Length >= DemoFile.HeaderSize, "rejected demo releases its file handle");
            }
            finally { DemoPlayback.Stop(); File.Delete(path); }
        }

        private static void LayoutChecks()
        {
            var match = new MatchDefinition { RoomKey = Rooms()[0], Mode = GameMode.BattleTeams };
            foreach (var (format, layout) in new[] {
                (MatchFormat.OneVsOne, new TeamLayout(2, 1, 1)), (MatchFormat.TwoVsTwo, new TeamLayout(2, 2, 2)),
                (MatchFormat.ThreeVsThree, new TeamLayout(2, 3, 3)), (MatchFormat.FourVsFour, new TeamLayout(2, 4, 4)),
                (MatchFormat.TwoVsTwoVsTwoVsTwo, new TeamLayout(4, 2, 2, 2, 2)) })
            {
                MatchDefinition preset = match with { Format = format };
                Check(LobbyRules.ResolveTeamLayout(preset) == layout, $"resolve {format}");
                Check(LobbyRules.ValidateDefinition(preset, out _) == LobbyResultCode.Ok, $"validate {format}");
            }
            foreach (TeamLayout layout in new[] { new TeamLayout(2, 1, 2), new(2, 4, 2), new(2, 2, 3),
                new(3, 1, 1, 1), new(3, 1, 2, 2), new(3, 2, 2, 2), new(4, 1, 1, 2, 4) })
            {
                MatchDefinition custom = match with { Format = MatchFormat.Custom, CustomTeams = layout };
                Check(LobbyRules.ValidateDefinition(custom, out _) == LobbyResultCode.Ok, $"valid custom {layout}");
                var state = new SessionStatePacket { MaxPlayers = 8, Match = custom, WorldProfile = MatchWorldProfile.Resolve(layout.TotalPlayers) };
                byte[] bytes = new byte[SessionStatePacket.Size]; state.Write(bytes);
                Check(SessionStatePacket.TryRead(bytes, out var read) && read.Match == custom && read.WorldProfile == state.WorldProfile, "custom/world wire roundtrip");
                int[] counts = new int[4];
                for (int player = 0; player < layout.TotalPlayers; player++)
                {
                    int team = TeamRules.ChooseTeam(layout, counts);
                    Check(team >= 0 && counts[team] < layout.Capacity(team), "normalized assignment stays within capacity");
                    for (int candidate = 0; candidate < layout.TeamCount; candidate++)
                        if (counts[candidate] < layout.Capacity(candidate))
                            Check(counts[team] * layout.Capacity(candidate) <= counts[candidate] * layout.Capacity(team), "lowest normalized occupancy");
                    counts[team]++;
                }
                Check(TeamRules.ChooseTeam(layout, counts) == -1, "full layout refuses admission");
                for (int team = 0; team < layout.TeamCount; team++) Check(counts[team] == layout.Capacity(team), "fills exact asymmetric layout");
            }
            foreach (TeamLayout invalid in new[] { new TeamLayout(2, 0, 2), new(5, 1, 1, 1, 1), new(3, 4, 4, 1), new(4, 2, 2), new(2, 2, 2, 1) })
                Check(LobbyRules.ValidateDefinition(match with { Format = MatchFormat.Custom, CustomTeams = invalid }, out _) == LobbyResultCode.InvalidConfiguration, "reject invalid layout");
            Check(LobbyRules.ValidateDefinition(match with { Mode = GameMode.Capture, Format = MatchFormat.TwoVsTwoVsTwoVsTwo }, out _) == LobbyResultCode.InvalidConfiguration, "capture rejects four teams");
            Check(LobbyRules.ValidateDefinition(match with { Mode = GameMode.PrimeHunter, Format = MatchFormat.OneVsOne }, out _) == LobbyResultCode.InvalidConfiguration, "prime hunter stays FFA");
            var single = RosterPacket.Create(); single.Count = 1;
            Check(LobbyRules.Validate(match with { Mode = GameMode.Battle, Format = MatchFormat.FreeForAll }, single, false, out _) == LobbyResultCode.NotEnoughPlayers, "explicit FFA minimum two");

            var flexible = RosterPacket.Create();
            flexible.Count = 2;
            flexible.Teams[0] = 0; flexible.Teams[1] = 1;
            flexible.Names[0] = "A"; flexible.Names[1] = "B";
            MatchDefinition twoVsTwo = match with { Format = MatchFormat.TwoVsTwo };
            Check(LobbyRules.Validate(twoVsTwo, flexible, false, out _) == LobbyResultCode.Ok,
                "fixed team format can start underfilled");
            flexible.Teams[1] = 0;
            Check(LobbyRules.Validate(twoVsTwo, flexible, false, out _) == LobbyResultCode.InvalidTeam,
                "team match still requires two occupied teams");
            for (int players = 2; players <= 8; players++)
            {
                MatchWorldProfile world = MatchWorldProfile.Resolve(players);
                Check(world.IsValid && world.EntityLayerPlayers == Math.Min(players, 4), "native entity layer bounded 2/3/4");
                Check(world.Resources == (players == 2 ? ResourceSpawnProfile.Low : players <= 4 ? ResourceSpawnProfile.Standard : ResourceSpawnProfile.High), "resource tier");
            }
        }

        private static void ClientStateChecks()
        {
            NetSession.StartPlayback();
            Check(!NetSession.SessionTimedOut, "playback has no network timeout");
            var state = new SessionStatePacket { AuthorityEpoch = 1, Policy = ServerSessionPolicy.Lobby,
                Phase = SessionPhase.Lobby, Revision = ushort.MaxValue, MatchId = 4, MaxPlayers = 8,
                OwnerSlot = 255, Match = new MatchDefinition { RoomKey = Rooms()[0], Mode = GameMode.Battle } };
            NetSession.ApplySessionState(state);
            state.Revision = 0; state.Phase = SessionPhase.Starting; state.MatchId++;
            state.StartCountdownMilliseconds = 3000;
            NetSession.ApplySessionState(state);
            Check(NetSession.IsStarting && NetSession.ServerSession?.MatchId == 5,
                "client accepts session revision wrap");
            Check(NetSession.StartCountdownRemainingSeconds > 2.5,
                "client turns countdown packet into a local deadline");
            state.Revision = ushort.MaxValue; state.Phase = SessionPhase.Lobby; state.MatchId--;
            NetSession.ApplySessionState(state);
            Check(NetSession.IsStarting && NetSession.ServerSession?.MatchId == 5,
                "delayed state cannot roll back a new match");
            NetMatchSync.Apply();
            Check(GameState.MatchTime == -1 && GameState.PointGoal == 0,
                "unlimited match uses the finite hidden-clock sentinel and no point goal");
            NetSession.Stop();
        }

        private sealed class Client : IDisposable
        {
            public NetTransport Transport = new(0);
            public readonly uint Id;
            public readonly IPEndPoint Server;
            public int Slot = -1;
            public SessionStatePacket? State;
            public RosterPacket Roster = RosterPacket.Create();
            public MatchStatePacket Match;
            public int OpenMapChoices;
            public bool Authority, Refused;
            public readonly List<ChatPacket> Chats = new();
            public readonly Dictionary<uint, LobbyCommandResultPacket> Results = new();
            private uint _command, _frame;
            public Client(int port, uint id, Guid token = default)
            {
                Id = id; Server = new IPEndPoint(IPAddress.Loopback, port);
                Transport.AnswerPingsImmediately(); Hello(token);
            }
            public void Hello(Guid token = default)
            {
                byte[] bytes = new byte[22]; bytes[0] = NetConfig.ProtocolVersion; bytes[1] = Slot < 0 ? (byte)255 : (byte)Slot;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), Id); token.TryWriteBytes(bytes.AsSpan(6));
                Send(PacketType.Hello, bytes);
            }
            public void Identify(byte hunter = 0)
            { byte[] bytes = new byte[8]; bytes[0] = hunter; NetText.Write(bytes.AsSpan(2), $"Test{Id}"); Send(PacketType.Identify, bytes); }
            public void Send(PacketType type, byte[] bytes) => Transport.Send(Server, type, bytes);
            public LobbyCommandPacket Command(LobbyCommandType type, bool ready = false, SessionStatePacket? config = null,
                byte target = 255, sbyte team = -1, ushort? revision = null)
            {
                var packet = new LobbyCommandPacket { CommandId = ++_command, ExpectedRevision = revision ?? State!.Value.Revision,
                    Type = type, Ready = ready, Configuration = config ?? State!.Value, TargetSlot = target, TeamIndex = team };
                Resend(packet); return packet;
            }
            public void Resend(LobbyCommandPacket command)
            { byte[] bytes = new byte[LobbyCommandPacket.Size]; command.Write(bytes); Send(PacketType.LobbyCommand, bytes); }
            public void Loaded(ushort? id = null)
            { byte[] bytes = new byte[2]; new MatchLoadedPacket(id ?? State!.Value.MatchId).Write(bytes); Send(PacketType.MatchLoaded, bytes); }
            public void ReadyResults()
            { var intent = new IntentPacket { Frame = ++_frame, Buttons = IntentButtons.ReadyState,
                MatchId = State!.Value.MatchId, AuthorityEpoch = State.Value.AuthorityEpoch,
                SlotGeneration = Roster.Generations[Array.IndexOf(Roster.Slots, (byte)Slot)] };
                byte[] bytes = new byte[IntentPacket.FullSize]; intent.Write(bytes); Send(PacketType.Intent, bytes); }
            public void EndMatch()
            { byte[] bytes = new byte[10]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, State!.Value.MatchId);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(2), State.Value.AuthorityEpoch);
                Send(PacketType.MatchEnd, bytes); }
            public void Drain()
            {
                foreach (var packet in Transport.Drain())
                {
                    if (packet.Type == PacketType.Welcome) Slot = packet.Payload[0];
                    if (packet.Type == PacketType.Authority) Authority = true;
                    if (packet.Type == PacketType.Refused) Refused = true;
                    if (packet.Type == PacketType.Chat && packet.Payload.Length == ChatPacket.Size) Chats.Add(ChatPacket.Read(packet.Payload));
                    if (packet.Type == PacketType.SessionState && SessionStatePacket.TryRead(packet.Payload, out var state)
                        && (State == null || state.Revision == State.Value.Revision || SessionStatePacket.IsNewer(state.Revision, State.Value.Revision))) State = state;
                    if (packet.Type == PacketType.Roster && RosterPacket.TryRead(packet.Payload, out var roster)
                        && (roster.Revision == Roster.Revision || NetLifecycleTracker.Newer(roster.Revision, Roster.Revision))) Roster = roster;
                    if (packet.Type == PacketType.LobbyCommandResult && LobbyCommandResultPacket.TryRead(packet.Payload, out var result)) Results[result.CommandId] = result;
                    if (packet.Type == PacketType.MatchState && packet.Payload.Length == MatchStatePacket.Size) Match = MatchStatePacket.Read(packet.Payload);
                    if (packet.Type == PacketType.MapChoices && packet.Payload.Length >= MapChoicesPacket.Size
                        && MapChoicesPacket.Read(packet.Payload).Open != 0) OpenMapChoices++;
                }
            }
            public void Rebind() { Transport.Dispose(); Transport = new NetTransport(0); Transport.AnswerPingsImmediately(); Hello(); }
            public void Dispose() { Send(PacketType.Bye, Array.Empty<byte>()); Transport.Dispose(); }
        }

        private sealed class Rig : IDisposable
        {
            public readonly DedicatedServer Server;
            public readonly List<Client> Clients = new();
            private readonly Thread _thread;
            private Exception? _error;
            public Rig(ServerSessionPolicy policy = ServerSessionPolicy.Lobby, Guid token = default)
            {
                Server = new DedicatedServer(0, 8, MapRotation.SingleMatch(Rooms()[0], GameMode.Battle, 0, 0))
                    { SessionPolicy = policy, OwnerToken = token, RunsTheMatch = false };
                _thread = new Thread(() => { try { Server.Run(); } catch (Exception ex) { _error = ex; } }) { IsBackground = true };
                _thread.Start(); Wait(() => Server.Listening, "server listening");
            }
            public Client Add(uint id, Guid token = default)
            {
                var client = new Client(Server.BoundPort, id, token); Clients.Add(client);
                Wait(() => client.Slot >= 0 && client.State != null, "client admitted"); client.Identify();
                Stable(); return client;
            }
            public void Stable() => Wait(() => Clients.Count > 0 && Clients.All(c => c.State?.Revision == Clients[0].State?.Revision
                && c.Roster.SessionRevision == c.State?.Revision && c.Roster.Count == Clients.Count
                && Enumerable.Range(0, c.Roster.Count).All(i => c.Roster.Names[i] == $"Test{Clients.Single(p => p.Slot == c.Roster.Slots[i]).Id}")), "roster and state converge");
            public void Wait(Func<bool> condition, string label, int ms = 4000)
            {
                var clock = Stopwatch.StartNew();
                do
                {
                    foreach (var client in Clients) client.Drain();
                    if (_error != null) throw _error;
                    if (condition()) { Check(true, label); return; }
                    Thread.Sleep(5);
                } while (clock.ElapsedMilliseconds < ms);
                throw new InvalidOperationException($"Timed out: {label}");
            }
            public LobbyCommandResultPacket Expect(Client client, LobbyCommandPacket command, LobbyResultCode expected)
            {
                // Deliberately duplicate every command and discard the first result before retrying.
                client.Resend(command);
                Wait(() => client.Results.ContainsKey(command.CommandId), "command answered");
                var first = client.Results[command.CommandId]; client.Results.Remove(command.CommandId); client.Resend(command);
                Wait(() => client.Results.ContainsKey(command.CommandId), "duplicate answered");
                var result = client.Results[command.CommandId];
                Check(first.CurrentRevision == result.CurrentRevision && first.ResultCode == result.ResultCode, "duplicate is idempotent");
                Check(result.ResultCode == expected, $"{command.Type}: expected {expected}, got {result.ResultCode}: {result.Reason}");
                Stable(); return result;
            }
            public void ReadyAll()
            { foreach (Client client in Clients) { Stable(); Expect(client, client.Command(LobbyCommandType.SetReady, true), LobbyResultCode.Ok); } }
            public void Dispose()
            { foreach (Client client in Clients) client.Dispose(); Server.Stop(); _thread.Join(5000); }
        }
        private static string[] Rooms() => Metadata.RoomMetadata.Where(p => p.Value.Multiplayer).Select(p => p.Key).Take(2).ToArray();

        private static void Scenario()
        {
            Guid token = Guid.NewGuid(); using var rig = new Rig(token: token);
            Client b = rig.Add(2); Check(b.State!.Value.OwnerSlot == 255, "first arrival cannot steal hosted ownership");
            Client a = rig.Add(1, token); Check(a.State!.Value.OwnerSlot == a.Slot, "creator claims owner token");
            var originalA = a.Transport; var originalB = b.Transport; int slotA = a.Slot, slotB = b.Slot;
            rig.Expect(b, b.Command(LobbyCommandType.StartMatch), LobbyResultCode.NotOwner);
            rig.Expect(a, a.Command(LobbyCommandType.StartMatch), LobbyResultCode.PlayersNotReady);
            rig.Expect(a, a.Command(LobbyCommandType.SetReady, true, revision: 0), LobbyResultCode.StaleRevision);
            rig.ReadyAll();
            var config = a.State.Value; config.Match = config.Match with
            {
                RoomKey = Rooms()[1], TimeLimitSeconds = 600, PointGoal = 25,
                HideOpponentHealth = true
            };
            rig.Expect(a, a.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            Check(a.Roster.LobbyReady.Take(a.Roster.Count).All(r => !r), "configuration clears ready");
            Check(a.State.Value.Match.TimeLimitSeconds == 600
                && a.State.Value.Match.PointGoal == 25
                && b.State!.Value.Match.TimeLimitSeconds == 600
                && b.State.Value.Match.PointGoal == 25,
                "time and point goal persist and synchronize to all clients");
            Check(a.State.Value.Match.HideOpponentHealth && b.State!.Value.Match.HideOpponentHealth,
                "owner hidden-health rule synchronizes to both UDP clients");
            var forbidden = b.State.Value;
            forbidden.Match = forbidden.Match with { HideOpponentHealth = false };
            forbidden.RuleFlags &= ~SessionRules.HideOpponentHealth;
            rig.Expect(b, b.Command(LobbyCommandType.UpdateMatch, config: forbidden), LobbyResultCode.NotOwner);
            Check(a.State.Value.Match.HideOpponentHealth, "non-owner cannot expose hidden health");
            var lobbyStatus = NetStatus.Query("127.0.0.1", rig.Server.BoundPort, allowJoinProbe: false);
            Check(lobbyStatus.Online && lobbyStatus.Phase == SessionPhase.Lobby && lobbyStatus.TimeRemaining == 600,
                "browser status clock stays at the full time limit in the lobby");
            rig.ReadyAll();
            var start = a.Command(LobbyCommandType.StartMatch); rig.Expect(a, start, LobbyResultCode.Ok);
            Check(a.State.Value.Phase == SessionPhase.Starting, "start enters barrier");
            a.Loaded((ushort)(a.State.Value.MatchId - 1));
            a.Loaded(); rig.Wait(() => a.State.Value.LoadedParticipants == (1 << a.Slot), "one participant loaded");
            Check(a.State.Value.Phase == SessionPhase.Starting, "one loaded cannot release barrier");
            var loadingStatus = NetStatus.Query("127.0.0.1", rig.Server.BoundPort, allowJoinProbe: false);
            Check(loadingStatus.Phase == SessionPhase.Starting && loadingStatus.TimeRemaining == 600,
                "browser status clock stays frozen through the load barrier");
            Client late = rig.Add(3); Check((late.State!.Value.ExpectedParticipants & (1 << late.Slot)) == 0, "late join excluded from barrier");
            b.Loaded();
            rig.Wait(() => a.State.Value.Phase == SessionPhase.Starting
                && a.State.Value.StartCountdownMilliseconds > 0,
                "loaded barrier publishes start countdown");
            Check(a.State.Value.StartCountdownMilliseconds <= 1500,
                "countdown remains bounded to one and a half seconds");
            rig.Wait(() => a.State.Value.Phase == SessionPhase.InMatch, "barrier released after countdown");
            b.EndMatch();
            rig.Wait(() => a.State.Value.Phase == SessionPhase.PostMatch, "results entered");
            Check(rig.Clients.All(c => c.OpenMapChoices == 0),
                "persistent lobby does not open a post-match map ballot");
            Check(a.Match.NextRoomKey.Length == 0,
                "persistent lobby results do not promise a next map");
            rig.Wait(() => a.State.Value.Phase == SessionPhase.Lobby,
                "results return directly to lobby without ready/vote input", 18000);
            rig.Stable();
            Check(ReferenceEquals(originalA, a.Transport) && ReferenceEquals(originalB, b.Transport)
                && a.Slot == slotA && b.Slot == slotB, "same UDP transports and slots across rounds");
            Check(a.Roster.LobbyReady.Take(a.Roster.Count).All(r => !r), "return clears lobby ready");
            ushort firstMatch = a.State.Value.MatchId;
            rig.ReadyAll(); rig.Expect(a, a.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            foreach (var client in rig.Clients) client.Loaded();
            rig.Wait(() => a.State.Value.Phase == SessionPhase.InMatch, "second round starts");
            Check(a.State.Value.MatchId != firstMatch, "new match id on same map");
            a.Dispose(); rig.Clients.Remove(a);
            rig.Wait(() => b.State!.Value.OwnerSlot == b.Slot, "oldest peer becomes owner");
            b.Rebind(); rig.Stable(); Check(b.Slot == slotB && b.State.Value.OwnerSlot == slotB, "owner rebind keeps identity and slot");
        }

        private static void ReadyOptionalScenario()
        {
            using var rig = new Rig();
            Client owner = rig.Add(130);
            Client other = rig.Add(131);

            var config = owner.State!.Value;
            config.RuleFlags &= ~SessionRules.RequireReady;
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config),
                LobbyResultCode.Ok);
            Check(owner.State!.Value.RequireReady == false
                && other.State!.Value.RequireReady == false,
                "ready-disabled rule synchronizes to every client");
            Check(owner.Roster.LobbyReady.Take(owner.Roster.Count).All(ready => !ready),
                "ready-disabled match starts from an entirely unready roster");

            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            Check(owner.State.Value.Phase == SessionPhase.Starting,
                "ready-disabled start enters the same load barrier");
            foreach (Client client in rig.Clients)
                client.Loaded();
            rig.Wait(() => owner.State.Value.Phase == SessionPhase.InMatch,
                "ready-disabled match starts without any ready commands");
        }

        private static void AbandonedLobbyScenario()
        {
            using var rig = new Rig();
            Client owner = rig.Add(100);
            Client other = rig.Add(101);
            rig.ReadyAll();
            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            foreach (Client client in rig.Clients) client.Loaded();
            rig.Wait(() => owner.State!.Value.Phase == SessionPhase.InMatch,
                "abandonment scenario enters match");

            owner.Dispose();
            other.Dispose();
            rig.Clients.Clear();
            rig.Wait(() => rig.Server.PeerCount == 0,
                "all abandoned-match peers removed");

            Client fresh = rig.Add(102);
            Check(fresh.State!.Value.Phase == SessionPhase.Lobby,
                "first player after abandonment lands in a clean lobby");
            Check(fresh.State.Value.ExpectedParticipants == 0
                && fresh.State.Value.LoadedParticipants == 0,
                "abandoned load barrier state is cleared");
        }

        private static void HostedOwnerDepartureScenario()
        {
            Guid token = Guid.NewGuid();
            using var rig = new Rig(token: token);
            Client owner = rig.Add(110, token);
            Client successor = rig.Add(111);
            owner.Dispose();
            rig.Clients.Remove(owner);
            rig.Wait(() => successor.State!.Value.OwnerSlot == successor.Slot,
                "hosted owner departure promotes successor");

            LobbyCommandPacket close = successor.Command(LobbyCommandType.CloseLobby);
            rig.Wait(() => successor.Results.TryGetValue(close.CommandId, out var result)
                && result.ResultCode == LobbyResultCode.Ok,
                "successor can close inherited hosted lobby");
            rig.Wait(() => !rig.Server.Listening,
                "inherited hosted lobby process stops on close");
        }

        private static void HostedOwnerTransferScenario()
        {
            Guid token = Guid.NewGuid();
            using var rig = new Rig(token: token);
            Client owner = rig.Add(120, token);
            Client successor = rig.Add(121);
            rig.Expect(owner, owner.Command(LobbyCommandType.TransferOwner,
                target: (byte)successor.Slot), LobbyResultCode.Ok);
            Check(successor.State!.Value.OwnerSlot == successor.Slot,
                "explicit transfer moves lobby ownership");

            LobbyCommandPacket close = successor.Command(LobbyCommandType.CloseLobby);
            rig.Wait(() => successor.Results.TryGetValue(close.CommandId, out var result)
                && result.ResultCode == LobbyResultCode.Ok,
                "transferred owner can close hosted lobby");
            rig.Wait(() => !rig.Server.Listening,
                "transferred hosted lobby process stops on close");
        }

        private static void TeamScenario()
        {
            using var rig = new Rig(); Client owner = rig.Add(10); Client other = rig.Add(11);
            var config = owner.State!.Value;
            config.Match = config.Match with { Mode = GameMode.BattleTeams, Format = MatchFormat.TwoVsTwo };
            config.RuleFlags &= ~SessionRules.RequireReady;
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            Check(LobbyRules.Validate(config.Match, owner.Roster, false, out _) == LobbyResultCode.Ok,
                "2v2 start is valid with one player on each side");
            rig.Add(12); rig.Add(13);
            Check(owner.Roster.Teams.Take(4).SequenceEqual(new sbyte[] { 0, 1, 0, 1 }), "deterministic 2v2 assignment");
            rig.Expect(owner, owner.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: 0), LobbyResultCode.TeamFull);
            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            Client[] leavingTeam = rig.Clients
                .Where(c => owner.Roster.Teams[c.Slot] == 1).ToArray();
            foreach (Client leaving in leavingTeam)
            {
                leaving.Dispose();
                rig.Clients.Remove(leaving);
            }
            rig.Wait(() => owner.State.Value.Phase == SessionPhase.Lobby,
                "start cancels only when a team becomes empty during load");
            config = owner.State.Value; config.Match = config.Match with { Format = MatchFormat.FourVsFour };
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            for (uint id = 14; rig.Clients.Count < 8; id++) rig.Add(id);
            Check(owner.Roster.Teams.Take(8).Count(t => t == 0) == 4 && owner.Roster.Teams.Take(8).Count(t => t == 1) == 4, "4v4 assignment");
            config = owner.State.Value; config.Match = config.Match with { Format = MatchFormat.TwoVsTwoVsTwoVsTwo };
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            rig.Wait(() => owner.State.Value.Phase == SessionPhase.InMatch,
                "load timeout releases barrier after countdown", 22000);
        }

        private static void CustomScenario()
        {
            using var rig = new Rig(); Client owner = rig.Add(50); Client other = rig.Add(51);
            var config = owner.State!.Value;
            config.Match = config.Match with { Mode = GameMode.BattleTeams, Format = MatchFormat.Custom, CustomTeams = new TeamLayout(2, 4, 2) };
            config.RuleFlags &= ~SessionRules.RequireReady;
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            rig.Expect(other, other.Command(LobbyCommandType.SetTeam, target: (byte)owner.Slot, team: 1), LobbyResultCode.NotOwner);
            rig.Expect(other, other.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: 0), LobbyResultCode.Ok);
            rig.Expect(other, other.Command(LobbyCommandType.SetReady, ready: true), LobbyResultCode.Ok);
            rig.Expect(owner, owner.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: -1), LobbyResultCode.Ok);
            Check(!owner.Roster.LobbyReady[other.Slot], "team move clears target ready");
            config = owner.State.Value; config.RuleFlags |= SessionRules.LockTeams;
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            rig.Expect(other, other.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: 0), LobbyResultCode.NotOwner);
            rig.Expect(owner, owner.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: -1), LobbyResultCode.Ok);
            rig.Expect(owner, owner.Command(LobbyCommandType.SetTeam, target: (byte)other.Slot, team: 3), LobbyResultCode.InvalidTeam);
            for (uint id = 52; rig.Clients.Count < 6; id++) rig.Add(id);
            Check(owner.Roster.Teams.Take(6).Count(t => t == 0) == 4 && owner.Roster.Teams.Take(6).Count(t => t == 1) == 2, "custom 4v2 fills asymmetrically");
            config = owner.State.Value; config.Match = config.Match with { CustomTeams = new TeamLayout(2, 2, 2) };
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.InvalidConfiguration);
            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            Check(owner.State.Value.WorldProfile == MatchWorldProfile.Resolve(6), "world profile frozen with configured layout");
            Client leaving = rig.Clients[^1]; leaving.Dispose(); rig.Clients.Remove(leaving);
            rig.Stable();
            Check(owner.State.Value.Phase == SessionPhase.Starting,
                "underfilled custom match keeps loading while both teams remain occupied");
            foreach (Client client in rig.Clients) client.Loaded();
            rig.Wait(() => owner.State.Value.Phase == SessionPhase.InMatch, "underfilled 4v2 starts after load");
            leaving = rig.Clients.First(c => c != owner && owner.Roster.Teams[c.Slot] == 0);
            leaving.Dispose(); rig.Clients.Remove(leaving); rig.Stable();
            Client late = rig.Add(61);
            Check(owner.Roster.Teams[Array.IndexOf(owner.Roster.Slots, (byte)late.Slot, 0, owner.Roster.Count)] == 0, "JIP fills only A vacancy");
            Check(late.State!.Value.WorldProfile == MatchWorldProfile.Resolve(6), "JIP retains frozen world");
            using var overflow = new Client(rig.Server.BoundPort, 62);
            rig.Wait(() => { overflow.Drain(); return overflow.Refused; }, "full custom layout rejects late join below physical player cap");
            Check(overflow.Slot < 0, "overflow never activated");
        }

        private static void FourTeamScenario()
        {
            using var rig = new Rig(); Client owner = rig.Add(70);
            var config = owner.State!.Value;
            config.Match = config.Match with { Mode = GameMode.BattleTeams, Format = MatchFormat.TwoVsTwoVsTwoVsTwo };
            config.RuleFlags &= ~SessionRules.RequireReady;
            rig.Expect(owner, owner.Command(LobbyCommandType.UpdateMatch, config: config), LobbyResultCode.Ok);
            for (int slot = 1; slot < 8; slot++)
            {
                Client added = rig.Add((uint)(70 + slot));
                if (slot >= 4 && slot <= 6)
                    rig.Expect(owner, owner.Command(LobbyCommandType.SetTeam, target: (byte)added.Slot, team: (sbyte)(7 - slot)), LobbyResultCode.Ok);
            }
            Check(owner.Roster.Teams.Take(8).SequenceEqual(new sbyte[] { 0, 1, 2, 3, 3, 2, 1, 0 }), "non-parity four-team roster");
            foreach (Client client in rig.Clients) client.Chats.Clear();
            byte[] chat = new byte[ChatPacket.Size];
            new ChatPacket { Kind = ChatPacket.KindTeam, Text = "A only", Name = "untrusted" }.Write(chat);
            owner.Send(PacketType.Chat, chat);
            rig.Wait(() => rig.Clients[7].Chats.Any(c => c.Text == "A only"), "team chat reaches non-parity ally");
            Check(rig.Clients.Skip(1).Take(6).All(c => c.Chats.All(chat => chat.Text != "A only")), "team chat excluded opposing teams");
            Client rebound = rig.Clients[5]; ushort beforeRebind = owner.State!.Value.Revision; rebound.Rebind();
            rig.Wait(() => owner.State!.Value.Revision != beforeRebind, "rebind advances roster revision"); rig.Stable();
            Check(owner.Roster.Teams[5] == 2, "reconnect preserves explicit team");
            rig.Expect(owner, owner.Command(LobbyCommandType.StartMatch), LobbyResultCode.Ok);
            foreach (Client client in rig.Clients) client.Loaded();
            rig.Wait(() => owner.State.Value.Phase == SessionPhase.InMatch, "four-team barrier starts");
        }

        private static void ContinuousScenario()
        {
            using var rig = new Rig(ServerSessionPolicy.Continuous); Client client = rig.Add(20);
            Check(client.State!.Value.Phase == SessionPhase.InMatch, "continuous starts in match");
            ushort match = client.State.Value.MatchId;
            client.EndMatch();
            rig.Wait(() => client.State.Value.Phase == SessionPhase.PostMatch, "continuous results"); client.ReadyResults();
            rig.Wait(() => client.State.Value.Phase == SessionPhase.InMatch && client.State.Value.MatchId != match, "continuous rotates automatically", 18000);
        }

        private static void ClientSessionScenario()
        {
            NetLag.Configure("80:20");
            using var rig = new Rig();
            Check(NetLaunch.Connect("127.0.0.1", rig.Server.BoundPort, "RealClient", Hunter.Samus), "NetSession connects to an idle lobby");
            int port = NetSession.ConnectionPort, slot = NetSession.LocalSlot;
            uint clientId = NetSession.ClientId;
            void PumpUntil(Func<bool> condition, string message, int timeout = 5000)
            {
                rig.Wait(() => { NetSession.Pump(); return condition(); }, message, timeout);
            }
            // Admission and Identify are separate exchanges. Matching revisions
            // can describe the initial unnamed roster; wait for the acknowledged
            // identity before freezing the revision and dropping retry traffic.
            PumpUntil(() => NetSession.LocalIsLobbyOwner
                && GameState.Nicknames[slot] == "RealClient"
                && NetSession.LobbyRoster().SessionRevision == NetSession.SessionRevision,
                "real client owns a consistent lobby");
            Check(NetSession.IsInLobby && !NetSession.ShouldLoadMatch, "connection does not require a running match");
            // Lose the first command and its first retry entirely. The same command ID must recover.
            NetLag.ConfigureLoss("100");
            Check(NetSession.SendLobbyCommand(LobbyCommandType.SetReady, ready: true), "enqueue ready");
            var loss = Stopwatch.StartNew();
            while (loss.ElapsedMilliseconds < 400) { NetSession.Pump(); Thread.Sleep(10); }
            Check(NetSession.LobbyCommandPending, "lost command remains pending");
            NetLag.ConfigureLoss("0");
            PumpUntil(() => !NetSession.LobbyCommandPending && NetSession.SlotLobbyReady[slot], "retransmission recovers lost ready");
            PumpUntil(() => NetSession.LobbyRoster().SessionRevision == NetSession.SessionRevision, "ready state converged");
            Check(NetSession.SendLobbyCommand(LobbyCommandType.StartMatch), "real client starts");
            PumpUntil(() => NetSession.IsStarting && !NetSession.LobbyCommandPending, "real client load barrier");
            Check(NetSession.FreezeGameplay, "gameplay frozen before loaded");
            Check(NetSession.IsStarting && NetSession.ConnectionPort == port,
                "lobby connection survives the load barrier");
            NetSession.MarkMatchLoaded();
            PumpUntil(() => NetSession.StartCountdownRemainingSeconds > 0,
                "real load ack starts countdown");
            Check(NetSession.IsStarting, "real client stays frozen during countdown");
            PumpUntil(() => NetSession.IsPlaying, "real load ack starts match");
            Check(!NetSession.FreezeGameplay, "gameplay released after barrier");
            NetSession.SendMatchEnd();
            PumpUntil(() => NetSession.IsPostMatch, "real client results");
            PumpUntil(() => NetSession.IsInLobby,
                "real client returns to lobby without post-match input", 18000);
            NetSession.ResetMatchState();
            Check(NetSession.Active && NetSession.ConnectionPort == port && NetSession.LocalSlot == slot
                && NetSession.ClientId == clientId && NetSession.LocalIsLobbyOwner, "real client socket/slot/id/owner survive match teardown");
            PumpUntil(() => NetSession.LobbyRoster().SessionRevision == NetSession.SessionRevision, "next lobby consistent");
            Check(NetSession.SendLobbyCommand(LobbyCommandType.SetReady, ready: true), "ready for second real-client match");
            PumpUntil(() => !NetSession.LobbyCommandPending && NetSession.SlotLobbyReady[slot], "second ready received");
            Check(NetSession.SendLobbyCommand(LobbyCommandType.StartMatch), "second real-client start");
            PumpUntil(() => NetSession.IsStarting, "second real-client load barrier");
            NetSession.MarkMatchLoaded(); PumpUntil(() => NetSession.IsPlaying, "second real-client round");
            Check(NetSession.ConnectionPort == port && NetSession.LocalSlot == slot, "same client UDP session in second match");
            NetSession.Stop();
            NetLag.Configure("0");
        }
    }
}
