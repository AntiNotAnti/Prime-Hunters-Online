using System;

namespace MphRead.Mods.Network;

public enum LagCompensationMode
{
    None, HistoricalTrace, ProjectileCatchUp, HomingProjectileCatchUp, Continuous, AreaHistorical, MeleeHistorical
}
public readonly record struct WeaponLagPolicy(LagCompensationMode Mode, bool UsesHistoricalPlayers,
    bool UsesHistoricalDynamicGeometry, bool ProjectileCatchUp, int MaximumCatchUpFrames,
    bool AllowPressAge, bool UseShadowPlausibility)
{
    public int CatchUpFrames(double rewind) => ProjectileCatchUp
        ? Math.Min(MaximumCatchUpFrames, (int)Math.Ceiling(Math.Clamp(rewind, 0, NetUnlagged.MaxRewindCeiling))) : 0;
}

public static class WeaponLagPolicies
{
    public static WeaponLagPolicy Resolve(WeaponInfo? mechanics, bool charged = false)
    {
        charged &= mechanics != null && mechanics.Flags.TestFlag(WeaponFlags.CanCharge);
        var mode = mechanics == null ? LagCompensationMode.ProjectileCatchUp
            : (charged ? mechanics.Flags.TestFlag(WeaponFlags.AoeCharged) : mechanics.Flags.TestFlag(WeaponFlags.AoeUncharged))
                ? LagCompensationMode.AreaHistorical
            : mechanics.Flags.TestFlag(WeaponFlags.Continuous) ? LagCompensationMode.Continuous
            : mechanics.Beam == BeamType.Imperialist ? LagCompensationMode.HistoricalTrace
            : (charged ? Math.Max(mechanics.MinChargeHoming, mechanics.ChargedHoming) : mechanics.UnchargedHoming) > 0
                ? LagCompensationMode.HomingProjectileCatchUp : LagCompensationMode.ProjectileCatchUp;
        // Existing mechanics spawn real projectiles even for Imperialist and
        // continuous beams. Preserve the existing bounded Process loop; an
        // existing/replaced continuous beam has no newly spawned work to do.
        return new(mode, true, false, mode != LagCompensationMode.AreaHistorical, Math.Clamp(NetUnlagged.MaxRewindFrames, 0, NetUnlagged.MaxRewindCeiling), true, true);
    }
    public static WeaponLagPolicy Resolve(EquipInfo equip)
    {
        var mechanics = equip.Weapon;
        bool charged = mechanics != null && mechanics.Flags.TestFlag(WeaponFlags.CanCharge)
            && equip.ChargeLevel >= (mechanics.Flags.TestFlag(WeaponFlags.PartialCharge) ? mechanics.MinCharge : mechanics.FullCharge) * 2;
        var policy = Resolve(mechanics, charged);
        // At precisely the partial-charge minimum, Spawn uses uncharged values.
        if (charged && mechanics!.Flags.TestFlag(WeaponFlags.PartialCharge)
            && equip.ChargeLevel == mechanics.MinCharge * 2 && policy.Mode == LagCompensationMode.HomingProjectileCatchUp)
            policy = policy with { Mode = mechanics.UnchargedHoming > 0 ? LagCompensationMode.HomingProjectileCatchUp : LagCompensationMode.ProjectileCatchUp };
        return policy;
    }
    // Alt/melee and independent area damage currently resolve on the live
    // timeline. Do not silently pull them into the projectile rewind path.
    public static WeaponLagPolicy CurrentVolume => new(LagCompensationMode.None, false, false, false, 0, false, false);
}
