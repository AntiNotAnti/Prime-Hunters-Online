using System;
using System.IO;

namespace MphRead.Mods.Network;

/// <summary>Compare every visible gameplay and presentation frame of a clip with
/// its source, including the initial world and hidden legacy warmup.</summary>
internal static class ReplayClipFidelity
{
    public static int Run(string source, string clip, uint sourceStart)
    {
        Headless.Enter();
        string tracePath = Path.Combine(Path.GetTempPath(), "prime-clip-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var clipPlayer = ReplayDeterminism.Open(clip);
            uint duration = clipPlayer.Current.Session.LastFrame;
            if (clipPlayer.Current.Session.Metadata?.Type != ReplayType.Clip)
                throw new InvalidDataException("Comparison file is not a clip.");
            uint sourceEnd = checked(sourceStart + duration);
            using var trace = new FileStream(tracePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.DeleteOnClose);
            using (var original = ReplayDeterminism.Open(source))
            {
                if (sourceEnd > original.Current.Session.LastFrame) throw new InvalidDataException("Clip extends past source EOF.");
                original.Seek(sourceStart);
                while (!original.Ready) original.Update();
                void Capture(Scene scene)
                {
                    uint frame = original.Current.Session.CurrentFrame;
                    if (frame > sourceEnd) return;
                    uint normalized = frame - sourceStart;
                    if (trace.Position != normalized * 64L) throw new InvalidDataException("Source skipped a frame.");
                    trace.Write(ReplayDeterminism.Projection(scene, normalized));
                }
                Capture(original.Current.Scene); original.Stepped += Capture;
                original.Seek(sourceEnd);
                while (!original.Ready) original.Update();
            }
            trace.Position = 0;
            void Compare(Scene scene)
            {
                uint frame = clipPlayer.Current.Session.CurrentFrame;
                if (trace.Position != frame * 64L) throw new InvalidDataException("Clip skipped a frame.");
                Span<byte> expected = stackalloc byte[64]; trace.ReadExactly(expected);
                if (!ReplayDeterminism.Projection(scene, frame).AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException($"First clip divergence: source {sourceStart + frame}, clip {frame}.");
            }
            Compare(clipPlayer.Current.Scene); clipPlayer.Stepped += Compare;
            clipPlayer.Transport.Play();
            while (!clipPlayer.Current.Session.AtEnd) clipPlayer.Update();
            if (trace.Position != ((long)duration + 1) * 64) throw new InvalidDataException("Clip ended early.");
            Console.WriteLine($"[replayclipcheck] PASS: {duration + 1} gameplay/presentation frames; clip 0-{duration} matches source {sourceStart}-{sourceEnd}.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replayclipcheck] FAIL: " + ex); return 1; }
    }
}
