using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Builds the model format's raw structures from scratch.
    ///
    /// The parsed classes -- Node, Mesh, Material -- only have constructors
    /// that take the raw struct read out of a file, because until now nothing
    /// ever made one that did not come from a file. Rather than widen those
    /// constructors, which live in upstream files, this writes the exact byte
    /// layout of each struct and marshals it back, which is the same thing the
    /// reader does. Nothing upstream has to change, and the packer receives
    /// objects indistinguishable from the ones a real model produces.
    /// </summary>
    public static class RawStructs
    {
        public static Node MakeNode(string name, int meshCount, int firstMeshId, int parent = -1,
            int child = -1, int next = -1)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteFixedString(writer, name, 64);
            writer.Write((short)parent);
            writer.Write((short)child);
            writer.Write((short)next);
            writer.Write((ushort)0); // Padding46
            writer.Write(1u); // Enabled
            writer.Write((ushort)meshCount);
            // the field is a byte offset into the mesh list, and every mesh entry is two bytes
            writer.Write((ushort)(firstMeshId * 2));
            WriteVector3Fx(writer, Vector3.One); // Scale
            writer.Write((short)0); // AngleX
            writer.Write((short)0); // AngleY
            writer.Write((short)0); // AngleZ
            writer.Write((ushort)0); // Padding62
            WriteVector3Fx(writer, Vector3.Zero); // Position
            writer.Write(0); // BoundingRadius
            WriteVector3Fx(writer, Vector3.Zero); // MinBounds, filled in by the packer
            WriteVector3Fx(writer, Vector3.Zero); // MaxBounds
            writer.Write((byte)BillboardMode.None);
            writer.Write((byte)0); // Padding8D
            writer.Write((ushort)0); // Padding8E
            for (int i = 0; i < 12; i++)
            {
                writer.Write(0); // Transform, set at runtime
            }
            for (int i = 0; i < 12; i++)
            {
                writer.Write(0u); // BeforeTransform, AfterTransform, UnusedC8-EC
            }
            return new Node(Marshal<RawNode>(stream));
        }

        public static Material MakeMaterial(string name, int textureId, int paletteId, RepeatMode xRepeat,
            RepeatMode yRepeat, bool lighting, ColorRgb diffuse, ColorRgb ambient)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteFixedString(writer, name, 64);
            writer.Write((byte)(lighting ? 1 : 0));
            writer.Write((byte)CullingMode.Back);
            writer.Write((byte)31); // Alpha, fully opaque
            writer.Write((byte)0); // Wireframe
            writer.Write((short)paletteId);
            writer.Write((short)textureId);
            writer.Write((byte)xRepeat);
            writer.Write((byte)yRepeat);
            WriteColorRgb(writer, diffuse);
            WriteColorRgb(writer, ambient);
            WriteColorRgb(writer, new ColorRgb(0, 0, 0)); // Specular
            writer.Write((byte)0); // Padding53
            writer.Write((uint)PolygonMode.Modulate);
            writer.Write((byte)RenderMode.Normal);
            writer.Write((byte)0); // AnimationFlags
            writer.Write((ushort)0); // Padding5A
            writer.Write((uint)TexgenMode.None);
            writer.Write((ushort)0); // TexcoordAnimationId
            writer.Write((ushort)0); // Padding62
            writer.Write(0u); // MatrixId
            writer.Write(4096); // ScaleS, 1.0
            writer.Write(4096); // ScaleT
            writer.Write((ushort)0); // RotateZ
            writer.Write((ushort)0); // Padding72
            writer.Write(0); // TranslateS
            writer.Write(0); // TranslateT
            writer.Write((ushort)0); // MaterialAnimationId
            writer.Write((ushort)0); // TextureAnimationId
            writer.Write((byte)0); // PackedRepeatMode
            writer.Write((byte)0); // Padding81
            writer.Write((ushort)0); // Padding82
            return new Material(Marshal<RawMaterial>(stream));
        }

        public static Mesh MakeMesh(int materialId, int dlistId)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)materialId);
            writer.Write((ushort)dlistId);
            return new Mesh(Marshal<RawMesh>(stream));
        }

        private static T Marshal<T>(MemoryStream stream) where T : struct
        {
            byte[] bytes = stream.ToArray();
            int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            Debug.Assert(bytes.Length == size, $"{typeof(T).Name} is {size} bytes, wrote {bytes.Length}.");
            if (bytes.Length != size)
            {
                throw new ProgramException($"Built {bytes.Length} bytes for {typeof(T).Name}, which is {size}.");
            }
            return Read.ReadStruct<T>(bytes);
        }

        private static void WriteFixedString(BinaryWriter writer, string value, int length)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < value.Length && i < length - 1; i++)
            {
                bytes[i] = (byte)value[i];
            }
            writer.Write(bytes);
        }

        private static void WriteVector3Fx(BinaryWriter writer, Vector3 value)
        {
            writer.Write(Fixed.ToInt(value.X));
            writer.Write(Fixed.ToInt(value.Y));
            writer.Write(Fixed.ToInt(value.Z));
        }

        private static void WriteColorRgb(BinaryWriter writer, ColorRgb value)
        {
            writer.Write(value.Red);
            writer.Write(value.Green);
            writer.Write(value.Blue);
        }
    }
}
