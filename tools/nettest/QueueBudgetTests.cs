using System;
using System.Net;
using MphRead.Mods.Network;

namespace MphRead.NetTest;

internal static class QueueBudgetTests
{
    public static int Run()
    {
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, 42);
            var queue = new NetPacketQueue();
            byte[] intent = { (byte)PacketType.Intent }, loaded = { (byte)PacketType.MatchLoaded }, chat = { (byte)PacketType.Chat };
            for (int i = 0; i < 10000; i++) queue.TryEnqueue(new(endpoint, intent, intent.Length));
            NetArchitectureTests.Check(queue.Count == 1920 && queue.Drops == 8080, "realtime flood cannot consume reserve");
            for (int i = 0; i < 128; i++) NetArchitectureTests.Check(queue.TryEnqueue(new(endpoint, loaded, 1)), "critical reserve survives flood");
            NetArchitectureTests.Check(!queue.TryEnqueue(new(endpoint, loaded, 1)) && !queue.TryEnqueue(new(endpoint, chat, 1))
                && queue.HighWater == 2048, "absolute capacity");
            int controls = 0;
            while (queue.TryDequeue(NetPacketPriority.Critical, out var packet))
            { NetArchitectureTests.Check(packet.Type == PacketType.MatchLoaded, "control priority"); controls++; }
            NetArchitectureTests.Check(controls == 128, "critical work drains before state backlog");
            var buckets = new NetTokenBucket[8];
            int[] admitted = new int[8];
            for (int tick = 0; tick < 600; tick++)
            {
                for (int peer = 0; peer < 8; peer++)
                    for (int n = 0; n < (peer == 0 ? 167 : 1); n++)
                        if (buckets[peer].Take(tick * 1000.0 / 60, 180, 64)) admitted[peer]++;
            }
            NetArchitectureTests.Check(admitted[0] < 1900, "10000pps abusive peer limited");
            for (int peer = 1; peer < 8; peer++) NetArchitectureTests.Check(admitted[peer] == 600, "normal peer not charged to abusive bucket");
            NetArchitectureTests.Check(NetPumpBudget.Default == new NetPumpBudget(128, 256, 32), "bounded default pump");
            Console.WriteLine("PASS: flood capacity, critical reserve, per-peer rate limits and pump budgets"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
