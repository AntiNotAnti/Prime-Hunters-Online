using System;
using System.IO;

namespace MphRead.Mods.Network;

/// <summary>Full recordings are a disk sink of the accepted-fact recorder.</summary>
internal static class DemoRecorder
{
    private static ReplayWritePump? _writer;
    private static uint _origin, _lastFrame;
    private static ReplayWritePump? _lastWriter;
    private static bool _pending;
    static DemoRecorder()
    {
        ReplayCapture.Recorder.Accepted += Accept;
        ReplayCapture.Recorder.CheckpointCaptured += checkpoint =>
        {
            if (_writer == null || checkpoint.RecordingFrame <= _origin) return;
            try { if (!_writer.Checkpoint(checkpoint)) Fail(_writer.Error!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
        };
        ReplayCapture.Recorder.Resetting += Stop;
    }
    public static bool IsRecording => _pending || _writer != null;
    public static string? CurrentPath { get; private set; }
    private static string? _lastError;
    public static string? LastError => _lastError ?? _lastWriter?.Error?.Message;
    public static bool Start()
    {
        if (IsRecording || !NetSession.Active || DemoPlayback.IsActive || NetSession.ServerMatch == null) return false;
        string room = NetSession.ServerMatch.Value.RoomKey;
        foreach (char c in Path.GetInvalidFileNameChars()) room = room.Replace(c, '_');
        CurrentPath = Paths.Combine(Paths.Export, "_demos", $"{room}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}");
        _lastError = null; _lastWriter = null; _pending = true;
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
                _writer = new ReplayWritePump(CurrentPath!, metadata, _origin); _pending = false; _lastWriter = _writer;
                _writer.EndFrame(0);
            }
            if (_writer != null)
            {
                _lastFrame = Math.Max(_lastFrame, NetSession.NetFrame);
                // Preserve EOF in quiet periods as well as during packet traffic.
                if (!_writer.EndFrame(_lastFrame - _origin)) Fail(_writer.Error!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { Fail(ex); }
    }
    private static void Accept(ReplayTimelineRecord record)
    {
        if (_writer == null) return;
        try { if (!_writer.Record(record)) { Fail(_writer.Error!); return; } _lastFrame = Math.Max(_lastFrame, record.RecordingFrame); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
    }
    public static void Stop()
    {
        try { _writer?.Complete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
        finally { _pending = false; _writer = null; CurrentPath = null; }
    }
    private static void Fail(Exception error)
    {
        _lastError = "Recording interrupted; recover completed chunks from its .part file. " + error.Message;
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
