using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Makes custom maps into rooms the rest of the game already knows how to
    /// handle: the launcher lists them, -maptest loads them, the server can
    /// run them.
    ///
    /// A map is a JSON file in `maps/` next to the executable. Its three
    /// binaries are generated into the player's own extracted files, since
    /// that is where a room's paths point and where the textures come from,
    /// and they are regenerated whenever the JSON is newer than they are.
    /// </summary>
    public static class CustomRooms
    {
        private static IReadOnlyList<MapDefinition>? _definitions;
        private static int _firstId = -1;
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
        private static IEnumerable<string> MapFiles()
        {
            if (!Directory.Exists(MapDirectory))
            {
                return Enumerable.Empty<string>();
            }
            var bundles = Directory.EnumerateFiles(MapDirectory, $"*{MapBundle.Extension}",
                SearchOption.AllDirectories).ToList();
            var names = new HashSet<string>(bundles.Select(
                p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
            return bundles.Concat(Directory
                .EnumerateFiles(MapDirectory, "*.json", SearchOption.AllDirectories)
                .Where(p => !names.Contains(Path.GetFileNameWithoutExtension(p))));
        }

        private static IReadOnlyList<MapDefinition> LoadDefinitions()
        {
            var results = new List<MapDefinition>();
            if (!Directory.Exists(MapDirectory))
            {
                return results;
            }
            foreach (string path in MapFiles().OrderBy(p => p))
            {
                try
                {
                    MapDefinition definition = MapDefinition.Load(path);
                    definition.Name = definition.Name.ToUpperInvariant();
                    if (definition.Import != null && definition.Import.Resolve() == null)
                    {
                        // Rooms are indexed by their position in a table that
                        // is built once, so a room registered here cannot be
                        // taken out again later -- it would sit in the launcher
                        // and crash whoever picked it. A converted map whose
                        // source level is not on this machine is the case that
                        // actually happens: the map file travels with the
                        // repository, the level it was made from does not.
                        Console.WriteLine($"Leaving out map {definition.Name}: its source level "
                            + $"{definition.Import.Source} is not here. Put it in "
                            + $"{definition.BaseDirectory ?? MapDirectory} to have this map.");
                        continue;
                    }
                    results.Add(definition);
                }
                catch (Exception ex)
                {
                    // a broken map file must not stop the game from starting
                    Console.WriteLine($"Ignoring map {Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return results;
        }

        /// <summary>Called from the room ID table, which fixes each room's ID as its index.</summary>
        public static IReadOnlyList<string> AppendIds(List<string> ids)
        {
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

        private static RoomMetadata MakeMetadata(MapDefinition def, int id)
        {
            string prefix = def.Name.ToLowerInvariant();
            return new RoomMetadata(
                id: id,
                name: def.Name,
                inGameName: def.InGameName ?? def.Name,
                archive: prefix,
                modelPath: $"{prefix}_Model.bin",
                animationPath: $"{prefix}_Anim.bin",
                collisionPath: $"{prefix}_Collision.bin",
                texturePath: null, // the textures are inside the model file
                entityPath: $"{prefix}_Ent.bin",
                // the metadata prepends levels\nodeData\ itself
                nodePath: $"{prefix}_Node.bin",
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
                try
                {
                    if (!NeedsGenerating(def))
                    {
                        continue;
                    }
                    Console.WriteLine($"[mapgen] building {def.Name}");
                    MapPacker.Generate(def, ArchiveDirectory(def), EntityDirectory(), NodeDirectory(), verbose: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[mapgen] {def.Name} could not be built: {ex.Message}");
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

        private static bool NeedsGenerating(MapDefinition def)
        {
            string prefix = def.Name.ToLowerInvariant();
            string model = Path.Combine(ArchiveDirectory(def), $"{prefix}_Model.bin");
            if (!File.Exists(model)
                || !File.Exists(Path.Combine(EntityDirectory(), $"{prefix}_Ent.bin"))
                || !File.Exists(Path.Combine(NodeDirectory(), $"{prefix}_Node.bin")))
            {
                // every file a room is made of, not just the first: a build
                // from before one of them existed leaves the others in place
                // and looks up to date
                return true;
            }
            // The file it was actually loaded from -- a recipe or a bundle --
            // rather than a search for one named after the room, which a map
            // whose file is not named after its room quietly failed.
            string? source = def.SourcePath;
            return source != null && File.Exists(source)
                && File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(model);
        }
    }
}
