using System;
using System.Buffers.Binary;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;

internal static class NetArchitectureTests
{
    internal static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); }

    // Independent v16 fixture: constants deliberately do not come from the codec.
    internal static byte[] IntentFixture()
    {
        byte[] bytes = new byte[92];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), 1);
        bytes[20] = 255;
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21 + i * 4), 1u << i);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(53), 123.25f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(57), -42.5f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(61), 17.75f);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(65), 99);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(67), 25);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(69), 0x87654321);
        bytes[73] = 128;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(74), 51);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(76), 0x1020304050607080);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(84), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
        bytes[88] = 17; bytes[89] = 19; bytes[90] = 1; bytes[91] = 0x82;
        return bytes;
    }

    public static int Run()
    {
        try
        {
            var fixture = IntentFixture();
            var intent = IntentPacket.Read(fixture);
            Check(intent.Position == new Vector3(123.25f, -42.5f, 17.75f), "owner position survives wire");
            byte[] output = new byte[IntentPacket.FullSize]; intent.Write(output);
            Check(output.SequenceEqual(fixture), "v16 intent byte fixture");
            Check(intent.AckFrame == 0x87654321 && intent.AckSubFrame == 128 && IntentPacket.PressHistory == 8,
                "displayed world ACK and eight edges retained");
            string[] forbidden = { "MovementCommand", "MovementAck", "ProcessedMovementFrame", "MovementReconciliation",
                "PredictedMovementState", "IntentBundle", "SnapshotDelta", "SnapshotKeyframe" };
            Check(!typeof(IntentPacket).Assembly.GetTypes().Any(t => forbidden.Any(n => t.Name.Contains(n))),
                "no reverted production protocol structures");
            Check(NetUnlagged.DefaultMaxRewindFrames == 45 && NetUnlagged.HistoryFrames == 128 && NetUnlagged.PressAgeEnabled,
                "rewind defaults preserved");
            for (int count = 1; count <= PlayerEntity.SlotCapacity; count++)
            {
                byte[] bytes = new byte[SnapshotHeader.Size + count * PlayerState.Size];
                new SnapshotHeader { MatchId = 51, AuthorityEpoch = 4, Frame = 400, PlayerCount = (byte)count }.Write(bytes);
                for (int slot = 0; slot < count; slot++)
                    new PlayerState { SlotIndex = (byte)slot, SlotGeneration = 9, LifeId = 2,
                        Position = new Vector3(slot + 1, 2, 3), Facing = Vector3.UnitZ }
                        .Write(bytes.AsSpan(SnapshotHeader.Size + slot * PlayerState.Size));
                Check(SnapshotHeader.Read(bytes).PlayerCount == count, "independent snapshot header");
                for (int slot = count - 1; slot >= 0; slot--)
                    Check(PlayerState.Read(bytes.AsSpan(SnapshotHeader.Size + slot * PlayerState.Size)).Position.X == slot + 1,
                        "independent full snapshot decode");
            }
            HealthShotTests.Session();
            var player = HealthShotTests.Player(1);
            NetPlayerBridge.ApplyReportedPosition(player, new IntentPacket { Frame = 10, SlotGeneration = 10, LifeId = 7, Buttons = IntentButtons.InPlayState, Position = intent.Position });
            Check(player.Position == intent.Position, "production bridge accepts owner movement for authoritative collision");
            Console.WriteLine("PASS: architecture, owner position, full snapshots and v16 byte fixture");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetSession.Stop(); }
    }
}
