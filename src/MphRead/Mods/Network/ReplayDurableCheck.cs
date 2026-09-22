using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayDurableCheck
{
    internal static int Run(string source, string directory)
    {
        Headless.Enter(); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "durable.ppdemo");
        try
        {
            var reference = new Dictionary<uint, (string Gameplay, string Presentation)>();
            using (var reader = DemoReader.Open(source)!)
            using (var world = new PassiveReplayScene(source, new(256, 192)))
            {
                if (!world.Step()) throw new InvalidDataException("Empty source.");
                var metadata = ReplayTimelineArchive.Metadata(world, ReplayType.FullMatch);
                using var writer = new ReplayWriterV3(path, metadata);
                DemoRecord? record = reader.ReadNext();
                do
                {
                    uint frame = world.Session.CurrentFrame;
                    reference.Add(frame, (ReplayStateHash.Compute(world.Scene, frame), world.Scene.ReplayPresentationHash(frame)));
                    while (record is { } packet && packet.Frame <= frame)
                    {
                        if (packet.Frame > 0) writer.WriteRecord(packet.Frame, packet.Data);
                        record = reader.ReadNext();
                    }
                    ReplayTimelineArchive.EndFrame(writer, frame);
                    if (frame != 0 && frame % 300 == 0)
                        writer.WriteCheckpoint(frame, ReplayWorldCheckpoint.Capture(world, world.Session.RecordingFrame).Bytes);
                } while (world.Step());
            }
            if (ReplayArchive.Validate(path) != ReplayOpenResult.Success) throw new InvalidDataException("Durable format validation failed.");
            using (var reader = DemoReader.Open(path)!)
            {
                if (reader.Checkpoints.Count < 2) throw new InvalidDataException("Durable test needs at least two checkpoints.");
                Console.WriteLine($"[replaydurable] {reader.Checkpoints.Count} durable checkpoints, {new FileInfo(path).Length} file bytes.");
            }
            foreach (uint target in new uint[] { 301, 799, 1250, 1500, 1800 })
            {
                if (!reference.ContainsKey(target)) continue;
                using var player = new PassiveReplayPlayer(path, new(256, 192));
                player.Seek(target); Complete(player);
                Compare(player, reference[target], target);
                if (player.CheckpointSource != "file" || player.SeekSimulationSteps >= 300)
                    throw new InvalidDataException("Cold seek did not use its durable baseline.");
                Console.WriteLine($"[replaydurable] cold seek {target}: {player.CheckpointSource} {player.SeekRestoreFrame}, {player.SeekSimulationSteps} steps, {player.SeekMilliseconds:F2} ms");
                player.Seek(301); Complete(player); Compare(player, reference[301], 301);
            }
            string clip = Path.Combine(directory, "range.ppdemo"), nested = Path.Combine(directory, "nested.ppdemo");
            if (ReplayArchive.Extract(path, 800, 1600, clip) != ReplayOpenResult.Success
                || ReplayArchive.Extract(clip, 250, 600, nested) != ReplayOpenResult.Success
                || ReplayClipFidelity.Run(source, clip, 800) != 0 || ReplayClipFidelity.Run(source, nested, 1050) != 0)
                throw new InvalidDataException("Durable nested range differs.");
            using (var player = new PassiveReplayPlayer(nested, new(256, 192)))
            {
                Complete(player);
                if (player.CheckpointSource != "file" || player.SeekSimulationSteps >= 300)
                    throw new InvalidDataException("Nested initial warmup ignored its durable checkpoint.");
            }
            string corrupt = Path.Combine(directory, "corrupt-checkpoint.ppdemo"); File.Copy(path, corrupt);
            ReplayCheckpointIndex first;
            using (var reader = DemoReader.Open(corrupt)!) first = reader.Checkpoints[0];
            using (var file = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = first.Offset + first.CompressedLength / 2; int original = file.ReadByte(); file.Position--; file.WriteByte((byte)(original ^ 0x55)); }
            if (ReplayArchive.Validate(corrupt) == ReplayOpenResult.Success) throw new InvalidDataException("Corrupt checkpoint passed validation.");
            using (var player = new PassiveReplayPlayer(corrupt, new(256, 192)))
            {
                player.Seek(301); Complete(player); Compare(player, reference[301], 301);
                if (player.RejectedCheckpoints != 1) throw new InvalidDataException("Invalid durable checkpoint did not fall back safely.");
            }
            if (!ReplayArchive.Recover(corrupt, out string? recovered, out _) || recovered == null
                || ReplayArchive.Validate(recovered) != ReplayOpenResult.Success) throw new InvalidDataException("Recovery did not omit the corrupt optional checkpoint.");
            Console.WriteLine("[replaydurable] PASS: cold/backward bounded seeks, nested range origins, checkpoint corruption fallback and recovery.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaydurable] FAIL: " + ex); return 1; }
    }
    private static void Complete(PassiveReplayPlayer player)
    {
        do { if (player.Update() > 120) throw new InvalidDataException("Unbounded durable seek."); } while (!player.Ready);
    }
    private static void Compare(PassiveReplayPlayer player, (string Gameplay, string Presentation) expected, uint frame)
    {
        if (ReplayStateHash.Compute(player.Current.Scene, frame) != expected.Gameplay
            || player.Current.Scene.ReplayPresentationHash(frame) != expected.Presentation)
            throw new InvalidDataException($"Durable world differs at frame {frame}.");
    }
}
