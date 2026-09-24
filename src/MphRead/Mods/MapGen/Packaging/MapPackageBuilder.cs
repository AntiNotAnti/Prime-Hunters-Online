using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MphRead.Mods.MapGen
{
    public static class MapPackageBuilder
    {
        public static Guid LegacyId(string name)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("Fruity Prime legacy map:"+name.ToUpperInvariant())).AsSpan(0,16));
        public static string Build(MapDefinition source, string outputPath)
            => MapBuildScheduler.Shared.PackageAsync(MapBuildSnapshot.Capture(source), outputPath).GetAwaiter().GetResult();

        // Called only by a scheduler worker after compilation/validation. Keeping
        // this separate avoids recursively scheduling from inside a bounded worker.
        internal static string WriteValidated(MapDefinition source, string outputPath, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            MapDefinition definition = MapProjectSerializer.Clone(source);
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
            foreach(string asset in MapDependencyAnalyzer.PackageAssets(source))
            {
                cancellation.ThrowIfCancellationRequested();
                if(!entries.TryAdd(asset,MapAssets.Read(source,asset)))throw new InvalidDataException("Asset conflicts with a generated package entry.");
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
                        cancellation.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(pair.Key, CompressionLevel.SmallestSize);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        entry.ExternalAttributes = 0;
                        using var target = entry.Open();
                        for (int offset = 0; offset < pair.Value.Length; offset += 65536)
                        { cancellation.ThrowIfCancellationRequested(); target.Write(pair.Value.AsSpan(offset, Math.Min(65536, pair.Value.Length - offset))); }
                    }
                using (var check = new MapPackageReader(temporary)) { _ = check.ReadProject(); }
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, full, true);
                return full;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
