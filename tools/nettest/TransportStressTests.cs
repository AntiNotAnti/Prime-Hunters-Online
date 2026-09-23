using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using MphRead.Mods.Network;

namespace MphRead.NetTest;
internal static class TransportStressTests
{
    public static int Run()
    {
        var clients = new List<NetTransport>();
        NetLag.Configure("0"); NetLag.ConfigureLoss("0");
        using var server = new NetTransport(0);
        var address = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
        var endpoints = new IPEndPoint[8];
        var controls = new HashSet<int>[8];
        var received = new int[8];
        var frames = new uint[8];
        long intents = 0;
        try
        {
            NetLag.ConfigureSeed("431"); NetLag.ConfigureReorder("3%"); NetLag.ConfigureDuplicate("1%");
            int[] latencies = { 0, 50, 100, 150, 250, 320, 400, 150 };
            for (int i = 0; i < 8; i++)
            {
                NetLag.Configure($"{latencies[i]}:{(i < 2 ? 0 : 40)}");
                NetLag.ConfigureLoss(i == 0 ? "0" : "5%");
                var client = new NetTransport(0); clients.Add(client); controls[i] = new();
                endpoints[i] = new(IPAddress.Loopback, client.LocalPort);
                byte[] hello = new byte[6]; hello[0] = NetConfig.ProtocolVersion; hello[1] = 255;
                BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(2), (uint)(100 + i)); client.Send(address, PacketType.Hello, hello);
                byte[] welcome = new byte[17]; welcome[0] = (byte)i;
                BinaryPrimitives.WriteUInt32LittleEndian(welcome.AsSpan(1), (uint)(100 + i));
                server.Send(endpoints[i], PacketType.Welcome, welcome);
            }
            void Pump(bool stalled)
            {
                foreach (var packet in server.Drain()) if (packet.Type == PacketType.Intent) intents++;
                for (int i = 0; i < clients.Count; i++)
                {
                    if (stalled && i == 7) continue; // a loading/render stall cannot block other peers
                    foreach (var packet in clients[i].Drain())
                    {
                        if (packet.Type == PacketType.SessionState)
                            NetArchitectureTests.Check(controls[i].Add(BinaryPrimitives.ReadInt32LittleEndian(packet.Payload)), "control applied once on actual impaired UDP");
                        if (packet.Type == PacketType.Snapshot)
                        {
                            var snapshot = SnapshotHeader.Read(packet.Payload);
                            if (SequenceMath.Newer(snapshot.Frame, frames[i])) { frames[i] = snapshot.Frame; received[i]++; }
                        }
                    }
                }
            }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 5)
            {
                Pump(false); bool ready = true;
                foreach (var client in clients) ready &= client.ConnectionStats(address).HasValue;
                if (ready) break;
                Thread.Sleep(2);
            }
            foreach (var client in clients) NetArchitectureTests.Check(client.ConnectionStats(address).HasValue, "all eight admitted");
            for (int i = 0; i < clients.Count; i++) for (int control = 1; control <= 16; control++)
                server.Send(endpoints[i], PacketType.SessionState, BitConverter.GetBytes(control));
            clock.Restart(); uint frame = 0; double nextTick = 0;
            byte[] intentBytes = NetArchitectureTests.IntentFixture();
            byte[] snapshotBytes = new byte[SnapshotHeader.Size + 8 * PlayerState.Size];
            while (clock.Elapsed.TotalSeconds < 8)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (now >= nextTick && now < 4000)
                {
                    nextTick = now + 1000.0 / 60; frame++;
                    BinaryPrimitives.WriteUInt32LittleEndian(intentBytes, frame);
                    new SnapshotHeader { Frame = frame, MatchId = 1, AuthorityEpoch = 1, PlayerCount = 8 }.Write(snapshotBytes);
                    for (int i = 0; i < 8; i++)
                    {
                        clients[i].Send(address, PacketType.Intent, intentBytes);
                        server.Send(endpoints[i], PacketType.Snapshot, snapshotBytes);
                    }
                }
                Pump(now is >= 500 and < 900);
                Thread.Sleep(2);
            }
            NetArchitectureTests.Check(intents > 800, "realtime continues while reliable events retry");
            for (int i = 0; i < 8; i++)
            {
                NetArchitectureTests.Check(controls[i].Count == 16 && received[i] > 60, "every peer receives all control and advancing state");
                var telemetry = clients[i].Telemetry.Capture();
                NetArchitectureTests.Check(telemetry.QueueHighWater <= 2048 && telemetry.QueueDrops == 0, "bounded queues without overflow");
                var reliability = server.ReliableStats(endpoints[i]);
                NetArchitectureTests.Check(reliability?.Pending == 0, "all controls acknowledged after impairment");
                Console.WriteLine($"peer {i}: configured RTT={latencies[i]}, snapshots={received[i]}, queueHigh={telemetry.QueueHighWater}, ACK RTT={clients[i].ConnectionStats(address)?.RttMilliseconds:F1}");
            }
            clients[6].Send(address, PacketType.Bye, ReadOnlySpan<byte>.Empty);
            clients[6].Dispose();
            NetArchitectureTests.Check(clients[6].UnacknowledgedCloseEvents == 0, "graceful close keeps retries and ACK reception alive");
            Console.WriteLine($"PASS: eight mixed-quality UDP peers, {intents} intents, 128 exact-once controls, 400ms client pump stall");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var client in clients) client.Dispose();
            NetLag.Configure("0"); NetLag.ConfigureLoss("0"); NetLag.ConfigureReorder("0"); NetLag.ConfigureDuplicate("0");
        }
    }
}
