using System;
using System.Collections.Generic;
using System.Numerics;

namespace MphRead.Mods.MapEditor;

/// <summary>A stable CPU mesh. Replaced only when this object's geometry changes.</summary>
public sealed record MapViewportMesh(Guid ObjectId, IReadOnlyList<MapViewportFace> Faces,
    IReadOnlyList<MapViewportFace>? CollisionFaces = null);

/// <summary>Editor presentation data, independent of the UI toolkit and graphics API.</summary>
public sealed record MapRenderFrame(MapViewportLayout Layout, MapViewportCamera Camera,
    IReadOnlyList<MapViewportMesh> Meshes, IReadOnlySet<Guid> Selection,
    IReadOnlyDictionary<Guid, Matrix4x4> PreviewTransforms, bool Wireframe, bool Collision);

public readonly record struct MapViewportCamera(Vector3 Position, Vector3 Target, bool Perspective)
{
    public (Vector3 Right, Vector3 Up, Vector3 Forward) Basis()
    {
        var direction = Target - Position;
        var forward = float.IsFinite(direction.LengthSquared()) && direction.LengthSquared() > 1e-10f
            ? Vector3.Normalize(direction) : -Vector3.UnitZ;
        var up = Math.Abs(forward.Y) > .99f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        return (right, Vector3.Cross(right, forward), forward);
    }
    public double FocalScale(MapViewportLayout layout) => Perspective
        ? Math.Min(layout.Width, layout.Height) * .9
        : Math.Min(layout.Width, layout.Height) / Math.Max(2, Vector3.Distance(Position, Target)) * 1.5;
    public (double X, double Y, double Depth)? Project(MapViewportLayout layout, Vector3 point)
    {
        if (!layout.IsValid) return null;
        var (right, up, forward) = Basis();
        var offset = point - Position;
        float depth = Vector3.Dot(offset, forward);
        if (depth < .05f) return null;
        double scale = FocalScale(layout) / (Perspective ? depth : 1);
        return (layout.Width / 2 + Vector3.Dot(offset, right) * scale,
            layout.Height / 2 - Vector3.Dot(offset, up) * scale, depth);
    }
    public (Vector3 Origin, Vector3 Direction) Ray(MapViewportLayout layout, double x, double y)
    {
        var (right, up, forward) = Basis();
        var offset = right * (float)((x - layout.Width / 2) / FocalScale(layout))
            - up * (float)((y - layout.Height / 2) / FocalScale(layout));
        return Perspective ? (Position, Vector3.Normalize(forward + offset)) : (Position + offset, forward);
    }
    public Matrix4x4 Projection(MapViewportLayout layout)
    {
        float vertical = (float)(layout.Height / FocalScale(layout));
        var projection = Perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(vertical / 2), (float)layout.AspectRatio, .05f, 10000)
            : Matrix4x4.CreateOrthographic(vertical * (float)layout.AspectRatio, vertical, .05f, 10000);
        // The game's GL renderer uses clip depth -1..1; Numerics uses 0..1.
        projection.M33 = Perspective ? -(10000 + .05f) / (10000 - .05f) : -2 / (10000 - .05f);
        projection.M43 = Perspective ? -2 * 10000 * .05f / (10000 - .05f) : -(10000 + .05f) / (10000 - .05f);
        return projection;
    }
}

public static class MapViewportPicking
{
    /// <summary>Pick actual triangles in world distance, including near-plane intersections.</summary>
    public static Guid Pick(MapRenderFrame frame, double x, double y)
    {
        if (!frame.Layout.IsValid) return Guid.Empty;
        var (origin, direction) = frame.Camera.Ray(frame.Layout, x, y);
        float nearest = float.PositiveInfinity;
        Guid picked = Guid.Empty;
        foreach (var mesh in frame.Meshes)
        {
            if (mesh.ObjectId == Guid.Empty) continue;
            var transform = frame.PreviewTransforms.GetValueOrDefault(mesh.ObjectId, Matrix4x4.Identity);
            foreach (var face in mesh.Faces)
            {
                if (frame.Collision && !face.Solid || face.Points.Length < 3) continue;
                var a = Vector3.Transform(face.Points[0], transform);
                for (int i = 1; i < face.Points.Length - 1; i++)
                {
                    var b = Vector3.Transform(face.Points[i], transform);
                    var c = Vector3.Transform(face.Points[i + 1], transform);
                    var edge = b - a; var other = c - a; var cross = Vector3.Cross(direction, other);
                    float determinant = Vector3.Dot(edge, cross);
                    if (MathF.Abs(determinant) < 1e-7f) continue;
                    var offset = origin - a;
                    float u = Vector3.Dot(offset, cross) / determinant;
                    if (u < 0 || u > 1) continue;
                    var q = Vector3.Cross(offset, edge);
                    float v = Vector3.Dot(direction, q) / determinant;
                    if (v < 0 || u + v > 1) continue;
                    float distance = Vector3.Dot(other, q) / determinant;
                    if (distance >= .05f && distance < nearest) { nearest = distance; picked = mesh.ObjectId; }
                }
            }
        }
        return picked;
    }
}
