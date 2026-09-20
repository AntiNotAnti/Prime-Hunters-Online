using System;
using System.Diagnostics;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    public static class NetTimingDiagnostics
    {
        public static long SnapshotPositionFrames, IntentFallbackEntries, IntentFallbackFrames, LongestIntentFallback;
        public static long CorrectionsAfterLocalStall, CorrectionsWithoutLocalStall;
        public static double SnapshotIntervalMs, SimulationIntervalMs, WorstSnapshotIntervalMs, WorstSimulationIntervalMs;
        private static long _snapshotAt, _simulationAt, _stallAt;
        private static readonly long[] _fallback = new long[PlayerEntity.SlotCapacity];
        private static readonly uint[] _positionFrame = new uint[PlayerEntity.SlotCapacity];
        private static readonly bool[] _positionSeen = new bool[PlayerEntity.SlotCapacity];

        public static void Simulation()
        {
            long now = Stopwatch.GetTimestamp();
            if (_simulationAt != 0)
            {
                SimulationIntervalMs = Stopwatch.GetElapsedTime(_simulationAt, now).TotalMilliseconds;
                WorstSimulationIntervalMs = Math.Max(WorstSimulationIntervalMs, SimulationIntervalMs);
                if (SimulationIntervalMs > 50) _stallAt = now;
            }
            _simulationAt = now;
        }
        public static void Snapshot(long now)
        {
            if (_snapshotAt != 0 && now >= _snapshotAt)
            {
                SnapshotIntervalMs = Stopwatch.GetElapsedTime(_snapshotAt, now).TotalMilliseconds;
                WorstSnapshotIntervalMs = Math.Max(WorstSnapshotIntervalMs, SnapshotIntervalMs);
            }
            _snapshotAt = now;
        }
        public static void Correction()
        {
            // This is attribution by observed timing, not proof of a network fault.
            if (_stallAt != 0 && Stopwatch.GetElapsedTime(_stallAt).TotalMilliseconds < 250) CorrectionsAfterLocalStall++;
            else CorrectionsWithoutLocalStall++;
        }
        public static void Position(int slot, bool snapshot)
        {
            if (slot < 0 || slot >= _fallback.Length || (_positionSeen[slot] && _positionFrame[slot] == NetSession.NetFrame)) return;
            _positionSeen[slot] = true; _positionFrame[slot] = NetSession.NetFrame;
            if (snapshot) { SnapshotPositionFrames++; _fallback[slot] = 0; }
            else
            {
                if (_fallback[slot]++ == 0) IntentFallbackEntries++;
                IntentFallbackFrames++;
                LongestIntentFallback = Math.Max(LongestIntentFallback, _fallback[slot]);
            }
        }
        public static void ForgetSlot(int slot)
        { if (slot >= 0 && slot < _fallback.Length) { _fallback[slot] = 0; _positionSeen[slot] = false; } }
        public static void Reset()
        {
            SnapshotPositionFrames = IntentFallbackEntries = IntentFallbackFrames = LongestIntentFallback = 0;
            CorrectionsAfterLocalStall = CorrectionsWithoutLocalStall = 0;
            SnapshotIntervalMs = SimulationIntervalMs = WorstSnapshotIntervalMs = WorstSimulationIntervalMs = 0;
            _snapshotAt = _simulationAt = _stallAt = 0;
            Array.Clear(_fallback); Array.Clear(_positionSeen);
        }
        public static string Describe() => $"positions: snapshots={SnapshotPositionFrames} fallback entries={IntentFallbackEntries} frames={IntentFallbackFrames} longest={LongestIntentFallback}; "
            + $"timing ms: snapshot={SnapshotIntervalMs:F1} worst={WorstSnapshotIntervalMs:F1} simulation={SimulationIntervalMs:F1} worst={WorstSimulationIntervalMs:F1}; "
            + $"playout snaps: after local stall={CorrectionsAfterLocalStall} without local stall={CorrectionsWithoutLocalStall}";
    }
}
