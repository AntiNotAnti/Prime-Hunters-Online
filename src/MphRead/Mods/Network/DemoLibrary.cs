using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MphRead.Mods.Network
{
    /// <summary>One recording found on disk, as a screen needs to show it.</summary>
    internal readonly struct DemoRecording
    {
        public string Path { get; }
        /// <summary>The room, when the file name still carries it. Otherwise empty.</summary>
        public string Room { get; }
        public DateTime Recorded { get; }
        public long Bytes { get; }

        public ReplayMetadata? Metadata { get; }
        public ReplayOpenResult Compatibility { get; }
        public uint DurationFrames { get; }
        public string DisplayName => DemoLibrary.DisplayName(Path, Room.Length > 0 ? Room : FileName);
        public bool Favorite => File.Exists(Path + ".favorite");
        public ReplayIntegrity Integrity => DemoLibrary.VerifiedIntegrity(Path) ?? Metadata?.Integrity ?? (Compatibility is ReplayOpenResult.Corrupt or ReplayOpenResult.InvalidMagic ? ReplayIntegrity.Corrupt : Compatibility == ReplayOpenResult.Truncated ? ReplayIntegrity.Truncated : ReplayIntegrity.Unknown);
        public string FileName => System.IO.Path.GetFileName(Path);

        public DemoRecording(string path, string room, DateTime recorded, long bytes, ReplayMetadata? metadata = null, ReplayOpenResult compatibility = ReplayOpenResult.Success, uint duration = 0)
        {
            Path = path;
            Room = room;
            Recorded = recorded;
            Bytes = bytes;
            Metadata = metadata;
            Compatibility = compatibility;
            DurationFrames = duration;
        }
    }

    /// <summary>
    /// The recordings this machine made, listed from the folder they are
    /// written to.
    ///
    /// It exists because the system file picker is the wrong tool for them on
    /// the platform most likely to be recording. <see cref="DemoRecorder"/>
    /// writes into the app's own directory, and since Android 11
    /// <c>Android/data</c> is excluded from the Storage Access Framework: the
    /// picker cannot be pointed at it and a player cannot navigate to it, even
    /// though the app itself reads and writes there with no permission at all.
    /// So the app lists its own folder and the picker is kept for the other
    /// case -- importing a demo somebody sent you, which really is somewhere
    /// else.
    /// </summary>
    internal static class DemoLibrary
    {
        /// <summary>
        /// Where <see cref="DemoRecorder"/> writes, as an absolute path.
        ///
        /// Absolute matters: <c>Paths.Export</c> is empty in every paths.txt a
        /// desktop extraction produces, which makes the recorder's own combine
        /// a *relative* path resolved against the working directory. That is
        /// fine for writing and useless for handing to anything else.
        /// </summary>
        private static readonly Dictionary<string, (long Bytes, DateTime Modified, uint Frames)> Durations = new();
        private static readonly Dictionary<string, (long Bytes, DateTime Modified, ReplayIntegrity Integrity)> Validation = new();
        public static ReplayIntegrity? VerifiedIntegrity(string path)
        {
            var info = new FileInfo(path);
            return Validation.TryGetValue(path, out var value) && info.Exists && value.Bytes == info.Length && value.Modified == info.LastWriteTimeUtc ? value.Integrity : null;
        }
        public static void NoteValidation(string path, ReplayOpenResult result)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            Validation[path] = (info.Length, info.LastWriteTimeUtc, result == ReplayOpenResult.Success ? ReplayIntegrity.Healthy : result == ReplayOpenResult.Truncated ? ReplayIntegrity.Truncated : ReplayIntegrity.Corrupt);
        }
        public static uint Duration(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return 0;
            if (Durations.TryGetValue(path, out var cached) && cached.Bytes == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached.Frames;
            using var reader = DemoReader.Open(path);
            uint frames = reader?.DurationFrames ?? 0;
            if (reader != null && reader.FormatVersion == 2)
                while (reader.ReadNext() is DemoRecord record) frames = record.Frame;
            Durations[path] = (info.Length, info.LastWriteTimeUtc, frames);
            return frames;
        }

        public static string Directory =>
            Path.GetFullPath(Paths.Combine(Paths.Export, "_demos"));

        /// <summary>
        /// Every recording in that folder, newest first.
        ///
        /// Nothing is opened. The file name carries the room and the moment
        /// already (see <see cref="DemoRecorder.Start"/>), and reading a
        /// header out of every file on a phone to learn what the name says is
        /// a directory listing turned into a disk full of seeks.
        /// </summary>
        public static IReadOnlyList<DemoRecording> List()
        {
            var found = new List<DemoRecording>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string directory = Directory;
                if (!System.IO.Directory.Exists(directory))
                {
                    return found;
                }
                foreach (string path in System.IO.Directory
                    .EnumerateFiles(directory, "*" + DemoFile.Extension + "*"))
                {
                    if (!path.EndsWith(DemoFile.Extension, StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(DemoFile.Extension + ".part", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var info = new FileInfo(path);
                    present.Add(info.Name);
                    if (ReplayLibraryIndex.TryGet(path, out DemoRecording indexed))
                    {
                        found.Add(indexed);
                        continue;
                    }

                    (string room, DateTime? stamp) = ReadName(info.Name);
                    using var reader = DemoReader.Open(path, out ReplayOpenResult result,
                        metadataOnly: true);
                    ReplayMetadata? metadata = reader?.Metadata;
                    if (reader != null && reader.ProtocolVersion != NetConfig.ProtocolVersion)
                        result = ReplayOpenResult.ProtocolMismatch;
                    uint duration = reader?.FormatVersion == 2
                        && Durations.TryGetValue(path, out var cached)
                        && cached.Bytes == info.Length
                        && cached.Modified == info.LastWriteTimeUtc
                            ? cached.Frames
                            : reader?.DurationFrames ?? 0;
                    var recording = new DemoRecording(path, metadata?.RoomKey ?? room,
                        metadata?.RecordedAtUtc.ToLocalTime() ?? stamp ?? info.LastWriteTime,
                        info.Length, metadata, result, duration);
                    found.Add(recording);
                    ReplayLibraryIndex.Note(path, recording, reader?.FormatVersion ?? 0);
                }
                ReplayLibraryIndex.Prune(present);
                ReplayLibraryIndex.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"[demo] could not list {Directory}: {ex.Message}");
            }
            found.Sort((a, b) => a.Favorite == b.Favorite
                ? b.Recorded.CompareTo(a.Recorded)
                : b.Favorite.CompareTo(a.Favorite));
            return found;
        }

        /// <summary>
        /// Take the room and the moment back out of "ROOM_2026-09-04_18-22-07".
        ///
        /// The stamp is a fixed nineteen characters at the end, which is what
        /// makes this safe: a room name may contain underscores of its own
        /// (SanitizeFileName puts one in for every character a file name
        /// cannot hold), so splitting on the separator would cut the wrong one.
        /// </summary>
        private static (string Room, DateTime? Stamp) ReadName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            const int stampLength = 19; // yyyy-MM-dd_HH-mm-ss
            if (name.Length < stampLength + 2 || name[^(stampLength + 1)] != '_')
            {
                return (name, null);
            }
            string stamp = name[^stampLength..];
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                return (name, null);
            }
            return (name[..^(stampLength + 1)], parsed);
        }

        /// <summary>"4 Sep 2026, 18:22 — 1.4 MB".</summary>
        public static string Describe(DemoRecording demo)
        {
            string duration = demo.DurationFrames > 0 ? Replay.ReplayHud.Time(demo.DurationFrames) : "duration unknown";
            string integrity = demo.Integrity switch { ReplayIntegrity.Unknown => "Not checked", ReplayIntegrity.Healthy => "Healthy", ReplayIntegrity.Recovered => "Recovered", ReplayIntegrity.Truncated => "Incomplete", _ => "Damaged" };
            string compatibility = demo.Compatibility switch { ReplayOpenResult.Success => "Compatible", ReplayOpenResult.ProtocolMismatch => "Incompatible protocol", ReplayOpenResult.UnsupportedFormat => "Unsupported format", _ => "Cannot read" };
            return $"{duration} / {integrity} / {compatibility}";
        }
        public static string Details(DemoRecording demo)
        {
            string details = $"{demo.Recorded:d MMM yyyy, HH:mm} / {Size(demo.Bytes)}";
            if (demo.Metadata is ReplayMetadata metadata)
                details = $"{metadata.Mode} / {metadata.Players.Count} players / {(metadata.Type == ReplayType.FullMatch ? "Full match" : "Clip")}\n"
                    + string.Join(", ", System.Linq.Enumerable.Select(metadata.Players, p => p.Name)) + "\n"
                    + (metadata.BuildMatches ? "Same build" : "Different build") + " / " + details;
            return details;
        }

        public static string DisplayName(string path, string fallback)
        {
            try { return File.Exists(path + ".name") ? File.ReadAllText(path + ".name").Trim() : fallback; }
            catch (IOException) { return fallback; }
        }
        public static void Rename(string path, string name)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > 100) throw new ArgumentException("Use a replay name between 1 and 100 characters.");
            File.WriteAllText(path + ".name", name);
        }
        public static void ToggleFavorite(string path)
        {
            string marker = path + ".favorite";
            if (File.Exists(marker)) File.Delete(marker); else File.WriteAllText(marker, "");
        }
        public static void Delete(string path)
        {
            File.Delete(path);
            File.Delete(path + ".name");
            File.Delete(path + ".favorite");
            File.Delete(path + ".camera");
            File.Delete(path + ".analytics.json");
            for (int i = 0; i < 3; i++)
                File.Delete(path + $".thumb{i}.png");
            Durations.Remove(path);
            ReplayLibraryIndex.Remove(path);
            ReplayLibraryIndex.Flush();
        }
        private static string Size(long bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return (bytes / (1024f * 1024f)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }
            if (bytes >= 1024)
            {
                return (bytes / 1024) + " KB";
            }
            return bytes + " bytes";
        }
    }
}
