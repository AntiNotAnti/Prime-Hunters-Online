using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MphRead.Mods.MapGen
{
    public sealed record MapBuildFingerprint(int CompilerVersion, int ProjectFormat,
        string RecipeHash, string SourceHash, string TextureHash, string ConfigurationHash)
    {
        // Bump when compiler output or build-relevant defaults change.
        public const int CurrentCompilerVersion = 4;

        public static MapBuildFingerprint Create(MapDefinition definition)
        {
            // In-memory edits are the recipe. The file on disk may be older,
            // differently formatted or absent for an unsaved editor document.
            string recipe = HashText(definition.Serialize());
            var dependencies = MapDependencyAnalyzer.Analyze(definition);
            string Select(string kind) => HashText(JsonSerializer.Serialize(
                System.Linq.Enumerable.Where(dependencies, d => d.Kind == kind)));
            return new(CurrentCompilerVersion, definition.FormatVersion, recipe,
                Select("source"), Select("textures"), HashText(JsonSerializer.Serialize(dependencies)));
        }

        public string ContentKey => HashText(JsonSerializer.Serialize(new
        { CompilerVersion, ProjectFormat, RecipeHash, SourceHash, TextureHash, ConfigurationHash }));

        public static string HashText(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        public static string HashFile(string? path)
        {
            if (path == null || !File.Exists(path))
            {
                return "missing";
            }
            using Stream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    public sealed class MapBuildManifest
    {
        public MapBuildFingerprint? Fingerprint { get; set; }
        public string[] OutputHashes { get; set; } = Array.Empty<string>();

        public static bool IsCurrent(MapDefinition definition, MapOutputSet outputs)
        {
            if (!outputs.Complete || !File.Exists(outputs.Manifest)) return false;
            try
            {
                var manifest = JsonSerializer.Deserialize<MapBuildManifest>(File.ReadAllText(outputs.Manifest));
                if (manifest?.Fingerprint != MapBuildFingerprint.Create(definition)
                    || manifest.OutputHashes?.Length != 5) return false;
                int index = 0;
                foreach (string file in outputs.Files)
                {
                    if (manifest.OutputHashes[index++] != MapBuildFingerprint.HashFile(file)) return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static void Write(MapDefinition definition, MapOutputSet outputs, MapBuildFingerprint? fingerprint = null)
        {
            var manifest = new MapBuildManifest { Fingerprint = fingerprint ?? MapBuildFingerprint.Create(definition) };
            manifest.OutputHashes = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(outputs.Files, file => MapBuildFingerprint.HashFile(file)));
            AtomicFile.Write(outputs.Manifest, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
        }
    }

    internal static class AtomicFile
    {
        public static void Write(string path, byte[] bytes)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
