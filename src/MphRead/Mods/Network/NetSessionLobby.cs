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
        public static bool CanEditLobby => IsInLobby && LocalIsLobbyOwner;
        public static bool PersistentLobby => ServerSession?.Policy == ServerSessionPolicy.Lobby;
        public static bool FreezeGameplay => PersistentLobby && SessionPhase is SessionPhase.Lobby or SessionPhase.Starting;
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
        private static ushort _rosterSessionRevision;
        private static double _lastLoadAck, _lastIdentity;
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
            if (_loadedMatch == ServerSession?.MatchId && IsStarting && now - _lastLoadAck >= 0.25)
                MarkMatchLoaded();
            // Identity updates are also eventually reliable, without a second identity protocol.
            if (now - _lastIdentity >= 1)
            { _lastIdentity = now; SendIdentify(); }
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
            if (state.Policy == ServerSessionPolicy.Lobby && (newMatch || (state.Phase == SessionPhase.Lobby && !IsInLobby)))
                ResetMatchState();
            ServerSession = state;
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
            if (newMatch) _loadedMatch = null;
        }

        private static void ApplyLobbyResult(LobbyCommandResultPacket result)
        {
            if (!_pendingLobby.Remove(result.CommandId)) return;
            LobbyMessage = result.ResultCode == LobbyResultCode.Ok ? "" : result.Reason;
        }

        public static void MarkMatchLoaded()
        {
            if (!PersistentLobby || ServerSession == null || _hostEndPoint == null) return;
            if (_loadedMatch == ServerSession.Value.MatchId && Clock - _lastLoadAck < 0.25) return;
            _loadedMatch = ServerSession.Value.MatchId;
            _lastLoadAck = Clock;
            new MatchLoadedPacket(_loadedMatch.Value).Write(_scratch);
            _transport?.Send(_hostEndPoint, PacketType.MatchLoaded, _scratch.AsSpan(0, MatchLoadedPacket.Size));
        }

        public static void ReportMatchLoadFailed(string reason)
        {
            if (ServerSession == null || _hostEndPoint == null) return;
            new MatchLoadFailedPacket(ServerSession.Value.MatchId, reason).Write(_scratch);
            _transport?.Send(_hostEndPoint, PacketType.MatchLoadFailed, _scratch.AsSpan(0, MatchLoadFailedPacket.Size));
        }

        // The socket, local slot, identity, authoritative roster and lobby state survive this reset.
        public static void ResetMatchState()
        {
            NetHealthSync.BeginRoom();
            NetPlayerSetup.Reset(); SpectatorMode.Reset(); NetMatchSync.Reset();
            NetSlotManager.Reset(); NetDamage.Reset(); NetRoomChange.Reset(); NetMatchEnd.Reset();
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
            ServerSession = null; _pendingLobby.Clear(); _loadedMatch = null;
            _rosterRevision = 0; _hasRoster = false; _ownerToken = Guid.Empty;
            _rosterSessionRevision = 0;
            LobbyMessage = ""; _lastLoadAck = _lastIdentity = 0;
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
