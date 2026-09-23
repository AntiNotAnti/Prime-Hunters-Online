using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayAuthorityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var world = new ReplayAuthorityWorld
        {
            MatchId = 7, Epoch = 9, Tick = 120, Prime = new(2, 3, 4), Phase = MatchState.GameOver,
            MatchTime = 3, EndCause = ReplayEndCause.Kill, EndingKill = new(7, 9, 120, 4, 2, 3, 1, 6, 8),
            Flags = [new(11, new(1, 2, 3), new(2, 3, 4), new(255, 0, 0), false, true, 0, -.01f)],
            Nodes = [new(12, 1, 2, 4, new(2, 3, 4), .5f, .2f, .3f, 80, 2, true, false)],
            Pickups = Enumerable.Range(0, 512).Select(i => new ReplayPickupState((short)i, new(i % 2 == 0, true, 5, 10, -1))).ToArray(),
            Drops = [new(ItemType.MissileBig, new(3, 4, 5), 800)],
            Doors = [new(14, DoorFlags.Closed, true, false, true)]
        };
        byte[] bytes = world.Encode();
        var decoded = ReplayAuthorityWorld.Decode(bytes);
        check(decoded.Encode().AsSpan().SequenceEqual(bytes), "world value codec roundtrip");
        var packets = ReplayAuthorityWire.Packets(world).ToArray();
        check(packets.Length > 1 && packets.All(p => p.Length <= NetConfig.MaxPacketSize), "world fragments obey MTU");
        var wire = new ReplayAuthorityWire(); ReplayAuthorityWorld? completed = null;
        for (int i = packets.Length - 1; i >= 0; i--)
        {
            completed = wire.Accept(packets[i].AsSpan(1), 7, 9);
            check((i == 0) == (completed != null), "reordered parts publish atomically");
            check(wire.Accept(packets[i].AsSpan(1), 7, 9) == null, "duplicate part ignored");
        }
        check(completed!.Encode().AsSpan().SequenceEqual(bytes), "assembled state unchanged");
        wire.Reset();
        check(wire.Accept(packets[0].AsSpan(1), 8, 9) == null, "wrong match ignored");
        check(wire.Accept(packets[0].AsSpan(1), 7, 10) == null, "wrong authority ignored");
        wire.Accept(packets[0].AsSpan(1), 7, 9);
        var newer = new ReplayAuthorityWorld { MatchId = 7, Epoch = 9, Tick = 121 };
        check(wire.Accept(ReplayAuthorityWire.Packets(newer).Single().AsSpan(1), 7, 9)?.Tick == 121, "new full state heals lost parts");
        check(wire.Accept(packets[^1].AsSpan(1), 7, 9) == null, "stale part cannot overwrite newer state");
        byte[] oversized = (byte[])packets[0].Clone(); BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(16), int.MaxValue);
        check(wire.Accept(oversized.AsSpan(1), 7, 9) == null, "oversized assembly rejected before allocation");
        for (int i = 0; i < bytes.Length; i += 53)
        {
            bool rejected = false;
            try { ReplayAuthorityWorld.Decode(bytes.AsSpan(0, i)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "truncated world rejected");
        }
        byte[] invalid = (byte[])bytes.Clone(); invalid[0] = 255;
        bool bad = false; try { ReplayAuthorityWorld.Decode(invalid); } catch (InvalidDataException) { bad = true; }
        check(bad, "unknown world contract rejected");
        var match = new MatchStatePacket { MatchId = 7, AuthorityEpoch = 9, RoomKey = "TEST ARENA", NextRoomKey = "", Mode = (byte)GameMode.Battle };
        byte[] matchBytes = new byte[1 + MatchStatePacket.Size]; matchBytes[0] = (byte)PacketType.MatchState; match.Write(matchBytes.AsSpan(1));
        var state = new ReplayReplicaState(); state.Accept(matchBytes, 0);
        foreach (var packet in packets) state.Accept(packet, 20);
        state.AuthorityAppliedTick = 120;
        var restored = new ReplayReplicaState(); restored.RestoreCheckpoint(state.CaptureCheckpoint());
        check(restored.AuthorityWorld!.Encode().AsSpan().SequenceEqual(bytes) && restored.AuthorityAppliedTick == 120, "decoder retains applied world and causal end");
        foreach (var packet in ReplayAuthorityWire.Packets(new ReplayAuthorityWorld { MatchId = 7, Epoch = 9, Tick = 119 })) restored.Accept(packet, 21);
        check(restored.AuthorityWorld.Tick == 120, "restored decoder rejects older world");
        var recorder = new ReplayRecorder(); recorder.AcceptMatch(match, 0); int accepted = 0;
        recorder.Accepted += record => { if (record.Kind == ReplayFactKind.AuthorityWorld) accepted++; };
        recorder.AcceptWorld(world, 20);
        check(accepted == packets.Length, "accepted world uses common recorder");
    }
}
