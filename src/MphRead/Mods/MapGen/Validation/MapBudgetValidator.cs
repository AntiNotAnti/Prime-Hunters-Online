using System;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    public static class MapBudgetValidator
    {
        public const long MaxGridCells = 2_000_000;
        public static void Analyze(BuiltMap map, MapValidationResult result)
        {
            var solid = map.Solid.SelectMany(MapPacker.CollisionParts).ToArray();
            Add(result, "Geometry faces", map.Faces.Count);
            Add(result, "Vertices", map.Faces.Sum(f => (long)f.Points.Length));
            Add(result, "Collision faces", solid.Length, 65535);
            Add(result, "Collision points", map.Solid.SelectMany(f => f.Points).Distinct().LongCount(), 65535);
            Add(result, "Collision point indices", solid.Sum(f => (long)f.Points.Length + 1), 65535);
            Add(result, "Entities", map.Entities.Count, 32767);
            int materialCount=Math.Max(map.Definition.Materials.Count,map.Faces.Count==0?0:map.Faces.Max(f=>f.Material)+1);
            Add(result, "Materials", materialCount, 32767);
            Add(result, "Textures", materialCount,4096);
            Add(result, "Authored assets",map.Definition.Assets.Count,MapPackageReader.MaxEntries-2);
            if (map.Solid.Count == 0) { result.Error("FP-MAP-013", "At least one solid face is required."); return; }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var face in solid)
            {
                if (face.Points.Length is < 3 or > 10) result.Error("FP-MAP-003", "Collision polygons require 3–10 vertices.");
                if (!float.IsFinite(face.Normal.LengthSquared) || face.Normal.LengthSquared < 0.99f || face.Normal.LengthSquared > 1.01f)
                    result.Error("FP-MAP-013", "Collision face normal must be normalized.");
                foreach (var p in face.Points) { min = Vector3.ComponentMin(min, p); max = Vector3.ComponentMax(max, p); }
            }
            long cells = 1;
            for (int axis = 0; axis < 3; axis++) cells = SaturatingMultiply(cells, (long)Math.Floor((max[axis] - min[axis]) / 4.0) + 1);
            Add(result, "Collision grid cells", cells, MaxGridCells);
            long references = 0;
            foreach (var face in solid)
            {
                long count = 1;
                for (int axis = 0; axis < 3; axis++)
                {
                    int a = axis;
                    double low = face.Points.Min(p => p[a]), high = face.Points.Max(p => p[a]);
                    count = SaturatingMultiply(count, (long)(Math.Floor((high - min[axis]) / 4) - Math.Floor((low - min[axis]) / 4) + 1));
                }
                references = count > long.MaxValue - references ? long.MaxValue : references + count;
            }
            Add(result, "Collision references", references, 65535);
            float limit = 8 * MathF.Pow(2, map.Definition.ScaleFactor);
            if (map.Faces.SelectMany(f => f.Points).Any(p => !float.IsFinite(p.LengthSquared)
                || p.X < -limit || p.Y < -limit || p.Z < -limit || p.X >= limit || p.Y >= limit || p.Z >= limit))
                result.Error("FP-MAP-004", "Compiled vertices exceed the model fixed-point range.");
        }

        private static long SaturatingMultiply(long a, long b) => a <= 0 || b <= 0 || a > long.MaxValue / b ? long.MaxValue : a * b;

        public static void Add(MapValidationResult result, string name, long used, long? limit = null)
        {
            var budget = new MapBudget(name, used, limit);
            result.Budgets.Add(budget);
            if (budget.Percent >= 100) result.Error("FP-MAP-003", $"{name}: {used:N0} / {limit:N0} (limit reached).");
            else if (budget.Percent >= 70) result.Warning("FP-MAP-018",
                $"{name}: {used:N0} / {limit:N0} ({budget.Percent:0}%)." + (budget.Percent >= 90 ? " Very little capacity remains." : ""));
        }
    }
}
