using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest
{
    internal static class HealthShotTests
    {
        private static int _checks;
        private static readonly Scene _scene = new(new Vector2i(256, 192),
            Mods.Input.SyntheticInput.CreateKeyboard(), Mods.Input.SyntheticInput.CreateMouse(),
            _ => { }, () => { }, initializeRuntime: false);
        internal static void Check(bool ok, string message)
        {
            _checks++;
            if (!ok) throw new InvalidOperationException(message);
        }
        public static int Run()
        {
            try
            {
                OpponentHudUsesAuthorityHealth();
                OpponentHudCanHideHealth();
                AuthoritativeHealRaisesOpponentHud();
                PredictionReconcileDoesNotFakeHeal();
                ClaimAfflictionComesFromCurrentHit();
                RespawnClearsPredictedHealth();
                DamageResetUsesNoAttackerSentinel();
                FiredCounterRequiresActualSpawn();
                OldLifeShootPressIsRejected();
                RecoveredShootPressCannotCrossLife();
                DuplicateIntentDoesNotDuplicateShot();
                FrameWrapDoesNotDuplicateShot();
                ReorderedIntentDoesNotDuplicateShot();
                DeadHeldFireDoesNotSpawnGhostShot();
                GhostShotFaultMatrix();
                ClaimGeometryBoundaries();
                ClaimArbitrationOrdering();
                DamagePipelineSessionTotalsSurviveMatchReset();
                Console.WriteLine($"PASS: {_checks} health/shot assertions");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { NetSession.Stop(); }
        }

        internal static PlayerState State(ushort life = 7, ushort health = 99) => new()
        {
            SlotIndex = 1, SlotGeneration = 10, LifeId = life, Health = health,
            Flags = (byte)(PlayerState.FlagActive | (health > 0 ? PlayerState.FlagSpawned : 0)),
            Position = new Vector3(10, 2, 3), Facing = Vector3.UnitZ
        };
        internal static void Session()
        {
            NetSession.StartPlayback();
            GameState.Mode = GameMode.Battle;
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 51, AuthorityEpoch = 4 }, false);
            var roster = RosterPacket.Create();
            roster.MatchId = 51; roster.AuthorityEpoch = 4; roster.Revision = 1; roster.Count = 2;
            roster.Slots[0] = 0; roster.Generations[0] = 9;
            roster.Slots[1] = 1; roster.Generations[1] = 10;
            NetSession.ApplyRoster(roster);
            // This packet-only prediction fixture supplies its local actor directly.
            // Playback must reject Welcome; real UDP admission has its own test.
            typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, 0);
            var own = State(2); own.SlotIndex = 0; own.SlotGeneration = 9;
            NetPlayerLifecycle.AcceptState(own, 1);
            Snapshot(1, State());
        }
        internal static void Deliver(byte[] bytes)
        { NetSession.InjectPlaybackPacket(bytes, bytes.Length); NetSession.Update(0); }
        internal static void Snapshot(uint frame, PlayerState state)
        {
            const ushort matchId = 51;
            const int timeSyncSize = PlayerEntity.SlotCapacity * sizeof(float) * 2;
            int healthOffset = 1 + SnapshotHeader.Size + PlayerState.Size + timeSyncSize;
            byte[] bytes = new byte[healthOffset + NetHealthSync.HeaderSize];
            bytes[0] = (byte)PacketType.Snapshot;
            new SnapshotHeader { MatchId = matchId, AuthorityEpoch = 4, Frame = frame, PlayerCount = 1 }
                .Write(bytes.AsSpan(1));
            state.Write(bytes.AsSpan(1 + SnapshotHeader.Size));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(healthOffset), matchId);
            Deliver(bytes);
        }
        internal static void Field(object instance, string name, object value)
        { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value); }
        internal static PlayerEntity Player(int slot, int health = 99)
        {
            var player = (PlayerEntity)RuntimeHelpers.GetUninitializedObject(typeof(PlayerEntity));
            typeof(EntityBase).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, _scene);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.SlotIndex))!.SetValue(player, slot);
            player.Health = health;
            Field(player, "<Controls>k__BackingField", PlayerControls.GetDefault());
            Field(player, "<EquipInfo>k__BackingField", new EquipInfo());
            Field(player, "_ammo", new int[2]); Field(player, "_ammoMax", new[] { 999, 999 });
            var input = typeof(PlayerEntity).GetField("<Input>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
            input.SetValue(player, Activator.CreateInstance(input.FieldType, nonPublic: true));
            return player;
        }
        private static void Predict(PlayerEntity victim, BeamType beam = BeamType.Missile, uint amount = 32)
        {
            DamageFlags flags = 0;
            NetHitPrediction.NoteHit(victim, Player(0), ref flags, ref amount, beam, 1);
            victim.Health = NetHitPrediction.HealthFor(1, NetSession.RemoteStates[1].Health);
        }
        private static void OpponentHudUsesAuthorityHealth()
        {
            Session(); var victim = Player(1); Predict(victim);
            Check(victim.Health == 67 && NetHudHealth.Sample(victim).Health == 99, nameof(OpponentHudUsesAuthorityHealth));
            Snapshot(2, State(7, 67));
            Check(NetHudHealth.Sample(victim) == new HudHealthSample(67, 2, 7, true), "HUD snapshot provenance");
            NetSession.RemoteStateValid[1] = false;
            Check(NetHudHealth.Sample(victim).Health == victim.Health, "missing snapshot fallback");
        }
        private static void OpponentHudCanHideHealth()
        {
            Session();
            var config = new SessionStatePacket { MatchId = 51, AuthorityEpoch = 4, Revision = 1,
                MaxPlayers = 8, OwnerSlot = 0, Policy = ServerSessionPolicy.Lobby,
                Match = new MatchDefinition { RoomKey = "MP1 SANCTORUS", Mode = GameMode.Battle,
                    HideOpponentHealth = true } };
            byte[] wire = new byte[1 + SessionStatePacket.Size]; wire[0] = (byte)PacketType.SessionState;
            config.Write(wire.AsSpan(1));
            Check(SessionStatePacket.TryRead(wire.AsSpan(1), out var read) && read.Match.HideOpponentHealth,
                "hidden HP bit round trip");
            Deliver(wire);
            Check(!NetHudHealth.Visible(1) && NetHudHealth.Visible(0), nameof(OpponentHudCanHideHealth));
            config.Revision++; config.Match = config.Match with { HideOpponentHealth = false };
            config.Write(wire.AsSpan(1)); Deliver(wire);
            Check(NetHudHealth.Visible(1), "owner can show HP again");
        }
        private static void AuthoritativeHealRaisesOpponentHud()
        {
            Session(); var victim = Player(1, 30); Snapshot(2, State(7, 30));
            Predict(victim, BeamType.ShockCoil, 1);
            int before = NetHitPrediction.HealthFor(1, 30);
            Snapshot(3, State(7, 80));
            Check(NetHudHealth.Sample(victim).Health == 80 && NetHitPrediction.HealthFor(1, 80) > before,
                nameof(AuthoritativeHealRaisesOpponentHud));
        }
        private static void PredictionReconcileDoesNotFakeHeal()
        {
            Session(); var victim = Player(1); Predict(victim);
            byte[] claims = new byte[2048]; int size = NetHitClaims.Compose(claims);
            Check(size > 0, "prediction declared claim");
            var claim = HitClaimPacket.Read(claims.AsSpan(1));
            byte[] verdict = new byte[HitVerdictPacket.HeaderSize + HitVerdictPacket.EntrySize];
            HitVerdictPacket.Write(verdict, new[] { (claim.ClaimId, HitVerdictPacket.ResultRefused) }, 51, 4, 9, 2);
            NetHitClaims.ApplyVerdicts(verdict);
            Snapshot(2, State()); victim.Health = NetHitPrediction.HealthFor(1, 99);
            Check(NetHudHealth.Sample(victim).Health == 99, nameof(PredictionReconcileDoesNotFakeHeal));
        }
        private static void ClaimAfflictionComesFromCurrentHit()
        {
            Session();
            var victim = Player(1);
            Field(victim, "_frozenTimer", (ushort)20);
            uint damage = 12;
            DamageFlags flags = 0;
            NetHitPrediction.NoteHit(victim, Player(0), ref flags, ref damage,
                BeamType.Judicator, launchFrame: 1, afflictions: Affliction.None);
            byte[] claims = new byte[2048];
            int size = NetHitClaims.Compose(claims);
            Check(size > 0, "already-frozen victim still declared a normal claim");
            HitClaimPacket claim = HitClaimPacket.Read(claims.AsSpan(1));
            Check((claim.Flags & HitClaimPacket.FlagFrozen) == 0,
                "already-frozen victim cannot contaminate a later non-freeze claim");

            Session();
            victim = Player(1);
            damage = 12;
            flags = 0;
            NetHitPrediction.NoteHit(victim, Player(0), ref flags, ref damage,
                BeamType.Judicator, launchFrame: 2, afflictions: Affliction.Freeze);
            Array.Clear(claims);
            size = NetHitClaims.Compose(claims);
            Check(size > 0, "Judicator freeze declared a claim");
            claim = HitClaimPacket.Read(claims.AsSpan(1));
            Check((claim.Flags & HitClaimPacket.FlagFrozen) != 0,
                "current Judicator freeze travels even before victim state changes");

        }

        private static void RespawnClearsPredictedHealth()
        {
            Session(); var victim = Player(1); Predict(victim);
            Snapshot(2, State(7, 0)); Snapshot(3, State(8));
            Check(NetHitPrediction.HealthFor(1, 99) == 99 && NetHudHealth.Sample(victim).Health == 99,
                nameof(RespawnClearsPredictedHealth));
        }
        private static void DamageResetUsesNoAttackerSentinel()
        {
            byte[] attackers = (byte[])typeof(NetDamage).GetField("_attacker", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            byte[] beams = (byte[])typeof(NetDamage).GetField("_beam", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            NetDamage.Reset(); Check(Array.TrueForAll(attackers, b => b == 255) && Array.TrueForAll(beams, b => b == 255), nameof(DamageResetUsesNoAttackerSentinel));
            attackers[1] = 0; beams[1] = 0; NetDamage.ForgetSlot(1);
            Check(attackers[1] == 255 && beams[1] == 255, "slot reset sentinel");
        }
        private static void FiredCounterRequiresActualSpawn()
        {
            Session(); var shooter = Player(1);
            foreach (var result in Enum.GetValues<ShotAttemptResult>())
                if (result != ShotAttemptResult.Spawned) NetShotDiagnostics.Finish(shooter, result);
            Check(NetDamage.Fired[1] == 0, nameof(FiredCounterRequiresActualSpawn));
            NetShotDiagnostics.Finish(shooter, ShotAttemptResult.Spawned);
            Check(NetDamage.Fired[1] == 1, "actual spawn counted once");
        }
        internal static IntentPacket Intent(uint frame, ushort life = 7, bool shooting = false, bool playing = true) => new()
        {
            MatchId = 51, AuthorityEpoch = 4, SlotGeneration = 10, LifeId = life, Frame = frame,
            AckFrame = frame, Aim = Vector3.UnitZ, WeaponSelect = 255,
            Buttons = (playing ? IntentButtons.InPlayState : 0) | (shooting ? IntentButtons.Shoot : 0),
            Presses = new InputEdgeHistory()
        };
        private static void OldLifeShootPressIsRejected()
        {
            Session(); var shooter = Player(1); Snapshot(2, State(8));
            var old = Intent(100, shooting: true); old.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, 0);
            NetSession.AcceptSlotIntent(1, old); NetPlayerBridge.ApplyIntent(shooter, old);
            Check(!NetSession.RemoteIntentValid[1] && !shooter.Controls.Shoot.IsDown, nameof(OldLifeShootPressIsRejected));
        }
        private static void RecoveredShootPressCannotCrossLife()
        {
            Session(); var shooter = Player(1); NetPlayerBridge.ApplyIntent(shooter, Intent(10));
            var old = Intent(15); old.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, 2);
            Snapshot(2, State(8)); NetPlayerBridge.ForgetSlot(1); NetPlayerBridge.ApplyIntent(shooter, old);
            NetPlayerBridge.ApplyIntent(shooter, Intent(16, life: 8));
            Check(!shooter.Controls.Shoot.IsPressed, nameof(RecoveredShootPressCannotCrossLife));
        }
        private static void FrameWrapDoesNotDuplicateShot()
        {
            Session(); var shooter = Player(1);
            var baseline = Intent(uint.MaxValue - 2);
            NetSession.AcceptSlotIntent(1, baseline); NetPlayerBridge.ApplyIntent(shooter, baseline);
            var shot = Intent(0); shot.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, 1);
            NetSession.AcceptSlotIntent(1, shot); NetPlayerBridge.ApplyIntent(shooter, NetSession.RemoteIntents[1]);
            Check(shooter.Controls.Shoot.IsPressed, "recovered press crosses uint wrap");
            long accepted = NetSession.IntentsReceived;
            NetSession.AcceptSlotIntent(1, shot);
            NetPlayerBridge.ApplyIntent(shooter, NetSession.RemoteIntents[1]);
            Check(NetSession.IntentsReceived == accepted && !shooter.Controls.Shoot.IsPressed, "frame zero duplicate cannot repeat action");
            NetSession.AcceptSlotIntent(1, Intent(uint.MaxValue, shooting: true));
            Check(NetSession.IntentsReceived == accepted, "pre-wrap reorder refused");
        }
        private static void DuplicateIntentDoesNotDuplicateShot() => Ordering(false);
        private static void ReorderedIntentDoesNotDuplicateShot() => Ordering(true);
        private static void Ordering(bool reorder)
        {
            Session(); var shooter = Player(1); NetPlayerBridge.ApplyIntent(shooter, Intent(10));
            var shot = Intent(12); shot.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, 1);
            NetSession.AcceptSlotIntent(1, shot); NetPlayerBridge.ApplyIntent(shooter, NetSession.RemoteIntents[1]);
            Check(shooter.Controls.Shoot.IsPressed, "lost edge recovered");
            NetSession.AcceptSlotIntent(1, reorder ? Intent(11, shooting: true) : shot);
            NetPlayerBridge.ApplyIntent(shooter, NetSession.RemoteIntents[1]);
            Check(!shooter.Controls.Shoot.IsPressed, reorder ? nameof(ReorderedIntentDoesNotDuplicateShot) : nameof(DuplicateIntentDoesNotDuplicateShot));
        }
        private static void DeadHeldFireDoesNotSpawnGhostShot()
        {
            Session(); var shooter = Player(1); NetPlayerBridge.ApplyIntent(shooter, Intent(10));
            // The receiver has not seen death yet; the owner's packet explicitly says dead.
            var dead = Intent(11, shooting: true, playing: false); dead.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, 0);
            NetPlayerBridge.ApplyIntent(shooter, dead);
            Check(!shooter.Controls.Shoot.IsDown && !shooter.Controls.Shoot.IsPressed, nameof(DeadHeldFireDoesNotSpawnGhostShot));
        }

        private static void ClaimGeometryBoundaries()
        {
            MethodInfo within = typeof(NetHitClaims).GetMethod("WithinClaimRadius",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            bool Accepted(Vector3 point, byte beam = (byte)BeamType.Imperialist)
                => (bool)within.Invoke(null, new object[] { Vector3.Zero, point, beam })!;

            Check(Accepted(Vector3.Zero), "claim geometry accepts exact position");
            Check(Accepted(Vector3.UnitX * (NetHitClaims.ClaimRadius - 0.001f)),
                "claim geometry accepts just inside beam radius");
            Check(Accepted(Vector3.UnitX * NetHitClaims.ClaimRadius),
                "claim geometry accepts exact beam radius");
            Check(!Accepted(Vector3.UnitX * (NetHitClaims.ClaimRadius + 0.001f)),
                "claim geometry rejects just outside beam radius");
            Check(Accepted(Vector3.UnitX * NetHitClaims.MeleeRadius, HitClaimPacket.NoBeam),
                "claim geometry accepts exact melee radius");
            Check(!Accepted(Vector3.UnitX * (NetHitClaims.MeleeRadius + 0.001f), HitClaimPacket.NoBeam),
                "claim geometry rejects just outside melee radius");
            Check(!Accepted(new Vector3(Single.NaN, 0, 0)),
                "claim geometry rejects non-finite point");
        }

        private static void ClaimArbitrationOrdering()
        {
            MethodInfo diedBefore = typeof(NetHitClaims).GetMethod("ShooterDiedBeforeShot",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            bool Refused(bool dead, uint deathFire, uint launch, uint ack)
                => (bool)diedBefore.Invoke(null, new object[] { dead, deathFire, launch, ack })!;

            Check(!Refused(false, 10, 20, 20), "live shooter is never voided");
            Check(!Refused(true, 20, 20, 20), "equal-world lethal shots trade");
            Check(Refused(true, 19, 20, 20), "strictly earlier death voids later shot");
            Check(!Refused(true, 21, 20, 20), "later death does not void earlier shot");
            Check(Refused(true, 19, 0, 20), "zero launch frame falls back to ack");
            Check(!Refused(true, 20, 0, 20), "ack fallback preserves equal-world trade");
        }

        private static void DamagePipelineSessionTotalsSurviveMatchReset()
        {
            NetDamage.Reset();
            NetDamage.Resolved[1] = 3;
            NetDamage.Replayed[1] = 2;
            NetDamage.ResolvedSession[1] = 3;
            NetDamage.ReplayedSession[1] = 2;

            NetDamage.ResetForRoomChange();
            Check(NetDamage.Resolved[1] == 0 && NetDamage.Replayed[1] == 0,
                "room reset clears match damage pipeline");
            Check(NetDamage.ResolvedSession[1] == 3 && NetDamage.ReplayedSession[1] == 2,
                "room reset preserves session damage pipeline");

            NetDamage.Resolved[1] = 4;
            NetDamage.Replayed[1] = 4;
            NetDamage.Reset(resetSessionTotals: false);
            Check(NetDamage.Resolved[1] == 0 && NetDamage.Replayed[1] == 0,
                "persistent match reset clears match damage pipeline");
            Check(NetDamage.ResolvedSession[1] == 3 && NetDamage.ReplayedSession[1] == 2,
                "persistent match reset preserves session damage pipeline");

            NetDamage.Reset();
            Check(NetDamage.ResolvedSession[1] == 0 && NetDamage.ReplayedSession[1] == 0,
                "session reset clears session damage pipeline");
        }

        private static void GhostShotFaultMatrix()
        {
            int profiles = 0; long dropped = 0, duplicated = 0, reordered = 0;
            var output = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                foreach (int rtt in new[] { 0, 50, 150, 250, 320, 400 })
                foreach (int jitter in new[] { 0, 20, 40, 80 })
                foreach (double loss in new[] { 0, .01, .02, .05 })
                foreach (double duplicate in new[] { 0, .01, .03 })
                foreach (double reorder in new[] { 0, .01, .03 })
                foreach (BeamType weapon in new[] { BeamType.PowerBeam, BeamType.Missile, BeamType.Imperialist,
                    BeamType.Magmaul, BeamType.ShockCoil, BeamType.Judicator, BeamType.Battlehammer, BeamType.VoltDriver, BeamType.OmegaCannon })
                {
                    // Two independent delivery streams model authority and observer arrival.
                    // This matrix checks production input/lifecycle, not weapon physics (the asset check does that).
                    for (int peer = 0; peer < 2; peer++)
                    {
                        Session(); var puppet = Player(1);
                        Field(puppet, "<CurrentWeapon>k__BackingField", weapon);
                        var queue = new NetFaultQueue<byte[]>(8128 + peer, rtt / 2.0, jitter, loss, reorder, duplicate);
                        uint lastApplied = 0;
                        for (uint frame = 1; frame <= 150; frame++)
                        {
                            if (frame == 60)
                            {
                                Snapshot(2, State(8)); NetPlayerBridge.ForgetSlot(1); puppet.Controls.ClearAll();
                            }
                            if (frame <= 105)
                            {
                                bool alive = frame < 30 || frame >= 70;
                                bool shooting = frame >= 10 && frame < 60 + (profiles % 3 - 1) * 3;
                                var input = Intent(frame, frame < 60 ? (ushort)7 : (ushort)8, shooting, alive);
                                input.WeaponSelect = (byte)weapon;
                                // A dead press repeated in history must not become an alive action.
                                if (frame is >= 30 and < 35) input.Presses[0] = InputEdgeHistory.Encode(1, IntentButtons.Shoot, (int)(frame - 30));
                                byte[] bytes = new byte[IntentPacket.FullSize]; input.Write(bytes);
                                queue.Enqueue(frame * 1000.0 / 60, bytes);
                            }
                            while (queue.TryDequeue(frame * 1000.0 / 60, out var bytes))
                                NetSession.AcceptSlotIntent(1, IntentPacket.Read(bytes));
                            if (!NetSession.RemoteIntentValid[1]) continue;
                            var accepted = NetSession.RemoteIntents[1];
                            NetPlayerBridge.ApplyIntent(puppet, accepted);
                            bool fires = puppet.Controls.Shoot.IsDown || puppet.Controls.Shoot.IsPressed;
                            Check(!fires || (accepted.LifeId == NetPlayerLifecycle.Get(1)
                                && accepted.Buttons.HasFlag(IntentButtons.InPlayState)),
                                $"ghost control {weapon} rtt={rtt} jitter={jitter} loss={loss} duplicate={duplicate} reorder={reorder} peer={peer}");
                            if (accepted.Frame == lastApplied) Check(!puppet.Controls.Shoot.IsPressed, "duplicate history never repeats a trigger");
                            lastApplied = accepted.Frame;
                        }
                        dropped += queue.Dropped; duplicated += queue.Duplicated; reordered += queue.Reordered;
                    }
                    profiles++;
                }
            }
            finally { Console.SetOut(output); }
            Check(dropped > 0 && duplicated > 0 && reordered > 0, "fault matrix exercised every fault type");
            Console.WriteLine($"Ghost input matrix: {profiles} weapon/profiles, 2 delivery streams, dropped={dropped} duplicated={duplicated} reordered={reordered}");
        }
    }
}
