using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// The source form of a custom map: everything needed to generate a room,
    /// in text, with no game data in it.
    ///
    /// This is deliberately the thing that lives in the repository. The three
    /// binaries a room is made of are derived from the player's own extracted
    /// files -- the textures especially -- so they are generated on the
    /// player's machine and never committed or shipped, exactly like the
    /// extraction itself. A map is a JSON file; a map is not a .bin.
    ///
    /// Coordinates are in MPH world units throughout (1 unit = 4096 in the
    /// game's fixed point). Anything imported from another engine is converted
    /// before it lands here, so this file never carries foreign units.
    /// </summary>
    public class MapDefinition
    {
        public int FormatVersion { get; set; } = 1;
        public Guid MapId { get; set; }
        public string? Author { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string Name { get; set; } = "CUSTOM";
        public string? InGameName { get; set; }

        /// <summary>Room whose textures and palettes are copied into this map.</summary>
        public string TextureSource { get; set; } = "MP3 PROVING GROUND";

        /// <summary>
        /// Model scale as a power of two. Vertices are 16-bit fixed point, so
        /// model space spans +/-8 units and the world extent is +/-8 * 2^this.
        /// 4 gives +/-128 units at a precision of about 4 mm, which suits an
        /// arena; raising it trades precision for reach.
        /// </summary>
        public int ScaleFactor { get; set; } = 4;

        public float KillHeight { get; set; } = -40f;
        public float FarClip { get; set; } = 350f;

        public bool FogEnabled { get; set; } = true;
        public int[] FogColor { get; set; } = new[] { 8, 10, 16 };
        public int FogSlope { get; set; } = 5;
        public int FogOffset { get; set; } = 65180;

        public int[] Light1Color { get; set; } = new[] { 31, 28, 24 };
        public float[] Light1Vector { get; set; } = new[] { 0.3f, -1f, 0.2f };
        public int[] Light2Color { get; set; } = new[] { 10, 11, 16 };
        public float[] Light2Vector { get; set; } = new[] { -0.3f, 1f, -0.2f };

        public uint BattleTimeLimit { get; set; } = 7 * 60 * 30;
        public short PointLimit { get; set; } = 7;

        /// <summary>Set to convert a level from another engine instead of building from brushes.</summary>
        public MapImport? Import { get; set; }

        /// <summary>
        /// Collision read from a Wavefront OBJ, replacing whatever the
        /// geometry would have produced. See <see cref="MapCollision"/>.
        /// </summary>
        public MapCollision? Collision { get; set; }

        /// <summary>Where to stand the camera for the launcher's map picture.</summary>
        public MapPreview? Preview { get; set; }

        public List<MapMaterial> Materials { get; set; } = new List<MapMaterial>();
        public List<MapBrush> Brushes { get; set; } = new List<MapBrush>();
        public List<MapGeometry> Geometry { get; set; } = new();
        public List<MapAsset> Assets { get; set; } = new();
        public MapAudioSettings? Audio { get; set; }
        public MapCapabilities? Capabilities { get; set; }
        public List<MapNavigationLink> NavigationLinks { get; set; } = new();
        public List<MapSpawn> Spawns { get; set; } = new List<MapSpawn>();
        public List<MapJumpPad> JumpPads { get; set; } = new List<MapJumpPad>();
        public List<MapItem> Items { get; set; } = new List<MapItem>();

        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions()
        {
            PropertyNameCaseInsensitive = true,
            // Every recipe in the repository is camelCase, having been written
            // by hand before anything generated one. Reading is
            // case-insensitive either way; this is so a recipe a command
            // writes looks like the ones beside it.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// The folder the map file was read from, so a map that keeps its own
        /// level and textures in a folder of its own -- maps/dust2/ -- finds
        /// them beside itself rather than only in maps/. Not part of the file:
        /// it is where the file was.
        /// </summary>
        [JsonIgnore]
        public string? BaseDirectory { get; set; }

        /// <summary>
        /// The bundle this recipe came out of, if it came out of one. Its
        /// level and its textures are inside the same file; see
        /// <see cref="MapBundle"/>.
        /// </summary>
        [JsonIgnore]
        public string? BundlePath { get; set; }

        /// <summary>The file this was read from -- a recipe, or the bundle holding one.</summary>
        [JsonIgnore]
        public string? SourcePath { get; set; }

        public static MapDefinition Load(string path)
        {
            if(!MapBundle.Is(path)&&new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Map project exceeds 8 MiB.");
            string text = MapBundle.Is(path)
                ? MapBundle.ReadRecipe(path)
                    ?? throw new ProgramException($"{Path.GetFileName(path)} has no map in it.")
                : File.ReadAllText(path);
            MapDefinition? result = JsonSerializer.Deserialize<MapDefinition>(text, _options);
            if (result == null)
            {
                throw new ProgramException($"Could not read map definition {path}.");
            }
            if (result.FormatVersion is < 1 or > 2)
                throw new MapAuthoringException("FP-MAP-008", $"Unsupported map format {result.FormatVersion}.");
            MapValidator.RequireRuntimeName(result.Name);
            if(result.FormatVersion==1)result.Name=result.Name.ToUpperInvariant();
            result.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
            result.SourcePath = Path.GetFullPath(path);
            result.BundlePath = MapBundle.Is(path) ? result.SourcePath : null;
            if (result.Import != null)
            {
                result.Import.BaseDirectory = result.BaseDirectory;
                result.Import.BundlePath = result.BundlePath;
            }
            if (result.Collision != null)
            {
                result.Collision.BaseDirectory = result.BaseDirectory;
                result.Collision.BundlePath = result.BundlePath;
            }
            return result;
        }

        public void Save(string path)
        {
            AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(Serialize()));
        }

        /// <summary>The recipe as it would be written, for a bundle to carry.</summary>
        public string Serialize()
        {
            return JsonSerializer.Serialize(this, _options);
        }
    }

    /// <summary>
    /// The shot the launcher shows. A shipped room gets its picture from the
    /// intro camera the developers authored for it; a custom map has none, so
    /// it names a viewpoint instead.
    /// </summary>
    public class MapPreview
    {
        public float[] Position { get; set; } = new float[3];
        public float[] Target { get; set; } = new float[3];
    }

    /// <summary>
    /// Collision read from a Wavefront OBJ rather than derived from the
    /// geometry.
    ///
    /// It **replaces** what the geometry produced rather than adding to it,
    /// and that is the whole point: the reason to reach for this is that a
    /// converted level carries collision nobody can ever touch -- on df_dust2,
    /// 372 faces and about a quarter of the room's collision area, outside the
    /// part of the map anyone can reach -- and adding could never delete one
    /// of them. Export with `tools/collision-to-obj.py`, edit, name it here.
    ///
    /// Writing a whole room's collision from nothing is not what this is for
    /// and would be miserable; the OBJ to start from is the one the exporter
    /// writes.
    /// </summary>
    public class MapCollision
    {
        /// <summary>
        /// The .obj, looked for beside the recipe, then in maps/, then beside
        /// the game files -- the same places a level and a texture pack are.
        /// </summary>
        public string Source { get; set; } = "";

        /// <summary>
        /// Set when the file was written with the exporter's `--zup`, which is
        /// what Blender and most other tools want. The game is Y up and so is
        /// the exporter by default.
        /// </summary>
        public bool ZUp { get; set; }

        [JsonIgnore]
        public string? BaseDirectory { get; set; }
        [JsonIgnore]
        public string? BundlePath { get; set; }

        /// <summary>The bytes, out of the bundle or off the disk, or null when
        /// the file is not on this machine.</summary>
        public byte[]? ReadBytes()
        {
            if (string.IsNullOrEmpty(Source))
            {
                return null;
            }
            if (BundlePath != null)
            {
                // A package must be self-contained; never read a local file
                // to satisfy a missing dependency from a downloaded map.
                return MapBundle.ReadEntry(BundlePath, Source)
                    ?? throw new InvalidDataException("Packaged collision mesh is missing.");
            }
            string? path = Resolve();
            if (path == null) return null;
            if (new FileInfo(path).Length > MapPackageReader.MaxEntryBytes)
                throw new InvalidDataException("Collision mesh exceeds the size limit.");
            return File.ReadAllBytes(path);
        }

        public string? Resolve()
        {
            if (string.IsNullOrEmpty(Source))
            {
                return null;
            }
            foreach (string candidate in Candidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private IEnumerable<string> Candidates()
        {
            if (Path.IsPathRooted(Source))
            {
                yield return Source;
                yield break;
            }
            if (BaseDirectory != null)
            {
                yield return Path.Combine(BaseDirectory, Source);
            }
            yield return Source;
            yield return Path.Combine(CustomRooms.MapDirectory, Source);
            yield return Path.Combine(Mods.Launcher.GameFiles.Root, Source);
        }
    }

    /// <summary>
    /// How to convert a Quake 3 level. The geometry stays in the player's own
    /// .pk3 -- this only says where to find it and how to translate it, so
    /// what lives in the repository is a conversion recipe and not a level
    /// somebody else wrote.
    /// </summary>
    public class MapImport
    {
        /// <summary>
        /// The .bsp, or the .pk3 with <see cref="MapName"/> naming the level
        /// inside it.
        ///
        /// A bare name is looked for beside the map files and then beside the
        /// game files, which is what lets one map file work on a desktop and a
        /// phone: the level it converts is nobody's to ship -- not ours to put
        /// in an APK and not the repository's to carry -- so it is the
        /// player's copy, in the directory they already put their own files
        /// in. An absolute path is taken as given.
        /// </summary>
        public string Source { get; set; } = "";

        /// <summary>
        /// Where the source level actually is, or null when it is not on this
        /// machine at all.
        /// </summary>
        /// <summary>The bundle this map travels in, if it travels in one. See <see cref="MapDefinition.BundlePath"/>.</summary>
        [JsonIgnore]
        public string? BundlePath { get; set; }

        public string? Resolve()
        {
            if (string.IsNullOrEmpty(Source))
            {
                return null;
            }
            if (BundlePath != null)
            {
                // The level is inside the bundle, and Q3Bsp.Load opens a zip
                // and finds a level in it by name -- which is how it reads a
                // .pk3. So the bundle *is* the source path.
                return BundlePath;
            }
            foreach (string candidate in Candidates(Source))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>Set from the map file's own folder; see <see cref="MapDefinition.BaseDirectory"/>.</summary>
        [JsonIgnore]
        public string? BaseDirectory { get; set; }

        private IEnumerable<string> Candidates(string name)
        {
            if (Path.IsPathRooted(name))
            {
                yield return name;
                yield break;
            }
            if (BaseDirectory != null)
            {
                yield return Path.Combine(BaseDirectory, name);
            }
            // A project owns its relative dependencies. Keep the historical
            // working-directory fallback only when that project has no match.
            yield return name;
            yield return Path.Combine(CustomRooms.MapDirectory, name);
            yield return Path.Combine(Mods.Launcher.GameFiles.Root, name);
        }
        public string? MapName { get; set; }

        /// <summary>
        /// Quake 3 units per MPH unit.
        ///
        /// The number that matters is the one that keeps the level's routes
        /// intact: a gap the map's author expected a player to clear must
        /// still be clearable. Samus jumps 1228/4096 per frame against 77/4096
        /// of gravity, so 2.39 units up and about 7.7 across at her walking
        /// cap; a Quake player leaves the ground at 270 u/s under 800 u/s^2,
        /// so 45.6 up and about 216 across. Dividing by less than 216/7.7 =
        /// 28.2 makes the world too big for its own jumps, and 22 -- which
        /// this was, chosen by feel -- puts a full-length Quake jump at 9.8
        /// units against her 7.7.
        ///
        /// 28 is therefore the floor, and it is the default. 35 would match
        /// the architecture exactly (56-unit Quake player against Samus's
        /// 1.6), and is worth trying on a map with no long jumps: everything
        /// above 28 only makes jumping easier than the author intended, while
        /// anything below it breaks routes.
        /// </summary>
        public float UnitsPerUnit { get; set; } = 28f;

        /// <summary>
        /// A texture pack baked from the level's own art by
        /// tools/bake-textures.py. With one, the map wears the textures it was
        /// made with and borrows nothing from a shipped room -- which also
        /// keeps cartridge data out of the files it generates. Looked for in
        /// the same places as <see cref="Source"/>.
        /// </summary>
        public string? Textures { get; set; }

        /// <summary>Where the texture pack is, or null if there is none here.</summary>
        /// <summary>
        /// The baked texture pack out of the bundle, or null when this map
        /// does not travel in one. Bytes rather than a path: nothing is
        /// unpacked to disk, so a bundle stays one file on the player's
        /// machine as well as in the download.
        /// </summary>
        public byte[]? ReadBundledTextures()
        {
            if (BundlePath == null || String.IsNullOrEmpty(Textures))
            {
                return null;
            }
            return MapBundle.ReadEntry(BundlePath, Textures);
        }

        /// <summary>
        /// This map's own baked textures, wherever they live: inside its
        /// bundle, or beside its recipe. Null means it has none and wears a
        /// shipped room's instead.
        ///
        /// One accessor because there are two callers -- the importer and the
        /// packer -- and when only the importer knew about bundles, a bundled
        /// map imported with its own art and was then packed with somebody
        /// else's, which is the branch that fails outright with "a map needs
        /// at least one material".
        /// </summary>
        public MapTexturePack? LoadTexturePack()
        {
            byte[]? bundled = ReadBundledTextures();
            if (bundled != null)
            {
                // ReadBundledTextures returns nothing without a name to look
                // one up by, so there is one here.
                return MapTexturePack.Load(bundled, Textures ?? "");
            }
            string? path = ResolveTextures();
            return path == null ? null : MapTexturePack.Load(path);
        }

        public string? ResolveTextures()
        {
            if (String.IsNullOrEmpty(Textures))
            {
                return null;
            }
            foreach (string candidate in Candidates(Textures))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>Shader name (or any prefix of it) to material index.</summary>
        public Dictionary<string, int> ShaderMaterials { get; set; } = new Dictionary<string, int>();
        public int DefaultMaterial { get; set; }

        /// <summary>Texels per world unit applied to the imported UVs.</summary>
        public float TexScale { get; set; } = 24f;

        /// <summary>
        /// Draw the level's sky surfaces. With a texture pack that has an
        /// image for the sky shader this is the level's own sky; without one
        /// the surfaces are dropped anyway and the result is the same as
        /// leaving it off. The sky shell is never collision either way.
        /// </summary>
        public bool KeepSky { get; set; }

        /// <summary>
        /// Keep the level's player-clip brushes -- its invisible walls.
        ///
        /// True is right for a level authored for the game it came from: a
        /// clip brush is usually there to stop an exploit or to smooth a
        /// staircase. It is wrong for a level whose clips exist to fence a
        /// route, which is every race map, where they turn a map you want to
        /// roam into a corridor.
        /// </summary>
        public bool KeepClip { get; set; } = true;

        /// <summary>
        /// How finely to tessellate a Bezier patch: this many quads along each
        /// side of each biquadratic piece. 3 is enough for an archway and
        /// costs 18 triangles a piece; past about 5 the triangles are smaller
        /// than the texels.
        /// </summary>
        public int PatchLevel { get; set; } = 3;

        /// <summary>
        /// Take the level's own player starts as spawn points. True is right
        /// for a deathmatch level, which was authored with eight of them in
        /// the places its author wanted people to appear. It is wrong for
        /// anything else: a race level has one start, often on a ledge sealed
        /// off from the course, and a player who spawns there is stuck. Turn
        /// it off and the map file's own spawns are the only ones.
        /// </summary>
        public bool KeepSpawns { get; set; } = true;

        /// <summary>
        /// Take the level's own pickups -- its health, armour, ammo and
        /// weapons -- as items, on top of whatever the recipe's own
        /// <see cref="MapDefinition.Items"/> lists.
        ///
        /// True is what every map did before there was a choice, and is
        /// therefore the default: an existing recipe still generates the room
        /// it was generating. False makes the recipe the only answer to where
        /// the pickups are, which is what you want once they are written down
        /// -- otherwise moving one in the recipe leaves the level's original
        /// where it was and the map has both.
        ///
        /// `-mapitems "ROOM"` prints the level's pickups as an `items` block
        /// to paste in, which is the other half of turning this off.
        /// </summary>
        public bool KeepItems { get; set; } = true;
    }

    /// <summary>
    /// A material, described as "the texture that material N of the source
    /// room uses". Copying the pair rather than an index into the texture list
    /// means the palette always matches the texture, which is the one thing
    /// that is silently wrong if you pick them separately.
    /// </summary>
    public class MapMaterial
    {
        public Guid Id { get; set; }
        public string? Texture { get; set; }
        public string Name { get; set; } = "mat";
        /// <summary>Index of the material in the source room to take the texture and palette from.</summary>
        public int SourceMaterial { get; set; }
        /// <summary>Texels per world unit. 32 means a 32x32 texture tiles once per unit.</summary>
        public float TexScale { get; set; } = 16f;
    }

    /// <summary>An axis-aligned box. Six quads of geometry, six faces of collision.</summary>
    public class MapBrush
    {
        public Guid Id { get; set; }
        public string? Label { get; set; }
        public float[] Min { get; set; } = new float[3];
        public float[] Max { get; set; } = new float[3];
        public int Material { get; set; }
        /// <summary>Brightness applied to the vertex colour, per face, before the top/side falloff.</summary>
        public float Shade { get; set; } = 1f;
        /// <summary>False for decoration the player passes through.</summary>
        public bool Solid { get; set; } = true;
        /// <summary>Kills on contact -- lava, or the floor of a pit.</summary>
        public bool Damaging { get; set; }
        public string? Terrain { get; set; }
    }

    public class MapSpawn : MapEntityDefinition
    {
        public int Team { get; set; } = -1;
        /// <summary>Degrees, 0 = facing +Z, counter-clockwise seen from above.</summary>
        public float Yaw { get; set; }
    }

    /// <summary>
    /// A jump pad. Either give it a Target and let the launch velocity be
    /// solved for, or set Vector and Speed directly.
    /// </summary>
    public class MapJumpPad : MapEntityDefinition
    {
        public float[]? Target { get; set; }
        public float[]? Vector { get; set; }
        public float Speed { get; set; }
        /// <summary>
        /// The trigger box. Tall enough that a player who arrives falling,
        /// rather than walking, is still inside it on the frame it is tested.
        /// </summary>
        public float[] Size { get; set; } = new[] { 1.6f, 1.8f, 1.6f };
        public uint ModelId { get; set; }
        public ushort CooldownTime { get; set; } = 20;
        public ushort ControlLockTime { get; set; } = 30;
    }

    public class MapItem : MapEntityDefinition
    {
        public string Type { get; set; } = "MissileSmall";
        public bool HasBase { get; set; } = true;
        public ushort SpawnInterval { get; set; } = 300;
    }
}
