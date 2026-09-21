using System;
using System.IO;
using System.Text.Json;

namespace MphRead.Mods.MapGen
{
    public sealed class MapProjectMetadata
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "CUSTOM";
        public string DisplayName { get; set; } = "Custom map";
        public string? Author { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
    }

    // The authoring aggregate owns the source. Runtime code receives a detached
    // definition; it never borrows the editor's mutable document.
    public sealed class MapProject
    {
        public MapDefinition Definition { get; }
        public MapProject(MapDefinition definition) { Definition = definition; }
        public MapProjectMetadata Metadata => new()
        {
            Id = Definition.MapId, Name = Definition.Name,
            DisplayName = Definition.InGameName ?? Definition.Name,
            Author = Definition.Author, Version = Definition.Version, Description = Definition.Description
        };
        public MapDefinition ToDefinition() => MapProjectSerializer.Clone(Definition);
    }

    public static class MapProjectSerializer
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
        public static MapProject Load(string path) => new(MapDefinition.Load(path));
        public static MapDefinition Clone(MapDefinition source)
        {
            var copy = JsonSerializer.Deserialize<MapDefinition>(source.Serialize(), Options)!;
            copy.BaseDirectory = source.BaseDirectory;
            copy.SourcePath = source.SourcePath;
            copy.BundlePath = source.BundlePath;
            if (copy.Import != null)
            {
                copy.Import.BaseDirectory = copy.BaseDirectory;
                copy.Import.BundlePath = copy.BundlePath;
            }
            if (copy.Collision != null)
            {
                copy.Collision.BaseDirectory = copy.BaseDirectory;
                copy.Collision.BundlePath = copy.BundlePath;
            }
            return copy;
        }
        public static void Save(MapProject project, string path)
        {
            if (MapBundle.Is(path)) throw new IOException("Save a project as JSON; use Build Package for .ppmap.");
            project.Definition.Save(path);
        }
    }

    public static class MapProjectMigrator
    {
        // Only explicit upgrade/save calls this; discovery never assigns identity.
        public static MapProject Upgrade(MapProject source)
        {
            MapDefinition copy = source.ToDefinition();
            copy.FormatVersion = 2;
            if (copy.MapId == Guid.Empty) copy.MapId = Guid.NewGuid();
            foreach (var x in copy.Materials) if (x.Id == Guid.Empty) x.Id = Guid.NewGuid();
            foreach (var x in copy.Brushes) if (x.Id == Guid.Empty) x.Id = Guid.NewGuid();
            foreach (var x in copy.Spawns) if (x.Id == Guid.Empty) x.Id = Guid.NewGuid();
            foreach (var x in copy.Items) if (x.Id == Guid.Empty) x.Id = Guid.NewGuid();
            foreach (var x in copy.JumpPads) if (x.Id == Guid.Empty) x.Id = Guid.NewGuid();
            return new(copy);
        }
    }
}
