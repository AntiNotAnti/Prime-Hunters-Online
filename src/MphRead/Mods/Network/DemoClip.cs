using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Network
{
    /// <summary>Bounded pooled packet pages; each bootstrap belongs to the beginning of its page.</summary>
    internal static class DemoClip
    {
        public static readonly int[] Lengths = { 15, 30, 60, 120 };
        public static readonly int[] PostRollLengths = { 0, 2, 3, 5 };
        private static int _seconds = 30;
        public static int Seconds
        {
            get => _seconds;
            set { _seconds = Math.Clamp(value, 0, 120); if (_seconds == 0) Purge(); }
        }
        public static int PostRollSeconds { get; set; } = 3;
        private const long MaxBytes = 24 * 1024 * 1024;
        private const int PageSize = 64 * 1024;
        private readonly record struct Entry(uint Frame, int Offset, ushort Length);
        private sealed class Page : IDisposable
        {
            public readonly byte[] Buffer;
            public readonly List<Entry> Entries = new(512);
            public readonly List<ReplayEvent> Events = new();
            public readonly uint First;
            public readonly ReplayMetadata Metadata;
            public int Used;
            public Page(uint frame)
            {
                First = frame;
                Metadata = ReplayCapture.Capture(ReplayType.Clip);
                Buffer = ArrayPool<byte>.Shared.Rent(PageSize);
            }
            public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
        }
        private static readonly Queue<Page> Pages = new();
        private static Page? _tail;
        private static long _bytes;
        private static string? _pendingPath;
        private static uint _finishFrame;
        public static bool IsSaving => _pendingPath != null;
        public static string? LastError { get; private set; }
        public static string? LastSavedPath { get; private set; }
        internal static long BufferedBytes => _bytes;
        internal static int BufferedPages => Pages.Count;
        public static bool Active => Seconds > 0 && NetSession.Active && !DemoPlayback.IsActive;
        public static double Held => Pages.Count == 0 || NetSession.NetFrame < Pages.Peek().First
            ? 0 : (NetSession.NetFrame - Pages.Peek().First) / 60.0;

        public static void Add(ReadOnlySpan<byte> data)
        {
            if (!Active || data.Length is < 1 or > NetConfig.MaxPacketSize || NetSession.ServerMatch == null) return;
            uint frame = NetSession.NetFrame;
            if (_tail != null && frame < _tail.First) Purge();
            try
            {
                if (_tail == null || frame - _tail.First >= 60 || _tail.Used + data.Length > _tail.Buffer.Length
                    || _tail.Entries.Count >= 4096)
                {
                    _tail = new Page(frame);
                    Pages.Enqueue(_tail);
                    // Include descriptor capacity and bootstrap overhead for even 1-byte packet floods.
                    _bytes += _tail.Buffer.Length + 96 * 1024;
                }
                data.CopyTo(_tail.Buffer.AsSpan(_tail.Used));
                _tail.Entries.Add(new(frame, _tail.Used, (ushort)data.Length));
                _tail.Used += data.Length;
                Trim(frame);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LastError = "Replay buffer unavailable: " + ex.Message;
            }
        }

        public static void AddEvent(ReplayEvent value)
        {
            if (Active && _tail != null && _tail.Events.Count < 2048) _tail.Events.Add(value);
        }

        public static void Tick()
        {
            if (IsSaving && NetSession.NetFrame >= _finishFrame) Finish();
            Trim(NetSession.NetFrame);
        }

        private static void Trim(uint now)
        {
            uint window = (uint)Math.Clamp(Seconds, 0, 120) * 60 + 60;
            if (IsSaving) window += (uint)Math.Clamp(PostRollSeconds, 0, 5) * 60;
            while (Pages.Count > 0 && (_bytes > MaxBytes
                || (now >= Pages.Peek().First && now - Pages.Peek().First > window)))
            {
                Page page = Pages.Dequeue();
                _bytes -= page.Buffer.Length + 96 * 1024;
                if (page == _tail) _tail = null;
                page.Dispose();
            }
        }

        public static void Purge()
        {
            // A disconnect during post-roll keeps the requested available portion.
            if (IsSaving) Finish();
            while (Pages.Count > 0) Pages.Dequeue().Dispose();
            _tail = null; _bytes = 0;
        }

        public static string? Save()
        {
            if (IsSaving) Finish();
            Tick();
            if (Pages.Count == 0) return null;
            LastError = null;
            string room = Pages.Peek().Metadata.RoomKey;
            foreach (char c in Path.GetInvalidFileNameChars()) room = room.Replace(c, '_');
            string name = $"{room}_clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}";
            _pendingPath = Paths.Combine(Paths.Export, "_demos", name);
            _finishFrame = NetSession.NetFrame + (uint)Math.Clamp(PostRollSeconds, 0, 5) * 60;
            string path = _pendingPath;
            if (PostRollSeconds <= 0) return Finish() ? path : null;
            return path;
        }

        private static bool Finish()
        {
            string? path = _pendingPath;
            _pendingPath = null;
            if (path == null || Pages.Count == 0) return false;
            ReplayWriterV3? writer = null;
            try
            {
                Page first = Pages.Peek();
                uint start = first.First;
                if (first.Metadata.MapHash == 0) throw new IOException("The clip's map could not be identified.");
                writer = new ReplayWriterV3(path, first.Metadata);
                foreach (Page page in Pages)
                {
                    foreach (Entry entry in page.Entries)
                        writer.WriteRecord(entry.Frame - start, page.Buffer.AsSpan(entry.Offset, entry.Length));
                    foreach (ReplayEvent value in page.Events)
                        if (value.Frame >= start) writer.WriteEvent(value with { Frame = value.Frame - start });
                }
                writer.Dispose();
                LastSavedPath = path;
                Chat.ChatBox.System("Saved replay clip: " + Path.GetFileName(path));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                writer?.Abort();
                LastError = "Could not save replay: " + ex.Message;
                Console.WriteLine($"[replay] {LastError}");
                Chat.ChatBox.System(LastError);
                return false;
            }
        }
    }
}
