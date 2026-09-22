using System;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Replay;

/// <summary>Model animation values and asset identity. No model, animation-group,
/// texture or display-list reference survives capture.</summary>
internal sealed class ReplayModelCheckpoint
{
    private const ushort Schema = 1;
    private readonly byte[] _data;
    internal ReadOnlySpan<byte> Bytes => _data;
    private ReplayModelCheckpoint(byte[] data) => _data = data;

    internal static ReplayModelCheckpoint Capture(ModelInstance model)
    {
        using var stream = new MemoryStream(160);
        using var writer = new BinaryWriter(stream);
        writer.Write(Schema); writer.Write(model.Model.Name); writer.Write(model.Model.FirstHunt);
        writer.Write(model.Active); writer.Write(model.IsPlaceholder); writer.Write(model.NodeAnimIgnoreRoot);
        AnimationInfo animation = model.AnimInfo;
        for (int slot = 0; slot < 2; slot++)
        {
            writer.Write(animation.Index[slot]); writer.Write(animation.PrevIndex[slot]);
            writer.Write(animation.Frame[slot]); writer.Write(animation.FrameCount[slot]);
            writer.Write((int)animation.Flags[slot]); writer.Write(animation.Step[slot]);
        }
        var groups = model.Model.AnimationGroups;
        writer.Write(animation.Node.Slot); writer.Write(Index(groups.Node, animation.Node.Group));
        writer.Write(animation.Material.Slot); writer.Write(Index(groups.Material, animation.Material.Group));
        writer.Write(animation.Texcoord.Slot); writer.Write(Index(groups.Texcoord, animation.Texcoord.Group));
        writer.Write(animation.Texture.Slot); writer.Write(Index(groups.Texture, animation.Texture.Group));
        writer.Flush();
        return new(stream.ToArray());
    }

    private static int Index<T>(IReadOnlyList<T> groups, T? group) where T : class
    {
        if (group == null) return -1;
        for (int i = 0; i < groups.Count; i++) if (ReferenceEquals(groups[i], group)) return i;
        throw new InvalidOperationException("Animation group does not belong to its checkpoint asset.");
    }

    private readonly record struct Slot(int Index, int Previous, int Frame, int Count, AnimFlags Flags, int Step);
    internal void Restore(ModelInstance model)
    {
        using var stream = new MemoryStream(_data, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != Schema || reader.ReadString() != model.Model.Name
            || reader.ReadBoolean() != model.Model.FirstHunt)
            throw new InvalidDataException("Replay animation asset identity does not match.");
        bool active = reader.ReadBoolean(), placeholder = reader.ReadBoolean(), ignoreRoot = reader.ReadBoolean();
        var slots = new Slot[2];
        for (int slot = 0; slot < 2; slot++) slots[slot] = new(reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32(), (AnimFlags)reader.ReadInt32(), reader.ReadInt32());
        (int Slot, T? Group) Group<T>(IReadOnlyList<T> groups) where T : class
        {
            int slot = reader.ReadInt32(), index = reader.ReadInt32();
            if ((uint)slot >= 2 || index < -1 || index >= groups.Count)
                throw new InvalidDataException("Replay animation group is invalid.");
            return (slot, index == -1 ? null : groups[index]);
        }
        var groups = model.Model.AnimationGroups;
        var node = Group(groups.Node); var material = Group(groups.Material);
        var texcoord = Group(groups.Texcoord); var texture = Group(groups.Texture);
        if (stream.Position != stream.Length) throw new InvalidDataException("Replay animation has trailing data.");
        // Everything is validated before applying state to the independently loaded asset.
        model.Active = active; model.IsPlaceholder = placeholder; model.NodeAnimIgnoreRoot = ignoreRoot;
        var animation = model.AnimInfo;
        for (int i = 0; i < slots.Length; i++)
        {
            animation.Index[i] = slots[i].Index; animation.PrevIndex[i] = slots[i].Previous;
            animation.Frame[i] = slots[i].Frame; animation.FrameCount[i] = slots[i].Count;
            animation.Flags[i] = slots[i].Flags; animation.Step[i] = slots[i].Step;
        }
        animation.Node.Slot = node.Slot; animation.Node.Group = node.Group;
        animation.Material.Slot = material.Slot; animation.Material.Group = material.Group;
        animation.Texcoord.Slot = texcoord.Slot; animation.Texcoord.Group = texcoord.Group;
        animation.Texture.Slot = texture.Slot; animation.Texture.Group = texture.Group;
    }
}
