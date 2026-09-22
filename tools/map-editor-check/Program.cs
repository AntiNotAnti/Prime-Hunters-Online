using System;
using System.IO;
using System.Linq;
using System.Numerics;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var definition = new MapDefinition { Name = "CHECK_ARENA", FormatVersion = 2, MapId = Guid.NewGuid() };
var box = new MapBox(); var spawn = new MapSpawn { Id = Guid.NewGuid() };
definition.Geometry.Add(box); definition.Spawns.Add(spawn); definition.Materials.Add(new());
var doc = new MapDocument(new MapProject(definition));
Check(doc.IsDirty, "new document dirty");
string root = Path.Combine(Path.GetTempPath(), "prime-history-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    doc.Save(Path.Combine(root, "map.json"));
    var saved = doc.CurrentStateId;
    Check(!doc.IsDirty, "save clean");
    var stable = doc.Project.Definition;
    doc.TransformSelection(new[] { box.Id }, "Move", new(1, 2, 3), 0, 1, false);
    Check(ReferenceEquals(stable, doc.Project.Definition), "transform preserves document graph");
    Check(doc.Project.Definition.Geometry[0].Transform.Position.SequenceEqual(new float[] { 1, 2, 3 }), "transform applied");
    Check(doc.IsDirty && doc.History.ApproximateBytes < 1024, "compact transform history");
    doc.History.Undo(); Check(doc.CurrentStateId == saved && !doc.IsDirty, "undo to saved identity");
    doc.History.Redo(); Check(doc.IsDirty, "redo dirty");
    var branch = doc.CurrentStateId;
    doc.History.Undo(); doc.TransformSelection(new[] { box.Id }, "Move", new(9, 0, 0), 0, 1, false);
    Check(doc.CurrentStateId != branch && !doc.History.CanRedo, "branch gets unique identity");
    int count = doc.History.CommandCount;
    object transaction = new();
    for (int i = 0; i < 100; i++) doc.TransformSelection(new[] { box.Id }, "Move", new(1, 0, 0), 0, 1, false, transaction);
    Check(doc.History.CommandCount == count + 1 && doc.History.ApproximateBytes < 2048, "100 updates coalesce");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry[0].Transform.Position[0] == 9, "coalesced undo");
    doc.History.Redo(); Check(doc.Project.Definition.Geometry[0].Transform.Position[0] == 109, "coalesced redo");
    doc.Save(Path.Combine(root, "map.json"));
    doc.TransformSelection(new[] { box.Id }, "Move", new(1, 0, 0), 0, 1, false, transaction);
    doc.History.Undo(); Check(!doc.IsDirty, "coalescing cannot cross save point");
    var newId = Guid.NewGuid();
    doc.EditObjects("Create", Array.Empty<Guid>(), d => d.Geometry.Add(new MapWedge { Id = newId }));
    Check(doc.Project.Definition.Geometry.Count == 2, "create");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry.Count == 1, "undo create");
    doc.History.Redo(); Check(doc.Project.Definition.Geometry[1] is MapWedge, "redo polymorphic create");
    doc.EditObjects("Delete", new[] { box.Id }, d => MapObjects.Delete(d, new System.Collections.Generic.HashSet<Guid> { box.Id }));
    Check(doc.Project.Definition.Geometry.Count == 1, "delete");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry[0].Id == box.Id, "undo preserves order");
    doc.EditObjects("Duplicate", new[] { box.Id }, d => MapObjects.Duplicate(d, new System.Collections.Generic.HashSet<Guid> { box.Id }));
    Check(doc.Project.Definition.Geometry.Count == 3 && doc.Project.Definition.Geometry[2].Id != box.Id, "duplicate identity");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry.Count == 2, "undo duplicate");
    doc.EditObjects("Entity", new[] { spawn.Id }, d => d.Spawns[0].Yaw = 90);
    Check(doc.Project.Definition.Spawns[0].Yaw == 90, "entity properties");
    doc.History.Undo(); Check(doc.Project.Definition.Spawns[0].Yaw == 0, "undo entity properties");
    doc.EditMaterial(0, m => m.TexScale = 42);
    doc.History.Undo(); Check(doc.Project.Definition.Materials[0].TexScale != 42, "undo material");
    doc.History.Redo(); Check(doc.Project.Definition.Materials[0].TexScale == 42, "redo material");
    var state = doc.CurrentStateId;
    try { doc.EditObjects("Fail", new[] { box.Id }, d => { d.Geometry[0].Label = "bad"; throw new InvalidOperationException(); }); }
    catch (InvalidOperationException) { }
    Check(doc.CurrentStateId == state && doc.Project.Definition.Geometry[0].Label != "bad", "failed edit atomic");
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.Zero, 0, 1, false);
    Check(doc.CurrentStateId == state, "no-op has no state");
    doc.EditObjects("Lock", new[] { box.Id }, d => d.Geometry[0].Locked = true);
    state = doc.CurrentStateId;
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.One, 0, 1, false);
    Check(doc.CurrentStateId == state, "locked transform no-op");
    doc.History.Undo(); Check(!doc.Project.Definition.Geometry[0].Locked, "undo lock");
    var history = new MapCommandHistory(2, 1000);
    int value = 0;
    for (int i = 0; i < 10; i++) history.Execute(new Counter(() => value++, () => value--, 100));
    Check(history.CommandCount == 2 && history.ApproximateBytes == 200, "count bound");
    history.Undo(); history.Undo(); Check(value == 8 && !history.CanUndo, "pruned undo floor");
    history.Execute(new Counter(() => value++, () => value--, 1001));
    Check(history.CommandCount == 0 && history.ApproximateBytes == 0, "oversized history bound and branch pruning");
    var cache = new MapViewportCache();
    cache.Invalidate(doc.Project.Definition, new(MapChangeDomain.All));
    doc.Invalidated += change => cache.Invalidate(doc.Project.Definition, change);
    int geometryCount = cache.GeometryRebuildCount, entityCount = cache.EntityRebuildCount;
    int selectionCount = cache.SelectionRebuildCount;
    doc.Selection.Add(box.Id); doc.SelectionChanged();
    Check(cache.GeometryRebuildCount == geometryCount && cache.EntityRebuildCount == entityCount
        && cache.SelectionRebuildCount == selectionCount + 1, "selection invalidates only selection");
    var native = cache.NativeFaces;
    doc.TransformSelection(new[] { spawn.Id }, "Move", Vector3.One, 0, 1, false);
    Check(cache.GeometryRebuildCount == geometryCount && ReferenceEquals(native, cache.NativeFaces), "entity move preserves mesh cache");
    Check(cache.EntityRebuildCount == entityCount + 1, "entity move invalidates entity cache");
    int rebuilt = cache.GeometryObjectsRebuilt;
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.One, 0, 1, false);
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "single object geometry rebuild");
    doc.OverlayChanged();
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "overlay leaves geometry cached");
    doc.Save(Path.Combine(root, "map.json"));
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "save leaves geometry cached");
    var layout = new MapViewportLayout(800, 600, 1.5);
    Check(layout.PixelWidth == 1200 && layout.PixelHeight == 900 && layout.Normalize(400, 300) == (0d, 0d), "DPI layout contract");
    Check(new MapViewportLayout(0, 0).PixelWidth == 0, "empty viewport safe");
    Console.WriteLine($"Map editor: {checks} checks passed.");
}
finally { Directory.Delete(root, true); }
sealed class Counter(Action execute, Action undo, long bytes) : IMapEditCommand
{
    public string Label => "Counter";
    public long ApproximateBytes => bytes;
    public MapDocumentChange Change { get; } = new(MapChangeDomain.Metadata);
    public void Execute() => execute();
    public void Undo() => undo();
}
