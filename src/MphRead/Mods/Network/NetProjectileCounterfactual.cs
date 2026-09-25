using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>
/// Side-effect-free alternate-rewind simulation for deterministic, non-homing,
/// non-ricochet projectile catch-up. It never applies damage, score, RNG, entity
/// state or projectile allocation. Unsupported mechanics remain explicitly
/// unavailable instead of being inferred as misses.
/// </summary>
public static class NetProjectileCounterfactual
{
    private readonly record struct Ballistic(float Speed, float Gravity, float Radius,
        float MaxDistance, int LifespanFrames, bool SurfaceCollision);

    public static ShadowOutcome Compare(PlayerEntity shooter, Vector3 origin, Vector3 direction,
        double hardDepth, double shadowDepth)
    {
        if (!TryBallistic(shooter.EquipInfo, out var ballistic)
            || direction.LengthSquared < .000001f)
            return ShadowOutcome.HistoricalDataUnavailable;

        var hard = Simulate(shooter, origin, direction.Normalized(), ballistic, hardDepth);
        var shadow = Simulate(shooter, origin, direction.Normalized(), ballistic, shadowDepth);
        return NetHistoricalTrace.Compare(hard, shadow);
    }

    private static bool TryBallistic(EquipInfo equip, out Ballistic result)
    {
        result = default;
        WeaponInfo w = equip.Weapon;
        bool charged = false;
        float chargePct = 0;
        if (w.Flags.TestFlag(WeaponFlags.CanCharge))
        {
            if (w.Flags.TestFlag(WeaponFlags.PartialCharge))
            {
                if (equip.ChargeLevel >= w.MinCharge * 2)
                {
                    charged = true;
                    int span = w.FullCharge * 2 - w.MinCharge * 2;
                    chargePct = span <= 0 ? 1 : (equip.ChargeLevel - w.MinCharge * 2) / (float)span;
                }
            }
            else if (equip.ChargeLevel >= w.FullCharge * 2)
            {
                charged = true; chargePct = 1;
            }
        }

        float Amount(float uncharged, float minimum, float full)
            => chargePct <= 0 ? uncharged : minimum + (full - minimum) * chargePct;
        int index = charged ? 1 : 0;
        bool ricochet = charged ? w.Flags.TestFlag(WeaponFlags.RicochetCharged)
            : w.Flags.TestFlag(WeaponFlags.RicochetUncharged);
        bool aoe = charged ? w.Flags.TestFlag(WeaponFlags.AoeCharged)
            : w.Flags.TestFlag(WeaponFlags.AoeUncharged);
        float homing = Amount(w.UnchargedHoming, w.MinChargeHoming, w.ChargedHoming);
        int projectiles = (int)Amount(w.Projectiles, w.MinChargeProjectiles, w.ChargedProjectiles);
        float spread = Amount(w.UnchargedSpread, w.MinChargeSpread, w.ChargedSpread);

        // These mechanics need their own exact shadow model. Do not downgrade
        // them to a straight-line guess just to reduce Unknown telemetry.
        if (w.Flags.TestFlag(WeaponFlags.Continuous) || ricochet || aoe || homing != 0
            || projectiles != 1 || spread != 0 || w.SpeedDecayTimes[index] != 0)
            return false;

        float speed = Amount(w.UnchargedSpeed, w.MinChargeSpeed, w.ChargedSpeed) / 4096f / 2;
        float gravity = Amount(w.UnchargedGravity, w.MinChargeGravity, w.ChargedGravity) / 4096f;
        float radius = Amount(w.UnchargedCylRadius, w.MinChargeCylRadius, w.ChargedCylRadius) / 4096f;
        float maxDistance = Amount(w.UnchargedDistance, w.MinChargeDistance, w.ChargedDistance) / 4096f;
        int lifespan = (int)Math.Ceiling(Amount(w.UnchargedLifespan, w.MinChargeLifespan, w.ChargedLifespan) * 2);
        if (!float.IsFinite(speed) || speed <= 0 || !float.IsFinite(gravity)
            || !float.IsFinite(radius) || radius < 0 || lifespan <= 0)
            return false;
        result = new(speed, gravity, radius, maxDistance, lifespan,
            w.Flags.TestFlag(WeaponFlags.SurfaceCollision));
        return true;
    }

    private static HistoricalHit Simulate(PlayerEntity shooter, Vector3 origin, Vector3 direction,
        in Ballistic ballistic, double depth)
    {
        int steps = Math.Min((int)Math.Ceiling(Math.Max(0, depth)), NetUnlagged.MaxRewindCeiling);
        if (steps == 0) return new(true, -1, false);
        Vector3 position = origin;
        Vector3 velocity = direction * ballistic.Speed;
        Vector3 acceleration = new(0, ballistic.Gravity / 2, 0);

        for (int step = 1; step <= steps && step <= ballistic.LifespanFrames; step++)
        {
            Vector3 back = position;
            position += velocity;
            velocity += acceleration / 2;

            float boundary = 1;
            if (ballistic.MaxDistance > 0)
            {
                float front = (position - origin).Length;
                if (front >= ballistic.MaxDistance)
                {
                    float backDistance = (back - origin).Length;
                    float denom = front - backDistance;
                    boundary = denom > .000001f
                        ? Math.Clamp((ballistic.MaxDistance - backDistance) / denom, 0, 1) : 0;
                }
            }

            double frame = NetSession.NetFrame - depth + step;
            if (ballistic.SurfaceCollision)
            {
                if (!NetDynamicGeometryHistory.TryTraceDistanceAtFrame(shooter.OwningScene,
                    back, position, frame, out float world))
                    return new(false, -1, false);
                boundary = Math.Min(boundary, world);
            }

            Span<HistoricalBody> bodies = stackalloc HistoricalBody[16];
            int count = 0;
            foreach (var player in shooter.OwningScene.GetPlayerEntities())
            {
                if (player == shooter || player.Health == 0
                    || player.Flags2.TestFlag(PlayerFlags2.Spectating)) continue;
                if (count >= bodies.Length) return new(false, -1, false);

                if (frame >= NetSession.NetFrame)
                {
                    bodies[count++] = NetHistoricalTrace.CurrentBody(player);
                    if (NetUnlagged.TryCollisionHalfturret(player, out Vector3 currentTurret))
                        bodies[count++] = new(player.SlotIndex, currentTurret, .45f, 0, 0, HistoricalBodyType.AltSphere);
                    continue;
                }

                if (!NetUnlagged.HistoryAvailable(frame)) return new(false, -1, false);
                if (NetUnlagged.TryHistoricalPose(player, frame, out var pose))
                    bodies[count++] = NetHistoricalTrace.Body(player, pose);
                if (NetUnlagged.TryHistoricalHalfturretPosition(player.SlotIndex, frame,
                    NetPlayerLifecycle.Generation(player.SlotIndex), NetPlayerLifecycle.Get(player.SlotIndex), out Vector3 turret))
                    bodies[count++] = new(player.SlotIndex, turret, .45f, 0, 0, HistoricalBodyType.AltSphere);
            }

            var hit = NetHistoricalTrace.Trace(bodies[..count], back, position, ballistic.Radius, boundary);
            if (hit.Slot >= 0) return hit;
            if (boundary < 1) return new(true, -1, false);
        }
        return new(true, -1, false);
    }
}
