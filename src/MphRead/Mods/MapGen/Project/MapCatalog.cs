using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRead.Mods.MapGen
{
    public sealed record MapCatalogEntry(string Path, MapDefinition? Definition, MapValidationResult Validation);

    public sealed class MapCatalog
    {
        public string DirectoryPath { get; }
        public IReadOnlyList<MapCatalogEntry> Entries { get; private set; } = Array.Empty<MapCatalogEntry>();
        public MapCatalog(string directory) { DirectoryPath = directory; }

        public IReadOnlyList<MapCatalogEntry> Refresh(bool preferPackages = true)
        {
            var entries = new List<MapCatalogEntry>();
            if (!Directory.Exists(DirectoryPath)) return Entries = entries;
            var names = new Dictionary<string, MapCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<Guid, MapCatalogEntry>();
            // Do not follow symlinks or consume autosave/build/asset JSON as maps.
            var options = new EnumerationOptions { RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            string installed=Path.Combine(DirectoryPath,".installed");
            var files = Directory.EnumerateFiles(DirectoryPath, "*", options)
                .Where(p => MapBundle.Is(p) || Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .Where(p => !Path.GetRelativePath(DirectoryPath, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.StartsWith('.') || part.Equals("preview", StringComparison.OrdinalIgnoreCase)
                        || part.Equals("textures", StringComparison.OrdinalIgnoreCase) || part.Equals("audio", StringComparison.OrdinalIgnoreCase)))
                .Where(p => !Path.GetFileName(p).Equals("map.build.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.Exists(installed)?Directory.EnumerateFiles(installed,"*"+MapBundle.Extension,SearchOption.TopDirectoryOnly):Array.Empty<string>())
                .OrderBy(p => preferPackages&&Path.GetDirectoryName(p)==installed ? -1 : MapBundle.Is(p) == preferPackages ? 0 : 1)
                .ThenBy(p => p, StringComparer.Ordinal).ToList();
            foreach (string path in files)
            {
                MapDefinition? definition = null;
                MapValidationResult validation;
                try { definition = MapDefinition.Load(path); validation = MapValidator.Validate(definition); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ProgramException or ArgumentException)
                { validation = new(); validation.Error("FP-MAP-020", ex.Message); }
                var entry = new MapCatalogEntry(path, definition, validation);
                if (definition != null)
                {
                    if (names.TryGetValue(definition.Name, out var prior))
                    {
                        // A package and its source are two views of one identity.
                        Guid priorId=prior.Definition!.MapId==Guid.Empty?MapPackageBuilder.LegacyId(prior.Definition.Name):prior.Definition.MapId;
                        Guid currentId=definition.MapId==Guid.Empty?MapPackageBuilder.LegacyId(definition.Name):definition.MapId;
                        bool sameId = priorId==currentId;
                        if (sameId && (MapBundle.Is(prior.Path) != MapBundle.Is(path)||Path.GetDirectoryName(prior.Path)==installed||Path.GetDirectoryName(path)==installed)) continue;
                        validation.Error("FP-MAP-010", $"Duplicate runtime name: {definition.Name}.");
                        prior.Validation.Error("FP-MAP-010", $"Duplicate runtime name: {definition.Name}.");
                    }
                    else names.Add(definition.Name, entry);
                    if (definition.MapId != Guid.Empty)
                    {
                        if (ids.TryGetValue(definition.MapId, out prior))
                        {
                            validation.Error("FP-MAP-010", "Duplicate map ID.");
                            prior.Validation.Error("FP-MAP-010", "Duplicate map ID.");
                        }
                        else ids.Add(definition.MapId, entry);
                    }
                }
                entries.Add(entry);
            }
            return Entries = entries.AsReadOnly();
        }
    }
}
