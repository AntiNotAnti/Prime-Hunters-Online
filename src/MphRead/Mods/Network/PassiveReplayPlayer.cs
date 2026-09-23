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
    private readonly HashSet<long> _rejectedDurable = new();
    internal string CheckpointSource { get; private set; } = "initial world";
    public PassiveReplayScene Current { get; private set; }
    public ReplayTransport Transport => Current.Session.Transport;
    internal event Action<Scene>? Stepped;
    internal event Action<Scene, PassiveReplayScene>? Replaced;
    internal long CheckpointBytes { get; private set; }
    internal int CheckpointCount => _checkpoints.Count;
    internal uint SeekRestoreFrame { get; private set; }
    internal int SeekSimulationSteps { get; private set; }
    internal double SeekMilliseconds { get; private set; }
    internal int RejectedCheckpoints { get; private set; }
    internal string? LastCheckpointError { get; private set; }
    internal bool Ready => !Transport.IsSeeking && !Current.Session.IsWarming;

    public PassiveReplayPlayer(string path, Vector2i size)
    {
        _open = () => new(path, size); Current = _open();
        // Nested ranges retain original source clocks. A durable baseline before
        // their visible start can skip most of the hidden lead-in immediately.
        if (Current.Session.IsWarming && DurableBefore(0) is { } baseline)
            Rebuild(0, true, null, baseline);
    }
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
            var durable = DurableBefore(target.Value);
            if (durable is { } disk && checkpoint.Value != null && Current.Session.CheckpointVisibleFrame(disk) <= checkpoint.Key)
                durable = null;
            uint bestFrame = durable is { } chosen ? Current.Session.CheckpointVisibleFrame(chosen) : checkpoint.Key;
            if (rebuild || bestFrame > Current.Session.CurrentFrame + MaximumStepsPerUpdate)
                Rebuild(target.Value, resume, durable.HasValue ? null : checkpoint.Value, durable);
        }
        if (Current.Session.IsWarming)
        {
            int warmup = 0;
            while (Current.Session.IsWarming && warmup < MaximumStepsPerUpdate)
            {
                if (!Current.Step()) throw new InvalidDataException("Replay ended during its required lead-in.");
                if (!Current.Session.IsWarming) Stepped?.Invoke(Current.Scene);
                warmup++;
                if (Transport.IsSeeking) SeekSimulationSteps++;
            }
            return warmup;
        }
        int due = Math.Min(MaximumStepsPerUpdate, Transport.FramesDue());
        bool seeking = Transport.IsSeeking;
        int steps = 0;
        for (; steps < due; steps++)
        {
            if (!Current.Step()) break;
            Stepped?.Invoke(Current.Scene);
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
    private ReplayCheckpointIndex? DurableBefore(uint target)
    {
        foreach (var index in Current.Session.DurableCheckpoints.Reverse())
            if (Current.Session.CheckpointVisibleFrame(index) <= target && !_rejectedDurable.Contains(index.Offset)) return index;
        return null;
    }
    private void Rebuild(uint target, bool resume, ReplayWorldCheckpoint? checkpoint, ReplayCheckpointIndex? durable = null)
    {
        PassiveReplayScene? replacement = null;
        try
        {
            if (durable is { } disk)
            {
                try { checkpoint = Current.Session.LoadCheckpoint(disk); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
                {
                    RejectedCheckpoints++; LastCheckpointError = ex.Message; _rejectedDurable.Add(disk.Offset);
                    durable = null; checkpoint = null;
                }
            }
            replacement = _open();
            CheckpointSource = checkpoint == null ? "initial world" : durable.HasValue ? "file" : "memory";
            if (checkpoint != null)
            {
                try { checkpoint.Restore(replacement, durable?.Frame); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
                {
                    RejectedCheckpoints++; LastCheckpointError = ex.Message;
                    if (durable is { } rejected) _rejectedDurable.Add(rejected.Offset);
                    else { CheckpointBytes -= checkpoint.Bytes.Length + 128; _checkpoints.Remove(checkpoint.Frame); }
                    replacement.Dispose(); replacement = _open(); CheckpointSource = "initial world";
                }
            }
            replacement.Session.Transport.CopyPreferences(Transport);
            replacement.Session.Transport.ContinueSeek(target, resume);
            SeekRestoreFrame = replacement.Session.CurrentFrame;
            var previous = Current; Current = replacement; replacement = null;
            try { Replaced?.Invoke(previous.Scene, Current); }
            finally { previous.Dispose(); }
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
