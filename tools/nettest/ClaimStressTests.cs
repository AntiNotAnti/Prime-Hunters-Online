using System;
using System.Reflection;
using MphRead.Mods.Network;

namespace MphRead.NetTest;
internal static class ClaimStressTests
{
    private static object? Call(string name, params object[] args) => typeof(NetHitClaims)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static void Frame(uint frame) => typeof(NetSession).GetProperty("NetFrame")!.SetValue(null, frame);
    public static int Run()
    {
        try
        {
            HealthShotTests.Session(); NetHitClaims.Reset();
            // 36 unconsumed Shock Coil resolutions survive a full 72-frame grace.
            for (uint frame = 1; frame <= 71; frame += 2)
            { Frame(frame); Call("NoteLedger", 0, 1, frame, frame, 1, false); }
            Frame(72);
            for (uint frame = 1; frame <= 71; frame += 2)
            {
                object[] args = { 0, 1, frame, frame, 72u, 72, 0 };
                NetArchitectureTests.Check((bool)Call("TakeLedger", args)!, "sustained exact hit retained");
                NetArchitectureTests.Check((int)args[6] == 1, "authority damage retained");
                NetArchitectureTests.Check(!(bool)Call("TakeLedger", args)!, "resolution consumed once");
            }
            Call("NoteLedger", 0, 1, 72u, 70u, 32, false);
            Call("NoteLedger", 0, 1, 72u, 70u, 20, false);
            object[] splash = { 0, 1, 72u, 70u, 72u, 72, 0 };
            NetArchitectureTests.Check((bool)Call("TakeLedger", splash)! && (int)splash[6] == 32, "direct hit");
            NetArchitectureTests.Check((bool)Call("TakeLedger", splash)! && (int)splash[6] == 20, "splash hit");
            NetArchitectureTests.Check(!(bool)Call("TakeLedger", splash)!, "no third hit");
            Call("NoteLedger", 0, 1, 72u, 71u, 128, false);
            NetArchitectureTests.Check(!(bool)Call("TakeLedger", splash)!, "conflicting valid launches never fall back");
            NetHitClaims.Reset(); Frame(100);
            Call("NoteLedger", 0, 1, 80u, 79u, 3, false);
            var phases = (uint[,,])typeof(NetHitClaims).GetField("_authorityContinuousPhase", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            phases[0, 1, 0] = 60;
            object[] continuous = { 0, 1, 60u, 0 };
            NetArchitectureTests.Check((bool)Call("TakeContinuousLedger", continuous)! && (int)continuous[3] == 3,
                "continuous source tick matches independently of displayed-world launch frame");
            NetArchitectureTests.Check((bool)Call("TakeContinuousLedger", continuous)!, "same tick reuses terminal resolution");
            NetArchitectureTests.Check(NetHitClaims.ContinuousAlreadyResolved(0, 1, 60)
                && !NetHitClaims.ContinuousAlreadyResolved(0, 1, 61), "late physical copy suppressed only for exact continuous tick");
            for (uint launch = 100; launch < 164; launch++) Call("NoteLedger", 0, 1, 100u, launch, 1, false);
            NetArchitectureTests.Check(NetHitClaims.ContinuousAlreadyResolved(0, 1, 60)
                && NetHitClaims.ResolvedLedgerCapacityRefused == 1,
                "settled continuous tick stays protected under ledger pressure until grace expires");
            Frame(100 + (uint)NetHitClaims.MaxGraceFrames + 1);
            NetArchitectureTests.Check(!NetHitClaims.ContinuousAlreadyResolved(0, 1, 60), "continuous identity expires with authoritative ledger");
            NetHitClaims.Reset();
            for (int shooter = 0; shooter < 8; shooter++)
                for (int i = 0; i < NetHitClaims.PendingPerShooter; i++)
                    Call("Park", shooter, new HitClaimPacket { ClaimId = (ushort)(i + 1), VictimSlot = 1 });
            NetArchitectureTests.Check(NetHitClaims.ClaimsPendingCurrent == 512, "isolated per-shooter storage");
            byte capacityVerdict = 255;
            NetHitClaims.VerdictSink = (int slot, ReadOnlySpan<(ushort Id, byte Result)> verdicts) =>
            { foreach (var verdict in verdicts) if (slot == 0 && verdict.Id == 65) capacityVerdict = verdict.Result; };
            Call("Park", 0, new HitClaimPacket { ClaimId = 65, VictimSlot = 1 });
            Call("FlushVerdicts");
            NetArchitectureTests.Check(capacityVerdict == HitVerdictPacket.ResultClaimCapacity, "full partition sends terminal capacity verdict");
            NetArchitectureTests.Check(NetHitClaims.ClaimsCapacityRefused == 1, "capacity is explicit");
            NetArchitectureTests.Check(NetHitClaims.ResolvedLedgerOverwrittenUnused == 0, "no unmatched overwrite");
            NetHitClaims.Reset(); Frame(80);
            for (uint launch = 1; launch <= 65; launch++) Call("NoteLedger", 0, 1, 80u, launch, 1, false);
            NetArchitectureTests.Check(NetHitClaims.ResolvedLedgerCapacityRefused == 1
                && NetHitClaims.ResolvedLedgerOverwrittenUnused == 0, "saturated resolution storage preserves all unused identities");
            Call("Park", 0, new HitClaimPacket { ClaimId = 66, VictimSlot = 1 });
            NetArchitectureTests.Check(NetHitClaims.ClaimsPendingCurrent == 0 && NetHitClaims.ClaimsCapacityRefused == 1,
                "unrecorded physical hit fences rescue until grace expires");
            Console.WriteLine("PASS: claim retention, multiplicity, exact matching, shooter isolation and capacity refusal");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetHitClaims.VerdictSink = null; NetSession.Stop(); }
    }
}
