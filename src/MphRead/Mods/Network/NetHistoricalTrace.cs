using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public enum HistoricalBodyType { BipedCylinder, AltSphere, KandenChain }
public readonly record struct HistoricalBody(int Slot, Vector3 Position, float Radius, float Bottom, float Top,
    HistoricalBodyType Type = HistoricalBodyType.BipedCylinder, AltCollisionPose Segments = default);

public readonly record struct HistoricalHit(bool Available, int Slot, bool Head);

/// <summary>Read-only historical player trace diagnostic. This compares geometry,
/// not damage: invulnerability, projectile travel, splashes and prediction are
/// deliberately not simulated a second time.</summary>
public static class NetHistoricalTrace
{
    public static HistoricalHit Trace(ReadOnlySpan<HistoricalBody> bodies, Vector3 start, Vector3 end,
        float beamRadius, float worldDistance = 1)
    {
        int slot = -1; bool head = false; float closest = worldDistance;
        foreach (var body in bodies)
        {
            if (Intersect(body, start, end, beamRadius, out var result) && result.Distance < closest)
            {
                closest = result.Distance; slot = body.Slot;
                head = body.Type == HistoricalBodyType.BipedCylinder
                    && result.Position.Y - body.Position.Y >= body.Top - .3f;
            }
        }
        return new(true, slot, head);
    }
    public static bool Intersect(in HistoricalBody body, Vector3 start, Vector3 end,
        float radius, out CollisionResult result)
    {
        result = default;
        float radii = body.Radius + radius;
        if (body.Type == HistoricalBodyType.BipedCylinder)
            return CollisionDetection.CheckCylindersOverlap(start, end, body.Position + Vector3.UnitY * body.Bottom,
                Vector3.UnitY, body.Top - body.Bottom, radii, ref result);
        if (body.Type == HistoricalBodyType.KandenChain)
        {
            if (!CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Segments.Seg2, 1.6f, ref result))
                return false;
            return CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Position, radii, ref result)
                || CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Segments.Seg1, radii, ref result)
                || CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Segments.Seg2, radii, ref result)
                || CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Segments.Seg3, radii, ref result);
        }
        return CollisionDetection.CheckCylinderOverlapSphere(start, end, body.Position, radii, ref result);
    }

    internal static HistoricalBody CurrentBody(PlayerEntity player) => new(player.SlotIndex,
        player.ModCollisionIsAltForm ? player.Volume.SpherePosition : player.Position, player.Volume.SphereRadius,
        Fixed.ToFloat(player.Values.MinPickupHeight), Fixed.ToFloat(player.Values.MaxPickupHeight),
        !player.ModCollisionIsAltForm ? HistoricalBodyType.BipedCylinder : player.Hunter == Hunter.Kanden
            ? HistoricalBodyType.KandenChain : HistoricalBodyType.AltSphere, player.ModCaptureAltPose());

    internal static HistoricalBody Body(PlayerEntity player, in HistoricalPlayerPose pose)
    {
        var volume = PlayerEntity.PlayerVolumes[(int)player.Hunter, pose.AltForm ? 2 : 0];
        return new(player.SlotIndex, pose.Position + (pose.AltForm ? volume.SpherePosition : Vector3.Zero),
            volume.SphereRadius, Fixed.ToFloat(player.Values.MinPickupHeight), Fixed.ToFloat(player.Values.MaxPickupHeight),
            !pose.AltForm ? HistoricalBodyType.BipedCylinder : player.Hunter == Hunter.Kanden
                ? HistoricalBodyType.KandenChain : HistoricalBodyType.AltSphere, pose.AltPose);
    }

    public static ShadowOutcome Compare(in HistoricalHit existing, in HistoricalHit shadow)
    {
        if (!existing.Available || !shadow.Available) return ShadowOutcome.HistoricalDataUnavailable;
        if (existing.Slot < 0) return shadow.Slot < 0 ? ShadowOutcome.SameOutcome : ShadowOutcome.ExistingMiss_ShadowHit;
        if (shadow.Slot < 0) return ShadowOutcome.ExistingHit_ShadowMiss;
        if (existing.Slot != shadow.Slot) return ShadowOutcome.DifferentVictim;
        if (existing.Head != shadow.Head) return existing.Head ? ShadowOutcome.ExistingHead_ShadowBody : ShadowOutcome.ExistingBody_ShadowHead;
        return ShadowOutcome.SameOutcome;
    }
    internal static ShadowOutcome CompareShot(PlayerEntity shooter, Vector3 origin, Vector3 direction,
        double hard, double allowed)
    {
        var policy = WeaponLagPolicies.Resolve(shooter.EquipInfo);
        if (policy.Mode == LagCompensationMode.ProjectileCatchUp)
            return NetProjectileCounterfactual.Compare(shooter, origin, direction, hard, allowed);
        // Imperialist's first travel segment is a practical read-only trace.
        // Homing, continuous, area and ricochet mechanics remain unavailable
        // until their exact alternate-state inputs can be reproduced.
        if (policy.Mode != LagCompensationMode.HistoricalTrace || direction.LengthSquared < .001f)
            return ShadowOutcome.HistoricalDataUnavailable;
        var scene = shooter.OwningScene;
        // Dynamic occluders/secondary bodies have no complete historical model.
        foreach (var door in scene.GetDoorEntities()) if (!door.Flags.TestFlag(DoorFlags.Open) && !door.ConnectorInactive)
            return ShadowOutcome.HistoricalDataUnavailable;
        foreach (var field in scene.GetForceFieldEntities()) if (field.Active) return ShadowOutcome.HistoricalDataUnavailable;
        foreach (var enemy in scene.GetEnemyInstanceEntities()) return ShadowOutcome.HistoricalDataUnavailable;
        Span<HistoricalBody> oldBodies = stackalloc HistoricalBody[16];
        Span<HistoricalBody> newBodies = stackalloc HistoricalBody[16];
        int count = 0;
        foreach (var player in scene.GetPlayerEntities())
        {
            if (player == shooter || player.Health == 0 || player.Flags2.TestFlag(PlayerFlags2.Spectating)) continue;
            if (count >= oldBodies.Length
                || !NetUnlagged.TryHistoricalPose(player, NetSession.NetFrame - hard, out var oldPose)
                || !NetUnlagged.TryHistoricalPose(player, NetSession.NetFrame - allowed, out var newPose))
                return ShadowOutcome.HistoricalDataUnavailable;
            oldBodies[count] = Body(player, oldPose);
            newBodies[count++] = Body(player, newPose);
            bool oldTurret = NetUnlagged.TryHistoricalHalfturretPosition(player.SlotIndex,
                NetSession.NetFrame - hard, NetPlayerLifecycle.Generation(player.SlotIndex),
                NetPlayerLifecycle.Get(player.SlotIndex), out Vector3 oldTurretPosition);
            bool newTurret = NetUnlagged.TryHistoricalHalfturretPosition(player.SlotIndex,
                NetSession.NetFrame - allowed, NetPlayerLifecycle.Generation(player.SlotIndex),
                NetPlayerLifecycle.Get(player.SlotIndex), out Vector3 newTurretPosition);
            if (oldTurret != newTurret) return ShadowOutcome.HistoricalDataUnavailable;
            if (oldTurret)
            {
                if (count >= oldBodies.Length) return ShadowOutcome.HistoricalDataUnavailable;
                oldBodies[count] = new(player.SlotIndex, oldTurretPosition, .45f, 0, 0, HistoricalBodyType.AltSphere);
                newBodies[count++] = new(player.SlotIndex, newTurretPosition, .45f, 0, 0, HistoricalBodyType.AltSphere);
            }
        }
        var mechanics = shooter.EquipInfo.Weapon;
        Vector3 end = origin + direction.Normalized() * (mechanics.UnchargedSpeed / 8192f);
        CollisionResult world = default;
        float boundary = CollisionDetection.CheckBetweenPoints(origin, end, TestFlags.Beams, scene, ref world)
            ? world.Distance : 1;
        return Compare(Trace(oldBodies[..count], origin, end, mechanics.UnchargedCylRadius / 4096f, boundary),
            Trace(newBodies[..count], origin, end, mechanics.UnchargedCylRadius / 4096f, boundary));
    }
}
