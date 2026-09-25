using System;
using MphRead.Mods.Multiplayer;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public static class NetContinuousTargeting
{
    // The cartridge selector has no hard range cutoff for continuous player beams.
    // Distance tightens the cone up to 4094/4096; do not replace it with a radius test.
    public static ContinuousTargetRejection Evaluate(Vector3 position, Vector3 direction, Vector3 target,
        bool active, bool targetable, bool allied, bool alt, bool morphing, float range, float tolerance,
        out float distance, out float dot, out float threshold)
    {
        distance = dot = 0; threshold = tolerance;
        if (!active || !targetable) return ContinuousTargetRejection.Eligibility;
        if (allied) return ContinuousTargetRejection.Team;
        Vector3 between = target - position;
        float squared = between.LengthSquared;
        if (!float.IsFinite(squared) || squared <= 0 || !float.IsFinite(direction.LengthSquared)
            || direction.LengthSquared <= 0 || !float.IsFinite(range) || range <= 0)
            return ContinuousTargetRejection.Range;
        distance = MathF.Sqrt(squared);
        dot = Vector3.Dot(between, direction.Normalized()) / distance;
        threshold = tolerance + Math.Min(distance / range, 1) * (Fixed.ToFloat(4094) - tolerance);
        if ((alt || morphing) && dot < Fixed.ToFloat(4006)) return ContinuousTargetRejection.Form;
        return dot >= tolerance && dot >= threshold ? ContinuousTargetRejection.None : ContinuousTargetRejection.Angle;
    }

    internal static ContinuousTargetRejection EvaluatePlayer(BeamProjectileEntity beam, EquipInfo equip,
        PlayerEntity target, HistoricalPlayerPose? historical, bool morphing, int team,
        ref NetContinuousTargetDiagnostics.Evaluation trace)
    {
        var owner = (PlayerEntity)beam.Owner!;
        bool alt = historical?.AltForm ?? target.IsAltForm;
        Vector3 position;
        if (historical.HasValue) position = historical.Value.Position.AddY(alt ? 0 : .5f);
        else target.GetPosition(out position);
        trace.Origin = beam.Position; trace.Direction = beam.Velocity.Normalized(); trace.TargetPosition = position;
        trace.AltForm = alt; trace.Morphing = morphing;
        trace.TeamEligible = !TeamRules.AreAllies(owner.TeamIndex, team);
        trace.Targetable = target.GetTargetable();
        trace.Tolerance = Fixed.ToFloat(equip.HomingTolerance);
        var rejection = Evaluate(beam.Position, beam.Velocity, position,
            target != owner && target.ModIsInPlay && target.LoadFlags.TestFlag(LoadFlags.Active)
                && !target.Flags2.TestFlag(PlayerFlags2.Spectating),
            trace.Targetable, !trace.TeamEligible, alt, morphing,
            Fixed.ToFloat(equip.Weapon.HomingRange), trace.Tolerance,
            out trace.Distance, out trace.Dot, out trace.Threshold);
        trace.Eligible = rejection == ContinuousTargetRejection.None;
        return rejection;
    }

    internal static bool Resolve(BeamProjectileEntity beam, EquipInfo equip, Scene scene,
        ref NetContinuousTargetDiagnostics.Evaluation trace)
    {
        if (beam.Beam != BeamType.ShockCoil || beam.Owner is not PlayerEntity owner || owner.IsBot) return false;
        var replication = scene.Services.PlayerReplication;
        if (!replication.Active || owner.SlotIndex == replication.LocalSlot) return false;
        trace.Authority = replication.IsAuthority || replication.IsHost;
        if (!replication.TryGetIntent(owner.SlotIndex, out var intent))
        { trace.Rejection = ContinuousTargetRejection.MissingDecision; return true; }
        trace.Reported = intent.Target; trace.ReportFrame = intent.Frame;
        if (!scene.Services.IsReplica && !NetPlayerLifecycle.AcceptIntent(owner.SlotIndex, intent))
        { trace.Rejection = ContinuousTargetRejection.Stream; return true; }
        if (intent.Frame == 0 || replication.IntentAge(owner.SlotIndex) > ContinuousWeaponPhase.MaxIntentAge)
        { trace.Rejection = ContinuousTargetRejection.Stale; return true; }
        if (intent.WeaponSelect != (byte)BeamType.ShockCoil || (intent.Buttons & IntentButtons.Shoot) == 0
            || !owner.ModIsInPlay || owner.Flags2.TestFlag(PlayerFlags2.Spectating))
        { trace.Rejection = ContinuousTargetRejection.Eligibility; return true; }
        if (!intent.HasState || !intent.Target.IsWellFormed)
        { trace.Rejection = ContinuousTargetRejection.MissingDecision; return true; }
        if (!intent.Target.HasPlayer)
        { trace.Rejection = ContinuousTargetRejection.ExplicitNone; return true; }
        int slot = intent.Target.Slot;
        if (!scene.Services.IsReplica && NetPlayerLifecycle.Generation(slot) != intent.Target.Generation)
        { trace.Rejection = ContinuousTargetRejection.WrongGeneration; return true; }
        if (!scene.Services.IsReplica && NetPlayerLifecycle.Get(slot) != intent.Target.LifeId)
        { trace.Rejection = ContinuousTargetRejection.WrongLife; return true; }
        if (scene.Services.IsReplica && !replication.Matches(slot, intent.Target.Generation, intent.Target.LifeId))
        { trace.Rejection = ContinuousTargetRejection.WrongLife; return true; }
        if (slot >= scene.Players.Items.Count)
        { trace.Rejection = ContinuousTargetRejection.Eligibility; return true; }
        var target = scene.Players.Items[slot];
        HistoricalPlayerPose? pose = null;
        bool morphing = target.IsMorphing; int team = target.TeamIndex;
        if (trace.Authority && !scene.Services.IsReplica)
        {
            if (!NetUnlagged.TryContinuousTargetPose(owner, target, out var historical, out morphing, out team))
            { trace.Rejection = ContinuousTargetRejection.UnavailableHistory; return true; }
            pose = historical;
        }
        trace.Rejection = EvaluatePlayer(beam, equip, target, pose, morphing, team, ref trace);
        if (trace.Rejection == ContinuousTargetRejection.None) beam.Target = target;
        return true;
    }

    internal static void Record(BeamProjectileEntity beam, EquipInfo equip, Scene scene,
        bool synchronized, ref NetContinuousTargetDiagnostics.Evaluation trace)
    {
        if (beam.Beam != BeamType.ShockCoil || beam.Owner is not PlayerEntity owner || scene.Services.IsReplica) return;
        trace.Owner = owner.SlotIndex; trace.Generation = NetPlayerLifecycle.Generation(trace.Owner);
        trace.Life = NetPlayerLifecycle.Get(trace.Owner); trace.Frame = NetSession.NetFrame;
        trace.Launch = beam.ModLaunchKey; trace.Timer = owner.ShockCoilTimer;
        trace.Phase = beam.ModContinuousPhase;
        trace.Selected = beam.Target is PlayerEntity target ? NetTargetIdentity.ForSlot(target.SlotIndex) : NetTargetIdentity.None;
        trace.Acquired = beam.Target != null;
        if (!synchronized)
        {
            trace.Reported = trace.Selected; trace.ReportFrame = trace.Frame;
            if (beam.Target is PlayerEntity selected)
                trace.Rejection = EvaluatePlayer(beam, equip, selected, null, selected.IsMorphing, selected.TeamIndex, ref trace);
            else trace.Rejection = ContinuousTargetRejection.ExplicitNone;
        }
        trace.Previous = owner.ModContinuousTargetState.Target;
        trace.Changed = trace.Previous != trace.Selected;
        owner.ModContinuousTargetState.Observe(trace.Selected, trace.Frame, trace.ReportFrame);
        if (!synchronized)
        {
            owner.ModContinuousNetworkTarget = trace.Selected;
            owner.ModContinuousFireTick = unchecked((uint)beam.ModContinuousPhase);
            NetHooks.CaptureContinuousShot(owner);
        }
        NetContinuousTargetDiagnostics.Record(beam, trace);
    }

    internal static void ResetPlayer(PlayerEntity player)
    {
        player.ModContinuousNetworkTarget = NetTargetIdentity.None;
        player.ModContinuousTargetState = default;
        player.ModContinuousFireTick = 0;
        NetContinuousTargetDiagnostics.EndBurst(player.SlotIndex);
    }
    internal static void Reset()
    {
        NetHooks.ResetContinuousIntent();
        foreach (var player in PlayerEntity.Players) if (player != null) ResetPlayer(player);
    }
    internal static void ForgetSlot(int slot)
    {
        foreach (var player in PlayerEntity.Players)
            if (player != null && (player.SlotIndex == slot || player.ModContinuousNetworkTarget.Slot == slot
                || player.ModContinuousTargetState.Target.Slot == slot)) ResetPlayer(player);
    }
}
