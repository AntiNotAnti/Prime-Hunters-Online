using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    public static partial class NetSession
    {
        public static SessionStatePacket? ServerSession { get; private set; }
        public static SessionPhase SessionPhase => ServerSession?.Phase ?? SessionPhase.InMatch;
        public static ushort SessionRevision => ServerSession?.Revision ?? 0;
        public static MatchDefinition? ActiveMatchDefinition => ServerSession?.Match;
        public static readonly sbyte[] SlotTeamIndex = new sbyte[PlayerEntity.SlotCapacity];
        public static readonly bool[] SlotLobbyReady = new bool[PlayerEntity.SlotCapacity];
        public static bool LocalIsLobbyOwner => LocalSlot >= 0 && ServerSession?.OwnerSlot == LocalSlot;
        public static bool IsInLobby => SessionPhase == SessionPhase.Lobby;
        public static bool IsStarting => SessionPhase == SessionPhase.Starting;
        public static bool IsPlaying => SessionPhase == SessionPhase.InMatch;
        public static bool IsPostMatch => SessionPhase == SessionPhase.PostMatch;
        public static double StartCountdownRemainingSeconds =>
            IsStarting && _startCountdownEndsAt > 0 ? Math.Max(0, _startCountdownEndsAt - Clock) : 0;
        public static bool StartReleaseReached => ServerSession is { } session
            && session.Phase == SessionPhase.Starting && session.StartStage == StartStage.Countdown
            && NetMatchStart.ClientReleaseReady(session.StartStage, _startCountdownEndsAt, Clock);
        public static bool CanEditLobby => IsInLobby && LocalIsLobbyOwner;
        public static bool PersistentLobby => ServerSession?.Policy == ServerSessionPolicy.Lobby;
        // Loading stays frozen, but the countdown is a commitment made ahead of
        // time. Release against that local deadline instead of waiting for the
        // InMatch datagram to reach every client at a different instant.
        public static bool FreezeGameplay => IsInLobby || (IsStarting && !StartReleaseReached);
        public static bool ShouldLoadMatch => ServerSession is { } session
            && (session.Phase == SessionPhase.InMatch || (session.Phase == SessionPhase.Starting
                && LocalSlot >= 0 && (session.ExpectedParticipants & (1 << LocalSlot)) != 0));
        public static string LobbyMessage { get; private set; } = "";
        public static bool LobbyCommandPending => _pendingLobby.Count != 0;
        internal static int ConnectionPort => _transport?.LocalPort ?? -1;
        public static bool SessionTimedOut => IsClient && !DemoPlayback.IsActive && _hostEndPoint != null
            && Clock - _lastServerPacket > NetConfig.TimeoutSeconds;
        public static double Clock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private static Guid _ownerToken;
        private static uint _nextCommandId;
        private static ushort? _loadedMatch;
        private static MatchStartIdentity? _loadedStart;
        private static (ushort MatchId, ulong AuthorityEpoch)? _pendingLoadedScene;
        private static ushort _rosterSessionRevision;
        private static MatchLoadStage _loadStage;
        private static double _lastLoadAck, _lastLoadProgress, _lastIdentity, _startCountdownEndsAt;
        private sealed class PendingLobbyCommand
        {
            public LobbyCommandPacket Packet;
            public double SentAt;
            public int Attempts;
        }
        private static readonly Dictionary<uint, PendingLobbyCommand> _pendingLobby = new();

        public static void Pump(double time = 0) => Update(time);

        public static bool SendLobbyCommand(LobbyCommandType type, byte targetSlot = 255,
            sbyte team = -1, bool ready = false, SessionStatePacket? configuration = null)
        {
            if (!Active || ServerSession == null || _pendingLobby.Count != 0) return false;
            uint id = ++_nextCommandId;
            if (id == 0) id = ++_nextCommandId;
            var command = new LobbyCommandPacket { CommandId = id, ExpectedRevision = SessionRevision,
                Type = type, TargetSlot = targetSlot, TeamIndex = team, Ready = ready,
                Configuration = configuration ?? ServerSession.Value };
            var pending = new PendingLobbyCommand { Packet = command, SentAt = Clock, Attempts = 0 };
            _pendingLobby.Add(id, pending);
            LobbyMessage = "Waiting for server...";
            SendLobbyPacket(command);
            return true;
        }

        private static void SendLobbyPacket(LobbyCommandPacket command)
        {
            command.Write(_scratch);
            if (_hostEndPoint != null) _transport?.Send(_hostEndPoint, PacketType.LobbyCommand,
                _scratch.AsSpan(0, LobbyCommandPacket.Size));
        }

        private static void PumpLobby(double now)
        {
            foreach (var pair in _pendingLobby)
            {
                var pending = pair.Value;
                if (now - pending.SentAt < Math.Min(1, 0.25 * (pending.Attempts + 1))) continue;
                if (pending.Attempts >= 4)
                {
                    LobbyMessage = "The server did not acknowledge the command. Check the current lobby and try again.";
                    _pendingLobby.Remove(pair.Key);
                    break;
                }
                pending.Attempts++; pending.SentAt = now;
                SendLobbyPacket(pending.Packet);
            }

            // Keep the most recent load stage alive while the scene is already built
            // and waiting at the barrier. Synchronous loading reports transitions
            // directly; once Pump is running again this is a cheap heartbeat.
            if (IsStarting && _loadStage != MatchLoadStage.None && now - _lastLoadProgress >= 1)
                SendMatchLoadProgress(_loadStage);

            // Identity updates are also eventually reliable, without a second identity protocol.
            if (now - _lastIdentity >= 1)
            {
                _lastIdentity = now; SendIdentify();
                if (_hostEndPoint != null && ServerSession is { } session)
                {
                    new PeerTimingPacket(session.MatchId, session.AuthorityEpoch, (float)NetSmoothing.Delay).Write(_scratch);
                    _transport?.Send(_hostEndPoint, PacketType.PeerTiming, _scratch.AsSpan(0, PeerTimingPacket.Size));
                }
            }
        }

        internal static void ApplySessionState(SessionStatePacket state)
        {
            // Match control can arrive before its session packet. Check that
            // stream too, before a stale lobby packet resets the running world.
            if (state.MatchId == 0 || state.AuthorityEpoch == 0) return;
            if (ServerMatch is { } match
                && (state.AuthorityEpoch < match.AuthorityEpoch
                    || (state.AuthorityEpoch == match.AuthorityEpoch && state.MatchId != match.MatchId
                        && !NetLifecycleTracker.Newer(state.MatchId, match.MatchId)))) return;
            if (ServerSession is { } old)
            {
                if (state.AuthorityEpoch != old.AuthorityEpoch
                    && !NetLifecycleTracker.Newer(state.AuthorityEpoch, old.AuthorityEpoch)) return;
                if (state.AuthorityEpoch == old.AuthorityEpoch && state.Revision != old.Revision
                    && !SessionStatePacket.IsNewer(state.Revision, old.Revision)) return;
            }
            bool newMatch = ServerSession?.MatchId != state.MatchId;
            bool returningToLobby = state.Phase == SessionPhase.Lobby && !IsInLobby;
            var pendingScene = _pendingLoadedScene;
            if (newMatch || returningToLobby)
            {
                // A direct PostMatch -> Starting transition keeps the scene alive long
                // enough for NetRoomChange to compare the old loaded match id with the
                // new one. Clearing that marker here makes a same-map rematch look like
                // a first join, so the room is never rebuilt and the player stays in
                // the old round's ended/spawn state.
                ResetMatchState(preserveRoomChange: newMatch && state.Phase != SessionPhase.Lobby);
            }
            ServerSession = state;
            ReplayCapture.AcceptedConfiguration(state);
            if (state.Policy == ServerSessionPolicy.Lobby && state.Phase == SessionPhase.Lobby)
            {
                Mods.RoomPrewarm.Begin(state.Match.RoomKey);
            }
            if (state.Phase == SessionPhase.Starting && state.StartStage == StartStage.Countdown
                && state.StartCountdownMilliseconds > 0)
                ArmStartCountdown(state.StartCountdownMilliseconds);
            else if (state.Phase != SessionPhase.Starting || state.StartStage != StartStage.Countdown)
                _startCountdownEndsAt = 0;
            if (ServerMatch == null || ServerMatch.Value.MatchId != state.MatchId
                || ServerMatch.Value.AuthorityEpoch != state.AuthorityEpoch)
            {
                ApplyMatchState(new MatchStatePacket { RoomKey = state.Match.RoomKey, Mode = (byte)state.Match.Mode,
                    AuthorityEpoch = state.AuthorityEpoch,
                    PointGoal = state.Match.PointGoal, TimeRemaining = state.Match.TimeLimitSeconds, MatchId = state.MatchId,
                    Flags = (byte)(MatchStatePacket.FlagInProgress | (state.Match.FriendlyFire ? MatchStatePacket.FlagFriendlyFire : 0)
                        | (state.Match.ShadowFreeze ? 0 : MatchStatePacket.FlagNoShadowFreeze)
                        | MatchStatePacket.RuleFlags(1, state.Match.AffinityWeapons)) }, rotated: false);
            }
            if (newMatch || returningToLobby || _loadedStart?.StartGeneration != state.StartGeneration)
            {
                _loadedMatch = null; _loadedStart = null; _loadStage = MatchLoadStage.None;
                _lastLoadProgress = 0;
            }
            if (state.Phase == SessionPhase.Starting && LocalSlot >= 0
                && (state.ExpectedParticipants & (1 << LocalSlot)) != 0)
                ReportMatchLoadProgress(MatchLoadStage.StartReceived);
            if (pendingScene is { } scene && scene.MatchId == state.MatchId
                && scene.AuthorityEpoch == state.AuthorityEpoch
                && state.Phase is SessionPhase.Starting or SessionPhase.InMatch)
            {
                _pendingLoadedScene = null;
                MarkMatchLoaded();
            }
        }

        private static void ApplyLobbyResult(LobbyCommandResultPacket result)
        {
            if (!_pendingLobby.Remove(result.CommandId)) return;
            LobbyMessage = result.ResultCode == LobbyResultCode.Ok ? "" : result.Reason;
        }

        private static void ArmStartCountdown(ushort remainingMilliseconds)
        {
            NetConnectionSnapshot? connection = _hostEndPoint == null
                ? null : _transport?.ConnectionStats(_hostEndPoint);
            double fallbackRtt = LocalSlot >= 0 && LocalSlot < SlotPing.Length
                ? SlotPing[LocalSlot] : 0;
            // Minimum RTT is the best estimate of propagation time; using the
            // smoothed RTT here can subtract a jitter spike and release early.
            double oneWay = Math.Clamp((connection?.MinimumRttMilliseconds ?? fallbackRtt) / 2000.0,
                0, 0.15);
            double jitterSafety = Math.Clamp(
                (connection?.RttJitterMilliseconds ?? 0) / 1000.0 + 0.02, 0.03, 0.08);
            double target = Clock + Math.Max(0,
                remainingMilliseconds / 1000.0 - oneWay + jitterSafety);
            // A delayed reliable SessionState can carry an older remaining value.
            // Fresh 10 Hz commit packets may move the estimate earlier, never later.
            if (_startCountdownEndsAt <= 0 || target < _startCountdownEndsAt)
                _startCountdownEndsAt = target;
        }

        internal static void ApplyStartCommit(MatchStartCommitPacket commit)
        {
            if (ServerSession is not { } state || state.Phase != SessionPhase.Starting
                || state.StartStage != StartStage.Countdown || commit.Identity != new MatchStartIdentity(
                    state.MatchId, state.AuthorityEpoch, state.StartGeneration)
                || LocalSlot < 0 || (state.ExpectedParticipants & (1 << LocalSlot)) == 0)
                return;
            ArmStartCountdown(commit.RemainingMilliseconds);
        }

        public static void ReportMatchLoadProgress(MatchLoadStage stage)
        {
            if (stage == MatchLoadStage.None || ServerSession is not { } state
                || state.Phase != SessionPhase.Starting || _hostEndPoint == null || LocalSlot < 0
                || (state.ExpectedParticipants & (1 << LocalSlot)) == 0 || stage <= _loadStage)
                return;
            _loadStage = stage;
            SendMatchLoadProgress(stage);
        }

        private static void SendMatchLoadProgress(MatchLoadStage stage)
        {
            if (ServerSession is not { } state || _hostEndPoint == null) return;
            _lastLoadProgress = Clock;
            new MatchLoadProgressPacket(state.MatchId, state.AuthorityEpoch,
                state.StartGeneration, stage).Write(_scratch);
            _transport?.Send(_hostEndPoint, PacketType.MatchLoadProgress,
                _scratch.AsSpan(0, MatchLoadProgressPacket.Size));
        }

        public static void MarkMatchLoaded()
        {
            ReportMatchLoadProgress(MatchLoadStage.SceneReady);
            if (_hostEndPoint == null) return;
            // A late join can finish its scene after MatchState but before the
            // reliable SessionState carrying the start generation arrives.
            if (ServerSession == null)
            {
                if (ServerMatch is { } match)
                    _pendingLoadedScene = (match.MatchId, match.AuthorityEpoch);
                return;
            }
            var state = ServerSession.Value;
            if (state.Phase is not (SessionPhase.Starting or SessionPhase.InMatch)) return;
            var identity = new MatchStartIdentity(state.MatchId, state.AuthorityEpoch, state.StartGeneration);
            if (_loadedStart == identity) return;
            _loadedStart = identity; _loadedMatch = state.MatchId;
            _lastLoadAck = Clock;
            new MatchLoadedPacket(state.MatchId, state.AuthorityEpoch, state.StartGeneration).Write(_scratch);
            _transport?.Send(_hostEndPoint, PacketType.MatchLoaded, _scratch.AsSpan(0, MatchLoadedPacket.Size));
        }

        public static void ReportMatchLoadFailed(string reason)
        {
            if (ServerSession == null || _hostEndPoint == null) return;
            new MatchLoadFailedPacket(ServerSession.Value.MatchId, reason, ServerSession.Value.AuthorityEpoch, ServerSession.Value.StartGeneration).Write(_scratch);
            _transport?.Send(_hostEndPoint, PacketType.MatchLoadFailed, _scratch.AsSpan(0, MatchLoadFailedPacket.Size));
        }

        // The socket, local slot, identity, authoritative roster and lobby state survive this reset.
        public static void ResetMatchState(bool preserveRoomChange = false)
        {
            _pendingLoadedScene = null;
            NetTelemetry.NewMatch();
            NetHealthSync.BeginRoom();
            NetPlayerSetup.Reset(); SpectatorMode.Reset(); NetMatchSync.Reset();
            NetSlotManager.Reset(); NetDamage.Reset(resetSessionTotals: false);
            if (!preserveRoomChange) NetRoomChange.Reset();
            NetMatchEnd.Reset();
            NetPlayerBridge.Reset(); NetUnlagged.Reset(); NetHitPrediction.Reset();
            NetHitClaims.Reset(); NetSmoothing.Reset();
            Array.Clear(RemoteStateValid); Array.Clear(RemoteIntentValid);
            Array.Clear(RemoteIntentArrived); Array.Clear(_lastSlotIntentFrame);
            _lastSnapshotFrame = 0; SnapshotArrived = 0; AppliedSnapshotFrame = 0;
            _hasSnapshot = false; ContinuousPhase.Reset();
            NetPlayerLifecycle.ResetLives();
        }

        private static void ResetLobbySession()
        {
            ServerSession = null; _pendingLobby.Clear(); _loadedMatch = null; _loadedStart = null;
            _pendingLoadedScene = null;
            _rosterRevision = 0; _hasRoster = false; _ownerToken = Guid.Empty;
            _rosterSessionRevision = 0;
            LobbyMessage = ""; _loadStage = MatchLoadStage.None;
            _lastLoadAck = _lastLoadProgress = _lastIdentity = _startCountdownEndsAt = 0;
            Mods.RoomPrewarm.Clear();
            Array.Fill(SlotTeamIndex, (sbyte)-1); Array.Clear(SlotLobbyReady);
            Chat.NetChat.Clear();
        }

        public static RosterPacket LobbyRoster()
        {
            var roster = RosterPacket.Create();
            roster.Revision = _rosterRevision;
            roster.SessionRevision = _rosterSessionRevision;
            roster.MatchId = CurrentMatchId;
            roster.AuthorityEpoch = AuthorityEpoch;
            for (int slot = 0; slot < SlotOccupied.Length; slot++)
            {
                if (!SlotOccupied[slot]) continue;
                int at = roster.Count++;
                roster.Slots[at] = (byte)slot; roster.Teams[at] = SlotTeamIndex[slot];
                roster.Generations[at] = NetPlayerLifecycle.Generation(slot);
                roster.LobbyReady[at] = SlotLobbyReady[slot]; roster.Names[at] = GameState.Nicknames[slot];
                roster.Hunters[at] = (byte)SlotHunter[slot]; roster.Colors[at] = (byte)PlayerColors.Choice[slot];
                roster.Pings[at] = (ushort)SlotPing[slot];
            }
            return roster;
        }
    }
}
