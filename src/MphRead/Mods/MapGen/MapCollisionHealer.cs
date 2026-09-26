using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen;

public enum MapCollisionRepairKind
{
    Canonicalized,
    Convexified,
    SeamStitched,
    TJunctionStitched,
    BuriedRestored,
    FloorProxyAdded,
    PhantomRemoved,
    SpawnMoved,
    ItemMoved,
    ProbeFailure,
    ReachabilityWarning
}

public sealed record MapCollisionRepair(
    MapCollisionRepairKind Kind,
    float Confidence,
    string Detail,
    Vector3[] Points);

public sealed class MapCollisionHealth
{
    public int InputFaces { get; set; }
    public int OutputFaces { get; set; }
    public int CanonicalizedFaces { get; set; }
    public int ConvexifiedFaces { get; set; }
    public int StitchedVertices { get; set; }
    public int TJunctions { get; set; }
    public int RestoredBuriedFaces { get; set; }
    public int FloorProxies { get; set; }
    public int PhantomFacesRemoved { get; set; }
    public int SpawnsMoved { get; set; }
    public int ItemsMoved { get; set; }
    public int NavigationNodes { get; set; }
    public int ReachableNodes { get; set; }
    public int NavigationComponents { get; set; }
    public int ReachableComponents { get; set; }
    public int ProbeCount { get; set; }
    public int ProbeFailures { get; set; }
    public int SweepCount { get; set; }
    public int SweepFailures { get; set; }

    public float Confidence
    {
        get
        {
            if (ProbeCount + SweepCount == 0) return 1;
            int total = ProbeCount + SweepCount;
            int failures = ProbeFailures + SweepFailures;
            return Math.Clamp(1f - failures / (float)total, 0, 1);
        }
    }
}

/// <summary>
/// Conservative self-healing for imported collision. It deliberately works on
/// BuiltFace rather than packed wc01 data so the same repaired graph feeds
/// validation, navigation, playtest and the final collision packer.
/// </summary>
public static class MapCollisionHealer
{
    private const float WalkableY = .45f;
    private const float CoverBehind = 1f;
    private const float CoverFront = .25f;
    private const float PlayerRadius = .45f;
    private const float PlayerHeight = 1.6f;
    private const float RuntimeEdgeMargin = -.03125f;

    public static MapCollisionHealth Heal(BuiltMap map, MapImport import,
        IReadOnlyList<BuiltFace>? buriedCandidates = null,
        CancellationToken cancellation = default)
    {
        var health = new MapCollisionHealth { InputFaces = map.Solid.Count };
        map.CollisionRepairs.Clear();
        if (!import.AutoHealCollision)
        {
            health.OutputFaces = map.Solid.Count;
            map.CollisionHealth = health;
            return health;
        }

        float tolerance = Math.Clamp(import.CollisionHealTolerance, Fixed.ToFloat(2), .25f);
        var normalized = new List<BuiltFace>(map.Solid.Count);
        foreach (BuiltFace face in map.Solid)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (BuiltFace result in Normalize(face, map, health))
                normalized.Add(result);
        }

        normalized = StitchVertices(normalized, tolerance, map, health, cancellation);
        StitchTJunctions(normalized, tolerance, map, health, cancellation);

        var candidateBuried = (buriedCandidates ?? Array.Empty<BuiltFace>())
            .SelectMany(face => Normalize(face, map: null, health: null)).ToList();
        RepairFloorCoverage(map.Faces, normalized, candidateBuried, map, health, cancellation);
        RemoveHighConfidencePhantoms(map.Faces, normalized, map, health, cancellation);

        // A repair can introduce vertices that are representable as floats but
        // not as the exact wc01 world that will run. Normalize once more after
        // topology changes.
        var final = new List<BuiltFace>(normalized.Count);
        foreach (BuiltFace face in normalized)
            final.AddRange(Normalize(face, map: null, health: null));
        map.Solid.Clear();
        map.Solid.AddRange(final);
        health.OutputFaces = final.Count;
        map.CollisionHealth = health;
        return health;
    }

    public static void RepairGameplayObjects(BuiltMap map, MapDefinition definition,
        CancellationToken cancellation = default)
    {
        MapCollisionHealth health = map.CollisionHealth ??= new();
        if (map.Solid.Count == 0) return;
        var index = new FaceIndex(map.Solid, 4f);

        foreach (MapSpawn spawn in definition.Spawns)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3 original = V(spawn.Position);
            if (FindSafeStandingPoint(original, index, out Vector3 repaired)
                && Vector3.DistanceSquared(original, repaired) > .0001f)
            {
                spawn.Position = A(repaired);
                health.SpawnsMoved++;
                map.CollisionRepairs.Add(new(MapCollisionRepairKind.SpawnMoved, .98f,
                    $"Spawn snapped {Vector3.Distance(original, repaired):0.###} units to valid floor/clearance.",
                    new[] { original, repaired }));
            }
        }

        foreach (MapItem item in definition.Items)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3 original = V(item.Position);
            if (TryFloor(index, original, 2.5f, .5f, out float floor))
            {
                Vector3 repaired = new(original.X, floor + .08f, original.Z);
                if (Vector3.DistanceSquared(original, repaired) > .0001f)
                {
                    item.Position = A(repaired);
                    health.ItemsMoved++;
                    map.CollisionRepairs.Add(new(MapCollisionRepairKind.ItemMoved, .96f,
                        "Pickup snapped to final healed floor.", new[] { original, repaired }));
                }
            }
        }

        AuditReachabilityAndProbes(map, definition, index, health, cancellation);
    }

    private static IEnumerable<BuiltFace> Normalize(BuiltFace source,
        BuiltMap? map, MapCollisionHealth? health)
    {
        Vector3[] points = source.Points.Select(Snap).ToArray();
        points = Simplify(points);
        if (points.Length < 3) yield break;

        Vector3 originalNormal = source.Normal.LengthSquared > 1e-10f
            ? source.Normal.Normalized() : Vector3.UnitY;
        Vector3 normal = Newell(points);
        if (normal.LengthSquared < 1e-10f) yield break;
        normal.Normalize();
        if (Vector3.Dot(normal, originalNormal) < 0)
        {
            Array.Reverse(points);
            normal = -normal;
        }

        bool changed = !Same(points, source.Points);
        bool invalid = !Convex(points, normal) || !RuntimeAcceptsPolygon(points, normal);
        if (invalid)
        {
            Vector3[] hull = ConvexHull(points, normal);
            if (hull.Length >= 3)
            {
                normal = Newell(hull);
                if (normal.LengthSquared > 1e-10f)
                {
                    normal.Normalize();
                    if (Vector3.Dot(normal, originalNormal) < 0)
                    {
                        Array.Reverse(hull);
                        normal = -normal;
                    }
                    BuiltFace repaired = Copy(source, hull, normal);
                    repaired.CollisionConfidence = Math.Min(repaired.CollisionConfidence, .98f);
                    if(health!=null)health.ConvexifiedFaces++;
                    map?.CollisionRepairs.Add(new(MapCollisionRepairKind.Convexified, .98f,
                        $"Runtime-invalid {points.Length}-vertex polygon rebuilt as a convex {hull.Length}-vertex polygon.",
                        hull));
                    yield return repaired;
                    yield break;
                }
            }

            // A source brush side should be convex. If numerical damage made
            // it impossible to reconstruct as one polygon, triangles are the
            // safest shape the MPH edge test can consume.
            for (int i = 1; i < points.Length - 1; i++)
            {
                Vector3[] tri = { points[0], points[i], points[i + 1] };
                Vector3 triNormal = Vector3.Cross(tri[1] - tri[0], tri[2] - tri[0]);
                if (triNormal.LengthSquared < 1e-10f) continue;
                triNormal.Normalize();
                if (Vector3.Dot(triNormal, originalNormal) < 0)
                {
                    (tri[1], tri[2]) = (tri[2], tri[1]);
                    triNormal = -triNormal;
                }
                BuiltFace repaired = Copy(source, tri, triNormal);
                repaired.CollisionConfidence = Math.Min(repaired.CollisionConfidence, .94f);
                yield return repaired;
            }
            if(health!=null)health.ConvexifiedFaces++;
            map?.CollisionRepairs.Add(new(MapCollisionRepairKind.Convexified, .94f,
                "Runtime-invalid polygon triangulated because a safe convex hull could not be retained.",
                points));
            yield break;
        }

        BuiltFace normalized = Copy(source, points, normal);
        if (changed)
        {
            if(health!=null)health.CanonicalizedFaces++;
            map?.CollisionRepairs.Add(new(MapCollisionRepairKind.Canonicalized, .995f,
                "Collision vertices snapped to exact 20.12 runtime precision and redundant edge points removed.",
                points));
        }
        yield return normalized;
    }

    private static List<BuiltFace> StitchVertices(List<BuiltFace> faces, float tolerance,
        BuiltMap map, MapCollisionHealth health, CancellationToken cancellation)
    {
        var anchors = new Dictionary<(int X, int Y, int Z), List<Vector3>>();
        var result = new List<BuiltFace>(faces.Count);
        float cell = tolerance;
        foreach (BuiltFace face in faces)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3[] points = new Vector3[face.Points.Length];
            bool changed = false;
            for (int i = 0; i < face.Points.Length; i++)
            {
                Vector3 point = face.Points[i];
                (int X, int Y, int Z) key = Cell(point, cell);
                Vector3? best = null;
                float bestSq = tolerance * tolerance;
                for (int x = key.X - 1; x <= key.X + 1; x++)
                    for (int y = key.Y - 1; y <= key.Y + 1; y++)
                        for (int z = key.Z - 1; z <= key.Z + 1; z++)
                            if (anchors.TryGetValue((x, y, z), out List<Vector3>? list))
                                foreach (Vector3 candidate in list)
                                {
                                    float sq = Vector3.DistanceSquared(point, candidate);
                                    if (sq <= bestSq) { bestSq = sq; best = candidate; }
                                }
                if (best.HasValue)
                {
                    points[i] = best.Value;
                    if (bestSq > 1e-12f) { changed = true; health.StitchedVertices++; }
                }
                else
                {
                    points[i] = point;
                    if (!anchors.TryGetValue(key, out List<Vector3>? list))
                        anchors.Add(key, list = new());
                    list.Add(point);
                }
            }
            points = Simplify(points);
            if (points.Length < 3) continue;
            Vector3 normal = Newell(points);
            if (normal.LengthSquared < 1e-10f) continue;
            normal.Normalize();
            if (Vector3.Dot(normal, face.Normal) < 0) { Array.Reverse(points); normal = -normal; }
            result.Add(Copy(face, points, normal));
            if (changed)
                map.CollisionRepairs.Add(new(MapCollisionRepairKind.SeamStitched, .99f,
                    $"Near-identical collision vertices welded within {tolerance:0.####} units.", points));
        }
        return result;
    }

    private static void StitchTJunctions(List<BuiltFace> faces, float tolerance,
        BuiltMap map, MapCollisionHealth health, CancellationToken cancellation)
    {
        float cellSize = Math.Max(.25f, tolerance * 4);
        var points = new Dictionary<(int X, int Y, int Z), List<Vector3>>();
        foreach (BuiltFace face in faces)
            foreach (Vector3 point in face.Points.Distinct())
            {
                var key = Cell(point, cellSize);
                if (!points.TryGetValue(key, out List<Vector3>? list)) points.Add(key, list = new());
                if (!list.Contains(point)) list.Add(point);
            }

        for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            BuiltFace face = faces[faceIndex];
            var rebuilt = new List<Vector3>();
            bool changed = false;
            for (int edge = 0; edge < face.Points.Length; edge++)
            {
                Vector3 a = face.Points[edge], b = face.Points[(edge + 1) % face.Points.Length];
                rebuilt.Add(a);
                Vector3 ab = b - a;
                float length = ab.Length;
                if (length < tolerance * 2 || length > 32) continue;
                var insertions = new List<(float T, Vector3 P)>();
                Vector3 min = Vector3.ComponentMin(a, b) - new Vector3(tolerance);
                Vector3 max = Vector3.ComponentMax(a, b) + new Vector3(tolerance);
                var low = Cell(min, cellSize); var high = Cell(max, cellSize);
                for (int x = low.X; x <= high.X; x++)
                    for (int y = low.Y; y <= high.Y; y++)
                        for (int z = low.Z; z <= high.Z; z++)
                            if (points.TryGetValue((x, y, z), out List<Vector3>? candidates))
                                foreach (Vector3 p in candidates)
                                {
                                    float t = Vector3.Dot(p - a, ab) / Math.Max(ab.LengthSquared, 1e-8f);
                                    if (t <= .02f || t >= .98f) continue;
                                    Vector3 projected = a + ab * t;
                                    if (Vector3.DistanceSquared(p, projected) > tolerance * tolerance) continue;
                                    float planeDistance = MathF.Abs(Vector3.Dot(face.Normal, p - a));
                                    if (planeDistance > tolerance) continue;
                                    Vector3 snapped = Snap(projected);
                                    if (!insertions.Any(i => Vector3.DistanceSquared(i.P, snapped) < 1e-10f))
                                        insertions.Add((t, snapped));
                                }
                // Do not turn a compact native polygon into a fan explosion.
                int room = Math.Max(0, 10 - face.Points.Length);
                foreach (var insertion in insertions.OrderBy(i => i.T).Take(room))
                {
                    rebuilt.Add(insertion.P);
                    changed = true;
                    health.TJunctions++;
                }
            }
            if (!changed) continue;
            Vector3[] repairedPoints = Simplify(rebuilt.ToArray());
            if (repairedPoints.Length < 3) continue;
            Vector3 normal = Newell(repairedPoints);
            if (normal.LengthSquared < 1e-10f) continue;
            normal.Normalize();
            if (Vector3.Dot(normal, face.Normal) < 0) { Array.Reverse(repairedPoints); normal = -normal; }
            faces[faceIndex] = Copy(face, repairedPoints, normal);
            map.CollisionRepairs.Add(new(MapCollisionRepairKind.TJunctionStitched, .97f,
                "A near-edge imported vertex was inserted into the neighbouring collision edge to close a T-junction.",
                repairedPoints));
        }
    }

    private static void RepairFloorCoverage(IReadOnlyList<BuiltFace> render,
        List<BuiltFace> solid, List<BuiltFace> buried, BuiltMap map,
        MapCollisionHealth health, CancellationToken cancellation)
    {
        var collision = new FaceIndex(solid, 4f);
        var buriedIndex = new FaceIndex(buried, 4f);
        var restored = new HashSet<string>();
        var proxies = new HashSet<string>();

        foreach (BuiltFace visible in render)
        {
            cancellation.ThrowIfCancellationRequested();
            if (visible.Sky || visible.Normal.Y < WalkableY) continue;
            Vector3[] samples = Samples(visible).ToArray();
            if (samples.All(sample => HasSupport(collision, sample, CoverBehind, CoverFront))) continue;

            bool restoredAny = false;
            foreach (Vector3 sample in samples)
            {
                if (HasSupport(collision, sample, CoverBehind, CoverFront)) continue;
                BuiltFace? candidate = FindSupportingFace(buriedIndex, sample, CoverBehind, CoverFront);
                if (candidate == null) continue;
                string key = FaceKey(candidate);
                if (restored.Add(key))
                {
                    BuiltFace restoredFace = Copy(candidate, candidate.Points, candidate.Normal);
                    restoredFace.CollisionSource = "BuriedRestored";
                    restoredFace.CollisionConfidence = .985f;
                    solid.Add(restoredFace);
                    health.RestoredBuriedFaces++;
                    map.CollisionRepairs.Add(new(MapCollisionRepairKind.BuriedRestored, .985f,
                        "Buried-face pruning would have left visible walkable geometry unsupported, so the source brush face was restored.",
                        restoredFace.Points));
                    restoredAny = true;
                }
            }
            if (restoredAny) collision = new FaceIndex(solid, 4f);
            if (samples.All(sample => HasSupport(collision, sample, CoverBehind, CoverFront))) continue;

            string proxyKey = FaceKey(visible);
            if (!proxies.Add(proxyKey)) continue;
            Vector3[] points = visible.Points
                .Select(p => Snap(p - visible.Normal * Fixed.ToFloat(1))).ToArray();
            Vector3 normal = visible.Normal.LengthSquared > 1e-10f ? visible.Normal.Normalized() : Vector3.UnitY;
            var proxy = new BuiltFace(points, new Vector2[points.Length], normal, 0, 1f)
            {
                CollisionSource = "AutoFloor",
                CollisionConfidence = .94f
            };
            solid.Add(proxy);
            health.FloorProxies++;
            map.CollisionRepairs.Add(new(MapCollisionRepairKind.FloorProxyAdded, .94f,
                "Visible walkable surface had no usable collision within the runtime coverage envelope; a thin render-aligned floor proxy was generated.",
                proxy.Points));
            collision = new FaceIndex(solid, 4f);
        }
    }

    private static void RemoveHighConfidencePhantoms(IReadOnlyList<BuiltFace> render,
        List<BuiltFace> solid, BuiltMap map, MapCollisionHealth health,
        CancellationToken cancellation)
    {
        var renderIndex = new FaceIndex(render.Where(f => !f.Sky).ToArray(), 4f);
        for (int i = solid.Count - 1; i >= 0; i--)
        {
            cancellation.ThrowIfCancellationRequested();
            BuiltFace face = solid[i];
            if (face.PlayerClip || face.CollisionSource is "Patch" or "AutoFloor" or "BuriedRestored")
                continue;
            if (!face.CollisionSource.Equals("Brush", StringComparison.OrdinalIgnoreCase))
                continue;

            bool hiddenShader = IsHiddenCollisionShader(face.CollisionShader);
            bool nearVisible = Samples(face).Any(p => HasNearbyRender(renderIndex, p, face.Normal, 1.15f));
            if (nearVisible) continue;

            if (hiddenShader)
            {
                solid.RemoveAt(i);
                health.PhantomFacesRemoved++;
                map.CollisionRepairs.Add(new(MapCollisionRepairKind.PhantomRemoved, .965f,
                    $"High-confidence invisible {face.CollisionShader ?? "brush"} collision had no rendered surface nearby and was removed.",
                    face.Points));
            }
            else
            {
                // Keep uncertain invisible solids, but surface them in the
                // repair overlay instead of silently guessing.
                map.CollisionRepairs.Add(new(MapCollisionRepairKind.PhantomRemoved, .68f,
                    "Collision has no corresponding rendered surface. Kept because its source shader is not confidently decorative/caulk.",
                    face.Points));
            }
        }
    }

    private static void AuditReachabilityAndProbes(BuiltMap map, MapDefinition definition,
        FaceIndex collision, MapCollisionHealth health, CancellationToken cancellation)
    {
        MapNodePacker.NavigationGraph? graph = null;
        try { graph = MapNodePacker.Analyze(map.Solid, definition.NavigationLinks, cancellation); }
        catch (MapAuthoringException) { }

        if (graph != null)
        {
            health.NavigationNodes = graph.Positions.Length;
            health.NavigationComponents = graph.Components.Distinct().Count();
            var spawnComponents = new HashSet<int>();
            foreach (MapSpawn spawn in definition.Spawns)
            {
                Vector3 position = V(spawn.Position);
                int nearest = -1; float best = float.MaxValue;
                for (int i = 0; i < graph.Positions.Length; i++)
                {
                    float sq = Vector3.DistanceSquared(position, graph.Positions[i]);
                    if (sq < best) { best = sq; nearest = i; }
                }
                if (nearest >= 0) spawnComponents.Add(graph.Components[nearest]);
            }
            if (spawnComponents.Count == 0 && graph.Components.Length > 0)
                spawnComponents.Add(graph.Components[0]);
            health.ReachableComponents = spawnComponents.Count;
            health.ReachableNodes = graph.Components.Count(spawnComponents.Contains);
            if (health.NavigationComponents > health.ReachableComponents)
                map.CollisionRepairs.Add(new(MapCollisionRepairKind.ReachabilityWarning, .72f,
                    $"Navigation has {health.NavigationComponents} components but spawn traversal reaches {health.ReachableComponents}.",
                    definition.Spawns.Select(s => V(s.Position)).ToArray()));

            // Short player-sized walking sweeps across graph edges.
            for (int a = 0; a < graph.Neighbours.Length; a++)
            {
                foreach (int b in graph.Neighbours[a])
                {
                    if (b <= a) continue;
                    cancellation.ThrowIfCancellationRequested();
                    Vector3 p = graph.Positions[a], q = graph.Positions[b];
                    Vector3 delta = q - p;
                    int steps = Math.Clamp((int)MathF.Ceiling(delta.Length / .5f), 1, 12);
                    bool fail = false;
                    for (int step = 1; step < steps; step++)
                    {
                        Vector3 sample = Vector3.Lerp(p, q, step / (float)steps);
                        if (!TryFloor(collision, sample + Vector3.UnitY * .4f, 1.1f, .4f, out _))
                        { fail = true; break; }
                    }
                    health.SweepCount++;
                    if (fail) health.SweepFailures++;
                    if (health.SweepCount >= 4000) break;
                }
                if (health.SweepCount >= 4000) break;
            }
        }

        // Render-driven probes catch floor holes navigation cannot see because a
        // missing floor never produces a node in the first place.
        int stride = Math.Max(1, map.Faces.Count / 10000);
        for (int i = 0; i < map.Faces.Count; i += stride)
        {
            cancellation.ThrowIfCancellationRequested();
            BuiltFace face = map.Faces[i];
            if (face.Sky || face.Normal.Y < WalkableY) continue;
            foreach (Vector3 sample in Samples(face).Take(3))
            {
                health.ProbeCount++;
                if (!HasSupport(collision, sample, CoverBehind, CoverFront))
                {
                    health.ProbeFailures++;
                    if (health.ProbeFailures <= 64)
                        map.CollisionRepairs.Add(new(MapCollisionRepairKind.ProbeFailure, .55f,
                            "Headless player-sized floor probe still found rendered walkable geometry without support.",
                            new[] { sample }));
                }
            }
        }
    }

    private static bool FindSafeStandingPoint(Vector3 origin, FaceIndex collision, out Vector3 result)
    {
        IEnumerable<Vector3> Candidates()
        {
            yield return origin;
            for (float radius = .5f; radius <= 3f; radius += .5f)
            {
                int steps = Math.Max(8, (int)(radius * 8));
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * MathF.Tau / steps;
                    yield return new(origin.X + MathF.Cos(angle) * radius, origin.Y,
                        origin.Z + MathF.Sin(angle) * radius);
                }
            }
        }
        foreach (Vector3 candidate in Candidates())
        {
            if (!TryFloor(collision, candidate + Vector3.UnitY * .5f, 5f, 1f, out float floor))
                continue;
            Vector3 standing = new(candidate.X, floor + .05f, candidate.Z);
            if (HasClearance(collision, standing))
            { result = standing; return true; }
        }
        result = origin;
        return false;
    }

    private static bool HasClearance(FaceIndex collision, Vector3 feet)
    {
        Vector3 middle = feet + Vector3.UnitY * (PlayerHeight * .5f);
        foreach (BuiltFace face in collision.Query(middle, PlayerRadius + .2f))
        {
            if (MathF.Abs(face.Normal.Y) > .75f) continue;
            Vector3 centre = Centre(face);
            if (centre.Y < feet.Y - .2f || centre.Y > feet.Y + PlayerHeight + .2f) continue;
            float distance = MathF.Abs(Vector3.Dot(face.Normal, middle - face.Points[0]));
            if (distance < PlayerRadius * .9f) return false;
        }
        return true;
    }

    private static bool TryFloor(FaceIndex collision, Vector3 point,
        float maxDrop, float maxRise, out float floor)
    {
        floor = float.MinValue;
        bool found = false;
        foreach (BuiltFace face in collision.Query(point, Math.Max(maxDrop, 2f)))
        {
            if (MathF.Abs(face.Normal.Y) < .2f) continue;
            float d = Vector3.Dot(face.Normal, face.Points[0]);
            float y = (d - face.Normal.X * point.X - face.Normal.Z * point.Z) / face.Normal.Y;
            float delta = point.Y - y;
            if (delta < -maxRise || delta > maxDrop) continue;
            Vector3 hit = new(point.X, y, point.Z);
            if (!Accepts(face, hit)) continue;
            if (!found || y > floor) { floor = y; found = true; }
        }
        return found;
    }

    private static bool HasSupport(FaceIndex collision, Vector3 sample, float behind, float front)
        => FindSupportingFace(collision, sample, behind, front) != null;

    private static BuiltFace? FindSupportingFace(FaceIndex collision, Vector3 sample,
        float behind, float front)
    {
        BuiltFace? best = null; float bestAbs = float.MaxValue;
        foreach (BuiltFace face in collision.Query(sample, behind + front + .5f))
        {
            if (MathF.Abs(face.Normal.Y) < .15f) continue;
            float d = Vector3.Dot(face.Normal, face.Points[0]);
            float y = (d - face.Normal.X * sample.X - face.Normal.Z * sample.Z) / face.Normal.Y;
            float delta = sample.Y - y;
            if (delta < -front || delta > behind) continue;
            Vector3 hit = new(sample.X, y, sample.Z);
            if (!Accepts(face, hit)) continue;
            float abs = MathF.Abs(delta);
            if (abs < bestAbs) { bestAbs = abs; best = face; }
        }
        return best;
    }

    private static bool HasNearbyRender(FaceIndex render, Vector3 sample, Vector3 normal, float range)
    {
        foreach (BuiltFace face in render.Query(sample, range))
        {
            float dot = MathF.Abs(Vector3.Dot(normal, face.Normal));
            if (dot < .65f) continue;
            float distance = MathF.Abs(Vector3.Dot(face.Normal, sample - face.Points[0]));
            if (distance > range) continue;
            Vector3 projected = sample - face.Normal * Vector3.Dot(face.Normal, sample - face.Points[0]);
            if (Accepts(face, projected)) return true;
        }
        return false;
    }

    private static IEnumerable<Vector3> Samples(BuiltFace face)
    {
        Vector3 centre = Centre(face);
        yield return centre;
        for (int i = 0; i < face.Points.Length; i++)
        {
            Vector3 corner = face.Points[i];
            yield return corner + (centre - corner) * .18f;
            if (i < 3)
            {
                Vector3 edge = (corner + face.Points[(i + 1) % face.Points.Length]) * .5f;
                yield return edge + (centre - edge) * .12f;
            }
        }
    }

    private static bool RuntimeAcceptsPolygon(Vector3[] points, Vector3 normal)
    {
        var face = new BuiltFace(points, new Vector2[points.Length], normal, 0, 1);
        foreach (Vector3 sample in Samples(face))
            if (!Accepts(face, sample)) return false;
        return true;
    }

    private static bool Accepts(BuiltFace face, Vector3 point)
    {
        Vector3[] points = face.Points;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 first = points[i], second = points[(i + 1) % points.Length];
            Vector3 edge = first - second;
            if (edge.LengthSquared < 1e-12f) continue;
            Vector3 cross = Vector3.Cross(edge.Normalized(), face.Normal);
            if (Vector3.Dot(point, cross) - Vector3.Dot(cross, second) < RuntimeEdgeMargin)
                return false;
        }
        return true;
    }

    private static bool Convex(Vector3[] points, Vector3 normal)
    {
        float sign = 0;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 a = points[i], b = points[(i + 1) % points.Length], c = points[(i + 2) % points.Length];
            float turn = Vector3.Dot(Vector3.Cross(b - a, c - b), normal);
            if (MathF.Abs(turn) < 1e-7f) continue;
            float next = MathF.Sign(turn);
            if (sign == 0) sign = next;
            else if (sign != next) return false;
        }
        return sign != 0;
    }

    private static Vector3[] ConvexHull(Vector3[] points, Vector3 normal)
    {
        int axis = MathF.Abs(normal.X) > MathF.Abs(normal.Y)
            ? (MathF.Abs(normal.X) > MathF.Abs(normal.Z) ? 0 : 2)
            : (MathF.Abs(normal.Y) > MathF.Abs(normal.Z) ? 1 : 2);
        (float X, float Y) Project(Vector3 p) => axis switch
        {
            0 => (p.Y, p.Z),
            1 => (p.X, p.Z),
            _ => (p.X, p.Y)
        };
        var unique = points.Distinct().Select(p => (P: p, Q: Project(p)))
            .OrderBy(v => v.Q.X).ThenBy(v => v.Q.Y).ToArray();
        if (unique.Length < 3) return Array.Empty<Vector3>();
        static float Cross((float X, float Y) a, (float X, float Y) b, (float X, float Y) c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var lower = new List<(Vector3 P, (float X, float Y) Q)>();
        foreach (var p in unique)
        {
            while (lower.Count >= 2 && Cross(lower[^2].Q, lower[^1].Q, p.Q) <= 1e-8f) lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<(Vector3 P, (float X, float Y) Q)>();
        for (int i = unique.Length - 1; i >= 0; i--)
        {
            var p = unique[i];
            while (upper.Count >= 2 && Cross(upper[^2].Q, upper[^1].Q, p.Q) <= 1e-8f) upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).Select(v => v.P).ToArray();
    }

    private static Vector3[] Simplify(Vector3[] input)
    {
        var points = new List<Vector3>();
        foreach (Vector3 point in input)
        {
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        }
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        bool changed = true;
        while (changed && points.Count > 3)
        {
            changed = false;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[(i - 1 + points.Count) % points.Count];
                Vector3 b = points[i], c = points[(i + 1) % points.Count];
                Vector3 ab = b - a, bc = c - b;
                float product = ab.LengthSquared * bc.LengthSquared;
                if (product > 0 && Vector3.Cross(ab, bc).LengthSquared <= product * 1e-10f
                    && Vector3.Dot(ab, bc) >= 0)
                {
                    points.RemoveAt(i); changed = true; break;
                }
            }
        }
        return points.ToArray();
    }

    private static Vector3 Snap(Vector3 p)
        => new(Fixed.ToFloat(Fixed.ToInt(p.X)), Fixed.ToFloat(Fixed.ToInt(p.Y)), Fixed.ToFloat(Fixed.ToInt(p.Z)));

    private static Vector3 Newell(IReadOnlyList<Vector3> points)
    {
        Vector3 n = Vector3.Zero;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 a = points[i], b = points[(i + 1) % points.Count];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }

    private static BuiltFace Copy(BuiltFace source, Vector3[] points, Vector3 normal)
    {
        var result = new BuiltFace(points, new Vector2[points.Length], normal, source.Material, source.Shade)
        {
            Damaging = source.Damaging,
            Terrain = source.Terrain,
            Sky = source.Sky,
            Slipperiness = source.Slipperiness,
            ReflectBeams = source.ReflectBeams,
            IgnorePlayers = source.IgnorePlayers,
            IgnoreBeams = source.IgnoreBeams,
            IgnoreScan = source.IgnoreScan,
            CollisionSource = source.CollisionSource,
            CollisionSourceId = source.CollisionSourceId,
            CollisionShader = source.CollisionShader,
            PlayerClip = source.PlayerClip,
            CollisionConfidence = source.CollisionConfidence
        };
        return result;
    }

    private static bool Same(IReadOnlyList<Vector3> a, IReadOnlyList<Vector3> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Vector3.DistanceSquared(a[i], b[i]) > 1e-12f) return false;
        return true;
    }

    private static Vector3 Centre(BuiltFace face)
    {
        Vector3 result = Vector3.Zero;
        foreach (Vector3 point in face.Points) result += point;
        return face.Points.Length == 0 ? result : result / face.Points.Length;
    }

    private static string FaceKey(BuiltFace face)
        => String.Join("|", face.Points.Select(p => $"{Fixed.ToInt(p.X)},{Fixed.ToInt(p.Y)},{Fixed.ToInt(p.Z)}").OrderBy(v => v));

    private static bool IsHiddenCollisionShader(string? shader)
    {
        if (String.IsNullOrWhiteSpace(shader)) return false;
        string value = shader.ToLowerInvariant();
        return value.Contains("caulk") || value.Contains("nodraw") || value.Contains("skip")
            || value.Contains("hint") || value.Contains("invisible");
    }

    private static (int X, int Y, int Z) Cell(Vector3 point, float cell)
        => ((int)MathF.Floor(point.X / cell), (int)MathF.Floor(point.Y / cell),
            (int)MathF.Floor(point.Z / cell));

    private static Vector3 V(float[] value) => new(value[0], value[1], value[2]);
    private static float[] A(Vector3 value) => new[] { value.X, value.Y, value.Z };

    private sealed class FaceIndex
    {
        private readonly Dictionary<(int X, int Y, int Z), List<BuiltFace>> _cells = new();
        private readonly List<BuiltFace> _large = new();
        private readonly float _cell;

        public FaceIndex(IEnumerable<BuiltFace> faces, float cell)
        {
            _cell = cell;
            foreach (BuiltFace face in faces)
            {
                if (face.Points.Length == 0) continue;
                Vector3 min = face.Points[0], max = face.Points[0];
                foreach (Vector3 point in face.Points.Skip(1))
                { min = Vector3.ComponentMin(min, point); max = Vector3.ComponentMax(max, point); }
                var low = Cell(min, _cell); var high = Cell(max, _cell);
                // Giant surfaces can cover thousands of cells. Keep them in
                // a small global fallback list so every query can still see
                // them without populating thousands of buckets.
                long count = (long)(high.X - low.X + 1) * (high.Y - low.Y + 1) * (high.Z - low.Z + 1);
                if (count > 4096)
                {
                    _large.Add(face);
                    continue;
                }
                for (int x = low.X; x <= high.X; x++)
                    for (int y = low.Y; y <= high.Y; y++)
                        for (int z = low.Z; z <= high.Z; z++)
                            Add((x, y, z), face);
            }
        }

        private void Add((int X, int Y, int Z) key, BuiltFace face)
        {
            if (!_cells.TryGetValue(key, out List<BuiltFace>? list)) _cells.Add(key, list = new());
            list.Add(face);
        }

        public IEnumerable<BuiltFace> Query(Vector3 point, float radius)
        {
            var low = Cell(point - new Vector3(radius), _cell);
            var high = Cell(point + new Vector3(radius), _cell);
            var seen = new HashSet<BuiltFace>();
            foreach(BuiltFace face in _large)
                if(seen.Add(face))yield return face;
            for (int x = low.X; x <= high.X; x++)
                for (int y = low.Y; y <= high.Y; y++)
                    for (int z = low.Z; z <= high.Z; z++)
                        if (_cells.TryGetValue((x, y, z), out List<BuiltFace>? list))
                            foreach (BuiltFace face in list)
                                if (seen.Add(face)) yield return face;
        }
    }
}
