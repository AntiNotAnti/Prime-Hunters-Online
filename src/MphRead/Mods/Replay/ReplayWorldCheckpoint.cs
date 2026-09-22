using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MphRead.Effects;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay;

/// <summary>A bounded value capsule. References are graph indices or construction
/// anchors in an independently loaded scene. Native resources and delegates never
/// enter the payload. Restore is only used on an unpublished, disposable replica.</summary>
internal sealed class ReplayWorldCheckpoint
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumObjects = 32768;
    private const uint Magic = 0x43575050; // PPWC
    private const ushort Version = 1;
    private readonly byte[] _data;
    internal ReadOnlySpan<byte> Bytes => _data;
    internal uint Frame { get; }
    private ReplayWorldCheckpoint(byte[] data, uint frame) { _data = data; Frame = frame; }
    internal static ReplayWorldCheckpoint FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Replay world exceeds its checkpoint budget.");
        byte[] data = bytes.ToArray();
        using var stream = new MemoryStream(data, writable: false); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version || reader.ReadString() != Contract)
            throw new InvalidDataException("Replay world checkpoint contract differs.");
        reader.ReadString(); reader.ReadInt32(); reader.ReadUInt64();
        return new(data, reader.ReadUInt32());
    }
    internal ReplayReplicaCheckpoint ConstructionState()
    {
        using var stream = new MemoryStream(_data, writable: false); using var reader = new BinaryReader(stream);
        reader.ReadUInt32(); reader.ReadUInt16(); reader.ReadString(); reader.ReadString(); reader.ReadInt32(); reader.ReadUInt64();
        reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();
        return new(ReadBytes(reader, ReplayReplicaCheckpoint.MaximumBytes));
    }

    private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> StructFields = new();
    private static readonly Dictionary<Type, FieldInfo[]> Fields = CreateFields();
    private static readonly Dictionary<string, Type> Types = CreateTypes();
    private static readonly Type[] ObjectTypes = Types.Values
        .Where(t => Fields.ContainsKey(t) || Collection(t) || t == typeof(ModelInstance) || t == typeof(WeaponInfo))
        .OrderBy(t => t.ToString(), StringComparer.Ordinal).ToArray();
    private static readonly Dictionary<Type, ushort> TypeIds = ObjectTypes.Select((type, index) => (type, index))
        .ToDictionary(pair => pair.type, pair => checked((ushort)pair.index));
    private static readonly string Contract = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        // Explicit codec revision plus field/asset contract, not the git SHA.
        // Unrelated commits must not invalidate durable clips.
        "world-codec-1|" + string.Join('|', Fields.OrderBy(p => p.Key.FullName, StringComparer.Ordinal).Select(p => p.Key.FullName + ":" +
            string.Join(',', p.Value.Select(f => f.DeclaringType!.FullName + "." + f.Name + ":" + f.FieldType))))
        + string.Join('|', Types.Values.Where(t => t.IsValueType && !t.IsPrimitive && !t.IsEnum).OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => t.FullName + ":" + string.Join(',', ValueFields(t).Select(f => f.Name + ":" + f.FieldType)))))));
    private static Dictionary<Type, FieldInfo[]> CreateFields()
    {
        var result = new Dictionary<Type, FieldInfo[]>();
        foreach (var pair in ReplayWorldSchemas.Fields)
        {
            Type type = typeof(Scene).Assembly.GetType(pair.Key, throwOnError: true)!;
            var fields = new List<FieldInfo>();
            for (Type? parent = type; parent != null; parent = parent.BaseType)
                if (ReplayWorldSchemas.Fields.TryGetValue(parent.FullName!, out string[]? names))
                    foreach (string name in names)
                        fields.Add(parent.GetField(name, InstanceFields | BindingFlags.DeclaredOnly)
                            ?? throw new InvalidOperationException($"Missing checkpoint field {parent.Name}.{name}."));
            result.Add(type, fields.ToArray());
        }
        return result;
    }
    private static FieldInfo[] ValueFields(Type type) => StructFields.GetOrAdd(type,
        static value => value.GetFields(InstanceFields).OrderBy(f => f.MetadataToken).ToArray());
    private static bool Collection(Type type) => type.IsArray || type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Queue<>)
        || type.GetGenericTypeDefinition() == typeof(LinkedList<>));
    private static Type Element(Type type) => type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
    private static Dictionary<string, Type> CreateTypes()
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        void Add(Type type)
        {
            if (result.ContainsKey(type.ToString())) return;
            result.Add(type.ToString(), type);
            if (Collection(type)) Add(Element(type));
            else if (Nullable.GetUnderlyingType(type) is Type underlying) Add(underlying);
            else if (type.IsValueType && !type.IsPrimitive && !type.IsEnum)
                foreach (var field in ValueFields(type)) Add(field.FieldType);
        }
        foreach (var pair in Fields) { Add(pair.Key); foreach (var field in pair.Value) Add(field.FieldType); }
        foreach (Type type in new[] { typeof(ModelInstance), typeof(WeaponInfo), typeof(int), typeof(uint), typeof(float),
            typeof(bool), typeof(string), typeof(Vector3), typeof(EntityBase) }) Add(type);
        return result;
    }

    /// <summary>Anchors are owned by the destination world, never by the capsule.
    /// Construct this once, immediately after the room is initialized.</summary>
    internal sealed class Bindings
    {
        internal readonly Dictionary<object, ulong> Names = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<ulong, object> Objects = new();
        internal readonly object[] Roots;
        internal Bindings(Scene scene)
        {
            Roots = [scene, scene.GameState, scene.Players, scene.PlayerReplication, scene.Services, scene.ContinuousPhase];
            for (int i = 0; i < Roots.Length; i++) Register(Roots[i], "root" + i);
        }
        private void Register(object? value, string name)
        {
            if (value == null || value is string || value.GetType().IsValueType || Names.ContainsKey(value)) return;
            Type type = value.GetType();
            if (value is WeaponInfo) return;
            if (!Fields.ContainsKey(type) && !Collection(type) && value is not ModelInstance)
                throw new InvalidOperationException($"Unsupported checkpoint anchor {type.Name}: {name}.");
            // Stable path hash, never a runtime object address. Collisions fail
            // construction rather than binding two unrelated objects together.
            ulong identity = 14695981039346656037;
            foreach (char character in name) { identity ^= character; identity *= 1099511628211; }
            if (identity == 0 || Objects.ContainsKey(identity)) throw new InvalidOperationException("Replay construction anchor collision.");
            Names.Add(value, identity); Objects.Add(identity, value);
            if (Collection(type))
            {
                int i = 0; foreach (object? item in (IEnumerable)value) Register(item, name + "/" + i++);
            }
            else if (Fields.TryGetValue(type, out FieldInfo[]? fields))
                foreach (var field in fields) Register(field.GetValue(value), name + "/" + field.DeclaringType!.Name + "." + field.Name);
        }
    }

    internal static ReplayWorldCheckpoint Capture(PassiveReplayScene replay, uint? recordingFrame = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic); writer.Write(Version); writer.Write(Contract);
        writer.Write(replay.Scene.Room!.Meta.Name); writer.Write((int)replay.Scene.GameState.Mode); writer.Write(replay.MapHash);
        uint frame = recordingFrame ?? replay.Session.CurrentFrame;
        writer.Write(frame); writer.Write(replay.Scene.Random.Rng1); writer.Write(replay.Scene.Random.Rng2);
        WriteBytes(writer, replay.InitialState.Bytes);
        WriteBytes(writer, replay.State.CaptureCheckpoint().Bytes);
        var graph = new CaptureGraph(replay.CheckpointBindings);
        foreach (object root in replay.CheckpointBindings.Roots) graph.Id(root);
        var nodes = graph.Finish();
        writer.Write(nodes.Count);
        foreach (var node in nodes) { writer.Write(node.Type); writer.Write(node.Anchor); WriteBytes(writer, node.Data); }
        writer.Flush();
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Replay world exceeds its checkpoint budget.");
        return new(stream.ToArray(), frame);
    }

    private sealed record Node(ushort Type, ulong Anchor, byte[] Data);
    private sealed class CaptureGraph(Bindings bindings)
    {
        private readonly Dictionary<object, int> _indices = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _objects = new();
        internal int Id(object? value)
        {
            if (value == null) return -1;
            if (_indices.TryGetValue(value, out int id)) return id;
            if (_objects.Count == MaximumObjects) throw new InvalidDataException("Replay object budget exceeded.");
            id = _objects.Count; _objects.Add(value); _indices.Add(value, id); return id;
        }
        internal List<Node> Finish()
        {
            var nodes = new List<Node>();
            long total = 0;
            for (int index = 0; index < _objects.Count; index++)
            {
                object value = _objects[index]; Type type = value.GetType();
                if (!Types.ContainsKey(type.ToString())) throw new InvalidOperationException($"No replay schema for {type}.");
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                if (value is ModelInstance model)
                {
                    writer.Write(model.Model.Name); writer.Write(model.Model.FirstHunt);
                    WriteBytes(writer, ReplayModelCheckpoint.Capture(model).Bytes);
                }
                else if (value is WeaponInfo weapon) WriteWeapon(writer, weapon);
                else
                {
                    if (value is ItemInstanceEntity item) writer.Write((int)item.ItemType);
                    if (value is HalfturretEntity turret) writer.Write(turret.Owner.SlotIndex);
                    if (value is SingleParticle single) writer.Write(SingleIdentity(single.ParticleDefinition));
                    if (Collection(type))
                    {
                        int[] lengths = value is Array a ? Enumerable.Range(0, a.Rank).Select(a.GetLength).ToArray()
                            : [((IEnumerable)value).Cast<object?>().Count()];
                        foreach (int length in lengths) writer.Write(length);
                        foreach (object? itemValue in (IEnumerable)value) WriteValue(writer, Element(type), itemValue, Id);
                    }
                    else if (Fields.TryGetValue(type, out var fields))
                        foreach (var field in fields) WriteValue(writer, field.FieldType, field.GetValue(value), Id);
                    else throw new InvalidOperationException($"No replay object schema for {type}.");
                }
                writer.Flush(); total += stream.Length;
                if (total > MaximumBytes) throw new InvalidDataException("Replay world exceeds its checkpoint budget.");
                nodes.Add(new(TypeIds[type], bindings.Names.GetValueOrDefault(value), stream.ToArray()));
            }
            return nodes;
        }
    }

    // These are immutable engine asset identities. Never serialize WeaponInfo's
    // metadata graph or a process-local object address.
    private static readonly IReadOnlyList<WeaponInfo>[] WeaponsByFamily = [Weapons.WeaponsMP, Weapons.Weapons1P,
        Weapons.EnemyWeapons, Weapons.BossWeapons, Weapons.GoreaWeapons, Weapons.PlatformWeapons, Weapons.Ricochets];
    private static void WriteWeapon(BinaryWriter writer, WeaponInfo weapon)
    {
        for (int family = 0; family < WeaponsByFamily.Length; family++)
            for (int i = 0; i < WeaponsByFamily[family].Count; i++)
                if (ReferenceEquals(weapon, WeaponsByFamily[family][i])) { writer.Write(family); writer.Write(i); return; }
        throw new InvalidOperationException("Replay weapon is not an identified engine asset.");
    }
    private static int SingleIdentity(Particle? particle)
    {
        if (particle == null) return -1;
        foreach (SingleType type in Enum.GetValues<SingleType>())
            if (ReferenceEquals(Read.GetSingleParticle(type), particle)) return (int)type;
        throw new InvalidOperationException("Replay single particle has no asset identity.");
    }

    internal void Restore(PassiveReplayScene replay, uint? playbackFrame = null)
    {
        if (replay.HasStepped) throw new InvalidOperationException("Restore requires a new unpublished replica.");
        using var stream = new MemoryStream(_data, writable: false); using var reader = new BinaryReader(stream);
        if (_data.Length > MaximumBytes || reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version
            || reader.ReadString() != Contract || reader.ReadString() != replay.Scene.Room!.Meta.Name
            || reader.ReadInt32() != (int)replay.Scene.GameState.Mode || reader.ReadUInt64() != replay.MapHash)
            throw new InvalidDataException("Replay world checkpoint contract or room differs.");
        uint frame = reader.ReadUInt32(), rng1 = reader.ReadUInt32(), rng2 = reader.ReadUInt32();
        // Older v4 capsules can contain an earlier decoder version. Compare the
        // normalized values, not two different encodings of the same baseline.
        var construction = new ReplayReplicaState();
        construction.RestoreCheckpoint(new ReplayReplicaCheckpoint(ReadBytes(reader, ReplayReplicaCheckpoint.MaximumBytes)));
        if (!construction.CaptureCheckpoint().Bytes.SequenceEqual(replay.InitialState.Bytes))
            throw new InvalidDataException("Replay world construction baseline differs.");
        var decoder = new ReplayReplicaCheckpoint(ReadBytes(reader, ReplayReplicaCheckpoint.MaximumBytes));
        var decoded = new ReplayReplicaState(); decoded.RestoreCheckpoint(decoder);
        for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
        {
            var occupant = decoded.Occupant(slot);
            PlayerEntity player = replay.Scene.Players.Items[slot];
            if (occupant.Generation != 0 && player.Hunter != occupant.Hunter)
            {
                player.ModPrepareHunterResources(occupant.Hunter); player.ModSetHunter(occupant.Hunter); player.Initialize();
            }
        }
        int count = Count(reader, MaximumObjects);
        var nodes = new Node[count]; var types = new Type[count]; var objects = new object[count];
        for (int i = 0; i < count; i++)
        {
            ushort typeId = reader.ReadUInt16(); ulong anchor = reader.ReadUInt64();
            if (typeId >= ObjectTypes.Length)
                throw new InvalidDataException("Unknown replay object contract.");
            nodes[i] = new(typeId, anchor, ReadBytes(reader, MaximumBytes)); types[i] = ObjectTypes[typeId];
        }
        if (stream.Position != stream.Length || frame != Frame) throw new InvalidDataException("Invalid replay world capsule length/frame.");
        // Allocate and bind every object before following any link. Only this new
        // replica is modified; its caller disposes it if any validation fails.
        for (int i = 0; i < count; i++)
        {
            Node node = nodes[i]; Type type = types[i];
            if (node.Anchor != 0)
            {
                if (!replay.CheckpointBindings.Objects.TryGetValue(node.Anchor, out object? anchor) || anchor.GetType() != type)
                    throw new InvalidDataException($"Construction anchor differs for {type.Name}.");
                objects[i] = Collection(type) ? Create(type, node.Data, replay.Scene) : anchor;
            }
            else objects[i] = Create(type, node.Data, replay.Scene);
        }
        object? Resolve(int index, Type type)
        {
            if (index == -1) return null;
            if ((uint)index >= count || !type.IsInstanceOfType(objects[index]))
                throw new InvalidDataException("Invalid replay object link.");
            return objects[index];
        }
        for (int i = 0; i < count; i++)
        {
            object target = objects[i]; Type type = types[i];
            using var data = new MemoryStream(nodes[i].Data, writable: false); using var input = new BinaryReader(data);
            if (target is ModelInstance model)
            {
                string name = input.ReadString(); bool firstHunt = input.ReadBoolean();
                if (model.Model.Name != name || model.Model.FirstHunt != firstHunt)
                    model.SetModel(Read.GetModelInstance(name, firstHunt).Model);
                ReplayModelCheckpoint.FromBytes(ReadBytes(input, 4096)).Restore(model);
            }
            else if (target is WeaponInfo) { input.ReadInt32(); input.ReadInt32(); }
            else
            {
                if (target is ItemInstanceEntity) input.ReadInt32();
                if (target is HalfturretEntity) input.ReadInt32();
                if (target is SingleParticle single)
                {
                    int identity = input.ReadInt32();
                    single.ParticleDefinition = identity == -1 ? null! : Enum.IsDefined((SingleType)identity)
                        ? Read.GetSingleParticle((SingleType)identity) : throw new InvalidDataException("Unknown replay particle asset.");
                }
                if (Collection(type))
                {
                    int rank = type.IsArray ? type.GetArrayRank() : 1;
                    var lengths = new int[rank]; int length = 1;
                    for (int d = 0; d < rank; d++) { lengths[d] = Count(input, MaximumObjects); length = checked(length * lengths[d]); }
                    if (length > MaximumObjects) throw new InvalidDataException("Replay collection too large.");
                    if (target is IList list && target is not Array) list.Clear();
                    var add = type.IsArray || target is IList ? null : type.GetMethod(type.GetGenericTypeDefinition() == typeof(Queue<>) ? "Enqueue" : "AddLast", [Element(type)]);
                    for (int n = 0; n < length; n++)
                    {
                        object? value = ReadValue(input, Element(type), Resolve);
                        if (target is Array array)
                        {
                            var indices = new int[rank]; int remainder = n;
                            for (int d = rank - 1; d >= 0; d--) { indices[d] = remainder % lengths[d]; remainder /= lengths[d]; }
                            array.SetValue(value, indices);
                        }
                        else if (target is IList values) values.Add(value);
                        else add!.Invoke(target, [value]);
                    }
                }
                else foreach (var field in Fields[type]) field.SetValue(target, ReadValue(input, field.FieldType, Resolve));
            }
            if (data.Position != data.Length) throw new InvalidDataException($"Trailing replay fields in {type.Name}.");
        }
        replay.Scene.FinishReplayWorldRestore();
        if (!replay.Session.Reposition(playbackFrame ?? frame, 0, sourceClock: playbackFrame.HasValue))
            throw new InvalidDataException(replay.Session.LastError);
        replay.State.RestoreCheckpoint(decoder);
        replay.Scene.Random.SetRng1(rng1); replay.Scene.Random.SetRng2(rng2);
    }

    private static object Create(Type type, byte[] data, Scene scene)
    {
        using var stream = new MemoryStream(data, writable: false); using var reader = new BinaryReader(stream);
        if (type == typeof(ModelInstance)) return Read.GetModelInstance(reader.ReadString(), reader.ReadBoolean());
        if (type == typeof(WeaponInfo))
        {
            int family = reader.ReadInt32(), index = reader.ReadInt32();
            if ((uint)family >= WeaponsByFamily.Length || (uint)index >= WeaponsByFamily[family].Count)
                throw new InvalidDataException("Unknown replay weapon asset.");
            return WeaponsByFamily[family][index];
        }
        if (type.IsArray)
        {
            var lengths = new int[type.GetArrayRank()]; int total = 1;
            for (int i = 0; i < lengths.Length; i++) { lengths[i] = Count(reader, MaximumObjects); total = checked(total * lengths[i]); }
            if (total > MaximumObjects) throw new InvalidDataException("Replay array too large.");
            return Array.CreateInstance(Element(type), lengths);
        }
        if (type == typeof(ItemInstanceEntity))
        {
            var item = (ItemType)reader.ReadInt32();
            if (!Enum.IsDefined(item)) throw new InvalidDataException("Unknown replay pickup type.");
            return new ItemInstanceEntity(new ItemInstanceEntityData(Vector3.Zero, item, -1), default, scene);
        }
        if (type == typeof(BeamProjectileEntity)) return new BeamProjectileEntity(scene);
        if (type == typeof(BombEntity)) return new BombEntity(scene);
        if (type == typeof(BeamEffectEntity)) return new BeamEffectEntity(scene);
        if (type == typeof(HalfturretEntity))
        {
            int slot = reader.ReadInt32(); if ((uint)slot >= PlayerEntity.SlotCapacity) throw new InvalidDataException("Invalid turret owner.");
            var turret = new HalfturretEntity(scene.Players.Items[slot], scene); turret.Create(); return turret;
        }
        if (typeof(EntityBase).IsAssignableFrom(type) || type == typeof(Scene) || type == typeof(SceneGameState)
            || type == typeof(ReplaySceneServices) || type == typeof(PlayerReplicationBridge))
            throw new InvalidDataException($"Missing construction anchor for {type.Name}.");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidDataException($"Cannot construct replay value {type.Name}.");
    }

    private static void WriteValue(BinaryWriter writer, Type type, object? value, Func<object?, int> reference)
    {
        if (type == typeof(object))
        {
            Type actual = value?.GetType() ?? typeof(int);
            if (!Types.ContainsKey(actual.ToString())) throw new InvalidDataException($"Unsupported replay message parameter {actual}.");
            writer.Write(actual.ToString()); WriteValue(writer, actual, value ?? 0, reference); return;
        }
        if (Nullable.GetUnderlyingType(type) is Type underlying)
        { writer.Write(value != null); if (value != null) WriteValue(writer, underlying, value, reference); return; }
        if (type == typeof(string)) { writer.Write(value != null); if (value != null) writer.Write((string)value); return; }
        if (type.IsEnum) { WriteValue(writer, Enum.GetUnderlyingType(type), Convert.ChangeType(value, Enum.GetUnderlyingType(type)), reference); return; }
        if (type == typeof(bool)) writer.Write((bool)value!);
        else if (type == typeof(byte)) writer.Write((byte)value!);
        else if (type == typeof(sbyte)) writer.Write((sbyte)value!);
        else if (type == typeof(short)) writer.Write((short)value!);
        else if (type == typeof(ushort)) writer.Write((ushort)value!);
        else if (type == typeof(int)) writer.Write((int)value!);
        else if (type == typeof(uint)) writer.Write((uint)value!);
        else if (type == typeof(long)) writer.Write((long)value!);
        else if (type == typeof(ulong)) writer.Write((ulong)value!);
        else if (type == typeof(float)) writer.Write((float)value!);
        else if (type == typeof(double)) writer.Write((double)value!);
        else if (type == typeof(char)) writer.Write((ushort)(char)value!);
        else if (type.IsValueType) foreach (var field in ValueFields(type)) WriteValue(writer, field.FieldType, field.GetValue(value), reference);
        else writer.Write(reference(value));
    }
    private static object? ReadValue(BinaryReader reader, Type type, Func<int, Type, object?> reference)
    {
        if (type == typeof(object))
        {
            if (!Types.TryGetValue(reader.ReadString(), out Type? actual) || actual == typeof(object))
                throw new InvalidDataException("Unsupported replay message parameter.");
            return ReadValue(reader, actual, reference);
        }
        if (Nullable.GetUnderlyingType(type) is Type underlying) return reader.ReadBoolean() ? ReadValue(reader, underlying, reference) : null;
        if (type == typeof(string)) return reader.ReadBoolean() ? reader.ReadString() : null;
        if (type.IsEnum) return Enum.ToObject(type, ReadValue(reader, Enum.GetUnderlyingType(type), reference)!);
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(sbyte)) return reader.ReadSByte();
        if (type == typeof(short)) return reader.ReadInt16();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(float)) return reader.ReadSingle();
        if (type == typeof(double)) return reader.ReadDouble();
        if (type == typeof(char)) return (char)reader.ReadUInt16();
        if (!type.IsValueType) return reference(reader.ReadInt32(), type);
        object value = Activator.CreateInstance(type)!;
        foreach (var field in ValueFields(type)) field.SetValue(value, ReadValue(reader, field.FieldType, reference));
        return value;
    }
    private static int Count(BinaryReader reader, int limit)
    { int count = reader.ReadInt32(); return count >= 0 && count <= limit ? count : throw new InvalidDataException("Replay length exceeds its bound."); }
    private static byte[] ReadBytes(BinaryReader reader, int limit)
    { int count = Count(reader, limit); byte[] bytes = reader.ReadBytes(count); return bytes.Length == count ? bytes : throw new EndOfStreamException(); }
    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
}
