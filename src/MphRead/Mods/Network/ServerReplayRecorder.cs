using System;
using System.IO;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Canonical replay capture for a dedicated server that runs the simulation.
    /// It records the packets the server itself treats as authoritative: accepted
    /// slot intents, its own snapshots, and the roster/match-state stream.
    ///
    /// Recording is intentionally failure-isolated. An I/O problem aborts the
    /// .part file and reports it, but never changes or stops the match.
    /// </summary>
    internal static class ServerReplayRecorder
    {
        private static ReplayWritePump? _writer, _lastWriter;
        private static uint _origin;
        private static bool _pending;
        static ServerReplayRecorder()
        {
            ReplayCapture.Recorder.Accepted += Accept;
            ReplayCapture.Recorder.CheckpointCaptured += checkpoint =>
            {
                if (_writer == null || checkpoint.RecordingFrame <= _origin) return;
                try { if (!_writer.Checkpoint(checkpoint)) Fail(_writer.Error!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(ex); }
            };
            ReplayCapture.Recorder.Resetting += () => Stop();
        }
        private static ServerReplayPolicy _policy = ServerReplayPolicy.Default;

        public static string ReplayDirectory => Paths.Combine(
            Paths.Export, "_demos", "server");
        public static bool Enabled => _policy.Enabled;
        public static bool IsRecording => _pending || _writer != null;
        public static string? CurrentPath { get; private set; }
        private static string? _lastError;
        public static string? LastError => _lastError ?? _lastWriter?.Error?.Message;

        public static void Configure(ServerReplayPolicy policy)
        {
            _policy = policy.Normalize();
            if (!_policy.Enabled)
            {
                Console.WriteLine("[replay] canonical server recording disabled");
                return;
            }

            Console.WriteLine($"[replay] canonical server recording enabled; "
                + $"storage {(_policy.StorageLimitGb == 0 ? "unlimited" : _policy.StorageLimitGb + " GB")}, "
                + $"retention {(_policy.RetentionDays == 0 ? "forever" : _policy.RetentionDays + " days")}, "
                + $"keep newest {_policy.KeepLast}");
            _ = System.Threading.Tasks.Task.Run(() => ApplyRetention("startup"));
        }

        public static bool Start(ReplayMetadata metadata)
        {
            if (!_policy.Enabled) return false;
            Stop();
            _lastError = null; _lastWriter = null;
            try
            {
                if (metadata.MapHash == 0)
                    throw new IOException("The server replay map could not be identified.");

                string room = Sanitize(metadata.RoomKey.Length == 0 ? "match" : metadata.RoomKey);
                CurrentPath = Path.Combine(ReplayDirectory,
                    $"{room}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}");
                _pending = true;
                Console.WriteLine($"[replay] canonical server recording: {CurrentPath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                _lastError = ex.Message;
                _writer = null;
                CurrentPath = null;
                Console.WriteLine($"[replay] canonical recording did not start: {ex.Message}");
                return false;
            }
        }

        internal static void Tick()
        {
            try
            {
                if (_pending && ReplayCapture.WorldCapture.World is { } world)
                {
                    _origin = world.Session.RecordingFrame;
                    _writer = new ReplayWritePump(CurrentPath!, ReplayTimelineArchive.Metadata(world, ReplayType.FullMatch), _origin,
                        finalized: () => ApplyRetention("match finalization"));
                    _pending = false; _lastWriter = _writer;
                    _writer.Event(new(0, ReplayEventType.MatchStarted));
                }
                if (_writer != null && !_writer.EndFrame(Frame())) Fail(_writer.Error!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            { Fail(ex); }
        }
        private static void Accept(ReplayTimelineRecord record)
        {
            if (_writer == null) return;
            try { if (!_writer.Record(record)) Fail(_writer.Error!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Fail(ex); }
        }
        private static void Fail(Exception ex)
        {
            _lastError = ex.Message; _writer?.Abort(); _writer = null; _pending = false; CurrentPath = null;
            Console.WriteLine($"[replay] canonical recording interrupted; recover its .part file: {ex.Message}");
        }

        public static void Stop(bool matchEnded = false)
        {
            ReplayWritePump? writer = _writer;
            _writer = null; _pending = false;
            if (writer == null)
            {
                CurrentPath = null;
                return;
            }
            try
            {
                if (matchEnded)
                    writer.Event(new ReplayEvent(Frame(), ReplayEventType.MatchEnded));
                writer.Complete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                _lastError = ex.Message;
                writer.Abort();
                Console.WriteLine($"[replay] canonical recording interrupted: {ex.Message}");
            }
            finally
            {
                CurrentPath = null;
            }
        }

        private static uint Frame() => NetSession.NetFrame >= _origin ? NetSession.NetFrame - _origin : 0;

        private static void ApplyRetention(string reason)
        {
            try
            {
                ServerReplayRetentionResult result = ServerReplayRetention.Apply(
                    ReplayDirectory, _policy, CurrentPath);
                if (result.DeletedFiles > 0)
                {
                    Console.WriteLine($"[replay] retention after {reason}: deleted "
                        + $"{result.DeletedFiles}, {FormatBytes(result.BeforeBytes)} -> "
                        + $"{FormatBytes(result.AfterBytes)}");
                }
                if (!result.LimitSatisfied)
                {
                    Console.WriteLine($"[replay] retention after {reason}: storage remains "
                        + $"{FormatBytes(result.AfterBytes)} because {_policy.KeepLast} newest "
                        + "recordings/favorites are protected");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                Console.WriteLine($"[replay] retention after {reason} skipped: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            const double GiB = 1024d * 1024 * 1024;
            const double MiB = 1024d * 1024;
            return bytes >= GiB ? $"{bytes / GiB:0.00} GiB"
                : bytes >= MiB ? $"{bytes / MiB:0.0} MiB"
                : $"{bytes / 1024d:0.0} KiB";
        }

        private static string Sanitize(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }
    }
}
