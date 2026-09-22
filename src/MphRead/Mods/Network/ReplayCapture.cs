using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network
{
    internal static class ReplayCapture
    {
        private static readonly byte[] Snapshot = new byte[NetConfig.MaxPacketSize];
        private static int _snapshotLength;
        private static string? _room;
        private static ulong _mapHash;
        private static readonly PlayerState[] Previous = new PlayerState[RosterPacket.MaxSlots];
        private static readonly bool[] Known = new bool[RosterPacket.MaxSlots];

        public static void Reset()
        {
            _snapshotLength = 0; _room = null; _mapHash = 0;
            Array.Clear(Known);
            DemoClip.Purge();
        }

        public static void Observe(ReadOnlySpan<byte> packet)
        {
            if (DemoPlayback.IsActive || packet.Length < 1) return;
            if ((PacketType)packet[0] == PacketType.Snapshot && packet.Length <= Snapshot.Length)
            {
                packet.CopyTo(Snapshot);
                _snapshotLength = packet.Length;
            }
        }

        public static ReplayMetadata Capture(ReplayType type, bool hashMap = true)
        {
            var packets = new List<byte[]>();
            var players = new List<ReplayPlayerInfo>();
            MatchStatePacket? match = NetSession.ServerMatch;
            string room = match?.RoomKey ?? "";
            if (_room != room || _mapHash == 0)
            {
                ulong hash = hashMap && room.Length > 0 ? ReplayMapIdentity.Compute(room) : 0;
                _room = room;
                _mapHash = hash;
            }
            if (NetSession.ServerSession is { } session)
            {
                byte[] packet = new byte[1 + SessionStatePacket.Size];
                packet[0] = (byte)PacketType.SessionState; session.Write(packet.AsSpan(1));
                packets.Add(packet);
            }
            if (match is { } state)
            {
                byte[] packet = new byte[1 + MatchStatePacket.Size];
                packet[0] = (byte)PacketType.MatchState; state.Write(packet.AsSpan(1));
                packets.Add(packet);
            }
            // Use the current wire roster instead of reconstructing an obsolete
            // packet shape. This carries stream identity, slot generations, teams
            // and lobby state, all of which playback needs before the first snapshot.
            RosterPacket roster = NetSession.LobbyRoster();
            for (int i = 0; i < roster.Count; i++)
            {
                players.Add(new(roster.Slots[i], roster.Hunters[i], roster.Teams[i], roster.Names[i]));
            }
            byte[] rosterBytes = new byte[1 + RosterPacket.Size];
            rosterBytes[0] = (byte)PacketType.Roster; roster.Write(rosterBytes.AsSpan(1));
            packets.Add(rosterBytes);
            if (_snapshotLength > 0) packets.Add(Snapshot.AsSpan(0, _snapshotLength).ToArray());
            return new ReplayMetadata
            {
                Type = type, RoomKey = room, Mode = (GameMode)(match?.Mode ?? 0), MapHash = _mapHash,
                Players = players, Bootstrap = new ReplayBootstrap { Packets = packets }
            };
        }

        public static void Event(ReplayEventType type, int actor = -1, int target = -1, int value = 0)
        {
            if (!NetSession.Active || DemoPlayback.IsActive) return;
            byte Actor(int slot) => slot is >= 0 and < RosterPacket.MaxSlots ? (byte)slot : byte.MaxValue;
            var e = new ReplayEvent(NetSession.NetFrame, type, Actor(actor), Actor(target), value);
            DemoRecorder.RecordEvent(e);
            ServerReplayRecorder.RecordEvent(e);
            DemoClip.AddEvent(e);
        }

        // Called where authoritative state is accepted, after normal validation. Annotations
        // describe confirmed transitions, never inferred projectile hits or local predictions.
        public static void AcceptedState(in PlayerState state, uint? authoritativeFrame = null)
        {
            int slot = state.SlotIndex;
            if (DemoPlayback.IsActive || slot >= Known.Length) return;
            if (Known[slot])
            {
                var old = Previous[slot];
                if (old.Points != state.Points) Event(ReplayEventType.ScoreChanged, slot, value: state.Points);
                if ((old.Flags & PlayerState.FlagSpawned) == 0 && (state.Flags & PlayerState.FlagSpawned) != 0)
                    Event(ReplayEventType.PlayerSpawn, slot);
                if (state.Deaths > old.Deaths)
                {
                    Event(ReplayEventType.PlayerDeath, slot, state.AttackerSlot);
                    if (state.AttackerSlot < RosterPacket.MaxSlots && state.AttackerSlot != slot)
                        Event(ReplayEventType.Kill, state.AttackerSlot, slot);
                    MphRead.Mods.KillCam.NoteDeath(slot, state.AttackerSlot,
                        authoritativeFrame ?? NetSession.NetFrame);
                }
                if (old.DamageEventId != state.DamageEventId) Event(ReplayEventType.Damage, state.AttackerSlot, slot,
                    Math.Max(0, old.Health - state.Health));
            }
            Previous[slot] = state; Known[slot] = true;
        }
    }
}
