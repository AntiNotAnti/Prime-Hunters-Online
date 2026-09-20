using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ReFuel.Stb;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Bakes a Quake level's own textures into the pack the packer feeds to
    /// the hardware.
    ///
    /// This used to be tools/bake-textures.py, and the Python is still there
    /// for anyone who wants it, but a conversion that needs a Python with
    /// Pillow on it is not a conversion anybody runs. Everything it needs is
    /// already in this process: the archive is a zip, the decoder is the one
    /// the exporter uses, and the quantiser is fifty lines.
    ///
    /// It is still done ahead of time rather than at load: the Android head
    /// has no image decoder at all -- its STB natives are desktop builds, left
    /// out of the APK on purpose -- so a phone can only copy the bytes.
    /// </summary>
    public static class MapTextureBake
    {
        public const int DefaultSize = 64;
        private const int PaletteSize = 256;

        /// <summary>
        /// Skybox and cloud-layer suffixes. A sky shader names no image of its
        /// own: `skyparms` points at a set of six sides or a pair of scrolling
        /// cloud layers, so `textures/skies/cloudsky` is answered by
        /// `cloudsky_1`. Taking the first that exists gives the sky one honest
        /// texture instead of none.
        /// </summary>
        private static readonly string[] _skySuffixes = new[] { "_1", "_2", "_ft", "_bk", "_lf", "_rt", "_up" };

        private static readonly string[] _extensions = new[] { ".tga", ".jpg", ".jpeg", ".png" };

        public sealed class Result
        {
            public int Baked { get; init; }
            public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
            public long Bytes { get; init; }
        }

        /// <summary>
        /// Writes a pack for every shader the level's drawn surfaces use.
        /// Images are looked for in the archives given, in order; a level's own
        /// .pk3 first, then whatever else the player has.
        /// </summary>
        public static Result Bake(Q3Bsp bsp, IReadOnlyList<string> archivePaths, string outputPath,
            int size = DefaultSize, bool sky = true)
        {
            var archives = new List<ZipArchive>();
            try
            {
                foreach (string path in archivePaths)
                {
                    if (File.Exists(path) && !Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                    {
                        archives.Add(ZipFile.OpenRead(path));
                    }
                }
                // A shader name in a .bsp is not the spelling of the file it
                // came from: the compiler upper-cases some of them, and a level
                // whose author worked on Windows has "SandTrim.JPG" answering
                // to "textures/dust2/SANDTRIM". Matching exactly finds nothing
                // and the level comes out untextured.
                var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchive archive in archives)
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (!files.ContainsKey(entry.FullName))
                        {
                            files.Add(entry.FullName, entry);
                        }
                    }
                }
                var entries = new List<(int Index, string Name, ushort[] Palette, byte[] Pixels)>();
                var missing = new List<string>();
                foreach ((int index, string name) in UsedTextures(bsp, sky))
                {
                    byte[]? raw = Find(files, name);
                    if (raw == null)
                    {
                        missing.Add(name);
                        continue;
                    }
                    (ushort[] palette, byte[] pixels) = Quantize(Decode(raw, size), size);
                    entries.Add((index, name, palette, pixels));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                using (var stream = File.Create(outputPath))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(new[] { 'F', 'P', 'T', 'X' });
                    writer.Write((ushort)1);
                    writer.Write((ushort)entries.Count);
                    foreach ((int index, string name, ushort[] palette, byte[] pixels) in entries)
                    {
                        byte[] encoded = Encoding.UTF8.GetBytes(name);
                        writer.Write((ushort)index);
                        writer.Write((ushort)size);
                        writer.Write((ushort)size);
                        writer.Write((ushort)palette.Length);
                        writer.Write((ushort)encoded.Length);
                        writer.Write(encoded);
                        foreach (ushort colour in palette)
                        {
                            writer.Write(colour);
                        }
                        writer.Write(pixels);
                    }
                }
                return new Result()
                {
                    Baked = entries.Count,
                    Missing = missing,
                    Bytes = new FileInfo(outputPath).Length
                };
            }
            finally
            {
                foreach (ZipArchive archive in archives)
                {
                    archive.Dispose();
                }
            }
        }

        /// <summary>Which shaders the drawn surfaces reference, and their names.</summary>
        private static IEnumerable<(int, string)> UsedTextures(Q3Bsp bsp, bool sky)
        {
            var seen = new HashSet<int>();
            var results = new List<(int, string)>();
            foreach (Q3Face face in bsp.Faces)
            {
                if (face.Type != 1 && face.Type != 2 && face.Type != 3)
                {
                    continue;
                }
                if (!seen.Add(face.Texture))
                {
                    continue;
                }
                Q3Texture texture = bsp.Textures[face.Texture];
                if ((texture.Flags & (Q3Bsp.SurfaceNoDraw | Q3Bsp.SurfaceHint | Q3Bsp.SurfaceSkip)) != 0)
                {
                    continue;
                }
                if ((texture.Flags & Q3Bsp.SurfaceSky) != 0 && !sky)
                {
                    continue;
                }
                results.Add((face.Texture, texture.Name));
            }
            results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return results;
        }

        private static byte[]? Find(Dictionary<string, ZipArchiveEntry> files, string name)
        {
            foreach (string suffix in _skySuffixes.Prepend(""))
            {
                foreach (string extension in _extensions)
                {
                    if (files.TryGetValue(name + suffix + extension, out ZipArchiveEntry? entry))
                    {
                        using Stream stream = entry.Open();
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            return null;
        }

        /// <summary>Decode and box-filter down to the square the hardware wants.</summary>
        private static byte[] Decode(byte[] raw, int size)
        {
            using var source = new MemoryStream(raw);
            using StbImage image = StbImage.Load(source, StbiImageFormat.Rgb);
            ReadOnlySpan<byte> pixels = image.AsSpan<byte>();
            int width = image.Width;
            int height = image.Height;
            var result = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
            {
                int y0 = y * height / size;
                int y1 = Math.Max(y0 + 1, (y + 1) * height / size);
                for (int x = 0; x < size; x++)
                {
                    int x0 = x * width / size;
                    int x1 = Math.Max(x0 + 1, (x + 1) * width / size);
                    int r = 0;
                    int g = 0;
                    int b = 0;
                    int count = 0;
                    for (int sy = y0; sy < y1 && sy < height; sy++)
                    {
                        for (int sx = x0; sx < x1 && sx < width; sx++)
                        {
                            int offset = (sy * width + sx) * 3;
                            r += pixels[offset];
                            g += pixels[offset + 1];
                            b += pixels[offset + 2];
                            count++;
                        }
                    }
                    int target = (y * size + x) * 3;
                    result[target] = (byte)(r / Math.Max(1, count));
                    result[target + 1] = (byte)(g / Math.Max(1, count));
                    result[target + 2] = (byte)(b / Math.Max(1, count));
                }
            }
            return result;
        }

        /// <summary>
        /// Median cut to 256 colours. Split the box with the widest channel at
        /// that channel's median until there are enough boxes, then take each
        /// box's mean as its colour -- the usual answer, and enough for a
        /// 64x64 tile that will be seen at a distance on a texture unit that
        /// only reads 8-bit indices anyway.
        /// </summary>
        private static (ushort[], byte[]) Quantize(byte[] rgb, int size)
        {
            int count = size * size;
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = i;
            }
            var boxes = new List<(int Start, int Length)>() { (0, count) };
            while (boxes.Count < PaletteSize)
            {
                int widest = -1;
                int widestSpread = 0;
                int widestChannel = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    (int start, int length) = boxes[i];
                    if (length < 2)
                    {
                        continue;
                    }
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int low = 255;
                        int high = 0;
                        for (int j = start; j < start + length; j++)
                        {
                            int value = rgb[indices[j] * 3 + channel];
                            low = Math.Min(low, value);
                            high = Math.Max(high, value);
                        }
                        if (high - low > widestSpread)
                        {
                            widestSpread = high - low;
                            widest = i;
                            widestChannel = channel;
                        }
                    }
                }
                if (widest < 0 || widestSpread == 0)
                {
                    break;
                }
                (int boxStart, int boxLength) = boxes[widest];
                Array.Sort(indices, boxStart, boxLength,
                    Comparer<int>.Create((a, b) => rgb[a * 3 + widestChannel].CompareTo(rgb[b * 3 + widestChannel])));
                int half = boxLength / 2;
                boxes[widest] = (boxStart, half);
                boxes.Add((boxStart + half, boxLength - half));
            }
            var palette = new ushort[Math.Max(1, boxes.Count)];
            var lookup = new byte[count];
            for (int i = 0; i < boxes.Count; i++)
            {
                (int start, int length) = boxes[i];
                int r = 0;
                int g = 0;
                int b = 0;
                for (int j = start; j < start + length; j++)
                {
                    r += rgb[indices[j] * 3];
                    g += rgb[indices[j] * 3 + 1];
                    b += rgb[indices[j] * 3 + 2];
                }
                int divisor = Math.Max(1, length);
                r /= divisor;
                g /= divisor;
                b /= divisor;
                // BGR555, red in the low bits, which is what the palette format is
                palette[i] = (ushort)(((b >> 3) << 10) | ((g >> 3) << 5) | (r >> 3));
                for (int j = start; j < start + length; j++)
                {
                    lookup[indices[j]] = (byte)i;
                }
            }
            return (palette, lookup);
        }
    }
}
