using System;
using System.Net;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    // A frame is also the command sequence: exactly one command per 60 Hz tick.
    // No client-supplied delta time and no catch-up simulation on packet arrival.
    public sealed class MovementCommandQueue
    {
        public const int Capacity = 128;
        private readonly IntentPacket[] _commands = new IntentPacket[Capacity];
        private readonly bool[] _present = new bool[Capacity];
        private uint _next;
        private uint _newest;
        private int _warmup, _queued, _emptyTicks;
        public bool HasProcessed { get; private set; }
        public bool Started { get; private set; }
        public uint LastProcessed { get; private set; }
        public long Rejected { get; private set; }
        public long Missing { get; private set; }

        public bool Enqueue(in IntentPacket command)
        {
            if (!Started)
            {
                Started = true;
                _next = _newest = command.Frame;
                _warmup = 2;
            }
            int distance = unchecked((int)(command.Frame - _next));
            // After a sustained outage the sender may be beyond the window.
            // Rebase only an empty, idle queue; a flood cannot buy extra ticks.
            if (distance >= Capacity && _queued == 0 && _emptyTicks >= Capacity)
            {
                _next = _newest = command.Frame;
                _warmup = 2;
                distance = 0;
            }
            int index = (int)(command.Frame % Capacity);
            if (distance < 0 || distance >= Capacity || _present[index])
            {
                Rejected++;
                return false;
            }
            _commands[index] = command;
            _present[index] = true;
            _queued++;
            _emptyTicks = 0;
            if (NetLifecycleTracker.Newer(command.Frame, _newest)) _newest = command.Frame;
            return true;
        }

        public bool TryTake(out IntentPacket command)
        {
            command = default;
            if (!Started || (_warmup > 0 && --_warmup >= 0)) return false;
            int index = (int)(_next % Capacity);
            if (_present[index])
            {
                command = _commands[index];
                _present[index] = false;
                _queued--;
                _emptyTicks = 0;
                HasProcessed = true;
                LastProcessed = _next++;
                return true;
            }
            // The initial jitter buffer is the only delay. Retire at most one
            // lost input per tick; waiting again at every gap accumulates lag.
            if (NetLifecycleTracker.Newer(_newest, _next))
            {
                LastProcessed = _next++;
                HasProcessed = true;
                Missing++;
            }
            if (_queued == 0 && _emptyTicks < Capacity) _emptyTicks++;
            return false;
        }

        public void Reset()
        {
            Array.Clear(_present);
            Started = false;
            LastProcessed = _next = _newest = 0;
            _warmup = _queued = _emptyTicks = 0;
            HasProcessed = false;
            Rejected = Missing = 0;
        }
    }

    internal static class NetCommandStream
    {
        internal const int Redundancy = 8;
        private static readonly MovementCommandQueue[] Queues = CreateQueues();
        private static readonly IntentPacket[] Sent = new IntentPacket[Redundancy];
        private static readonly byte[] Wire = new byte[1 + Redundancy * IntentPacket.FullSize];
        private static int _sentCount, _sentHead;
        private static MovementCommandQueue[] CreateQueues()
        {
            var queues = new MovementCommandQueue[PlayerEntity.SlotCapacity];
            for (int i = 0; i < queues.Length; i++) queues[i] = new();
            return queues;
        }
        internal static bool ValidateBatch(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 1 || bytes[0] < 1 || bytes[0] > Redundancy
                || bytes.Length != 1 + bytes[0] * IntentPacket.FullSize) return false;
            uint previous = 0;
            for (int i = 0; i < bytes[0]; i++)
            {
                var input = IntentPacket.Read(bytes.Slice(1 + i * IntentPacket.FullSize, IntentPacket.FullSize));
                if (!input.HasValidAim || !input.HasValidMovement
                    || (i > 0 && !NetLifecycleTracker.Newer(input.Frame, previous))) return false;
                previous = input.Frame;
            }
            return true;
        }
        internal static void Send(NetTransport transport, IPEndPoint endpoint, in IntentPacket input)
        {
            if (_sentCount > 0)
            {
                var previous = Sent[(_sentHead + Redundancy - 1) % Redundancy];
                if (previous.MatchId != input.MatchId || previous.AuthorityEpoch != input.AuthorityEpoch
                    || previous.SlotGeneration != input.SlotGeneration || previous.LifeId != input.LifeId)
                    _sentCount = _sentHead = 0;
                else if (!NetLifecycleTracker.Newer(input.Frame, previous.Frame)) return;
            }
            Sent[_sentHead] = input;
            _sentHead = (_sentHead + 1) % Redundancy;
            _sentCount = Math.Min(_sentCount + 1, Redundancy);
            Wire[0] = (byte)_sentCount;
            for (int i = 0; i < _sentCount; i++)
                Sent[(_sentHead + Redundancy - _sentCount + i) % Redundancy]
                    .Write(Wire.AsSpan(1 + i * IntentPacket.FullSize, IntentPacket.FullSize));
            transport.Send(endpoint, PacketType.InputCommands, Wire.AsSpan(0, 1 + _sentCount * IntentPacket.FullSize));
        }
        internal static void Enqueue(int slot, in IntentPacket input)
        {
            if ((uint)slot < Queues.Length && NetPlayerLifecycle.AcceptIntent(slot, input)) Queues[slot].Enqueue(input);
        }
        internal static uint Ack(int slot) => Queues[slot].LastProcessed;
        internal static bool Enabled(int slot) => Queues[slot].Started;
        internal static bool HasProcessed(int slot) => Queues[slot].HasProcessed;
        internal static void SelectForTick(int slot)
        {
            if ((uint)slot >= Queues.Length) return;
            if (Queues[slot].TryTake(out var command))
            {
                NetSession.AcceptSlotIntent(slot, command);
                Span<byte> bytes = stackalloc byte[IntentPacket.FullSize];
                command.Write(bytes);
                ServerReplayRecorder.RecordSlotIntent(slot, bytes);
            }
        }
        internal static void ResetSlot(int slot)
        {
            if ((uint)slot < Queues.Length) Queues[slot].Reset();
            if (slot == NetSession.LocalSlot) _sentCount = _sentHead = 0;
        }
        internal static void Reset()
        {
            foreach (var queue in Queues) queue.Reset();
            _sentCount = _sentHead = 0;
        }
    }
}
