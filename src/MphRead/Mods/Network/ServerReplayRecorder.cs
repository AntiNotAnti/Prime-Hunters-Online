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
        private static ReplayWriterV3? _writer;
        private static uint _startFrame;
        private static ServerReplayPolicy _policy = ServerReplayPolicy.Default;

        public static string ReplayDirectory => Paths.Combine(
            Paths.Export, "_demos", "server");
        public static bool Enabled => _policy.Enabled;
        public static bool IsRecording => _writer != null;
        public static string? CurrentPath { get; private set; }
        public static string? LastError { get; private set; }

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
            ApplyRetention("startup");
        }

        public static bool Start(ReplayMetadata metadata)
        {
            if (!_policy.Enabled) return false;
            Stop();
            LastError = null;
            try
            {
                if (metadata.MapHash == 0)
                    throw new IOException("The server replay map could not be identified.");

                string room = Sanitize(metadata.RoomKey.Length == 0 ? "match" : metadata.RoomKey);
                Directory.CreateDirectory(ReplayDirectory);
                CurrentPath = Path.Combine(ReplayDirectory,
                    $"{room}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{DemoFile.Extension}");
                _writer = new ReplayWriterV3(CurrentPath, metadata);
                _startFrame = NetSession.NetFrame;
                _writer.WriteEvent(new ReplayEvent(0, ReplayEventType.MatchStarted));
                Console.WriteLine($"[replay] canonical server recording: {CurrentPath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                LastError = ex.Message;
                _writer = null;
                CurrentPath = null;
                Console.WriteLine($"[replay] canonical recording did not start: {ex.Message}");
                return false;
            }
        }

        public static void Record(PacketType type, ReadOnlySpan<byte> payload)
        {
            if (_writer == null) return;
            byte[] packet = new byte[1 + payload.Length];
            packet[0] = (byte)type;
            payload.CopyTo(packet.AsSpan(1));
            Write(packet);
        }

        public static void RecordSlotIntent(int slot, ReadOnlySpan<byte> payload)
        {
            if (_writer == null || slot is < 0 or >= RosterPacket.MaxSlots) return;
            byte[] packet = new byte[2 + payload.Length];
            packet[0] = (byte)PacketType.SlotIntent;
            packet[1] = (byte)slot;
            payload.CopyTo(packet.AsSpan(2));
            Write(packet);
        }

        public static void RecordEvent(ReplayEvent value)
        {
            if (_writer == null) return;
            uint frame = Frame();
            _writer.WriteEvent(value with { Frame = frame });
        }

        public static void Stop(bool matchEnded = false)
        {
            ReplayWriterV3? writer = _writer;
            _writer = null;
            if (writer == null)
            {
                CurrentPath = null;
                return;
            }
            try
            {
                if (matchEnded)
                    writer.WriteEvent(new ReplayEvent(Frame(), ReplayEventType.MatchEnded));
                writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                LastError = ex.Message;
                writer.Abort();
                Console.WriteLine($"[replay] canonical recording interrupted: {ex.Message}");
            }
            finally
            {
                CurrentPath = null;
                if (_policy.Enabled) ApplyRetention("match finalization");
            }
        }

        private static void Write(ReadOnlySpan<byte> packet)
        {
            ReplayWriterV3? writer = _writer;
            if (writer == null) return;
            try
            {
                writer.WriteRecord(Frame(), packet);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                LastError = ex.Message;
                writer.Abort();
                _writer = null;
                CurrentPath = null;
                Console.WriteLine($"[replay] canonical recording interrupted; recover its .part file: {ex.Message}");
            }
        }

        private static uint Frame()
        {
            uint now = NetSession.NetFrame;
            return now >= _startFrame ? now - _startFrame : 0;
        }

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
