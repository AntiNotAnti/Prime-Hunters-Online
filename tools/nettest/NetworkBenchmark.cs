using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;

// Seeded virtual-time codec/queue workload. Does not claim to simulate an asset-backed Scene.
internal static class NetworkBenchmark
{
    internal record Scenario(int Players, int Rtt, int Jitter, double Loss, double Reorder, double Duplicate);
    private readonly record struct Datagram(int Peer, int Kind, uint Frame);
    internal static IEnumerable<Scenario> Matrix(bool extended)
    {
        int[] rtts = { 0, 50, 100, 150, 250, 320, 400 };
        if (extended)
        {
            foreach (int p in new[] { 2, 4, 8 }) foreach (int r in rtts)
            foreach (int j in new[] { 0, 20, 40, 80 }) foreach (double l in new[] { 0, .01, .02, .05 })
            foreach (double o in new[] { 0, .01, .03 }) foreach (double d in new[] { 0, .01 })
                yield return new(p, r, j, l, o, d);
        }
        else
        {
            for (int i = 0; i < 14; i++) yield return new(new[] { 2, 4, 8 }[i % 3], rtts[i % 7],
                new[] { 0, 20, 40, 80 }[i % 4], new[] { 0, .01, .02, .05 }[i % 4],
                new[] { 0, .01, .03 }[i % 3], i % 2 * .01);
            yield return new(8, 0, 0, 0, 0, 0);
            yield return new(8, 320, 80, .02, .01, .01);
        }
    }

    private static object Measure(Scenario s)
    {
        const int ticks = 600;
        var queue = new NetFaultQueue<Datagram>(8128, s.Rtt / 2.0, s.Jitter, s.Loss, s.Reorder, s.Duplicate);
        uint[,] newest = new uint[4, s.Players];
        bool[,] seen = new bool[4, s.Players];
        long[] gaps = new long[4], duplicate = new long[4], reorder = new long[4];
        long sent = 0, received = 0, bytesSent = 0, bytesReceived = 0;
        int high = 0, maxPacket = 0;
        var timings = new List<double>(ticks * s.Players * 3);
        byte[] buffer = new byte[NetConfig.MaxPacketSize];
        byte[] canonical = new byte[NetConfig.MaxPacketSize];
        var lanes = new NetReplicationLanes();
        var receivers = Enumerable.Range(0, s.Players).Select(_ => new NetReplicationReceiver()).ToArray();
        int Canonical(uint frame)
        {
            new SnapshotHeader { Frame = frame, MatchId = 1, AuthorityEpoch = 1, PlayerCount = (byte)s.Players }.Write(canonical);
            for (int slot = 0; slot < s.Players; slot++) new PlayerState { SlotIndex = (byte)slot, SlotGeneration = 9, LifeId = 2,
                Position = new Vector3(1, 2, 3), Facing = Vector3.UnitZ }.Write(canonical.AsSpan(SnapshotHeader.Size + slot * PlayerState.Size));
            int at = SnapshotHeader.Size + s.Players * PlayerState.Size;
            canonical.AsSpan(at, 64 + NetHealthSync.HeaderSize).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(canonical.AsSpan(at + 64), 1);
            return at + 64 + NetHealthSync.HeaderSize;
        }
        lanes.Prepare(canonical.AsSpan(0, Canonical(1)));
        int PacketLength(int kind) => NetHeader.Size + (kind == 0 ? IntentPacket.FullSize : kind == 1
            ? SnapshotHeader.Size + s.Players * SnapshotFast.PlayerSize : kind == 2 ? lanes.SlowLength : lanes.WorldLength);
        var intent = IntentPacket.Read(NetArchitectureTests.IntentFixture());
        var state = new PlayerState { SlotGeneration = 9, LifeId = 2, Position = new Vector3(1, 2, 3), Facing = Vector3.UnitZ };
        // Warm the exact codecs before allocation measurement.
        for (int i = 0; i < 1000; i++) { intent.Write(buffer); _ = IntentPacket.Read(buffer); state.Write(buffer); _ = PlayerState.Read(buffer); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var telemetry = new NetTransportTelemetry();
        var clock = Stopwatch.StartNew();
        for (uint frame = 1; frame <= ticks + 120; frame++)
        {
            double now = frame * 1000.0 / 60;
            if (frame <= ticks)
                for (int peer = 0; peer < s.Players; peer++)
                    for (int kind = 0; kind < 4; kind++)
                    {
                        if (kind == 2 && frame % 6 != 0 || kind == 3 && frame % 15 != 0) continue;
                        var packet = new Datagram(peer, kind, frame);
                        queue.Enqueue(now, packet);
                        int length = PacketLength(kind);
                        telemetry.Sent(length);
                        sent++; bytesSent += length; maxPacket = Math.Max(maxPacket, length);
                    }
            high = Math.Max(high, queue.Count); telemetry.Queue(queue.Count);
            int processed = 0;
            while (queue.TryDequeue(now, out Datagram packet))
            {
                long start = Stopwatch.GetTimestamp();
                int kind = packet.Kind;
                if (kind == 0)
                {
                    intent.Frame = packet.Frame; intent.Write(buffer);
                    var decoded = IntentPacket.Read(buffer.AsSpan(0, IntentPacket.FullSize));
                    NetArchitectureTests.Check(decoded.Position == intent.Position && decoded.Frame == packet.Frame, "benchmark intent mismatch");
                    bytesReceived += IntentPacket.FullSize + NetHeader.Size;
                }
                else if (kind == 1)
                {
                    int size = Canonical(packet.Frame);
                    int fast = SnapshotFast.Write(canonical.AsSpan(0, size), buffer);
                    int assembled = receivers[packet.Peer].Assemble(buffer.AsSpan(0, fast), canonical, 1, 1);
                    NetArchitectureTests.Check(assembled > 0, "independent fast decode");
                    for (int slot = 0; slot < s.Players; slot++)
                    {
                        var decoded = PlayerState.Read(canonical.AsSpan(SnapshotHeader.Size + slot * PlayerState.Size));
                        NetArchitectureTests.Check(decoded.SlotIndex == slot && decoded.Position == state.Position, "benchmark snapshot mismatch");
                    }
                    bytesReceived += PacketLength(kind);
                }
                else
                {
                    byte[] data = kind == 2 ? lanes.Slow : lanes.World;
                    int size = kind == 2 ? lanes.SlowLength : lanes.WorldLength;
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), packet.Frame);
                    receivers[packet.Peer].Receive(kind == 2 ? PacketType.PlayerSlowState : PacketType.WorldState, data.AsSpan(0, size), 1, 1);
                    bytesReceived += PacketLength(kind);
                }
                uint previous = newest[kind, packet.Peer];
                if (seen[kind, packet.Peer])
                {
                    if (packet.Frame == previous) duplicate[kind]++;
                    else if (!NetLifecycleTracker.Newer(packet.Frame, previous)) reorder[kind]++;
                    else gaps[kind] += packet.Frame - previous - 1;
                }
                if (!seen[kind, packet.Peer] || NetLifecycleTracker.Newer(packet.Frame, previous)) newest[kind, packet.Peer] = packet.Frame;
                seen[kind, packet.Peer] = true;
                telemetry.Received(PacketLength(kind));
                telemetry.Processed(Stopwatch.GetTimestamp() - start);
                received++; processed++;
                timings.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
            NetArchitectureTests.Check(processed < 2048 && queue.Count < 2048, "queue overflow");
        }
        long allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        clock.Stop(); timings.Sort();
        NetArchitectureTests.Check(queue.Count == 0 && maxPacket <= 1200, "drained queue and packet budget");
        return new { telemetry = telemetry.Capture(), scenario = s, durationSeconds = 10, packetsSent = sent, packetsReceived = received, bytesSent, bytesReceived,
            intentPackets = ticks * s.Players, snapshotPackets = ticks * s.Players, slowPackets = ticks / 6 * s.Players, worldPackets = ticks / 15 * s.Players, controlPackets = 0,
            transportQueueHighWater = high, injectedDrops = queue.Dropped, transportDrops = 0, coalescedPackets = 0,
            meanProcessingMicroseconds = timings.Average(), p95ProcessingMicroseconds = timings[(int)(timings.Count * .95)],
            p99ProcessingMicroseconds = timings[(int)(timings.Count * .99)],
            schedulerTicksDue = ticks, schedulerTicksCompleted = ticks, schedulerTicksDropped = 0,
            intentFrameGaps = gaps[0], intentDuplicates = duplicate[0], intentReordered = reorder[0],
            snapshotFrameGaps = gaps[1], snapshotDuplicates = duplicate[1], snapshotReordered = reorder[1],
            managedAllocations = allocationBytes, gen0 = GC.CollectionCount(0) - g0, gen1 = GC.CollectionCount(1) - g1,
            gen2 = GC.CollectionCount(2) - g2, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, maxPacketBytes = maxPacket,
            unavailable = new[] { "asset-backed simulation ticks", "smoothing", "rewind", "CPU process time" } };
    }

    public static int Run(string[] args)
    {
        try
        {
            var scenarios = Matrix(Array.IndexOf(args, "--extended") >= 0).Select(Measure).ToArray();
            int output = Array.IndexOf(args, "--network-benchmark-json");
            if (output >= 0)
            {
                if (output + 1 >= args.Length) throw new ArgumentException("Missing JSON path");
                string path = Path.GetFullPath(args[output + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var start = new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true };
                using var git = Process.Start(start)!; string commit = git.StandardOutput.ReadToEnd().Trim(); git.WaitForExit();
                File.WriteAllText(path, JsonSerializer.Serialize(new { commit, protocol = NetConfig.ProtocolVersion,
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, seed = 8128,
                    scope = "virtual-time production codec and impairment queue; timings are machine dependent", scenarios },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine($"PASS: {scenarios.Length} seeded network benchmark scenarios"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
