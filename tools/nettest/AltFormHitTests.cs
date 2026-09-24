using System;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;
internal static class AltFormHitTests
{
    public static int Run()
    {
        try
        {
            HistoricalPoseTests.Run();
            foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Noxus, Hunter.Trace, Hunter.Sylux, Hunter.Weavel })
            {
                var volume = PlayerEntity.PlayerVolumes[(int)hunter, 2];
                var center = volume.SpherePosition;
                var body = new HistoricalBody(1, center, volume.SphereRadius, 0, 0, HistoricalBodyType.AltSphere);
                var bodies = new[] { body };
                var hit = NetHistoricalTrace.Trace(bodies, center - Vector3.UnitX * 3, center + Vector3.UnitX * 3, .01f);
                HistoricalPoseTests.Check(hit.Slot == 1 && !hit.Head, $"{hunter} historical alt hit has no headshot");
                var miss = NetHistoricalTrace.Trace(bodies, center + new Vector3(-3, 3, 0), center + new Vector3(3, 3, 0), .01f);
                HistoricalPoseTests.Check(miss.Slot == -1, $"{hunter} alt visible miss");
                bodies[0] = body with { Position = center + new Vector3(5, 5, 0) };
                HistoricalPoseTests.Check(NetHistoricalTrace.Trace(bodies, center - Vector3.UnitX * 3, center + Vector3.UnitX * 3, .01f).Slot == -1,
                    $"{hunter} lateral/vertical movement differs from history");
            }
            var chain = new HistoricalBody(2, Vector3.Zero, .15f, 0, 0, HistoricalBodyType.KandenChain,
                new(new(.6f, 0, 0), new(1.2f, 0, 0), new(1.8f, 0, 0)));
            foreach (float x in new[] { 0, .6f, 1.2f, 1.8f })
            {
                var hit = NetHistoricalTrace.Trace(new[] { chain }, new(x, 0, -2), new(x, 0, 2), .01f);
                HistoricalPoseTests.Check(hit.Slot == 2 && !hit.Head, "Kanden head/middle/rear historical intersection");
            }
            AltContactLagTests.Run();
            Console.WriteLine("PASS: alt-form poses, all hunter beams, Kanden chain, contact sweeps and lifecycle boundaries");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
