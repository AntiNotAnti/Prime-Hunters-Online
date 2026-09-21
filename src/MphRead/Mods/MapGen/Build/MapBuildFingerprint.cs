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
        public const int CurrentCompilerVersion = 3;

        public static MapBuildFingerprint Create(MapDefinition definition)
        {
            string recipe = definition.SourcePath == null ? HashText(definition.Serialize())
                : HashFile(definition.SourcePath);
            string source = definition.BundlePath != null ? recipe
                : definition.Import == null ? "" : HashFile(definition.Import.Resolve());
            string textures = definition.BundlePath != null ? recipe
                : definition.Import?.Textures is not { Length: > 0 } ? ""
                : HashFile(definition.Import.ResolveTextures());
            return new(CurrentCompilerVersion, definition.FormatVersion, recipe, source, textures,
                HashText(definition.Serialize() + AssetHashes(definition)
                    + (definition.BundlePath == null && definition.Collision is { Source.Length: > 0 } collision
                        ? HashFile(collision.Resolve()) : "")));
        }

        private static string AssetHashes(MapDefinition definition)
        {
            if(definition.BundlePath!=null)return "";
            var builder=new StringBuilder();
            foreach(var asset in System.Linq.Enumerable.OrderBy(definition.Assets,a=>a.Path,StringComparer.Ordinal))
            {
                builder.Append(asset.Path).Append(':');
                try{builder.Append(Convert.ToHexString(SHA256.HashData(MapAssets.Read(definition,asset.Path))));}
                catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException){builder.Append("missing");}
            }
            return builder.ToString();
        }

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

        public static void Write(MapDefinition definition, MapOutputSet outputs)
        {
            var manifest = new MapBuildManifest { Fingerprint = MapBuildFingerprint.Create(definition) };
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
