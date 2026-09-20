using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// One file that is a whole custom map: the recipe, the level it converts
    /// and the textures baked from it.
    ///
    /// A map used to be a folder of three files, one of them somebody else's
    /// `.pk3` -- 2.7 MB of Quake level of which this importer reads 1.1, the
    /// rest being lightmaps, light volumes and a BSP tree nothing here opens.
    /// A bundle is those three cooked into a zip with the level trimmed to the
    /// lumps <see cref="Q3Bsp.UsedLumps"/> names: de_dust2 comes out at 386 KB
    /// against the 2.8 MB the folder shipped, and it is one file, which is
    /// what makes a map something you can hand somebody. Handing them out is
    /// the point: the plan is for a server to offer its maps to players who do
    /// not have them, and a downloader wants one file with everything in it,
    /// not a folder to reassemble.
    ///
    /// It is a zip because <see cref="Q3Bsp.Load"/> already opens one and
    /// finds the level inside it by name -- that is how a .pk3 is read -- so
    /// the level half of this format cost nothing to support.
    ///
    /// What a bundle does *not* settle is whether a level may be handed out at
    /// all. Cooking somebody's level into a smaller container leaves it their
    /// level; that judgement belongs to whoever publishes the bundle.
    /// </summary>
    public static class MapBundle
    {
        public const string Extension = ".fpmap";

        /// <summary>Where the level goes inside the bundle. The pk3 layout, so the same reader finds it.</summary>
        private const string LevelDirectory = "maps/";

        public static bool Is(string path)
        {
            return Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cook a map folder into a bundle: the recipe as it stands, the level
        /// with its unread lumps emptied, and the baked texture pack if the
        /// map names one.
        ///
        /// The recipe inside is rewritten to point at what is beside it in the
        /// bundle rather than at the pk3 it was made from, so a bundle names
        /// nothing that is not in it.
        /// </summary>
        public static string Cook(MapDefinition definition, string recipePath, string? outputPath,
            bool verbose = true)
        {
            MapImport? import = definition.Import;
            if (import == null || import.Source.Length == 0)
            {
                throw new ProgramException($"{definition.Name} builds from its own description; "
                    + "there is no level to bundle.");
            }
            string? level = import.Resolve();
            if (level == null)
            {
                throw new ProgramException($"{definition.Name}: its source level {import.Source} "
                    + "is not here, so there is nothing to cook.");
            }
            string mapName = import.MapName ?? Path.GetFileNameWithoutExtension(level);
            byte[] trimmed = Q3Bsp.Trim(Q3Bsp.ReadLevel(level, import.MapName));
            string? texturePath = import.ResolveTextures();
            if (texturePath == null && !String.IsNullOrEmpty(import.Textures))
            {
                // Bake it now, because a bundle cannot be baked from later.
                // The pack is derived from the level's own art, so it is not
                // in git and a fresh clone does not have one -- the game bakes
                // it the first time the map is played. A bundle carries the
                // level trimmed to the lumps the importer reads, and the art
                // is in none of them: it lives in the .pk3 beside the recipe,
                // which the bundle exists not to hand out. So a bundle cooked
                // where the pack was not already sitting there -- a CI runner,
                // every time -- shipped a map with no textures, which is a map
                // with no materials, which crashed the moment it was picked.
                texturePath = Q3Import.BakeTextures(
                    Q3Bsp.Load(level, import.MapName), import, verbose);
                if (texturePath == null)
                {
                    throw new ProgramException($"{definition.Name}: its textures "
                        + $"({import.Textures}) are not beside its recipe and could not be baked "
                        + "from " + Path.GetFileName(level) + ". A bundle without them is a room "
                        + "with no materials, so this is a failure and not a bundle.");
                }
            }
            // The top of maps/ by default, not beside the recipe. The working
            // copy of a map is a folder with somebody's .pk3 in it; the bundle
            // is the one file that goes out, and it goes where every platform
            // already looks -- including Android, whose asset glob does not
            // recurse into folders.
            string path = outputPath ?? Path.Combine(CustomRooms.MapDirectory,
                Path.GetFileNameWithoutExtension(recipePath) + Extension);
            string recipeName = Path.GetFileName(recipePath);
            string textureName = texturePath == null ? "" : Path.GetFileName(texturePath);
            // A copy, because what goes in the bundle names what is in the
            // bundle: the source is the level beside it, not the pk3 it came
            // out of, which the player will not have.
            // A hand-edited collision mesh is the map, not a working file: the
            // room it produces cannot be built without it, so it travels with
            // the recipe the way the level and the baked textures do.
            string? collisionPath = null;
            if (definition.Collision != null && definition.Collision.Source.Length > 0)
            {
                collisionPath = definition.Collision.Resolve()
                    ?? throw new ProgramException($"{definition.Name}: its collision mesh "
                        + $"{definition.Collision.Source} is not beside its recipe, and a bundle "
                        + "without it is a map that cannot be generated.");
            }
            MapDefinition inside = MapDefinition.Load(recipePath);
            if (inside.Import != null)
            {
                inside.Import.Source = $"{LevelDirectory}{mapName}.bsp";
                inside.Import.MapName = mapName;
                inside.Import.Textures = textureName;
            }
            if (inside.Collision != null && collisionPath != null)
            {
                inside.Collision.Source = Path.GetFileName(collisionPath);
            }
            string temporary = path + ".tmp";
            using (var file = File.Create(temporary))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Write(archive, recipeName, System.Text.Encoding.UTF8.GetBytes(inside.Serialize()));
                Write(archive, $"{LevelDirectory}{mapName}.bsp", trimmed);
                if (texturePath != null)
                {
                    Write(archive, textureName, File.ReadAllBytes(texturePath));
                }
                if (collisionPath != null)
                {
                    Write(archive, Path.GetFileName(collisionPath), File.ReadAllBytes(collisionPath));
                }
            }
            File.Move(temporary, path, overwrite: true);
            if (verbose)
            {
                long before = new FileInfo(level).Length
                    + (texturePath == null ? 0 : new FileInfo(texturePath).Length);
                Console.WriteLine($"[mapbundle] {definition.Name} -> {path} "
                    + $"({new FileInfo(path).Length / 1024} KiB, from {before / 1024} KiB)");
            }
            return path;
        }

        private static void Write(ZipArchive archive, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>The recipe inside a bundle, or null if it holds none.</summary>
        public static string? ReadRecipe(string bundlePath)
        {
            using ZipArchive archive = ZipFile.OpenRead(bundlePath);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return null;
            }
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// One file out of a bundle, by name or by the end of a name -- the
        /// recipe names "dust2.tex" and the entry is "dust2.tex", but a
        /// bundle cooked from a folder layout may carry a path.
        /// </summary>
        public static byte[]? ReadEntry(string bundlePath, string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                return null;
            }
            using ZipArchive archive = ZipFile.OpenRead(bundlePath);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault(
                    e => e.FullName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return null;
            }
            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
