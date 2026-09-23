using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>A canonical replica consumes the same accepted facts as the timeline.
/// It never copies the live scene or borrows its input, RNG, entities or resources.
/// Network callbacks enqueue values; construction/stepping/disposal stay on the scene thread.</summary>
internal sealed class ReplayLiveWorld : IDisposable
{
    private const int MaximumPendingRecords = 8192;
    private const int MaximumPendingBytes = 4 * 1024 * 1024;
    private readonly ReplayRecorder _recorder;
    private readonly ReplayReplicaState _bootstrap = new();
    private readonly List<ReplayTimelineRecord> _pending = new();
    private PassiveReplayScene? _world;
    private bool _reset = true, _failed;
    private long _pendingBytes;
    private uint _lastCheckpoint;
    internal string? LastError { get; private set; }
    internal long CaptureCount { get; private set; }
    internal double LastCaptureMilliseconds { get; private set; }
    internal double LastStepMilliseconds { get; private set; }
    internal PassiveReplayScene? World => _world;

    internal ReplayLiveWorld(ReplayRecorder recorder)
    {
        _recorder = recorder; recorder.ProducesWorldCheckpoints = true;
        recorder.Accepted += Accept; recorder.Resetting += Reset;
    }
    private void Reset()
    {
        _pending.Clear(); _pendingBytes = 0; _bootstrap.Reset();
        _reset = true; _failed = false; LastError = null; _lastCheckpoint = 0;
    }
    private void Accept(ReplayTimelineRecord record)
    {
        if (_failed) return;
        if (_pending.Count >= MaximumPendingRecords || _pendingBytes + record.PayloadBytes > MaximumPendingBytes)
        { Fail("Accepted replay facts exceeded the pending budget."); return; }
        _pending.Add(record); _pendingBytes += record.PayloadBytes;
    }
    internal void Advance(uint frame, Vector2i size)
    {
        try
        {
            if (_reset || _failed) { _world?.Dispose(); _world = null; _reset = false; }
            if (_failed) return;
            if (_world == null)
            {
                foreach (var record in _pending)
                    if (!record.Payload.IsEmpty) _bootstrap.Accept(record.Payload, record.RecordingFrame);
                _pending.Clear(); _pendingBytes = 0;
                bool any = false;
                for (int slot = 0; slot < 8; slot++) any |= _bootstrap.TryGetPlayer(slot, out _);
                if (_bootstrap.Match is not { } match || !any) return;
                ulong mapHash = ReplayMapIdentity.Compute(match.RoomKey);
                if (mapHash == 0) throw new InvalidDataException("The capture room has no content identity.");
                _world = new PassiveReplayScene(_bootstrap.CaptureCheckpoint(), frame, mapHash, size);
                // Presentation telemetry comes from this canonical reconstruction,
                // so disk/instant clips count the same shots as the replay world.
                _world.Scene.ReplayShotPresented = (slot, weapon) => _recorder.Marker(
                    _world.Session.CurrentFrame, _world.State.ServerTick,
                    new(ReplayMarkerKind.WeaponFired, (byte)slot, byte.MaxValue, weapon));
            }
            else if (_world.Session.CurrentFrame == frame) return;
            var timer = Stopwatch.StartNew();
            using (ReplayPerfTelemetry.Measure(ReplayPerfOperation.Step))
                _world.StepLive(frame, _pending);
            _pending.Clear(); _pendingBytes = 0;
            LastStepMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (_recorder.Timeline.NeedsRestorePoint || frame - _lastCheckpoint >= 300)
            {
                timer.Restart();
                var checkpoint = ReplayWorldCheckpoint.Capture(_world);
                if (!_recorder.AppendWorldCheckpoint(frame, _world.State.ServerTick, checkpoint.Bytes))
                    throw new InvalidDataException("The timeline rejected its world checkpoint.");
                _lastCheckpoint = frame; CaptureCount++;
                LastCaptureMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
            // A quiet simulation frame still extends the available clip, even
            // when no network snapshot or input arrived on that frame.
            _recorder.Timeline.Append(new(frame, _world.State.ServerTick, ReplayFactKind.Presentation, ReadOnlySpan<byte>.Empty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { Fail(ex.Message); _world?.Dispose(); _world = null; }
    }
    private void Fail(string error)
    {
        _failed = true; LastError = error; _pending.Clear(); _pendingBytes = 0;
        _recorder.Timeline.Reset();
        Console.WriteLine("[replay] Live world capture unavailable: " + error);
    }
    public void Dispose()
    {
        _recorder.Accepted -= Accept; _recorder.Resetting -= Reset;
        _world?.Dispose(); _world = null; _pending.Clear(); _pendingBytes = 0;
    }
}
