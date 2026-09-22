using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Playback, seek and checkpoint lifetime for one private presentation.
/// A failed restore never replaces the currently presented world.</summary>
internal sealed class PassiveReplayPlayer : IDisposable
{
    internal const int MaximumStepsPerUpdate = 120;
    private const int MaximumCheckpoints = 128;
    private const long MaximumCheckpointBytes = 64L * 1024 * 1024;
    private readonly SortedDictionary<uint, ReplayWorldCheckpoint> _checkpoints = new();
    private readonly Func<PassiveReplayScene> _open;
    private readonly uint _firstFrame;
    private readonly Stopwatch _seekTime = new();
    private bool _disposed;
    public PassiveReplayScene Current { get; private set; }
    public ReplayTransport Transport => Current.Session.Transport;
    internal long CheckpointBytes { get; private set; }
    internal int CheckpointCount => _checkpoints.Count;
    internal uint SeekRestoreFrame { get; private set; }
    internal int SeekSimulationSteps { get; private set; }
    internal double SeekMilliseconds { get; private set; }
    internal int RejectedCheckpoints { get; private set; }
    internal string? LastCheckpointError { get; private set; }
    internal bool Ready => !Transport.IsSeeking;

    public PassiveReplayPlayer(string path, Vector2i size)
    { _open = () => new(path, size); Current = _open(); }
    public PassiveReplayPlayer(ReplayTimelineClip clip, Vector2i size)
    { _open = () => new(clip, size); _firstFrame = clip.StartRecordingFrame; Current = _open(); }

    public void Seek(uint frame, bool resume = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = Math.Clamp(frame, _firstFrame, Current.Session.LastFrame);
        _seekTime.Restart(); SeekSimulationSteps = 0; SeekRestoreFrame = Current.Session.CurrentFrame;
        Transport.Seek(frame, resume);
    }

    /// <summary>One host update. Seeking never exceeds 120 fixed steps, and leaves
    /// the target pending for the next update. No intermediate frame is presented.</summary>
    public int Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint? target = Transport.SeekTarget;
        bool resume = Transport.ResumeAfterSeek;
        bool rebuild = Transport.TakeRebuild(out uint rebuildTarget, out bool rebuildResume);
        if (rebuild) { target = rebuildTarget; resume = rebuildResume; }
        if (target.HasValue)
        {
            var checkpoint = _checkpoints.LastOrDefault(p => p.Key <= target.Value);
            if (rebuild || checkpoint.Value != null && checkpoint.Key > Current.Session.CurrentFrame + MaximumStepsPerUpdate)
                Rebuild(target.Value, resume, checkpoint.Value);
        }
        int due = Math.Min(MaximumStepsPerUpdate, Transport.FramesDue());
        bool seeking = Transport.IsSeeking;
        int steps = 0;
        for (; steps < due; steps++)
        {
            if (!Current.Step()) break;
            uint frame = Current.Session.CurrentFrame;
            if (seeking) SeekSimulationSteps++;
            if (frame % 300 == 0 && !_checkpoints.ContainsKey(frame))
            {
                try { Remember(ReplayWorldCheckpoint.Capture(Current)); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                { LastCheckpointError = ex.Message; }
            }
        }
        if (!Transport.IsSeeking && _seekTime.IsRunning)
        { _seekTime.Stop(); SeekMilliseconds = _seekTime.Elapsed.TotalMilliseconds; }
        return steps;
    }
    private void Rebuild(uint target, bool resume, ReplayWorldCheckpoint? checkpoint)
    {
        PassiveReplayScene? replacement = null;
        try
        {
            replacement = _open();
            if (checkpoint != null)
            {
                try { checkpoint.Restore(replacement); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
                {
                    RejectedCheckpoints++; LastCheckpointError = ex.Message;
                    CheckpointBytes -= checkpoint.Bytes.Length + 128; _checkpoints.Remove(checkpoint.Frame);
                    replacement.Dispose(); replacement = _open();
                }
            }
            replacement.Session.Transport.CopyPreferences(Transport);
            replacement.Session.Transport.ContinueSeek(target, resume);
            SeekRestoreFrame = replacement.Session.CurrentFrame;
            var previous = Current; Current = replacement; replacement = null; previous.Dispose();
        }
        finally { replacement?.Dispose(); }
    }
    private void Remember(ReplayWorldCheckpoint checkpoint)
    {
        long cost = checkpoint.Bytes.Length + 128;
        if (cost > MaximumCheckpointBytes) return;
        while (_checkpoints.Count >= MaximumCheckpoints || CheckpointBytes + cost > MaximumCheckpointBytes)
        {
            var first = _checkpoints.First(); _checkpoints.Remove(first.Key); CheckpointBytes -= first.Value.Bytes.Length + 128;
        }
        _checkpoints.Add(checkpoint.Frame, checkpoint); CheckpointBytes += cost;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Current.Dispose(); _checkpoints.Clear(); CheckpointBytes = 0;
    }
}
