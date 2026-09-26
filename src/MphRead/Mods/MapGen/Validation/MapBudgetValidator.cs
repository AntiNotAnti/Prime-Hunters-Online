using System;
using System.Linq;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    public static class MapBudgetValidator
    {
        public const long MaxGridCells = 2_000_000;
        public static void Analyze(BuiltMap map, MapValidationResult result)
        {
            var solid = map.Solid.SelectMany(MapPacker.CollisionParts).ToArray();
            MapCollisionOptimizer.Result collision=MapCollisionOptimizer.Optimize(
                MapCollisionOptimizer.FromFaces(map.Solid));
            MapCollisionOptimizer.Metrics collisionMetrics=MapCollisionOptimizer.Measure(collision.Editors);
            Add(result, "Geometry faces", map.Faces.Count);
            Add(result, "Vertices", map.Faces.Sum(f => (long)f.Points.Length));
            if(map.Faces.Count>0)
            {
                var render=MapPacker.EstimateRenderLayout(map);
                Add(result,"Render partitions",render.Partitions,Int16.MaxValue-1);
                Add(result,"Render meshes",render.Meshes,UInt16.MaxValue/2);
                Add(result,"Render command bytes",render.CommandBytes,MapPackageReader.MaxEntryBytes);
                if(map.Definition.Partitioning?.PortalCulling==true)
                {
                    Add(result,"Portal room parts",render.Partitions,MapRuntimePartitioner.MaxPortalParts+1);
                    Add(result,"Generated portals",render.Portals,512);
                    if(!render.PortalCullingApplied&&render.Partitions>1)
                        result.Warning("FP-MAP-018",
                            render.Partitions>MapRuntimePartitioner.MaxPortalParts
                                ? $"Portal culling requested, but {render.Partitions} spatial parts exceed the runtime {MapRuntimePartitioner.MaxPortalParts}-part visibility ceiling. Render partitioning remains enabled without portal culling."
                                : "Portal culling requested, but the spatial part graph is disconnected. Render partitioning remains enabled without portal culling.");
                }
            }
            Add(result, "Collision faces", collisionMetrics.Faces, 65535);
            Add(result, "Collision points", collisionMetrics.Points, 65535);
            Add(result, "Collision point indices", collisionMetrics.PointIndices, 65535);
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
            Add(result, "Collision grid cells", collisionMetrics.GridCells, MaxGridCells);
            Add(result, "Collision references", collisionMetrics.References, 65535);
            if(map.CollisionHealth is {} health)
            {
                Add(result,"Collision auto-heal repairs",map.CollisionRepairs.Count);
                Add(result,"Collision probes",health.ProbeCount+health.SweepCount);
                Add(result,"Collision probe failures",health.ProbeFailures+health.SweepFailures);
                if(health.ProbeFailures+health.SweepFailures>0)
                    result.Warning("FP-MAP-018",
                        $"Imported collision auto-heal completed with {health.ProbeFailures:N0} unsupported floor probes and "
                        +$"{health.SweepFailures:N0} short-walk sweep failures. Review the Collision repairs overlay.");
                if(health.NavigationComponents>health.ReachableComponents&&health.ReachableComponents>0)
                    result.Warning("FP-MAP-007",
                        $"Imported traversal has {health.NavigationComponents} navigation regions; "
                        +$"{health.ReachableComponents} contain or connect to repaired spawn areas.");
            }
            if(collision.RemovedVertices>0||collision.MergedFaces>0)
                result.Warning("FP-MAP-018",
                    $"Collision compaction removed {collision.RemovedVertices:N0} redundant vertices and merged "
                    +$"{collision.MergedFaces:N0} adjacent coplanar faces; point indices "
                    +$"{collision.OriginalPointIndices:N0} -> {collision.OptimizedPointIndices:N0}.");
            if (map.Definition.Import is { CollisionPatchLevel: -1 }
                && map.Definition.Collision == null
                && map.ImportedPatchCollisionLevel >= 0
                && map.ImportedPatchCollisionLevel < map.Definition.Import.PatchLevel
                && map.ImportedPatchCollisionSourceFaces > map.ImportedPatchCollisionFaces)
            {
                string mode = map.ImportedPatchCollisionLevel == 0
                    ? "disabled"
                    : $"reduced to level {map.ImportedPatchCollisionLevel}";
                result.Warning("FP-MAP-018",
                    $"Q3 patch collision was {mode} automatically to fit MPH collision budgets "
                    + $"({map.ImportedPatchCollisionFaces:N0} of {map.ImportedPatchCollisionSourceFaces:N0} patch collision faces kept). "
                    + "Rendered curves are unchanged; structural BSP brushes and player clips remain solid.");
            }
            float limit = 8 * MathF.Pow(2, map.Definition.ScaleFactor);
            if (map.Faces.SelectMany(f => f.Points).Any(p => !float.IsFinite(p.LengthSquared)
                || p.X < -limit || p.Y < -limit || p.Z < -limit || p.X >= limit || p.Y >= limit || p.Z >= limit))
                result.Error("FP-MAP-004", "Compiled vertices exceed the model fixed-point range.");
        }

        public static bool CollisionFits(IEnumerable<BuiltFace> faces)
        {
            MapCollisionOptimizer.Result optimized=MapCollisionOptimizer.Optimize(
                MapCollisionOptimizer.FromFaces(faces));
            MapCollisionOptimizer.Metrics metrics=MapCollisionOptimizer.Measure(optimized.Editors);
            return metrics.Faces < 65535
                && metrics.Points < 65535
                && metrics.PointIndices <= 65535
                && metrics.GridCells < MaxGridCells
                && metrics.References < 65535;
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
