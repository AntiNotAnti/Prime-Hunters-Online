using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Reflection;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest
{
    internal static partial class LifecycleTests
    {
        private delegate void MovementReceive(ReadOnlySpan<byte> bytes);
        private static void MovementOwnerValidation()
        {
            Session(local: true);
            var own = State(1, generation: 9); own.SlotIndex = 0;
            Deliver(Packet(1, own));
            Type type = typeof(NetSession).Assembly.GetType("MphRead.Mods.Network.NetMovementPrediction")!;
            var receive = type.GetMethod("Receive", PrivateStatic)!.CreateDelegate<MovementReceive>();
            MovementCall(type, "Record", null, new IntentPacket { Frame = 100, MatchId = 51, AuthorityEpoch = 4,
                SlotGeneration = 9, LifeId = 1, Buttons = IntentButtons.InPlayState });
            var bytes = new byte[23 + MovementState.Size];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 51);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(2), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 9);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 90);
            var state = new MovementState { Position = Vector3.One, Speed = Vector3.UnitX };
            state.Write(bytes.AsSpan(23)); receive(bytes);
            Check(MovementField<bool>(type, "_pending") && MovementField<uint>(type, "_pendingTick") == 100, "current-life owner state accepted");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 1000);
            state.Speed.Y = float.NaN; state.Write(bytes.AsSpan(23)); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 100, "bad owner state cannot poison server tick ordering");
            state.Speed = Vector3.Zero; state.Write(bytes.AsSpan(23));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 2); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 100, "owner state from another life rejected");
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 10); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 100, "owner state for another slot generation rejected");
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 9);
            bytes[22] = 1; receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 100, "owner packet for another slot rejected even with the same generation and life");
            bytes[22] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 101); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 100, "ack for unsent input rejected");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 90);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 101); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 101, "new server state with repeated input ack remains usable");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 99); receive(bytes);
            Check(MovementField<uint>(type, "_pendingTick") == 101, "reordered owner state rejected");
        }

        private static void MovementStateWire()
        {
            var state = new MovementState { Position = new(1, 2, 3), Speed = new(-.5f, .3f, .8f),
                Acceleration = Vector3.UnitY, BoostCharge = 42, FrozenTimer = 17, JumpPadControlLock = 18,
                MovementMorphTicks = 7, MovementBoostFrame = uint.MaxValue, Flags1 = 0x10000200,
                LastJumpPad = 32, StandingEntity = 14, StandingPart = 1, RollRight = Vector3.UnitX,
                RollUp = Vector3.UnitY, RollFacing = Vector3.UnitZ, RollContacts = 1 };
            var bytes = new byte[MovementState.Size]; state.Write(bytes);
            Check(MovementState.TryRead(bytes, out var read) && read.Position == state.Position
                && read.Speed == state.Speed && read.Acceleration == state.Acceleration
                && read.BoostCharge == 42 && read.FrozenTimer == 17 && read.JumpPadControlLock == 18
                && read.MovementMorphTicks == 7 && read.MovementBoostFrame == uint.MaxValue
                && read.RollRight == Vector3.UnitX && read.RollContacts == 1,
                "owner movement state round trips physics, impulse, freeze, roll and form state");
            Check(MovementState.Size + 23 + 1 < NetConfig.MaxPacketSize, "owner correction fits one datagram");
            Check(!MovementState.TryRead(bytes.AsSpan(0, bytes.Length - 1), out _), "truncated owner state rejected");
            state.Speed.X = float.NaN; state.Write(bytes);
            Check(!MovementState.TryRead(bytes, out _), "non-finite owner state rejected atomically");
            state.Speed = default; state.Slipperiness = int.MaxValue; state.Write(bytes);
            Check(!MovementState.TryRead(bytes, out _), "invalid traction table index rejected");
            MovementOwnerValidation();
        }

        private static void MovementCommandTransport()
        {
            IntentPacket Command(uint frame) => new() { Frame = frame, Aim = Vector3.UnitZ, Buttons = IntentButtons.MoveUp };
            var queue = new MovementCommandQueue();
            Check(queue.Enqueue(Command(10)) && queue.Enqueue(Command(12)) && queue.Enqueue(Command(11)), "queue accepts reordered commands");
            Check(!queue.Enqueue(Command(11)) && !queue.Enqueue(Command(9)) && !queue.Enqueue(Command(1000)), "queue rejects duplicates, retired and unbounded future input");
            Check(!queue.TryTake(out _) && !queue.TryTake(out _), "two-tick jitter buffer warms up");
            for (uint i = 10; i <= 12; i++) Check(queue.TryTake(out var command) && command.Frame == i && queue.LastProcessed == i, "one ordered input consumed per tick");
            Check(!queue.TryTake(out _) && queue.LastProcessed == 12, "idle queue never invents a processed command");
            for (int i = 0; i < MovementCommandQueue.Capacity; i++) queue.TryTake(out _);
            Check(queue.Enqueue(Command(1000)), "empty queue recovers after an outage longer than its input window");
            queue.TryTake(out _); queue.TryTake(out _);
            Check(queue.TryTake(out var recovered) && recovered.Frame == 1000, "outage recovery consumes one command, never a catch-up burst");
            queue.Reset();
            Check(queue.Enqueue(Command(uint.MaxValue)) && queue.Enqueue(Command(0)) && queue.Enqueue(Command(1)), "queue accepts sequence wrap");
            queue.TryTake(out _); queue.TryTake(out _);
            foreach (uint expected in new[] {uint.MaxValue, 0u, 1u}) Check(queue.TryTake(out var command) && command.Frame == expected, "sequence wrap preserves order");
            // Repeat actual commands in each datagram, then inject seeded delay,
            // loss, duplicates and reverse delivery. Receiver is production code.
            foreach (var profile in new[] { (0, 0, 0.0), (3, 2, .05), (6, 5, .15), (12, 8, .3) })
            {
                queue.Reset();
                var random = new Random(8128);
                var delivery = new SortedDictionary<int, List<IntentPacket>>();
                uint previous = 0;
                int consumed = 0;
                for (int tick = 1; tick < 2600; tick++)
                {
                    if (tick <= 2400 && random.NextDouble() >= profile.Item3)
                    {
                        int arrival = tick + profile.Item1 + random.Next(profile.Item2 + 1);
                        if (!delivery.TryGetValue(arrival, out var list)) delivery[arrival] = list = new();
                        for (int j = Math.Max(1, tick - 7); j <= tick; j++) list.Add(Command((uint)j));
                        if (tick % 17 == 0) list.Add(Command((uint)tick));
                    }
                    if (delivery.Remove(tick, out var due))
                        for (int j = due.Count - 1; j >= 0; j--) queue.Enqueue(due[j]);
                    if (queue.TryTake(out var command))
                    {
                        Check(command.Frame > previous, "faulted stream cannot repeat or reverse a simulated command");
                        previous = command.Frame; consumed++;
                    }
                }
                Check(consumed > 1500 && previous > 2350, $"faulted stream progresses with delay={profile.Item1}, jitter={profile.Item2}, loss={profile.Item3}");
            }
        }
    }
}
