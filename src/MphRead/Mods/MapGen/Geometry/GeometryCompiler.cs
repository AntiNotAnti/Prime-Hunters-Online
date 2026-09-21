using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    public static class GeometryCompiler
    {
        public static IReadOnlyList<BuiltFace> Compile(MapGeometry geometry, float texScale)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int[]>();
            if (geometry is MapBox)
            {
                vertices.AddRange(new[] { new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,-.5f,.5f), new Vector3(-.5f,-.5f,.5f),
                    new Vector3(-.5f,.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f) });
                indices.AddRange(new[] { new[] {0,1,2,3}, new[] {4,5,6,7}, new[] {0,1,5,4}, new[] {1,2,6,5}, new[] {2,3,7,6}, new[] {3,0,4,7} });
            }
            else if (geometry is MapWedge)
            {
                vertices.AddRange(new[] { new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,-.5f,.5f),
                    new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,.5f,.5f), new Vector3(.5f,.5f,.5f) });
                indices.AddRange(new[] { new[] {0,1,2,3}, new[] {0,1,5,4}, new[] {2,3,4,5}, new[] {0,3,4}, new[] {1,2,5} });
            }
            else if (geometry is MapPrism prism)
            {
                if (prism.Sides is < 3 or > 32) throw new MapAuthoringException("FP-MAP-013", "Prisms require 3–32 sides.");
                for (int y = 0; y < 2; y++)
                    for (int i = 0; i < prism.Sides; i++)
                    {
                        float angle = 2 * MathF.PI * i / prism.Sides;
                        vertices.Add(new Vector3(MathF.Cos(angle) * .5f, y - .5f, MathF.Sin(angle) * .5f));
                    }
                indices.Add(Enumerable.Range(0, prism.Sides).ToArray());
                indices.Add(Enumerable.Range(prism.Sides, prism.Sides).ToArray());
                for (int i = 0; i < prism.Sides; i++) indices.Add(new[] { i, (i+1)%prism.Sides, (i+1)%prism.Sides+prism.Sides, i+prism.Sides });
            }
            else if (geometry is MapConvexBrush convex)
            {
                if (convex.Vertices == null || convex.Faces == null || convex.Vertices.Count is < 4 or > 256 || convex.Faces.Count is < 4 or > 256
                    || convex.Vertices.Any(v => !MapValidator.Vector(v)) || convex.Faces.Any(f => f == null || f.Length is < 3 or > 32 || f.Any(i => i < 0 || i >= convex.Vertices.Count)))
                    throw new MapAuthoringException("FP-MAP-013", "Invalid convex brush vertices or faces.");
                vertices.AddRange(convex.Vertices.Select(MapBuilder.ToVector)); indices.AddRange(convex.Faces);
                var edgeCounts = new Dictionary<(int, int), int>();
                foreach (var face in indices)
                    for (int i = 0; i < face.Length; i++)
                    {
                        int a = face[i], b = face[(i+1)%face.Length]; var edge = (Math.Min(a,b), Math.Max(a,b));
                        edgeCounts[edge] = edgeCounts.GetValueOrDefault(edge) + 1;
                    }
                if (edgeCounts.Values.Any(c => c != 2)) throw new MapAuthoringException("FP-MAP-013", "Convex brush must be closed, with two faces per edge.");
            }
            else throw new MapAuthoringException("FP-MAP-013", "Unsupported geometry kind.");
            var t = geometry.Transform;
            if (t == null || !MapValidator.Vector(t.Position) || !MapValidator.Vector(t.Scale) || t.Scale.Any(x => x <= 0)
                || t.Rotation?.Length != 4 || t.Rotation.Any(x => !float.IsFinite(x))
                || Math.Abs(t.Rotation.Sum(x => x*x) - 1) > .001)
                throw new MapAuthoringException("FP-MAP-013", "Transform requires finite position, positive scale and normalized quaternion.");
            var rotation = new Quaternion(t.Rotation[0], t.Rotation[1], t.Rotation[2], t.Rotation[3]);
            vertices = vertices.Select(p => Vector3.Transform(p * MapBuilder.ToVector(t.Scale), rotation) + MapBuilder.ToVector(t.Position)).ToList();
            Vector3 center = vertices.Aggregate(Vector3.Zero, (sum,p) => sum+p) / vertices.Count;
            var result = new List<BuiltFace>();
            for (int faceIndex = 0; faceIndex < indices.Count; faceIndex++)
            {
                Vector3[] points = indices[faceIndex].Select(i => vertices[i]).ToArray();
                Vector3 normal = Vector3.Cross(points[1]-points[0], points[2]-points[0]);
                if (normal.LengthSquared < 1e-10f) throw new MapAuthoringException("FP-MAP-013", "Degenerate geometry face.");
                normal.Normalize();
                if (Vector3.Dot(normal, points[0]-center) < 0) { Array.Reverse(points); normal = -normal; }
                // Two faces per edge also describes a flattened tetrahedron.
                // A solid convex brush must have an interior behind every face.
                if (geometry is MapConvexBrush && !(Vector3.Dot(normal, center-points[0]) < 0))
                    throw new MapAuthoringException("FP-MAP-013", "Convex brush must enclose a nonzero volume.");
                if (geometry is MapConvexBrush && (vertices.Any(p => Vector3.Dot(normal, p-points[0]) > .001)
                    || points.Any(p => Math.Abs(Vector3.Dot(normal, p-points[0])) > .001)))
                    throw new MapAuthoringException("FP-MAP-013", "Brush faces must be planar and convex.");
                MapUv uv = geometry.FaceUv != null && geometry.FaceUv.TryGetValue(faceIndex, out var custom) ? custom : geometry.Uv;
                if (uv == null || uv.Scale?.Length != 2 || uv.Offset?.Length != 2 || uv.Scale.Any(x => !float.IsFinite(x) || x <= 0)
                    || uv.Offset.Any(x => !float.IsFinite(x)) || !float.IsFinite(uv.Rotation)) throw new MapAuthoringException("FP-MAP-001", "Invalid UV settings.");
                Vector3 u = (points[1]-points[0]).Normalized(), v = Vector3.Cross(normal,u);
                float angle = MathHelper.DegreesToRadians(uv.Rotation), cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                Vector2[] coords = points.Select(p =>
                {
                    float x = Vector3.Dot(p-points[0],u)*texScale*uv.Scale[0], y = Vector3.Dot(p-points[0],v)*texScale*uv.Scale[1];
                    return new Vector2(x*cos-y*sin+uv.Offset[0], x*sin+y*cos+uv.Offset[1]);
                }).ToArray();
                if (coords.Any(p => Math.Abs(p.X) >= 2048 || Math.Abs(p.Y) >= 2048)) throw new MapAuthoringException("FP-MAP-001", "UV coordinates exceed the runtime range; lower texture scale.");
                result.Add(new BuiltFace(points,coords,normal,geometry.Material,geometry.Shade * (.7f+.3f*Math.Max(0,normal.Y)))
                { Damaging = geometry.Damaging, Terrain = Enum.TryParse<Terrain>(geometry.Terrain, true, out var terrain) ? terrain : Terrain.Metal });
            }
            return result;
        }

        public static void Add(BuiltMap map, MapDefinition definition)
        {
            foreach (var geometry in definition.Geometry)
                foreach (var face in Compile(geometry, definition.Materials[geometry.Material].TexScale))
                { map.Faces.Add(face); if (geometry.Solid) map.Solid.Add(face); }
        }
    }
}
