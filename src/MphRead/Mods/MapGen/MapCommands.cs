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
                var snapshot = MapBuildSnapshot.Capture(definition);
                if (command == "mapbuild")
                {
                    var result = MapBuildScheduler.Shared.BuildAsync(snapshot).GetAwaiter().GetResult();
                    Report(result.Validation());
                    if (!result.Succeeded) return 1;
                    if (output != null)
                    {
                        output = Path.GetFullPath(Path.Combine(ConsoleSetup.LaunchDirectory, output));
                        MapBuildScheduler.Install(result, definition, Path.Combine(output, "archive"),
                            Path.Combine(output, "entities"), Path.Combine(output, "nodes"));
                    }
                    else MapBuildScheduler.Install(result, definition, CustomRooms.ArchiveDirectory(definition),
                        CustomRooms.EntityDirectory(), CustomRooms.NodeDirectory());
                }
                else
                {
                    var result = MapBuildScheduler.Shared.AnalyzeAsync(snapshot, navigation: command == "mapinspect")
                        .GetAwaiter().GetResult();
                    Report(result.Validation());
                    if (!result.Succeeded) return 1;
                }
                void Report(MapValidationResult validation) => Console.WriteLine(JsonSerializer.Serialize(new
                {
                    definition.Name, definition.MapId, definition.FormatVersion,
                    validation.IsValid, validation.Diagnostics, validation.Budgets
                }, MapPackageReader.JsonOptions));
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
