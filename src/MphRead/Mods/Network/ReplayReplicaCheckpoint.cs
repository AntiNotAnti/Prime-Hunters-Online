using System;
using System.Buffers.Binary;
using System.IO;

namespace MphRead.Mods.Network;

/// <summary>Detached decoder state only. A complete scene checkpoint must also
/// supply entity, simulation and presentation state; this is never one by itself.</summary>
internal sealed class ReplayReplicaCheckpoint
{
    internal const int MaximumBytes = 64 * 1024;
    private readonly byte[] _bytes;
    internal ReadOnlySpan<byte> Bytes => _bytes;
    internal ReplayReplicaCheckpoint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Replica checkpoint is too large.");
        _bytes = bytes.ToArray();
    }
}

internal sealed partial class ReplayReplicaState
{
    private const uint CheckpointMagic = 0x43525050; // PPRC, independent of demo/wire formats
    private const ushort CheckpointVersion = 1;
    internal ReplayReplicaCheckpoint CaptureCheckpoint()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(CheckpointMagic); writer.Write(CheckpointVersion); writer.Write((byte)NetConfig.ProtocolVersion);
        writer.Write(RecordingFrame); writer.Write(MatchRecordingFrame); writer.Write(ServerTick);
        writer.Write(Rng1); writer.Write(Rng2);
        writer.Write(AcceptedPackets); writer.Write(IgnoredPackets);
        writer.Write(_hasSnapshot); writer.Write(_rosterRevision.HasValue);
        if (_rosterRevision.HasValue) writer.Write(_rosterRevision.Value);
        writer.Write(Match.HasValue);
        if (Match is { } match)
        {
            byte[] bytes = new byte[MatchStatePacket.Size]; match.Write(bytes); writer.Write(bytes);
        }
        writer.Write(Configuration.HasValue);
        if (Configuration is { } configuration)
        {
            byte[] bytes = new byte[SessionStatePacket.Size]; configuration.Write(bytes); writer.Write(bytes);
        }
        var roster = RosterPacket.Create();
        roster.MatchId = Match?.MatchId ?? 0; roster.AuthorityEpoch = Match?.AuthorityEpoch ?? 0;
        roster.Revision = _rosterRevision ?? 0;
        for (int i = 0; i < _roster.Length; i++)
        {
            var occupant = _roster[i];
            if (occupant.Generation == 0) continue;
            int index = roster.Count++;
            roster.Slots[index] = (byte)i; roster.Generations[index] = occupant.Generation;
            roster.Hunters[index] = (byte)occupant.Hunter; roster.Colors[index] = occupant.Color;
            roster.Teams[index] = occupant.Team; roster.Names[index] = occupant.Name;
        }
        byte[] rosterBytes = new byte[RosterPacket.Size]; roster.Write(rosterBytes); writer.Write(rosterBytes);
        byte[] playerBytes = new byte[PlayerState.Size], intentBytes = new byte[IntentPacket.FullSize];
        for (int i = 0; i < _roster.Length; i++)
        {
            var life = _lives[i].Capture();
            writer.Write(life.Generation); writer.Write(life.LifeId); writer.Write((byte)life.State); writer.Write(life.Dead);
            writer.Write(_hasPlayer[i]); _players[i].Write(playerBytes); writer.Write(playerBytes);
            writer.Write(_hasIntent[i]); _intents[i].Write(intentBytes); writer.Write(intentBytes);
            writer.Write(_intentReceivedFrame[i]);
        }
        writer.Write(_worldTail.Length); writer.Write(_worldTail);
        writer.Flush();
        return new(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    internal void RestoreCheckpoint(ReplayReplicaCheckpoint checkpoint)
    {
        // Parse into a temporary owner first: an invalid checkpoint must not
        // partially replace a running session's decoder/lifecycle state.
        var restored = new ReplayReplicaState();
        using var stream = new MemoryStream(checkpoint.Bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        byte[] Read(int count)
        {
            if (count < 0 || count > stream.Length - stream.Position) throw new InvalidDataException("Truncated replica checkpoint.");
            return reader.ReadBytes(count);
        }
        try
        {
            if (reader.ReadUInt32() != CheckpointMagic || reader.ReadUInt16() != CheckpointVersion
                || reader.ReadByte() != NetConfig.ProtocolVersion) throw new InvalidDataException("Incompatible replica checkpoint.");
            restored.RecordingFrame = reader.ReadUInt32(); restored.MatchRecordingFrame = reader.ReadUInt32();
            restored.ServerTick = reader.ReadUInt32(); restored.Rng1 = reader.ReadUInt32(); restored.Rng2 = reader.ReadUInt32();
            restored.AcceptedPackets = reader.ReadInt64(); restored.IgnoredPackets = reader.ReadInt64();
            restored._hasSnapshot = reader.ReadBoolean();
            restored._rosterRevision = reader.ReadBoolean() ? reader.ReadUInt32() : null;
            if (reader.ReadBoolean()) restored.Match = MatchStatePacket.Read(Read(MatchStatePacket.Size));
            if (reader.ReadBoolean())
            {
                if (!SessionStatePacket.TryRead(Read(SessionStatePacket.Size), out var configuration)) throw Malformed();
                restored.Configuration = configuration;
            }
            if (!RosterPacket.TryRead(Read(RosterPacket.Size), out var roster)) throw Malformed();
            if (restored.Match is { } current
                && (string.IsNullOrEmpty(current.RoomKey) || !Enum.IsDefined(typeof(GameMode), current.Mode)
                    || !float.IsFinite(current.TimeRemaining) || !float.IsFinite(current.TimeElapsed)
                    || roster.MatchId != current.MatchId || roster.AuthorityEpoch != current.AuthorityEpoch
                    || restored.Configuration is { } rules && !restored.Matches(rules.MatchId, rules.AuthorityEpoch))) throw Malformed();
            for (int i = 0; i < roster.Count; i++)
                restored._roster[roster.Slots[i]] = new(roster.Generations[i], (Hunter)roster.Hunters[i],
                    roster.Colors[i], roster.Teams[i], roster.Names[i]);
            for (int i = 0; i < _roster.Length; i++)
            {
                restored._lives[i].Restore(new(reader.ReadUInt16(), reader.ReadUInt16(),
                    (NetworkPlayerState)reader.ReadByte(), reader.ReadBoolean()));
                restored._hasPlayer[i] = reader.ReadBoolean(); restored._players[i] = PlayerState.Read(Read(PlayerState.Size));
                restored._hasIntent[i] = reader.ReadBoolean(); restored._intents[i] = IntentPacket.Read(Read(IntentPacket.FullSize));
                restored._intentReceivedFrame[i] = reader.ReadUInt32();
                if (restored._lives[i].Generation != restored._roster[i].Generation
                    || restored._hasPlayer[i] && (!restored.MatchesLife(i, restored._players[i].SlotGeneration, restored._players[i].LifeId)
                        || restored._players[i].SlotIndex != i || !Sane(restored._players[i].Position)
                        || !Sane(restored._players[i].Speed) || !Sane(restored._players[i].Facing))
                    || restored._hasIntent[i] && (!restored.MatchesLife(i, restored._intents[i].SlotGeneration, restored._intents[i].LifeId)
                        || !restored.Matches(restored._intents[i].MatchId, restored._intents[i].AuthorityEpoch)
                        || restored._intentReceivedFrame[i] > restored.RecordingFrame)) throw Malformed();
            }
            restored._worldTail = Read(reader.ReadInt32());
            if (!restored._hasSnapshot && restored._worldTail.Length != 0) throw Malformed();
            if (restored._hasSnapshot)
            {
                var tail = restored.WorldTail;
                if (tail.Length < NetMatchTimeSync.Size + NetHealthSync.HeaderSize
                    || !NetMatchTimeSync.Validate(tail[..NetMatchTimeSync.Size])
                    || !NetHealthSync.Validate(tail[NetMatchTimeSync.Size..])) throw Malformed();
                var health = tail[NetMatchTimeSync.Size..];
                if (BinaryPrimitives.ReadUInt16LittleEndian(health) != restored.Match?.MatchId) throw Malformed();
                for (int offset = NetHealthSync.HeaderSize; offset < health.Length; offset += NetHealthSync.EntrySize)
                {
                    byte flags = health[offset + 2];
                    restored._healthSpawns.Add(BinaryPrimitives.ReadInt16LittleEndian(health[offset..]), new(
                        (flags & 1) != 0, (flags & 2) != 0, BinaryPrimitives.ReadUInt16LittleEndian(health[(offset + 3)..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(health[(offset + 5)..]), (sbyte)(((flags >> 2) & 15) - 1)));
                }
            }
            if (stream.Position != stream.Length || restored.MatchRecordingFrame > restored.RecordingFrame
                || restored.AcceptedPackets < 0 || restored.IgnoredPackets < 0) throw Malformed();
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated replica checkpoint.", ex); }
        Match = restored.Match; Configuration = restored.Configuration;
        RecordingFrame = restored.RecordingFrame; MatchRecordingFrame = restored.MatchRecordingFrame;
        ServerTick = restored.ServerTick; Rng1 = restored.Rng1; Rng2 = restored.Rng2;
        AcceptedPackets = restored.AcceptedPackets; IgnoredPackets = restored.IgnoredPackets;
        _hasSnapshot = restored._hasSnapshot; _rosterRevision = restored._rosterRevision;
        Array.Copy(restored._roster, _roster, _roster.Length); Array.Copy(restored._players, _players, _players.Length);
        Array.Copy(restored._intents, _intents, _intents.Length); Array.Copy(restored._hasPlayer, _hasPlayer, _hasPlayer.Length);
        Array.Copy(restored._hasIntent, _hasIntent, _hasIntent.Length);
        Array.Copy(restored._intentReceivedFrame, _intentReceivedFrame, _intentReceivedFrame.Length);
        for (int i = 0; i < _lives.Length; i++) _lives[i].Restore(restored._lives[i].Capture());
        _worldTail = restored._worldTail;
        _healthSpawns.Clear(); foreach (var pair in restored._healthSpawns) _healthSpawns.Add(pair.Key, pair.Value);
    }
}
