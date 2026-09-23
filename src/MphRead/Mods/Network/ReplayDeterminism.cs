using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Disk-backed, per-frame verification of the production private player.
/// The trace bounds memory independently of recording duration.</summary>
internal static class ReplayDeterminism
{
    internal static PassiveReplayPlayer Open(string path)
    {
        var player = new PassiveReplayPlayer(path, new Vector2i(256, 192));
        try
        {
            player.Seek(0);
            while (!player.Ready) player.Update();
            return player;
        }
        catch { player.Dispose(); throw; }
    }
    internal static byte[] Projection(Scene scene, uint frame)
        => Convert.FromHexString(ReplayStateHash.Compute(scene, frame) + scene.ReplayPresentationHash(frame));

    public static int Run(string path, string? hashOutput = null)
    {
        Headless.Enter();
        string tracePath = Path.Combine(Path.GetTempPath(), "prime-replay-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new FileStream(tracePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.DeleteOnClose);
            var expected = new List<ReplayExpectedHash>();
            uint duration;
            using (var linear = Open(path))
            {
                duration = linear.Current.Session.LastFrame;
                void Capture(Scene scene)
                {
                    uint frame = linear.Current.Session.CurrentFrame;
                    if (trace.Position != frame * 64L) throw new InvalidDataException($"Missing/duplicate frame {frame}.");
                    byte[] projection = Projection(scene, frame); trace.Write(projection);
                    if (frame % 300 == 0 || frame == duration) expected.Add(new(frame, Convert.ToHexString(projection.AsSpan(0, 32))));
                }
                Capture(linear.Current.Scene); linear.Stepped += Capture;
                linear.Transport.Play();
                while (!linear.Current.Session.AtEnd) linear.Update();
            }
            trace.Flush();
            void Compare(Scene scene, uint frame)
            {
                trace.Position = frame * 64L;
                Span<byte> reference = stackalloc byte[64]; trace.ReadExactly(reference);
                if (!Projection(scene, frame).AsSpan().SequenceEqual(reference))
                    throw new InvalidOperationException($"First gameplay/presentation divergence at frame {frame}.");
            }
            var random = new Random(2718);
            uint[] targets = new[] { 0u, Math.Min(60u, duration), Math.Min(300u, duration), duration / 3, duration * 2 / 3, duration }
                .Concat(Enumerable.Range(0, 8).Select(_ => (uint)random.NextInt64(duration + 1L))).Distinct().ToArray();
            foreach (uint target in targets)
            {
                using var player = Open(path);
                Compare(player.Current.Scene, 0);
                player.Stepped += scene => Compare(scene, player.Current.Session.CurrentFrame);
                player.Seek(target);
                while (!player.Ready) player.Update();
                Compare(player.Current.Scene, target);
            }
            // Exercise already populated checkpoint caches, including backward seeks.
            using (var cached = Open(path))
            {
                cached.Seek(duration);
                while (!cached.Ready) cached.Update();
                foreach (uint target in targets.Reverse())
                {
                    cached.Seek(target);
                    while (!cached.Ready) cached.Update();
                    Compare(cached.Current.Scene, target);
                }
            }
            foreach (float rate in new[] { .25f, .5f, 1f, 2f, 4f })
            {
                using var player = Open(path);
                player.Stepped += scene => Compare(scene, player.Current.Session.CurrentFrame);
                player.Transport.SetPlaybackRate(rate); player.Transport.Play();
                while (!player.Current.Session.AtEnd) player.Update();
                Compare(player.Current.Scene, duration);
                byte[] before = Projection(player.Current.Scene, duration);
                for (int i = 0; i < 20; i++) player.Update();
                if (!Projection(player.Current.Scene, duration).AsSpan().SequenceEqual(before))
                    throw new InvalidDataException("Frozen EOF advanced.");
            }
            // The checker must detect and identify a corrupt reference frame.
            using (var probe = Open(path))
            {
                trace.Position = 0; int original = trace.ReadByte(); trace.Position = 0; trace.WriteByte((byte)(original ^ 1));
                bool detected = false;
                try { Compare(probe.Current.Scene, 0); }
                catch (InvalidOperationException ex) when (ex.Message.EndsWith("frame 0.", StringComparison.Ordinal)) { detected = true; }
                trace.Position = 0; trace.WriteByte((byte)original);
                if (!detected) throw new InvalidDataException("The checker accepted a damaged reference hash.");
            }
            if (hashOutput != null)
            {
                var result = ReplayArchive.WithExpectedHashes(path, hashOutput, expected);
                if (result != ReplayOpenResult.Success) throw new IOException($"Could not write expected hashes: {result}.");
            }
            Console.WriteLine($"[replaydeterminism] PASS: {duration + 1} gameplay/presentation frames, {targets.Length} cold and cached seeks, all five rates, frozen EOF and divergence detection.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaydeterminism] FAIL: " + ex); return 1; }
    }
}
