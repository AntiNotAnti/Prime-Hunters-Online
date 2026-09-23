using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public sealed partial class MapDocument
{
    // Only selected objects are cloned/compared. Unrelated geometry, materials,
    // imported maps and assets are never serialized by common object operations.
    public void EditObjects(string label, IEnumerable<Guid> ids, Action<MapDefinition> edit, object? transaction = null)
    {
        var selected = ids.ToHashSet();
        var before = CaptureObjects(Project.Definition, selected);
        var scratch = new MapDefinition();
        foreach (var item in before) item.Insert(scratch);
        edit(scratch);
        var after = CaptureObjects(scratch, null);
        var changed = before.Select(x => x.Id).Union(after.Select(x => x.Id)).Where(id =>
            before.FirstOrDefault(x => x.Id == id)?.Json != after.FirstOrDefault(x => x.Id == id)?.Json).ToArray();
        if (changed.Length == 0) return;
        // Preserve positions within each original typed collection. New objects append.
        after = after.Select(item => item with
        { Index = before.FirstOrDefault(old => old.Id == item.Id)?.Index ?? int.MaxValue }).ToArray();
        History.Execute(new ObjectCommand(this, label,
            before.Where(x => changed.Contains(x.Id)).ToArray(),
            after.Where(x => changed.Contains(x.Id)).ToArray(), changed), transaction);
    }

    public void EditMaterial(int index, Action<MapMaterial> edit)
    {
        var before = JsonSerializer.Serialize(Project.Definition.Materials[index]);
        var value = JsonSerializer.Deserialize<MapMaterial>(before)!;
        edit(value);
        string after = JsonSerializer.Serialize(value);
        if (before != after) History.Execute(new MaterialCommand(this, index, before, after));
    }

    private sealed record ObjectValue(Guid Id, Type Type, int Index, string Json)
    {
        public void Insert(MapDefinition definition)
        {
            IList list = ObjectList(definition, Type);
            list.Insert(Math.Min(Index, list.Count), JsonSerializer.Deserialize(Json, Type)!);
        }
        public long Bytes => 160 + Json.Length * 4L;
    }
    private static IList ObjectList(MapDefinition d, Type type)
    {
        if (typeof(MapGeometry).IsAssignableFrom(type)) return d.Geometry;
        if (type == typeof(MapBrush)) return d.Brushes;
        if (type == typeof(MapSpawn)) return d.Spawns;
        if (type == typeof(MapItem)) return d.Items;
        if (type == typeof(MapJumpPad)) return d.JumpPads;
        if (type == typeof(MapNavigationLink)) return d.NavigationLinks;
        throw new ArgumentException("Unsupported map object " + type.Name);
    }
    private static ObjectValue[] CaptureObjects(MapDefinition d, HashSet<Guid>? ids)
        => (ids == null ? MapObjects.All(d) : ids.Select(id => MapObjects.Find(d, id)).OfType<MapObject>()).Select(o =>
            new ObjectValue(o.Id, o.Value.GetType(), ObjectList(d, o.Value.GetType()).IndexOf(o.Value),
                JsonSerializer.Serialize(o.Value, o.Value.GetType()))).ToArray();

    private sealed class ObjectCommand : IMapEditCommand
    {
        private readonly MapDocument _document;
        private readonly ObjectValue[] _before;
        private ObjectValue[] _after;
        private readonly Guid[] _ids;
        public string Label { get; }
        public MapDocumentChange Change { get; }
        public long ApproximateBytes => 128 + _before.Sum(o => o.Bytes) + _after.Sum(o => o.Bytes);
        public ObjectCommand(MapDocument document, string label, ObjectValue[] before, ObjectValue[] after, Guid[] ids)
        {
            _document = document; Label = label; _before = before; _after = after; _ids = ids;
            var domains = MapChangeDomain.Selection;
            foreach (var item in before.Concat(after))
                domains |= typeof(MapGeometry).IsAssignableFrom(item.Type) || item.Type == typeof(MapBrush)
                    ? MapChangeDomain.Geometry | MapChangeDomain.Navigation
                    : item.Type == typeof(MapNavigationLink) ? MapChangeDomain.Navigation : MapChangeDomain.Entity;
            Change = new(domains, Array.AsReadOnly(ids));
        }
        public void Execute() => Apply(_after);
        public void Undo() => Apply(_before);
        private void Apply(ObjectValue[] values)
        {
            var definition = _document.Project.Definition;
            // Remove by identity even for locked objects: undo must restore lock changes too.
            foreach (var item in MapObjects.All(definition).Where(o => _ids.Contains(o.Id)).ToArray())
                ObjectList(definition, item.Value.GetType()).Remove(item.Value);
            foreach (var item in values.OrderBy(o => o.Index)) item.Insert(definition);
        }
        public bool TryMerge(IMapEditCommand next)
        {
            if (next is not ObjectCommand other || other._document != _document
                || !_ids.SequenceEqual(other._ids) || Label != other.Label) return false;
            _after = other._after;
            return true;
        }
    }
    private sealed class MaterialCommand : IMapEditCommand
    {
        private readonly MapDocument _document;
        private readonly int _index;
        private readonly string _before, _after;
        public string Label => "Edit material";
        public long ApproximateBytes => 128 + 4L * (_before.Length + _after.Length);
        public MapDocumentChange Change { get; } = new(MapChangeDomain.Material);
        public MaterialCommand(MapDocument document, int index, string before, string after)
        { _document = document; _index = index; _before = before; _after = after; }
        public void Execute() => _document.Project.Definition.Materials[_index] = JsonSerializer.Deserialize<MapMaterial>(_after)!;
        public void Undo() => _document.Project.Definition.Materials[_index] = JsonSerializer.Deserialize<MapMaterial>(_before)!;
    }
}
