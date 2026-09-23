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
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(NetSession).Assembly.GetType("MphRead.Mods.Replay.ReplayWorldCheckpoint", throwOnError: true)!.TypeHandle);
            var counters = new NetPeerTelemetry();
            counters.Intent(10, NetIntentRejection.None, 100);
            counters.Intent(12, NetIntentRejection.None, 200);
            counters.Intent(12, NetIntentRejection.Duplicate, 201);
            var captured = counters.Capture(300);
            Check(captured.Accepted == 2 && captured.Duplicate == 1 && captured.FrameGaps == 1,
                "telemetry counts accepted and rejected separately");
            Check(counters.Capture(300) == captured, "telemetry reads are pure");
            counters.NewLife(); counters.Intent(1, NetIntentRejection.None, 400);
            Check(counters.Capture(500).FrameGaps == 1, "new life cannot inherit frame gaps");
            counters.Reset(); Check(counters.Capture().SilenceMilliseconds == null, "unavailable age is explicit");
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
            var bridge = (PlayerReplicationBridge)Activator.CreateInstance(typeof(PlayerReplicationBridge),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: new object[] { new OwnerHost() }, culture: null)!;
            var state = new PlayerState { SlotIndex = 1, SlotGeneration = 10, LifeId = 7,
                Health = 100, Flags = PlayerState.FlagSpawned, Facing = Vector3.UnitZ };
            var lives = new ushort[PlayerEntity.SlotCapacity]; lives[1] = 7;
            var applied = new bool[PlayerEntity.SlotCapacity]; applied[1] = true;
            HealthShotTests.Field(bridge, "_appliedLifeId", lives);
            HealthShotTests.Field(bridge, "_lifeApplied", applied);
            player.Position = new Vector3(100, 20, 30); player.Speed = new Vector3(1, 2, 3);
            Vector3 position = player.Position, speed = player.Speed, previous = player.PrevPosition;
            state.Position = new Vector3(-1000, 300, 200); state.Speed = new Vector3(-3, -2, -1);
            for (int i = 0; i < 180; i++) bridge.ApplyState(player, state, isLocal: true);
            Check(player.Position == position && player.Speed == speed && player.PrevPosition == previous,
                "same-life snapshots cannot correct owner's physical position or velocity");
            Console.WriteLine("PASS: architecture, owner position, full snapshots and v16 byte fixture");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetSession.Stop(); }
    }
    private sealed class OwnerHost : IPlayerReplicationHost
    {
        public bool IsReplica => true;
        public bool Active => true;
        public bool IsAuthority => false;
        public bool IsHost => false;
        public int LocalSlot => 1;
        public uint Frame => 1000;
        public bool Settling => false;
        public bool GameplayReady => true;
        public bool CanSpawn => true;
        public int Ping(int slot) => 100;
        public bool Matches(int slot, ushort generation, ushort life) => generation == 10 && life == 7;
        public bool TryGetIntent(int slot, out IntentPacket intent) { intent = default; return false; }
        public bool TryGetState(int slot, out PlayerState state) { state = default; return false; }
        public uint IntentAge(int slot) => 0;
        public void OnSpawn(PlayerEntity player) { }
        public void BeginLife(PlayerEntity player, in PlayerState state) { }
        public void Spawn(PlayerEntity player, in PlayerState state) { }
        public void ReplayDamage(PlayerEntity player, in PlayerState state) { }
        public void ReplayDeath(PlayerEntity player) { }
        public void NoteDeath(int slot) { }
        public int HealthFor(PlayerEntity player, int health, bool local) => health;
        public bool SamplePosition(int slot, bool presentation, out Vector3 position, out bool alt)
        { position = default; alt = false; return false; }
        public void StampAcknowledgement(ref IntentPacket intent) { }
    }

}
