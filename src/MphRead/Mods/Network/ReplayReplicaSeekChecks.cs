using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayReplicaSeekChecks
{
    internal static void Run(string path, Vector2i size, IReadOnlyDictionary<uint, (string Gameplay, string Presentation)> frames,
        ReplayWorldCheckpoint? checkpoint, bool comparePresentation)
    {
        void Compare(PassiveReplayScene scene)
        {
            var expected = frames[scene.Session.CurrentFrame];
            if (ReplayStateHash.Compute(scene.Scene, scene.Session.CurrentFrame) != expected.Gameplay
                || comparePresentation && scene.Scene.ReplayPresentationHash(scene.Session.CurrentFrame) != expected.Presentation)
                throw new InvalidDataException($"Checkpoint seek differs from linear playback at {scene.Session.CurrentFrame}.");
        }
        using var player = new PassiveReplayPlayer(path, size);
        uint last = player.Current.Session.LastFrame;
        int comparisons = 0;
        foreach (uint requested in new uint[] { 1500, 35, 1100, 615, 0, 1799, 1201, 610, last })
        {
            uint target = Math.Min(last, requested);
            player.Seek(target);
            int updates = 0;
            while (player.Transport.IsSeeking)
            {
                if (player.Update() > PassiveReplayPlayer.MaximumStepsPerUpdate || ++updates > last + 2)
                    throw new InvalidDataException("Replica seek failed its bounded scheduling contract.");
            }
            if (player.Current.Session.CurrentFrame != target || player.RejectedCheckpoints != 0)
                throw new InvalidDataException("Replica seek did not reach its exact target.");
            Compare(player.Current); comparisons++;
            Console.WriteLine($"[replayseek] {target} from {player.SeekRestoreFrame}: {player.SeekSimulationSteps} steps, {updates} updates, {player.SeekMilliseconds:F2} ms");
        }
        if (player.CheckpointBytes > 64L * 1024 * 1024 || player.CheckpointCount > 128)
            throw new InvalidDataException("Replica checkpoint cache exceeded its bound.");
        string eof = ReplayStateHash.Compute(player.Current.Scene, last);
        for (int i = 0; i < 10; i++) player.Update();
        if (ReplayStateHash.Compute(player.Current.Scene, last) != eof) throw new InvalidDataException("Replica EOF advanced the world.");

        if (checkpoint != null && last >= 1200)
        {
            var timeline = new RollingReplayTimeline();
            timeline.AppendRestorePoint(new(checkpoint.Frame, checkpoint.Frame, ReplayRestoreKind.ReplicaCheckpoint,
                [new(checkpoint.Frame, checkpoint.Frame, ReplayFactKind.World, checkpoint.Bytes)]));
            using var reader = DemoReader.Open(path, out var result) ?? throw new InvalidDataException(result.ToString());
            while (reader.ReadNext() is DemoRecord record)
            {
                if (record.Frame <= checkpoint.Frame) continue;
                if (record.Frame > 1200) break;
                timeline.Append(new(record.Frame, record.Frame, ReplayFactKind.Snapshot, record.Data));
            }
            timeline.Append(new(1200, 1200, ReplayFactKind.Event, ReadOnlySpan<byte>.Empty));
            if (!timeline.TryFreeze(1150, 1200, out var clip) || clip == null) throw new InvalidDataException("Could not freeze the replica clip.");
            timeline.Reset(); // frozen bytes and references must survive eviction/reset
            using var frozen = new PassiveReplayPlayer(clip, size);
            int warmup = 0;
            while (!frozen.Ready)
            {
                warmup += frozen.Update();
                if (warmup > 300) throw new InvalidDataException("Frozen clip warmup did not finish.");
            }
            if (frozen.Current.Session.CurrentFrame != 1150) throw new InvalidDataException("Frozen clip start differs.");
            Compare(frozen.Current); comparisons++;
            while (!frozen.Current.Session.AtEnd) { frozen.Update(); Compare(frozen.Current); comparisons++; }
            if (frozen.Current.Session.CurrentFrame != 1200) throw new InvalidDataException("Frozen clip end differs.");
            frozen.Seek(1160);
            while (!frozen.Ready) frozen.Update();
            Compare(frozen.Current); comparisons++;
            Console.WriteLine($"[replayseek] frozen clip: {warmup} bounded warmup steps, frames 1150–1200, backward seek, source timeline reset");
        }
        Console.WriteLine($"[replayseek] PASS: {comparisons} file/clip comparisons, {player.CheckpointCount} checkpoints, {player.CheckpointBytes} retained bytes, frozen EOF.");
    }
}
