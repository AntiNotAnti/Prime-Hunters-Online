using System;
using System.Collections.Generic;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    internal readonly record struct ReplayNetworkSnapshot(
        long Packets,
        long Bytes,
        int Snapshots,
        int Intents,
        uint MaxSnapshotGapFrames,
        int MaxPacketsInFrame,
        uint LastSnapshotFrame,
        IReadOnlyDictionary<PacketType, long> PacketTypes);

    /// <summary>
    /// Replay-side packet telemetry. It describes the stream that is actually being
    /// injected into NetSession, which makes packet gaps/bursts visible while debugging
    /// a recorded desync without changing playback behavior.
    /// </summary>
    internal static class ReplayNetworkDiagnostics
    {
        private static readonly Dictionary<PacketType, long> Counts = new();
        private static long _packets;
        private static long _bytes;
        private static int _snapshots;
        private static int _intents;
        private static uint _lastSnapshot;
        private static bool _hasSnapshot;
        private static uint _maxSnapshotGap;
        private static uint _burstFrame;
        private static int _burst;
        private static int _maxBurst;

        public static void Reset()
        {
            Counts.Clear();
            _packets = 0;
            _bytes = 0;
            _snapshots = 0;
            _intents = 0;
            _lastSnapshot = 0;
            _hasSnapshot = false;
            _maxSnapshotGap = 0;
            _burstFrame = UInt32.MaxValue;
            _burst = 0;
            _maxBurst = 0;
        }

        public static void OnPacket(uint frame, ReadOnlySpan<byte> packet)
        {
            if (packet.Length == 0) return;
            PacketType type = (PacketType)packet[0];
            Counts[type] = Counts.TryGetValue(type, out long count) ? count + 1 : 1;
            _packets++;
            _bytes += packet.Length;

            if (_burstFrame != frame)
            {
                _burstFrame = frame;
                _burst = 0;
            }
            _burst++;
            _maxBurst = Math.Max(_maxBurst, _burst);

            if (type == PacketType.Snapshot)
            {
                _snapshots++;
                if (_hasSnapshot)
                    _maxSnapshotGap = Math.Max(_maxSnapshotGap, frame - _lastSnapshot);
                _lastSnapshot = frame;
                _hasSnapshot = true;
            }
            else if (type == PacketType.SlotIntent)
            {
                _intents++;
            }
        }

        public static ReplayNetworkSnapshot Snapshot()
            => new(_packets, _bytes, _snapshots, _intents, _maxSnapshotGap,
                _maxBurst, _lastSnapshot,
                new Dictionary<PacketType, long>(Counts));

        public static string Summary
        {
            get
            {
                ReplayNetworkSnapshot value = Snapshot();
                double kb = value.Bytes / 1024d;
                return $"{value.Packets} packets · {kb:0.0} KiB · "
                    + $"{value.Snapshots} snapshots · {value.Intents} intents · "
                    + $"max snapshot gap {value.MaxSnapshotGapFrames}f · "
                    + $"max burst {value.MaxPacketsInFrame}";
            }
        }
    }
}
