using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MphRead.Mods.MapGen;

public sealed record MapDependency(string Kind, string Name, string ContentHash);

/// <summary>Content identities used by build/cache validation, independent of source timestamps.</summary>
public static class MapDependencyAnalyzer
{
    /// <summary>Portable, declared map assets shared by fingerprints, packaging and Save As.
    /// Cartridge references are intentionally excluded.</summary>
    public static IReadOnlyList<string> PackageAssets(MapDefinition definition) => Array.AsReadOnly(
        definition.Assets.Select(a => a.Path)
            .Concat(definition.Materials.Where(m => m.Texture != null).Select(m => m.Texture!))
            .Concat(definition.Audio?.Music is { } music ? new[] { music } : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<MapDependency> Analyze(MapDefinition definition)
    {
        var dependencies = new List<MapDependency>();
        void FileDependency(string kind, string name, string? path)
            => dependencies.Add(new(kind, name, MapBuildFingerprint.HashFile(path)));
        if (definition.BundlePath != null)
            FileDependency("bundle", "package", definition.BundlePath);
        else
        {
            if (definition.Import is { } import)
            {
                FileDependency("source", import.Source, import.Resolve());
                if (!string.IsNullOrEmpty(import.Textures)) FileDependency("textures", import.Textures, import.ResolveTextures());
            }
            if (definition.Collision is { } collision) FileDependency("collision", collision.Source, collision.Resolve());
            foreach (var asset in PackageAssets(definition))
            {
                string hash;
                try { hash = Convert.ToHexString(SHA256.HashData(MapAssets.Read(definition, asset))).ToLowerInvariant(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { hash = "missing"; }
                dependencies.Add(new("asset", asset, hash));
            }
        }
        bool usesBase = definition.Import?.Textures == null && (definition.Import != null
            || definition.Materials.Any(m => m.Texture == null));
        if (usesBase)
        {
            var (room, _) = Metadata.GetRoomByName(definition.TextureSource);
            if (room == null) dependencies.Add(new("base", definition.TextureSource, "missing"));
            else
            {
                string? root = null;
                try { root = room.FirstHunt ? Paths.FhFileSystem : Paths.FileSystem; }
                catch (KeyNotFoundException) { }
                foreach (var path in new[] { room.ModelPath, room.TexturePath })
                    if (!string.IsNullOrEmpty(path)) FileDependency("base", path,
                        root == null ? null : Paths.Combine(root, path));
            }
        }
        return Array.AsReadOnly(dependencies.OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal).ToArray());
    }
}
