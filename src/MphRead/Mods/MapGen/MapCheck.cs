using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// What is wrong with a map's collision, said before it is generated.
    ///
    /// This exists because collision is now something a person edits by hand
    /// (see <see cref="CollisionObj"/>), and every way of getting it wrong is
    /// invisible: a face turned inside out in a 3D tool is a wall you walk
    /// through, a concave polygon rejects part of its own interior, a floor
    /// deleted by accident is a hole you fall down, and a material name with
    /// "damaging" in it that should not be there is a floor that kills. None
    /// of those look like anything in Blender, and the first three do not look
    /// like anything in the game either until somebody walks into them.
    ///
    /// So the checks here are the engine's own tests run ahead of time, and
    /// the report says what is there rather than only what is broken -- how
    /// many faces are damaging, how many are lava -- because the dangerous
    /// mistakes are the ones that produce a number rather than an error.
    ///
    /// Nothing is written. The map is built in memory, which is also what lets
    /// this run before a room exists and catch the limits the packer would
    /// otherwise throw on.
    /// </summary>
    public static class MapCheck
    {
        /// <summary>
        /// The margin the run-time point-on-face test allows, from
        /// `CollisionDetection.GetEdgeDotDifference`: a contact this far
        /// outside an edge still counts as on the face.
        /// </summary>
        private const float EdgeMargin = -0.03125f;

        /// <summary>The collision grid's step; the run-time lookup divides by four.</summary>
        private const float CellSize = 4f;

        /// <summary>How far apart to sample a drawn surface when asking
        /// whether anything solid is behind it. Half a unit finds a hole about
        /// a third of Samus's width, which is the size of the one df_dust2 has
        /// at its south-east corner.</summary>
        private const float CoverStep = 0.5f;

        /// <summary>
        /// How far behind a drawn surface its collision may sit and still be
        /// the collision for it, and how far in front.
        ///
        /// Not the same number, because they are not the same question. A
        /// converted level's collision is often a coarse version of what is
        /// drawn -- a `modelclip` ramp arrives as a staircase of flat plates,
        /// so the drawn slope runs up to a step's height above the plate
        /// holding it -- and none of that is a hole: you land on the step. A
        /// unit behind covers every such case in the two maps here. In front
        /// is only the slack a plate's own thickness needs.
        /// </summary>
        private const float CoverBehind = 1f;
        private const float CoverFront = 0.25f;

        public static int Run(string room)
        {
            MapDefinition? def = CustomRooms.Definitions.FirstOrDefault(
                d => d.Name.Equals(room, StringComparison.OrdinalIgnoreCase));
            if (def == null)
            {
                Console.WriteLine($"No custom map called {room}. {CustomRooms.MapDirectory} holds "
                    + $"{String.Join(", ", CustomRooms.Definitions.Select(d => d.Name))}.");
                return 1;
            }
            BuiltMap map;
            List<BuiltFace> geometry;
            try
            {
                map = def.Import == null ? MapBuilder.Build(def) : Q3Import.Build(def, verbose: false);
                geometry = new List<BuiltFace>(map.Solid);
                MapPacker.ApplyCollision(map, def, verbose: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{room} cannot be built at all: {ex.Message}");
                return 1;
            }
            Console.WriteLine($"{def.Name}: {map.Solid.Count} collision faces, {map.Faces.Count} drawn polygons"
                + (def.Collision == null ? "" : $", collision from {def.Collision.Source}"));
            int problems = 0;
            problems += Size(map);
            problems += Shape(map);
            Surfaces(map);
            if (def.Collision != null)
            {
                Difference(geometry, map.Solid);
            }
            problems += Cover(map);
            Console.WriteLine();
            Console.WriteLine(problems == 0
                ? "  Nothing to report."
                : $"  {problems} thing{(problems == 1 ? "" : "s")} to look at.");
            return 0;
        }

        /// <summary>
        /// Against the format's ceilings. The one that bites first is not the
        /// face count: it is the grid, which lists every face in every cell it
        /// reaches and indexes those listings with sixteen bits.
        /// </summary>
        private static int Size(BuiltMap map)
        {
            var points = new HashSet<Vector3>();
            int faces = 0;
            int fanned = 0;
            foreach (BuiltFace face in map.Solid)
            {
                foreach (Vector3 point in face.Points)
                {
                    points.Add(point);
                }
                if (face.Points.Length > 10)
                {
                    faces += face.Points.Length - 2;
                    fanned++;
                }
                else
                {
                    faces++;
                }
            }
            Bounds(map.Solid, out Vector3 low, out Vector3 high);
            int partsX = Math.Max(1, (int)MathF.Floor((high.X - low.X) / CellSize) + 1);
            int partsY = Math.Max(1, (int)MathF.Floor((high.Y - low.Y) / CellSize) + 1);
            int partsZ = Math.Max(1, (int)MathF.Floor((high.Z - low.Z) / CellSize) + 1);
            int references = 0;
            foreach (BuiltFace face in map.Solid)
            {
                Bounds(new[] { face }, out Vector3 faceLow, out Vector3 faceHigh);
                references += Span(faceLow.X, faceHigh.X, low.X, partsX)
                    * Span(faceLow.Y, faceHigh.Y, low.Y, partsY)
                    * Span(faceLow.Z, faceHigh.Z, low.Z, partsZ);
            }
            Console.WriteLine();
            Console.WriteLine("  against the format's limits");
            Console.WriteLine($"    faces written      {faces,8}"
                + (fanned > 0 ? $"   ({fanned} with more than ten points, split into triangles)" : ""));
            Console.WriteLine($"    distinct points    {points.Count,8}   of 65535  ({Percent(points.Count, 65535)})");
            Console.WriteLine($"    grid references    {references,8}   of 65535  ({Percent(references, 65535)})"
                + $"   in {partsX}x{partsY}x{partsZ} cells");
            int problems = 0;
            if (points.Count > 65535 || references > 65535)
            {
                Console.WriteLine("    This will not pack. Take collision out, or convert at a larger scale.");
                problems++;
            }
            return problems;
        }

        private static string Percent(int value, int of)
        {
            return $"{value * 100f / of:0.#}%";
        }

        private static int Span(float low, float high, float origin, int parts)
        {
            int first = Math.Clamp((int)((low - origin) / CellSize), 0, parts - 1);
            int last = Math.Clamp((int)((high - origin) / CellSize), 0, parts - 1);
            return last - first + 1;
        }

        /// <summary>
        /// Whether each face is a polygon the engine can use: it has to
        /// enclose an area, and it has to accept every point inside itself.
        ///
        /// The second is the run-time test and is the interesting one. A
        /// concave polygon -- which is what a 3D tool produces the moment
        /// somebody drags a vertex past its neighbours -- fails its own edge
        /// test over part of its area, so it is a surface that is there and
        /// does not stop you, in a shape nothing on screen explains.
        /// </summary>
        private static int Shape(BuiltMap map)
        {
            int degenerate = 0;
            var partial = new List<(BuiltFace Face, float Usable, float Area)>();
            foreach (BuiltFace face in map.Solid)
            {
                Vector3 twiceArea = CollisionObj.Newell(face.Points);
                if (twiceArea == Vector3.Zero)
                {
                    degenerate++;
                    continue;
                }
                float usable = Usable(face);
                if (usable < 0.999f)
                {
                    partial.Add((face, usable, Area(face)));
                }
            }
            Console.WriteLine();
            Console.WriteLine("  shape");
            Console.WriteLine($"    faces enclosing no area            {degenerate,6}"
                + (degenerate > 0 ? "   (nothing can touch one; they are dropped)" : ""));
            Console.WriteLine($"    faces that reject part of themselves {partial.Count,4}");
            if (partial.Count > 0)
            {
                Console.WriteLine("      A face is tested edge by edge at run time, so a polygon that bends"
                    + " back on itself blocks only part of its own area. Make them convex, or triangulate.");
                foreach ((BuiltFace face, float usable, float area) in partial
                    .OrderByDescending(p => p.Area * (1 - p.Usable)).Take(8))
                {
                    Vector3 centre = Centre(face);
                    Console.WriteLine($"      {area,8:0.00} u2  {usable * 100,5:0.0}% usable"
                        + $"  at ({centre.X:0.###}, {centre.Y:0.###}, {centre.Z:0.###})"
                        + $"  {face.Points.Length} points");
                }
            }
            return (partial.Count > 0 ? 1 : 0) + (degenerate > 0 ? 1 : 0);
        }

        /// <summary>
        /// How much of a face's own interior the engine's point-on-face test
        /// accepts, sampled over a fan of its corners. This is
        /// `GetEdgeDotDifference` with its own margin, not an approximation of
        /// it: the question is whether this polygon works, and the only
        /// authority on that is the code that will run.
        /// </summary>
        private static float Usable(BuiltFace face)
        {
            int inside = 0;
            int total = 0;
            Vector3[] points = face.Points;
            for (int i = 1; i < points.Length - 1; i++)
            {
                // Barycentric over each triangle of the fan, away from the
                // edges: a point exactly on an edge is the case the margin is
                // there for and says nothing about the polygon.
                for (int a = 1; a <= 6; a++)
                {
                    for (int b = 1; a + b <= 7; b++)
                    {
                        float u = a / 8f;
                        float v = b / 8f;
                        Vector3 sample = points[0] * (1 - u - v) + points[i] * u + points[i + 1] * v;
                        total++;
                        if (Accepts(face, sample))
                        {
                            inside++;
                        }
                    }
                }
            }
            return total == 0 ? 1f : inside / (float)total;
        }

        /// <summary>
        /// `CollisionDetection.GetEdgeDotDifference`, transcribed. Every edge
        /// is walked in the order the face states them, the cross of the edge
        /// direction with the plane normal says which side is in, and a point
        /// further than 1/32 outside any of them is off the face.
        /// </summary>
        private static bool Accepts(BuiltFace face, Vector3 point)
        {
            Vector3[] points = face.Points;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 first = points[i];
                Vector3 second = points[(i + 1) % points.Length];
                Vector3 edge = first - second;
                if (edge.LengthSquared < 1e-12f)
                {
                    continue;
                }
                Vector3 cross = Vector3.Cross(edge.Normalized(), face.Normal);
                if (Vector3.Dot(point, cross) - Vector3.Dot(cross, second) < EdgeMargin)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// What the surfaces are, which is the half of this report that is not
        /// about anything being wrong.
        ///
        /// Terrain and the flags are carried by a material name, and a
        /// material name is a string somebody typed. "lava" where "rock" was
        /// meant is not an error and never will be -- it is a floor that
        /// kills, generated without complaint -- so the count of faces that
        /// hurt goes on its own line whether or not it is zero.
        /// </summary>
        private static void Surfaces(BuiltMap map)
        {
            var terrain = new Dictionary<Terrain, int>();
            var slip = new Dictionary<int, int>();
            int damaging = 0, reflect = 0, noPlayers = 0, noBeams = 0, noScan = 0;
            float damagingArea = 0;
            foreach (BuiltFace face in map.Solid)
            {
                terrain.TryGetValue(face.Terrain, out int count);
                terrain[face.Terrain] = count + 1;
                slip.TryGetValue(face.Slipperiness, out int slipCount);
                slip[face.Slipperiness] = slipCount + 1;
                if (face.Damaging)
                {
                    damaging++;
                    damagingArea += Area(face);
                }
                if (face.ReflectBeams)
                {
                    reflect++;
                }
                if (face.IgnorePlayers)
                {
                    noPlayers++;
                }
                if (face.IgnoreBeams)
                {
                    noBeams++;
                }
                if (face.IgnoreScan)
                {
                    noScan++;
                }
            }
            Console.WriteLine();
            Console.WriteLine("  what the surfaces are");
            foreach ((Terrain type, int count) in terrain.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"    {type.ToString().ToLowerInvariant(),-12} {count,6}");
            }
            if (slip.Count > 1 || !slip.ContainsKey(0))
            {
                Console.WriteLine("    slipperiness  "
                    + String.Join(", ", slip.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}")));
            }
            int lava = terrain.GetValueOrDefault(Terrain.Lava);
            int acid = terrain.GetValueOrDefault(Terrain.Acid);
            Console.WriteLine($"    faces that hurt: {damaging} damaging ({damagingArea:0.#} u2),"
                + $" {lava} lava, {acid} acid"
                + (damaging + lava + acid == 0 ? "" : "  <- check this is deliberate"));
            if (reflect + noPlayers + noBeams + noScan > 0)
            {
                Console.WriteLine($"    reflect {reflect}, ignore players {noPlayers},"
                    + $" ignore beams {noBeams}, ignore scan {noScan}");
            }
        }

        /// <summary>
        /// What the .obj changed, against the collision the geometry would
        /// have made.
        ///
        /// The most useful line in the report once a map has a hand-edited
        /// mesh, because the edit is not visible anywhere else: the recipe
        /// says a filename, the .obj is thousands of numbers, and a face
        /// deleted on purpose and a face deleted by a stray click look exactly
        /// alike. This says how many went and how much of the room they were.
        /// </summary>
        private static void Difference(IReadOnlyList<BuiltFace> geometry, IReadOnlyList<BuiltFace> edited)
        {
            // Both sides as the packer will write them. A face of more than
            // ten points is split into a fan on the way out, so an .obj
            // exported from a generated room holds the triangles while the
            // geometry still holds the polygon, and comparing the two
            // unsplit reports every one of them as deleted and re-added.
            var before = new Dictionary<string, BuiltFace>();
            foreach (BuiltFace face in geometry.SelectMany(Split))
            {
                before[Shape(face)] = face;
            }
            var after = new HashSet<string>();
            int added = 0;
            foreach (BuiltFace face in edited.SelectMany(Split))
            {
                string key = Shape(face);
                after.Add(key);
                if (!before.ContainsKey(key))
                {
                    added++;
                }
            }
            var removed = before.Where(p => !after.Contains(p.Key)).Select(p => p.Value).ToList();
            float removedArea = removed.Sum(Area);
            float total = before.Values.Sum(Area);
            int kept = before.Count - removed.Count;
            Console.WriteLine();
            Console.WriteLine("  what the .obj changed, against the collision the geometry makes");
            Console.WriteLine($"    kept    {kept,6} faces");
            Console.WriteLine($"    removed {removed.Count,6} faces  ({removedArea:0.#} u2,"
                + $" {(total > 0 ? removedArea / total * 100 : 0):0.#}% of the room's collision area)");
            Console.WriteLine($"    added   {added,6} faces");
            foreach (BuiltFace face in removed.OrderByDescending(Area).Take(6))
            {
                Vector3 centre = Centre(face);
                Console.WriteLine($"      {Area(face),9:0.00} u2 gone from"
                    + $" ({centre.X:0.###}, {centre.Y:0.###}, {centre.Z:0.###})");
            }
        }

        /// <summary>
        /// A face's corners, as fixed point and in a fixed order, so that two
        /// faces can be recognised as the same one.
        ///
        /// Fixed point because that is what gets written: the geometry's own
        /// collision is still in floats at this stage while an .obj read back
        /// has already been snapped, and two values that land on the same
        /// 1/4096 are the same corner. `Fixed.ToInt` -- which is what writes
        /// the file -- truncates rather than rounds, so this does too, or
        /// every corner of every face reads as moved. In a fixed order because
        /// an .obj may state a face from a different corner round and still be
        /// the face that was exported.
        /// </summary>
        /// <summary>
        /// A face as the packer will write it: itself, or the fan of triangles
        /// it becomes when it has more points than the format's ten. The same
        /// split `MapPacker.BuildCollision` makes.
        /// </summary>
        private static IEnumerable<BuiltFace> Split(BuiltFace face)
        {
            if (face.Points.Length <= 10)
            {
                yield return face;
                yield break;
            }
            for (int i = 1; i < face.Points.Length - 1; i++)
            {
                yield return new BuiltFace(
                    new[] { face.Points[0], face.Points[i], face.Points[i + 1] },
                    new Vector2[3], face.Normal, face.Material, face.Shade);
            }
        }

        private static string Shape(BuiltFace face)
        {
            return String.Join(";", face.Points
                .Select(p => $"{Fixed.ToInt(p.X)},{Fixed.ToInt(p.Y)},{Fixed.ToInt(p.Z)}")
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// Every drawn surface with nothing solid behind it: the holes.
        ///
        /// Sampled on a grid over each drawn polygon rather than at its
        /// centre, because the hole that matters is rarely a whole surface --
        /// df_dust2's is a triangle of about one square unit in the corner of
        /// a floor that is otherwise covered, and a centre sample walks
        /// straight past it.
        ///
        /// The sky is skipped: it is drawn and is deliberately never
        /// collision.
        /// </summary>
        private static int Cover(BuiltMap map)
        {
            Bounds(map.Solid, out Vector3 low, out Vector3 high);
            var grid = new Dictionary<(int, int, int), List<BuiltFace>>();
            foreach (BuiltFace face in map.Solid)
            {
                Bounds(new[] { face }, out Vector3 faceLow, out Vector3 faceHigh);
                foreach ((int, int, int) cell in Cells(faceLow, faceHigh, low))
                {
                    if (!grid.TryGetValue(cell, out List<BuiltFace>? list))
                    {
                        grid[cell] = list = new List<BuiltFace>();
                    }
                    list.Add(face);
                }
            }
            int samples = 0;
            int uncovered = 0;
            var worst = new Dictionary<(int, int, int), (int Count, Vector3 Where)>();
            foreach (BuiltFace face in map.Faces)
            {
                if (face.Sky)
                {
                    continue;
                }
                foreach (Vector3 point in Samples(face))
                {
                    samples++;
                    if (Covered(grid, low, point, face.Normal))
                    {
                        continue;
                    }
                    uncovered++;
                    (int, int, int) cell = Cell(point, low);
                    worst.TryGetValue(cell, out (int Count, Vector3 Where) had);
                    worst[cell] = (had.Count + 1, had.Count == 0 ? point : had.Where);
                }
            }
            Console.WriteLine();
            Console.WriteLine("  drawn surfaces with nothing solid behind them");
            Console.WriteLine("    Read this as a list of places to look at, not as a list of faults: a"
                + " level draws plenty");
            Console.WriteLine("    that was never meant to stop anybody -- trim, decoration, and every"
                + " brush its own author made non-solid.");
            Console.WriteLine($"    {uncovered} of {samples} samples uncovered"
                + $"  (about {uncovered * CoverStep * CoverStep:0.#} u2, in {worst.Count} places,"
                + $" sampled {CoverStep} units apart)");
            foreach (((int, int, int) _, (int count, Vector3 where)) in worst
                .OrderByDescending(p => p.Value.Count).Take(8))
            {
                Console.WriteLine($"      {count,5} samples around ({where.X:0.###}, {where.Y:0.###}, {where.Z:0.###})");
            }
            return 0;
        }

        private static bool Covered(Dictionary<(int, int, int), List<BuiltFace>> grid,
            Vector3 low, Vector3 point, Vector3 normal)
        {
            if (!grid.TryGetValue(Cell(point, low), out List<BuiltFace>? candidates))
            {
                return false;
            }
            foreach (BuiltFace solid in candidates)
            {
                // Facing roughly the same way: the far side of the wall behind
                // this one is not what stops you from walking into this one.
                if (Vector3.Dot(solid.Normal, normal) < 0.5f)
                {
                    continue;
                }
                float distance = Vector3.Dot(solid.Normal, point) - Vector3.Dot(solid.Normal, solid.Points[0]);
                if (distance > CoverBehind || distance < -CoverFront)
                {
                    continue;
                }
                if (Accepts(solid, point - solid.Normal * distance))
                {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<Vector3> Samples(BuiltFace face)
        {
            Vector3[] points = face.Points;
            for (int i = 1; i < points.Length - 1; i++)
            {
                Vector3 a = points[0];
                Vector3 b = points[i];
                Vector3 c = points[i + 1];
                // A grid over the triangle in its own coordinates, dense
                // enough that the smallest hole worth finding gets a sample.
                float longest = MathF.Max((b - a).Length, MathF.Max((c - b).Length, (a - c).Length));
                int steps = Math.Clamp((int)MathF.Ceiling(longest / CoverStep), 1, 64);
                for (int u = 0; u <= steps; u++)
                {
                    for (int v = 0; u + v <= steps; v++)
                    {
                        float fu = u / (float)steps;
                        float fv = v / (float)steps;
                        yield return a * (1 - fu - fv) + b * fu + c * fv;
                    }
                }
            }
        }

        private static IEnumerable<(int, int, int)> Cells(Vector3 low, Vector3 high, Vector3 origin)
        {
            (int x0, int y0, int z0) = Cell(low, origin);
            (int x1, int y1, int z1) = Cell(high, origin);
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        yield return (x, y, z);
                    }
                }
            }
        }

        private static (int, int, int) Cell(Vector3 point, Vector3 origin)
        {
            return ((int)MathF.Floor((point.X - origin.X) / CellSize),
                (int)MathF.Floor((point.Y - origin.Y) / CellSize),
                (int)MathF.Floor((point.Z - origin.Z) / CellSize));
        }

        private static void Bounds(IReadOnlyList<BuiltFace> faces, out Vector3 low, out Vector3 high)
        {
            low = new Vector3(Single.MaxValue);
            high = new Vector3(Single.MinValue);
            foreach (BuiltFace face in faces)
            {
                foreach (Vector3 point in face.Points)
                {
                    low = Vector3.ComponentMin(low, point);
                    high = Vector3.ComponentMax(high, point);
                }
            }
        }

        private static Vector3 Centre(BuiltFace face)
        {
            var total = Vector3.Zero;
            foreach (Vector3 point in face.Points)
            {
                total += point;
            }
            return total / face.Points.Length;
        }

        private static float Area(BuiltFace face)
        {
            return CollisionObj.TwiceArea(face.Points).Length / 2;
        }
    }
}
