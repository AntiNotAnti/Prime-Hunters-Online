namespace MphRead.Mods.MapGen;

/// <summary>Detached authoring input. Capture on the UI thread before scheduling work.</summary>
public sealed class MapBuildSnapshot
{
    private readonly MapDefinition _definition;
    private MapBuildSnapshot(MapDefinition definition) => _definition = MapSnapshotCopy.Copy(definition);
    public static MapBuildSnapshot Capture(MapProject project) => new(project.Definition);
    public static MapBuildSnapshot Capture(MapDefinition definition) => new(definition);
    // Every worker receives its own graph, including arrays and polymorphic geometry.
    public MapDefinition CreateDefinition() => MapSnapshotCopy.Copy(_definition);
}
