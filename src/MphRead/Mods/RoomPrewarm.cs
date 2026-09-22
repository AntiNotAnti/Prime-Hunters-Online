using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods
{
    /// <summary>
    /// Warms the one room a persistent lobby is currently advertising.
    /// No scene or GL state is created here. Normal loading consumes the same
    /// lazy file reads and the same pre-parsed room model.
    /// </summary>
    public static class RoomPrewarm
    {
        private static readonly object Gate = new();
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static string _room = "";
        private static int _generation;
        private static Dictionary<string, Lazy<byte[]>> _files = new(PathComparer);
        private static Lazy<Model>? _roomModel;

        public static void Begin(string roomName)
        {
            if (String.IsNullOrWhiteSpace(roomName))
                return;

            RoomMetadata? metadata;
            try
            {
                (metadata, _) = Metadata.GetRoomByName(roomName);
                if (metadata == null)
                    return;

                string root = metadata.FirstHunt || metadata.Hybrid
                    ? Paths.FhFileSystem
                    : Paths.FileSystem;
                if (!Directory.Exists(root))
                    return;
            }
            catch
            {
                // Asset-free tests and an unconfigured launcher have nothing to warm.
                return;
            }

            if (!Headless.Active)
                MphRead.Sound.Sfx.Prewarm();

            int generation;
            lock (Gate)
            {
                if (String.Equals(_room, metadata.Name, StringComparison.OrdinalIgnoreCase))
                    return;
                _room = metadata.Name;
                generation = ++_generation;
                _files = new Dictionary<string, Lazy<byte[]>>(PathComparer);
                _roomModel = null;
            }

            _ = Task.Run(() => Warm(metadata, generation));
        }

        public static void Release(string roomName)
        {
            lock (Gate)
            {
                if (String.Equals(_room, roomName, StringComparison.OrdinalIgnoreCase))
                    ClearLocked();
            }
        }

        public static void Invalidate(string roomName) => Release(roomName);

        public static void Clear()
        {
            lock (Gate)
                ClearLocked();
        }

        internal static bool TryGetFile(string fullPath, out byte[] bytes)
        {
            Lazy<byte[]>? source;
            string key;
            try { key = Path.GetFullPath(fullPath); }
            catch
            {
                bytes = null!;
                return false;
            }

            lock (Gate)
                _files.TryGetValue(key, out source);
            if (source == null)
            {
                bytes = null!;
                return false;
            }

            try
            {
                bytes = source.Value;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bytes = null!;
                return false;
            }
        }

        internal static bool TryGetRoomModel(string roomName, out Model? model)
        {
            Lazy<Model>? source;
            lock (Gate)
            {
                source = String.Equals(_room, roomName, StringComparison.OrdinalIgnoreCase)
                    ? _roomModel
                    : null;
            }
            if (source == null)
            {
                model = null;
                return false;
            }

            try
            {
                model = source.Value;
                return true;
            }
            catch
            {
                model = null;
                return false;
            }
        }

        private static void Warm(RoomMetadata metadata, int generation)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                // Custom maps can compile while players are choosing settings,
                // instead of making Start Match pay that cost.
                MapGen.CustomRooms.GenerateMissing(metadata.Name);

                List<string> paths = AssetPaths(metadata);
                var files = new Dictionary<string, Lazy<byte[]>>(PathComparer);
                foreach (string path in paths)
                {
                    string full = Path.GetFullPath(path);
                    if (!File.Exists(full) || files.ContainsKey(full))
                        continue;
                    files.Add(full, new Lazy<byte[]>(
                        () => File.ReadAllBytes(full),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                }

                var model = new Lazy<Model>(
                    () => Read.PrepareRoomModel(metadata),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                lock (Gate)
                {
                    if (generation != _generation
                        || !String.Equals(_room, metadata.Name, StringComparison.OrdinalIgnoreCase))
                        return;
                    _files = files;
                    _roomModel = model;
                }

                long bytes = 0;
                int count = 0;
                foreach (KeyValuePair<string, Lazy<byte[]>> pair in files)
                {
                    lock (Gate)
                    {
                        if (generation != _generation)
                            return;
                    }
                    try
                    {
                        byte[] data = pair.Value.Value;
                        bytes += data.LongLength;
                        count++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The real loader will make the normal attempt/report.
                    }
                }

                // Moves geometry, texture and animation decode off the Start path.
                _ = model.Value;

                lock (Gate)
                {
                    if (generation != _generation)
                        return;
                }
                Console.WriteLine($"[prewarm] {metadata.Name}: {count} files, "
                    + $"{bytes / (1024.0 * 1024.0):0.0} MiB in {clock.Elapsed.TotalSeconds:0.00}s");
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    if (generation == _generation)
                        ClearLocked();
                }
                Console.WriteLine($"[prewarm] {metadata.Name} skipped: {ex.Message}");
            }
        }

        private static List<string> AssetPaths(RoomMetadata room)
        {
            string modelRoot = room.FirstHunt || room.Hybrid
                ? Paths.FhFileSystem
                : Paths.FileSystem;
            string dataRoot = room.FirstHunt
                ? Paths.FhFileSystem
                : Paths.FileSystem;

            var paths = new List<string>(6);
            Add(paths, modelRoot, room.ModelPath);
            Add(paths, modelRoot, room.AnimationPath);
            Add(paths, modelRoot, room.CollisionPath);
            Add(paths, modelRoot, room.TexturePath);
            Add(paths, dataRoot, room.EntityPath);
            Add(paths, dataRoot, room.NodePath);
            return paths;
        }

        private static void Add(List<string> paths, string root, string? relative)
        {
            if (!String.IsNullOrWhiteSpace(relative))
                paths.Add(Paths.Combine(root, relative));
        }

        private static void ClearLocked()
        {
            _generation++;
            _room = "";
            _files = new Dictionary<string, Lazy<byte[]>>(PathComparer);
            _roomModel = null;
            if (!Headless.Active)
                MphRead.Sound.Sfx.DropPrewarm();
        }
    }
}
