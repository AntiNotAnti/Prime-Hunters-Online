using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Mods.Network;

namespace MphRead.NetTest;
internal static class WeaponPolicyTests
{
    public static int Run()
    {
        try
        {
            int profiles = 0; var weapons = new HashSet<BeamType>();
            foreach (var mechanics in Weapons.WeaponsMP)
            {
                weapons.Add(mechanics.Beam);
                foreach (bool charged in new[] { false, true })
                {
                    var policy = WeaponLagPolicies.Resolve(mechanics, charged);
                    Console.WriteLine($"{mechanics.Beam} charged={charged}: {policy.Mode}, cap={policy.MaximumCatchUpFrames}, homing={mechanics.UnchargedHoming}/{mechanics.MinChargeHoming}/{mechanics.ChargedHoming}");
                    foreach (int rtt in new[] { 0, 50, 150, 250, 320 })
                    foreach (int jitter in new[] { 0, 40, 80 })
                    foreach (int loss in new[] { 0, 2, 5 })
                    {
                        double requested = rtt * .06 + jitter * .06 + 1.25 + (loss == 0 ? 0 : 7);
                        double oldRewind = Math.Min(NetUnlagged.MaxRewindFrames, requested);
                        NetArchitectureTests.Check(policy.CatchUpFrames(oldRewind) == (policy.Mode == LagCompensationMode.AreaHistorical ? 0 : (int)Math.Ceiling(oldRewind))
                            && policy.CatchUpFrames(100000) <= NetUnlagged.MaxRewindFrames,
                            "old and policy catch-up bounds equivalent for every multiplayer variant");
                        NetArchitectureTests.Check(policy.UsesHistoricalPlayers && !policy.UsesHistoricalDynamicGeometry
                            && policy.AllowPressAge && policy.UseShadowPlausibility, "existing timing behavior preserved"); profiles++;
                    }
                    if (mechanics.Flags.TestFlag(WeaponFlags.Continuous))
                        NetArchitectureTests.Check(policy.Mode == LagCompensationMode.Continuous, "continuous precedence");
                    if (mechanics.Beam == BeamType.Imperialist)
                        NetArchitectureTests.Check(policy.Mode == LagCompensationMode.HistoricalTrace && policy.ProjectileCatchUp, "Imperialist remains a processed projectile");
                }
            }
            NetArchitectureTests.Check(weapons.Count == 9, "all nine multiplayer weapons covered");
            NetArchitectureTests.Check(WeaponLagPolicies.CurrentVolume.CatchUpFrames(45) == 0, "melee and area stay on current timeline");
            NetArchitectureTests.Check(typeof(BeamProjectileEntity).GetField("<Target>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "homing target retains the explicit replay checkpoint field contract");
            HealthShotTests.Session();
            var target = HealthShotTests.Player(1);
            var beam = (BeamProjectileEntity)RuntimeHelpers.GetUninitializedObject(typeof(BeamProjectileEntity));
            typeof(EntityBase).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(beam, typeof(EntityBase).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));
            beam.Target = target;
            var validate = typeof(BeamProjectileEntity).GetMethod("ValidateHomingTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            validate.Invoke(beam, null);
            NetArchitectureTests.Check(beam.Target == target, "homing retains same target life");
            HealthShotTests.Snapshot(2, HealthShotTests.State(8));
            validate.Invoke(beam, null);
            NetArchitectureTests.Check(beam.Target == null, "homing cannot follow a respawned target through an old object reference");
            beam.Target = target;
            NetPlayerLifecycle.SetOccupant(1, 11);
            validate.Invoke(beam, null);
            NetArchitectureTests.Check(beam.Target == null, "homing cannot inherit a reused slot");
            NetSession.Stop();
            Console.WriteLine(ServerSim.Available(out string reason) ? "Asset-backed simulation available" : $"Asset-backed simulation unavailable: {reason}");
            Console.WriteLine($"PASS: {profiles} weapon timing profiles, all MP variants, bounded catch-up equivalence");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NetSession.Stop(); }
    }
}
