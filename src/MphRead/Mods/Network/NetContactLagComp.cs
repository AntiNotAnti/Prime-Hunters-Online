using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public enum ContactAttackKind { None, Boost, Spire, Noxus, Trace, Weavel }
public readonly record struct HistoricalAltAttackState(ContactAttackKind Kind, bool AltForm,
    Vector3 Center, Vector3 PreviousCenter, float Radius, Vector3 LeftRock, Vector3 RightRock,
    ushort Life, ushort Generation, bool InPlay);

/// <summary>Read-only damage queries after owner movement has been applied. The
/// attack is the current accepted owner pose; ACK time belongs to its view of
/// opponents, not to its own movement. No physics state is ever rewound here.</summary>
public static class NetContactLagComp
{
    private static readonly HistoricalAltAttackState[] Previous = new HistoricalAltAttackState[PlayerEntity.SlotCapacity];
    private static readonly HistoricalAltAttackState[] Current = new HistoricalAltAttackState[PlayerEntity.SlotCapacity];
    private static readonly uint[] Stamp = new uint[PlayerEntity.SlotCapacity];
    private static readonly uint[,] LastLog = new uint[PlayerEntity.SlotCapacity, PlayerEntity.SlotCapacity];
    public static long Checks, HistoricalHits, LiveHits, HistoricalOnlyHits, LiveOnlyHits, HistoryUnavailable, HistoryFallbacks;
    public static long SweepChecks, SweepHits, SweepOnlyHits, AttacksAttempted, VisibleChecks, VisibleOverlaps;
    public static double RewindDepth, SweepDistance;

    public static void Reset()
    {
        Array.Clear(Previous); Array.Clear(Current); Array.Clear(Stamp); Array.Clear(LastLog);
        Checks = HistoricalHits = LiveHits = HistoricalOnlyHits = LiveOnlyHits = HistoryUnavailable = HistoryFallbacks = 0;
        SweepChecks = SweepHits = SweepOnlyHits = AttacksAttempted = VisibleChecks = VisibleOverlaps = 0; RewindDepth = SweepDistance = 0;
    }
    public static void ResetSlot(int slot)
    {
        if ((uint)slot >= Stamp.Length) return;
        Previous[slot] = Current[slot] = default; Stamp[slot] = 0;
        for (int i = 0; i < PlayerEntity.SlotCapacity; i++) LastLog[slot, i] = LastLog[i, slot] = 0;
    }

    internal static HistoricalAltAttackState CaptureHistory(PlayerEntity player, uint frame)
    {
        int slot = player.SlotIndex;
        var sample = Current[slot];
        return Stamp[slot] == frame && sample.Life == NetPlayerLifecycle.Get(slot)
            && sample.Generation == NetPlayerLifecycle.Generation(slot) ? sample : player.ModCaptureContactState();
    }

    public static HistoricalAltAttackState WithSweep(in HistoricalAltAttackState current,
        in HistoricalAltAttackState previous, bool consecutive)
    {
        bool continuous = consecutive && current.InPlay && previous.InPlay && current.AltForm == previous.AltForm
            && current.Life == previous.Life && current.Generation == previous.Generation
            && (current.Kind == previous.Kind || previous.Kind == ContactAttackKind.None) && (current.Center - previous.Center).LengthSquared <= 15f * 15f;
        return current with { PreviousCenter = continuous ? previous.Center : current.Center };
    }

    public static double TargetFrame(uint now, uint ack, byte subFrame)
    {
        if (ack == 0 || ack >= now) return now;
        return Math.Max(Math.Max(1, (double)now - NetUnlagged.MaxRewindFrames), ack + subFrame / 256.0);
    }

    // Contact retains the game's contact sphere (also for biped victims), rather
    // than borrowing the taller beam/headshot cylinder and changing melee reach.
    public static bool Intersects(in HistoricalAltAttackState attack, in HistoricalBody victim, bool sweep)
    {
        if (!attack.InPlay || attack.Kind == ContactAttackKind.None) return false;
        CollisionResult result = default;
        if (attack.Kind == ContactAttackKind.Spire)
            return CollisionDetection.CheckCylinderOverlapSphere(attack.LeftRock, attack.LeftRock,
                victim.Position, victim.Radius + .5f, ref result)
                || CollisionDetection.CheckCylinderOverlapSphere(attack.RightRock, attack.RightRock,
                    victim.Position, victim.Radius + .5f, ref result);
        if (attack.Kind == ContactAttackKind.Noxus)
        {
            Vector3 between = victim.Position - attack.Center;
            float range = victim.Radius + 1.8f;
            return between.Y > -victim.Radius && between.Y < victim.Radius
                && between.X * between.X + between.Z * between.Z < range * range;
        }
        Vector3 start = sweep ? attack.PreviousCenter : attack.Center;
        // Exact segment/sphere distance, including endpoints, avoids extending a
        // lunge beyond either end of its actually accepted movement.
        Vector3 segment = attack.Center - start;
        float length = segment.LengthSquared;
        float t = length > 0 ? Math.Clamp(Vector3.Dot(victim.Position - start, segment) / length, 0, 1) : 0;
        float radii = attack.Radius + victim.Radius;
        float distanceSquared = (start + segment * t - victim.Position).LengthSquared;
        // A resolved movement step can end exactly at touching distance after
        // physical separation. That endpoint belongs to the swept contact.
        return sweep ? distanceSquared <= radii * radii : distanceSquared < radii * radii;
    }

    internal static void ResolveFrame()
    {
        if (!NetSession.Active || !(NetSession.IsHost || NetSession.IsAuthority)) return;
        uint now = NetSession.NetFrame;
        foreach (var attacker in PlayerEntity._players)
        {
            if (attacker == null) continue;
            int slot = attacker.SlotIndex;
            var sample = attacker.ModCaptureContactState();
            if (sample.Kind != ContactAttackKind.None && sample.Kind != Previous[slot].Kind) AttacksAttempted++;
            Current[slot] = WithSweep(sample, Previous[slot], Stamp[slot] + 1 == now && Stamp[slot] != 0);
        }
        foreach (var attacker in PlayerEntity._players)
        {
            if (attacker == null) continue;
            int slot = attacker.SlotIndex;
            var attack = Current[slot];
            if (!attack.InPlay || attack.Kind == ContactAttackKind.None) continue;
            double target = now;
            if (NetUnlagged.Enabled && !attacker.IsBot && slot != NetSession.LocalSlot && NetSession.RemoteIntentValid[slot])
            {
                var intent = NetSession.RemoteIntents[slot];
                target = Math.Max(1, now - LagCompensationPolicy.Evaluate(slot, now, intent.AckFrame, intent.AckSubFrame, 0).GlobalServedDepth);
            }
            foreach (var victim in PlayerEntity._players)
            {
                if (victim == null || victim == attacker || !victim.LoadFlags.TestFlag(LoadFlags.Active) || !victim.ModIsInPlay || victim.Flags2.TestFlag(PlayerFlags2.Spectating)) continue;
                var live = new HistoricalBody(victim.SlotIndex, victim.Volume.SpherePosition,
                    victim.Volume.SphereRadius, 0, 0, HistoricalBodyType.AltSphere);
                var body = live;
                Checks++;
                bool historical = target < now;
                bool liveFallback = false;
                if (historical)
                {
                    if (!NetUnlagged.TryHistoricalPose(victim, target, out var pose))
                    {
                        HistoryUnavailable++;
                        // Noxus used the live authority volume before contact lag compensation
                        // existed. If the history ring does not contain this world frame at all,
                        // preserve that working behavior instead of converting uncertainty into
                        // an automatic miss. A frame that *does* exist but refuses this victim is
                        // a lifecycle/in-play fence (death, respawn, spectator, slot reuse) and
                        // must remain fail-closed.
                        if (attack.Kind != ContactAttackKind.Noxus || NetUnlagged.HistoryAvailable(target))
                            continue;
                        HistoryFallbacks++;
                        liveFallback = true;
                    }
                    else
                    {
                        var volume = PlayerEntity.PlayerVolumes[(int)victim.Hunter, pose.AltForm ? 2 : 0];
                        body = live with { Position = pose.Position + volume.SpherePosition, Radius = volume.SphereRadius };
                    }
                }
                bool liveHit = Intersects(attack, live, false);
                bool hit = Intersects(attack, body, true);
                if (Telemetry.ProductionTelemetry.Enabled)
                {
                    var intent = NetSession.RemoteIntents[slot];
                    LagCompensationPolicy.Study(slot, victim.SlotIndex, 10,
                        LagCompensationPolicy.Evaluate(slot, now, intent.AckFrame, intent.AckSubFrame, 0), hit ? 1 : 0);
                }
                bool endpoint = Intersects(attack, body, false);
                SweepChecks++; SweepDistance += (attack.Center - attack.PreviousCenter).Length;
                RewindDepth += now - target;
                if (liveHit) LiveHits++;
                if (hit) { HistoricalHits++; SweepHits++; }
                if (hit && !endpoint) SweepOnlyHits++;
                if (hit && !liveHit) HistoricalOnlyHits++;
                if (liveHit && !hit) LiveOnlyHits++;
                int v = victim.SlotIndex;
                if (NetLog.Enabled && (hit != liveHit || liveFallback) && (LastLog[slot, v] == 0 || now - LastLog[slot, v] >= 60))
                {
                    LastLog[slot, v] = now;
                    NetLog.Event($"ALT-HIT slot={slot} victim={v} hunter={attacker.Hunter} ack={target:F2} now={now} rewind={now-target:F2} live={liveHit} historical={hit} fallback={liveFallback} sweepOnly={hit && !endpoint}");
                }
                if (hit && attacker.ModCaptureContactState().Kind == attack.Kind)
                    attacker.ModApplyContactHit(victim, attack.Kind);
            }
        }
        foreach (var player in PlayerEntity._players)
        {
            if (player == null) continue;
            int slot = player.SlotIndex;
            Previous[slot] = Current[slot]; Stamp[slot] = now;
        }
    }

    internal static void ObservePresentation()
    {
        int slot = NetSession.LocalSlot;
        if ((uint)slot >= PlayerEntity.SlotCapacity || PlayerEntity._players[slot] is not { } attacker) return;
        uint now = NetSession.NetFrame;
        var sample = attacker.ModCaptureContactState();
        if (sample.Kind != ContactAttackKind.None && sample.Kind != Previous[slot].Kind) AttacksAttempted++;
        var attack = WithSweep(sample, Previous[slot], Stamp[slot] + 1 == now && Stamp[slot] != 0);
        if (attack.Kind != ContactAttackKind.None)
            foreach (var victim in PlayerEntity._players)
            {
                if (victim == null || victim == attacker || !victim.LoadFlags.TestFlag(LoadFlags.Active)
                    || !victim.ModIsInPlay || victim.Flags2.TestFlag(PlayerFlags2.Spectating)) continue;
                VisibleChecks++;
                if (Intersects(attack, new(victim.SlotIndex, victim.Volume.SpherePosition, victim.Volume.SphereRadius,
                    0, 0, HistoricalBodyType.AltSphere), true)) VisibleOverlaps++;
            }
        Previous[slot] = sample; Stamp[slot] = now;
    }

    public static string Describe() => $"alt contact: attempts {AttacksAttempted}, visible checks {VisibleChecks}, visible overlaps {VisibleOverlaps}, checks {Checks}, historical {HistoricalHits}, live {LiveHits}, historical-only {HistoricalOnlyHits}, live-only {LiveOnlyHits}, unavailable {HistoryUnavailable}, noxus-live-fallback {HistoryFallbacks}, sweep-only {SweepOnlyHits}, rewind total {RewindDepth:F2}, sweep distance {SweepDistance:F2}";
}
