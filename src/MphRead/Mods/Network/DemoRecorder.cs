using System;
using System.IO;

namespace MphRead.Mods.Network;

/// <summary>Full recordings are a disk sink of the accepted-fact recorder.</summary>
internal static class DemoRecorder
{
    private static ReplayWriterV3? _writer;
    private static uint _origin, _lastFrame;
    private static bool _pending;
    static DemoRecorder()
    {
        ReplayCapture.Recorder.Accepted += Accept;
            ReplayCapture.Recorder.CheckpointCaptured += checkpoint =>
            {
                if (_writer == null || checkpoint.RecordingFrame <= _origin) return;
                try { _writer.WriteCheckpoint(checkpoint.RecordingFrame - _origin, checkpoint.Payload); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
            };
        ReplayCapture.Recorder.Resetting += Stop;
    }
    public static bool IsRecording => _pending || _writer != null;
    public static string? CurrentPath { get; private set; }
    public static string? LastError { get; private set; }
    public static bool Start()
    {
        if (IsRecording || !NetSession.Active || DemoPlayback.IsActive || NetSession.ServerMatch == null) return false;
        string room = NetSession.ServerMatch.Value.RoomKey;
        foreach (char c in Path.GetInvalidFileNameChars()) room = room.Replace(c, '_');
        CurrentPath = Paths.Combine(Paths.Export, "_demos", $"{room}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}");
        LastError = null; _pending = true;
        return true;
    }
    internal static void Tick()
    {
        try
        {
            if (_pending && ReplayCapture.WorldCapture.World is { } world)
            {
                var metadata = ReplayTimelineArchive.Metadata(world, ReplayType.FullMatch);
                _origin = _lastFrame = world.Session.RecordingFrame;
                _writer = new ReplayWriterV3(CurrentPath!, metadata); _pending = false;
                ReplayTimelineArchive.EndFrame(_writer, 0);
            }
            if (_writer != null)
            {
                _lastFrame = Math.Max(_lastFrame, NetSession.NetFrame);
                // Preserve EOF in quiet periods as well as during packet traffic.
                ReplayTimelineArchive.EndFrame(_writer, _lastFrame - _origin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { Fail(ex); }
    }
    private static void Accept(ReplayTimelineRecord record)
    {
        if (_writer == null) return;
        try { ReplayTimelineArchive.Write(_writer, record, _origin); _lastFrame = Math.Max(_lastFrame, record.RecordingFrame); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
    }
    public static void Stop()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
        finally { _pending = false; _writer = null; CurrentPath = null; }
    }
    private static void Fail(Exception error)
    {
        LastError = "Recording interrupted; recover completed chunks from its .part file. " + error.Message;
        Console.WriteLine("[replay] " + LastError);
        _writer?.Abort(); _writer = null; _pending = false; CurrentPath = null;
    }
    internal static void RecordOwnSnapshot(ReadOnlySpan<byte> payload)
    {
        Span<byte> packet = stackalloc byte[1 + payload.Length];
        packet[0] = (byte)PacketType.Snapshot; payload.CopyTo(packet[1..]);
        ReplayCapture.Observe(packet);
        ReplayCapture.AcceptedSnapshot(packet, NetSession.NetFrame);
    }
}
