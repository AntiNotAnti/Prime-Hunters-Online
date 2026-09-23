using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Network;

/// <summary>Optional, loss-tolerant full state. At most one 48 KiB assembly lives
/// per reader; a new tick replaces incomplete older work. Only a complete, valid
/// authority snapshot enters the recorder, so file checkpoints need no partial assembly.</summary>
internal sealed class ReplayAuthorityWire
{
    internal const int HeaderSize = 21, PartBytes = NetConfig.MaxPayloadSize - HeaderSize;
    private byte[]? _pending;
    private ulong _received;
    private uint _tick;
    private ushort _match;
    private ulong _epoch;
    private uint? _accepted;
    private int _parts;
    internal void Reset() { _pending = null; _received = 0; _accepted = null; _parts = 0; }
    internal static IEnumerable<byte[]> Packets(ReplayAuthorityWorld world)
    {
        byte[] bytes = world.Encode(); int parts = (bytes.Length + PartBytes - 1) / PartBytes;
        for (int part = 0; part < parts; part++)
        {
            int offset = part * PartBytes, length = Math.Min(PartBytes, bytes.Length - offset);
            byte[] packet = new byte[1 + HeaderSize + length]; packet[0] = (byte)PacketType.ReplayWorld;
            var span = packet.AsSpan(1); span[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(span[1..], world.MatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(span[3..], world.Epoch);
            BinaryPrimitives.WriteUInt32LittleEndian(span[11..], world.Tick);
            BinaryPrimitives.WriteInt32LittleEndian(span[15..], bytes.Length); span[19] = (byte)part; span[20] = (byte)parts;
            bytes.AsSpan(offset, length).CopyTo(span[HeaderSize..]); yield return packet;
        }
    }
    internal static bool ValidPart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize || payload[0] != 1) return false;
        int total = BinaryPrimitives.ReadInt32LittleEndian(payload[15..]), part = payload[19], parts = payload[20];
        return total is > 0 and <= ReplayAuthorityWorld.MaximumBytes && parts == (total + PartBytes - 1) / PartBytes
            && part < parts && payload.Length == HeaderSize + Math.Min(PartBytes, total - part * PartBytes);
    }
    internal ReplayAuthorityWorld? Accept(ReadOnlySpan<byte> payload, ushort match, ulong epoch, bool strict = false)
    {
        if (!ValidPart(payload))
        { if (strict) throw new InvalidDataException("Malformed authoritative replay fragment."); return null; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]) != match
            || BinaryPrimitives.ReadUInt64LittleEndian(payload[3..]) != epoch) return null;
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[11..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(payload[15..]), part = payload[19], parts = payload[20];
        if (total is <= 0 or > ReplayAuthorityWorld.MaximumBytes || parts != (total + PartBytes - 1) / PartBytes
            || part >= parts || payload.Length != HeaderSize + Math.Min(PartBytes, total - part * PartBytes)) return null;
        if (_match != match || _epoch != epoch) { Reset(); _match = match; _epoch = epoch; }
        if (_accepted is uint accepted && !NetLifecycleTracker.Newer(tick, accepted)) return null;
        if (_pending == null || NetLifecycleTracker.Newer(tick, _tick))
        { _pending = new byte[total]; _parts = parts; _tick = tick; _received = 0; }
        if (tick != _tick || _parts != parts || _pending.Length != total) return null;
        ulong mask = 1UL << part; if ((_received & mask) != 0) return null;
        payload[HeaderSize..].CopyTo(_pending.AsSpan(part * PartBytes)); _received |= mask;
        if (_received != (1UL << parts) - 1) return null;
        byte[] completed = _pending; _pending = null;
        try
        {
            var world = ReplayAuthorityWorld.Decode(completed);
            if (world.MatchId != match || world.Epoch != epoch || world.Tick != tick) return null;
            _accepted = tick; return world;
        }
        catch (InvalidDataException) when (!strict) { return null; }
    }
}
