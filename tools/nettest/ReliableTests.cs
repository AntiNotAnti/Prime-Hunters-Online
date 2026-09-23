using System;
using System.Collections.Generic;
using System.Net;
using MphRead.Mods.Network;

namespace MphRead.NetTest;

internal static class ReliableTests
{
    private readonly record struct Wire(bool ToReceiver, NetHeader Header, uint EventId);
    public static int Run()
    {
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, 42);
            NetConnection sender = new NetConnection(endpoint, 12), receiver = new NetConnection(endpoint, 12);
            var queue = new NetFaultQueue<Wire>(42, 160, 80, .05, .03, .01);
            var applied = new HashSet<uint>();
            for (int i = 0; i < 32; i++)
                NetArchitectureTests.Check(sender.Reliable.TryQueue(PacketType.LobbyCommandResult, BitConverter.GetBytes(i), 0, out _), "ordinary reliable capacity");
            NetArchitectureTests.Check(!sender.Reliable.TryQueue(PacketType.Roster, new byte[] { 80 }, 0, out _), "ordinary queue cannot consume reserve");
            for (int i = 0; i < 8; i++)
                NetArchitectureTests.Check(sender.Reliable.TryQueue(PacketType.SessionState, BitConverter.GetBytes(i), 0, out _), "critical reserve");
            for (double now = 0; now <= 14000; now += 1000.0 / 60)
            {
                while (sender.Reliable.TrySend(now, out var packet))
                    queue.Enqueue(now, new(true, sender.Send(packet.Type, now, NetHeaderFlags.Reliable, packet.EventId), packet.EventId));
                // Real-time packets continue throughout; a retry gets a fresh
                // datagram sequence after the first attempt leaves the ACK window.
                queue.Enqueue(now, new(true, sender.Send(PacketType.Intent, now), 0));
                while (queue.TryDequeue(now, out var wire))
                {
                    if (!wire.ToReceiver) { sender.Receive(wire.Header, now); continue; }
                    var sequence = receiver.Receive(wire.Header, now);
                    if ((wire.Header.Flags & NetHeaderFlags.Reliable) != 0)
                    {
                        if (sequence is SequenceResult.New or SequenceResult.Reordered && receiver.Reliable.Receive(wire.EventId))
                            NetArchitectureTests.Check(applied.Add(wire.EventId), "exactly once application");
                        queue.Enqueue(now, new(false, receiver.Send(0, now, NetHeaderFlags.AckOnly), 0));
                    }
                }
            }
            NetArchitectureTests.Check(applied.Count == 40 && sender.Reliable.Capture(14000).Pending == 0, "every critical and ordinary event delivered under impairment");
            NetArchitectureTests.Check(sender.Reliable.Capture(14000).HighWater == 40 && sender.Reliable.Capture(14000).Retransmissions > 0, "bounded retries measured");
            var full = new NetReliableChannel();
            for (int i = 0; i < 40; i++) full.TryQueue(PacketType.SessionState, BitConverter.GetBytes(i), 0, out _);
            NetArchitectureTests.Check(!full.TryQueue(PacketType.Bye, new byte[] { 99 }, 0, out _) && full.Failed, "critical exhaustion fails visibly");
            var span = new NetReliableChannel(); span.TryQueue(PacketType.Roster, new byte[] { 0 }, 0, out _);
            for (int i = 1; i < NetReliableChannel.History; i++)
            {
                NetArchitectureTests.Check(span.TryQueue(PacketType.Roster, BitConverter.GetBytes(i), 0, out uint id), "dedup span admits covered events");
                span.Acknowledge(id);
            }
            NetArchitectureTests.Check(!span.TryQueue(PacketType.Roster, new byte[] { 1 }, 0, out _)
                && span.Capture(1).SpanRefused == 1, "old pending event protects receiver dedup window");
            var expiry = new NetReliableChannel(); expiry.TryQueue(PacketType.MatchLoaded, new byte[] { 1 }, 0, out _);
            expiry.TrySend(15000, out _);
            NetArchitectureTests.Check(expiry.Failed, "bounded retransmit lifetime");
            foreach (var type in new[] { PacketType.Intent, PacketType.SlotIntent, PacketType.Snapshot, PacketType.Ping, PacketType.Pong })
                NetArchitectureTests.Check(!NetReliableChannel.IsReliable(type), "realtime never reliable");
            Console.WriteLine("PASS: reliability under 5% loss / 80ms jitter / 3% reorder / 1% duplicate; capacity, dedup span and expiry"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
