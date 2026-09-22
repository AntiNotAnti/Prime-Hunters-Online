using System;
using System.IO;
using System.Linq;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayAssetChecks
{
    internal static void Run(PassiveReplayScene first, PassiveReplayScene sibling, string path, Vector2i size)
    {
        byte[] before = ReplayAssetCheckpoint.Capture(first.Scene);
        byte[] siblingBefore = ReplayAssetCheckpoint.Capture(sibling.Scene);
        Model room = first.Scene.Room!.GetModels()[0].Model;
        Model other = sibling.Scene.Room!.GetModels()[0].Model;
        Model foreground = Read.GetRoomModelInstance(room.Name).Model;
        bool foregroundEnabled = foreground.Nodes[0].Enabled;
        byte foregroundAlpha = foreground.Materials[0].Alpha;
        try
        {
            if (ReferenceEquals(room, other) || ReferenceEquals(room.Nodes[0], foreground.Nodes[0])
                || ReferenceEquals(room.Materials[0], other.Materials[0]) || ReferenceEquals(room.Meshes[0], other.Meshes[0])
                || !ReferenceEquals(room.RenderInstructionLists, foreground.RenderInstructionLists))
                throw new InvalidDataException("Replica asset ownership or immutable geometry sharing differs.");
            // Exercise the same mutation as loading a different CTF/player-count layer.
            room.FilterNodes(0); room.Nodes[0].Enabled = !foregroundEnabled;
            room.Nodes[0].AnimIgnoreChild = true; room.Nodes[0].BeforeTransform = Matrix4.CreateTranslation(3, 4, 5);
            room.Meshes[0].Visible = false; room.Meshes[0].PlaceholderColor = new(1, 0, 0, .5f);
            room.Materials[0].Alpha = 7; room.Materials[0].Diffuse = new ColorRgb(3, 8, 11);
            first.Scene.Room.RoomCollision[0].Active = false;
            if (!ReplayAssetCheckpoint.Capture(sibling.Scene).AsSpan().SequenceEqual(siblingBefore)
                || foreground.Nodes[0].Enabled != foregroundEnabled || foreground.Materials[0].Alpha != foregroundAlpha)
                throw new InvalidDataException("Replica model mutation escaped its scene.");
            foreground.Nodes[0].Enabled = !foregroundEnabled; foreground.Materials[0].Alpha = 3;
            using var restored = new PassiveReplayScene(path, size);
            if (restored.Scene.Room!.GetModels()[0].Model.Materials[0].Alpha != foregroundAlpha)
                throw new InvalidDataException("Replica inherited a foreground material mutation.");
            var checkpoint = ReplayWorldCheckpoint.Capture(first);
            byte[] expected = ReplayAssetCheckpoint.Capture(first.Scene);
            checkpoint.Restore(restored);
            if (!ReplayAssetCheckpoint.Capture(restored.Scene).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Detached world omitted mutable asset or room activation state.");
        }
        finally
        {
            foreground.Nodes[0].Enabled = foregroundEnabled; foreground.Materials[0].Alpha = foregroundAlpha;
            ReplayAssetCheckpoint.Restore(first.Scene, before);
        }
        Console.WriteLine("[replayassets] PASS: private room layers/materials/meshes, shared immutable geometry, foreground defaults and detached asset/activation restore.");
    }
}
