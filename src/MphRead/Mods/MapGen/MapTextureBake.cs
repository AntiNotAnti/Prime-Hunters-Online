using System;
using System.Threading;
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
        public static byte[] BakeImage(byte[] image, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            const int size=64;
            var(palette,pixels)=Quantize(Decode(image,size,cancellation),size,cancellation);
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8);
            writer.Write(new[]{'F','P','T','X'});writer.Write((ushort)1);writer.Write((ushort)1);
            writer.Write((ushort)0);writer.Write((ushort)size);writer.Write((ushort)size);writer.Write((ushort)palette.Length);
            writer.Write((ushort)3);writer.Write(Encoding.UTF8.GetBytes("map"));foreach(var color in palette)writer.Write(color);writer.Write(pixels);
            return stream.ToArray();
        }
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
            public int Resolved { get; init; }
            public int Fallbacks { get; init; }
            public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> Archives { get; init; } = Array.Empty<string>();
            public long Bytes { get; init; }
        }

        public sealed record Coverage(int Total, int Resolved, IReadOnlyList<string> Missing,
            IReadOnlyList<string> Archives);

        /// <summary>
        /// Texture archives for a Q3 source, in deterministic precedence order:
        /// the selected source first, explicit dependencies next, then sibling
        /// PK3s. This makes the GUI promise that "PK3s beside the map are used"
        /// true instead of merely advisory text.
        /// </summary>
        public static IReadOnlyList<string> DiscoverArchives(string source,
            IEnumerable<string>? dependencies = null)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? value)
            {
                if (String.IsNullOrWhiteSpace(value) || !File.Exists(value)) return;
                string full = Path.GetFullPath(value);
                if (seen.Add(full)) result.Add(full);
            }
            Add(source);
            if (dependencies != null)
                foreach (string dependency in dependencies) Add(dependency);
            string? directory = Path.GetDirectoryName(Path.GetFullPath(source));
            if (directory != null && Directory.Exists(directory))
                foreach (string sibling in Directory.EnumerateFiles(directory, "*.pk3")
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                    Add(sibling);
            return result;
        }

        public static Coverage Analyze(Q3Bsp bsp, IReadOnlyList<string> archivePaths, bool sky = true)
        {
            var archives = OpenArchives(archivePaths);
            try
            {
                var files = Index(archives);
                var aliases = ParseShaderAliases(files);
                var missing = new List<string>();
                int resolved = 0, total = 0;
                foreach ((_, string name) in UsedTextures(bsp, sky))
                {
                    total++;
                    if (FindEntry(files, aliases, name) != null) resolved++;
                    else missing.Add(name);
                }
                return new(total, resolved, missing.AsReadOnly(),
                    archivePaths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            finally { foreach (var archive in archives) archive.Dispose(); }
        }

        /// <summary>
        /// Writes a pack for every shader the level's drawn surfaces use.
        /// Images are looked for in the archives given, in order; a level's own
        /// .pk3 first, then whatever else the player has.
        /// </summary>
        public static Result Bake(Q3Bsp bsp, IReadOnlyList<string> archivePaths, string outputPath,
            int size = DefaultSize, bool sky = true, CancellationToken cancellation = default)
        {
            if (size is < 8 or > 256) throw new ArgumentOutOfRangeException(nameof(size), "Texture size must be 8-256.");
            var archives = OpenArchives(archivePaths);
            try
            {
                var files = Index(archives);
                var aliases = ParseShaderAliases(files);
                var entries = new List<(int Index, string Name, ushort[] Palette, byte[] Pixels)>();
                var missing = new List<string>();
                int resolved = 0;
                foreach ((int index, string name) in UsedTextures(bsp, sky))
                {
                    cancellation.ThrowIfCancellationRequested();
                    byte[]? raw = Find(files, aliases, name);
                    byte[] rgb;
                    if (raw == null)
                    {
                        missing.Add(name);
                        rgb = Fallback(size, name);
                    }
                    else
                    {
                        resolved++;
                        rgb = Decode(raw, size, cancellation);
                    }
                    (ushort[] palette, byte[] pixels) = Quantize(rgb, size, cancellation);
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
                    Resolved = resolved,
                    Fallbacks = missing.Count,
                    Missing = missing.AsReadOnly(),
                    Archives = archivePaths.Where(File.Exists).Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Bytes = new FileInfo(outputPath).Length
                };
            }
            finally { foreach (ZipArchive archive in archives) archive.Dispose(); }
        }

        private static List<ZipArchive> OpenArchives(IReadOnlyList<string> paths)
        {
            var archives = new List<ZipArchive>();
            try
            {
                foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(path) && !Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                        archives.Add(ZipFile.OpenRead(path));
                return archives;
            }
            catch
            {
                foreach (var archive in archives) archive.Dispose();
                throw;
            }
        }

        private static Dictionary<string, ZipArchiveEntry> Index(IEnumerable<ZipArchive> archives)
        {
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchive archive in archives)
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string key = entry.FullName.Replace('\\', '/');
                    if (!files.ContainsKey(key)) files.Add(key, entry);
                }
            return files;
        }

        /// <summary>
        /// Resolve common Quake 3 shader indirection. Full shader simulation is
        /// deliberately out of scope; for editor import we need the image the
        /// material should visibly resemble. The first concrete qer/map/
        /// clampmap/animMap/skyparms image wins.
        /// </summary>
        private static Dictionary<string, string> ParseShaderAliases(
            Dictionary<string, ZipArchiveEntry> files)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var script in files.Where(p => p.Key.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)))
            {
                string text;
                try
                {
                    if (script.Value.Length > 4 * 1024 * 1024) continue;
                    using var reader = new StreamReader(script.Value.Open(), Encoding.UTF8, true);
                    text = reader.ReadToEnd();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
                var tokens = TokenizeShader(text);
                for (int i = 0; i + 1 < tokens.Count;)
                {
                    string shader = tokens[i++];
                    if (shader is "{" or "}") continue;
                    if (i >= tokens.Count || tokens[i++] != "{") continue;
                    int depth = 1;
                    string? candidate = null;
                    while (i < tokens.Count && depth > 0)
                    {
                        string token = tokens[i++];
                        if (token == "{") { depth++; continue; }
                        if (token == "}") { depth--; continue; }
                        if (depth <= 0 || candidate != null) continue;
                        string key = token.ToLowerInvariant();
                        if (key is "qer_editorimage" or "map" or "clampmap")
                        {
                            if (i < tokens.Count)
                            {
                                string value = tokens[i++];
                                if (!value.StartsWith('        private static IEnumerable<(int, string)> UsedTextures(Q3Bsp bsp, bool sky)
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

        private static ZipArchiveEntry? FindEntry(Dictionary<string, ZipArchiveEntry> files,
            Dictionary<string, string> aliases, string name)
        {
            foreach (string candidate in aliases.TryGetValue(name, out string? alias)
                ? new[] { name, alias }
                : new[] { name })
            {
                string normalized = candidate.TrimStart('/').Replace('\\', '/');
                foreach (string suffix in _skySuffixes.Prepend(""))
                    foreach (string extension in _extensions)
                        if (files.TryGetValue(normalized + suffix + extension, out var entry)) return entry;
                if (files.TryGetValue(normalized, out var exact)) return exact;
            }
            return null;
        }

        private static byte[]? Find(Dictionary<string, ZipArchiveEntry> files,
            Dictionary<string, string> aliases, string name)
        {
            var entry = FindEntry(files, aliases, name);
            if (entry == null) return null;
            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            byte[] buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (memory.Length + read > MapPackageReader.MaxEntryBytes)
                    throw new InvalidDataException("Texture image exceeds the map asset limit.");
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }

        private static byte[] Fallback(int size, string name)
        {
            // Loud, deterministic checkerboard: missing art stays visible and
            // geometry never disappears. The hash stripe makes adjacent
            // missing materials distinguishable while authoring.
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
            var rgb = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                    bool stripe = ((x + y + (hash & 31)) % 17) < 3;
                    int o = (y * size + x) * 3;
                    rgb[o] = (byte)(stripe ? 255 : checker ? 230 : 30);
                    rgb[o + 1] = (byte)(stripe ? 220 : 20);
                    rgb[o + 2] = (byte)(checker ? 230 : 30);
                }
            return rgb;
        }

        /// <summary>Decode and box-filter down to the square the hardware wants.</summary>
        private static byte[] Decode(byte[] raw, int size, CancellationToken cancellation)
        {
            using var source = new MemoryStream(raw);
            using StbImage image = StbImage.Load(source, StbiImageFormat.Rgb);
            ReadOnlySpan<byte> pixels = image.AsSpan<byte>();
            int width = image.Width;
            int height = image.Height;
            var result = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
            {
                cancellation.ThrowIfCancellationRequested();
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
        private static (ushort[], byte[]) Quantize(byte[] rgb, int size, CancellationToken cancellation)
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
                cancellation.ThrowIfCancellationRequested();
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
) && value != "-") candidate = value;
                            }
                        }
                        else if (key == "animmap")
                        {
                            if (i < tokens.Count) i++; // frequency
                            if (i < tokens.Count)
                            {
                                string value = tokens[i++];
                                if (!value.StartsWith('        private static IEnumerable<(int, string)> UsedTextures(Q3Bsp bsp, bool sky)
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
        private static byte[] Decode(byte[] raw, int size, CancellationToken cancellation)
        {
            using var source = new MemoryStream(raw);
            using StbImage image = StbImage.Load(source, StbiImageFormat.Rgb);
            ReadOnlySpan<byte> pixels = image.AsSpan<byte>();
            int width = image.Width;
            int height = image.Height;
            var result = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
            {
                cancellation.ThrowIfCancellationRequested();
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
        private static (ushort[], byte[]) Quantize(byte[] rgb, int size, CancellationToken cancellation)
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
                cancellation.ThrowIfCancellationRequested();
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
) && value != "-") candidate = value;
                            }
                        }
                        else if (key == "skyparms" && i < tokens.Count)
                        {
                            string value = tokens[i++];
                            if (value != "-") candidate = value;
                        }
                    }
                    if (candidate != null && !aliases.ContainsKey(shader))
                        aliases.Add(shader, candidate.TrimStart('/'));
                }
            }
            return aliases;
        }

        private static List<string> TokenizeShader(string source)
        {
            var tokens = new List<string>();
            using var reader = new StringReader(source);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line[..comment];
                var word = new StringBuilder();
                void Flush()
                {
                    if (word.Length == 0) return;
                    tokens.Add(word.ToString());
                    word.Clear();
                }
                foreach (char ch in line)
                {
                    if (Char.IsWhiteSpace(ch)) { Flush(); continue; }
                    if (ch is '{' or '}') { Flush(); tokens.Add(ch.ToString()); continue; }
                    word.Append(ch);
                }
                Flush();
            }
            return tokens;
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
        private static byte[] Decode(byte[] raw, int size, CancellationToken cancellation)
        {
            using var source = new MemoryStream(raw);
            using StbImage image = StbImage.Load(source, StbiImageFormat.Rgb);
            ReadOnlySpan<byte> pixels = image.AsSpan<byte>();
            int width = image.Width;
            int height = image.Height;
            var result = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
            {
                cancellation.ThrowIfCancellationRequested();
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
        private static (ushort[], byte[]) Quantize(byte[] rgb, int size, CancellationToken cancellation)
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
                cancellation.ThrowIfCancellationRequested();
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
