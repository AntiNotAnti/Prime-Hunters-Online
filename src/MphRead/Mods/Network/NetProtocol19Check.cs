using System;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public static class NetProtocol19Check
{
    private static int _checks;
    private static void Check(bool value, string name)
    { _checks++; if (!value) throw new InvalidOperationException(name); Console.WriteLine("V19 COMBAT PASS " + name); }
    public static int Run(string room)
    {
        var sim = new ServerSim();
        if (!sim.Start(room, GameMode.Battle, 2, _ => { }, () => { })) return 1;
        try
        {
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 1, AuthorityEpoch = 1, RoomKey = room, Mode = (byte)GameMode.Battle }, false);
            var roster = RosterPacket.Create(); roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.Count = 2;
            for (byte i = 0; i < 2; i++) { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Hunters[i] = (byte)(i == 1 ? Hunter.Weavel : Hunter.Samus); roster.Names[i] = "V19"; }
            NetSession.ApplyRoster(roster); NetSlotManager.Sync();
            for (int i = 0; i < 120; i++) sim.Step();
            var shooter = PlayerEntity.Players[0]; var victim = PlayerEntity.Players[1];
            Check(victim.Hunter == Hunter.Weavel && victim.ModIsInPlay, "Weavel spawned");
            GameState.PointGoal = 10000; GameState.MatchTime = 3600;
            void Prepare(int body, int turret)
            {
                Authority(true); NetHitClaims.Reset(); NetHitPrediction.Reset();
                victim.Spawn(victim.Position, Vector3.UnitZ, Vector3.UnitY, victim.NodeRef, respawn: true);
                victim.Health = 199;
                victim.ModStartFormSwitch(); victim.ModForceForm(true);
                typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(victim, (ushort)0);
                victim.Health = body; victim.Halfturret.Health = turret;
                if (turret == 0) victim.OnHalfturretDied();
                shooter.Health = 199;
                NetHitClaims.Tick();
                NetUnlagged.Record(NetSession.NetFrame);
            }
            foreach (int body in new[] { 1, 9, 100 })
            foreach (int turret in new[] { 1, 37, 100 })
            foreach (uint damage in new uint[] { 1, 11, 64, 128, 256 })
            {
                Prepare(body, turret);
                victim.TakeDamage(damage, DamageFlags.Halfturret | DamageFlags.NoDmgInvuln, null, shooter);
                int authorityBody = victim.Health, authorityTurret = victim.Halfturret.Health;
                Prepare(body, turret); Authority(false);
                victim.TakeDamage(damage, DamageFlags.Halfturret | DamageFlags.NoDmgInvuln, null, shooter);
                Check(victim.Health == authorityBody && victim.Halfturret.Health == authorityTurret,
                    $"same body/turret split body={body} turret={turret} damage={damage}");
            }
            Prepare(100, 37);
            CombatAckEntry received = default; int answers = 0;
            NetHitClaims.CombatAckSink = (int slot, ReadOnlySpan<CombatAckEntry> entries) => { received = entries[0]; answers++; };
            uint frame = NetSession.NetFrame;
            victim.TakeDamage(11, DamageFlags.Halfturret | DamageFlags.NoDmgInvuln, null, shooter);
            int bodyAfter = victim.Health, turretAfter = victim.Halfturret.Health;
            victim.Health = 50; victim.Halfturret.Health = 2;
            var claim = new HitClaimPacket { ClaimId = 1, MatchId = 1, AuthorityEpoch = 1,
                ShooterGeneration = NetPlayerLifecycle.Generation(0), ShooterLifeId = NetPlayerLifecycle.Get(0),
                VictimSlot = 1, VictimGeneration = NetPlayerLifecycle.Generation(1), VictimLifeId = NetPlayerLifecycle.Get(1),
                AckFrame = frame, Damage = 11, Beam = (byte)BeamType.PowerBeam, Flags = HitClaimPacket.FlagHalfturret, HitPoint = victim.Position };
            byte[] packet = new byte[1 + HitClaimPacket.Size]; packet[0] = 1; claim.Write(packet.AsSpan(1));
            NetHitClaims.Receive(0, packet); NetHitClaims.Tick();
            Check(answers == 1 && received.Result == (byte)CombatAckResult.AlreadyResolved && received.HealthAfter == bodyAfter
                && received.HalfturretHealthAfter == turretAfter && received.DamageApplied == 5
                && (received.Flags & CombatAckFlags.HalfturretAffected) != 0, $"AlreadyResolved preserves actual historical outcome after later damage: answers={answers}, result={received.Result}, health={received.HealthAfter}/{bodyAfter}, turret={received.HalfturretHealthAfter}/{turretAfter}, damage={received.DamageApplied}, flags={received.Flags}, received={NetHitClaims.Received}, pending={NetHitClaims.ClaimsPendingCurrent}, refused={NetHitClaims.RefusedHere}, oldlife={NetPlayerLifecycle.OldLifeClaims}, lives={claim.ShooterLifeId}/{claim.VictimLifeId}, ready={NetRoomChange.GameplayReady}");
            var first = received; victim.Health = 20; victim.Halfturret.Health = 1;
            NetHitClaims.Receive(0, packet); NetHitClaims.Tick();
            Check(answers == 2 && first.Equals(received), "claim retry returns byte-identical terminal outcome");
            Prepare(100, 37); answers = 0;
            claim.ShooterLifeId = NetPlayerLifecycle.Get(0); claim.VictimLifeId = NetPlayerLifecycle.Get(1);
            claim.AckFrame = NetSession.NetFrame; claim.Write(packet.AsSpan(1));
            string studyRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "prime-claim-study-" + Guid.NewGuid().ToString("N"));
            Telemetry.ProductionTelemetry.Configure(new Telemetry.NetTelemetryConfig { Directory = studyRoot, LocalRaw = false });
            Telemetry.ProductionTelemetry.Begin(room, "Battle", 2);
            LagCompensationPolicy.SetTiming(0, new LagTiming(100, 10, 80, 8));
            NetHitClaims.Receive(0, packet);
            for (int i = 0; i <= NetHitClaims.MaxGraceFrames + 1; i++) { typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, NetSession.NetFrame + 1); NetHitClaims.Tick(); }
            Check(answers == 1 && received.Result == (byte)CombatAckResult.Applied && victim.Health == 95 && victim.Halfturret.Health == 31,
                "rescued turret claim uses canonical pre-split damage once");
            NetHitClaims.Receive(0, packet); NetHitClaims.Tick();
            Check(victim.Health == 95 && victim.Halfturret.Health == 31 && answers == 2, "rescued turret retry cannot pay twice");
            Telemetry.ProductionTelemetry.Shutdown();
            var studyFiles = System.IO.Directory.GetFiles(studyRoot, "*.summary.json", System.IO.SearchOption.AllDirectories);
            var study = System.Text.Json.JsonSerializer.Deserialize(System.IO.File.ReadAllText(studyFiles[0]), Telemetry.TelemetryJsonContext.Default.TelemetrySummary)!;
            long inside = 0, outside = 0;
            foreach (var bucket in study.LagComp) { inside += bucket.RescuesInside; outside += bucket.RescuesOutside; }
            Check(inside == 1 && outside == 0, "rescue study preserves admission rewind without adding arbitration grace");
            Telemetry.ProductionTelemetry.Configure(new Telemetry.NetTelemetryConfig { Enabled = false });
            System.IO.Directory.Delete(studyRoot, recursive: true);
            Prepare(100, 37); answers = 0;
            uint phase = NetSession.NetFrame + 50;
            var continuous = new BeamProjectileEntity(shooter.OwningScene) { Owner = shooter,
                Beam = BeamType.ShockCoil, ModHasSharedContinuousPhase = true, ModContinuousPhase = phase };
            NetPlayerLifecycle.StampProjectile(continuous);
            victim.TakeDamage(10, DamageFlags.Halfturret | DamageFlags.NoDmgInvuln, null, continuous);
            int continuousBody = victim.Health, continuousTurret = victim.Halfturret.Health;
            Check(continuousBody < 100 && continuousTurret < 37, "continuous physical hit records actual split");
            typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, NetSession.NetFrame + 1);
            NetUnlagged.Record(NetSession.NetFrame);
            claim = new HitClaimPacket { ClaimId = 21, MatchId = 1, AuthorityEpoch = 1,
                ShooterGeneration = NetPlayerLifecycle.Generation(0), ShooterLifeId = NetPlayerLifecycle.Get(0),
                VictimSlot = 1, VictimGeneration = NetPlayerLifecycle.Generation(1), VictimLifeId = NetPlayerLifecycle.Get(1),
                Frame = phase, AckFrame = NetSession.NetFrame, LaunchFrame = NetSession.NetFrame,
                Damage = 10, Beam = (byte)BeamType.ShockCoil,
                Flags = HitClaimPacket.FlagHalfturret | HitClaimPacket.FlagContinuousTick, HitPoint = victim.Position };
            claim.Write(packet.AsSpan(1)); NetHitClaims.Receive(0, packet); NetHitClaims.Tick();
            Check(answers == 1 && received.Result == (byte)CombatAckResult.AlreadyResolved
                && received.HealthAfter == continuousBody && received.HalfturretHealthAfter == continuousTurret,
                $"same continuous tick settles despite different world ACK frames: answers={answers} result={received.Result} body={received.HealthAfter}/{continuousBody} turret={received.HalfturretHealthAfter}/{continuousTurret}");
            claim.ClaimId++; claim.Write(packet.AsSpan(1)); NetHitClaims.Receive(0, packet); NetHitClaims.Tick();
            victim.TakeDamage(10, DamageFlags.Halfturret | DamageFlags.NoDmgInvuln, null, continuous);
            Check(answers == 2 && received.Result == (byte)CombatAckResult.AlreadyResolved
                && victim.Health == continuousBody && victim.Halfturret.Health == continuousTurret,
                "different claim ID and delayed physical copy cannot pay continuous tick twice");
            Prepare(100, 37); Authority(false);
            NetSession.RemoteStates[1] = new PlayerState { SlotIndex = 1, SlotGeneration = NetPlayerLifecycle.Generation(1),
                LifeId = NetPlayerLifecycle.Get(1), Health = 100, HalfturretActive = true, HalfturretHealth = 37 };
            victim.TakeDamage(20, DamageFlags.NoDmgInvuln, null, shooter);
            byte[] outgoing = new byte[2048];
            Check(NetHitClaims.Compose(outgoing) > 0, "prediction emits exact claim");
            var predictedClaim = HitClaimPacket.Read(outgoing.AsSpan(1));
            var correction = new CombatAckEntry { ClaimId = predictedClaim.ClaimId, VictimSlot = 1,
                VictimGeneration = predictedClaim.VictimGeneration, VictimLife = predictedClaim.VictimLifeId,
                Result = (byte)CombatAckResult.AlreadyResolved, DamageApplied = 15, HealthAfter = 85,
                HalfturretHealthAfter = 37, DamageSequence = 1, Flags = CombatAckFlags.OutcomePresent };
            byte[] verdict = new byte[HitVerdictPacket.HeaderSize + CombatAckEntry.Size];
            HitVerdictPacket.Write(verdict, new[] { correction }, 1, 1, NetPlayerLifecycle.Generation(0), NetPlayerLifecycle.Get(0));
            NetHitClaims.ApplyVerdicts(verdict);
            Check(victim.Health == 85 && NetHitPrediction.HealthFor(1, 100) == 85 && NetHitPrediction.DamageCorrections > 0,
                "CombatAck corrects visible health and retires exact debit immediately");
            long confirmations = NetHitPrediction.Confirmed;
            NetHitClaims.ApplyVerdicts(verdict);
            Check(NetHitPrediction.Confirmed == confirmations && victim.Health == 85, "repeated CombatAck is idempotent");
            var newer = NetSession.RemoteStates[1]; newer.DamageEventId = 2; newer.Health = 90; NetSession.RemoteStates[1] = newer;
            Check(NetHitPrediction.HealthFor(1, 90) == 90, "newer snapshot replaces acknowledgement presentation");
            Console.WriteLine($"PASS: {_checks} Protocol 19 asset-backed checks"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { NetHitClaims.CombatAckSink = null; Authority(true); sim.Stop(); }
    }
    private static void Authority(bool enabled)
    {
        typeof(NetSession).GetProperty(nameof(NetSession.Role))!.SetValue(null, enabled ? NetRole.Host : NetRole.Client);
        typeof(NetSession).GetProperty(nameof(NetSession.IsAuthority))!.SetValue(null, enabled);
        typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, enabled ? -1 : 0);
    }
}
