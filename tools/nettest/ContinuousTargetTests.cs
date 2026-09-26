using System;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;
internal static class ContinuousTargetTests
{
    private static void Check(bool ok, string label) => NetArchitectureTests.Check(ok, label);
    public static int Run()
    {
        try
        {
            Check(NetConfig.ProtocolVersion == 23 && IntentPacket.FullSize == 102, "protocol 23 preserves intent layout");
            Span<byte> bytes = stackalloc byte[102];
            foreach (byte slot in new byte[] { 0x80, 0x81, 0x88 })
            {
                var identity = new NetTargetIdentity(slot, slot == 0x80 ? (ushort)0 : ushort.MaxValue, slot == 0x80 ? (ushort)0 : (ushort)42);
                var packet = new IntentPacket { Target = identity, ChargeLevel = 50, BoostDamage = 10, ShotFlags = 3,
                    MoveX = 12, MoveY = -13, ContinuousFireTick = 0x10203040,
                    HasAnalogMove = true, HasContinuousFireTick = true };
                packet.Write(bytes);
                Check(bytes[91] == slot && bytes[92] == (byte)identity.Generation && bytes[94] == (byte)identity.LifeId,
                    "independent wire offsets");
                var decoded = IntentPacket.Read(bytes);
                Check(decoded.Target == identity && identity.IsWellFormed
                    && decoded.ContinuousFireTick == 0x10203040 && decoded.HasContinuousFireTick,
                    "fenced identity and firing tick roundtrip");
            }
            Check(!default(NetTargetIdentity).IsSupplied && NetTargetIdentity.None.IsSupplied && !NetTargetIdentity.None.HasPlayer,
                "missing differs from explicit none");
            Check(!new NetTargetIdentity(0x89, 1, 1).IsWellFormed && !new NetTargetIdentity(0x81, 0, 1).IsWellFormed,
                "invalid slots and zero generation rejected");
            Check(!IntentPacket.Read(bytes[..92]).HasState, "protocol 18 tail cannot masquerade as v19 state");
            var a = new NetTargetIdentity(0x82, 7, 4); var b = new NetTargetIdentity(0x83, 8, 5);
            var state = new ContinuousTargetState();
            state.Observe(a, 10, 8); Check(state.Status == ContinuousTargetStatus.Acquired, "acquire");
            state.Observe(a, 11, 8); Check(state.Status == ContinuousTargetStatus.Held && state.AcquiredFrame == 10, "hold same fresh report");
            state.Observe(b, 12, 10); Check(state.Status == ContinuousTargetStatus.Changed, "A to B");
            state.Observe(NetTargetIdentity.None, 13, 11); Check(state.Status == ContinuousTargetStatus.Lost, "loss drops prior target");
            state.Observe(a, 14, 12); Check(state.Status == ContinuousTargetStatus.Acquired, "re-entry");
            ContinuousTargetRejection Eval(Vector3 target, bool alt = false, bool morph = false, bool active = true, bool allied = false) =>
                NetContinuousTargeting.Evaluate(Vector3.Zero, Vector3.UnitZ, target, active, true, allied, alt, morph,
                    10, .9f, out _, out _, out _);
            Check(Eval(new(0, 0, 5)) == ContinuousTargetRejection.None, "single target");
            Check(Eval(new(0, 0, 100)) == ContinuousTargetRejection.None, "preserve continuous distance cone, no invented hard range");
            Check(Eval(new(5, 0, 5)) == ContinuousTargetRejection.Angle, "leaves cone");
            Check(Eval(new(0, 0, 5), active: false) == ContinuousTargetRejection.Eligibility, "dead/spectator eligibility");
            Check(Eval(new(0, 0, 5), allied: true) == ContinuousTargetRejection.Team, "friendly targeting prohibited");
            Check(Eval(Vector3.Zero) == ContinuousTargetRejection.Range && Eval(new(float.NaN, 0, 5)) == ContinuousTargetRejection.Range,
                "degenerate and nonfinite geometry");
            foreach (bool alt in new[] { true, false }) foreach (bool morph in new[] { true, false })
                Check(Eval(new(0, 0, 5), alt, morph) == ContinuousTargetRejection.None, "all forms retain centerline target");
            Check(Eval(new(.15f, 0, .5f), alt: true) == ContinuousTargetRejection.Form
                && Eval(new(.15f, 0, .5f)) == ContinuousTargetRejection.None, "cartridge form-specific cone");
            var owner = new NetContinuousTargetDiagnostics.Evaluation { Selected = a, Phase = 10, Eligible = true };
            var authority = owner;
            Check(NetContinuousTargetDiagnostics.Compare(owner, authority) == ContinuousTargetDivergence.None, "paired traces agree");
            authority.Selected = b; Check(NetContinuousTargetDiagnostics.Compare(owner, authority) == ContinuousTargetDivergence.DifferentPlayer, "different player classification");
            authority = owner; authority.Selected = a with { LifeId = 5 };
            Check(NetContinuousTargetDiagnostics.Compare(owner, authority) == ContinuousTargetDivergence.WrongLife, "life classification");
            authority = owner; authority.CollisionTest = true;
            Check(NetContinuousTargetDiagnostics.Compare(owner, authority) == ContinuousTargetDivergence.CollisionDisagreement, "collision classification");
            authority = owner; authority.Phase++;
            Check(NetContinuousTargetDiagnostics.Compare(owner, authority) == ContinuousTargetDivergence.PhaseDisagreement, "phase classification");
            HealthShotTests.Session();
            foreach (var profile in new[] { (0,0), (100,20), (250,40), (320,80), (400,80) })
            foreach (double loss in new[] { 0, .01, .02, .05 })
            {
                var queue = new NetFaultQueue<IntentPacket>(431, profile.Item1 / 2.0, profile.Item2, loss, .03, .01);
                uint newest = 0; NetTargetIdentity expected = default;
                for (uint frame = 1; frame <= 500; frame++)
                {
                    double now = frame * 1000.0 / 60;
                    if (frame <= 400)
                    {
                        var packet = new IntentPacket { Frame = frame, MatchId = 51, AuthorityEpoch = 4,
                            SlotGeneration = 10, LifeId = 7, HasState = true,
                            Target = frame < 100 ? a : frame < 200 ? b : frame < 250 ? NetTargetIdentity.None : a };
                        // Explicit loss of change and loss packets plus 400ms pump stall.
                        if (frame != 100 && frame != 200) queue.Enqueue(now, packet);
                    }
                    if (frame >= 300 && frame < 324) continue;
                    while (queue.TryDequeue(now, out var packet))
                    {
                        if (newest == 0 || NetLifecycleTracker.Newer(packet.Frame, newest)) { newest = packet.Frame; expected = packet.Target; }
                        NetSession.AcceptSlotIntent(1, packet);
                        Check(NetSession.RemoteIntents[1].Target == expected, "production stream rejects reordered/duplicate decisions");
                    }
                }
                Check(newest > 390 && expected == a, "latest repeated target converges after impairment");
                NetSession.ForgetSlot(1);
            }
            for (int i = 0; i < 10000; i++) Eval(new(0, 0, 5));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) Eval(new(0, 0, 5));
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "zero warmed predicate allocations");
            Console.WriteLine("PASS: protocol 19, targeting geometry, retention, classified traces, 20 impairment profiles, zero warmed allocations");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetSession.Stop(); }
    }
}
