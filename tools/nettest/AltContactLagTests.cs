using System;
using System.Diagnostics;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;
internal static class AltContactLagTests
{
    public static void Run()
    {
        var body = new HistoricalBody(1, Vector3.Zero, .5f, 0, 0, HistoricalBodyType.AltSphere);
        var attack = new HistoricalAltAttackState(ContactAttackKind.Boost, true, new(2, 0, 0), new(-2, 0, 0), .5f,
            new(-.8f, 0, 0), new(.8f, 0, 0), 1, 2, true);
        foreach (var kind in new[] { ContactAttackKind.Boost, ContactAttackKind.Trace, ContactAttackKind.Weavel })
        {
            var a = attack with { Kind = kind };
            HistoricalPoseTests.Check(NetContactLagComp.Intersects(a, body, true)
                && !NetContactLagComp.Intersects(a, body, false), $"{kind} pass-through only sweep hits");
            HistoricalPoseTests.Check(!NetContactLagComp.Intersects(a, body with { Position = new(0, 2, 0) }, true), "no radius inflation");
            HistoricalPoseTests.Check(!NetContactLagComp.Intersects(a, body with { Position = new(3.1f, 0, 0) }, true), "no extension past sweep endpoint");
        }
        HistoricalPoseTests.Check(NetContactLagComp.Intersects(attack with { Center = new(1, 0, 0), PreviousCenter = new(2, 0, 0) }, body, true),
            "sweep retains touching endpoint after physical separation");
        foreach (bool left in new[] { true, false })
        {
            var a = attack with { Kind = ContactAttackKind.Spire, LeftRock = left ? Vector3.Zero : new(20), RightRock = left ? new(20) : Vector3.Zero };
            HistoricalPoseTests.Check(NetContactLagComp.Intersects(a, body, true), "Spire independent left/right extremes");
        }
        var noxus = attack with { Kind = ContactAttackKind.Noxus, Center = Vector3.Zero };
        HistoricalPoseTests.Check(NetContactLagComp.Intersects(noxus, body with { Position = new(2.29f, 0, 0) }, true)
            && !NetContactLagComp.Intersects(noxus, body with { Position = new(2.31f, 0, 0) }, true)
            && !NetContactLagComp.Intersects(noxus, body with { Position = new(0, .51f, 0) }, true), "Noxus original radial and vertical bounds");
        var previous = attack with { Center = attack.PreviousCenter };
        foreach (var invalid in new[] { previous with { Life = 2 }, previous with { Generation = 3 },
            previous with { InPlay = false }, previous with { AltForm = false }, previous with { Center = new(-30, 0, 0) } })
            HistoricalPoseTests.Check(NetContactLagComp.WithSweep(attack, invalid, true).PreviousCenter == attack.Center,
                "life/generation/death/spectator/form/teleport cannot bridge a sweep");
        HistoricalPoseTests.Check(NetContactLagComp.WithSweep(attack, previous, false).PreviousCenter == attack.Center,
            "disconnect/join/gap cannot bridge a sweep");
        HistoricalPoseTests.Check(!NetContactLagComp.Intersects(attack with { InPlay = false }, body, true)
            && !NetContactLagComp.Intersects(attack with { Kind = ContactAttackKind.None }, body, true), "inactive/frozen/non-attack rejected");
        HistoricalPoseTests.Check(NetContactLagComp.TargetFrame(100, 90, 128) == 90.5
            && NetContactLagComp.TargetFrame(100, 1, 0) == 55
            && NetContactLagComp.TargetFrame(100, 0, 0) == 100
            && NetContactLagComp.TargetFrame(100, 101, 0) == 100, "exact ACK fraction, bounded rewind and unavailable ACK fallback");
        for (int i = 0; i < 10000; i++) NetContactLagComp.Intersects(attack, body, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp(); int hits = 0;
        for (int i = 0; i < 100000; i++) if (NetContactLagComp.Intersects(attack, body, true)) hits++;
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        HistoricalPoseTests.Check(allocated == 0 && hits == 100000, "100000 production contact queries allocate zero bytes");
        Console.WriteLine($"CONTACT PERFORMANCE 100000 queries: {milliseconds:F2} ms, {allocated} allocated bytes; 56 pairs: {milliseconds * 56 / 100000:F4} ms");
    }
}
