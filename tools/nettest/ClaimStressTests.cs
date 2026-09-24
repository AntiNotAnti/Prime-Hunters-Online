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
            NetHitClaims.Reset();
            for (int shooter = 0; shooter < 8; shooter++)
                for (int i = 0; i < NetHitClaims.PendingPerShooter; i++)
                    Call("Park", shooter, new HitClaimPacket { ClaimId = (ushort)(i + 1), VictimSlot = 1 });
            NetArchitectureTests.Check(NetHitClaims.ClaimsPendingCurrent == 512, "isolated per-shooter storage");
            Call("Park", 0, new HitClaimPacket { ClaimId = 65, VictimSlot = 1 });
            NetArchitectureTests.Check(NetHitClaims.ClaimsCapacityRefused == 1, "capacity is explicit");
            NetArchitectureTests.Check(NetHitClaims.ResolvedLedgerOverwrittenUnused == 0, "no unmatched overwrite");
            Console.WriteLine("PASS: claim retention, multiplicity, exact matching, shooter isolation and capacity refusal");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetSession.Stop(); }
    }
}
