using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace MphRead.Mods.Network
{
    internal sealed record ReplayLibraryPlayerEntry(byte Slot, byte Hunter, sbyte Team, string Name);

    internal sealed class ReplayLibraryIndexEntry
    {
        public string FileName { get; set; } = "";
        public long Bytes { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string Room { get; set; } = "";
        public long RecordedUtcTicks { get; set; }
        public byte FormatVersion { get; set; }
        public byte ProtocolVersion { get; set; }
        public ReplayOpenResult Compatibility { get; set; }
        public uint DurationFrames { get; set; }
        public bool HasMetadata { get; set; }
        public GameMode Mode { get; set; }
        public ReplayType Type { get; set; }
        public ulong MapHash { get; set; }
        public string BuildId { get; set; } = "";
        public string BuildVersion { get; set; } = "";
        public ReplayIntegrity Integrity { get; set; }
        public List<ReplayLibraryPlayerEntry> Players { get; set; } = new();

        public DemoRecording ToRecording(string path)
        {
            ReplayMetadata? metadata = null;
            if (HasMetadata)
            {
                metadata = new ReplayMetadata
                {
                    FormatVersion = FormatVersion,
                    ProtocolVersion = ProtocolVersion,
                    RecordedAtUtc = RecordedUtcTicks > 0
                        ? new DateTime(RecordedUtcTicks, DateTimeKind.Utc)
                        : File.GetLastWriteTimeUtc(path),
                    Type = Type,
                    RoomKey = Room,
                    MapHash = MapHash,
                    Mode = Mode,
                    Integrity = Integrity,
                    BuildId = BuildId,
                    BuildVersion = BuildVersion,
                    Players = Players.Select(p => new ReplayPlayerInfo(
                        p.Slot, p.Hunter, p.Team, p.Name)).ToArray()
                };
            }
            DateTime recorded = RecordedUtcTicks > 0
                ? new DateTime(RecordedUtcTicks, DateTimeKind.Utc).ToLocalTime()
                : File.GetLastWriteTime(path);
            return new DemoRecording(path, Room, recorded, Bytes, metadata,
                Compatibility, DurationFrames);
        }
    }

    /// <summary>
    /// Persistent replay-library header cache. A replay is only opened when its
    /// size or mtime changed; normal library rebuilds are directory metadata reads.
    /// </summary>
    internal static class ReplayLibraryIndex
    {
        private const int Version = 1;
        private sealed class Document
        {
            public int Version { get; set; } = ReplayLibraryIndex.Version;
            public Dictionary<string, ReplayLibraryIndexEntry> Entries { get; set; }
                = new(StringComparer.OrdinalIgnoreCase);
        }

        private static Document? _document;
        private static bool _dirty;
        private static string IndexPath => Path.Combine(DemoLibrary.Directory, "library.index.json");

        private static Document Load()
        {
            if (_document != null) return _document;
            try
            {
                if (File.Exists(IndexPath))
                {
                    Document? loaded = JsonSerializer.Deserialize<Document>(
                        File.ReadAllText(IndexPath));
                    if (loaded is { Version: Version })
                    {
                        loaded.Entries = new Dictionary<string, ReplayLibraryIndexEntry>(
                            loaded.Entries, StringComparer.OrdinalIgnoreCase);
                        _document = loaded;
                        return loaded;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException)
            {
                Console.WriteLine($"[replay] library index ignored: {ex.Message}");
            }
            return _document = new Document();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static bool TryGet(string path, out DemoRecording recording)
        {
            recording = default;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                string key = info.Name;
                Document doc = Load();
                if (!doc.Entries.TryGetValue(key, out ReplayLibraryIndexEntry? entry)
                    || entry.Bytes != info.Length
                    || entry.ModifiedUtcTicks != info.LastWriteTimeUtc.Ticks)
                    return false;
                recording = entry.ToRecording(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void Note(string path, DemoRecording recording, byte formatVersion)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            ReplayMetadata? metadata = recording.Metadata;
            var entry = new ReplayLibraryIndexEntry
            {
                FileName = info.Name,
                Bytes = info.Length,
                ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                Room = recording.Room,
                RecordedUtcTicks = recording.Recorded.ToUniversalTime().Ticks,
                FormatVersion = formatVersion,
                ProtocolVersion = metadata?.ProtocolVersion ?? 0,
                Compatibility = recording.Compatibility,
                DurationFrames = recording.DurationFrames,
                HasMetadata = metadata != null,
                Mode = metadata?.Mode ?? default,
                Type = metadata?.Type ?? ReplayType.FullMatch,
                MapHash = metadata?.MapHash ?? 0,
                BuildId = metadata?.BuildId ?? "",
                BuildVersion = metadata?.BuildVersion ?? "",
                Integrity = metadata?.Integrity ?? ReplayIntegrity.Unknown,
                Players = metadata?.Players.Select(p => new ReplayLibraryPlayerEntry(
                    p.Slot, p.Hunter, p.Team, p.Name)).ToList() ?? new()
            };
            Load().Entries[info.Name] = entry;
            _dirty = true;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void Remove(string path)
        {
            if (Load().Entries.Remove(Path.GetFileName(path)))
                _dirty = true;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void Prune(ISet<string> presentFileNames)
        {
            Document doc = Load();
            foreach (string key in doc.Entries.Keys
                .Where(k => !presentFileNames.Contains(k)).ToArray())
            {
                doc.Entries.Remove(key);
                _dirty = true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void Flush()
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(DemoLibrary.Directory);
                string temp = IndexPath + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Load(),
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, IndexPath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                Console.WriteLine($"[replay] could not save library index: {ex.Message}");
            }
        }
    }
}
