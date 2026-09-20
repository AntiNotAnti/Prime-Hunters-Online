using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// The level's own textures, already in the form the hardware wants.
    ///
    /// A converted map used to wear textures borrowed from a shipped room,
    /// which looks wrong and puts cartridge data inside the generated files.
    /// These are the Quake level's own art instead, quantised to an 8-bit
    /// palette by <c>tools/bake-textures.py</c> -- ahead of time, because the
    /// conversion runs on the machine that plays the game and on Android that
    /// machine has no JPEG decoder.
    ///
    /// Entries are keyed by the index of the shader in the BSP's texture lump,
    /// so the importer can name one without depending on the order they were
    /// baked in.
    /// </summary>
    public sealed class MapTexturePack
    {
        public sealed class Entry
        {
            public int SourceIndex { get; init; }
            public string Name { get; init; } = "";
            public ushort Width { get; init; }
            public ushort Height { get; init; }
            public IReadOnlyList<ushort> Palette { get; init; } = Array.Empty<ushort>();
            public IReadOnlyList<byte> Pixels { get; init; } = Array.Empty<byte>();
        }

        public IReadOnlyList<Entry> Entries { get; }

        /// <summary>Shader index in the BSP to position in <see cref="Entries"/>.</summary>
        public IReadOnlyDictionary<int, int> BySourceIndex { get; }

        private MapTexturePack(IReadOnlyList<Entry> entries)
        {
            Entries = entries;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < entries.Count; i++)
            {
                map[entries[i].SourceIndex] = i;
            }
            BySourceIndex = map;
        }

        /// <summary>The same pack out of a map bundle, where it is bytes rather than a file.</summary>
        public static MapTexturePack Load(byte[] bytes, string name)
        {
            using var memory = new MemoryStream(bytes);
            return Load(memory, name);
        }

        public static MapTexturePack Load(string path)
        {
            using var stream = File.OpenRead(path);
            return Load(stream, Path.GetFileName(path));
        }

        private static MapTexturePack Load(Stream stream, string path)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (new string(reader.ReadChars(4)) != "FPTX")
            {
                throw new ProgramException($"{Path.GetFileName(path)} is not a texture pack.");
            }
            ushort version = reader.ReadUInt16();
            if (version != 1)
            {
                throw new ProgramException($"{Path.GetFileName(path)} is version {version}; this build reads 1.");
            }
            int count = reader.ReadUInt16();
            var entries = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                ushort sourceIndex = reader.ReadUInt16();
                ushort width = reader.ReadUInt16();
                ushort height = reader.ReadUInt16();
                int paletteLength = reader.ReadUInt16();
                int nameLength = reader.ReadUInt16();
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                var palette = new ushort[paletteLength];
                for (int p = 0; p < paletteLength; p++)
                {
                    palette[p] = reader.ReadUInt16();
                }
                byte[] pixels = reader.ReadBytes(width * height);
                entries.Add(new Entry()
                {
                    SourceIndex = sourceIndex,
                    Name = name,
                    Width = width,
                    Height = height,
                    Palette = palette,
                    Pixels = pixels
                });
            }
            return new MapTexturePack(entries);
        }
    }
}
