using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MphRead.Mods.MapGen
{
    public sealed class MapPackageManifest
    {
        public int Format { get; set; } = 2;
        public Guid MapId { get; set; }
        public string? MapVersion { get; set; }
        public string Name { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Author { get; set; }
        public string ContentHash { get; set; } = "";
        public string Project { get; set; } = "project.json";
        public string? Preview { get; set; }
    }

    public sealed class MapPackageReader : IDisposable
    {
        public const long MaxArchiveBytes = 128 * 1024 * 1024;
        public const long MaxExpandedBytes = 256 * 1024 * 1024;
        public const long MaxEntryBytes = 64 * 1024 * 1024;
        public const int MaxEntries = 2048;
        public static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        public MapPackageManifest? Manifest { get; }
        public string ProjectEntry { get; }

        public MapPackageReader(string path)
        {
            if (new FileInfo(path).Length > MaxArchiveBytes) throw new InvalidDataException("Package exceeds the 128 MiB limit.");
            _archive = ZipFile.OpenRead(path);
            try
            {
                if (_archive.Entries.Count > MaxEntries) throw new InvalidDataException("Package contains too many entries.");
                long total = 0;
                foreach (var entry in _archive.Entries)
                {
                    string name = CanonicalName(entry.FullName);
                    if (!_entries.TryAdd(name, entry)) throw new InvalidDataException("Duplicate package path: " + name);
                    if (entry.Length < 0 || entry.Length > MaxEntryBytes || (total += entry.Length) > MaxExpandedBytes)
                        throw new InvalidDataException("Package expanded size exceeds the limit.");
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is not (".json" or ".bsp" or ".obj" or ".tex" or ".png" or ".jpg" or ".jpeg" or ".ogg" or ".wav" or ".mp3"))
                        throw new InvalidDataException("Unsupported package asset: " + name);
                }
                if (_entries.ContainsKey("manifest.json"))
                {
                    Manifest = JsonSerializer.Deserialize<MapPackageManifest>(ReadRequired("manifest.json", 64 * 1024), JsonOptions)
                        ?? throw new InvalidDataException("Missing package manifest.");
                    if (Manifest.Format != 2 || Manifest.MapId == Guid.Empty || !MapValidator.ValidRuntimeName(Manifest.Name)
                        || Manifest.Project != "project.json" || Manifest.ContentHash?.Length != 64)
                        throw new InvalidDataException("Invalid package manifest or identity.");
                    ProjectEntry = Manifest.Project;
                    if (_entries.Keys.Count(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) != 2)
                        throw new InvalidDataException("Package must have exactly one manifest and one project.");
                    string hash = ContentHash(_entries.Keys.Where(n => n != "manifest.json"), n => ReadRequired(n));
                    if (!hash.Equals(Manifest.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package content hash does not match.");
                    using JsonDocument project = JsonDocument.Parse(ReadRequired(ProjectEntry, 8 * 1024 * 1024));
                    MapDefinition? definition = JsonSerializer.Deserialize<MapDefinition>(project.RootElement.GetRawText(), JsonOptions);
                    if (definition == null || definition.MapId != Manifest.MapId || definition.Name != Manifest.Name || definition.FormatVersion != 2)
                        throw new InvalidDataException("Manifest and project identities differ.");
                    if(definition.Version!=Manifest.MapVersion||definition.InGameName!=Manifest.DisplayName||definition.Author!=Manifest.Author)
                        throw new InvalidDataException("Manifest and project metadata differ.");
                    if (Manifest.Preview != null && !_entries.ContainsKey(CanonicalName(Manifest.Preview)))
                        throw new InvalidDataException("Packaged preview is missing.");
                    ValidateReferences(definition);
                }
                else
                {
                    var recipes = _entries.Keys.Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (recipes.Length != 1) throw new InvalidDataException("Legacy package requires exactly one recipe.");
                    ProjectEntry = recipes[0];
                    var options = new JsonSerializerOptions(JsonOptions) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                    var definition = JsonSerializer.Deserialize<MapDefinition>(ReadRequired(ProjectEntry, 8 * 1024 * 1024), options)
                        ?? throw new InvalidDataException("Invalid legacy recipe.");
                    MapValidator.RequireRuntimeName(definition.Name);
                    ValidateReferences(definition);
                }
            }
            catch { _archive.Dispose(); throw; }
        }

        private void ValidateReferences(MapDefinition definition)
        {
            if(definition.Assets==null)throw new InvalidDataException("Missing asset list.");
            foreach(var asset in definition.Assets)
                if(asset==null||!_entries.ContainsKey(CanonicalName(asset.Path)))throw new InvalidDataException("Packaged asset is missing.");
            if (definition.Collision is { } collision)
            {
                if (string.IsNullOrEmpty(collision.Source)
                    || !collision.Source.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
                    || !_entries.ContainsKey(CanonicalName(collision.Source)))
                    throw new InvalidDataException("Packaged collision mesh is missing or invalid.");
            }
            if (definition.Import is { } import)
            {
                if (!_entries.ContainsKey(CanonicalName(import.Source))) throw new InvalidDataException("Packaged BSP is missing.");
                if (!import.Source.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package import must reference a BSP.");
                if (!string.IsNullOrEmpty(import.Textures) && Find(import.Textures) == null) throw new InvalidDataException("Packaged texture pack is missing.");
            }
        }

        public static string CanonicalName(string name)
        {
            // Apply Windows rules on every platform so an archive safe on Linux
            // cannot become a traversal or device path when sent to a PC.
            if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Contains('\\') || name.StartsWith('/')
                || name.Any(c => c < 32 || ":<>\"|?*".Contains(c))) throw new InvalidDataException("Unsafe package path.");
            foreach (string part in name.Split('/'))
            {
                // Windows recognizes device names before the first dot, even
                // when the path has multiple extensions (CON.backup.tex).
                if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || !MapValidator.ValidRuntimeName(part.Split('.')[0])
                    || !MapValidator.ValidRuntimeName(Path.GetFileNameWithoutExtension(part).Replace('.', '_')))
                    throw new InvalidDataException("Unsafe package path: " + name);
            }
            return name;
        }

        private string? Find(string name)
        {
            name = CanonicalName(name);
            if (_entries.ContainsKey(name)) return name;
            var matches = _entries.Keys.Where(n => n.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Ambiguous package reference.");
            return matches.SingleOrDefault();
        }
        public byte[]? Read(string name) => Find(name) is { } found ? ReadRequired(found) : null;
        public string ReadProject() => Encoding.UTF8.GetString(ReadRequired(ProjectEntry, 8 * 1024 * 1024));
        private byte[] ReadRequired(string name, long limit = MaxEntryBytes)
        {
            if (!_entries.TryGetValue(name, out var entry) || entry.Length > limit) throw new InvalidDataException("Missing or oversized entry: " + name);
            using var input = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > limit || output.Length + count > entry.Length) throw new InvalidDataException("Entry exceeds declared size.");
                output.Write(buffer, 0, count);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated package entry.");
            return output.ToArray();
        }
        public static string ContentHash(IEnumerable<string> names, Func<string, byte[]> read)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal))
            {
                byte[] path = Encoding.UTF8.GetBytes(name), data = read(name);
                using var buffer = new MemoryStream();
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
                { writer.Write(path.Length); writer.Write(path); writer.Write((long)data.Length); }
                hash.AppendData(buffer.ToArray());
                hash.AppendData(data);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        public void Dispose() => _archive.Dispose();
    }
}
