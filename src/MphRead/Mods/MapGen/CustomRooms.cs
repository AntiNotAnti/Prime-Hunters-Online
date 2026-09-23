using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Makes custom maps into rooms the rest of the game already knows how to
    /// handle: the launcher lists them, -maptest loads them, the server can
    /// run them.
    ///
    /// A map is a source project or package in `maps/`. Its five
    /// binaries are generated into the player's own extracted files, since
    /// that is where a room's paths point and where the textures come from,
    /// and all outputs are regenerated when a dependency fingerprint changes.
    /// </summary>
    public static class CustomRooms
    {
        private static IReadOnlyList<MapDefinition>? _definitions;
        private static int _firstId = -1;
        internal static int FirstId => _firstId;
        // Android builds the map binaries on a background thread while the
        // front screen is listing rooms on another, and both go through here.
        private static readonly object _lock = new object();

        /// <summary>
        /// Where the map files are. In Resources for a Mac app bundle and
        /// beside the executable for portable desktop builds. The
        /// Android head moves it, because the package directory there is read
        /// only and the maps have to live where the extracted game files
        /// already do. Set it before anything reads <see cref="Definitions"/>:
        /// the list is loaded once and cached.
        /// </summary>
        public static string MapDirectory { get; set; }
            = Platform.AppPaths.Maps;

        public static IReadOnlyList<MapDefinition> Definitions
        {
            get
            {
                lock (_lock)
                {
                    _definitions ??= LoadDefinitions();
                    return _definitions;
                }
            }
        }

        /// <summary>
        /// Every map file: a loose recipe, one in a folder of its own with the
        /// level it converts beside it, or a bundle, which is all of that in
        /// one file (see <see cref="MapBundle"/>).
        ///
        /// A bundle wins where both exist, and it is the same map either way:
        /// the working copy of a map is a folder with somebody's .pk3 in it,
        /// and the bundle is what that folder is cooked into to be shipped or
        /// handed out, so a checkout that has both would otherwise register
        /// the same room twice.
        /// </summary>
        private static IReadOnlyList<MapDefinition> LoadDefinitions()
        {
            var catalog = new MapCatalog(MapDirectory);
            var entries = catalog.Refresh();
            foreach (var entry in entries)
                foreach (var diagnostic in entry.Validation.Diagnostics)
                    Console.WriteLine($"[map] {Path.GetFileName(entry.Path)} {diagnostic.Code}: {diagnostic.Message}");
            return entries.Where(e => e.Definition != null && e.Validation.IsValid)
                .Select(e => e.Definition!).ToArray();
        }

        // Runtime IDs are a process snapshot. Refreshing the editor/catalog must
        // never replace that snapshot beneath a loaded match or a room vote.
        public static IReadOnlyList<MapCatalogEntry> Reload()
        {
            lock (_lock)
            {
                var entries = new MapCatalog(MapDirectory).Refresh();
                if (_firstId < 0) _definitions = null;
                return entries;
            }
        }
        internal static void InstallSnapshot(MapDefinition definition)
        {
            lock(_lock)
            {
                var list=Definitions.ToList();int index=list.FindIndex(d=>d.Name.Equals(definition.Name,StringComparison.OrdinalIgnoreCase));
                if(index<0)list.Add(definition);else list[index]=definition;
                _definitions=list.AsReadOnly();
            }
        }

        /// <summary>Called from the room ID table, which fixes each room's ID as its index.</summary>
        public static IReadOnlyList<string> AppendIds(List<string> ids)
        {
            _definitions=Definitions.Where(d=>!ids.Contains(d.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
            _firstId = ids.Count;
            ids.AddRange(Definitions.Select(d => d.Name));
            return ids;
        }

        /// <summary>Called from the room table, after the IDs have been assigned.</summary>
        public static IReadOnlyList<RoomMetadata> AppendRooms(List<RoomMetadata> rooms)
        {
            for (int i = 0; i < Definitions.Count; i++)
            {
                rooms.Add(MakeMetadata(Definitions[i], _firstId + i));
            }
            return rooms;
        }

        internal static RoomMetadata MakeMetadata(MapDefinition def, int id)
        {
            string prefix = def.Name.ToLowerInvariant();
            // Metadata is also read before game-file setup. Only filenames
            // belong here; resolving runtime directories requires configured paths.
            MapOutputSet outputs = MapOutputSet.Create(def,"","","");
            return new RoomMetadata(
                id: id,
                name: def.Name,
                inGameName: def.InGameName ?? def.Name,
                archive: prefix,
                modelPath: Path.GetFileName(outputs.Model),
                animationPath: Path.GetFileName(outputs.Animation),
                collisionPath: Path.GetFileName(outputs.Collision),
                texturePath: null, // the textures are inside the model file
                entityPath: Path.GetFileName(outputs.Entities),
                // the metadata prepends levels\nodeData\ itself
                nodePath: Path.GetFileName(outputs.Nodes),
                roomNodeName: null,
                battleTimeLimit: def.BattleTimeLimit,
                timeLimit: def.BattleTimeLimit,
                pointLimit: def.PointLimit,
                nodeLayer: 0,
                fogEnabled: def.FogEnabled,
                clearFog: false,
                fogColor: ToColor(def.FogColor),
                fogSlope: def.FogSlope,
                fogOffset: (ushort)def.FogOffset,
                light1Color: ToColor(def.Light1Color),
                light1Vector: ToVector(def.Light1Vector),
                light2Color: ToColor(def.Light2Color),
                light2Vector: ToVector(def.Light2Vector),
                farClip: Fixed.ToInt(def.FarClip),
                killHeight: Fixed.ToInt(def.KillHeight),
                size: RoomSize.Large,
                // no camera or player limits: a custom map decides its own
                // extent, and a limit box inherited from someone else's room
                // is how the camera ends up stuck behind a wall
                multiplayer: true);
        }

        private static ColorRgb ToColor(int[] values)
        {
            return new ColorRgb((byte)values[0], (byte)values[1], (byte)values[2]);
        }

        private static Vector3 ToVector(float[] values)
        {
            return new Vector3(values[0], values[1], values[2]);
        }

        public static string ArchiveDirectory(MapDefinition def)
        {
            return Paths.Combine(Paths.FileSystem, @"_archives", def.Name.ToLowerInvariant());
        }

        public static string EntityDirectory()
        {
            return Paths.Combine(Paths.FileSystem, @"levels\entities");
        }

        public static string NodeDirectory()
        {
            return Paths.Combine(Paths.FileSystem, @"levels\nodeData");
        }

        /// <summary>
        /// Generates every map whose binaries are missing or older than its
        /// source. Returns the number generated.
        /// </summary>
        public static int GenerateAll(bool force = false, bool verbose = true)
        {
            int count = 0;
            foreach (MapDefinition def in Definitions)
            {
                if (force || NeedsGenerating(def))
                {
                    MapPacker.Generate(def, ArchiveDirectory(def), EntityDirectory(), NodeDirectory(), verbose);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Generates any map whose binaries are missing or out of date, and
        /// says so only when there is something to say. Never throws: a map
        /// that cannot be built must not stop the game from starting, and the
        /// line it prints is what explains the room that is not there.
        /// </summary>
        public static void GenerateMissing()
        {
            IReadOnlyList<MapDefinition> definitions;
            try
            {
                definitions = Definitions;
            }
            catch
            {
                return;
            }
            foreach (MapDefinition def in definitions)
            {
                GenerateIfNeeded(def);
            }
        }

        /// <summary>
        /// Generate only the room about to be loaded. Normal retail rooms are
        /// not in <see cref="Definitions"/>, so this is effectively free for
        /// the common online-match path and avoids re-validating every custom
        /// package each time Start Match is pressed.
        /// </summary>
        public static void GenerateMissing(string roomName)
        {
            if (String.IsNullOrWhiteSpace(roomName)) return;
            MapDefinition? definition;
            try
            {
                definition = Definitions.FirstOrDefault(def =>
                    def.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return;
            }
            if (definition != null) GenerateIfNeeded(definition);
        }

        private static void GenerateIfNeeded(MapDefinition def)
        {
            // Lobby prewarm, Android preview workers and the real match can ask
            // for the same runtime outputs concurrently. Fence publication across
            // threads/processes, then re-check after acquiring the lease so every
            // waiter except the first becomes a cache hit.
            try
            {
                using var lease = AcquireGenerationLease(def);
                if (!NeedsGenerating(def))
                    return;
                Console.WriteLine($"[mapgen] building {def.Name}");
                MapPacker.Generate(def, ArchiveDirectory(def), EntityDirectory(), NodeDirectory(), verbose: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[mapgen] {def.Name} could not be built: {ex.Message}");
            }
        }

        private static FileStream AcquireGenerationLease(MapDefinition def)
        {
            string path = OutputsFor(def).Manifest + ".runtime.lock";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(60))
                {
                    Thread.Sleep(25);
                }
            }
        }

        /// <summary>
        /// Why this room cannot be loaded, or null when it can.
        ///
        /// Only a custom map can answer with a reason. A custom room is
        /// registered from its recipe and built from it separately (see
        /// <see cref="GenerateMissing"/>), so the case this exists for is a
        /// map that is listed -- the launcher offers it, the picker shows a
        /// frame for it -- and whose build failed. That used to be a crash
        /// the moment somebody picked it, in a process with no console to say
        /// why, which is the worst way for a bad map file to be reported.
        /// </summary>
        public static string? WhyUnplayable(string roomName)
        {
            MapDefinition? def = null;
            foreach (MapDefinition candidate in Definitions)
            {
                if (candidate.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase))
                {
                    def = candidate;
                    break;
                }
            }
            if (def == null || !NeedsGenerating(def))
            {
                return null;
            }
            string file = Path.GetFileName(def.SourcePath) ?? "its map file";
            return $"{def.Name} could not be built from {file}, so there is no room to load. "
                + "The [mapgen] line above says what went wrong with it.";
        }

        public static MapOutputSet OutputsFor(MapDefinition definition)
            => MapOutputSet.Create(definition, ArchiveDirectory(definition), EntityDirectory(), NodeDirectory());

        public static bool NeedsGenerating(MapDefinition def)
            => !MapBuildManifest.IsCurrent(def, OutputsFor(def));
    }
}
