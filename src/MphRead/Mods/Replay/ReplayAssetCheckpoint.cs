using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay;

/// <summary>Explicit mutable asset values, never geometry or native handles.
/// The field order below is the version-1 asset component wire contract.</summary>
internal static class ReplayAssetCheckpoint
{
    private static readonly PropertyInfo[] NodeFields = Properties<Node>(
        "Enabled", "AnimIgnoreParent", "AnimIgnoreChild", "Scale", "Angle", "Position",
        "Transform", "BeforeTransform", "AfterTransform", "Animation", "RoomPartId", "RoomPartActive");
    private static readonly PropertyInfo[] MeshFields = Properties<Mesh>("Visible", "PlaceholderColor", "Selection");
    private static readonly PropertyInfo[] MaterialFields = Properties<Material>(
        "Lighting", "Culling", "Alpha", "CurrentAlpha", "Wireframe", "CurrentTextureId", "CurrentPaletteId",
        "Diffuse", "Ambient", "CurrentDiffuse", "CurrentAmbient", "CurrentSpecular", "PolygonMode", "RenderMode",
        "AnimationFlags", "TexgenMode", "TexcoordAnimationId", "MatrixId");
    internal static readonly string AccessorContract = string.Join(";", NodeFields.Concat(MeshFields).Concat(MaterialFields)
        .Select(p => p.DeclaringType + "." + p.Name + ":" + p.PropertyType));
    private static readonly bool BoundAccessorsCurrent = ReplayAssetAccessors.Contract == AccessorContract;
    private static PropertyInfo[] Properties<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(params string[] names) => names.Select(name => typeof(T).GetProperty(name)
        ?? throw new InvalidOperationException("Missing replay asset field " + name)).ToArray();

    internal static byte[] Capture(Scene scene)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        Write(writer, scene, new());
        return stream.ToArray();
    }
    internal static void Write(BinaryWriter writer, Scene scene, System.Collections.Generic.List<Model> models, bool boundAccessors = true)
    {
        boundAccessors &= BoundAccessorsCurrent;
        writer.Write((ushort)1);
        models.Clear();
        foreach (var model in scene.ReplicaModels.Values) models.Add(model);
        models.Sort(static (a, b) => { int order = StringComparer.Ordinal.Compare(a.Name, b.Name); return order != 0 ? order : a.FirstHunt.CompareTo(b.FirstHunt); });
        writer.Write(models.Count);
        foreach (Model model in models)
        {
            writer.Write(model.Name); writer.Write(model.FirstHunt);
            writer.Write(model.Nodes.Count);
            foreach (Node node in model.Nodes)
            {
                if (boundAccessors) ReplayAssetAccessors.Write(writer, node); else WriteFields(writer, node, NodeFields);
                foreach (float value in node.Bounds) writer.Write(value);
            }
            writer.Write(model.Meshes.Count);
            foreach (Mesh mesh in model.Meshes) if (boundAccessors) ReplayAssetAccessors.Write(writer, mesh); else WriteFields(writer, mesh, MeshFields);
            writer.Write(model.Materials.Count);
            foreach (Material material in model.Materials) if (boundAccessors) ReplayAssetAccessors.Write(writer, material); else WriteFields(writer, material, MaterialFields);
            writer.Write(model.MatrixStackValues.Count);
            foreach (float value in model.MatrixStackValues) writer.Write(value);
        }
        scene.Room!.WriteReplayActivation(writer);
        models.Clear();
    }

    internal static void Restore(Scene scene, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 1) throw new InvalidDataException("Unsupported replay asset component.");
        int count = reader.ReadInt32();
        if (count is < 0 or > 4096) throw new InvalidDataException("Invalid replay asset count.");
        var seen = new System.Collections.Generic.HashSet<(string, bool)>();
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString(); bool firstHunt = reader.ReadBoolean();
            if (!seen.Add((name, firstHunt))) throw new InvalidDataException("Duplicate replay asset.");
            if (!scene.ReplicaModels.TryGetValue((name, firstHunt), out Model? model))
                model = scene.GetModelInstance(name, firstHunt).Model;
            CheckCount(reader, model.Nodes.Count);
            foreach (Node node in model.Nodes)
            {
                ReadFields(reader, node, NodeFields);
                for (int b = 0; b < node.Bounds.Length; b++) node.Bounds[b] = Float(reader);
            }
            CheckCount(reader, model.Meshes.Count);
            foreach (Mesh mesh in model.Meshes) ReadFields(reader, mesh, MeshFields);
            CheckCount(reader, model.Materials.Count);
            foreach (Material material in model.Materials) ReadFields(reader, material, MaterialFields);
            CheckCount(reader, model.MatrixStackValues.Count);
            for (int m = 0; m < model.MatrixStackValues.Count / 16; m++)
                model.SetMatrixStackValues(m, (Matrix4)Read(reader, typeof(Matrix4))!);
        }
        scene.Room!.RestoreReplayActivation(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing replay asset data.");
    }

    private static void CheckCount(BinaryReader reader, int expected)
    { if (reader.ReadInt32() != expected) throw new InvalidDataException("Replay model asset topology differs."); }
    private static float Float(BinaryReader reader)
    { float value = reader.ReadSingle(); return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite replay asset value."); }
    private static void WriteFields(BinaryWriter writer, object value, PropertyInfo[] fields)
    { foreach (var field in fields) Write(writer, field.PropertyType, field.GetValue(value)); }
    private static void ReadFields(BinaryReader reader, object value, PropertyInfo[] fields)
    { foreach (var field in fields) field.SetValue(value, Read(reader, field.PropertyType)); }
    private static void Write(BinaryWriter writer, Type type, object? value)
    {
        if (Nullable.GetUnderlyingType(type) is Type inner)
        { writer.Write(value != null); if (value != null) Write(writer, inner, value); }
        else if (type.IsEnum) writer.Write(Convert.ToInt32(value));
        else if (value is bool boolean) writer.Write(boolean);
        else if (value is byte integer8) writer.Write(integer8);
        else if (value is int integer) writer.Write(integer);
        else if (value is float number) writer.Write(number);
        else if (value is Vector3 v3) { writer.Write(v3.X); writer.Write(v3.Y); writer.Write(v3.Z); }
        else if (value is Vector4 v4) { writer.Write(v4.X); writer.Write(v4.Y); writer.Write(v4.Z); writer.Write(v4.W); }
        else if (value is Matrix4 matrix) for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) writer.Write(matrix[r, c]);
        else if (value is ColorRgb color) { writer.Write(color.Red); writer.Write(color.Green); writer.Write(color.Blue); }
        else throw new InvalidOperationException("Unknown replay asset value " + type);
    }
    private static object? Read(BinaryReader reader, Type type)
    {
        if (Nullable.GetUnderlyingType(type) is Type inner) return reader.ReadBoolean() ? Read(reader, inner) : null;
        if (type.IsEnum) return Enum.ToObject(type, reader.ReadInt32());
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(float)) return Float(reader);
        if (type == typeof(Vector3)) return new Vector3(Float(reader), Float(reader), Float(reader));
        if (type == typeof(Vector4)) return new Vector4(Float(reader), Float(reader), Float(reader), Float(reader));
        if (type == typeof(Matrix4))
        { var result = new Matrix4(); for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) result[r, c] = Float(reader); return result; }
        if (type == typeof(ColorRgb)) return new ColorRgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        throw new InvalidDataException("Unknown replay asset value " + type);
    }
}
