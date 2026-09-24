using System;
using System.Buffers.Binary;

namespace MphRead.Mods.Network;

/// <summary>Independent full-state lanes. The canonical snapshot stays an in-process
/// and replay representation; live UDP never needs its large combined payload.</summary>
public static class SnapshotFast
{
    public const int PlayerSize = PlayerState.Size - 7;
    public const int MaximumEncodedSize = NetHeader.Size + SnapshotHeader.Size + 8 * PlayerSize;
    public static int Write(ReadOnlySpan<byte> canonical, Span<byte> dest)
    {
        var header = SnapshotHeader.Read(canonical); header.Write(dest);
        for (int i = 0; i < header.PlayerCount; i++)
        {
            var source = canonical.Slice(SnapshotHeader.Size + i * PlayerState.Size, PlayerState.Size);
            var target = dest.Slice(SnapshotHeader.Size + i * PlayerSize, PlayerSize);
            source[..41].CopyTo(target); source[48..].CopyTo(target[41..]);
        }
        return SnapshotHeader.Size + header.PlayerCount * PlayerSize;
    }
}

public sealed class NetReplicationLanes
{
    public const int LaneHeader = 14, SlowEntry = 10;
    public readonly byte[] Fast = new byte[1200], Slow = new byte[256], World = new byte[512];
    public int FastLength { get; private set; }
    public int SlowLength { get; private set; }
    public int WorldLength { get; private set; }
    public uint SlowRevision { get; private set; }
    public uint WorldRevision { get; private set; }
    public bool SendSlow { get; private set; }
    public bool SendWorld { get; private set; }
    private ushort _match;
    private ulong _epoch;
    private uint _slowFrame, _worldFrame;
    public static long FastPackets, FastBytes, SlowPackets, SlowBytes, WorldPackets, WorldBytes;
    public static int FastMaximum;
    private static uint Next(uint revision) => revision == uint.MaxValue ? 1 : revision + 1;
    private static void Header(Span<byte> dest, SnapshotHeader snapshot, uint revision)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest, snapshot.MatchId);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[2..], snapshot.AuthorityEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[10..], revision);
    }
    public void Prepare(ReadOnlySpan<byte> canonical)
    {
        var header = SnapshotHeader.Read(canonical);
        bool reset = _match != header.MatchId || _epoch != header.AuthorityEpoch;
        if (reset) { _match = header.MatchId; _epoch = header.AuthorityEpoch; SlowRevision = WorldRevision = 0; }
        FastLength = SnapshotFast.Write(canonical, Fast);
        Span<byte> slow = stackalloc byte[256];
        int at = LaneHeader; slow[at++] = header.PlayerCount;
        for (int i = 0; i < header.PlayerCount; i++)
        {
            var source = canonical.Slice(SnapshotHeader.Size + i * PlayerState.Size, PlayerState.Size);
            slow[at] = source[0]; source.Slice(48, 2).CopyTo(slow[(at + 1)..]);
            source.Slice(41, 7).CopyTo(slow[(at + 3)..]); at += SlowEntry;
        }
        int timeAt = SnapshotHeader.Size + header.PlayerCount * PlayerState.Size;
        int slowLength = at + NetMatchTimeSync.Size;
        canonical.Slice(timeAt, NetMatchTimeSync.Size).CopyTo(slow[at..]);
        SendSlow = reset || SlowRevision == 0 || slowLength != SlowLength
            || !slow.Slice(LaneHeader, at - LaneHeader).SequenceEqual(Slow.AsSpan(LaneHeader, at - LaneHeader))
            || header.Frame - _slowFrame >= 6;
        if (SendSlow)
        {
            SlowRevision = Next(SlowRevision); Header(slow, header, SlowRevision);
            slow[..slowLength].CopyTo(Slow); SlowLength = slowLength; _slowFrame = header.Frame;
        }
        var health = canonical[(timeAt + NetMatchTimeSync.Size)..];
        bool changed = WorldLength != LaneHeader + health.Length;
        if (!changed)
            for (int i = NetHealthSync.HeaderSize; i < health.Length; i += NetHealthSync.EntrySize)
                if (!health.Slice(i, 3).SequenceEqual(World.AsSpan(LaneHeader + i, 3))
                    || !health.Slice(i + 5, 2).SequenceEqual(World.AsSpan(LaneHeader + i + 5, 2))) { changed = true; break; }
        SendWorld = reset || WorldRevision == 0 || changed || header.Frame - _worldFrame >= 15;
        if (SendWorld)
        {
            WorldRevision = Next(WorldRevision); Header(World, header, WorldRevision);
            health.CopyTo(World.AsSpan(LaneHeader)); WorldLength = LaneHeader + health.Length; _worldFrame = header.Frame;
        }
    }
    public static void Count(PacketType type, int length)
    {
        int bytes = length + NetHeader.Size;
        if (type == PacketType.SnapshotFast) { FastPackets++; FastBytes += bytes; FastMaximum = Math.Max(FastMaximum, bytes); }
        else if (type == PacketType.PlayerSlowState) { SlowPackets++; SlowBytes += bytes; }
        else if (type == PacketType.WorldState) { WorldPackets++; WorldBytes += bytes; }
    }
}

/// <summary>Revision fencing and full-state assembly, with no decode allocations.</summary>
public sealed class NetReplicationReceiver
{
    private readonly byte[] _slow = new byte[256], _world = new byte[512];
    private int _slowLength, _worldLength;
    private ushort _match;
    private ulong _epoch;
    public uint SlowRevision { get; private set; }
    public uint WorldRevision { get; private set; }
    public void Reset(ushort match, ulong epoch)
    { _match = match; _epoch = epoch; _slowLength = _worldLength = 0; SlowRevision = WorldRevision = 0; }
    public bool Receive(PacketType type, ReadOnlySpan<byte> data, ushort match, ulong epoch)
    {
        if (data.Length < NetReplicationLanes.LaneHeader || BinaryPrimitives.ReadUInt16LittleEndian(data) != match
            || BinaryPrimitives.ReadUInt64LittleEndian(data[2..]) != epoch) return false;
        if (_match != match || _epoch != epoch) Reset(match, epoch);
        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        if (revision == 0) return false;
        if (type == PacketType.PlayerSlowState)
        {
            if (data.Length < 15 || data[14] > 8 || data.Length != 15 + data[14] * 10 + NetMatchTimeSync.Size
                || SlowRevision != 0 && !NetLifecycleTracker.Newer(revision, SlowRevision)
                || !NetMatchTimeSync.Validate(data[(15 + data[14] * 10)..])) return false;
            int seen = 0;
            for (int i = 0; i < data[14]; i++)
            { int slot = data[15 + i * 10]; if (slot >= 8 || (seen & (1 << slot)) != 0) return false; seen |= 1 << slot; }
            data.CopyTo(_slow); _slowLength = data.Length; SlowRevision = revision; return true;
        }
        if (type != PacketType.WorldState || data.Length > _world.Length
            || WorldRevision != 0 && !NetLifecycleTracker.Newer(revision, WorldRevision)
            || !NetHealthSync.Validate(data[14..]) || BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) != match) return false;
        data.CopyTo(_world); _worldLength = data.Length; WorldRevision = revision; return true;
    }
    public int Assemble(ReadOnlySpan<byte> fast, Span<byte> canonical, ushort match, ulong epoch)
    {
        if (fast.Length < SnapshotHeader.Size) return 0;
        var header = SnapshotHeader.Read(fast);
        if (header.MatchId != match || header.AuthorityEpoch != epoch || header.PlayerCount > 8
            || fast.Length != SnapshotHeader.Size + header.PlayerCount * SnapshotFast.PlayerSize) return 0;
        if (_match != match || _epoch != epoch) Reset(match, epoch);
        int length = SnapshotHeader.Size + header.PlayerCount * PlayerState.Size + NetMatchTimeSync.Size
            + (_worldLength == 0 ? NetHealthSync.HeaderSize : _worldLength - 14);
        if (canonical.Length < length) return 0;
        canonical[..length].Clear(); header.Write(canonical);
        for (int i = 0; i < header.PlayerCount; i++)
        {
            var source = fast.Slice(SnapshotHeader.Size + i * SnapshotFast.PlayerSize, SnapshotFast.PlayerSize);
            var target = canonical.Slice(SnapshotHeader.Size + i * PlayerState.Size, PlayerState.Size);
            source[..41].CopyTo(target); source[41..].CopyTo(target[48..]);
            if (_slowLength != 0)
                for (int j = 0; j < _slow[14]; j++)
                {
                    int at = 15 + j * 10;
                    if (_slow[at] == source[0] && BinaryPrimitives.ReadUInt16LittleEndian(_slow.AsSpan(at + 1))
                        == BinaryPrimitives.ReadUInt16LittleEndian(target[48..]))
                        _slow.AsSpan(at + 3, 7).CopyTo(target[41..]);
                }
        }
        int timeAt = SnapshotHeader.Size + header.PlayerCount * PlayerState.Size;
        if (_slowLength != 0) _slow.AsSpan(_slowLength - NetMatchTimeSync.Size, NetMatchTimeSync.Size).CopyTo(canonical[timeAt..]);
        int worldAt = timeAt + NetMatchTimeSync.Size;
        if (_worldLength != 0) _world.AsSpan(14, _worldLength - 14).CopyTo(canonical[worldAt..]);
        else BinaryPrimitives.WriteUInt16LittleEndian(canonical[worldAt..], match);
        return length;
    }
}
