using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MphRead.Effects;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Network;

internal static class ReplayPerformanceChecks
{
    internal static void CheckScene(Scene scene)
    {
        var scratch = new ReplayAuthorityCaptureScratch();
        Func<MphRead.Entities.ItemInstanceEntity, int> identify = static item => item.Id;
        byte[] reference = ReplayAuthorityWorld.Capture(scene, 1, 1, 1, identify).Encode();
        if (!scratch.Encode(scratch.Capture(scene, 1, 1, 1, identify)).SequenceEqual(reference))
            throw new InvalidDataException("Authority scratch changed captured values.");
        for (int i = 0; i < 100; i++) scratch.Encode(scratch.Capture(scene, 1, 1, 1, identify));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) scratch.Encode(scratch.Capture(scene, 1, 1, 1, identify));
        if (GC.GetAllocatedBytesForCurrentThread() != before)
            throw new InvalidDataException("Warmed authority capture/encode allocated.");
        var charge = new EffectEntry(); var muzzle = new EffectEntry();
        for (int i = 0; i < 100; i++)
        {
            scene.SetPresentationEffectTransform(charge, OpenTK.Mathematics.Matrix4.Identity);
            scene.SetPresentationEffectTransform(muzzle, OpenTK.Mathematics.Matrix4.Identity);
            scene.SetPresentationEffectTransform(charge, OpenTK.Mathematics.Matrix4.Identity);
            if (scene.PresentationEffectOverrideCount != 2) throw new InvalidDataException("Override registered twice.");
            scene.ClearPresentationEffectTransforms();
            if (charge.DrawTransformOverride != null || muzzle.DrawTransformOverride != null)
                throw new InvalidDataException("Stale effect presentation transform.");
        }
        Console.WriteLine("[replayperf] Authority capture/encode byte parity and zero allocation; targeted effect reset passed.");
    }
    internal static void Run(Action<bool, string> check, ReplayMetadata metadata)
    {
        check(GameState.MatchFinalCameraSeconds == 5 && GameState.MatchEndingSeconds == 10
            && DedicatedServer.EndSequenceSeconds == 16, "shared final/results/intermission durations");
        check(KillcamController.PersonalReplayFrames == 300 && KillcamController.FinalReplayFrames == 300
            && KillcamController.PlaybackRate == 1, "five-second killcam footage at one times speed");
        var encoded = new ReplayAuthorityCaptureScratch();
        var authority = new ReplayAuthorityWorld { MatchId = 1, Epoch = 1, Tick = 1 };
        byte[] reference = authority.Encode();
        check(encoded.Encode(authority).SequenceEqual(reference), "reused authority encoder preserves bytes");
        for (int i = 0; i < 100; i++) encoded.Encode(authority);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) encoded.Encode(authority);
        check(GC.GetAllocatedBytesForCurrentThread() == allocated, "authority encoding allocates zero after warmup");
        Span<byte> packet = stackalloc byte[NetConfig.MaxPacketSize];
        int part = 0;
        foreach (byte[] expected in ReplayAuthorityWire.Packets(authority))
        {
            int length = ReplayAuthorityWire.WritePacket(authority, reference, part++, packet);
            check(packet[..length].SequenceEqual(expected), "span fragments preserve wire bytes");
        }
        string directory = Path.Combine(Path.GetTempPath(), "prime-write-pump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string asyncPath = Path.Combine(directory, "async.ppdemo"), syncPath = Path.Combine(directory, "sync.ppdemo");
            var pump = new ReplayWritePump(asyncPath, metadata, 0);
            using (var sync = new ReplayWriterV3(syncPath, metadata))
            {
                for (uint frame = 0; frame <= 360; frame++)
                {
                    check(pump.EndFrame(frame), "healthy queue accepts frame");
                    ReplayTimelineArchive.EndFrame(sync, frame);
                }
            }
            pump.Complete(); check(pump.Completion.Wait(10000), "writer finalizes off producer");
            check(pump.Error == null && File.ReadAllBytes(asyncPath).AsSpan().SequenceEqual(File.ReadAllBytes(syncPath)),
                "worker output is byte-identical to synchronous writer");
            // Both production sinks use this pump. Exercise count and byte backpressure independently.
            for (int sink = 0; sink < 2; sink++)
            {
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                int writes = 0;
                string path = Path.Combine(directory, $"slow-{sink}.ppdemo");
                int producerThread = Environment.CurrentManagedThreadId;
                bool wrongThread = false;
                var slow = new ReplayWritePump(path, metadata, 0, capacity: 512,
                    maximumBytes: sink == 0 ? ReplayWritePump.MaximumBytes : 512 * 128,
                    beforeWrite: () =>
                    {
                        wrongThread |= Environment.CurrentManagedThreadId == producerThread;
                        if (++writes == 302) { entered.Set(); release.Wait(); }
                    });
                try
                {
                    for (uint frame = 0; frame < 302; frame++) check(slow.EndFrame(frame), "slow writer initial stream");
                    check(entered.Wait(10000), "storage reaches held boundary");
                    long start = Stopwatch.GetTimestamp();
                    uint next = 302;
                    while (slow.EndFrame(next++)) check(next < 10000, "queue has finite capacity");
                    check(Stopwatch.GetElapsedTime(start).TotalSeconds < 1, "producer never waits for held storage");
                    check(slow.Error != null && slow.QueuedBytes <= ReplayWritePump.MaximumBytes, "overflow fails bounded recording");
                    slow.Complete(); // also must return while storage is held
                }
                finally { release.Set(); }
                check(slow.Completion.Wait(10000) && !wrongThread && slow.QueuedBytes == 0, "worker drains leases after abort");
                check(ReplayArchive.Recover(path + ".part", out string? recovered, out _) && recovered != null
                    && ReplayArchive.Validate(recovered) == ReplayOpenResult.Success, "slow writer partial chunks recover");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
