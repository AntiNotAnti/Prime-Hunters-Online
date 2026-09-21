using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Reads the lumps of a Quake 3 level.
    ///
    /// Only what a conversion needs: the drawn surfaces, the brushes the
    /// collision comes from, and the entity list. The visibility tree, the
    /// lightmaps and the fog volumes have no counterpart here and are not
    /// touched.
    ///
    /// The file itself stays where the player put it. Nothing read here is
    /// written back out in its original form or committed anywhere.
    /// </summary>
    public class Q3Bsp
    {
        public IReadOnlyList<Q3Texture> Textures { get; private set; } = Array.Empty<Q3Texture>();
        public IReadOnlyList<Q3Plane> Planes { get; private set; } = Array.Empty<Q3Plane>();
        public IReadOnlyList<Q3Brush> Brushes { get; private set; } = Array.Empty<Q3Brush>();
        public IReadOnlyList<Q3BrushSide> BrushSides { get; private set; } = Array.Empty<Q3BrushSide>();
        public IReadOnlyList<Q3Vertex> Vertices { get; private set; } = Array.Empty<Q3Vertex>();
        public IReadOnlyList<int> MeshVerts { get; private set; } = Array.Empty<int>();
        public IReadOnlyList<Q3Face> Faces { get; private set; } = Array.Empty<Q3Face>();
        public IReadOnlyList<Q3Model> Models { get; private set; } = Array.Empty<Q3Model>();
        public IReadOnlyList<Dictionary<string, string>> Entities { get; private set; }
            = Array.Empty<Dictionary<string, string>>();

        public const int ContentsSolid = 0x1;
        public const int ContentsPlayerClip = 0x10000;
        public const int SurfaceSky = 0x4;
        public const int SurfaceNoDraw = 0x80;
        public const int SurfaceHint = 0x100;
        public const int SurfaceSkip = 0x200;

        /// <summary>
        /// The lumps <see cref="Parse"/> actually reads, and so the only ones
        /// worth carrying: entities, textures, planes, models, brushes,
        /// brushsides, vertexes, meshverts, faces.
        ///
        /// The rest of a compiled level -- lightmaps, light volumes, visdata
        /// and the BSP tree itself -- is for a renderer that lights and culls
        /// the level the way Quake does, and this importer does neither. In
        /// df_dust2 they are 7.6 MB of the 8.8, which is what
        /// <see cref="Trim"/> exists to leave behind.
        ///
        /// Beside the reader deliberately: the day it reads a tenth lump,
        /// this is the line that has to grow with it, and a trimmed level
        /// missing that lump would be a map that half builds.
        /// </summary>
        public static readonly int[] UsedLumps = { 0, 1, 2, 7, 8, 9, 10, 11, 13 };

        /// <summary>
        /// The same level with every lump this importer never opens emptied
        /// out, ready to be compressed into a map bundle.
        ///
        /// The header keeps all seventeen entries, because that is what makes
        /// it a Quake 3 level; the ones that are gone are given a length of
        /// zero rather than removed, so any reader that looks at them finds
        /// nothing instead of finding somebody else's bytes.
        /// </summary>
        public static byte[] Trim(byte[] bsp)
        {
            if (bsp.Length < 8 + 17 * 8)
            {
                throw new ProgramException("Not a Quake 3 level: too short to hold a header.");
            }
            var output = new List<byte>(bsp.Length / 4);
            output.AddRange(bsp.AsSpan(0, 8 + 17 * 8).ToArray());
            Span<byte> header = CollectionsMarshal.AsSpan(output);
            for (int i = 0; i < 17; i++)
            {
                int offset = BitConverter.ToInt32(bsp, 8 + i * 8);
                int length = BitConverter.ToInt32(bsp, 8 + i * 8 + 4);
                if (!UsedLumps.Contains(i) || offset < 0 || length <= 0
                    || offset + length > bsp.Length)
                {
                    BitConverter.TryWriteBytes(header[(8 + i * 8)..], output.Count);
                    BitConverter.TryWriteBytes(header[(8 + i * 8 + 4)..], 0);
                    continue;
                }
                BitConverter.TryWriteBytes(header[(8 + i * 8)..], output.Count);
                BitConverter.TryWriteBytes(header[(8 + i * 8 + 4)..], length);
                output.AddRange(bsp.AsSpan(offset, length).ToArray());
                while (output.Count % 4 != 0)
                {
                    output.Add(0);
                }
                header = CollectionsMarshal.AsSpan(output);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Loads a level from a .bsp on disk, or by name from inside a .pk3 or
        /// a map bundle, both of which are zips. Passing the pk3 is the normal
        /// case for a map somebody is converting; a bundle is what a converted
        /// map is handed out as.
        /// </summary>
        public static Q3Bsp Load(string source, string? mapName)
        {
            return Parse(ReadLevel(source, mapName));
        }

        /// <summary>
        /// The level's bytes, out of a .bsp or out of the zip around it --
        /// what <see cref="Load"/> parses and what <see cref="Trim"/> cooks.
        /// </summary>
        public static byte[] ReadLevel(string source, string? mapName)
        {
            if (!File.Exists(source))
            {
                throw new ProgramException($"No such file: {source}");
            }
            if (Path.GetExtension(source).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
            {
                if(new FileInfo(source).Length>MapPackageReader.MaxEntryBytes)throw new InvalidDataException("BSP exceeds the map asset limit.");
                return File.ReadAllBytes(source);
            }
            using ZipArchive archive = ZipFile.OpenRead(source);
            List<ZipArchiveEntry> maps = archive.Entries
                .Where(e => e.FullName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)).ToList();
            if (maps.Count == 0)
            {
                throw new ProgramException($"{Path.GetFileName(source)} contains no .bsp.");
            }
            ZipArchiveEntry? entry = mapName == null
                ? maps[0]
                : maps.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.FullName)
                    .Equals(mapName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                string available = String.Join(", ", maps
                    .Select(e => Path.GetFileNameWithoutExtension(e.FullName)).OrderBy(n => n));
                throw new ProgramException($"{Path.GetFileName(source)} has no map {mapName}. It has: {available}");
            }
            using Stream stream = entry.Open();
            if(entry.Length>MapPackageReader.MaxEntryBytes)throw new InvalidDataException("BSP exceeds the map asset limit.");
            using var memory = new MemoryStream();
            byte[] buffer=new byte[65536];int read;
            while((read=stream.Read(buffer))>0)
            {
                if(memory.Length+read>MapPackageReader.MaxEntryBytes)throw new InvalidDataException("BSP exceeds the map asset limit.");
                memory.Write(buffer,0,read);
            }
            return memory.ToArray();
        }

        public static IReadOnlyList<string> ListMaps(string source)
        {
            if (Path.GetExtension(source).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { Path.GetFileNameWithoutExtension(source) };
            }
            using ZipArchive archive = ZipFile.OpenRead(source);
            return archive.Entries
                .Where(e => e.FullName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName))
                .OrderBy(n => n)
                .ToList();
        }

        private static Q3Bsp Parse(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            string magic = new string(reader.ReadChars(4));
            int version = reader.ReadInt32();
            if (magic != "IBSP" || version != 46)
            {
                throw new ProgramException($"Not a Quake 3 level (magic {magic}, version {version}).");
            }
            var offsets = new (int offset, int length)[17];
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = (reader.ReadInt32(), reader.ReadInt32());
                if(offsets[i].offset<0||offsets[i].length<0||(long)offsets[i].offset+offsets[i].length>bytes.Length)
                    throw new InvalidDataException("Invalid BSP lump bounds.");
            }
            var bsp = new Q3Bsp();
            bsp.Entities = ParseEntities(Encoding.ASCII.GetString(bytes, offsets[0].offset, offsets[0].length));
            bsp.Textures = ReadLump(reader, offsets[1], 72, r =>
            {
                string name = Encoding.ASCII.GetString(r.ReadBytes(64)).TrimEnd('\0');
                return new Q3Texture(name, r.ReadInt32(), r.ReadInt32());
            });
            bsp.Planes = ReadLump(reader, offsets[2], 16, r =>
                new Q3Plane(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            bsp.Models = ReadLump(reader, offsets[7], 40, r =>
            {
                float[] mins = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                float[] maxs = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                return new Q3Model(mins, maxs, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            });
            bsp.Brushes = ReadLump(reader, offsets[8], 12, r =>
                new Q3Brush(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
            bsp.BrushSides = ReadLump(reader, offsets[9], 8, r =>
                new Q3BrushSide(r.ReadInt32(), r.ReadInt32()));
            bsp.Vertices = ReadLump(reader, offsets[10], 44, r =>
            {
                float[] position = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                float[] surface = new[] { r.ReadSingle(), r.ReadSingle() };
                r.ReadSingle(); // lightmap S
                r.ReadSingle(); // lightmap T
                float[] normal = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                byte[] color = r.ReadBytes(4);
                return new Q3Vertex(position, surface, normal, color);
            });
            bsp.MeshVerts = ReadLump(reader, offsets[11], 4, r => r.ReadInt32());
            bsp.Faces = ReadLump(reader, offsets[13], 104, r =>
            {
                int texture = r.ReadInt32();
                int effect = r.ReadInt32();
                int type = r.ReadInt32();
                int vertex = r.ReadInt32();
                int vertexCount = r.ReadInt32();
                int meshVert = r.ReadInt32();
                int meshVertCount = r.ReadInt32();
                r.ReadInt32(); // lightmap index
                r.ReadBytes(8 + 8 + 12 + 24); // lightmap start, size, origin, vectors
                float[] normal = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                int[] size = new[] { r.ReadInt32(), r.ReadInt32() };
                return new Q3Face(texture, effect, type, vertex, vertexCount, meshVert, meshVertCount, normal, size);
            });
            static bool Range(int start,int count,int size)=>start>=0&&count>=0&&(long)start+count<=size;
            foreach(var brush in bsp.Brushes)
                if(!Range(brush.FirstSide,brush.SideCount,bsp.BrushSides.Count)||brush.SideCount>128||brush.Texture<0||brush.Texture>=bsp.Textures.Count)
                    throw new InvalidDataException("Invalid BSP brush references.");
            foreach(var side in bsp.BrushSides)
                if(side.Plane<0||side.Plane>=bsp.Planes.Count||side.Texture<0||side.Texture>=bsp.Textures.Count)throw new InvalidDataException("Invalid BSP brush side.");
            foreach(var model in bsp.Models)
                if(!Range(model.Face,model.FaceCount,bsp.Faces.Count)||!Range(model.Brush,model.BrushCount,bsp.Brushes.Count))throw new InvalidDataException("Invalid BSP model ranges.");
            foreach(var face in bsp.Faces)
            {
                if(!Range(face.Vertex,face.VertexCount,bsp.Vertices.Count)||!Range(face.MeshVert,face.MeshVertCount,bsp.MeshVerts.Count)
                    ||face.Texture<0||face.Texture>=bsp.Textures.Count)throw new InvalidDataException("Invalid BSP face references.");
                if(face.Type==2&&(face.Size[0]<3||face.Size[1]<3||face.Size[0]>129||face.Size[1]>129||(long)face.Size[0]*face.Size[1]>face.VertexCount))
                    throw new InvalidDataException("Invalid BSP patch dimensions.");
                for(int i=face.MeshVert;i<face.MeshVert+face.MeshVertCount;i++)
                    if(bsp.MeshVerts[i]<0||bsp.MeshVerts[i]>=face.VertexCount)throw new InvalidDataException("Invalid BSP mesh vertex index.");
            }
            if(bsp.Vertices.Any(v=>v.Position.Concat(v.Surface).Concat(v.Normal).Any(x=>!float.IsFinite(x)))
                ||bsp.Planes.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||!float.IsFinite(p.Distance)))
                throw new InvalidDataException("Nonfinite BSP coordinates.");
            return bsp;
        }

        private static IReadOnlyList<T> ReadLump<T>(BinaryReader reader, (int offset, int length) lump,
            int size, Func<BinaryReader, T> read)
        {
            if(lump.length%size!=0)throw new InvalidDataException("BSP lump has a partial record.");
            var results = new List<T>(lump.length / size);
            for (int i = 0; i < lump.length / size; i++)
            {
                reader.BaseStream.Position = lump.offset + i * size;
                results.Add(read(reader));
            }
            return results;
        }

        /// <summary>
        /// The entity lump is one long string of { "key" "value" } blocks.
        /// </summary>
        private static IReadOnlyList<Dictionary<string, string>> ParseEntities(string text)
        {
            var results = new List<Dictionary<string, string>>();
            Dictionary<string, string>? current = null;
            var token = new StringBuilder();
            var tokens = new List<string>();
            bool inString = false;
            foreach (char c in text)
            {
                if (c == '"')
                {
                    if (inString)
                    {
                        tokens.Add(token.ToString());
                        token.Clear();
                    }
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    token.Append(c);
                    continue;
                }
                if (c == '{')
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    tokens.Clear();
                }
                else if (c == '}')
                {
                    if (current != null)
                    {
                        for (int i = 0; i + 1 < tokens.Count; i += 2)
                        {
                            current[tokens[i]] = tokens[i + 1];
                        }
                        results.Add(current);
                        current = null;
                    }
                    tokens.Clear();
                }
            }
            return results;
        }
    }

    public record Q3Texture(string Name, int Flags, int Contents);
    public record Q3Plane(float X, float Y, float Z, float Distance);
    public record Q3Brush(int FirstSide, int SideCount, int Texture);
    public record Q3BrushSide(int Plane, int Texture);
    public record Q3Vertex(float[] Position, float[] Surface, float[] Normal, byte[] Color);
    public record Q3Face(int Texture, int Effect, int Type, int Vertex, int VertexCount,
        int MeshVert, int MeshVertCount, float[] Normal, int[] Size);
    public record Q3Model(float[] Mins, float[] Maxs, int Face, int FaceCount, int Brush, int BrushCount);
}
