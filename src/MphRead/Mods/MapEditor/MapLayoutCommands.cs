using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public static class MapLayoutCommands
{
    private static MapObject[] Selected(MapDefinition definition, ISet<Guid> ids)
        => MapObjects.All(definition).Where(o => ids.Contains(o.Id) && o.Value is not MapGeometry { Locked: true }).ToArray();
    private static (float Min, float Max) Bounds(MapObject item, int axis)
    {
        if (item.Value is MapBrush box) return (box.Min[axis], box.Max[axis]);
        if (item.Value is MapGeometry geometry)
        {
            var points = GeometryCompiler.Compile(geometry, 1).SelectMany(f => f.Points).ToArray();
            if (points.Length > 0) return (points.Min(p => p[axis]), points.Max(p => p[axis]));
        }
        return (item.Position[axis], item.Position[axis]);
    }
    public static void Align(MapDefinition definition, ISet<Guid> ids, int axis, int edge)
    {
        if (axis is < 0 or > 2 || edge is < -1 or > 1) throw new ArgumentOutOfRangeException();
        var items = Selected(definition, ids);
        if (items.Length < 2) return;
        var bounds = items.Select(o => Bounds(o, axis)).ToArray();
        float target = edge < 0 ? bounds.Min(b => b.Min) : edge > 0 ? bounds.Max(b => b.Max)
            : (bounds.Min(b => b.Min) + bounds.Max(b => b.Max)) / 2;
        for (int i = 0; i < items.Length; i++)
        {
            var delta = new float[3];
            delta[axis] = target - (edge < 0 ? bounds[i].Min : edge > 0 ? bounds[i].Max : (bounds[i].Min + bounds[i].Max) / 2);
            items[i].Move(delta);
        }
    }
    public static void Distribute(MapDefinition definition, ISet<Guid> ids, int axis)
    {
        if (axis is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(axis));
        var items = Selected(definition, ids).OrderBy(o => o.Position[axis]).ToArray();
        if (items.Length < 3) return;
        float first = items[0].Position[axis], step = (items[^1].Position[axis] - first) / (items.Length - 1);
        for (int i = 1; i < items.Length - 1; i++)
        { var delta = new float[3]; delta[axis] = first + step * i - items[i].Position[axis]; items[i].Move(delta); }
    }
    public static void Snap(MapDefinition definition, ISet<Guid> ids, float grid)
    {
        if (!float.IsFinite(grid) || grid <= 0) throw new ArgumentOutOfRangeException(nameof(grid));
        foreach (var item in Selected(definition, ids))
            item.Move(item.Position.Select(p => MathF.Round(p / grid) * grid - p).ToArray());
    }
    public static void SnapToFloor(MapDefinition definition, ISet<Guid> ids, IReadOnlyList<MapViewportFace> faces)
        => SnapToFloor(definition, ids, _ => faces);

    public static void SnapToFloor(MapDefinition definition, ISet<Guid> ids,
        Func<Vector3,IReadOnlyList<MapViewportFace>> candidates)
    {
        foreach (var item in Selected(definition, ids))
        {
            float bottom = Bounds(item, 1).Min;
            var point = new Vector3(item.Position[0], bottom + .05f, item.Position[2]);
            float? floor = FloorBelow(point, candidates(point), ids);
            if (floor.HasValue) item.Move(new[] { 0f, floor.Value - bottom, 0f });
        }
    }
    public static float? FloorBelow(Vector3 point, IReadOnlyList<MapViewportFace> faces, ISet<Guid>? excluded = null)
    {
        float? height = null;
        foreach (var face in faces)
        {
            if (!face.Solid || excluded?.Contains(face.ObjectId) == true || face.Points.Length < 3) continue;
            var a = face.Points[0];
            for (int i = 1; i < face.Points.Length - 1; i++)
            {
                var b = face.Points[i]; var c = face.Points[i + 1];
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < .00001f || Vector3.Normalize(normal).Y < .7f) continue;
                float y = a.Y - (normal.X * (point.X - a.X) + normal.Z * (point.Z - a.Z)) / normal.Y;
                if (y > point.Y || height.HasValue && y <= height.Value) continue;
                var p = new Vector3(point.X, y, point.Z);
                if (Vector3.Dot(Vector3.Cross(b-a, p-a), normal) >= -.0001f
                    && Vector3.Dot(Vector3.Cross(c-b, p-b), normal) >= -.0001f
                    && Vector3.Dot(Vector3.Cross(a-c, p-c), normal) >= -.0001f) height = y;
            }
        }
        return height;
    }
    public static void Array(MapDefinition definition, ISet<Guid> ids, int count, Vector3 spacing, bool radial = false)
    {
        if (count is < 1 or > 256 || !float.IsFinite(spacing.X) || !float.IsFinite(spacing.Y) || !float.IsFinite(spacing.Z))
            throw new ArgumentOutOfRangeException(nameof(count));
        var originals = Selected(definition, ids);
        var center = originals.Length == 0 ? Vector3.Zero : originals.Select(o => new Vector3(o.Position[0], o.Position[1], o.Position[2])).Aggregate(Vector3.Zero, (a,b) => a+b) / originals.Length;
        for (int i = 1; i <= count; i++)
        {
            var existing = MapObjects.All(definition).Select(o => o.Id).ToHashSet();
            MapObjects.Duplicate(definition, ids);
            foreach (var item in MapObjects.All(definition).Where(o => !existing.Contains(o.Id)))
            {
                var delta = spacing * i - new Vector3(1, 0, 1);
                if (radial)
                {
                    float angle = MathF.Tau * i / (count + 1);
                    var original = new Vector3(item.Position[0] - 1, item.Position[1], item.Position[2] - 1);
                    var desired = center + Vector3.Transform(original - center + spacing, Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle));
                    delta = desired - new Vector3(item.Position[0], item.Position[1], item.Position[2]);
                }
                item.Move(new[] { delta.X, delta.Y, delta.Z });
            }
        }
    }
}
