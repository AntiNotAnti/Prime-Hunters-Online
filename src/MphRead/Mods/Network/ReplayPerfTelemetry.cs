using System;
using System.Diagnostics;
using System.Text;

namespace MphRead.Mods.Network;

internal enum ReplayPerfOperation { Capture, Step, Checkpoint, AuthorityCapture, AuthorityEncode, Timeline, Enqueue }

/// <summary>Owner-thread counters. No formatting, timers or collections are allocated by measurement.</summary>
internal static class ReplayPerfTelemetry
{
    private struct Counter { internal long Count, Ticks, Maximum, Allocated; }
    private static readonly Counter[] Counters = new Counter[7];
    private static readonly long[] FrameTicks = new long[7];
    private static readonly int[] Collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    internal static bool Enabled { get; set; }
    internal static long CheckpointBytes { get; set; }
    private static long _lastSpike;
    internal readonly struct Scope : IDisposable
    {
        private readonly bool _enabled;
        private readonly ReplayPerfOperation _operation;
        private readonly long _start, _allocated;
        internal Scope(ReplayPerfOperation operation)
        {
            _enabled = Enabled || NetDiagnostics.Enabled; _operation = operation;
            _start = _enabled ? Stopwatch.GetTimestamp() : 0;
            _allocated = _enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        }
        public void Dispose()
        {
            if (!_enabled) return;
            long ticks = Stopwatch.GetTimestamp() - _start;
            ref var counter = ref Counters[(int)_operation];
            counter.Count++; counter.Ticks += ticks; counter.Maximum = Math.Max(counter.Maximum, ticks);
            counter.Allocated += GC.GetAllocatedBytesForCurrentThread() - _allocated;
            FrameTicks[(int)_operation] += ticks;
        }
    }
    internal readonly struct FrameScope : IDisposable
    {
        private readonly long _start;
        internal FrameScope(bool enabled) { _start = enabled ? Stopwatch.GetTimestamp() : 0; if (enabled) BeginFrame(); }
        public void Dispose() { if (_start != 0) EndFrame(NetSession.NetFrame, _start); }
    }
    internal static FrameScope Frame() => new(Enabled || NetDiagnostics.Enabled);
    internal static Scope Measure(ReplayPerfOperation operation) => new(operation);
    internal static void BeginFrame() { if (Enabled || NetDiagnostics.Enabled) Array.Clear(FrameTicks); }
    internal static void EndFrame(uint frame, long start)
    {
        if (!Enabled && !NetDiagnostics.Enabled) return;
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long now = Stopwatch.GetTimestamp();
        if (ms < 20 || Stopwatch.GetElapsedTime(_lastSpike, now).TotalSeconds < 1) return;
        _lastSpike = now;
        Console.WriteLine($"[frametime] frame={frame} cadence={frame % 300} total={ms:F2}ms replayStep={Milliseconds(FrameTicks[1]):F2}ms checkpoint={Milliseconds(FrameTicks[2]):F2}ms authority={Milliseconds(FrameTicks[3]):F2}ms");
    }
    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    internal static string Summary(RollingReplayTimeline timeline)
    {
        var text = new StringBuilder("[replayperf]");
        for (int i = 0; i < Counters.Length; i++)
        {
            var c = Counters[i];
            text.Append($" {(ReplayPerfOperation)i}={Milliseconds(c.Ticks) / Math.Max(1, c.Count):F3}ms max={Milliseconds(c.Maximum):F3}ms alloc={c.Allocated / Math.Max(1, c.Count)}B n={c.Count}");
        }
        text.Append($" checkpoint={CheckpointBytes}B timeline={timeline.PayloadBytes}B facts={timeline.RecordCount} GC={GC.CollectionCount(0) - Collections[0]}/{GC.CollectionCount(1) - Collections[1]}/{GC.CollectionCount(2) - Collections[2]}");
        return text.ToString();
    }
}
