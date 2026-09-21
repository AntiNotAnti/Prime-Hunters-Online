using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRead.Mods.MapGen
{
    public static class MapCommands
    {
        public static int Run(string command, string? argument, string? output)
        {
            try
            {
                if (argument == null) throw new MapAuthoringException("FP-MAP-020", $"-{command} requires a map path or runtime name.");
                string path = Path.GetFullPath(Path.Combine(ConsoleSetup.LaunchDirectory, argument));
                MapDefinition definition = File.Exists(path) ? MapDefinition.Load(path)
                    : new MapCatalog(CustomRooms.MapDirectory).Refresh(false).FirstOrDefault(e =>
                        e.Definition?.Name.Equals(argument, StringComparison.OrdinalIgnoreCase) == true)?.Definition
                        ?? throw new MapAuthoringException("FP-MAP-020", "Map was not found.");
                MapCompilation compilation = MapCompiler.Compile(definition);
                if (command == "mapinspect" && compilation.Map != null)
                {
                    var navigation = MapNodePacker.Pack(compilation.Map.Solid,compilation.Map.Definition.NavigationLinks);
                    MapBudgetValidator.Add(compilation.Validation, "Navigation nodes", navigation.Nodes, MapNodePacker.MaxNodes);
                    MapBudgetValidator.Add(compilation.Validation, "Navigation edges", navigation.Edges);
                }
                Console.WriteLine(JsonSerializer.Serialize(new { definition.Name, definition.MapId, definition.FormatVersion,
                    compilation.Validation.IsValid, compilation.Validation.Diagnostics, compilation.Validation.Budgets },
                    MapPackageReader.JsonOptions));
                if (!compilation.Validation.IsValid) return 1;
                if (command == "mapbuild")
                {
                    if (output != null)
                    {
                        output = Path.GetFullPath(Path.Combine(ConsoleSetup.LaunchDirectory, output));
                        MapPacker.Generate(compilation.Map!, Path.Combine(output, "archive"), Path.Combine(output, "entities"), Path.Combine(output, "nodes"));
                    }
                    else MapPacker.Generate(compilation.Map!, CustomRooms.ArchiveDirectory(definition), CustomRooms.EntityDirectory(), CustomRooms.NodeDirectory());
                }
                return 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ProgramException or JsonException or ArgumentException)
            {
                Console.WriteLine(JsonSerializer.Serialize(new MapDiagnostic(ex is MapAuthoringException author ? author.Code : "FP-MAP-020",
                    MapDiagnosticSeverity.Error, ex.Message), MapPackageReader.JsonOptions));
                return 1;
            }
        }
    }
}
