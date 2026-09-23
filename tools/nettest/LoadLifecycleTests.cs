using System;
using MphRead.Mods.Network;

namespace MphRead.NetTest;

internal static class LoadLifecycleTests
{
    public static int Run()
    {
        try
        {
            foreach (double lag in new[] { 0.0, .1, 2, 10 })
            {
                var start = new NetMatchStart(); start.Begin(42, 9, 255); var identity = start.Identity;
                start.MarkLoaded(0, identity);
                NetArchitectureTests.Check(!start.Advance(1), "server preparation gates countdown");
                start.AuthorityReady(1);
                for (int slot = 1; slot < 7; slot++) start.MarkLoaded(slot, identity);
                NetArchitectureTests.Check(!start.MarkLoaded(7, identity with { MatchId = 41 })
                    && !start.MarkLoaded(7, identity with { AuthorityEpoch = 8 })
                    && !start.MarkLoaded(7, identity with { StartGeneration = 99 }), "load identity fenced");
                NetArchitectureTests.Check(!start.Advance(1 + lag), "slow participant holds loading barrier");
                NetArchitectureTests.Check(start.MarkLoaded(7, identity), "last healthy participant accepted");
                NetArchitectureTests.Check(!start.MarkLoaded(7, identity), "duplicate loaded idempotent");
                NetArchitectureTests.Check(start.Advance(1 + lag) && start.Stage == StartStage.Countdown, "countdown follows readiness");
                NetArchitectureTests.Check(!start.Advance(2.49 + lag), "no early simulation");
                NetArchitectureTests.Check(start.Advance(2.5 + lag) && start.Stage == StartStage.InMatch, "one authoritative start boundary");
                start.Begin(43, 9, 255);
                NetArchitectureTests.Check(start.Identity.StartGeneration != identity.StartGeneration && !start.MarkLoaded(0, identity), "rematch refuses old ACK");
            }
            var missing = new NetMatchStart(); missing.Begin(1, 1, 7); missing.AuthorityReady(0);
            missing.MarkLoaded(0, missing.Identity); missing.MarkLoaded(1, missing.Identity);
            NetArchitectureTests.Check(missing.MissingAtDeadline(14.9) == 0 && missing.MissingAtDeadline(15) == 4, "15 second deadline identifies only missing participant");
            missing.Remove(2); missing.Advance(15);
            NetArchitectureTests.Check(missing.Expected == 3 && missing.Stage == StartStage.Countdown, "timeout removal preserves healthy participants");
            missing.Remove(0);
            NetArchitectureTests.Check(missing.Expected == 2 && missing.Loaded == 2, "owner disconnect cannot poison countdown");
            NetArchitectureTests.Check(!missing.MarkLoaded(3, missing.Identity), "new join cannot enlarge frozen barrier");
            var bytes = new byte[MatchLoadedPacket.Size];
            new MatchLoadedPacket(42, 9, 17).Write(bytes);
            NetArchitectureTests.Check(MatchLoadedPacket.TryRead(bytes, out var loaded) && loaded.Identity == new MatchStartIdentity(42, 9, 17), "load wire identity");
            for (int size = 0; size < bytes.Length; size++) NetArchitectureTests.Check(!MatchLoadedPacket.TryRead(bytes.AsSpan(0, size), out _), "truncated load rejected");
            Console.WriteLine("PASS: ready barrier, slow loaders, generation, failures, disconnect and rematch");
            return NetLobbyTest.Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
