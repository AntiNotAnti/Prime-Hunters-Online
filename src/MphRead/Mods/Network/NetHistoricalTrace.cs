using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public readonly record struct HistoricalBody(int Slot, Vector3 Position, float Radius, float Bottom, float Top);
public readonly record struct HistoricalHit(bool Available, int Slot, bool Head);

/// <summary>Read-only historical biped trace diagnostic. This compares geometry,
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
            CollisionResult result = default;
            if (CollisionDetection.CheckCylindersOverlap(start, end, body.Position + Vector3.UnitY * body.Bottom,
                Vector3.UnitY, body.Top - body.Bottom, body.Radius + beamRadius, ref result) && result.Distance < closest)
            {
                closest = result.Distance; slot = body.Slot;
                head = result.Position.Y - body.Position.Y >= body.Top - .3f;
            }
        }
        return new(true, slot, head);
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
        // Imperialist's first travel segment is a practical read-only trace.
        // Other mechanics need a private simulation, not a second live Process.
        if (shooter.CurrentWeapon != BeamType.Imperialist || direction.LengthSquared < .001f)
            return ShadowOutcome.HistoricalDataUnavailable;
        var scene = shooter.OwningScene;
        // Dynamic occluders/secondary bodies have no complete historical model.
        foreach (var door in scene.GetDoorEntities()) if (!door.Flags.TestFlag(DoorFlags.Open) && !door.ConnectorInactive)
            return ShadowOutcome.HistoricalDataUnavailable;
        foreach (var field in scene.GetForceFieldEntities()) if (field.Active) return ShadowOutcome.HistoricalDataUnavailable;
        foreach (var enemy in scene.GetEnemyInstanceEntities()) return ShadowOutcome.HistoricalDataUnavailable;
        Span<HistoricalBody> oldBodies = stackalloc HistoricalBody[8];
        Span<HistoricalBody> newBodies = stackalloc HistoricalBody[8];
        int count = 0;
        foreach (var player in scene.GetPlayerEntities())
        {
            if (player == shooter || player.Health == 0 || player.Flags2.TestFlag(PlayerFlags2.Spectating)) continue;
            // Avoid inventing past alt-form volumes and detached turret poses.
            if (player.IsAltForm || player.Hunter == Hunter.Weavel || count == 8
                || !NetUnlagged.TryHistoricalBiped(player, NetSession.NetFrame - hard, out Vector3 oldPosition)
                || !NetUnlagged.TryHistoricalBiped(player, NetSession.NetFrame - allowed, out Vector3 newPosition))
                return ShadowOutcome.HistoricalDataUnavailable;
            var body = new HistoricalBody(player.SlotIndex, oldPosition, player.Volume.SphereRadius,
                Fixed.ToFloat(player.Values.MinPickupHeight), Fixed.ToFloat(player.Values.MaxPickupHeight));
            oldBodies[count] = body; newBodies[count++] = body with { Position = newPosition };
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
