using System;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Asset-backed checks: actual controls, weapon spawn, damage and respawn in the headless engine.
    public static class NetCombatCheck
    {
        private static int _checks;
        private static void Check(bool ok, string name)
        {
            _checks++;
            if (!ok) throw new InvalidOperationException(name);
            Console.WriteLine($"COMBAT PASS {name}");
        }
        public static int Run(string room)
        {
            if (!ServerSim.Available(out string reason)) { Console.WriteLine(reason); return 1; }
            var sim = new ServerSim();
            if (!sim.Start(room, GameMode.Battle, 2, _ => { }, () => { })) return 1;
            try
            {
                NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 1, AuthorityEpoch = 1,
                    RoomKey = room, Mode = (byte)GameMode.Battle }, false);
                var roster = RosterPacket.Create();
                roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.Count = 2;
                for (byte i = 0; i < 2; i++) { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Names[i] = $"CHECK{i}"; }
                NetSession.ApplyRoster(roster);
                for (int i = 0; i < 120; i++) sim.Step();
                var shooter = PlayerEntity.Players[0]; var victim = PlayerEntity.Players[1];
                Check(shooter.ModIsInPlay && victim.ModIsInPlay && sim.StepFailures == 0, "spawn both players");
                var scene = (Scene)typeof(ServerSim).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sim)!;
                var fire = typeof(PlayerEntity).GetMethod("TryFireWeapon", BindingFlags.Instance | BindingFlags.NonPublic)!;
                InvalidHitClaimIsRefused();
                InvulnerableClaimIsRefused();
                MutualKillOrdering();
                ClaimArbitrationHasDeadline();
                ContinuousPhaseAgreesAcrossPeers();
                GameState.PointGoal = 1000; GameState.MatchTime = 3600;
                Array.Clear(GameState.Points);
                shooter.Health = victim.Health = 99;
                uint frame = 200;
                foreach (BeamType weapon in new[] { BeamType.PowerBeam, BeamType.Missile, BeamType.Imperialist,
                    BeamType.Magmaul, BeamType.ShockCoil, BeamType.Judicator, BeamType.Battlehammer, BeamType.VoltDriver })
                {
                    shooter.ModArmWeapon(weapon);
                    int before = NetDamage.Fired[0];
                    for (int i = 0; i < 30; i++)
                    {
                        NetSession.AcceptSlotIntent(0, Intent(shooter, ++frame, playing: false, shoot: true));
                        sim.Step();
                    }
                    Check(shooter.ModIsInPlay && NetDamage.Fired[0] == before, $"DeadHeldFireDoesNotSpawnGhostShot/{weapon} hp={shooter.Health} load={shooter.LoadFlags} fired={before}->{NetDamage.Fired[0]} match={GameState.MatchState}");
                    NetSession.AcceptSlotIntent(0, Intent(shooter, ++frame, playing: true, shoot: false));
                    sim.Step();
                }
                shooter.ModArmWeapon(BeamType.Missile);
                for (int i = 0; i < 40; i++) sim.Step();
                shooter.ModSetAmmo(0, 0);
                typeof(PlayerEntity).GetField("_timeSinceShot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shooter, (ushort)1000);
                shooter.Controls.Shoot.IsDown = shooter.Controls.Shoot.IsPressed = true;
                int fired = NetDamage.Fired[0];
                Check(!(bool)fire.Invoke(shooter, null)! && NetDamage.Fired[0] == fired
                    && NetShotDiagnostics.Outcomes[(int)BeamType.Missile, (int)ShotAttemptResult.NoAmmo] > 0,
                    "FiredCounterRequiresActualSpawn/empty missile");
                shooter.ModSetAmmo(400, 50);
                // Spawn with the production weapon table, then cross the actual Spawn method.
                foreach (var shot in new[] { (BeamType.Missile, false), (BeamType.Missile, true), (BeamType.Magmaul, false), (BeamType.Judicator, false) })
                {
                    BeamType weapon = shot.Item1;
                    shooter.ModArmWeapon(weapon);
                    shooter.EquipInfo.ChargeLevel = shot.Item2 ? (ushort)(shooter.EquipInfo.Weapon.FullCharge * 2) : (ushort)0;
                    NetUnlagged.BeginShot(shooter);
                    var result = BeamProjectileEntity.Spawn(shooter, shooter.EquipInfo, shooter.Position + Vector3.UnitY,
                        Vector3.UnitY, BeamSpawnFlags.NoMuzzle, shooter.NodeRef, scene);
                    NetUnlagged.EndShot(shooter);
                    BeamProjectileEntity? launched = null;
                    foreach (var beam in shooter.EquipInfo.Beams)
                        if (beam.Owner == shooter && beam.Lifespan > 0 && beam.Beam == weapon) launched = beam;
                    Check(result != BeamResultFlags.NoSpawn && launched != null, $"production projectile/{weapon}");
                    typeof(NetHitClaims).GetMethod("NoteRescued", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { 0, 1, launched!.ModLaunchFrame });
                    if (shot.Item2) Check(launched!.Flags.TestFlag(BeamFlags.Homing), "charged missile uses homing flight");
                    shooter.Health = 0;
                    Check(NetPlayerLifecycle.CurrentProjectile(launched!), $"ProjectileLifecycleAcrossShooterDeath/{weapon}");
                    ushort life = NetPlayerLifecycle.Get(0);
                    shooter.Spawn(shooter.Position, Vector3.UnitZ, Vector3.UnitY, shooter.NodeRef, respawn: true);
                    Check(NetPlayerLifecycle.Get(0) != life, "actual spawn allocates new life");
                    Console.WriteLine($"COMBAT projectile {weapon} lifespan={launched!.Lifespan:F2}s survivesSpawn={launched.Lifespan > 0} valid={NetPlayerLifecycle.CurrentProjectile(launched)}");
                    Check(launched.Lifespan > 0 && NetPlayerLifecycle.CurrentProjectile(launched), $"ProjectileLifecycleAcrossShooterRespawn/{weapon}");
                    Check(NetHitClaims.AlreadyRescued(0, 1, launched.ModLaunchFrame, launched.ModLaunchKey)
                        && !NetHitClaims.AlreadyRescued(0, 1, launched.ModLaunchFrame, launched.ModLaunchKey),
                        $"rescued flight cannot pay twice after respawn/{weapon}");
                    victim.Health = 99;
                    victim.TakeDamage(1, DamageFlags.IgnoreInvuln | DamageFlags.NoDmgInvuln, null, launched);
                    Check(victim.Health < 99, $"old launch still damages target/{weapon}");
                    ushort savedLife = launched.ModLaunchLife;
                    launched.ModLaunchLife++;
                    Check(!NetPlayerLifecycle.CurrentProjectile(launched), "forged launch life rejected");
                    launched.ModLaunchLife = savedLife;
                    var childKey = launched.ModLaunchKey;
                    var child = BeamProjectileEntity.Spawn(shooter, shooter.EquipInfo, shooter.Position + Vector3.UnitY,
                        Vector3.UnitY, BeamSpawnFlags.NoMuzzle, shooter.NodeRef, scene, parent: launched);
                    bool inherited = false;
                    foreach (var beam in shooter.EquipInfo.Beams)
                        if (beam != launched && beam.Lifespan > 0 && beam.ModLaunchKey == childKey) inherited = true;
                    Check(child != BeamResultFlags.NoSpawn && inherited, $"ricochet child keeps original fire event/{weapon}");
                }
                Check(sim.StepFailures == 0, "no simulation failures");
                Console.WriteLine($"COMBAT PASS {_checks} assertions");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"COMBAT FAIL {ex}"); return 1; }
            finally { sim.Stop(); }
        }
        private static IntentPacket Intent(PlayerEntity player, uint frame, bool playing, bool shoot) => new()
        {
            MatchId = NetSession.CurrentMatchId, AuthorityEpoch = NetSession.AuthorityEpoch,
            SlotGeneration = NetPlayerLifecycle.Generation(player.SlotIndex), LifeId = NetPlayerLifecycle.Get(player.SlotIndex),
            Frame = frame, AckFrame = NetSession.NetFrame, Aim = Vector3.UnitZ, Position = player.Position,
            WeaponSelect = 255, AmmoUa = 400, AmmoMissiles = 50,
            Buttons = (playing ? IntentButtons.InPlayState : 0) | (shoot ? IntentButtons.Shoot : 0),
            Presses = new uint[IntentPacket.PressHistory]
        };

        private static void PrepareClaims()
        {
            NetHitClaims.Reset();
            foreach (var player in PlayerEntity.Players)
            {
                if (player.SlotIndex > 1) continue;
                player.Spawn(player.Position, Vector3.UnitZ, Vector3.UnitY, player.NodeRef, respawn: true);
                typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(player, (ushort)0);
                player.Health = 1;
            }
            NetHitClaims.Tick();
            NetUnlagged.Record(NetSession.NetFrame - 2);
            NetUnlagged.Record(NetSession.NetFrame - 1);
            NetUnlagged.Record(NetSession.NetFrame);
        }
        private static HitClaimPacket Claim(int shooter, uint world) => new()
        {
            MatchId = NetSession.CurrentMatchId, AuthorityEpoch = NetSession.AuthorityEpoch,
            ShooterGeneration = NetPlayerLifecycle.Generation(shooter), ShooterLifeId = NetPlayerLifecycle.Get(shooter),
            VictimSlot = (byte)(1 - shooter), VictimGeneration = NetPlayerLifecycle.Generation(1 - shooter),
            VictimLifeId = NetPlayerLifecycle.Get(1 - shooter), ClaimId = 1, AckFrame = world, LaunchFrame = world,
            Damage = 1, Beam = (byte)BeamType.Imperialist, HitPoint = PlayerEntity.Players[1 - shooter].Position
        };
        private static void Receive(int shooter, in HitClaimPacket claim)
        {
            byte[] bytes = new byte[1 + HitClaimPacket.Size]; bytes[0] = 1; claim.Write(bytes.AsSpan(1));
            NetHitClaims.Receive(shooter, bytes);
        }
        private static void InvalidHitClaimIsRefused()
        {
            PrepareClaims();
            var judge = typeof(NetHitClaims).GetMethod("Judge", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (float offset in new[] { .5f, 1f, 1.5f, 2f, 2.5f, 4f })
            {
                var claim = Claim(0, NetSession.NetFrame - 1); claim.HitPoint += Vector3.UnitX * offset;
                byte result = (byte)judge.Invoke(null, new object[] { 0, claim })!;
                Check(offset <= 2 ? result == HitVerdictPacket.ResultApplied : result != HitVerdictPacket.ResultApplied,
                    $"InvalidHitClaimIsRefused/offset={offset} result={HitVerdictPacket.Describe(result)}");
            }
        }
        private static void MutualKillOrdering()
        {
            foreach (int earlier in new[] { -1, 0, 1 })
            foreach (bool reverse in new[] { false, true })
            foreach (int arrivalGap in new[] { 0, 1, 8 })
            {
                PrepareClaims();
                uint world = NetSession.NetFrame - 1;
                var a = Claim(0, earlier == 0 ? world - 1 : world);
                var b = Claim(1, earlier == 1 ? world - 1 : world);
                if (reverse) Receive(1, b); else Receive(0, a);
                for (int i = 0; i < arrivalGap; i++) { NetSession.Update(NetSession.NetFrame / 60.0); NetHitClaims.Tick(); }
                if (reverse) Receive(0, a); else Receive(1, b);
                for (int i = 0; i < NetHitClaims.MaxGraceFrames + 2; i++)
                { NetSession.Update(NetSession.NetFrame / 60.0); NetHitClaims.Tick(); }
                bool aDead = PlayerEntity.Players[0].Health == 0, bDead = PlayerEntity.Players[1].Health == 0;
                Check(earlier == -1 ? aDead && bDead : earlier == 0 ? !aDead && bDead : aDead && !bDead,
                    $"MutualKillOrdering/earlier={earlier} reversed={reverse} arrivalGap={arrivalGap} ADead={aDead} BDead={bDead}");
            }
            PrepareClaims();
        }

        private static void InvulnerableClaimIsRefused()
        {
            PrepareClaims();
            var victim = PlayerEntity.Players[1];
            typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(victim, (ushort)1000);
            var claim = Claim(0, NetSession.NetFrame - 1);
            Receive(0, claim);
            for (int i = 0; i < NetHitClaims.MaxGraceFrames + 2; i++)
            { NetSession.Update(NetSession.NetFrame / 60.0); NetHitClaims.Tick(); }
            Check(victim.Health == 1 && NetHitClaims.AppliedHere == 0
                && !NetHitClaims.AlreadyRescued(0, 1, claim.LaunchFrame),
                "invulnerable target cannot be reported or prepaid as rescued damage");
            int bucket = NetShotDiagnostics.Bucket(BeamType.Imperialist);
            long refused = NetShotDiagnostics.Refusals[bucket], declared = NetShotDiagnostics.Claims[bucket];
            Receive(0, claim); NetHitClaims.Tick();
            Check(NetShotDiagnostics.Refusals[bucket] == refused && NetShotDiagnostics.Claims[bucket] == declared,
                "repeated claim does not duplicate per-weapon outcome counters");
            PrepareClaims();
        }

        private static void ClaimArbitrationHasDeadline()
        {
            PrepareClaims();
            var victim = PlayerEntity.Players[1];
            typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(victim, (ushort)1000);
            uint firstWorld = NetSession.NetFrame - 1;
            byte verdict = 255;
            var previousSink = NetHitClaims.VerdictSink;
            NetHitClaims.VerdictSink = (int slot, ReadOnlySpan<(ushort Id, byte Result)> entries) =>
            {
                foreach (var entry in entries) if (slot == 0 && entry.Id == 1) verdict = entry.Result;
            };
            try
            {
                Receive(0, Claim(0, firstWorld));
                // Keep delivering earlier shots before the preceding one has
                // finished grace. None can kill; they must not starve id 1.
                for (uint tick = 1; tick <= 2 * NetHitClaims.MaxGraceFrames + 1; tick++)
                {
                    NetSession.Update(NetSession.NetFrame / 60.0);
                    if (tick % 8 == 0)
                    {
                        NetUnlagged.Record(NetSession.NetFrame);
                        var claim = Claim(0, NetSession.NetFrame);
                        claim.ClaimId = (ushort)(tick + 1); claim.LaunchFrame = firstWorld - tick;
                        Receive(0, claim);
                    }
                    NetHitClaims.Tick();
                }
                Check(verdict == HitVerdictPacket.ResultNoDamage,
                    "earlier claim stream cannot starve a verdict past the arbitration deadline");
            }
            finally { NetHitClaims.VerdictSink = previousSink; PrepareClaims(); }
        }

        private static void ContinuousPhaseAgreesAcrossPeers()
        {
            // Independent golden phases preserve the original 30 Hz fractional
            // damage cadence, including the exact-boundary rounding difference.
            foreach (var golden in new[] {
                (10, new[] { 6, 12, 18, 24, 30, 38, 44, 50, 56, 62 }),
                (15, new[] { 4, 8, 12, 16, 20, 24, 28, 34, 38, 42, 46, 50, 54, 58, 62 }) })
            {
                bool exact = true;
                for (int phase = 0; phase < 64; phase++)
                    exact &= ContinuousWeaponPhase.Amount(golden.Item1, (ulong)phase, true)
                        == (Array.IndexOf(golden.Item2, phase) >= 0 ? 1 : 0);
                Check(exact, $"continuous golden damage cadence/{golden.Item1}");
            }
            foreach (int damage in new[] { 10, 15, 32, 47, 64 })
            foreach (int phaseOffset in new[] { 0, 1, 5, 19 })
            {
                var owner = new ContinuousWeaponPhase(2); var authority = new ContinuousWeaponPhase(2);
                var observer = new ContinuousWeaponPhase(2);
                bool agrees = true;
                for (uint tick = 1; tick <= 192; tick++)
                {
                    uint logical = 100 + (uint)phaseOffset + tick;
                    ulong a = owner.Resolve(0, tick, true, true, logical, true, 0, 0, out _);
                    // Jitter changes the latest packet without re-anchoring a held stream.
                    uint age = tick % 6;
                    ulong b = authority.Resolve(0, tick + 700, true, false, 9000, true,
                        logical - age, age, out _);
                    ulong c = observer.Resolve(0, tick + 1300, true, false, 5000, true,
                        logical, 0, out _);
                    agrees &= a == b && b == c && ContinuousWeaponPhase.Amount(damage, a, true) == ContinuousWeaponPhase.Amount(damage, b, true)
                        && ContinuousWeaponPhase.Amount(damage, a, false) == ContinuousWeaponPhase.Amount(damage, c, false);
                }
                Check(agrees, $"ContinuousPhaseAgreesAcrossPeers/damage={damage} initialOffset={phaseOffset}");
            }
        }
    }
}
