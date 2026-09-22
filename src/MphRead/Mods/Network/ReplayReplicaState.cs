using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    internal readonly record struct ReplayOccupant(ushort Generation, Hunter Hunter,
        byte Color, sbyte Team, string Name);

    /// <summary>Packet-visible replica values, with private lifecycle/order tracking.
    /// This decoder deliberately has no NetSession or NetPlayerLifecycle dependency.</summary>
    internal sealed class ReplayReplicaState
    {
        private readonly ReplayOccupant[] _roster = new ReplayOccupant[PlayerEntity.SlotCapacity];
        private readonly PlayerState[] _players = new PlayerState[PlayerEntity.SlotCapacity];
        private readonly IntentPacket[] _intents = new IntentPacket[PlayerEntity.SlotCapacity];
        private readonly bool[] _hasPlayer = new bool[PlayerEntity.SlotCapacity];
        private readonly bool[] _hasIntent = new bool[PlayerEntity.SlotCapacity];
        private readonly uint[] _intentReceivedFrame = new uint[PlayerEntity.SlotCapacity];
        private readonly NetLifecycleTracker[] _lives = new NetLifecycleTracker[PlayerEntity.SlotCapacity];
        private uint? _rosterRevision;
        private bool _hasSnapshot;
        private readonly Dictionary<short, HealthSpawnState> _healthSpawns = new();
        public MatchStatePacket? Match { get; private set; }
        public SessionStatePacket? Configuration { get; private set; }
        public uint ServerTick { get; private set; }
        public uint RecordingFrame { get; private set; }
        public uint Rng1 { get; private set; } = Rng.Rng1StartValue;
        public uint Rng2 { get; private set; } = Rng.Rng2StartValue;
        private byte[] _worldTail = Array.Empty<byte>();
        public ReadOnlySpan<byte> WorldTail => _worldTail;
        public bool TryGetHealthSpawn(short id, out HealthSpawnState state) => _healthSpawns.TryGetValue(id, out state);
        public long AcceptedPackets { get; private set; }
        public long IgnoredPackets { get; private set; }
        public ReplayReplicaState()
        {
            for (int i = 0; i < _lives.Length; i++) _lives[i] = new NetLifecycleTracker();
        }
        public ReplayOccupant Occupant(int slot) => _roster[slot];
        public bool MatchesLife(int slot, ushort generation, ushort life) => (uint)slot < (uint)_lives.Length
            && generation != 0 && _lives[slot].Generation == generation && _lives[slot].LifeId == life;
        public bool TryGetPlayer(int slot, out PlayerState player)
        { player = _players[slot]; return _hasPlayer[slot]; }
        public bool TryGetIntent(int slot, out IntentPacket intent)
        { intent = _intents[slot]; return _hasIntent[slot]; }
        public uint IntentAge(int slot) => _hasIntent[slot] ? RecordingFrame - _intentReceivedFrame[slot] : uint.MaxValue;
        internal void Advance(uint frame) => RecordingFrame = frame;
        public void Reset()
        {
            Match = null;
            Configuration = null;
            Array.Clear(_roster);
            foreach (var life in _lives) life.SetOccupant(0);
            Rewind();
        }
        public void Rewind()
        {
            Array.Clear(_players);
            Array.Clear(_intents);
            Array.Clear(_hasPlayer);
            Array.Clear(_hasIntent);
            foreach (var life in _lives) life.ResetLife();
            _rosterRevision = null;
            _hasSnapshot = false;
            ServerTick = RecordingFrame = 0;
            AcceptedPackets = IgnoredPackets = 0;
            Rng1 = Rng.Rng1StartValue;
            Rng2 = Rng.Rng2StartValue;
            _worldTail = Array.Empty<byte>();
            _healthSpawns.Clear();
        }
        private bool Matches(ushort match, ulong authority) => Match is MatchStatePacket current
            && current.MatchId == match && current.AuthorityEpoch == authority;
        public void Accept(ReadOnlySpan<byte> packet, uint frame)
        {
            if (packet.IsEmpty) throw new InvalidDataException("Empty replay packet.");
            ReadOnlySpan<byte> payload = packet[1..];
            bool accepted = false;
            switch ((PacketType)packet[0])
            {
                case PacketType.MatchState:
                    if (payload.Length != MatchStatePacket.Size) throw Malformed();
                    var match = MatchStatePacket.Read(payload);
                    if (string.IsNullOrEmpty(match.RoomKey) || !Enum.IsDefined(typeof(GameMode), match.Mode)
                        || !float.IsFinite(match.TimeRemaining) || !float.IsFinite(match.TimeElapsed)) throw Malformed();
                    if (Match is MatchStatePacket previous && !Matches(match.MatchId, match.AuthorityEpoch))
                    {
                        if (match.AuthorityEpoch < previous.AuthorityEpoch
                            || match.AuthorityEpoch == previous.AuthorityEpoch
                            && !SessionStatePacket.IsNewer(match.MatchId, previous.MatchId)) break;
                        Reset();
                    }
                    Match = match;
                    accepted = true;
                    break;
                case PacketType.SessionState:
                    if (!SessionStatePacket.TryRead(payload, out var configuration)) throw Malformed();
                    if (Match.HasValue && !Matches(configuration.MatchId, configuration.AuthorityEpoch)) break;
                    if (Configuration is SessionStatePacket old && configuration.Revision != old.Revision
                        && !SessionStatePacket.IsNewer(configuration.Revision, old.Revision)) break;
                    Configuration = configuration;
                    accepted = true;
                    break;
                case PacketType.Roster:
                    if (!RosterPacket.TryRead(payload, out var roster)) throw Malformed();
                    if (!Matches(roster.MatchId, roster.AuthorityEpoch)
                        || _rosterRevision is uint revision && !NetLifecycleTracker.Newer(roster.Revision, revision)) break;
                    _rosterRevision = roster.Revision;
                    Span<bool> present = stackalloc bool[PlayerEntity.SlotCapacity];
                    for (int i = 0; i < roster.Count; i++)
                    {
                        int slot = roster.Slots[i];
                        present[slot] = true;
                        SetOccupant(slot, new(roster.Generations[i], (Hunter)roster.Hunters[i],
                            roster.Colors[i], roster.Teams[i], roster.Names[i]));
                    }
                    for (int i = 0; i < present.Length; i++) if (!present[i]) SetOccupant(i, default);
                    accepted = true;
                    break;
                case PacketType.Snapshot:
                    accepted = AcceptSnapshot(payload);
                    break;
                case PacketType.SlotIntent:
                    if ((payload.Length != 1 + IntentPacket.Size && payload.Length != 1 + IntentPacket.FullSize)
                        || payload[0] >= _intents.Length) throw Malformed();
                    int actor = payload[0];
                    IntentPacket intent = IntentPacket.Read(payload[1..]);
                    if (!Matches(intent.MatchId, intent.AuthorityEpoch) || intent.SlotGeneration == 0
                        || intent.SlotGeneration != _lives[actor].Generation || intent.LifeId == 0
                        || intent.LifeId != _lives[actor].LifeId
                        || _hasIntent[actor] && !NetLifecycleTracker.Newer(intent.Frame, _intents[actor].Frame)) break;
                    _intents[actor] = intent;
                    _intentReceivedFrame[actor] = frame;
                    _hasIntent[actor] = true;
                    accepted = true;
                    break;
                // Welcome, Authority, Bye, lobby commands, reconnect and chat are inert.
                // They describe a connection, not a replica's authoritative world.
            }
            RecordingFrame = frame;
            if (accepted) AcceptedPackets++; else IgnoredPackets++;
        }
        private void SetOccupant(int slot, ReplayOccupant occupant)
        {
            if (_roster[slot].Generation != occupant.Generation)
            {
                _lives[slot].SetOccupant(occupant.Generation);
                _hasPlayer[slot] = _hasIntent[slot] = false;
            }
            _roster[slot] = occupant;
        }
        private bool AcceptSnapshot(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < SnapshotHeader.Size) throw Malformed();
            var header = SnapshotHeader.Read(payload);
            int tail = SnapshotHeader.Size + header.PlayerCount * PlayerState.Size;
            int health = tail + NetMatchTimeSync.Size;
            if (header.PlayerCount > _players.Length || health > payload.Length) throw Malformed();
            if (!Matches(header.MatchId, header.AuthorityEpoch)) return false;
            if (!NetMatchTimeSync.Validate(payload.Slice(tail, NetMatchTimeSync.Size))
                || !NetHealthSync.Validate(payload[health..])
                || BinaryPrimitives.ReadUInt16LittleEndian(payload[health..]) != header.MatchId) throw Malformed();
            if (_hasSnapshot && !NetLifecycleTracker.Newer(header.Frame, ServerTick)) return false;
            Span<PlayerState> players = stackalloc PlayerState[PlayerEntity.SlotCapacity];
            int seen = 0;
            for (int i = 0; i < header.PlayerCount; i++)
            {
                var player = PlayerState.Read(payload[(SnapshotHeader.Size + i * PlayerState.Size)..]);
                if (player.SlotIndex >= _players.Length || (seen & 1 << player.SlotIndex) != 0
                    || !Sane(player.Position) || !Sane(player.Speed) || !Sane(player.Facing)
                    || player.Health > 0 && ((player.Flags & PlayerState.FlagSpawned) == 0 || player.LifeId == 0)) throw Malformed();
                seen |= 1 << player.SlotIndex;
                players[i] = player;
            }
            for (int i = 0; i < header.PlayerCount; i++)
            {
                var player = players[i];
                int slot = player.SlotIndex;
                var next = (player.Flags & PlayerState.FlagSpectating) != 0 ? NetworkPlayerState.Spectating
                    : player.LifeId == 0 ? NetworkPlayerState.WaitingToSpawn
                    : player.Health == 0 ? NetworkPlayerState.Dead : NetworkPlayerState.Alive;
                if (_lives[slot].Accept(player.SlotGeneration, player.LifeId, next, out bool fresh) != LifecycleRejection.None) continue;
                if (fresh) _hasIntent[slot] = false;
                _players[slot] = player;
                _hasPlayer[slot] = true;
            }
            ServerTick = header.Frame;
            Rng1 = header.Rng1;
            Rng2 = header.Rng2;
            _worldTail = payload[tail..].ToArray();
            _healthSpawns.Clear();
            var healthState = payload[health..];
            for (int offset = NetHealthSync.HeaderSize; offset < healthState.Length; offset += NetHealthSync.EntrySize)
            {
                byte flags = healthState[offset + 2];
                _healthSpawns.Add(BinaryPrimitives.ReadInt16LittleEndian(healthState[offset..]), new(
                    (flags & 1) != 0, (flags & 2) != 0,
                    BinaryPrimitives.ReadUInt16LittleEndian(healthState[(offset + 3)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(healthState[(offset + 5)..]),
                    (sbyte)(((flags >> 2) & 15) - 1)));
            }
            _hasSnapshot = true;
            return true;
        }
        private static bool Sane(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
            && Math.Abs(v.X) < 100000 && Math.Abs(v.Y) < 100000 && Math.Abs(v.Z) < 100000;
        private static InvalidDataException Malformed()
            => new("Replay packet has an invalid length or state value.");
    }
}
