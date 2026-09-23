using System;
using System.IO;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Instant-clip selection over the shared bounded replay timeline.
/// Frozen facts survive reset; existing v4 lead-in removes save-time world construction.</summary>
internal static class DemoClip
{
    public static readonly int[] Lengths = { 15, 30, 60, 120 };
    public static readonly int[] PostRollLengths = { 0, 2, 3, 5 };
    private static int _seconds = 30;
    public static int Seconds { get => _seconds; set { _seconds = Math.Clamp(value, 0, 120); } }
    public static int PostRollSeconds { get; set; } = 3;
    private static string? _pendingPath;
    private static uint _start, _finish;
    private static ReplayTimelineClip? _clip;
    private static Task? _writing;
    static DemoClip() { ReplayCapture.Recorder.Resetting += Freeze; }
    public static bool IsSaving => _pendingPath != null;
    public static string? LastError { get; private set; }
    public static string? LastSavedPath { get; private set; }
    public static bool Active => Seconds > 0 && NetSession.Active && !DemoPlayback.IsActive;
    public static double Held => ReplayCapture.Recorder.Timeline.FirstRecordingFrame is uint first
        && ReplayCapture.Recorder.Timeline.LastRecordingFrame is uint last
        ? Math.Min(Seconds, (last - first) / 60.0) : 0;
    public static string? Save()
    {
        if (IsSaving) return _pendingPath;
        var timeline = ReplayCapture.Recorder.Timeline;
        if (!Active || timeline.FirstRecordingFrame is not uint first || timeline.LastRecordingFrame is not uint last) return null;
        LastError = null;
        _start = Math.Max(first, last > Seconds * 60 ? last - (uint)(Seconds * 60) : 0);
        _finish = last + (uint)Math.Clamp(PostRollSeconds, 0, 5) * 60;
        string room = NetSession.ServerMatch?.RoomKey ?? "match";
        foreach (char c in Path.GetInvalidFileNameChars()) room = room.Replace(c, '_');
        _pendingPath = Paths.Combine(Paths.Export, "_demos", $"{room}_clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}");
        if (PostRollSeconds <= 0) Freeze();
        return _pendingPath;
    }
    public static string? SaveWithFeedback()
    {
        double held = Held;
        string? path = Save();
        Chat.ChatBox.System(path == null ? "nothing to clip yet" : $"saving the last {held:0} s to " + Path.GetFileName(path));
        return path;
    }
    private static void Freeze()
    {
        if (!IsSaving || _clip != null) return;
        var timeline = ReplayCapture.Recorder.Timeline;
        if (timeline.LastRecordingFrame is uint last && timeline.TryFreeze(_start, Math.Min(last, _finish), out var frozen))
            _clip = frozen;
        else Fail(new InvalidDataException("The requested replay history is no longer available."));
    }
    // Settings/respawn changes stop an outstanding selection's post-roll; the
    // shared world history remains available to killcams and other consumers.
    public static void Purge() => Freeze();
    internal static void Tick(Vector2i size)
    {
        if (!IsSaving) return;
        try
        {
            if (_clip == null && ReplayCapture.Recorder.Timeline.LastRecordingFrame >= _finish) Freeze();
            if (_clip == null) return;
            if (_writing != null)
            {
                if (!_writing.IsCompleted) return;
                if (_writing.IsFaulted) throw _writing.Exception!.GetBaseException();
                LastSavedPath = _pendingPath;
                Chat.ChatBox.System("Saved replay clip: " + Path.GetFileName(_pendingPath));
                Clear(); return;
            }
            var frozen = _clip;
            string path = _pendingPath!;
            _writing = Task.Run(() => ReplayTimelineArchive.SaveFrozen(frozen, path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Fail(ex); }
    }
    // Called on scene teardown with its GL context current. Preserve an already
    // requested clip even when disconnect truncates the requested post-roll.
    internal static void CompletePending(Vector2i size)
    {
        Freeze();
        while (IsSaving)
        {
            Tick(size);
            if (_writing != null)
            {
                try { _writing.GetAwaiter().GetResult(); }
                catch (Exception ex) { Fail(ex); }
            }
        }
    }
    private static void Fail(Exception error)
    {
        LastError = "Could not save replay: " + error.Message;
        Console.WriteLine("[replay] " + LastError); Chat.ChatBox.System(LastError); Clear();
    }
    private static void Clear()
    {
        _clip?.Dispose(); _clip = null; _pendingPath = null; _writing = null;
    }
}
