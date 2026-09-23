using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public sealed partial class MapDocument
{
    /// <summary>Transform history stores only numeric transforms, even for large convex meshes.</summary>
    public void TransformSelection(IEnumerable<Guid> ids, string tool, Vector3 move,
        float angle, float scale, bool localAxes, object? transaction = null)
    {
        if (tool is not ("Move" or "Rotate" or "Scale")) throw new ArgumentException("Unknown transform tool.", nameof(tool));
        if (!float.IsFinite(move.X) || !float.IsFinite(move.Y) || !float.IsFinite(move.Z)
            || !float.IsFinite(angle) || !float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(move));
        var selected = ids.ToHashSet();
        var objects = selected.Select(id => MapObjects.Find(Project.Definition, id)).OfType<MapObject>()
            .Where(o => o.Value is not MapGeometry { Locked: true }).ToArray();
        var before = objects.Select(TransformValue.Capture).ToArray();
        var after = objects.Select(o =>
        {
            // Minimal temporary object: geometry vertices and unrelated properties are never copied.
            object value = o.Value switch
            {
                MapGeometry g => new MapBox { Transform = new() { Position = (float[])g.Transform.Position.Clone(),
                    Rotation = (float[])g.Transform.Rotation.Clone(), Scale = (float[])g.Transform.Scale.Clone() } },
                MapBrush b => new MapBrush { Min = (float[])b.Min.Clone(), Max = (float[])b.Max.Clone() },
                MapSpawn s => new MapSpawn { Position = (float[])s.Position.Clone(), Yaw = s.Yaw },
                MapItem i => new MapItem { Position = (float[])i.Position.Clone() },
                MapJumpPad p => new MapJumpPad { Position = (float[])p.Position.Clone() },
                MapNavigationLink n => new MapNavigationLink { From = (float[])n.From.Clone(), To = (float[])n.To.Clone() },
                _ => throw new InvalidOperationException("Unknown transform target")
            };
            var copy = new MapObject(o.Id, o.Kind, o.Label, value, _ => { });
            if (tool == "Move")
            {
                var delta = move;
                if (localAxes && value is MapGeometry g)
                { var r = g.Transform.Rotation; delta = Vector3.Transform(move, new Quaternion(r[0], r[1], r[2], r[3])); }
                copy.Move(new[] { delta.X, delta.Y, delta.Z });
            }
            else if (value is MapGeometry g)
            {
                if (tool == "Scale") for (int i = 0; i < 3; i++) g.Transform.Scale[i] *= scale;
                else
                {
                    var r = g.Transform.Rotation;
                    var current = new Quaternion(r[0], r[1], r[2], r[3]);
                    var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle * MathF.PI / 180);
                    var q = Quaternion.Normalize(localAxes ? current * turn : turn * current);
                    g.Transform.Rotation = new[] { q.X, q.Y, q.Z, q.W };
                }
            }
            else if (value is MapSpawn spawn && tool == "Rotate") spawn.Yaw += angle;
            return TransformValue.Capture(copy);
        }).ToArray();
        var changed = Enumerable.Range(0, before.Length).Where(i => !before[i].Values.SequenceEqual(after[i].Values)).ToArray();
        if (changed.Length == 0) return;
        History.Execute(new TransformCommand(this, tool,
            changed.Select(i => before[i]).ToArray(), changed.Select(i => after[i]).ToArray(),
            objects.Where(o => changed.Any(i => before[i].Id == o.Id)).Any(o => o.Value is MapGeometry or MapBrush)), transaction);
    }

    private sealed record TransformValue(Guid Id, float[] Values)
    {
        public static TransformValue Capture(MapObject o) => new(o.Id, o.Value switch
        {
            MapGeometry g => g.Transform.Position.Concat(g.Transform.Rotation).Concat(g.Transform.Scale).ToArray(),
            MapBrush b => b.Min.Concat(b.Max).ToArray(),
            MapSpawn s => s.Position.Append(s.Yaw).ToArray(),
            MapNavigationLink n => n.From.Concat(n.To).ToArray(),
            _ => (float[])o.Position.Clone()
        });
        public void Apply(MapObject o)
        {
            switch (o.Value)
            {
                case MapGeometry g: g.Transform.Position = Values[..3]; g.Transform.Rotation = Values[3..7]; g.Transform.Scale = Values[7..]; break;
                case MapBrush b: b.Min = Values[..3]; b.Max = Values[3..]; break;
                case MapSpawn s: s.Position = Values[..3]; s.Yaw = Values[3]; break;
                case MapItem i: i.Position = (float[])Values.Clone(); break;
                case MapJumpPad p: p.Position = (float[])Values.Clone(); break;
                case MapNavigationLink n: n.From = Values[..3]; n.To = Values[3..]; break;
            }
        }
    }
    private sealed class TransformCommand : IMapEditCommand
    {
        private readonly MapDocument _document;
        private readonly TransformValue[] _before;
        private TransformValue[] _after;
        public string Label { get; }
        public long ApproximateBytes => 128 + _before.Sum(v => 96L + 8L * v.Values.Length);
        public MapDocumentChange Change { get; }
        public TransformCommand(MapDocument document, string tool, TransformValue[] before, TransformValue[] after, bool geometry)
        {
            _document = document; Label = tool + " selection"; _before = before; _after = after;
            Change = new(MapChangeDomain.Transform | MapChangeDomain.Selection | MapChangeDomain.Navigation
                | (geometry ? MapChangeDomain.Geometry : MapChangeDomain.Entity), Array.AsReadOnly(before.Select(v => v.Id).ToArray()));
        }
        public void Execute() => Apply(_after);
        public void Undo() => Apply(_before);
        private void Apply(TransformValue[] values)
        {
            foreach (var value in values)
                value.Apply(MapObjects.Find(_document.Project.Definition, value.Id)
                    ?? throw new InvalidOperationException("Transform target no longer exists."));
        }
        public bool TryMerge(IMapEditCommand next)
        {
            if (next is not TransformCommand other || other._document != _document || Label != other.Label
                || !_before.Select(x => x.Id).SequenceEqual(other._before.Select(x => x.Id))) return false;
            _after = other._after; return true;
        }
    }
}
