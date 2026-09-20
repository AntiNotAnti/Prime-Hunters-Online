using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Works out where a bot can stand and how it gets from one place to
    /// another, and writes that as the node data the game's own AI reads.
    ///
    /// Without it a custom room has `nodePath` null, and the difference is not
    /// subtle: a bot with no node data never moves, never changes form and
    /// never fires. The same eight bots in a cartridge room fight. Everything
    /// else about a custom map worked long before this did, which is why it
    /// looked like a map problem rather than a missing file.
    ///
    /// Two lists per node, because the AI reads two. The neighbours are what
    /// it walks; the other is a routing table -- for every destination in the
    /// room, which neighbour to set off towards -- run-length encoded, since
    /// whole ranges of destinations lie the same way. That is the game's own
    /// format, not an invention here: the code that reads it walks pairs of
    /// (how many destinations this covers, which node to head for).
    /// </summary>
    public static class MapNodePacker
    {
        /// <summary>
        /// Distance between candidate standing places while working out what
        /// connects to what. Fine on purpose: a doorway in a real level is
        /// about two units wide once converted, so a grid any coarser steps
        /// straight over every one of them and produces a room of separate
        /// islands that no bot can leave.
        /// </summary>
        private const float FineSpacing = 2f;

        /// <summary>
        /// Distance between the waypoints actually written. The routing table
        /// is a row per node over every node, so the cost is quadratic and the
        /// fine grid is far too dense to ship; these are chosen from it, which
        /// is what lets them be sparse without being disconnected.
        /// </summary>
        private const float Spacing = 6f;
        /// <summary>Headroom a hunter needs to stand up.</summary>
        private const float Headroom = 1.9f;
        /// <summary>What a walk can climb, and what a fall can drop.</summary>
        private const float StepUp = 1.2f;
        private const float StepDown = 8f;
        /// <summary>Cosine of the steepest slope that counts as floor.</summary>
        private const float WalkNormal = 0.7f;
        /// <summary>
        /// The AI walks a node's routes into a buffer of 20 and stops at 19,
        /// so that is the ceiling; 16 leaves room and is more than a waypoint
        /// in a real room ever needs.
        /// </summary>
        private const int MaxNeighbours = 16;
        /// <summary>
        /// How close counts as standing on a node. Not the spacing: the game's
        /// own rooms put this between 0.6 and 2 units whatever their nodes are
        /// spaced at, and a bot that thinks it has arrived from five units away
        /// stops short of every corner.
        /// </summary>
        private const float Reach = 1.6f;
        /// <summary>
        /// The routing table is a row per node over every node, so its cost is
        /// quadratic. Past this the spacing is widened instead -- a bot that
        /// navigates a room on 700 waypoints is not improved by 3000.
        /// </summary>
        private const int MaxNodes = 700;

        private sealed class Node
        {
            public Vector3 Position;
            public int Cell;
            public readonly List<int> Neighbours = new List<int>();
        }

        public static (byte[] Bytes, int Nodes, int Edges) Pack(IReadOnlyList<BuiltFace> solid)
        {
            List<Node> fine = Sample(solid, FineSpacing);
            Connect(solid, fine, FineSpacing);
            if (Environment.GetEnvironmentVariable("FP_NODEDEBUG") != null)
            {
                Console.WriteLine($"  [nodes] fine {fine.Count} nodes,"
                    + $" {fine.Sum(n => n.Neighbours.Count) / 2} edges,"
                    + $" largest component {Largest(fine)}");
            }
            float spacing = Spacing;
            List<Node> nodes;
            while (true)
            {
                nodes = Decimate(fine, (int)MathF.Round(spacing / FineSpacing));
                if (nodes.Count <= MaxNodes || spacing > 64)
                {
                    break;
                }
                spacing *= 1.4f;
            }
            int edges = nodes.Sum(n => n.Neighbours.Count) / 2;
            if (Environment.GetEnvironmentVariable("FP_NODEDEBUG") != null)
            {
                Console.WriteLine($"  [nodes] coarse {nodes.Count} nodes, {edges} edges,"
                    + $" largest component {Largest(nodes)},"
                    + $" degree max {nodes.Max(n => n.Neighbours.Count)},"
                    + $" isolated {nodes.Count(n => n.Neighbours.Count == 0)}");
            }
            return (Write(nodes, spacing), nodes.Count, edges);
        }

        private static int Largest(List<Node> nodes)
        {
            var seen = new bool[nodes.Count];
            int best = 0;
            var queue = new Queue<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (seen[i])
                {
                    continue;
                }
                int size = 0;
                seen[i] = true;
                queue.Clear();
                queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    size++;
                    foreach (int neighbour in nodes[current].Neighbours)
                    {
                        if (!seen[neighbour])
                        {
                            seen[neighbour] = true;
                            queue.Enqueue(neighbour);
                        }
                    }
                }
                best = Math.Max(best, size);
            }
            return best;
        }

        /// <summary>
        /// Thins the fine grid down to the waypoints that are written, keeping
        /// the connectivity it found.
        ///
        /// The thinning is done over the graph and not over the room: two
        /// points either side of a wall are close together and not connected,
        /// and picking by distance alone drops one of them and loses whatever
        /// was behind it. Growing each waypoint outward along the edges
        /// instead means every corner of the fine grid belongs to one, and two
        /// waypoints are neighbours exactly when some fine step crosses
        /// between them -- which is to say, when you can walk it.
        /// </summary>
        private static List<Node> Decimate(List<Node> fine, int radius)
        {
            radius = Math.Max(1, radius);
            var owner = new int[fine.Count];
            Array.Fill(owner, -1);
            var seeds = new List<int>();
            var queue = new Queue<(int Node, int Depth)>();
            for (int i = 0; i < fine.Count; i++)
            {
                if (owner[i] != -1)
                {
                    continue;
                }
                int index = seeds.Count;
                seeds.Add(i);
                owner[i] = index;
                queue.Clear();
                queue.Enqueue((i, 0));
                while (queue.Count > 0)
                {
                    (int current, int depth) = queue.Dequeue();
                    if (depth == radius)
                    {
                        continue;
                    }
                    foreach (int neighbour in fine[current].Neighbours)
                    {
                        if (owner[neighbour] == -1)
                        {
                            owner[neighbour] = index;
                            queue.Enqueue((neighbour, depth + 1));
                        }
                    }
                }
            }
            var nodes = new List<Node>(seeds.Count);
            foreach (int seed in seeds)
            {
                nodes.Add(new Node() { Position = fine[seed].Position, Cell = fine[seed].Cell });
            }
            for (int i = 0; i < fine.Count; i++)
            {
                foreach (int j in fine[i].Neighbours)
                {
                    int a = owner[i];
                    int b = owner[j];
                    if (a == b || a < 0 || b < 0)
                    {
                        continue;
                    }
                    if (nodes[a].Neighbours.Count < MaxNeighbours && !nodes[a].Neighbours.Contains(b))
                    {
                        nodes[a].Neighbours.Add(b);
                    }
                    if (nodes[b].Neighbours.Count < MaxNeighbours && !nodes[b].Neighbours.Contains(a))
                    {
                        nodes[b].Neighbours.Add(a);
                    }
                }
            }
            return nodes;
        }

        /// <summary>
        /// Every place a hunter could stand: the floor under each point of a
        /// grid over the room, at every height where there is one, with room
        /// above it.
        /// </summary>
        private static List<Node> Sample(IReadOnlyList<BuiltFace> solid, float spacing)
        {
            Bounds(solid, out Vector3 min, out Vector3 max);
            var buckets = Buckets(solid, min, spacing, out int columns, out int rows);
            var nodes = new List<Node>();
            var heights = new List<float>();
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    float x = min.X + (column + 0.5f) * spacing;
                    float z = min.Z + (row + 0.5f) * spacing;
                    heights.Clear();
                    List<int>? bucket = buckets[row * columns + column];
                    if (bucket == null)
                    {
                        continue;
                    }
                    foreach (int index in bucket)
                    {
                        BuiltFace face = solid[index];
                        if (face.Normal.Y < WalkNormal || !Contains(face, x, z, out float y))
                        {
                            continue;
                        }
                        heights.Add(y);
                    }
                    heights.Sort();
                    float last = Single.MinValue;
                    foreach (float y in heights)
                    {
                        // one node per storey: a floor and the thing it rests
                        // on are not two places to stand
                        if (y - last < Headroom)
                        {
                            last = y;
                            continue;
                        }
                        last = y;
                        if (Blocked(solid, bucket, x, z, y))
                        {
                            continue;
                        }
                        nodes.Add(new Node()
                        {
                            Position = new Vector3(x, y + 0.5f, z),
                            Cell = row * columns + column
                        });
                    }
                }
            }
            return nodes;
        }

        /// <summary>Is there something in the way of standing up here?</summary>
        private static bool Blocked(IReadOnlyList<BuiltFace> solid, List<int> bucket, float x, float z, float y)
        {
            foreach (int index in bucket)
            {
                BuiltFace face = solid[index];
                if (face.Normal.Y > -0.3f)
                {
                    continue;
                }
                if (Contains(face, x, z, out float ceiling) && ceiling > y + 0.2f && ceiling < y + Headroom)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Connect(IReadOnlyList<BuiltFace> solid, List<Node> nodes, float spacing)
        {
            Bounds(solid, out Vector3 min, out Vector3 max);
            var buckets = Buckets(solid, min, spacing, out int columns, out int rows);
            var byCell = new Dictionary<int, List<int>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!byCell.TryGetValue(nodes[i].Cell, out List<int>? list))
                {
                    list = new List<int>();
                    byCell.Add(nodes[i].Cell, list);
                }
                list.Add(i);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                Node node = nodes[i];
                int column = node.Cell % columns;
                int row = node.Cell / columns;
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0)
                        {
                            continue;
                        }
                        int nc = column + dc;
                        int nr = row + dr;
                        if (nc < 0 || nr < 0 || nc >= columns || nr >= rows)
                        {
                            continue;
                        }
                        if (!byCell.TryGetValue(nr * columns + nc, out List<int>? candidates))
                        {
                            continue;
                        }
                        foreach (int j in candidates)
                        {
                            if (j == i || node.Neighbours.Count >= MaxNeighbours || node.Neighbours.Contains(j))
                            {
                                continue;
                            }
                            float rise = nodes[j].Position.Y - node.Position.Y;
                            if (rise > StepUp || rise < -StepDown)
                            {
                                continue;
                            }
                            if (Wall(solid, buckets, columns, rows, min, spacing, node.Position, nodes[j].Position))
                            {
                                continue;
                            }
                            node.Neighbours.Add(j);
                            if (nodes[j].Neighbours.Count < MaxNeighbours && !nodes[j].Neighbours.Contains(i))
                            {
                                nodes[j].Neighbours.Add(i);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Is there a wall between these two places? Every near-vertical
        /// surface nearby is a plane; if the line between the two crosses one
        /// inside the surface's own extent, at a height a hunter occupies,
        /// they are not neighbours however close together they look.
        /// </summary>
        private static bool Wall(IReadOnlyList<BuiltFace> solid, List<int>?[] buckets, int columns, int rows,
            Vector3 min, float spacing, Vector3 from, Vector3 to)
        {
            float low = MathF.Min(from.Y, to.Y) + 0.3f;
            float high = MathF.Max(from.Y, to.Y) + Headroom;
            var seen = new HashSet<int>();
            foreach (int cell in Cells(from, to, min, spacing, columns, rows))
            {
                List<int>? bucket = buckets[cell];
                if (bucket == null)
                {
                    continue;
                }
                foreach (int index in bucket)
                {
                    if (!seen.Add(index))
                    {
                        continue;
                    }
                    BuiltFace face = solid[index];
                    if (MathF.Abs(face.Normal.Y) > 0.5f)
                    {
                        continue;
                    }
                    float d = Vector3.Dot(face.Normal, face.Points[0]);
                    float start = Vector3.Dot(face.Normal, from) - d;
                    float end = Vector3.Dot(face.Normal, to) - d;
                    if (start > 0 == end > 0 || MathF.Abs(start - end) < 0.0001f)
                    {
                        continue;
                    }
                    Vector3 crossing = from + (to - from) * (start / (start - end));
                    if (Extent(face, crossing, low, high))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Does the surface actually reach the point the line crossed its plane at?</summary>
        private static bool Extent(BuiltFace face, Vector3 point, float low, float high)
        {
            float minX = Single.MaxValue;
            float maxX = Single.MinValue;
            float minY = Single.MaxValue;
            float maxY = Single.MinValue;
            float minZ = Single.MaxValue;
            float maxZ = Single.MinValue;
            foreach (Vector3 corner in face.Points)
            {
                minX = MathF.Min(minX, corner.X);
                maxX = MathF.Max(maxX, corner.X);
                minY = MathF.Min(minY, corner.Y);
                maxY = MathF.Max(maxY, corner.Y);
                minZ = MathF.Min(minZ, corner.Z);
                maxZ = MathF.Max(maxZ, corner.Z);
            }
            return point.X >= minX - 0.05f && point.X <= maxX + 0.05f
                && point.Z >= minZ - 0.05f && point.Z <= maxZ + 0.05f
                && maxY > low && minY < high;
        }

        private static IEnumerable<int> Cells(Vector3 from, Vector3 to, Vector3 min, float spacing,
            int columns, int rows)
        {
            int steps = 1 + (int)((to - from).Length / spacing);
            for (int i = 0; i <= steps; i++)
            {
                Vector3 point = from + (to - from) * (i / (float)steps);
                int column = (int)MathF.Floor((point.X - min.X) / spacing);
                int row = (int)MathF.Floor((point.Z - min.Z) / spacing);
                if (column >= 0 && row >= 0 && column < columns && row < rows)
                {
                    yield return row * columns + column;
                }
            }
        }

        private static List<int>?[] Buckets(IReadOnlyList<BuiltFace> solid, Vector3 min, float spacing,
            out int columns, out int rows)
        {
            Bounds(solid, out Vector3 low, out Vector3 high);
            columns = Math.Max(1, (int)MathF.Ceiling((high.X - low.X) / spacing) + 1);
            rows = Math.Max(1, (int)MathF.Ceiling((high.Z - low.Z) / spacing) + 1);
            var buckets = new List<int>?[columns * rows];
            for (int i = 0; i < solid.Count; i++)
            {
                BuiltFace face = solid[i];
                float minX = face.Points.Min(p => p.X);
                float maxX = face.Points.Max(p => p.X);
                float minZ = face.Points.Min(p => p.Z);
                float maxZ = face.Points.Max(p => p.Z);
                int c0 = Math.Clamp((int)MathF.Floor((minX - low.X) / spacing), 0, columns - 1);
                int c1 = Math.Clamp((int)MathF.Floor((maxX - low.X) / spacing), 0, columns - 1);
                int r0 = Math.Clamp((int)MathF.Floor((minZ - low.Z) / spacing), 0, rows - 1);
                int r1 = Math.Clamp((int)MathF.Floor((maxZ - low.Z) / spacing), 0, rows - 1);
                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        (buckets[r * columns + c] ??= new List<int>()).Add(i);
                    }
                }
            }
            return buckets;
        }

        private static void Bounds(IReadOnlyList<BuiltFace> solid, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(Single.MaxValue);
            max = new Vector3(Single.MinValue);
            foreach (BuiltFace face in solid)
            {
                foreach (Vector3 point in face.Points)
                {
                    min = Vector3.ComponentMin(min, point);
                    max = Vector3.ComponentMax(max, point);
                }
            }
        }

        /// <summary>Point-in-triangle in plan, and the height of the surface there.</summary>
        private static bool Contains(BuiltFace face, float x, float z, out float y)
        {
            y = 0;
            Vector3[] points = face.Points;
            bool inside = false;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                if (points[i].Z > z != points[j].Z > z
                    && x < (points[j].X - points[i].X) * (z - points[i].Z)
                        / (points[j].Z - points[i].Z) + points[i].X)
                {
                    inside = !inside;
                }
            }
            if (!inside || MathF.Abs(face.Normal.Y) < 0.0001f)
            {
                return false;
            }
            float d = Vector3.Dot(face.Normal, points[0]);
            y = (d - face.Normal.X * x - face.Normal.Z * z) / face.Normal.Y;
            return true;
        }

        /// <summary>
        /// The file. Version 6: a header, one set holding one list, a struct
        /// per node, and then the shared array of 16-bit values both of each
        /// node's lists point into.
        /// </summary>
        private static byte[] Write(List<Node> nodes, float spacing)
        {
            var routes = Routes(nodes);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            const int indexOffset = 14;
            const int dataOffset = 16;
            int listOffset = 24;
            int nodeOffset = 32;
            int valuesOffset = nodeOffset + nodes.Count * 36;
            // Where each node's routing table lands in the shared array. A run
            // is a *pair* of 16-bit values, so it is four bytes and not two --
            // getting that wrong puts every list after the first at an offset
            // half way into the one before it, and the bots read a routing
            // table made of the tail of somebody else's.
            var routeOffset = new int[nodes.Count];
            int cursor = valuesOffset;
            for (int i = 0; i < nodes.Count; i++)
            {
                routeOffset[i] = cursor;
                cursor += routes[i].Count * 4;
            }
            writer.Write((ushort)6);
            writer.Write((ushort)1); // one set
            writer.Write((uint)indexOffset);
            writer.Write((uint)dataOffset);
            // No set index, which is what the game's own files carry: with one,
            // the AI can turn it on and index a second set that is not there.
            // The two bytes it would have occupied stay, because the header
            // says the data begins two after the index does.
            writer.Write((ushort)0); // index count
            writer.Write((ushort)0); // the unused index slot
            writer.Write((uint)listOffset); // the set holds one list
            writer.Write((ushort)1);
            writer.Write((ushort)0x5C);
            writer.Write((uint)nodeOffset); // the list holds the nodes
            writer.Write((ushort)nodes.Count);
            writer.Write((ushort)0x5C);
            for (int i = 0; i < nodes.Count; i++)
            {
                Node node = nodes[i];
                writer.Write((ushort)NodeType.Navigation);
                // the id is the index: the routing table is read as indices
                // into the list, and elsewhere as ids, and making them the
                // same number is what lets one node answer both
                writer.Write((ushort)i);
                writer.Write((ushort)0);
                // The second list is left empty, as it is on two thirds of the
                // nodes in the game's own rooms. Navigation does not come from
                // it: the routing table below is the graph, and the neighbours
                // of a node are the distinct places it sends you.
                writer.Write((ushort)0);
                writer.Write(Fixed.ToInt(node.Position.X));
                writer.Write(Fixed.ToInt(node.Position.Y));
                writer.Write(Fixed.ToInt(node.Position.Z));
                writer.Write(Fixed.ToInt(Reach));
                writer.Write((uint)routeOffset[i]);
                writer.Write((uint)routeOffset[i]);
                writer.Write(0u);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                foreach ((ushort run, ushort hop) in routes[i])
                {
                    writer.Write(run);
                    writer.Write(hop);
                }
            }
            return stream.ToArray();
        }

        /// <summary>
        /// For every node, which way to set off for every other node, run-
        /// length encoded over the destinations. A breadth-first search from
        /// each destination gives the distances; the neighbour that is one
        /// step closer is the answer, and a node with nowhere to go points at
        /// itself so the reader's own loop still advances.
        /// </summary>
        private static List<(ushort, ushort)>[] Routes(List<Node> nodes)
        {
            int count = nodes.Count;
            var hop = new ushort[count, count];
            var distance = new int[count];
            var queue = new Queue<int>();
            for (int destination = 0; destination < count; destination++)
            {
                Array.Fill(distance, -1);
                distance[destination] = 0;
                queue.Clear();
                queue.Enqueue(destination);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    foreach (int neighbour in nodes[current].Neighbours)
                    {
                        if (distance[neighbour] < 0)
                        {
                            distance[neighbour] = distance[current] + 1;
                            queue.Enqueue(neighbour);
                        }
                    }
                }
                for (int source = 0; source < count; source++)
                {
                    ushort answer = (ushort)source;
                    if (source != destination && distance[source] > 0)
                    {
                        foreach (int neighbour in nodes[source].Neighbours)
                        {
                            if (distance[neighbour] == distance[source] - 1)
                            {
                                answer = (ushort)neighbour;
                                break;
                            }
                        }
                    }
                    hop[source, destination] = answer;
                }
            }
            var results = new List<(ushort, ushort)>[count];
            for (int source = 0; source < count; source++)
            {
                var runs = new List<(ushort, ushort)>();
                int start = 0;
                while (start < count)
                {
                    ushort value = hop[source, start];
                    int end = start + 1;
                    while (end < count && hop[source, end] == value && end - start < UInt16.MaxValue)
                    {
                        end++;
                    }
                    runs.Add(((ushort)(end - start), value));
                    start = end;
                }
                results[source] = runs;
            }
            return results;
        }
    }
}
