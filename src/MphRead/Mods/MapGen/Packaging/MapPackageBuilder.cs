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
    public static class MapPackageBuilder
    {
        public static Guid LegacyId(string name)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("Fruity Prime legacy map:"+name.ToUpperInvariant())).AsSpan(0,16));
        public static string Build(MapDefinition source, string outputPath)
        {
            MapDefinition definition = MapProjectSerializer.Clone(source);
            MapCompilation compilation = MapCompiler.Compile(definition);
            MapCompiler.ThrowIfInvalid(compilation.Validation);
            var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            if (definition.FormatVersion == 1)
            {
                definition.FormatVersion = 2;
                // Legacy builds stay reproducible without rewriting their source.
                // Explicit editor upgrades assign a fresh permanent identity.
                definition.MapId = LegacyId(definition.Name);
            }
            if (definition.Import is { } import)
            {
                string level = import.Resolve() ?? throw new MapAuthoringException("FP-MAP-005", "Imported level is missing.");
                entries.Add("import/level.bsp", Q3Bsp.Trim(Q3Bsp.ReadLevel(level, import.MapName)));
                byte[]? textures = import.ReadBundledTextures();
                string? textureFile = import.ResolveTextures();
                if (textures == null && textureFile != null) textures = File.ReadAllBytes(textureFile);
                if (!string.IsNullOrEmpty(import.Textures) && textures == null)
                    throw new MapAuthoringException("FP-MAP-006", "Texture pack could not be baked or loaded.");
                import.Source = "import/level.bsp";
                import.MapName = "level";
                import.Textures = textures == null ? null : "textures/map.tex";
                if (textures != null) entries.Add(import.Textures!, textures);
            }
            if (definition.Collision is { Source.Length: > 0 } collision)
            {
                byte[] bytes = collision.ReadBytes()
                    ?? throw new InvalidDataException("Collision mesh is missing.");
                collision.Source = "collision/mesh.obj";
                entries.Add(collision.Source, bytes);
            }
            foreach(var asset in definition.Assets)
            {
                if(!entries.TryAdd(asset.Path,MapAssets.Read(source,asset.Path)))throw new InvalidDataException("Asset conflicts with a generated package entry.");
            }
            entries.Add("project.json", Encoding.UTF8.GetBytes(definition.Serialize()));
            var manifest = new MapPackageManifest
            {
                MapId = definition.MapId, Name = definition.Name, DisplayName = definition.InGameName,
                MapVersion = definition.Version, Author = definition.Author,
                Preview = definition.Assets.FirstOrDefault(a=>a.Kind=="preview")?.Path,
                ContentHash = MapPackageReader.ContentHash(entries.Keys, name => entries[name])
            };
            entries.Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, MapPackageReader.JsonOptions));
            foreach (var entry in entries)
            {
                MapPackageReader.CanonicalName(entry.Key);
                if (entry.Value.LongLength > MapPackageReader.MaxEntryBytes) throw new InvalidDataException("Package asset is too large.");
            }
            if (entries.Sum(e => (long)e.Value.Length) > MapPackageReader.MaxExpandedBytes) throw new InvalidDataException("Package is too large.");
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                    foreach (var pair in entries)
                    {
                        var entry = archive.CreateEntry(pair.Key, CompressionLevel.SmallestSize);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        entry.ExternalAttributes = 0;
                        using var target = entry.Open();
                        target.Write(pair.Value);
                    }
                using (var check = new MapPackageReader(temporary)) { _ = check.ReadProject(); }
                File.Move(temporary, full, true);
                return full;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
