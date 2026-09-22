using System;
using System.Buffers.Binary;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    internal static class NetMovementPrediction
    {
        private static readonly MovementState[] LastOwnerState = new MovementState[PlayerEntity.SlotCapacity];
        private static readonly uint[] LastOwnerTick = new uint[PlayerEntity.SlotCapacity];
        private static readonly ushort[] LastOwnerLife = new ushort[PlayerEntity.SlotCapacity];
        private static readonly ushort[] LastOwnerGeneration = new ushort[PlayerEntity.SlotCapacity];
        internal const int HeaderSize = 23;
        internal const int PacketSize = HeaderSize + MovementState.Size;
        private const int Capacity = 128;
        private static readonly IntentPacket[] Commands = new IntentPacket[Capacity];
        private static int _head, _count;
        private static uint _lastSent, _lastServerTick, _pendingTick, _pendingAck, _lastAck;
        private static ushort _generation, _life;
        private static MovementState _pendingState;
        private static bool _pending, _overflow;
        internal static bool Active { get; private set; }
        internal static long ReplayedCommands { get; private set; }
        internal static long HistoryOverflows { get; private set; }
        internal static float LastCorrectionDistance { get; private set; }

        internal static void Record(in IntentPacket input)
        {
            if (_generation != input.SlotGeneration || _life != input.LifeId)
            {
                Reset();
                _generation = input.SlotGeneration; _life = input.LifeId;
            }
            if (_count > 0 && !NetLifecycleTracker.Newer(input.Frame, _lastSent)) return;
            _lastSent = input.Frame;
            if (!input.Buttons.HasFlag(IntentButtons.InPlayState)) { _count = 0; _overflow = false; return; }
            if (_count == Capacity)
            {
                _overflow = true;
                HistoryOverflows++;
            }
            Commands[_head] = input;
            _head = (_head + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
        }

        internal static int WriteOwner(int slot, Span<byte> bytes)
        {
            if ((uint)slot >= PlayerEntity.Players.Count || !NetCommandStream.HasProcessed(slot)) return 0;
            var player = PlayerEntity.Players[slot];
            if (!player.ModIsInPlay) return 0;
            var state = player.ModCaptureMovement();
            var previous = LastOwnerState[slot];
            bool immediate = LastOwnerLife[slot] != NetPlayerLifecycle.Get(slot)
                || LastOwnerGeneration[slot] != NetPlayerLifecycle.Generation(slot)
                || Vector3.DistanceSquared(state.Position, previous.Position) > 4
                || Vector3.DistanceSquared(state.Speed, previous.Speed) > 0.04f
                || state.TeleportSerial != previous.TeleportSerial
                || state.MovementBoostFrame != previous.MovementBoostFrame
                || (state.FrozenTimer == 0) != (previous.FrozenTimer == 0)
                || ((state.Flags1 ^ previous.Flags1) & (uint)(PlayerFlags1.AltForm | PlayerFlags1.Morphing | PlayerFlags1.Unmorphing)) != 0;
            if (!immediate && unchecked(NetSession.NetFrame - LastOwnerTick[slot]) < 2) return 0;
            LastOwnerState[slot] = state;
            LastOwnerTick[slot] = NetSession.NetFrame;
            LastOwnerLife[slot] = NetPlayerLifecycle.Get(slot);
            LastOwnerGeneration[slot] = NetPlayerLifecycle.Generation(slot);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, NetSession.CurrentMatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[2..], NetSession.AuthorityEpoch);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], NetPlayerLifecycle.Generation(slot));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], NetPlayerLifecycle.Get(slot));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[14..], NetSession.NetFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[18..], NetCommandStream.Ack(slot));
            bytes[22] = (byte)slot;
            state.Write(bytes[HeaderSize..]);
            return PacketSize;
        }

        internal static void Receive(ReadOnlySpan<byte> bytes)
        {
            int slot = NetSession.LocalSlot;
            if (slot < 0 || bytes.Length != PacketSize || bytes[22] != slot || NetSession.IsAuthority
                || !NetSession.MatchesStream(BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[2..]))
                || !NetPlayerLifecycle.Matches(slot, BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]))
                || !MovementState.TryRead(bytes[HeaderSize..], out var state)) return;
            uint tick = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
            uint ack = BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]);
            if ((Active && !NetLifecycleTracker.Newer(tick, _lastServerTick))
                || (_pending && !NetLifecycleTracker.Newer(tick, _pendingTick))
                || (Active && NetLifecycleTracker.Newer(_lastAck, ack))
                || (_pending && NetLifecycleTracker.Newer(_pendingAck, ack))
                || NetLifecycleTracker.Newer(ack, _lastSent)) return;
            _pendingState = state;
            _pendingTick = tick; _pendingAck = ack;
            _pending = true;
        }

        internal static void Apply(PlayerEntity player)
        {
            player.MovementVisualOffset *= 0.75f;
            if (!_pending || !player.ModIsInPlay
                || NetLifecycleTracker.Newer(_pendingTick, NetSession.LastSnapshotFrame)) return;
            _pending = false;
            Active = true;
            _lastServerTick = _pendingTick;
            _lastAck = _pendingAck;
            Vector3 before = player.Position;
            uint previousTeleport = player.ModCaptureMovement().TeleportSerial;
            // Remove only acknowledged commands; a repeated input ack with a
            // newer server tick still carries new impulses or freeze state.
            while (_count > 0)
            {
                int oldest = (_head + Capacity - _count) % Capacity;
                if (NetLifecycleTracker.Newer(Commands[oldest].Frame, _pendingAck)) break;
                _count--;
                _overflow = false;
            }
            player.ModRestoreMovement(_pendingState);
            if (!_overflow)
            {
                for (int i = 0; i < _count; i++)
                    player.ModReplayMovement(Commands[(_head + Capacity - _count + i) % Capacity]);
                ReplayedCommands += _count;
            }
            // An incomplete history must never be replayed as if it were
            // complete. Adopt the server and resume once its ack catches up.
            LastCorrectionDistance = (player.Position - before).Length;
            if (LastCorrectionDistance < 2 && _pendingState.MovementMorphTicks == 0 && previousTeleport == _pendingState.TeleportSerial)
                player.MovementVisualOffset += before - player.Position;
            else player.MovementVisualOffset = Vector3.Zero;
            if (player.MovementVisualOffset.LengthSquared > 4) player.MovementVisualOffset = Vector3.Zero;
            player.ModFinishMovementReplay();
        }

        internal static void Reset()
        {
            Array.Clear(LastOwnerTick);
            Array.Clear(LastOwnerLife);
            Array.Clear(LastOwnerGeneration);
            _head = _count = 0;
            _lastSent = _lastServerTick = _pendingTick = _pendingAck = _lastAck = 0;
            _pending = _overflow = Active = false;
            _generation = _life = 0;
            ReplayedCommands = HistoryOverflows = 0;
            LastCorrectionDistance = 0;
        }
    }
}
