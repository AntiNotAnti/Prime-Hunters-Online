using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Network;

/// <summary>Durable sink for the same accepted facts used by passive playback.
/// V4 adds an exact initial world; subsequent records retain the v3 chunk/index
/// envelope. No packet-arrival buffer or second gameplay recorder is involved.</summary>
internal static class ReplayTimelineArchive
{
    // File-only quiet-frame marker; never sent over a network connection.
    private static readonly byte[] FrameMarker = [255];
    internal static ReplayMetadata Metadata(PassiveReplayScene world, ReplayType type)
    {
        var match = world.State.Match ?? throw new InvalidDataException("Replay has no match.");
        using var checkpoint = ReplayWorldCheckpoint.Capture(world, world.Session.RecordingFrame);
        return new ReplayMetadata
        {
            FormatVersion = 4, Type = type, RoomKey = match.RoomKey, Mode = (GameMode)match.Mode,
            MapHash = world.MapHash, OriginRecordingFrame = world.Session.RecordingFrame,
            WorldCheckpoint = checkpoint.Bytes.ToArray(),
            Players = Enumerable.Range(0, 8).Select(slot => (Slot: slot, Occupant: world.State.Occupant(slot)))
                .Where(p => p.Occupant.Generation != 0)
                .Select(p => new ReplayPlayerInfo((byte)p.Slot, (byte)p.Occupant.Hunter,
                    p.Occupant.Team, p.Occupant.Name)).ToArray()
        };
    }
    internal static void Write(ReplayWriterV3 writer, ReplayTimelineRecord record, uint origin)
    {
        if (record.RecordingFrame < origin) return;
        uint frame = record.RecordingFrame - origin;
        if (frame > 0 && record.Kind is ReplayFactKind.Match or ReplayFactKind.Roster or ReplayFactKind.Snapshot or ReplayFactKind.Intent or ReplayFactKind.AuthorityWorld)
            writer.WriteRecord(frame, record.Payload);
        if (record.Marker is { } marker)
        {
            writer.WriteRecord(frame, EncodeMarker(record.ServerTick, marker));
            if (EventType(marker.Kind) is { } type) writer.WriteEvent(new(frame, type, marker.Actor, marker.Target, marker.Value));
        }
    }
    // File-only compressed construction decoder for faithful v2 extraction.
    // It preserves the legacy preflight roster/rules even for clips shorter
    // than that preflight, without including future gameplay in the clip.
    internal static byte[] Construction(ReplayReplicaState state)
    {
        using var buffer = new MemoryStream(); buffer.WriteByte(253);
        using (var compressed = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            compressed.Write(state.CaptureCheckpoint().Bytes);
        return buffer.ToArray();
    }
    internal static ReplayReplicaCheckpoint ReadConstruction(ReadOnlySpan<byte> packet)
    {
        using var input = new MemoryStream(packet[1..].ToArray());
        using var compressed = new DeflateStream(input, CompressionMode.Decompress);
        byte[] bytes = new byte[ReplayReplicaCheckpoint.MaximumBytes + 1];
        int length = 0, read;
        while (length < bytes.Length && (read = compressed.Read(bytes, length, bytes.Length - length)) != 0) length += read;
        if (length > ReplayReplicaCheckpoint.MaximumBytes) throw new InvalidDataException("Oversized replay construction decoder.");
        return new(bytes.AsSpan(0, length));
    }
    internal static void EndFrame(ReplayWriterV3 writer, uint frame) => writer.WriteRecord(frame, FrameMarker);
    internal static ReplayEventType? EventType(ReplayMarkerKind kind) => kind switch
    {
        ReplayMarkerKind.Kill => ReplayEventType.Kill,
        ReplayMarkerKind.Death => ReplayEventType.PlayerDeath,
        ReplayMarkerKind.Spawn => ReplayEventType.PlayerSpawn,
        ReplayMarkerKind.Damage => ReplayEventType.Damage,
        ReplayMarkerKind.Score => ReplayEventType.ScoreChanged,
        ReplayMarkerKind.Join => ReplayEventType.PlayerJoined,
        ReplayMarkerKind.Leave => ReplayEventType.PlayerLeft,
        ReplayMarkerKind.MatchEnd => ReplayEventType.MatchEnded,
        ReplayMarkerKind.MatchStart => ReplayEventType.MatchStarted,
        ReplayMarkerKind.WeaponFired => ReplayEventType.WeaponFired,
        ReplayMarkerKind.Objective => ReplayEventType.Objective,
        ReplayMarkerKind.Headshot => ReplayEventType.Headshot,
        ReplayMarkerKind.FlagCapture => ReplayEventType.FlagCapture,
        ReplayMarkerKind.NodeCapture => ReplayEventType.NodeCapture,
        ReplayMarkerKind.PrimeChange => ReplayEventType.PrimeChanged,
        ReplayMarkerKind.MatchPoint => ReplayEventType.MatchPoint,
        ReplayMarkerKind.Overtime => ReplayEventType.Overtime,
        _ => null
    };
    // File-only semantic facts retain exact identity even though the legacy
    // Studio event index intentionally remains a compact coarse projection.
    internal static byte[] EncodeMarker(uint tick, ReplayMarker marker)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        writer.Write((byte)254); writer.Write((byte)1); writer.Write(tick);
        writer.Write((byte)marker.Kind); writer.Write(marker.Actor); writer.Write(marker.Target);
        writer.Write(marker.Value); writer.Write(marker.Weapon); writer.Write(marker.DamageFlags);
        writer.Write(marker.Kill.HasValue);
        if (marker.Kill is { } kill)
        {
            writer.Write(kill.MatchId); writer.Write(kill.AuthorityEpoch); writer.Write(kill.ServerTick);
            writer.Write(kill.EventId); writer.Write(kill.KillerSlot); writer.Write(kill.KillerGeneration);
            writer.Write(kill.VictimSlot); writer.Write(kill.VictimGeneration); writer.Write(kill.VictimLifeId);
        }
        return buffer.ToArray();
    }
    internal static ReplayTimelineRecord DecodeMarker(uint frame, byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        if (reader.ReadByte() != 254 || reader.ReadByte() != 1) throw new InvalidDataException("Unknown replay semantic fact.");
        uint tick = reader.ReadUInt32();
        var kind = (ReplayMarkerKind)reader.ReadByte(); byte actor = reader.ReadByte(), target = reader.ReadByte();
        int value = reader.ReadInt32(); byte weapon = reader.ReadByte(), flags = reader.ReadByte(), hasKill = reader.ReadByte();
        if (!Enum.IsDefined(kind) || actor != byte.MaxValue && actor >= 8 || target != byte.MaxValue && target >= 8 || hasKill > 1)
            throw new InvalidDataException("Invalid replay semantic identity.");
        ReplayKillIdentity? kill = hasKill == 0 ? null : new(reader.ReadUInt16(), reader.ReadUInt64(), reader.ReadUInt32(),
            reader.ReadUInt16(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16());
        if (reader.BaseStream.Position != bytes.Length || kill is { } k && (k.KillerSlot >= 8 || k.VictimSlot >= 8))
            throw new InvalidDataException("Invalid replay semantic fact length/slot.");
        return new(frame, tick, ReplayFactKind.Event, ReadOnlySpan<byte>.Empty, new(kind, actor, target, value, kill, weapon, flags));
    }
    internal static void Save(ReplayTimelineClip clip, PassiveReplayScene start, string path)
    {
        if (start.Session.CurrentFrame != clip.StartRecordingFrame)
            throw new InvalidOperationException("The clip must warm up to its exact visible start before saving.");
        Save(clip, Metadata(start, ReplayType.Clip), path);
    }
    internal static void Save(ReplayTimelineClip clip, ReplayMetadata metadata, string path)
    {
        using var writer = new ReplayWriterV3(path, metadata);
        try
        {
            EndFrame(writer, 0);
            foreach (var record in clip.Records) Write(writer, record, clip.StartRecordingFrame);
            EndFrame(writer, clip.EndRecordingFrame - clip.StartRecordingFrame);
        }
        catch { writer.Abort(); throw; }
    }
}
