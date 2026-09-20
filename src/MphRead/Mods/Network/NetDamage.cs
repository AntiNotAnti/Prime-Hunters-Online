using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Carries hits from the machine that resolved them to the machines that
    /// have to show them.
    ///
    /// Health alone is not a hit. Everything a player feels when they are
    /// shot -- the directional indicator, the damage animation, the grunt,
    /// the knockback, the "X KILLED YOU" banner -- is produced inside
    /// PlayerEntity.TakeDamage, and a client whose health was simply assigned
    /// from a snapshot ran none of it: the bar dropped in silence. So the
    /// authority records what it resolved, the snapshot carries it, and each
    /// client replays it through TakeDamage, which is the only way to get
    /// the engine's own feedback rather than an imitation of it.
    ///
    /// The counter is what makes the replay safe: snapshots repeat and can
    /// arrive out of order, and comparing health would replay one hit several
    /// times or miss two that cancelled out.
    /// </summary>
    public static class NetDamage
    {
        private const int Slots = PlayerEntity.SlotCapacity;

        private static readonly ushort[] _sequence = new ushort[Slots];
        private static readonly DamageEvent[,] _history = new DamageEvent[Slots, PlayerState.DamageHistory];
        private static readonly byte[] _attacker = new byte[Slots];
        private static readonly byte[] _beam = new byte[Slots];
        private static readonly byte[] _flags = new byte[Slots];
        private static readonly Vector3[] _direction = new Vector3[Slots];

        private static readonly ushort[] _lastLife = new ushort[Slots];
        private static readonly ushort[] _lastGeneration = new ushort[Slots];
        private static readonly ushort[] _lastSeen = new ushort[Slots];
        private static readonly bool[] _everSeen = new bool[Slots];

        public const byte NoSlot = 0xFF;
        public const byte NoBeam = 0xFF;

        static NetDamage()
        {
            Array.Fill(_attacker, NoSlot);
            Array.Fill(_beam, NoBeam);
        }

        /// <summary>
        /// Flags worth sending. The rest either describe how the damage was
        /// delivered locally (invulnerability handling) or would change the
        /// replay's outcome, which the authority has already decided.
        /// </summary>
        private const int RelayedFlags = (int)(DamageFlags.Headshot | DamageFlags.Deathalt
            | DamageFlags.Burn);

        /// <summary>
        /// True while a client is replaying the authority's hit, which is the
        /// one moment damage is allowed to land on a machine that does not
        /// own the simulation.
        /// </summary>
        public static bool Replaying { get; private set; }

        /// <summary>
        /// The weapon of the hit being replayed, for the kill banner. The
        /// beam entity only ever existed on the authority's machine, so
        /// without this the victim was told it had been killed by whatever
        /// the fallback branch guessed.
        /// </summary>
        public static BeamType ReplayBeam { get; private set; } = BeamType.None;

        /// <summary>
        /// Hits the authority resolved, and hits each client replayed, per
        /// slot. The two are the ends of the damage pipeline: if the first is
        /// zero the shot never connected anywhere, and if the second lags the
        /// first the hit was resolved and then failed to reach the victim.
        /// </summary>
        public static readonly int[] Resolved = new int[Slots];
        public static readonly int[] Replayed = new int[Slots];

        /// <summary>
        /// Beams each slot actually spawned on this machine.
        ///
        /// The missing third of the picture. Resolved says whether a hit
        /// landed and Replayed whether the victim was told, but neither can
        /// tell "the shot missed" from "the shot was never fired here at
        /// all" -- and on the authority, which is the only machine whose
        /// shots count, those are completely different faults. A puppet that
        /// holds fire on its owner's screen and spawns nothing here means the
        /// input never arrived; one that spawns plenty and resolves nothing
        /// means it is firing into the wrong place.
        /// </summary>
        public static readonly int[] Fired = new int[Slots];

        /// <summary>
        /// This machine's own shots, split by whether it was moving. The
        /// denominator for NetHitPrediction.UnpredictedMoving.
        /// </summary>
        public static long FiredMoving;
        public static long FiredStill;
        public static readonly int[] PlayerChecks = new int[Slots];
        public static readonly int[] PlayerOverlaps = new int[Slots];
        public static readonly int[] PlayerAccepted = new int[Slots];
        public static readonly int[,] PlayerOverlapsByShooter = new int[Slots, Slots];

        /// <summary>
        /// Total degrees between where a slot's shots went and where its gun
        /// was pointing, and the worst single case. UpdateAimVecs drags the
        /// shot half way from the aim towards the body's facing, so a puppet
        /// whose facing does not follow its aim fires beside itself.
        /// </summary>
        public static readonly double[] AimDrift = new double[Slots];
        public static readonly double[] WorstDrift = new double[Slots];

        /// <summary>
        /// The weapons that carry their damage somewhere other than a plain
        /// beam hit, counted where they decide whether to hurt anybody.
        ///
        /// A continuous homing beam does nothing at all without a target, and
        /// a bomb walks the player list itself and skips whoever it thinks is
        /// a team mate. Both refusals look identical from
        /// <see cref="Resolved"/> -- zero -- and neither is distinguishable
        /// there from a shot that simply missed, which is why the Shock Coil
        /// and the two bomb types could be reported dead in a live match
        /// while every count this file already kept looked healthy.
        /// </summary>
        public static int ShockCoilSpawned;
        public static int ShockCoilAcquired;
        public static int BombPlayerChecks;
        public static int BombTeamSkips;
        public static int BombHits;

        /// <summary>
        /// Damage the authority resolved, split by what delivered it.
        ///
        /// <see cref="Resolved"/> counts hits per victim, which answers
        /// "is damage working" and nothing finer. It cannot answer the
        /// question a player actually asks -- "does the Shock Coil hurt
        /// anybody?" -- and neither could any check in this project, which is
        /// why a weapon could be reported dead in a live match while every
        /// harness run came back PASS. Indexed by <see cref="BeamType"/>;
        /// bombs carry no beam and are counted on their own.
        /// </summary>
        public static readonly int[] DamageByBeam = new int[(int)BeamType.Enemy + 1];
        public static readonly int[] HitsByBeam = new int[(int)BeamType.Enemy + 1];
        public static int BombDamageDealt;
        public static int BombDamageHits;

        /// <summary>Why a press to lay a bomb did or did not produce one.</summary>
        public static int BombSpawnCalls;
        public static int BombSpawnMade;
        public static int BombSpawnDetonated;
        public static int BombSpawnStaleCount;
        public static int BombSpawnPoolEmpty;

        /// <summary>
        /// The closest any bomb came to somebody it could have hurt, and the
        /// radius it needed. Without these a run of "no bomb ever hit" cannot
        /// be told from "no bomb was ever near anybody", which is the whole
        /// difference between a broken weapon and a harness that never walked
        /// onto one.
        /// </summary>
        public static float BombNearest = Single.MaxValue;
        public static float BombRadiusSeen;

        /// <summary>Called wherever a beam is spawned, for <see cref="Fired"/>.</summary>
        public static void NoteFired(PlayerEntity shooter, Vector3 shotVec, Vector3 aimVec)
        {
            if (!NetSession.Active)
            {
                return;
            }
            int slot = shooter.SlotIndex;
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            Fired[slot]++;
            if (slot == NetHooks.LocalSlot)
            {
                if (shooter.Speed.LengthSquared > 0.0004f)
                {
                    FiredMoving++;
                }
                else
                {
                    FiredStill++;
                }
            }
            if (NetLog.Enabled)
            {
                // A beam is born at the muzzle, and the muzzle is derived from
                // CameraInfo rather than from Position -- so a respawn that
                // leaves the camera behind fires from wherever the player used
                // to be standing. Reported as "phantom shots from my position
                // before I died". Three units is far more than the offset can
                // legitimately be.
                Vector3 muzzle = shooter.ModMuzzlePos;
                float gap = (muzzle - shooter.Position).Length;
                if (gap > 3f)
                {
                    NetLog.Event($"[muzzle] slot {slot} fired from "
                        + $"({muzzle.X:F2},{muzzle.Y:F2},{muzzle.Z:F2}) while standing at "
                        + $"({shooter.Position.X:F2},{shooter.Position.Y:F2},{shooter.Position.Z:F2})"
                        + $" -- {gap:F1} units apart");
                }
                // And every shot in the first second of a life, with where it
                // left from. A beam leaving the spot this player died on is the
                // previous life still holding the trigger; one leaving the spawn
                // point is the game working as designed.
                uint spawned = NetPlayerBridge.SpawnFrame[slot];
                if (spawned != 0 && NetSession.NetFrame - spawned < 60)
                {
                    NetLog.Event($"[spawnfire] slot {slot} fired "
                        + $"{NetSession.NetFrame - spawned} frame(s) after spawning, from "
                        + $"({shooter.Position.X:F2},{shooter.Position.Y:F2},"
                        + $"{shooter.Position.Z:F2}) hp={shooter.Health}"
                        // The direction is the last unmeasured quantity: a shot
                        // that lands 23 units away seven frames after a spawn
                        // is either aimed there or carrying the aim of the life
                        // that just ended.
                        + $" shot=({shotVec.X:F2},{shotVec.Y:F2},{shotVec.Z:F2})"
                        + $" aim=({aimVec.X:F2},{aimVec.Y:F2},{aimVec.Z:F2})");
                }
            }
            if (shotVec.LengthSquared > 0.0001f && aimVec.LengthSquared > 0.0001f)
            {
                float dot = Math.Clamp(Vector3.Dot(shotVec.Normalized(), aimVec.Normalized()), -1f, 1f);
                double degrees = Math.Acos(dot) * 180.0 / Math.PI;
                AimDrift[slot] += degrees;
                WorstDrift[slot] = Math.Max(WorstDrift[slot], degrees);
            }
        }

        public static void NotePlayerOverlap(EntityBase? owner, PlayerEntity target)
        {
            if (!NetSession.Active || owner is not PlayerEntity shooter)
            {
                return;
            }
            if (shooter.SlotIndex >= 0 && shooter.SlotIndex < Slots
                && target.SlotIndex >= 0 && target.SlotIndex < Slots)
            {
                PlayerOverlapsByShooter[shooter.SlotIndex, target.SlotIndex]++;
            }
        }

        /// <summary>
        /// Everything except the counter and the baseline, for a room change.
        ///
        /// The counter must survive one. It is not a count of anything a
        /// match owns -- it is a sequence number, and its only use is the
        /// difference between two of them, so a new map does not invalidate
        /// it. Resetting it does invalidate it, because the authority and its
        /// clients do not change room on the same frame: whichever resets
        /// first goes back to zero while the other is still holding two
        /// hundred and thirty, and the difference between those is either a
        /// resync that swallows the next real hits or -- when the numbers
        /// fall the other way round -- up to thirty-two hits replayed into a
        /// player who has just spawned into a fresh match. Both were
        /// happening, four "damage sequence jumped" events per client per
        /// rotation.
        ///
        /// The tallies go, because they are per-match diagnostics.
        /// </summary>
        public static void ResetForRoomChange()
        {
            Array.Clear(Resolved);
            Array.Clear(Replayed);
            Array.Clear(Fired);
            NetShotDiagnostics.Reset();
            NetTimingDiagnostics.Reset();
            Array.Clear(PlayerChecks);
            Array.Clear(PlayerOverlaps);
            Array.Clear(PlayerAccepted);
            Array.Clear(PlayerOverlapsByShooter);
            Array.Clear(AimDrift);
            Array.Clear(WorstDrift);
            ShockCoilSpawned = 0;
            ShockCoilAcquired = 0;
            BombPlayerChecks = 0;
            BombTeamSkips = 0;
            BombHits = 0;
            Array.Clear(DamageByBeam);
            Array.Clear(HitsByBeam);
            BombDamageDealt = 0;
            BombDamageHits = 0;
            BombSpawnCalls = 0;
            BombSpawnMade = 0;
            BombSpawnDetonated = 0;
            BombSpawnStaleCount = 0;
            BombSpawnPoolEmpty = 0;
            BombNearest = Single.MaxValue;
            BombRadiusSeen = 0;
            Replaying = false;
            ReplayBeam = BeamType.None;
        }

        /// <summary>
        /// Forget one slot's damage history, because the slot has changed
        /// hands. See <see cref="NetPlayerBridge.ForgetSlot"/>.
        ///
        /// The counter is what makes Replay safe, and it is a byte: the new
        /// occupant would otherwise be compared against the sequence the
        /// previous one left, and the difference between two unrelated byte
        /// counters is as likely to read as a two-hundred-hit burst (which
        /// Replay discards as a resync) as it is to read as nothing.
        /// </summary>
        public static void ForgetSlot(int slot)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            for (int i = 0; i < PlayerState.DamageHistory; i++) _history[slot, i] = default;
            _sequence[slot] = 0;
            _attacker[slot] = NoSlot;
            _beam[slot] = NoBeam;
            _flags[slot] = 0;
            _direction[slot] = Vector3.Zero;
            _lastSeen[slot] = 0;
            _everSeen[slot] = false;
            Resolved[slot] = 0;
            Replayed[slot] = 0;
        }

        /// <summary>
        /// A new life starts here: take the authority's counter as the baseline
        /// instead of replaying the difference.
        ///
        /// A hit that landed in the last moments of the previous life reaches
        /// this machine a round trip after the respawn has already been
        /// applied, so the difference is non-zero while the health in the same
        /// snapshot is the fresh 99. <see cref="Replay"/> then floors the
        /// amount at one point -- and adds NoDmgInvuln, so spawn
        /// invulnerability does not stop it. That is the single point of
        /// damage a player takes a second after coming back, with nothing
        /// having hit them. The counter is per slot and survives the death; a
        /// life that is over owes the new one nothing.
        /// </summary>
        public static void NoteRespawn(int slot, ushort sequence)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            _everSeen[slot] = true;
            _lastSeen[slot] = sequence;
        }

        public static void Reset()
        {
            Array.Clear(_history);
            Array.Clear(_sequence);
            Array.Fill(_attacker, NoSlot);
            Array.Fill(_beam, NoBeam);
            Array.Clear(_flags);
            Array.Clear(_direction);
            Array.Clear(_lastSeen);
            Array.Clear(_everSeen);
            Array.Clear(Resolved);
            Array.Clear(Replayed);
            Array.Clear(Fired);
            NetShotDiagnostics.Reset();
            NetTimingDiagnostics.Reset();
            Array.Clear(PlayerChecks);
            Array.Clear(PlayerOverlaps);
            Array.Clear(PlayerAccepted);
            Array.Clear(PlayerOverlapsByShooter);
            Array.Clear(AimDrift);
            Array.Clear(WorstDrift);
            ShockCoilSpawned = 0;
            ShockCoilAcquired = 0;
            BombPlayerChecks = 0;
            BombTeamSkips = 0;
            BombHits = 0;
            Array.Clear(DamageByBeam);
            Array.Clear(HitsByBeam);
            BombDamageDealt = 0;
            BombDamageHits = 0;
            BombSpawnCalls = 0;
            BombSpawnMade = 0;
            BombSpawnDetonated = 0;
            BombSpawnStaleCount = 0;
            BombSpawnPoolEmpty = 0;
            BombNearest = Single.MaxValue;
            BombRadiusSeen = 0;
            Replaying = false;
            ReplayBeam = BeamType.None;
        }

        /// <summary>
        /// Whether damage resolved on this machine should be thrown away.
        ///
        /// On a client that is not the authority, every player is a puppet
        /// and its beams are a local echo: the authority already decided
        /// whether that shot connected. Letting the echo land too gave the
        /// same hit twice on the scoreboard and produced kills that never
        /// happened anywhere else.
        /// </summary>
        public static bool Suppress(PlayerEntity victim, EntityBase? source, DamageFlags flags)
        {
            if (!NetSession.Active || Replaying)
            {
                return false;
            }
            if (source is BeamProjectileEntity projectile && !NetPlayerLifecycle.CurrentProjectile(projectile))
                return true;
            if (NetSession.IsHost || NetSession.IsAuthority)
            {
                // Except its own copy of a shot a hit claim has already made
                // real. For anything that travels, the authority's projectile
                // can still be in the air when the claim for it is applied,
                // and this is the only point early enough to refuse the second
                // helping. NetHitClaims.AlreadyRescued.
                if (source is BeamProjectileEntity rescued && rescued.ModLaunchFrame != 0)
                {
                    PlayerEntity? owner = rescued.Owner as PlayerEntity
                        ?? (rescued.Owner as HalfturretEntity)?.Owner;
                    if (owner != null && NetHitClaims.AlreadyRescued(
                        owner.SlotIndex, victim.SlotIndex, rescued.ModLaunchFrame, rescued.ModLaunchKey))
                    {
                        return true;
                    }
                }
                return false;
            }
            // Except for this machine's own shots on somebody else, which are
            // resolved here and now and reconciled against the authority's
            // answer when it arrives. NetHitPrediction.
            // Old-life flights remain authoritative. Clients must not re-declare one
            // under their new life; the claim protocol deliberately requires current life.
            if (source is BeamProjectileEntity flight && flight.ModLaunchKey.ShooterSlot >= 0
                && !NetPlayerLifecycle.Matches(flight.ModLaunchKey.ShooterSlot,
                    flight.ModLaunchKey.Generation, flight.ModLaunchKey.LifeId)) return true;
            return !NetHitPrediction.Predicts(victim, source, flags);
        }

        /// <summary>
        /// The beam a rescued hit claim was fired with.
        ///
        /// A claim is applied on the authority with the shooter as the source
        /// rather than a beam, because the beam only ever existed on the
        /// machine that fired it -- so <c>TakeDamage</c> hands
        /// <see cref="Note"/> <see cref="BeamType.None"/> and the victim's own
        /// machine would replay a nameless hit with the wrong effect, the
        /// wrong sound and no weapon on the kill feed. Set for the length of
        /// one <c>TakeDamage</c> call and cleared straight after.
        /// <see cref="NetHitClaims"/>.
        /// </summary>
        private static BeamType _claimedBeam = BeamType.None;

        public static void SetClaimedBeam(BeamType beam) => _claimedBeam = beam;

        /// <summary>
        /// True for the length of the one <c>TakeDamage</c> call that applies a
        /// rescued hit claim.
        ///
        /// <b>It stops the damage being multiplied twice.</b> A claim carries
        /// the number the shooter's own machine arrived at, with every
        /// multiplier that machine knows about already in it -- the beam's
        /// effectiveness against that hunter, the double-damage powerup, the
        /// match's damage level. <c>TakeDamage</c> would then apply the two it
        /// can see all over again: 4x for a shooter holding double damage, and
        /// 1.25x on a server set to high. The effectiveness multiplier is not
        /// among them, because that branch only runs for a beam and a claim
        /// has none.
        ///
        /// Read by <c>PlayerEntity.TakeDamage</c> in exactly two places, both
        /// of which are the word "again". <see cref="NetHitClaims"/>.
        /// </summary>
        public static bool ApplyingClaim { get; private set; }

        /// <summary>
        /// Wrap the one call that applies a claim. A struct rather than a
        /// pair of calls so that an exception inside <c>TakeDamage</c> cannot
        /// leave every subsequent hit in the match unmultiplied.
        /// </summary>
        public readonly struct ClaimScope : IDisposable
        {
            public ClaimScope(BeamType beam)
            {
                _claimedBeam = beam;
                ApplyingClaim = true;
            }

            public void Dispose()
            {
                _claimedBeam = BeamType.None;
                ApplyingClaim = false;
            }
        }

        /// <summary>Called by the authority for every hit it resolves.</summary>
        public static void Note(PlayerEntity victim, PlayerEntity? attacker, BeamType beam,
            DamageFlags flags, Vector3? direction, uint amount = 0, bool fromBomb = false,
            uint launchFrame = 0, ShotKey? launchKey = null)
        {
            if (beam == BeamType.None && _claimedBeam != BeamType.None)
            {
                beam = _claimedBeam;
            }
            if (ApplyingClaim) launchFrame = NetHitClaims.CurrentClaimLaunch;
            if (!NetSession.Active || Replaying || NetHitPrediction.Predicting)
            {
                // A predicted hit is not a resolution. Letting it through here
                // would put a damage sequence, and a Resolved count, on a
                // machine that decides nothing -- and the whole damage
                // pipeline measurement is the comparison between the one
                // machine that resolves and the ones that replay.
                return;
            }
            // Before the slot check: what hurt somebody is worth knowing even
            // for a victim this table cannot index.
            if (fromBomb)
            {
                BombDamageDealt += (int)amount;
                BombDamageHits++;
            }
            else if (beam >= 0 && (int)beam < DamageByBeam.Length)
            {
                DamageByBeam[(int)beam] += (int)amount;
                HitsByBeam[(int)beam]++;
            }
            int slot = victim.SlotIndex;
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            int weapon = NetShotDiagnostics.Bucket(beam);
            NetShotDiagnostics.AuthorityHits[weapon]++;
            NetShotDiagnostics.AuthorityDamage[weapon] += amount;
            if (flags.TestFlag(DamageFlags.Headshot)) NetShotDiagnostics.AuthorityHeadshots[weapon]++;
            if (attacker != null && NetLog.Enabled) NetShotDiagnostics.Trace("authority-hit",
                launchKey ?? ShotKey.For(attacker.SlotIndex, launchFrame), beam, $"victim={slot} damage={amount}");
            _sequence[slot] = NetLifecycleTracker.Next(_sequence[slot]);
            if (NetLog.Enabled) NetLog.Event($"[damage-publish] epoch={NetSession.AuthorityEpoch} match={NetSession.CurrentMatchId} victim={slot}/{NetPlayerLifecycle.Generation(slot)}/{NetPlayerLifecycle.Get(slot)} event={_sequence[slot]} shooter={attacker?.SlotIndex} launch={launchFrame}");
            Resolved[slot]++;
            if (NetLog.Enabled)
            {
                // Every hit the machine running the match resolves, with the
                // stamp that identifies the shot. The only way to line a
                // client's claims up against what the authority actually did
                // with the same shots -- a client's own report can say it hit
                // and the authority's silence cannot be read from outside.
                NetLog.Event($"[resolve] slot {(attacker != null ? attacker.SlotIndex : -1)} "
                    + $"hit slot {slot} for {amount} with {beam} (launch {launchFrame}), "
                    + $"health {victim.Health} -> {Math.Max(0, victim.Health - (int)amount)}"
                    // Where both of them were standing when the authority
                    // resolved it. A shooter that just respawned and is still
                    // reported at the spot it died on is a stale intent
                    // undoing the spawn, and nothing else in this log can tell
                    // that from a legitimate hit.
                    + (attacker != null
                        ? $" | shooter ({attacker.Position.X:F2},{attacker.Position.Y:F2},"
                            + $"{attacker.Position.Z:F2}) hp={attacker.Health}"
                        : "")
                    + $" | victim ({victim.Position.X:F2},{victim.Position.Y:F2},"
                    + $"{victim.Position.Z:F2})");
            }
            // The world-frame this hit was aimed at, for the kill
            // arbitration: two players who killed each other are separated by
            // which of them pulled the trigger in the earlier world, and this
            // is where that stamp is taken. NetHitClaims.
            NetHitClaims.NoteAuthorityHit(
                attacker != null ? attacker.SlotIndex : -1, slot, launchFrame, (int)amount);
            _attacker[slot] = attacker != null && attacker.SlotIndex >= 0 && attacker.SlotIndex < Slots
                ? (byte)attacker.SlotIndex
                : NoSlot;
            _beam[slot] = beam == BeamType.None ? NoBeam : (byte)beam;
            _flags[slot] = (byte)((int)flags & RelayedFlags);
            // The impulse the engine applied, verbatim -- not a vector between
            // two players.
            //
            // TakeDamage adds `direction` straight onto Speed, so whatever
            // travels here is a velocity in units per frame. A beam supplies
            // one that GetDamageDirection built from a unit vector and the
            // weapon's own magnitude, which is a fraction of a unit; the
            // difference between two players' positions is the distance
            // between them, which at ten metres apart launched the victim at
            // ten units a frame and put them through the wall. That was the
            // "hits send people flying off the map" bug, and it was
            // asymmetric because the authority applies its own damage
            // directly and only ever replayed everyone else's.
            //
            // Zero is a real answer and is kept as one: most beams carry
            // DamageDirType 0 and knock nobody back. The receiver turns it
            // into a null direction, which is what makes TakeDamage fall back
            // to the attacker's position for the damage indicator -- for the
            // indicator only, exactly as it does for a local hit.
            _direction[slot] = ClampImpulse(direction ?? Vector3.Zero);
            for (int i = 0; i < PlayerState.DamageHistory - 1; i++) _history[slot, i] = _history[slot, i + 1];
            _history[slot, PlayerState.DamageHistory - 1] = new DamageEvent
            {
                EventId = _sequence[slot],
                AttackerSlot = _attacker[slot],
                AttackerGeneration = launchKey?.Generation ?? (attacker != null ? NetPlayerLifecycle.Generation(attacker.SlotIndex) : (ushort)0),
                Damage = (ushort)Math.Min(amount, ushort.MaxValue), Beam = _beam[slot],
                Flags = _flags[slot], Direction = _direction[slot]
            };
        }

        /// <summary>
        /// The largest knockback a hit is allowed to carry over the wire.
        ///
        /// Every impulse the weapon tables produce is well under this; the
        /// cap is here so that a value which is not one of those -- a future
        /// damage source, a corrupted read -- costs the victim a shove rather
        /// than the match.
        /// </summary>
        private const float MaxImpulse = 1.5f;

        private static Vector3 ClampImpulse(Vector3 impulse)
        {
            if (!Single.IsFinite(impulse.X) || !Single.IsFinite(impulse.Y)
                || !Single.IsFinite(impulse.Z))
            {
                return Vector3.Zero;
            }
            float length = impulse.Length;
            if (length <= MaxImpulse)
            {
                return impulse;
            }
            NetLog.Event($"knockback clamped from {length:0.##} to {MaxImpulse}");
            return impulse * (MaxImpulse / length);
        }

        private static readonly int[] _savedPoints = new int[Slots];
        private static readonly int[] _savedKills = new int[Slots];
        private static readonly int[] _savedDeaths = new int[Slots];

        /// <summary>
        /// The scoreboard, held across a replayed hit.
        ///
        /// A replay is feedback, not scoring. TakeDamage runs the engine's
        /// whole death path, and that path awards the kill: Points and Kills
        /// for the attacker, Deaths for the victim. The authority has already
        /// done all three and the snapshot being applied already carries the
        /// result, so letting the replay run them again counts the kill twice
        /// on every machine that is not the authority.
        ///
        /// One frame of a score one too high is not cosmetic, because
        /// EndIfPointGoalReached reads TeamPoints, which UpdateState rebuilds
        /// from Points at the end of the same frame. On the sixth kill of a
        /// seven-point match that transient seven ended the match on the
        /// client while the authority carried on -- the client sat in its
        /// results screen for the rest of the round, its player standing
        /// still on everybody else's screen, with 6-2 on the scoreboard it
        /// was showing. Seen in a real match on 2026-08-23.
        ///
        /// Restoring rather than suppressing keeps this in one place. The
        /// alternative -- assigning the authority's scores after the replay
        /// instead of before it, in ApplyState -- only works when the
        /// attacker's slot is applied after the victim's, since the replay
        /// runs on the victim and moves the *attacker's* row. Here the
        /// ordering does not matter.
        /// </summary>
        private static int _predictionScoreDepth;
        public readonly struct PredictionScoreScope : IDisposable
        {
            private readonly bool _active;
            public PredictionScoreScope(bool active)
            {
                _active = active;
                if (active && _predictionScoreDepth++ == 0) SaveScores();
            }
            public void Dispose()
            {
                if (_active && --_predictionScoreDepth == 0) RestoreScores();
            }
        }

        public static void ReplayDeath(PlayerEntity player)
        {
            bool wasReplaying = Replaying;
            Replaying = true;
            SaveScores();
            try { player.TakeDamage(1, DamageFlags.Death | DamageFlags.NoDmgInvuln, null, null); }
            finally { RestoreScores(); Replaying = wasReplaying; }
        }

        private static void SaveScores()
        {
            Array.Copy(GameState.Points, _savedPoints, Slots);
            Array.Copy(GameState.Kills, _savedKills, Slots);
            Array.Copy(GameState.Deaths, _savedDeaths, Slots);
        }

        private static void RestoreScores()
        {
            Array.Copy(_savedPoints, GameState.Points, Slots);
            Array.Copy(_savedKills, GameState.Kills, Slots);
            Array.Copy(_savedDeaths, GameState.Deaths, Slots);
        }

        /// <summary>Fill a snapshot entry for one slot.</summary>
        public static void Write(int slot, ref PlayerState state)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            state.DamageEventId = _sequence[slot];
            state.Damage0 = _history[slot, 0];
            state.Damage1 = _history[slot, 1];
            state.Damage2 = _history[slot, 2];
            state.Damage3 = _history[slot, 3];
            state.AttackerSlot = _attacker[slot];
            state.DamageBeam = _beam[slot];
            state.DamageFlags = _flags[slot];
            state.HitDirection = _direction[slot];
        }

        /// <summary>
        /// Replay a hit the authority resolved, if this snapshot carries one
        /// this machine has not shown yet.
        ///
        /// The first snapshot for a slot only records where the counter
        /// stands: a client joining a match in progress would otherwise open
        /// with a burst of damage for every hit landed before it arrived.
        /// </summary>
        public static void BeginLife(int slot, in PlayerState state)
        {
            _lastLife[slot] = state.LifeId;
            _lastGeneration[slot] = state.SlotGeneration;
            _everSeen[slot] = true;
            _lastSeen[slot] = state.DamageEventId;
        }

        public static void Replay(PlayerEntity player, in PlayerState state)
        {
            int slot = player.SlotIndex;
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            if (!NetPlayerLifecycle.Matches(slot, state.SlotGeneration, state.LifeId))
            {
                NetPlayerLifecycle.OldLifeDamage++;
                return;
            }
            if (!_everSeen[slot] || _lastLife[slot] != state.LifeId || _lastGeneration[slot] != state.SlotGeneration)
            {
                BeginLife(slot, state);
                return;
            }
            for (int i = 0; i < PlayerState.DamageHistory; i++)
            {
                DamageEvent hit = state.EventAt(i);
                if (hit.EventId == 0 || (_lastSeen[slot] != 0 && !NetLifecycleTracker.Newer(hit.EventId, _lastSeen[slot]))) continue;
                // Victim slot/life are implicit in the enclosing PlayerState.
                // The state itself was lifecycle-validated above, and damage
                // history is cleared at every life/occupant boundary.
                // Redundant history carries the actual metadata for each hit,
                // rather than replaying the last attacker's hit N times.
                _lastSeen[slot] = hit.EventId;
                PlayerState feedback = state;
                // An authority event can legitimately name an earlier firing life.
                // Keep its attribution only while the same occupant still owns the slot.
                feedback.AttackerSlot = hit.AttackerGeneration != 0
                    && NetPlayerLifecycle.Generation(hit.AttackerSlot) == hit.AttackerGeneration
                    ? hit.AttackerSlot : NoSlot;
                feedback.DamageBeam = hit.Beam;
                feedback.DamageFlags = hit.Flags;
                feedback.HitDirection = hit.Direction;
                feedback.Health = hit.EventId == state.DamageEventId ? state.Health
                    : (ushort)Math.Max(1, player.Health - hit.Damage);
                if (NetLog.Enabled) NetLog.Event($"[damage-replay] epoch={NetSession.AuthorityEpoch} match={NetSession.CurrentMatchId} victim={slot}/{state.SlotGeneration}/{state.LifeId} event={hit.EventId} shooter={hit.AttackerSlot}/{hit.AttackerGeneration}");
                ReplayEvent(player, feedback);
            }
        }

        private static void ReplayEvent(PlayerEntity player, in PlayerState state)
        {
            int slot = player.SlotIndex;
            const int landed = 1;
            Replayed[slot]++;
            bool lethal = state.Health == 0;
            // Consumed before the "already down" return below, not after it.
            //
            // A kill predicted here leaves the victim at zero health on this
            // machine, so the authority's confirmation of that very kill would
            // hit that return and never retire the prediction -- which would
            // both count a hit that landed as denied and hold the corpse down
            // for the whole of the hold window rather than until the answer
            // arrived. Retiring it here is the answer arriving.
            // Including a hit on this machine's own player that its own
            // player dealt -- your splash, on you. That used to be excluded
            // here, on the grounds that nothing was ever predicted onto the
            // local player and asking would report every splash from one's own
            // bomb as a prediction that had missed. Self-damage is predicted
            // now, so the opposite is true: not asking would replay a hit this
            // machine has already applied, and a rocket jump would cost its
            // health twice. Damage from anybody else still names another
            // attacker and is replayed exactly as before.
            bool mine = state.AttackerSlot == NetHooks.LocalSlot;
            // The authority's own verdict on where the shot landed, forwarded
            // so a client can tell "you hit them" from "you hit them in the
            // head" -- the two are the same confirmation to everything else
            // here, and on the Imperialist they are a kill and half a kill.
            bool authorityHeadshot = ((DamageFlags)state.DamageFlags).TestFlag(DamageFlags.Headshot);
            bool predicted = mine && NetHitPrediction.Confirm(slot, landed, authorityHeadshot);

            if (player.Health <= 0)
            {
                return; // already down here; the respawn is what matters next
            }
            PlayerEntity? attacker = state.AttackerSlot < PlayerEntity.Players.Count
                ? PlayerEntity.Players[state.AttackerSlot]
                : null;
            // Already shown here, the moment the trigger was pulled: the
            // flinch, the sound, the knockback and the mark over the
            // crosshair all ran when this machine resolved the shot for
            // itself. Only the health is still owed, and ApplyState assigns
            // that from this same snapshot immediately after.
            //
            // A lethal confirmation still replays even when the nonlethal part
            // of the hit was predicted. Remote lethal prediction is always
            // clamped to one health now, so the authority owns the body falling;
            // returning early here would leave this client alive while the
            // authoritative state says dead. Self-damage is handled by the exact
            // confirmation path above and is not double-applied.
            if (predicted && !lethal)
            {
                if (authorityHeadshot && attacker == PlayerEntity.Main)
                {
                    // The local prediction deliberately suppresses HEADSHOT
                    // text. This is the authority agreeing with that predicted
                    // classification, and the rest of the hit is skipped
                    // because its feedback was already shown.
                    player.QueueHudMessage(128, 40, 20 / 30f, 0, 228);
                }
                return;
            }
            // Never let the replay decide the outcome: the authority already
            // has. A non-fatal hit is clamped so local rounding cannot kill,
            // and a fatal one carries the Death flag so it cannot fail to.
            int amount = Math.Max(1, player.Health - state.Health);
            if (!lethal)
            {
                amount = Math.Min(amount, Math.Max(0, player.Health - 1));
            }
            DamageFlags flags = (DamageFlags)state.DamageFlags | DamageFlags.NoDmgInvuln;
            if (lethal)
            {
                flags |= DamageFlags.Death;
            }
            // Clamped again on arrival: what a peer sends is not something
            // this machine controls, and one bad impulse is the difference
            // between a hit and a player outside the level.
            Vector3 impulse = ClampImpulse(state.HitDirection);
            Vector3? direction = impulse == Vector3.Zero ? null : impulse;
            Replaying = true;
            ReplayBeam = state.DamageBeam == NoBeam ? BeamType.None : (BeamType)state.DamageBeam;
            SaveScores();
            try
            {
                player.TakeDamage((uint)amount, flags, direction, attacker);
            }
            finally
            {
                RestoreScores();
                Replaying = false;
                ReplayBeam = BeamType.None;
            }
        }
    }
}
