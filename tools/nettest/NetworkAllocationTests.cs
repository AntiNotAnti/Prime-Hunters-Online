using System;
using System.Collections.Generic;
using MphRead.Mods.Network;

namespace MphRead.NetTest;

internal static class NetworkAllocationTests
{
    private static uint _sink;
    public static int Run(bool reportOnly = false)
    {
        try
        {
            byte[] intentBytes = NetArchitectureTests.IntentFixture();
            byte[] bytes = new byte[NetConfig.MaxPacketSize];
            var intent = IntentPacket.Read(intentBytes);
            var player = new PlayerState();
            var header = new SnapshotHeader { PlayerCount = 8 };
            var operations = new Dictionary<string, Action>
            {
                ["IntentPacket.Read"] = () => _sink = IntentPacket.Read(intentBytes).Frame,
                ["IntentPacket.Write"] = () => intent.Write(bytes),
                ["PlayerState.Read"] = () => _sink = PlayerState.Read(bytes).LifeId,
                ["PlayerState.Write"] = () => player.Write(bytes),
                ["Snapshot.Compose"] = () => { header.Write(bytes); for (int i = 0; i < 8; i++) player.Write(bytes.AsSpan(SnapshotHeader.Size + i * PlayerState.Size)); },
                ["Snapshot.Decode"] = () => { _sink = SnapshotHeader.Read(bytes).Frame; for (int i = 0; i < 8; i++) _sink += PlayerState.Read(bytes.AsSpan(SnapshotHeader.Size + i * PlayerState.Size)).LifeId; }
            };
            foreach (var (name, action) in operations)
            {
                for (int i = 0; i < 10000; i++) action();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) action();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine($"{name}: {allocated / 10000.0:F2} B/op");
                if (!reportOnly) NetArchitectureTests.Check(allocated == 0, $"{name} allocation regression: {allocated}");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
