using System;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;
internal static class LagCompensationTests
{
    public static int Run()
    {
        try
        {
            NetArchitectureTests.Check(LagCompensationPolicy.Plausibility == LagCompPlausibility.Shadow, "shadow default");
            int shots = 0;
            foreach (int rtt in new[] { 0, 50, 100, 150, 250, 320, 400 })
            foreach (int jitter in new[] { 0, 20, 40, 80 })
            foreach (double delay in new[] { 1.25, 2, 6, 8 })
            foreach (int age in new[] { 0, 3, 7 })
            foreach (double requested in new[] { 0, 1.125, 12.75, 44.875, 45, 75, 128 })
            {
                var timing = new LagTiming(rtt, jitter, rtt, delay);
                var result = LagCompensationPolicy.Evaluate(requested, timing, age);
                NetArchitectureTests.Check(result.HardAppliedFrames == Math.Min(45, requested)
                    && LagCompensationPolicy.Applied(result) == Math.Min(45, requested), "shadow preserves fractional rewind");
                NetArchitectureTests.Check(result.ShadowAllowedFrames >= 0 && result.ShadowAllowedFrames <= 45
                    && result.FramesShadowRefused >= 0, "bounded plausibility");
                LagCompensationPolicy.SetTiming(1, timing);
                LagCompensationPolicy.Record(1, 0, result, ShadowOutcome.HistoricalDataUnavailable); shots++;
            }
            var normal = LagCompensationPolicy.Evaluate(30, new(100, 0, 100, 2), 0);
            NetArchitectureTests.Check(normal.ShadowAllowedFrames == 10, "full RTT plus actual delay and scheduling");
            NetArchitectureTests.Check(LagCompensationPolicy.Evaluate(40, new(1000, 5, 100, 2), 0).ShadowAllowedFrames < 15,
                "one delayed ACK cannot expand compensation dramatically");
            NetArchitectureTests.Check(LagCompensationPolicy.Evaluate(30, default, 0).ShadowAllowedFrames == null,
                "missing timing explicit, no assumed delay");
            byte[] bytes = new byte[PeerTimingPacket.Size];
            new PeerTimingPacket(1, 2, 7.5f).Write(bytes);
            NetArchitectureTests.Check(PeerTimingPacket.TryRead(bytes, out var packet) && packet.DelayFrames == 7.5f, "actual delay round trip");
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -1, 9 })
            { new PeerTimingPacket(1, 2, invalid).Write(bytes); NetArchitectureTests.Check(!PeerTimingPacket.TryRead(bytes, out _), "invalid delay rejected"); }
            var body = new HistoricalBody(1, Vector3.Zero, .5f, 0, 2);
            var bodies = new[] { body };
            var head = NetHistoricalTrace.Trace(bodies, new(-3, 1.9f, 0), new(3, 1.9f, 0), .1f);
            var torso = NetHistoricalTrace.Trace(bodies, new(-3, 1, 0), new(3, 1, 0), .1f);
            var miss = NetHistoricalTrace.Trace(bodies, new(-3, 3, 0), new(3, 3, 0), .1f);
            NetArchitectureTests.Check(head.Slot == 1 && head.Head && torso.Slot == 1 && !torso.Head && miss.Slot == -1
                && bodies[0] == body, "production collision primitive, read-only bodies and head/body");
            var cases = new[] { (head, head, ShadowOutcome.SameOutcome), (head, miss, ShadowOutcome.ExistingHit_ShadowMiss),
                (head, torso, ShadowOutcome.ExistingHead_ShadowBody), (torso, head, ShadowOutcome.ExistingBody_ShadowHead),
                (miss, head, ShadowOutcome.ExistingMiss_ShadowHit), (head, head with { Slot = 2 }, ShadowOutcome.DifferentVictim),
                (head, default(HistoricalHit), ShadowOutcome.HistoricalDataUnavailable) };
            foreach (var (a, b, expected) in cases) NetArchitectureTests.Check(NetHistoricalTrace.Compare(a, b) == expected, "outcome classification");
            LagCompensationPolicy.Reset();
            NetArchitectureTests.Check(LagCompensationPolicy.Capture(1, 0, 0, 0, 0, ShadowOutcome.SameOutcome).Shots == 0, "match reset");
#if !DEBUG
            NetArchitectureTests.Check(!LagCompensationPolicy.Configure("enforce"), "release enforcement refused");
#endif
            Console.WriteLine($"PASS: {shots} shadow timing profiles, fractional equivalence, pure geometry and outcome categories");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
