using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Formats.Collision;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Writes a room's collision file.
    ///
    /// Upstream's packer produces the same bytes and was the first thing this
    /// used, but it was written to round-trip one of the game's own rooms and
    /// its cost shows on a converted level: it looks each point up by scanning
    /// the list it is building, and it fills the lookup grid by asking every
    /// cell about every face. A hand-built arena of a hundred faces does not
    /// notice. A level with tens of thousands does -- it is quadratic twice
    /// over, and the second one is cells times faces.
    ///
    /// Same format, same conventions, two changes: points are deduplicated
    /// through a dictionary, and faces are pushed into the cells their bounds
    /// cover instead of every cell interrogating every face. Cells claim a
    /// face by its bounding box rather than by the exact polygon test, which
    /// can only list a face in a cell it does not quite reach -- the real
    /// polygon is tested at run time anyway, so the cost is a slightly longer
    /// list and never a missed surface.
    /// </summary>
    public static class MapCollisionPacker
    {
        /// <summary>The grid step is fixed: the run-time lookup divides by four.</summary>
        private const float CellSize = 4f;

        public static byte[] Pack(IReadOnlyList<CollisionDataEditor> data)
        {
            if (data.Count == 0)
            {
                throw new ProgramException("A map needs at least one solid face.");
            }
            var points = new List<Vector3>();
            var pointIds = new Dictionary<Vector3, ushort>();
            var planes = new List<Vector4>();
            var planeIds = new Dictionary<Vector4, ushort>();
            var pointIndices = new List<ushort>();
            var faces = new List<(ushort PlaneIndex, CollisionDataEditor Editor, ushort Count, ushort Start)>();
            var min = new Vector3(Single.MaxValue);
            var max = new Vector3(Single.MinValue);

            foreach (CollisionDataEditor editor in data)
            {
                if (editor.Points.Count < 3 || editor.Points.Count > 10)
                {
                    throw new ProgramException(
                        $"A collision face has {editor.Points.Count} points; the format allows 3 to 10.");
                }
                if (!planeIds.TryGetValue(editor.Plane, out ushort planeIndex))
                {
                    planeIndex = (ushort)planes.Count;
                    planes.Add(editor.Plane);
                    planeIds.Add(editor.Plane, planeIndex);
                }
                int start = pointIndices.Count;
                foreach (Vector3 point in editor.Points)
                {
                    if (!pointIds.TryGetValue(point, out ushort pointIndex))
                    {
                        pointIndex = (ushort)points.Count;
                        if (points.Count > UInt16.MaxValue)
                        {
                            throw new ProgramException(
                                "The map has more than 65535 distinct collision points, which the format cannot index. "
                                + "Convert at a larger scale or with less geometry.");
                        }
                        points.Add(point);
                        pointIds.Add(point, pointIndex);
                        min = Vector3.ComponentMin(min, point);
                        max = Vector3.ComponentMax(max, point);
                    }
                    pointIndices.Add(pointIndex);
                }
                // the list repeats each face's first point after its last, the
                // way the game's own files do
                pointIndices.Add(pointIndices[start]);
                faces.Add((planeIndex, editor, (ushort)editor.Points.Count, (ushort)start));
            }

            int partsX = Math.Max(1, (int)MathF.Floor((max.X - min.X) / CellSize) + 1);
            int partsY = Math.Max(1, (int)MathF.Floor((max.Y - min.Y) / CellSize) + 1);
            int partsZ = Math.Max(1, (int)MathF.Floor((max.Z - min.Z) / CellSize) + 1);
            var cells = new List<ushort>[partsX * partsY * partsZ];
            for (int i = 0; i < data.Count; i++)
            {
                CollisionDataEditor editor = data[i];
                var faceMin = new Vector3(Single.MaxValue);
                var faceMax = new Vector3(Single.MinValue);
                foreach (Vector3 point in editor.Points)
                {
                    faceMin = Vector3.ComponentMin(faceMin, point);
                    faceMax = Vector3.ComponentMax(faceMax, point);
                }
                int x0 = CellIndex(faceMin.X, min.X, partsX);
                int x1 = CellIndex(faceMax.X, min.X, partsX);
                int y0 = CellIndex(faceMin.Y, min.Y, partsY);
                int y1 = CellIndex(faceMax.Y, min.Y, partsY);
                int z0 = CellIndex(faceMin.Z, min.Z, partsZ);
                int z1 = CellIndex(faceMax.Z, min.Z, partsZ);
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int index = y * partsX * partsZ + z * partsX + x;
                            (cells[index] ??= new List<ushort>()).Add((ushort)i);
                        }
                    }
                }
            }

            int references = 0;
            foreach (List<ushort>? cell in cells)
            {
                references += cell?.Count ?? 0;
            }
            var dataIndices = new List<ushort>();
            var entries = new List<(ushort Count, ushort Start)>();
            foreach (List<ushort>? cell in cells)
            {
                int start = dataIndices.Count;
                if (start > UInt16.MaxValue)
                {
                    throw new ProgramException(
                        $"The collision grid needs {references} face references ({partsX}x{partsY}x{partsZ} cells "
                        + $"over {data.Count} faces), and the format indexes them with 16 bits. "
                        + "Convert at a larger scale, or with fewer solid surfaces.");
                }
                if (cell != null)
                {
                    dataIndices.AddRange(cell);
                }
                entries.Add(((ushort)(cell?.Count ?? 0), (ushort)start));
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            stream.Position = Sizes.CollisionHeader;
            int pointOffset = (int)stream.Position;
            foreach (Vector3 point in points)
            {
                writer.WriteVector3(point);
            }
            int planeOffset = (int)stream.Position;
            foreach (Vector4 plane in planes)
            {
                writer.WriteVector4(plane);
            }
            int pointIndexOffset = (int)stream.Position;
            foreach (ushort index in pointIndices)
            {
                writer.Write(index);
            }
            Align(stream, writer);
            int dataOffset = (int)stream.Position;
            foreach ((ushort planeIndex, CollisionDataEditor editor, ushort count, ushort start) in faces)
            {
                writer.Write(0u); // Counter, set at run time
                writer.Write(planeIndex);
                writer.Write((ushort)editor.Flags);
                writer.Write(editor.LayerMask);
                writer.Write((ushort)0);
                writer.Write(count);
                writer.Write(start);
            }
            int dataIndexOffset = (int)stream.Position;
            foreach (ushort index in dataIndices)
            {
                writer.Write(index);
            }
            Align(stream, writer);
            int entryOffset = (int)stream.Position;
            foreach ((ushort count, ushort start) in entries)
            {
                writer.Write(count);
                writer.Write(start);
            }
            // no portals: a custom map is one room part, with nothing to see
            // through into another
            int portalOffset = (int)stream.Position;
            stream.Position = 0;
            writer.Write("wc01".ToCharArray());
            writer.Write(points.Count);
            writer.Write(pointOffset);
            writer.Write(planes.Count);
            writer.Write(planeOffset);
            writer.Write(pointIndices.Count);
            writer.Write(pointIndexOffset);
            writer.Write(faces.Count);
            writer.Write(dataOffset);
            writer.Write(dataIndices.Count);
            writer.Write(dataIndexOffset);
            writer.Write(partsX);
            writer.Write(partsY);
            writer.Write(partsZ);
            writer.WriteVector3(min);
            writer.Write(entries.Count);
            writer.Write(entryOffset);
            writer.Write(0); // portal count
            writer.Write(portalOffset);
            return stream.ToArray();
        }

        private static int CellIndex(float value, float origin, int parts)
        {
            return Math.Clamp((int)((value - origin) / CellSize), 0, parts - 1);
        }

        private static void Align(MemoryStream stream, BinaryWriter writer)
        {
            while (stream.Position % 4 != 0)
            {
                writer.Write((byte)0);
            }
        }
    }
}
