using System;
using System.Buffers.Binary;
using MphRead.Mods.Network;
namespace MphRead.NetTest;
internal static class Protocol18Tests
{
    public static int Run()
    {
        try
        {
            NetArchitectureTests.Check(SnapshotFast.MaximumEncodedSize <= 1200, "worst-case fast datagram MTU");
            int time = SnapshotHeader.Size + 8 * PlayerState.Size, world = time + 64;
            byte[] canonical = new byte[world + NetHealthSync.HeaderSize + NetHealthSync.MaxSpawns * NetHealthSync.EntrySize];
            new SnapshotHeader { MatchId = 65535, AuthorityEpoch = ulong.MaxValue, Frame = uint.MaxValue, PlayerCount = 8, Rng1 = 3, Rng2 = 4 }.Write(canonical);
            for (int slot = 0; slot < 8; slot++)
                new PlayerState { SlotIndex = (byte)slot, Flags = 255, SlotGeneration = 65535, LifeId = 65535,
                    Points = short.MaxValue, Kills = 65535, Deaths = 65535, Team = (byte)slot,
                    DamageEventId = 65535, Damage0 = new DamageEvent { Damage = 65535, EventId = 65535, Flags = 255 },
                    Damage1 = new DamageEvent { EventId = 65534 }, Damage2 = new DamageEvent { EventId = 65533 }, Damage3 = new DamageEvent { EventId = 65532 } }
                    .Write(canonical.AsSpan(SnapshotHeader.Size + slot * PlayerState.Size));
            BinaryPrimitives.WriteUInt16LittleEndian(canonical.AsSpan(world), 65535); canonical[world + 2] = 56;
            for (int i = 0; i < 56; i++) BinaryPrimitives.WriteInt16LittleEndian(canonical.AsSpan(world + 3 + i * 7), (short)i);
            var lanes = new NetReplicationLanes(); lanes.Prepare(canonical);
            var receiver = new NetReplicationReceiver();
            NetArchitectureTests.Check(receiver.Receive(PacketType.WorldState, lanes.World.AsSpan(0, lanes.WorldLength), 65535, ulong.MaxValue), "world independent");
            NetArchitectureTests.Check(receiver.Receive(PacketType.PlayerSlowState, lanes.Slow.AsSpan(0, lanes.SlowLength), 65535, ulong.MaxValue), "slow independent");
            NetArchitectureTests.Check(!receiver.Receive(PacketType.PlayerSlowState, lanes.Slow.AsSpan(0, lanes.SlowLength), 65535, ulong.MaxValue), "duplicate revision refused");
            var decoded = new byte[1472]; int length = receiver.Assemble(lanes.Fast.AsSpan(0, lanes.FastLength), decoded, 65535, ulong.MaxValue);
            NetArchitectureTests.Check(canonical.AsSpan().SequenceEqual(decoded.AsSpan(0, length)), "all status/damage/lifecycle/world/score bytes preserved");
            for (int i = 0; i < 100; i++) receiver.Assemble(lanes.Fast.AsSpan(0, lanes.FastLength), decoded, 65535, ulong.MaxValue);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) receiver.Assemble(lanes.Fast.AsSpan(0, lanes.FastLength), decoded, 65535, ulong.MaxValue);
            NetArchitectureTests.Check(GC.GetAllocatedBytesForCurrentThread() == before, "warmed fast decode allocation-free");
            Console.WriteLine($"PASS: protocol 18 lane round-trip, revision fencing, 0 decode allocations, {SnapshotFast.MaximumEncodedSize}B worst-case fast datagram"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
