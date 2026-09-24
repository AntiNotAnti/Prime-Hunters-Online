using System;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Real players/weapon data and the production selector in an eight-slot scene.</summary>
public static class NetContinuousTargetCheck
{
    private static int _checks;
    private static void Check(bool ok, string label)
    { _checks++; if (!ok) throw new InvalidOperationException(label); }
    public static int Run(string room)
    {
        var sim = new ServerSim();
        if (!sim.Start(room, GameMode.Battle, 8, _ => { }, () => { })) return 1;
        try
        {
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 1, AuthorityEpoch = 1, RoomKey = room, Mode = (byte)GameMode.Battle }, false);
            var roster = RosterPacket.Create(); roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.Count = 8;
            for (byte i = 0; i < 8; i++)
            { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Names[i] = $"COIL{i}"; roster.Hunters[i] = (byte)(i % 7); }
            NetSession.ApplyRoster(roster);
            for (int i = 0; i < 120; i++) sim.Step();
            Check(sim.StepFailures == 0, "eight-slot simulation bootstraps");
            var owner = PlayerEntity.Players[0]; var scene = owner.OwningScene;
            owner.ModArmWeapon(BeamType.ShockCoil); owner.EquipInfo.HomingTolerance = 4006;
            var beam = new BeamProjectileEntity(scene) { Owner = owner, Beam = BeamType.ShockCoil,
                Flags = BeamFlags.Continuous | BeamFlags.Homing, Position = new(0, .5f, 0), Velocity = Vector3.UnitZ };
            foreach (var p in PlayerEntity.Players) { p.Health = 99; p.ModForceForm(false); p.ModPlaceAt(new(50 + p.SlotIndex, 0, -5)); }
            var target = PlayerEntity.Players[1]; var other = PlayerEntity.Players[2];
            target.ModPlaceAt(new(0, 0, 5)); other.ModPlaceAt(new(.01f, 0, 5));
            var selector = typeof(BeamProjectileEntity).GetMethod("CheckHomingTargets", BindingFlags.Static | BindingFlags.NonPublic)!;
            void Local()
            {
                typeof(NetSession).GetProperty(nameof(NetSession.Role))!.SetValue(null, NetRole.Host);
                typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, 0);
                beam.Target = null; selector.Invoke(null, new object[] { beam, owner.EquipInfo, scene });
            }
            Local(); Check(beam.Target == target && owner.ModContinuousNetworkTarget == NetTargetIdentity.ForSlot(1), $"production owner captures actual selected player target={beam.Target?.Type}/{(beam.Target as PlayerEntity)?.SlotIndex} identity={owner.ModContinuousNetworkTarget} flags={target.LoadFlags} morph={target.IsMorphing} tolerance={owner.EquipInfo.HomingTolerance} weapon={owner.EquipInfo.Weapon.Beam} position={target.Position}");
            other.ModPlaceAt(target.Position);
            Local(); Check(beam.Target == target, "equal-angle tie uses slot order");
            var entities = (System.Collections.Generic.LinkedList<EntityBase>)typeof(Scene)
                .GetField("_entities", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
            var node = entities.Find(other)!; entities.Remove(node); entities.AddFirst(node);
            Local(); Check(beam.Target == target, "equal-angle tie survives reversed entity traversal");
            other.ModPlaceAt(new(.01f, 0, 5));
            uint frame = 500;
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)200);
            NetUnlagged.Record(200);
            ContinuousTargetRejection Remote(NetTargetIdentity identity, uint ack = 200)
            {
                typeof(NetSession).GetProperty(nameof(NetSession.Role))!.SetValue(null, NetRole.Server);
                typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, -1);
                var intent = new IntentPacket { Frame = ++frame, MatchId = 1, AuthorityEpoch = 1,
                    SlotGeneration = NetPlayerLifecycle.Generation(0), LifeId = NetPlayerLifecycle.Get(0),
                    Target = identity, HasState = true, WeaponSelect = (byte)BeamType.ShockCoil,
                    Buttons = IntentButtons.Shoot | IntentButtons.InPlayState, AckFrame = ack };
                NetSession.AcceptSlotIntent(0, intent);
                beam.Target = null;
                var trace = new NetContinuousTargetDiagnostics.Evaluation();
                Check(NetContinuousTargeting.Resolve(beam, owner.EquipInfo, scene, ref trace), "network player decision handled");
                return trace.Rejection;
            }
            Check(Remote(NetTargetIdentity.ForSlot(2)) == ContinuousTargetRejection.None && beam.Target == other,
                "authority accepts owner's eligible choice even if another player has better angle");
            Check(Remote(NetTargetIdentity.None) == ContinuousTargetRejection.ExplicitNone && beam.Target == null, "explicit loss drops player");
            // Full selector must not undo the explicit decision with player fallback.
            selector.Invoke(null, new object[] { beam, owner.EquipInfo, scene });
            Check(beam.Target is not PlayerEntity, "explicit none cannot fallback to a different player");
            Check(Remote(default) == ContinuousTargetRejection.MissingDecision && beam.Target == null, "missing decision fails closed");
            var identity = NetTargetIdentity.ForSlot(1);
            Check(Remote(identity with { Generation = 7 }) == ContinuousTargetRejection.WrongGeneration, "generation fenced");
            Check(Remote(identity with { LifeId = 7 }) == ContinuousTargetRejection.WrongLife, "life fenced");
            target.ModPlaceAt(new(10, 0, 5));
            Check(Remote(identity) == ContinuousTargetRejection.Angle && beam.Target == null, "loss leaves cone without retaining prior target");
            target.ModPlaceAt(new(0, 0, 5)); Check(Remote(identity) == ContinuousTargetRejection.None, "re-entry reacquires");
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(target, target.Flags2 | PlayerFlags2.Spectating);
            Check(Remote(identity) == ContinuousTargetRejection.Eligibility, "spectator rejected");
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(target, target.Flags2 & ~PlayerFlags2.Spectating);
            foreach (var player in PlayerEntity.Players)
            {
                if (player == owner) continue;
                foreach (bool alt in new[] { false, true })
                {
                    player.ModForceForm(alt); player.ModPlaceAt(new(0, alt ? .5f : 0, 5));
                    NetUnlagged.Record(200);
                    typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)202);
                    player.ModForceForm(!alt); player.ModPlaceAt(new(50, 0, -5));
                    Check(Remote(NetTargetIdentity.ForSlot(player.SlotIndex)) == ContinuousTargetRejection.None && beam.Target == player,
                        $"{player.Hunter} historical {(alt ? "alt to biped" : "biped to alt")} selection");
                    typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)200);
                }
            }
            target.ModForceForm(false); target.ModPlaceAt(new(0, 0, 5));
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)300);
            Check(Remote(identity, 250) == ContinuousTargetRejection.UnavailableHistory, "unavailable history does not fall back to current world");
            Remote(identity, 300);
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)331);
            beam.Target = null; var staleTrace = new NetContinuousTargetDiagnostics.Evaluation();
            NetContinuousTargeting.Resolve(beam, owner.EquipInfo, scene, ref staleTrace);
            Check(staleTrace.Rejection == ContinuousTargetRejection.Stale && beam.Target == null, "phase and target expire at same intent age");
            target.Spawn(target.Position, Vector3.UnitZ, Vector3.UnitY, target.NodeRef, respawn: true);
            Check(Remote(identity, 331) == ContinuousTargetRejection.WrongLife, "delayed pre-respawn target refused");
            identity = NetTargetIdentity.ForSlot(1); NetPlayerLifecycle.SetOccupant(1, 2);
            Check(Remote(identity, 331) == ContinuousTargetRejection.WrongGeneration, "delayed old occupant refused");
            // Warm the actual resolver, including scene lookup, lifecycle, predicate and historical lookup.
            other.ModForceForm(false); other.ModPlaceAt(new(0, 0, 5));
            NetUnlagged.Record(331);
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)333);
            Check(Remote(NetTargetIdentity.ForSlot(2), 331) == ContinuousTargetRejection.None, "historical allocation fixture accepts target");
            for (int i = 0; i < 1000; i++) { beam.Target = null; var t = new NetContinuousTargetDiagnostics.Evaluation(); NetContinuousTargeting.Resolve(beam, owner.EquipInfo, scene, ref t); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { beam.Target = null; var t = new NetContinuousTargetDiagnostics.Evaluation(); NetContinuousTargeting.Resolve(beam, owner.EquipInfo, scene, ref t); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "zero warmed production validation allocations");
            owner.ModSetPendingHomingTarget(NetTargetIdentity.ForSlot(2));
            scene.PlayerReplication.ApplyIntent(owner, NetSession.RemoteIntents[0]);
            Check(owner.ModConsumePendingHomingTarget() == default,
                "continuous report cannot leak into a later charged Volt Driver release");
            Console.WriteLine($"PASS: {_checks} continuous target scene assertions, all hunters, historical forms, lifecycle, zero allocations");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { sim.Stop(); NetSession.Stop(); }
    }
}
