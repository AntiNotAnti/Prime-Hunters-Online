using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    internal static class NetHealthSyncTest
    {
        public static void Run()
        {
            static void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("Health snapshot: " + message); }
            NetHealthSync.BeginRoom();
            Span<byte> empty = stackalloc byte[NetHealthSync.HeaderSize];
            Check(NetHealthSync.Write(empty) == empty.Length && NetHealthSync.Validate(empty), "empty world round trip");
            Check(!NetHealthSync.Validate(empty[..2]), "truncated header accepted");
            Span<byte> data = stackalloc byte[NetHealthSync.HeaderSize + 2 * NetHealthSync.EntrySize];
            data.Clear(); data[2] = 2;
            BinaryPrimitives.WriteInt16LittleEndian(data[3..], 4);
            const int pickerSlot = 4;
            data[5] = (byte)(3 | ((pickerSlot + 1) << 2));
            BinaryPrimitives.WriteUInt16LittleEndian(data[6..], 600);
            BinaryPrimitives.WriteUInt16LittleEndian(data[8..], 8);
            BinaryPrimitives.WriteInt16LittleEndian(data[10..], 7);
            Check(NetHealthSync.Validate(data), "valid state rejected");
            NetHealthSync.Receive(data);
            Check(NetHealthSync.TryGet(4, out var state)
                && state == new HealthSpawnState(true, true, 600, 8, pickerSlot), "state round trip");
            Check(NetHealthSync.TryGet(7, out state) && !state.Available && state.PickerSlot == -1,
                "unavailable pickup");
            data[5] = 0x80;
            Check(!NetHealthSync.Validate(data), "reserved flags accepted");
            data[5] = (byte)(3 | ((PlayerEntity.SlotCapacity + 1) << 2));
            Check(!NetHealthSync.Validate(data), "out-of-range picker accepted");
            data[5] = (byte)(3 | ((pickerSlot + 1) << 2)); data[10] = 4;
            Check(!NetHealthSync.Validate(data), "duplicate entity accepted");
            data[10] = 7;
            Check(!NetHealthSync.Validate(data[..^1]), "truncated entry accepted");
            BinaryPrimitives.WriteUInt16LittleEndian(data, 99);
            data[5] = 0;
            NetHealthSync.Receive(data);
            Check(NetHealthSync.TryGet(4, out state) && state.Available, "old match mutated health");
            Check(SnapshotHeader.Size + SnapshotWire.PlayerSize * PlayerEntity.SlotCapacity + 1
                + SnapshotWire.DamageGroupSize * PlayerEntity.SlotCapacity
                + NetMatchTimeSync.Size + NetHealthSync.HeaderSize + NetHealthSync.MaxSpawns * NetHealthSync.EntrySize < NetConfig.MaxPacketSize,
                "full snapshot exceeds datagram budget");
            Span<byte> times = stackalloc byte[NetMatchTimeSync.Size];
            times.Clear();
            BinaryPrimitives.WriteSingleLittleEndian(times[60..], 100);
            Check(NetMatchTimeSync.Validate(times), "valid objective clocks rejected");
            NetMatchTimeSync.Receive(times);
            Check(GameState.TeamTime[7] == 100, "slot-seven time not replicated");
            BinaryPrimitives.WriteSingleLittleEndian(times, Single.NaN);
            Check(!NetMatchTimeSync.Validate(times), "NaN objective clock accepted");
            GameState.TeamTime[7] = 0;
            NetHealthSync.BeginRoom();
        }
    }
}
