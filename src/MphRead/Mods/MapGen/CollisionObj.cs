using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// A room's collision read back from a Wavefront OBJ, so that what stops a
    /// player is something a person can edit.
    ///
    /// The importer's collision is a by-product of somebody else's level:
    /// every brush side survives the conversion, including the ones outside
    /// the part of the map anyone can reach -- the outer skin of the sky
    /// shell, the underside of every floor slab -- because the buried-face
    /// test only drops a side that is inside another brush, not one that is
    /// merely somewhere nobody can go. On df_dust2 that is 372 faces and
    /// 20,359 square units: about a quarter of the room's collision area and a
    /// seventh of its grid references, none of which can ever be touched.
    ///
    /// None of that is a bug an importer can fix, because the faces really are
    /// in the source. Which ones are worth keeping is a judgement about the
    /// map, so this is the other half of `tools/collision-to-obj.py`: export
    /// the collision, delete and patch it in a 3D tool, and name the result
    /// here.
    ///
    /// **Winding is the whole contract.** A collision face is one-sided --
    /// `CheckSphereBetweenPoints` refuses a contact that starts behind the
    /// plane -- so the polygon's own normal is the side it blocks from, and a
    /// face flipped in Blender is a face you walk through. The exporter winds
    /// every face to agree with its stored plane and this derives the plane
    /// from the winding, by the same Newell sum, so a round trip is exact.
    /// </summary>
    public static class CollisionObj
    {
        /// <summary>
        /// Fixed point: the file stores world units times 4096, so a
        /// coordinate is snapped on the way in. Without it a number that has
        /// been through decimal text lands a fraction off the one beside it,
        /// and the packer -- which deduplicates points by exact equality --
        /// writes two points where the file had one, and two edges where the
        /// mesh had a shared one.
        /// </summary>
        private const float FixedOne = 4096f;

        public sealed class Result
        {
            public List<BuiltFace> Faces { get; } = new List<BuiltFace>();
            /// <summary>Faces that enclose no area: nothing can collide with one.</summary>
            public int Degenerate { get; set; }
            public int Vertices { get; set; }
            /// <summary>How many faces each material name claimed, for the report.</summary>
            public Dictionary<string, int> Materials { get; } = new Dictionary<string, int>();
        }

        public static Result Read(string path, bool zUp)
        {
            using FileStream stream = File.OpenRead(path);
            return Read(stream, Path.GetFileName(path), zUp);
        }

        public static Result Read(byte[] bytes, string name, bool zUp)
        {
            using var stream = new MemoryStream(bytes);
            return Read(stream, name, zUp);
        }

        public static Result Read(Stream stream, string name, bool zUp)
        {
            var points = new List<Vector3>();
            var result = new Result();
            // No material named yet means the file never names one, which is
            // an ordinary OBJ out of a tool nobody asked to write them. Plain
            // solid ground is the right reading of that.
            var surface = new Surface();
            string materialName = "(none)";
            using var reader = new StreamReader(stream);
            int line = 0;
            string? text;
            while ((text = reader.ReadLine()) != null)
            {
                line++;
                string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0].StartsWith('#'))
                {
                    continue;
                }
                switch (parts[0])
                {
                    case "v":
                        points.Add(Snap(Place(Vertex(parts, name, line), zUp)));
                        break;
                    case "usemtl":
                        materialName = parts.Length > 1 ? parts[1] : "(none)";
                        surface = Surface.Parse(materialName, name, line);
                        break;
                    case "f":
                        AddFace(result, points, parts, surface, materialName, name, line);
                        break;
                }
            }
            result.Vertices = points.Count;
            if (result.Faces.Count == 0)
            {
                throw new ProgramException(
                    $"{name} has no usable collision faces in it. A face needs three points that "
                    + "are not all in one line; check that the mesh was exported as faces rather "
                    + "than as loose vertices or edges.");
            }
            return result;
        }

        /// <summary>
        /// Z up is the exporter's `--zup` undone: it writes (x, -z, y), so
        /// this reads (X, Z, -Y) back. A rotation either way, which is what
        /// keeps the handedness and with it every face's facing.
        /// </summary>
        private static Vector3 Place(Vector3 point, bool zUp)
        {
            return zUp ? new Vector3(point.X, point.Z, -point.Y) : point;
        }

        private static Vector3 Snap(Vector3 point)
        {
            return new Vector3(
                MathF.Round(point.X * FixedOne) / FixedOne,
                MathF.Round(point.Y * FixedOne) / FixedOne,
                MathF.Round(point.Z * FixedOne) / FixedOne);
        }

        private static Vector3 Vertex(string[] parts, string name, int line)
        {
            if (parts.Length < 4)
            {
                throw new ProgramException($"{name} line {line}: a vertex needs three numbers.");
            }
            return new Vector3(Number(parts[1], name, line), Number(parts[2], name, line),
                Number(parts[3], name, line));
        }

        private static float Number(string text, string name, int line)
        {
            if (!Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                throw new ProgramException($"{name} line {line}: {text} is not a number.");
            }
            return value;
        }

        private static void AddFace(Result result, List<Vector3> points, string[] parts,
            Surface surface, string materialName, string name, int line)
        {
            var corners = new List<Vector3>();
            for (int i = 1; i < parts.Length; i++)
            {
                // "index", "index/texture", "index//normal" and
                // "index/texture/normal" all name the same vertex; only the
                // first field is a position, and the normal is not read at all
                // -- the winding is what says which way a face points, and a
                // tool that writes a normal disagreeing with its own winding
                // would otherwise decide which side of a wall you can walk
                // through.
                string field = parts[i];
                int slash = field.IndexOf('/');
                if (slash >= 0)
                {
                    field = field[..slash];
                }
                if (!Int32.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    || index == 0)
                {
                    throw new ProgramException($"{name} line {line}: {parts[i]} is not a vertex reference.");
                }
                // OBJ counts from one, and a negative index counts back from
                // the newest vertex.
                int resolved = index > 0 ? index - 1 : points.Count + index;
                if (resolved < 0 || resolved >= points.Count)
                {
                    throw new ProgramException(
                        $"{name} line {line}: vertex {index} is not one of the {points.Count} "
                        + "declared before it.");
                }
                Vector3 point = points[resolved];
                // A polygon that states the same point twice running has an
                // edge of no length, and the run-time edge test divides by one.
                if (corners.Count == 0 || corners[^1] != point)
                {
                    corners.Add(point);
                }
            }
            if (corners.Count > 2 && corners[0] == corners[^1])
            {
                corners.RemoveAt(corners.Count - 1);
            }
            if (corners.Count < 3)
            {
                result.Degenerate++;
                return;
            }
            Vector3 normal = Newell(corners);
            if (normal == Vector3.Zero)
            {
                // Three or more points in a straight line. It encloses nothing,
                // so it has no plane of its own, and a plane is what a
                // collision face is.
                result.Degenerate++;
                return;
            }
            result.Faces.Add(new BuiltFace(corners.ToArray(), new Vector2[corners.Count],
                normal, 0, 1f)
            {
                Terrain = surface.Terrain,
                Damaging = surface.Damaging,
                Slipperiness = surface.Slipperiness,
                ReflectBeams = surface.ReflectBeams,
                IgnorePlayers = surface.IgnorePlayers,
                IgnoreBeams = surface.IgnoreBeams,
                IgnoreScan = surface.IgnoreScan
            });
            result.Materials.TryGetValue(materialName, out int count);
            result.Materials[materialName] = count + 1;
        }

        /// <summary>
        /// The polygon's own unit normal, or zero when it encloses no area.
        ///
        /// Newell rather than one cross product because a face may have up to
        /// ten points and is not required to be perfectly flat -- and because
        /// `tools/collision-to-obj.py` decides its winding with the same sum,
        /// which is what makes a round trip agree rather than nearly agree.
        ///
        /// **In double, and about the polygon's own centre.** The sum is a
        /// difference of products of coordinates, so on a wall fifty units
        /// from the origin whose own edges are a fraction of a unit long,
        /// single precision cancels most of the answer away: a face that
        /// should have come out (1, 0, 0) came out (0.9998, 0, 0), which is a
        /// different plane, and the packer then stores two planes where the
        /// room has one. Subtracting the centre first makes the arithmetic
        /// about the face's own size instead of about where it is.
        /// </summary>
        public static Vector3 Newell(IReadOnlyList<Vector3> points)
        {
            (double nx, double ny, double nz) = Sum(points);
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            // Twice the area. The same threshold tools/collision-to-obj.py
            // drops a face at, so the two agree on how many a room has that
            // enclose nothing -- a number that appears in both reports and
            // would otherwise be two different numbers for one thing.
            if (length < 1e-6)
            {
                return Vector3.Zero;
            }
            nx /= length;
            ny /= length;
            nz /= length;
            // An axis-aligned face is most of a room, and its other two
            // components are rounding noise rather than a slope. Left in, a
            // wall that is flat to a millionth is a plane of its own and no
            // other face shares it.
            double largest = Math.Max(Math.Abs(nx), Math.Max(Math.Abs(ny), Math.Abs(nz)));
            double floor = largest * 1e-6;
            nx = Math.Abs(nx) < floor ? 0 : nx;
            ny = Math.Abs(ny) < floor ? 0 : ny;
            nz = Math.Abs(nz) < floor ? 0 : nz;
            length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
        }

        /// <summary>The same sum left unnormalised, whose length is twice the
        /// polygon's area.</summary>
        public static Vector3 TwiceArea(IReadOnlyList<Vector3> points)
        {
            (double nx, double ny, double nz) = Sum(points);
            return new Vector3((float)nx, (float)ny, (float)nz);
        }

        private static (double, double, double) Sum(IReadOnlyList<Vector3> points)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (Vector3 point in points)
            {
                cx += point.X;
                cy += point.Y;
                cz += point.Z;
            }
            cx /= points.Count;
            cy /= points.Count;
            cz /= points.Count;
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 first = points[i];
                Vector3 second = points[(i + 1) % points.Count];
                double ax = first.X - cx, ay = first.Y - cy, az = first.Z - cz;
                double bx = second.X - cx, by = second.Y - cy, bz = second.Z - cz;
                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
            }
            return (nx, ny, nz);
        }

        /// <summary>Every terrain a face may be. `All` is the debug viewer's
        /// "show me all of them" and is not one a surface can have.</summary>
        public static readonly IReadOnlyList<Terrain> Terrains = new[]
        {
            Terrain.Metal, Terrain.OrangeHolo, Terrain.GreenHolo, Terrain.BlueHolo,
            Terrain.Ice, Terrain.Snow, Terrain.Sand, Terrain.Rock,
            Terrain.Lava, Terrain.Acid, Terrain.Gorea
        };

        public static readonly IReadOnlyList<string> Attributes = new[]
        {
            "slip0", "slip1", "slip2", "slip3",
            "damaging", "reflect", "noplayers", "nobeams", "noscan"
        };

        /// <summary>The material name a face with these properties would carry.</summary>
        public static string MaterialName(BuiltFace face)
        {
            string name = face.Terrain.ToString().ToLowerInvariant();
            if (face.Slipperiness != 0)
            {
                name += $"_slip{face.Slipperiness}";
            }
            if (face.Damaging)
            {
                name += "_damaging";
            }
            if (face.ReflectBeams)
            {
                name += "_reflect";
            }
            if (face.IgnorePlayers)
            {
                name += "_noplayers";
            }
            if (face.IgnoreBeams)
            {
                name += "_nobeams";
            }
            if (face.IgnoreScan)
            {
                name += "_noscan";
            }
            return name;
        }

        /// <summary>
        /// What a material name says about a surface.
        ///
        /// `&lt;terrain&gt;` followed by any number of `_attribute`s, which is
        /// the most an OBJ can carry: it has one name per face and no room for
        /// a table of its own. Everything the collision format holds per face
        /// fits in it -- the terrain, how slippery it is, and the five flags --
        /// so nothing is lost on the way through a 3D tool, and the whole
        /// vocabulary is visible in Blender's material list.
        /// </summary>
        private readonly struct Surface
        {
            public Terrain Terrain { get; init; }
            public int Slipperiness { get; init; }
            public bool Damaging { get; init; }
            public bool ReflectBeams { get; init; }
            public bool IgnorePlayers { get; init; }
            public bool IgnoreBeams { get; init; }
            public bool IgnoreScan { get; init; }

            public static Surface Parse(string material, string file, int line)
            {
                // Blender numbers a material it had to rename, and the
                // exporter writes that number out: "sand.001" is the same sand.
                string name = material;
                int dot = name.LastIndexOf('.');
                if (dot > 0 && name.Length - dot == 4 && name[(dot + 1)..].All(Char.IsAsciiDigit))
                {
                    name = name[..dot];
                }
                string[] words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || name.Equals("(none)", StringComparison.Ordinal))
                {
                    return new Surface();
                }
                foreach (Terrain candidate in Terrains)
                {
                    if (candidate.ToString().Equals(words[0], StringComparison.OrdinalIgnoreCase))
                    {
                        var surface = new Surface() { Terrain = candidate };
                        for (int i = 1; i < words.Length; i++)
                        {
                            surface = Apply(surface, words[i], material, file, line);
                        }
                        return surface;
                    }
                }
                throw new ProgramException(
                    $"{file} line {line}: \"{material}\" does not begin with a terrain. A material "
                    + "is <terrain>[_attribute...]; the terrains are "
                    + $"{String.Join(", ", Terrains.Select(t => t.ToString().ToLowerInvariant()))}. "
                    + "A face with no material at all is plain metal, so naming none is also an "
                    + "answer -- but a name that is not one of these is a typo, and a typo on a "
                    + "lava face is a floor that kills.");
            }

            private static Surface Apply(Surface surface, string word, string material, string file, int line)
            {
                return word.ToLowerInvariant() switch
                {
                    "slip0" => surface with { Slipperiness = 0 },
                    "slip1" => surface with { Slipperiness = 1 },
                    "slip2" => surface with { Slipperiness = 2 },
                    "slip3" => surface with { Slipperiness = 3 },
                    "damaging" => surface with { Damaging = true },
                    "reflect" => surface with { ReflectBeams = true },
                    "noplayers" => surface with { IgnorePlayers = true },
                    "nobeams" => surface with { IgnoreBeams = true },
                    "noscan" => surface with { IgnoreScan = true },
                    _ => throw new ProgramException(
                        $"{file} line {line}: \"{material}\" has no attribute \"{word}\". The "
                        + $"attributes are {String.Join(", ", Attributes)}. This is refused rather "
                        + "than ignored on purpose: a misspelt \"damaging\" would silently be an "
                        + "ordinary floor, and a misspelt anything on a lava face is a floor that "
                        + "kills without saying so.")
                };
            }
        }
    }
}
