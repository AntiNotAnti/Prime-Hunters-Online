using System;
using System.Buffers.Binary;

namespace MphRead.Mods.Network;

// Anonymous, lossy diagnostics only. Never enters a reliable channel or a
// gameplay decision. At most one 431-byte batch per client per second.
public static class CombatStudyReports
{
    public const int Capacity = 32, HeaderSize = 15, EntrySize = 13;
    private static readonly byte[] Pending = new byte[Capacity * EntrySize];
    private static int _count;
    private static ushort _generation, _life;
    private static ulong _epoch;
    private static ushort _match;
    public static void Record(in CombatAckEntry ack, int ageFrames, int damageCorrection,
        int healthCorrection, bool headCorrection, byte weapon)
    {
        int slot = NetSession.LocalSlot;
        if ((uint)slot >= 8 || NetSession.Role != NetRole.Client) return;
        ushort generation = NetPlayerLifecycle.Generation(slot), life = NetPlayerLifecycle.Get(slot);
        if (_generation != generation || _life != life || _epoch != NetSession.AuthorityEpoch || _match != NetSession.CurrentMatchId)
        { _count = 0; _generation = generation; _life = life; _epoch = NetSession.AuthorityEpoch; _match = NetSession.CurrentMatchId; }
        if (_count == Capacity) return;
        Span<byte> b = Pending.AsSpan(_count++ * EntrySize, EntrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(b, ack.ClaimId); b[2] = ack.VictimSlot; b[3] = ack.Result;
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], (ushort)Math.Clamp(ageFrames * 1000 / 60, 0, 60000));
        BinaryPrimitives.WriteInt16LittleEndian(b[6..], (short)Math.Clamp(damageCorrection, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(b[8..], (short)Math.Clamp(healthCorrection, short.MinValue, short.MaxValue));
        b[10] = headCorrection ? (byte)1 : (byte)0; b[11] = (byte)ack.Flags;
        b[12] = weapon;
    }
    public static int Write(Span<byte> b)
    {
        if (_count == 0) return 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b, _match); BinaryPrimitives.WriteUInt64LittleEndian(b[2..], _epoch);
        BinaryPrimitives.WriteUInt16LittleEndian(b[10..], _generation); BinaryPrimitives.WriteUInt16LittleEndian(b[12..], _life);
        b[14] = (byte)_count; int length = _count * EntrySize;
        Pending.AsSpan(0, length).CopyTo(b[HeaderSize..]); _count = 0; return HeaderSize + length;
    }
    public static bool Receive(int slot, ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize || b[14] > Capacity || b.Length != HeaderSize + b[14] * EntrySize
            || !NetSession.MatchesStream(BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt64LittleEndian(b[2..]))
            || !NetPlayerLifecycle.Matches(slot, BinaryPrimitives.ReadUInt16LittleEndian(b[10..]), BinaryPrimitives.ReadUInt16LittleEndian(b[12..]))) return false;
        for (int i = 0; i < b[14]; i++)
        {
            var entry = b.Slice(HeaderSize + i * EntrySize, EntrySize);
            if (entry[2] >= 8 || entry[3] > (byte)CombatAckResult.Corrected || entry[10] > 1
                || entry[12] >= NetShotDiagnostics.WeaponCount) return false;
        }
        for (int i = 0; i < b[14]; i++)
        {
            var entry = b.Slice(HeaderSize + i * EntrySize, EntrySize);
            Telemetry.ProductionTelemetry.Emit(new(Telemetry.TelemetryEventType.CombatAck, NetSession.NetFrame,
                Player: (byte)slot, Victim: entry[2], Weapon: entry[12],
                Id: BinaryPrimitives.ReadUInt16LittleEndian(entry), Result: entry[3],
                Flags: entry[11] | 128, A: BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]),
                B: BinaryPrimitives.ReadInt16LittleEndian(entry[6..]), C: BinaryPrimitives.ReadInt16LittleEndian(entry[8..]), D: entry[10]));
        }
        return true;
    }
}
public static partial class NetSession
{
    private static void SendCombatStudy()
    {
        if (_transport == null || _hostEndPoint == null) return;
        int length = CombatStudyReports.Write(_scratch);
        if (length > 0) _transport.Send(_hostEndPoint, PacketType.CombatStudy, _scratch.AsSpan(0, length));
    }
}
public sealed partial class DedicatedServer
{
    private readonly uint[] _lastCombatStudy = new uint[8];
    private void ReceiveCombatStudy(ReceivedPacket packet)
    {
        if (!Telemetry.ProductionTelemetry.Enabled) return;
        var peer = Find(packet.Sender);
        if (peer == null || (uint)peer.SlotIndex >= 8 || !peer.MatchReady) return;
        int slot = peer.SlotIndex;
        if (NetSession.NetFrame - _lastCombatStudy[slot] < 30) return;
        if (CombatStudyReports.Receive(slot, packet.Payload)) _lastCombatStudy[slot] = NetSession.NetFrame;
    }
}
