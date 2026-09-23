using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Net;
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
            var claim = new HitClaimPacket();
            var envelope = new NetHeader(PacketType.Intent, NetHeaderFlags.AckValid, 7, 9, 8, 255);
            byte[] envelopeBytes = new byte[NetHeader.Size]; envelope.Write(envelopeBytes);
            byte[] health = new byte[NetHealthSync.HeaderSize + 56 * NetHealthSync.EntrySize];
            health[2] = 56;
            for (short i = 0; i < 56; i++) BinaryPrimitives.WriteInt16LittleEndian(health.AsSpan(3 + i * NetHealthSync.EntrySize), i);
            NetHealthSync.BeginRoom();
            var operations = new Dictionary<string, Action>
            {
                ["NetHeader.Read"] = () => { NetHeader.TryRead(envelopeBytes, out var value); _sink = value.Sequence; },
                ["NetHeader.Write"] = () => envelope.Write(envelopeBytes),
                ["HitClaim.Read"] = () => _sink = HitClaimPacket.Read(bytes).ClaimId,
                ["HitClaim.Write"] = () => claim.Write(bytes),
                ["Health.Validate56"] = () => _sink = NetHealthSync.Validate(health) ? 1u : 0u,
                ["Health.Receive56"] = () => NetHealthSync.Receive(health),
                ["Health.ComposeEmpty"] = () => _sink = (uint)NetHealthSync.Write(bytes),
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
            using var server = new NetTransport(0);
            using var client = new NetTransport(0);
            Protocol17Tests.Connect(server, client);
            var endpoint = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            for (int i = 0; i < 10000; i++) client.Send(endpoint, PacketType.Intent, intentBytes);
            long sendStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) client.Send(endpoint, PacketType.Intent, intentBytes);
            long sendBytes = GC.GetAllocatedBytesForCurrentThread() - sendStart;
            Console.WriteLine($"NetTransport.Send UDP: {sendBytes / 10000.0:F2} B/op (receive/fault/replay ownership excluded)");
            if (!reportOnly) NetArchitectureTests.Check(sendBytes == 0, "steady connected UDP send allocation regression");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
