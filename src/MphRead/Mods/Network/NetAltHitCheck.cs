using System;
using System.Diagnostics;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Asset-backed integration checks, invoked explicitly by nettest.</summary>
public static class NetAltHitCheck
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
            var roster = RosterPacket.Create();
            roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.Count = 8;
            for (byte i = 0; i < 8; i++)
            { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Names[i] = $"ALT{i}"; roster.Hunters[i] = (byte)(i % 7); }
            NetSession.ApplyRoster(roster);
            for (int i = 0; i < 120; i++) sim.Step();
            Check(sim.StepFailures == 0, "scene simulation");
            var shooter = PlayerEntity.Players[0];
            Check(shooter.ModIsInPlay, "spawned attacker");
            var reconcile = typeof(NetUnlagged).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (bool historicalAlt in new[] { true, false })
            {
                for (int i = 1; i < 8; i++)
                {
                    var player = PlayerEntity.Players[i];
                    player.ModForceForm(historicalAlt);
                    player.ModPlaceAt(new Vector3(i * 3, 2, 3));
                }
                NetUnlagged.Record(200);
                var poses = new HistoricalPlayerPose[8];
                for (int i = 1; i < 8; i++)
                {
                    var player = PlayerEntity.Players[i];
                    Check(NetUnlagged.TryHistoricalPose(player, 200, out poses[i]), "history available");
                    player.ModForceForm(!historicalAlt);
                    player.ModPlaceAt(new Vector3(i * 3 + 20, 5, 8));
                }
                NetUnlagged.Record(201);
                var states = new HistoricalCollisionState[8];
                for (int i = 1; i < 8; i++) states[i] = PlayerEntity.Players[i].ModCaptureCollisionState();
                try
                {
                    Check((bool)reconcile.Invoke(null, new object[] { 0, 200.75 })!, "production rewind enters");
                    for (int i = 1; i < 8; i++)
                    {
                        var player = PlayerEntity.Players[i];
                        Check(player.Position == poses[i].Position && player.ModCollisionIsAltForm == historicalAlt,
                            "historical form at interpolation boundary");
                        Check(player.IsAltForm != historicalAlt, "gameplay form preserved during actual rewind");
                    }
                    throw new OperationCanceledException("exercise unwind");
                }
                catch (OperationCanceledException) { }
                finally { NetUnlagged.AbortShot(); }
                for (int i = 1; i < 8; i++)
                    Check(states[i] == PlayerEntity.Players[i].ModCaptureCollisionState(), "exception restores exact collision state");
            }
            var weavel = PlayerEntity.Players[6];
            weavel.ModForceForm(true);
            weavel.ModPlaceAt(new Vector3(30, 5, 10));
            weavel.Halfturret.Health = 37;
            Vector3 historicalTurret = weavel.Halfturret.Position;
            NetUnlagged.Record(205);
            weavel.Halfturret.Reposition(new Vector3(8, 0, 0), weavel.NodeRef);
            NetUnlagged.Record(206);
            Check(NetUnlagged.TryHistoricalHalfturretPosition(weavel.SlotIndex, 205,
                NetPlayerLifecycle.Generation(weavel.SlotIndex), NetPlayerLifecycle.Get(weavel.SlotIndex), out var oldTurret)
                && oldTurret == historicalTurret && oldTurret != weavel.Halfturret.Position,
                "detached Weavel turret uses historical collision position");

            var victim = PlayerEntity.Players[1];
            NetUnlagged.Record(210);
            Check(NetUnlagged.TryHistoricalAttack(victim.SlotIndex, 210, NetPlayerLifecycle.Generation(victim.SlotIndex),
                NetPlayerLifecycle.Get(victim.SlotIndex), out var recordedAttack) && recordedAttack.InPlay,
                "collision-critical attack state retained in history");
            victim.Spawn(victim.Position, Vector3.UnitZ, Vector3.UnitY, victim.NodeRef, respawn: true);
            Check(!NetUnlagged.TryHistoricalPose(victim, 210, out _), "respawn fences old victim pose");
            try
            {
                Check((bool)reconcile.Invoke(null, new object[] { 0, 210.0 })!, "historical world still available after victim respawn");
                Check(!NetUnlagged.CollisionTargetAvailable(victim), "beams cannot hit current replacement inside old world");
            }
            finally { NetUnlagged.AbortShot(); }
            Check(NetUnlagged.CollisionTargetAvailable(victim), "live beams can hit current life after restoration");
            NetUnlagged.Record(211);
            NetUnlagged.ResetSlot(1);
            Check(!NetUnlagged.TryHistoricalPose(victim, 211, out _), "slot reset invalidates history");
            NetUnlagged.Record(212);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(victim, victim.Flags2 | PlayerFlags2.Spectating);
            NetUnlagged.Record(213);
            Check(!NetUnlagged.TryHistoricalPose(victim, 213, out _), "spectators never enter history");
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(victim, victim.Flags2 & ~PlayerFlags2.Spectating);

            // Real Samus damage path: accepted endpoint misses, sweep crosses.
            shooter.ModForceForm(true); victim.ModForceForm(false);
            shooter.ModPlaceAt(new Vector3(-2, 5, 10)); victim.ModPlaceAt(new Vector3(0, 5, 10));
            shooter.Health = victim.Health = 99;
            typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(victim, (ushort)0);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(shooter, shooter.Flags1 | PlayerFlags1.Boosting);
            typeof(PlayerEntity).GetField("_boostDamage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shooter, (ushort)20);
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)300);
            Check(shooter.ModCaptureContactState().Kind == ContactAttackKind.Boost, "active boost captured");
            var frozenTimer = typeof(PlayerEntity).GetField("_frozenTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            frozenTimer.SetValue(shooter, (ushort)5);
            Check(shooter.ModCaptureContactState().Kind == ContactAttackKind.None, "frozen attacker cannot author contact");
            frozenTimer.SetValue(shooter, (ushort)0);
            shooter.Health = 0;
            Check(shooter.ModCaptureContactState().Kind == ContactAttackKind.None, "dead attacker cannot author contact");
            shooter.Health = 99;
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(shooter, shooter.Flags2 | PlayerFlags2.Spectating);
            Check(shooter.ModCaptureContactState().Kind == ContactAttackKind.None, "spectating attacker cannot author contact");
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags2))!.SetValue(shooter, shooter.Flags2 & ~PlayerFlags2.Spectating);
            NetContactLagComp.ResolveFrame();
            shooter.ModPlaceAt(new Vector3(2, 5, 10)); typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(shooter, shooter.Flags1 | PlayerFlags1.Boosting);
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)301);
            Vector3 current = victim.Position;
            int health = victim.Health;
            NetContactLagComp.ResolveFrame();
            Check(NetContactLagComp.SweepOnlyHits > 0, "actual frame resolver detects pass-through");
            Check(victim.Position == current, "historical damage never moves victim");
            Check(!shooter.Flags1.TestFlag(PlayerFlags1.Boosting), "confirmed boost ends attack");
            Check(victim.Health < health, "authority applies actual boost damage");
            // Authority consumes the remote ACK, not the victim's current body.
            victim.ModPlaceAt(new Vector3(0, 5, 10));
            NetUnlagged.Record(320); NetUnlagged.Record(321);
            victim.ModPlaceAt(new Vector3(20, 5, 10));
            shooter.ModPlaceAt(new Vector3(0, 5, 10));
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(shooter, shooter.Flags1 | PlayerFlags1.Boosting);
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, (uint)325);
            NetSession.RemoteIntentValid[0] = true;
            NetSession.RemoteIntents[0] = new IntentPacket { AckFrame = 320, AckSubFrame = 128 };
            health = victim.Health; current = victim.Position;
            NetContactLagComp.ResolveFrame();
            Check(victim.Health < health && victim.Position == current, "ACK-timed authority damage against departed victim");
            long unavailable = NetContactLagComp.HistoryUnavailable;
            victim.Spawn(current, Vector3.UnitZ, Vector3.UnitY, victim.NodeRef, respawn: true);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(shooter, shooter.Flags1 | PlayerFlags1.Boosting);
            health = victim.Health;
            NetContactLagComp.ResolveFrame();
            Check(victim.Health == health && NetContactLagComp.HistoryUnavailable > unavailable,
                "actual delayed contact cannot cross victim respawn");
            NetSession.RemoteIntentValid[0] = false;
            ImpairmentMatrix(victim);
            for (uint f = 400; f < 500; f++) NetUnlagged.Record(f);
            shooter.ModPlaceAt(new Vector3(100, 5, 10));
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(shooter, shooter.Flags1 | PlayerFlags1.Boosting);
            var setFrame = typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.GetSetMethod(true)!.CreateDelegate<Action<uint>>();
            for (uint f = 500; f < 600; f++) { setFrame(f); NetContactLagComp.ResolveFrame(); NetUnlagged.Record(f); }
            long before = GC.GetAllocatedBytesForCurrentThread(); long started = Stopwatch.GetTimestamp();
            for (uint f = 600; f < 1600; f++) { setFrame(f); NetContactLagComp.ResolveFrame(); NetUnlagged.Record(f); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 1000;
            Check(allocated == 0, "eight-player production history plus contact resolution allocates zero bytes");
            Console.WriteLine($"ALT SCENE PASS {_checks} checks; history+contact {ms:F4} ms/frame, {allocated} bytes; {NetContactLagComp.Describe()}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"ALT SCENE FAIL {ex}"); return 1; }
        finally { sim.Stop(); }
    }
    private readonly record struct Probe(uint Frame, HistoricalPlayerPose Pose, Vector3 Start, Vector3 End,
        bool Visible, HistoricalAltAttackState Attack, bool ContactVisible);

    private static void ImpairmentMatrix(PlayerEntity victim)
    {
        // Deterministic two-way network scheduling around the production history
        // and collision queries. Separately from the real-client smoke runner.
        foreach (var profile in new[] { ("LAN", 0, 0, 0.0, 0.0), ("moderate", 100, 20, .01, .01),
            ("severe", 250, 40, .02, .01), ("extreme", 320, 80, .05, .03) })
        {
            var down = new NetFaultQueue<Probe>(17, profile.Item2 / 2.0, profile.Item3, profile.Item4, profile.Item5, 0);
            var up = new NetFaultQueue<Probe>(31, profile.Item2 / 2.0, profile.Item3, profile.Item4, profile.Item5, 0);
            int attempts = 0, visible = 0, hits = 0, legacyDisagreements = 0, disagreements = 0, historyMisses = 0;
            int contactVisible = 0, contactHits = 0, contactLiveHits = 0, historicalOnly = 0, liveOnly = 0;
            double rewind = 0; uint last = 0;
            for (uint tick = 1; tick <= 800; tick++)
            {
                uint frame = 2000 + tick;
                typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, frame);
                bool alt = tick / 30 % 2 == 0;
                if (victim.IsAltForm != alt) victim.ModForceForm(alt);
                victim.ModPlaceAt(new Vector3(MathF.Sin(tick * .18f) * 3, 5 + MathF.Cos(tick * .09f), 10));
                if (victim.Hunter == Hunter.Kanden)
                {
                    var segments = (Vector3[])victim.KandenSegPos;
                    for (int segment = 1; segment <= 3; segment++)
                        segments[segment] = victim.Volume.SpherePosition + Vector3.UnitX * (segment * .5f);
                }
                NetUnlagged.Record(frame);
                double now = tick * 1000.0 / 60;
                if (tick < 720 && tick % 3 == 0)
                {
                    var pose = new HistoricalPlayerPose(victim.Position, victim.IsAltForm, victim.ModCaptureAltPose());
                    var body = NetHistoricalTrace.Body(victim, pose);
                    Vector3 center = victim.Volume.SpherePosition;
                    Vector3 aim = center + (tick % 2 == 0 ? Vector3.Zero : Vector3.UnitY * 3);
                    Vector3 start = aim - Vector3.UnitX * 3, end = aim + Vector3.UnitX * 3;
                    bool seen = NetHistoricalTrace.Intersect(body, start, end, .01f, out _);
                    var attack = new HistoricalAltAttackState(ContactAttackKind.Boost, true, aim + Vector3.UnitX * 2,
                        aim - Vector3.UnitX * 2, .5f, default, default, 1, 1, true);
                    var contactBody = new HistoricalBody(victim.SlotIndex, center, victim.Volume.SphereRadius, 0, 0, HistoricalBodyType.AltSphere);
                    down.Enqueue(now, new(frame, pose, start, end, seen, attack, NetContactLagComp.Intersects(attack, contactBody, true)));
                }
                while (down.TryDequeue(now, out var presentation)) up.Enqueue(now, presentation);
                while (up.TryDequeue(now, out var probe))
                {
                    if (probe.Frame <= last) continue; // same ordering contract as accepted intent
                    last = probe.Frame; attempts++; if (probe.Visible) visible++; if (probe.ContactVisible) contactVisible++;
                    double target = NetContactLagComp.TargetFrame(frame, probe.Frame, 0);
                    if (!NetUnlagged.TryHistoricalPose(victim, target, out var pose)) { historyMisses++; continue; }
                    rewind += frame - target;
                    bool hit = NetHistoricalTrace.Intersect(NetHistoricalTrace.Body(victim, pose), probe.Start, probe.End, .01f, out _);
                    if (hit) hits++; if (hit != probe.Visible) disagreements++;
                    var legacy = new HistoricalPlayerPose(NetPlayerBridge.InFormFor(victim, probe.Pose.Position, probe.Pose.AltForm),
                        victim.IsAltForm, victim.ModCaptureAltPose());
                    bool old = NetHistoricalTrace.Intersect(NetHistoricalTrace.Body(victim, legacy), probe.Start, probe.End, .01f, out _);
                    if (old != probe.Visible) legacyDisagreements++;
                    var volume = PlayerEntity.PlayerVolumes[(int)victim.Hunter, pose.AltForm ? 2 : 0];
                    var contact = new HistoricalBody(victim.SlotIndex, pose.Position + volume.SpherePosition, volume.SphereRadius,
                        0, 0, HistoricalBodyType.AltSphere);
                    bool contactHit = NetContactLagComp.Intersects(probe.Attack, contact, true);
                    bool live = NetContactLagComp.Intersects(probe.Attack, contact with { Position = victim.Volume.SpherePosition,
                        Radius = victim.Volume.SphereRadius }, false);
                    if (contactHit) contactHits++; if (live) contactLiveHits++;
                    if (contactHit && !live) historicalOnly++; if (live && !contactHit) liveOnly++;
                    Check(contactHit == probe.ContactVisible, "impaired contact agrees with acknowledged presentation");
                }
            }
            Check(attempts > 100 && visible > 0 && disagreements == 0 && historyMisses == 0,
                "impaired beam history agrees with presentation");
            Console.WriteLine($"ALT MATRIX {profile.Item1} rtt={profile.Item2} jitter={profile.Item3} loss={profile.Item4:P0} reorder={profile.Item5:P0} "
                + $"attempts={attempts} visible={visible} authority={hits} beamDisagreeBefore={legacyDisagreements} beamDisagreeAfter={disagreements} "
                + $"contactVisible={contactVisible} contactHistorical={contactHits} contactLive={contactLiveHits} historicalOnly={historicalOnly} liveOnly={liveOnly} "
                + $"historyMisses={historyMisses} meanRewind={rewind / attempts:F2} sweepDistance=4");
        }
    }

}
